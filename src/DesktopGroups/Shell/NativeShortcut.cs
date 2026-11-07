using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace DesktopGroups;

internal sealed record ShortcutMetadata(string Target, string Arguments, string WorkingDirectory, string IconPath, int IconIndex);

// The Unicode Shell interface avoids WScript's system-codepage-dependent filenames.
internal static class NativeShortcut
{
    public static ShortcutMetadata Read(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("快捷方式未找到。", path);
        var instance = Create();
        try
        {
            ((IPersistFile)instance).Load(Path.GetFullPath(path), 0);
            var link = (IShellLinkW)instance;
            var target = new StringBuilder(32768); link.GetPath(target, target.Capacity, 0, 4);
            var arguments = new StringBuilder(32768); link.GetArguments(arguments, arguments.Capacity);
            var working = new StringBuilder(32768); link.GetWorkingDirectory(working, working.Capacity);
            var icon = new StringBuilder(32768); link.GetIconLocation(icon, icon.Capacity, out var iconIndex);
            return new(target.ToString(), arguments.ToString(), working.ToString(), icon.ToString(), iconIndex);
        }
        finally { Marshal.ReleaseComObject(instance); }
    }

    public static void Write(string path, string target, string arguments = "", string working = "", string? iconPath = null, int iconIndex = 0)
    {
        var instance = Create();
        try
        {
            var link = (IShellLinkW)instance;
            link.SetPath(target); link.SetArguments(arguments); link.SetWorkingDirectory(working);
            if (iconPath != null) link.SetIconLocation(iconPath, iconIndex);
            ((IPersistFile)instance).Save(Path.GetFullPath(path), true);
        }
        finally { Marshal.ReleaseComObject(instance); }
    }

    private static object Create() => Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), throwOnError: true)!)!;

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, nint findData, uint flags);
        void GetIDList(out nint list);
        void SetIDList(nint list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int capacity);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int capacity);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int capacity);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetHotkey(out short value);
        void SetHotkey(short value);
        void GetShowCmd(out int value);
        void SetShowCmd(int value);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int capacity, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string value, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string value, uint reserved);
        void Resolve(nint window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string value);
    }
}
