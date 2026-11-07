using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopGroups;

internal enum TrayCommand { Show = 1, Locate, Restore, Recollect, Startup, Exit }

internal sealed class ResidentTray : IDisposable
{
    private const uint Callback = 0x8001;
    private readonly HwndSource source;
    private readonly nint icon;
    private readonly Guid id;
    private readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly Action<TrayCommand> command;
    private readonly Func<bool> startupEnabled;
    private bool disposed;
    public bool Available { get; private set; }
    public nint Handle => source.Handle;
    public int RegistrationCount { get; private set; }
    public event Action<string>? EnvironmentChanged;

    public ResidentTray(string root, Action<TrayCommand> command, Func<bool> startupEnabled)
    {
        this.command = command; this.startupEnabled = startupEnabled;
        id = new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root.ToUpperInvariant()))[..16]);
        source = new HwndSource(new HwndSourceParameters("栖格 Qige · 常驻消息")
        {
            WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
            Width = 0, Height = 0
        });
        source.AddHook(Hook);
        icon = CreateTrayIcon();
        WTSRegisterSessionNotification(Handle, 0);
        EnsureAvailable();
    }

    public bool EnsureAvailable()
    {
        if (disposed || Available) return Available;
        var data = Data(); data.Flags = 1 | 2 | 4 | 0x20; // MESSAGE | ICON | TIP | GUID
        Available = Shell_NotifyIcon(0, ref data);
        // A recreated taskbar may already know our GUID even if the add reply
        // was lost. Modifying our own entry safely recovers that state.
        if (!Available) Available = Shell_NotifyIcon(1, ref data);
        if (Available)
        {
            data.Version = 4; Shell_NotifyIcon(4, ref data);
            RegistrationCount++;
        }
        return Available;
    }

    private nint Hook(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if ((uint)message == taskbarCreated)
        {
            Available = false; EnsureAvailable(); EnvironmentChanged?.Invoke("Explorer 重建");
        }
        else if (message is 0x7E or 0x1A or 0x2E0) EnvironmentChanged?.Invoke("显示设置变化");
        else if (message == 0x218 && wParam.ToInt64() is 7 or 18) EnvironmentChanged?.Invoke("休眠唤醒");
        else if (message == 0x2B1 && wParam.ToInt64() == 8) EnvironmentChanged?.Invoke("解锁桌面");
        else if ((uint)message == Callback)
        {
            var action = lParam.ToInt64() & 0xffff;
            if (action is 0x400 or 0x401 or 0x203) Dispatch(TrayCommand.Show);
            else if (action is 0x7B or 0x205) OpenMenu();
            handled = true;
        }
        return 0;
    }

    private void OpenMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            AppendMenu(menu, 0, (nuint)TrayCommand.Show, "打开控制窗口");
            AppendMenu(menu, 0, (nuint)TrayCommand.Locate, "找回分区");
            AppendMenu(menu, 0x800, 0, null);
            AppendMenu(menu, 0, (nuint)TrayCommand.Restore, "全部归还桌面");
            AppendMenu(menu, 0, (nuint)TrayCommand.Recollect, "按分组重新收纳");
            AppendMenu(menu, 0x800, 0, null);
            AppendMenu(menu, startupEnabled() ? 8u : 0u, (nuint)TrayCommand.Startup, "开机启动");
            AppendMenu(menu, 0, (nuint)TrayCommand.Exit, "退出并归还桌面");
            DesktopAttachment.GetCursorPos(out var cursor);
            SetForegroundWindow(Handle);
            var selected = TrackPopupMenuEx(menu, 0x100 | 2, cursor.X, cursor.Y, Handle, 0);
            PostMessage(Handle, 0, 0, 0);
            if (selected != 0) Dispatch((TrayCommand)selected);
        }
        finally { DestroyMenu(menu); }
    }

    private void Dispatch(TrayCommand selected) => source.Dispatcher.BeginInvoke(() => command(selected));

    private NotifyData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyData>(), Window = Handle, Id = 1,
        CallbackMessage = Callback, Icon = icon, Tip = "栖格 Qige · 双击打开，右键管理",
        Info = "", InfoTitle = "", Guid = id
    };

    private static nint CreateTrayIcon()
    {
        var bitmap = new FormatConvertedBitmap(AppBrand.TrayBitmap(), PixelFormats.Pbgra32, null, 0);
        var info = new BitmapInfo { Size = 40, Width = 32, Height = -32, Planes = 1, Bits = 32 };
        var color = CreateDIBSection(0, ref info, 0, out var pixels, 0, 0);
        var mask = CreateBitmap(32, 32, 1, 1, 0);
        try
        {
            if (color == 0 || mask == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            bitmap.CopyPixels(Int32Rect.Empty, pixels, 4096, 128);
            var iconInfo = new IconInfo { Icon = true, Mask = mask, Color = color };
            var result = CreateIconIndirect(ref iconInfo);
            if (result == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            return result;
        }
        finally { if (color != 0) DeleteObject(color); if (mask != 0) DeleteObject(mask); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        var data = Data(); data.Flags = 0x20; Shell_NotifyIcon(2, ref data);
        Available = false;
        WTSUnRegisterSessionNotification(Handle);
        source.RemoveHook(Hook); source.Dispose(); DestroyIcon(icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyData
    {
        public uint Size; public nint Window; public uint Id, Flags, CallbackMessage; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize; public int XPixels, YPixels; public uint Used, Important; }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo
    { [MarshalAs(UnmanagedType.Bool)] public bool Icon; public uint HotX, HotY; public nint Mask, Color; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? title);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint owner, nint parameters);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint CreateIconIndirect(ref IconInfo info);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint pixels, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint CreateBitmap(int width, int height, uint planes, uint bits, nint pixels);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("wtsapi32.dll")] private static extern bool WTSRegisterSessionNotification(nint window, uint flags);
    [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(nint window);
}
