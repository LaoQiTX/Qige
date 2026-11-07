using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DesktopGroups;

internal static class StorageSafetyChecks
{
    public static int Run(AppStorage storage)
    {
        var results = new List<string>();
        void Check(bool pass, string text)
        {
            if (!pass) throw new InvalidOperationException(text);
            results.Add("PASS " + text);
        }
        void Reject(Action action, string text)
        {
            try { action(); }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            { results.Add("PASS " + text); return; }
            throw new InvalidOperationException(text);
        }
        string Location(string name) => storage.Resolve(name);
        var install = Location("install with spaces");
        var elsewhere = Location("unrelated working directory");
        Directory.CreateDirectory(install); Directory.CreateDirectory(elsewhere);
        var cwd = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = elsewhere;
            var normal = AppStorage.FromArguments([], install);
            Check(normal.Root == Path.Combine(install, "QigeData"), "default data is beside the executable, independent of working directory");
            Check(!Directory.Exists(normal.Root), "path selection performs no writes");
            normal.EnsureWritable(); normal.WriteJson("config/settings.json", new { 中文 = "保留" });
            Check(!Directory.Exists(Path.Combine(elsewhere, "QigeData")), "no data is created in the current working directory");
            var data = Location("custom data");
            Check(AppStorage.FromArguments(["--data-dir", data], install).Root == data, "explicit development data path is supported");
            Check(AppStorage.FromArguments(["--root", install], install).Root == Path.Combine(install, "QigeData"), "beta.1 startup root is accepted");
            Reject(() => AppStorage.FromArguments(["--data-dir"], install), "missing option path is rejected");
            Reject(() => AppStorage.FromArguments(["--data-dir", "relative"], install), "relative data path is rejected");
            Reject(() => AppStorage.FromArguments(["--root", install, "--data-dir", data], install), "ambiguous data options are rejected");
        }
        finally { Environment.CurrentDirectory = cwd; }
        foreach (var input in new[] { "../escape", "config/../../escape", @"C:\escape", "config/settings.json:stream" })
            Reject(() => storage.Resolve(input), "path escape or stream rejected: " + input);
        var roundtrip = storage.WriteJson("roundtrip.json", new { Revision = 1 });
        storage.WriteJson("roundtrip.json", new { Revision = 2 });
        Check(JsonDocument.Parse(File.ReadAllText(roundtrip)).RootElement.GetProperty("Revision").GetInt32() == 2
            && !File.Exists(roundtrip + ".tmp"), "atomic JSON replacement preserves final revision");
        Check(Marshal.SizeOf<NativeDesktop.Rect>() == 16 && Marshal.SizeOf<NativeDesktop.Point>() == 8, "native structure sizes remain correct");

        // Deterministically unwritable: a file blocks the required directory.
        var blocked = Location("blocked"); File.WriteAllText(blocked, "keep");
        Reject(() => new AppStorage(Path.Combine(blocked, "QigeData")).EnsureWritable(), "unwritable installation fails without alternate storage");
        var target = Location("junction-target"); Directory.CreateDirectory(target);
        var junction = Location("junction");
        // Junction creation on Windows needs no Developer Mode or elevation.
        var linkInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        linkInfo.ArgumentList.Add("/c"); linkInfo.ArgumentList.Add("mklink"); linkInfo.ArgumentList.Add("/J");
        linkInfo.ArgumentList.Add(junction); linkInfo.ArgumentList.Add(target);
        using (var process = System.Diagnostics.Process.Start(linkInfo)!)
        {
            process.WaitForExit();
            Check(process.ExitCode == 0, "isolated junction fixture created");
        }
        try { Reject(() => storage.Resolve("junction/out.json"), "junction escapes are rejected"); }
        finally { Directory.Delete(junction); }

        AppStorage Fixture(string name, bool active)
        {
            var legacy = new AppStorage(Location(name + "/old"));
            legacy.EnsureWritable();
            var desktop = Location(name + "/Desktop"); Directory.CreateDirectory(desktop);
            var original = Path.Combine(desktop, "测试.url");
            File.WriteAllText(original, "[InternetShortcut]\r\nURL=https://example.com/migration\r\n");
            var stored = "Storage/Default/id/current/测试.url";
            var version = "Storage/Default/id/previous/测试.url";
            Directory.CreateDirectory(Path.GetDirectoryName(legacy.Resolve(version))!);
            File.WriteAllText(legacy.Resolve(version), "[InternetShortcut]\r\nURL=https://example.com/previous\r\n");
            if (active)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(legacy.Resolve(stored))!);
                File.Move(original, legacy.Resolve(stored));
                File.WriteAllText(legacy.Resolve(stored) + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            }
            var content = active ? legacy.Resolve(stored) : original;
            legacy.WriteJson("Data/Groups/default.json", new ShortcutProfile
            {
                X = -1300, Folded = true,
                Entries = [new StoredShortcut { OriginalPath = original, StoredRelative = stored, Versions = [version],
                    Hash = VerifiedTransfer.Hash(content), Key = ShortcutInfo.Read(content).Key, Phase = active ? "Stored" : "Restored", TransferVerified = true }]
            });
            legacy.WriteJson("Data/Recovery/native-layout-test.json", new { Version = 1, OriginalPositions = Array.Empty<object>() });
            legacy.WriteJson("Data/Diagnostics/old.json", new { Preserve = true });
            return new AppStorage(Location(name + "/new/QigeData"), install, legacy.Root);
        }
        var migration = Fixture("active-migration", true);
        migration.EnsureWritable();
        LegacyDataMigration.Run(migration);
        var profile = JsonSerializer.Deserialize<ShortcutProfile>(File.ReadAllText(migration.Resolve("config/groups/default.json")))!;
        var entry = profile.Entries.Single();
        Check(profile.X == -1300 && profile.Folded && entry.Phase == "Stored", "migration preserves positions, folded state and active recovery phase");
        Check(entry.StoredRelative.StartsWith("storage/") && entry.Versions.Single().StartsWith("storage/"), "migration rewrites current and history references");
        Check(File.ReadAllText(migration.Resolve(entry.StoredRelative) + ":Zone.Identifier").Contains("ZoneId=3"), "migration preserves NTFS download origin streams");
        Check(File.Exists(migration.Resolve("recovery/legacy-beta1/groups/default.json")) && File.Exists(migration.Resolve("logs/legacy-beta1/old.json")), "original configuration and diagnostics are retained");
        var store = new ShortcutStore(migration, Path.GetDirectoryName(entry.OriginalPath));
        Check(store.Active.Count == 1 && store.RestoreAll().Count == 0 && File.Exists(entry.OriginalPath), "migrated active shortcut restores to original path");
        var saved = File.ReadAllText(migration.Resolve("config/groups/default.json"));
        LegacyDataMigration.Run(migration);
        Check(File.ReadAllText(migration.Resolve("config/groups/default.json")) == saved, "completed migration is idempotent and does not replay stale profiles");

        var interrupted = Fixture("interrupted", false); interrupted.EnsureWritable();
        var steps = new[]
        {
            ("Data/Groups", "config/groups"), ("Data/Recovery", "recovery/layout"),
            ("Data/Diagnostics", "logs/legacy-beta1"), ("Storage", "storage")
        }.Select(pair => new LegacyDataMigration.Step
        {
            Source = pair.Item1, Target = pair.Item2,
            Hashes = Directory.GetFiles(Path.Combine(interrupted.LegacyRoot!, pair.Item1), "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(Path.Combine(interrupted.LegacyRoot!, pair.Item1), path), VerifiedTransfer.Hash)
        }).ToList();
        interrupted.WriteJson("recovery/migration-beta1.json", new LegacyDataMigration.Plan { SourceRoot = interrupted.LegacyRoot!, Steps = steps });
        Directory.CreateDirectory(interrupted.Resolve("config"));
        Directory.Move(Path.Combine(interrupted.LegacyRoot!, "Data/Groups"), interrupted.Resolve("config/groups"));
        LegacyDataMigration.Run(interrupted);
        Check(File.Exists(interrupted.Resolve("config/groups/default.json")) && File.Exists(interrupted.Resolve("storage/Default/id/previous/测试.url")), "migration resumes after directory rename before commit");

        var pending = Fixture("pending", true); pending.EnsureWritable();
        var old = new AppStorage(pending.LegacyRoot!);
        var oldProfile = JsonSerializer.Deserialize<ShortcutProfile>(File.ReadAllText(old.Resolve("Data/Groups/default.json")))!;
        var oldEntry = oldProfile.Entries.Single();
        oldEntry.PendingRelative = "Storage/Default/id/pending/测试.url";
        Directory.CreateDirectory(Path.GetDirectoryName(old.Resolve(oldEntry.PendingRelative))!);
        File.Copy(old.Resolve(oldEntry.StoredRelative), old.Resolve(oldEntry.PendingRelative));
        oldEntry.PendingHash = VerifiedTransfer.Hash(old.Resolve(oldEntry.PendingRelative));
        oldEntry.Phase = "Updating";
        oldProfile.Ungrouped.Add(new StoredShortcut { OriginalPath = oldEntry.OriginalPath,
            StoredRelative = "Storage/Default/id/ungrouped/测试.url", Versions = [oldEntry.Versions.Single()] });
        old.WriteJson("Data/Groups/default.json", oldProfile);
        LegacyDataMigration.Run(pending);
        var pendingStore = new ShortcutStore(pending, Path.GetDirectoryName(oldEntry.OriginalPath));
        Check(pendingStore.Profile.Entries.Single().PendingRelative!.StartsWith("storage/") &&
            pendingStore.Profile.Ungrouped.Single().StoredRelative.StartsWith("storage/") &&
            pendingStore.Profile.Ungrouped.Single().Versions.Single().StartsWith("storage/"),
            "migration rewrites pending updates and ungrouped history");
        Check(pendingStore.StartSession().Count == 0 && pendingStore.Active.Single().PendingRelative == null &&
            pendingStore.RestoreAll().Count == 0, "a verified pending update completes and restores after migration");

        var rewritten = Fixture("rewritten-interruption", false); rewritten.EnsureWritable();
        LegacyDataMigration.Run(rewritten);
        var finished = JsonSerializer.Deserialize<LegacyDataMigration.Plan>(File.ReadAllText(rewritten.Resolve("recovery/migration-beta1.json")))!;
        finished.Complete = false;
        rewritten.WriteJson("recovery/migration-beta1.json", finished);
        LegacyDataMigration.Run(rewritten);
        Check(JsonSerializer.Deserialize<LegacyDataMigration.Plan>(File.ReadAllText(rewritten.Resolve("recovery/migration-beta1.json")))!.Complete,
            "migration resumes after profile rewrite before completion commit");
        finished.Complete = false; rewritten.WriteJson("recovery/migration-beta1.json", finished);
        var changedProfile = JsonSerializer.Deserialize<ShortcutProfile>(File.ReadAllText(rewritten.Resolve("config/groups/default.json")))!;
        changedProfile.X = 987;
        rewritten.WriteJson("config/groups/default.json", changedProfile);
        var changedHash = VerifiedTransfer.Hash(rewritten.Resolve("config/groups/default.json"));
        Reject(() => LegacyDataMigration.Run(rewritten), "a changed profile blocks incomplete migration replay");
        Check(VerifiedTransfer.Hash(rewritten.Resolve("config/groups/default.json")) == changedHash, "conflicting migrated profile is never overwritten");

        var conflict = Fixture("conflict", false); conflict.EnsureWritable();
        Directory.CreateDirectory(conflict.Resolve("config/groups")); File.WriteAllText(conflict.Resolve("config/groups/keep.txt"), "unchanged");
        Reject(() => LegacyDataMigration.Run(conflict), "migration refuses an existing destination");
        Check(File.ReadAllText(conflict.Resolve("config/groups/keep.txt")) == "unchanged" &&
            File.Exists(Path.Combine(conflict.LegacyRoot!, "Data/Groups/default.json")), "migration conflict preserves both source and destination");
        var busy = Fixture("busy", false); busy.EnsureWritable();
        using (var held = new FileStream(Path.Combine(busy.LegacyRoot!, "Data/shortcut-session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Reject(() => LegacyDataMigration.Run(busy), "migration refuses a running legacy file-transfer session");
        storage.WriteJson("results.json", new { Passed = results.Count, Results = results, RealDesktopModified = false });
        Console.WriteLine(string.Join(Environment.NewLine, results));
        return 0;
    }
}
