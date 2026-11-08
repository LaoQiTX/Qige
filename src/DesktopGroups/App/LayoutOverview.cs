using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopGroups;

// A live map, not a desktop screenshot. It uses the same stored screen coordinates as the panels.
internal sealed class LayoutOverview : FrameworkElement
{
    public IReadOnlyList<(string Id, string Name, LayoutRect Rect)> Drawers { get; set; } = [];
    public IReadOnlyList<DesktopMonitor> Monitors { get; set; } = [];
    public string? SelectedId { get; set; }
    public event Action<string>? Selected;
    private readonly List<(string Id, Rect Bounds)> hitAreas = [];
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); hitAreas.Clear();
        dc.DrawRoundedRectangle(AppPalette.Sidebar, null, new Rect(0, 0, ActualWidth, ActualHeight), 8, 8);
        if (Monitors.Count == 0) return;
        var x = Monitors.Min(m => m.Work.X); var y = Monitors.Min(m => m.Work.Y);
        var width = Monitors.Max(m => m.Work.Right) - x; var height = Monitors.Max(m => m.Work.Bottom) - y;
        var scale = Math.Min((ActualWidth - 48) / Math.Max(1, width), (ActualHeight - 32) / Math.Max(1, height));
        var ox = (ActualWidth - width * scale) / 2; var oy = (ActualHeight - height * scale) / 2;
        Rect Map(double px, double py, double w, double h) => new(ox + (px - x) * scale, oy + (py - y) * scale, w * scale, h * scale);
        foreach (var m in Monitors)
            dc.DrawRoundedRectangle(AppPalette.Surface, new Pen(AppPalette.Border, 1),
                Map(m.Work.X, m.Work.Y, m.Work.Width, m.Work.Height), 4, 4);
        foreach (var drawer in Drawers)
        {
            var bounds = Map(drawer.Rect.X, drawer.Rect.Y, drawer.Rect.Width, drawer.Rect.Height);
            var chosen = drawer.Id == SelectedId;
            dc.DrawRoundedRectangle(chosen ? AppPalette.Selected : AppPalette.Control,
                new Pen(chosen ? AppPalette.Focus : AppPalette.ScrollThumb, chosen ? 1.5 : 1), bounds, 3, 3);
            if (bounds.Width > 36 && bounds.Height > 18)
            {
                var text = new FormattedText(drawer.Name, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                    new Typeface("Microsoft YaHei UI"), 10, AppPalette.Text, VisualTreeHelper.GetDpi(this).PixelsPerDip)
                { MaxTextWidth = Math.Max(1, bounds.Width - 10), MaxTextHeight = Math.Max(1, bounds.Height - 6), Trimming = TextTrimming.CharacterEllipsis };
                dc.DrawText(text, new Point(bounds.X + 5, bounds.Y + 3));
            }
            hitAreas.Add((drawer.Id, bounds));
        }
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var hit = hitAreas.LastOrDefault(a => a.Bounds.Contains(e.GetPosition(this)));
        if (hit.Id != null) Selected?.Invoke(hit.Id);
        base.OnMouseLeftButtonDown(e);
    }
}
