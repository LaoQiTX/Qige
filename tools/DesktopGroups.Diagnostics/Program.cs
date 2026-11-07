using System.IO;
using System.Text.Json;
using System.Windows;

namespace DesktopGroups;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var storage = AppStorage.FromArguments(args);
            storage.EnsureWritable();
            storage.ConfigureTemporaryDirectory();
            if (args.Contains("--migrate-data"))
            {
                Directory.CreateDirectory(storage.Resolve("recovery"));
                using var dataLock = new FileStream(storage.Resolve("recovery/session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                LegacyDataMigration.Run(storage);
                Console.WriteLine("Migration verified: " + storage.Root);
                return 0;
            }
            if (args.Contains("--render-test")) return NativeRenderCheck.Run(storage);
            if (args.Contains("--visibility-probe")) return DesktopVisibilityProbe.Run(storage);
            if (args.Contains("--shell-test"))
            {
                var count = NativeIconLayout.CountReadableItems();
                storage.WriteJson("checks/ShellRead/results.json", new { ReadableItems = count, Repositioned = false });
                Console.WriteLine($"PASS read-only Shell identities ({count}; none repositioned)");
                return 0;
            }
            if (args.Contains("--inspect"))
            {
                var report = DesktopProbe.CaptureAsync().GetAwaiter().GetResult();
                storage.WriteJson("logs/desktop-latest.json", report);
                Console.WriteLine(JsonSerializer.Serialize(report, AppStorage.JsonOptions));
                return 0;
            }
            return new Application().Run(new ProbeWindow(storage));
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
}
