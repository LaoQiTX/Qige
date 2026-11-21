using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DesktopGroups;

internal enum AppTheme { Mica = 0, Acrylic = 1 }
internal enum ColorMode { System, Light, Dark }
internal readonly record struct ThemeVariant(AppTheme Theme, bool Dark)
{
    internal static ThemeVariant Resolve(AppTheme theme, ColorMode mode, bool systemDark) =>
        new(theme, mode == ColorMode.Dark || (mode == ColorMode.System && systemDark));
}

internal static class AppPalette
{
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static readonly Dictionary<string, Brush> fallback = Create(new(AppTheme.Mica, false));
    private static SolidColorBrush GetBrush(string name) =>
        (Application.Current?.TryFindResource("Palette" + name) as SolidColorBrush) ?? (SolidColorBrush)fallback[name];
    internal static SolidColorBrush Sidebar => GetBrush(nameof(Sidebar));
    internal static SolidColorBrush Surface => GetBrush(nameof(Surface));
    internal static SolidColorBrush Control => GetBrush(nameof(Control));
    internal static SolidColorBrush Selected => GetBrush(nameof(Selected));
    internal static SolidColorBrush Border => GetBrush(nameof(Border));
    internal static SolidColorBrush Text => GetBrush(nameof(Text));
    internal static SolidColorBrush Focus => GetBrush(nameof(Focus));
    internal static SolidColorBrush ScrollThumb => GetBrush(nameof(ScrollThumb));

    internal static bool IsThemeMessage(int message) => message is 0x001A or 0x031A; // WM_SETTINGCHANGE, WM_THEMECHANGED

    internal static bool TransparencyEnabled()
    {
        if (SystemParameters.HighContrast) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Personalize);
            return key?.GetValue("EnableTransparency") is not 0;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }

    internal static bool ReadSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Personalize);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }

    internal static ResourceDictionary Apply(ThemeVariant variant, ResourceDictionary? previous)
    {
        var resources = new ResourceDictionary();
        foreach (var (name, brush) in Create(variant)) resources.Add("Palette" + name, brush);
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        if (previous is not null && dictionaries.IndexOf(previous) is var index && index >= 0)
            dictionaries[index] = resources;
        else dictionaries.Add(resources);
        return resources;
    }

    private static Dictionary<string, Brush> Create(ThemeVariant variant)
    {
        var acrylic = variant.Theme == AppTheme.Acrylic;
        var dark = variant.Dark;
        uint Pick(uint micaLight, uint micaDark, uint acrylicLight, uint acrylicDark) =>
            acrylic ? (dark ? acrylicDark : acrylicLight) : (dark ? micaDark : micaLight);
        SolidColorBrush Ink(uint micaLight, uint micaDark, uint acrylicLight, uint acrylicDark) =>
            Solid(Pick(micaLight, micaDark, acrylicLight, acrylicDark));
        var canvas = Ink(0xFFFFFF, 0x191D23, 0xEFEDE7, 0x232221);
        return new()
        {

            ["WindowSurface"] = acrylic
                ? Gradient(dark ? 0x32312Fu : 0xFBFAF6u, dark ? 0x232221u : 0xE9E6DFu)
                : canvas,
            ["CanvasBackdrop"] = Brushes.Transparent,
            ["Sidebar"] = Ink(0xF5F7FA, 0x20252D, 0xEBE8E1, 0x292826),
            ["Surface"] = Ink(0xFFFFFF, 0x252B33, 0xF7F5EF, 0x302F2D),
            ["Control"] = Ink(0xEFF3F8, 0x303741, 0xF0EEE8, 0x3A3936),
            ["Hover"] = Ink(0xE6EDF5, 0x3B4654, 0xE6E3DD, 0x474542),
            ["Selected"] = Ink(0xDFEBFA, 0x304860, 0xDFE9F4, 0x344654),

            ["Border"] = Ink(0xCBD5E1, 0x535F6E, 0xC9C5BC, 0x66625C),
            ["Divider"] = Ink(0xE3EAF2, 0x39434F, 0xDFDCD4, 0x474540),
            ["Text"] = Ink(0x1D2B3E, 0xF0F4F9, 0x242320, 0xF4F2ED),
            ["Secondary"] = Ink(0x425369, 0xCBD5E1, 0x4D4942, 0xDEDBD4),
            ["Muted"] = Ink(0x53657B, 0xB4C1CF, 0x615C54, 0xC6C2BA),
            ["Quiet"] = Ink(0x5C6E84, 0xA9B7C7, 0x625E55, 0xC5C0B6),
            ["Accent"] = Ink(0x315E8F, 0x9BC5F0, 0x235F97, 0x9BCFF9),
            ["OnAccent"] = Ink(0xFFFFFF, 0x172433, 0xFFFFFF, 0x142638),
            ["Focus"] = Ink(0x2C64A0, 0xB3D6FA, 0x266399, 0xAFD8FF),
            ["ScrollThumb"] = Ink(0xA6B5C7, 0x66798D, 0xA39E94, 0x77726B),
            ["Disabled"] = Ink(0xEFF2F6, 0x2A3038, 0xE8E5DF, 0x33312E),
            ["DisabledText"] = Ink(0x627085, 0x94A1B0, 0x756F64, 0xA39E95),
            ["ErrorSurface"] = Ink(0xFFF1F0, 0x40292D, 0xFFF2F0, 0x422D34),
            ["Warning"] = Ink(0xA23737, 0xFFB2AC, 0xA2343A, 0xFFB7B4),
            ["Scrim"] = Solid(dark ? 0x080C13u : 0x0F1D38u, dark ? (byte)144 : (byte)64)
        };
    }

    internal static SolidColorBrush Solid(uint rgb, byte alpha = 255)
    {
        var brush = new SolidColorBrush(Argb(rgb, alpha));
        brush.Freeze();
        return brush;
    }

    internal static LinearGradientBrush Gradient(uint top, uint bottom, byte alpha = 255)
    {
        var brush = new LinearGradientBrush(Argb(top, alpha), Argb(bottom, alpha), new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }

    private static Color Argb(uint rgb, byte alpha) => Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}