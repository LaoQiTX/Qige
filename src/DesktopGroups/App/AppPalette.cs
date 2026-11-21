using System.Windows.Media;

namespace DesktopGroups;

// Shared light palette for the manager, desktop drawers and layout overview.
internal static class AppPalette
{
    public static SolidColorBrush Canvas { get; } = Create(0xFFFFFF);
    public static SolidColorBrush Sidebar { get; } = Create(0xF5F7FA);
    public static SolidColorBrush Surface { get; } = Create(0xFFFFFF);
    public static SolidColorBrush Control { get; } = Create(0xEFF3F8);
    public static SolidColorBrush Hover { get; } = Create(0xE6EDF5);
    public static SolidColorBrush Selected { get; } = Create(0xDFEBFA);
    public static SolidColorBrush Border { get; } = Create(0xCBD5E1);
    public static SolidColorBrush Divider { get; } = Create(0xE3EAF2);
    public static SolidColorBrush Text { get; } = Create(0x1D2B3E);
    public static SolidColorBrush Secondary { get; } = Create(0x425369);
    public static SolidColorBrush Muted { get; } = Create(0x53657B);
    public static SolidColorBrush Quiet { get; } = Create(0x5C6E84);
    public static SolidColorBrush Accent { get; } = Create(0x315E8F);
    public static SolidColorBrush OnAccent { get; } = Create(0xFFFFFF);
    public static SolidColorBrush Focus { get; } = Create(0x2C64A0);
    public static SolidColorBrush ScrollThumb { get; } = Create(0xA6B5C7);
    public static SolidColorBrush Pressed { get; } = Create(0xD5E3F3);
    public static SolidColorBrush Disabled { get; } = Create(0xEFF2F6);
    public static SolidColorBrush DisabledText { get; } = Create(0x627085);
    public static SolidColorBrush ErrorSurface { get; } = Create(0xFFF1F0);
    public static SolidColorBrush Warning { get; } = Create(0xA23737);

    public static SolidColorBrush Translucent(byte alpha, SolidColorBrush brush)
        => new(Color.FromArgb(alpha, brush.Color.R, brush.Color.G, brush.Color.B));

    private static SolidColorBrush Create(uint rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }
}
