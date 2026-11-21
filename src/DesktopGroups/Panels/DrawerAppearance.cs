using System.Windows;
using System.Windows.Media;

namespace DesktopGroups;

// Drawer-only resources keep the desktop surface independent of manager styling.
// Frozen brushes are shared by redraws; the translucent body keeps opaque icons.
internal static class DrawerAppearance
{
    public const int DefaultTransparency = 12;
    public const int MaxTransparency = 80;
    private static readonly Dictionary<int, Brush> surfaces = new();
    public static int NormalizeTransparency(int value) => Math.Clamp(value, 0, MaxTransparency);
    public static Brush Surface(int transparency)
    {
        transparency = NormalizeTransparency(transparency);
        if (!surfaces.TryGetValue(transparency, out var brush)) surfaces[transparency] = brush = MakeSurface(transparency);
        return brush;
    }

    public static Brush MenuSurface(int transparency, int depth)
    {
        depth = Math.Clamp(depth, 1, 4);
        var alpha = (byte)Math.Ceiling(255 * (1 - NormalizeTransparency(transparency) / 100d * Math.Pow(.5, depth)));
        return Brush(depth switch { 1 => 0xFCFDFE, 2 => 0xF3F6FA, 3 => 0xEBF0F7, _ => 0xE5ECF5 }, alpha);
    }
    public static SolidColorBrush Edge { get; } = Brush(0x8796AA, 100);
    public static SolidColorBrush InnerEdge { get; } = Brush(0xFFFFFF, 155);
    public static SolidColorBrush Divider { get; } = Brush(0x61758E, 28);
    public static SolidColorBrush Text { get; } = Brush(0x263244);
    public static SolidColorBrush Secondary { get; } = Brush(0x596B81);
    public static SolidColorBrush Muted { get; } = Brush(0x6C7D92);
    public static SolidColorBrush Hover { get; } = Brush(0xFFFFFF, 145);
    public static SolidColorBrush HoverEdge { get; } = Brush(0xFFFFFF, 205);
    public static SolidColorBrush Selected { get; } = Brush(0xDCE9F8, 225);
    public static SolidColorBrush SelectedEdge { get; } = Brush(0x85A6CF, 195);
    public static SolidColorBrush Accent { get; } = Brush(0x527CAB);
    public static SolidColorBrush Scroll { get; } = Brush(0x71859D, 110);
    public static SolidColorBrush Placeholder { get; } = Brush(0xD7E1EC);

    private static SolidColorBrush Brush(uint rgb, byte alpha = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }

    private static Brush MakeSurface(int transparency)
    {
        var alpha = (byte)Math.Ceiling(255 * (1 - transparency / 100d));
        var brush = new LinearGradientBrush(
            Color.FromArgb(alpha, 250, 252, 255), Color.FromArgb(alpha, 237, 242, 248),
            new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }
}
