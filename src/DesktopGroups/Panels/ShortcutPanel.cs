using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopGroups;

// One premultiplied bitmap holds the translucent surface and opaque icons.
// WPF renders offscreen only; no WPF composition target is attached to Explorer.
internal sealed class ShortcutPanel : IDisposable
{
    private const string ClassName = "DesktopGroups.ShortcutPanel.v1";
    private static readonly WindowProcedure Procedure = Dispatch;
    private static readonly Dictionary<nint, ShortcutPanel> Instances = [];
    private static bool registered;
    private readonly Action<string> report;
    private readonly Action<int, int> moved;
    private readonly Func<IReadOnlyList<string>, bool> dropped;
    private readonly Func<IReadOnlyList<string>, bool> canAccept;
    private readonly Action<string>? returnToDesktop;
    private readonly Func<nint> findHost;
    private readonly DispatcherTimer animationTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(8) };
    private readonly DispatcherTimer gestureTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch animationClock = new();
    private double displayHeight = 352, animationFrom = 352, animationTo = 352;
    private IconDragGhost? iconGhost;
    private int pressedItem = -1;
    private bool iconDragging;
    private NativeDesktop.Point iconStart;
    private PanelDropTarget? dropTarget;
    private bool oleInitialized, dropRegistered, fileDrag;
    private List<PanelItem> items = [];
    private nint host;
    private NativeDesktop.Point? screenPosition;
    private bool disposed;
    private bool dragging, tracking, folded, hoveringOpen;
    private NativeDesktop.Point dragStart;
    private int startX, startY, selected = -1, hovered = -1;
    private double scale = 1;
    private BitmapSource? lastBitmap;
    public nint Handle { get; private set; }
    public int X { get; private set; }
    public int Y { get; private set; }
    public int Width => (int)Math.Round(420 * scale);
    public int Height => (int)Math.Round(displayHeight * scale);
    private int FullHeight => (int)Math.Round(352 * scale);
    public int SurfaceBuilds { get; private set; }
    public int MoveCount { get; private set; }
    internal bool IsFolded => folded;
    internal bool IsExpanded => !folded || hoveringOpen;
    internal int HoveredIndex => hovered;
    internal int SelectedIndex => selected;
    internal bool IsAnimating => animationTimer.IsEnabled;
    internal int AnimationFrames { get; private set; }
    internal bool IsAttached => Handle != 0 && IsWindow(Handle) && IsWindow(host) && NativeDesktop.GetParent(Handle) == host;
    internal bool IsInteracting => dragging || pressedItem >= 0 || fileDrag;
    internal int AttachmentCount { get; private set; }
    public event Action<bool>? FoldedChanged;
    internal IPanelDropTarget DropTarget => dropTarget ?? throw new InvalidOperationException("拖放尚未注册。");
    public event Action? Disposed;

    public ShortcutPanel(Action<string> report, Action<int, int> moved, Func<IReadOnlyList<string>, bool> dropped,
        Func<IReadOnlyList<string>, bool>? canAccept = null, Action<string>? returnToDesktop = null, Func<nint>? findHost = null)
    {
        this.report = report; this.moved = moved; this.dropped = dropped;
        this.canAccept = canAccept ?? (paths => paths.Count is > 0 and <= 12 && paths.All(path => File.Exists(path) && ShortcutInfo.IsShortcut(path)));
        this.returnToDesktop = returnToDesktop;
        this.findHost = findHost ?? DesktopAttachment.FindIconHost;
        animationTimer.Tick += (_, _) => AdvanceAnimation();
        gestureTimer.Tick += (_, _) =>
        {
            if ((GetAsyncKeyState(0x1B) & 0x8000) != 0) { CancelIconGesture(); UpdateHoverAfterMove(); }
        };
    }

    public void Show(int x, int y)
    {
        if (disposed) throw new ObjectDisposedException(nameof(ShortcutPanel));
        if (!IsAttached) { X = x; Y = y; }
        EnsureAttached(refreshEnvironment: true);
    }

    internal bool EnsureAttached(bool refreshEnvironment = false)
    {
        if (disposed) throw new ObjectDisposedException(nameof(ShortcutPanel));
        if (IsAttached && (!refreshEnvironment || IsInteracting)) return false;
        var nextHost = findHost();
        var recreate = !IsAttached || host != nextHost;
        if (recreate) ReleaseSurface();
        host = nextHost;
        var point = screenPosition ?? new NativeDesktop.Point { X = X, Y = Y };
        if (screenPosition == null) ClientToScreen(host, ref point);
        var placement = DesktopPlacement.Resolve(point.X, point.Y, DesktopPlacement.Capture());
        screenPosition = new NativeDesktop.Point { X = placement.X, Y = placement.Y };
        point = screenPosition.Value;
        DesktopAttachment.ScreenToClient(host, ref point);
        var scaleChanged = Math.Abs(scale - placement.Dpi / 96d) > .001;
        var movedPosition = X != point.X || Y != point.Y;
        X = point.X; Y = point.Y; scale = placement.Dpi / 96d;
        if (!recreate)
        {
            if (scaleChanged) Render();
            DesktopAttachment.Place(Handle, X, Y, Width, Height);
            if (movedPosition) moved(X, Y);
            return false;
        }
        Register();
        Handle = CreateWindowEx(0x08080080, ClassName, "桌面分区 · 常用", 0x44000000, X, Y, Width, Height, host, 0, GetModuleHandle(null), 0);
        if (Handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        Instances.Add(Handle, this);
        try
        {
            Render();
            DesktopAttachment.Place(Handle, X, Y, Width, Height);
            if (!oleInitialized) { Marshal.ThrowExceptionForHR(OleInitialize(0)); oleInitialized = true; }
            dropTarget = new PanelDropTarget(this.canAccept, this.dropped, SetFileDrag, report);
            Marshal.ThrowExceptionForHR(RegisterDragDrop(Handle, dropTarget));
            dropRegistered = true;
            AttachmentCount++;
            if (movedPosition) moved(X, Y);
            return true;
        }
        catch { ReleaseSurface(); throw; }
    }

    internal void RestoreFolded(bool value)
    {
        folded = value; hoveringOpen = false;
        displayHeight = animationFrom = animationTo = value ? 46 : 352;
        animationTimer.Stop();
        if (IsAttached) Render();
    }

    public void SetItems(IEnumerable<(string Name, string Path, string? OriginalPath)> entries)
    {
        items = entries.Select(entry => new PanelItem(entry.Name, entry.Path, ShortcutInfo.LoadIcon(entry.Path, entry.OriginalPath))).ToList();
        selected = -1; hovered = -1;
        if (IsAttached) Render();
    }

    internal void MoveTo(int x, int y)
    {
        if (!SetWindowPos(Handle, 0, x, y, 0, 0, 0x01 | 0x04 | 0x10)) throw new Win32Exception(Marshal.GetLastWin32Error());
        X = x; Y = y; MoveCount++;
        var point = new NativeDesktop.Point { X = X, Y = Y };
        ClientToScreen(host, ref point); screenPosition = point;
    }

    public void Render()
    {
        if (!IsAttached) return;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushTransform(new ScaleTransform(scale, scale));
            const double height = 352;
            drawing.DrawRoundedRectangle(Solid(225, 31, 34, 40), new Pen(Solid(40, 255, 255, 255), 1), new Rect(.5, .5, 419, height - 1), 12, 12);
            Text(drawing, "常用", 19, 12, 15, Colors.White, 240, 24, FontWeights.SemiBold);
            Text(drawing, items.Count == 0 ? "" : items.Count.ToString(), 65, 15, 11, Color.FromRgb(164, 171, 181), 80, 20);
            Text(drawing, folded ? "+" : "−", 384, 10, 19, Color.FromRgb(183, 191, 203), 20, 25);
            if (height > 46)
            {
                drawing.DrawLine(new Pen(Solid(20, 255, 255, 255), 1), new Point(16, 45), new Point(404, 45));
                if (items.Count == 0)
                {
                    Text(drawing, "把常用入口放在这里", 26, 132, 17, Color.FromRgb(224, 228, 234), 368, 28, FontWeights.SemiBold);
                    Text(drawing, "选择桌面快捷方式，再点击“收纳选中”", 26, 169, 12, Color.FromRgb(155, 165, 180), 368, 40);
                }
                for (var index = 0; index < items.Count; index++)
                {
                    var left = 16 + index % 4 * 98;
                    var top = 57 + index / 4 * 94;
                    if (index == selected) drawing.DrawRoundedRectangle(Solid(44, 157, 187, 219), new Pen(Solid(95, 174, 200, 228), 1), new Rect(left, top, 94, 88), 7, 7);
                    else if (index == hovered) drawing.DrawRoundedRectangle(Solid(23, 255, 255, 255), new Pen(Solid(38, 255, 255, 255), 1), new Rect(left, top, 94, 88), 7, 7);
                    if (items[index].Icon is { } icon) drawing.DrawImage(icon, new Rect(left + 27, top + 7, 40, 40));
                    else drawing.DrawRoundedRectangle(Solid(255, 119, 140, 166), null, new Rect(left + 31, top + 11, 32, 32), 6, 6);
                    Text(drawing, items[index].Name, left + 4, top + 53, 11, Color.FromRgb(234, 237, 242), 86, 31, alignment: TextAlignment.Center);
                }
            }
            if (fileDrag) drawing.DrawRoundedRectangle(null, new Pen(Solid(180, 158, 195, 219), 1.5), new Rect(2, 2, 416, height - 4), 10, 10);
            drawing.Pop();
        }
        var bitmap = new RenderTargetBitmap(Width, FullHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        lastBitmap = bitmap;
        SurfaceBuilds++;
        var target = IsExpanded ? 352d : 46d;
        if (Math.Abs(target - animationTo) > .01)
        {
            animationFrom = displayHeight; animationTo = target;
            animationClock.Restart(); animationTimer.Start();
        }
        Present();
    }

    private void AdvanceAnimation()
    {
        if (Handle == 0 || lastBitmap == null) { animationTimer.Stop(); return; }
        var progress = Math.Min(1, animationClock.Elapsed.TotalMilliseconds / 150);
        var eased = 1 - Math.Pow(1 - progress, 3);
        displayHeight = animationFrom + (animationTo - animationFrom) * eased;
        AnimationFrames++;
        Present();
        if (progress >= 1) animationTimer.Stop();
    }

    private void Present()
    {
        if (lastBitmap != null) Upload(lastBitmap);
    }

    internal void SavePreview(string path)
    {
        if (lastBitmap == null) throw new InvalidOperationException("面板尚未绘制。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(new CroppedBitmap(lastBitmap, new Int32Rect(0, 0, Width, Height))));
        using var output = File.Create(path); encoder.Save(output);
    }

    internal (int OpaquePixels, byte BodyAlpha) InspectPixels()
    {
        if (lastBitmap == null) throw new InvalidOperationException("面板尚未绘制。");
        var pixels = new byte[Width * FullHeight * 4];
        lastBitmap.CopyPixels(pixels, Width * 4, 0);
        var opaque = 0;
        for (var index = 3; index < pixels.Length; index += 4) if (pixels[index] == 255) opaque++;
        return (opaque, pixels[((Height - (int)(15 * scale)) * Width + Width / 2) * 4 + 3]);
    }

    private void Upload(BitmapSource bitmap)
    {
        var screen = GetDC(0);
        var memory = CreateCompatibleDC(screen);
        if (screen == 0 || memory == 0) { if (memory != 0) DeleteDC(memory); if (screen != 0) ReleaseDC(0, screen); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        nint dib = 0, previous = 0;
        try
        {
            var info = new BitmapInfo { Size = 40, Width = Width, Height = -Height, Planes = 1, Bits = 32 };
            dib = CreateDIBSection(memory, ref info, 0, out var pixels, 0, 0);
            if (dib == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            bitmap.CopyPixels(new Int32Rect(0, 0, Width, Height), pixels, Width * Height * 4, Width * 4);
            // The cached full surface keeps its top corners. Round the newly
            // clipped bottom edge without rerendering text/icons on every frame.
            if (Height < FullHeight)
            {
                var radius = (int)Math.Round(12 * scale);
                for (var row = 0; row < radius; row++)
                    for (var column = 0; column < radius; column++)
                    {
                        var distance = Math.Sqrt(Math.Pow(radius - column - .5, 2) + Math.Pow(row + .5, 2));
                        var coverage = Math.Clamp(radius + .5 - distance, 0, 1);
                        foreach (var x in new[] { column, Width - 1 - column })
                        {
                            var offset = ((Height - radius + row) * Width + x) * 4;
                            for (var channel = 0; channel < 4; channel++)
                                Marshal.WriteByte(pixels, offset + channel, (byte)(Marshal.ReadByte(pixels, offset + channel) * coverage));
                        }
                    }
            }
            previous = SelectObject(memory, dib);
            var destination = new NativeDesktop.Point { X = X, Y = Y };
            ClientToScreen(host, ref destination);
            var size = new NativeDesktop.Point { X = Width, Y = Height };
            var origin = new NativeDesktop.Point();
            var blend = new BlendFunction { SourceConstantAlpha = 255, AlphaFormat = 1 };
            if (!UpdateLayeredWindow(Handle, screen, ref destination, ref size, memory, ref origin, 0, ref blend, 2)) throw new Win32Exception(Marshal.GetLastWin32Error(), "分区画面提交失败。");
        }
        finally
        {
            if (previous != 0) SelectObject(memory, previous);
            if (dib != 0) DeleteObject(dib);
            DeleteDC(memory); ReleaseDC(0, screen);
        }
    }

    private int HitItem(int x, int y)
    {
        if (folded && !hoveringOpen || y < 57 * scale) return -1;
        var column = (int)(x / scale - 16) / 98;
        var row = (int)(y / scale - 57) / 94;
        var index = row * 4 + column;
        return x >= 16 * scale && column is >= 0 and < 4 && row is >= 0 and < 3 && index < items.Count ? index : -1;
    }

    internal void SetFolded(bool value)
    {
        if (folded == value && !hoveringOpen) return;
        folded = value; hoveringOpen = false; hovered = -1;
        Render();
        FoldedChanged?.Invoke(value);
    }

    internal void PointerMove(int x, int y)
    {
        var needsRender = false;
        if (folded && !hoveringOpen) { hoveringOpen = true; needsRender = true; }
        var next = HitItem(x, y);
        if (next != hovered) { hovered = next; needsRender = true; }
        if (needsRender) Render();
    }

    private void TrackPointerLeave()
    {
        if (!tracking)
        {
            var trackingInfo = new TrackMouse { Size = (uint)Marshal.SizeOf<TrackMouse>(), Flags = 2, Window = Handle };
            tracking = TrackMouseEvent(ref trackingInfo);
        }
    }

    internal void PointerLeave()
    {
        tracking = false;
        var needsRender = hovered != -1;
        hovered = -1;
        if (!dragging && !fileDrag && pressedItem < 0 && folded && hoveringOpen) { hoveringOpen = false; needsRender = true; }
        if (needsRender) Render();
    }

    internal void SelectAt(int x, int y) { selected = HitItem(x, y); Render(); }

    private void SetFileDrag(bool active, bool keepHover)
    {
        fileDrag = active; hovered = -1;
        if (active && folded) hoveringOpen = true;
        if (!active)
        {
            if (!keepHover)
            {
                if (folded && !dragging) hoveringOpen = false;
                Render(); return;
            }
            // Drop occurs while normal mouse messages are captured by Explorer.
            // Preserve expansion if the pointer remains on the opened drawer.
            DesktopAttachment.GetCursorPos(out var point);
            DesktopAttachment.ScreenToClient(Handle, ref point);
            var inside = point.X >= 0 && point.X < Width && point.Y >= 0 && point.Y < Height;
            if (!inside && folded && !dragging) hoveringOpen = false;
            if (inside) { PointerMove(point.X, point.Y); TrackPointerLeave(); }
        }
        Render();
    }

    private static nint Dispatch(nint window, uint message, nint wParam, nint lParam)
    {
        if (!Instances.TryGetValue(window, out var panel)) return DefWindowProc(window, message, wParam, lParam);
        try { return panel.OnMessage(message, wParam, lParam); }
        catch (Exception exception) { panel.report($"操作未完成：{exception.Message}"); return 0; }
    }

    private nint OnMessage(uint message, nint wParam, nint lParam)
    {
        var x = (short)(lParam.ToInt64() & 0xffff);
        var y = (short)((lParam.ToInt64() >> 16) & 0xffff);
        switch (message)
        {
            case 0x82: // The Explorer-owned parent can destroy this surface.
                var previous = Handle;
                ReleaseSurface(destroy: false);
                return DefWindowProc(previous, message, wParam, lParam);
            case 0x21: return 3;
            case 0x14: return 1;
            case 0x201:
                if (y < 46 * scale)
                {
                    if (x > Width - 42 * scale) { SetFolded(!folded); return 0; }
                    DesktopAttachment.GetCursorPos(out dragStart);
                    startX = X; startY = Y; dragging = true; SetCapture(Handle);
                }
                else
                {
                    SelectAt(x, y);
                    if (selected >= 0 && returnToDesktop != null)
                    {
                        pressedItem = selected; iconDragging = false;
                        DesktopAttachment.GetCursorPos(out iconStart);
                        SetCapture(Handle); gestureTimer.Start();
                    }
                }
                return 0;
            case 0x200:
                if (dragging)
                {
                    DesktopAttachment.GetCursorPos(out var cursor);
                    MoveTo(startX + cursor.X - dragStart.X, startY + cursor.Y - dragStart.Y);
                }
                else if (pressedItem >= 0)
                {
                    DesktopAttachment.GetCursorPos(out var cursor);
                    if (!iconDragging && (Math.Abs(cursor.X - iconStart.X) >= GetSystemMetrics(68) || Math.Abs(cursor.Y - iconStart.Y) >= GetSystemMetrics(69)))
                    {
                        iconDragging = true;
                        iconGhost = new IconDragGhost(items[pressedItem].Icon, cursor);
                    }
                    iconGhost?.Move(cursor);
                    if (iconDragging) SetCursor(LoadCursor(0, (nint)(IsDesktopAt(cursor) ? 32512 : 32648)));
                }
                else { PointerMove(x, y); TrackPointerLeave(); }
                return 0;
            case 0x202:
                if (pressedItem >= 0) { CompleteIconGesture(); return 0; }
                if (dragging) { dragging = false; ReleaseCapture(); EnsureAttached(true); moved(X, Y); UpdateHoverAfterMove(); }
                return 0;
            case 0x215:
                if (pressedItem >= 0) { CancelIconGesture(); UpdateHoverAfterMove(); }
                if (dragging) { dragging = false; moved(X, Y); UpdateHoverAfterMove(); }
                return 0;
            case 0x203:
                var item = HitItem(x, y);
                if (item >= 0) Process.Start(new ProcessStartInfo(items[item].Path) { UseShellExecute = true });
                return 0;
            case 0x2A3:
                PointerLeave(); return 0;
        }
        return DefWindowProc(Handle, message, wParam, lParam);
    }

    internal bool IsDesktopAt(NativeDesktop.Point point)
    {
        var window = WindowFromPoint(point);
        return window == host || (NativeDesktop.GetParent(window) == host && NativeDesktop.ClassName(window) == "SysListView32");
    }

    private void CompleteIconGesture()
    {
        var path = pressedItem >= 0 && pressedItem < items.Count ? items[pressedItem].Path : null;
        var didDrag = iconDragging;
        DesktopAttachment.GetCursorPos(out var point);
        CancelIconGesture(); // Remove the ghost before checking the target window.
        if (didDrag && path != null)
        {
            if (IsDesktopAt(point)) returnToDesktop?.Invoke(path);
            else report("已取消拖出：请拖到桌面空白处松开。");
        }
        UpdateHoverAfterMove();
    }

    private void CancelIconGesture()
    {
        pressedItem = -1; iconDragging = false; gestureTimer.Stop();
        iconGhost?.Dispose(); iconGhost = null;
        ReleaseCapture();
        SetCursor(LoadCursor(0, (nint)32512));
    }

    private void UpdateHoverAfterMove()
    {
        DesktopAttachment.GetCursorPos(out var point);
        DesktopAttachment.ScreenToClient(Handle, ref point);
        if (point.X < 0 || point.X >= Width || point.Y < 0 || point.Y >= Height) PointerLeave();
    }

    private static SolidColorBrush Solid(byte alpha, byte red, byte green, byte blue) => new(Color.FromArgb(alpha, red, green, blue));
    private static void Text(DrawingContext drawing, string text, double x, double y, double size, Color color, double width, double height, FontWeight? weight = null, TextAlignment alignment = TextAlignment.Left)
    {
        if (string.IsNullOrEmpty(text)) return;
        var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal), size, new SolidColorBrush(color), 1)
        { MaxTextWidth = width, MaxTextHeight = height, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = alignment };
        drawing.DrawText(formatted, new Point(x, y));
    }

    private static void Register()
    {
        if (registered) return;
        var definition = new WindowClassDefinition
        {
            Size = (uint)Marshal.SizeOf<WindowClassDefinition>(), Style = 8,
            Procedure = Marshal.GetFunctionPointerForDelegate(Procedure), Instance = GetModuleHandle(null),
            ClassName = ClassName, Cursor = LoadCursor(0, (nint)32512)
        };
        if (RegisterClassEx(ref definition) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        registered = true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ReleaseSurface();
        if (oleInitialized) { OleUninitialize(); oleInitialized = false; }
        Disposed?.Invoke();
    }

    private void ReleaseSurface(bool destroy = true)
    {
        animationTimer.Stop(); gestureTimer.Stop();
        if (pressedItem >= 0) CancelIconGesture();
        if (dragging) { dragging = false; ReleaseCapture(); }
        if (dropRegistered) { RevokeDragDrop(Handle); dropRegistered = false; }
        dropTarget = null;
        if (Handle != 0) { var handle = Handle; Handle = 0; Instances.Remove(handle); if (destroy && IsWindow(handle)) DestroyWindow(handle); }
        fileDrag = tracking = hoveringOpen = false; hovered = -1;
        displayHeight = animationFrom = animationTo = folded ? 46 : 352;
    }

    private sealed record PanelItem(string Name, string Path, BitmapSource? Icon);
    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClassDefinition
    { public uint Size, Style; public nint Procedure; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? MenuName; public string ClassName; public nint SmallIcon; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize; public int XPixels, YPixels; public uint Used, Important; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BlendFunction { public byte Operation, Flags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct TrackMouse { public uint Size, Flags; public nint Window; public uint HoverTime; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClassDefinition definition);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint exStyle, string name, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern nint LoadCursor(nint instance, nint name);
    [DllImport("user32.dll")] private static extern nint SetCapture(nint window);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int metric);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint SetCursor(nint cursor);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativeDesktop.Point point);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref NativeDesktop.Point point);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TrackMouse tracking);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint pixels, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(nint window, nint destinationDC, ref NativeDesktop.Point destination, ref NativeDesktop.Point size, nint sourceDC, ref NativeDesktop.Point source, uint colorKey, ref BlendFunction blend, uint flags);
    [DllImport("ole32.dll")] private static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] private static extern int RegisterDragDrop(nint window, IPanelDropTarget target);
    [DllImport("ole32.dll")] private static extern int RevokeDragDrop(nint window);
}
