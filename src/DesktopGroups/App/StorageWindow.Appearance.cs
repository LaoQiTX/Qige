using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;

namespace DesktopGroups;

internal sealed partial class StorageWindow
{
    private readonly Func<bool> readSystemDark;
    private ThemeVariant? activeTheme;
    private ContextMenu? themedOpenMenu;
    private ResourceDictionary? themeResources;
    private DispatcherOperation? pendingThemeRefresh;
    private bool glassEnabled, pendingWallpaperRefresh;
    private Border? activeMaterialOverlay;
    private int modalTransition;

    private void ApplyTheme(bool wallpaperChanged = false)
    {
        var settings = catalog.Settings;
        var variant = ThemeVariant.Resolve(settings.Theme, settings.ColorMode,
            settings.ColorMode == ColorMode.System && readSystemDark());
        if (AppearanceSettingsPage != null) RefreshAppearanceControls(variant);
        var glass = AppPalette.TransparencyEnabled();
        var changed = variant != activeTheme;
        if (!changed && glass == glassEnabled && !(wallpaperChanged && glass)) return;
        if (wallpaperChanged || glass != glassEnabled) DesktopGlass.Invalidate();
        glassEnabled = DrawerAppearance.GlassEnabled = glass;

        activeTheme = variant;
        DrawerAppearance.Apply(variant);
        if (changed) themeResources = AppPalette.Apply(variant, themeResources);
        ApplyCaptionTheme(this);
        ApplyManagerGlass();
        foreach (Window ownerDialog in OwnedWindows) ApplyCaptionTheme(ownerDialog);
        if (Overview != null) Overview.InvalidateVisual();
        if (themedOpenMenu != null) DrawerMenuAppearance.Refresh(themedOpenMenu);
        foreach (var runtime in runtimes.Values) runtime.Panel.Render();
    }

    private void InitializeAppearance()
    {
        MicaLightPreview.Content = CreateThemePreview(new(AppTheme.Mica, false));
        MicaDarkPreview.Content = CreateThemePreview(new(AppTheme.Mica, true));
        AcrylicLightPreview.Content = CreateThemePreview(new(AppTheme.Acrylic, false));
        AcrylicDarkPreview.Content = CreateThemePreview(new(AppTheme.Acrylic, true));
        RefreshAppearanceControls(activeTheme!.Value);
        SourceInitialized += (_, _) =>
        {
            ApplyCaptionTheme(this);
            ApplyManagerGlass();
            var source = (HwndSource?)PresentationSource.FromVisual(this);
            source?.AddHook(WindowThemeMessage);
        };
    }

    private nint WindowThemeMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        OnThemeMessage(message, wParam);
        return 0;
    }

    private void OnThemeMessage(int message, nint action)
    {
        var wallpaperChanged = message is 0x007E or 0x031E || (message == 0x001A && action == 20);
        if (!wallpaperChanged && !AppPalette.IsThemeMessage(message)) return;
        if (disposed || closing) return;
        pendingWallpaperRefresh |= wallpaperChanged;
        if (pendingThemeRefresh?.Status == DispatcherOperationStatus.Pending) return;
        pendingThemeRefresh = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            var refreshWallpaper = pendingWallpaperRefresh;
            pendingWallpaperRefresh = false;
            if (!disposed && !closing) ApplyTheme(refreshWallpaper);
        });
    }

    private void ApplyManagerGlass()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source || source.CompositionTarget == null) return;
        const int systemBackdropType = 38;
        const int noBackdrop = 1, mainWindow = 2, transientWindow = 3;
        var backdrop = !glassEnabled ? noBackdrop : activeTheme?.Theme == AppTheme.Acrylic ? transientWindow : mainWindow;
        var native = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)
            && DwmSetWindowAttribute(source.Handle, systemBackdropType, ref backdrop, sizeof(int)) == 0
            && glassEnabled;
        var chrome = WindowChrome.GetWindowChrome(this);
        if (chrome != null)
        {
            if (chrome.IsFrozen) { chrome = (WindowChrome)chrome.Clone(); WindowChrome.SetWindowChrome(this, chrome); }
            chrome.UseAeroCaptionButtons = !native;
            chrome.GlassFrameThickness = native ? WindowChrome.GlassFrameCompleteThickness : new Thickness(0);
        }
        if (native)
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            Background = Brushes.Transparent;
            // Keep the app's manual color choice legible even when DWM chooses
            // its own fallback tint (for example when the window is inactive).
            ManagerFrame.Background = activeTheme switch
            {
                { Theme: AppTheme.Acrylic, Dark: true } => AppPalette.Solid(0x242321, 145),
                { Theme: AppTheme.Acrylic } => AppPalette.Solid(0xFAF8F0, 176),
                { Dark: true } => AppPalette.Solid(0x242629, 170),
                _ => AppPalette.Solid(0xF4F6F8, 188)
            };
        }
        else
        {
            SetResourceReference(BackgroundProperty, "PaletteWindowSurface");
            ManagerFrame.SetResourceReference(Border.BackgroundProperty, "PaletteWindowSurface");
        }
    }

    private void ApplyCaptionTheme(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        var dark = activeTheme?.Dark == true ? 1 : 0;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    private void AnimateMaterialOverlay(FrameworkElement overlay)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var duration = new Duration(TimeSpan.FromMilliseconds(135));
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var offset = new TranslateTransform();
        overlay.RenderTransform = offset;
        overlay.BeginAnimation(OpacityProperty, new DoubleAnimation(.9, 1, duration)
            { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
        offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(4, 0, duration)
            { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
    }

    private void DismissMaterialOverlay()
    {
        if (activeMaterialOverlay is not { } overlay || !SystemParameters.ClientAreaAnimation)
        {
            DismissModal();
            return;
        }
        if (!ModalLayer.IsHitTestVisible) return;
        ModalLayer.IsHitTestVisible = false;
        var token = ++modalTransition;
        var duration = new Duration(TimeSpan.FromMilliseconds(105));
        var offset = new TranslateTransform();
        overlay.RenderTransform = offset;
        offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 3, duration)
            { FillBehavior = FillBehavior.Stop });
        var fade = new DoubleAnimation(overlay.Opacity, 0, duration) { FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => { if (token == modalTransition) DismissModal(); };
        overlay.BeginAnimation(OpacityProperty, fade);
    }

    private void RefreshAppearanceControls(ThemeVariant variant)
    {
        MicaThemeChoice.IsChecked = catalog.Settings.Theme == AppTheme.Mica;
        AcrylicThemeChoice.IsChecked = catalog.Settings.Theme == AppTheme.Acrylic;
        SystemColorChoice.IsChecked = catalog.Settings.ColorMode == ColorMode.System;
        LightColorChoice.IsChecked = catalog.Settings.ColorMode == ColorMode.Light;
        DarkColorChoice.IsChecked = catalog.Settings.ColorMode == ColorMode.Dark;
        CurrentSystemColor.Text = catalog.Settings.ColorMode == ColorMode.System
            ? variant.Dark ? "Windows 当前使用暗色。" : "Windows 当前使用亮色。"
            : "";
        CurrentSystemColor.Visibility = catalog.Settings.ColorMode == ColorMode.System ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AppearanceChoiceClick(object sender, RoutedEventArgs e)
    {
        var settings = catalog.Settings;
        var oldTheme = settings.Theme;
        var oldMode = settings.ColorMode;
        if (sender == MicaThemeChoice) settings.Theme = AppTheme.Mica;
        else if (sender == AcrylicThemeChoice) settings.Theme = AppTheme.Acrylic;
        else if (sender == SystemColorChoice) settings.ColorMode = ColorMode.System;
        else if (sender == LightColorChoice) settings.ColorMode = ColorMode.Light;
        else if (sender == DarkColorChoice) settings.ColorMode = ColorMode.Dark;
        FormFeedback.ShowError(AppearanceError, "");
        if (settings.Theme == oldTheme && settings.ColorMode == oldMode) return;
        try { catalog.Save(); }
        catch (Exception error)
        {
            settings.Theme = oldTheme;
            settings.ColorMode = oldMode;
            RefreshAppearanceControls(activeTheme!.Value);
            FormFeedback.ShowError(AppearanceError, "无法保存外观设置：" + error.Message);
            return;
        }
        ApplyTheme();
    }

    private static Border CreateThemePreview(ThemeVariant variant)
    {
        var appearance = DrawerAppearance.For(variant);
        var preview = new StackPanel { IsHitTestVisible = false };
        preview.Children.Add(new TextBlock { Text = variant.Dark ? "暗色" : "亮色", FontSize = 11,
            FontWeight = FontWeights.SemiBold, Foreground = appearance.Text });
        preview.Children.Add(new Border { Height = 1, Background = appearance.Divider, Margin = new(0, 5, 0, 5) });
        var items = new UniformGrid { Rows = 1, Columns = 3 };
        for (var index = 0; index < 3; index++)
        {
            items.Children.Add(new Border
            {
                Height = 15, Margin = new(0, 0, index == 2 ? 0 : 5, 0), CornerRadius = new(3),
                Background = index == 0 ? appearance.Selected : appearance.Hover,
                Child = new Border { Width = 7, Height = 7, CornerRadius = new(2),
                    Background = index == 0 ? appearance.Accent : appearance.Placeholder }
            });
        }
        preview.Children.Add(items);
        return new Border { Background = appearance.Surface(0), BorderBrush = appearance.Edge,
            BorderThickness = new(1), CornerRadius = new(6), Padding = new(8), Child = preview };
    }
}