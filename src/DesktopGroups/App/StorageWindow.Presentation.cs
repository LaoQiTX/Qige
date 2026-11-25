using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace DesktopGroups;

internal sealed record ManagerContent(string Id, string Name, string OriginalPath, string Path, string State, string Detail, BitmapSource? Icon, bool CanReturn, bool CanOpen = true, bool CanDismiss = false)
{
    public DrawerContentKind Kind { get; init; }
    public bool CanUnlink => Kind == DrawerContentKind.Reference;
    public bool ShowOnDesktop { get; init; }
    public string IconKey { get; init; } = "";
    public string Type { get; init; } = "";
    public DateTime Modified { get; init; }
    public long Size { get; init; }
}
internal sealed record ManagerMonitor(string? Device, string Label);

internal sealed partial class StorageWindow
{
    private ListCollectionView? canvasRows, studioRows;
    private IReadOnlyList<ManagerContent> currentContents = [];
    private string? studioSelectionOwner;
    private IReadOnlyList<DesktopMonitor> managerMonitors = [];
    private bool refreshingViews;
    private string monitorSignature = "";
    private IInputElement? modalReturnFocus;

    private void InitializeManagerViews()
    {
        canvasRows = new ListCollectionView(rows) { Filter = MatchesCanvas };
        studioRows = new ListCollectionView(rows) { Filter = MatchesSearch };
        studioRows.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DrawerRow.NavigationGroup)));
        CanvasDrawerList.ItemsSource = canvasRows;
        DrawerList.ItemsSource = studioRows;
    }

    private bool MatchesCanvas(object item) => item is DrawerRow row &&
        (CanvasMonitorBox.SelectedItem is not ManagerMonitor { Device: { } device } || MonitorFor(row)?.Device == device);
    private bool MatchesSearch(object item)
    {
        var query = SearchBox.Text.Trim();
        return item is DrawerRow row && (query.Length == 0 || row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Store.Profile.Entries.Any(e => Path.GetFileName(e.OriginalPath).Contains(query, StringComparison.OrdinalIgnoreCase)));
    }
    private DesktopMonitor? MonitorFor(DrawerRow row)
    {
        var p = row.Store.Profile;
        var rect = runtimes.TryGetValue(row.Id, out var runtime) && runtime.Panel.IsAttached
            ? runtime.Panel.ScreenBounds : new LayoutRect(p.X, p.Y, p.Width, p.Folded ? 46 : p.Height);
        return managerMonitors.OrderByDescending(m => Math.Max(0, Math.Min(rect.Right, m.Work.Right) - Math.Max(rect.X, m.Work.X))
                * Math.Max(0, Math.Min(rect.Bottom, m.Work.Bottom) - Math.Max(rect.Y, m.Work.Y)))
            .ThenBy(m => Math.Pow(rect.X - m.Work.X, 2) + Math.Pow(rect.Y - m.Work.Y, 2)).FirstOrDefault();
    }
    private string PlacementFor(DrawerRow row)
    {
        var m = MonitorFor(row);
        var screen = m == null ? "等待显示器" : $"显示器 {managerMonitors.ToList().IndexOf(m) + 1}";
        return $"{screen} · {(row.Store.Profile.ManualExpansion ? "手动展开" : row.Store.Profile.Folded ? "悬停展开" : "常驻展开")}";
    }
    private void RefreshManagerViews()
    {
        if (canvasRows == null || refreshingViews) return;
        refreshingViews = true;
        try
        {
            managerMonitors = Overview.Monitors;
            var signature = string.Join("|", managerMonitors.Select(m => $"{m.Device}:{m.Bounds}:{m.Work}:{m.Dpi}"));
            if (monitorSignature != signature || CanvasMonitorBox.Items.Count == 0)
            {
                monitorSignature = signature;
                var selectedDevice = (CanvasMonitorBox.SelectedItem as ManagerMonitor)?.Device;
                var monitors = managerMonitors.Select((m, i) => new ManagerMonitor(m.Device,
                    $"显示器 {i + 1} · {m.Bounds.Width} × {m.Bounds.Height}")).ToArray();
                CanvasMonitorBox.ItemsSource = new[] { new ManagerMonitor(null, $"全部显示器 · {managerMonitors.Count} 个") }.Concat(monitors).ToArray();
                CanvasMonitorBox.SelectedItem = CanvasMonitorBox.Items.Cast<ManagerMonitor>().FirstOrDefault(m => m.Device == selectedDevice)
                    ?? CanvasMonitorBox.Items[0];
                TargetMonitorBox.ItemsSource = monitors;
            }
            foreach (var row in rows) { row.Placement = PlacementFor(row); row.Refresh(); }
            canvasRows.Refresh();
            CanvasDrawerList.SelectedItem = Selected != null && canvasRows.Contains(Selected) ? Selected : null;
            CanvasEmptyView.Visibility = canvasRows.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
            TargetMonitorBox.IsEnabled = Selected is { } selected && !selected.Store.Profile.Locked;
            TargetMonitorBox.SelectedItem = TargetMonitorBox.Items.Cast<ManagerMonitor>().FirstOrDefault(m => m.Device == (Selected == null ? null : MonitorFor(Selected)?.Device));
            if (Selected is { } rowSelected) SelectedSubtitle.Text = $"{rowSelected.Placement} · {rowSelected.State}";
        }
        finally { refreshingViews = false; }
    }
    internal void SetManagerMode(bool studio)
    {
        CanvasView.Visibility = studio ? Visibility.Collapsed : Visibility.Visible;
        StudioView.Visibility = studio ? Visibility.Visible : Visibility.Collapsed;
        CanvasModeButton.IsChecked = !studio; StudioModeButton.IsChecked = studio;
    }
    private void CanvasModeClick(object sender, RoutedEventArgs e) => SetManagerMode(false);
    private void StudioModeClick(object sender, RoutedEventArgs e) => SetManagerMode(true);
    private void CanvasSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initializing && !refreshingViews && CanvasDrawerList.SelectedItem is DrawerRow row)
        {
            // A drawer chosen from the canvas remains selectable even after a Studio search.
            if (studioRows?.Contains(row) != true) SearchBox.Clear();
            SelectDrawer(row.Id);
        }
    }
    private void CanvasDrawerDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not ListBoxItem { DataContext: DrawerRow row }
            || closing || maintenanceBusy) return;
        // Use the clicked container, including its child previews; never the previous selection.
        SearchBox.Clear(); SelectDrawer(row.Id); SetManagerMode(true);
        StudioContentsList.BringIntoView(); e.Handled = true;
    }
    private void CanvasMonitorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initializing && !refreshingViews) RefreshManagerViews();
    }

    private void CanvasScrollWheel(object sender, MouseWheelEventArgs e)
    {
        // The card list's hidden ScrollViewer otherwise handles the wheel first.
        if (sender is not ScrollViewer scroll || scroll.ScrollableHeight <= 0 || e.Delta == 0) return;
        if (e.Delta > 0) scroll.LineUp();
        else scroll.LineDown();
        e.Handled = true;
    }

    private void TargetMonitorChanged(object sender, SelectionChangedEventArgs e) => Try(() =>
    {
        if (initializing || refreshingViews || updating || SelectedRuntime is not { } r || r.Row.Store.Profile.Locked
            || TargetMonitorBox.SelectedItem is not ManagerMonitor target) return;
        var monitor = managerMonitors.FirstOrDefault(m => m.Device == target.Device);
        if (monitor == null || MonitorFor(r.Row)?.Device == target.Device) return;
        var p = r.Row.Store.Profile;
        r.Panel.ApplyScreenLayout(monitor.Work.X + 24, monitor.Work.Y + 24, p.Width, p.Height);
        SaveLayout(r); UpdateDetail(); Status("已将抽屉移至目标显示器。");
    });
    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (initializing || refreshingViews || studioRows == null) return;
        var previous = Selected;
        studioRows.Refresh();
        if (previous != null && studioRows.Contains(previous)) DrawerList.SelectedItem = previous;
        else DrawerList.SelectedItem = studioRows.Cast<DrawerRow>().FirstOrDefault();
        StudioNoMatches.Visibility = studioRows.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        UpdateDetail();
    }
    private static long FileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 0; }
    }
    private IReadOnlyList<ManagerContent> BuildContents(ShortcutStore store)
    {
        var active = store.Active.Select(e => e.Id).ToHashSet();
        var managed = store.Profile.Entries.Select(e =>
        {
            var collected = active.Contains(e.Id);
            var path = collected ? store.StoredPath(e) : e.OriginalPath;
            var key = e.Id + ":" + e.Hash + ":" + path;
            iconCache.TryGet(key, out var icon);
            var missing = recoveryProblems.Any(p => p.CanDismiss && p.EntryId == e.Id && p.OriginalPath == e.OriginalPath);
            var state = missing ? "文件已不存在 · 可移除记录" : collected ? "已收纳" : "在桌面／待检查";
            var coordinates = e.OriginalX.HasValue && e.OriginalY.HasValue ? $"原坐标 ({e.OriginalX}, {e.OriginalY})" : "未记录原坐标";
            return new ManagerContent(e.Id, DrawerItemName(e.OriginalPath, e.IsDirectory), e.OriginalPath, path,
                state, $"{state} · {coordinates}", missing ? null : icon, collected && !missing, !missing, missing)
            {
                ShowOnDesktop = collected && !missing, IconKey = key, Type = e.IsDirectory ? "文件夹" : Path.GetExtension(path), Modified = File.GetLastWriteTimeUtc(path), Size = FileSize(path)
            };
        });
        var runtime = runtimes.Values.FirstOrDefault(candidate => candidate.Row.Store == store);
        if (runtime == null) return managed.ToArray();
        var external = runtime.ExternalItems.Select(item =>
        {
            iconCache.TryGet(item.IconKey, out var icon);
            var state = item.Kind == DrawerContentKind.Mapped ? "映射目录" : item.Available ? "外部引用" : "引用已失效";
            return new ManagerContent(item.Id, DrawerItemName(item.Path, item.IsDirectory), item.Path, item.Path, state, $"{state} · {item.Type}",
                icon, false, item.Available)
            {
                Kind = item.Kind, ShowOnDesktop = item.Available, IconKey = item.IconKey,
                Type = item.Type, Modified = item.Modified, Size = item.Size
            };
        });
        var combined = managed.Concat(external);
        return (runtime.Definition.Sort switch
        {
            DrawerSort.Name => combined.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            DrawerSort.Type => combined.OrderBy(item => item.Type, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            DrawerSort.Modified => combined.OrderByDescending(item => item.Modified).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            DrawerSort.Size => combined.OrderByDescending(item => item.Size).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => ApplyManualOrder(combined, runtime.Definition.ManualOrder)
        }).ToArray();
    }
    private static IEnumerable<ManagerContent> ApplyManualOrder(IEnumerable<ManagerContent> contents, List<string> order)
    {
        if (order.Count == 0) return contents;
        var positions = order.Select((id, index) => (id, index)).ToDictionary(item => item.id, item => item.index, StringComparer.OrdinalIgnoreCase);
        return contents.OrderBy(item => positions.GetValueOrDefault(item.Id, int.MaxValue));
    }

    private void RefreshRowPresentation(DrawerRuntime runtime)
    {
        var contents = BuildContents(runtime.Row.Store);
        runtime.Row.MissingCount = contents.Count(e => e.CanDismiss);
        runtime.Row.ContentCount = contents.Count;
        runtime.Row.Preview = contents.Where(e => !e.CanDismiss).Take(4).ToArray();
        runtime.Row.Placement = PlacementFor(runtime.Row);
    }
    private void FillStudioContents()
    {
        var query = SearchBox.Text.Trim();
        var contents = currentContents.Where(e => query.Length == 0 || (Selected?.Name.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            || e.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(e.OriginalPath).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var runtime = SelectedRuntime;
        var canEdit = runtime is { Busy: false, Definition.Removing: false } && !closing && !maintenanceBusy && !exitAfterOperations;
        var priorSelection = studioSelectionOwner == Selected?.Id
            ? StudioContentsList.SelectedItems.Cast<ManagerContent>().Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        studioSelectionOwner = Selected?.Id;
        StudioContentsList.ItemsSource = contents;
        foreach (var item in contents.Where(item => priorSelection.Contains(item.Id))) StudioContentsList.SelectedItems.Add(item);
        var mapping = runtime?.Definition.Kind == DrawerKind.FolderMapping;
        ContentActions.Visibility = runtime == null ? Visibility.Collapsed : Visibility.Visible;
        ContentActions.IsEnabled = canEdit;
        AddContentsMenuButton.Visibility = runtime?.Definition.Kind == DrawerKind.Desktop ? Visibility.Visible : Visibility.Collapsed;
        AddContentsMenuButton.IsEnabled = canEdit;
        var operationLabel = mapping ? "文件操作" : "整理操作";
        MappedOperationsButton.Content = operationLabel + " ▾";
        System.Windows.Automation.AutomationProperties.SetName(MappedOperationsButton, operationLabel);
        AddSelectedButton.Visibility = mapping ? Visibility.Collapsed : Visibility.Visible;
        AddSelectedButton.IsEnabled = canEdit && !demo;
        RelocateButton.Visibility = mapping ? Visibility.Visible : Visibility.Collapsed;
        RelocateButton.IsEnabled = canEdit;
        OpenDrawerDirectoryButton.Content = mapping ? "打开映射目录" : "打开抽屉目录";
        SelectedDrawerKindLabel.Text = mapping ? "文件夹映射" : "桌面整理";
        StudioExternalIssue.Text = SelectedRuntime?.ExternalIssue ?? "";
        StudioExternalIssue.Visibility = StudioExternalIssue.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        StudioEmptyLabel.Visibility = contents.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        StudioTitle.Text = Selected?.Name ?? "选择一个抽屉";
        StudioSubtitle.Text = Selected == null ? "从左侧选择抽屉，查看内容与操作" : $"{Selected.NavigationGroup} · {currentContents.Count} 个入口 · {contents.Length} 项匹配";
    }
    private void AlwaysModeClick(object sender, RoutedEventArgs e) => SetFoldMode(false);
    private void ManualModeClick(object sender, RoutedEventArgs e) => Try(() => { SelectedRuntime?.Panel.SetManualMode(); UpdateDetail(); });
    private void HoverModeClick(object sender, RoutedEventArgs e) => SetFoldMode(true);
    private void SetFoldMode(bool folded) => Try(() => { SelectedRuntime?.Panel.SetFolded(folded); UpdateDetail(); });
    private void OpenItemClick(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (sender is not FrameworkElement { DataContext: ManagerContent item }) return;
        if (item.Kind != DrawerContentKind.Managed) DrawerContentSource.AssertUsable(item.Path);
        else if (!File.Exists(item.Path) && !Directory.Exists(item.Path)) throw new IOException("入口文件暂不可用，请检查桌面或归还状态。");
        OpenContent(item.Path);
    });
    private void ReturnItemClick(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (sender is not FrameworkElement { DataContext: ManagerContent item } || SelectedRuntime is not { } runtime) return;
        if (!item.CanReturn) { Status("此入口已在桌面，无需再次归还。"); return; }
        _ = RunStoreAsync(runtime, "归还并解除分组", store => { store.ReturnAndUngroup(item.Path); return []; });
    });
    private async void DismissMissingContentClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ManagerContent item } || SelectedRuntime is not { } runtime) return;
        var problem = recoveryProblems.FirstOrDefault(p => p.DrawerId == runtime.Definition.Id && p.EntryId == item.Id && p.CanDismiss);
        if (problem != null) await ConfirmDismissMissingAsync(problem);
        else { Status("文件状态已变化，请重新检查。"); await ScanRecoveryAsync(); }
    }
    private void ShowModal(Border box)
    {
        if (ModalLayer.Visibility != Visibility.Visible) modalReturnFocus = Keyboard.FocusedElement;
        CreateBox.Visibility = SettingsBox.Visibility = CloseBox.Visibility = PreferenceSyncBox.Visibility = NewWebsiteBox.Visibility = Visibility.Collapsed;
        modalTransition++;
        activeMaterialOverlay = box;
        ModalLayer.IsHitTestVisible = true;
        ModalLayer.Visibility = box.Visibility = Visibility.Visible;
        AnimateMaterialOverlay(box);
        KeyboardNavigation.SetTabNavigation(box, KeyboardNavigationMode.Cycle);
        box.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }
    private void DismissModal()
    {
        modalTransition++;
        activeMaterialOverlay = null;
        ModalLayer.IsHitTestVisible = true;
        ModalLayer.Visibility = CreateBox.Visibility = SettingsBox.Visibility = CloseBox.Visibility = PreferenceSyncBox.Visibility = NewWebsiteBox.Visibility = Visibility.Collapsed;
        if (modalReturnFocus != null) Keyboard.Focus(modalReturnFocus);
    }
    private void DismissModalClick(object sender, RoutedEventArgs e) => DismissMaterialOverlay();
    private void ClosePanelClick(object sender, RoutedEventArgs e) => ShowModal(CloseBox);
    private void MinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void MaximizeClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this);
    }
    private void KeepRunningClick(object sender, RoutedEventArgs e)
    {
        DismissModal();
        if (tray?.EnsureAvailable() == true) { Status("已收起到系统托盘，双击栖格图标可重新打开。"); Hide(); }
        else Status("托盘暂不可用，已保留管理面板。");
    }
    private void ManagerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.OemComma && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        { ShowSettings(); e.Handled = true; return; }
        if (e.Key == Key.Escape && ModalLayer.Visibility == Visibility.Visible) { DismissModal(); e.Handled = true; return; }
        if (StudioContentsList.IsKeyboardFocusWithin && SelectedRuntime is { Definition.Kind: DrawerKind.FolderMapping } runtime && !runtime.Busy)
        {
            var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (control && e.Key is Key.C or Key.X) { Try(() => CopyMappedSelection(e.Key == Key.X)); e.Handled = true; }
            if (control && e.Key == Key.V) { Try(() => PasteIntoMapping(runtime)); e.Handled = true; }
            if (!control && e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None) { Try(() => RecycleMapped(runtime, SelectedMappedItems())); e.Handled = true; }
            if (!control && e.Key == Key.F2) { Try(() => RenameMapped(runtime, SelectedMappedItems())); e.Handled = true; }
        }
        else if (StudioContentsList.IsKeyboardFocusWithin && SelectedRuntime is { Definition.Kind: DrawerKind.Desktop, Busy: false } organizer)
        {
            var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            var items = StudioContentsList.SelectedItems.Cast<ManagerContent>().ToArray();
            var managed = items.Where(item => item.Kind == DrawerContentKind.Managed && item.CanReturn).ToArray();
            if (control && e.Key == Key.C) { Try(() => SetFileClipboard(items.Where(item => item.CanOpen).Select(item => item.Path).ToArray(), false)); e.Handled = true; }
            if (control && e.Key == Key.X) { _ = ReturnManagedSelectionAsync(organizer, managed, cut: true); e.Handled = true; }
            if (control && e.Key == Key.V) { Try(() => PasteIntoOrganizer(organizer)); e.Handled = true; }
            if (!control && e.Key == Key.F2 && managed.Length == 1) { Try(() => RenameManaged(organizer, managed[0])); e.Handled = true; }
            if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None) { Try(() => RecycleManaged(organizer, managed)); e.Handled = true; }
        }
    }
}
