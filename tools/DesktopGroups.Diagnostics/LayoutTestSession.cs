using System.IO;
using System.Text.Json;

namespace DesktopGroups;

internal sealed class LayoutTestSession(AppStorage storage)
{
    private const string RecoveryPath = "recovery/layout/native-layout-test.json";
    private readonly Dictionary<string, IconPlacement> originals = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<IconPlacement> Members => originals.Values.ToArray();
    public bool HasRecovery => File.Exists(storage.Resolve(RecoveryPath)) &&
        (JsonSerializer.Deserialize<LayoutRecovery>(File.ReadAllText(storage.Resolve(RecoveryPath)))?.OriginalPositions.Count ?? 0) > 0;

    public int AddSelected(int x, int y, int width, IconSpacing spacing)
    {
        if (originals.Count == 0 && HasRecovery)
            throw new InvalidOperationException("存在上次未完成的布局恢复记录，请先恢复。");
        var selected = NativeIconLayout.ReadSelection();
        if (selected.Count == 0) throw new InvalidOperationException("请先在真实桌面上选择几个图标，再回到这里加入测试分区。");
        var total = originals.Keys.Concat(selected.Select(item => item.Identity)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var columns = Math.Max(1, (width - 24) / Math.Max(1, spacing.X));
        if (total > columns * 2)
            throw new InvalidOperationException($"这个测试边框最多放 {columns * 2} 个图标；请先少量测试。正式分区滚动尚未实现。");
        foreach (var item in selected) originals.TryAdd(item.Identity, item);
        storage.WriteJson(RecoveryPath, new LayoutRecovery(1, Members));
        Reposition(x, y, width, spacing);
        return selected.Count;
    }

    public void Reposition(int x, int y, int width, IconSpacing spacing)
    {
        if (originals.Count == 0) return;
        var columns = Math.Max(1, (width - 24) / Math.Max(1, spacing.X));
        var positions = Members.Select((item, index) => item with
        {
            X = x + 12 + index % columns * spacing.X,
            Y = y + 52 + index / columns * spacing.Y
        }).ToArray();
        if (NativeIconLayout.Position(positions) != positions.Length)
            throw new InvalidOperationException("部分图标已发生变化，已保留原位置记录，请恢复或重新检查。");
    }

    public int Restore()
    {
        if (!HasRecovery) return 0;
        var snapshot = JsonSerializer.Deserialize<LayoutRecovery>(File.ReadAllText(storage.Resolve(RecoveryPath)))
            ?? throw new InvalidOperationException("无法读取恢复记录。");
        if (snapshot.Version != 1) throw new InvalidOperationException("恢复记录版本不支持。");
        var restored = NativeIconLayout.Position(snapshot.OriginalPositions);
        if (restored != snapshot.OriginalPositions.Count)
            throw new InvalidOperationException($"已恢复 {restored} 项，其他图标当前不存在；恢复记录已保留。");
        // Preserve a readable local history rather than deleting recovery evidence.
        storage.WriteJson("recovery/layout/native-layout-test-last-restored.json", snapshot);
        storage.WriteJson(RecoveryPath, new LayoutRecovery(1, []));
        originals.Clear();
        return restored;
    }
}
