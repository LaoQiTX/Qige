using System.Diagnostics;
using System.IO;
using System.Windows;

namespace DesktopGroups;

internal enum SettingsSection { General, Appearance, Recovery, About }

internal sealed partial class StorageWindow
{
    private void OpenContent(string path)
    {
        try { ShellItem.Open(path); }
        catch (Exception error)
        {
            ShowControl();
            Status("无法打开：" + error.Message);
            MessageBox.Show(this, error.Message, "无法打开此项目", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal void ShowSettings(SettingsSection section = SettingsSection.General)
    {
        RefreshStartup(); RefreshBackupControls(); _ = RefreshBackupListAsync();
        SnapBox.IsChecked = catalog.Settings.SnapEnabled;
        SelectSettingsSection(section);
        ShowModal(SettingsBox);
        (section == SettingsSection.General ? GeneralSettingsTab
            : section == SettingsSection.Appearance ? AppearanceSettingsTab
            : section == SettingsSection.Recovery ? RecoverySettingsTab : AboutSettingsTab).Focus();
    }

    private void SelectSettingsSection(SettingsSection section)
    {
        GeneralSettingsPage.Visibility = section == SettingsSection.General ? Visibility.Visible : Visibility.Collapsed;
        AppearanceSettingsPage.Visibility = section == SettingsSection.Appearance ? Visibility.Visible : Visibility.Collapsed;
        RecoverySettingsPage.Visibility = section == SettingsSection.Recovery ? Visibility.Visible : Visibility.Collapsed;
        AboutSettingsPage.Visibility = section == SettingsSection.About ? Visibility.Visible : Visibility.Collapsed;
        GeneralSettingsTab.IsChecked = section == SettingsSection.General;
        AppearanceSettingsTab.IsChecked = section == SettingsSection.Appearance;
        RecoverySettingsTab.IsChecked = section == SettingsSection.Recovery;
        AboutSettingsTab.IsChecked = section == SettingsSection.About;
        SettingsScroll.ScrollToTop();
        if (section == SettingsSection.Recovery) { RefreshHealthText(); _ = ScanRecoveryAsync(); }
    }

    private void SettingsClick(object sender, RoutedEventArgs e) => ShowSettings();
    private void GeneralSettingsClick(object sender, RoutedEventArgs e) => SelectSettingsSection(SettingsSection.General);
    private void AppearanceSettingsClick(object sender, RoutedEventArgs e) => SelectSettingsSection(SettingsSection.Appearance);
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

    private void OpenProjectClick(object sender, RoutedEventArgs e) => OpenExternalPage("https://github.com/LaoQiTX/Qige");
    private void SupportProjectClick(object sender, RoutedEventArgs e) => OpenExternalPage("https://ifdian.net/a/QiTongXue");

    private void OpenExternalPage(string address) => Try(() =>
        Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }));
}
