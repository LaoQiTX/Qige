using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopGroups;

internal sealed partial class StorageWindow : Window
{
    private readonly AppStorage storage;
    private readonly bool demo, background;
    private readonly DrawerCatalog catalog;
    private readonly Dictionary<string, DrawerRuntime> runtimes = [];
    private readonly ObservableCollection<DrawerRow> rows = [];
    private readonly SnapGuides guides = new();
    private readonly DispatcherTimer syncTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DispatcherTimer desktopTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer environmentTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly StartupRegistration? startup;
    private FileSystemWatcher? watcher;
    private ResidentTray? tray;
    private bool initializing = true, updating, closing, exitRequested, sessionEnding;
    private string lastConnection = "";
    private int syncRetries;
    internal bool TrayAvailable => tray?.Available == true;
    internal DrawerCatalog Catalog => catalog;
    internal IReadOnlyList<ShortcutPanel> Panels => runtimes.Values.Select(r => r.Panel).ToArray();

    public StorageWindow(AppStorage storage, bool demo = false, bool background = false)
    {
        this.demo = demo; this.background = background;
        this.storage = demo ? new AppStorage(storage.Resolve($"samples/manager-{Guid.NewGuid():N}")) : storage;
        this.storage.EnsureWritable();
        string? sampleDesktop = null;
        if (demo)
        {
            sampleDesktop = this.storage.Resolve("sample-desktop");
            Directory.CreateDirectory(sampleDesktop);
        }
        catalog = new DrawerCatalog(this.storage, sampleDesktop);
        if (demo) SeedDemo(sampleDesktop!);
        if (!demo) startup = new StartupRegistration(storage);
        InitializeComponent();
        InitializeHealth();
        Title = demo ? "栖格 Qige · 管理面板样例" : $"栖格 Qige · 抽屉管理 · {AppBrand.Version}";
        Icon = BitmapFrame.Create(AppBrand.IconUri); BrandIcon.Source = Icon;
        VersionLabel.Text = $"QIGE / {AppBrand.Version}";
        SettingsDataPath.Text = this.storage.Root;
        if (demo) StartupSettingsNote.Text = "隔离样例不修改 Windows 启动项，请在正式模式中设置。";
        InitializeManagerViews();
        SnapBox.IsChecked = catalog.Settings.SnapEnabled;
        StartupBox.IsEnabled = !demo; AddSelectedButton.IsEnabled = !demo;
        Overview.Selected += SelectDrawer;
        SourceInitialized += (_, _) =>
        {
            var dark = 0;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
        };
        Loaded += (_, _) => Initialize();
        syncTimer.Tick += (_, _) => Synchronize();
        desktopTimer.Tick += (_, _) => DesktopTick();
        environmentTimer.Tick += (_, _) =>
        {
            if (!recoveryPolicy.Pending) environmentTimer.Stop();
            else if (recoveryPolicy.IsDue(DateTimeOffset.UtcNow)) { environmentTimer.Stop(); RecoverDesktop(true); }
        };
        Closing += OnClosing;
        Application.Current.SessionEnding += OnSessionEnding;
    }

    private void SeedDemo(string desktop)
    {
        var readme = storage.Resolve("sample-readme.txt");
        File.WriteAllText(readme, "这是栖格管理面板的隔离样例。可以试用新建、重命名、布局、滚动与折叠；真实桌面文件不会被收纳。\n");
        var common = catalog.Stores["default"];
        for (var index = 0; index < 18; index++)
        {
            var name = index == 0 ? "项目文件夹" : index == 1 ? "使用说明" : index == 2 ? "计算器" : $"常用入口 {index + 1:00}";
            var path = Path.Combine(desktop, name + ".lnk");
            NativeShortcut.Write(path, index == 0 ? storage.Root : index == 1 ? readme
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "calc.exe"));
            common.Add(path);
        }
        common.Profile.X = 180; common.Profile.Y = 160; common.Profile.Width = 420; common.Profile.Height = 352; common.Save();
        var work = catalog.Create("工作");
        catalog.Stores[work.Id].Profile.X = 620; catalog.Stores[work.Id].Profile.Y = 160;
        catalog.Stores[work.Id].Profile.Width = 322; catalog.Stores[work.Id].Profile.Height = 260;
        catalog.Stores[work.Id].Save();
    }

    internal void Start()
    {
        if (background) Initialize();
        else ShowControl();
    }

    private bool initializationStarted;
    private void Initialize()
    {
        // Loaded also fires after hiding/reopening. Never recreate trays/panels.
        if (initializationStarted || disposed) return;
        initializationStarted = true;
        try
        {
            if (!demo)
            {
                var legacy = new LegacyLayoutRecovery(storage);
                if (legacy.HasRecovery) legacy.Restore();
            }
            tray = new ResidentTray(storage.Root, TrayAction);
            tray.EnvironmentChanged += SignalEnvironment;
            foreach (var drawer in catalog.Settings.Drawers.ToArray())
                AddRuntime(drawer);
            initializing = false;
            SelectDrawer(catalog.Settings.SelectedId ?? rows.FirstOrDefault()?.Id ?? "");
            RecreateDesktopWatcher();
            recoveryPolicy.BeginStartup(DateTimeOffset.UtcNow);
            RecoverDesktop(true); desktopTimer.Start();
            foreach (var pending in catalog.Settings.Drawers.Where(d => d.Removing).ToArray()) _ = RemoveDrawerAsync(pending.Id);
            _ = ScanRecoveryAsync();
            if (demo) Status("样例使用独立文件。关闭面板后可在系统托盘重新打开；选择退出会归还样例内容。");
            else Status(runtimes.Values.FirstOrDefault(r => r.Issue != null)?.Issue ?? "抽屉已就绪。关闭面板后仍在托盘运行。");
            // Paint drawers before doing startup registration or loading shell icons.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                if (disposed) return;
                Try(() => startup?.UpgradeLegacyRegistration(storage));
                RefreshStartup();
            }));
            if (background && tray?.Available != true) ShowControl();
        }
        catch (Exception exception) { initializing = false; Status("初始化未完成：" + exception.Message); ShowControl(); }
    }

    private void AddRuntime(DrawerDefinition definition)
    {
        var store = catalog.Stores[definition.Id];
        var row = new DrawerRow(definition, store);
        DrawerRuntime? runtime = null;
        var panel = new ShortcutPanel(Status, (_, _) => { if (runtime != null) SaveLayout(runtime); },
            paths => !demo && QueueAddPaths(definition.Id, paths),
            paths =>
            {
                if (closing || demo || definition.Removing || runtime?.Busy == true) return false;
                try { catalog.EnsureOwnership(definition.Id, paths); return store.CanAccept(paths); }
                catch (Exception exception) when (exception is IOException or ArgumentException) { return false; }
            },
            path =>
            {
                if (demo) { Status("样例内容仅在样例目录内使用。"); return; }
                if (runtime != null) _ = RunStoreAsync(runtime, "归还桌面并解除分组", copy => { copy.ReturnAndUngroup(path); return []; });
            });
        runtime = new DrawerRuntime(definition, row, panel);
        panel.Configure(definition.Name, store.Profile.Width, store.Profile.Height, store.Profile.Locked);
        panel.RestoreFolded(store.Profile.Folded);
        panel.FoldedChanged += folded =>
        {
            store.Profile.Folded = folded; if (!runtime.Busy) store.Save(); UpdateRow(runtime);
            if (Selected?.Id == definition.Id)
            {
                AlwaysModeButton.IsChecked = !folded; HoverModeButton.IsChecked = folded;
                SelectedSubtitle.Text = $"{runtime.Row.Placement} · {runtime.Row.State}";
            }
            RefreshOverview();
        };
        panel.TransformLayout = (rect, edges) => Transform(runtime, rect, edges);
        panel.InteractionEnded += () => { runtime.Snap = new(); guides.Hide(); };
        panel.LayoutCommitted += () => { SaveLayout(runtime); UpdateDetail(); };
        panel.ManageRequested += () => { SelectDrawer(definition.Id); ShowControl(); };
        panel.ItemMenuRequested += path => OpenDrawerItemMenu(runtime, path);
        panel.Disposed += () => { if (itemMenu != null) itemMenu.IsOpen = false; };
        store.Changed += () => RefreshItems(runtime);
        rows.Add(row); runtimes.Add(definition.Id, runtime);
        RefreshItems(runtime);
    }

    private LayoutRect Transform(DrawerRuntime runtime, LayoutRect rect, int edges)
    {
        var monitors = DesktopPlacement.Capture();
        if (monitors.Count == 0) return rect;
        var monitor = monitors.OrderByDescending(m =>
            Math.Max(0, Math.Min(rect.Right, m.Work.Right) - Math.Max(rect.X, m.Work.X)) *
            Math.Max(0, Math.Min(rect.Bottom, m.Work.Bottom) - Math.Max(rect.Y, m.Work.Y)))
            .ThenBy(m => Math.Pow(rect.X - m.Work.X, 2) + Math.Pow(rect.Y - m.Work.Y, 2)).First();
        var scale = monitor.Dpi / 96d;
        var work = new LayoutRect(monitor.Work.X, monitor.Work.Y, monitor.Work.Width, monitor.Work.Height);
        var neighbors = runtimes.Values.Where(r => r != runtime && r.Panel.IsAttached)
            .Select(r => r.Panel.ScreenBounds).Where(r => r.Right >= work.X && r.X <= work.Right && r.Bottom >= work.Y && r.Y <= work.Bottom).ToArray();
        var result = runtime.Snap.Resolve(rect, work, neighbors, catalog.Settings.SnapEnabled,
            (GetAsyncKeyState(0x12) & 0x8000) != 0, scale, edges);
        var next = result.Rect;
        var valid = next.Width >= 244 * scale && ((edges & 12) == 0 || next.Height >= 160 * scale)
            && next.Width <= 1400 * scale && next.Height <= 1100 * scale;
        if (!valid) { guides.Hide(); return rect; }
        guides.Show(result, monitor.Work, scale);
        return next;
    }

    private void SaveLayout(DrawerRuntime runtime)
    {
        if (!runtime.Panel.IsAttached || recoveringLayout) return;
        var rect = runtime.Panel.ScreenBounds; var profile = runtime.Row.Store.Profile;
        profile.X = (int)Math.Round(rect.X); profile.Y = (int)Math.Round(rect.Y);
        profile.Width = runtime.Panel.LogicalWidth; profile.Height = runtime.Panel.LogicalHeight;
        if (!runtime.Busy) runtime.Row.Store.Save();
        if (Selected?.Id == runtime.Definition.Id) FillGeometry(runtime);
        RefreshOverview();
    }

    private void RefreshItems(DrawerRuntime runtime)
    {
        var active = runtime.Row.Store.Active;
        var signature = string.Join("|", runtime.Row.Store.Profile.Entries.Select(e => e.Id + ":" + e.Phase + ":" + e.StoredRelative + ":" + e.Hash));
        if (runtime.ItemsSignature != signature)
        {
            runtime.ItemsSignature = signature;
            runtime.Panel.SetPreparedItems(active.Select(e => (Path.GetFileNameWithoutExtension(e.OriginalPath), runtime.Row.Store.StoredPath(e), (BitmapSource?)null)));
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => { if (!disposed) _ = LoadRuntimeIconsAsync(runtime, signature); }));
        }
        UpdateRow(runtime);
        if (Selected?.Id == runtime.Definition.Id) FillContents(runtime.Row.Store);
    }
    private void UpdateRow(DrawerRuntime runtime)
    {
        runtime.Row.Issue = runtime.Issue; RefreshRowPresentation(runtime); runtime.Row.Refresh();
        if (Selected?.Id == runtime.Definition.Id) FillIssue(runtime);
    }
    private void FillIssue(DrawerRuntime runtime)
    {
        DrawerIssueLabel.Text = runtime.Issue ?? "";
        DrawerIssueLabel.Visibility = runtime.Issue == null ? Visibility.Collapsed : Visibility.Visible;
    }
    private void RecoverDesktop(bool force)
    {
        if (closing) return;
        tray?.EnsureAvailable();
        var deferred = false;
        var failed = false;
        recoveringLayout = true;
        try
        {
        foreach (var runtime in runtimes.Values.ToArray())
        {
            if (runtime.Panel.IsInteracting) { deferred |= force; continue; }
            try
            {
                var p = runtime.Row.Store.Profile;
                if (force || !runtime.Panel.IsAttached)
                    runtime.Panel.ApplyScreenLayout(p.X, p.Y, p.Width, p.Height);
                else runtime.Panel.EnsureAttached(false);
                if (!runtime.Started && !runtime.Definition.Removing)
                {
                    runtime.Started = true;
                    _ = RunStoreAsync(runtime, "恢复分组收纳", store => store.StartSession());
                }
            }
            catch (Exception exception) { failed = true; environmentIssue = "桌面连接暂不可用：" + exception.Message; }
        }
        }
        finally { recoveringLayout = false; }
        recoveryPolicy.Complete(!failed && !deferred, DateTimeOffset.UtcNow);
        if (recoveryPolicy.Pending) environmentTimer.Start();
        if (!failed) environmentIssue = "";
        if (!failed && !deferred && runtimes.Values.All(r => r.Panel.IsAttached)) performance.MarkReady();
        var attached = runtimes.Values.Count(r => r.Panel.IsAttached);
        var state = $"桌面守护中 · {attached}/{runtimes.Count} 个抽屉";
        ConnectionLabel.Text = state;
        if (lastConnection != state || force)
        {
            lastConnection = state;
            RecordDesktopLifetime(state);
            RefreshOverview();
        }
    }
    private async void Synchronize()
    {
        syncTimer.Stop();
        if (closing) return;
        var retry = false;
        foreach (var runtime in runtimes.Values.Where(r => r.Started && !r.Paused && !r.Definition.Removing).ToArray())
        {
            if (runtime.Busy) { retry = true; continue; }
            retry |= !await RunStoreAsync(runtime, "同步桌面入口", store => store.Synchronize());
        }
        if (retry && ++syncRetries <= 3) syncTimer.Start();
    }
    private void WatchChanged(object sender, FileSystemEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (closing || !ShortcutInfo.IsShortcut(e.FullPath)) return;
        syncRetries = 0; syncTimer.Stop(); syncTimer.Start();
    });
    private void Issues(DrawerRuntime runtime, IReadOnlyList<string> issues, bool clear = true)
    {
        var previous = runtime.Issue;
        if (issues.Count > 0 || clear) runtime.Issue = issues.Count > 0 ? string.Join("；", issues) : null;
        if (issues.Count > 0 && previous != runtime.Issue) { Status(runtime.Definition.Name + "：" + runtime.Issue); if (!demo) ShowControl(); }
        UpdateRow(runtime);
    }

    private DrawerRow? Selected => DrawerList.SelectedItem as DrawerRow;
    private DrawerRuntime? SelectedRuntime => Selected is { } row ? runtimes.GetValueOrDefault(row.Id) : null;
    internal void SelectDrawer(string id)
    {
        var target = rows.FirstOrDefault(r => r.Id == id) ?? rows.FirstOrDefault();
        if (target != null && studioRows?.Contains(target) != true) SearchBox.Clear();
        DrawerList.SelectedItem = target;
        UpdateDetail();
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing) return;
        catalog.Settings.SelectedId = Selected?.Id; catalog.Save(); UpdateDetail();
    }
    private void UpdateDetail()
    {
        var row = Selected;
        DetailView.Visibility = row == null ? Visibility.Collapsed : Visibility.Visible;
        EmptyView.Visibility = row == null ? Visibility.Visible : Visibility.Collapsed;
        if (row == null) { currentContents = []; FillStudioContents(); RefreshOverview(); return; }
        updating = true;
        try
        {
            SelectedTitle.Text = row.Name; NameBox.Text = row.Name;
            LockBox.IsChecked = row.Store.Profile.Locked;
            AlwaysModeButton.IsChecked = !row.Store.Profile.Folded; HoverModeButton.IsChecked = row.Store.Profile.Folded;
            ApplyLayoutButton.IsEnabled = !row.Store.Profile.Locked;
            foreach (var box in new[] { XBox, YBox, WidthBox, HeightBox }) box.IsEnabled = !row.Store.Profile.Locked;
            if (SelectedRuntime is { } runtime) { FillGeometry(runtime); FillIssue(runtime); }
            FillContents(row.Store);
        }
        finally { updating = false; }
        RefreshOverview();
    }
    private void FillGeometry(DrawerRuntime runtime)
    {
        var p = runtime.Row.Store.Profile;
        XBox.Text = p.X.ToString(CultureInfo.InvariantCulture); YBox.Text = p.Y.ToString(CultureInfo.InvariantCulture);
        WidthBox.Text = Math.Round(p.Width).ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = Math.Round(p.Height).ToString(CultureInfo.InvariantCulture);
    }
    private void FillContents(ShortcutStore store)
    {
        currentContents = BuildContents(store);
        FillStudioContents();
    }
    private void RefreshOverview()
    {
        Overview.Monitors = DesktopPlacement.Capture();
        Overview.Drawers = runtimes.Values.Select(r => (r.Definition.Id, r.Definition.Name,
            r.Panel.IsAttached ? r.Panel.ScreenBounds : new LayoutRect(r.Row.Store.Profile.X, r.Row.Store.Profile.Y,
                r.Row.Store.Profile.Width, r.Row.Store.Profile.Folded ? 46 : r.Row.Store.Profile.Height))).ToArray();
        Overview.SelectedId = Selected?.Id; Overview.InvalidateVisual(); RefreshManagerViews();
    }
    public void ShowControl() { Show(); WindowState = WindowState.Normal; Activate(); RefreshStartup(); }
    private void Status(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Status(message)); return; }
        StatusLabel.Text = message;
    }
    private void Try(Action action)
    {
        try { action(); }
        catch (Exception exception) { Status(exception.Message); }
    }
    private void NewDrawerClick(object sender, RoutedEventArgs e)
    {
        CreateErrorLabel.Visibility = Visibility.Collapsed; ShowModal(CreateBox); NewNameBox.Text = ""; NewNameBox.Focus();
    }
    private void CancelCreateClick(object sender, RoutedEventArgs e) => DismissModal();
    private void CreateClick(object sender, RoutedEventArgs e)
    {
        try { CreateDrawer(NewNameBox.Text); }
        catch (Exception exception) { CreateErrorLabel.Text = exception.Message; CreateErrorLabel.Visibility = Visibility.Visible; NewNameBox.Focus(); }
    }
    internal void CreateDrawer(string name)
    {
        if (closing || exitAfterOperations) throw new InvalidOperationException("正在退出，请等待文件归还完成。");
        var definition = catalog.Create(name); AddRuntime(definition);
        DismissModal();
        SelectDrawer(definition.Id); RecoverDesktop(false); Status($"已创建「{definition.Name}」，可到桌面拖动和调整边缘。");
    }
    private void NewNameKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) CreateClick(sender, e); if (e.Key == Key.Escape) CancelCreateClick(sender, e); }
    private void RenameKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) RenameClick(sender, e); }
    private void RenameClick(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (SelectedRuntime is not { } runtime) return;
        catalog.Rename(runtime.Definition.Id, NameBox.Text);
        var p = runtime.Row.Store.Profile;
        runtime.Panel.Configure(runtime.Definition.Name, p.Width, p.Height, p.Locked);
        runtime.Row.Refresh(); UpdateDetail(); Status("名称已保存。");
    });
    private void LocateClick(object sender, RoutedEventArgs e) => Try(LocateSelected);
    private void LocateSelected()
    {
        RecoverDesktop(true);
        if (SelectedRuntime is not { } r || !r.Panel.IsAttached) { Status("正在等待桌面恢复。"); return; }
        // A brief frame accent identifies the selected drawer without changing saved folding.
        r.Panel.Highlight();
        if (demo) WindowState = WindowState.Minimized;
        else if (tray?.EnsureAvailable() == true) Hide();
    }
    private void SnapClick(object sender, RoutedEventArgs e) => Try(() =>
    {
        catalog.Settings.SnapEnabled = SnapBox.IsChecked == true; catalog.Save();
        guides.Hide(); Status(catalog.Settings.SnapEnabled ? "吸附已开启。按住 Alt 可临时自由拖动。" : "吸附已关闭。");
    });
    private void LockClick(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (updating || SelectedRuntime is not { } r) return;
        var p = r.Row.Store.Profile; p.Locked = LockBox.IsChecked == true; if (!r.Busy) r.Row.Store.Save();
        r.Panel.Configure(r.Definition.Name, p.Width, p.Height, p.Locked);
        r.Row.Refresh(); UpdateDetail(); Status(p.Locked ? "布局已锁定，内容仍可打开和滚动。" : "已解除布局锁定。");
    });
    private void ApplyLayoutClick(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (SelectedRuntime is not { } r || r.Row.Store.Profile.Locked) return;
        if (!int.TryParse(XBox.Text, out var x) || !int.TryParse(YBox.Text, out var y)
            || !double.TryParse(WidthBox.Text, out var width) || !double.TryParse(HeightBox.Text, out var height)
            || !double.IsFinite(width) || !double.IsFinite(height) || width is < 244 or > 1400 || height is < 160 or > 1100
            || x is < -100000 or > 100000 || y is < -100000 or > 100000)
            throw new ArgumentException("请输入有效位置；宽度 244–1400，高度 160–1100。");
        r.Panel.ApplyScreenLayout(x, y, width, height); SaveLayout(r); Status("布局已保存。");
    });
    private void AddSelectedClick(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (demo || Selected == null) return;
        var selection = NativeIconLayout.ReadSelection();
        if (selection.Count == 0) { Status("先在真实桌面选择快捷方式，再点击收纳。"); return; }
        QueueAddPaths(Selected.Id, selection.Select(i => i.Identity).ToArray());
    });
    internal bool QueueAddPaths(string id, IReadOnlyList<string> paths)
    {
        var r = runtimes[id];
        if (closing || exitAfterOperations || r.Busy || !r.Panel.IsAttached || r.Definition.Removing) return false;
        catalog.EnsureOwnership(id, paths);
        if (runtimes.Values.Any(other => other != r && other.PendingPaths.Intersect(paths, StringComparer.OrdinalIgnoreCase).Any()))
        { Status("其中有入口正由另一个抽屉收纳，请稍后重试。"); return false; }
        r.PendingPaths = paths.ToArray(); r.Paused = false;
        _ = Collect(); return true;
        async Task Collect()
        {
            try { await RunStoreAsync(r, "收纳入口", store =>
            {
                var issues = new List<string>();
                foreach (var path in paths) { try { store.Add(path); } catch (Exception e) { issues.Add(Path.GetFileName(path) + "：" + e.Message); } }
                return issues;
            }); }
            finally { r.PendingPaths = []; }
        }
    }
    private void RecollectClick(object sender, RoutedEventArgs e) => Try(() =>
    {
        RecoverDesktop(false);
        if (SelectedRuntime is not { } r || !r.Panel.IsAttached || r.Definition.Removing) return;
        r.Paused = false; _ = RunStoreAsync(r, "重新收纳", store => store.StartSession());
    });
    private void RestoreClick(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (SelectedRuntime is not { } r) return;
        r.Paused = true; _ = RunStoreAsync(r, "归还此抽屉", store => store.RestoreAll());
    });
    private void OpenFolderClick(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (Selected is not { } row) return;
        var folder = storage.Resolve($"storage/{row.Id}"); Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    });
    private void RemoveClick(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (Selected is not { } row) return;
        if (MessageBox.Show(this, $"将「{row.Name}」的内容归还桌面并移除抽屉？历史恢复记录会保留。",
            "移除抽屉", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _ = RemoveDrawerAsync(row.Id);
    });
    private bool RemoveDrawer(string id)
    {
        var r = runtimes[id]; r.Paused = true;
        if (r.Busy) { Status("抽屉仍有文件操作进行中，完成后才能移除。"); return false; }
        var issues = catalog.CompleteRemoval(id);
        if (issues.Count > 0) { Issues(r, issues); Status("归还未完成，抽屉及恢复记录已保留。"); return false; }
        r.Panel.Dispose(); runtimes.Remove(id); rows.Remove(r.Row);
        _ = ScanRecoveryAsync();
        SelectDrawer(rows.FirstOrDefault()?.Id ?? ""); RecoverDesktop(false); Status("已归还内容并移除抽屉。");
        return true;
    }
    private void DemoClick(object sender, RoutedEventArgs e) => Try(() => { if (demo) Status("当前已是隔离样例。"); else Process.Start(AppLaunch.StartInfo(storage, "--demo")); });
    private void StartupClick(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (updating) return;
        try { startup?.SetEnabled(StartupBox.IsChecked == true); Status("启动设置已保存。"); }
        finally { RefreshStartup(); }
    });
    private void RefreshStartup()
    {
        if (startup == null || StartupBox == null) return;
        updating = true;
        try { StartupBox.IsChecked = startup.Enabled; }
        catch (Exception exception) { Status(exception.Message); }
        finally { updating = false; }
    }
    internal void RequestExit() { exitRequested = true; Close(); }
    private void ExitClick(object sender, RoutedEventArgs e) => RequestExit();
    private void TrayAction(TrayCommand command) => Try(() =>
    {
        if (closing && command != TrayCommand.Show) return;
        switch (command)
        {
            case TrayCommand.Show: ShowControl(); break;
            case TrayCommand.Settings: ShowControl(); ShowSettings(); break;
            case TrayCommand.Exit: RequestExit(); break;
        }
    });
    private bool exitReady, restoringExit;
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!exitRequested && !sessionEnding)
        {
            e.Cancel = true;
            ShowModal(CloseBox);
            return;
        }
        if (exitReady || sessionEnding) { DisposeRuntime(); return; }
        e.Cancel = true;
        if (restoringExit) return;
        if (BusyOperations > 0)
        {
            exitAfterOperations = true; Status("正在完成文件操作，完成后自动归还并退出。"); return;
        }
        restoringExit = closing = true; syncTimer.Stop(); desktopTimer.Stop(); environmentTimer.Stop(); guides.Hide();
        if (itemMenu != null) itemMenu.IsOpen = false;
        var issues = new List<string>();
        foreach (var r in runtimes.Values.ToArray())
        {
            r.Paused = true;
            if (!await RunStoreAsync(r, "退出归还", store => store.RestoreAll(), duringExit: true)) issues.Add(r.Definition.Name + "：" + r.Issue);
        }
        restoringExit = false;
        if (issues.Count > 0)
        {
            closing = exitRequested = false;
            Status(string.Join("；", issues));
            DismissModal(); ShowControl(); ShowSettings(SettingsSection.Recovery); await ScanRecoveryAsync(); desktopTimer.Start(); return;
        }
        SamplePerformance(); exitReady = true;
        _ = Dispatcher.BeginInvoke(Close);
    }
    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        sessionEnding = closing = true; syncTimer.Stop(); desktopTimer.Stop(); environmentTimer.Stop();
        var issues = new List<string>();
        foreach (var r in runtimes.Values)
        {
            try { issues.AddRange(fileWorker.Run(() => r.Row.Store.Detached().RestoreAll()).GetAwaiter().GetResult()); }
            catch (Exception error) { issues.Add(error.Message); }
        }
        storage.WriteJson("logs/session-ending.json", new { At = DateTimeOffset.Now, Restored = issues.Count == 0, Issues = issues });
        DisposeRuntime();
    }
    private void DisposeRuntime()
    {
        if (disposed) return;
        DisposeHealth();
        watcher?.Dispose(); foreach (var r in runtimes.Values) r.Panel.Dispose();
        guides.Dispose(); tray?.Dispose(); Application.Current.SessionEnding -= OnSessionEnding;
    }
    private sealed class DrawerRuntime(DrawerDefinition definition, DrawerRow row, ShortcutPanel panel)
    {
        public DrawerDefinition Definition { get; } = definition;
        public DrawerRow Row { get; } = row;
        public ShortcutPanel Panel { get; } = panel;
        public LayoutSnapSession Snap { get; set; } = new();
        public bool Started, Paused, Busy;
        public IReadOnlyList<string> PendingPaths = [];
        public string? Issue, ItemsSignature;
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
