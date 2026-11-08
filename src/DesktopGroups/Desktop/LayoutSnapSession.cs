namespace DesktopGroups;

internal readonly record struct LayoutRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}
internal sealed record SnapResult(LayoutRect Rect, double? VerticalGuide, double? HorizontalGuide);

// One stateful session per pointer gesture; hysteresis prevents oscillation.
internal sealed class LayoutSnapSession
{
    private double? heldX, heldY;
    private int sourceX, sourceY;
    public SnapResult Resolve(LayoutRect moving, LayoutRect work, IReadOnlyList<LayoutRect> neighbors,
        bool enabled, bool bypass, double scale = 1, int edges = 0)
    {
        if (!enabled || bypass) { heldX = heldY = null; return new(moving, null, null); }
        var references = neighbors.Append(work).ToArray();
        var xCandidates = references.SelectMany(r => new[] { (r.X, false), (r.Right, false), (r.X + r.Width / 2, true) }).ToArray();
        var yCandidates = references.SelectMany(r => new[] { (r.Y, false), (r.Bottom, false), (r.Y + r.Height / 2, true) }).ToArray();
        var xOffsets = edges == 0 ? new[] { (0d, false), (moving.Width, false), (moving.Width / 2, true) }
            : (edges & 1) != 0 ? [(0d, false)] : (edges & 2) != 0 ? [(moving.Width, false)] : Array.Empty<(double, bool)>();
        var yOffsets = edges == 0 ? new[] { (0d, false), (moving.Height, false), (moving.Height / 2, true) }
            : (edges & 4) != 0 ? [(0d, false)] : (edges & 8) != 0 ? [(moving.Height, false)] : Array.Empty<(double, bool)>();
        var dx = Axis(moving.X, xOffsets, xCandidates, ref heldX, ref sourceX, 10 * scale, 18 * scale);
        var dy = Axis(moving.Y, yOffsets, yCandidates, ref heldY, ref sourceY, 10 * scale, 18 * scale);
        var rect = edges == 0 ? moving with { X = moving.X + dx, Y = moving.Y + dy }
            : moving with
            {
                X = moving.X + ((edges & 1) != 0 ? dx : 0),
                Y = moving.Y + ((edges & 4) != 0 ? dy : 0),
                Width = moving.Width + ((edges & 1) != 0 ? -dx : (edges & 2) != 0 ? dx : 0),
                Height = moving.Height + ((edges & 4) != 0 ? -dy : (edges & 8) != 0 ? dy : 0)
            };
        return new(rect, heldX, heldY);
    }
    private static double Axis(double origin, (double Offset, bool Center)[] offsets, (double Position, bool Center)[] targets,
        ref double? held, ref int source, double enter, double leave)
    {
        if (offsets.Length == 0) { held = null; return 0; }
        if (held.HasValue && source < offsets.Length && Math.Abs(origin + offsets[source].Offset - held.Value) <= leave)
            return held.Value - origin - offsets[source].Offset;
        held = null;
        var best = enter + .001; var delta = 0d;
        for (var index = 0; index < offsets.Length; index++)
            foreach (var target in targets)
            {
                var offset = offsets[index];
                if (offset.Center != target.Center) continue;
                var distance = target.Position - origin - offset.Offset;
                if (Math.Abs(distance) >= best) continue;
                best = Math.Abs(distance); delta = distance; held = target.Position; source = index;
            }
        return delta;
    }
}
