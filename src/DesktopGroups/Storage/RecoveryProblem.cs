using System.IO;

namespace DesktopGroups;

internal sealed record RecoveryProblem(string DrawerId, string DrawerName, string EntryId, string Name,
    string Message, string OriginalPath, string StoredPath, string Phase, bool CanDismiss = false)
{
    public string Location => $"桌面：{OriginalPath}\n收纳：{StoredPath}";
}

internal static class RecoveryInspection
{
    // Called from the file worker on a detached store, never while the UI model is mutating.
    public static IReadOnlyList<RecoveryProblem> Inspect(string id, string name, ShortcutStore store)
    {
        var problems = new List<RecoveryProblem>();
        foreach (var entry in store.Profile.Entries)
        {
            string? message = null;
            var canDismiss = false;
            var path = store.StoredPath(entry);
            try
            {
                var original = ShortcutStore.Exists(entry, entry.OriginalPath); var stored = ShortcutStore.Exists(entry, path);
                if (!original && !stored)
                {
                    canDismiss = store.IsMissing(entry);
                    message = canDismiss
                        ? "桌面和收纳文件均已不存在，不会阻止退出。若已卸载或删除，可移除失效记录；历史记录仍保留。"
                        : "文件未找到，但仍有待处理副本，请检查后重试。";
                }
                else if (entry.PendingRelative != null || entry.Phase is "Collecting" or "Restoring" or "Updating")
                    message = "上次文件操作尚未完成，需要检查并继续恢复。";
                else if (original && stored)
                    message = !entry.IsOrdinaryFile && ShortcutInfo.Read(entry.OriginalPath).Key == entry.Key
                        ? "原桌面又出现了同一入口，可重试检查并同步。"
                        : "原桌面存在同名文件。两份都已保留，请先检查并改名其中一份。";
                else if (stored && !entry.IsOrdinaryFile && ShortcutInfo.Read(path).Key != entry.Key)
                    message = "收纳入口目标已改变，请检查后归还。";
                else if (original && entry.Phase != "Restored") message = "文件在桌面，收纳记录需要恢复确认。";
            }
            catch (Exception e) { message = "检查未完成：" + e.Message; }
            if (message != null) problems.Add(new(id, name, entry.Id, Path.GetFileName(entry.OriginalPath), message,
                entry.OriginalPath, path, entry.Phase, canDismiss));
        }
        return problems;
    }
}
