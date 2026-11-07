using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopGroups;

internal static class NativeDesktop
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPixel, Width, Height, Flags, Frequency;
        public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    private delegate bool EnumWindowCallback(nint window, nint parameter);
    private delegate bool EnumMonitorCallback(nint monitor, nint dc, ref Rect rect, nint parameter);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int maximum);
    [DllImport("user32.dll")] internal static extern nint GetParent(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, EnumMonitorCallback callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int mode, ref DisplayMode settings);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    public static string ClassName(nint window)
    {
        var buffer = new StringBuilder(256);
        GetClassName(window, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    public static IReadOnlyList<DesktopWindowInfo> InspectWindows()
    {
        var result = new List<DesktopWindowInfo>();
        var visited = new HashSet<nint>();
        var interesting = new HashSet<string>(StringComparer.Ordinal)
            { "Progman", "WorkerW", "SHELLDLL_DefView", "SysListView32" };
        void Inspect(nint window)
        {
            if (!visited.Add(window)) return;
            var className = ClassName(window);
            if (!interesting.Contains(className)) return;
            GetWindowThreadProcessId(window, out var processId);
            string process;
            try { using var item = Process.GetProcessById((int)processId); process = item.ProcessName; }
            catch { process = "unavailable"; }
            GetWindowRect(window, out var rect);
            result.Add(new(window.ToInt64(), GetParent(window).ToInt64(), className, process,
                IsWindowVisible(window), rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
        }
        EnumWindows((window, _) =>
        {
            Inspect(window);
            if (ClassName(window) is "Progman" or "WorkerW")
                EnumChildWindows(window, (child, _) => { Inspect(child); return true; }, 0);
            return true;
        }, 0);
        return result;
    }

    public static IReadOnlyList<DisplayInfo> InspectDisplays()
    {
        var result = new List<DisplayInfo>();
        EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref Rect rect, nint parameter) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (!GetMonitorInfo(monitor, ref info)) return true;
            uint dpiX = 0, dpiY = 0;
            var dpiResult = GetDpiForMonitor(monitor, 0, out dpiX, out dpiY);
            var mode = new DisplayMode { Size = (ushort)Marshal.SizeOf<DisplayMode>(), Device = "", FormName = "" };
            var modeAvailable = EnumDisplaySettings(info.Device, -1, ref mode);
            result.Add(new(info.Device, (info.Flags & 1) != 0,
                info.Monitor.Left, info.Monitor.Top,
                info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top,
                dpiResult == 0 ? dpiX : null, dpiResult == 0 ? Math.Round(dpiX / 96.0, 2) : null,
                modeAvailable && mode.Frequency > 1 ? mode.Frequency : null));
            return true;
        }, 0);
        return result;
    }
}

internal sealed record DesktopWindowInfo(long Handle, long Parent, string ClassName, string Process,
    bool Visible, int X, int Y, int Width, int Height);
internal sealed record DisplayInfo(string Device, bool Primary, int X, int Y, int Width, int Height,
    uint? Dpi, double? Scale, uint? RefreshHz);
