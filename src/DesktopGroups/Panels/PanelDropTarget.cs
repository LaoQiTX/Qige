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
    Func<IReadOnlyList<string>, bool> receive, Action<bool, bool> dragState, Action<string> report) : IPanelDropTarget
{
    private string[] paths = [];
    private bool accepted;
    public int DragEnter(IDataObject data, uint keys, NativeDesktop.Point point, ref uint effect)
    {
        try
        {
            paths = ReadPaths(data);
            accepted = (effect & 2) != 0 && paths.Length > 0 && canAccept(paths);
            effect = accepted ? 2u : 0u;
            dragState(accepted, false);
        }
        catch (Exception exception) { effect = 0; Reset(); report($"拖入未完成：{exception.Message}"); }
        return 0;
    }

    public int DragOver(uint keys, NativeDesktop.Point point, ref uint effect)
    {
        effect = accepted && (effect & 2) != 0 ? 2u : 0u;
        return 0;
    }

    public int DragLeave() { Reset(); return 0; }

    public int Drop(IDataObject data, uint keys, NativeDesktop.Point point, ref uint effect)
    {
        try
        {
            var finalPaths = ReadPaths(data);
            if (!accepted || (effect & 2) == 0 || !finalPaths.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase) || !canAccept(finalPaths))
            { effect = 0; return 0; }
            // The target performs the verified move itself. Explicitly tell the
            // Shell that it must not delete the source a second time.
            SetEffect(data, "Performed DropEffect", 0);
            var success = receive(finalPaths);
            effect = success ? 2u : 0u;
            SetEffect(data, "Logical Performed DropEffect", effect);
        }
        catch (Exception exception) { effect = 0; report($"拖入未完成：{exception.Message}"); }
        finally { Reset(keepHover: true); }
        return 0;
    }

    private void Reset(bool keepHover = false)
    {
        paths = []; accepted = false;
        try { dragState(false, keepHover); } catch (Exception exception) { report(exception.Message); }
    }

    internal static string[] ReadPaths(IDataObject data)
    {
        var format = new FORMATETC { cfFormat = 15, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
        if (data.QueryGetData(ref format) < 0) return [];
        data.GetData(ref format, out var medium);
        try
        {
            if (medium.tymed != TYMED.TYMED_HGLOBAL || medium.unionmember == 0) return [];
            var count = DragQueryFile(medium.unionmember, uint.MaxValue, null, 0);
            if (count == 0 || count > 12) return [];
            var result = new List<string>();
            for (uint index = 0; index < count; index++)
            {
                var length = DragQueryFile(medium.unionmember, index, null, 0);
                if (length == 0 || length >= 32768) return [];
                var path = new System.Text.StringBuilder((int)length + 1);
                DragQueryFile(medium.unionmember, index, path, (uint)path.Capacity);
                result.Add(path.ToString());
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally { ReleaseStgMedium(ref medium); }
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
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint handle);
}
