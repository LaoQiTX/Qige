using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

namespace DesktopGroups;

internal static class DisplayLayoutChecks
{
    internal static void CheckLayout(Action<bool, string> check)
    {
        var large = new DesktopMonitor("main", new(0, 0, 2560, 1440), new(0, 0, 2560, 1392), 96);
        var small = new DesktopMonitor("main", new(0, 0, 1920, 1080), new(0, 0, 1920, 1032), 96);
        var profile = new ShortcutProfile { X = 2000, Y = 900, Width = 560, Height = 400, Locked = true };
        profile.LayoutReference = DesktopLayout.Capture(profile, [large]);
        var reference = profile.LayoutReference;
        var fitted = DesktopLayout.Resolve(profile, [small]);
        check(fitted.Width == 420 && fitted.Height < 400 && fitted.X + fitted.Width == 1920 && fitted.Y < 900,
            "resolution change scales drawer geometry and preserves right-edge alignment even when locked");
        profile.X = fitted.X; profile.Y = fitted.Y; profile.Width = fitted.Width; profile.Height = fitted.Height;
        for (var i = 0; i < 100; i++)
        {
            var next = DesktopLayout.Resolve(profile, [small]);
            if (next != fitted) throw new InvalidOperationException("display recovery accumulated layout drift");
        }
        var restored = DesktopLayout.Resolve(profile, [large]);
        check(restored == new PanelPlacement(2000, 900, 96, 560, 400),
            "100 repeated refreshes and a resolution round trip restore exact original geometry");
        var reopened = JsonSerializer.Deserialize<ShortcutProfile>(JsonSerializer.Serialize(profile))!;
        check(DesktopLayout.Resolve(reopened, [large]) == restored && reopened.LayoutReference == reference,
            "layout reference survives restart while a smaller resolution is active");

        var dpi = small with { Dpi = 144 };
        var scaled = DesktopLayout.Resolve(profile, [dpi]);
        check(scaled.Dpi == 144 && scaled.X + Math.Round(scaled.Width * 1.5) <= 1920
            && scaled.Y + Math.Round(scaled.Height * 1.5) <= 1032,
            "resolution and 150 percent DPI changes use the new physical work area");
        var dpiOnly = DesktopLayout.Resolve(profile, [large with { Dpi = 144 }]);
        check(dpiOnly.Width == 560 && dpiOnly.Height == 400 && dpiOnly.X + 840 == 2560,
            "DPI-only changes retain logical size and screen-edge anchoring");

        profile.Folded = true; profile.Y = 1346;
        profile.Width = 560; profile.Height = 400; profile.X = 2000;
        profile.LayoutReference = DesktopLayout.Capture(profile, [large]);
        var folded = DesktopLayout.Resolve(profile, [small]);
        check(folded.Y == 986 && folded.X + folded.Width == 1920,
            "folded bottom drawers map their title anchor instead of the hidden body");
        var relocated = small with { Bounds = new(-1920, -200, 1920, 1080), Work = new(-1920, -200, 1920, 1032) };
        var negative = DesktopLayout.Resolve(profile, [relocated, large with { Device = "other" }]);
        check(negative.X < 0 && negative.Y + 46 == relocated.Work.Bottom,
            "monitor identity wins over old coordinates when topology moves to a negative origin");
        var removed = DesktopLayout.Resolve(profile, [small with { Device = "remaining" }]);
        check(removed.X >= 0 && removed.X + removed.Width <= 1920 && removed.Y + 46 <= 1032
            && DesktopLayout.Resolve(profile, [large]).Y == 1346,
            "unplugging uses an available monitor and reconnecting restores the original monitor layout");

        var taskbar = DesktopLayout.Resolve(profile, [large with { Work = new(60, 0, 2500, 1440) }]);
        check(taskbar.Width == 560 && taskbar.Height == 400 && taskbar.Y + 46 == 1440,
            "taskbar work-area changes retain size while updating the title anchor");
        var tiny = small with { Bounds = new(0, 0, 800, 600), Work = new(0, 0, 800, 552), Dpi = 192 };
        var constrained = DesktopLayout.Resolve(profile, [tiny]);
        check(constrained.Width >= 244 && constrained.Width * 2 <= 800 && constrained.Height * 2 <= 552,
            "small high-DPI screens enforce usable limits and keep resize edges reachable");
        var legacy = new ShortcutProfile { X = 100, Y = 100 };
        check(DesktopLayout.Resolve(legacy, [large]) == new PanelPlacement(100, 100, 96),
            "legacy profiles without a display reference retain their existing valid layout");
        legacy.LayoutReference = new(large, 100, 100, 420, 352);
        var storeDesktop = new DesktopMonitor("remaining", new(0, 0, 1280, 720), new(0, 0, 1280, 672), 96);
        check(DesktopLayout.Resolve(legacy, [storeDesktop]).X >= 0,
            "a missing source monitor never leaves a drawer in a desktop gap");
    }

    internal static int Run(AppStorage storage)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var results = new List<string>();
                void Check(bool result, string name)
                {
                    if (!result) throw new InvalidOperationException(name);
                    results.Add("PASS " + name);
                }
                CheckLayout(Check);
                var large = new DesktopMonitor("main", new(0, 0, 2560, 1440), new(0, 0, 2560, 1392), 96);
                IReadOnlyList<DesktopMonitor> monitors = [large];
                using var host = new HwndSource(new HwndSourceParameters("Qige isolated display test")
                    { WindowStyle = unchecked((int)0x80000000), PositionX = 0, PositionY = 0, Width = 2560, Height = 1440 });
                var errors = new List<string>();
                using var panel = new ShortcutPanel(errors.Add, (_, _) => { }, _ => false,
                    findHost: () => host.Handle, captureMonitors: () => monitors);
                var commits = 0;
                panel.LayoutCommitted += () => commits++;
                var profile = new ShortcutProfile { X = 2000, Y = 900, Width = 560, Height = 400 };
                profile.LayoutReference = DesktopLayout.Capture(profile, monitors);
                void Apply()
                {
                    var p = DesktopLayout.Resolve(profile, monitors);
                    panel.ApplyScreenLayout(p.X, p.Y, p.Width, p.Height, commit: false);
                }
                Apply();
                var original = DesktopAttachment.ReadState(panel.Handle);
                var builds = panel.SurfaceBuilds;
                monitors = [large with { Bounds = new(0, 0, 1920, 1080), Work = new(0, 0, 1920, 1032), Dpi = 144 }];
                Apply();
                var resized = DesktopAttachment.ReadState(panel.Handle);
                Check(resized.Width != original.Width && resized.X + resized.Width == 1920
                    && resized.Y + resized.Height <= 1032 && panel.SurfaceBuilds > builds && panel.AttachmentCount == 1,
                    "native layered surface resizes and rerenders at the new DPI without reattaching");
                monitors = [large]; Apply();
                var back = DesktopAttachment.ReadState(panel.Handle);
                Check(back.X == original.X && back.Y == original.Y && back.Width == original.Width && back.Height == original.Height,
                    "native surface returns to exact original geometry after a live resolution round trip");
                Check(commits == 0, "automatic display recovery does not commit a user layout edit");
                panel.ApplyScreenLayout(100, 100, 420, 352);
                Check(commits == 1, "explicit user layout edits still commit normally");
                profile.Folded = true; profile.Y = 1346;
                profile.LayoutReference = DesktopLayout.Capture(profile, monitors);
                panel.RestoreFolded(true);
                monitors = [large with { Bounds = new(0, 0, 1920, 1080), Work = new(0, 0, 1920, 1032), Dpi = 144 }];
                Apply();
                var title = DesktopAttachment.ReadState(panel.Handle);
                Check(title.Y + title.Height == 1032 && panel.ExpandsUpward && panel.IsFolded && panel.DropTarget != null,
                    "folded native drawer remains above taskbar and retains upward expansion and OLE after display changes");
                Check(errors.Count == 0, "display transitions report no native callback errors");
                storage.WriteJson("display-layout-results.json", new { Passed = results.Count, Results = results,
                    ActualSystemResolutionChanged = false, RealDesktopModified = false });
                Console.WriteLine(string.Join(Environment.NewLine, results));
                app.Shutdown(0);
            }
            catch (Exception e) { Console.Error.WriteLine(e); app.Shutdown(1); }
        }));
        return app.Run();
    }
}
