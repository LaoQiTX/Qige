using System.IO;
using System.Text.Json;

namespace DesktopGroups;

// Runtime state has one explicit root; it never falls back to AppData or the cwd.
internal sealed class AppStorage
{
    public string Root { get; }
    public string InstallDirectory { get; }
    public string? LegacyRoot { get; }
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AppStorage(string root, string? installDirectory = null, string? legacyRoot = null)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("数据目录必须是绝对路径。");
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        InstallDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory ?? AppContext.BaseDirectory));
        LegacyRoot = legacyRoot == null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyRoot));
        VerifiedTransfer.ValidatePath(Root);
    }

    public static AppStorage FromArguments(string[] args, string? baseDirectory = null)
    {
        string? Value(string flag)
        {
            var indices = args.Select((value, index) => (value, index)).Where(item => item.value == flag).ToArray();
            if (indices.Length == 0) return null;
            if (indices.Length != 1) throw new ArgumentException($"{flag} 不能重复。");
            var index = indices[0].index;
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--") || !Path.IsPathFullyQualified(args[index + 1]))
                throw new ArgumentException($"{flag} 需要一个绝对路径。");
            return args[index + 1];
        }
        var install = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        var data = Value("--data-dir");
        var oldRoot = Value("--root"); // Accept beta.1 startup commands during upgrades.
        if (data != null && oldRoot != null) throw new ArgumentException("--root 与 --data-dir 不能同时使用。");
        var legacy = Value("--migrate-from") ?? oldRoot ?? (data == null ? install : null);
        return new AppStorage(data ?? Path.Combine(oldRoot ?? install, "QigeData"), install, legacy);
    }

    public void EnsureWritable()
    {
        VerifiedTransfer.ValidatePath(Root);
        try
        {
            Directory.CreateDirectory(Root);
            using var probe = new FileStream(Resolve($".write-check-{Guid.NewGuid():N}"), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"无法写入数据目录：{Root}。请将软件完整解压到可写目录；栖格不会改用 C 盘保存数据。", exception);
        }
    }

    public string Resolve(string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':')) throw new ArgumentException("只接受数据目录内的相对路径。");
        var candidate = Path.GetFullPath(Path.Combine(Root, relativePath));
        var relative = Path.GetRelativePath(Root, candidate);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(relative))
            throw new ArgumentException("路径超出数据目录。");
        VerifiedTransfer.ValidatePath(candidate);
        return candidate;
    }

    public string WriteJson<T>(string relativePath, T value)
    {
        var path = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = Resolve(relativePath + ".tmp");
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, JsonOptions);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
        return path;
    }

    public void ConfigureTemporaryDirectory()
    {
        var temp = Resolve("cache/temp");
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("TEMP", temp);
        Environment.SetEnvironmentVariable("TMP", temp);
    }
}
