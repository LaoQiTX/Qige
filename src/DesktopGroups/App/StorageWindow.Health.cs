using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace DesktopGroups;

internal sealed partial class StorageWindow
{
    private readonly StaWorkQueue fileWorker = new("Qige files");
    private readonly AsyncIconCache iconCache = new();
    private readonly PerformanceRecorder performance = new();
    private readonly DesktopRecoveryPolicy recoveryPolicy = new();
    private readonly ObservableCollection<RecoveryProblem> recoveryProblems = [];
    private readonly DispatcherTimer performanceTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly Stopwatch heartbeat = Stopwatch.StartNew();
    private bool recoveringLayout, scanning, rescanRequested, disposed, performanceWriting;
    private int watchdogTicks;
    private string topology = "", environmentIssue = "";
    private long lastHeartbeat;
    private double maxUiDelay;
    internal IReadOnlyList<RecoveryProblem> RecoveryProblems => recoveryProblems;
    internal int BusyOperations => runtimes.Values.Count(r => r.Busy);

    private void InitializeHealth()
    {
        RecoveryList.ItemsSource = recoveryProblems;
        performanceTimer.Tick += (_, _) => SamplePerformance();
        performanceTimer.Start();
        lastHeartbeat = heartbeat.ElapsedMilliseconds;
    }
    private async Task LoadRuntimeIconsAsync(DrawerRuntime r, string signature)
    {
        var store = r.Row.Store;
        var active = store.Active.Select(e => e.Id).ToHashSet();
        var entries = store.Profile.Entries.Select(e => (e.Id, Name: Path.GetFileNameWithoutExtension(e.OriginalPath),
            Path: active.Contains(e.Id) ? store.StoredPath(e) : e.OriginalPath, Original: e.OriginalPath, e.Hash)).ToArray();
        var loaded = await Task.WhenAll(entries.Select(async e =>
            (e.Id, e.Name, e.Path, Icon: await iconCache.LoadAsync(e.Id + ":" + e.Hash + ":" + e.Path, e.Path, e.Original))));
        if (disposed || !runtimes.ContainsKey(r.Definition.Id) || r.ItemsSignature != signature) return;
        r.Panel.SetPreparedItems(loaded.Where(e => active.Contains(e.Id)).Select(e => (e.Name, e.Path, e.Icon)));
        UpdateRow(r);
        if (Selected?.Id == r.Definition.Id) FillContents(r.Row.Store);
    }
    internal async Task WaitForIdleAsync()
    {
        var timeout = Stopwatch.StartNew();
        long? idleSince = null;
        while (timeout.Elapsed < TimeSpan.FromSeconds(20))
        {
            var pending = BusyOperations > 0 || scanning || iconCache.PendingCount > 0 || restoringExit || syncTimer.IsEnabled;
            if (pending) idleSince = null;
            else
            {
                idleSince ??= timeout.ElapsedMilliseconds;
                if (timeout.ElapsedMilliseconds - idleSince >= 200) return;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException($"后台操作未在验证时限内完成：文件任务={BusyOperations}，恢复检查={scanning}，图标={iconCache.PendingCount}，退出={restoringExit}，同步计时={syncTimer.IsEnabled}。");
    }
    private void SignalEnvironment(string reason)
    {
        recoveryPolicy.Signal(reason, DateTimeOffset.UtcNow);
        environmentTimer.Stop(); environmentTimer.Start();
        RecreateDesktopWatcher();
    }
    private void RecreateDesktopWatcher()
    {
        watcher?.Dispose(); watcher = null;
        if (closing || disposed) return;
        var desktop = catalog.Stores.Values.FirstOrDefault()?.DesktopPath
            ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        try
        {
            if (!Directory.Exists(desktop)) return;
            watcher = new FileSystemWatcher(desktop)
                { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            watcher.Created += WatchChanged; watcher.Changed += WatchChanged;
            watcher.Renamed += WatchChanged; watcher.Deleted += WatchChanged;
            watcher.Error += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (closing || disposed) return;
                RecreateDesktopWatcher(); syncRetries = 0; syncTimer.Stop(); syncTimer.Start();
                _ = ScanRecoveryAsync();
            });
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        { watcher?.Dispose(); watcher = null; Status("目录监听暂不可用，将定期完整同步：" + e.Message); }
    }
    private void DesktopTick()
    {
        var elapsed = heartbeat.ElapsedMilliseconds;
        maxUiDelay = Math.Max(maxUiDelay, Math.Max(0, elapsed - lastHeartbeat - desktopTimer.Interval.TotalMilliseconds));
        lastHeartbeat = elapsed;
        if (closing) return;
        if (++watchdogTicks % 15 == 0)
        {
            if (watcher == null) RecreateDesktopWatcher();
            // The watcher can lose events during sleep, Explorer restart or a buffer overflow.
            if (!syncTimer.IsEnabled) { syncRetries = 0; syncTimer.Start(); }
        }
        var monitors = DesktopPlacement.Capture();
        var signature = string.Join("|", monitors.Select(m => $"{m.Device}:{m.Work}:{m.Dpi}"));
        if (signature != topology) { topology = signature; SignalEnvironment("显示器／缩放变化"); }
        var missing = runtimes.Values.Any(r => !r.Panel.IsAttached);
        if (missing && !recoveryPolicy.Pending) recoveryPolicy.Signal("桌面窗口失效", DateTimeOffset.UtcNow);
        var due = recoveryPolicy.IsDue(DateTimeOffset.UtcNow);
        if (due) RecoverDesktop(true);
        else if (!missing && !recoveryPolicy.Pending) RecoverDesktop(false);
        if (HealthBox.Visibility == Visibility.Visible) RefreshHealthText();
    }
    private void SamplePerformance()
    {
        if (disposed) return;
        performance.Sample(maxUiDelay, IsVisible, runtimes.Values.Any(r => r.Panel.IsInteracting)); maxUiDelay = 0;
        RefreshHealthText();
        if (performanceWriting) return;
        var snapshot = performance.Snapshot(iconCache.Metrics(), runtimes.Values.Select(r => new
            { r.Definition.Id, r.Panel.SurfaceBuilds, r.Panel.MoveCount, r.Panel.AttachmentCount }).ToArray());
        performanceWriting = true;
        _ = WritePerformance();
        async Task WritePerformance()
        {
            try { await fileWorker.Run(() => { storage.WriteJson("logs/performance-current.json", snapshot); return true; }); }
            catch (Exception e) { if (!disposed) Status("性能记录写入失败：" + e.Message); }
            finally { performanceWriting = false; }
        }
    }
    private void RefreshHealthText()
    {
        HealthSummary.Text = $"桌面连接 {runtimes.Values.Count(r => r.Panel.IsAttached)}/{runtimes.Count} · 托盘{(TrayAvailable ? "正常" : "等待恢复")} · 正在执行 {BusyOperations} 项操作";
        HealthEnvironment.Text = environmentIssue.Length > 0 ? environmentIssue : $"最近环境事件：{recoveryPolicy.Reason}。恢复状态：{(recoveryPolicy.Pending ? "等待重试" : "已连接")}";
        var sample = performance.Latest;
        PerformanceLabel.Text = $"抽屉首次就绪：{(performance.ReadyMs is { } ms ? ms.ToString("F0") + " ms" : "等待桌面")}\n" +
            (sample == null ? "等待首个资源采样（每 15 秒一次）" : $"进程 CPU：{sample.ProcessCpuPercent:F2}% · 工作集：{sample.WorkingSetMB:F1} MB · 私有内存：{sample.PrivateMB:F1} MB\nUI 调度最大延迟：{sample.UiDelayMs:F0} ms · 已采样 {performance.SampleCount} 次（最多保留 240 次）") +
            $"\n图标缓存：{iconCache.Count}/256 · 已加载：{iconCache.Loads} · 缓存命中：{iconCache.Hits}";
        RecoveryCountLabel.Text = recoveryProblems.Count == 0 ? "没有检查到待处理文件。" : $"{recoveryProblems.Count} 个待处理文件 · 先检查两处内容，操作不会覆盖同名文件";
    }
    private async void HealthClick(object sender, RoutedEventArgs e)
    {
        ShowModal(HealthBox); RefreshHealthText(); await ScanRecoveryAsync();
    }
    private async void RefreshRecoveryClick(object sender, RoutedEventArgs e) => await ScanRecoveryAsync();
    internal async Task ScanRecoveryAsync()
    {
        if (disposed) return;
        if (scanning) { rescanRequested = true; return; }
        scanning = true;
        try
        {
            var collected = new List<RecoveryProblem>();
            foreach (var r in runtimes.Values.ToArray())
            {
                if (r.Busy) { collected.AddRange(recoveryProblems.Where(p => p.DrawerId == r.Definition.Id)); continue; }
                var id = r.Definition.Id; var name = r.Definition.Name;
                var items = await fileWorker.Run(() => RecoveryInspection.Inspect(id, name, r.Row.Store.Detached()));
                if (!runtimes.ContainsKey(id)) continue;
                collected.AddRange(items);
                if (items.Count == 0 && (r.Issue != null || r.Definition.Removing))
                    collected.Add(new(id, name, "", "抽屉操作", r.Issue ?? "上次移除尚未完成，请重试归还并移除。",
                        r.Row.Store.DesktopPath, storage.Resolve($"storage/{id}"), "Operation"));
            }
            if (disposed) return;
            recoveryProblems.Clear(); foreach (var item in collected) recoveryProblems.Add(item);
            HealthButton.Content = collected.Count == 0 ? "运行与恢复" : $"运行与恢复 · {collected.Count}";
            RefreshHealthText();
        }
        catch (Exception e) { if (!disposed) { HealthEnvironment.Text = "检查未完成：" + e.Message; Status(HealthEnvironment.Text); } }
        finally
        {
            scanning = false;
            if (rescanRequested && !disposed) { rescanRequested = false; _ = ScanRecoveryAsync(); }
        }
    }
    private void OpenRecoveryLocation(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RecoveryProblem problem } element) return;
        Try(() =>
        {
            var path = Equals(element.Tag, "original") ? problem.OriginalPath : problem.StoredPath;
            if (File.Exists(path))
            {
                var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                info.ArgumentList.Add("/select,"); info.ArgumentList.Add(path); Process.Start(info);
            }
            else
            {
                var parent = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                while (parent != null && !Directory.Exists(parent)) parent = Path.GetDirectoryName(parent);
                if (parent != null) Process.Start(new ProcessStartInfo(parent) { UseShellExecute = true });
                else Status("所在位置暂不可用，请检查磁盘。");
            }
        });
    }
    private async void RetryProblemClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RecoveryProblem p }) await RetryProblemAsync(p);
    }
    internal async Task RetryProblemAsync(RecoveryProblem problem)
    {
        if (!runtimes.TryGetValue(problem.DrawerId, out var r)) return;
        if (r.Definition.Removing) { await RemoveDrawerAsync(r.Definition.Id); await ScanRecoveryAsync(); return; }
        var restore = r.Paused;
        if (!restore && !r.Panel.IsAttached) { Status("桌面尚未连接，请先重新连接桌面，或归还此抽屉后重试。"); return; }
        await RunStoreAsync(r, "重试恢复", store =>
        {
            if (problem.EntryId.Length == 0) return restore ? store.RestoreAll() : store.StartSession();
            store.RetryEntry(problem.EntryId, restore); return [];
        });
        await ScanRecoveryAsync();
    }
    private async void ExportProblemClick(object sender, RoutedEventArgs e)
    {
        if (closing || exitAfterOperations) return;
        if (sender is not FrameworkElement { DataContext: RecoveryProblem p }) return;
        try
        {
            var destination = await fileWorker.Run(() =>
            {
                var file = storage.Resolve($"recovery/exports/{Guid.NewGuid():N}/{Path.GetFileName(p.StoredPath)}");
                if (!File.Exists(p.StoredPath)) throw new IOException("收纳文件不可用，未导出。");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                VerifiedTransfer.Copy(p.StoredPath, file);
                return file;
            });
            Status("副本已另存至 " + destination + "；原文件和分组记录保留。");
            Process.Start(new ProcessStartInfo(Path.GetDirectoryName(destination)!) { UseShellExecute = true });
        }
        catch (Exception error) { Status(error.Message); }
    }
    private void RetryDesktopClick(object sender, RoutedEventArgs e) { SignalEnvironment("手动重新连接"); RecoverDesktop(true); }
    private void LocateAllClick(object sender, RoutedEventArgs e)
    { RecoverDesktop(true); foreach (var r in runtimes.Values) if (r.Panel.IsAttached) r.Panel.Highlight(); Status("全部抽屉已定位；可最小化管理面板查看。"); }
    private void OpenLogsClick(object sender, RoutedEventArgs e) => Try(() =>
    { var path = storage.Resolve("logs"); Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); });

    private async Task<bool> RunStoreAsync(DrawerRuntime runtime, string operation, Func<ShortcutStore, IReadOnlyList<string>> action, bool duringExit = false)
    {
        if ((closing || exitAfterOperations) && !duringExit) return false;
        if (runtime.Busy || disposed) { Status("此抽屉仍有操作进行中，请稍后重试。"); return false; }
        runtime.Busy = true; Status(operation + "中…");
        var success = false;
        try
        {
            // The UI retains a stable snapshot; the worker alone mutates its detached profile.
            var result = await fileWorker.Run(() =>
            {
                var detached = runtime.Row.Store.Detached();
                IReadOnlyList<string> issues;
                try { issues = action(detached); } catch (Exception e) { issues = [e.Message]; }
                return (detached.Profile, issues);
            });
            if (disposed) return false;
            runtime.Row.Store.Publish(result.Profile);
            Issues(runtime, result.issues); success = result.issues.Count == 0;
            if (success) Status(operation + "完成。");
        }
        catch (Exception e) { Issues(runtime, [e.Message]); }
        finally
        {
            runtime.Busy = false; RefreshHealthText();
            if (exitAfterOperations && BusyOperations == 0 && !restoringExit && !disposed) { exitAfterOperations = false; RequestExit(); }
        }
        if (!disposed) await ScanRecoveryAsync();
        return success;
    }
    private bool exitAfterOperations;
    internal async Task<bool> RemoveDrawerAsync(string id)
    {
        if (closing || exitAfterOperations || !runtimes.TryGetValue(id, out var r) || r.Busy) { Status("请等待当前文件操作完成。"); return false; }
        r.Definition.Removing = true; r.Paused = true; catalog.Save();
        if (!await RunStoreAsync(r, "移除前归还", store => store.RestoreAll())) return false;
        // Files have already been returned by the worker; catalog removal only retires metadata.
        return RemoveDrawer(id);
    }
    private void DisposeHealth()
    {
        disposed = true; performanceTimer.Stop(); iconCache.Dispose(); fileWorker.Dispose();
    }
}
