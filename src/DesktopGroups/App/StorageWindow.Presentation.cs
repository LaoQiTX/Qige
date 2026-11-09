using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace DesktopGroups;

internal sealed record ManagerContent(string Id, string Name, string OriginalPath, string Path, string State, string Detail, BitmapSource? Icon, bool CanReturn);
internal sealed record ManagerMonitor(string? Device, string Label);

internal sealed partial class StorageWindow
{
    private ListCollectionView? canvasRows, studioRows;
    private IReadOnlyList<ManagerContent> currentContents = [];
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
        return $"{screen} · {(row.Store.Profile.Folded ? "悬停展开" : "常驻展开")}";
    }
    private void RefreshManagerViews()
    {
        if (canvasRows == null || refreshingViews) return;
        refreshingViews = true;
        try
        {
            managerMonitors = Overview.Monitors;
            var signature = string.Join("|", managerMonitors.Select(m => $"{m.Device}:{m.Work}:{m.Dpi}"));
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
    private void CanvasMonitorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initializing && !refreshingViews) RefreshManagerViews();
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
    private IReadOnlyList<ManagerContent> BuildContents(ShortcutStore store)
    {
        var active = store.Active.Select(e => e.Id).ToHashSet();
        return store.Profile.Entries.Select(e =>
        {
            var collected = active.Contains(e.Id);
            var path = collected ? store.StoredPath(e) : e.OriginalPath;
            var key = e.Id + ":" + e.Hash + ":" + path;
            iconCache.TryGet(key, out var icon);
            var state = collected ? "已收纳" : "在桌面／待检查";
            var coordinates = e.OriginalX.HasValue && e.OriginalY.HasValue ? $"原坐标 ({e.OriginalX}, {e.OriginalY})" : "未记录原坐标";
            return new ManagerContent(e.Id, Path.GetFileNameWithoutExtension(e.OriginalPath), e.OriginalPath, path,
                state, $"{state} · {coordinates}", icon, collected);
        }).ToArray();
    }
    private void RefreshRowPresentation(DrawerRuntime runtime)
    {
        runtime.Row.Preview = BuildContents(runtime.Row.Store).Take(4).ToArray();
        runtime.Row.Placement = PlacementFor(runtime.Row);
    }
    private void FillStudioContents()
    {
        var query = SearchBox.Text.Trim();
        var contents = currentContents.Where(e => query.Length == 0 || (Selected?.Name.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            || e.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        StudioContentsList.ItemsSource = contents;
        StudioEmptyLabel.Visibility = contents.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        StudioTitle.Text = Selected?.Name ?? "选择一个抽屉";
        StudioSubtitle.Text = Selected == null ? "从左侧选择抽屉，查看内容与操作" : $"桌面整理 · {currentContents.Count} 个入口 · {contents.Length} 项匹配";
    }
    private void GridPreviewClick(object sender, RoutedEventArgs e) => SetContentPreview(false);
    private void ListPreviewClick(object sender, RoutedEventArgs e) => SetContentPreview(true);
    internal void SetContentPreview(bool list)
    {
        ContentsList.ItemTemplate = (DataTemplate)FindResource(list ? "ContentRow" : "IconTile");
        var factory = new FrameworkElementFactory(list ? typeof(StackPanel) : typeof(WrapPanel));
        ContentsList.ItemsPanel = new ItemsPanelTemplate(factory);
        GridViewButton.IsChecked = !list; ListViewButton.IsChecked = list;
    }
    private void AlwaysModeClick(object sender, RoutedEventArgs e) { FoldBox.IsChecked = false; FoldClick(sender, e); }
    private void HoverModeClick(object sender, RoutedEventArgs e) { FoldBox.IsChecked = true; FoldClick(sender, e); }
    private void LocateCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DrawerRow row }) { SelectDrawer(row.Id); LocateClick(sender, e); }
        e.Handled = true;
    }
    private void RestoreCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DrawerRow row }) { SelectDrawer(row.Id); RestoreClick(sender, e); }
        e.Handled = true;
    }
    private void OpenItemClick(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (sender is not FrameworkElement { DataContext: ManagerContent item }) return;
        if (!File.Exists(item.Path)) throw new IOException("入口文件暂不可用，请检查桌面或归还状态。");
        Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
    });
    private void ReturnItemClick(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (sender is not FrameworkElement { DataContext: ManagerContent item } || SelectedRuntime is not { } runtime) return;
        if (!item.CanReturn) { Status("此入口已在桌面，无需再次归还。"); return; }
        _ = RunStoreAsync(runtime, "归还并解除分组", store => { store.ReturnAndUngroup(item.Path); return []; });
    });
    private void ShowModal(Border box)
    {
        if (ModalLayer.Visibility != Visibility.Visible) modalReturnFocus = Keyboard.FocusedElement;
        CreateBox.Visibility = PreferencesBox.Visibility = CloseBox.Visibility = HealthBox.Visibility = Visibility.Collapsed;
        ModalLayer.Visibility = box.Visibility = Visibility.Visible;
        KeyboardNavigation.SetTabNavigation(box, KeyboardNavigationMode.Cycle);
        box.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }
    private void DismissModal()
    {
        ModalLayer.Visibility = CreateBox.Visibility = PreferencesBox.Visibility = CloseBox.Visibility = HealthBox.Visibility = Visibility.Collapsed;
        if (modalReturnFocus != null) Keyboard.Focus(modalReturnFocus);
    }
    private void DismissModalClick(object sender, RoutedEventArgs e) => DismissModal();
    private void PreferencesClick(object sender, RoutedEventArgs e) { RefreshStartup(); ShowModal(PreferencesBox); }
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
        if (e.Key == Key.Escape && ModalLayer.Visibility == Visibility.Visible) { DismissModal(); e.Handled = true; }
    }
}
