using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace DesktopGroups;

internal sealed record ShortcutInfo(string Key, string Target, string Arguments, string WorkingDirectory)
{
    public static (ShortcutInfo Info, string Hash) ReadStable(string path)
    {
        var before = VerifiedTransfer.Hash(path);
        var info = Read(path);
        if (VerifiedTransfer.Hash(path) != before) throw new IOException("快捷方式正在更新，请稍后重试。");
        return (info, before);
    }

    public static bool IsShortcut(string path) => Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase);

    public static ShortcutInfo Read(string path)
    {
        string target, arguments = "", working = "";
        if (Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            target = File.ReadLines(path).Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))?[4..] ?? "";
            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)) throw new IOException("无法识别此网址快捷方式。");
            target = uri.AbsoluteUri;
        }
        else if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            object? shell = null, shortcut = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
                shortcut = ((dynamic)shell).CreateShortcut(path);
                target = (string)((dynamic)shortcut).TargetPath;
                arguments = (string)((dynamic)shortcut).Arguments;
                working = (string)((dynamic)shortcut).WorkingDirectory;
                if (string.IsNullOrWhiteSpace(target)) throw new IOException("此快捷方式的目标无法可靠识别，暂不收纳。");
                target = Environment.ExpandEnvironmentVariables(target).ToUpperInvariant();
                working = Environment.ExpandEnvironmentVariables(working).ToUpperInvariant();
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.ReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
            }
        }
        else throw new IOException("这个原型先支持 .lnk 和 .url 快捷方式。");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new[] { target, arguments, working }))));
        return new(key, target, arguments, working);
    }

    public static BitmapSource? LoadIcon(string path, string? originalPath = null) => ShortcutIcon.Load(path, originalPath).Bitmap;
}
