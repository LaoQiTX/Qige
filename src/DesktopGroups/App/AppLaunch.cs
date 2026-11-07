using System.Diagnostics;
using System.IO;

namespace DesktopGroups;

internal static class AppLaunch
{
    public static string Executable => Path.Combine(AppContext.BaseDirectory, "DesktopGroups.exe");
    public static ProcessStartInfo StartInfo(AppStorage storage, string mode)
    {
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false };
        info.ArgumentList.Add("--data-dir");
        info.ArgumentList.Add(storage.Root);
        info.ArgumentList.Add(mode);
        return info;
    }
}
