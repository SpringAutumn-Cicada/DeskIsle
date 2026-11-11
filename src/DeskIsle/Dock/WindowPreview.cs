using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
namespace DeskIsle.Dock;

internal sealed class WindowPreview : Window
{
    private sealed class Tile
    {
        internal required DockWindow Window;
        internal required FrameworkElement Host;
        internal WindowThumbnail? Thumbnail;
    }
    private readonly DockBar bar;
    private readonly Button anchor;
    private DockWindow[] windows;
    private readonly ImageSource? icon;
    private readonly Action<DockWindow> activate;
    private readonly Action dismiss;
    private readonly StackPanel content = new();
    private readonly List<Tile> tiles = [];
    private readonly DispatcherTimer check = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private int page;
    private bool finished;
    private HwndSource? source;
    internal WindowPreview(DockBar bar, Button anchor, DockWindow[] windows, ImageSource? icon, Action<DockWindow> activate, Action dismiss)
    {
        this.bar = bar; this.anchor = anchor; this.windows = windows; this.icon = icon; this.activate = activate; this.dismiss = dismiss;
        Title = "窗口预览"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; Owner = bar;
        // Non-layered destination so DWM can compose its thumbnails directly into the client area.
        Background = Brush("#202936"); Foreground = Brush("#EAF0F8");
        SizeToContent = SizeToContent.WidthAndHeight; UseLayoutRounding = true; SnapsToDevicePixels = true;
        Content = new Border { Padding = new Thickness(12), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), BorderBrush = Brush("#43536B"), Background = Brush("#202936"), Child = content };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            DockNative.SetWindowLong(hwnd, -20, DockNative.GetWindowLong(hwnd, -20) | 0x08000000 | 0x80);
            source = HwndSource.FromHwnd(hwnd); source?.AddHook(NoActivate);
            var corner = 2;
            DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int));
        };
        ContentRendered += (_, _) => { RenderPage(); Place(); check.Start(); };
        LayoutUpdated += (_, _) => UpdateThumbnails();
        SizeChanged += (_, _) => Place();
        check.Tick += (_, _) =>
        {
            var live = windows.Where(w => DockNative.IsWindow(w.Handle) && DockNative.IsWindowVisible(w.Handle)).ToArray();
            if (live.Length == 0) { dismiss(); return; }
            if (live.Length != windows.Length) { windows = live; RenderPage(); }
            else UpdateThumbnails();
        };
        Closed += (_, _) => { finished = true; check.Stop(); Release(); source?.RemoveHook(NoActivate); };
        RenderPage();
    }
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
    private nint NoActivate(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    { if (message == 0x21) { handled = true; return 3; } return 0; }
    private int PerPage => Math.Clamp((int)((SystemParameters.WorkArea.Width - 30) / 270), 1, 4);
    private void RenderPage()
    {
        if (finished) return;
        Release(); content.Children.Clear();
        var perPage = PerPage;
        var pages = Math.Max(1, (windows.Length + perPage - 1) / perPage);
        page = Math.Clamp(page, 0, pages - 1);
        var label = new Grid { Margin = new Thickness(6, 2, 6, 10) };
        label.Children.Add(new TextBlock { Text = "窗口预览", FontSize = 12, Foreground = Brush("#D5E3F4"), FontWeight = FontWeights.SemiBold });
        label.Children.Add(new TextBlock { Text = $"{windows.Length} 个窗口", FontSize = 11, Foreground = Brush("#91A5C0"), HorizontalAlignment = HorizontalAlignment.Right });
        content.Children.Add(label);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(row);
        foreach (var window in windows.Skip(page * perPage).Take(perPage))
        {
            var card = new StackPanel { Width = 260, Margin = new Thickness(4) };
            var heading = new Grid { Margin = new Thickness(4, 0, 0, 8) };
            heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heading.Children.Add(new TextBlock { Text = window.Title, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, ToolTip = window.Title });
            var close = new Button { Content = "×", Width = 26, Height = 26, Padding = new Thickness(0), FontSize = 18, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Focusable = false, ToolTip = "关闭窗口" };
            Grid.SetColumn(close, 1); heading.Children.Add(close);
            close.Click += (_, _) => { if (DockNative.IsWindow(window.Handle)) DockNative.PostMessage(window.Handle, 0x0010, 0, 0); };
            card.Children.Add(heading);
            var fallback = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            fallback.Children.Add(new Image { Source = icon, Width = 38, Height = 38 });
            fallback.Children.Add(new TextBlock { Text = "系统暂未提供窗口画面", Foreground = Brush("#9CACC2"), FontSize = 11, Margin = new Thickness(0, 8, 0, 0) });
            var host = new Grid { Width = 248, Height = 148, Background = Brush("#151D29") };
            host.Children.Add(fallback);
            var open = new Button { Content = host, Padding = new Thickness(2), Margin = new Thickness(0), Focusable = false, Background = Brushes.Transparent, ToolTip = window.Title };
            open.Click += (_, _) => { activate(window); dismiss(); };
            card.Children.Add(open); row.Children.Add(card);
            // Minimized sources may still have a cached DWM frame. Always attempt registration.
            tiles.Add(new Tile { Window = window, Host = host });
        }
        if (pages > 1)
        {
            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
            var previous = new Button { Content = "‹", IsEnabled = page > 0, Focusable = false, Padding = new Thickness(9, 2, 9, 2) };
            var next = new Button { Content = "›", IsEnabled = page < pages - 1, Focusable = false, Padding = new Thickness(9, 2, 9, 2) };
            previous.Click += (_, _) => { page--; RenderPage(); }; next.Click += (_, _) => { page++; RenderPage(); };
            footer.Children.Add(previous); footer.Children.Add(new TextBlock { Text = $"{page + 1} / {pages}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8) }); footer.Children.Add(next); content.Children.Add(footer);
        }
    }
    private void UpdateThumbnails()
    {
        if (finished || !IsLoaded || !IsVisible) return;
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        foreach (var tile in tiles)
        {
            if (tile.Host.ActualWidth <= 0 || !DockNative.IsWindow(tile.Window.Handle)) continue;
            tile.Thumbnail ??= WindowThumbnail.Create(new WindowInteropHelper(this).Handle, tile.Window.Handle);
            if (tile.Thumbnail == null) continue;
            var point = tile.Host.TranslatePoint(new Point(), this);
            var a = scale.Transform(point); var b = scale.Transform(point + new Vector(tile.Host.ActualWidth, tile.Host.ActualHeight));
            tile.Thumbnail.Update(new DockNative.Rect { Left = (int)Math.Round(a.X), Top = (int)Math.Round(a.Y), Right = (int)Math.Round(b.X), Bottom = (int)Math.Round(b.Y) });
        }
    }
    private void Place()
    {
        if (!anchor.IsLoaded) return;
        var point = anchor.PointToScreen(new Point(anchor.ActualWidth / 2, 0));
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var center = transform.Transform(point);
        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(center.X - ActualWidth / 2, area.Left + 4, Math.Max(area.Left + 4, area.Right - ActualWidth - 4));
        Top = Math.Max(area.Top + 4, bar.Top - ActualHeight - 6);
    }
    private void Release() { foreach (var tile in tiles) tile.Thumbnail?.Dispose(); tiles.Clear(); }
}
