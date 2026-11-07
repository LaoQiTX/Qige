using System.Runtime.InteropServices;

namespace DesktopGroups;

// Query capabilities before attempting any desktop mutation. An available COM
// interface is only a prerequisite; it does not prove reliable item filtering.
internal static class DesktopVisibilityProbe
{
    private static readonly Guid FolderView2 = new("1AF3A467-214F-4298-908E-06B03E0B39F9");
    private static readonly Guid ShellView = new("000214E3-0000-0000-C000-000000000046");
    private static readonly Guid LegacyFolderView = new("37A378C0-F82D-11CE-AE65-08002B2E1262");

    public static int Run(AppStorage storage)
    {
        var report = ShellDesktop.WithView(view =>
        {
            Marshal.ThrowExceptionForHR(view.ItemCount(2, out var count));
            var results = new[]
            {
                Query(view, "IFolderView2", FolderView2),
                Query(view, "IShellView", ShellView),
                Query(view, "IShellFolderView (legacy)", LegacyFolderView)
            };
            var legacy = results[2].Available;
            return new
            {
                CapturedAt = DateTimeOffset.Now,
                OS = RuntimeInformation.OSDescription,
                DesktopItemCount = count,
                Interfaces = results,
                LegacyRemovalCandidateAvailable = legacy,
                ReliableIndividualHidingVerified = false,
                DesktopModified = false,
                FilesMoved = false,
                FileAttributesChanged = false,
                IconsRepositioned = false,
                NextStep = legacy
                    ? "旧接口可查询，仍需单项隐藏、刷新与恢复测试；不能直接视为可用。"
                    : "当前桌面未暴露旧的单项移除接口。IFolderView2 没有通用的单项隐藏方法；不能据此实现可靠局部隐藏。"
            };
        });
        var path = storage.WriteJson("checks/DesktopVisibility/capabilities.json", report);
        foreach (var result in report.Interfaces)
            Console.WriteLine($"{result.Name}: {(result.Available ? "AVAILABLE" : "UNAVAILABLE")} (0x{result.HResult:X8})");
        Console.WriteLine(report.NextStep);
        Console.WriteLine($"Read-only capability report: {path}");
        return 0;
    }

    private static InterfaceCapability Query(object view, string name, Guid interfaceId)
    {
        var unknown = Marshal.GetIUnknownForObject(view);
        nint result = 0;
        try
        {
            var status = Marshal.QueryInterface(unknown, in interfaceId, out result);
            return new(name, interfaceId, status, status >= 0 && result != 0);
        }
        finally
        {
            if (result != 0) Marshal.Release(result);
            Marshal.Release(unknown);
        }
    }

    private sealed record InterfaceCapability(string Name, Guid InterfaceId, int HResult, bool Available);
}
