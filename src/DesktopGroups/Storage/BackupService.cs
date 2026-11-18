using System.IO;
using System.Text.Json;

namespace DesktopGroups;

internal sealed class BackupOptions
{
    public bool Enabled { get; set; } = true;
    public string Directory { get; set; } = "";
    public int IntervalMinutes { get; set; } = 60;
    public int RetainCount { get; set; } = 10;
}
internal sealed record BackupFile(string Path, long Length, string Sha256, Dictionary<string, string>? Streams = null);
internal sealed class BackupManifest
{
    public int Format { get; set; } = 1;
    public string Id { get; set; } = "";
    public string SourceRoot { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Reason { get; set; } = "";
    public bool Pinned { get; set; }
    public List<BackupFile> Files { get; set; } = [];
    public List<string> Directories { get; set; } = [];
}
internal sealed record BackupSummary(string Directory, BackupManifest Manifest)
{
    public string Label => $"{Manifest.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm} · {Manifest.Reason} · {Manifest.Files.Count} 个文件" + (Manifest.Pinned ? " · 保留" : "");
}
internal sealed record RestoreJournal(string Id, bool HadConfig, bool HadStorage, bool Committed);

// Every caller holds the single-instance lock and fences configuration/file mutations.
// Snapshots never include caches, locks, logs, other backups or external directories.
internal sealed class BackupService(AppStorage storage, string? desktop = null)
{
    private const string JournalPath = "recovery/backup-restore.json";
    private string Desktop => desktop ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public BackupOptions Options()
    {
        var path = storage.Resolve("config/backup.json");
        var options = File.Exists(path) ? Read<BackupOptions>(path) : new();
        if (string.IsNullOrWhiteSpace(options.Directory)) options.Directory = storage.Resolve("Backups");
        return options;
    }
    public void SaveOptions(BackupOptions options)
    {
        ValidateDestination(options.Directory);
        if (options.IntervalMinutes is < 15 or > 10080 || options.RetainCount is < 1 or > 100)
            throw new IOException("备份间隔需为 15–10080 分钟，自动备份保留数量需为 1–100。 ");
        storage.WriteJson("config/backup.json", options);
    }
    private void ValidateDestination(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new IOException("请选择备份目录的绝对路径。");
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        VerifiedTransfer.ValidatePath(path);
        var root = Path.TrimEndingDirectorySeparator(storage.Root);
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !path.Equals(storage.Resolve("Backups"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("数据目录内仅可使用 Backups 子目录；也可选择数据目录外的其他磁盘位置。");
    }
    private void CheckSnapshotCapacity(DrawerCatalog catalog, string destination)
    {
        const long capacity = 512L * 1024 * 1024;
        long bytes = 0;
        var count = 0;
        void Count(long size)
        {
            bytes += size;
            if (++count > 20000 || bytes > capacity)
                throw new IOException("预计备份超过当前版本的 20,000 项或 512 MB 上限。");
        }

        foreach (var relative in new[] { "config", "storage" })
            foreach (var file in EnumerateSafe(storage.Resolve(relative)))
                if (relative != "config" || !file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) Count(new FileInfo(file).Length);
        foreach (var folder in EnumerateDirectoriesSafe(storage.Resolve("storage"))) Count(0);
        foreach (var store in catalog.Stores.Values)
        foreach (var entry in store.Profile.Entries)
        {
            if (ShortcutStore.Exists(entry, store.StoredPath(entry))) continue;
            if (entry.IsDirectory && Directory.Exists(entry.OriginalPath))
            {
                Count(0);
                foreach (var folder in EnumerateDirectoriesSafe(entry.OriginalPath)) Count(0);
                foreach (var file in EnumerateSafe(entry.OriginalPath)) Count(new FileInfo(file).Length);
            }
            else if (File.Exists(entry.OriginalPath)) Count(new FileInfo(entry.OriginalPath).Length);
        }
        var drive = new DriveInfo(Path.GetPathRoot(destination)!);
        if (drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.AvailableFreeSpace < bytes)
            throw new IOException("备份目标卷的可用空间不足，未创建快照。");
    }

    public BackupSummary Create(BackupOptions options, string reason, bool pinned = false)
    {
        ValidateDestination(options.Directory);
        var catalog = new DrawerCatalog(storage, Desktop);
        if (catalog.Settings.Drawers.Any(d => d.Removing)) throw new IOException("有抽屉尚未完成移除，请处理后再备份。");
        CheckSnapshotCapacity(catalog, options.Directory);
        var id = Guid.NewGuid().ToString("N");
        var pending = Path.Combine(options.Directory, ".pending-" + id);
        var destination = Path.Combine(options.Directory, id);
        var target = new AppStorage(Path.Combine(pending, "data"));
        target.EnsureWritable();
        try
        {
            // Copy only configuration and owned shortcut/history files, validating every path.
            CopyTree(storage.Resolve("config"), target.Resolve("config"), config: true);
            CopyTree(storage.Resolve("storage"), target.Resolve("storage"), config: false);
            foreach (var pair in catalog.Stores)
            {
                var profile = pair.Value.Profile;
                foreach (var entry in profile.Entries)
                {
                    if (entry.PendingRelative != null || entry.RemoveAfterRestore || entry.Phase is not ("Stored" or "Restored"))
                        throw new IOException("存在未完成的文件操作，请先处理运行与恢复中的问题。");
                    var original = entry.OriginalPath;
                    var storedPath = pair.Value.StoredPath(entry);
                    if (entry.IsDirectory)
                    {
                        if (File.Exists(original) || File.Exists(storedPath) || Directory.Exists(original) && Directory.Exists(storedPath))
                            throw new IOException("目录路径存在冲突，请先处理后再备份。");
                        var tree = Directory.Exists(storedPath) ? storedPath : original;
                        var copyPath = target.Resolve(entry.StoredRelative);
                        if (!Directory.Exists(copyPath)) MappedFileOperations.CopyTree(tree, copyPath, default);
                        entry.Phase = "Stored";
                        entry.TransferVerified = true;
                        continue;
                    }
                    var source = File.Exists(storedPath) ? storedPath : original;
                    if (!File.Exists(source)) throw new IOException("备份未完成：找不到 " + Path.GetFileName(original));
                    if (File.Exists(original) && File.Exists(pair.Value.StoredPath(entry)) && VerifiedTransfer.Hash(original) != VerifiedTransfer.Hash(source))
                        throw new IOException("桌面与收纳副本有差异，请先完成同步或处理冲突后再备份。");
                    var copy = target.Resolve(entry.StoredRelative);
                    if (!File.Exists(copy)) VerifiedTransfer.Copy(source, copy, entry.IsOrdinaryFile);
                    if (!entry.IsOrdinaryFile && ShortcutInfo.Read(copy).Key != entry.Key) throw new IOException("快捷方式目标与记录不一致，备份已停止。");
                    // Restored entries also receive a complete owned copy, so a backup can recover missing originals.
                    entry.Hash = VerifiedTransfer.Hash(copy); entry.Phase = "Stored"; entry.TransferVerified = true;
                }
                target.WriteJson($"config/groups/{pair.Key}.json", profile);
            }
            var manifest = new BackupManifest { Id = id, SourceRoot = storage.Root, AppVersion = AppBrand.Version,
                CreatedAt = DateTimeOffset.Now, Reason = reason, Pinned = pinned };
            if (catalog.Stores.Values.Any(store => store.Profile.Entries.Any(entry => entry.IsDirectory)))
            {
                manifest.Format = 2;
                manifest.Directories = EnumerateDirectoriesSafe(target.Resolve("storage"))
                    .Select(dir => Path.GetRelativePath(target.Root, dir).Replace('\\', '/')).ToList();
            }
            foreach (var file in EnumerateSafe(target.Root))
            {
                var relative = Path.GetRelativePath(target.Root, file).Replace('\\', '/');
                ValidateRelative(relative);
                manifest.Files.Add(new(relative, new FileInfo(file).Length, VerifiedTransfer.Hash(file), VerifiedTransfer.NamedStreamHashes(file)));
            }
            if (manifest.Files.Count + manifest.Directories.Count > 20000 || manifest.Files.Sum(f => f.Length) > 512L * 1024 * 1024)
                throw new IOException("备份超过当前版本的 20000 个文件或 512 MB 限制。");
            Write(Path.Combine(pending, "manifest.json"), manifest);
            Validate(pending);
            Directory.Move(pending, destination);
            // Rotation is attempted only after the new snapshot is fully validated and committed.
            try { Rotate(options); } catch (Exception e) { storage.WriteJson("logs/backup-retention.json", new { At = DateTimeOffset.Now, Error = e.Message }); }
            return new(destination, manifest);
        }
        catch
        {
            // Clean only the exact temporary child created by this call, never older snapshots.
            try
            {
                var expected = Path.GetFullPath(Path.Combine(options.Directory, ".pending-" + id));
                if (expected.Equals(Path.GetFullPath(pending), StringComparison.OrdinalIgnoreCase) && Directory.Exists(expected))
                {
                    _ = EnumerateSafe(expected).ToArray();
                    Directory.Delete(expected, recursive: true);
                }
            }
            catch { /* Failed staging cleanup must not hide the original backup error. */ }
            throw;
        }
    }
    public IReadOnlyList<BackupSummary> List(BackupOptions options)
    {
        ValidateDestination(options.Directory);
        if (!Directory.Exists(options.Directory)) return [];
        var results = new List<BackupSummary>();
        foreach (var path in Directory.EnumerateDirectories(options.Directory))
        {
            if (!Guid.TryParseExact(Path.GetFileName(path), "N", out _)) continue;
            try
            {
                VerifiedTransfer.ValidatePath(path);
                var manifest = Read<BackupManifest>(Path.Combine(path, "manifest.json"));
                if ((manifest.Format is 1 or 2) && manifest.Id == Path.GetFileName(path) && SameRoot(manifest.SourceRoot)) results.Add(new(path, manifest));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return results.OrderByDescending(b => b.Manifest.CreatedAt).ToArray();
    }
    public BackupManifest Validate(string directory)
    {
        VerifiedTransfer.ValidatePath(directory);
        var manifest = Read<BackupManifest>(Path.Combine(directory, "manifest.json"));
        if (manifest.Format is not (1 or 2) || !Guid.TryParseExact(manifest.Id, "N", out _) || !SameRoot(manifest.SourceRoot))
            throw new IOException("备份格式不支持或不属于当前数据目录。请使用原数据目录和兼容版本恢复。");
        if (manifest.Files.Count is < 1 or > 20000 || manifest.Files.Any(f => f.Length < 0 || f.Path.StartsWith("config/", StringComparison.Ordinal) && f.Length > 8 * 1024 * 1024)
            || manifest.Files.Sum(f => f.Length) > 512L * 1024 * 1024) throw new IOException("备份清单大小无效。");
        var data = new AppStorage(Path.Combine(directory, "data"));
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            ValidateRelative(file.Path);
            if (!paths.Add(file.Path)) throw new IOException("备份路径重复。");
            var path = data.Resolve(file.Path);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Length || VerifiedTransfer.Hash(path) != file.Sha256 || !StreamsMatch(path, file.Streams))
                throw new IOException("备份文件缺失或校验失败：" + file.Path);
        }
        if (manifest.Format == 1 && manifest.Directories.Count != 0 || manifest.Files.Count + manifest.Directories.Count > 20000)
            throw new IOException("备份目录清单无效。");
        if (manifest.Format == 2)
        {
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var relative in manifest.Directories)
            {
                if (!relative.StartsWith("storage/", StringComparison.Ordinal) || relative.Contains('\\') || relative.Contains(':')
                    || relative.Split('/').Any(part => part is "" or "." or "..") || !directories.Add(relative))
                    throw new IOException("备份目录路径无效。");
                if (!Directory.Exists(data.Resolve(relative))) throw new IOException("备份目录缺失：" + relative);
            }
            var actualDirectories = EnumerateDirectoriesSafe(data.Resolve("storage"))
                .Select(path => Path.GetRelativePath(data.Root, path).Replace('\\', '/'));
            if (!directories.SetEquals(actualDirectories)) throw new IOException("目录清单与实际备份不一致。");
        }
        var actual = EnumerateSafe(data.Root).Select(f => Path.GetRelativePath(data.Root, f).Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.SetEquals(paths) || !paths.Contains("config/drawers.json")) throw new IOException("备份包含清单之外的文件或缺少抽屉配置。");
        ValidateCatalog(data);
        return manifest;
    }
    private DrawerCatalog ValidateCatalog(AppStorage data)
    {
        // Reject missing profiles instead of letting catalog defaults mask incomplete backups.
        var definitions = Read<DrawerSettings>(data.Resolve("config/drawers.json"));
        foreach (var drawer in definitions.Drawers)
        {
            if (drawer.Id != "default" && !Guid.TryParseExact(drawer.Id, "N", out _)) throw new IOException("备份抽屉标识无效。");
            if (!File.Exists(data.Resolve($"config/groups/{drawer.Id}.json"))) throw new IOException("备份缺少抽屉配置。");
            if (drawer.Removing) throw new IOException("备份包含未完成移除的抽屉。");
        }
        var catalog = new DrawerCatalog(data, Desktop);
        foreach (var store in catalog.Stores.Values)
            foreach (var entry in store.Profile.Entries)
                if (entry.IsDirectory)
                {
                    if (entry.Phase != "Stored" || entry.RemoveAfterRestore || !Directory.Exists(store.StoredPath(entry)))
                        throw new IOException("备份缺少完整目录。");
                }
                else if (entry.Phase != "Stored" || entry.PendingRelative != null || entry.RemoveAfterRestore || !File.Exists(store.StoredPath(entry))
                    || VerifiedTransfer.Hash(store.StoredPath(entry)) != entry.Hash || !entry.IsOrdinaryFile && ShortcutInfo.Read(store.StoredPath(entry)).Key != entry.Key)
                    throw new IOException("备份中的分组与快捷方式副本不一致。");
        return catalog;
    }
    public void CheckDesktopConflicts(string directory)
    {
        Validate(directory);
        var snapshot = new AppStorage(Path.Combine(directory, "data"));
        var catalog = ValidateCatalog(snapshot);
        foreach (var entry in catalog.Stores.Values.SelectMany(s => s.Profile.Entries))
        {
            if (!File.Exists(entry.OriginalPath) && !Directory.Exists(entry.OriginalPath)) continue;
            if (!OriginalMatchesBackup(entry, snapshot.Resolve(entry.StoredRelative)))
                throw new IOException("桌面已有不同内容，未覆盖：" + Path.GetFileName(entry.OriginalPath) + "。请先移开该项目。");
        }
    }
    private static bool OriginalMatchesBackup(StoredShortcut entry, string copy)
    {
        if (entry.IsDirectory) return Directory.Exists(entry.OriginalPath) && MappedFileOperations.TreesMatch(entry.OriginalPath, copy);
        return File.Exists(entry.OriginalPath) && VerifiedTransfer.Hash(entry.OriginalPath) == entry.Hash
            && VerifiedTransfer.SameStreams(entry.OriginalPath, copy);
    }

    public void Restore(string directory)
    {
        RecoverInterrupted(storage);
        CheckDesktopConflicts(directory);
        var id = Guid.NewGuid().ToString("N");
        var transaction = storage.Resolve("recovery/restore-transactions/" + id);
        var staged = new AppStorage(Path.Combine(transaction, "new"));
        var manifest = Validate(directory);
        var volume = new DriveInfo(Path.GetPathRoot(storage.Root)!);
        if (volume.DriveType is DriveType.Fixed or DriveType.Removable &&
            volume.AvailableFreeSpace < manifest.Files.Sum(file => file.Length))
            throw new IOException("数据卷空间不足，当前配置和收纳内容已保留。");
        var data = new AppStorage(Path.Combine(directory, "data"));
        var snapshotCatalog = ValidateCatalog(data);
        var existing = snapshotCatalog.Stores.Values.SelectMany(store => store.Profile.Entries)
            .Where(entry => entry.IsOrdinaryFile && OriginalMatchesBackup(entry, data.Resolve(entry.StoredRelative))).ToHashSet();
        bool HasOriginal(string relative) => existing.Any(entry => relative.Equals(entry.StoredRelative, StringComparison.OrdinalIgnoreCase)
            || entry.IsDirectory && relative.StartsWith(entry.StoredRelative + "/", StringComparison.OrdinalIgnoreCase));
        foreach (var relative in manifest.Directories.Where(relative => !HasOriginal(relative)))
            Directory.CreateDirectory(staged.Resolve(relative));
        foreach (var file in manifest.Files)
        {
            if (HasOriginal(file.Path)) continue;
            var target = staged.Resolve(file.Path);
            CopyFile(data.Resolve(file.Path), target, file.Path.StartsWith("config/", StringComparison.Ordinal));
            if (VerifiedTransfer.Hash(target) != file.Sha256 || !StreamsMatch(target, file.Streams)) throw new IOException("恢复暂存副本校验失败。");
        }
        foreach (var (drawerId, store) in snapshotCatalog.Stores)
        {
            foreach (var entry in store.Profile.Entries.Where(existing.Contains)) entry.Phase = "Restored";
            staged.WriteJson($"config/groups/{drawerId}.json", store.Profile);
        }
        _ = new DrawerCatalog(staged, Desktop);
        var journal = new RestoreJournal(id, Directory.Exists(storage.Resolve("config")), Directory.Exists(storage.Resolve("storage")), false);
        storage.WriteJson(JournalPath, journal);
        try
        {
            foreach (var name in new[] { "config", "storage" })
            {
                var current = storage.Resolve(name);
                if (Directory.Exists(current)) Directory.Move(current, Path.Combine(transaction, "old-" + name));
                var next = staged.Resolve(name);
                if (Directory.Exists(next)) Directory.Move(next, current);
            }
            _ = new DrawerCatalog(storage, Desktop);
            storage.WriteJson(JournalPath, journal with { Committed = true });
        }
        catch { RecoverInterrupted(storage); throw; }
        RecoverInterrupted(storage);
    }
    // Called under the session lock before opening any live configuration. A crash
    // between directory renames rolls back to the intact pre-restore directories.
    internal static void RecoverInterrupted(AppStorage storage)
    {
        var path = storage.Resolve(JournalPath);
        if (!File.Exists(path)) return;
        var journal = Read<RestoreJournal>(path);
        if (!Guid.TryParseExact(journal.Id, "N", out _)) throw new IOException("恢复事务标识无效。");
        var transaction = storage.Resolve("recovery/restore-transactions/" + journal.Id);
        if (!journal.Committed)
            foreach (var name in new[] { "config", "storage" })
            {
                var current = storage.Resolve(name); var old = Path.Combine(transaction, "old-" + name);
                VerifiedTransfer.ValidatePath(old);
                var had = name == "config" ? journal.HadConfig : journal.HadStorage;
                if (!Directory.Exists(old) && had) continue;
                if (Directory.Exists(current)) Directory.Move(current, Path.Combine(transaction, "aborted-" + name + "-" + Guid.NewGuid().ToString("N")));
                if (Directory.Exists(old)) Directory.Move(old, current);
            }
        storage.WriteJson("logs/last-backup-restore.json", new { At = DateTimeOffset.Now, journal.Id, journal.Committed });
        File.Delete(path);
    }
    private void Rotate(BackupOptions options)
    {
        foreach (var backup in List(options).Where(b => !b.Manifest.Pinned).Skip(options.RetainCount))
        {
            Validate(backup.Directory);
            // The exact GUID child of the selected destination is the only recursive deletion target.
            var expected = Path.GetFullPath(Path.Combine(options.Directory, backup.Manifest.Id));
            if (!expected.Equals(Path.GetFullPath(backup.Directory), StringComparison.OrdinalIgnoreCase)) throw new IOException("备份轮换路径无效。");
            var extras = EnumerateSafe(backup.Directory).Where(f => !f.StartsWith(Path.Combine(backup.Directory, "data") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !f.Equals(Path.Combine(backup.Directory, "manifest.json"), StringComparison.OrdinalIgnoreCase));
            if (extras.Any()) continue;
            Directory.Delete(expected, recursive: true);
        }
    }
    private bool SameRoot(string root) => Path.IsPathFullyQualified(root) && Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).Equals(storage.Root, StringComparison.OrdinalIgnoreCase);
    private static void ValidateRelative(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.Contains(':') || path.Split('/').Any(p => p is "" or "." or "..")
            || !(path.StartsWith("config/", StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("storage/", StringComparison.Ordinal) && path.Split('/').Length >= 5)) throw new IOException("备份包含无效或越界路径。");
    }
    private static IEnumerable<string> EnumerateSafe(string root)
    {
        VerifiedTransfer.ValidatePath(root);
        if (!Directory.Exists(root)) yield break;
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            VerifiedTransfer.ValidatePath(path);
            if (Directory.Exists(path)) { foreach (var file in EnumerateSafe(path)) yield return file; }
            else yield return path;
        }
    }
    private static IEnumerable<string> EnumerateDirectoriesSafe(string root)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var path in Directory.EnumerateDirectories(root))
        {
            DrawerContentSource.AssertUsable(path, directory: true);
            if (VerifiedTransfer.NamedStreamHashes(path).Count > 0) throw new IOException("暂不备份带备用数据流的目录。");
            yield return path;
            foreach (var child in EnumerateDirectoriesSafe(path)) yield return child;
        }
    }

    private static void CopyTree(string source, string destination, bool config)
    {
        if (!config)
            foreach (var directory in EnumerateDirectoriesSafe(source))
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in EnumerateSafe(source))
        {
            if (config && file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            if (config && !file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                throw new IOException("备份范围包含不支持的文件：" + Path.GetFileName(file));
            CopyFile(file, Path.Combine(destination, Path.GetRelativePath(source, file)), config);
        }
    }
    private static void CopyFile(string source, string destination, bool config)
    {
        if (!config) { _ = VerifiedTransfer.NamedStreamHashes(source); VerifiedTransfer.Copy(source, destination, ordinary: true); return; }
        VerifiedTransfer.ValidatePath(source); VerifiedTransfer.ValidatePath(destination);
        if (new FileInfo(source).Length > 8 * 1024 * 1024) throw new IOException("配置文件过大。");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output); output.Flush(true);
    }
    private static bool StreamsMatch(string path, Dictionary<string, string>? expected)
    {
        var actual = VerifiedTransfer.NamedStreamHashes(path);
        return expected != null && actual.Count == expected.Count && actual.All(p => expected.TryGetValue(p.Key, out var hash) && hash == p.Value);
    }
    private static T Read<T>(string path)
    {
        VerifiedTransfer.ValidatePath(path);
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new IOException("备份元数据过大。");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new IOException("备份元数据无法读取。");
    }
    private static void Write<T>(string path, T value)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(file, value, AppStorage.JsonOptions); file.Flush(true);
    }
}
