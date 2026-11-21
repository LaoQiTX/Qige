using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopGroups;

// Popup windows cannot infer menu depth from the visual tree. Supply the
// submenu surface explicitly from the logical menu hierarchy instead.
internal static class DrawerMenuAppearance
{
    public static readonly DependencyProperty SubmenuBackgroundProperty = DependencyProperty.RegisterAttached(
        "SubmenuBackground", typeof(Brush), typeof(DrawerMenuAppearance), new PropertyMetadata(null));
    public static Brush? GetSubmenuBackground(DependencyObject target) => (Brush?)target.GetValue(SubmenuBackgroundProperty);
    public static void SetSubmenuBackground(DependencyObject target, Brush value) => target.SetValue(SubmenuBackgroundProperty, value);

    private static readonly DependencyProperty TransparencyProperty = DependencyProperty.RegisterAttached(
        "Transparency", typeof(int), typeof(DrawerMenuAppearance), new PropertyMetadata(0));

    internal static void Refresh(ContextMenu menu) => Apply(menu, (int)menu.GetValue(TransparencyProperty));

    internal static void Apply(ContextMenu menu, int transparency)
    {
        menu.SetValue(TransparencyProperty, transparency);
        menu.Background = DrawerAppearance.Current.MenuSurface(transparency, 1);
        ApplyChildren(menu.Items, transparency, 2);
    }

    private static void ApplyChildren(ItemCollection items, int transparency, int depth)
    {
        var background = DrawerAppearance.Current.MenuSurface(transparency, depth);
        foreach (var item in items.OfType<MenuItem>())
        {
            SetSubmenuBackground(item, background);
            ApplyChildren(item.Items, transparency, depth + 1);
        }
    }
}
