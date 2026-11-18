using System.IO;
using System.Runtime.InteropServices;

namespace DesktopGroups;

internal static class RecycleBin
{
    private static readonly Guid RecycleBinFolder = new("B7534046-3ECB-4C18-BE4E-64CD4CB7D6AC");
    private static readonly Guid ShellItemId = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid TransferHandler = new("D5E346A1-F753-4932-B403-4574800E2498");
    private static readonly Guid TransferSourceId = new("00ADB003-BDE9-45C6-8E29-D09F9353E108");

    internal static void AssertAvailable(string path)
    {
        DrawerContentSource.AssertUsable(path);
        var root = Path.GetPathRoot(path)!;
        var parentPath = Path.GetDirectoryName(path);
        if (parentPath == null || new DriveInfo(root).DriveType != DriveType.Fixed)
            throw new IOException("此位置不支持安全回收，项目已保留。");
        var capacity = new RecycleBinInfo { Size = Marshal.SizeOf<RecycleBinInfo>() };
        if (SHQueryRecycleBin(root, ref capacity) < 0) throw new IOException("此卷的回收站不可用，项目已保留。");

    }

    public static void Send(string path)
    {
        AssertAvailable(path);
        var parentPath = Path.GetDirectoryName(path)!;
        IShellItem? item = null, parent = null, recycle = null, recycled = null;
        ITransferSource? source = null;
        try
        {
            Check(SHCreateItemFromParsingName(path, 0, in ShellItemId, out item));
            Check(SHCreateItemFromParsingName(parentPath, 0, in ShellItemId, out parent));
            Check(SHGetKnownFolderItem(in RecycleBinFolder, 0, 0, in ShellItemId, out recycle));
            Check(parent.BindToHandler(0, in TransferHandler, in TransferSourceId, out source));
            // This method has no permanent-delete fallback. Do not call RemoveItem if recycling fails.
            var result = source.RecycleItem(item, recycle, 0, out recycled);
            if ((uint)result == 0x80270038) throw new IOException("该项目的路径超过 Windows 回收站限制，无法回收。");
            Check(result);
            if (recycled == null || File.Exists(path) || Directory.Exists(path))
                throw new IOException("Windows 未确认项目进入回收站。");
        }
        finally
        {
            if (recycled != null) Marshal.ReleaseComObject(recycled);
            if (source != null) Marshal.ReleaseComObject(source);
            if (recycle != null) Marshal.ReleaseComObject(recycle);
            if (parent != null) Marshal.ReleaseComObject(parent);
            if (item != null) Marshal.ReleaseComObject(item);
        }
    }

    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);

    [StructLayout(LayoutKind.Sequential)]
    private struct RecycleBinInfo { public int Size; public long Bytes, Items; }

    [DllImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string root, ref RecycleBinInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, nint context, in Guid id, out IShellItem item);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderItem(in Guid folder, uint flags, nint token, in Guid id, out IShellItem item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(nint context, in Guid handler, in Guid id, out ITransferSource source);
    }

    [ComImport, Guid("00ADB003-BDE9-45C6-8E29-D09F9353E108"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITransferSource
    {
        [PreserveSig] int Advise(nint sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetProperties(nint properties);
        [PreserveSig] int OpenItem(IShellItem item, uint flags, in Guid id, out nint result);
        [PreserveSig] int MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IShellItem result);
        [PreserveSig] int RecycleItem(IShellItem item, IShellItem destination, uint flags, out IShellItem result);
    }
}
