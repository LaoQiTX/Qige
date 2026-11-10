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
    private string title = "常用";
    private double logicalWidth = 420, logicalHeight = 352, scrollOffset;
    private bool layoutLocked, scrolling, highlighted;
    private bool itemMenuOpen;
    private int resizeEdges;
    private LayoutRect gestureStart;
    private long lastResize;
    public Func<LayoutRect, int, LayoutRect>? TransformLayout { get; set; }
    public event Action? LayoutCommitted;
    public event Action? InteractionEnded;
    public event Action? ManageRequested;
    public event Action<string>? ItemMenuRequested;
    public bool LayoutLocked => layoutLocked;
    public double LogicalWidth => logicalWidth;
    public double LogicalHeight => logicalHeight;
    public double ScrollOffset => scrollOffset;
    public int Columns => Math.Max(1, (int)((logicalWidth - 28) / 98));
    private double CellWidth => (logicalWidth - 28) / Columns;
    private double MaxScroll => Math.Max(0, Math.Ceiling(items.Count / (double)Columns) * 94 - (logicalHeight - 63));
    internal LayoutRect ScreenBounds
    {
        get
        {
            var point = new NativeDesktop.Point { X = X, Y = Y };
            if (host != 0) ClientToScreen(host, ref point);
            return new(point.X, point.Y, Width, Height);
        }
    }
    private double scale = 1;
    private BitmapSource? lastBitmap;
    public nint Handle { get; private set; }
    public int X { get; private set; }
    public int Y { get; private set; }
    public int Width => (int)Math.Round(logicalWidth * scale);
    public int Height => (int)Math.Round(displayHeight * scale);
    private int FullHeight => (int)Math.Round(logicalHeight * scale);
    public int SurfaceBuilds { get; private set; }
    public int MoveCount { get; private set; }
    internal bool IsFolded => folded;
    internal bool IsExpanded => !folded || hoveringOpen;
    internal int HoveredIndex => hovered;
    internal int SelectedIndex => selected;
    internal bool IsAnimating => animationTimer.IsEnabled;
    internal int AnimationFrames { get; private set; }
    internal bool IsAttached => Handle != 0 && IsWindow(Handle) && IsWindow(host) && NativeDesktop.GetParent(Handle) == host;
    internal bool IsInteracting => dragging || resizeEdges != 0 || scrolling || pressedItem >= 0 || fileDrag || itemMenuOpen;
    internal int AttachmentCount { get; private set; }
    public event Action<bool>? FoldedChanged;
    internal IPanelDropTarget DropTarget => dropTarget ?? throw new InvalidOperationException("拖放尚未注册。");
    public event Action? Disposed;

    public ShortcutPanel(Action<string> report, Action<int, int> moved, Func<IReadOnlyList<string>, bool> dropped,
        Func<IReadOnlyList<string>, bool>? canAccept = null, Action<string>? returnToDesktop = null, Func<nint>? findHost = null)
    {
        this.report = report; this.moved = moved; this.dropped = dropped;
        this.canAccept = canAccept ?? (paths => paths.Count > 0 && paths.All(path => File.Exists(path) && ShortcutInfo.IsShortcut(path)));
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
        var placement = DesktopPlacement.Resolve(point.X, point.Y, logicalWidth, logicalHeight, DesktopPlacement.Capture(), folded: !IsExpanded);
        screenPosition = new NativeDesktop.Point { X = placement.X, Y = placement.Y };
        point = screenPosition.Value;
        DesktopAttachment.ScreenToClient(host, ref point);
        var scaleChanged = Math.Abs(scale - placement.Dpi / 96d) > .001;
        var sizeChanged = Math.Abs(logicalWidth - placement.Width) > .001 || Math.Abs(logicalHeight - placement.Height) > .001;
        var movedPosition = X != point.X || Y != point.Y;
        X = point.X; Y = point.Y; scale = placement.Dpi / 96d;
        if (sizeChanged)
        {
            logicalWidth = placement.Width; logicalHeight = placement.Height;
            scrollOffset = Math.Clamp(scrollOffset, 0, MaxScroll);
            animationTimer.Stop();
            displayHeight = animationFrom = animationTo = IsExpanded ? logicalHeight : 46;
        }
        if (!recreate)
        {
            if (scaleChanged || sizeChanged) Render();
            DesktopAttachment.Place(Handle, X, Y, Width, Height);
            if (movedPosition || sizeChanged || scaleChanged) moved(X, Y);
            return false;
        }
        Register();
        Handle = CreateWindowEx(0x08080080, ClassName, "桌面分区 · " + title, 0x44000000, X, Y, Width, Height, host, 0, GetModuleHandle(null), 0);
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
            if (movedPosition || sizeChanged || scaleChanged) moved(X, Y);
            return true;
        }
        catch { ReleaseSurface(); throw; }
    }

    internal void RestoreFolded(bool value)
    {
        folded = value; hoveringOpen = false;
        displayHeight = animationFrom = animationTo = value ? 46 : logicalHeight;
        animationTimer.Stop();
        if (IsAttached) Render();
    }

    public void Configure(string name, double width, double height, bool locked)
    {
        title = name; layoutLocked = locked;
        logicalWidth = Math.Clamp(width, 244, 1400); logicalHeight = Math.Clamp(height, 160, 1100);
        scrollOffset = Math.Clamp(scrollOffset, 0, MaxScroll);
        animationTimer.Stop();
        displayHeight = animationFrom = animationTo = IsExpanded ? logicalHeight : 46;
        if (IsAttached) Render();
    }

    public void ShowScreen(int x, int y)
    {
        screenPosition = new NativeDesktop.Point { X = x, Y = y };
        Show(x, y);
    }

    public void ApplyScreenLayout(int x, int y, double width, double height)
    {
        Configure(title, width, height, layoutLocked);
        ShowScreen(x, y);
        LayoutCommitted?.Invoke();
    }

    public void ScrollBy(double amount)
    {
        var next = Math.Clamp(scrollOffset + amount, 0, MaxScroll);
        if (Math.Abs(next - scrollOffset) < .1) return;
        scrollOffset = next; hovered = -1;
        if (IsAttached) Render();
    }

    public void SetItems(IEnumerable<(string Name, string Path, string? OriginalPath)> entries)
    {
        items = entries.Select(entry => new PanelItem(entry.Name, entry.Path, ShortcutInfo.LoadIcon(entry.Path, entry.OriginalPath))).ToList();
        selected = -1; hovered = -1;
        scrollOffset = Math.Clamp(scrollOffset, 0, MaxScroll);
        if (IsAttached) Render();
    }

    internal void SetPreparedItems(IEnumerable<(string Name, string Path, BitmapSource? Icon)> entries)
    {
        items = entries.Select(e => new PanelItem(e.Name, e.Path, e.Icon)).ToList();
        selected = -1; hovered = -1; scrollOffset = Math.Clamp(scrollOffset, 0, MaxScroll);
        if (IsAttached) Render();
    }

    internal void MoveTo(int x, int y)
    {
        if (!SetWindowPos(Handle, 0, x, y, 0, 0, 0x01 | 0x04 | 0x10)) throw new Win32Exception(Marshal.GetLastWin32Error());
        X = x; Y = y; MoveCount++;
        var point = new NativeDesktop.Point { X = X, Y = Y };
        ClientToScreen(host, ref point); screenPosition = point;
    }

    public async void Highlight()
    {
        if (!IsAttached) return;
        highlighted = true; Render();
        await Task.Delay(650);
        highlighted = false;
        if (!disposed && IsAttached) Render();
    }

    public void Render()
    {
        if (!IsAttached) return;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushTransform(new ScaleTransform(scale, scale));
            var height = logicalHeight;
            drawing.DrawRoundedRectangle(AppPalette.Translucent(225, AppPalette.Sidebar), new Pen(AppPalette.Translucent(40, AppPalette.Text), 1), new Rect(.5, .5, logicalWidth - 1, height - 1), 12, 12);
            Text(drawing, title, 19, 12, 15, AppPalette.Text.Color, Math.Max(60, logicalWidth - 150), 24, FontWeights.SemiBold);
            var countX = Math.Min(logicalWidth - 124, 24 + title.Length * 15);
            Text(drawing, items.Count == 0 ? "" : items.Count.ToString(), countX, 15, 11, AppPalette.Muted.Color, 42, 20);
            Text(drawing, "···", logicalWidth - 74, 9, 18, AppPalette.Secondary.Color, 28, 25);
            Text(drawing, folded ? "+" : "−", logicalWidth - 36, 10, 19, AppPalette.Secondary.Color, 20, 25);
            drawing.DrawLine(new Pen(AppPalette.Translucent(20, AppPalette.Text), 1), new Point(16, 45), new Point(logicalWidth - 16, 45));
            drawing.PushClip(new RectangleGeometry(new Rect(10, 49, logicalWidth - 20, height - 57)));
            if (items.Count == 0)
            {
                Text(drawing, "把常用入口放在这里", 24, 88, 16, AppPalette.Text.Color, logicalWidth - 48, 46, FontWeights.SemiBold);
                Text(drawing, "拖入桌面快捷方式，或从管理面板添加", 24, 140, 12, AppPalette.Muted.Color, logicalWidth - 48, 45);
            }
            for (var index = 0; index < items.Count; index++)
            {
                var left = 14 + index % Columns * CellWidth;
                var top = 57 + index / Columns * 94 - scrollOffset;
                if (top + 88 < 49 || top > height - 8) continue;
                var cell = new Rect(left, top, CellWidth - 4, 88);
                if (index == selected) drawing.DrawRoundedRectangle(AppPalette.Translucent(38, AppPalette.Text), new Pen(AppPalette.Translucent(95, AppPalette.Text), 1), cell, 7, 7);
                else if (index == hovered) drawing.DrawRoundedRectangle(AppPalette.Translucent(12, AppPalette.Text), new Pen(AppPalette.Translucent(22, AppPalette.Text), 1), cell, 7, 7);
                var center = left + (CellWidth - 4) / 2;
                if (items[index].Icon is { } icon) drawing.DrawImage(icon, new Rect(center - 20, top + 7, 40, 40));
                else drawing.DrawRoundedRectangle(AppPalette.ScrollThumb, null, new Rect(center - 16, top + 11, 32, 32), 6, 6);
                Text(drawing, items[index].Name, left + 4, top + 53, 11, AppPalette.Text.Color, CellWidth - 12, 31, alignment: TextAlignment.Center);
            }
            drawing.Pop();
            if (MaxScroll > 0)
            {
                var track = height - 64;
                var thumb = Math.Max(24, track * track / (track + MaxScroll));
                drawing.DrawRoundedRectangle(AppPalette.Translucent(95, AppPalette.Secondary), null,
                    new Rect(logicalWidth - 8, 54 + scrollOffset / MaxScroll * (track - thumb), 3, thumb), 1.5, 1.5);
            }
            if (!layoutLocked)
                drawing.DrawLine(new Pen(AppPalette.Translucent(72, AppPalette.Secondary), 1), new Point(logicalWidth - 16, height - 8), new Point(logicalWidth - 8, height - 16));
            if (highlighted) drawing.DrawRoundedRectangle(null, new Pen(AppPalette.Translucent(235, AppPalette.Accent), 2), new Rect(1, 1, logicalWidth - 2, height - 2), 11, 11);
            if (fileDrag) drawing.DrawRoundedRectangle(null, new Pen(AppPalette.Translucent(180, AppPalette.Accent), 1.5), new Rect(2, 2, logicalWidth - 4, height - 4), 10, 10);
            drawing.Pop();
        }
        var bitmap = new RenderTargetBitmap(Width, FullHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        lastBitmap = bitmap;
        SurfaceBuilds++;
        var target = IsExpanded ? logicalHeight : 46d;
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
        if (y > Height - 8 * scale || x < 14 * scale || x >= (logicalWidth - 14) * scale) return -1;
        var column = (int)((x / scale - 14) / CellWidth);
        var row = (int)((y / scale - 57 + scrollOffset) / 94);
        var index = row * Columns + column;
        return column >= 0 && column < Columns && row >= 0 && index >= 0 && index < items.Count ? index : -1;
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
        // Captured resize/scroll gestures can leave the visible bounds. Keep
        // the expanded surface until the gesture ends, just as for dragging.
        if (!IsInteracting && folded && hoveringOpen) { hoveringOpen = false; needsRender = true; }
        if (needsRender) Render();
    }

    internal void SelectAt(int x, int y) { selected = HitItem(x, y); Render(); }

    internal void CloseItemMenu()
    {
        itemMenuOpen = false;
        UpdateHoverAfterMove();
    }

    private void SetFileDrag(bool active, bool keepHover)
    {
        fileDrag = active; hovered = -1;
        if (active && folded) hoveringOpen = true;
        if (!active)
        {
            if (!keepHover)
            {
                if (folded && !IsInteracting) hoveringOpen = false;
                Render(); return;
            }
            // Drop occurs while normal mouse messages are captured by Explorer.
            // Preserve expansion if the pointer remains on the opened drawer.
            DesktopAttachment.GetCursorPos(out var point);
            DesktopAttachment.ScreenToClient(Handle, ref point);
            var inside = point.X >= 0 && point.X < Width && point.Y >= 0 && point.Y < Height;
            if (!inside && folded && !IsInteracting) hoveringOpen = false;
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
                var edges = HitResize(x, y);
                if (edges != 0)
                {
                    resizeEdges = edges; gestureStart = ScreenBounds;
                    DesktopAttachment.GetCursorPos(out dragStart); SetCapture(Handle); return 0;
                }
                if (x >= Width - 12 * scale && y >= 49 * scale && MaxScroll > 0)
                {
                    scrolling = true; ScrollAt(y); SetCapture(Handle); return 0;
                }
                if (y < 46 * scale)
                {
                    if (x > Width - 42 * scale) { SetFolded(!folded); return 0; }
                    if (x > Width - 82 * scale) { ManageRequested?.Invoke(); return 0; }
                    if (layoutLocked) return 0;
                    DesktopAttachment.GetCursorPos(out dragStart);
                    startX = X; startY = Y; gestureStart = ScreenBounds; dragging = true; SetCapture(Handle);
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
                if (scrolling) { ScrollAt(y); return 0; }
                if (resizeEdges != 0) { ResizePointer(); return 0; }
                if (dragging)
                {
                    DesktopAttachment.GetCursorPos(out var cursor);
                    var rect = gestureStart with { X = gestureStart.X + cursor.X - dragStart.X, Y = gestureStart.Y + cursor.Y - dragStart.Y };
                    rect = TransformLayout?.Invoke(rect, 0) ?? rect;
                    var point = new NativeDesktop.Point { X = (int)Math.Round(rect.X), Y = (int)Math.Round(rect.Y) };
                    DesktopAttachment.ScreenToClient(host, ref point);
                    MoveTo(point.X, point.Y);
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
                else
                {
                    var hit = HitResize(x, y);
                    var cursorId = hit is 3 or 12 ? 32512 : (hit & 3) != 0 && (hit & 12) != 0 ? (hit is 5 or 10 ? 32642 : 32643) : (hit & 3) != 0 ? 32644 : (hit & 12) != 0 ? 32645 : 32512;
                    SetCursor(LoadCursor(0, (nint)cursorId));
                    PointerMove(x, y); TrackPointerLeave();
                }
                return 0;
            case 0x202:
                if (pressedItem >= 0) { CompleteIconGesture(); return 0; }
                if (resizeEdges != 0) { ResizePointer(force: true); resizeEdges = 0; ReleaseCapture(); CommitLayout(); return 0; }
                if (scrolling) { scrolling = false; ReleaseCapture(); UpdateHoverAfterMove(); return 0; }
                if (dragging) { dragging = false; ReleaseCapture(); CommitLayout(); }
                return 0;
            case 0x20A:
                ScrollBy(-(short)((wParam.ToInt64() >> 16) & 0xffff) / 120d * 94);
                return 0;
            case 0x205:
                var contextItem = HitItem(x, y);
                if (contextItem >= 0 && ItemMenuRequested != null)
                {
                    if (IsInteracting) return 0;
                    selected = contextItem; itemMenuOpen = true; Render();
                    try { ItemMenuRequested.Invoke(items[contextItem].Path); }
                    catch { CloseItemMenu(); throw; }
                    return 0;
                }
                ManageRequested?.Invoke(); return 0;
            case 0x215:
                if (pressedItem >= 0) { CancelIconGesture(); UpdateHoverAfterMove(); }
                var wasScrolling = scrolling;
                scrolling = false;
                if (dragging || resizeEdges != 0) { dragging = false; resizeEdges = 0; CommitLayout(); }
                else if (wasScrolling) UpdateHoverAfterMove();
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

    private int HitResize(int x, int y)
    {
        if (layoutLocked || x < 0 || y < 0 || x >= Width || y >= Height) return 0;
        // The visible diagonal sits inside the thin edge resize margin. Give
        // the entire corner its own DPI-scaled grip for resizing both axes.
        if (IsExpanded && x >= Width - 24 * scale && y >= Height - 24 * scale) return 2 | 8;
        var margin = 6 * scale;
        var horizontal = x < margin ? 1 : x >= Width - margin ? 2 : 0;
        var vertical = !IsExpanded ? 0 : y < margin ? 4 : y >= Height - margin ? 8 : 0;
        return horizontal | vertical;
    }

    private void ScrollAt(int y)
    {
        if (MaxScroll <= 0) return;
        var next = Math.Clamp((y / scale - 57) / Math.Max(1, logicalHeight - 68), 0, 1) * MaxScroll;
        ScrollBy(next - scrollOffset);
    }

    private void ResizePointer(bool force = false)
    {
        if (!force && Environment.TickCount64 - lastResize < 16) return;
        lastResize = Environment.TickCount64;
        DesktopAttachment.GetCursorPos(out var cursor);
        var dx = cursor.X - dragStart.X; var dy = cursor.Y - dragStart.Y;
        var rect = gestureStart;
        if ((resizeEdges & 1) != 0) rect = rect with { X = rect.X + dx, Width = rect.Width - dx };
        if ((resizeEdges & 2) != 0) rect = rect with { Width = rect.Width + dx };
        if ((resizeEdges & 4) != 0) rect = rect with { Y = rect.Y + dy, Height = rect.Height - dy };
        if ((resizeEdges & 8) != 0) rect = rect with { Height = rect.Height + dy };
        var width = Math.Clamp(rect.Width, 244 * scale, 1400 * scale);
        var height = IsExpanded ? Math.Clamp(rect.Height, 160 * scale, 1100 * scale) : 46 * scale;
        rect = rect with { X = (resizeEdges & 1) != 0 ? gestureStart.Right - width : rect.X,
            Y = (resizeEdges & 4) != 0 ? gestureStart.Bottom - height : rect.Y, Width = width, Height = height };
        rect = TransformLayout?.Invoke(rect, resizeEdges) ?? rect;
        var point = new NativeDesktop.Point { X = (int)Math.Round(rect.X), Y = (int)Math.Round(rect.Y) };
        DesktopAttachment.ScreenToClient(host, ref point);
        X = point.X; Y = point.Y;
        logicalWidth = Math.Clamp(rect.Width / scale, 244, 1400);
        if (IsExpanded) logicalHeight = Math.Clamp(rect.Height / scale, 160, 1100);
        displayHeight = animationFrom = animationTo = IsExpanded ? logicalHeight : 46;
        animationTimer.Stop(); scrollOffset = Math.Clamp(scrollOffset, 0, MaxScroll);
        screenPosition = new NativeDesktop.Point { X = (int)Math.Round(rect.X), Y = (int)Math.Round(rect.Y) };
        Render();
    }

    private void CommitLayout()
    {
        InteractionEnded?.Invoke();
        EnsureAttached(true); moved(X, Y); LayoutCommitted?.Invoke(); UpdateHoverAfterMove();
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
        if (!IsAttached) return;
        DesktopAttachment.GetCursorPos(out var point);
        DesktopAttachment.ScreenToClient(Handle, ref point);
        if (point.X < 0 || point.X >= Width || point.Y < 0 || point.Y >= Height) PointerLeave();
        else { PointerMove(point.X, point.Y); TrackPointerLeave(); }
    }

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
        if (dragging || resizeEdges != 0 || scrolling) { dragging = false; resizeEdges = 0; scrolling = false; ReleaseCapture(); InteractionEnded?.Invoke(); }
        if (dropRegistered) { RevokeDragDrop(Handle); dropRegistered = false; }
        dropTarget = null;
        if (Handle != 0) { var handle = Handle; Handle = 0; Instances.Remove(handle); if (destroy && IsWindow(handle)) DestroyWindow(handle); }
        fileDrag = tracking = hoveringOpen = false; hovered = -1;
        displayHeight = animationFrom = animationTo = folded ? 46 : logicalHeight;
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
