using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.Json;

namespace DesktopGroups;

internal static class InputDiagnosticsChecks
{
    public static int Run(AppStorage storage, bool checkExit = false)
    {
        Marshal.ThrowExceptionForHR(OleInitialize(0));
        try { Check(storage); return checkExit ? CheckExit(storage) : 0; }
        finally { OleUninitialize(); }
    }

    private static int CheckExit(AppStorage storage)
    {
        var application = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        application.Startup += (_, _) => application.Dispatcher.BeginInvoke(new Action(async () =>
        {
            StorageWindow? window = null;
            try
            {
                var exitStorage = new AppStorage(storage.Resolve("input-exit"));
                window = new StorageWindow(exitStorage, demo: true, background: true);
                window.Start();
                await window.WaitForIdleAsync();
                var store = window.Catalog.Stores["default"];
                var path = Path.Combine(store.DesktopPath, "退出时的收纳.url");
                File.WriteAllText(path, "[InternetShortcut]\r\nURL=https://example.com/exit\r\n");
                if (!window.QueueAddPaths("default", [path], "input-exit-test", "exit-operation") || window.BusyOperations != 1)
                    throw new InvalidOperationException("isolated exit collection did not enter the worker queue");
                window.RequestExit();
                await window.WaitForIdleAsync();
                var events = Directory.GetFiles(exitStorage.Root, "input-current.jsonl", SearchOption.AllDirectories)
                    .SelectMany(File.ReadLines).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
                if (!File.Exists(path) || !events.Any(entry => entry.GetProperty("Stage").GetString() == "input.completed"
                    && entry.GetProperty("Detail").GetProperty("OperationId").GetString() == "exit-operation"
                    && entry.GetProperty("Detail").GetProperty("Success").GetBoolean())
                    || events.Last().GetProperty("Stage").GetString() != "session.end")
                    throw new InvalidOperationException("exit lost the last input completion record or failed to restore its source");
                Console.WriteLine("PASS immediate exit flushes the queued collection result and restores the isolated source");
                application.Shutdown(0);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                window?.RequestExit();
                application.Shutdown(1);
            }
        }));
        return application.Run();
    }

    private static int Check(AppStorage storage)
    {
        var results = new List<string>();
        void Assert(bool pass, string label)
        { if (!pass) throw new InvalidOperationException(label); results.Add("PASS " + label); }
        DesktopInputChecks.Check(storage, Assert);
        var desktop = storage.Resolve("input/Desktop");
        Directory.CreateDirectory(desktop);
        var store = new ShortcutStore(storage, desktop, "input/profile.json", "input/Stored");
        var first = Path.Combine(desktop, "拖入.url");
        var second = Path.Combine(desktop, "另一文件.url");
        File.WriteAllText(first, "[InternetShortcut]\r\nURL=https://example.com/first\r\n");
        File.WriteAllText(second, "[InternetShortcut]\r\nURL=https://example.com/second\r\n");
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        Assert(!store.CanAccept([Path.Combine(common, "DeskBox.lnk")], out var reason) && reason.Contains("公共桌面"),
            "managed storage still refuses moving public-desktop originals; the input router adds references instead");
        Assert(!store.CanAccept([], out reason) && reason.Length > 0, "empty file lists have a rejection reason");
        var events = new List<(string Stage, JsonElement Detail)>();
        using (var log = new InputDiagnostics(storage))
        using (var firstData = new StoragePanelCheck.ShellSampleData(first))
        using (var secondData = new StoragePanelCheck.ShellSampleData(second))
        {
            void Trace(string stage, object detail)
            { events.Add((stage, JsonSerializer.SerializeToElement(detail))); log.Write(stage, detail); }
            var errors = new List<string>();
            var received = 0;
            var target = new PanelDropTarget(store.CanAccept, paths =>
            { received++; store.Add(paths[0]); return true; }, (_, _) => { }, errors.Add, diagnostic: Trace);
            uint effect = 1;
            target.DragEnter(firstData.Data, 0, default, ref effect);
            Assert(effect == 0 && events.Any(e => e.Stage == "ole.decision"
                && e.Detail.GetProperty("Detail").GetProperty("Reason").GetString() == "no-compatible-effect"),
                "copy-only drag is rejected with effect-negotiation evidence");
            target.DragLeave();
            events.Clear(); effect = 2;
            target.DragEnter(firstData.Data, 0, default, ref effect);
            var operation = target.OperationId;
            for (var index = 0; index < 500; index++)
            { effect = 2; target.DragOver(0, default, ref effect); }
            Assert(events.All(e => e.Stage != "ole.over-changed"), "unchanged DragOver callbacks do not flood the log");
            effect = 1; target.DragOver(8, default, ref effect);
            Assert(effect == 0 && events.Count(e => e.Stage == "ole.over-changed") == 1,
                "changed modifier keys and allowed effects are logged once");
            effect = 2; target.Drop(secondData.Data, 0, default, ref effect);
            Assert(effect == 0 && received == 0 && File.Exists(first) && File.Exists(second)
                && events.Any(e => e.Stage == "ole.drop-rejected"
                    && e.Detail.GetProperty("Detail").GetProperty("Reason").GetString() == "paths-changed"),
                "changed drop paths are diagnosed and both originals remain intact");
            events.Clear(); effect = 2;
            target.DragEnter(firstData.Data, 0, default, ref effect);
            operation = target.OperationId;
            effect = 2; target.Drop(firstData.Data, 0, default, ref effect);
            Assert(effect == 2 && received == 1 && !File.Exists(first) && store.Active.Count == 1 && errors.Count == 0,
                "logging preserves verified OLE collection and move effect");
            Assert(events.All(e => e.Detail.GetProperty("OperationId").GetString() == operation)
                && events.Any(e => e.Stage == "ole.receipt"), "enter, validation and receipt share one drag operation id");
            Assert(events.Any(e => e.Stage == "ole.enter" && e.Detail.GetProperty("Detail").GetProperty("Formats")
                .EnumerateArray().Any(f => f.GetProperty("Name").GetString() == "CF_HDROP")),
                "the actual Shell file-drop format is captured");
            events.Clear();
            var accept = true;
            target = new PanelDropTarget(_ => accept, _ => { received++; return true; }, (_, _) => { }, errors.Add, diagnostic: Trace);
            effect = 2; target.DragEnter(secondData.Data, 0, default, ref effect); accept = false;
            effect = 2; target.Drop(secondData.Data, 0, default, ref effect);
            Assert(effect == 0 && received == 1 && events.Any(e => e.Stage == "ole.drop-rejected"
                && e.Detail.GetProperty("Detail").GetProperty("Reason").GetString() == "validation-rejected-at-drop"),
                "a drawer becoming unavailable after enter is distinguished from initial rejection");
            events.Clear();
            target = new PanelDropTarget(store.CanAccept, _ => false, (_, _) => { }, errors.Add, diagnostic: Trace);
            effect = 2; target.DragEnter(secondData.Data, 0, default, ref effect);
            effect = 2; target.Drop(secondData.Data, 0, default, ref effect);
            Assert(effect == 0 && File.Exists(second) && events.Any(e => e.Stage == "ole.receive-returned"
                && !e.Detail.GetProperty("Detail").GetProperty("Queued").GetBoolean()),
                "receiver queue refusal is logged separately and leaves source intact");
            events.Clear();
            var readOnlyData = (IDataObject)new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, new[] { second });
            target = new PanelDropTarget(store.CanAccept, _ => { received++; return true; }, (_, _) => { }, errors.Add, diagnostic: Trace);
            effect = 2; target.DragEnter(readOnlyData, 0, default, ref effect);
            effect = 2; target.Drop(readOnlyData, 0, default, ref effect);
            Assert(effect == 0 && received == 1 && File.Exists(second) && events.Any(e => e.Stage == "ole.error"
                && e.Detail.GetProperty("Detail").GetProperty("At").GetString() == "set-performed-effect"),
                "a source refusing the OLE move receipt is identified before collection touches it");
            events.Clear(); errors.Clear();
            target = new PanelDropTarget(store.CanAccept, _ => true, (_, _) => { }, errors.Add,
                (_, keys, allowed) => keys == 8 ? throw new IOException("simulated negotiation error") : allowed, diagnostic: Trace);
            effect = 2; target.DragEnter(secondData.Data, 0, default, ref effect);
            effect = 2; target.DragOver(8, default, ref effect);
            Assert(effect == 0 && errors.Count == 1 && events.Any(e => e.Stage == "ole.error"
                && e.Detail.GetProperty("Detail").GetProperty("Error").GetString()!.Contains("simulated negotiation error")),
                "negotiation exceptions are contained and include a stack trace and HRESULT");
            target = new PanelDropTarget(store.CanAccept, _ => false, (_, _) => { }, errors.Add,
                diagnostic: (_, _) => throw new IOException("log unavailable"));
            effect = 2; target.DragEnter(secondData.Data, 0, default, ref effect);
            Assert(effect == 2, "a failing diagnostic callback never changes drag acceptance");
            target.DragLeave();
            log.Write("large-record", new { Paths = new string('测', 100_000) });
        }
        var lines = File.ReadAllLines(storage.Resolve("logs/input-current.jsonl"));
        var records = lines.Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        Assert(records.Length > 20 && records.All(record => record.GetProperty("Session").GetString()!.Length == 32),
            "background writer drains valid JSON lines with session metadata");
        Assert(lines.All(line => System.Text.Encoding.UTF8.GetByteCount(line) <= 64 * 1024)
            && records.Last().GetProperty("Detail").GetProperty("Truncated").GetBoolean(),
            "large payloads are truncated into valid bounded JSON records");
        var rotating = new AppStorage(storage.Resolve("rotation"));
        Directory.CreateDirectory(rotating.Resolve("logs"));
        File.WriteAllText(rotating.Resolve("logs/input-current.jsonl"), new string(' ', (int)InputDiagnostics.MaximumBytes - 32));
        using (var log = new InputDiagnostics(rotating)) log.Write("rotation-check", new { Result = "preserved" });
        Assert(File.Exists(rotating.Resolve("logs/input-previous.jsonl"))
            && new FileInfo(rotating.Resolve("logs/input-current.jsonl")).Length < InputDiagnostics.MaximumBytes,
            "rotation retains the previous file and limits the current log");
        var blocked = new AppStorage(storage.Resolve("unwritable"));
        blocked.EnsureWritable(); File.WriteAllText(blocked.Resolve("logs"), "directory blocked by a test file");
        using (var log = new InputDiagnostics(blocked)) log.Write("must-not-throw", new { });
        Assert(File.Exists(second), "unwritable log directories are tolerated without touching desktop originals");
        Assert(store.RestoreAll().Count == 0 && File.Exists(first), "all test collection is restored within the isolated desktop");
        storage.WriteJson("checks/InputDiagnostics/results.json", new { Results = results, RealDesktopModified = false });
        Console.WriteLine(string.Join(Environment.NewLine, results));
        return 0;
    }

    [DllImport("ole32.dll")] private static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
}
