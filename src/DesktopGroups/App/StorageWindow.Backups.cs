using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DesktopGroups;

internal sealed class BackupRunState
{
    public DateTimeOffset? LastSuccess { get; set; }
    public DateTimeOffset? LastFailure { get; set; }
    public string Error { get; set; } = "";
}

internal sealed partial class StorageWindow
{
    private readonly DispatcherTimer backupTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private BackupOptions backupOptions = new();
    private BackupRunState backupState = new();
    private bool refreshingBackups;
    private string lastBackupSignature = "", observedBackupSignature = "";
    private DateTimeOffset? backupDirtySince;
    private BackupService Backups => new(storage, demo ? storage.Resolve("sample-desktop") : null);
    internal bool MaintenanceBusy => maintenanceBusy;

    private void InitializeBackups()
    {
        backupOptions = Backups.Options();
        var state = storage.Resolve("logs/backup-state.json");
        if (File.Exists(state))
            try { backupState = JsonSerializer.Deserialize<BackupRunState>(File.ReadAllText(state)) ?? new(); }
            catch (Exception e) when (e is IOException or JsonException) { backupState.Error = "上次备份状态无法读取：" + e.Message; }
        RefreshBackupControls();
        backupTimer.Tick += async (_, _) =>
        {
            if (demo || !backupOptions.Enabled || disposed || closing || maintenanceBusy || BusyOperations > 0) return;
            var signature = BackupSignature();
            if (signature != observedBackupSignature) { observedBackupSignature = signature; backupDirtySince = DateTimeOffset.UtcNow; }
            var scheduled = backupState.LastSuccess == null || DateTimeOffset.Now - backupState.LastSuccess >= TimeSpan.FromMinutes(backupOptions.IntervalMinutes);
            var changed = signature != lastBackupSignature && backupDirtySince is { } dirty && DateTimeOffset.UtcNow - dirty >= TimeSpan.FromMinutes(2);
            // Retry failures at a bounded interval, leaving the failure visible between attempts.
            if (backupState.LastFailure is { } failed && DateTimeOffset.Now - failed < TimeSpan.FromMinutes(5)) return;
            if (scheduled || changed) await BackupNowAsync("自动备份", false);
        };
        if (!demo) backupTimer.Start();
    }
    private string BackupSignature() => JsonSerializer.Serialize(new { catalog.Settings,
        Profiles = catalog.Stores.OrderBy(p => p.Key).Select(p => p.Value.Profile).ToArray() });
    private void RefreshBackupControls()
    {
        refreshingBackups = true;
        try
        {
            AutoBackupBox.IsChecked = backupOptions.Enabled; BackupDirectoryBox.Text = backupOptions.Directory;
            BackupIntervalBox.Text = backupOptions.IntervalMinutes.ToString(); BackupRetainBox.Text = backupOptions.RetainCount.ToString();
            BackupStatusLabel.Text = (backupState.LastSuccess is { } success ? $"最近成功：{success.LocalDateTime:yyyy-MM-dd HH:mm}" : "尚未创建备份")
                + (backupState.LastFailure is { } failed ? $"\n最近失败：{failed.LocalDateTime:yyyy-MM-dd HH:mm}" : "")
                + (backupState.Error.Length > 0 ? "\n" + backupState.Error : "");
        }
        finally { refreshingBackups = false; }
    }
    private async void SaveBackupSettingsClick(object sender, RoutedEventArgs e)
    {
        if (refreshingBackups || maintenanceBusy) return;
        try
        {
            if (!int.TryParse(BackupIntervalBox.Text, out var minutes) || !int.TryParse(BackupRetainBox.Text, out var count))
                throw new IOException("请输入有效的备份间隔和保留数量。");
            var options = new BackupOptions { Enabled = AutoBackupBox.IsChecked == true,
                Directory = BackupDirectoryBox.Text.Trim(), IntervalMinutes = minutes, RetainCount = count };
            Backups.SaveOptions(options); backupOptions = options;
            Status("备份设置已保存。手动与恢复前的备份不参与自动轮换。");
            await RefreshBackupListAsync();
        }
        catch (Exception error) { Status(error.Message); BackupStatusLabel.Text = error.Message; }
    }
    private void ChooseBackupDirectoryClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择备份保存目录", Multiselect = false };
        if (picker.ShowDialog(this) == true) BackupDirectoryBox.Text = picker.FolderName;
    }
    private async void BackupNowClick(object sender, RoutedEventArgs e) => await BackupNowAsync();
    internal async Task<bool> BackupNowAsync(string reason = "手动备份", bool pinned = true)
    {
        if (!BeginMaintenance()) return false;
        try
        {
            await WaitForIdleAsync();
            var options = backupOptions;
            var result = await fileWorker.Run(() => Backups.Create(options, reason, pinned));
            lastBackupSignature = observedBackupSignature = BackupSignature(); backupDirtySince = null;
            backupState.LastSuccess = DateTimeOffset.Now; backupState.Error = ""; backupState.LastFailure = null;
            await fileWorker.Run(() => { storage.WriteJson("logs/backup-state.json", backupState); return true; });
            Status("已创建并校验备份：" + result.Label);
            return true;
        }
        catch (Exception error)
        {
            backupState.LastFailure = DateTimeOffset.Now; backupState.Error = error.Message;
            try { await fileWorker.Run(() => { storage.WriteJson("logs/backup-state.json", backupState); return true; }); } catch { }
            Status("备份失败，已有备份保留：" + error.Message); return false;
        }
        finally { EndMaintenance(); await RefreshBackupListAsync(); }
    }
    private bool BeginMaintenance()
    {
        if (maintenanceBusy || closing || exitAfterOperations || disposed || BusyOperations > 0 || scanning || runtimes.Values.Any(r => r.Panel.IsInteracting))
        { Status("请等待文件操作、恢复检查和抽屉交互结束后重试。"); return false; }
        maintenanceBusy = true; syncTimer.Stop(); desktopTimer.Stop();
        foreach (var r in runtimes.Values) r.Panel.InteractionBlocked = true;
        ((UIElement)Content).IsEnabled = false;
        return true;
    }
    private void EndMaintenance()
    {
        maintenanceBusy = false;
        if (disposed) return;
        ((UIElement)Content).IsEnabled = true;
        foreach (var r in runtimes.Values) r.Panel.InteractionBlocked = false;
        if (!closing) desktopTimer.Start();
        RefreshBackupControls();
    }
    private async void RefreshBackupsClick(object sender, RoutedEventArgs e) => await RefreshBackupListAsync();
    private async Task RefreshBackupListAsync()
    {
        if (disposed || maintenanceBusy) return;
        try
        {
            var items = await fileWorker.Run(() => Backups.List(backupOptions));
            if (disposed) return;
            BackupList.ItemsSource = items; BackupList.SelectedIndex = items.Count > 0 ? 0 : -1;
            RestoreBackupButton.IsEnabled = items.Count > 0;
        }
        catch (Exception error) { BackupStatusLabel.Text = "备份列表读取失败：" + error.Message; }
    }
    private void OpenBackupFolderClick(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (!Directory.Exists(backupOptions.Directory)) throw new IOException("备份目录尚未创建，请先立即备份。");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(backupOptions.Directory) { UseShellExecute = true });
    });
    private async void RestoreBackupClick(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not BackupSummary backup) return;
        try
        {
            var manifest = await fileWorker.Run(() => Backups.Validate(backup.Directory));
            var count = manifest.Files.Count(f => f.Path.StartsWith("config/groups/", StringComparison.Ordinal));
            var answer = MessageBox.Show(this, $"恢复 {manifest.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm} 的 {count} 个抽屉配置及托管内容？\n\n会先备份当前状态、归还当前内容，再恢复所选备份。桌面同名不同内容会阻止恢复；不会覆盖。", "恢复备份", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.OK) await RestoreBackupAsync(backup.Directory);
        }
        catch (Exception error) { Status("恢复未开始：" + error.Message); BackupStatusLabel.Text = error.Message; }
    }
    internal async Task<bool> RestoreBackupAsync(string directory)
    {
        if (!BeginMaintenance()) return false;
        var reloaded = false;
        try
        {
            await WaitForIdleAsync();
            await fileWorker.Run(() => { Backups.CheckDesktopConflicts(directory); return true; });
            await fileWorker.Run(() => Backups.Create(backupOptions, "恢复前备份", true));
            foreach (var runtime in runtimes.Values.ToArray())
            {
                runtime.Paused = true;
                if (!await RunStoreAsync(runtime, "恢复前归还", store => store.RestoreAll(), duringExit: true))
                    throw new IOException("当前内容尚未全部归还，恢复已停止，请处理运行与恢复中的问题。");
            }
            await fileWorker.Run(() => { Backups.Restore(directory); return true; });
            ReloadAfterRestore(); reloaded = true;
            backupOptions = Backups.Options(); backupState.LastSuccess = DateTimeOffset.Now; backupState.Error = "";
            lastBackupSignature = observedBackupSignature = BackupSignature();
            Status("备份已恢复。当前状态的安全备份与恢复前目录均已保留。");
            return true;
        }
        catch (Exception error) { backupState.Error = "恢复停止：" + error.Message; Status("恢复停止，原数据与安全备份已保留：" + error.Message); return false; }
        finally
        {
            EndMaintenance();
            if (reloaded) RecoverDesktop(true);
            await RefreshBackupListAsync();
        }
    }
    private void ReloadAfterRestore()
    {
        var restored = new DrawerCatalog(storage, demo ? storage.Resolve("sample-desktop") : null);
        initializing = true;
        try
        {
            watcher?.Dispose(); watcher = null;
            foreach (var r in runtimes.Values) r.Panel.Dispose();
            runtimes.Clear(); rows.Clear(); catalog = restored;
            ApplyTheme();
            foreach (var definition in catalog.Settings.Drawers) AddRuntime(definition);
        }
        finally { initializing = false; }
        SelectDrawer(catalog.Settings.SelectedId ?? rows.FirstOrDefault()?.Id ?? "");
        RecreateDesktopWatcher(); _ = ScanRecoveryAsync();
    }
}
