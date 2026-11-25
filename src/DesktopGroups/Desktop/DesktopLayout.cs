namespace DesktopGroups;

// Keep the user's placement separately from the fitted live geometry. Repeated
// display changes must not accumulate rounding or overwrite the original layout.
internal sealed record DesktopLayoutReference(DesktopMonitor Monitor, int X, int Y, double Width, double Height);

internal static class DesktopLayout
{
    public static DesktopLayoutReference Capture(ShortcutProfile profile, IReadOnlyList<DesktopMonitor> monitors)
        => new(Target(profile.X, profile.Y, profile.Width, profile.Height, monitors),
            profile.X, profile.Y, profile.Width, profile.Height);

    public static PanelPlacement Resolve(ShortcutProfile profile, IReadOnlyList<DesktopMonitor> monitors)
    {
        var reference = profile.LayoutReference ?? Capture(profile, monitors);
        var source = reference.Monitor;
        var target = monitors.FirstOrDefault(m => m.Device == source.Device)
            ?? Target(reference.X, reference.Y, reference.Width, reference.Height, monitors);
        if (source.Work.Width <= 0 || source.Work.Height <= 0 || source.Dpi == 0)
            return DesktopPlacement.Resolve(profile.X, profile.Y, profile.Width, profile.Height, monitors, profile.Folded);

        var scale = target.Dpi / 96d;
        var oldScale = source.Dpi / 96d;
        // A DPI or taskbar-only change keeps the chosen logical size. A new
        // resolution fits the drawer's footprint to the new logical work area.
        var resolutionChanged = source.Bounds.Width != target.Bounds.Width || source.Bounds.Height != target.Bounds.Height;
        var width = Math.Min(target.Work.Width / scale, Math.Clamp(reference.Width *
            (resolutionChanged ? target.Work.Width / (double)source.Work.Width * oldScale / scale : 1), 244, 1400));
        var height = Math.Min(target.Work.Height / scale, Math.Clamp(reference.Height *
            (resolutionChanged ? target.Work.Height / (double)source.Work.Height * oldScale / scale : 1), 160, 1100));
        var x = Map(reference.X, source.Work.X, source.Work.Width, reference.Width * oldScale,
            target.Work.X, target.Work.Width, Math.Round(width * scale));
        var y = Map(reference.Y, source.Work.Y, source.Work.Height, (profile.Folded ? 46 : reference.Height) * oldScale,
            target.Work.Y, target.Work.Height, Math.Round((profile.Folded ? Math.Min(46, height) : height) * scale));
        return DesktopPlacement.Resolve(x, y, width, height, [target], profile.Folded);
    }

    private static int Map(int value, int oldOrigin, int oldLength, double oldSize, int origin, int length, double size)
    {
        var travel = oldLength - Math.Round(oldSize);
        var fraction = travel > 0 ? Math.Clamp((value - oldOrigin) / travel, 0, 1) : 0;
        return origin + (int)Math.Round(fraction * Math.Max(0, length - size));
    }

    private static DesktopMonitor Target(int x, int y, double width, double height, IReadOnlyList<DesktopMonitor> monitors)
    {
        var placed = DesktopPlacement.Resolve(x, y, width, height, monitors);
        return monitors.First(m => m.Dpi == placed.Dpi && placed.X >= m.Work.X && placed.X < m.Work.Right
            && placed.Y >= m.Work.Y && placed.Y < m.Work.Bottom);
    }
}
