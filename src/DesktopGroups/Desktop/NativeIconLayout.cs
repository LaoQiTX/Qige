using System.Runtime.InteropServices;

namespace DesktopGroups;

internal sealed record IconPlacement(string Identity, int X, int Y);

internal static class NativeIconLayout
{
    [DllImport("shell32.dll")] private static extern nint ILCombine(nint parent, nint child);
    [DllImport("shell32.dll")] private static extern int SHGetNameFromIDList(nint idList, uint nameType, out nint text);

    public static IReadOnlyList<IconPlacement> ReadSelection() => ShellDesktop.WithView(view =>
    {
        var items = Enumerate(view, selectionOnly: true);
        try { return (IReadOnlyList<IconPlacement>)items.Select(item => item.Placement).ToArray(); }
        finally { Free(items); }
    });

    public static int Position(IReadOnlyList<IconPlacement> placements) => ShellDesktop.WithView(view =>
    {
        if (placements.Count == 0) return 0;
        var autoArrange = view.GetAutoArrange();
        if (autoArrange != 1)
            throw new InvalidOperationException("当前桌面未确认关闭自动排列。测试不会替你修改系统排列设置。");
        var items = Enumerate(view, selectionOnly: false);
        try
        {
            var desired = placements.ToDictionary(item => item.Identity, StringComparer.OrdinalIgnoreCase);
            var matches = items.Where(item => desired.ContainsKey(item.Placement.Identity)).ToArray();
            if (matches.Length == 0) return 0;
            var pointers = matches.Select(item => item.Pointer).ToArray();
            var points = matches.Select(item =>
            {
                var target = desired[item.Placement.Identity];
                return new NativeDesktop.Point { X = target.X, Y = target.Y };
            }).ToArray();
            Marshal.ThrowExceptionForHR(view.SelectAndPositionItems((uint)matches.Length, pointers, points, 0x80));
            foreach (var item in matches)
            {
                Marshal.ThrowExceptionForHR(view.GetItemPosition(item.Pointer, out var actual));
                var target = desired[item.Placement.Identity];
                if (Math.Abs(actual.X - target.X) > 2 || Math.Abs(actual.Y - target.Y) > 2)
                    throw new InvalidOperationException("系统未接受精确位置，可能存在吸附或其他整理工具干预；请使用恢复布局。");
            }
            return matches.Length;
        }
        finally { Free(items); }
    });

    public static int CountReadableItems() => ShellDesktop.WithView(view =>
    {
        var items = Enumerate(view, selectionOnly: false);
        try { return items.Count; }
        finally { Free(items); }
    });

    private static List<ShellItem> Enumerate(ShellDesktop.IFolderViewNative view, bool selectionOnly)
    {
        var items = new List<ShellItem>();
        var persistId = typeof(IPersistFolder2Native).GUID;
        object? folder = null;
        nint parent = 0, enumeratorPointer = 0;
        object? enumeration = null;
        try
        {
            Marshal.ThrowExceptionForHR(view.GetFolder(ref persistId, out folder));
            Marshal.ThrowExceptionForHR(((IPersistFolder2Native)folder).GetCurFolder(out parent));
            var enumId = typeof(IEnumIdListNative).GUID;
            var result = view.Items(selectionOnly ? 1u : 2u, ref enumId, out enumeratorPointer);
            if (result == 1 || enumeratorPointer == 0) return items;
            Marshal.ThrowExceptionForHR(result);
            enumeration = Marshal.GetObjectForIUnknown(enumeratorPointer);
            var iterator = (IEnumIdListNative)enumeration;
            while (true)
            {
                var next = iterator.Next(1, out var child, out var fetched);
                Marshal.ThrowExceptionForHR(next);
                if (fetched == 0) break;
                try
                {
                    var absolute = ILCombine(parent, child);
                    if (absolute == 0) throw new OutOfMemoryException();
                    string identity;
                    try
                    {
                        Marshal.ThrowExceptionForHR(SHGetNameFromIDList(absolute, 0x80028000, out var text));
                        try { identity = Marshal.PtrToStringUni(text) ?? throw new InvalidOperationException("无法读取图标身份。"); }
                        finally { Marshal.FreeCoTaskMem(text); }
                    }
                    finally { Marshal.FreeCoTaskMem(absolute); }
                    Marshal.ThrowExceptionForHR(view.GetItemPosition(child, out var point));
                    items.Add(new(child, new(identity, point.X, point.Y)));
                    child = 0;
                }
                finally { if (child != 0) Marshal.FreeCoTaskMem(child); }
            }
            return items;
        }
        catch { Free(items); throw; }
        finally
        {
            if (enumeration != null) Marshal.ReleaseComObject(enumeration);
            if (enumeratorPointer != 0) Marshal.Release(enumeratorPointer);
            if (parent != 0) Marshal.FreeCoTaskMem(parent);
            if (folder != null) Marshal.ReleaseComObject(folder);
        }
    }

    private static void Free(IEnumerable<ShellItem> items)
    {
        foreach (var item in items) Marshal.FreeCoTaskMem(item.Pointer);
    }

    private sealed record ShellItem(nint Pointer, IconPlacement Placement);

    [ComImport, Guid("000214F2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumIdListNative
    {
        [PreserveSig] int Next(uint count, out nint item, out uint fetched);
        [PreserveSig] int Skip(uint count);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out nint clone);
    }

    [ComImport, Guid("1AC3D9F0-175C-11D1-95BE-00609797EA4F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFolder2Native
    {
        [PreserveSig] int GetClassID(out Guid id);
        [PreserveSig] int Initialize(nint idList);
        [PreserveSig] int GetCurFolder(out nint idList);
    }
}
