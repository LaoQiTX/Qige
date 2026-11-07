using System.IO;
using System.Text.Json;

namespace DesktopGroups;

internal sealed class StoredShortcut
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OriginalPath { get; set; } = "";
    public string StoredRelative { get; set; } = "";
    public string Key { get; set; } = "";
    public string Hash { get; set; } = "";
    public string Phase { get; set; } = "Restored";
    public bool TransferVerified { get; set; }
    public bool RemoveAfterRestore { get; set; }
    public string? PendingRelative { get; set; }
    public string? PendingHash { get; set; }
    public List<string> Versions { get; set; } = [];
    public int? OriginalX { get; set; }
    public int? OriginalY { get; set; }
}

internal sealed class ShortcutProfile
{
    public int Version { get; set; } = 1;
    public int X { get; set; } = 520;
    public int Y { get; set; } = 180;
    public bool Folded { get; set; }
    public List<StoredShortcut> Entries { get; set; } = [];
    public List<StoredShortcut> Ungrouped { get; set; } = [];
}

internal sealed class ShortcutStore
{
    private readonly AppStorage storage;
    private readonly string desktop;
    private readonly string profileRelative;
    private readonly string storeRelative;
    public ShortcutProfile Profile { get; }
    public IReadOnlyList<StoredShortcut> Active => Profile.Entries.Where(entry => (entry.Phase is "Stored" or "Updating") && File.Exists(StoredPath(entry))).ToArray();
    public string DesktopPath => desktop;
    public event Action? Changed;

    public ShortcutStore(AppStorage storage, string? desktop = null, string profileRelative = "config/groups/default.json", string storeRelative = "storage/default")
    {
        this.storage = storage;
        this.desktop = Path.TrimEndingDirectorySeparator(Path.GetFullPath(desktop ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)));
        this.profileRelative = profileRelative;
        this.storeRelative = storeRelative;
        var profile = storage.Resolve(profileRelative);
        Profile = File.Exists(profile) ? JsonSerializer.Deserialize<ShortcutProfile>(File.ReadAllText(profile)) ?? throw new IOException("收纳记录无法读取。") : new();
        if (Profile.Version != 1) throw new IOException("收纳记录版本不支持。");
        foreach (var entry in Profile.Entries.Concat(Profile.Ungrouped))
        {
            ValidateOriginal(entry.OriginalPath);
            ValidateStored(entry.StoredRelative);
            if (entry.PendingRelative != null) ValidateStored(entry.PendingRelative);
            foreach (var version in entry.Versions) ValidateStored(version);
        }
    }

    public string StoredPath(StoredShortcut entry) => storage.Resolve(entry.StoredRelative);
    public bool CanAccept(IReadOnlyList<string> paths)
    {
        if (paths.Count is 0 or > 12) return false;
        try
        {
            foreach (var path in paths) { ValidateOriginal(path); if (!File.Exists(path)) return false; }
            return Profile.Entries.Select(entry => entry.OriginalPath).Concat(paths).Distinct(StringComparer.OrdinalIgnoreCase).Count() <= 12;
        }
        catch (IOException) { return false; }
        catch (ArgumentException) { return false; }
    }
    public void Save() => storage.WriteJson(profileRelative, Profile);

    public void Add(string path, IconPlacement? position = null)
    {
        path = Path.GetFullPath(path);
        ValidateOriginal(path);
        if (Profile.Entries.Count >= 12 && !Profile.Entries.Any(item => item.OriginalPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("小原型最多收纳 12 个快捷方式。");
        var info = ShortcutInfo.Read(path);
        var entry = Profile.Entries.SingleOrDefault(item => item.OriginalPath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            entry = new StoredShortcut { OriginalPath = path, Key = info.Key, OriginalX = position?.X, OriginalY = position?.Y };
            entry.StoredRelative = NewStoredPath(entry);
            Profile.Entries.Add(entry);
        }
        else if (entry.Key != info.Key) throw new IOException("这个已归组入口的目标已改变，请先归还并检查，未自动合并。");
        Save();
        Collect(entry);
        Changed?.Invoke();
    }

    public IReadOnlyList<string> StartSession()
    {
        var issues = new List<string>();
        foreach (var entry in Profile.Entries.ToArray())
        {
            try
            {
                if (entry.RemoveAfterRestore) { RestoreEntry(entry); ForgetRestored(entry); }
                else { ResolvePending(entry); Collect(entry); }
            }
            catch (Exception exception) { issues.Add($"{Path.GetFileName(entry.OriginalPath)}：{exception.Message}"); }
        }
        Changed?.Invoke();
        return issues;
    }

    public IReadOnlyList<string> Synchronize()
    {
        var issues = new List<string>();
        foreach (var entry in Profile.Entries.Where(item => (item.Phase is "Stored" or "Updating") && File.Exists(item.OriginalPath) && File.Exists(StoredPath(item))))
        {
            try
            {
                ResolvePending(entry);
                if (!File.Exists(entry.OriginalPath)) continue;
                if (ShortcutInfo.Read(entry.OriginalPath).Key != entry.Key)
                    throw new IOException("桌面同名入口目标不同，已保留两份，未自动合并。");
                BeginUpdate(entry);
            }
            catch (Exception exception) { issues.Add($"{Path.GetFileName(entry.OriginalPath)}：{exception.Message}"); }
        }
        Changed?.Invoke();
        return issues;
    }

    public IReadOnlyList<string> RestoreAll()
    {
        var issues = new List<string>();
        foreach (var entry in Profile.Entries.ToArray())
        {
            try
            {
                RestoreEntry(entry);
                if (entry.RemoveAfterRestore) ForgetRestored(entry);
            }
            catch (Exception exception) { issues.Add($"{Path.GetFileName(entry.OriginalPath)}：{exception.Message}"); }
        }
        Changed?.Invoke();
        return issues;
    }

    public void ReturnAndUngroup(string storedPath)
    {
        var entry = Profile.Entries.SingleOrDefault(item => StoredPath(item).Equals(storedPath, StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("此入口已不在分区中。");
        entry.RemoveAfterRestore = true;
        Save(); // A crash between return and forgetting membership must not recollect it.
        RestoreEntry(entry);
        ForgetRestored(entry);
        Changed?.Invoke();
    }

    private void ForgetRestored(StoredShortcut entry)
    {
        if (entry.Phase != "Restored" || !File.Exists(entry.OriginalPath) || File.Exists(StoredPath(entry)))
            throw new IOException("归还尚未确认，仍保留分组与恢复记录。");
        Profile.Ungrouped.Add(entry);
        Profile.Entries.Remove(entry);
        Save();
    }

    private void RestoreEntry(StoredShortcut entry)
    {
        ResolvePending(entry);
        if (entry.Phase == "Restoring" && File.Exists(entry.OriginalPath) && File.Exists(StoredPath(entry)))
        {
            if (!entry.TransferVerified || VerifiedTransfer.Hash(entry.OriginalPath) != entry.Hash)
                throw new IOException("上次归还没有完整校验记录，两份文件均保留，请检查后重试。");
            VerifiedTransfer.RemoveDuplicate(StoredPath(entry), entry.Hash);
            entry.Phase = "Restored"; Save(); return;
        }
        if (File.Exists(entry.OriginalPath) && File.Exists(StoredPath(entry)))
        {
            if (ShortcutInfo.Read(entry.OriginalPath).Key != entry.Key)
                throw new IOException("桌面同名内容与分区入口不同，已保留两份；请处理同名文件后再次归还。");
            BeginUpdate(entry);
        }
        var stored = StoredPath(entry);
        if (!File.Exists(stored))
        {
            if (!File.Exists(entry.OriginalPath)) throw new IOException("两处均未找到文件，恢复记录已保留。");
            entry.Phase = "Restored"; Save(); return;
        }
        entry.Hash = VerifiedTransfer.Hash(stored);
        entry.Phase = "Restoring"; entry.TransferVerified = false; Save();
        VerifiedTransfer.Move(stored, entry.OriginalPath, entry.Hash, () => { entry.TransferVerified = true; Save(); });
        entry.Phase = "Restored"; Save();
    }

    private void Collect(StoredShortcut entry)
    {
        var stored = StoredPath(entry);
        if (File.Exists(stored))
        {
            var hash = VerifiedTransfer.Hash(stored);
            if (entry.Phase == "Restoring" && File.Exists(entry.OriginalPath))
            {
                if (!entry.TransferVerified || VerifiedTransfer.Hash(entry.OriginalPath) != hash) throw new IOException("上次恢复未完整确认或遇到同名冲突，两份文件均已保留。");
                VerifiedTransfer.RemoveDuplicate(stored, hash);
                entry.Phase = "Restored"; Save(); Collect(entry); return;
            }
            if (entry.Phase == "Collecting")
            {
                if (!entry.TransferVerified || hash != entry.Hash) throw new IOException("上次收纳副本未完整确认，源文件和记录均已保留。");
                if (File.Exists(entry.OriginalPath)) VerifiedTransfer.RemoveDuplicate(entry.OriginalPath, entry.Hash);
            }
            if (ShortcutInfo.Read(stored).Key != entry.Key) throw new IOException("收纳中的快捷方式目标已变化，请先归还检查。");
            entry.Hash = hash; entry.Phase = "Stored"; Save();
            if (File.Exists(entry.OriginalPath))
            {
                if (ShortcutInfo.Read(entry.OriginalPath).Key != entry.Key) throw new IOException("桌面同名文件目标不同，未移动。");
                BeginUpdate(entry);
            }
            return;
        }
        if (!File.Exists(entry.OriginalPath)) throw new IOException("原快捷方式未找到，分组记录已保留。");
        var source = ShortcutInfo.ReadStable(entry.OriginalPath);
        if (source.Info.Key != entry.Key) throw new IOException("入口目标已改变，未自动收纳。");
        entry.Hash = source.Hash;
        entry.Phase = "Collecting"; entry.TransferVerified = false; Save();
        VerifiedTransfer.Move(entry.OriginalPath, stored, entry.Hash, () => { entry.TransferVerified = true; Save(); });
        entry.Phase = "Stored"; Save();
    }

    private void BeginUpdate(StoredShortcut entry)
    {
        var source = ShortcutInfo.ReadStable(entry.OriginalPath);
        if (source.Info.Key != entry.Key) throw new IOException("桌面同名入口的目标或参数已改变，未合并。");
        entry.PendingRelative = NewStoredPath(entry);
        entry.PendingHash = source.Hash;
        entry.Phase = "Updating"; entry.TransferVerified = false; Save();
        VerifiedTransfer.Move(entry.OriginalPath, storage.Resolve(entry.PendingRelative), entry.PendingHash, () => { entry.TransferVerified = true; Save(); });
        CommitUpdate(entry);
    }

    private void ResolvePending(StoredShortcut entry)
    {
        if (entry.PendingRelative == null) return;
        var pending = storage.Resolve(entry.PendingRelative);
        if (!File.Exists(pending)) { entry.PendingRelative = null; entry.PendingHash = null; Save(); return; }
        if (!entry.TransferVerified || VerifiedTransfer.Hash(pending) != entry.PendingHash) throw new IOException("待完成更新的副本未完整确认，所有文件均保留。");
        if (File.Exists(entry.OriginalPath)) VerifiedTransfer.RemoveDuplicate(entry.OriginalPath, entry.PendingHash!);
        CommitUpdate(entry);
    }

    private void CommitUpdate(StoredShortcut entry)
    {
        if (File.Exists(StoredPath(entry))) entry.Versions.Add(entry.StoredRelative);
        entry.StoredRelative = entry.PendingRelative!;
        entry.Hash = entry.PendingHash!;
        entry.PendingRelative = null; entry.PendingHash = null;
        entry.Phase = "Stored"; Save();
    }

    private string NewStoredPath(StoredShortcut entry) => $"{storeRelative}/{entry.Id}/{Guid.NewGuid():N}/{Path.GetFileName(entry.OriginalPath)}";
    private void ValidateStored(string relative)
    {
        var full = storage.Resolve(relative);
        var root = storage.Resolve(storeRelative) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !ShortcutInfo.IsShortcut(full)) throw new IOException("收纳记录包含无效路径。");
    }
    private void ValidateOriginal(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), desktop, StringComparison.OrdinalIgnoreCase) || !ShortcutInfo.IsShortcut(path))
            throw new IOException("这个原型只实际收纳你个人桌面根目录的 .lnk／.url；公共桌面与其他文件暂不移动。");
        VerifiedTransfer.ValidatePath(path);
    }
}
