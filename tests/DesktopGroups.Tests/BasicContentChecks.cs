using System.IO;

namespace DesktopGroups;

internal static class BasicContentChecks
{
    public static int Run(AppStorage storage)
    {
        var desktop = storage.Resolve("test/desktop");
        var source = storage.Resolve("test/source");
        var target = storage.Resolve("test/target");
        foreach (var folder in new[] { desktop, source, target }) Directory.CreateDirectory(folder);
        var data = new AppStorage(storage.Resolve("test/data"));
        data.EnsureWritable();
        var catalog = new DrawerCatalog(data, desktop);
        Check(catalog.Settings.Version == 1 && catalog.Stores["default"].Profile.Version == 1, "unchanged beta profile loads");
        var ordinary = Path.Combine(desktop, "中文 文档.bin");
        var payload = new byte[5 * 1024 * 1024];
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(ordinary, payload);
        File.WriteAllText(ordinary + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        var store = catalog.Stores["default"];
        store.Add(ordinary);
        var stored = store.StoredPath(store.Active.Single());
        Check(UninstallSafety.Verify(data, desktop) == 2 && File.Exists(stored), "uninstall guard blocks unreturned managed content and preserves it");
        Check(!File.Exists(ordinary) && File.ReadAllBytes(stored).SequenceEqual(payload), "large file is collected byte-for-byte");
        Check(File.ReadAllText(stored + ":Zone.Identifier").Contains("ZoneId=3"), "ordinary move preserves the origin stream");
        File.AppendAllText(stored, "edited");
        File.WriteAllText(ordinary, "unrelated");
        Check(store.RestoreAll().Count == 1 && File.ReadAllText(ordinary) == "unrelated" && new FileInfo(stored).Length > payload.Length,
            "same-name desktop file never merges or overwrites");
        File.Move(ordinary, Path.Combine(desktop, "preserved collision.txt"));

        var reference = Path.Combine(source, "原图.png");
        File.WriteAllText(reference, "referenced only");
        catalog.AddReferences("default", [reference, reference]);
        var referenceId = catalog.Settings.Drawers[0].References.Single().Id;
        var mapping = catalog.Create("资料", source);
        var child = Path.Combine(source, "子目录");
        Directory.CreateDirectory(Path.Combine(child, "空目录"));
        File.WriteAllText(Path.Combine(child, "raw"), "nested file");
        var visible = DrawerContentSource.ScanMapping(source);
        Check(visible.Length == 2 && visible.Any(item => item.IsDirectory) && visible.Any(item => item.Name == "原图.png"), "mapping exposes direct files and folders");
        var backup = new BackupService(data, desktop);
        var snapshot = backup.Create(new BackupOptions { Directory = storage.Resolve("test/backups") }, "普通文件", true);
        var manifest = backup.Validate(snapshot.Directory);
        Check(manifest.Files.Any(file => file.Length > payload.Length) && !manifest.Files.Any(file => file.Path.Contains("原图")), "backup includes edited file and excludes mapped/reference bytes");
        Check(store.RestoreAll().Count == 0 && new FileInfo(ordinary).Length > payload.Length, "edited ordinary file is restored");
        backup.Restore(snapshot.Directory);
        catalog = new DrawerCatalog(data, desktop);
        Check(catalog.Settings.Version == 2 && catalog.Settings.Drawers.Single(d => d.Id == mapping.Id).MappedPath == source &&
            catalog.Settings.Drawers[0].References.Single().Id == referenceId, "snapshot restores mapping and reference settings");
        store = catalog.Stores["default"];
        Check(store.Active.Count == 0 && store.Profile.Entries.Single().IsOrdinaryFile && File.Exists(ordinary),
            "restore reuses identical desktop file without creating competing ownership");
        // Explorer or an indexer can keep reading while explicitly allowing the source to be removed.
        using (var desktopReader = new FileStream(ordinary, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            var recollect = store.StartSession();
            Check(recollect.Count == 0, "restored membership recollects: " + string.Join("; ", recollect));
            Check(!File.Exists(ordinary) && desktopReader.ReadByte() == payload[0],
                "successful recollection removes the desktop name while an existing reader can finish");
            var returned = store.RestoreAll();
            Check(returned.Count == 0, "restored membership returns: " + string.Join("; ", returned));
        }
        Check(File.ReadAllBytes(ordinary).AsSpan(0, payload.Length).SequenceEqual(payload) &&
            File.ReadAllText(ordinary + ":Zone.Identifier").Contains("ZoneId=3"),
            "repeated backup recovery and return preserves file bytes and source stream");
        Check(UninstallSafety.Verify(data, desktop) == 0, "uninstall guard allows returned content and leaves mapping and references alone");
        catalog.RemoveReference("default", referenceId);
        Check(File.Exists(reference), "removing a reference preserves target");
        var unavailable = source + "-renamed";
        Directory.Move(source, unavailable);
        Throws(() => DrawerContentSource.ScanMapping(source), "mapping reports missing directory");
        catalog.Relocate(mapping.Id, unavailable);
        Check(DrawerContentSource.ScanMapping(unavailable).Length == 2 && !Directory.Exists(source), "relocation uses selected directory instead of silently recreating missing folder");
        Check(catalog.Remove(mapping.Id).Count == 0 && Directory.Exists(unavailable), "removing a mapping preserves directory tree");

        var desktopFolder = Path.Combine(desktop, "资料 .lnk");
        Directory.CreateDirectory(Path.Combine(desktopFolder, "项目", "空目录"));
        File.WriteAllText(Path.Combine(desktopFolder, "项目", "附件.tmp"), "directory contents");
        store.Add(desktopFolder);
        var folderEntry = store.Active.Single(entry => entry.IsDirectory);
        Check(!Directory.Exists(desktopFolder) && Directory.Exists(Path.Combine(store.StoredPath(folderEntry), "项目", "空目录")),
            "managed directory moves atomically even with a shortcut-like extension");
        var treeBackup = backup.Create(new BackupOptions { Directory = storage.Resolve("test/backups") }, "目录", true);
        var treeManifest = backup.Validate(treeBackup.Directory);
        Check(treeManifest.Format == 2 && treeManifest.Directories.Any(path => path.EndsWith("空目录", StringComparison.Ordinal)) &&
            treeManifest.Files.Any(file => file.Path.EndsWith("附件.tmp", StringComparison.Ordinal)), "directory backup includes empty folders and all file extensions");
        Check(store.RestoreAll().Count == 0 && File.Exists(Path.Combine(desktopFolder, "项目", "附件.tmp")), "managed directory returns intact");
        Directory.Move(desktopFolder, desktopFolder + "-kept");
        backup.Restore(treeBackup.Directory);
        catalog = new DrawerCatalog(data, desktop);
        store = catalog.Stores["default"];
        Check(store.Active.Single(entry => entry.IsDirectory).Phase == "Stored" && store.RestoreAll().Count == 0 &&
            Directory.Exists(Path.Combine(desktopFolder, "项目", "空目录")), "missing directory recovers from backup with empty children");

        var interruptId = Guid.NewGuid().ToString("N");
        var repairAt = Path.Combine(unavailable, "未提交目录");
        var holding = Path.Combine(unavailable, $".qige-transfer-{interruptId}.source");
        Directory.CreateDirectory(holding);
        File.WriteAllText(Path.Combine(holding, "保留内容"), "restore name on restart");
        var unavailableTarget = Path.Combine(target, "未提交目录");
        data.WriteJson($"recovery/file-transfers/{interruptId}.json",
            new MappedFileOperations.TransferReceipt(repairAt, unavailableTarget, null, true, "Pending", holding));
        MappedFileOperations.RecoverInterrupted(data);
        Check(File.Exists(Path.Combine(repairAt, "保留内容")) && !Directory.Exists(holding) && !Directory.Exists(unavailableTarget),
            "startup rolls back a held source before destination commit");
        var transfer = new MappedFileOperations(data);
        child = Path.Combine(unavailable, "子目录");
        transfer.Transfer(child, target, false, default);
        var copied = Path.Combine(target, "子目录");
        Check(Directory.Exists(Path.Combine(copied, "空目录")) && File.ReadAllText(Path.Combine(copied, "raw")) == "nested file" && Directory.Exists(child),
            "verified directory copy preserves hierarchy, empty directory and source");
        Throws(() => transfer.Transfer(child, target, true, default), "same-name directory is preserved");
        var raw = Path.Combine(child, "raw");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { transfer.Transfer(raw, target, true, cancelled.Token); throw new InvalidOperationException("Cancel did not stop transfer."); }
        catch (OperationCanceledException) { }
        Check(File.Exists(raw) && !File.Exists(Path.Combine(target, "raw")), "cancelled move retains source");
        transfer.Transfer(raw, target, true, default);
        Check(!File.Exists(raw) && File.Exists(Path.Combine(target, "raw")), "same-volume move commits without asking source app to delete");
        MappedFileOperations.Rename(Path.Combine(target, "raw"), "renamed");
        MappedFileOperations.Create(target, "new.txt", false);
        MappedFileOperations.Create(target, "new dir", true);
        Throws(() => MappedFileOperations.Rename(Path.Combine(target, "renamed"), "new.txt"), "rename conflict keeps both items");
        Check(File.Exists(Path.Combine(target, "renamed")) && Directory.Exists(Path.Combine(target, "new dir")), "rename and create operate on mapped content");
        return 0;
    }

    public static int CrossVolume(AppStorage storage, string otherRoot)
    {
        var other = Path.Combine(Path.GetFullPath(otherRoot), Guid.NewGuid().ToString("N"));
        if (MappedFileOperations.SameDrive(other, storage.Root)) throw new IOException("Cross-volume test requires two different drives.");
        Directory.CreateDirectory(other);
        var desktop = storage.Resolve("cross-volume-desktop");
        Directory.CreateDirectory(desktop);
        var target = Path.Combine(other, "mapped");
        Directory.CreateDirectory(target);
        var data = new AppStorage(Path.Combine(other, "data"));
        data.EnsureWritable();
        var operations = new MappedFileOperations(data);
        var regularFile = Path.Combine(desktop, "no-extension");
        File.WriteAllText(regularFile, "cross-volume file");
        operations.Transfer(regularFile, target, true, default);
        Check(!File.Exists(regularFile) && File.ReadAllText(Path.Combine(target, "no-extension")) == "cross-volume file", "cross-volume move commits verified file before source removal");

        var mappedFolder = Path.Combine(desktop, "普通目录");
        Directory.CreateDirectory(Path.Combine(mappedFolder, "空子目录"));
        File.WriteAllText(Path.Combine(mappedFolder, "视频示例.txt"), "cross-volume directory");
        operations.Transfer(mappedFolder, target, true, default);
        Check(!Directory.Exists(mappedFolder) && Directory.Exists(Path.Combine(target, "普通目录", "空子目录")), "cross-volume move preserves directory tree and empty children");

        var managedFolder = Path.Combine(desktop, "托管目录");
        Directory.CreateDirectory(Path.Combine(managedFolder, "空目录"));
        File.WriteAllText(Path.Combine(managedFolder, "文档.txt"), "cross-volume managed directory");
        var catalog = new DrawerCatalog(data, desktop);
        var store = catalog.Stores["default"];
        store.Add(managedFolder);
        var entry = store.Active.Single();
        Check(!Directory.Exists(managedFolder) && Directory.Exists(store.StoredPath(entry)), "desktop folder can be collected to a different data drive");
        var issues = store.RestoreAll();
        Check(issues.Count == 0 && Directory.Exists(Path.Combine(managedFolder, "空目录")), "cross-volume collected directory returns to its original path: " + string.Join("; ", issues));
        Check(MappedFileOperations.ReadUnfinished(data).Count == 0, "completed operations leave no unfinished recovery receipt");
        return 0;
    }

    private static void Throws(Action action, string label)
    {
        try { action(); }
        catch (IOException) { Check(true, label); return; }
        Check(false, label);
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine($"PASS {label}");
    }
}
