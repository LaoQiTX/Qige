using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;

namespace DesktopGroups;

internal sealed partial class StorageWindow
{
    private void OpenDrawerContentMenu(DrawerRuntime runtime)
    {
        var menu = CreateDrawerActionMenu(runtime, "内容操作"); if (menu == null) return;
        var ready = !runtime.Busy && !exitAfterOperations;
        menu.Items.Add(DrawerPasteAction(menu, runtime, ready));
        menu.Items.Add(new Separator { Style = (Style)menu.FindResource(typeof(Separator)) });
        if (runtime.Definition.Kind == DrawerKind.Desktop)
        {
            AddDesktopContentActions(menu, runtime, ready);
        }
        else
        {
            menu.Items.Add(DrawerAction(menu, runtime, "new-folder", "新建文件夹…", "New", () => CreateMapped(runtime, true), ready));
            menu.Items.Add(DrawerAction(menu, runtime, "new-text", "新建文本文档…", "New", () => CreateMapped(runtime, false), ready));
            menu.Items.Add(DrawerAction(menu, runtime, "relocate", "重定位映射目录…", "Location", () => RelocateMapping(runtime), ready));
        }
        menu.Items.Add(new Separator { Style = (Style)menu.FindResource(typeof(Separator)) });
        var directory = DrawerAction(menu, runtime, "directory", "打开抽屉文件夹", "Location", () =>
            Process.Start(DrawerLocationStartInfo(runtime.Definition.Id, terminal: false)));
        directory.ToolTip = runtime.Definition.MappedPath ?? runtime.Row.Store.DirectoryPath; menu.Items.Add(directory);
        var terminal = DrawerAction(menu, runtime, "terminal", "在抽屉目录打开终端", "Terminal", () =>
            Process.Start(DrawerLocationStartInfo(runtime.Definition.Id, terminal: true)));
        terminal.ToolTip = runtime.Definition.MappedPath ?? runtime.Row.Store.DirectoryPath; menu.Items.Add(terminal);
        menu.Items.Add(DrawerAction(menu, runtime, "refresh", "刷新内容", "Refresh", () => _ = RefreshDrawerContentsAsync(runtime), ready));
        menu.Items.Add(new Separator { Style = (Style)menu.FindResource(typeof(Separator)) });
        menu.Items.Add(DrawerAction(menu, runtime, "manage", "管理此抽屉内容", "Manage", () => OpenDrawerContents(runtime)));
        ShowDrawerActionMenu(menu, runtime);
    }
    private void AddContentsMenuClick(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (sender is not Button { IsEnabled: true } button || SelectedRuntime is not { Definition.Kind: DrawerKind.Desktop } runtime) return;
        var menu = CreateDrawerActionMenu(runtime, "添加内容");
        if (menu == null) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        AddDesktopContentActions(menu, runtime, !runtime.Busy && !exitAfterOperations);
        ShowDrawerActionMenu(menu, runtime);
    });

    private void AddDesktopContentActions(ContextMenu menu, DrawerRuntime runtime, bool ready)
    {
        void Add(string key, string title, string icon, Action action, bool enabled, string help)
        {
            var item = DrawerAction(menu, runtime, key, title, icon, action, enabled);
            item.ToolTip = help;
            menu.Items.Add(item);
        }

        Add("add", "收纳桌面文件…", "Properties", () => AddDesktopFiles(runtime), ready && !demo, "将个人或公共桌面上的文件移入此抽屉");
        Add("add-folder", "收纳桌面文件夹…", "Location", () => PickManagedFolder(runtime), ready && !demo, "将个人或公共桌面上的文件夹移入此抽屉");
        menu.Items.Add(new Separator { Style = (Style)menu.FindResource(typeof(Separator)) });
        Add("reference", "引用外部文件…", "Properties", () => ChooseReferences(runtime, false), ready && !demo, "添加文件入口，原文件留在原处");
        Add("reference-folder", "引用外部文件夹…", "Location", () => ChooseReferences(runtime, true), ready && !demo, "添加文件夹入口，原文件夹留在原处");
        menu.Items.Add(new Separator { Style = (Style)menu.FindResource(typeof(Separator)) });
        Add("new", "新建网址快捷方式…", "New", () => ShowNewWebsite(runtime), ready, "为网址创建一个可打开的入口");
    }

    internal ProcessStartInfo DrawerLocationStartInfo(string drawerId, bool terminal)
    {
        var runtime = runtimes[drawerId];
        var directory = runtime.Definition.MappedPath ?? runtime.Row.Store.DirectoryPath;
        if (runtime.Definition.Kind == DrawerKind.FolderMapping) DrawerContentSource.AssertUsable(directory, directory: true);
        else Directory.CreateDirectory(directory);
        return new ProcessStartInfo(terminal ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe") : directory)
            { WorkingDirectory = directory, UseShellExecute = true };
    }
    private MenuItem DrawerPasteAction(ContextMenu menu, DrawerRuntime runtime, bool ready)
    {
        var mapping = runtime.Definition.Kind == DrawerKind.FolderMapping;
        var canPaste = false;
        try
        {
            canPaste = ready && Clipboard.ContainsFileDropList() && (!demo || !mapping
                && runtime.Row.Store.CanAccept(Clipboard.GetFileDropList().Cast<string>().ToArray()));
        }
        catch (System.Runtime.InteropServices.ExternalException) { /* Clipboard temporarily belongs to another app. */ }

        var paste = DrawerAction(menu, runtime, "paste", "粘贴", "Paste", () =>
        {
            if (mapping) PasteIntoMapping(runtime);
            else PasteIntoOrganizer(runtime);
        }, canPaste);
        paste.ToolTip = mapping ? "将文件粘贴到映射目录" : "收纳桌面文件，其他位置的文件添加为引用";
        return paste;
    }

    private void AddDesktopFiles(DrawerRuntime runtime)
    {
        if (runtime.Definition.Kind != DrawerKind.Desktop || runtime.Busy || maintenanceBusy || closing) return;
        var picker = new OpenFileDialog { Title = "选择个人或公共桌面文件（实际收纳）", InitialDirectory = runtime.Row.Store.DesktopPath,
            Filter = "所有文件 (*.*)|*.*", Multiselect = true, DereferenceLinks = false };
        ShowControl();
        if (picker.ShowDialog(this) != true) return;
        Try(() => QueueAddPaths(runtime.Definition.Id, picker.FileNames, "file-picker"));
    }
    private void PickManagedFolder(DrawerRuntime runtime)
    {
        if (demo || runtime.Definition.Kind != DrawerKind.Desktop || runtime.Busy || maintenanceBusy || closing || exitAfterOperations) return;
        ShowControl();
        var picker = new OpenFolderDialog { Title = "选择个人或公共桌面文件夹（实际收纳）", InitialDirectory = runtime.Row.Store.DesktopPath, Multiselect = true };
        if (picker.ShowDialog(this) != true) return;
        QueueAddPaths(runtime.Definition.Id, picker.FolderNames, "folder-picker");
    }

    private async Task RefreshDrawerContentsAsync(DrawerRuntime runtime)
    {
        await RefreshExternalContentsAsync(runtime);
        if (runtime.Definition.Kind == DrawerKind.FolderMapping) return;
        await RunStoreAsync(runtime, "刷新内容", store => runtime.Paused ? store.RestoreAll() : store.Synchronize());
        RefreshItems(runtime); await ScanRecoveryAsync();
    }
    private async void RefreshContentClick(object sender, RoutedEventArgs e) { if (SelectedRuntime is { } runtime) await RefreshDrawerContentsAsync(runtime); }
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
        if (!runtimes.TryGetValue(drawerId, out var runtime) || runtime.Definition.Kind != DrawerKind.Desktop || runtime.Busy || maintenanceBusy || closing) return false;
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
