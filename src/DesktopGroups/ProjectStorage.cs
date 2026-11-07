using System.IO;
using System.Text.Json;

namespace DesktopGroups;

internal sealed class ProjectStorage
{
    public string Root { get; }
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ProjectStorage(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!File.Exists(Path.Combine(Root, "desktopgroups.root.json")))
            throw new InvalidOperationException("未找到项目根目录标记，拒绝在其他位置写入程序数据。");
    }

    public string Resolve(string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new ArgumentException("只接受项目内相对路径。");
        var candidate = Path.GetFullPath(Path.Combine(Root, relativePath));
        var relative = Path.GetRelativePath(Root, candidate);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(relative))
            throw new ArgumentException("路径超出项目根目录。");
        var current = Root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("数据目录不能通过目录联接或符号链接跳出项目。");
        }
        return candidate;
    }

    public string WriteJson<T>(string relativePath, T value)
    {
        var path = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        // Reject an existing reparse point before opening either destination.
        Resolve(relativePath + ".tmp");
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, JsonOptions);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
        return path;
    }

    public static string LocateRoot(string[] args)
    {
        var index = Array.IndexOf(args, "--root");
        if (index >= 0)
        {
            if (index + 1 >= args.Length) throw new ArgumentException("--root 缺少路径。");
            return args[index + 1];
        }
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "desktopgroups.root.json"))) return directory.FullName;
        throw new InvalidOperationException("无法定位项目目录。请使用项目内 build.ps1 启动。");
    }
}
