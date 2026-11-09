using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Runtime.InteropServices.ComTypes;
using IDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace DesktopGroups;

internal static class StoragePanelCheck
{
    public static int Run(AppStorage storage)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Startup += (_, _) => application.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await CheckAsync(storage); application.Shutdown(0); }
            catch (Exception exception) { Console.Error.WriteLine(exception); application.Shutdown(1); }
        }));
        return application.Run();
    }

    private static async Task CheckAsync(AppStorage storage)
    {
        var store = DemoContent.Create(storage);
        var errors = new List<string>();
        using var panel = new ShortcutPanel(errors.Add, (_, _) => { }, paths =>
        {
            foreach (var path in paths) store.Add(path);
            return true;
        }, store.CanAccept);
        try
        {
            panel.SetItems(store.Active.Select(entry => (System.IO.Path.GetFileNameWithoutExtension(entry.OriginalPath), store.StoredPath(entry), (string?)entry.OriginalPath)));
            panel.Show(520, 180);
            await Task.Delay(60);
            var initial = DesktopAttachment.ReadState(panel.Handle);
            if (!initial.Visible) throw new InvalidOperationException("面板窗口未显示。");
            var surfaceBuilds = panel.SurfaceBuilds;
            var timings = new List<double>();
            for (var index = 0; index < 120; index++)
            {
                var watch = Stopwatch.StartNew();
                panel.MoveTo(520 + index * 2, 180 + index % 20);
                watch.Stop(); timings.Add(watch.Elapsed.TotalMilliseconds);
                var actual = DesktopAttachment.ReadState(panel.Handle);
                if (actual.X != initial.X + index * 2 || actual.Y != initial.Y + index % 20)
                    throw new InvalidOperationException("面板移动坐标不匹配。");
                await Task.Delay(8);
            }
            if (panel.SurfaceBuilds != surfaceBuilds) throw new InvalidOperationException("移动时重新生成了图标画面。");
            var moveRebuilds = panel.SurfaceBuilds - surfaceBuilds;
            var pixels = panel.InspectPixels();
            if (pixels.OpaquePixels < 100 || pixels.BodyAlpha != 225) throw new InvalidOperationException("图标不透明度与半透明背景未正确分离。");
            if (errors.Count > 0) throw new InvalidOperationException(string.Join("；", errors));
            panel.SavePreview(storage.Resolve("checks/StoragePanel/preview.png"));
            // Exercise the same enter/leave and OLE callbacks used by Explorer,
            // using project sample files and without injecting mouse input.
            var stableBuilds = panel.SurfaceBuilds;
            panel.PointerMove(30, 75);
            if (panel.HoveredIndex != 0 || panel.SelectedIndex != -1 || panel.SurfaceBuilds != stableBuilds + 1)
                throw new InvalidOperationException("图标悬停反馈未更新。");
            panel.SavePreview(storage.Resolve("checks/StoragePanel/hover.png"));
            var hoverBuilds = panel.SurfaceBuilds;
            panel.PointerMove(31, 76);
            if (panel.SurfaceBuilds != hoverBuilds) throw new InvalidOperationException("同一图标内移动重复生成画面。");
            panel.SelectAt(30, 75);
            panel.SavePreview(storage.Resolve("checks/StoragePanel/selected.png"));
            panel.PointerLeave();
            if (panel.HoveredIndex != -1 || panel.SelectedIndex != 0) throw new InvalidOperationException("悬停与选中状态未分离。");
            panel.SetFolded(true);
            await FinishAnimation(panel);
            var collapsed = DesktopAttachment.ReadState(panel.Handle);
            panel.PointerMove(30, 20);
            if (!panel.IsExpanded || !panel.IsAnimating) throw new InvalidOperationException("悬停没有立即开始展开动画。");
            var animationBuilds = panel.SurfaceBuilds;
            await Task.Delay(32);
            panel.SavePreview(storage.Resolve("checks/StoragePanel/animation-mid.png"));
            await FinishAnimation(panel);
            if (panel.SurfaceBuilds != animationBuilds) throw new InvalidOperationException("展开动画重复绘制了图标内容。");
            var expanded = DesktopAttachment.ReadState(panel.Handle);
            if (!panel.IsExpanded || expanded.Height <= collapsed.Height) throw new InvalidOperationException("悬停没有立即展开。");
            panel.PointerLeave();
            if (panel.IsExpanded || !panel.IsAnimating) throw new InvalidOperationException("离开没有立即开始收起动画。");
            await FinishAnimation(panel);
            if (panel.IsExpanded || DesktopAttachment.ReadState(panel.Handle).Height != collapsed.Height)
                throw new InvalidOperationException("离开没有立即折叠。");
            var newShortcut = Path.Combine(store.DesktopPath, "拖入样例.url");
            File.WriteAllText(newShortcut, "[InternetShortcut]\r\nURL=https://example.com/drop-check\r\n");
            using var dragData = new ShellSampleData(newShortcut);
            var data = dragData.Data;
            var point = new NativeDesktop.Point();
            uint effect = 2;
            panel.DropTarget.DragEnter(data, 1, point, ref effect);
            if (effect != 2 || !panel.IsExpanded || !panel.IsAnimating)
                throw new InvalidOperationException("拖入折叠标题没有立即展开。");
            await FinishAnimation(panel);
            if (DesktopAttachment.ReadState(panel.Handle).Height != expanded.Height) throw new InvalidOperationException("拖入展开动画未到达完整高度。");
            panel.PointerLeave();
            if (!panel.IsExpanded) throw new InvalidOperationException("拖放过程中错误收起。");
            panel.DropTarget.DragLeave();
            if (panel.IsExpanded) throw new InvalidOperationException("拖离标题后没有立即收起。");
            await FinishAnimation(panel);
            effect = 2; panel.DropTarget.DragEnter(data, 1, point, ref effect);
            effect = 2; panel.DropTarget.Drop(data, 0, point, ref effect);
            if (errors.Count > 0) throw new InvalidOperationException(string.Join("；", errors));
            if (effect != 2 || File.Exists(newShortcut) || store.Active.Count != 4)
                throw new InvalidOperationException("展开后的 OLE 拖入没有完成收纳。");
            var performed = ReadEffect(data, "Performed DropEffect");
            var logical = ReadEffect(data, "Logical Performed DropEffect");
            if (performed != 0 || logical != 2) throw new InvalidOperationException("未正确通知拖出方无需再次删除源文件。");
            panel.PointerLeave();
            var rejectedFile = Path.Combine(store.DesktopPath, "不支持.txt"); File.WriteAllText(rejectedFile, "not moved");
            using var unsupportedData = new ShellSampleData(rejectedFile);
            var unsupported = unsupportedData.Data;
            effect = 2; panel.DropTarget.DragEnter(unsupported, 1, point, ref effect);
            if (effect != 0 || panel.IsExpanded || !File.Exists(rejectedFile)) throw new InvalidOperationException("不支持的文件没有被正确拒绝。");
            panel.DropTarget.DragLeave();
            if (errors.Count > 0) throw new InvalidOperationException(string.Join("；", errors));
            using (var ghost = new IconDragGhost(ShortcutInfo.LoadIcon(store.StoredPath(store.Active[0])), new NativeDesktop.Point { X = 800, Y = 240 }))
                ghost.Move(new NativeDesktop.Point { X = 820, Y = 260 });
            timings.Sort();
            // The production receiver queues its own move. The Shell must never delete
            // the source while that queue is still waiting, including on transfer failure.
            var queuedPath = Path.Combine(store.DesktopPath, "后台拖入.url");
            File.WriteAllText(queuedPath, "[InternetShortcut]\r\nURL=https://example.com/queued-drop\r\n");
            using (var queue = new StaWorkQueue("queued OLE check"))
            using (var release = new ManualResetEventSlim())
            using (var queuedData = new ShellSampleData(queuedPath))
            {
                Task<bool>? work = null;
                var queuedTarget = new PanelDropTarget(store.CanAccept, paths =>
                {
                    work = queue.Run(() => { release.Wait(TimeSpan.FromSeconds(5)); return File.Exists(paths[0]); });
                    return true;
                }, (_, _) => { }, errors.Add);
                uint queuedEffect = 2;
                queuedTarget.DragEnter(queuedData.Data, 0, default, ref queuedEffect);
                queuedEffect = 2; queuedTarget.Drop(queuedData.Data, 0, default, ref queuedEffect);
                if (queuedEffect != 2 || !File.Exists(queuedPath) || ReadEffect(queuedData.Data, "Performed DropEffect") != 0
                    || ReadEffect(queuedData.Data, "Logical Performed DropEffect") != 2 || work?.IsCompleted != false)
                    throw new InvalidOperationException("后台排队前的 OLE 回执未保护源文件。");
                release.Set();
                if (!await work!) throw new InvalidOperationException("后台操作开始前源文件丢失。");
                File.Delete(queuedPath);
            }
            storage.WriteJson("checks/StoragePanel/results.json", new
            {
                Samples = 120, panel.MoveCount, SurfaceRebuildsDuringMove = moveRebuilds,
                ShellIconRepositionCalls = 0, pixels.OpaquePixels, pixels.BodyAlpha,
                MoveCallP95Milliseconds = timings[(int)(timings.Count * .95)],
                HoverFeedback = true, SelectedStateIndependent = true, ImmediateHoverAnimationStart = true,
                panel.AnimationFrames, AnimationRebuildsContent = false, AnimatedExpansionAndCollapse = true,
                OleDragEnterExpands = true, OleDropCollectedSample = true, OptimizedMoveReceipt = true,
                UnsupportedDropRejected = true, NativeOleRegistrationSucceeded = true,
                DragGhostCreatedMovedDisposed = true,
                VisualDesktopAccepted = false, RealDesktopFilesMoved = false
            });
            Console.WriteLine($"PASS 120 single-surface moves; no bitmap rebuilds or Shell icon reposition; icon alpha255 and body alpha225; move API p95 {timings[(int)(timings.Count * .95)]:F3}ms");
            Console.WriteLine("PASS queued OLE receiver reports no Shell source deletion while background work is pending");
            Console.WriteLine("PASS immediate-start animated expansion/collapse using cached pixels; independent hover/selection; OLE drag-enter expansion and sample drop; source deletion receipt; unsupported files rejected");
        }
        finally
        {
            panel.Dispose();
            var issues = store.RestoreAll();
            if (issues.Count > 0) throw new InvalidOperationException(string.Join("；", issues));
        }
    }

    private static async Task FinishAnimation(ShortcutPanel panel)
    {
        var deadline = Environment.TickCount64 + 1500;
        while (panel.IsAnimating && Environment.TickCount64 < deadline) await Task.Delay(16);
        if (panel.IsAnimating) throw new InvalidOperationException("展开／收起动画未正常结束。");
    }

    private static int ReadEffect(IDataObject data, string name)
    {
        var format = new FORMATETC { cfFormat = unchecked((short)RegisterClipboardFormat(name)), dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
        data.GetData(ref format, out var medium);
        try
        {
            var pointer = GlobalLock(medium.unionmember);
            if (pointer == 0) throw new InvalidOperationException("无法读取拖放结果。");
            try { return System.Runtime.InteropServices.Marshal.ReadInt32(pointer); }
            finally { GlobalUnlock(medium.unionmember); }
        }
        finally { ReleaseStgMedium(ref medium); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [System.Runtime.InteropServices.DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    // Use the Shell's real data object, including its optimized-move receipts.
    // WPF's standalone DataObject does not implement native IDataObject.SetData.
    private sealed class ShellSampleData : IDisposable
    {
        public IDataObject Data { get; }
        public ShellSampleData(string path)
        {
            nint folder = 0, item = 0;
            try
            {
                System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(SHParseDisplayName(Path.GetDirectoryName(path)!, 0, out folder, 0, out _));
                System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(SHParseDisplayName(path, 0, out item, 0, out _));
                var id = new Guid("0000010e-0000-0000-C000-000000000046");
                System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(SHCreateDataObject(folder, 1, [ILFindLastID(item)], 0, in id, out var data));
                Data = data;
            }
            finally
            {
                if (folder != 0) System.Runtime.InteropServices.Marshal.FreeCoTaskMem(folder);
                if (item != 0) System.Runtime.InteropServices.Marshal.FreeCoTaskMem(item);
            }
        }
        public void Dispose() => System.Runtime.InteropServices.Marshal.ReleaseComObject(Data);
        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int SHParseDisplayName(string name, nint bind, out nint id, uint attributes, out uint actual);
        [System.Runtime.InteropServices.DllImport("shell32.dll")] private static extern nint ILFindLastID(nint id);
        [System.Runtime.InteropServices.DllImport("shell32.dll")] private static extern int SHCreateDataObject(nint folder, uint count, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPArray, SizeParamIndex = 1)] nint[] items, nint inner, in Guid id, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Interface)] out IDataObject data);
    }
}
