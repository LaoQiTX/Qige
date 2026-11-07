using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace DesktopGroups;

// Shortcuts can cross volumes. Keep the exact source file locked until the
// destination is flushed and verified; delete through that same file handle.
internal static class VerifiedTransfer
{
    public static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static void Move(string source, string destination, string expectedHash, Action? destinationVerified = null)
    {
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("目标位置已存在内容，未覆盖任何文件。");
        ValidatePath(source);
        ValidatePath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var handle = OpenForTransfer(source);
        using var input = new FileStream(handle, FileAccess.Read);
        if (input.Length > 4 * 1024 * 1024) throw new IOException("此原型仅收纳不超过 4 MB 的快捷方式。");
        if (Convert.ToHexString(SHA256.HashData(input)) != expectedHash)
            throw new IOException("源文件刚刚发生变化，本次搬移已停止。");
        input.Position = 0;
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        }
        CopyStreams(source, destination);
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
        if (Hash(destination) != expectedHash) throw new IOException("复制校验未通过，源文件已保留。");
        destinationVerified?.Invoke();
        MarkDeleted(handle);
    }

    public static void RemoveDuplicate(string path, string expectedHash)
    {
        ValidatePath(path);
        using var handle = OpenForTransfer(path);
        using var input = new FileStream(handle, FileAccess.Read);
        if (Convert.ToHexString(SHA256.HashData(input)) != expectedHash)
            throw new IOException("文件内容已改变，未清理重复副本。");
        MarkDeleted(handle);
    }

    public static void ValidatePath(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("原型暂不搬移经过符号链接或目录联接的文件。");
    }

    private static SafeFileHandle OpenForTransfer(string path)
    {
        var handle = CreateFile(path, 0x80010000, 1, 0, 3, 0x08000000, 0); // READ | DELETE; share read
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "文件被占用或没有搬移权限，原文件未动。");
        }
        return handle;
    }

    private static void MarkDeleted(SafeFileHandle handle)
    {
        var disposition = new FileDisposition { Delete = true };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<FileDisposition>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "副本已保存，但源文件未能移除；恢复记录已保留。");
    }

    // Preserve named streams, including Windows' download origin marker.
    private static void CopyStreams(string source, string destination)
    {
        var search = FindFirstStream(source, 0, out var stream, 0);
        if (search == -1)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 38 or 1 or 87) return; // no streams / filesystem does not support streams
            throw new Win32Exception(error);
        }
        try
        {
            do
            {
                if (stream.Name == "::$DATA") continue;
                if (!stream.Name.StartsWith(':') || !stream.Name.EndsWith(":$DATA") ||
                    stream.Name.IndexOfAny(['\\', '/']) >= 0)
                    throw new IOException("备用数据流名称不受支持，源文件已保留。");
                using var input = new FileStream(source + stream.Name, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                using var output = new FileStream(destination + stream.Name, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            } while (FindNextStream(search, out stream));
            var lastError = Marshal.GetLastWin32Error();
            if (lastError != 38) throw new Win32Exception(lastError);
        }
        finally { FindClose(search); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct FileDisposition { [MarshalAs(UnmanagedType.Bool)] public bool Delete; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamData { public long Size; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, ref FileDisposition information, uint size);
    [DllImport("kernel32.dll", EntryPoint = "FindFirstStreamW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint FindFirstStream(string path, int level, out StreamData data, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "FindNextStreamW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool FindNextStream(nint search, out StreamData data);
    [DllImport("kernel32.dll")] private static extern bool FindClose(nint search);
}
