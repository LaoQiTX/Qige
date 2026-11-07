using System.IO;

namespace DesktopGroups;

internal static class DemoContent
{
    public static ShortcutStore Create(AppStorage storage)
    {
        var relative = $"samples/{Guid.NewGuid():N}";
        var desktop = storage.Resolve(relative + "/Desktop");
        Directory.CreateDirectory(desktop);
        CreateLink(Path.Combine(desktop, "项目文件夹.lnk"), storage.Root);
        CreateLink(Path.Combine(desktop, "使用说明.lnk"), Path.Combine(storage.InstallDirectory, "使用说明.md"));
        CreateLink(Path.Combine(desktop, "计算器.lnk"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "calc.exe"));
        var store = new ShortcutStore(storage, desktop, relative + "/profile.json", relative + "/Storage");
        foreach (var path in Directory.GetFiles(desktop)) store.Add(path);
        return store;
    }

    private static void CreateLink(string path, string target, string arguments = "")
        => NativeShortcut.Write(path, target, arguments);
}
