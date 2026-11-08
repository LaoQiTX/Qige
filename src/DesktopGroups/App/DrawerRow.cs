using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DesktopGroups;

internal sealed class DrawerRow(DrawerDefinition definition, ShortcutStore store) : INotifyPropertyChanged
{
    public DrawerDefinition Definition { get; } = definition;
    public ShortcutStore Store { get; } = store;
    public string Id => Definition.Id;
    public string Name => Definition.Name;
    public string? Issue { get; set; }
    public string NavigationGroup => Definition.Kind == DrawerKind.FolderMapping ? "文件夹映射抽屉" : "桌面整理抽屉";
    public string NavigationSubtitle => $"{Store.Profile.Entries.Count} 个项目 · {(Definition.Removing ? "待归还" : Issue != null ? "待解决" : Store.Profile.Folded ? "悬停展开" : "常驻展开")}";
    public string NavigationBadge => Definition.Removing ? "待归还" : Issue != null ? "有冲突" : "";
    public System.Windows.Visibility NavigationBadgeVisibility => NavigationBadge.Length == 0 ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    public System.Windows.Visibility NavigationDotVisibility => NavigationBadge.Length == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public string NavigationIcon => Definition.Kind == DrawerKind.FolderMapping
        ? "M 2,5 C 2,4 3,3 4,3 L 8,3 L 10,5 L 18,5 C 19,5 20,6 20,7 L 20,17 C 20,18 19,19 18,19 L 4,19 C 3,19 2,18 2,17 Z"
        : Issue != null || Definition.Removing
            ? "M 5,2 L 13,2 L 18,7 L 18,20 L 5,20 Z M 13,2 L 13,7 L 18,7"
            : "M 4,3 L 18,3 Q 20,3 20,5 L 20,18 Q 20,20 18,20 L 4,20 Q 2,20 2,18 L 2,5 Q 2,3 4,3 Z M 2,8 L 20,8 M 8,8 L 8,20";
    public string Description => $"{Store.Profile.Entries.Count} 个入口  ·  {(Store.Profile.Locked ? "已锁定" : "自由布局")}";
    public string State => Definition.Removing ? "待归还后移除" : Issue != null ? "需要检查" : Store.Profile.Entries.Count == 0 ? "空抽屉" : Store.Active.Count > 0 ? "正在桌面上" : "已归还桌面";
    public IReadOnlyList<ManagerContent> Preview { get; set; } = [];
    public string Placement { get; set; } = "";
    public string Summary => $"{Store.Profile.Entries.Count} 个入口 · {(Issue != null ? "需要检查" : Store.Profile.Folded ? "悬停展开" : "常驻展开")}";
    public System.Windows.Visibility EmptyVisibility => Preview.Count == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public void Refresh() { OnChanged(nameof(NavigationGroup)); OnChanged(nameof(NavigationSubtitle)); OnChanged(nameof(NavigationBadge)); OnChanged(nameof(NavigationBadgeVisibility)); OnChanged(nameof(NavigationDotVisibility)); OnChanged(nameof(NavigationIcon)); OnChanged(nameof(Preview)); OnChanged(nameof(Placement)); OnChanged(nameof(Summary)); OnChanged(nameof(EmptyVisibility)); OnChanged(nameof(Name)); OnChanged(nameof(Description)); OnChanged(nameof(State)); OnChanged(nameof(Issue)); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
