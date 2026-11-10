using System.Diagnostics;
using System.IO;
using System.Windows;

namespace DesktopGroups;

internal enum SettingsSection { General, Recovery, About }

internal sealed partial class StorageWindow
{
    internal void ShowSettings(SettingsSection section = SettingsSection.General)
    {
        RefreshStartup();
        SnapBox.IsChecked = catalog.Settings.SnapEnabled;
        SelectSettingsSection(section);
        ShowModal(SettingsBox);
        (section == SettingsSection.General ? GeneralSettingsTab
            : section == SettingsSection.Recovery ? RecoverySettingsTab : AboutSettingsTab).Focus();
    }

    private void SelectSettingsSection(SettingsSection section)
    {
        GeneralSettingsPage.Visibility = section == SettingsSection.General ? Visibility.Visible : Visibility.Collapsed;
        RecoverySettingsPage.Visibility = section == SettingsSection.Recovery ? Visibility.Visible : Visibility.Collapsed;
        AboutSettingsPage.Visibility = section == SettingsSection.About ? Visibility.Visible : Visibility.Collapsed;
        GeneralSettingsTab.IsChecked = section == SettingsSection.General;
        RecoverySettingsTab.IsChecked = section == SettingsSection.Recovery;
        AboutSettingsTab.IsChecked = section == SettingsSection.About;
        SettingsScroll.ScrollToTop();
        if (section == SettingsSection.Recovery) { RefreshHealthText(); _ = ScanRecoveryAsync(); }
    }

    private void SettingsClick(object sender, RoutedEventArgs e) => ShowSettings();
    private void GeneralSettingsClick(object sender, RoutedEventArgs e) => SelectSettingsSection(SettingsSection.General);
    private void RecoverySettingsClick(object sender, RoutedEventArgs e) => SelectSettingsSection(SettingsSection.Recovery);
    private void AboutSettingsClick(object sender, RoutedEventArgs e) => SelectSettingsSection(SettingsSection.About);

    private void RestoreAllClick(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (closing || exitAfterOperations) return;
        if (BusyOperations > 0) { Status("请等待当前文件操作完成，再归还全部内容。"); return; }
        foreach (var runtime in runtimes.Values)
        {
            runtime.Paused = true;
            _ = RunStoreAsync(runtime, "归还桌面", store => store.RestoreAll());
        }
    });

    private void RecollectAllClick(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (closing || exitAfterOperations) return;
        if (BusyOperations > 0) { Status("请等待当前文件操作完成，再全部收纳。"); return; }
        RecoverDesktop(false);
        foreach (var runtime in runtimes.Values.Where(r => r.Panel.IsAttached && !r.Definition.Removing && !r.Busy))
        {
            runtime.Paused = false;
            _ = RunStoreAsync(runtime, "重新收纳", store => store.StartSession());
        }
    });

    private void OpenDataClick(object sender, RoutedEventArgs e) => Try(() =>
        Process.Start(new ProcessStartInfo(storage.Root) { UseShellExecute = true }));

    private void OpenGuideClick(object sender, RoutedEventArgs e) => Try(() =>
    {
        var guide = Path.Combine(AppContext.BaseDirectory, "使用说明.md");
        if (!File.Exists(guide)) throw new IOException("使用指南未找到，请检查软件文件是否完整。");
        Process.Start(new ProcessStartInfo(guide) { UseShellExecute = true });
    });
}
