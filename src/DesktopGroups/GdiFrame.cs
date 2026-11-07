using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DesktopGroups;

// No WPF/Direct3D surface is parented across processes. Windows paints these
// ordinary native children using GDI, including after Explorer invalidates them.
internal sealed class GdiFrame : IDisposable
{
    private const string WindowClass = "DesktopGroups.GdiFrame.v1";
    private static readonly WindowProcedure Procedure = Dispatch;
    private static readonly Dictionary<nint, GdiFrame> Instances = [];
    private static bool registered;
    private readonly bool backdrop;
    private readonly Action<int, int>? dragged;
    private readonly Action? dragCompleted;
    private bool dragging;
    private NativeDesktop.Point dragStart;
    private int startX, startY;
    private int width, height, header;
    private nint font;
    public nint Handle { get; private set; }
    public int X { get; private set; }
    public int Y { get; private set; }
    public int PaintCount { get; private set; }
    public int RegionBuildCount { get; private set; }
    public int FontBuildCount { get; private set; }

    public GdiFrame(nint parent, bool backdrop, Action<int, int>? dragged = null, Action? dragCompleted = null)
    {
        this.backdrop = backdrop;
        this.dragged = dragged;
        this.dragCompleted = dragCompleted;
        Register();
        // Explorer can composite layered children while ordinary GDI children
        // report valid paint pixels yet remain absent from its desktop surface.
        // Both surfaces must use the same layered composition path.
        var extended = 0x08080000u | (backdrop ? 0x20u : 0u); // NOACTIVATE | LAYERED, optional TRANSPARENT
        Handle = CreateWindowEx(extended, WindowClass, backdrop ? "DesktopGroups.Backdrop" : "DesktopGroups.Drawer",
            0x44000000, 80, 140, 360, 280, parent, 0, GetModuleHandle(null), 0); // CHILD | CLIPSIBLINGS
        if (Handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "创建原生分区失败。");
        Instances.Add(Handle, this);
        if (!SetLayeredWindowAttributes(Handle, 0, backdrop ? (byte)100 : (byte)255, 2))
        {
            var error = Marshal.GetLastWin32Error();
            Dispose();
            throw new Win32Exception(error, "分区背景透明度设置失败。");
        }
    }

    public void Place(int x, int y, int width, int height, int header)
    {
        var sizeChanged = this.width != width || this.height != height || this.header != header;
        X = x; Y = y; this.width = width; this.height = height; this.header = header;
        if (!backdrop && sizeChanged)
        {
            DesktopAttachment.Hollow(Handle, width, height, header, 3);
            RegionBuildCount++;
            CreateTitleFont();
        }
        DesktopAttachment.Place(Handle, x, y, width, height);
        if (sizeChanged) DesktopAttachment.Redraw(Handle);
    }

    public void RememberPosition(int x, int y) { X = x; Y = y; }

    public NativePaintState InspectPaint()
    {
        var dc = GetDC(Handle);
        if (dc == 0) return new(PaintCount, null, null, false, RegionBuildCount, FontBuildCount);
        try
        {
            var first = GetPixel(dc, 10, 10);
            var second = GetPixel(dc, Math.Max(1, width / 2), backdrop ? Math.Max(header + 10, height / 2) : 10);
            var expected = backdrop ? 0x002B3F21u : 0x00272F1Fu;
            return new(PaintCount, first == uint.MaxValue ? null : first,
                second == uint.MaxValue ? null : second, PaintCount > 0 && first == expected, RegionBuildCount, FontBuildCount);
        }
        finally { ReleaseDC(Handle, dc); }
    }

    private void Paint()
    {
        var dc = BeginPaint(Handle, out var paint);
        if (dc == 0) return;
        try
        {
            var rect = new NativeDesktop.Rect { Right = width, Bottom = height };
            var background = CreateSolidBrush(backdrop ? 0x002B3F21u : 0x00272F1Fu);
            try { FillRect(dc, ref rect, background); }
            finally { DeleteObject(background); }
            if (!backdrop)
            {
                var edge = CreateSolidBrush(0x00AED19Fu);
                try
                {
                    var edgeRect = rect;
                    FrameRect(dc, ref edgeRect, edge);
                    edgeRect.Left++; edgeRect.Top++; edgeRect.Right--; edgeRect.Bottom--;
                    FrameRect(dc, ref edgeRect, edge);
                    edgeRect.Left++; edgeRect.Top++; edgeRect.Right--; edgeRect.Bottom--;
                    FrameRect(dc, ref edgeRect, edge);
                }
                finally { DeleteObject(edge); }
                SetBkMode(dc, 1);
                SetTextColor(dc, 0x00FFFFFF);
                var previousFont = font == 0 ? 0 : SelectObject(dc, font);
                var textRect = new NativeDesktop.Rect { Left = 15, Top = 0, Right = width - 12, Bottom = header };
                const string title = "桌面分区  ·  拖动标题移动";
                DrawText(dc, title, -1, ref textRect, 0x04 | 0x20 | 0x8000); // VCENTER, SINGLELINE, END_ELLIPSIS
                if (previousFont != 0) SelectObject(dc, previousFont);
            }
            PaintCount++;
        }
        finally { EndPaint(Handle, ref paint); }
    }

    private void CreateTitleFont()
    {
        var newFont = CreateFont(-Math.Max(13, header * 13 / 50), 0, 0, 0, 400,
            0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
        if (newFont == 0) return;
        if (font != 0) DeleteObject(font);
        font = newFont;
        FontBuildCount++;
    }

    private static nint Dispatch(nint window, uint message, nint wParam, nint lParam)
    {
        if (!Instances.TryGetValue(window, out var frame)) return DefWindowProc(window, message, wParam, lParam);
        switch (message)
        {
            case 0x0F: frame.Paint(); return 0; // WM_PAINT
            case 0x14: return 1; // WM_ERASEBKGND: fully handled in Paint
            case 0x21: return 3; // WM_MOUSEACTIVATE: MA_NOACTIVATE
            case 0x201: // WM_LBUTTONDOWN
                if (!frame.backdrop && (short)((lParam.ToInt64() >> 16) & 0xFFFF) < frame.header)
                {
                    DesktopAttachment.GetCursorPos(out frame.dragStart);
                    frame.startX = frame.X; frame.startY = frame.Y; frame.dragging = true;
                    SetCapture(window);
                }
                return 0;
            case 0x200: // WM_MOUSEMOVE
                if (frame.dragging)
                {
                    DesktopAttachment.GetCursorPos(out var current);
                    frame.dragged?.Invoke(frame.startX + current.X - frame.dragStart.X, frame.startY + current.Y - frame.dragStart.Y);
                }
                return 0;
            case 0x202: // WM_LBUTTONUP
                if (frame.dragging) { frame.dragging = false; ReleaseCapture(); frame.dragCompleted?.Invoke(); }
                return 0;
            case 0x215: // WM_CAPTURECHANGED: also settle when another window takes capture.
                if (frame.dragging) { frame.dragging = false; frame.dragCompleted?.Invoke(); }
                return 0;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    private static void Register()
    {
        if (registered) return;
        var definition = new WindowClassDefinition
        {
            Size = (uint)Marshal.SizeOf<WindowClassDefinition>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(Procedure), Instance = GetModuleHandle(null),
            ClassName = WindowClass, Cursor = LoadCursor(0, (nint)32512)
        };
        if (RegisterClassEx(ref definition) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        registered = true;
    }

    public void Dispose()
    {
        if (Handle != 0)
        {
            var window = Handle;
            Handle = 0;
            Instances.Remove(window);
            DestroyWindow(window);
        }
        if (font != 0) { DeleteObject(font); font = 0; }
    }

    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassDefinition
    {
        public uint Size, Style;
        public nint Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public nint DeviceContext;
        [MarshalAs(UnmanagedType.Bool)] public bool Erase;
        public NativeDesktop.Rect Paint;
        [MarshalAs(UnmanagedType.Bool)] public bool Restore;
        [MarshalAs(UnmanagedType.Bool)] public bool Incremental;
        public int Reserved1, Reserved2, Reserved3, Reserved4, Reserved5, Reserved6, Reserved7, Reserved8;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClassDefinition definition);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint BeginPaint(nint window, out PaintStruct paint);
    [DllImport("user32.dll")] private static extern bool EndPaint(nint window, ref PaintStruct paint);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref NativeDesktop.Rect rect, nint brush);
    [DllImport("user32.dll")] private static extern int FrameRect(nint dc, ref NativeDesktop.Rect rect, nint brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawText(nint dc, string text, int count, ref NativeDesktop.Rect rect, uint format);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern nint LoadCursor(nint instance, nint cursor);
    [DllImport("user32.dll")] private static extern nint SetCapture(nint window);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(nint window, uint key, byte alpha, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charset, uint outputPrecision, uint clipPrecision, uint quality, uint pitch, string face);
}

internal sealed record NativePaintState(int PaintCount, uint? HeaderPixel, uint? SecondPixel,
    bool ExpectedColorObserved, int RegionBuildCount, int FontBuildCount);
