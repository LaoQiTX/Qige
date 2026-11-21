using System.IO;

namespace DesktopGroups;

// Explorer merges personal and public desktop items into one view. Public items
// use the same references as the file picker; their originals are never moved.
internal sealed record DesktopInputPlan(string[] Managed, string[] References)
{
    internal static DesktopInputPlan Create(ShortcutStore store, IReadOnlyList<string> paths, string? publicDesktop = null)
    {
        if (paths.Count == 0) throw new IOException("没有可收纳的文件路径。");
        var normalized = paths.Select(DrawerContentSource.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var references = normalized.Where(path => IsPublic(path, store.DesktopPath, publicDesktop)).ToArray();
        var managed = normalized.Except(references, StringComparer.OrdinalIgnoreCase).ToArray();
        if (managed.Length > 0 && !store.CanAccept(managed, out var reason)) throw new IOException(reason);
        foreach (var path in references) DrawerContentSource.AssertUsable(path);
        return new(managed, references);
    }

    internal static uint SelectEffect(IReadOnlyList<string> paths, string desktop, uint allowed, string? publicDesktop = null)
    {
        var references = paths.Count(path => IsPublic(path, desktop, publicDesktop));
        if (references == 0) return allowed & 2;
        // A MOVE receipt could make the Shell delete a public original. A mixed
        // selection uses COPY; only our worker moves the personal desktop items.
        if (references == paths.Count && (allowed & 4) != 0) return 4;
        return allowed & 1;
    }

    private static bool IsPublic(string path, string desktop, string? publicDesktop)
    {
        publicDesktop ??= Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (publicDesktop.Length == 0 || string.Equals(Path.TrimEndingDirectorySeparator(desktop),
            Path.TrimEndingDirectorySeparator(publicDesktop), StringComparison.OrdinalIgnoreCase)) return false;
        return Path.IsPathFullyQualified(path) && string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)),
            Path.TrimEndingDirectorySeparator(publicDesktop), StringComparison.OrdinalIgnoreCase);
    }
}
