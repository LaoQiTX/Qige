using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopGroups;

// Frozen resources are reused across redraws. Only the backdrop receives the opacity setting.
internal sealed class DrawerAppearance
{
    public const int DefaultTransparency = 12;
    public const int MaxTransparency = 80;
    internal const double Radius = 10;
    private static readonly Lazy<Brush> grain = new(CreateGrain);
    private static readonly Dictionary<ThemeVariant, DrawerAppearance> variants = [];
    internal static DrawerAppearance Current { get; private set; } = For(new(AppTheme.Mica, false));
    private readonly Dictionary<int, Brush> surfaces = [];
    private readonly Dictionary<(int Transparency, int Depth), Brush> menuSurfaces = [];
    private readonly uint top, bottom;
    private readonly Brush glassTint, strongGlassTint;
    private readonly bool acrylic, dark;
    internal static bool GlassEnabled { get; set; } = AppPalette.TransparencyEnabled();
    private readonly uint[] layers;
    internal SolidColorBrush Edge { get; }
    internal SolidColorBrush InnerEdge { get; }
    internal SolidColorBrush Divider { get; }
    internal SolidColorBrush Text { get; }
    internal SolidColorBrush Secondary { get; }
    internal SolidColorBrush Muted { get; }
    internal SolidColorBrush Hover { get; }
    internal SolidColorBrush HoverEdge { get; }
    internal SolidColorBrush Selected { get; }
    internal SolidColorBrush SelectedEdge { get; }
    internal SolidColorBrush Accent { get; }
    internal SolidColorBrush Scroll { get; }
    internal SolidColorBrush Placeholder { get; }

    private DrawerAppearance(ThemeVariant variant)
    {
        acrylic = variant.Theme == AppTheme.Acrylic;
        dark = variant.Dark;
        uint Pick(uint micaLight, uint micaDark, uint acrylicLight, uint acrylicDark) =>
            acrylic ? (dark ? acrylicDark : acrylicLight) : (dark ? micaDark : micaLight);
        top = Pick(0xFAFCFF, 0x2B323B, 0xF9F7F0, 0x383633);
        bottom = Pick(0xEDF2F8, 0x1C222A, 0xEAE6DD, 0x262523);
        glassTint = AppPalette.Gradient(top, bottom, acrylic ? (dark ? (byte)179 : (byte)164) : (byte)232);
        strongGlassTint = acrylic ? AppPalette.Gradient(top, bottom, 204) : glassTint;
        layers = (acrylic, dark) switch
        {
            (true, true) => [0x33312E, 0x302E2A, 0x2B2926, 0x282623],
            (true, false) => [0xF8F6F0, 0xF1EFE8, 0xEBE8E0, 0xE6E3DB],
            (false, true) => [0x272E38, 0x232A32, 0x1F252D, 0x1B2129],
            _ => [0xFCFDFE, 0xF3F6FA, 0xEBF0F7, 0xE5ECF5]
        };
        Edge = AppPalette.Solid(Pick(0x8796AA, 0x7890A5, 0x82796A, 0x918D84), 125);
        InnerEdge = AppPalette.Solid(dark ? 0xA9C8DDu : 0xFFFFFFu, dark ? (byte)(acrylic ? 73 : 37) : (byte)(acrylic ? 220 : 155));
        Divider = AppPalette.Solid(dark ? 0xB6CBDEu : 0x61758Eu, 38);
        Text = AppPalette.Solid(Pick(0x263244, 0xF0F4F9, 0x242320, 0xF4F2ED));
        Secondary = AppPalette.Solid(Pick(0x596B81, 0xCBD5E1, 0x4D4942, 0xDEDBD4));
        Muted = AppPalette.Solid(Pick(0x64758A, 0xB4C1CF, 0x615C54, 0xC6C2BA));
        Hover = AppPalette.Solid(Pick(0xFFFFFF, 0x465464, 0xFFFFFF, 0x514E47), 172);
        HoverEdge = AppPalette.Solid(dark ? 0x7896ACu : 0xFFFFFFu, 140);
        Selected = AppPalette.Solid(Pick(0xDCE9F8, 0x304860, 0xDFE9F4, 0x344654), 235);
        SelectedEdge = AppPalette.Solid(Pick(0x85A6CF, 0x88AACD, 0x78AAC9, 0x9CCCE8), 210);
        Accent = AppPalette.Solid(Pick(0x527CAB, 0x9BC5F0, 0x235F97, 0x9BCFF9));
        Scroll = AppPalette.Solid(dark ? 0xB1CCE1u : 0x62798Fu, 140);
        Placeholder = AppPalette.Solid(Pick(0xD7E1EC, 0x536375, 0xDFDAD1, 0x554F46));
    }

    internal static DrawerAppearance For(ThemeVariant variant)
    {
        if (!variants.TryGetValue(variant, out var appearance)) variants[variant] = appearance = new(variant);
        return appearance;
    }

    internal static void Apply(ThemeVariant variant) => Current = For(variant);
    public static int NormalizeTransparency(int value) => Math.Clamp(value, 0, MaxTransparency);

    internal bool UsesWallpaper(int transparency) => GlassEnabled && transparency > 0;

    internal void DrawSurface(DrawingContext drawing, Rect bounds, Rect screen, int transparency)
    {
        if (UsesWallpaper(transparency))
        {
            var group = new DrawingGroup { Opacity = Opacity(transparency, 0) / 255d,
                ClipGeometry = new RectangleGeometry(bounds, Radius, Radius) };
            bool painted;
            using (var layer = group.Open())
            {
                painted = DesktopGlass.Draw(layer, new Rect(0, 0, bounds.Width + 1, bounds.Height + 1), screen, out var brightness);
                if (painted)
                {
                    layer.DrawRectangle(dark ? (brightness > .55 ? strongGlassTint : glassTint)
                        : (brightness < .38 ? strongGlassTint : glassTint), null, bounds);
                    if (acrylic) layer.DrawRectangle(grain.Value, null, bounds);
                }
            }
            if (painted) { drawing.DrawDrawing(group); return; }
        }
        drawing.DrawRoundedRectangle(Surface(!GlassEnabled ? 0 : transparency), null, bounds, Radius, Radius);
    }

    internal Brush Surface(int transparency)
    {
        transparency = NormalizeTransparency(transparency);
        if (!surfaces.TryGetValue(transparency, out var brush))
            surfaces[transparency] = brush = AppPalette.Gradient(top, bottom, Opacity(transparency, 0));
        return brush;
    }

    internal Brush MenuSurface(int transparency, int depth)
    {
        transparency = NormalizeTransparency(transparency);
        if (!GlassEnabled) transparency = 0;
        depth = Math.Clamp(depth, 1, layers.Length);
        if (!menuSurfaces.TryGetValue((transparency, depth), out var brush))
            menuSurfaces[(transparency, depth)] = brush = AppPalette.Solid(layers[depth - 1], Opacity(transparency, depth));
        return brush;
    }

    private static Brush CreateGrain()
    {
        const int size = 64;
        var pixels = new byte[size * size * 4];
        var random = new Random(619);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var shade = random.Next(-4, 5);
            pixels[i + 3] = (byte)Math.Abs(shade);
            if (shade > 0) pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)shade;
        }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap) { TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, size, size) };
        brush.Freeze();
        return brush;
    }

    private static byte Opacity(int transparency, int depth) =>
        (byte)Math.Ceiling(255 * (1 - transparency / 100d * Math.Pow(.5, depth)));
}