using System.IO;

namespace DesktopGroups;

internal enum DrawerContentKind { Managed, Reference, Mapped }
internal enum DrawerSort { Manual, Name, Type, Modified, Size }

internal sealed class DrawerReference
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = "";
}

internal sealed record ExternalContent(string Id, string Path, DrawerContentKind Kind, bool IsDirectory, bool Available,
    DateTime Modified, long Size)
{
    public string Name => DrawerContentSource.DisplayName(Path, IsDirectory);
    public string Type => IsDirectory ? "文件夹" : System.IO.Path.GetExtension(Path);
    public string IconKey => $"{Id}:{Path}:{Modified.Ticks}:{Size}:{IsDirectory}";
}

internal static class DrawerContentSource
{
    private const FileAttributes UnsupportedAttributes = FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.Device;

    public static string DisplayName(string path, bool isDirectory)
    {
        var name = Path.GetFileName(path);
        if (name.Length == 0) return path;
        if (!isDirectory && name.Length > 4 && name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            return name[..^4];
        return name;
    }

    public static string Normalize(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[3..].Contains(':'))
            throw new IOException("请选择本地磁盘上的普通文件或文件夹。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static FileAttributes AssertUsable(string path, bool directory = false)
    {
        path = Normalize(path);
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new IOException("此版本支持本地固定磁盘和可移动磁盘。");
        var root = Path.GetPathRoot(path)!;
        foreach (var part in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar))
        {
            if (part == ".") continue;
            root = System.IO.Path.Combine(root, part);
            if ((File.GetAttributes(root) & UnsupportedAttributes) != 0)
                throw new IOException("暂不支持符号链接、目录联接或离线文件。");
        }
        var attributes = File.GetAttributes(path);
        if (directory && !attributes.HasFlag(FileAttributes.Directory)) throw new IOException("请选择文件夹。");

        return attributes;
    }

    public static ExternalContent[] ScanMapping(string path)
    {
        AssertUsable(path, directory: true);
        var children = new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false });
        return children.Where(item => (item.Attributes & UnsupportedAttributes) == 0 && !item.Name.StartsWith(".qige-transfer-", StringComparison.OrdinalIgnoreCase))
            .Select(item => Describe(item.FullName, item.FullName, DrawerContentKind.Mapped)).Where(item => item.Available).OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static ExternalContent Describe(string id, string path, DrawerContentKind kind)
    {
        try
        {
            var attributes = AssertUsable(path);
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            return new(id, path, kind, isDirectory, true, File.GetLastWriteTimeUtc(path), isDirectory ? 0 : new FileInfo(path).Length);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(id, path, kind, false, false, default, 0);
        }
    }
}
