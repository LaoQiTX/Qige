using System.IO;

namespace DesktopGroups;

internal static class UninstallSafety
{
    internal static int Verify(AppStorage storage, string? desktop = null)
    {
        try
        {
            if (!Directory.Exists(storage.Root)) return 0;
            var lockFile = storage.Resolve("recovery/session.lock");
            Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
            using var guard = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (File.Exists(storage.Resolve("recovery/backup-restore.json")) || new LegacyLayoutRecovery(storage).HasRecovery) return 2;
            if (MappedFileOperations.ReadUnfinished(storage).Any(receipt => receipt.Managed && receipt.Holding != null && Directory.Exists(receipt.Holding))) return 2;
            if (!File.Exists(storage.Resolve("config/drawers.json")))
                return Directory.Exists(storage.Resolve("storage")) ? 2 : 0;
            var catalog = new DrawerCatalog(storage, desktop);
            if (catalog.Settings.Drawers.Any(drawer => drawer.Removing && drawer.Kind == DrawerKind.Desktop)) return 2;
            foreach (var store in catalog.Stores.Values)
            foreach (var entry in store.Profile.Entries)
            {
                if (entry.Phase != "Restored" || entry.PendingRelative != null || entry.RemoveAfterRestore) return 2;
                var path = store.StoredPath(entry);
                try { _ = File.GetAttributes(path); return 2; }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 3;
        }
    }
}
