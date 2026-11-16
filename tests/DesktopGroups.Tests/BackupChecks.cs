using System.IO;
using System.Text.Json;

namespace DesktopGroups;

internal static class BackupChecks
{
    public static int Run(AppStorage storage)
    {
        var results = new List<string>();
        void Check(bool pass, string name) { if (!pass) throw new InvalidOperationException(name); results.Add("PASS " + name); }
        bool Refuses(Action action) { try { action(); return false; } catch (Exception e) when (e is IOException or ArgumentException or JsonException) { return true; } }
        var desktop = storage.Resolve("Desktop"); Directory.CreateDirectory(desktop);
        var catalog = new DrawerCatalog(storage, desktop);
        var drawer = catalog.Create("备份样例");
        var store = catalog.Stores[drawer.Id];
        var original = Path.Combine(desktop, "备份入口.url");
        File.WriteAllText(original, "[InternetShortcut]\r\nURL=https://example.com/backup\r\n");
        File.WriteAllText(original + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        store.Add(original); store.Profile.X = -540; store.Profile.Y = 210; store.Profile.Folded = true;
        store.Profile.ManualExpansion = true; store.Profile.ManualCollapsed = false; store.Profile.Density = IconDensity.Compact; store.Save();
        storage.WriteJson("cache/secret.json", new { Secret = true });
        storage.WriteJson("logs/private.json", new { Secret = true });
        Directory.CreateDirectory(storage.Resolve("recovery"));
        File.WriteAllText(storage.Resolve("recovery/session.lock"), "not backed up");
        var service = new BackupService(storage, desktop);
        var options = service.Options(); options.RetainCount = 1;
        service.SaveOptions(options);
        var manual = service.Create(options, "手动测试", true);
        Check(service.Validate(manual.Directory).Files.All(f => f.Path.StartsWith("config/") || f.Path.StartsWith("storage/")), "backup excludes logs, cache, active locks and backup recursion");
        var backed = new DrawerCatalog(new AppStorage(Path.Combine(manual.Directory, "data")), desktop);
        Check(backed.Stores[drawer.Id].Active.Count == 1 && !File.Exists(original), "complete snapshot includes owned shortcuts without touching the desktop");
        var copied = backed.Stores[drawer.Id].StoredPath(backed.Stores[drawer.Id].Active[0]);
        Check(File.ReadAllText(copied + ":Zone.Identifier").Contains("ZoneId=3"), "backup preserves the download-origin named stream");
        var a = service.Create(options, "自动一"); var b = service.Create(options, "自动二");
        Check(!Directory.Exists(a.Directory) && Directory.Exists(b.Directory) && Directory.Exists(manual.Directory), "rotation keeps the newest automatic snapshot and preserves pinned manual snapshots");
        var configHash = VerifiedTransfer.Hash(storage.Resolve("config/drawers.json"));
        var manifestPath = Path.Combine(manual.Directory, "manifest.json");
        var pristineManifest = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(pristineManifest)!;
        var protectedCopy = File.ReadAllBytes(copied);
        var protectedZone = File.ReadAllText(copied + ":Zone.Identifier");
        File.WriteAllText(copied, "truncated");
        Check(Refuses(() => service.Restore(manual.Directory)) && VerifiedTransfer.Hash(storage.Resolve("config/drawers.json")) == configHash,
            "corrupt snapshot is rejected before any live configuration changes");
        File.WriteAllBytes(copied, protectedCopy);
        File.WriteAllText(copied + ":Zone.Identifier", "changed zone");
        Check(Refuses(() => service.Validate(manual.Directory)), "snapshot validates named streams as well as main file bytes");
        File.WriteAllText(copied + ":Zone.Identifier", protectedZone);
        manifest.Format = 999; File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
        Check(Refuses(() => service.Validate(manual.Directory)), "unsupported snapshot format is rejected");
        manifest.Format = 1; manifest.Files.Add(new("../escape.json", 0, "")); File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
        Check(Refuses(() => service.Validate(manual.Directory)), "snapshot traversal paths are rejected");
        File.WriteAllText(manifestPath, pristineManifest);
        manifest = JsonSerializer.Deserialize<BackupManifest>(pristineManifest)!; manifest.Files.Add(manifest.Files[0]);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
        Check(Refuses(() => service.Validate(manual.Directory)), "duplicate manifest paths are rejected");
        File.WriteAllText(manifestPath, pristineManifest);
        manifest = JsonSerializer.Deserialize<BackupManifest>(pristineManifest)!; manifest.SourceRoot = Path.Combine(storage.Root, "other");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
        Check(Refuses(() => service.Validate(manual.Directory)), "snapshot from another data root cannot be applied accidentally");
        File.WriteAllText(manifestPath, pristineManifest);
        File.WriteAllText(original, "[InternetShortcut]\r\nURL=https://example.com/conflict\r\n");
        var conflictHash = VerifiedTransfer.Hash(original);
        Check(Refuses(() => service.Restore(manual.Directory)) && VerifiedTransfer.Hash(original) == conflictHash,
            "restoration preserves different desktop contents and refuses the snapshot");
        File.Move(original, storage.Resolve("kept-conflict.url"));
        var owned = store.StoredPath(store.Active[0]);
        var holding = storage.Resolve("holding.url"); File.Move(owned, holding);
        Check(Refuses(() => service.Create(options, "应失败")) && Directory.Exists(b.Directory), "failed backup cannot rotate away the last verified snapshot");
        File.Move(holding, owned);
        Check(store.RestoreAll().Count == 0 && File.Exists(original), "current contents return before snapshot restoration");
        store.Profile.X = 999; store.Save();
        File.Move(original, storage.Resolve("outside-before-restore.url"));
        service.Restore(manual.Directory);
        var restored = new DrawerCatalog(storage, desktop); var restoredStore = restored.Stores[drawer.Id];
        Check(restoredStore.Profile.X == -540 && restoredStore.Profile.ManualExpansion && !restoredStore.Profile.ManualCollapsed
            && restoredStore.Profile.Density == IconDensity.Compact && restoredStore.Active.Count == 1, "restore recovers independent layout, manual state, density and complete membership");
        Check(restoredStore.StartSession().Count == 0 && restoredStore.RestoreAll().Count == 0 && File.Exists(original),
            "restored snapshot recovers a missing original and survives collection and return");
        Check(File.ReadAllText(original + ":Zone.Identifier").Contains("ZoneId=3"), "restored file retains its download-origin stream");
        // Simulate process loss after moving old config and installing the new config.
        var txId = Guid.NewGuid().ToString("N");
        var tx = storage.Resolve("recovery/restore-transactions/" + txId); Directory.CreateDirectory(tx);
        var before = VerifiedTransfer.Hash(storage.Resolve("config/drawers.json"));
        storage.WriteJson("recovery/backup-restore.json", new RestoreJournal(txId, true, true, false));
        Directory.Move(storage.Resolve("config"), Path.Combine(tx, "old-config"));
        storage.WriteJson("config/drawers.json", new DrawerSettings());
        BackupService.RecoverInterrupted(storage); BackupService.RecoverInterrupted(storage);
        Check(VerifiedTransfer.Hash(storage.Resolve("config/drawers.json")) == before && !File.Exists(storage.Resolve("recovery/backup-restore.json")),
            "interrupted restore rolls back before loading live config and is idempotent");
        var invalid = new BackupOptions { Directory = storage.Resolve("storage/backups") };
        Check(Refuses(() => service.SaveOptions(invalid)), "backup destination cannot overlap managed storage");
        storage.WriteJson("backup-results.json", new { Passed = results.Count, Results = results, RealDesktopFilesMoved = false });
        Console.WriteLine(string.Join(Environment.NewLine, results)); return 0;
    }
}
