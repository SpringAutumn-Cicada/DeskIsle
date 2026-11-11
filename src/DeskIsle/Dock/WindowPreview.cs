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
        internal required Action Click;
        internal Action? Close;
        internal required object Group;
        internal bool Composite;
        internal int Attempts, Failures;
        internal long RetryAt;
        internal long RetryBatchAt;
    }
    private readonly DockBar bar;
    private readonly Button anchor;
    private DockWindow[] windows;
    private readonly ImageSource? icon;
    private readonly Action<DockWindow> activate;
    private readonly Action dismiss;
    private readonly StackPanel content = new();
    private readonly SplitGroups groups;
    private readonly List<Tile> tiles = [];
    private readonly DispatcherTimer check = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private int page;
    private bool finished;
    private long outsideSince;
    private HwndSource? source;
    internal WindowPreview(DockBar bar, Button anchor, DockWindow[] windows, ImageSource? icon, Action<DockWindow> activate, Action dismiss, SplitGroups groups)
    {
        this.bar = bar; this.anchor = anchor; this.windows = windows; this.icon = icon; this.activate = activate; this.dismiss = dismiss; this.groups = groups;
        Title = "窗口预览"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; Owner = bar;
        // Non-layered destination so DWM can compose its thumbnails directly into the client area.
        Background = Brush("#202936"); Foreground = Brush("#EAF0F8");
        SizeToContent = SizeToContent.WidthAndHeight; UseLayoutRounding = true; SnapsToDevicePixels = true;
        Content = new Border { Padding = new Thickness(3), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), BorderBrush = Brush("#52647C"), Background = Brush("#202936"), Child = content };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            DockNative.SetWindowLong(hwnd, -20, DockNative.GetWindowLong(hwnd, -20) | 0x08000000 | 0x80);
            source = HwndSource.FromHwnd(hwnd); source?.AddHook(NoActivate);
            var corner = 2;
            DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int));
        };
        Loaded += (_, _) => { UpdateThumbnails(); check.Start(); };
        LayoutUpdated += (_, _) => UpdateThumbnails();
        SizeChanged += (_, _) => Place();
        check.Tick += (_, _) =>
        {
            if (ContainsInteractionPointer()) outsideSince = 0;
            else if (outsideSince == 0) outsideSince = Environment.TickCount64;
            else if (Environment.TickCount64 - outsideSince >= 350) { dismiss(); return; }
            var live = windows.Where(w => DockNative.IsWindow(w.Handle)).ToArray();
            if (live.Length == 0) { dismiss(); return; }
            if (live.Length != windows.Length) { windows = live; RenderPage(); }
            else UpdateThumbnails();
        };
        Closed += (_, _) => { finished = true; check.Stop(); Release(); source?.RemoveHook(NoActivate); };
        new WindowInteropHelper(this).EnsureHandle();
        RenderPage();
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Arrange(new Rect(new Point(), DesiredSize));
        UpdateLayout(); Place();
        UpdateThumbnails(false);
    }
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
    private nint NoActivate(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    { if (message == 0x21) { handled = true; return 3; } return 0; }
    private int PerPage => Math.Clamp((int)((SystemParameters.WorkArea.Width - 300) / 260), 1, 3);
    private void RenderPage()
    {
        if (finished) return;
        var previousTiles = tiles.ToDictionary(tile => (tile.Window.Handle, tile.Composite));
        tiles.Clear(); content.Children.Clear();
        var perPage = PerPage;
        var pages = Math.Max(1, (windows.Length + perPage - 1) / perPage);
        page = Math.Clamp(page, 0, pages - 1);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(row);
        void AddImage(DockWindow window, Panel parent, double width, double height, Action click, Action? close, object? group = null)
        {
            var host = new Grid { Width = width, Height = height, Background = Brush("#151D29"), Margin = new Thickness(3), VerticalAlignment = VerticalAlignment.Center };
            host.Children.Add(new Image { Source = icon, Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
            host.MouseLeftButtonUp += (_, _) => click();
            if (!previousTiles.Remove((window.Handle, group != null), out var tile))
                tile = new Tile { Window = window, Host = host, Click = click, Close = close, Group = group ?? host, Composite = group != null };
            tile.Window = window; tile.Host = host; tile.Click = click; tile.Close = close; tile.Group = group ?? host;
            EnsureThumbnail(tile);
            if (group == null && tile.Thumbnail is { AspectRatio: > 0 } thumbnail)
            {
                var ratio = thumbnail.AspectRatio;
                host.Width = Math.Clamp(160 * ratio, 32, 320);
                host.Height = host.Width / ratio + 22;
            }
            parent.Children.Add(host);
            tiles.Add(tile);
        }
        if (page == 0 && groups.Find(windows) is { } pair)
        {
            var width = 300.0;
            var unionHeight = Math.Max(pair.LeftRect.Bottom, pair.RightRect.Bottom) - Math.Min(pair.LeftRect.Top, pair.RightRect.Top);
            var unionWidth = pair.RightRect.Right - pair.LeftRect.Left;
            var height = width * unionHeight / Math.Max(1, unionWidth) + 22;
            var composite = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2) };
            Action restore = () => { SplitGroups.Restore(pair); dismiss(); };
            AddImage(pair.Left, composite, width * (pair.LeftRect.Right - pair.LeftRect.Left) / Math.Max(1, unionWidth), height, restore, () => { if (DockNative.IsWindow(pair.Left.Handle)) DockNative.PostMessage(pair.Left.Handle, 0x0010, 0, 0); }, composite);
            AddImage(pair.Right, composite, width * (pair.RightRect.Right - pair.RightRect.Left) / Math.Max(1, unionWidth), height, restore, () => { if (DockNative.IsWindow(pair.Right.Handle)) DockNative.PostMessage(pair.Right.Handle, 0x0010, 0, 0); }, composite);
            row.Children.Add(composite);
        }
        foreach (var window in windows.Skip(page * perPage).Take(perPage))
        {
            var width = 240.0; var height = 135.0;
            if (DockNative.GetWindowRect(window.Handle, out var bounds) && !DockNative.IsIconic(window.Handle) && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top)
            {
                var ratio = (double)(bounds.Right - bounds.Left) / (bounds.Bottom - bounds.Top);
                width = Math.Min(250, 156 * ratio); height = width / ratio;
            }
            // Equal row height avoids blank areas around windows with different aspect ratios.
            var ratioAtSize = width / height;
            height = 160; width = height * ratioAtSize;
            AddImage(window, row, Math.Max(24, width), height,
                () => { activate(window); dismiss(); }, () => { if (DockNative.IsWindow(window.Handle)) DockNative.PostMessage(window.Handle, 0x0010, 0, 0); });
        }
        foreach (var obsolete in previousTiles.Values) obsolete.Thumbnail?.Dispose();
        // Fit the whole row as a unit; no tile becomes disproportionately large on small screens.
        var rowWidth = tiles.Sum(tile => tile.Host.Width + 6) + 12;
        var rowHeight = tiles.Count == 0 ? 160 : tiles.Max(tile => tile.Host.Height);
        var fit = Math.Min(1, Math.Min((SystemParameters.WorkArea.Width - 24) / Math.Max(1, rowWidth),
            (SystemParameters.WorkArea.Height * .35) / Math.Max(1, rowHeight)));
        if (fit < 1)
            foreach (var tile in tiles) { tile.Host.Width *= fit; tile.Host.Height *= fit; }
        if (pages > 1)
        {
            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
            var previous = new Button { Content = "‹", IsEnabled = page > 0, Focusable = false, Padding = new Thickness(9, 2, 9, 2) };
            var next = new Button { Content = "›", IsEnabled = page < pages - 1, Focusable = false, Padding = new Thickness(9, 2, 9, 2) };
            previous.Click += (_, _) => { page--; RenderPage(); }; next.Click += (_, _) => { page++; RenderPage(); };
            footer.Children.Add(previous); footer.Children.Add(next); content.Children.Add(footer);
        }
    }
    private void UpdateThumbnails(bool present = true)
    {
        if (finished || (present && (!IsLoaded || !IsVisible))) return;
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        object? hoverGroup = null;
        // Before Show(), descendants have no PresentationSource even when the HWND exists.
        if (present && IsLoaded && DockNative.GetCursorPos(out var mouse))
            foreach (var tile in tiles)
            {
                if (PresentationSource.FromVisual(tile.Host) == null) continue;
                var start = tile.Host.PointToScreen(new Point());
                var end = tile.Host.PointToScreen(new Point(tile.Host.ActualWidth, tile.Host.ActualHeight));
                if (tile.Thumbnail?.ContainsPointer() == true || (mouse.X >= start.X && mouse.X < end.X && mouse.Y >= start.Y && mouse.Y < end.Y)) { hoverGroup = tile.Group; break; }
            }
        foreach (var tile in tiles)
        {
            if (tile.Host.ActualWidth <= 0 || !DockNative.IsWindow(tile.Window.Handle)) continue;
            EnsureThumbnail(tile);
            if (tile.Thumbnail == null) continue;
            tile.Thumbnail.Feedback(ReferenceEquals(hoverGroup, tile.Group));
            var point = tile.Host.TranslatePoint(new Point(), this);
            var a = scale.Transform(point); var b = scale.Transform(point + new Vector(tile.Host.ActualWidth, tile.Host.ActualHeight));
            if (tile.Thumbnail.Update(new DockNative.Rect { Left = (int)Math.Round(a.X), Top = (int)Math.Round(a.Y), Right = (int)Math.Round(b.X), Bottom = (int)Math.Round(b.Y) }, present))
                tile.Failures = 0;
            else if (++tile.Failures >= 3)
            {
                tile.Thumbnail.Dispose(); tile.Thumbnail = null; tile.Failures = 0;
                tile.RetryAt = Environment.TickCount64 + 500;
            }
        }
    }
    private void EnsureThumbnail(Tile tile)
    {
        var now = Environment.TickCount64;
        if (tile.Thumbnail != null || now < tile.RetryAt) return;
        if (tile.Attempts >= 3)
        {
            if (tile.RetryBatchAt == 0) tile.RetryBatchAt = now + 3000;
            if (now < tile.RetryBatchAt) return;
            tile.Attempts = 0; tile.RetryBatchAt = 0;
        }
        tile.Attempts++;
        tile.RetryAt = Environment.TickCount64 + 500;
        // Indirection keeps retained native surfaces bound to the latest layout/click action.
        tile.Thumbnail = WindowThumbnail.Create(new WindowInteropHelper(this).Handle, tile.Window.Handle,
            () => tile.Click(), () => tile.Close?.Invoke());
    }
    private bool ContainsInteractionPointer()
    {
        if (ContainsPointer()) return true;
        if (!anchor.IsLoaded || PresentationSource.FromVisual(anchor) == null || !DockNative.GetCursorPos(out var mouse)) return false;
        var a = anchor.PointToScreen(new Point());
        var b = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
        if (mouse.X >= a.X && mouse.X < b.X && mouse.Y >= a.Y && mouse.Y < b.Y) return true;
        // Preserve the preview while crossing the gap between its native surface and its icon.
        if (!DockNative.GetWindowRect(new WindowInteropHelper(this).Handle, out var box)) return false;
        return mouse.X >= Math.Min(box.Left, a.X) && mouse.X < Math.Max(box.Right, b.X)
            && mouse.Y >= box.Bottom && mouse.Y <= a.Y;
    }
    internal bool ContainsPointer()
    {
        if (tiles.Any(tile => tile.Thumbnail?.ContainsPointer() == true)) return true;
        if (!IsVisible || !DockNative.GetCursorPos(out var point) || !DockNative.GetWindowRect(new WindowInteropHelper(this).Handle, out var box)) return false;
        return point.X >= box.Left && point.X < box.Right && point.Y >= box.Top && point.Y < box.Bottom;
    }
    private void Place()
    {
        if (!anchor.IsLoaded || PresentationSource.FromVisual(anchor) == null) return;
        var point = anchor.PointToScreen(new Point(anchor.ActualWidth / 2, 0));
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var center = transform.Transform(point);
        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(center.X - ActualWidth / 2, area.Left + 4, Math.Max(area.Left + 4, area.Right - ActualWidth - 4));
        Top = Math.Max(area.Top + 4, bar.Top - ActualHeight - 6);
    }
    internal void SetWindows(DockWindow[] latest)
    {
        if (finished) return;
        // An enumeration snapshot can omit a window during minimize/activation transitions.
        // While interacting, retain living handles until the source really closes.
        if (ContainsInteractionPointer())
            latest = latest.Concat(windows.Where(w => DockNative.IsWindow(w.Handle)))
                .DistinctBy(w => w.Handle).OrderBy(w => w.Handle.ToInt64()).ToArray();
        var compositeHandles = tiles.Where(tile => tile.Composite).Select(tile => tile.Window.Handle).ToArray();
        var nextGroup = page == 0 ? groups.Find(latest) : null;
        var nextHandles = nextGroup == null ? Array.Empty<nint>() : new[] { nextGroup.Left.Handle, nextGroup.Right.Handle };
        var changed = !compositeHandles.SequenceEqual(nextHandles) || !windows.Select(w => w.Handle).SequenceEqual(latest.Select(w => w.Handle));
        windows = latest;
        if (windows.Length == 0) { dismiss(); return; }
        if (changed) RenderPage();
    }
    private void Release() { foreach (var tile in tiles) tile.Thumbnail?.Dispose(); tiles.Clear(); }
}
