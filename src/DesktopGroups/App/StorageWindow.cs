using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopGroups;

internal sealed class StorageWindow : Window
{
    private readonly AppStorage storage;
    private readonly ShortcutStore store;
    private readonly bool demo;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush("#B6C0CE"), Margin = new Thickness(0, 16, 0, 12) };
    private readonly StackPanel members = new();
    private readonly DispatcherTimer syncTimer = new() { Interval = TimeSpan.FromMilliseconds(1000) };
    private readonly DispatcherTimer desktopTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer environmentTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly TextBlock connection = new() { Foreground = Brush("#94B7AC"), Margin = new Thickness(0, 3, 0, 10) };
    private readonly CheckBox startupBox = new() { Content = "开机启动（登录后收起到托盘）", Margin = new Thickness(0, 4, 0, 10), Foreground = Brush("#B6C0CE") };
    private readonly StartupRegistration? startup;
    private readonly bool background;
    private FileSystemWatcher? watcher;
    private ShortcutPanel? panel;
    private ResidentTray? tray;
    private bool paused, closing, started, exitRequested, sessionEnding, environmentPending, updatingStartup;
    private bool hasIssues;
    private string environmentReason = "启动";
    private string lastConnection = "";
    private int syncRetries;

    public StorageWindow(AppStorage storage, bool demo = false, bool background = false)
    {
        this.storage = storage; this.demo = demo; this.background = background;
        store = demo ? DemoContent.Create(storage) : new ShortcutStore(storage);
        if (!demo) startup = new StartupRegistration(storage);
        Title = demo ? "栖格 Qige · 样例" : $"栖格 Qige · {AppBrand.Version}";
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(AppBrand.IconUri);
        Width = 800; Height = 600; MinWidth = 700; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush("#1B1E24"); Foreground = Brush("#E8ECF2");
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13;
        var root = new DockPanel { Margin = new Thickness(28) }; Content = root;
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        heading.Children.Add(new TextBlock { Text = demo ? "先试试整体移动" : "你的常用入口", FontSize = 25, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock
        {
            Text = demo ? "样例只使用项目内文件。拖动桌面面板标题，图标会随面板一起移动。" : "关闭窗口收起到托盘；选择“退出并归还”才结束运行，下次启动按分组重新收纳。",
            Foreground = Brush("#9BA8BA"), Margin = new Thickness(0, 10, 0, 16), TextWrapping = TextWrapping.Wrap
        });
        var actions = new WrapPanel(); heading.Children.Add(actions);
        actions.Children.Add(Button("定位分区", LocatePanel));
        if (!demo) actions.Children.Add(Button("收纳选中", AddSelection));
        actions.Children.Add(Button("重新收纳", Recollect));
        actions.Children.Add(Button("全部归还", Restore));
        if (!demo) actions.Children.Add(Button("打开样例", () => Process.Start(AppLaunch.StartInfo(storage, "--demo"))));
        actions.Children.Add(Button("查看收纳目录", () =>
        {
            var folder = demo ? store.DesktopPath : storage.Resolve("storage/default");
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }));
        if (!demo) actions.Children.Add(Button("退出并归还", RequestExit));
        heading.Children.Add(connection);
        if (!demo)
        {
            heading.Children.Add(startupBox);
            startupBox.Click += (_, _) => ChangeStartup(startupBox.IsChecked == true);
            RefreshStartup();
        }
        heading.Children.Add(status);
        var footer = new TextBlock
        {
            Text = demo ? "样例不会移动你的桌面文件。关闭窗口会归还样例内容。" : "当前支持个人桌面的 .lnk／.url，最多 12 项。双击打开；悬停展开、离开收起。拖到桌面空白处归还并解除分组，Esc 取消拖出。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brush("#8A98AA"), Margin = new Thickness(0, 14, 0, 0)
        };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = members, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        store.Changed += Refresh;
        syncTimer.Tick += (_, _) =>
        {
            syncTimer.Stop();
            if (paused || closing) return;
            var issues = store.Synchronize();
            ShowIssues(issues, "已检查桌面重新生成的入口。");
            if (issues.Count > 0 && ++syncRetries <= 3) syncTimer.Start();
        };
        Loaded += (_, _) => Initialize();
        desktopTimer.Tick += (_, _) => RecoverDesktop(force: environmentPending);
        environmentTimer.Tick += (_, _) => { environmentTimer.Stop(); RecoverDesktop(force: true); };
        Closing += (_, args) =>
        {
            if (!demo && !exitRequested && !sessionEnding)
            {
                args.Cancel = true;
                if (tray?.EnsureAvailable() == true) Hide();
                else status.Text = "托盘暂不可用，已保留控制窗口。需要退出时请点“退出并归还”。";
                return;
            }
            closing = true; paused = true; syncTimer.Stop(); desktopTimer.Stop(); environmentTimer.Stop();
            var issues = store.RestoreAll();
            if (issues.Count > 0 && !sessionEnding)
            {
                args.Cancel = true; closing = exitRequested = false;
                ShowControl(); ShowIssues(issues, ""); desktopTimer.Start(); return;
            }
            watcher?.Dispose(); panel?.Dispose(); tray?.Dispose();
        };
        Application.Current.SessionEnding += (_, _) =>
        {
            sessionEnding = closing = true; syncTimer.Stop(); desktopTimer.Stop(); environmentTimer.Stop();
            var issues = store.RestoreAll();
            storage.WriteJson("logs/session-ending.json", new { At = DateTimeOffset.Now, Restored = issues.Count == 0, Issues = issues });
            watcher?.Dispose(); panel?.Dispose(); tray?.Dispose();
        };
    }

    private void Initialize()
    {
        try
        {
            if (!demo)
            {
                startup?.UpgradeLegacyRegistration(storage);
                RefreshStartup();
                tray = new ResidentTray(storage.Root, TrayAction, () => startup?.Enabled == true);
                tray.EnvironmentChanged += QueueEnvironmentChange;
            }
            if (!demo)
            {
                var legacy = new LegacyLayoutRecovery(storage);
                if (legacy.HasRecovery) legacy.Restore();
            }
            panel = new ShortcutPanel(message => Dispatcher.BeginInvoke(() => status.Text = message), (x, y) =>
            {
                store.Profile.X = x; store.Profile.Y = y; store.Save();
                storage.WriteJson("logs/storage-panel-latest.json", new
                {
                    panel!.SurfaceBuilds, panel.MoveCount, Items = store.Active.Count,
                    X = x, Y = y, ShellIconRepositionCalls = 0
                });
            }, paths =>
            {
                if (demo) { status.Text = "样例不接收真实桌面文件；请在收纳原型中添加。"; return false; }
                return AddPaths(paths);
            }, paths => !demo && store.CanAccept(paths), path =>
            {
                if (demo) { status.Text = "样例不拖回真实桌面，请在收纳原型中操作。"; return; }
                try
                {
                    store.ReturnAndUngroup(path);
                    status.Text = "已归还桌面并解除分组，下次启动不再自动收纳此入口。";
                }
                catch (Exception exception) { status.Text = $"拖回未完成：{exception.Message}"; }
            });
            panel.RestoreFolded(store.Profile.Folded);
            panel.FoldedChanged += value => { store.Profile.Folded = value; store.Save(); };
            watcher = new FileSystemWatcher(store.DesktopPath)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false
            };
            watcher.Created += WatchChanged; watcher.Changed += WatchChanged; watcher.Renamed += WatchChanged;
            watcher.Error += (_, _) => Dispatcher.BeginInvoke(() => { if (!paused && !closing) { syncRetries = 0; syncTimer.Start(); } });
            watcher.EnableRaisingEvents = true;
            RecoverDesktop(force: true);
            desktopTimer.Start();
            if (background && tray?.Available == true && !hasIssues) Hide();
            else ShowControl();
        }
        catch (Exception exception) { status.Text = $"分区尚未就绪：{exception.Message}。未开始新的收纳。"; ShowControl(); }
    }

    private void RecoverDesktop(bool force)
    {
        if (closing || panel == null) return;
        tray?.EnsureAvailable();
        if (panel.IsInteracting) { environmentPending |= force; return; }
        try
        {
            var previous = panel.AttachmentCount;
            if (previous == 0) panel.Show(store.Profile.X, store.Profile.Y);
            else panel.EnsureAttached(force);
            var recovered = previous != panel.AttachmentCount;
            environmentPending = false;
            SetConnection("已连接桌面", recovered || force);
            if (!started)
            {
                started = true;
                ShowIssues(store.StartSession(), demo ? "样例已就绪。点击“定位分区”查看桌面面板。" : "分区已就绪。关闭窗口可收起到托盘。再次打开程序可找回控制窗口。");
                SetConnection("已连接桌面", record: true);
                if (background && tray?.Available == true && !hasIssues) Hide();
            }
            else if (force && !paused) ShowIssues(store.Synchronize(), $"已检查{environmentReason}后的桌面入口。");
        }
        catch (Exception exception)
        {
            SetConnection($"正在等待桌面恢复：{exception.Message}");
            if (!started) status.Text = "桌面就绪后会自动收纳；当前未开始新的收纳。";
        }
    }

    private void SetConnection(string message, bool record = false)
    {
        connection.Text = message;
        if (message == lastConnection && !record) return;
        lastConnection = message;
        storage.WriteJson(demo ? "samples/demo-lifetime.json" : "logs/desktop-lifetime.json", new
        {
            At = DateTimeOffset.Now, State = message, Reason = environmentReason, Started = started,
            Attached = panel?.IsAttached == true, panel?.AttachmentCount, TrayAvailable = tray?.Available == true,
            TrayRegistrations = tray?.RegistrationCount ?? 0, AttachmentRebuildMovesFiles = false
        });
    }

    private void QueueEnvironmentChange(string reason)
    {
        if (closing) return;
        environmentReason = reason; environmentPending = true;
        environmentTimer.Stop(); environmentTimer.Start();
    }

    public void ShowControl()
    {
        Show(); WindowState = WindowState.Normal; Activate(); RefreshStartup();
    }

    private void LocatePanel()
    {
        RecoverDesktop(force: true);
        if (panel?.IsAttached != true) { ShowControl(); return; }
        if (demo) WindowState = WindowState.Minimized;
        else if (tray?.EnsureAvailable() == true) Hide();
    }

    private void Restore()
    {
        paused = true; syncTimer.Stop();
        ShowIssues(store.RestoreAll(), demo ? "样例已归还项目内的样例目录。" : "已归还桌面，分组记忆仍保留。点击“重新收纳”恢复。");
    }

    private void Recollect()
    {
        RecoverDesktop(force: true);
        if (panel?.IsAttached != true) { status.Text = "桌面尚未就绪，未开始新的收纳。"; return; }
        paused = false; ShowIssues(store.StartSession(), "已按分组重新收纳。");
    }

    private void RequestExit() { exitRequested = true; Close(); }

    private void TrayAction(TrayCommand action)
    {
        try
        {
            switch (action)
            {
                case TrayCommand.Show: ShowControl(); break;
                case TrayCommand.Locate: LocatePanel(); break;
                case TrayCommand.Restore: Restore(); if (store.Active.Count != 0) ShowControl(); break;
                case TrayCommand.Recollect: Recollect(); break;
                case TrayCommand.Startup: ChangeStartup(startup?.Enabled != true); break;
                case TrayCommand.Exit: RequestExit(); break;
            }
        }
        catch (Exception exception) { status.Text = exception.Message; ShowControl(); }
    }

    private void ChangeStartup(bool enabled)
    {
        if (updatingStartup) return;
        try { startup?.SetEnabled(enabled); status.Text = enabled ? "已开启登录后启动，控制窗口自动收起到托盘。" : "已关闭开机启动。"; }
        catch (Exception exception) { status.Text = $"启动设置未完成：{exception.Message}"; ShowControl(); }
        RefreshStartup();
    }

    private void RefreshStartup()
    {
        if (startup == null) return;
        updatingStartup = true;
        try { startupBox.IsChecked = startup.Enabled; }
        catch (Exception exception) { startupBox.IsChecked = false; status.Text = $"无法读取启动设置：{exception.Message}"; }
        finally { updatingStartup = false; }
    }

    private void WatchChanged(object sender, FileSystemEventArgs args) => Dispatcher.BeginInvoke(() =>
    {
        if (paused || closing || !ShortcutInfo.IsShortcut(args.FullPath)) return;
        syncRetries = 0;
        syncTimer.Stop(); syncTimer.Start();
    });

    private void AddSelection()
    {
        try
        {
            var selection = NativeIconLayout.ReadSelection();
            if (selection.Count == 0) { status.Text = "先在真实桌面选择快捷方式，再回来点击“收纳选中”。"; return; }
            AddPaths(selection.Select(item => item.Identity).ToArray(), selection);
        }
        catch (Exception exception) { status.Text = exception.Message; }
    }

    private bool AddPaths(IReadOnlyList<string> paths, IReadOnlyList<IconPlacement>? positions = null)
    {
        if (panel?.IsAttached != true) { status.Text = "分区未创建，尚未移动文件。"; return false; }
        var issues = new List<string>(); var count = 0;
        foreach (var path in paths)
        {
            try { store.Add(path, positions?.FirstOrDefault(item => item.Identity.Equals(path, StringComparison.OrdinalIgnoreCase))); count++; }
            catch (Exception exception) { issues.Add($"{Path.GetFileName(path)}：{exception.Message}"); }
        }
        paused = false;
        Refresh(); ShowIssues(issues, $"已收纳 {count} 个快捷方式。拖动分区时不会重新排列其他桌面图标。");
        return count == paths.Count;
    }

    private void ShowIssues(IReadOnlyList<string> issues, string success)
    {
        hasIssues = issues.Count != 0;
        status.Text = issues.Count == 0 ? success : string.Join(Environment.NewLine, issues);
        Refresh();
        if (hasIssues && !demo && !IsVisible) ShowControl();
    }

    private void Refresh()
    {
        var active = store.Active;
        panel?.SetItems(active.Select(entry => (Path.GetFileNameWithoutExtension(entry.OriginalPath), store.StoredPath(entry), (string?)entry.OriginalPath)));
        members.Children.Clear();
        if (store.Profile.Entries.Count == 0)
            members.Children.Add(new TextBlock { Text = "还没有收纳的入口", Foreground = Brush("#8492A5"), Margin = new Thickness(4, 24, 0, 0) });
        foreach (var entry in store.Profile.Entries)
        {
            var row = new DockPanel { Margin = new Thickness(12) };
            var state = new TextBlock { Text = active.Contains(entry) ? "已收纳" : "桌面原位／待检查", Foreground = Brush("#94B7AC"), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(state, Dock.Right); row.Children.Add(state);
            row.Children.Add(new TextBlock { Text = Path.GetFileNameWithoutExtension(entry.OriginalPath), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 15, 0) });
            members.Children.Add(new Border { Child = row, Background = Brush("#242932"), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 7) });
        }
    }

    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Background = Brush("#303743"), Foreground = Brush("#EDF1F6"), BorderBrush = Brush("#455060"), Padding = new Thickness(13, 8, 13, 8), Margin = new Thickness(0, 0, 9, 8) };
        button.Click += (_, _) => { try { action(); } catch (Exception exception) { MessageBox.Show(exception.Message, "操作未完成"); } };
        return button;
    }
    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
}
