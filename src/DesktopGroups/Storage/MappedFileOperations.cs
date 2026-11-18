using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DesktopGroups;

internal sealed class MappedFileOperations(AppStorage storage)
{
    internal sealed record TransferReceipt(string Source, string Destination, string? Staging, bool Move, string State,
        string? Holding = null, bool Managed = false);

    public void Transfer(string source, string directory, bool move, CancellationToken cancellation, bool allowManagedCopy = false)
    {
        source = DrawerContentSource.Normalize(source);
        directory = DrawerContentSource.Normalize(directory);
        var sourceAttributes = DrawerContentSource.AssertUsable(source);
        DrawerContentSource.AssertUsable(directory, directory: true);
        var folder = sourceAttributes.HasFlag(FileAttributes.Directory);
        var leaf = Path.GetFileName(source);
        if (leaf.Length == 0) throw new IOException("不能搬移磁盘根目录。");
        var destination = Path.Combine(directory, leaf);
        var copyingManagedItem = allowManagedCopy && !move;
        if (IsAtOrInside(directory, source) || !copyingManagedItem && IsAtOrInside(source, storage.Root) ||
            IsAtOrInside(storage.Root, source) || IsAtOrInside(directory, storage.Root))
            throw new IOException("不能向项目自身或栖格数据目录传输文件。");
        RequireAbsent(destination);
        var sameVolume = SameDrive(source, destination);
        if (move && folder && !sameVolume)
        {
            MoveDirectoryAcrossVolumes(source, destination, cancellation);
            return;
        }
        if (folder || sourceAttributes.HasFlag(FileAttributes.Encrypted)) Inspect(source, cancellation);
        cancellation.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N");
        var staging = move && sameVolume ? null : Path.Combine(directory, $".qige-transfer-{id}.partial");
        var journal = $"recovery/file-transfers/{id}.json";
        var receipt = new TransferReceipt(source, destination, staging, move, "Pending");
        storage.WriteJson(journal, receipt);
        try
        {
            if (staging == null) AtomicRename(source, destination);
            else if (move)
            {
                var hash = VerifiedTransfer.Hash(source);
                VerifiedTransfer.Move(source, staging, hash, () =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    AtomicRename(staging, destination);
                    storage.WriteJson(journal, receipt with { State = "CopyVerified" });
                }, ordinary: true, cancellation: cancellation);
            }
            else
            {
                if (folder) CopyTree(source, staging, cancellation);
                else VerifiedTransfer.Copy(source, staging, ordinary: true, cancellation);
                cancellation.ThrowIfCancellationRequested();
                AtomicRename(staging, destination);
            }
            storage.WriteJson(journal, receipt with { State = "Completed" });
            // Only failed or interrupted transfers need a durable receipt.
            TryRemoveReceipt(journal);
        }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException or OperationCanceledException)
        {
            storage.WriteJson(journal, receipt with { State = error is OperationCanceledException ? "Cancelled" : "NeedsReview" });
            if (staging != null && (File.Exists(staging) || Directory.Exists(staging)))
                throw new IOException($"{error.Message} 原件保留。未提交副本：{staging}", error);
            throw;
        }
    }

    internal void MoveDirectoryAcrossVolumes(string source, string destination, CancellationToken cancellation, bool managed = false)
    {
        DrawerContentSource.AssertUsable(source, directory: true);
        DrawerContentSource.AssertUsable(Path.GetDirectoryName(destination)!, directory: true);
        if (IsAtOrInside(destination, source)) throw new IOException("目标目录位于源目录内。");
        RequireAbsent(destination);
        RecycleBin.AssertAvailable(source);
        var id = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(Path.GetDirectoryName(destination)!, $".qige-transfer-{id}.partial");
        var holdingDirectory = IsAtOrInside(source, storage.Root) ? storage.Root : Path.GetDirectoryName(source)!;
        var holding = Path.Combine(holdingDirectory, $".qige-transfer-{id}.source");
        var journal = $"recovery/file-transfers/{id}.json";
        var receipt = new TransferReceipt(source, destination, staging, true, "Pending", holding, managed);
        storage.WriteJson(journal, receipt);
        var published = false;
        try
        {
            CopyTree(source, staging, cancellation);
            cancellation.ThrowIfCancellationRequested();
            // Close the name at the source before comparing. Other writers can no longer open it
            // through the original path. Windows retains the complete source in its Recycle Bin.
            AtomicRename(source, holding);
            if (!TreesMatch(holding, staging, cancellation)) throw new IOException("复制期间源目录发生变化。");
            cancellation.ThrowIfCancellationRequested();
            AtomicRename(staging, destination);
            published = true;
            storage.WriteJson(journal, receipt with { State = "CopyVerified" });
            RecycleBin.Send(holding);
            storage.WriteJson(journal, receipt with { State = "Completed" });
            TryRemoveReceipt(journal);
        }
        catch (Exception error)
        {
            if (Directory.Exists(holding))
            {
                try { AtomicRename(holding, source); }
                catch (Win32Exception) { /* Source may have been recreated. Keep the separate holding directory. */ }
            }
            storage.WriteJson(journal, receipt with { State = error is OperationCanceledException ? "Cancelled" : "NeedsReview" });
            var preserved = Directory.Exists(holding) ? $"源副本位于 {holding}。" : "请检查源位置。";
            var copy = published ? $"已校验的目标副本位于 {destination}。" : $"暂存位置为 {staging}。";
            throw new IOException($"{error.Message} {preserved}{copy}", error);
        }
    }

    private void TryRemoveReceipt(string journal)
    {
        try { File.Delete(storage.Resolve(journal)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal static bool TreesMatch(string original, string backup, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        DrawerContentSource.AssertUsable(original, directory: true);
        DrawerContentSource.AssertUsable(backup, directory: true);
        var names = Directory.EnumerateFileSystemEntries(original).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.SetEquals(Directory.EnumerateFileSystemEntries(backup).Select(Path.GetFileName))) return false;
        if (!VerifiedTransfer.SameStreams(original, backup)) return false;
        foreach (var name in names)
        {
            cancellation.ThrowIfCancellationRequested();
            var left = Path.Combine(original, name!);
            var right = Path.Combine(backup, name!);
            var leftAttributes = DrawerContentSource.AssertUsable(left);
            var rightAttributes = DrawerContentSource.AssertUsable(right);
            if (leftAttributes.HasFlag(FileAttributes.Directory) != rightAttributes.HasFlag(FileAttributes.Directory)) return false;
            if (leftAttributes.HasFlag(FileAttributes.Directory)) { if (!TreesMatch(left, right, cancellation)) return false; }
            else if (VerifiedTransfer.Hash(left) != VerifiedTransfer.Hash(right) || !VerifiedTransfer.SameStreams(left, right)) return false;
        }
        return true;
    }

    internal static void RecoverInterrupted(AppStorage storage)
    {
        var folder = storage.Resolve("recovery/file-transfers");
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            try
            {
                VerifiedTransfer.ValidatePath(file);
                var receipt = JsonSerializer.Deserialize<TransferReceipt>(File.ReadAllText(file));
                if (receipt is not { State: "Pending", Holding: { } holding } || !Directory.Exists(holding)) continue;
                var source = DrawerContentSource.Normalize(receipt.Source);
                var destination = DrawerContentSource.Normalize(receipt.Destination);
                var sourceParent = Path.GetDirectoryName(source);
                if (sourceParent == null) continue;
                var holdingParent = IsAtOrInside(source, storage.Root) ? storage.Root : sourceParent;
                if (!holding.Equals(Path.Combine(holdingParent, $".qige-transfer-{id}.source"), StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(source) || Directory.Exists(source) || File.Exists(destination) || Directory.Exists(destination)) continue;
                DrawerContentSource.AssertUsable(holding, directory: true);
                DrawerContentSource.AssertUsable(sourceParent, directory: true);
                AtomicRename(holding, source);
                storage.WriteJson($"recovery/file-transfers/{id}.json", receipt with { State = "RolledBack" });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or JsonException)
            { /* Leave all copies and the receipt for inspection. */ }
        }
    }

    internal static IReadOnlyList<TransferReceipt> ReadUnfinished(AppStorage storage)
    {
        var folder = storage.Resolve("recovery/file-transfers");
        if (!Directory.Exists(folder)) return [];
        var pending = new List<TransferReceipt>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            VerifiedTransfer.ValidatePath(file);
            var record = JsonSerializer.Deserialize<TransferReceipt>(File.ReadAllText(file));
            if (record == null || record.State == "Completed") continue;
            if (record.State == "Cancelled" && (record.Staging == null || !File.Exists(record.Staging) && !Directory.Exists(record.Staging))
                && (record.Holding == null || !Directory.Exists(record.Holding))) continue;
            pending.Add(record);
        }
        return pending;
    }

    public static bool SameDrive(string a, string b) => string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

    public static bool IsAtOrInside(string candidate, string root) => candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static void Rename(string source, string name)
    {
        DrawerContentSource.AssertUsable(source);
        var parent = Path.GetDirectoryName(source) ?? throw new IOException("不能重命名磁盘根目录。");
        var destination = Path.Combine(parent, ValidateLeaf(name));
        if (source == destination) return;
        RequireAbsent(destination);
        AtomicRename(source, destination);
    }

    public static void Create(string directory, string name, bool folder)
    {
        DrawerContentSource.AssertUsable(directory, directory: true);
        var path = Path.Combine(directory, ValidateLeaf(name));
        RequireAbsent(path);
        if (folder)
        {
            if (!CreateDirectory(VerifiedTransfer.NativePath(path), 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        else using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
    }

    internal static string PathForNewItem(string directory, string name) => Path.Combine(directory, ValidateLeaf(name));

    private static string ValidateLeaf(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith(".qige-transfer-", StringComparison.OrdinalIgnoreCase) || name.EndsWith('.') || name.EndsWith(' ') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new IOException("请输入有效的文件名或文件夹名。");
        var prefix = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        if (prefix is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || prefix.Length == 4 && (prefix.StartsWith("COM", StringComparison.Ordinal) || prefix.StartsWith("LPT", StringComparison.Ordinal)) && "0123456789¹²³".Contains(prefix[3]))
            throw new IOException("名称是 Windows 保留名称。");
        return name;
    }

    private static void RequireAbsent(string path)
    {
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        throw new IOException($"已存在同名项目，未覆盖。{Path.GetFileName(path)}");
    }

    private static void Inspect(string path, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var attrs = DrawerContentSource.AssertUsable(path);
        if (attrs.HasFlag(FileAttributes.Encrypted)) throw new IOException("暂不传输加密项目。");
        if (attrs.HasFlag(FileAttributes.Directory))
            foreach (var child in Directory.EnumerateFileSystemEntries(path)) Inspect(child, cancellation);
    }

    internal static void MoveDirectoryAtomically(string source, string destination)
    {
        if (!SameDrive(source, destination)) throw new IOException("暂不跨盘搬移目录，原目录已保留。");
        RequireAbsent(destination);
        Inspect(source, default);
        DrawerContentSource.AssertUsable(Path.GetDirectoryName(destination)!, directory: true);
        AtomicRename(source, destination);
    }

    internal static void CopyTree(string source, string target, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        DrawerContentSource.AssertUsable(source, directory: true);
        if (VerifiedTransfer.NamedStreamHashes(source).Count != 0) throw new IOException("暂不复制带备用数据流的目录。");
        if (!CreateDirectory(VerifiedTransfer.NativePath(target), 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        foreach (var child in Directory.EnumerateFileSystemEntries(source))
        {
            cancellation.ThrowIfCancellationRequested();
            var attributes = DrawerContentSource.AssertUsable(child);
            if (attributes.HasFlag(FileAttributes.Encrypted)) throw new IOException("暂不复制加密项目。");
            var destination = Path.Combine(target, Path.GetFileName(child));
            if (attributes.HasFlag(FileAttributes.Directory)) CopyTree(child, destination, cancellation);
            else VerifiedTransfer.Copy(child, destination, ordinary: true, cancellation);
        }
        Directory.SetLastWriteTimeUtc(target, Directory.GetLastWriteTimeUtc(source));
    }

    private static void AtomicRename(string source, string destination)
    {
        if (!MoveFileEx(VerifiedTransfer.NativePath(source), VerifiedTransfer.NativePath(destination), 8))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string destination, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, nint securityAttributes);
}
