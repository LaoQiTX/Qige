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
    public string Description => $"{Store.Profile.Entries.Count} 个入口  ·  {(Store.Profile.Locked ? "已锁定" : "自由布局")}";
    public string State => Definition.Removing ? "待归还后移除" : Issue != null ? "需要检查" : Store.Profile.Entries.Count == 0 ? "空抽屉" : Store.Active.Count > 0 ? "正在桌面上" : "已归还桌面";
    public IReadOnlyList<ManagerContent> Preview { get; set; } = [];
    public string Placement { get; set; } = "";
    public string Summary => $"{Store.Profile.Entries.Count} 个入口 · {(Issue != null ? "需要检查" : Store.Profile.Folded ? "悬停展开" : "常驻展开")}";
    public System.Windows.Visibility EmptyVisibility => Preview.Count == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public void Refresh() { OnChanged(nameof(Preview)); OnChanged(nameof(Placement)); OnChanged(nameof(Summary)); OnChanged(nameof(EmptyVisibility)); OnChanged(nameof(Name)); OnChanged(nameof(Description)); OnChanged(nameof(State)); OnChanged(nameof(Issue)); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
