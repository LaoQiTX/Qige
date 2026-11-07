using System.IO;
using System.Text.Json;

namespace DesktopGroups;

internal sealed record LayoutRecovery(int Version, IReadOnlyList<IconPlacement> OriginalPositions);

internal sealed class LegacyLayoutRecovery(AppStorage storage)
{
    private const string RecoveryPath = "recovery/layout/native-layout-test.json";
    public bool HasRecovery => File.Exists(storage.Resolve(RecoveryPath)) &&
        (JsonSerializer.Deserialize<LayoutRecovery>(File.ReadAllText(storage.Resolve(RecoveryPath)))?.OriginalPositions.Count ?? 0) > 0;

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
        return restored;
    }
}
