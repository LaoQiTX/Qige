using System.Runtime.InteropServices;

namespace DesktopGroups;

// Only visible during a desktop layout gesture; transparent to input.
internal sealed class SnapGuides : IDisposable
{
    private nint vertical, horizontal;
    public void Show(SnapResult result, ScreenArea work, double scale)
    {
        var thickness = Math.Max(1, (int)Math.Round(scale));
        Line(ref vertical, result.VerticalGuide, true);
        Line(ref horizontal, result.HorizontalGuide, false);
        void Line(ref nint window, double? position, bool isVertical)
        {
            if (!position.HasValue) { if (window != 0) ShowWindow(window, 0); return; }
            if (window == 0)
            {
                window = CreateWindowEx(0x080800A8, "STATIC", "", 0x80000000, 0, 0, 1, 1, 0, 0, 0, 0);
                SetLayeredWindowAttributes(window, 0, 210, 2);
                // SS_WHITERECT gives a consistent thin neutral guide.
                SetWindowLong(window, -16, unchecked((int)0x80000006));
            }
            SetWindowPos(window, (nint)(-1), isVertical ? (int)Math.Round(position.Value) : work.X,
                isVertical ? work.Y : (int)Math.Round(position.Value),
                isVertical ? thickness : work.Width, isVertical ? work.Height : thickness, 0x10 | 0x40);
        }
    }
    public void Hide() { if (vertical != 0) ShowWindow(vertical, 0); if (horizontal != 0) ShowWindow(horizontal, 0); }
    public void Dispose() { if (vertical != 0) DestroyWindow(vertical); if (horizontal != 0) DestroyWindow(horizontal); vertical = horizontal = 0; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint ex, string name, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint data);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint window, uint key, byte alpha, uint flags);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
}
