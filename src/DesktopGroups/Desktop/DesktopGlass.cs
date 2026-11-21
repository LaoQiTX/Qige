using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopGroups;

// Desktop icons and windows are never captured. Only the configured wallpaper
// is decoded, blurred once at a small size and shared by every drawer.
internal static class DesktopGlass
{
    private sealed record Backdrop(ScreenArea Monitor, Rect ImageBounds, BitmapSource Image, byte[] Pixels, bool Tile);
    private static List<Backdrop>? cache;
    private static SolidColorBrush? background;

    internal static void Invalidate() => cache = null;

    internal static bool Draw(DrawingContext drawing, Rect panel, Rect screen, out double brightness)
    {
        var backdrops = cache ??= Load();
        brightness = .5;
        if (backdrops.Count == 0) return false;
        var painted = false;
        foreach (var backdrop in backdrops)
        {
            var monitor = backdrop.Monitor;
            var intersection = Rect.Intersect(screen, new Rect(monitor.X, monitor.Y, monitor.Width, monitor.Height));
            if (intersection.IsEmpty || intersection.Width <= 0 || intersection.Height <= 0) continue;
            var scale = panel.Width / screen.Width;
            Rect Local(Rect bounds) => new(panel.X + (bounds.X - screen.X) * scale,
                panel.Y + (bounds.Y - screen.Y) * scale, bounds.Width * scale, bounds.Height * scale);
            var clip = Local(intersection);
            drawing.PushClip(new RectangleGeometry(clip));
            drawing.DrawRectangle(background, null, clip);
            var image = Local(backdrop.ImageBounds);
            if (backdrop.Tile)
            {
                var tiles = new ImageBrush(backdrop.Image) { ViewportUnits = BrushMappingMode.Absolute,
                    Viewport = image, TileMode = TileMode.Tile, Stretch = Stretch.Fill };
                drawing.DrawRectangle(tiles, null, clip);
            }
            else drawing.DrawImage(backdrop.Image, image);
            drawing.Pop();
            if (intersection.Contains(new Point(screen.X + screen.Width / 2, screen.Y + screen.Height / 2)))
            {
                var u = (screen.X + screen.Width / 2 - backdrop.ImageBounds.X) / backdrop.ImageBounds.Width;
                var v = (screen.Y + screen.Height / 2 - backdrop.ImageBounds.Y) / backdrop.ImageBounds.Height;
                if (backdrop.Tile) { u -= Math.Floor(u); v -= Math.Floor(v); }
                var x = Math.Clamp((int)(u * backdrop.Image.PixelWidth), 0, backdrop.Image.PixelWidth - 1);
                var y = Math.Clamp((int)(v * backdrop.Image.PixelHeight), 0, backdrop.Image.PixelHeight - 1);
                var index = (y * backdrop.Image.PixelWidth + x) * 4;
                brightness = (.0722 * backdrop.Pixels[index] + .7152 * backdrop.Pixels[index + 1]
                    + .2126 * backdrop.Pixels[index + 2]) / 255;
            }
            painted = true;
        }
        return painted;
    }

    private static List<Backdrop> Load()
    {
        var result = new List<Backdrop>();
        IDesktopWallpaper? wallpaper = null;
        try
        {
            wallpaper = (IDesktopWallpaper)Activator.CreateInstance(Type.GetTypeFromCLSID(new("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD"))!)!;
            var position = wallpaper.GetPosition();
            var rgb = wallpaper.GetBackgroundColor();
            background = AppPalette.Solid(((rgb & 0xff) << 16) | (rgb & 0xff00) | (rgb >> 16));
            var monitors = new List<(string Id, ScreenArea Bounds)>();
            var count = wallpaper.GetMonitorDevicePathCount();
            for (uint i = 0; i < count; i++)
            {
                var id = wallpaper.GetMonitorDevicePathAt(i);
                if (wallpaper.GetMonitorRECT(id, out var rect) != 0 || rect.Right <= rect.Left || rect.Bottom <= rect.Top) continue;
                monitors.Add((id, new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)));
            }
            if (monitors.Count == 0) return result;
            var desktop = new Rect(new Point(monitors.Min(m => m.Bounds.X), monitors.Min(m => m.Bounds.Y)),
                new Point(monitors.Max(m => m.Bounds.Right), monitors.Max(m => m.Bounds.Bottom)));
            var images = new Dictionary<string, (BitmapSource Bitmap, byte[] Pixels, int Width, int Height)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, monitor) in monitors)
            {
                try
                {
                    var path = wallpaper.GetWallpaper(id);
                    if (string.IsNullOrEmpty(path)) continue;
                    if (!images.TryGetValue(path, out var image)) images[path] = image = LoadImage(path);
                    var area = position == 5 ? desktop : new Rect(monitor.X, monitor.Y, monitor.Width, monitor.Height);
                    var bounds = ImageBounds(area, image.Width, image.Height, position);
                    result.Add(new(monitor, bounds, image.Bitmap, image.Pixels, position == 1));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or COMException) { }
            }
        }
        catch (COMException) { }
        finally { if (wallpaper != null) Marshal.ReleaseComObject(wallpaper); }
        return result;
    }

    private static Rect ImageBounds(Rect area, int width, int height, int position)
    {
        if (position == 1) return new(area.X, area.Y, width, height); // Tile
        if (position == 2) return area; // Stretch
        var fit = area.Width / width;
        var fill = area.Height / height;
        var scale = position switch { 0 => 1, 3 => Math.Min(fit, fill), _ => Math.Max(fit, fill) };
        return new(area.X + (area.Width - width * scale) / 2, area.Y + (area.Height - height * scale) / 2,
            width * scale, height * scale);
    }

    private static (BitmapSource Bitmap, byte[] Pixels, int Width, int Height) LoadImage(string path)
    {
        using var file = File.OpenRead(path);
        var frame = BitmapDecoder.Create(file, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        file.Position = 0;
        var decoded = new BitmapImage();
        decoded.BeginInit();
        decoded.StreamSource = file;
        decoded.CacheOption = BitmapCacheOption.OnLoad;
        if (width >= height) decoded.DecodePixelWidth = Math.Min(width, 128);
        else decoded.DecodePixelHeight = Math.Min(height, 128);
        decoded.EndInit();
        var small = new FormatConvertedBitmap(decoded, PixelFormats.Bgr32, null, 0);
        int w = small.PixelWidth, h = small.PixelHeight, stride = w * 4;
        var pixels = new byte[h * stride];
        var blurred = new byte[pixels.Length];
        small.CopyPixels(pixels, stride, 0);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        for (var channel = 0; channel < 3; channel++)
        {
            var sum = 0;
            for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
                sum += pixels[Math.Clamp(y + dy, 0, h - 1) * stride + Math.Clamp(x + dx, 0, w - 1) * 4 + channel];
            blurred[y * stride + x * 4 + channel] = (byte)(sum / 25);
        }
        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, blurred, stride);
        bitmap.Freeze();
        return (bitmap, blurred, width, height);
    }

    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitor, [MarshalAs(UnmanagedType.LPWStr)] string path);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitor);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint index);
        uint GetMonitorDevicePathCount();
        [PreserveSig] int GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitor, out NativeDesktop.Rect rect);
        void SetBackgroundColor(uint color);
        uint GetBackgroundColor();
        void SetPosition(int position);
        int GetPosition();
    }
}
