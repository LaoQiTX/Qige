using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DesktopGroups;

internal static class DesktopProbe
{
    public static Task<ProbeReport> CaptureAsync()
    {
        var completion = new TaskCompletionSource<ProbeReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(Capture()); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true, Name = "DesktopGroups.ShellProbe" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static ProbeReport Capture()
    {
        var displays = NativeDesktop.InspectDisplays();
        var windows = NativeDesktop.InspectWindows();
        var shell = ShellDesktop.Inspect();
        var relatedProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var name = process.ProcessName;
                    if (name.Contains("pogget", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("wallpaper", StringComparison.OrdinalIgnoreCase)) relatedProcesses.Add(name);
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        return new(DateTimeOffset.UtcNow, RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(), displays, windows, shell,
            relatedProcesses.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            [
                new("显示器与缩放读取", displays.Count > 0 ? "Observed" : "Unavailable",
                    $"当前连接 {displays.Count} 块显示器；报告记录当前模式，不推测设备最高刷新率。"),
                new("桌面窗口结构", windows.Any(item => item.ClassName == "SHELLDLL_DefView") ? "Observed" : "Unavailable",
                    "仅枚举已有窗口；未创建 WorkerW 或更改 Wallpaper 的窗口关系。"),
                new("原生图标接口", shell.Available ? "Observed" : "Unavailable",
                    shell.Available ? $"可读取 IFolderView，桌面包含 {shell.ItemCount} 个项目。" : shell.Error ?? "接口不可用。"),
                new("图标分组与位置恢复", "NotVerified", "需要下一阶段验证图标定位、布局记录及正常退出恢复。"),
                new("分区折叠", "NotVerified", "保留原文件时，单组图标折叠需要单独验证；不能用移动文件代替。"),
                new("Win+D 与 Explorer 恢复", "NotVerified", "尚未进行交互测试，不能据窗口枚举结果认定通过。"),
                new("Pogget 与 Wallpaper 共存", "NotVerified", "检测相关进程只证明它们正在运行，尚未验证兼容性。")
            ]);
    }
}

internal sealed record ProbeCheck(string Name, string Status, string Detail);
internal sealed record ProbeReport(DateTimeOffset CapturedAtUtc, string OperatingSystem, string Architecture,
    IReadOnlyList<DisplayInfo> Displays, IReadOnlyList<DesktopWindowInfo> DesktopWindows,
    ShellViewInfo ShellView, IReadOnlyList<string> RelatedProcesses, IReadOnlyList<ProbeCheck> Checks);
