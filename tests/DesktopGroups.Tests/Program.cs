using System.IO;

namespace DesktopGroups;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            // Each test invocation is isolated from real memberships and previous results.
            var storage = AppStorage.FromArguments(args);
            storage = new AppStorage(Path.Combine(storage.Root, "runs", Guid.NewGuid().ToString("N")));
            storage.EnsureWritable();
            storage.ConfigureTemporaryDirectory();
            if (args.Contains("--layout-test")) return DrawerLayoutChecks.Run(storage);
            if (args.Contains("--manager-test")) return ManagerChecks.Run(storage);
            if (args.Contains("--storage-test")) return ShortcutStorageChecks.Run(storage);
            if (args.Contains("--storage-render-test")) return StoragePanelCheck.Run(storage);
            if (args.Contains("--resident-test")) return ResidentChecks.Run(storage);
            if (args.Contains("--icon-test")) return ShortcutIconCheck.Run(storage);
            return StorageSafetyChecks.Run(storage);
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
}
