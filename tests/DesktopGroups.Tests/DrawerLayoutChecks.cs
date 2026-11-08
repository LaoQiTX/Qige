using System.IO;

namespace DesktopGroups;

internal static class DrawerLayoutChecks
{
    public static int Run(AppStorage storage)
    {
        var results = new List<string>();
        void Check(bool result, string name)
        {
            if (!result) throw new InvalidOperationException(name);
            results.Add("PASS " + name);
        }
        var desktop = storage.Resolve("Desktop"); Directory.CreateDirectory(desktop);
        var legacy = new ShortcutStore(storage, desktop);
        var original = Path.Combine(desktop, "旧分组.url");
        File.WriteAllText(original, "[InternetShortcut]\r\nURL=https://example.com/legacy\r\n");
        legacy.Add(original);
        var legacyPath = legacy.Profile.Entries.Single().StoredRelative;
        var catalog = new DrawerCatalog(storage, desktop);
        Check(catalog.Stores["default"].Active.Count == 1 && catalog.Stores["default"].Profile.Entries.Single().StoredRelative == legacyPath,
            "existing active beta profile is adopted without changing file paths or recovery state");
        var work = catalog.Create("工作");
        catalog.Rename(work.Id, "设计与开发");
        var store = catalog.Stores[work.Id];
        for (var i = 0; i < 24; i++)
        {
            var path = Path.Combine(desktop, $"入口 {i:00}.url");
            File.WriteAllText(path, $"[InternetShortcut]\r\nURL=https://example.com/items/{i}\r\n");
            catalog.EnsureOwnership(work.Id, [path]);
            store.Add(path);
        }
        Check(store.Active.Count == 24 && catalog.Stores["default"].Active.Count == 1, "drawers collect independently and support more than 12 items");
        var refused = false;
        try { catalog.EnsureOwnership(work.Id, [original]); } catch (IOException) { refused = true; }
        Check(refused, "the same desktop entry cannot belong to two drawers");
        store.Profile.X = -1200; store.Profile.Y = 180; store.Profile.Width = 616; store.Profile.Height = 448;
        store.Profile.Folded = true; store.Profile.Locked = true; store.Save();
        catalog.Settings.SnapEnabled = false; catalog.Settings.SelectedId = work.Id; catalog.Save();
        var reopened = new DrawerCatalog(storage, desktop);
        var layout = reopened.Stores[work.Id].Profile;
        Check(reopened.Settings.Drawers.Single(d => d.Id == work.Id).Name == "设计与开发" &&
            layout.X == -1200 && layout.Width == 616 && layout.Height == 448 && layout.Folded && layout.Locked &&
            !reopened.Settings.SnapEnabled, "names, negative coordinates, sizes, folding, locks and snap preferences survive restart");
        var first = store.Profile.Entries[0];
        File.WriteAllText(first.OriginalPath, "[InternetShortcut]\r\nURL=https://example.com/different\r\n");
        var conflictHash = VerifiedTransfer.Hash(first.OriginalPath);
        var issues = reopened.Remove(work.Id);
        Check(issues.Count == 1 && reopened.Settings.Drawers.Single(d => d.Id == work.Id).Removing
            && VerifiedTransfer.Hash(first.OriginalPath) == conflictHash && File.Exists(reopened.Stores[work.Id].StoredPath(first)),
            "failed remove retains the drawer, both conflicting files and a persisted removal intent");
        reopened = new DrawerCatalog(storage, desktop);
        Check(reopened.Settings.Drawers.Single(d => d.Id == work.Id).Removing, "interrupted removal stays pending instead of recollecting");
        File.Move(first.OriginalPath, storage.Resolve("kept-conflict.url"));
        Check(reopened.Remove(work.Id).Count == 0 && File.Exists(first.OriginalPath)
            && File.Exists(storage.Resolve($"recovery/removed-drawers/{work.Id}.json")),
            "retry completes restoration and archives recovery history before removing the drawer");
        Check(new DrawerCatalog(storage, desktop).Settings.Drawers.All(d => d.Id != work.Id), "removed drawers do not reappear after restart");
        Check(reopened.Remove("default").Count == 0 && new DrawerCatalog(storage, desktop).Settings.Drawers.Count == 0,
            "removing the final drawer persists the empty state without recreating a default");
        refused = false;
        try { reopened.Create(" \t"); } catch (ArgumentException) { refused = true; }
        Check(refused, "drawer names reject blank input");

        var workArea = new LayoutRect(0, 0, 1920, 1040);
        var session = new LayoutSnapSession();
        var snapped = session.Resolve(new(7, 9, 420, 352), workArea, [], true, false);
        Check(snapped.Rect.X == 0 && snapped.Rect.Y == 0 && snapped.VerticalGuide == 0 && snapped.HorizontalGuide == 0,
            "screen edges snap in both axes with matching guide coordinates");
        var held = session.Resolve(new(15, 16, 420, 352), workArea, [], true, false);
        Check(held.Rect.X == 0 && held.Rect.Y == 0, "snap hysteresis holds within the release threshold");
        var released = session.Resolve(new(22, 24, 420, 352), workArea, [], true, false);
        Check(released.Rect.X == 22 && released.Rect.Y == 24 && released.VerticalGuide == null, "dragging beyond release threshold detaches cleanly");
        var bypass = session.Resolve(new(5, 7, 420, 352), workArea, [], true, true);
        Check(bypass.Rect.X == 5 && bypass.VerticalGuide == null, "Alt bypass clears snap state and guides");
        var disabled = session.Resolve(new(5, 7, 420, 352), workArea, [], false, false);
        Check(disabled.Rect.X == 5 && disabled.HorizontalGuide == null, "global snap switch leaves free placement unchanged");
        snapped = new LayoutSnapSession().Resolve(new(746, 348, 420, 352), workArea, [], true, false);
        Check(snapped.Rect.X == 750 && snapped.Rect.Y == 344, "screen horizontal and vertical center lines align drawer centers");
        var neighbor = new LayoutRect(500, 200, 320, 300);
        snapped = new LayoutSnapSession().Resolve(new(825, 204, 300, 220), workArea, [neighbor], true, false);
        Check(snapped.Rect.X == 820 && snapped.Rect.Y == 200, "neighbor edges align without moving the neighbor");
        snapped = new LayoutSnapSession().Resolve(new(511, 242, 300, 220), workArea, [neighbor], true, false);
        Check(snapped.Rect.X == 510 && snapped.Rect.Y == 240, "neighbor centers align independently of unequal drawer sizes");
        snapped = new LayoutSnapSession().Resolve(new(-1910, -188, 420, 352), new(-1920, -200, 1920, 1040), [], true, false, 1.5);
        Check(snapped.Rect.X == -1920 && snapped.Rect.Y == -200, "negative monitor coordinates and DPI-scaled snap tolerance work");
        snapped = new LayoutSnapSession().Resolve(new(100, 100, 395, 395), workArea, [new(500, 500, 200, 200)], true, false, edges: 10);
        Check(snapped.Rect.Right == 500 && snapped.Rect.Bottom == 500 && snapped.Rect.X == 100,
            "right and bottom resize edges snap while the origin remains fixed");
        snapped = new LayoutSnapSession().Resolve(new(505, 505, 395, 395), workArea, [new(300, 300, 200, 200)], true, false, edges: 5);
        Check(snapped.Rect.X == 500 && snapped.Rect.Y == 500 && snapped.Rect.Right == 900 && snapped.Rect.Bottom == 900,
            "left and top resize edges snap while opposite edges remain fixed");
        var continuous = new LayoutSnapSession();
        var nearEdge = new LayoutRect(100, 100, 395, 395);
        for (var i = 0; i < 9; i++)
        {
            var result = continuous.Resolve(nearEdge with { Width = 395 + i, Height = 395 + i }, workArea,
                [new(500, 500, 200, 200)], true, false, edges: 10);
            Check(result.Rect.Right == 500 && result.Rect.Bottom == 500, "continuous resize keeps exact snapped edges, frame " + i);
        }
        var placement = DesktopPlacement.Resolve(-1600, 180, 616, 448,
            [new DesktopMonitor("remaining", new(0,0,2560,1440), new(0,0,2560,1392), 144)]);
        Check(placement.X >= 0 && placement.Y + 448 * 1.5 <= 1392, "monitor removal recovers variable-size drawers inside the remaining work area");
        var small = new DesktopMonitor("small", new(0, 0, 1920, 1080), new(0, 0, 1920, 1032), 144);
        var fitted = DesktopPlacement.Resolve(1200, 900, 1400, 1100, [small]);
        Check(fitted.Width * 1.5 <= 1920 && fitted.Height * 1.5 <= 1032 &&
            fitted.X + fitted.Width * 1.5 <= 1920 && fitted.Y + fitted.Height * 1.5 <= 1032,
            "oversized drawers shrink to fit the remaining monitor instead of losing resize edges");
        fitted = DesktopPlacement.Resolve(800, 960, 420, 1100, [small], folded: true);
        Check(fitted.Y == 960 && fitted.Height * 1.5 <= 1032,
            "folded placement uses the visible title bar while retaining a usable expanded size");
        storage.WriteJson("drawer-layout-results.json", new { Passed = results.Count, Results = results, RealDesktopModified = false });
        Console.WriteLine(string.Join(Environment.NewLine, results));
        return 0;
    }
}
