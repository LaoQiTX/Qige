using System.Runtime.InteropServices;

namespace DesktopGroups;

internal readonly record struct ScreenArea(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

internal sealed record DesktopMonitor(string Device, ScreenArea Bounds, ScreenArea Work, uint Dpi);
internal sealed record PanelPlacement(int X, int Y, uint Dpi, double Width = 420, double Height = 352);

internal static class DesktopPlacement
{
    public static IReadOnlyList<DesktopMonitor> Capture()
    {
        var monitors = new List<DesktopMonitor>();
        EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref NativeDesktop.Rect rect, nint data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (GetMonitorInfo(monitor, ref info))
            {
                var result = GetDpiForMonitor(monitor, 0, out var dpi, out _);
                monitors.Add(new(info.Device, Area(info.Bounds), Area(info.Work), result == 0 && dpi > 0 ? dpi : 96));
            }
            return true;
        }, 0);
        return monitors;
    }

    // Use one actual monitor work area, not the bounding box of all monitors:
    // the latter contains gaps where a panel could become unreachable.
    public static PanelPlacement Resolve(int x, int y, IReadOnlyList<DesktopMonitor> monitors)
        => Resolve(x, y, 420, 352, monitors);

    public static PanelPlacement Resolve(int x, int y, double widthDip, double heightDip, IReadOnlyList<DesktopMonitor> monitors, bool folded = false)
    {
        if (monitors.Count == 0) throw new InvalidOperationException("正在等待可用显示器。");
        var target = monitors.OrderByDescending(m => Intersection(x, y, widthDip, heightDip, m))
            .ThenBy(m => Distance(x, y, m.Work)).First();
        var scale = target.Dpi / 96d;
        widthDip = Math.Min(widthDip, target.Work.Width / scale);
        heightDip = Math.Min(heightDip, target.Work.Height / scale);
        var width = (int)Math.Round(widthDip * scale);
        var height = (int)Math.Round((folded ? Math.Min(46, heightDip) : heightDip) * scale);
        return new(Math.Clamp(x, target.Work.X, Math.Max(target.Work.X, target.Work.Right - width)),
            Math.Clamp(y, target.Work.Y, Math.Max(target.Work.Y, target.Work.Bottom - height)), target.Dpi, widthDip, heightDip);
    }

    private static long Intersection(int x, int y, double widthDip, double heightDip, DesktopMonitor monitor)
    {
        var width = (int)Math.Round(widthDip * monitor.Dpi / 96d);
        var height = (int)Math.Round(heightDip * monitor.Dpi / 96d);
        return (long)Math.Max(0, Math.Min(x + width, monitor.Work.Right) - Math.Max(x, monitor.Work.X)) *
            Math.Max(0, Math.Min(y + height, monitor.Work.Bottom) - Math.Max(y, monitor.Work.Y));
    }

    private static double Distance(int x, int y, ScreenArea area)
    {
        var dx = x - Math.Clamp(x, area.X, area.Right);
        var dy = y - Math.Clamp(y, area.Y, area.Bottom);
        return (double)dx * dx + (double)dy * dy;
    }
    private static ScreenArea Area(NativeDesktop.Rect rect) => new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    private delegate bool MonitorCallback(nint monitor, nint dc, ref NativeDesktop.Rect rect, nint data);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeDesktop.Rect Bounds, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
}
