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
            if (args.Contains("--input-test")) return InputDiagnosticsChecks.Run(storage);
            if (args.Contains("--input-exit-test")) return InputDiagnosticsChecks.Run(storage, checkExit: true);
            var crossVolume = Array.IndexOf(args, "--cross-volume-test");
            if (crossVolume >= 0 && crossVolume + 1 < args.Length) return BasicContentChecks.CrossVolume(storage, args[crossVolume + 1]);
            if (args.Contains("--recycle-test"))
            {
                var test = storage.Resolve("qige-recycle-test.txt");
                File.WriteAllText(test, "isolated recycle test");
                RecycleBin.Send(test);
                Console.WriteLine("PASS Windows recycled generated file");
                return 0;
            }
            if (args.Contains("--basic-content-test")) return BasicContentChecks.Run(storage);
            if (args.Contains("--backup-test")) return BackupChecks.Run(storage);
            if (args.Contains("--p0-test")) return P0Checks.Run(storage);
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
