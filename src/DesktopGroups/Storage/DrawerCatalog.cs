using System.IO;
using System.Text.Json;

namespace DesktopGroups;

internal enum DrawerKind { Desktop, FolderMapping }
internal sealed class DrawerDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新抽屉";
    public DrawerKind Kind { get; set; }
    public bool Removing { get; set; }
    public string? MappedPath { get; set; }
    public List<DrawerReference> References { get; set; } = [];
    public DrawerSort Sort { get; set; }
    public List<string> ManualOrder { get; set; } = [];
}
internal sealed class DrawerSettings
{
    public int Version { get; set; } = 1;
    public bool SnapEnabled { get; set; } = true;
    public AppTheme Theme { get; set; }
    public ColorMode ColorMode { get; set; }
    public string? SelectedId { get; set; }
    public List<DrawerDefinition> Drawers { get; set; } = [];
}

// The catalog controls ownership. Each drawer retains its own recovery state machine.
internal sealed class DrawerCatalog
{
    private readonly AppStorage storage;
    private readonly string? desktop;
    public DrawerSettings Settings { get; }
    public Dictionary<string, ShortcutStore> Stores { get; } = [];
    public DrawerCatalog(AppStorage storage, string? desktop = null)
    {
        this.storage = storage; this.desktop = desktop;
        var path = storage.Resolve("config/drawers.json");
        Settings = File.Exists(path) ? JsonSerializer.Deserialize<DrawerSettings>(File.ReadAllText(path))
            ?? throw new IOException("抽屉列表无法读取。") : new();
        if (!Enum.IsDefined(Settings.Theme)) Settings.Theme = AppTheme.Mica;
        if (!Enum.IsDefined(Settings.ColorMode)) Settings.ColorMode = ColorMode.System;
        if (Settings.Version is not (1 or 2)) throw new IOException("抽屉列表版本不支持。");
        if (!File.Exists(path))
        {
            // Adopt the existing beta profile without rewriting shortcut paths.
            Settings.Drawers.Add(new() { Id = "default", Name = "常用" });
            Settings.SelectedId = "default";
            Save();
        }
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drawer in Settings.Drawers)
        {
            if (drawer.Id != "default" && !Guid.TryParseExact(drawer.Id, "N", out _)) throw new IOException("抽屉标识无效。");
            ValidateName(drawer.Name);
            if (!ids.Add(drawer.Id)) throw new IOException("抽屉标识重复。");
            if (!Enum.IsDefined(drawer.Kind) || !Enum.IsDefined(drawer.Sort)) throw new IOException("抽屉类型或排序无效。");
            if (drawer.Kind == DrawerKind.FolderMapping)
            {
                if (Settings.Version < 2 || drawer.MappedPath == null || drawer.References.Count != 0)
                    throw new IOException("文件夹映射配置无效。");
                drawer.MappedPath = NormalizeExternalPath(drawer.MappedPath);
            }
            else if (drawer.MappedPath != null || (Settings.Version < 2 && (drawer.References.Count != 0 || drawer.Sort != DrawerSort.Manual || drawer.ManualOrder.Count != 0)))
                throw new IOException("桌面抽屉配置无效。");
            if (drawer.ManualOrder.Count > 20000 || drawer.ManualOrder.Any(string.IsNullOrWhiteSpace) ||
                drawer.ManualOrder.Distinct(StringComparer.OrdinalIgnoreCase).Count() != drawer.ManualOrder.Count)
                throw new IOException("抽屉手动顺序无效。");
            var referenceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var referencePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var reference in drawer.References)
            {
                reference.Path = NormalizeExternalPath(reference.Path);
                if (!Guid.TryParseExact(reference.Id, "N", out _) || !referenceIds.Add(reference.Id) || !referencePaths.Add(reference.Path))
                    throw new IOException("引用标识或路径重复或无效。");
            }
            var store = Open(drawer.Id);
            if (drawer.Kind == DrawerKind.FolderMapping && (store.Profile.Entries.Count != 0 || store.Profile.Ungrouped.Count != 0))
                throw new IOException("文件夹映射包含异常托管记录，未继续处理。");
            Stores.Add(drawer.Id, store);
        }
        var duplicate = Stores.Values.SelectMany(s => s.Profile.Entries)
            .GroupBy(e => e.OriginalPath, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null) throw new IOException("同一个桌面入口属于多个抽屉，未开始收纳，请检查恢复记录。");
    }
    private ShortcutStore Open(string id) => new(storage, desktop, $"config/groups/{id}.json", $"storage/{id}");
    public void Save()
    {
        if (Settings.Drawers.Any(d => d.Kind == DrawerKind.FolderMapping || d.References.Count > 0 || d.Sort != DrawerSort.Manual || d.ManualOrder.Count > 0))
            Settings.Version = 2;
        storage.WriteJson("config/drawers.json", Settings);
    }

    private string NormalizeExternalPath(string path)
    {
        var full = DrawerContentSource.Normalize(path);
        var protectedRoot = Path.TrimEndingDirectorySeparator(storage.Root);
        if (full.Equals(protectedRoot, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(protectedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("不能使用栖格的数据目录或其内部项目。");
        return full;
    }

    public void Relocate(string id, string path)
    {
        var drawer = Find(id);
        if (drawer.Kind != DrawerKind.FolderMapping || drawer.Removing) throw new IOException("此抽屉无法重定位。");
        var target = NormalizeExternalPath(path);
        DrawerContentSource.AssertUsable(target, directory: true);
        var previous = drawer.MappedPath;
        drawer.MappedPath = target;
        try { Save(); } catch { drawer.MappedPath = previous; throw; }
    }

    public void AddReferences(string id, IReadOnlyList<string> paths)
    {
        var drawer = Find(id);
        if (drawer.Kind != DrawerKind.Desktop || drawer.Removing) throw new IOException("此抽屉无法添加外部引用。");
        var additions = new List<DrawerReference>();
        var existing = drawer.References.Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var target = NormalizeExternalPath(path);
            DrawerContentSource.AssertUsable(target);
            if (existing.Add(target)) additions.Add(new() { Path = target });
        }
        drawer.References.AddRange(additions);
        try { Save(); } catch { foreach (var entry in additions) drawer.References.Remove(entry); throw; }
    }

    public void RelocateReference(string id, string entryId, string path)
    {
        var drawer = Find(id);
        var entry = drawer.References.SingleOrDefault(item => item.Id == entryId) ?? throw new IOException("引用已不存在。");
        var target = NormalizeExternalPath(path);
        DrawerContentSource.AssertUsable(target);
        if (drawer.References.Any(item => item != entry && item.Path.Equals(target, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("此处已引用该项目。");
        var previous = entry.Path;
        entry.Path = target;
        try { Save(); } catch { entry.Path = previous; throw; }
    }

    public void MoveReference(string sourceId, string targetId, string entryId)
    {
        var source = Find(sourceId);
        var target = Find(targetId);
        var index = source.References.FindIndex(entry => entry.Id == entryId);
        if (source == target || source.Removing || target.Removing || target.Kind != DrawerKind.Desktop || index < 0)
            throw new IOException("此引用无法移动。");
        var reference = source.References[index];
        DrawerContentSource.AssertUsable(reference.Path);
        var exists = target.References.Any(entry => entry.Path.Equals(reference.Path, StringComparison.OrdinalIgnoreCase));
        if (!exists) target.References.Add(reference);
        source.References.RemoveAt(index);
        try { Save(); }
        catch
        {
            if (!exists) target.References.Remove(reference);
            source.References.Insert(index, reference);
            throw;
        }
    }

    public void RemoveReference(string id, string entryId)
    {
        var references = Find(id).References;
        var index = references.FindIndex(r => r.Id == entryId);
        if (index < 0) throw new IOException("引用已不存在。");
        var reference = references[index];
        references.RemoveAt(index);
        try { Save(); } catch { references.Insert(index, reference); throw; }
    }
    public DrawerDefinition Create(string name, string? mappedPath = null)
    {
        ValidateName(name);
        var drawer = new DrawerDefinition { Name = name.Trim() };
        if (mappedPath != null)
        {
            drawer.MappedPath = NormalizeExternalPath(mappedPath);
            DrawerContentSource.AssertUsable(drawer.MappedPath, directory: true);
            drawer.Kind = DrawerKind.FolderMapping;
        }
        var store = Open(drawer.Id);
        store.Profile.X = 100 + Settings.Drawers.Count * 36;
        store.Profile.Y = 120 + Settings.Drawers.Count * 36;
        store.Save();
        Settings.Drawers.Add(drawer);
        try { Save(); }
        catch { Settings.Drawers.Remove(drawer); throw; }
        Stores.Add(drawer.Id, store);
        return drawer;
    }
    public void Rename(string id, string name)
    {
        ValidateName(name);
        var drawer = Find(id);
        var previous = drawer.Name; drawer.Name = name.Trim();
        try { Save(); } catch { drawer.Name = previous; throw; }
    }
    public void EnsureOwnership(string id, IReadOnlyList<string> paths)
    {
        Find(id);
        var others = Stores.Where(pair => pair.Key != id).SelectMany(pair => pair.Value.Profile.Entries)
            .Select(entry => entry.OriginalPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (paths.Any(path => others.Contains(Path.GetFullPath(path))))
            throw new IOException("其中有入口已属于另一个抽屉。先从原抽屉归还并解除分组，再加入此处。");
    }
    public IReadOnlyList<string> Remove(string id)
    {
        var drawer = Find(id);
        drawer.Removing = true;
        Save(); // An interrupted remove must restore, never recollect.
        var issues = Stores[id].RestoreAll();
        if (issues.Count > 0) return issues;
        return CompleteRemoval(id);
    }
    internal IReadOnlyList<string> CompleteRemoval(string id)
    {
        var drawer = Find(id);
        if (!drawer.Removing) return ["移除操作尚未开始。"];
        if (Stores[id].Profile.Entries.Any(e => e.Phase != "Restored" || e.PendingRelative != null
            || ShortcutStore.Exists(e, Stores[id].StoredPath(e)) || !ShortcutStore.Exists(e, e.OriginalPath)))
            return ["文件归还尚未确认，抽屉和恢复记录已保留。"];
        // Archive membership, receipts and old versions; removing a view never destroys recovery history.
        storage.WriteJson($"recovery/removed-drawers/{id}.json", new { RemovedAt = DateTimeOffset.Now, Drawer = drawer, Profile = Stores[id].Profile });
        Settings.Drawers.Remove(drawer);
        try { Save(); } catch { Settings.Drawers.Add(drawer); throw; }
        Stores.Remove(id);
        return [];
    }
    private DrawerDefinition Find(string id) => Settings.Drawers.SingleOrDefault(d => d.Id == id)
        ?? throw new IOException("抽屉已不存在。");
    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 40 || name.Any(char.IsControl))
            throw new ArgumentException("抽屉名称需要 1–40 个字符，不能包含控制字符。");
    }
}
