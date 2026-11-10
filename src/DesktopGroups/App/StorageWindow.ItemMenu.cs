using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MenuIcon = System.Windows.Shapes.Path;

namespace DesktopGroups;

internal sealed partial class StorageWindow
{
    private ContextMenu? itemMenu;
    private ResourceDictionary? drawerItemMenuResources;
    internal ContextMenu? ActiveItemMenu => itemMenu;

    private void OpenDrawerItemMenu(DrawerRuntime runtime, string path)
    {
        if (itemMenu != null) itemMenu.IsOpen = false;
        var entry = runtime.Row.Store.Active.FirstOrDefault(e => string.Equals(runtime.Row.Store.StoredPath(e), path, StringComparison.OrdinalIgnoreCase));
        if (disposed || closing || runtime.Definition.Removing || entry == null)
        {
            runtime.Panel.CloseItemMenu();
            return;
        }
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        menu.Resources.MergedDictionaries.Add(drawerItemMenuResources ??= new ResourceDictionary
        {
            Source = new Uri("/Qige;component/App/DrawerItemMenu.xaml", UriKind.Relative)
        });
        menu.Style = (Style)menu.FindResource(typeof(ContextMenu));
        itemMenu = menu;
        var name = Path.GetFileNameWithoutExtension(entry.OriginalPath);
        menu.Items.Add(new MenuItem
        {
            Header = name, ToolTip = name, IsEnabled = false,
            Style = (Style)menu.FindResource("DrawerMenuHeading")
        });
        menu.Items.Add(new Separator { Style = (Style)menu.FindResource(typeof(Separator)) });
        void Add(string key, string text, Action action, bool enabled = true)
        {
            var item = new MenuItem
            {
                Header = text, Tag = key, IsEnabled = enabled,
                Style = (Style)menu.FindResource(typeof(MenuItem)),
                Icon = new MenuIcon
                {
                    Data = (Geometry)menu.FindResource("DrawerMenu" + char.ToUpperInvariant(key[0]) + key[1..] + "Icon"),
                    Style = (Style)menu.FindResource("DrawerMenuIcon")
                }
            };
            item.Click += (_, _) => Try(action);
            menu.Items.Add(item);
        }
        void WithFile(Action action)
        {
            if (disposed || closing || exitAfterOperations || runtime.Definition.Removing || runtime.Busy)
                throw new InvalidOperationException("请等待文件操作结束后重试。");
            if (!runtime.Row.Store.Active.Any(e => string.Equals(runtime.Row.Store.StoredPath(e), path, StringComparison.OrdinalIgnoreCase))
                || !File.Exists(path)) throw new IOException("入口已归还或暂不可用，请重新打开菜单。");
            action();
        }
        Add("open", "打开", () => WithFile(() => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })), !runtime.Busy);
        Add("location", "打开快捷方式所在位置", () => WithFile(() =>
        {
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            info.ArgumentList.Add("/select,"); info.ArgumentList.Add(path); Process.Start(info);
        }), !runtime.Busy);
        Add("properties", "属性", () => WithFile(() =>
        {
            var info = new ShortcutShellExecuteInfo
            {
                Size = Marshal.SizeOf<ShortcutShellExecuteInfo>(), Mask = 0xC | 0x100,
                Verb = "properties", File = path, Show = 1
            };
            if (!ShellExecuteEx(ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }), !runtime.Busy);
        menu.Items.Add(new Separator { Style = (Style)menu.FindResource(typeof(Separator)) });
        Add("return", "归还桌面并解除分组", () => WithFile(() =>
        {
            _ = RunStoreAsync(runtime, "归还并解除分组", store => { store.ReturnAndUngroup(path); return []; });
        }), !runtime.Busy);
        Add("manage", "管理此抽屉", () => { SelectDrawer(runtime.Definition.Id); ShowControl(); });
        menu.Closed += (_, _) =>
        {
            if (itemMenu == menu) itemMenu = null;
            runtime.Panel.CloseItemMenu();
        };
        try { menu.IsOpen = true; }
        catch { itemMenu = null; runtime.Panel.CloseItemMenu(); throw; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShortcutShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public nint Window;
        public string? Verb, File, Parameters, Directory;
        public int Show;
        public nint Instance, IdList;
        public string? Class;
        public nint ClassKey;
        public uint HotKey;
        public nint Icon, Process;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref ShortcutShellExecuteInfo info);
}
