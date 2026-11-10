using System.Windows;

namespace DesktopGroups;

internal sealed partial class StorageWindow
{
    private string? preferenceSyncSourceId;

    private void OpenPreferenceSyncClick(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (SelectedRuntime is not { } source || source.Definition.Removing) return;
        preferenceSyncSourceId = source.Definition.Id;
        var count = runtimes.Values.Count(r => r != source && !r.Definition.Removing);
        PreferenceSyncSummary.Text = $"将「{source.Definition.Name}」的以下参数应用到其余 {count} 个抽屉。";
        SyncExpansionBox.IsChecked = SyncLockBox.IsChecked = true;
        PreferenceSyncError.Visibility = Visibility.Collapsed;
        ConfirmPreferenceSyncButton.IsEnabled = count > 0;
        ShowModal(PreferenceSyncBox);
    });

    private void ConfirmPreferenceSyncClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (closing || exitAfterOperations || disposed) throw new InvalidOperationException("正在退出，请等待文件归还完成。");
            if (preferenceSyncSourceId == null || !runtimes.TryGetValue(preferenceSyncSourceId, out var source) || source.Definition.Removing)
                throw new InvalidOperationException("来源抽屉已不可用，请重新选择。");
            var expansion = SyncExpansionBox.IsChecked == true;
            var locked = SyncLockBox.IsChecked == true;
            if (!expansion && !locked) throw new InvalidOperationException("请至少选择一项要同步的参数。");
            var targets = runtimes.Values.Where(r => r != source && !r.Definition.Removing).ToArray();
            if (targets.Length == 0) throw new InvalidOperationException("当前没有其他可同步的抽屉。");
            if (source.Busy || source.Panel.IsInteracting || targets.Any(r => r.Busy || r.Panel.IsInteracting))
                throw new InvalidOperationException("请等待文件操作和抽屉拖动结束，再同步参数。");

            var foldedValue = source.Row.Store.Profile.Folded;
            var lockedValue = source.Row.Store.Profile.Locked;
            var failures = new List<string>();
            var saved = 0;
            foreach (var target in targets)
            {
                var profile = target.Row.Store.Profile;
                var previousFolded = profile.Folded;
                var previousLocked = profile.Locked;
                if (expansion) profile.Folded = foldedValue;
                if (locked) profile.Locked = lockedValue;
                try { target.Row.Store.Save(); }
                catch (Exception error)
                {
                    profile.Folded = previousFolded;
                    profile.Locked = previousLocked;
                    failures.Add(target.Definition.Name + "：" + error.Message);
                    continue;
                }
                saved++;
                // Keep temporary on-screen geometry and each persisted layout independent.
                target.Panel.Configure(target.Definition.Name, target.Panel.LogicalWidth, target.Panel.LogicalHeight, profile.Locked);
                if (expansion) target.Panel.RestoreFolded(profile.Folded);
                UpdateRow(target);
            }
            UpdateDetail();
            Status($"已将所选参数同步到 {saved} 个抽屉。");
            if (failures.Count > 0) throw new InvalidOperationException($"已同步 {saved} 个抽屉；以下 {failures.Count} 个未能保存，可重试：\n" + string.Join("\n", failures));
            DismissModal();
        }
        catch (Exception error)
        {
            PreferenceSyncError.Text = error.Message;
            PreferenceSyncError.Visibility = Visibility.Visible;
        }
    }
}
