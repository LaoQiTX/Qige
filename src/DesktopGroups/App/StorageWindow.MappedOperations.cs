using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DesktopGroups;

internal sealed partial class StorageWindow
{
    private CancellationTokenSource? activeMappedTransfer;

    private string[] SelectedMappedItems() => StudioContentsList.SelectedItems.Cast<ManagerContent>()
        .Where(item => item.Kind == DrawerContentKind.Mapped && item.CanOpen).Select(item => item.Path).ToArray();

    private void StudioContentsDoubleClick(object sender, MouseButtonEventArgs e) => Try(() =>
    {
        if (e.OriginalSource is FrameworkElement { DataContext: ManagerContent { CanOpen: true } item })
        {
            DrawerContentSource.AssertUsable(item.Path);
            OpenContent(item.Path);
        }
    });

    private static void SetFileClipboard(string[] paths, bool cut)
    {
        if (paths.Length == 0) return;
        foreach (var path in paths) DrawerContentSource.AssertUsable(path);
        var collection = new StringCollection();
        collection.AddRange(paths);
        var data = new DataObject();
        data.SetFileDropList(collection);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2u : 1u)));
        Clipboard.SetDataObject(data, true);
    }

    private void CopyMappedSelection(bool cut)
    {
        var paths = SelectedMappedItems();
        SetFileClipboard(paths, cut);
        if (paths.Length > 0) Status($"已{(cut ? "剪切" : "复制")} {paths.Length} 项。");
    }

    private void PasteIntoMapping(DrawerRuntime runtime)
    {
        if (demo || runtime.Busy || !Clipboard.ContainsFileDropList()) return;
        var paths = Clipboard.GetFileDropList().Cast<string>().ToArray();
        var move = Clipboard.GetData("Preferred DropEffect") is MemoryStream effect && effect.Length >= 4
            && BitConverter.ToUInt32(effect.ToArray()) == 2;
        _ = TransferIntoMappingAsync(runtime, paths, _ => move);
    }

    private bool CanTransferIntoMapping(DrawerRuntime runtime, IReadOnlyList<string> paths) =>
        paths.Count > 0 && runtime.Definition.MappedPath != null && !demo && !closing && !maintenanceBusy &&
        !exitAfterOperations && !runtime.Busy && !runtime.Definition.Removing && activeMappedTransfer == null;

    private static uint PickMappedDropEffect(DrawerRuntime runtime, IReadOnlyList<string> paths, uint keys, uint allowed)
    {
        if ((allowed & 1) == 0) return 0; // A queued transfer must be safe to acknowledge as COPY.
        if ((keys & 8) != 0) return 1; // Ctrl
        if ((keys & 4) != 0) return allowed & 2; // Shift
        return (allowed & 2) != 0 && paths.All(path => MappedFileOperations.SameDrive(path, runtime.Definition.MappedPath!)) ? 2u : 1u;
    }

    private async Task TransferIntoMappingAsync(DrawerRuntime runtime, IReadOnlyList<string> paths, Func<string, bool> chooseMove)
    {
        var operationId = runtime.Panel.DropOperationId ?? Guid.NewGuid().ToString("N");
        inputDiagnostics.Write("mapping.request", new { OperationId = operationId, Drawer = runtime.Definition.Id,
            Paths = InputDiagnostics.Paths(paths), State = InputState(runtime) });
        if (!CanTransferIntoMapping(runtime, paths))
        { inputDiagnostics.Write("mapping.rejected", new { OperationId = operationId, Reason = "mapping-unavailable" }); return; }
        var directory = runtime.Definition.MappedPath!;
        var copySources = runtimes.Values.Where(drawer => drawer.Definition.Kind == DrawerKind.Desktop && !drawer.Busy)
            .SelectMany(drawer => drawer.Row.Store.Active.Select(drawer.Row.Store.StoredPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var operations = paths.Distinct(StringComparer.OrdinalIgnoreCase).Select(path => (Path: path, Move: chooseMove(path))).ToArray();
        var desktopEntries = catalog.Stores.Values.SelectMany(store => store.Profile.Entries).Select(entry => entry.OriginalPath).ToArray();
        using var cancellation = new CancellationTokenSource();
        activeMappedTransfer = cancellation;
        CancelTransferButton.Visibility = Visibility.Visible;
        try
        {
            await RunMappedOperationAsync(runtime, "传输", () =>
            {
                var files = new MappedFileOperations(storage);
                var issues = new List<string>();
                var completed = 0;
                foreach (var operation in operations)
                {
                    if (cancellation.IsCancellationRequested) break;
                    Status($"正在{(operation.Move ? "移动" : "复制")} {completed + issues.Count + 1}/{operations.Length} · {Path.GetFileName(operation.Path)}");
                    try
                    {
                        var destination = AssertDrawerTransferAllowed(operation.Path, directory, operation.Move, desktopEntries);
                        files.Transfer(operation.Path, directory, operation.Move, cancellation.Token, allowManagedCopy: copySources.Contains(operation.Path));
                        completed++;
                        inputDiagnostics.Write("mapping.file-completed", new { OperationId = operationId, operation.Path, operation.Move, destination });
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception error)
                    {
                        inputDiagnostics.Write("mapping.file-error", new { OperationId = operationId, operation.Path,
                            operation.Move, HResult = $"0x{error.HResult:X8}", Error = error.ToString() });
                        issues.Add($"{Path.GetFileName(operation.Path)}：{error.Message}");
                    }
                }
                inputDiagnostics.Write("mapping.completed", new { OperationId = operationId, completed,
                    Count = operations.Length, Cancelled = cancellation.IsCancellationRequested, Issues = issues.ToArray() });
                return $"已完成 {completed}/{operations.Length} 项" + (cancellation.IsCancellationRequested ? "，传输已取消" : "")
                    + (issues.Count > 0 ? "。" + string.Join("；", issues) : "。");
            });
        }
        finally { activeMappedTransfer = null; CancelTransferButton.Visibility = Visibility.Collapsed; }
    }

    private static string AssertDrawerTransferAllowed(string path, string directory, bool move, IReadOnlyList<string> desktopEntries,
        string? detachingOriginal = null)
    {
        var source = Path.GetFullPath(path);
        var destination = Path.Combine(directory, Path.GetFileName(Path.TrimEndingDirectorySeparator(source)));
        if (desktopEntries.Any(owned => string.Equals(owned, destination, StringComparison.OrdinalIgnoreCase) ||
            move && !string.Equals(owned, detachingOriginal, StringComparison.OrdinalIgnoreCase) && MappedFileOperations.IsAtOrInside(owned, source)))
            throw new IOException("此项目包含已分组的桌面文件，请先解除原分组。");
        return destination;
    }

    private async Task RunMappedOperationAsync(DrawerRuntime runtime, string label, Func<string> operation)
    {
        if (demo || closing || maintenanceBusy || exitAfterOperations || runtime.Busy || runtime.Definition.Removing || runtime.Definition.MappedPath == null) return;
        runtime.Busy = true;
        RefreshHealthText();
        try { Status(await fileWorker.Run(operation)); }
        catch (Exception error)
        {
            inputDiagnostics.Write("mapping.error", new { Drawer = runtime.Definition.Id, label, Error = error.ToString() });
            Status($"{label}失败：{error.Message}");
        }
        finally
        {
            runtime.Busy = false;
            RefreshHealthText();
            InvalidateExternalContents(runtime);
            _ = ScanRecoveryAsync();
            if (exitAfterOperations && BusyOperations == 0 && !restoringExit && !disposed) { exitAfterOperations = false; RequestExit(); }
        }
    }

    private void CancelTransferClick(object sender, RoutedEventArgs e) => activeMappedTransfer?.Cancel();

    private void MappedOperationsClick(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (sender is not Button button || SelectedRuntime is not { } runtime) return;
        var menu = CreateStyledDrawerMenu(runtime, button);
        var paths = SelectedMappedItems();
        void Add(string title, Action command, bool enabled = true)
        {
            var item = new MenuItem { Header = title, IsEnabled = enabled };
            item.Click += (_, _) => Try(command);
            menu.Items.Add(item);
        }
        if (runtime.Definition.Kind == DrawerKind.Desktop)
        {
            var items = StudioContentsList.SelectedItems.Cast<ManagerContent>().ToArray();
            var managed = items.Where(item => item.Kind == DrawerContentKind.Managed && item.CanReturn).ToArray();
            var references = items.Where(item => item.Kind == DrawerContentKind.Reference).ToArray();
            Add("复制选中项", () => SetFileClipboard(items.Where(item => item.CanOpen).Select(item => item.Path).ToArray(), false), items.Any(item => item.CanOpen));
            Add("归还并剪切…", () => _ = ReturnManagedSelectionAsync(runtime, managed, cut: true), managed.Length > 0);
            Add("归还并解除分组", () => _ = ReturnManagedSelectionAsync(runtime, managed), managed.Length > 0);
            Add("删除实际文件到回收站…", () => RecycleManaged(runtime, managed), managed.Length > 0);
            Add("重命名…", () => RenameManaged(runtime, managed[0]), managed.Length == 1);
            Add("移除引用", () => { foreach (var reference in references) RemoveReference(runtime, reference.Id); }, references.Length > 0);
            Add("重定位引用…", () => RelocateReference(runtime, references[0]), references.Length == 1);
            Add("粘贴文件", () => PasteIntoOrganizer(runtime));
            Add("新建文件夹…", () => CreateManaged(runtime, folder: true));
            Add("新建文本文档…", () => CreateManaged(runtime, folder: false));
        }
        else
        {
            Add("复制选中项", () => SetFileClipboard(paths, false), paths.Length > 0);
            Add("剪切选中项", () => SetFileClipboard(paths, true), paths.Length > 0);
            Add("粘贴文件", () => PasteIntoMapping(runtime));
            Add("重命名…", () => RenameMapped(runtime, paths), paths.Length == 1);
            Add("移到回收站…", () => RecycleMapped(runtime, paths), paths.Length > 0);
            Add("新建文件夹…", () => CreateMapped(runtime, true));
            Add("新建文本文档…", () => CreateMapped(runtime, false));
        }
        menu.IsOpen = true;
    });

    private async Task ReturnManagedSelectionAsync(DrawerRuntime runtime, ManagerContent[] items, bool cut = false, bool recycle = false)
    {
        if (demo || items.Length == 0) return;
        var returned = new List<string>();
        await RunStoreAsync(runtime, recycle ? "回收" : "归还并解除分组", store =>
        {
            var issues = new List<string>();
            foreach (var item in items)
            {
                try
                {
                    store.ReturnAndUngroup(item.Path);
                    if (recycle) RecycleBin.Send(item.OriginalPath);
                    else returned.Add(item.OriginalPath);
                }
                catch (Exception error) { issues.Add($"{item.Name}：{error.Message}"); }
            }
            return issues;
        });
        if (cut && returned.Count > 0) Try(() => SetFileClipboard(returned.ToArray(), true));
    }

    private void RecycleManaged(DrawerRuntime runtime, ManagerContent[] items)
    {
        if (items.Length == 0 || demo || !ConfirmRecycle(items.Length)) return;
        _ = ReturnManagedSelectionAsync(runtime, items, recycle: true);
    }

    private void RenameManaged(DrawerRuntime runtime, ManagerContent item)
    {
        if (demo || runtime.Busy) return;
        var name = AskFileName("重命名", Path.GetFileName(item.OriginalPath));
        if (name == null) return;
        var target = MappedFileOperations.PathForNewItem(Path.GetDirectoryName(item.OriginalPath)!, name);
        if (target.Equals(item.OriginalPath, StringComparison.OrdinalIgnoreCase)) return;
        catalog.EnsureOwnership(runtime.Definition.Id, [target]);
        if (IncludesManagedOriginal(target) || File.Exists(target) || Directory.Exists(target)) throw new IOException("已有同名项目。");
        _ = RunStoreAsync(runtime, "重命名托管项目", store =>
        {
            store.ReturnAndUngroup(item.Path);
            MappedFileOperations.Rename(item.OriginalPath, name);
            store.Add(target);
            return [];
        });
    }

    private void CreateManaged(DrawerRuntime runtime, bool folder)
    {
        if (demo || runtime.Busy) return;
        var desktop = runtime.Row.Store.DesktopPath;

        var name = AskFileName(folder ? "新建托管文件夹" : "新建托管文档", folder ? "新建文件夹" : "新建文本文档.txt");
        if (name == null) return;
        if (!folder && !name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) name += ".txt";
        var target = MappedFileOperations.PathForNewItem(desktop, name);
        if (IncludesManagedOriginal(target)) throw new IOException("名称与桌面收纳记录重叠。");
        _ = RunStoreAsync(runtime, "新建托管项目", store =>
        {
            MappedFileOperations.Create(desktop, name, folder);
            store.Add(target);
            return [];
        });
    }

    private void PasteIntoOrganizer(DrawerRuntime runtime)
    {
        if (runtime.Busy || closing || maintenanceBusy || exitAfterOperations || runtime.Definition.Removing || !Clipboard.ContainsFileDropList()) return;
        var paths = Clipboard.GetFileDropList().Cast<string>().ToArray();
        if (paths.Length == 0) return;
        if (runtime.Row.Store.CanAccept(paths))
        {
            QueueAddPaths(runtime.Definition.Id, paths, "clipboard");
            return;
        }
        if (demo) return;
        catalog.AddReferences(runtime.Definition.Id, paths);
        InvalidateExternalContents(runtime);
        Status("已粘贴为引用，文件保留在原位置。");
    }

    private void RecycleMapped(DrawerRuntime runtime, string[] paths)
    {
        if (demo || paths.Length == 0 || runtime.Busy || runtime.Definition.MappedPath is not { } directory) return;
        if (paths.Any(path => !string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase) ||
            IncludesManagedOriginal(path) || MappedFileOperations.IsAtOrInside(storage.Root, path) || MappedFileOperations.IsAtOrInside(path, storage.Root)))
            throw new IOException("项目与栖格数据或桌面收纳记录重叠，请先解除分组。");
        if (!ConfirmRecycle(paths.Length)) return;
        _ = RunMappedOperationAsync(runtime, "回收", () =>
        {
            var issues = new List<string>();
            foreach (var path in paths)
            {
                try { RecycleBin.Send(path); }
                catch (Exception error) { issues.Add($"{Path.GetFileName(path)}：{error.Message}"); }
            }
            return $"已回收 {paths.Length - issues.Count}/{paths.Length} 项。" + string.Join("；", issues);
        });
    }

    private bool ConfirmRecycle(int count)
    {
        ShowControl();
        return MessageBox.Show(this, $"将 {count} 项实际文件或文件夹移到 Windows 回收站？", "移到回收站",
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;
    }

    private bool IncludesManagedOriginal(string path) => catalog.Stores.Values.SelectMany(store => store.Profile.Entries)
        .Any(entry => MappedFileOperations.IsAtOrInside(entry.OriginalPath, path));

    private void RenameMapped(DrawerRuntime runtime, string[] paths)
    {
        if (paths.Length != 1 || runtime.Busy || runtime.Definition.MappedPath == null) return;
        if (!string.Equals(Path.GetDirectoryName(paths[0]), runtime.Definition.MappedPath, StringComparison.OrdinalIgnoreCase)) return;
        var name = AskFileName("重命名", Path.GetFileName(paths[0]));
        if (name == null) return;
        var destination = MappedFileOperations.PathForNewItem(runtime.Definition.MappedPath, name);
        if (MappedFileOperations.IsAtOrInside(storage.Root, paths[0]) || IncludesManagedOriginal(paths[0]) || IncludesManagedOriginal(destination))
            throw new IOException("项目与桌面收纳记录重叠，请先解除分组。");
        _ = RunMappedOperationAsync(runtime, "重命名", () => { MappedFileOperations.Rename(paths[0], name); return $"已重命名为 {name}。"; });
    }

    private void CreateMapped(DrawerRuntime runtime, bool folder)
    {
        if (runtime.Busy || runtime.Definition.MappedPath is not { } directory) return;
        var name = AskFileName(folder ? "新建文件夹" : "新建文本文档", folder ? "新建文件夹" : "新建文本文档.txt");
        if (name == null) return;
        if (!folder && !name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) name += ".txt";
        var destination = MappedFileOperations.PathForNewItem(directory, name);
        if (MappedFileOperations.IsAtOrInside(storage.Root, destination) || IncludesManagedOriginal(destination))
            throw new IOException("名称与栖格数据或桌面收纳记录重叠。");
        _ = RunMappedOperationAsync(runtime, "新建", () => { MappedFileOperations.Create(directory, name, folder); return $"已创建 {name}。"; });
    }

    private string? AskFileName(string title, string initial)
    {
        ShowControl();
        var dialog = new Window
        {
            Title = title, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false,
            FontFamily = FontFamily,
            FontSize = FontSize, UseLayoutRounding = true
        };
        dialog.SetResourceReference(BackgroundProperty, "PaletteSurface");
        dialog.SetResourceReference(ForegroundProperty, "PaletteText");
        dialog.SourceInitialized += (_, _) => ApplyCaptionTheme(dialog);
        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Qige;component/App/SharedControls.xaml", UriKind.Relative) });
        var input = new TextBox { Text = initial };
        System.Windows.Automation.AutomationProperties.SetName(input, "文件或文件夹名称");
        var message = new TextBlock { Style = (Style)dialog.FindResource("InputError"), Text = "" };
        var controls = new StackPanel { Width = 336, Margin = new Thickness(20) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var confirm = new Button { Content = "确定", IsDefault = true, MinWidth = 72, Style = (Style)dialog.FindResource("PrimaryButton") };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 72, Margin = new Thickness(0, 0, 8, 0) };
        confirm.Click += (_, _) =>
        {
            try { MappedFileOperations.ValidateLeaf(input.Text); }
            catch (IOException error)
            {
                FormFeedback.SetError(input, error.Message);
                FormFeedback.ShowError(message, error.Message);
                input.Focus();
                return;
            }
            dialog.DialogResult = true;
        };
        input.TextChanged += (_, _) => { FormFeedback.SetError(input, null); FormFeedback.ShowError(message, ""); };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        controls.Children.Add(new TextBlock { Text = "文件或文件夹名称", Margin = new Thickness(0, 0, 0, 6) });
        controls.Children.Add(input);
        controls.Children.Add(message);
        controls.Children.Add(buttons);
        dialog.Content = controls;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text : null;
    }
}
