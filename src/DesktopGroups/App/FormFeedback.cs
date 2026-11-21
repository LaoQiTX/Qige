using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace DesktopGroups;

internal static class FormFeedback
{
    public static readonly DependencyProperty HasErrorProperty = DependencyProperty.RegisterAttached(
        "HasError", typeof(bool), typeof(FormFeedback), new PropertyMetadata(false));

    public static bool GetHasError(DependencyObject input) => (bool)input.GetValue(HasErrorProperty);
    public static void SetHasError(DependencyObject input, bool value) => input.SetValue(HasErrorProperty, value);

    public static void SetError(TextBox input, string? error)
    {
        SetHasError(input, error != null);
        AutomationProperties.SetHelpText(input, error ?? "");
    }

    public static void ShowError(TextBlock message, string error)
    {
        if (message.Text == error) return;
        message.Text = error;
        if (error.Length > 0)
            UIElementAutomationPeer.CreatePeerForElement(message)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
