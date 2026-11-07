using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace DesktopGroups;

internal static class AppBrand
{
    public const string Name = "栖格 Qige";
    public const string Version = "0.1.0-beta.1";
    public static Uri IconUri => new("pack://application:,,,/DesktopGroups;component/Assets/qige.ico");

    public static BitmapSource TrayBitmap()
    {
        var lightTaskbar = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            lightTaskbar = key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
        }
        catch (System.Security.SecurityException) { }
        var variant = lightTaskbar ? "dark" : "light";
        var decoder = new IconBitmapDecoder(new Uri($"pack://application:,,,/DesktopGroups;component/Assets/qige-tray-{variant}.ico"),
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bitmap = decoder.Frames.First(frame => frame.PixelWidth == 32);
        bitmap.Freeze();
        return bitmap;
    }
}
