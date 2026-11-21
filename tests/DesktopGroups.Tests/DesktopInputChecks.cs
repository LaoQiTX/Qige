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
        var catalog = new DrawerCatalog(data, desktop);
        var store = catalog.Stores["default"];
        var publicLink = Path.Combine(common, "DeskBox.lnk");
        NativeShortcut.Write(publicLink, Environment.ProcessPath!);
        var personal = Path.Combine(desktop, "个人.url");
        File.WriteAllText(personal, "[InternetShortcut]\r\nURL=https://example.com/personal\r\n");
        var hash = VerifiedTransfer.Hash(publicLink);
        var errors = new List<string>();
        var received = 0;
        var target = new PanelDropTarget(paths =>
        {
            try { _ = DesktopInputPlan.Create(store, paths, common); return true; }
            catch (IOException) { return false; }
        }, paths =>
        {
            var plan = DesktopInputPlan.Create(store, paths, common);
            catalog.EnsureOwnership("default", plan.Managed);
            catalog.AddReferences("default", plan.References);
            foreach (var path in plan.Managed) store.Add(path);
            received++;
            return true;
        }, (_, _) => { }, errors.Add,
            (paths, _, allowed) => DesktopInputPlan.SelectEffect(paths, desktop, allowed, common));
        using var publicData = new StoragePanelCheck.ShellSampleData(publicLink);
        uint effect = 7;
        target.DragEnter(publicData.Data, 1, default, ref effect);
        assert(effect == 4, "public-desktop shortcut drag advertises a reference instead of rejecting it");
        effect = 7; target.Drop(publicData.Data, 0, default, ref effect);
        assert(effect == 4 && received == 1 && catalog.Settings.Drawers[0].References.Single().Path == publicLink
            && VerifiedTransfer.Hash(publicLink) == hash && store.Active.Count == 0 && errors.Count == 0,
            "Shell public-desktop drop creates the same reference as right-click and preserves the original");
        assert(StoragePanelCheck.ReadEffect(publicData.Data, "Performed DropEffect") == 0
            && StoragePanelCheck.ReadEffect(publicData.Data, "Logical Performed DropEffect") == 4,
            "public-desktop reference receipts never authorize Shell source deletion");
        effect = 1; target.DragEnter(publicData.Data, 1, default, ref effect);
        assert(effect == 1, "copy-only sources can add a public-desktop reference");
        effect = 1; target.Drop(publicData.Data, 0, default, ref effect);
        assert(effect == 1 && catalog.Settings.Drawers[0].References.Count == 1 && File.Exists(publicLink),
            "repeated public-desktop drops remain idempotent and preserve their source");
        effect = 2; target.DragEnter(publicData.Data, 1, default, ref effect);
        assert(effect == 0, "move-only reference drags are refused so public originals cannot be deleted");
        target.DragLeave();
        var plan = DesktopInputPlan.Create(store, [personal, publicLink], common);
        assert(plan.Managed.SequenceEqual([personal]) && plan.References.SequenceEqual([publicLink])
            && DesktopInputPlan.SelectEffect([personal, publicLink], desktop, 7, common) == 1,
            "mixed desktop selections collect personal files and reference public files with a safe copy receipt");
        using var personalData = new StoragePanelCheck.ShellSampleData(personal);
        effect = 7; target.DragEnter(personalData.Data, 1, default, ref effect);
        effect = 7; target.Drop(personalData.Data, 0, default, ref effect);
        assert(effect == 2 && !File.Exists(personal) && store.Active.Count == 1 && File.Exists(publicLink),
            "personal-desktop drag continues to perform verified collection");
        var outside = storage.Resolve("routing/outside.txt"); File.WriteAllText(outside, "preserved");
        var refused = false;
        try { _ = DesktopInputPlan.Create(store, [publicLink, outside], common); }
        catch (IOException) { refused = true; }
        assert(refused && File.Exists(outside) && VerifiedTransfer.Hash(publicLink) == hash,
            "an invalid mixed selection is rejected before any original is changed");
        var reopened = new DrawerCatalog(data, desktop);
        assert(reopened.Settings.Drawers[0].References.Single().Path == publicLink
            && reopened.Remove("default").Count == 0 && File.Exists(personal) && VerifiedTransfer.Hash(publicLink) == hash,
            "references survive restart and drawer removal restores managed files without altering public originals");
    }
}
