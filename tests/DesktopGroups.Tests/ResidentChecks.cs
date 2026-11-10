using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DesktopGroups;

internal static class ResidentChecks
{
    public static int Run(AppStorage storage)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await CheckAsync(storage); app.Shutdown(0); }
            catch (Exception exception) { Console.Error.WriteLine(exception); app.Shutdown(1); }
        }));
        return app.Run();
    }

    private static async Task CheckAsync(AppStorage storage)
    {
        var results = new List<string>();
        void Check(bool result, string label)
        {
            if (!result) throw new InvalidOperationException(label);
            results.Add("PASS " + label);
        }
        var executable = Path.GetFullPath(Path.Combine(storage.InstallDirectory, "Qige.exe"));
        var values = new MemoryStartup();
        var startup = new StartupRegistration(storage, executable, values);
        Check(!startup.Enabled && values.Value == null, "startup stays disabled until explicitly enabled");
        startup.SetEnabled(true);
        Check(startup.Enabled && values.Value == $"\"{executable}\" --data-dir \"{storage.Root}\" --background", "startup quotes the installed executable and data directory and uses background mode");
        startup.SetEnabled(false);
        Check(!startup.Enabled && values.Value == null, "disabling startup removes only this project entry");
        values.Value = "other command";
        var conflict = false;
        try { startup.SetEnabled(true); } catch (IOException) { conflict = true; }
        Check(conflict && values.Value == "other command", "startup does not overwrite a modified registration");
        conflict = false;
        try { startup.SetEnabled(false); } catch (IOException) { conflict = true; }
        Check(conflict && values.Value == "other command", "startup does not delete a modified registration");
        var escaped = false;
        try { _ = new StartupRegistration(storage, @"C:\outside.exe", values); } catch (IOException) { escaped = true; }
        Check(escaped, "startup rejects executables outside the project");

        values.Entries.Clear();
        var oldExecutable = Path.Combine(storage.InstallDirectory, "DesktopGroups.exe");
        values.Entries[startup.ValueName] = $"\"{oldExecutable}\" --data-dir \"{storage.Root}\" --background";
        startup.UpgradeLegacyRegistration(storage);
        Check(startup.Enabled && values.Read(startup.ValueName) == startup.Command, "startup follows the renamed Qige executable without changing its data path");
        startup.UpgradeLegacyRegistration(storage);
        Check(startup.Enabled, "renamed startup upgrade is idempotent");
        values.Entries.Clear();
        values.Entries[startup.ValueName] = "manually modified startup";
        startup.UpgradeLegacyRegistration(storage);
        Check(values.Read(startup.ValueName) == "manually modified startup", "renamed startup upgrade preserves a modified command");
        values.Entries.Clear();
        var oldRoot = storage.Resolve("legacy-install");
        var oldName = "DesktopGroups-" + StartupRegistration.ProjectId(oldRoot);
        values.Entries[oldName] = $"\"{oldExecutable}\" --root \"{oldRoot}\" --background";
        startup.UpgradeLegacyRegistration(new AppStorage(storage.Root, storage.InstallDirectory, oldRoot));
        Check(values.Read(startup.ValueName) == startup.Command && values.Read(oldName) == null, "exact legacy startup registration upgrades to the new data path");
        values.Entries.Clear();
        values.Entries[oldName] = "manually modified startup";
        startup.UpgradeLegacyRegistration(new AppStorage(storage.Root, storage.InstallDirectory, oldRoot));
        Check(values.Read(oldName) == "manually modified startup" && values.Read(startup.ValueName) == null, "modified legacy startup command is preserved");
        values.Entries.Clear();
        values.Entries[oldName] = $"\"{oldExecutable}\" --root \"{oldRoot}\" --background";
        values.Entries[startup.ValueName] = "other new command";
        var upgradeConflict = false;
        try { startup.UpgradeLegacyRegistration(new AppStorage(storage.Root, storage.InstallDirectory, oldRoot)); }
        catch (IOException) { upgradeConflict = true; }
        Check(upgradeConflict && values.Read(oldName) != null && values.Read(startup.ValueName) == "other new command", "startup upgrade preserves both entries on conflict");

        DesktopMonitor Monitor(string id, int x, int y, int width, int height, uint dpi) =>
            new(id, new(x, y, width, height), new(x, y, width, height - 48), dpi);
        var primary = Monitor("primary", 0, 0, 2560, 1440, 96);
        var left = Monitor("left", -1920, 0, 1920, 1080, 144);
        Check(DesktopPlacement.Resolve(520, 180, [primary]) == new PanelPlacement(520, 180, 96), "valid single-monitor position is unchanged");
        Check(DesktopPlacement.Resolve(-1600, 150, [left, primary]) == new PanelPlacement(-1600, 150, 144), "negative monitor coordinates choose that monitor DPI");
        var removed = DesktopPlacement.Resolve(-1600, 150, [primary]);
        Check(removed.X >= 0 && removed.Y >= 0, "disconnecting a monitor returns the panel to an available screen");
        var gap = DesktopPlacement.Resolve(1800, 400, [Monitor("one", 0, 0, 1280, 1024, 96), Monitor("two", 3000, 0, 1280, 1024, 96)]);
        Check(gap.X <= 860 || gap.X >= 3000, "the panel cannot remain inside a gap between monitors");
        var edge = DesktopPlacement.Resolve(2500, 1390, [primary]);
        Check(edge.X + 420 <= 2560 && edge.Y + 352 <= 1392, "placement respects the monitor work area and taskbar");

        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var relayRoot = storage.Root + "-test-" + Guid.NewGuid().ToString("N");
        using (var relay = new InstanceRelay(relayRoot, () => received.TrySetResult()))
        {
            Check(await InstanceRelay.ShowExistingAsync(relayRoot), "a second launch can signal the current-user instance");
            await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(received.Task.IsCompletedSuccessfully, "the existing instance receives the show request");
        }

        var relative = "checks/Resident/" + Guid.NewGuid().ToString("N");
        var desktop = storage.Resolve(relative + "/Desktop"); Directory.CreateDirectory(desktop);
        var original = Path.Combine(desktop, "恢复样例.url");
        File.WriteAllText(original, "[InternetShortcut]\r\nURL=https://example.com/recovery\r\n");
        var store = new ShortcutStore(storage, desktop, relative + "/groups.json", relative + "/Storage");
        store.Add(original);
        var stored = store.StoredPath(store.Active.Single());
        var hash = VerifiedTransfer.Hash(stored);
        var errors = new List<string>();
        HwndSource Host() => new(new HwndSourceParameters("DesktopGroups · isolated test parent")
        { WindowStyle = unchecked((int)0x80000000), PositionX = 0, PositionY = 0, Width = 2560, Height = 1440 });
        HwndSource? host = null;
        using var panel = new ShortcutPanel(errors.Add, (_, _) => { }, _ => false, findHost: () =>
            host?.Handle is > 0 and var handle ? handle : throw new InvalidOperationException("test desktop unavailable"));
        try
        {
            panel.SetItems([( "恢复样例", stored, original )]);
            var waiting = false;
            try { panel.Show(520, 180); } catch (InvalidOperationException) { waiting = true; }
            Check(waiting && !panel.IsAttached && File.Exists(stored), "a missing host leaves stored files intact and can be retried");
            host = Host(); panel.Show(520, 180);
            Check(panel.IsAttached && panel.AttachmentCount == 1, "the panel attaches when its desktop host becomes ready");
            panel.RestoreFolded(true);
            host.Dispose(); host = null;
            await Task.Delay(40);
            Check(!panel.IsAttached, "destroying the parent invalidates the native surface");
            host = Host(); panel.EnsureAttached(true);
            var restoredPlacement = DesktopPlacement.Resolve(panel.X, panel.Y, panel.LogicalWidth, panel.LogicalHeight,
                DesktopPlacement.Capture(), folded: true);
            Check(panel.IsAttached && panel.AttachmentCount == 2 && panel.IsFolded && panel.Height == (int)Math.Round(46 * restoredPlacement.Dpi / 96d),
                "a replacement parent recreates the surface and preserves folded state");
            Check(panel.DropTarget != null, "OLE drop target is registered again after surface recovery");
            Check(File.Exists(stored) && !File.Exists(original) && VerifiedTransfer.Hash(stored) == hash && store.Active.Count == 1,
                "desktop recovery neither transfers files nor changes group membership");
            var count = panel.SurfaceBuilds;
            for (var i = 0; i < 30; i++) panel.EnsureAttached();
            Check(panel.SurfaceBuilds == count && panel.AttachmentCount == 2, "healthy watchdog checks do not rebuild or reattach the surface");
            for (var i = 0; i < 20; i++)
            {
                host.Dispose(); host = null;
                Check(!panel.IsAttached, "stress host loss " + (i + 1));
                host = Host(); panel.EnsureAttached(true);
            }
            Check(panel.AttachmentCount == 22 && panel.IsAttached && panel.IsFolded && panel.DropTarget != null
                && VerifiedTransfer.Hash(stored) == hash && store.Active.Count == 1,
                "20 desktop-host replacements preserve folding, OLE, original bytes and membership");
        }
        finally
        {
            panel.Dispose(); host?.Dispose();
            var issues = store.RestoreAll();
            if (issues.Count > 0) throw new IOException(string.Join("; ", issues));
        }
        Check(File.Exists(original) && !File.Exists(stored), "recovery test restores its project sample on exit");
        Check(errors.Count == 0, "native recovery does not report unexpected callback failures");

        var openReceived = false;
        using (var tray = new ResidentTray(relayRoot, action => openReceived |= action == TrayCommand.Show))
        {
            Check(tray.Available, "native tray registration succeeds on the current desktop");
            PostMessage(tray.Handle, 0x8001, 0, 0x400);
            await Task.Delay(100);
            Check(openReceived, "selecting the native tray icon dispatches the show-manager command");
            var eventReceived = false;
            tray.EnvironmentChanged += reason => eventReceived |= reason == "Explorer 重建";
            var previous = tray.RegistrationCount;
            PostMessage(tray.Handle, RegisterWindowMessage("TaskbarCreated"), 0, 0);
            await Task.Delay(100);
            Check(eventReceived && tray.Available && tray.RegistrationCount > previous,
                "a taskbar-created notification restores this process tray registration");
            var events = new List<string>();
            tray.EnvironmentChanged += events.Add;
            PostMessage(tray.Handle, 0x7E, 0, 0);
            PostMessage(tray.Handle, 0x218, 18, 0);
            PostMessage(tray.Handle, 0x2B1, 8, 0);
            await Task.Delay(100);
            Check(events.Contains("显示设置变化") && events.Contains("休眠唤醒") && events.Contains("解锁桌面"),
                "display, power-resume and unlock notifications reach the recovery coordinator");
        }

        storage.WriteJson("checks/Resident/latest.json", new
        {
            Passed = results.Count, Results = results, RealDesktopFilesMoved = false,
            ActualExplorerRestarted = false, ActualSleepOrDisplayChangePerformed = false,
            ActualStartupRegistrationChanged = false, Monitors = DesktopPlacement.Capture()
        });
        Console.WriteLine(string.Join(Environment.NewLine, results));
    }

    private sealed class MemoryStartup : IStartupValue
    {
        public Dictionary<string, string> Entries { get; } = [];
        private string? currentName;
        public string? Value
        {
            get => currentName == null ? null : Read(currentName);
            set { if (currentName != null) { if (value == null) Entries.Remove(currentName); else Entries[currentName] = value; } }
        }
        public string? Read(string name) { currentName = name; return Entries.GetValueOrDefault(name); }
        public void Write(string name, string value) { currentName = name; Entries[name] = value; }
        public void Remove(string name) { currentName = name; Entries.Remove(name); }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
