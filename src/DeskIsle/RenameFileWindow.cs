using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskIsle.Services;
namespace DeskIsle;

internal sealed class RenameFileWindow : Window
{
    internal string FileName => input.Text.Trim();
    private readonly TextBox input;
    internal RenameFileWindow(Window owner, string name)
    {
        Owner = owner; Title = "重命名"; Width = 390; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Foreground = Brushes.White; UseLayoutRounding = true;
        var panel = new StackPanel { Margin = new Thickness(22) };
        Content = new Border { Child = panel, CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Color.FromRgb(32, 41, 54)), BorderBrush = Brushes.SlateGray, BorderThickness = new Thickness(1) };
        var title = new TextBlock { Text = "重命名", FontSize = 18, Margin = new Thickness(0, 0, 0, 16) };
        title.MouseLeftButtonDown += (_, _) => DragMove(); panel.Children.Add(title);
        input = new TextBox { Text = name, FontSize = 14, Padding = new Thickness(10), Background = new SolidColorBrush(Color.FromRgb(23, 31, 43)), Foreground = Brushes.White, BorderBrush = Brushes.SlateGray, MaxLength = 255 };
        panel.Children.Add(input);
        var error = new TextBlock { Foreground = Brushes.LightCoral, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) }; panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 15, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true }; cancel.Click += (_, _) => Close(); buttons.Children.Add(cancel);
        var save = new Button { Content = "确定", IsDefault = true }; buttons.Children.Add(save);
        save.Click += (_, _) =>
        {
            try { NewItemService.ValidateName(FileName, NewItemKind.Folder); DialogResult = true; }
            catch (ArgumentException ex) { error.Text = ex.Message; }
        };
        panel.Children.Add(buttons);
        ContentRendered += (_, _) => { input.Focus(); var dot = name.LastIndexOf('.'); input.Select(0, dot > 0 ? dot : name.Length); };
    }
}
