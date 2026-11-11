using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace DeskIsle.Dock;

internal sealed class DockSettingsWindow : Window
{
    internal DockSettingsWindow(DockController controller)
    {
        Title = "悬浮 Dock"; Width = 420; Height = 430;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(32, 38, 50)); Foreground = Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(24) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "悬浮 Dock", FontSize = 24, Margin = new Thickness(0, 0, 0, 15) });
        var enabled = new CheckBox { Content = "启用主屏 Dock，接管原生任务栏", IsChecked = controller.Settings.Enabled, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 16) };
        panel.Children.Add(enabled);
        panel.Children.Add(new TextBlock { Text = "底边停留唤出；原生任务栏按钮可随时切换。\n首次启用后请检查返回按钮和快捷键。", Foreground = Brushes.LightSlateGray, Margin = new Thickness(0, 0, 0, 16) });
        Slider AddSlider(string title, double min, double max, double value)
        { panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 5, 0, 3) }); var s = new Slider { Minimum = min, Maximum = max, Value = value }; panel.Children.Add(s); return s; }
        var reveal = AddSlider("唤出等待（0～1000 毫秒）", 0, 1000, controller.Settings.RevealMs);
        var hide = AddSlider("移开后收起（200～3000 毫秒）", 200, 3000, controller.Settings.HideMs);
        var opacity = AddSlider("背景不透明度", .5, 1, controller.Settings.Opacity);
        panel.Children.Add(new TextBlock { Text = "切换快捷键：Ctrl + Alt +", Margin = new Thickness(0, 12, 0, 4) });
        var key = new ComboBox { ItemsSource = Enumerable.Range('A', 26).Select(x => ((char)x).ToString()).ToArray(), SelectedItem = ((char)controller.Settings.HotKey).ToString() };
        panel.Children.Add(key);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true }; cancel.Click += (_, _) => Close(); buttons.Children.Add(cancel);
        var save = new Button { Content = "保存" }; buttons.Children.Add(save);
        save.Click += (_, _) =>
        {
            controller.Settings.RevealMs = (int)reveal.Value; controller.Settings.HideMs = (int)hide.Value;
            controller.Settings.Opacity = opacity.Value; controller.Settings.HotKey = (key.SelectedItem as string ?? "D")[0];
            if (controller.Apply(enabled.IsChecked == true)) Close();
        };
        panel.Children.Add(buttons);
    }
}
