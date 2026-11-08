using System.Windows.Media;

namespace DesktopGroups;

// Shared light palette for the manager, desktop drawers and layout overview.
internal static class AppPalette
{
    public static SolidColorBrush Canvas { get; } = Create(0xFFFFFF);
    public static SolidColorBrush Sidebar { get; } = Create(0xF6F6F8);
    public static SolidColorBrush Surface { get; } = Create(0xFFFFFF);
    public static SolidColorBrush Control { get; } = Create(0xF0F0F3);
    public static SolidColorBrush Hover { get; } = Create(0xECECF1);
    public static SolidColorBrush Selected { get; } = Create(0xE6E6ED);
    public static SolidColorBrush Border { get; } = Create(0xD3D3DC);
    public static SolidColorBrush Divider { get; } = Create(0xE9E9EE);
    public static SolidColorBrush Text { get; } = Create(0x202025);
    public static SolidColorBrush Secondary { get; } = Create(0x4B4B56);
    public static SolidColorBrush Muted { get; } = Create(0x656573);
    public static SolidColorBrush Quiet { get; } = Create(0x6F6F7B);
    public static SolidColorBrush Accent { get; } = Create(0x33333C);
    public static SolidColorBrush OnAccent { get; } = Create(0xFFFFFF);
    public static SolidColorBrush Focus { get; } = Create(0x575766);
    public static SolidColorBrush ScrollThumb { get; } = Create(0xB3B3BE);
    public static SolidColorBrush Warning { get; } = Create(0xA33F3F);

    public static SolidColorBrush Translucent(byte alpha, SolidColorBrush brush)
        => new(Color.FromArgb(alpha, brush.Color.R, brush.Color.G, brush.Color.B));

    private static SolidColorBrush Create(uint rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }
}
