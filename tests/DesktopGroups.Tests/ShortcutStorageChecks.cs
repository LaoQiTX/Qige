using System.IO;

namespace DesktopGroups;

internal static class ShortcutStorageChecks
{
    public static int Run(AppStorage storage)
    {
        var relative = $"checks/ShortcutStorage/{Guid.NewGuid():N}";
        var desktop = storage.Resolve(relative + "/Desktop"); Directory.CreateDirectory(desktop);
        var results = new List<string>();
        ShortcutStore Open() => new(storage, desktop, relative + "/profile.json", relative + "/Storage");
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); results.Add("PASS " + label); }
        var source = Path.Combine(desktop, "样例.url");
        const string first = "[InternetShortcut]\r\nURL=https://example.com/\r\nDescription=first\r\n";
        const string second = "[InternetShortcut]\r\nURL=https://example.com/\r\nDescription=updated\r\n";
        File.WriteAllText(source, first);
        File.WriteAllText(source + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        var store = Open(); store.Add(source);
        Check(!File.Exists(source) && File.ReadAllText(store.StoredPath(store.Active.Single())) == first, "verified collection removes source only after an exact copy");
        Check(File.ReadAllText(store.StoredPath(store.Active.Single()) + ":Zone.Identifier").Contains("ZoneId=3"), "download origin stream preserved");
        store = Open();
        Check(store.StartSession().Count == 0 && store.Active.Count == 1, "new process recovers an interrupted active session");
        Check(store.RestoreAll().Count == 0 && File.ReadAllText(source) == first, "normal restoration returns original content");
        Check(File.ReadAllText(source + ":Zone.Identifier").Contains("ZoneId=3"), "download origin stream survives restoration");
        Check(store.StartSession().Count == 0 && !File.Exists(source), "membership survives exit and recollects on next launch");
        File.WriteAllText(source, second);
        Check(store.Synchronize().Count == 0 && !File.Exists(source), "same-target shortcut recreation is absorbed");
        var entry = store.Active.Single();
        Check(File.ReadAllText(store.StoredPath(entry)) == second && entry.Versions.Count == 1 && File.ReadAllText(storage.Resolve(entry.Versions[0])) == first, "latest shortcut used and prior bytes preserved");
        File.WriteAllText(source, "[InternetShortcut]\nURL=https://example.org/different\n");
        var conflictHash = VerifiedTransfer.Hash(source);
        Check(store.Synchronize().Count == 1 && VerifiedTransfer.Hash(source) == conflictHash && File.ReadAllText(store.StoredPath(entry)) == second, "different-target conflict preserves both entries");
        Check(store.RestoreAll().Count == 1 && VerifiedTransfer.Hash(source) == conflictHash, "restoration never overwrites a conflicting desktop entry");
        VerifiedTransfer.Move(source, storage.Resolve(relative + "/conflict-preserved.url"), conflictHash);
        Check(store.RestoreAll().Count == 0 && File.ReadAllText(source) == second, "restoration succeeds after conflict is moved aside");
        // Simulate interruption after copy but before deletion, without killing Explorer.
        entry.Phase = "Collecting"; entry.Hash = VerifiedTransfer.Hash(source); entry.TransferVerified = true;
        Directory.CreateDirectory(Path.GetDirectoryName(store.StoredPath(entry))!);
        File.Copy(source, store.StoredPath(entry)); store.Save();
        store = Open();
        Check(store.StartSession().Count == 0 && !File.Exists(source) && store.Active.Count == 1, "interrupted collection with two exact copies is recoverable");
        Check(store.RestoreAll().Count == 0, "recovered session restores normally");
        entry = store.Profile.Entries.Single(); entry.Phase = "Collecting"; entry.Hash = VerifiedTransfer.Hash(source); entry.TransferVerified = false;
        File.WriteAllText(store.StoredPath(entry), "partial"); store.Save(); store = Open();
        Check(store.StartSession().Count == 1 && File.ReadAllText(source) == second, "incomplete copy is rejected and original retained");
        var locked = Path.Combine(desktop, "占用.url"); File.WriteAllText(locked, first);
        using (var held = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var refused = false;
            try { store.Add(locked); } catch (IOException) { refused = true; }
            Check(refused && File.Exists(locked), "locked source is not removed");
        }
        var link = Path.Combine(desktop, "参数入口.lnk");
        CreateLink(link, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"), "first");
        var key1 = ShortcutInfo.Read(link).Key;
        CreateLink(link, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"), "second");
        Check(ShortcutInfo.Read(link).Key != key1, "different launch arguments are not merged");
        var outside = storage.Resolve(relative + "/outside.url"); File.WriteAllText(outside, first);
        var rejected = false; try { store.Add(outside); } catch (IOException) { rejected = true; }
        Check(rejected && File.Exists(outside), "only the allowed desktop root can be collected");
        var interrupted = Path.Combine(desktop, "未确认副本.url"); File.WriteAllText(interrupted, first);
        var isolated = new ShortcutStore(storage, desktop, relative + "/unconfirmed.json", relative + "/Unconfirmed");
        isolated.Add(interrupted); isolated.RestoreAll();
        var uncertain = isolated.Profile.Entries.Single();
        File.Copy(interrupted, isolated.StoredPath(uncertain));
        uncertain.Phase = "Collecting"; uncertain.TransferVerified = false; isolated.Save();
        isolated = new ShortcutStore(storage, desktop, relative + "/unconfirmed.json", relative + "/Unconfirmed");
        Check(isolated.StartSession().Count == 1 && File.Exists(interrupted) && File.Exists(isolated.StoredPath(uncertain)), "matching main bytes without a complete-copy receipt never authorize source deletion");
        Check(isolated.RestoreAll().Count == 0 && File.ReadAllText(interrupted) == first, "uncertain collection can return the authoritative original without overwriting it");
        isolated.StartSession(); uncertain = isolated.Profile.Entries.Single();
        File.Copy(isolated.StoredPath(uncertain), interrupted); uncertain.Phase = "Restoring"; uncertain.TransferVerified = true; isolated.Save();
        isolated = new ShortcutStore(storage, desktop, relative + "/unconfirmed.json", relative + "/Unconfirmed");
        Check(isolated.RestoreAll().Count == 0 && File.Exists(interrupted) && !File.Exists(isolated.StoredPath(uncertain)), "interrupted verified restoration finishes without duplicate launchers");
        isolated.StartSession(); uncertain = isolated.Profile.Entries.Single();
        File.Copy(isolated.StoredPath(uncertain), interrupted); uncertain.Phase = "Restoring"; uncertain.TransferVerified = false; isolated.Save();
        isolated = new ShortcutStore(storage, desktop, relative + "/unconfirmed.json", relative + "/Unconfirmed");
        Check(isolated.RestoreAll().Count == 1 && File.Exists(interrupted) && File.Exists(isolated.StoredPath(uncertain)), "unverified restoration retains both copies for recovery");
        var returnPath = Path.Combine(desktop, "拖回桌面.url"); File.WriteAllText(returnPath, first);
        var returning = new ShortcutStore(storage, desktop, relative + "/return.json", relative + "/Return");
        returning.Add(returnPath);
        var returnStored = returning.StoredPath(returning.Active.Single());
        returning.ReturnAndUngroup(returnStored);
        Check(File.ReadAllText(returnPath) == first && !File.Exists(returnStored) && returning.Profile.Entries.Count == 0, "drag return restores content and removes active membership");
        returning = new ShortcutStore(storage, desktop, relative + "/return.json", relative + "/Return");
        Check(returning.StartSession().Count == 0 && File.Exists(returnPath) && returning.Profile.Ungrouped.Count == 1, "returned icon stays on desktop after restarting");
        returning.Add(returnPath);
        var interruptedReturn = returning.Active.Single(); interruptedReturn.RemoveAfterRestore = true; returning.Save();
        returning.RestoreAll();
        Check(File.Exists(returnPath) && returning.Profile.Entries.Count == 0, "pending drag return can finish through exit recovery");
        returning.Add(returnPath);
        interruptedReturn = returning.Active.Single(); interruptedReturn.RemoveAfterRestore = true;
        interruptedReturn.Hash = VerifiedTransfer.Hash(returning.StoredPath(interruptedReturn));
        interruptedReturn.Phase = "Restoring"; interruptedReturn.TransferVerified = false; returning.Save();
        VerifiedTransfer.Move(returning.StoredPath(interruptedReturn), returnPath, interruptedReturn.Hash, () => { interruptedReturn.TransferVerified = true; returning.Save(); });
        returning = new ShortcutStore(storage, desktop, relative + "/return.json", relative + "/Return");
        Check(returning.StartSession().Count == 0 && File.Exists(returnPath) && returning.Profile.Entries.Count == 0, "interrupted drag return cannot recollect an already restored shortcut");
        returning.Add(returnPath);
        File.WriteAllText(returnPath, "[InternetShortcut]\nURL=https://example.org/return-conflict\n");
        var conflict = false;
        try { returning.ReturnAndUngroup(returning.StoredPath(returning.Active.Single())); } catch (IOException) { conflict = true; }
        Check(conflict && File.Exists(returnPath) && returning.Active.Count == 1, "drag return preserves a conflicting desktop file and the stored entry");
        storage.WriteJson("checks/ShortcutStorage/latest.json", new { Passed = results.Count, Root = relative, Results = results, RealDesktopModified = false });
        Console.WriteLine(string.Join(Environment.NewLine, results));
        return 0;
    }

    private static void CreateLink(string path, string target, string arguments = "")
        => NativeShortcut.Write(path, target, arguments);
}
