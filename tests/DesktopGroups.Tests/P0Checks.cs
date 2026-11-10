using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopGroups;

internal static class P0Checks
{
    public static int Run(AppStorage storage) => CheckAsync(storage).GetAwaiter().GetResult();

    private static async Task<int> CheckAsync(AppStorage storage)
    {
        var results = new List<string>();
        void Check(bool pass, string text)
        { if (!pass) throw new InvalidOperationException(text); results.Add("PASS " + text); }
        var now = DateTimeOffset.UtcNow;
        var policy = new DesktopRecoveryPolicy();
        policy.Signal("显示器变化", now);
        Check(!policy.IsDue(now.AddMilliseconds(299)) && policy.IsDue(now.AddMilliseconds(300)), "environment events coalesce for 300ms");
        policy.Signal("解锁", now.AddMilliseconds(200));
        Check(!policy.IsDue(now.AddMilliseconds(499)) && policy.IsDue(now.AddMilliseconds(500)), "new event postpones the recovery boundary");
        foreach (var seconds in new[] { 1, 2, 4, 8, 16, 30, 30 })
        {
            policy.Complete(false, now);
            Check(!policy.IsDue(now.AddSeconds(seconds).AddMilliseconds(-1)) && policy.IsDue(now.AddSeconds(seconds)), "failed recovery retries after " + seconds + " seconds");
        }
        policy.Complete(true, now);
        Check(!policy.Pending && !policy.IsDue(now.AddDays(1)), "successful recovery stops retries");

        policy.Signal("连续显示通知", now);
        for (var i = 1; i <= 20; i++) policy.Signal("设置通知 " + i, now.AddMilliseconds(i * 100));
        Check(policy.IsDue(now.AddSeconds(2)), "continuous environment notifications cannot postpone recovery beyond two seconds");
        policy.Complete(false, now.AddSeconds(2));
        policy.Signal("重复的显示通知", now.AddMilliseconds(2100));
        Check(!policy.IsDue(now.AddMilliseconds(2999)) && policy.IsDue(now.AddSeconds(3)),
            "notifications during a failed recovery preserve its retry backoff");
        policy.Complete(false, now.AddSeconds(3));
        policy.Signal("唤醒期间的重复通知", now.AddMilliseconds(3100));
        Check(!policy.IsDue(now.AddMilliseconds(4999)) && policy.IsDue(now.AddSeconds(5)),
            "repeated notifications do not reset exponential recovery backoff");
        policy.Complete(true, now.AddSeconds(5));
        policy.Signal("下一次独立变化", now.AddSeconds(10));
        Check(!policy.IsDue(now.AddMilliseconds(10299)) && policy.IsDue(now.AddMilliseconds(10300)),
            "successful recovery starts a fresh debounce window for a later event");

        var caller = Environment.CurrentManagedThreadId;
        var loaderThread = caller;
        var apartment = ApartmentState.Unknown;
        var calls = 0;
        using var release = new ManualResetEventSlim();
        using var icons = new AsyncIconCache((path, _) =>
        {
            Interlocked.Increment(ref calls); loaderThread = Environment.CurrentManagedThreadId;
            apartment = Thread.CurrentThread.GetApartmentState();
            if (path == "blocked") release.Wait(TimeSpan.FromSeconds(5));
            if (path == "bad") throw new IOException("unreadable icon");
            return BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 10, 20, 30, 255 }, 4);
        });
        var first = icons.LoadAsync("one", "blocked", null);
        var duplicate = icons.LoadAsync("one", "blocked", null);
        Check(!first.IsCompleted && ReferenceEquals(first, duplicate), "icon parsing is nonblocking and concurrent requests share one task");
        release.Set();
        var bitmap = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Check(calls == 1 && loaderThread != caller && apartment == ApartmentState.STA && bitmap?.IsFrozen == true, "Shell icons load once on a background STA and can cross threads");
        await icons.LoadAsync("one", "unused", null);
        Check(calls == 1 && icons.Hits == 2, "completed icons reuse the cache");
        await icons.LoadAsync("bad", "bad", null);
        Check(icons.TryGet("bad", out var missing) && missing == null, "unreadable optional icons do not fault the UI pipeline");
        await Task.WhenAll(Enumerable.Range(0, 300).Select(i => icons.LoadAsync("key" + i, "ok", null)));
        Check(icons.Count == 256 && icons.PendingCount == 0 && !icons.TryGet("one", out _), "icon cache remains bounded after 300 unique loads");

        using (var queue = new StaWorkQueue("isolated file test"))
        {
            var order = new List<int>();
            var tasks = Enumerable.Range(0, 12).Select(i => queue.Run(() => { order.Add(i); return i; })).ToArray();
            await Task.WhenAll(tasks);
            Check(order.SequenceEqual(Enumerable.Range(0, 12)), "file work remains serialized in submission order");
            var failed = false;
            try { await queue.Run<int>(() => throw new IOException("test")); } catch (IOException) { failed = true; }
            Check(failed && await queue.Run(() => true), "a failed job does not stop the following file operation");
        }

        var desktop = storage.Resolve("p0/Desktop"); Directory.CreateDirectory(desktop);
        var original = Path.Combine(desktop, "恢复入口.url");
        File.WriteAllText(original, "[InternetShortcut]\r\nURL=https://example.com/original\r\n");
        var store = new ShortcutStore(storage, desktop, "p0/profile.json", "p0/Storage");
        // Shortcut reads and verified transfers run on an STA in the production path as well.
        using var files = new StaWorkQueue("isolated recovery");
        await files.Run(() => { store.Add(original); return true; });
        var entry = store.Active.Single(); var stored = store.StoredPath(entry);
        var hash = VerifiedTransfer.Hash(stored);
        var detached = store.Detached();
        store.Profile.X = -1600; store.Profile.Y = 230; store.Profile.Width = 550;
        store.Profile.Folded = true; store.Profile.Locked = true;
        store.Publish(detached.Profile);
        var reloaded = store.Detached();
        Check(reloaded.Profile.X == -1600 && reloaded.Profile.Y == 230 && reloaded.Profile.Width == 550 && reloaded.Profile.Folded && reloaded.Profile.Locked,
            "publishing worker results preserves geometry, fold and lock edits made by the UI");
        var collision = "[InternetShortcut]\r\nURL=https://example.com/different\r\n";
        File.WriteAllText(original, collision);
        var problems = await files.Run(() => RecoveryInspection.Inspect("default", "常用", store.Detached()));
        Check(problems.Count == 1 && problems[0].OriginalPath == original && problems[0].StoredPath == stored,
            "recovery inspection exposes both full paths for a conflicting shortcut");
        Check(File.ReadAllText(original) == collision && VerifiedTransfer.Hash(stored) == hash, "inspection leaves conflicting files untouched");
        var issues = await files.Run(() => store.RestoreAll());
        Check(issues.Count == 1 && File.ReadAllText(original) == collision && VerifiedTransfer.Hash(stored) == hash, "recovery retry refuses to overwrite a different desktop file");
        var export = storage.Resolve("p0/export/copy.url");
        File.WriteAllText(stored + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        await files.Run(() => { VerifiedTransfer.Copy(stored, export); return true; });
        Check(VerifiedTransfer.Hash(export) == hash && File.Exists(stored) && File.ReadAllText(export + ":Zone.Identifier").Contains("ZoneId=3"),
            "export verifies the copy, preserves download-origin streams and retains the source");
        File.Delete(original);
        await files.Run(() => { store.RetryEntry(entry.Id, restore: true); return true; });
        Check(File.Exists(original) && !File.Exists(stored) && VerifiedTransfer.Hash(original) == hash,
            "retry restores the original bytes after the collision is resolved");
        Check((await files.Run(() => RecoveryInspection.Inspect("default", "常用", store.Detached()))).Count == 0,
            "successfully restored files disappear from recovery inspection");
        File.Delete(original);
        problems = await files.Run(() => RecoveryInspection.Inspect("default", "常用", store.Detached()));
        Check(problems.Count == 1 && problems[0].Message.Contains("均未找到"), "missing files retain an actionable recovery record");

        var recorder = new PerformanceRecorder(); recorder.MarkReady();
        for (var i = 0; i < 245; i++) recorder.Sample(i, false, false);
        Check(recorder.SampleCount == 240 && recorder.Latest is { WorkingSetMB: > 0, PrivateMB: > 0, ProcessCpuPercent: >= 0 and <= 100 },
            "resource sampling is bounded and reports real process CPU and memory");
        storage.WriteJson("logs/performance-current.json", recorder.Snapshot(icons.Metrics(), Array.Empty<object>()));
        using var json = JsonDocument.Parse(File.ReadAllText(storage.Resolve("logs/performance-current.json")));
        Check(json.RootElement.GetProperty("Samples").GetArrayLength() == 240 && json.RootElement.GetProperty("StartupScope").GetString()!.Contains("管理窗口"),
            "performance export includes bounded samples and an explicit startup measurement scope");
        var session = json.RootElement.GetProperty("Session");
        Check(session.GetProperty("TotalSamples").GetInt64() == 245 && session.GetProperty("UptimeSeconds").GetDouble() > 0,
            "session totals survive rolling sample eviction and include runtime duration");
        Check(session.GetProperty("PeakWorkingSetMB").GetDouble() >= recorder.Latest!.WorkingSetMB
            && session.GetProperty("PeakPrivateMB").GetDouble() >= recorder.Latest.PrivateMB
            && session.GetProperty("AverageCpuPercent").GetDouble() is >= 0 and <= 100,
            "whole-session resource peaks and time-weighted CPU remain available after sample eviction");
        Check(Math.Abs(session.GetProperty("InitialPrivateMB").GetDouble() + session.GetProperty("PrivateMemoryChangeMB").GetDouble()
            - recorder.Latest.PrivateMB) < .001, "session memory growth retains the original baseline rather than the rolling window start");
        storage.WriteJson("p0-results.json", new { Passed = results.Count, Results = results, RealDesktopFilesMoved = false });
        Console.WriteLine(string.Join(Environment.NewLine, results));
        Console.WriteLine("Results: " + storage.Resolve("p0-results.json"));
        return 0;
    }
}
