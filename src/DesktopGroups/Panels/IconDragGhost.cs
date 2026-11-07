using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopGroups;

// A disabled transparent helper follows only an active icon gesture. It does
// not become a target and is destroyed before the desktop hit test on release.
internal sealed class IconDragGhost : IDisposable
{
    private nint handle;
    public IconDragGhost(BitmapSource? icon, NativeDesktop.Point point)
    {
        handle = CreateWindowEx(0x080800A8, "STATIC", "", 0x88000000, point.X + 10, point.Y + 10, 56, 56, 0, 0, 0, 0);
        if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(180, 40, 47, 58)), null, new Rect(0, 0, 56, 56), 9, 9);
            if (icon != null) drawing.DrawImage(icon, new Rect(8, 8, 40, 40));
        }
        var bitmap = new RenderTargetBitmap(56, 56, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var screen = GetDC(0); var memory = CreateCompatibleDC(screen);
        nint dib = 0, old = 0;
        try
        {
            var info = new BitmapInfo { Size = 40, Width = 56, Height = -56, Planes = 1, Bits = 32 };
            dib = CreateDIBSection(memory, ref info, 0, out var pixels, 0, 0);
            if (dib == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            bitmap.CopyPixels(Int32Rect.Empty, pixels, 56 * 56 * 4, 56 * 4);
            old = SelectObject(memory, dib);
            var destination = new NativeDesktop.Point { X = point.X + 10, Y = point.Y + 10 };
            var size = new NativeDesktop.Point { X = 56, Y = 56 }; var origin = new NativeDesktop.Point();
            var blend = new Blend { Alpha = 225, Format = 1 };
            if (!UpdateLayeredWindow(handle, screen, ref destination, ref size, memory, ref origin, 0, ref blend, 2)) throw new Win32Exception(Marshal.GetLastWin32Error());
            SetWindowPos(handle, (nint)(-1), destination.X, destination.Y, 56, 56, 0x10 | 0x40);
        }
        catch { Dispose(); throw; }
        finally
        {
            if (old != 0) SelectObject(memory, old);
            if (dib != 0) DeleteObject(dib);
            if (memory != 0) DeleteDC(memory);
            if (screen != 0) ReleaseDC(0, screen);
        }
    }
    public void Move(NativeDesktop.Point point) => SetWindowPos(handle, 0, point.X + 10, point.Y + 10, 0, 0, 0x01 | 0x04 | 0x10);
    public void Dispose() { if (handle != 0) { DestroyWindow(handle); handle = 0; } }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize; public int XPixels, YPixels; public uint Used, Important; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend { public byte Operation, Flags, Alpha, Format; }
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint exStyle, string name, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint data);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint pixels, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(nint window, nint destinationDC, ref NativeDesktop.Point destination, ref NativeDesktop.Point size, nint sourceDC, ref NativeDesktop.Point source, uint key, ref Blend blend, uint flags);
}
