using System.IO;
using System.Text.Json;

namespace DesktopGroups;

// Directory renames on the same volume preserve shortcut bytes and NTFS streams.
// Persist the plan first so an interrupted migration resumes before any collection.
internal static class LegacyDataMigration
{
    private const string Journal = "recovery/migration-beta1.json";
    private static readonly (string Source, string Target)[] Mappings =
    [
        ("Data/Groups", "config/groups"),
        ("Data/Recovery", "recovery/layout"),
        ("Data/Diagnostics", "logs/legacy-beta1"),
        ("Storage", "storage")
    ];

    internal sealed class Plan
    {
        public int Version { get; set; } = 1;
        public string SourceRoot { get; set; } = "";
        public bool Complete { get; set; }
        public List<Step> Steps { get; set; } = [];
    }
    internal sealed class Step
    {
        public string Source { get; set; } = "";
        public string Target { get; set; } = "";
        public Dictionary<string, string> Hashes { get; set; } = [];
    }

    public static void Run(AppStorage storage)
    {
        Directory.CreateDirectory(storage.Resolve("recovery"));
        using var migrationLock = new FileStream(storage.Resolve("recovery/migration.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var journal = storage.Resolve(Journal);
        var plan = File.Exists(journal)
            ? JsonSerializer.Deserialize<Plan>(File.ReadAllText(journal)) ?? throw new IOException("迁移记录无法读取。")
            : null;
        if (plan?.Version is not null and not 1) throw new IOException("迁移记录版本不支持。");
        if (plan?.Complete == true) return;
        var sourceRoot = plan?.SourceRoot ?? storage.LegacyRoot;
        if (sourceRoot == null) return;
        var source = new AppStorage(sourceRoot);
        if (source.Root.Equals(storage.Root, StringComparison.OrdinalIgnoreCase)) throw new IOException("旧数据目录不能与新数据目录相同。");
        if (!Path.GetPathRoot(source.Root)!.Equals(Path.GetPathRoot(storage.Root), StringComparison.OrdinalIgnoreCase))
            throw new IOException("旧版数据需要在同一磁盘迁移。请先将完整旧软件目录复制到目标磁盘。");
        // Do not race a still-running beta.1 file-transfer session.
        var oldData = source.Resolve("Data");
        if (!Directory.Exists(oldData) && plan == null && !Directory.Exists(source.Resolve("Storage"))) return;
        using var oldLock = Directory.Exists(oldData)
            ? new FileStream(source.Resolve("Data/shortcut-session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
            : null;
        if (plan == null)
        {
            plan = new Plan { SourceRoot = source.Root };
            foreach (var mapping in Mappings)
            {
                var from = source.Resolve(mapping.Source);
                if (!Directory.Exists(from)) continue;
                var to = storage.Resolve(mapping.Target);
                if (Directory.Exists(to) || File.Exists(to)) throw new IOException($"迁移目标已有内容，未覆盖：{to}");
                plan.Steps.Add(new Step { Source = mapping.Source, Target = mapping.Target, Hashes = Snapshot(from) });
            }
            if (plan.Steps.Count == 0) return;
            storage.WriteJson(Journal, plan);
        }
        foreach (var step in plan.Steps)
        {
            if (!Mappings.Contains((step.Source, step.Target))) throw new IOException("迁移记录包含未知路径。");
            var from = source.Resolve(step.Source);
            var to = storage.Resolve(step.Target);
            if (Directory.Exists(from))
            {
                if (Directory.Exists(to) || File.Exists(to)) throw new IOException($"迁移遇到同名目录，所有文件已保留：{to}");
                Verify(from, step.Hashes);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                Directory.Move(from, to);
            }
            if (!Directory.Exists(to)) throw new IOException($"迁移内容未找到，已保留记录：{to}");
            // Profiles may have been rewritten just before an interrupted final commit.
            if (step.Target != "config/groups") Verify(to, step.Hashes);
        }
        var groups = storage.Resolve("config/groups");
        var groupStep = plan.Steps.SingleOrDefault(step => step.Target == "config/groups");
        if (groupStep != null)
        {
            foreach (var file in groupStep.Hashes)
            {
                var path = new AppStorage(groups).Resolve(file.Key);
                VerifiedTransfer.ValidatePath(path);
                var backupRelative = "recovery/legacy-beta1/groups/" + file.Key;
                var backup = storage.Resolve(backupRelative);
                if (!File.Exists(backup))
                {
                    if (VerifiedTransfer.Hash(path) != file.Value) throw new IOException("旧配置校验未通过，未改写。");
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(path, backup);
                }
                if (VerifiedTransfer.Hash(backup) != file.Value) throw new IOException("原配置备份校验未通过。");
                if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                var profile = JsonSerializer.Deserialize<ShortcutProfile>(File.ReadAllText(backup)) ?? throw new IOException("旧分组无法读取。");
                if (profile.Version != 1) throw new IOException("旧分组版本不支持。");
                foreach (var entry in profile.Entries.Concat(profile.Ungrouped))
                {
                    entry.StoredRelative = Rewrite(entry.StoredRelative);
                    if (entry.PendingRelative != null) entry.PendingRelative = Rewrite(entry.PendingRelative);
                    entry.Versions = entry.Versions.Select(Rewrite).ToList();
                }
                var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(profile, AppStorage.JsonOptions)));
                var current = VerifiedTransfer.Hash(path);
                if (current != file.Value && current != expected)
                    throw new IOException("迁移期间分组配置已改变，文件与备份均已保留。");
                storage.WriteJson(Path.GetRelativePath(storage.Root, path), profile);
            }
        }
        plan.Complete = true;
        storage.WriteJson(Journal, plan);
    }

    private static string Rewrite(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (!normalized.StartsWith("Storage/", StringComparison.OrdinalIgnoreCase))
            throw new IOException("旧配置中的收纳路径不受支持，原配置已保留。");
        var relative = normalized["Storage/".Length..];
        if (relative.Split('/').Any(part => part is "." or ".." or "" ) || relative.Contains(':'))
            throw new IOException("旧配置中的收纳路径无效。");
        return "storage/" + relative;
    }

    private static Dictionary<string, string> Snapshot(string root)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string directory)
        {
            VerifiedTransfer.ValidatePath(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                VerifiedTransfer.ValidatePath(path);
                if (Directory.Exists(path)) Visit(path);
                else files.Add(Path.GetRelativePath(root, path), VerifiedTransfer.Hash(path));
            }
        }
        Visit(root);
        return files;
    }

    private static void Verify(string root, Dictionary<string, string> expected)
    {
        var actual = Snapshot(root);
        if (actual.Count != expected.Count || expected.Any(file => !actual.TryGetValue(file.Key, out var hash) || hash != file.Value))
            throw new IOException($"迁移校验未通过，所有文件和恢复记录均已保留：{root}");
    }
}
