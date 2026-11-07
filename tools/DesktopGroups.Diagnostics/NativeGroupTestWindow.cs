using System.Windows.Threading;

namespace DesktopGroups;

internal sealed class NativeGroupTestWindow(Action<string> report, Action<int, int, int> reposition)
{
    private GdiFrame? frame, backdrop;
    private nint host;
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private (int X, int Y)? pendingMove;
    private DispatcherOperation? scheduledMove;
    private long lastIconMove;
    private bool iconsPending;
    private int surfaceMoves, iconUpdates;
    public event EventHandler? Closed;
    public int NativeX { get; private set; } = 80;
    public int NativeY { get; private set; } = 140;
    public int NativeWidth { get; private set; } = 360;
    public int NativeHeight { get; private set; } = 280;
    public bool IsOpen => frame is { Handle: not 0 };

    public void Show()
    {
        if (IsOpen) { Reveal(); return; }
        try
        {
            host = DesktopAttachment.FindIconHost();
            var hostInfo = NativeDesktop.InspectWindows().Single(item => item.Handle == host.ToInt64());
            var dpi = DesktopAttachment.Dpi(host) / 96.0;
            NativeWidth = (int)Math.Round(360 * dpi);
            NativeHeight = (int)Math.Round(280 * dpi);
            NativeX = Math.Clamp(NativeX, 0, Math.Max(0, hostInfo.Width - NativeWidth));
            NativeY = Math.Clamp(NativeY, 0, Math.Max(0, hostInfo.Height - NativeHeight));
            backdrop = new GdiFrame(host, backdrop: true);
            frame = new GdiFrame(host, backdrop: false, QueueMove, CompleteMove);
            Place();
            report("已创建原生绘制分区：深绿色标题、浅绿色边框和半透明背景。点击“定位分区”回桌面查看。");
        }
        catch { Close(); throw; }
    }

    public void Reveal()
    {
        if (!IsOpen) throw new InvalidOperationException("分区已关闭，请重新创建。");
        Place();
    }

    public GroupRenderState Inspect()
    {
        if (!IsOpen) throw new InvalidOperationException("分区尚未创建。");
        return new("Win32/GDI", DesktopAttachment.ReadState(frame!.Handle), frame.InspectPaint(),
            backdrop == null ? null : DesktopAttachment.ReadState(backdrop.Handle), backdrop?.InspectPaint(),
            surfaceMoves, iconUpdates);
    }

    public void Close()
    {
        pendingMove = null;
        scheduledMove?.Abort(); scheduledMove = null;
        iconsPending = false;
        frame?.Dispose(); frame = null;
        backdrop?.Dispose(); backdrop = null;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    internal void QueueMove(int x, int y)
    {
        if (!IsOpen) return;
        pendingMove = (x, y);
        if (scheduledMove != null) return;
        scheduledMove = dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            scheduledMove = null;
            ApplyPendingMove(final: false);
        }));
    }

    internal void CompleteMove()
    {
        scheduledMove?.Abort(); scheduledMove = null;
        ApplyPendingMove(final: true);
    }

    private void ApplyPendingMove(bool final)
    {
        if (!IsOpen || backdrop == null) return;
        try
        {
            if (pendingMove is { } target)
            {
                pendingMove = null;
                if (target.X != NativeX || target.Y != NativeY)
                {
                    DesktopAttachment.MoveTogether(backdrop.Handle, frame!.Handle, target.X, target.Y);
                    NativeX = target.X; NativeY = target.Y;
                    backdrop.RememberPosition(NativeX, NativeY);
                    frame.RememberPosition(NativeX, NativeY);
                    surfaceMoves++;
                    iconsPending = true;
                }
            }
            // Shell enumeration is much more expensive than moving two cached
            // surfaces. Merge high-rate mouse events; always settle on release.
            var now = Environment.TickCount64;
            if (iconsPending && (final || now - lastIconMove >= 33))
            {
                lastIconMove = now;
                reposition(NativeX, NativeY, NativeWidth);
                iconUpdates++;
                iconsPending = false;
            }
        }
        catch (Exception exception)
        {
            var code = exception is System.ComponentModel.Win32Exception native ? $"（Win32 {native.NativeErrorCode}）" : "";
            report($"分区移动未完成：{exception.Message}{code}");
        }
    }

    private void Place()
    {
        var header = (int)Math.Round(50 * DesktopAttachment.Dpi(host) / 96.0);
        // Raise the tint first, then the opaque title/border above it. Neither is
        // TOPMOST; both stay children of Explorer's desktop icon view.
        backdrop?.Place(NativeX, NativeY, NativeWidth, NativeHeight, header);
        frame?.Place(NativeX, NativeY, NativeWidth, NativeHeight, header);
    }
}

internal sealed record GroupRenderState(string Renderer, GroupWindowState Frame,
    NativePaintState FramePaint, GroupWindowState? Backdrop, NativePaintState? BackdropPaint,
    int SurfaceMoves, int IconUpdates);
