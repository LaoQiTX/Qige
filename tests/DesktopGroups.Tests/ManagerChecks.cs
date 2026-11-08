using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopGroups;

internal static class ManagerChecks
{
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
                Check(window.Panels.Count == 2 && window.Panels.All(p => p.IsAttached), "manager attaches both independent demo drawers");
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
                var search = (TextBox)window.FindName("SearchBox");
                search.Text = "计算器";
                Check(drawerList.Items.Count == 1 && ((ItemsControl)window.FindName("StudioContentsList")).Items.Count == 1,
                    "search finds a drawer by an entry name and filters the content list");
                search.Text = "没有这个入口";
                Check(drawerList.Items.Count == 0 && ((FrameworkElement)window.FindName("EmptyView")).Visibility == Visibility.Visible,
                    "a search with no results clears the inspector and shows an empty state");
                search.Clear(); window.SelectDrawer("default");
                var contentList = (ItemsControl)window.FindName("ContentsList");
                Check(contentList.Items.Cast<ManagerContent>().Any(i => i.Icon != null), "manager displays real shortcut icons");
                window.SetContentPreview(true);
                Check(((RadioButton)window.FindName("ListViewButton")).IsChecked == true && contentList.Items.Count == 18,
                    "list preview preserves all real entries");
                window.SetContentPreview(false); window.SetManagerMode(false);
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
                var first = window.Panels[0];
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
                Check(first.Height == 46, "folded drawer keeps its title bar after resizing");
                first.RestoreFolded(false);
                first.SetFolded(true);
                Check(((System.Windows.Controls.CheckBox)window.FindName("FoldBox")).IsChecked == true,
                    "folding the desktop drawer updates the selected drawer settings in the manager");
                Check(((RadioButton)window.FindName("HoverModeButton")).IsChecked == true,
                    "native folding synchronizes the visible behavior segments");
                first.SetFolded(false);
                window.CreateDrawer("阅读与资料");
                Check(window.Panels.Count == 3 && window.Catalog.Settings.Drawers.Any(d => d.Name == "阅读与资料"), "manager creates and selects an independent third drawer");
                var added = window.Catalog.Settings.Drawers.Single(d => d.Name == "阅读与资料");
                var batchStore = window.Catalog.Stores[added.Id];
                var batchPanel = window.Panels[2];
                var batchPaths = Enumerable.Range(0, 24).Select(i => Path.Combine(batchStore.DesktopPath, $"批量入口 {i}.url")).ToArray();
                foreach (var path in batchPaths) File.WriteAllText(path, "[InternetShortcut]\r\nURL=https://example.com/batch\r\n");
                var builds = batchPanel.SurfaceBuilds;
                Check(window.AddPaths(added.Id, batchPaths) && batchStore.Active.Count == 24 && batchPanel.SurfaceBuilds - builds == 1,
                    "collecting 24 items refreshes panel icons and surface once after the verified transfers: " + ((TextBlock)window.FindName("StatusLabel")).Text);
                window.SetManagerMode(true); window.UpdateLayout();
                var studioContents = (ItemsControl)window.FindName("StudioContentsList");
                var returnButton = Descendants(studioContents).OfType<Button>().First(b => Equals(b.Content, "归还"));
                var item = (ManagerContent)returnButton.DataContext;
                var collision = "[InternetShortcut]\r\nURL=https://example.com/existing\r\n";
                File.WriteAllText(item.OriginalPath, collision);
                returnButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(File.ReadAllText(item.OriginalPath) == collision && File.Exists(item.Path) && batchStore.Profile.Entries.Count == 24,
                    "single-item return protects an existing desktop file and retains recovery membership on conflict");
                File.Delete(item.OriginalPath);
                returnButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(File.Exists(item.OriginalPath) && !File.Exists(item.Path) && batchStore.Profile.Entries.Count == 23,
                    "single-item return restores the file and removes only its membership");
                window.SetManagerMode(false);
                Check(window.RemoveDrawer(added.Id) && window.Panels.Count == 2, "manager removes a drawer after successful restoration");
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
                window.ShowControl();
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
                await Task.Delay(100);
                Check(window.IsVisible && File.ReadAllText(closingEntry.OriginalPath) == "existing desktop entry"
                    && File.Exists(closingStore.StoredPath(closingEntry)),
                    "exit with a return conflict cancels closing, preserves both files, and keeps the manager accessible");
                File.Delete(closingEntry.OriginalPath);
                var stores = window.Catalog.Stores.Values.ToArray();
                window.RequestExit();
                Check(!window.IsVisible && stores.All(s => s.Profile.Entries.All(e =>
                    File.Exists(e.OriginalPath) && !File.Exists(s.StoredPath(e)))),
                    "closing the demo restores every drawer's sample files before disposal: " + ((System.Windows.Controls.TextBlock)window.FindName("StatusLabel")).Text);
                window = null;
                storage.WriteJson("manager-results.json", new { Passed = results.Count, Results = results, Preview = output, RealDesktopFilesMoved = false });
                Console.WriteLine(string.Join(Environment.NewLine, results));
                Console.WriteLine("Preview: " + output);
                app.Shutdown(0);
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); window?.RequestExit(); app.Shutdown(1); }
        }));
        return app.Run();
    }
}
