using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopGroups;

internal static class ManagerChecks
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    private static void RightClickItem(ShortcutPanel panel, int index = 0)
    {
        var scale = panel.Width / panel.LogicalWidth;
        var x = (int)((30 + index % panel.Columns * (panel.LogicalWidth - 28) / panel.Columns) * scale);
        var y = (int)((75 + index / panel.Columns * 94 - panel.ScrollOffset) * scale);
        SendMessage(panel.Handle, 0x205, 0, (nint)((y << 16) | (x & 0xffff)));
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static void SaveManagerPreview(StorageWindow window, string path)
    {
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static void SaveMenuPreview(ContextMenu menu, string path)
    {
        menu.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth * 2), (int)Math.Ceiling(menu.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(menu);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    public static int Run(AppStorage storage)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            StorageWindow? window = null;
            try
            {
                var results = new List<string>();
                void Check(bool pass, string text) { if (!pass) throw new InvalidOperationException(text); results.Add("PASS " + text); }
                window = new StorageWindow(storage, demo: true);
                window.Show();
                await Task.Delay(500);
                await window.WaitForIdleAsync();
                Check(window.Panels.Count == 2 && window.Panels.All(p => p.IsAttached), "manager attaches both independent demo drawers");
                var beforeNotifications = window.Panels[0].SurfaceBuilds;
                for (var i = 0; i < 25; i++)
                {
                    window.SignalEnvironment("连续显示设置通知");
                    await Task.Delay(100);
                }
                Check(window.Panels[0].SurfaceBuilds > beforeNotifications && window.Panels.All(p => p.IsAttached),
                    "the real manager timer recovers its drawers during a continuous stream of environment notifications");
                await Task.Delay(400);
                await window.WaitForIdleAsync();
                var demoRoot = Directory.GetParent(window.Catalog.Stores["default"].DesktopPath)!.FullName;
                var lifetimeLog = Path.Combine(demoRoot, "logs", "desktop-lifetime.json");
                var savedLifetimeLog = lifetimeLog + ".saved";
                File.Move(lifetimeLog, savedLifetimeLog);
                Directory.CreateDirectory(lifetimeLog);
                try
                {
                    window.SignalEnvironment("日志不可写时的桌面恢复");
                    await Task.Delay(700);
                    await window.WaitForIdleAsync();
                    Check(window.Panels.All(p => p.IsAttached)
                        && ((TextBlock)window.FindName("StatusLabel")).Text.Contains("连接日志写入失败"),
                        "a failed diagnostic write reports the problem without interrupting desktop recovery or the dispatcher");
                }
                finally { Directory.Delete(lifetimeLog); File.Move(savedLifetimeLog, lifetimeLog); }
                window.SignalEnvironment("恢复日志写入");
                await Task.Delay(700);
                await window.WaitForIdleAsync();
                using (var log = System.Text.Json.JsonDocument.Parse(File.ReadAllText(lifetimeLog)))
                    Check(log.RootElement.GetProperty("Reason").GetString() == "恢复日志写入"
                        && log.RootElement.GetProperty("Monitors").GetArrayLength() > 0,
                        "desktop diagnostics resume after a write failure and include the recovery reason and monitor topology");
                var canvasList = (ListBox)window.FindName("CanvasDrawerList");
                var drawerList = (ListBox)window.FindName("DrawerList");
                Check(((FrameworkElement)window.FindName("CanvasView")).Visibility == Visibility.Visible && canvasList.Items.Count == 2,
                    "canvas opens by default with the real drawer count");
                var workRow = drawerList.Items.Cast<DrawerRow>().Single(r => r.Name == "工作");
                canvasList.SelectedItem = workRow;
                Check(drawerList.SelectedItem == workRow && ((TextBlock)window.FindName("SelectedTitle")).Text == "工作",
                    "selecting a canvas card synchronizes the Studio list and inspector");
                window.SetManagerMode(true);
                Check(((FrameworkElement)window.FindName("StudioView")).Visibility == Visibility.Visible && drawerList.SelectedItem == workRow,
                    "mode switching preserves the selected drawer");
                var settingsButton = (Button)window.FindName("SettingsButton");
                settingsButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                window.UpdateLayout();
                Check(((FrameworkElement)window.FindName("SettingsBox")).IsVisible
                    && ((FrameworkElement)window.FindName("GeneralSettingsPage")).IsVisible
                    && !((CheckBox)window.FindName("StartupBox")).IsEnabled,
                    $"the persistent settings entry opens general preferences and explains disabled startup in demo mode (dialog={((FrameworkElement)window.FindName("SettingsBox")).IsVisible}, general={((FrameworkElement)window.FindName("GeneralSettingsPage")).IsVisible}, startup={((CheckBox)window.FindName("StartupBox")).IsEnabled})");
                window.UpdateLayout(); SaveManagerPreview(window, storage.Resolve("manager-settings-general.png"));
                window.Width = window.MinWidth; window.Height = window.MinHeight; window.UpdateLayout();
                SaveManagerPreview(window, storage.Resolve("manager-settings-general-minimum.png"));
                var settingsBounds = ((FrameworkElement)window.FindName("SettingsBox")).TransformToAncestor((Visual)window.Content)
                    .TransformBounds(new Rect(((FrameworkElement)window.FindName("SettingsBox")).RenderSize));
                Check(settingsBounds.Left >= 0 && settingsBounds.Top >= 0
                    && settingsBounds.Right <= ((FrameworkElement)window.Content).ActualWidth
                    && settingsBounds.Bottom <= ((FrameworkElement)window.Content).ActualHeight,
                    "the unified settings dialog fits inside the minimum-size manager window");
                window.Width = 1200; window.Height = 780; window.UpdateLayout();
                ((RadioButton)window.FindName("AboutSettingsTab")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(((FrameworkElement)window.FindName("AboutSettingsPage")).IsVisible
                    && ((TextBox)window.FindName("SettingsDataPath")).Text == demoRoot,
                    "settings navigation exposes the real data directory and version in the same dialog");
                window.UpdateLayout(); SaveManagerPreview(window, storage.Resolve("manager-settings-about.png"));
                ((Button)window.FindName("CloseSettingsButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(drawerList.SelectedItem == workRow && ((FrameworkElement)window.FindName("StudioView")).IsVisible,
                    "closing settings preserves the selected drawer and main view");
                var search = (TextBox)window.FindName("SearchBox");
                search.Text = "计算器";
                Check(drawerList.Items.Count == 1 && ((ItemsControl)window.FindName("StudioContentsList")).Items.Count == 1,
                    "search finds a drawer by an entry name and filters the content list");
                search.Text = "没有这个入口";
                Check(drawerList.Items.Count == 0 && ((FrameworkElement)window.FindName("EmptyView")).Visibility == Visibility.Visible,
                    "a search with no results clears the inspector and shows an empty state");
                settingsButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var snapBox = (CheckBox)window.FindName("SnapBox");
                var previousSnap = window.Catalog.Settings.SnapEnabled;
                snapBox.IsChecked = !previousSnap; snapBox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                using (var settings = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(demoRoot, "config", "drawers.json"))))
                    Check(settings.RootElement.GetProperty("SnapEnabled").GetBoolean() == !previousSnap && window.Catalog.Settings.SnapEnabled == !previousSnap,
                        "global snapping remains editable and persists even when no drawer is selected");
                snapBox.IsChecked = previousSnap; snapBox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Check(((FrameworkElement)window.FindName("ModalLayer")).Visibility == Visibility.Collapsed,
                    "Escape dismisses the settings dialog through the manager keyboard handler");
                search.Clear(); window.SelectDrawer("default");
                var contentList = (ItemsControl)window.FindName("StudioContentsList");
                Check(contentList.Items.Cast<ManagerContent>().Any(i => i.Icon != null), "manager displays real shortcut icons");
                window.SetManagerMode(false);
                settingsButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(settingsButton.IsVisible && ((FrameworkElement)window.FindName("GeneralSettingsPage")).IsVisible,
                    "the same settings entry is available from the canvas view");
                ((RadioButton)window.FindName("RecoverySettingsTab")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await window.WaitForIdleAsync();
                ((Button)window.FindName("RestoreAllButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await window.WaitForIdleAsync();
                Check(window.Catalog.Stores.Values.All(s => s.Active.Count == 0 && s.Profile.Entries.All(e => File.Exists(e.OriginalPath))),
                    "the global settings action restores content across drawers while retaining memberships");
                ((Button)window.FindName("RecollectAllButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await window.WaitForIdleAsync();
                Check(window.Catalog.Stores["default"].Active.Count == 18 && window.Catalog.Stores["default"].Profile.Entries.All(e => !File.Exists(e.OriginalPath)),
                    "global recollection in settings resumes the same grouped entries");
                ((Button)window.FindName("CloseSettingsButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var monitorBox = (ComboBox)window.FindName("CanvasMonitorBox");
                monitorBox.SelectedIndex = 1;
                Check(canvasList.Items.Count == 2, "monitor filtering keeps both drawers on their actual display");
                monitorBox.SelectedIndex = 0; window.UpdateLayout();
                Check(Descendants(monitorBox).OfType<TextBlock>().Any(t => t.Text == ((ManagerMonitor)monitorBox.SelectedItem).Label),
                    "monitor selector shows its human-readable label instead of the underlying object");
                var lockBox = (CheckBox)window.FindName("LockBox");
                lockBox.IsChecked = true; lockBox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(window.Panels[0].LayoutLocked && !((Button)window.FindName("ApplyLayoutButton")).IsEnabled
                    && !((ComboBox)window.FindName("TargetMonitorBox")).IsEnabled,
                    "inspector locking reaches the native drawer and disables all geometry controls");
                lockBox.IsChecked = false; lockBox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var sourceStore = window.Catalog.Stores["default"];
                var targetStore = window.Catalog.Stores[workRow.Id];
                var targetPanel = window.Panels[1];
                var targetLayout = (targetStore.Profile.X, targetStore.Profile.Y, targetStore.Profile.Width, targetStore.Profile.Height);
                var targetEntries = targetStore.Profile.Entries.Select(e => e.Id).ToArray();
                var sourceEntries = sourceStore.Profile.Entries.Select(e => e.Id).ToArray();
                var syncButton = (Button)window.FindName("SyncDrawerPreferencesButton");
                var syncConfirm = (Button)window.FindName("ConfirmPreferenceSyncButton");
                var syncExpansion = (CheckBox)window.FindName("SyncExpansionBox");
                var syncLock = (CheckBox)window.FindName("SyncLockBox");
                var syncModal = (FrameworkElement)window.FindName("PreferenceSyncBox");
                var syncCancel = Descendants(syncModal).OfType<Button>().Single(b => Equals(b.Content, "取消"));
                void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Click((RadioButton)window.FindName("HoverModeButton"));
                lockBox.IsChecked = true; Click(lockBox);
                Check(sourceStore.Profile.Folded && sourceStore.Profile.Locked && !targetStore.Profile.Folded && !targetStore.Profile.Locked,
                    "individual expansion and lock edits do not implicitly change other drawers");
                Click(syncButton); window.UpdateLayout();
                Check(syncModal.IsVisible && ((TextBlock)window.FindName("PreferenceSyncSummary")).Text.Contains("常用")
                    && ((TextBlock)window.FindName("PreferenceSyncSummary")).Text.Contains("1 个抽屉"),
                    "preference sync identifies the source drawer and all eligible targets");
                SaveManagerPreview(window, storage.Resolve("manager-preference-sync.png"));
                window.Width = window.MinWidth; window.Height = window.MinHeight; window.UpdateLayout();
                var syncBounds = syncModal.TransformToAncestor((Visual)window.Content).TransformBounds(new Rect(syncModal.RenderSize));
                Check(syncBounds.Left >= 0 && syncBounds.Top >= 0 && syncBounds.Right <= ((FrameworkElement)window.Content).ActualWidth
                    && syncBounds.Bottom <= ((FrameworkElement)window.Content).ActualHeight,
                    "the preference sync dialog fits inside the minimum-size manager window");
                SaveManagerPreview(window, storage.Resolve("manager-preference-sync-minimum.png"));
                window.Width = 1200; window.Height = 780;
                Click(syncCancel);
                Check(!targetStore.Profile.Folded && !targetStore.Profile.Locked && syncModal.Visibility == Visibility.Collapsed,
                    "canceling preference sync leaves target drawers unchanged");
                Click(syncButton); syncExpansion.IsChecked = syncLock.IsChecked = false; Click(syncConfirm);
                Check(((FrameworkElement)window.FindName("PreferenceSyncError")).Visibility == Visibility.Visible
                    && !targetStore.Profile.Folded && !targetStore.Profile.Locked,
                    "sync requires at least one selected field before changing any drawer");
                syncExpansion.IsChecked = true; Click(syncConfirm);
                Check(targetStore.Profile.Folded && !targetStore.Profile.Locked && targetPanel.IsFolded && !targetPanel.LayoutLocked
                    && targetStore.Detached().Profile.Folded && !targetStore.Detached().Profile.Locked,
                    "expansion-only sync updates the native target and disk while preserving its lock state");
                Click((RadioButton)window.FindName("AlwaysModeButton"));
                Check(targetStore.Profile.Folded, "later source edits do not create a permanent link between drawer preferences");
                Click(syncButton); syncExpansion.IsChecked = false; Click(syncConfirm);
                Check(targetStore.Profile.Folded && targetStore.Profile.Locked && targetPanel.IsFolded && targetPanel.LayoutLocked
                    && targetStore.Detached().Profile.Folded && targetStore.Detached().Profile.Locked,
                    "lock-only sync preserves target expansion and persists the selected lock value");
                lockBox.IsChecked = false; Click(lockBox);
                Click(syncButton);
                // Selection may change programmatically while a modal is open; its captured source remains stable.
                window.SelectDrawer(workRow.Id); Click(syncConfirm);
                Check(!targetStore.Profile.Folded && !targetStore.Profile.Locked && !targetPanel.IsFolded && !targetPanel.LayoutLocked
                    && !targetStore.Detached().Profile.Folded && !targetStore.Detached().Profile.Locked,
                    "syncing both fields uses the captured source even if the inspector selection changes");
                Check((targetStore.Profile.X, targetStore.Profile.Y, targetStore.Profile.Width, targetStore.Profile.Height) == targetLayout
                    && targetStore.Profile.Entries.Select(e => e.Id).SequenceEqual(targetEntries)
                    && sourceStore.Profile.Entries.Select(e => e.Id).SequenceEqual(sourceEntries)
                    && window.Catalog.Settings.Drawers.Single(d => d.Id == workRow.Id).Name == "工作",
                    "preference sync preserves independent geometry, names and shortcut membership");
                window.SelectDrawer("default");
                var first = window.Panels[0];
                first.SetFolded(true); first.PointerMove(30, 20); await Task.Delay(180);
                RightClickItem(first, 1); window.UpdateLayout();
                var itemMenu = window.ActiveItemMenu;
                Check(itemMenu?.IsOpen == true && first.SelectedIndex == 1
                    && Equals(((MenuItem)itemMenu.Items[0]).Header, "使用说明")
                    && itemMenu.Items.OfType<MenuItem>().Where(i => i.Tag != null).Select(i => i.Tag)
                        .SequenceEqual(new object[] { "open", "location", "properties", "return", "manage" }),
                    "right-click selects the pointed shortcut and exposes open, location, properties, return and manage actions");
                await Task.Delay(100);
                SaveMenuPreview(itemMenu!, storage.Resolve("drawer-item-menu.png"));
                itemMenu!.Items.OfType<MenuItem>().Single(m => Equals(m.Tag, "location")).Focus();
                SaveMenuPreview(itemMenu, storage.Resolve("drawer-item-menu-focused.png"));
                var menuHeading = (MenuItem)itemMenu.Items[0];
                menuHeading.Header = "项目说明与日常使用文档 · 很长的快捷方式名称也应该保持菜单宽度稳定";
                SaveMenuPreview(itemMenu, storage.Resolve("drawer-item-menu-long-name.png"));
                menuHeading.Header = "使用说明";
                first.PointerLeave();
                Check(first.IsExpanded && first.IsInteracting, "a hover drawer stays expanded while its item menu is open");
                itemMenu!.IsOpen = false;
                await Task.Delay(250); // WPF raises Closed after the popup's close animation.
                Check(window.ActiveItemMenu == null && !first.IsInteracting && sourceStore.Active.Count == 18,
                    "canceling an item menu clears interaction state without changing shortcut membership");
                first.SetFolded(false);
                Check(window.Catalog.Stores["default"].Active.Count == 18, "demo drawer contains more than 12 items");
                first.ScrollBy(188);
                Check(first.ScrollOffset > 0, "content scrolls beyond the first visible rows");
                first.SelectAt(30, 75);
                Check(first.SelectedIndex >= first.Columns, "scrolled hit testing selects the correct underlying item");
                first.ApplyScreenLayout(180, 160, 620, 440);
                Check(first.Columns == 6 && first.LogicalHeight == 440, "resizing recomputes columns and expanded height");
                first.Configure("常用", 420, 352, true);
                Check(first.LayoutLocked, "layout locking reaches the native panel");
                first.RestoreFolded(true);
                Check(first.IsFolded && !first.IsExpanded && Math.Abs(first.Height - 46 * first.Width / first.LogicalWidth) <= 1, "folded drawer keeps its title bar after resizing at the current display scale");
                first.RestoreFolded(false);
                first.SetFolded(true);
                Check(((RadioButton)window.FindName("HoverModeButton")).IsChecked == true,
                    "native folding synchronizes the visible behavior segments");
                first.SetFolded(false);
                window.CreateDrawer("阅读与资料");
                await window.WaitForIdleAsync();
                Check(window.Panels.Count == 3 && window.Catalog.Settings.Drawers.Any(d => d.Name == "阅读与资料"), "manager creates and selects an independent third drawer");
                var added = window.Catalog.Settings.Drawers.Single(d => d.Name == "阅读与资料");
                var batchStore = window.Catalog.Stores[added.Id];
                var batchPanel = window.Panels[2];
                Click((RadioButton)window.FindName("HoverModeButton"));
                lockBox.IsChecked = true; Click(lockBox); Click(syncButton); Click(syncConfirm);
                Check(window.Catalog.Stores.Values.All(s => s.Profile.Folded && s.Profile.Locked)
                    && window.Panels.All(p => p.IsFolded && p.LayoutLocked),
                    "sync applies selected values to every other drawer when there are multiple targets");
                Click((RadioButton)window.FindName("AlwaysModeButton"));
                lockBox.IsChecked = false; Click(lockBox); Click(syncButton); Click(syncConfirm);
                Check(window.Catalog.Stores.Values.All(s => !s.Profile.Folded && !s.Profile.Locked),
                    "sync can also clear both preferences across every drawer");
                var batchPaths = Enumerable.Range(0, 24).Select(i => Path.Combine(batchStore.DesktopPath, $"批量入口 {i}.url")).ToArray();
                foreach (var path in batchPaths) File.WriteAllText(path, "[InternetShortcut]\r\nURL=https://example.com/batch\r\n");
                var builds = batchPanel.SurfaceBuilds;
                Check(window.QueueAddPaths(added.Id, batchPaths) && window.BusyOperations == 1,
                    "24-item collection enters the background queue without blocking the dispatcher");
                Click(syncButton); Click(syncConfirm);
                Check(((FrameworkElement)window.FindName("PreferenceSyncError")).Visibility == Visibility.Visible
                    && window.BusyOperations == 1 && !targetStore.Profile.Folded && !targetStore.Profile.Locked,
                    "preference sync refuses pending file work before making any target changes");
                Click(syncCancel);
                batchPanel.ApplyScreenLayout(200, 180, 510, 352);
                await window.WaitForIdleAsync();
                Check(batchStore.Active.Count == 24 && batchPanel.SurfaceBuilds - builds < 12,
                    $"batch collection plus layout change uses bounded surface rebuilds ({batchPanel.SurfaceBuilds - builds}) rather than a rebuild per item; active={batchStore.Active.Count}");
                Check(batchStore.Detached().Profile.Width == batchStore.Profile.Width && batchStore.Profile.Width == batchPanel.LogicalWidth,
                    "layout edits during collection survive worker publication and persist on disk");
                var menuSample = Path.Combine(batchStore.DesktopPath, "右键归还样例.url");
                File.WriteAllText(menuSample, "[InternetShortcut]\r\nURL=https://example.com/menu-return\r\n");
                Check(window.QueueAddPaths(added.Id, [menuSample]), "item-menu return test collects an isolated extra shortcut");
                RightClickItem(batchPanel);
                var busyMenu = window.ActiveItemMenu!;
                Check(busyMenu.IsOpen && busyMenu.Items.OfType<MenuItem>().Where(m => m.Tag != null && !Equals(m.Tag, "manage")).All(m => !m.IsEnabled),
                    "item file actions are disabled while their drawer has pending file work");
                await Task.Delay(100);
                SaveMenuPreview(busyMenu, storage.Resolve("drawer-item-menu-disabled.png"));
                busyMenu.IsOpen = false;
                await window.WaitForIdleAsync();
                await Task.Delay(250);
                var menuReturnEntry = batchStore.Active[0];
                var menuReturnPath = batchStore.StoredPath(menuReturnEntry);
                batchPanel.Configure("阅读与资料", batchPanel.LogicalWidth, batchPanel.LogicalHeight, true);
                RightClickItem(batchPanel);
                var returnMenu = window.ActiveItemMenu!;
                Check(returnMenu.IsOpen && batchPanel.LayoutLocked && batchPanel.SelectedIndex == 0,
                    "layout locking still permits shortcut context operations");
                var menuReturn = returnMenu.Items.OfType<MenuItem>().Single(m => Equals(m.Tag, "return"));
                returnMenu.IsOpen = false;
                menuReturn.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await window.WaitForIdleAsync();
                Check(File.Exists(menuReturnEntry.OriginalPath) && !File.Exists(menuReturnPath)
                    && batchStore.Profile.Entries.Count == 24 && batchStore.Detached().Profile.Entries.Count == 24,
                    "item-menu return restores only the pointed shortcut and persists removal of its grouping");
                menuReturn.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Check(batchStore.Profile.Entries.Count == 24 && window.BusyOperations == 0,
                    "a stale menu action cannot return an already ungrouped shortcut twice");
                batchPanel.Configure("阅读与资料", batchPanel.LogicalWidth, batchPanel.LogicalHeight, false);
                window.SetManagerMode(true); window.UpdateLayout();
                var studioContents = (ItemsControl)window.FindName("StudioContentsList");
                var returnButton = Descendants(studioContents).OfType<Button>().First(b => Equals(b.Content, "归还"));
                var item = (ManagerContent)returnButton.DataContext;
                var collision = "[InternetShortcut]\r\nURL=https://example.com/existing\r\n";
                File.WriteAllText(item.OriginalPath, collision);
                returnButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await window.WaitForIdleAsync();
                Check(File.ReadAllText(item.OriginalPath) == collision && File.Exists(item.Path) && batchStore.Profile.Entries.Count == 24,
                    "single-item return protects an existing desktop file and retains recovery membership on conflict");
                await window.ScanRecoveryAsync();
                var recovery = window.RecoveryProblems.Single(p => p.OriginalPath == item.OriginalPath);
                Check(recovery.StoredPath == item.Path && recovery.EntryId == item.Id,
                    "recovery center identifies the exact conflicting entry and both locations");
                settingsButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                ((RadioButton)window.FindName("RecoverySettingsTab")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await window.WaitForIdleAsync(); window.UpdateLayout();
                SaveManagerPreview(window, storage.Resolve("manager-health-preview.png"));
                window.Width = window.MinWidth; window.Height = window.MinHeight; window.UpdateLayout();
                SaveManagerPreview(window, storage.Resolve("manager-health-minimum.png"));
                var settingsScroll = (ScrollViewer)window.FindName("SettingsScroll");
                settingsScroll.ScrollToBottom(); window.UpdateLayout();
                var recoveryRetry = Descendants((DependencyObject)window.FindName("RecoveryList")).OfType<Button>()
                    .First(b => Equals(b.Content, "检查并重试"));
                var retryBounds = recoveryRetry.TransformToAncestor(settingsScroll).TransformBounds(new Rect(recoveryRetry.RenderSize));
                Check(retryBounds.Top >= 0 && retryBounds.Bottom <= settingsScroll.ActualHeight,
                    "recovery actions remain reachable by scrolling settings at the minimum window size");
                SaveManagerPreview(window, storage.Resolve("manager-settings-recovery-scrolled.png"));
                var dismissHealth = (Button)window.FindName("CloseSettingsButton");
                dismissHealth.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                window.Width = 1200; window.Height = 780;
                File.Delete(item.OriginalPath);
                await window.RetryProblemAsync(recovery);
                await window.WaitForIdleAsync();
                Check(File.Exists(item.OriginalPath) && !File.Exists(item.Path) && batchStore.Profile.Entries.Count == 23,
                    "recovery center retry restores the file and removes only its pending ungroup membership");
                Check(!window.RecoveryProblems.Any(p => p.EntryId == item.Id), "resolved conflict disappears from the recovery center");
                window.SetManagerMode(false);
                Check(await window.RemoveDrawerAsync(added.Id) && window.Panels.Count == 2, "manager removes a drawer after background restoration");
                await window.WaitForIdleAsync();
                Check(!window.RecoveryProblems.Any(p => p.DrawerId == added.Id)
                    && ((TextBlock)window.FindName("SettingsButtonLabel")).Text == "设置",
                    "removing a drawer refreshes the unified settings badge and retires its pending-removal warning");
                first.Configure("常用", 420, 352, false);
                first.ApplyScreenLayout(180, 160, 420, 352);
                first.ScrollBy(-10000);
                window.SelectDrawer("default");
                await Task.Delay(150);
                window.UpdateLayout();
                var content = (FrameworkElement)window.Content;
                var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var output = storage.Resolve("manager-preview.png");
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(output)) encoder.Save(file);
                window.SetManagerMode(true); window.UpdateLayout();
                SaveManagerPreview(window, storage.Resolve("manager-studio-preview.png"));
                window.Width = window.MinWidth; window.Height = window.MinHeight; window.UpdateLayout();
                SaveManagerPreview(window, storage.Resolve("manager-studio-minimum.png"));
                window.SetManagerMode(false); window.UpdateLayout();
                SaveManagerPreview(window, storage.Resolve("manager-canvas-minimum.png"));
                var createButton = Descendants((DependencyObject)window.Content).OfType<Button>().First(b => Equals(b.Content, "＋ 新建抽屉"));
                createButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var confirm = Descendants((DependencyObject)window.FindName("CreateBox")).OfType<Button>().First(b => Equals(b.Content, "创建并在桌面放置"));
                confirm.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(((FrameworkElement)window.FindName("CreateErrorLabel")).Visibility == Visibility.Visible && window.Panels.Count == 2,
                    "invalid creation keeps the modal open with a visible error and creates no drawer");
                window.UpdateLayout(); SaveManagerPreview(window, storage.Resolve("manager-create-preview.png"));
                var cancel = Descendants((DependencyObject)window.FindName("CreateBox")).OfType<Button>().First(b => Equals(b.Content, "取消"));
                cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(((FrameworkElement)window.FindName("ModalLayer")).Visibility == Visibility.Collapsed, "cancel dismisses the complete modal overlay");
                var close = Descendants((DependencyObject)window.Content).OfType<Button>().First(b => Equals(b.Content, "✕"));
                close.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(((FrameworkElement)window.FindName("CloseBox")).Visibility == Visibility.Visible && window.IsVisible,
                    "close button offers resident operation or restoration before exiting");
                window.UpdateLayout(); SaveManagerPreview(window, storage.Resolve("manager-close-preview.png"));
                var keepRunning = Descendants((DependencyObject)window.FindName("CloseBox")).OfType<Button>()
                    .Single(b => b.Content is StackPanel panel && panel.Children.OfType<TextBlock>().Any(t => t.Text == "关闭面板，保持桌面抽屉运行"));
                keepRunning.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(!window.IsVisible && window.TrayAvailable && window.Panels.All(p => p.IsAttached),
                    "closing the demo to the tray hides its taskbar window while keeping a registered tray icon and attached drawers");
                var baseline = new PerformanceRecorder();
                for (var i = 0; i < 6; i++)
                {
                    var delay = System.Diagnostics.Stopwatch.StartNew();
                    await Task.Delay(5000);
                    baseline.Sample(Math.Max(0, delay.Elapsed.TotalMilliseconds - 5000), window.IsVisible, false);
                }
                storage.WriteJson("manager-idle-baseline.json", new
                {
                    Scope = "30-second isolated manager harness, hidden manager, 2 drawers and 18 sample shortcuts; not the release executable or an overnight soak",
                    Measurements = baseline.Snapshot(new { Source = "See each demo data logs/performance-current.json" }, new { Count = window.Panels.Count })
                });
                Check(window.Panels.All(p => p.IsAttached) && baseline.SampleCount == 6,
                    "30-second hidden-manager baseline retains both attached drawers and records six process samples");
                RightClickItem(first);
                Check(window.ActiveItemMenu?.IsOpen == true && !window.IsVisible,
                    "shortcut context menus remain available while the manager is hidden in the tray");
                var hiddenMenu = window.ActiveItemMenu!;
                hiddenMenu.IsOpen = false;
                hiddenMenu.Items.OfType<MenuItem>().Single(m => Equals(m.Tag, "manage")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Task.Delay(250);
                Check(window.IsVisible && ((TextBlock)window.FindName("SelectedTitle")).Text == "常用",
                    "reopening the manager preserves its drawer selection");
                close.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var cancelClose = Descendants((DependencyObject)window.FindName("CloseBox")).OfType<Button>().First(b => Equals(b.Content, "取消"));
                cancelClose.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                first.SavePreview(storage.Resolve("drawer-preview.png"));
                var closingStore = window.Catalog.Stores["default"];
                var closingEntry = closingStore.Active.First();
                File.WriteAllText(closingEntry.OriginalPath, "existing desktop entry");
                window.RequestExit();
                await window.WaitForIdleAsync();
                Check(window.IsVisible && ((FrameworkElement)window.FindName("SettingsBox")).Visibility == Visibility.Visible
                    && ((FrameworkElement)window.FindName("RecoverySettingsPage")).Visibility == Visibility.Visible
                    && File.ReadAllText(closingEntry.OriginalPath) == "existing desktop entry"
                    && File.Exists(closingStore.StoredPath(closingEntry)),
                    "exit with a return conflict cancels closing, preserves both files, and keeps the manager accessible");
                File.Delete(closingEntry.OriginalPath);
                var stores = window.Catalog.Stores.Values.ToArray();
                var pendingPath = Path.Combine(closingStore.DesktopPath, "退出等待.url");
                File.WriteAllText(pendingPath, "[InternetShortcut]\r\nURL=https://example.com/exit-wait\r\n");
                Check(window.QueueAddPaths("default", [pendingPath]) && window.BusyOperations == 1, "exit test begins with a pending background collection");
                window.RequestExit();
                await window.WaitForIdleAsync();
                Check(!window.IsVisible && File.Exists(pendingPath) && stores.All(s => s.Profile.Entries.All(e =>
                    File.Exists(e.OriginalPath) && !File.Exists(s.StoredPath(e)))),
                    "closing the demo restores every drawer's sample files before disposal: " + ((System.Windows.Controls.TextBlock)window.FindName("StatusLabel")).Text);
                window = null;
                storage.WriteJson("manager-results.json", new { Passed = results.Count, Results = results, Preview = output, RealDesktopFilesMoved = false });
                Console.WriteLine(string.Join(Environment.NewLine, results));
                Console.WriteLine("Preview: " + output);
                app.Shutdown(0);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                if (window != null) { window.RequestExit(); try { await window.WaitForIdleAsync(); } catch (Exception cleanup) { Console.Error.WriteLine(cleanup.Message); } }
                app.Shutdown(1);
            }
        }));
        return app.Run();
    }
}
