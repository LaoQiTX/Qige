using System.Windows;

namespace DesktopGroups;

// Exercises the actual desktop child HWNDs and the same queue as pointer drag.
// No desktop items are selected or repositioned by this check.
internal static class NativeRenderCheck
{
    public static int Run(ProjectStorage storage)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Startup += (_, _) => application.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await CheckAsync(storage);
                application.Shutdown(0);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.Message);
                application.Shutdown(1);
            }
        }));
        return application.Run();
    }

    private static async Task CheckAsync(ProjectStorage storage)
    {
        var errors = new List<string>();
        (int X, int Y)? iconTarget = null;
        var group = new NativeGroupTestWindow(message => errors.Add(message),
            (x, y, _) => iconTarget = (x, y));
        var startX = group.NativeX;
        var startY = group.NativeY;
        try
        {
            group.Show();
            errors.Clear(); // The normal creation status is informational.
            await Task.Delay(60);
            var initial = group.Inspect();
            if (initial.FramePaint.PaintCount == 0 || initial.BackdropPaint?.PaintCount is not > 0)
                throw new InvalidOperationException("分区尚未完成初始绘制。");
            for (var index = 0; index < 120; index++)
            {
                var x = startX + index * 2;
                var y = startY + (index % 20) * 2;
                group.QueueMove(x - 1, y - 1);
                group.QueueMove(x, y);
                await Task.Delay(8);
                // Flush the queue before observing; an idle pointer uses this same path.
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                if (errors.Count != 0) throw new InvalidOperationException(string.Join("；", errors));
                AssertPosition(group.Inspect(), initial, x - startX, y - startY);
            }
            // Release must flush a position even when the render callback has not run.
            group.QueueMove(startX + 48, startY + 24);
            group.CompleteMove();
            await Task.Delay(40);
            var final = group.Inspect();
            AssertPosition(final, initial, 48, 24);
            if (iconTarget != (startX + 48, startY + 24))
                throw new InvalidOperationException("松开后图标目标未到达最后位置。");
            if (errors.Count != 0) throw new InvalidOperationException(string.Join("；", errors));
            var frameRepaints = final.FramePaint.PaintCount - initial.FramePaint.PaintCount;
            var backdropRepaints = final.BackdropPaint!.PaintCount - initial.BackdropPaint!.PaintCount;
            if (final.FramePaint.RegionBuildCount != initial.FramePaint.RegionBuildCount ||
                final.FramePaint.FontBuildCount != initial.FramePaint.FontBuildCount)
                throw new InvalidOperationException("移动期间重复创建了边框区域或字体。");
            storage.WriteJson("TestData/NativeRender/results.json", new
            {
                Samples = 120, final.SurfaceMoves, final.IconUpdates,
                FrameRepaints = frameRepaints, BackdropRepaints = backdropRepaints,
                RegionRebuilds = 0, FontRebuilds = 0, PairAligned = true, ReleaseFlushed = true,
                RepositionedDesktopItems = false,
                VisualFlickerAccepted = false,
                Note = "原生窗口位置与重绘检查；最终合成闪烁仍需桌面观察。"
            });
            Console.WriteLine($"PASS 120 desktop frame moves; paired surfaces aligned; release flushed; paints {frameRepaints}/{backdropRepaints}; no region/font rebuilds; no desktop items moved");
        }
        finally
        {
            group.QueueMove(startX, startY);
            group.CompleteMove();
            group.Close();
        }
    }

    private static void AssertPosition(GroupRenderState actual, GroupRenderState initial, int dx, int dy)
    {
        if (!actual.Frame.Visible || actual.Backdrop is not { Visible: true } backdrop ||
            actual.Frame.X != initial.Frame.X + dx || actual.Frame.Y != initial.Frame.Y + dy ||
            backdrop.X != actual.Frame.X || backdrop.Y != actual.Frame.Y ||
            backdrop.Width != actual.Frame.Width || backdrop.Height != actual.Frame.Height ||
            actual.Frame.Width != initial.Frame.Width || actual.Frame.Height != initial.Frame.Height)
            throw new InvalidOperationException($"移动后背景与标题的位置、尺寸或可见状态不一致。期望偏移 {dx},{dy}；初始 {System.Text.Json.JsonSerializer.Serialize(initial)}；实际 {System.Text.Json.JsonSerializer.Serialize(actual)}");
    }
}
