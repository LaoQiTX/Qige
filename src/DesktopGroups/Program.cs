using System.IO;
using System.Text;
using System.Windows;

namespace DesktopGroups;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var storage = AppStorage.FromArguments(args);
            storage.EnsureWritable();
            using var sessionLock = AcquireSession(storage, args);
            if (sessionLock == null) return 0;
            LegacyDataMigration.Run(storage);
            storage.ConfigureTemporaryDirectory();
            var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            application.DispatcherUnhandledException += (_, eventArgs) =>
            {
                MessageBox.Show(eventArgs.Exception.Message, "栖格 Qige · 错误", MessageBoxButton.OK, MessageBoxImage.Error);
                eventArgs.Handled = true;
            };
            var window = new StorageWindow(storage, args.Contains("--demo"), args.Contains("--background"));
            using var relay = args.Contains("--demo") ? null
                : new InstanceRelay(storage.Root, () => application.Dispatcher.BeginInvoke(window.ShowControl));
            return application.Run(window);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
            MessageBox.Show(exception.Message, "栖格 Qige · 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    private static FileStream? AcquireSession(AppStorage storage, string[] args)
    {
        Directory.CreateDirectory(storage.Resolve("recovery"));
        // Demo and real sessions have separate locks but share the installation's data root.
        var path = storage.Resolve(args.Contains("--demo") ? "recovery/demo-session.lock" : "recovery/session.lock");
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException)
        {
            if (!args.Contains("--demo") && InstanceRelay.ShowExistingAsync(storage.Root).GetAwaiter().GetResult()) return null;
            throw new IOException("栖格已在运行或数据目录被占用，请从托盘打开管理面板。");
        }
    }
}
