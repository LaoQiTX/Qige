using System.Windows;

namespace DesktopGroups;

internal sealed partial class ShortcutPanel
{
    private HashSet<string> selectedPaths = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>? selectionBeforeBox;
    private bool additiveBox, boxDragging, keepSelectionOnClick;
    private Point? boxStart;
    private Rect box = Rect.Empty;

    private double SurfaceShift => expandsUpward ? logicalHeight - displayHeight : 0;

    private Rect ItemCell(int index) => new(14 + index % Columns * CellWidth,
        BodyTop + index / Columns * RowHeight - scrollOffset, CellWidth - 4, RowHeight - 6);

    private Rect SelectionViewport()
    {
        var top = Math.Max(expandsUpward ? 8 : 49, SurfaceShift);
        var bottom = Math.Min(logicalHeight - (expandsUpward ? 49 : 8), SurfaceShift + Height / scale);
        return bottom > top ? new Rect(10, top, logicalWidth - 20, bottom - top) : Rect.Empty;
    }

    internal string[] SelectionPaths() => items.Select(item => item.Path).Where(selectedPaths.Contains)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private void ReplaceItems(List<PanelItem> next)
    {
        // Icon-only refreshes must not reset a selection or a drag. A changed
        // list, however, can move the pressed index to a different file.
        if ((pressedItem >= 0 || boxStart.HasValue) && !items.Select(item => item.Path)
            .SequenceEqual(next.Select(item => item.Path), StringComparer.OrdinalIgnoreCase))
            CancelContentGesture(updateHover: false);
        selectedPaths.IntersectWith(next.Select(item => item.Path));
        items = next;
        hovered = -1;
        scrollOffset = Math.Clamp(scrollOffset, 0, MaxScroll);
        if (IsAttached) Render();
    }

    private void BeginContentGesture(int x, int y, bool control)
    {
        var index = HitItem(x, y);
        if (index >= 0)
        {
            var path = items[index].Path;
            if (control && selectedPaths.Remove(path)) { Render(); return; }
            if (!control && !selectedPaths.Contains(path)) selectedPaths.Clear();
            selectedPaths.Add(path);
            keepSelectionOnClick = control;
            pressedItem = index;
            iconStart = ToScreen(x, y);
        }
        else
        {
            var point = new Point(x / scale, y / scale + SurfaceShift);
            if (!IsExpanded || !SelectionViewport().Contains(point))
            {
                if (!control && selectedPaths.Count != 0) { selectedPaths.Clear(); Render(); }
                return;
            }
            selectionBeforeBox = new(selectedPaths, StringComparer.OrdinalIgnoreCase);
            additiveBox = control;
            boxStart = new(point.X, point.Y + scrollOffset);
            hovered = -1;
        }
        SetCapture(Handle);
        gestureTimer.Start();
        Render();
    }

    private static bool PassedDragThreshold(double x, double y) =>
        Math.Abs(x) >= GetSystemMetrics(68) || Math.Abs(y) >= GetSystemMetrics(69);

    private void UpdateBox(int x, int y)
    {
        if (boxStart is not { } start) return;
        var viewport = SelectionViewport();
        if (viewport.IsEmpty) return;
        var pointer = new Point(Math.Clamp(x / scale, viewport.Left, viewport.Right),
            Math.Clamp(y / scale + SurfaceShift, viewport.Top, viewport.Bottom) + scrollOffset);
        if (!boxDragging && !PassedDragThreshold((pointer.X - start.X) * scale, (pointer.Y - start.Y) * scale)) return;
        boxDragging = true;
        var area = new Rect(start, pointer);
        var next = additiveBox ? new HashSet<string>(selectionBeforeBox!, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        // Select only rows reached by the frame, including rows traversed by scrolling.
        var first = Math.Clamp((int)Math.Floor((area.Top - BodyTop) / RowHeight), 0, items.Count / Columns);
        var last = Math.Min((int)Math.Floor((area.Bottom - BodyTop) / RowHeight), items.Count / Columns);
        for (var index = first * Columns; index < items.Count && index / Columns <= last; index++)
        {
            var cell = ItemCell(index);
            cell.Offset(0, scrollOffset);
            if (area.Width > 0 && area.Height > 0 && area.Left < cell.Right && area.Right > cell.Left
                && area.Top < cell.Bottom && area.Bottom > cell.Top) next.Add(items[index].Path);
        }
        area.Offset(0, -scrollOffset);
        area.Intersect(viewport);
        if (box != area || !selectedPaths.SetEquals(next))
        {
            box = area;
            selectedPaths = next;
            Render();
        }
    }

    private void FinishBox(int x, int y)
    {
        UpdateBox(x, y);
        if (!boxDragging && !additiveBox) selectedPaths.Clear();
        ResetContentGesture();
        Render();
        UpdateHoverAfterMove();
    }

    private NativeDesktop.Point ToScreen(int x, int y)
    {
        var point = new NativeDesktop.Point { X = x, Y = y };
        ClientToScreen(Handle, ref point);
        return point;
    }

    private void MoveIconGesture(int x, int y)
    {
        if (ReturnToDesktop == null && ReorderItems == null && DropOnAnotherPanel == null) return;
        var cursor = ToScreen(x, y);
        if (!iconDragging && PassedDragThreshold(cursor.X - iconStart.X, cursor.Y - iconStart.Y))
        {
            iconDragging = true;
            iconGhost = new IconDragGhost(items[pressedItem].Icon, cursor, selectedPaths.Count);
        }
        iconGhost?.Move(cursor);
        if (!iconDragging) return;
        var target = ReorderIndex(x, y);
        if (hovered != target) { hovered = target; Render(); }
        var accepted = target >= 0 || IsDesktopAt(cursor) || Instances.Values.Any(panel => panel != this && panel.IsAtPoint(cursor));
        SetCursor(LoadCursor(0, (nint)(accepted ? 32512 : 32648)));
    }

    private void CompleteIconGesture(int x, int y)
    {
        var path = items[pressedItem].Path;
        var paths = iconDragging ? SelectionPaths() : [];
        var point = ToScreen(x, y);
        if (!iconDragging && !keepSelectionOnClick) { selectedPaths.Clear(); selectedPaths.Add(path); }
        ResetContentGesture(); // Remove the preview before hit-testing the drop target.
        if (paths.Length > 0)
        {
            if (IsAtPoint(point)) TryReorderAt(paths, x, y);
            else if (DropOnAnotherPanel?.Invoke(paths, point) == true) { }
            else if (IsDesktopAt(point)) ReturnToDesktop?.Invoke(paths);
            else report("已取消拖出：请拖到其他抽屉或桌面空白处松开。");
        }
        Render();
        UpdateHoverAfterMove();
    }

    private int ReorderIndex(int x, int y)
    {
        if (!IsExpanded || y < 0 || y >= Height || items.Count == 0 || ReorderItems == null || HitTitle(y)
            || x < 14 * scale || x >= (logicalWidth - 14) * scale) return -1;
        var fullY = y / scale + SurfaceShift;
        if (fullY < BodyTop || fullY > logicalHeight - (expandsUpward ? 49 : 8)) return -1;
        var column = (int)((x / scale - 14) / CellWidth);
        var row = (int)((fullY - BodyTop + scrollOffset) / RowHeight);
        return Math.Min(items.Count - 1, row * Columns + column);
    }

    internal bool TryReorderAt(IReadOnlyList<string> paths, int x, int y)
    {
        var index = ReorderIndex(x, y);
        return !InteractionBlocked && index >= 0 && ReorderItems?.Invoke(paths, index) == true;
    }

    private void CancelContentGesture(bool updateHover = true)
    {
        if (selectionBeforeBox != null) selectedPaths = selectionBeforeBox;
        ResetContentGesture();
        if (updateHover)
        {
            Render();
            UpdateHoverAfterMove();
        }
    }

    private void ResetContentGesture()
    {
        pressedItem = -1;
        iconDragging = keepSelectionOnClick = boxDragging = additiveBox = false;
        boxStart = null;
        selectionBeforeBox = null;
        box = Rect.Empty;
        gestureTimer.Stop();
        iconGhost?.Dispose(); iconGhost = null;
        if (Handle != 0 && GetCapture() == Handle) ReleaseCapture();
        SetCursor(LoadCursor(0, (nint)32512));
    }
}
