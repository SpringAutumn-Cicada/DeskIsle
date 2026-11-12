using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
namespace DeskIsle.Dock;

public partial class DockBar
{
    private sealed class BackgroundView
    {
        internal required DockPin App;
        internal required Button Button;
        internal required Image Image;
        internal required TextBlock Placeholder;
        internal long IconVersion;
        internal string? IconKey;
        internal long RetryAt;
    }
    private readonly Dictionary<string, BackgroundView> backgroundViews = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> backgroundOrder = [];
    private bool backgroundBusy;
    private async Task RefreshBackgroundApps()
    {
        if (closed || backgroundBusy) return;
        backgroundBusy = true;
        try
        {
            var apps = await controller.BackgroundApps();
            if (closed || !IsVisible) return;
            var alive = apps.Select(a => a.Executable!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var changed = backgroundOrder.Count != alive.Count || backgroundOrder.Any(id => !alive.Contains(id));
            foreach (var stale in backgroundViews.Keys.Where(id => !alive.Contains(id)).ToArray())
            { backgroundViews[stale].IconVersion++; backgroundViews.Remove(stale); }
            backgroundOrder.RemoveAll(id => !alive.Contains(id));
            foreach (var app in apps)
            {
                var id = app.Executable!;
                if (!backgroundViews.TryGetValue(id, out var view))
                {
                    var image = new Image { Width = 22, Height = 22, Stretch = Stretch.Uniform };
                    var placeholder = new TextBlock { Text = app.Name[..1], FontSize = 13, Foreground = Brushes.LightSteelBlue, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    var content = new Grid(); content.Children.Add(placeholder); content.Children.Add(image);
                    var button = new Button { Style = (Style)FindResource("TerminalKey"), Width = 28, Content = content };
                    view = new BackgroundView { App = app, Image = image, Button = button, Placeholder = placeholder };
                    var retained = view;
                    button.Click += (_, _) => { CloseSystemPanel(); ClosePreview(); controller.OpenPinned(retained.App, false); };
                    backgroundViews.Add(id, view);
                    RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                }
                view.App = app;
                view.Button.ToolTip = app.Name + " · 后台运行，点击恢复";
                System.Windows.Automation.AutomationProperties.SetName(view.Button, "恢复后台应用 " + app.Name);
                if (!backgroundOrder.Contains(id, StringComparer.OrdinalIgnoreCase)) backgroundOrder.Add(id);
            }
            BackgroundKey.ToolTip = backgroundOrder.Count > 0 ? $"后台应用（{backgroundOrder.Count}）" : "后台应用";
            if (changed && systemPopup?.PlacementTarget == BackgroundKey) ShowBackgroundPanel();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { BackgroundKey.ToolTip = "后台应用暂不可读取"; }
        finally { backgroundBusy = false; }
    }
    private async void LoadBackgroundIcon(BackgroundView view)
    {
        var pixels = (int)Math.Clamp(Math.Ceiling(28 * VisualTreeHelper.GetDpi(this).DpiScaleX * 1.5), 96, 256);
        var path = view.App.Executable!;
        var key = path + "|background|" + pixels;
        if (closed || view.IconKey == key || Environment.TickCount64 < view.RetryAt) return;
        view.IconKey = key;
        var version = ++view.IconVersion;
        ImageSource? icon = null;
        if (icon == null && !icons.TryGetValue(key, out icon))
        {
            try
            {
                await iconSlots.WaitAsync();
                try { if (closed || version != view.IconVersion) return; icon = await Task.Run(() => DockApps.Icon(path, pixels)); }
                finally { iconSlots.Release(); }
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or System.Runtime.InteropServices.COMException) { icon = null; }
        }
        if (closed || version != view.IconVersion) return;
        if (icon == null) { view.IconKey = null; view.RetryAt = Environment.TickCount64 + 3000; return; }
        if (icons.Count >= 256) icons.Clear();
        icons[key] = icon;
        view.Image.Source = icon; view.Placeholder.Visibility = Visibility.Collapsed;
    }
    private void BackgroundClick(object sender, RoutedEventArgs e)
    {
        if (systemPopup?.PlacementTarget == BackgroundKey && systemPopup.IsOpen) { CloseSystemPanel(); return; }
        ShowBackgroundPanel();
    }
    private void ShowBackgroundPanel()
    {
        var panel = new StackPanel { Width = Math.Clamp(backgroundOrder.Count, 1, 5) * 44 };
        var extra = backgroundOrder.Select(id => backgroundViews[id]).ToArray();
        if (extra.Length > 0)
        {
            var grid = new WrapPanel { Width = panel.Width, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var view in extra)
            {
                LoadBackgroundIcon(view);
                var image = new Image { Width = 28, Height = 28, SnapsToDevicePixels = true, UseLayoutRounding = true };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                image.SetBinding(Image.SourceProperty, new Binding(nameof(Image.Source)) { Source = view.Image });
                var content = new Grid();
                var placeholder = new TextBlock { Text = view.App.Name[..1], FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.LightSteelBlue };
                placeholder.SetBinding(VisibilityProperty, new Binding(nameof(Visibility)) { Source = view.Placeholder });
                content.Children.Add(placeholder); content.Children.Add(image);
                var button = new Button { Style = (Style)FindResource("TerminalKey"), Width = 44, Height = 44, ToolTip = view.App.Name, Content = content };
                System.Windows.Automation.AutomationProperties.SetName(button, "恢复后台应用 " + view.App.Name);
                button.Click += (_, _) => { CloseSystemPanel(); controller.OpenPinned(view.App, false); };
                grid.Children.Add(button);
            }
            panel.Children.Add(new ScrollViewer { Content = grid, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        }
        else panel.Children.Add(PanelText("暂无可识别的后台应用"));
        OpenSystemPanel(BackgroundKey, panel);
    }
}
