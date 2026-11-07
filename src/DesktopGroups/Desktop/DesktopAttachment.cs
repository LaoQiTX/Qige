using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DesktopGroups;

internal static class DesktopAttachment
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out NativeDesktop.Point point);
    [DllImport("user32.dll")] internal static extern bool ScreenToClient(nint window, ref NativeDesktop.Point point);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(nint destination, nint first, nint second, int operation);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeDesktop.Rect rect);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(nint window, nint update, nint region, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint BeginDeferWindowPos(int count);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint DeferWindowPos(nint batch, nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EndDeferWindowPos(nint batch);

    public static uint Dpi(nint window) => GetDpiForWindow(window) is > 0 and var dpi ? dpi : 96;

    public static nint FindIconHost()
    {
        var hosts = NativeDesktop.InspectWindows().Where(item =>
            item.ClassName == "SHELLDLL_DefView" && item.Process.Equals("explorer", StringComparison.OrdinalIgnoreCase) && item.Visible).ToArray();
        if (hosts.Length != 1) throw new InvalidOperationException("未找到唯一的 Explorer 桌面图标宿主，暂不附着测试窗口。");
        return (nint)hosts[0].Handle;
    }

    public static void Place(nint window, int x, int y, int width, int height)
    {
        // HWND_TOP is scoped to siblings under Explorer, not a system TOPMOST window.
        // Keeping SWP_NOZORDER here leaves the frame underneath the full-size ListView.
        if (!SetWindowPos(window, 0, x, y, width, height, 0x10 | 0x20 | 0x40))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static void Redraw(nint window) => RedrawWindow(window, 0, 0, 0x01 | 0x04 | 0x80 | 0x100 | 0x400);

    public static void MoveTogether(nint backdrop, nint frame, int x, int y)
    {
        // Keep the compositor's cached layered surfaces. Do not erase, resize,
        // change their regions, or force synchronous painting on mouse movement.
        const uint flags = 0x01 | 0x04 | 0x10; // NOSIZE | NOZORDER | NOACTIVATE
        var batch = BeginDeferWindowPos(2);
        if (batch == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建窗口移动批次。");
        batch = DeferWindowPos(batch, backdrop, 0, x, y, 0, 0, flags);
        if (batch == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加入背景移动。");
        batch = DeferWindowPos(batch, frame, 0, x, y, 0, 0, flags);
        if (batch == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加入边框移动。");
        if (!EndDeferWindowPos(batch)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法提交窗口移动批次。");
    }

    public static GroupWindowState ReadState(nint window)
    {
        GetWindowRect(window, out var rect);
        var siblingAbove = GetWindow(window, 3); // GW_HWNDPREV
        return new(window.ToInt64(), NativeDesktop.GetParent(window).ToInt64(), IsWindowVisible(window),
            rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
            siblingAbove == 0 ? null : NativeDesktop.ClassName(siblingAbove));
    }

    public static void Hollow(nint window, int width, int height, int headerHeight, int border)
    {
        var outer = CreateRectRgn(0, 0, width, height);
        var inner = CreateRectRgn(border, headerHeight, width - border, height - border);
        if (outer == 0 || inner == 0)
        {
            if (outer != 0) DeleteObject(outer);
            if (inner != 0) DeleteObject(inner);
            throw new InvalidOperationException("无法创建测试分区的穿透区域。");
        }
        try
        {
            if (CombineRgn(outer, outer, inner, 4) == 0 || SetWindowRgn(window, outer, true) == 0)
                throw new InvalidOperationException("无法设置测试分区的穿透区域。");
            outer = 0; // Windows owns the region after a successful SetWindowRgn.
        }
        finally
        {
            DeleteObject(inner);
            if (outer != 0) DeleteObject(outer);
        }
    }
}

internal sealed record GroupWindowState(long Handle, long Parent, bool Visible,
    int X, int Y, int Width, int Height, string? SiblingAbove);
