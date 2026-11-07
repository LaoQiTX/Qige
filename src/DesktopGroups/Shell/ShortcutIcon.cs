using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace DesktopGroups;

internal sealed record IconResolution(BitmapSource? Bitmap, string Source);

internal static class ShortcutIcon
{
    public static IconResolution Load(string path, string? originalPath = null)
    {
        string? target = null, working = null, iconPath = null;
        var iconIndex = 0;
        try
        {
            if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var shortcut = NativeShortcut.Read(path);
                target = shortcut.Target;
                working = shortcut.WorkingDirectory;
                iconPath = shortcut.IconPath;
                iconIndex = shortcut.IconIndex;
            }
            else if (Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                var lines = File.ReadLines(path).Select(line => line.Trim()).ToArray();
                iconPath = lines.FirstOrDefault(line => line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))?[9..];
                var value = lines.FirstOrDefault(line => line.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase))?[10..];
                if (value != null) int.TryParse(value, out iconIndex);
            }
            if (!string.IsNullOrWhiteSpace(iconPath))
                foreach (var candidate in Candidates(iconPath, originalPath ?? path, working))
                    if (File.Exists(candidate) && Extract(candidate, iconIndex) is { } custom)
                        return new(custom, "ExplicitIcon");
            if (!string.IsNullOrWhiteSpace(target))
                foreach (var candidate in Candidates(target, originalPath ?? path, working))
                    if (File.Exists(candidate) && Extract(candidate, 0) is { } executable)
                        return new(executable, "TargetResource");
        }
        catch (Exception exception) when (exception is IOException or COMException or ArgumentException or System.UnauthorizedAccessException)
        { /* Broken optional icon metadata must not prevent the item from displaying. */ }
        return new(LoadShell(path), "ShellFallback");
    }

    private static IEnumerable<string> Candidates(string value, string originalPath, string? working)
    {
        value = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        if (Path.IsPathFullyQualified(value)) { yield return value; yield break; }
        if (!string.IsNullOrWhiteSpace(working))
        {
            working = Environment.ExpandEnvironmentVariables(working.Trim().Trim('"'));
            if (Path.IsPathFullyQualified(working)) yield return Path.GetFullPath(Path.Combine(working, value));
        }
        yield return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(originalPath)!, value));
    }

    private static BitmapSource? Extract(string path, int index)
    {
        var icons = new nint[1];
        var ids = new uint[1];
        try
        {
            var count = PrivateExtractIcons(path, index, 48, 48, icons, ids, 1, 0);
            if (count == 0 || count == uint.MaxValue || icons[0] == 0) return null;
            return FromHandle(icons[0]);
        }
        finally { if (icons[0] != 0) DestroyIcon(icons[0]); }
    }

    private static BitmapSource? LoadShell(string path)
    {
        if (SHGetFileInfo(path, 0, out var information, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x4100) == 0 || information.Icon == 0) return null;
        try
        {
            var imageListId = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
            if (SHGetImageList(2, in imageListId, out var imageList) >= 0 && imageList != 0)
            {
                try
                {
                    var larger = ImageList_GetIcon(imageList, information.Index, 1);
                    if (larger != 0) { DestroyIcon(information.Icon); information.Icon = larger; }
                }
                finally { Marshal.Release(imageList); }
            }
            return FromHandle(information.Icon);
        }
        finally { DestroyIcon(information.Icon); }
    }

    private static BitmapSource FromHandle(nint icon)
    {
        var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        bitmap.Freeze();
        return bitmap;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint Icon; public int Index; public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)] private static extern nint SHGetFileInfo(string path, uint attributes, out ShellFileInfo information, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll", EntryPoint = "PrivateExtractIconsW", CharSet = CharSet.Unicode)] private static extern uint PrivateExtractIcons(string file, int index, int width, int height, [Out] nint[] icons, [Out] uint[] ids, uint count, uint flags);
    [DllImport("shell32.dll")] private static extern int SHGetImageList(int size, in Guid interfaceId, out nint imageList);
    [DllImport("comctl32.dll")] private static extern nint ImageList_GetIcon(nint imageList, int index, uint flags);
}
