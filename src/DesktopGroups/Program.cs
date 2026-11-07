using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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
            var storage = new ProjectStorage(ProjectStorage.LocateRoot(args));
            var runtimeTemp = storage.Resolve(".build/app-temp");
            Directory.CreateDirectory(runtimeTemp);
            Environment.SetEnvironmentVariable("TEMP", runtimeTemp);
            Environment.SetEnvironmentVariable("TMP", runtimeTemp);
            if (args.Contains("--self-test")) return SelfTest(storage);
            if (args.Contains("--render-test")) return NativeRenderCheck.Run(storage);
            if (args.Contains("--visibility-probe")) return DesktopVisibilityProbe.Run(storage);
            if (args.Contains("--storage-test")) return ShortcutStorageChecks.Run(storage);
            if (args.Contains("--storage-render-test")) return StoragePanelCheck.Run(storage);
            if (args.Contains("--icon-test")) return ShortcutIconCheck.Run(storage);
            if (args.Contains("--resident-test")) return ResidentChecks.Run(storage);
            if (args.Contains("--shell-test"))
            {
                var count = NativeIconLayout.CountReadableItems();
                storage.WriteJson("TestData/ShellRead/results.json", new { ReadableItems = count, Repositioned = false });
                Console.WriteLine($"PASS read-only Shell PIDL identities and positions ({count} items; none repositioned)");
                return 0;
            }
            if (args.Contains("--inspect"))
            {
                var report = DesktopProbe.CaptureAsync().GetAwaiter().GetResult();
                var path = storage.WriteJson("Data/Diagnostics/desktop-latest.json", report);
                Console.WriteLine(JsonSerializer.Serialize(report, ProjectStorage.JsonOptions));
                Console.WriteLine($"报告：{path}");
                return 0;
            }
            var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            application.DispatcherUnhandledException += (_, eventArgs) =>
            {
                MessageBox.Show(eventArgs.Exception.Message, "栖格 Qige · 错误", MessageBoxButton.OK, MessageBoxImage.Error);
                eventArgs.Handled = true;
            };
            // A file lock prevents concurrent moves from multiple main instances.
            Directory.CreateDirectory(storage.Resolve("Data"));
            FileStream? sessionLock = null;
            if (!args.Contains("--demo"))
            {
                try { sessionLock = new FileStream(storage.Resolve("Data/shortcut-session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException)
                {
                    if (args.Contains("--background") || InstanceRelay.ShowExistingAsync(storage.Root).GetAwaiter().GetResult()) return 0;
                    MessageBox.Show("程序已运行，但控制窗口暂未响应。请从托盘打开，或稍后重试。", AppBrand.Name);
                    return 1;
                }
            }
            using (sessionLock)
            {
                Window window = args.Contains("--legacy-probe") ? new ProbeWindow(storage) : new StorageWindow(storage, args.Contains("--demo"), args.Contains("--background"));
                using var relay = window is StorageWindow control && !args.Contains("--demo")
                    ? new InstanceRelay(storage.Root, () => application.Dispatcher.BeginInvoke(control.ShowControl)) : null;
                return application.Run(window);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static int SelfTest(ProjectStorage storage)
    {
        var results = new List<string>();
        void Reject(string input)
        {
            try { storage.Resolve(input); }
            catch (ArgumentException) { results.Add($"PASS reject: {input}"); return; }
            throw new InvalidOperationException($"路径越界测试未通过：{input}");
        }
        Reject("../outside.json");
        Reject("Data/../../outside.json");
        Reject("C:\\outside.json");
        var relative = "TestData/StorageSafety/roundtrip.json";
        var path = storage.WriteJson(relative, new { Text = "中文与空格 / 桌面分区", Revision = 1 });
        storage.WriteJson(relative, new { Text = "更新后仍可读取", Revision = 2 });
        using (var document = JsonDocument.Parse(File.ReadAllText(path)))
        {
            if (document.RootElement.GetProperty("Revision").GetInt32() != 2)
                throw new InvalidOperationException("JSON 替换后的内容不正确。");
        }
        results.Add("PASS atomic JSON replacement and Unicode roundtrip");
        if (File.Exists(path + ".tmp")) throw new InvalidOperationException("成功保存后仍有未完成临时文件。");
        results.Add("PASS completed write leaves no temporary file");
        if (Marshal.SizeOf<NativeDesktop.Rect>() != 16 || Marshal.SizeOf<NativeDesktop.Point>() != 8)
            throw new InvalidOperationException("原生坐标结构大小不正确。");
        results.Add("PASS native coordinate structure sizes");
        storage.WriteJson("TestData/StorageSafety/results.json", new { Passed = results.Count, Results = results });
        Console.WriteLine(string.Join(Environment.NewLine, results));
        return 0;
    }
}
