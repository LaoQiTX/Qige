using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace DesktopGroups;

internal sealed partial class StorageWindow
{
    private ContextMenu CreateStyledDrawerMenu(DrawerRuntime runtime, UIElement? anchor = null)
    {
        var menu = new ContextMenu
        {
            Placement = anchor == null ? PlacementMode.MousePoint : PlacementMode.Bottom,
            PlacementTarget = anchor
        };
        menu.Resources.MergedDictionaries.Add(drawerItemMenuResources ??= new ResourceDictionary
            { Source = new Uri("/Qige;component/App/DrawerItemMenu.xaml", UriKind.Relative) });
        menu.Style = (Style)menu.FindResource(typeof(ContextMenu));
        menu.Opened += (_, _) => DrawerMenuAppearance.Apply(menu, anchor == null ? runtime.Panel.Transparency : 0);
        return menu;
    }

    private FrameworkElement CreateTransparencyEditor(DrawerRuntime runtime, bool inMenu = false)
    {
        var panel = new StackPanel { MinWidth = 172 };
        var heading = new Grid();
        heading.Children.Add(new TextBlock { Text = "背景透明度", Foreground = AppPalette.Text, FontSize = 12 });
        var value = new TextBlock { Text = $"{runtime.Panel.Transparency}%", HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = AppPalette.Secondary, FontSize = 12 };
        heading.Children.Add(value); panel.Children.Add(heading);
        var slider = new Slider { Minimum = 0, Maximum = DrawerAppearance.MaxTransparency,
            Value = runtime.Panel.Transparency, TickFrequency = 1, IsSnapToTickEnabled = true,
            SmallChange = 1, LargeChange = 10, Margin = new Thickness(0, 12, 0, 8),
            Style = (Style)FindResource("DrawerTransparencySlider") };
        System.Windows.Automation.AutomationProperties.SetName(slider, "抽屉背景透明度，0% 不透明，80% 更通透");
        panel.Children.Add(slider);
        var endpoints = new Grid();
        endpoints.Children.Add(new TextBlock { Text = "0% 不透明", FontSize = 10, Foreground = AppPalette.Muted });
        endpoints.Children.Add(new TextBlock { Text = "80% 更通透", FontSize = 10, Foreground = AppPalette.Muted,
            HorizontalAlignment = HorizontalAlignment.Right });
        panel.Children.Add(endpoints);

        void Commit() => Try(() =>
        {
            if (disposed || closing || runtime.Definition.Removing || !runtimes.ContainsKey(runtime.Definition.Id)) return;
            var profile = runtime.Row.Store.Profile;
            if (profile.Transparency == runtime.Panel.Transparency) return;
            var previous = profile.Transparency;
            profile.Transparency = runtime.Panel.Transparency;
            try { if (!runtime.Busy) runtime.Row.Store.Save(); }
            catch
            {
                profile.Transparency = previous; runtime.Panel.SetTransparency(previous);
                slider.Value = runtime.Panel.Transparency;
                if (itemMenu != null) DrawerMenuAppearance.Apply(itemMenu, runtime.Panel.Transparency);
                throw;
            }
            if (inMenu && SelectedRuntime == runtime) UpdateDetail();
        });
        slider.ValueChanged += (_, _) =>
        {
            runtime.Panel.SetTransparency((int)Math.Round(slider.Value));
            value.Text = $"{runtime.Panel.Transparency}%";
            if (inMenu && itemMenu != null)
            {
                DrawerMenuAppearance.Apply(itemMenu, runtime.Panel.Transparency);
                foreach (var item in itemMenu.Items.OfType<MenuItem>().Where(item => Equals(item.Tag, "transparency")))
                {
                    item.InputGestureText = value.Text;
                    foreach (var reset in item.Items.OfType<MenuItem>().Where(child => Equals(child.Tag, "transparency-default")))
                        reset.IsChecked = runtime.Panel.Transparency == DrawerAppearance.DefaultTransparency;
                }
            }
            // Preview while dragging; persist once capture ends. Keyboard and
            // track clicks commit immediately without depending on mouse-up.
            if (!slider.IsMouseCaptureWithin) Commit();
        };
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => Commit()));
        slider.LostMouseCapture += (_, _) => Commit();
        slider.LostKeyboardFocus += (_, _) => Commit();
        slider.Unloaded += (_, _) => Commit();
        return panel;
    }
}
