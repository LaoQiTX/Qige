using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopGroups;

internal sealed class ProbeWindow : Window
{
    private readonly ProjectStorage storage;
    private readonly StackPanel results = new();
    private readonly Button inspectButton;
    private readonly TextBlock status = new()
    {
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0), Foreground = Brush("#62685F")
    };
    private readonly LayoutTestSession layoutTest;
    private NativeGroupTestWindow? testWindow;
    private IconSpacing spacing = new(75, 97);
    private Button? addIconsButton;

    public ProbeWindow(ProjectStorage storage)
    {
        this.storage = storage;
        layoutTest = new LayoutTestSession(storage);
        Title = "桌面分区 · 融合诊断";
        Width = 880;
        Height = 700;
        MinWidth = 650;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush("#F4F3EF");
        Foreground = Brush("#242824");
        FontFamily = new FontFamily("Microsoft YaHei UI");
        FontSize = 14;
        var root = new DockPanel { Margin = new Thickness(32) };
        Content = root;
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 22) };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "桌面融合诊断", FontSize = 28, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock
        {
            Text = "阶段一 · 读取当前系统，验证原生桌面分组的前提条件",
            Margin = new Thickness(0, 9, 0, 0), Foreground = Brush("#62685F")
        });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 20, 0, 0) };
        header.Children.Add(actions);
        inspectButton = ActionButton("重新检查", async () => await InspectAsync());
        actions.Children.Add(inspectButton);
        actions.Children.Add(ActionButton("打开诊断目录", () =>
        {
            var directory = storage.Resolve("Data/Diagnostics");
            if (System.IO.Directory.Exists(directory)) Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }));
        var testActions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        testActions.Children.Add(ActionButton("创建／显示分区", CreateTestWindow));
        testActions.Children.Add(ActionButton("定位分区", () =>
        {
            CreateTestWindow();
            if (testWindow == null) return;
            testWindow.Reveal();
            storage.WriteJson("Data/Diagnostics/group-window-latest.json", testWindow.Inspect());
            status.Text = "分区在桌面左上区域。诊断窗口已最小化，可回桌面查看；从任务栏打开诊断窗口可继续操作。";
            WindowState = WindowState.Minimized;
        }));
        addIconsButton = ActionButton("加入桌面选中图标", () =>
        {
            try
            {
                if (testWindow == null) throw new InvalidOperationException("请先创建测试边框，再在桌面选择图标。");
                testWindow.Reveal();
                var count = layoutTest.AddSelected(testWindow.NativeX, testWindow.NativeY, testWindow.NativeWidth, spacing);
                status.Text = $"已将 {count} 个选中图标排入测试边框。文件仍在原位置；关闭诊断窗口会尝试恢复原布局。";
            }
            catch (Exception exception) { status.Text = exception.Message; }
        });
        addIconsButton.IsEnabled = false;
        testActions.Children.Add(addIconsButton);
        testActions.Children.Add(ActionButton("恢复图标布局", () =>
        {
            try { status.Text = $"已恢复 {layoutTest.Restore()} 个图标位置。"; }
            catch (Exception exception) { status.Text = $"恢复未完成：{exception.Message}"; }
        }));
        header.Children.Add(testActions);
        header.Children.Add(status);
        var footer = new TextBlock
        {
            Text = "检查仅读取桌面状态。报告位于项目 Data/Diagnostics；“已读取”表示接口可访问，完整兼容性仍需交互验证。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brush("#62685F"), Margin = new Thickness(0, 16, 0, 0)
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = results });
        Loaded += async (_, _) =>
        {
            await InspectAsync();
            CreateTestWindow();
        };
        Closing += (_, eventArgs) =>
        {
            try
            {
                if (layoutTest.HasRecovery) layoutTest.Restore();
                testWindow?.Close();
            }
            catch (Exception exception)
            {
                eventArgs.Cancel = true;
                status.Text = $"退出前恢复未完成：{exception.Message}。请保留恢复记录并处理后再退出。";
            }
        };
    }

    private void CreateTestWindow()
    {
        if (testWindow != null)
        {
            testWindow.Reveal();
            status.Text = "分区已显示。点击“定位分区”可暂时最小化诊断窗口，回桌面查看。";
            return;
        }
        try
        {
            testWindow = new NativeGroupTestWindow(message => status.Text = message,
                (x, y, width) => layoutTest.Reposition(x, y, width, spacing));
            testWindow.Closed += (_, _) =>
            {
                testWindow = null;
                if (addIconsButton != null) addIconsButton.IsEnabled = false;
            };
            testWindow.Show();
            if (addIconsButton != null) addIconsButton.IsEnabled = true;
            storage.WriteJson("Data/Diagnostics/group-window-latest.json", testWindow.Inspect());
        }
        catch (Exception exception)
        {
            status.Text = $"分区显示失败：{exception.Message}。图标加入已停用。";
            testWindow?.Close();
        }
    }

    private async Task InspectAsync()
    {
        inspectButton.IsEnabled = false;
        status.Text = "正在读取显示器、桌面窗口和 Shell 图标接口…";
        try
        {
            var report = await DesktopProbe.CaptureAsync();
            spacing = report.ShellView.Spacing ?? spacing;
            storage.WriteJson("Data/Diagnostics/desktop-latest.json", report);
            results.Children.Clear();
            foreach (var display in report.Displays)
                AddResult(display.Primary ? "主显示器" : "扩展显示器", "已读取",
                    $"{display.Width} × {display.Height}  ·  当前 {display.RefreshHz?.ToString() ?? "未知"} Hz  ·  缩放 {(display.Scale * 100)?.ToString("0") ?? "未知"}%", true);
            foreach (var check in report.Checks)
                AddResult(check.Name, check.Status == "Observed" ? "已读取" : check.Status == "Unavailable" ? "待排查" : "待验证",
                    check.Detail, check.Status == "Observed");
            var related = report.RelatedProcesses.Count == 0 ? "未检测到匹配进程" : string.Join("、", report.RelatedProcesses);
            status.Text = $"诊断已保存。桌面工具：{related}。";
        }
        catch (Exception exception) { status.Text = $"检查未完成：{exception.Message}"; }
        finally { inspectButton.IsEnabled = true; }
    }

    private void AddResult(string title, string state, string detail, bool observed)
    {
        var panel = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        var heading = new DockPanel();
        var stateText = new TextBlock
        {
            Text = state, Foreground = Brush(observed ? "#386249" : "#796B47"),
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0)
        };
        DockPanel.SetDock(stateText, Dock.Right);
        heading.Children.Add(stateText);
        heading.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(heading);
        panel.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#62685F"), Margin = new Thickness(0, 7, 0, 0) });
        results.Children.Add(new Border { Background = Brushes.White, Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(8), Child = panel });
    }

    private static Button ActionButton(string label, Action action)
    {
        var button = new Button
        {
            Content = label, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 10, 0),
            Background = Brushes.White, BorderBrush = Brush("#D5D9D1"), Foreground = Brush("#242824")
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}
