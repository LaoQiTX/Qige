using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MenuIcon = System.Windows.Shapes.Path;

namespace DesktopGroups;

internal sealed partial class StorageWindow
{
    private void SavePanelPreferences(DrawerRuntime runtime)
    {
        var profile = runtime.Row.Store.Profile;
        profile.Folded = runtime.Panel.IsFolded; profile.ManualExpansion = runtime.Panel.ManualExpansion;
        profile.ManualCollapsed = runtime.Panel.ManualCollapsed; profile.Density = runtime.Panel.Density;
        if (!runtime.Busy) runtime.Row.Store.Save();
        UpdateRow(runtime); UpdateDetail();
    }
    private ContextMenu? CreateDrawerActionMenu(DrawerRuntime runtime, string purpose)
    {
        if (itemMenu != null) itemMenu.IsOpen = false;
        if (disposed || closing || maintenanceBusy || runtime.Definition.Removing) { runtime.Panel.CloseItemMenu(); return null; }
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        menu.Resources.MergedDictionaries.Add(drawerItemMenuResources ??= new ResourceDictionary
            { Source = new Uri("/Qige;component/App/DrawerItemMenu.xaml", UriKind.Relative) });
        menu.Style = (Style)menu.FindResource(typeof(ContextMenu));
        menu.Items.Add(new MenuItem { Header = runtime.Definition.Name, InputGestureText = purpose, IsEnabled = false,
            Style = (Style)menu.FindResource("DrawerMenuHeading") });
        menu.Closed += (_, _) => { if (itemMenu == menu) itemMenu = null; runtime.Panel.CloseItemMenu(); };
        itemMenu = menu; return menu;
    }
    private MenuItem DrawerAction(ContextMenu menu, DrawerRuntime runtime, string key, string title, string icon, Action? action = null, bool enabled = true)
    {
        var item = new MenuItem { Header = title, Tag = key, IsEnabled = enabled, Style = (Style)menu.FindResource(typeof(MenuItem)),
            Icon = new MenuIcon { Data = (Geometry)menu.FindResource("DrawerMenu" + icon + "Icon"), Style = (Style)menu.FindResource("DrawerMenuIcon") } };
        if (action != null) item.Click += (_, e) =>
        {
            e.Handled = true;
            Try(() => { if (!disposed && !closing && !maintenanceBusy && runtimes.ContainsKey(runtime.Definition.Id)) action(); });
        };
        return item;
    }
    private void ShowDrawerActionMenu(ContextMenu menu, DrawerRuntime runtime)
    {
        try { menu.IsOpen = true; } catch { itemMenu = null; runtime.Panel.CloseItemMenu(); throw; }
    }
    private void OpenDrawerSettingsMenu(DrawerRuntime runtime)
    {
        var menu = CreateDrawerActionMenu(runtime, "抽屉设置"); if (menu == null) return;
        var mode = DrawerAction(menu, runtime, "expansion", "展开方式", "Mode");
        mode.InputGestureText = runtime.Panel.ManualExpansion ? "手动" : runtime.Panel.IsFolded ? "悬停" : "常开";
        void Choice(MenuItem parent, string key, string title, bool selected, Action action)
        {
            var item = DrawerAction(menu, runtime, key, title, "Mode", action); item.IsCheckable = true; item.IsChecked = selected; parent.Items.Add(item);
        }
        Choice(mode, "always", "常驻展开", !runtime.Panel.IsFolded, () => runtime.Panel.SetFolded(false));
        Choice(mode, "hover", "悬停展开", runtime.Panel.IsFolded && !runtime.Panel.ManualExpansion, () => runtime.Panel.SetFolded(true));
        Choice(mode, "manual", "手动展开 / 折叠", runtime.Panel.ManualExpansion, runtime.Panel.SetManualMode);
        menu.Items.Add(mode);
        var densityMenu = DrawerAction(menu, runtime, "density", "图标间距", "Density");
        foreach (var (value, name) in new[] { (IconDensity.Compact, "紧凑"), (IconDensity.Standard, "标准"), (IconDensity.Spacious, "宽松") })
        {
            Choice(densityMenu, "density-" + value, name, runtime.Panel.Density == value, () => runtime.Panel.SetDensity(value));
            if (runtime.Panel.Density == value) densityMenu.InputGestureText = name;
        }
        menu.Items.Add(densityMenu);
        var locked = DrawerAction(menu, runtime, "lock", "锁定位置与尺寸", "Lock", () =>
        {
            var p = runtime.Row.Store.Profile; var old = p.Locked; p.Locked = !old;
            try { if (!runtime.Busy) runtime.Row.Store.Save(); } catch { p.Locked = old; throw; }
            runtime.Panel.Configure(runtime.Definition.Name, runtime.Panel.LogicalWidth, runtime.Panel.LogicalHeight, p.Locked);
            UpdateDetail(); UpdateRow(runtime);
        });
        locked.IsCheckable = true; locked.IsChecked = runtime.Panel.LayoutLocked; menu.Items.Add(locked);
        menu.Items.Add(new Separator { Style = (Style)menu.FindResource(typeof(Separator)) });
        menu.Items.Add(DrawerAction(menu, runtime, "sync", "同步设置到其他抽屉…", "Manage", () =>
            { SelectDrawer(runtime.Definition.Id); ShowControl(); OpenPreferenceSyncClick(this, new RoutedEventArgs()); }));
        menu.Items.Add(DrawerAction(menu, runtime, "manage", "管理此抽屉内容", "Open", () => OpenDrawerContents(runtime)));
        ShowDrawerActionMenu(menu, runtime);
    }
    private void OpenDrawerContents(DrawerRuntime runtime)
    {
        SearchBox.Clear(); SelectDrawer(runtime.Definition.Id); SetManagerMode(true); ShowControl();
    }
}
