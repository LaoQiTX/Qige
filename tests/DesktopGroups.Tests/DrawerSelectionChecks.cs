using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace DesktopGroups;

internal static class DrawerSelectionChecks
{
    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    internal static int Run(AppStorage storage)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(new Action(() =>
        {
            try { CheckNativeSelection(storage); app.Shutdown(0); }
            catch (Exception error) { Console.Error.WriteLine(error); app.Shutdown(1); }
        }));
        return app.Run();
    }

    private static void CheckNativeSelection(AppStorage storage)
    {
        static void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            Console.WriteLine("PASS " + label);
        }

        DrawerAppearance.GlassEnabled = false; // The isolated test does not need desktop wallpaper COM access.
        using var host = new HwndSource(new HwndSourceParameters("Qige isolated drawer selection test")
            { WindowStyle = unchecked((int)0x80000000), Width = 1440, Height = 900 });
        var errors = new List<string>();
        var monitor = new DesktopMonitor("test", new(0, 0, 1440, 900), new(0, 0, 1440, 900), 96);
        using var panel = new ShortcutPanel(errors.Add, (_, _) => { }, _ => false, findHost: () => host.Handle,
            captureMonitors: () => [monitor]);
        var entries = Enumerable.Range(0, 24).Select(index => ($"File {index}", $"file{index}.txt", (BitmapSource?)null)).ToArray();
        panel.SetPreparedItems(entries);
        panel.Show(100, 100);
        Check(panel.IsAttached && panel.Columns == 5, "isolated layered drawer opens without using Explorer's desktop");

        void Mouse(uint message, int x, int y, int keys = 0) => SendMessage(panel.Handle, message, keys, (nint)((y << 16) | (x & 0xffff)));
        void Box(int x, int y, bool control = false)
        {
            Mouse(0x201, 11, 55, control ? 9 : 1);
            Mouse(0x200, x, y, control ? 9 : 1);
            Mouse(0x202, x, y, control ? 8 : 0);
        }
        bool IsSelected(params int[] expected) => panel.SelectionPaths().SequenceEqual(expected.Select(index => entries[index].Item2));

        Mouse(0x201, 11, 55, 1);
        Mouse(0x200, 160, 130, 1);
        Check(IsSelected(0, 1) && panel.IsInteracting, "rubber band highlights the intersecting cells while held");
        panel.SavePreview(storage.Resolve("drawer-selection.png"));
        Mouse(0x202, 160, 130);
        Check(IsSelected(0, 1) && !panel.IsInteracting, "releasing the marquee keeps both items selected");

        string[] menuPaths = [];
        panel.ItemMenuRequested += paths => { menuPaths = paths.ToArray(); panel.CloseItemMenu(); };
        Mouse(0x205, 120, 80);
        Check(menuPaths.SequenceEqual(["file0.txt", "file1.txt"]) && IsSelected(0, 1), "right-clicking a selected icon dispatches the entire selection");
        panel.SetPreparedItems(entries);
        Check(IsSelected(0, 1), "an asynchronous icon refresh keeps the selection");

        string[] draggedPaths = [];
        panel.DropOnAnotherPanel = (paths, _) => { draggedPaths = paths.ToArray(); return true; };
        Mouse(0x201, 120, 80, 1);
        Mouse(0x200, -120, 110, 1);
        Mouse(0x202, -120, 110);
        Check(draggedPaths.SequenceEqual(["file0.txt", "file1.txt"]) && IsSelected(0, 1), "dragging a selected icon dispatches both items in visual order instead of just the pressed icon");

        int target = -1;
        panel.ReorderItems = (paths, index) => { draggedPaths = paths.ToArray(); target = index; return true; };
        Check(panel.TryReorderAt(panel.SelectionPaths(), 340, 80) && target == 4 && draggedPaths.SequenceEqual(["file0.txt", "file1.txt"]),
            "the same-drawer drop handler receives the entire selection and grid target");
        int[] order = [0, 1, 2, 3, 4, 5];
        Check(StorageWindow.RepositionSelection(order, i => i is 1 or 3, 5).SequenceEqual([0, 2, 4, 5, 1, 3]) &&
            StorageWindow.RepositionSelection(order, i => i is 2 or 4, 0).SequenceEqual([2, 4, 0, 1, 3, 5]) &&
            StorageWindow.RepositionSelection(order, i => i == 0, 1).SequenceEqual([1, 0, 2, 3, 4, 5]) &&
            StorageWindow.RepositionSelection(order, i => i is 1 or 3, 3).SequenceEqual(order),
            "reordering moves a stable block forward or backward, preserves single-item behavior, and ignores its own selected cells");

        Mouse(0x201, 190, 80, 9); Mouse(0x202, 190, 80, 8);
        Check(IsSelected(0, 1, 2), "Ctrl+click adds a file without losing the frame selection");
        Mouse(0x201, 120, 80, 9); Mouse(0x202, 120, 80, 8);
        Check(IsSelected(0, 2), "Ctrl+click toggles the existing selection");
        Mouse(0x201, 11, 55, 1); Mouse(0x200, 80, 130, 1);
        SendMessage(panel.Handle, 0x1F, 0, 0);
        Check(IsSelected(0, 2) && !panel.IsInteracting, "cancelling the marquee restores the prior selection and releases interaction");
        Mouse(0x201, 190, 80, 1); Mouse(0x202, 190, 80);
        Check(IsSelected(2), "clicking a selected icon without dragging collapses the selection to that item");
        Mouse(0x201, 11, 55, 1); Mouse(0x202, 11, 55);
        Check(IsSelected(), "clicking blank space clears the selection");

        Mouse(0x201, 190, 80, 1); Mouse(0x202, 190, 80);
        Box(160, 130, control: true);
        Check(IsSelected(0, 1, 2), "Ctrl+marquee adds multiple cells to a prior selection");
        panel.ScrollBy(panel.RowHeight);
        Box(160, 130);
        Check(IsSelected(5, 6), "marquee hit testing includes the current scroll offset");
        panel.SetPreparedItems(entries.Where((_, i) => i != 5));
        Check(IsSelected(6), "removing one of the selected paths does not clear the remaining selected file");
        monitor = monitor with { Dpi = 144 };
        panel.SetPreparedItems(entries);
        panel.ScrollBy(-panel.ScrollOffset);
        panel.RestorePreferences(new ShortcutProfile { Folded = true, ManualExpansion = true, ManualCollapsed = false });
        panel.ApplyScreenLayout(120, 831, 420, 352, commit: false);
        Check(panel.ExpandsUpward && panel.IsExpanded && panel.Width == 630, "selection fixture opens upward at 150 percent DPI");
        Mouse(0x201, 240, 152, 1); // Blank gap just below the first row, in physical pixels.
        Mouse(0x200, 17, 13, 1);
        Mouse(0x202, 17, 13);
        Check(IsSelected(0, 1), "reverse marquee selects the right cells in an upward drawer at 150 percent DPI");
        Check(errors.Count == 0, "selection callbacks report no native window errors: " + string.Join("; ", errors));
    }
}
