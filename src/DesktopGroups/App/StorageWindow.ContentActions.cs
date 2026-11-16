using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace DesktopGroups;

internal sealed partial class StorageWindow
{
    private void OpenDrawerContentMenu(DrawerRuntime runtime)
    {
        var menu = CreateDrawerActionMenu(runtime, "内容操作"); if (menu == null) return;
        var ready = !runtime.Busy && !exitAfterOperations;
        menu.Items.Add(DrawerAction(menu, runtime, "new", "新建网址快捷方式…", "New", () => ShowNewWebsite(runtime), ready));
        menu.Items.Add(DrawerAction(menu, runtime, "add", "添加桌面快捷方式…", "Open", () => AddDesktopShortcuts(runtime), ready));
        var paste = DrawerAction(menu, runtime, "paste", "粘贴桌面快捷方式", "Paste", () =>
        {
            var paths = ClipboardDesktopPaths(runtime);
            if (paths.Length == 0 || !QueueAddPaths(runtime.Definition.Id, paths)) Status("剪贴板中没有可添加的个人桌面快捷方式。");
        }, ready && ClipboardDesktopPaths(runtime).Length > 0);
        paste.ToolTip = "将剪贴板中的个人桌面 .lnk / .url 收纳到此抽屉"; menu.Items.Add(paste);
        menu.Items.Add(new Separator { Style = (Style)menu.FindResource(typeof(Separator)) });
        menu.Items.Add(DrawerAction(menu, runtime, "directory", "打开桌面文件夹", "Location", () =>
            Process.Start(new ProcessStartInfo(runtime.Row.Store.DesktopPath) { UseShellExecute = true })));
        menu.Items.Add(DrawerAction(menu, runtime, "terminal", "在桌面打开终端", "Terminal", () =>
            Process.Start(new ProcessStartInfo("cmd.exe") { WorkingDirectory = runtime.Row.Store.DesktopPath, UseShellExecute = true })));
        menu.Items.Add(DrawerAction(menu, runtime, "refresh", "刷新内容", "Refresh", () => _ = RefreshDrawerContentsAsync(runtime), ready));
        menu.Items.Add(new Separator { Style = (Style)menu.FindResource(typeof(Separator)) });
        menu.Items.Add(DrawerAction(menu, runtime, "manage", "管理此抽屉内容", "Manage", () => OpenDrawerContents(runtime)));
        ShowDrawerActionMenu(menu, runtime);
    }
    private string[] ClipboardDesktopPaths(DrawerRuntime runtime)
    {
        try
        {
            if (!Clipboard.ContainsFileDropList()) return [];
            var paths = Clipboard.GetFileDropList().Cast<string>().ToArray();
            return runtime.Row.Store.CanAccept(paths) ? paths : [];
        }
        catch (System.Runtime.InteropServices.ExternalException) { return []; }
    }
    private void AddDesktopShortcuts(DrawerRuntime runtime)
    {
        if (runtime.Busy || maintenanceBusy || closing) return;
        var picker = new OpenFileDialog { Title = "选择个人桌面的快捷方式", InitialDirectory = runtime.Row.Store.DesktopPath,
            Filter = "快捷方式 (*.lnk;*.url)|*.lnk;*.url", Multiselect = true };
        ShowControl();
        if (picker.ShowDialog(this) != true) return;
        if (!runtime.Row.Store.CanAccept(picker.FileNames)) { Status("请选择个人桌面根目录中的 .lnk / .url。其他位置的文件暂不收纳。"); return; }
        Try(() => QueueAddPaths(runtime.Definition.Id, picker.FileNames));
    }
    private async Task RefreshDrawerContentsAsync(DrawerRuntime runtime)
    {
        await RunStoreAsync(runtime, "刷新内容", store => runtime.Paused ? store.RestoreAll() : store.Synchronize());
        RefreshItems(runtime); await ScanRecoveryAsync();
    }
    private void AddContentClick(object sender, RoutedEventArgs e) { if (SelectedRuntime is { } runtime) AddDesktopShortcuts(runtime); }
    private async void RefreshContentClick(object sender, RoutedEventArgs e) { if (SelectedRuntime is { } runtime) await RefreshDrawerContentsAsync(runtime); }
    private void NewWebsiteClick(object sender, RoutedEventArgs e) { if (SelectedRuntime is { } runtime) ShowNewWebsite(runtime); }
    private string? newWebsiteDrawerId;
    private void ShowNewWebsite(DrawerRuntime runtime)
    {
        if (runtime.Busy || closing || maintenanceBusy) return;
        OpenDrawerContents(runtime); newWebsiteDrawerId = runtime.Definition.Id;
        WebsiteNameBox.Text = ""; WebsiteUrlBox.Text = ""; WebsiteErrorLabel.Text = "";
        WebsiteDrawerLabel.Text = "添加到「" + runtime.Definition.Name + "」";
        ShowModal(NewWebsiteBox); WebsiteNameBox.Focus();
    }
    private async void CreateWebsiteClick(object sender, RoutedEventArgs e)
    {
        if (newWebsiteDrawerId == null) return;
        if (await CreateWebsiteAsync(newWebsiteDrawerId, WebsiteNameBox.Text, WebsiteUrlBox.Text)) DismissModal();
        else WebsiteErrorLabel.Text = StatusLabel.Text;
    }
    internal async Task<bool> CreateWebsiteAsync(string drawerId, string name, string url)
    {
        if (!runtimes.TryGetValue(drawerId, out var runtime) || runtime.Busy || maintenanceBusy || closing) return false;
        try
        {
            var path = ShortcutStore.WebsitePath(runtime.Row.Store.DesktopPath, name, url);
            catalog.EnsureOwnership(drawerId, [path]);
            if (File.Exists(path) || Directory.Exists(path) || runtime.Row.Store.Profile.Entries.Any(e => e.OriginalPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("已有同名入口，请使用其他名称。");
            return await RunStoreAsync(runtime, "新建网址快捷方式", store => { store.CreateWebsite(name, url); return []; });
        }
        catch (Exception error) { Status(error.Message); return false; }
    }
}
