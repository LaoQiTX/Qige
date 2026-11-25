using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace DesktopGroups;

internal sealed partial class StorageWindow
{
    private bool checkingExternalContents;

    private async Task CheckExternalContentsAsync()
    {
        if (checkingExternalContents || closing || maintenanceBusy || disposed) return;
        checkingExternalContents = true;
        try
        {
            foreach (var runtime in runtimes.Values.ToArray())
                if (!runtime.Busy && (runtime.Definition.Kind == DrawerKind.FolderMapping || runtime.Definition.References.Count > 0))
                    await RefreshExternalContentsAsync(runtime);
        }
        finally { checkingExternalContents = false; }
    }

    private async Task RefreshExternalContentsAsync(DrawerRuntime runtime)
    {
        if (disposed || closing || maintenanceBusy || runtime.Definition.Removing || runtime.ScanningExternal) return;
        runtime.ScanningExternal = true;
        try
        {
            do
            {
                var generation = runtime.ExternalGeneration;
                var mappedPath = runtime.Definition.MappedPath;
                var references = runtime.Definition.References.Select(r => (r.Id, r.Path)).ToArray();
                ExternalContent[] contents = [];
                string? issue = null;
                try
                {
                    contents = await fileWorker.Run(() => mappedPath != null
                        ? DrawerContentSource.ScanMapping(mappedPath).Where(item => !MappedFileOperations.IsAtOrInside(item.Path, storage.Root)).ToArray()
                        : references.Select(r => DrawerContentSource.Describe(r.Id, r.Path, DrawerContentKind.Reference)).ToArray());
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    issue = $"映射目录暂不可用，可重新定位。{error.Message}";
                }

                if (disposed || closing || runtime.Definition.Removing || runtimes.GetValueOrDefault(runtime.Definition.Id) != runtime) return;
                if (generation != runtime.ExternalGeneration) continue;
                if (runtime.ExternalIssue != issue || !runtime.ExternalItems.SequenceEqual(contents))
                {
                    runtime.ExternalIssue = issue;
                    runtime.ExternalItems = contents;
                    RefreshItems(runtime);
                }
                return;
            } while (true);
        }
        finally { runtime.ScanningExternal = false; }
    }

    private void InvalidateExternalContents(DrawerRuntime runtime)
    {
        runtime.ExternalGeneration++;
        _ = RefreshExternalContentsAsync(runtime);
    }

    private void ChooseMappingClick(object sender, RoutedEventArgs e)
    {
        if (MappingDrawerKindCard.IsChecked != true) return;
        if (demo) { ShowCreateError("隔离样例不能映射外部目录。"); return; }
        try
        {
            var picker = new OpenFolderDialog { Title = "选择映射目录" };
            if (Directory.Exists(NewMappingPathBox.Text)) picker.InitialDirectory = NewMappingPathBox.Text;
            if (picker.ShowDialog(this) != true) return;
            NewMappingPathBox.Text = picker.FolderName;
            CreateErrorLabel.Visibility = Visibility.Collapsed;
        }
        catch (Exception error) { ShowCreateError(error.Message); }
    }

    private void RelocateMapping(DrawerRuntime runtime)
    {
        if (runtime.Busy || maintenanceBusy || closing || exitAfterOperations || demo) return;
        ShowControl();
        var picker = new OpenFolderDialog { Title = "重新定位映射目录" };
        if (picker.ShowDialog(this) != true) return;
        catalog.Relocate(runtime.Definition.Id, picker.FolderName);
        runtime.ExternalItems = [];
        runtime.ExternalIssue = null;
        RefreshItems(runtime);
        InvalidateExternalContents(runtime);
    }

    private void RelocateMappingClick(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (SelectedRuntime is { } runtime) RelocateMapping(runtime);
    });

    private void ChooseReferences(DrawerRuntime runtime, bool directories)
    {
        if (runtime.Busy || maintenanceBusy || closing || exitAfterOperations || demo) return;
        ShowControl();
        string[] paths;
        if (directories)
        {
            var picker = new OpenFolderDialog { Title = "选择要引用的文件夹", Multiselect = true };
            if (picker.ShowDialog(this) != true) return;
            paths = picker.FolderNames;
        }
        else
        {
            var picker = new OpenFileDialog { Title = "选择要引用的文件", Filter = "所有文件 (*.*)|*.*", Multiselect = true, DereferenceLinks = false };
            if (picker.ShowDialog(this) != true) return;
            paths = picker.FileNames;
        }
        var operationId = Guid.NewGuid().ToString("N");
        inputDiagnostics.Write("reference.request", new { OperationId = operationId, Drawer = runtime.Definition.Id,
            Source = directories ? "reference-folder-picker" : "reference-file-picker", Paths = InputDiagnostics.Paths(paths), State = InputState(runtime) });
        try { catalog.AddReferences(runtime.Definition.Id, paths); }
        catch (Exception error)
        {
            inputDiagnostics.Write("reference.error", new { OperationId = operationId, Error = error.ToString() });
            throw;
        }
        inputDiagnostics.Write("reference.completed", new { OperationId = operationId });
        InvalidateExternalContents(runtime);
        Status("引用已添加，文件保留在原位置。");
    }

    private void RelocateReference(DrawerRuntime runtime, ManagerContent item)
    {
        if (demo || runtime.Busy || closing || maintenanceBusy || exitAfterOperations) return;
        if (item.Type == "文件夹" || !item.CanOpen && MessageBox.Show(this, "新引用位置是文件夹吗？", "重定位引用",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            var folder = new OpenFolderDialog { Title = "选择引用的新文件夹" };
            if (folder.ShowDialog(this) != true) return;
            catalog.RelocateReference(runtime.Definition.Id, item.Id, folder.FolderName);
            InvalidateExternalContents(runtime);
            return;
        }
        var picker = new OpenFileDialog { Title = "选择引用的新位置", Filter = "所有文件 (*.*)|*.*", DereferenceLinks = false };
        if (picker.ShowDialog(this) != true) return;
        catalog.RelocateReference(runtime.Definition.Id, item.Id, picker.FileName);
        InvalidateExternalContents(runtime);
    }

    private void RemoveReference(DrawerRuntime runtime, string entryId)
    {
        if (runtime.Busy || maintenanceBusy || closing || exitAfterOperations || demo) return;
        catalog.RemoveReference(runtime.Definition.Id, entryId);
        runtime.ExternalItems = runtime.ExternalItems.Where(item => item.Id != entryId).ToArray();
        RefreshItems(runtime);
        InvalidateExternalContents(runtime);
        Status("已移除引用，原件仍在原位置。");
    }

    private void RemoveReferenceClick(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (sender is FrameworkElement { DataContext: ManagerContent { Kind: DrawerContentKind.Reference } item } && SelectedRuntime is { } runtime)
            RemoveReference(runtime, item.Id);
    });

    private void OpenExternalItemMenu(DrawerRuntime runtime, ExternalContent item)
    {
        var menu = CreateDrawerActionMenu(runtime, DrawerItemName(item.Path, item.IsDirectory));
        if (menu == null) { runtime.Panel.CloseItemMenu(); return; }
        menu.Items.Add(DrawerAction(menu, runtime, "open", "打开", "Open", () =>
        {
            DrawerContentSource.AssertUsable(item.Path);
            OpenContent(item.Path);
        }, item.Available));
        menu.Items.Add(DrawerAction(menu, runtime, "location", "打开所在位置", "Location", () =>
        {
            DrawerContentSource.AssertUsable(item.Path);
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            info.ArgumentList.Add("/select,");
            info.ArgumentList.Add(item.Path);
            Process.Start(info);
        }, item.Available));
        var ready = !runtime.Busy && !exitAfterOperations;
        var canEdit = ready && item.Available && !demo;
        menu.Items.Add(new Separator { Style = (Style)menu.FindResource(typeof(Separator)) });
        menu.Items.Add(DrawerAction(menu, runtime, "copy", "复制", "Copy", () => SetFileClipboard([item.Path], false), ready && item.Available));
        if (item.Kind == DrawerContentKind.Mapped)
            menu.Items.Add(DrawerAction(menu, runtime, "cut", "剪切", "Cut", () => SetFileClipboard([item.Path], true), canEdit));
        menu.Items.Add(DrawerPasteAction(menu, runtime, ready));
        if (item.Kind == DrawerContentKind.Mapped)
        {
            menu.Items.Add(DrawerAction(menu, runtime, "rename", "重命名…", "Properties", () => RenameMapped(runtime, [item.Path]), canEdit));
            menu.Items.Add(DrawerAction(menu, runtime, "recycle", "移到回收站…", "Recycle", () => RecycleMapped(runtime, [item.Path]), canEdit));
        }
        else
            menu.Items.Add(DrawerAction(menu, runtime, "unlink", "移除引用", "Return", () => RemoveReference(runtime, item.Id), ready));
        ShowDrawerActionMenu(menu, runtime);
    }

    internal bool ReorderDesktopContent(string drawerId, string path, int index)
    {
        if (!runtimes.TryGetValue(drawerId, out var runtime)
            || runtime.Busy || closing || maintenanceBusy || exitAfterOperations || runtime.Definition.Removing) return false;
        var contents = BuildContents(runtime.Row.Store).ToList();
        var visible = contents.Where(item => item.ShowOnDesktop).ToList();
        var source = visible.FindIndex(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (source < 0 || index < 0 || index >= visible.Count) return false;
        if (source == index) return true;
        var moved = visible[source];
        visible.RemoveAt(source);
        visible.Insert(index, moved);
        var next = 0;
        SaveContentOrder(runtime, contents.Select(item => item.ShowOnDesktop ? visible[next++].Id : item.Id).ToList());
        Status("抽屉顺序已保存，已切换为手动排序。");
        return true;
    }

    private void SaveContentOrder(DrawerRuntime runtime, List<string> order)
    {
        if (order.Count > 20000) throw new IOException("手动排序最多支持 20000 项。");
        var previous = runtime.Definition.ManualOrder;
        var previousSort = runtime.Definition.Sort;
        runtime.Definition.ManualOrder = order;
        runtime.Definition.Sort = DrawerSort.Manual;
        try { catalog.Save(); }
        catch { runtime.Definition.ManualOrder = previous; runtime.Definition.Sort = previousSort; throw; }
        RefreshItems(runtime);
    }

    private void NudgeContent(DrawerRuntime runtime, HashSet<string> selection, int delta)
    {
        if (runtime.Busy || runtime != SelectedRuntime || closing || maintenanceBusy || exitAfterOperations) return;
        var order = BuildContents(runtime.Row.Store).Select(item => item.Id).ToList();
        if (order.Count > 20000) throw new IOException("手动排序最多支持 20000 项。");
        for (var index = delta < 0 ? 1 : order.Count - 2; index >= 0 && index < order.Count; index -= delta)
        {
            var neighbor = index + delta;
            if (neighbor < 0 || neighbor >= order.Count || !selection.Contains(order[index]) || selection.Contains(order[neighbor])) continue;
            (order[index], order[neighbor]) = (order[neighbor], order[index]);
        }
        SaveContentOrder(runtime, order);
    }

    private void SortContentClick(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (sender is not Button button || SelectedRuntime is not { } runtime) return;
        var menu = CreateStyledDrawerMenu(runtime, button);
        foreach (var (sort, label) in new[] { (DrawerSort.Manual, "手动顺序"), (DrawerSort.Name, "名称"),
            (DrawerSort.Type, "类型"), (DrawerSort.Modified, "修改时间"), (DrawerSort.Size, "大小") })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = runtime.Definition.Sort == sort };
            item.Click += (_, _) => Try(() =>
            {
                var previous = runtime.Definition.Sort;
                runtime.Definition.Sort = sort;
                try { catalog.Save(); } catch { runtime.Definition.Sort = previous; throw; }
                RefreshItems(runtime);
            });
            menu.Items.Add(item);
        }
        var choice = StudioContentsList.SelectedItems.Cast<ManagerContent>().Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (choice.Count > 0)
        {
            menu.Items.Add(new Separator { Style = (Style)menu.FindResource(typeof(Separator)) });
            var up = new MenuItem { Header = "选中项上移" };
            var down = new MenuItem { Header = "选中项下移" };
            up.Click += (_, _) => Try(() => NudgeContent(runtime, choice, -1));
            down.Click += (_, _) => Try(() => NudgeContent(runtime, choice, 1));
            menu.Items.Add(up);
            menu.Items.Add(down);
        }
        menu.IsOpen = true;
    });
}
