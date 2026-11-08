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
}
internal sealed class DrawerSettings
{
    public int Version { get; set; } = 1;
    public bool SnapEnabled { get; set; } = true;
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
        if (Settings.Version != 1) throw new IOException("抽屉列表版本不支持。");
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
            if (drawer.Kind != DrawerKind.Desktop) throw new IOException("此版本尚未支持文件夹映射，请使用支持此类型的版本打开。");
            Stores.Add(drawer.Id, Open(drawer.Id));
        }
        var duplicate = Stores.Values.SelectMany(s => s.Profile.Entries)
            .GroupBy(e => e.OriginalPath, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null) throw new IOException("同一个桌面入口属于多个抽屉，未开始收纳，请检查恢复记录。");
    }
    private ShortcutStore Open(string id) => new(storage, desktop, $"config/groups/{id}.json", $"storage/{id}");
    public void Save() => storage.WriteJson("config/drawers.json", Settings);
    public DrawerDefinition Create(string name)
    {
        ValidateName(name);
        var drawer = new DrawerDefinition { Name = name.Trim() };
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
