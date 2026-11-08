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
    private bool initializing = true, updating, closing, exitRequested, sessionEnding, environmentPending;
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
        Title = demo ? "栖格 Qige · 管理面板样例" : $"栖格 Qige · 抽屉管理 · {AppBrand.Version}";
        Icon = BitmapFrame.Create(AppBrand.IconUri); BrandIcon.Source = Icon;
        VersionLabel.Text = $"QIGE / {AppBrand.Version}";
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
        desktopTimer.Tick += (_, _) => RecoverDesktop(environmentPending);
        environmentTimer.Tick += (_, _) => { environmentTimer.Stop(); RecoverDesktop(true); };
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

    private void Initialize()
    {
        try
        {
            if (!demo)
            {
                startup?.UpgradeLegacyRegistration(storage);
                var legacy = new LegacyLayoutRecovery(storage);
                if (legacy.HasRecovery) legacy.Restore();
            }
            RefreshStartup();
            tray = new ResidentTray(storage.Root, TrayAction, () => startup?.Enabled == true, startupAvailable: !demo);
            tray.EnvironmentChanged += _ => { environmentPending = true; environmentTimer.Stop(); environmentTimer.Start(); };
            var pendingIssues = new Dictionary<string, IReadOnlyList<string>>();
            foreach (var pending in catalog.Settings.Drawers.Where(d => d.Removing).ToArray())
            {
                var issues = catalog.Remove(pending.Id);
                if (issues.Count > 0) pendingIssues[pending.Id] = issues;
            }
            foreach (var drawer in catalog.Settings.Drawers.ToArray())
            {
                AddRuntime(drawer);
                if (pendingIssues.TryGetValue(drawer.Id, out var issues)) Issues(runtimes[drawer.Id], issues);
            }
            initializing = false;
            SelectDrawer(catalog.Settings.SelectedId ?? rows.FirstOrDefault()?.Id ?? "");
            var desktop = catalog.Stores.Values.FirstOrDefault()?.DesktopPath
                ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (Directory.Exists(desktop))
            {
                watcher = new FileSystemWatcher(desktop)
                { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                watcher.Created += WatchChanged; watcher.Changed += WatchChanged; watcher.Renamed += WatchChanged;
                watcher.Error += (_, _) => Dispatcher.BeginInvoke(() => { if (!closing) { syncRetries = 0; syncTimer.Start(); } });
                watcher.EnableRaisingEvents = true;
            }
            RecoverDesktop(true); desktopTimer.Start();
            if (demo) Status("样例使用独立文件。关闭面板后可在系统托盘重新打开；选择退出会归还样例内容。");
            else Status(runtimes.Values.FirstOrDefault(r => r.Issue != null)?.Issue ?? "抽屉已就绪。关闭面板后仍在托盘运行。");
            if (background && tray?.Available == true && runtimes.Values.All(r => r.Issue == null)) Hide();
        }
        catch (Exception exception) { initializing = false; Status("初始化未完成：" + exception.Message); ShowControl(); }
    }

    private void AddRuntime(DrawerDefinition definition)
    {
        var store = catalog.Stores[definition.Id];
        var row = new DrawerRow(definition, store);
        DrawerRuntime? runtime = null;
        var panel = new ShortcutPanel(Status, (_, _) => { if (runtime != null) SaveLayout(runtime); },
            paths => !demo && AddPaths(definition.Id, paths),
            paths =>
            {
                if (demo || definition.Removing) return false;
                try { catalog.EnsureOwnership(definition.Id, paths); return store.CanAccept(paths); }
                catch (Exception exception) when (exception is IOException or ArgumentException) { return false; }
            },
            path =>
            {
                if (demo) { Status("样例内容仅在样例目录内使用。"); return; }
                try { store.ReturnAndUngroup(path); Status("已归还桌面并解除分组。"); }
                catch (Exception exception) { Status(exception.Message); }
            });
        runtime = new DrawerRuntime(definition, row, panel);
        panel.Configure(definition.Name, store.Profile.Width, store.Profile.Height, store.Profile.Locked);
        panel.RestoreFolded(store.Profile.Folded);
        panel.FoldedChanged += folded =>
        {
            store.Profile.Folded = folded; store.Save(); UpdateRow(runtime);
            if (Selected?.Id == definition.Id)
            {
                FoldBox.IsChecked = folded;
                AlwaysModeButton.IsChecked = !folded; HoverModeButton.IsChecked = folded;
                SelectedSubtitle.Text = $"{runtime.Row.Placement} · {runtime.Row.State}";
            }
            RefreshOverview();
        };
        panel.TransformLayout = (rect, edges) => Transform(runtime, rect, edges);
        panel.InteractionEnded += () => { runtime.Snap = new(); guides.Hide(); };
        panel.LayoutCommitted += () => { SaveLayout(runtime); UpdateDetail(); };
        panel.ManageRequested += () => { SelectDrawer(definition.Id); ShowControl(); };
        store.Changed += () => { if (!runtime.Batching) RefreshItems(runtime); };
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
        if (!runtime.Panel.IsAttached) return;
        var rect = runtime.Panel.ScreenBounds; var profile = runtime.Row.Store.Profile;
        profile.X = (int)Math.Round(rect.X); profile.Y = (int)Math.Round(rect.Y);
        profile.Width = runtime.Panel.LogicalWidth; profile.Height = runtime.Panel.LogicalHeight;
        runtime.Row.Store.Save();
        if (Selected?.Id == runtime.Definition.Id) FillGeometry(runtime);
        RefreshOverview();
    }

    private void RefreshItems(DrawerRuntime runtime)
    {
        var active = runtime.Row.Store.Active;
        var signature = string.Join("|", active.Select(e => e.StoredRelative + ":" + e.Hash));
        if (runtime.ItemsSignature != signature)
        {
            runtime.ItemsSignature = signature;
            runtime.Panel.SetItems(active.Select(e => (Path.GetFileNameWithoutExtension(e.OriginalPath), runtime.Row.Store.StoredPath(e), (string?)e.OriginalPath)));
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
        foreach (var runtime in runtimes.Values.ToArray())
        {
            if (runtime.Panel.IsInteracting) { deferred |= force; continue; }
            try
            {
                if (!runtime.Panel.IsAttached)
                    runtime.Panel.ShowScreen(runtime.Row.Store.Profile.X, runtime.Row.Store.Profile.Y);
                else runtime.Panel.EnsureAttached(force);
                if (!runtime.Started && !runtime.Definition.Removing)
                {
                    runtime.Started = true;
                    Issues(runtime, runtime.Row.Store.StartSession());
                }
                else if (force && !runtime.Paused && !runtime.Definition.Removing) Issues(runtime, runtime.Row.Store.Synchronize(), clear: false);
            }
            catch (Exception exception) { Issues(runtime, [exception.Message], clear: false); }
        }
        environmentPending = deferred;
        var attached = runtimes.Values.Count(r => r.Panel.IsAttached);
        var state = $"桌面守护中 · {attached}/{runtimes.Count} 个抽屉";
        ConnectionLabel.Text = state;
        if (lastConnection != state || force)
        {
            lastConnection = state;
            storage.WriteJson("logs/desktop-lifetime.json", new { At = DateTimeOffset.Now, State = state,
                Drawers = runtimes.Values.Select(r => new { r.Definition.Id, r.Definition.Name, Attached = r.Panel.IsAttached, r.Panel.AttachmentCount, r.Issue }),
                TrayAvailable = tray?.Available == true });
            RefreshOverview();
        }
    }
    private void Synchronize()
    {
        syncTimer.Stop();
        if (closing) return;
        var retry = false;
        foreach (var runtime in runtimes.Values.Where(r => r.Started && !r.Paused && !r.Definition.Removing))
        {
            var issues = runtime.Row.Store.Synchronize();
            Issues(runtime, issues, clear: false); retry |= issues.Count > 0;
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
        if (row == null) { currentContents = []; ContentsList.ItemsSource = null; FillStudioContents(); RefreshOverview(); return; }
        updating = true;
        try
        {
            SelectedTitle.Text = row.Name; NameBox.Text = row.Name;
            FoldBox.IsChecked = row.Store.Profile.Folded; LockBox.IsChecked = row.Store.Profile.Locked;
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
        ContentsTitle.Text = $"内容透视 · {currentContents.Count}";
        NoContentsLabel.Visibility = currentContents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ContentsList.ItemsSource = currentContents;
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
    private void FoldClick(object sender, RoutedEventArgs e) => Try(() =>
    {
        SelectedRuntime?.Panel.SetFolded(FoldBox.IsChecked == true);
        UpdateDetail();
    });
    private void LockClick(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (updating || SelectedRuntime is not { } r) return;
        var p = r.Row.Store.Profile; p.Locked = LockBox.IsChecked == true; r.Row.Store.Save();
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
        AddPaths(Selected.Id, selection.Select(i => i.Identity).ToArray());
    });
    internal bool AddPaths(string id, IReadOnlyList<string> paths)
    {
        var r = runtimes[id]; var store = r.Row.Store;
        if (!r.Panel.IsAttached || r.Definition.Removing) { Status("抽屉尚未就绪，未移动文件。"); return false; }
        catalog.EnsureOwnership(id, paths);
        var issues = new List<string>(); var count = 0;
        r.Batching = true;
        try
        {
            foreach (var path in paths)
            {
                try { store.Add(path); count++; }
                catch (Exception exception) { issues.Add(Path.GetFileName(path) + "：" + exception.Message); }
            }
        }
        finally { r.Batching = false; RefreshItems(r); }
        r.Paused = false;
        Issues(r, issues);
        if (issues.Count == 0) Status($"已将 {count} 个入口收纳到「{r.Definition.Name}」。");
        return issues.Count == 0;
    }
    private void RecollectClick(object sender, RoutedEventArgs e) => Try(() =>
    {
        RecoverDesktop(false);
        if (SelectedRuntime is not { } r || !r.Panel.IsAttached || r.Definition.Removing) return;
        r.Paused = false; Issues(r, r.Row.Store.StartSession()); Status(r.Issue ?? "已按分组重新收纳。");
    });
    private void RestoreClick(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (SelectedRuntime is not { } r) return;
        r.Paused = true; Issues(r, r.Row.Store.RestoreAll()); Status(r.Issue ?? "已归还此抽屉，分组记忆保留。");
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
        RemoveDrawer(row.Id);
    });
    internal bool RemoveDrawer(string id)
    {
        var r = runtimes[id]; r.Paused = true;
        var issues = catalog.Remove(id);
        if (issues.Count > 0) { Issues(r, issues); Status("归还未完成，抽屉及恢复记录已保留。"); return false; }
        r.Panel.Dispose(); runtimes.Remove(id); rows.Remove(r.Row);
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
        switch (command)
        {
            case TrayCommand.Show: ShowControl(); break;
            case TrayCommand.Locate: LocateSelected(); break;
            case TrayCommand.Restore:
                foreach (var r in runtimes.Values) { r.Paused = true; Issues(r, r.Row.Store.RestoreAll()); }
                Status(runtimes.Values.FirstOrDefault(r => r.Issue != null)?.Issue ?? "全部内容已归还，分组记忆保留。"); break;
            case TrayCommand.Recollect:
                RecoverDesktop(false);
                foreach (var r in runtimes.Values.Where(r => r.Panel.IsAttached && !r.Definition.Removing))
                { r.Paused = false; Issues(r, r.Row.Store.StartSession()); }
                break;
            case TrayCommand.Startup: startup?.SetEnabled(startup.Enabled != true); RefreshStartup(); break;
            case TrayCommand.Exit: RequestExit(); break;
        }
    });
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!exitRequested && !sessionEnding)
        {
            e.Cancel = true;
            ShowModal(CloseBox);
            return;
        }
        closing = true; syncTimer.Stop(); desktopTimer.Stop(); environmentTimer.Stop(); guides.Hide();
        var issues = RestoreEverything();
        if (issues.Count > 0 && !sessionEnding)
        {
            e.Cancel = true; closing = exitRequested = false;
            Status(string.Join("；", issues));
            DismissModal(); Dispatcher.BeginInvoke(() => { if (!closing) ShowControl(); }); desktopTimer.Start(); return;
        }
        DisposeRuntime();
    }
    private List<string> RestoreEverything()
    {
        var issues = new List<string>();
        foreach (var r in runtimes.Values)
        {
            r.Paused = true;
            // One drawer's IO or presentation error must not prevent the others from restoring.
            try { issues.AddRange(r.Row.Store.RestoreAll().Select(i => r.Definition.Name + "：" + i)); }
            catch (Exception exception) { issues.Add(r.Definition.Name + "：" + exception.Message); }
        }
        return issues;
    }
    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        sessionEnding = closing = true; syncTimer.Stop(); desktopTimer.Stop(); environmentTimer.Stop();
        var issues = RestoreEverything();
        storage.WriteJson("logs/session-ending.json", new { At = DateTimeOffset.Now, Restored = issues.Count == 0, Issues = issues });
        DisposeRuntime();
    }
    private void DisposeRuntime()
    {
        watcher?.Dispose(); foreach (var r in runtimes.Values) r.Panel.Dispose();
        guides.Dispose(); tray?.Dispose(); Application.Current.SessionEnding -= OnSessionEnding;
    }
    private sealed class DrawerRuntime(DrawerDefinition definition, DrawerRow row, ShortcutPanel panel)
    {
        public DrawerDefinition Definition { get; } = definition;
        public DrawerRow Row { get; } = row;
        public ShortcutPanel Panel { get; } = panel;
        public LayoutSnapSession Snap { get; set; } = new();
        public bool Started, Paused, Batching;
        public string? Issue, ItemsSignature;
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
