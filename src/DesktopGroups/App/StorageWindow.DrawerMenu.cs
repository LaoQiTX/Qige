using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace DesktopGroups;

internal sealed partial class StorageWindow
{
    private void SavePanelPreferences(DrawerRuntime runtime)
    {
        var profile = runtime.Row.Store.Profile;
        profile.Folded = runtime.Panel.IsFolded;
        profile.ManualExpansion = runtime.Panel.ManualExpansion;
        profile.ManualCollapsed = runtime.Panel.ManualCollapsed;
        profile.Density = runtime.Panel.Density;
        if (!runtime.Busy) runtime.Row.Store.Save();
        UpdateRow(runtime); UpdateDetail();
    }

    private void OpenDrawerSettingsMenu(DrawerRuntime runtime)
    {
        if (itemMenu != null) itemMenu.IsOpen = false;
        if (disposed || closing || maintenanceBusy || runtime.Definition.Removing) { runtime.Panel.CloseItemMenu(); return; }
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, MaxHeight = 640 };
        menu.Resources.MergedDictionaries.Add(drawerItemMenuResources ??= new ResourceDictionary
        { Source = new Uri("/Qige;component/App/DrawerItemMenu.xaml", UriKind.Relative) });
        menu.Style = (Style)menu.FindResource(typeof(ContextMenu));
        itemMenu = menu;
        void Heading(string text) => menu.Items.Add(new TextBlock
        { Text = text, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)), Margin = new Thickness(12, 8, 12, 4) });
        void Add(string key, string text, bool selected, Action action)
        {
            var entry = new MenuItem { Header = text, Tag = key, Style = (Style)menu.FindResource(typeof(MenuItem)),
                Icon = new TextBlock { Text = selected ? "✓" : "", FontSize = 12 } };
            entry.Click += (_, _) => Try(() =>
            {
                if (disposed || closing || maintenanceBusy || !runtimes.ContainsKey(runtime.Definition.Id)) return;
                action();
            });
            menu.Items.Add(entry);
        }
        Heading(runtime.Definition.Name + " · 展开方式");
        Add("always", "常驻展开", !runtime.Panel.IsFolded, () => runtime.Panel.SetFolded(false));
        Add("hover", "悬停展开", runtime.Panel.IsFolded && !runtime.Panel.ManualExpansion, () => runtime.Panel.SetFolded(true));
        Add("manual", "手动展开 / 折叠", runtime.Panel.ManualExpansion, runtime.Panel.SetManualMode);
        Add("toggle", runtime.Panel.IsExpanded ? "立即折叠" : "立即展开", false, runtime.Panel.ToggleManual);
        menu.Items.Add(new Separator());
        Heading("图标间距");
        foreach (var (density, name) in new[] { (IconDensity.Compact, "紧凑"), (IconDensity.Standard, "标准"), (IconDensity.Spacious, "宽松") })
            Add("density-" + density, name, runtime.Panel.Density == density, () => runtime.Panel.SetDensity(density));
        menu.Items.Add(new Separator());
        Add("lock", "锁定位置与尺寸", runtime.Panel.LayoutLocked, () =>
        {
            var p = runtime.Row.Store.Profile; var old = p.Locked; p.Locked = !old;
            try { if (!runtime.Busy) runtime.Row.Store.Save(); } catch { p.Locked = old; throw; }
            runtime.Panel.Configure(runtime.Definition.Name, runtime.Panel.LogicalWidth, runtime.Panel.LogicalHeight, p.Locked);
            UpdateDetail(); UpdateRow(runtime);
        });
        Add("sync", "同步参数到全部抽屉…", false, () =>
        { SelectDrawer(runtime.Definition.Id); ShowControl(); OpenPreferenceSyncClick(this, new RoutedEventArgs()); });
        Add("manage", "管理此抽屉", false, () => { SelectDrawer(runtime.Definition.Id); ShowControl(); });
        menu.Closed += (_, _) => { if (itemMenu == menu) itemMenu = null; runtime.Panel.CloseItemMenu(); };
        try { menu.IsOpen = true; } catch { itemMenu = null; runtime.Panel.CloseItemMenu(); throw; }
    }
}
