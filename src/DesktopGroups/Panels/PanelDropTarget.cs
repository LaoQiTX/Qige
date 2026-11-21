using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace DesktopGroups;

[ComVisible(true), Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPanelDropTarget
{
    [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] IDataObject data, uint keys, NativeDesktop.Point point, ref uint effect);
    [PreserveSig] int DragOver(uint keys, NativeDesktop.Point point, ref uint effect);
    [PreserveSig] int DragLeave();
    [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] IDataObject data, uint keys, NativeDesktop.Point point, ref uint effect);
}

// OLE sends DragEnter/DragLeave while the source owns mouse capture. WM_MOUSEMOVE
// and WM_DROPFILES alone cannot implement expansion before a file is released.
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class PanelDropTarget(Func<IReadOnlyList<string>, bool> canAccept,
    Func<IReadOnlyList<string>, bool> receive,     Action<bool, bool> dragState, Action<string> report,
    Func<IReadOnlyList<string>, uint, uint, uint>? mappedEffect = null,
    Func<IReadOnlyList<string>, uint, bool>? receiveMapped = null,
    Action<string, object>? diagnostic = null) : IPanelDropTarget
{
    private string[] paths = [];
    private bool accepted;
    private uint lastKeys, lastAllowed, lastEffect;
    internal string? OperationId { get; private set; }
    public int DragEnter(IDataObject data, uint keys, NativeDesktop.Point point, ref uint effect)
    {
        OperationId = Guid.NewGuid().ToString("N");
        var allowed = effect;
        try
        {
            if (diagnostic != null) Trace("ole.enter", new { keys, allowed, point.X, point.Y, Formats = DescribeFormats(data) });
            paths = ReadPaths(data, Trace);
            accepted = paths.Length > 0 && canAccept(paths);
            effect = SelectEffect(keys, effect);
            lastKeys = keys; lastAllowed = allowed; lastEffect = effect;
            Trace("ole.decision", new { Paths = InputDiagnostics.Paths(paths), accepted, keys, allowed, effect,
                Reason = paths.Length == 0 ? "no-file-paths" : !accepted ? "validation-rejected" : effect == 0 ? "no-compatible-effect" : "accepted" });
            dragState(effect != 0, false);
        }
        catch (Exception exception) { TraceError("enter", exception); effect = 0; Reset(); report($"拖入未完成：{exception.Message}"); }
        return 0;
    }

    public int DragOver(uint keys, NativeDesktop.Point point, ref uint effect)
    {
        var allowed = effect;
        try
        {
            effect = SelectEffect(keys, allowed);
            // Explorer calls this repeatedly; record only changes in negotiation.
            if (lastKeys != keys || lastAllowed != allowed || lastEffect != effect)
                Trace("ole.over-changed", new { keys, allowed, effect, point.X, point.Y });
            lastKeys = keys; lastAllowed = allowed; lastEffect = effect;
        }
        catch (Exception exception) { TraceError("over", exception); effect = 0; Reset(); report($"拖入未完成：{exception.Message}"); }
        return 0;
    }

    public int DragLeave() { Trace("ole.leave", new { accepted }); Reset(); return 0; }

    public int Drop(IDataObject data, uint keys, NativeDesktop.Point point, ref uint effect)
    {
        var stage = "read-paths";
        try
        {
            Trace("ole.drop", new { keys, Allowed = effect, point.X, point.Y });
            var finalPaths = ReadPaths(data, Trace);
            var chosen = SelectEffect(keys, effect);
            var reason = chosen == 0 ? "no-compatible-effect"
                : !finalPaths.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase) ? "paths-changed"
                : !canAccept(finalPaths) ? "validation-rejected-at-drop" : null;
            if (reason != null)
            { Trace("ole.drop-rejected", new { Reason = reason, Paths = InputDiagnostics.Paths(finalPaths), chosen }); effect = 0; return 0; }
            // The target performs the verified move itself. Explicitly tell the
            // Shell that it must not delete the source a second time.
            stage = "set-performed-effect";
            SetEffect(data, "Performed DropEffect", 0);
            stage = "receive";
            var success = receiveMapped != null ? receiveMapped(finalPaths, chosen) : receive(finalPaths);
            // Mapped operations complete asynchronously. Returning COPY leaves the source intact for our worker.
            effect = success ? (receiveMapped != null ? 1u : chosen) : 0u;
            Trace("ole.receive-returned", new { Queued = success, chosen, effect, Mapped = receiveMapped != null });
            stage = "set-logical-effect";
            SetEffect(data, "Logical Performed DropEffect", effect);
            Trace("ole.receipt", new { effect });
        }
        catch (Exception exception) { TraceError(stage, exception); effect = 0; report($"拖入未完成：{exception.Message}"); }
        finally { Reset(keepHover: true); }
        return 0;
    }

    private uint SelectEffect(uint keys, uint allowed) => !accepted ? 0 : mappedEffect != null
        ? mappedEffect(paths, keys, allowed)
        : allowed & 2;

    private void Reset(bool keepHover = false)
    {
        paths = []; accepted = false;
        try { dragState(false, keepHover); } catch (Exception exception) { TraceError("reset", exception); report(exception.Message); }
        OperationId = null;
    }

    private void Trace(string stage, object detail)
    {
        try { diagnostic?.Invoke(stage, new { OperationId, Detail = detail }); } catch { }
    }
    private void TraceError(string stage, Exception error) => Trace("ole.error", new
        { At = stage, HResult = $"0x{error.HResult:X8}", Error = error.ToString() });

    internal static string[] ReadPaths(IDataObject data, Action<string, object>? diagnostic = null)
    {
        var format = new FORMATETC { cfFormat = 15, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
        var query = data.QueryGetData(ref format);
        diagnostic?.Invoke("ole.file-drop-query", new { Format = "CF_HDROP", HResult = $"0x{query:X8}" });
        if (query < 0) return [];
        data.GetData(ref format, out var medium);
        try
        {
            if (medium.tymed != TYMED.TYMED_HGLOBAL || medium.unionmember == 0)
            { diagnostic?.Invoke("ole.paths-rejected", new { Reason = "invalid-storage-medium", Type = medium.tymed.ToString() }); return []; }
            var count = DragQueryFile(medium.unionmember, uint.MaxValue, null, 0);
            diagnostic?.Invoke("ole.file-count", new { count });
            if (count == 0 || count > 4096)
            { diagnostic?.Invoke("ole.paths-rejected", new { Reason = "invalid-file-count", count }); return []; }
            var result = new List<string>();
            for (uint index = 0; index < count; index++)
            {
                var length = DragQueryFile(medium.unionmember, index, null, 0);
                if (length == 0 || length >= 32768)
                { diagnostic?.Invoke("ole.paths-rejected", new { Reason = "invalid-path-length", index, length }); return []; }
                var path = new System.Text.StringBuilder((int)length + 1);
                DragQueryFile(medium.unionmember, index, path, (uint)path.Capacity);
                result.Add(path.ToString());
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally { ReleaseStgMedium(ref medium); }
    }

    private static object DescribeFormats(IDataObject data)
    {
        var formats = new List<object>();
        try
        {
            var enumerator = data.EnumFormatEtc(DATADIR.DATADIR_GET);
            var item = new FORMATETC[1];
            var fetched = new int[1];
            while (formats.Count < 32 && enumerator.Next(1, item, fetched) == 0)
            {
                var name = new System.Text.StringBuilder(256);
                var id = unchecked((ushort)item[0].cfFormat);
                GetClipboardFormatName(id, name, name.Capacity);
                formats.Add(new { Id = id, Name = id == 15 ? "CF_HDROP" : name.ToString(), Medium = item[0].tymed.ToString() });
                if (item[0].ptd != 0) Marshal.FreeCoTaskMem(item[0].ptd);
            }
            return formats;
        }
        catch (Exception error) { return new { Formats = formats, Error = error.ToString() }; }
    }

    private static void SetEffect(IDataObject data, string name, uint effect)
    {
        var format = new FORMATETC { cfFormat = unchecked((short)RegisterClipboardFormat(name)), dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
        var handle = GlobalAlloc(0x42, 4);
        if (handle == 0) throw new OutOfMemoryException();
        var medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = handle };
        var transferred = false;
        try
        {
            var pointer = GlobalLock(handle);
            if (pointer == 0) throw new OutOfMemoryException();
            try { Marshal.WriteInt32(pointer, unchecked((int)effect)); }
            finally { GlobalUnlock(handle); }
            data.SetData(ref format, ref medium, true);
            transferred = true;
        }
        finally { if (!transferred) ReleaseStgMedium(ref medium); }
    }

    [DllImport("shell32.dll", EntryPoint = "DragQueryFileW", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(nint drop, uint index, System.Text.StringBuilder? path, uint length);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string format);
    [DllImport("user32.dll", EntryPoint = "GetClipboardFormatNameW", CharSet = CharSet.Unicode)] private static extern int GetClipboardFormatName(uint format, System.Text.StringBuilder name, int length);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint handle);
}
