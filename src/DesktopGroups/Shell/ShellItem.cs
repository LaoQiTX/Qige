using System.Diagnostics;
using System.IO;

namespace DesktopGroups;

internal static class ShellItem
{
    internal static void ValidateOpen(string path)
    {
        if (Directory.Exists(path)) return;
        if (!File.Exists(path)) throw new IOException("文件或文件夹暂不可用，请检查是否已移动、删除或设备离线。");
        if (!Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return;

        var target = Environment.ExpandEnvironmentVariables(NativeShortcut.Read(path).Target);
        if (!Path.IsPathFullyQualified(target)) return;
        if (!File.Exists(target) && !Directory.Exists(target))
            throw new IOException($"快捷方式目标已失效：{target}。请检查文件夹、磁盘或网络连接，快捷方式已保留。");
    }

    internal static void Open(string path)
    {
        ValidateOpen(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
