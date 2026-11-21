using System.IO;
using System.Text.Json;

namespace DesktopGroups;

internal sealed class StoredShortcut
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OriginalPath { get; set; } = "";
    public string StoredRelative { get; set; } = "";
    public string Key { get; set; } = "";
    public bool IsOrdinaryFile { get; set; }
    public bool IsDirectory { get; set; }
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
    public double Width { get; set; } = 420;
    public double Height { get; set; } = 352;
    public bool Locked { get; set; }
    public bool ManualExpansion { get; set; }
    public bool ManualCollapsed { get; set; } = true;
    public IconDensity Density { get; set; } = IconDensity.Standard;
    public int Transparency { get; set; } = DrawerAppearance.DefaultTransparency;
    public List<StoredShortcut> Entries { get; set; } = [];
    public List<StoredShortcut> Ungrouped { get; set; } = [];
}

internal sealed class ShortcutStore
{
    private readonly AppStorage storage;
    private readonly string desktop;
    private readonly string profileRelative;
    private readonly string storeRelative;
    public ShortcutProfile Profile { get; private set; }
    public IReadOnlyList<StoredShortcut> Active => Profile.Entries.Where(entry => (entry.Phase is "Stored" or "Updating") && Exists(entry, StoredPath(entry))).ToArray();
    public string DesktopPath => desktop;
    public string DirectoryPath => storage.Resolve(storeRelative);
    public event Action? Changed;

    public ShortcutStore(AppStorage storage, string? desktop = null, string profileRelative = "config/groups/default.json", string storeRelative = "storage/default")
    {
        this.storage = storage;
        this.desktop = Path.TrimEndingDirectorySeparator(Path.GetFullPath(desktop ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)));
        this.profileRelative = profileRelative;
        this.storeRelative = storeRelative;
        var profile = storage.Resolve(profileRelative);
        Profile = File.Exists(profile) ? JsonSerializer.Deserialize<ShortcutProfile>(File.ReadAllText(profile)) ?? throw new IOException("收纳记录无法读取。") : new();
        if (Profile.Version is not (1 or 2)) throw new IOException("收纳记录版本不支持。");
        foreach (var entry in Profile.Entries.Concat(Profile.Ungrouped))
        {
            if ((entry.IsOrdinaryFile && (Profile.Version < 2 || entry.Key.Length != 0 || entry.PendingRelative != null || entry.Versions.Count != 0))
                || entry.IsDirectory && !entry.IsOrdinaryFile || !entry.IsDirectory && entry.IsOrdinaryFile == ShortcutInfo.IsShortcut(entry.OriginalPath)) throw new IOException("收纳记录的文件类型无效。");
            ValidateOriginal(entry.OriginalPath);
            ValidateStored(entry.StoredRelative, entry);
            if (entry.PendingRelative != null) ValidateStored(entry.PendingRelative, entry);
            foreach (var version in entry.Versions) ValidateStored(version, entry);
        }
    }

    internal static bool Exists(StoredShortcut entry, string path) => entry.IsDirectory ? Directory.Exists(path) : File.Exists(path);
    public string StoredPath(StoredShortcut entry) => storage.Resolve(entry.StoredRelative);
    public bool CanAccept(IReadOnlyList<string> paths) => CanAccept(paths, out _);
    internal bool CanAccept(IReadOnlyList<string> paths, out string reason)
    {
        reason = "";
        if (paths.Count == 0) { reason = "没有可收纳的文件路径。"; return false; }
        try
        {
            foreach (var path in paths)
            {
                ValidateOriginal(path);
                var directory = AssertSource(Path.GetFullPath(path));
                if (directory && MappedFileOperations.IsAtOrInside(storage.Root, path))
                { reason = "不能收纳包含栖格数据的目录。"; return false; }
            }
            return true;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        { reason = error.Message; return false; }
    }
    public void Save() => storage.WriteJson(profileRelative, Profile);

    // The worker uses a separate model. Publishing the result happens on the dispatcher.
    internal ShortcutStore Detached() => new(storage, desktop, profileRelative, storeRelative);
    internal void Publish(ShortcutProfile result)
    {
        result.X = Profile.X; result.Y = Profile.Y; result.Width = Profile.Width; result.Height = Profile.Height;
        result.Folded = Profile.Folded; result.Locked = Profile.Locked;
        result.ManualExpansion = Profile.ManualExpansion; result.ManualCollapsed = Profile.ManualCollapsed; result.Density = Profile.Density;
        result.Transparency = Profile.Transparency;
        Profile = result; Save(); Changed?.Invoke();
    }
    internal void RetryEntry(string id, bool restore)
    {
        var entry = Profile.Entries.Single(e => e.Id == id);
        if (restore || entry.RemoveAfterRestore || entry.Phase == "Restoring")
        { RestoreEntry(entry); if (entry.RemoveAfterRestore) ForgetRestored(entry); }
        else { ResolvePending(entry); Collect(entry); }
        Changed?.Invoke();
    }

    internal static string WebsitePath(string desktopPath, string name, string url)
    {
        name = name.Trim();
        if (name.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length is < 1 or > 100 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.')
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new IOException("请输入有效名称和 http / https 网址。");
        return Path.Combine(desktopPath, name + ".url");
    }
    internal void CreateWebsite(string name, string url)
    {
        var path = WebsitePath(desktop, name, url); ValidateOriginal(path);
        if (Profile.Entries.Any(e => e.OriginalPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("抽屉已有同名入口，请使用其他名称。");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(file, new System.Text.UTF8Encoding(false)))
            writer.Write("[InternetShortcut]\r\nURL=" + new Uri(url.Trim()).AbsoluteUri + "\r\n");
        Add(path); // On collection failure the created original remains recoverable on the desktop.
    }
    public void Add(string path, IconPlacement? position = null)
    {
        path = Path.GetFullPath(path);
        ValidateOriginal(path);
        var directory = AssertSource(path);
        if (directory)
        {
            if (!MappedFileOperations.SameDrive(path, DirectoryPath)) RecycleBin.AssertAvailable(path);
            if (MappedFileOperations.IsAtOrInside(storage.Root, path)) throw new IOException("不能收纳包含栖格数据的目录。");
        }
        var ordinary = directory || !ShortcutInfo.IsShortcut(path);
        var key = ordinary ? "" : ShortcutInfo.Read(path).Key;
        var entry = Profile.Entries.SingleOrDefault(item => item.OriginalPath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            entry = new StoredShortcut { OriginalPath = path, Key = key, IsOrdinaryFile = ordinary, IsDirectory = directory, OriginalX = position?.X, OriginalY = position?.Y };
            entry.StoredRelative = NewStoredPath(entry);
            Profile.Entries.Add(entry);
        }
        else if (entry.Key != key || entry.IsDirectory != directory) throw new IOException("这个已归组入口的目标已改变，请先归还并检查，未自动合并。");
        if (ordinary) Profile.Version = 2;
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
        foreach (var entry in Profile.Entries.Where(item => (item.Phase is "Stored" or "Updating") && Exists(item, item.OriginalPath) && Exists(item, StoredPath(item))))
        {
            try
            {
                ResolvePending(entry);
                if (!Exists(entry, entry.OriginalPath)) continue;
                if (entry.IsOrdinaryFile) throw new IOException("桌面出现同名文件，两份均已保留。");
                if (ShortcutInfo.Read(entry.OriginalPath).Key != entry.Key)
                    throw new IOException("桌面同名入口目标不同，已保留两份，未自动合并。");
                BeginUpdate(entry);
            }
            catch (Exception exception) { issues.Add($"{Path.GetFileName(entry.OriginalPath)}：{exception.Message}"); }
        }
        Changed?.Invoke();
        return issues;
    }

    // Absence must be distinguished from permission failures and disconnected roots.
    internal bool IsMissing(StoredShortcut entry)
    {
        bool Exists(string path)
        {
            VerifiedTransfer.ValidatePath(path);
            try { _ = File.GetAttributes(path); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }
        if (Exists(entry.OriginalPath) || Exists(StoredPath(entry))
            || entry.PendingRelative != null && Exists(storage.Resolve(entry.PendingRelative))) return false;
        // The two roots must still be online and readable before absence is accepted.
        foreach (var root in new[] { desktop, storage.Root })
        {
            VerifiedTransfer.ValidatePath(root);
            using var files = Directory.EnumerateFileSystemEntries(root).GetEnumerator();
            _ = files.MoveNext();
        }
        return true;
    }

    internal void DismissMissing(string id)
    {
        var index = Profile.Entries.FindIndex(e => e.Id == id);
        if (index < 0) throw new IOException("此记录已移除，请重新检查。");
        var entry = Profile.Entries[index];
        if (!IsMissing(entry)) throw new IOException("文件已出现或仍有待处理副本，未移除记录，请重新检查。");
        // Retire membership only. Preserve paths, history and pending metadata; never delete files.
        Profile.Entries.RemoveAt(index); Profile.Ungrouped.Add(entry);
        try { Save(); }
        catch { Profile.Ungrouped.Remove(entry); Profile.Entries.Insert(index, entry); throw; }
        Changed?.Invoke();
    }

    public IReadOnlyList<string> RestoreAll(bool allowMissing = false)
    {
        var issues = new List<string>();
        foreach (var entry in Profile.Entries.ToArray())
        {
            try
            {
                // Exit may leave an externally removed shortcut recorded for later inspection.
                // A conflict, unreadable root or pending copy still requires recovery.
                if (allowMissing && IsMissing(entry)) continue;
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
        if (entry.Phase != "Restored" || !Exists(entry, entry.OriginalPath) || Exists(entry, StoredPath(entry)))
            throw new IOException("归还尚未确认，仍保留分组与恢复记录。");
        Profile.Ungrouped.Add(entry);
        Profile.Entries.Remove(entry);
        Save();
    }

    private void RestoreEntry(StoredShortcut entry)
    {
        if (entry.IsDirectory) { TransferManagedDirectory(entry, restore: true); return; }
        if (entry.IsOrdinaryFile) { TransferOrdinary(entry, restore: true); return; }
        RestoreShortcut(entry);
    }

    private void RestoreShortcut(StoredShortcut entry)
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

    private void TransferManagedDirectory(StoredShortcut entry, bool restore)
    {
        var original = entry.OriginalPath;
        var stored = StoredPath(entry);
        if (File.Exists(original) || File.Exists(stored) || Directory.Exists(original) && Directory.Exists(stored))
            throw new IOException("存在同名项目，原件与收纳内容均已保留。");
        var source = restore ? stored : original;
        var destination = restore ? original : stored;
        if (Directory.Exists(source))
        {
            if (MappedFileOperations.IsAtOrInside(storage.Root, original)) throw new IOException("不能收纳或移动包含栖格数据的目录。");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.Phase = restore ? "Restoring" : "Collecting";
            Save();
            if (MappedFileOperations.SameDrive(source, destination)) MappedFileOperations.MoveDirectoryAtomically(source, destination);
            else new MappedFileOperations(storage).MoveDirectoryAcrossVolumes(source, destination, default, managed: true);
        }
        else DrawerContentSource.AssertUsable(destination, directory: true);
        entry.Phase = restore ? "Restored" : "Stored";
        entry.TransferVerified = true;
        Save();
    }

    private void TransferOrdinary(StoredShortcut entry, bool restore)
    {
        var original = entry.OriginalPath;
        var stored = StoredPath(entry);
        if (Directory.Exists(original) || Directory.Exists(stored)) throw new IOException("目标路径存在同名文件夹，所有内容均已保留。");
        var originalExists = File.Exists(original);
        var storedExists = File.Exists(stored);
        if (originalExists && storedExists)
            throw new IOException("桌面和收纳位置均有副本，两份已保留。请检查并移走其中一份后重试。");
        if (!originalExists && !storedExists) throw new IOException("两处均未找到文件，恢复记录已保留。");
        var source = restore ? stored : original;
        var destination = restore ? original : stored;
        var finalPhase = restore ? "Restored" : "Stored";
        if (!File.Exists(source))
        {
            AssertSourceFile(destination);
            // A pending record may point at an incomplete copy. Never promote it as the only good copy.
            if (entry.Phase is "Collecting" or "Restoring" && (!entry.TransferVerified || VerifiedTransfer.Hash(destination) != entry.Hash))
                throw new IOException("上次搬移未完成校验，剩余副本和恢复记录已保留。");
            entry.Phase = finalPhase;
            Save();
            return;
        }
        AssertSourceFile(source);
        if (entry.Phase == (restore ? "Collecting" : "Restoring") && (!entry.TransferVerified || VerifiedTransfer.Hash(source) != entry.Hash))
            throw new IOException("上次搬移未完成校验，剩余副本和恢复记录已保留。");
        entry.Hash = VerifiedTransfer.Hash(source);
        entry.Phase = restore ? "Restoring" : "Collecting";
        entry.TransferVerified = false;
        Save();
        VerifiedTransfer.Move(source, destination, entry.Hash, () => { entry.TransferVerified = true; Save(); }, ordinary: true);
        entry.Phase = finalPhase;
        Save();
    }

    private void Collect(StoredShortcut entry)
    {
        if (entry.IsDirectory) { TransferManagedDirectory(entry, restore: false); return; }
        if (entry.IsOrdinaryFile) { TransferOrdinary(entry, restore: false); return; }
        CollectShortcut(entry);
    }

    private void CollectShortcut(StoredShortcut entry)
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
    private void ValidateStored(string relative, StoredShortcut entry)
    {
        var full = storage.Resolve(relative);
        var root = storage.Resolve(storeRelative) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !entry.IsDirectory && ShortcutInfo.IsShortcut(full) == entry.IsOrdinaryFile
            || entry.IsOrdinaryFile && !Path.GetFileName(full).Equals(Path.GetFileName(entry.OriginalPath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("收纳记录包含无效路径。");
    }
    private void ValidateOriginal(string path)
    {
        var commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (Path.IsPathFullyQualified(path) && commonDesktop.Length > 0
            && !string.Equals(desktop, commonDesktop, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), commonDesktop, StringComparison.OrdinalIgnoreCase))
            throw new IOException("此项目位于公共桌面，不能直接收纳。可通过抽屉右键“引用外部文件…”加入，或先移到个人桌面后再收纳。");
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), desktop, StringComparison.OrdinalIgnoreCase))
            throw new IOException("只收纳个人桌面根目录中的文件。其他位置可以添加引用。");
        _ = DrawerContentSource.Normalize(path);
        VerifiedTransfer.ValidatePath(path);
    }

    private static void AssertSourceFile(string path)
    {
        if (AssertSource(path)) throw new IOException("需要普通文件，已保留目录。");
    }

    private static bool AssertSource(string path)
    {
        var attributes = DrawerContentSource.AssertUsable(path);
        if (attributes.HasFlag(FileAttributes.Encrypted)) throw new IOException("加密项目暂不收纳。");
        return attributes.HasFlag(FileAttributes.Directory);
    }
}
