using System.IO;

namespace DesktopGroups;

internal static class DesktopInputChecks
{
    internal static void Check(AppStorage storage, Action<bool, string> assert)
    {
        var desktop = storage.Resolve("routing/Desktop");
        var common = storage.Resolve("routing/PublicDesktop");
        Directory.CreateDirectory(desktop); Directory.CreateDirectory(common);
        var data = new AppStorage(storage.Resolve("routing/Data"));
        var catalog = new DrawerCatalog(data, desktop, common);
        var store = catalog.Stores["default"];
        var publicLink = Path.Combine(common, "DeskBox.lnk");
        NativeShortcut.Write(publicLink, Environment.ProcessPath!);
        File.WriteAllText(publicLink + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        var hash = VerifiedTransfer.Hash(publicLink);
        catalog.AddReferences("default", [publicLink]); // A reference saved by diag.2.
        var errors = new List<string>();
        var received = 0;
        var target = new PanelDropTarget(store.CanAccept, paths =>
        {
            catalog.EnsureOwnership("default", paths);
            foreach (var path in paths) store.Add(path);
            catalog.RetireCollectedReferences("default");
            received++;
            return true;
        }, (_, _) => { }, errors.Add);
        using var publicData = new StoragePanelCheck.ShellSampleData(publicLink);
        uint effect = 1;
        target.DragEnter(publicData.Data, 1, default, ref effect);
        assert(effect == 0 && File.Exists(publicLink), "copy-only public-desktop drags cannot silently create references");
        target.DragLeave();
        effect = 7; target.DragEnter(publicData.Data, 1, default, ref effect);
        assert(effect == 2, "public-desktop Shell drag negotiates verified managed MOVE");
        effect = 7; target.Drop(publicData.Data, 0, default, ref effect);
        assert(effect == 2 && received == 1 && !File.Exists(publicLink) && store.Active.Count == 1 && errors.Count == 0,
            "dropping public DeskBox really removes its original desktop name");
        var entry = store.Active.Single();
        assert(entry.OriginalPath == publicLink && entry.TransferVerified && VerifiedTransfer.Hash(store.StoredPath(entry)) == hash
            && File.ReadAllText(store.StoredPath(entry) + ":Zone.Identifier").Contains("ZoneId=3"),
            "public shortcut collection preserves exact contents, named streams and original root");
        assert(catalog.Settings.Drawers[0].References.Count == 0 && !catalog.RetireCollectedReferences("default"),
            "successful collection retires the old automatic reference without a duplicate item");
        assert(StoragePanelCheck.ReadEffect(publicData.Data, "Performed DropEffect") == 0
            && StoragePanelCheck.ReadEffect(publicData.Data, "Logical Performed DropEffect") == 2,
            "public managed MOVE uses the existing Shell receipt without asking Shell to delete the source");
        assert(store.RestoreAll().Count == 0 && File.Exists(publicLink) && !File.Exists(Path.Combine(desktop, "DeskBox.lnk")),
            "public shortcuts return to the public desktop rather than the personal desktop");
        assert(store.Detached().CanAccept([publicLink]), "worker models retain the configured public desktop root");

        var personal = Path.Combine(desktop, "同名.txt");
        var shared = Path.Combine(common, "同名.txt");
        File.WriteAllText(personal, "personal"); File.WriteAllText(shared, "public");
        using var mixed = new StoragePanelCheck.ShellSampleData(personal, shared, publicLink);
        effect = 7; target.DragEnter(mixed.Data, 1, default, ref effect);
        effect = 7; target.Drop(mixed.Data, 0, default, ref effect);
        assert(effect == 2 && received == 2 && !File.Exists(personal) && !File.Exists(shared) && !File.Exists(publicLink)
            && store.Active.Count == 3, $"mixed personal and public desktop selection collects every original (effect={effect}, received={received}, active={store.Active.Count}, errors={string.Join("; ", errors)})");
        assert(store.Active.Select(store.StoredPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3
            && store.Active.Where(e => e.IsOrdinaryFile).Select(e => File.ReadAllText(store.StoredPath(e))).Order().SequenceEqual(new[] { "personal", "public" }),
            "same filenames in the two desktop roots keep independent data and ownership");
        var folder = Path.Combine(common, "共享文件夹"); Directory.CreateDirectory(Path.Combine(folder, "子目录"));
        File.WriteAllText(Path.Combine(folder, "子目录", "内容.txt"), "nested public contents");
        store.Add(folder);
        assert(!Directory.Exists(folder) && store.Active.Any(e => e.OriginalPath == folder && e.IsDirectory),
            "public desktop folders are really collected with their contents");
        assert(UninstallSafety.Verify(data, desktop, common) == 2, "uninstall refuses while public desktop contents are collected");
        var service = new BackupService(data, desktop, common);
        var snapshot = service.Create(service.Options(), "公共桌面收纳回归", true);
        assert(service.Validate(snapshot.Directory).Files.Any(f => f.Path.EndsWith("DeskBox.lnk")),
            "backup validates public desktop membership and verified contents");
        catalog = new DrawerCatalog(data, desktop, common); store = catalog.Stores["default"];
        assert(store.StartSession().Count == 0 && store.Active.Count == 4 && !File.Exists(publicLink),
            "restart recovers public desktop collection without recreating desktop icons");
        File.WriteAllText(shared, "conflict");
        assert(store.RestoreAll().Count == 1 && File.ReadAllText(shared) == "conflict"
            && store.Active.Single().OriginalPath == shared && File.ReadAllText(store.StoredPath(store.Active.Single())) == "public",
            "return conflicts preserve both public desktop files and the stored recovery copy");
        File.Delete(shared);
        assert(store.RestoreAll().Count == 0 && File.ReadAllText(shared) == "public" && File.ReadAllText(personal) == "personal"
            && File.ReadAllText(Path.Combine(folder, "子目录", "内容.txt")) == "nested public contents",
            "retry returns files and folders to their exact original desktop roots");
        service.Restore(snapshot.Directory);
        catalog = new DrawerCatalog(data, desktop, common); store = catalog.Stores["default"];
        assert(store.StartSession().Count == 0 && store.Active.Count == 4 && !File.Exists(publicLink) && !Directory.Exists(folder),
            "restoring a backup recollects public items using their original root");
        assert(catalog.Remove("default").Count == 0 && File.Exists(publicLink) && File.Exists(shared) && File.Exists(personal)
            && Directory.Exists(folder) && UninstallSafety.Verify(data, desktop, common) == 0,
            "removing the drawer returns public originals and clears uninstall recovery blocks");

        var failureData = new AppStorage(storage.Resolve("routing/Failures"));
        catalog = new DrawerCatalog(failureData, desktop, common); store = catalog.Stores["default"];
        var locked = Path.Combine(common, "占用.txt"); File.WriteAllText(locked, "preserved");
        catalog.AddReferences("default", [locked]);
        var failed = false;
        using (var guard = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { store.Add(locked); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { failed = true; }
            assert(failed && File.Exists(locked) && store.Active.Count == 0 && !catalog.RetireCollectedReferences("default")
                && catalog.Settings.Drawers[0].References.Single().Path == locked,
                "a locked public source reports failure, preserves its original and never substitutes a new reference");
        }
        catalog = new DrawerCatalog(failureData, desktop, common); store = catalog.Stores["default"];
        assert(store.StartSession().Count == 0 && !File.Exists(locked) && catalog.RetireCollectedReferences("default"),
            "interrupted public collection can retry after restart and retire its old reference only on success");
        var lockedEntry = store.Active.Single();
        store.ReturnAndUngroup(store.StoredPath(lockedEntry));
        assert(File.ReadAllText(locked) == "preserved" && store.Profile.Entries.Count == 0,
            "return and ungroup restores the public original and prevents recollection");
        var outside = storage.Resolve("routing/outside.txt"); File.WriteAllText(outside, "outside");
        assert(!store.CanAccept([publicLink, outside]) && File.Exists(publicLink) && File.Exists(outside),
            "invalid mixed selections are rejected before any desktop original changes");
        var isolated = new ShortcutStore(new AppStorage(storage.Resolve("routing/Isolated")), desktop);
        assert(!isolated.CanAccept([publicLink]) && isolated.PublicDesktopPath.Length == 0,
            "demo and isolated stores require an explicit fake public root");
    }
}
