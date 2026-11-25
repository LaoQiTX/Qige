using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace DesktopGroups;

internal sealed partial class StorageWindow
{
    private bool CanOperateOnSelection(DrawerRuntime runtime) =>
        !disposed && !closing && !maintenanceBusy && !exitAfterOperations && !runtime.Busy && !runtime.Definition.Removing;

    private ManagerContent[] ResolveDrawerSelection(DrawerRuntime runtime, IReadOnlyList<string> paths)
    {
        var wanted = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = BuildContents(runtime.Row.Store).Where(item => item.ShowOnDesktop && wanted.Contains(item.Path))
            .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (items.Length != wanted.Count) throw new IOException("选中的项目已变化，请重新选择。");
        return items;
    }

    private void OpenDrawerSelectionMenu(DrawerRuntime runtime, IReadOnlyList<string> paths)
    {
        var items = ResolveDrawerSelection(runtime, paths);
        var menu = CreateDrawerActionMenu(runtime, $"已选中 {items.Length} 项");
        if (menu == null) return;
        var ready = CanOperateOnSelection(runtime);
        var managed = items.Where(item => item.CanReturn).ToArray();
        var mapped = items.Where(item => item.Kind == DrawerContentKind.Mapped).Select(item => item.Path).ToArray();
        var references = items.Where(item => item.CanUnlink).Select(item => item.Id).ToArray();
        menu.Items.Add(new Separator { Style = (Style)menu.FindResource(typeof(Separator)) });
        void Add(string key, string title, string icon, Action action, bool enabled)
        {
            menu.Items.Add(DrawerAction(menu, runtime, key, title, icon, () =>
            {
                if (!CanOperateOnSelection(runtime)) throw new IOException("请等待抽屉文件操作结束后重试。");
                ResolveDrawerSelection(runtime, paths);
                action();
            }, ready && enabled));
        }
        Add("copy", "复制选中项", "Copy", () => SetFileClipboard(paths.ToArray(), false), true);
        if (mapped.Length > 0)
        {
            Add("cut", "剪切选中项", "Cut", () => SetFileClipboard(mapped, true), !demo);
            Add("recycle", "将选中项移到回收站…", "Recycle", () => RecycleMapped(runtime, mapped), !demo);
        }
        if (managed.Length > 0)
        {
            var label = managed.Length == items.Length ? "选中项" : $"{managed.Length} 个托管项";
            Add("return", $"归还{label}并解除分组", "Return", () => _ = ReturnManagedSelectionAsync(runtime, managed), !demo);
            Add("recycle", $"将{label}移到回收站…", "Recycle", () => RecycleManaged(runtime, managed), !demo);
        }
        if (references.Length > 0)
            Add("unlink", $"移除 {references.Length} 个引用", "Return", () =>
            {
                catalog.RemoveReferences(runtime.Definition.Id, references);
                var removed = references.ToHashSet(StringComparer.OrdinalIgnoreCase);
                runtime.ExternalItems = runtime.ExternalItems.Where(item => !removed.Contains(item.Id)).ToArray();
                RefreshItems(runtime);
                InvalidateExternalContents(runtime);
                Status("已移除选中引用，原件仍在原位置。");
            }, !demo);
        ShowDrawerActionMenu(menu, runtime);
    }

    private void DropSelectionOnDesktop(DrawerRuntime runtime, IReadOnlyList<string> paths) => Try(() =>
    {
        if (demo) { Status("样例内容仅在样例目录内使用。"); return; }
        if (!CanOperateOnSelection(runtime)) throw new IOException("请等待抽屉文件操作结束后重试。");
        var items = ResolveDrawerSelection(runtime, paths);
        if (runtime.Definition.Kind == DrawerKind.Desktop)
        {
            var managed = items.Where(item => item.CanReturn).ToArray();
            if (managed.Length == 0) { Status("引用不会移动原件，可在右键菜单中移除引用。"); return; }
            _ = ReturnManagedSelectionAsync(runtime, managed);
            return;
        }
        var (copy, move) = DrawerDropModifiers();
        var desktop = runtime.Row.Store.DesktopPath;
        var owned = ManagedOriginalPaths();
        _ = RunMappedOperationAsync(runtime, "拖到桌面", () =>
        {
            var files = new MappedFileOperations(storage);
            var issues = new List<string>();
            foreach (var item in items)
            {
                try
                {
                    var moveFile = !copy && (move || MappedFileOperations.SameDrive(item.Path, desktop));
                    AssertDrawerTransferAllowed(item.Path, desktop, moveFile, owned);
                    files.Transfer(item.Path, desktop, moveFile, default);
                }
                catch (Exception error) { issues.Add($"{item.Name}：{error.Message}"); }
            }
            return DrawerDropResult(items.Length - issues.Count, items.Length, issues);
        });
    });

    private static (bool Copy, bool Move) DrawerDropModifiers() =>
        ((GetAsyncKeyState(0x11) & 0x8000) != 0, (GetAsyncKeyState(0x10) & 0x8000) != 0);

    private string[] ManagedOriginalPaths() => catalog.Stores.Values.SelectMany(store => store.Profile.Entries)
        .Select(entry => entry.OriginalPath).ToArray();

    private bool DropBetweenDrawers(DrawerRuntime source, IReadOnlyList<string> paths, NativeDesktop.Point point)
    {
        var target = runtimes.Values.FirstOrDefault(runtime => runtime != source && runtime.Panel.IsAtPoint(point));
        if (target == null) return false;
        Try(() =>
        {
            if (demo) { Status("样例内容仅在样例目录内使用。"); return; }
            var items = ResolveDrawerSelection(source, paths);
            var (copy, move) = DrawerDropModifiers();
            _ = TransferDrawerSelectionAsync(source, target, items, copy, move);
        });
        return true;
    }

    private async Task TransferDrawerSelectionAsync(DrawerRuntime source, DrawerRuntime target, ManagerContent[] items, bool copy, bool forceMove)
    {
        var directory = target.Definition.MappedPath;
        if (!CanOperateOnSelection(source) || !CanOperateOnSelection(target) ||
            directory != null && !CanTransferIntoMapping(target, items.Select(item => item.Path).ToArray()))
        { Status("请等待抽屉文件操作结束后重试。"); return; }

        var owned = ManagedOriginalPaths();
        var issues = new List<string>();
        var completed = 0;
        using var cancellation = new CancellationTokenSource();
        source.Busy = target.Busy = true;
        try
        {
            RefreshHealthText();
            Status($"正在处理 {items.Length} 个选中项…");
            if (directory != null) { activeMappedTransfer = cancellation; CancelTransferButton.Visibility = Visibility.Visible; }
            if (directory == null)
            {
                var references = items.Where(item => item.CanUnlink && !copy).ToArray();
                var additions = items.Where(item => item.Kind == DrawerContentKind.Mapped || item.CanUnlink && copy).ToArray();
                try
                {
                    if (references.Length > 0) catalog.MoveReferences(source.Definition.Id, target.Definition.Id, references.Select(item => item.Id).ToArray());
                    completed += references.Length;
                }
                catch (Exception error) { issues.Add("移动引用：" + error.Message); }
                try
                {
                    if (additions.Length > 0) catalog.AddReferences(target.Definition.Id, additions.Select(item => item.Path).ToArray());
                    completed += additions.Length;
                }
                catch (Exception error) { issues.Add("添加引用：" + error.Message); }
            }

            var pending = directory != null ? items : items.Where(item => item.Kind == DrawerContentKind.Managed).ToArray();
            if (pending.Length > 0)
            {
                var result = await fileWorker.Run(() =>
                {
                    var originalStore = source.Row.Store.Detached();
                    var targetStore = target.Row.Store.Detached();
                    var batch = TransferSelectedFiles(storage, originalStore, targetStore, pending, directory,
                        copy, forceMove, owned, cancellation.Token);
                    return (Source: originalStore.Profile, Target: targetStore.Profile, batch.Completed, batch.Issues);
                });
                if (disposed) return;
                completed += result.Completed;
                issues.AddRange(result.Issues);
                if (source.Definition.Kind == DrawerKind.Desktop && pending.Any(item => item.Kind == DrawerContentKind.Managed))
                    source.Row.Store.Publish(result.Source);
                if (target.Definition.Kind == DrawerKind.Desktop) target.Row.Store.Publish(result.Target);
            }
            Status(DrawerDropResult(completed, items.Length, issues, cancellation.IsCancellationRequested));
        }
        catch (Exception error) { Status(error.Message); }
        finally
        {
            source.Busy = target.Busy = false;
            if (directory != null) activeMappedTransfer = null;
            if (!disposed)
            {
                if (directory != null) CancelTransferButton.Visibility = Visibility.Collapsed;
                RefreshHealthText();
                InvalidateExternalContents(source);
                InvalidateExternalContents(target);
                if (exitAfterOperations && BusyOperations == 0 && !restoringExit) { exitAfterOperations = false; RequestExit(); }
                if (!disposed) _ = ScanRecoveryAsync();
            }
        }
    }

    internal static (int Completed, List<string> Issues) TransferSelectedFiles(AppStorage storage,
        ShortcutStore originalStore, ShortcutStore targetStore, IReadOnlyList<ManagerContent> pending,
        string? directory, bool copy, bool forceMove, IReadOnlyList<string> owned, CancellationToken cancellation)
    {
        var issues = new List<string>();
        var completed = 0;
        var files = new MappedFileOperations(storage);
        foreach (var item in pending)
        {
            if (cancellation.IsCancellationRequested) break;
            try
            {
                if (directory == null)
                {
                    originalStore.ReturnAndUngroup(item.Path);
                    targetStore.Add(item.OriginalPath);
                }
                else
                {
                    var managed = item.Kind == DrawerContentKind.Managed;
                    var move = !copy && (forceMove || (managed || item.Kind == DrawerContentKind.Mapped) &&
                        MappedFileOperations.SameDrive(item.OriginalPath, directory));
                    var path = managed && move ? item.OriginalPath : item.Path;
                    var destination = AssertDrawerTransferAllowed(path, directory, move, owned, managed ? item.OriginalPath : null);
                    if (managed && move)
                    {
                        DrawerContentSource.AssertUsable(directory, directory: true);
                        if (Path.Exists(destination)) throw new IOException("目标位置已有同名项目，未覆盖。");
                        originalStore.ReturnAndUngroup(item.Path);
                    }
                    files.Transfer(path, directory, move, cancellation, allowManagedCopy: managed);
                }
                completed++;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
            catch (Exception error) { issues.Add($"{item.Name}：{error.Message}"); }
        }
        return (completed, issues);
    }

    private static string DrawerDropResult(int completed, int total, List<string> issues, bool cancelled = false) =>
        $"已处理 {completed}/{total} 项" + (cancelled ? "，已取消剩余操作" : "") + "。" + string.Join("；", issues);
}
