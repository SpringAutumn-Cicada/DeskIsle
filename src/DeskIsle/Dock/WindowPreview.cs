using System.Runtime.InteropServices;
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
    private sealed record CloseRequest(long ExpiresAt, int? Error = null);
    private readonly Dictionary<nint, CloseRequest> closeRequests = [];
    private readonly WindowPeek peek = new();
    private Card? hoveredCard, peekCandidate, blockedPeek;
    private long peekSince;
    private bool peekAttempted;
    private readonly DispatcherTimer check = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private bool windowCheckQueued;
    private int page;
    private bool finished, preparing, ready;
    private long outsideSince;
    private HwndSource? source;
    internal WindowPreview(DockBar bar, Button anchor, DockWindow[] windows, ImageSource? icon, Action<DockWindow> activate, Action dismiss, SplitGroups groups)
    {
        this.bar = bar; this.anchor = anchor; this.windows = windows; this.icon = icon; this.activate = activate; this.dismiss = dismiss; this.groups = groups;
        Title = "窗口预览"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; Owner = bar;
        // Non-layered destination so DWM can compose its thumbnails directly into the client area.
        Background = Brush("#202936"); Foreground = Brush("#EAF0F8");
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual; UseLayoutRounding = true; SnapsToDevicePixels = true;
        Content = new Border { Padding = new Thickness(3), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), BorderBrush = Brush("#52647C"), Background = Brush("#202936"), Child = content };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            DockNative.SetWindowLong(hwnd, -20, DockNative.GetWindowLong(hwnd, -20) | 0x08000000 | 0x80);
            source = HwndSource.FromHwnd(hwnd); source?.AddHook(NoActivate);
            var corner = 2;
            DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int));
            var disableTransitions = 1;
            DwmSetWindowAttribute(hwnd, 3, ref disableTransitions, sizeof(int));
        };
        Loaded += (_, _) =>
        {
            PrepareLayout();
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                if (finished) return;
                ready = true; UpdateThumbnails(); check.Start();
            }));
        };
        LayoutUpdated += (_, _) => { if (ready && !preparing) UpdateThumbnails(); };
        SizeChanged += (_, _) => { if (!preparing) Place(); };
        check.Tick += (_, _) =>
        {
            // Unowned topmost DWM hosts must not survive Show Desktop or a
            // foreground switch. Check before updating/raising any thumbnails.
            if (peek.CheckForeground() || DesktopShortcutDown()) { dismiss(); return; }
            if (ContainsInteractionPointer()) outsideSince = 0;
            else if (outsideSince == 0) outsideSince = Environment.TickCount64;
            else if (Environment.TickCount64 - outsideSince >= 350) { dismiss(); return; }
            if (!RemoveUnavailableWindows()) UpdateThumbnails();
            ExpireCloseRequests();
            UpdatePeek();
        };
        Closed += (_, _) => { finished = true; check.Stop(); Release(); source?.RemoveHook(NoActivate); };
        new WindowInteropHelper(this).EnsureHandle();
        RenderPage();
    }
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
    private nint NoActivate(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    { if (message == 0x21) { handled = true; return 3; } return 0; }
    private const int PerPage = 7;
    private sealed class Card
    {
        internal required Border Surface;
        internal required StackPanel Images;
        internal required TextBlock Title;
        internal required Button Close;
        internal required DockWindow[] Windows;
        internal bool Closing;
        internal bool? Hover;
    }
    private readonly List<Card> cards = [];
    private Card MakeCard(DockWindow[] members, bool composite, Action click)
    {
        var images = new StackPanel { Orientation = Orientation.Horizontal };
        var title = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0), Foreground = Brush("#DDE5EF") };
        var close = new Button { Content = "×", FontSize = 18, Width = 22, Height = 22,
            Focusable = false, Visibility = Visibility.Hidden };
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        borderFactory.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.AppendChild(presenter);
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Button)) { VisualTree = borderFactory }));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#DDE5EF")));
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#B4404B"))); style.Triggers.Add(hover);
        close.Style = style;
        close.Click += (_, e) =>
        {
            e.Handled = true;
            RequestClose(members);
        };
        var icons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < (composite ? 2 : 1); i++)
            icons.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(1, 0, 1, 0) });
        var header = new Grid { Height = 28 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        Grid.SetColumn(title, 1); Grid.SetColumn(close, 2);
        header.Children.Add(icons); header.Children.Add(title); header.Children.Add(close);
        header.MouseLeftButtonUp += (_, e) => { e.Handled = true; SelectPeek(members, click); };
        var layout = new StackPanel(); layout.Children.Add(header); layout.Children.Add(images);
        var surface = new Border { Child = layout, Padding = new Thickness(3.5), BorderThickness = new Thickness(1.5),
            BorderBrush = Brush("#344252"), CornerRadius = new CornerRadius(8), Background = Brush("#202936"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3) };
        var card = new Card { Surface = surface, Images = images, Title = title, Close = close, Windows = members };
        cards.Add(card);
        return card;
    }
    private void RefreshTitles()
    {
        foreach (var card in cards)
        {
            var title = windows.FirstOrDefault(w => w.Handle == card.Windows[0].Handle)?.Title ?? card.Windows[0].Title;
            var name = (card.Windows.Length > 1 ? "组 | " : "") + title;
            var requests = card.Windows.Select(window => closeRequests.GetValueOrDefault(window.Handle)).ToArray();
            card.Closing = requests.Any(request => request is { Error: null });
            var failed = requests.FirstOrDefault(request => request?.Error != null);
            var feedback = failed != null ? "关闭请求发送失败" : card.Closing ? "正在关闭…" : "";
            var help = failed?.Error == 5 ? "Windows 拒绝关闭请求，可在应用窗口内关闭。" : feedback;
            card.Title.Text = feedback.Length == 0 ? name : $"{feedback}  {name}";
            card.Title.ToolTip = help.Length == 0 ? name : $"{help}\n{name}";
            card.Close.Content = card.Closing ? "…" : "×";
            card.Close.ToolTip = help.Length != 0 ? help : card.Windows.Length > 1 ? "关闭组合窗口" : "关闭窗口";
        }
    }

    private void RequestClose(DockWindow[] members)
    {
        if (finished || members.Any(member => closeRequests.GetValueOrDefault(member.Handle) is { Error: null })) return;
        CancelPeek();
        var now = Environment.TickCount64;
        foreach (var member in members.DistinctBy(window => window.Handle))
        {
            if (!IsVisibleWindow(member)) continue;
            if (DockNative.PostMessage(member.Handle, 0x0010, 0, 0))
                closeRequests[member.Handle] = new(now + 2000);
            else
                closeRequests[member.Handle] = new(now + 4000, Marshal.GetLastPInvokeError());
        }
        if (!RemoveUnavailableWindows()) RefreshTitles();
    }

    private void ExpireCloseRequests()
    {
        if (finished || closeRequests.Count == 0) return;
        // A delivered WM_CLOSE can be cancelled by the target. Expiry only clears feedback.
        var now = Environment.TickCount64;
        var expired = closeRequests.Where(item => item.Value.ExpiresAt <= now).Select(item => item.Key).ToArray();
        if (expired.Length == 0) return;
        foreach (var handle in expired) closeRequests.Remove(handle);
        RefreshTitles();
    }
    private void RenderPage()
    {
        if (finished) return;
        CancelPeek();
        var previousTiles = tiles.ToDictionary(tile => (tile.Window.Handle, tile.Composite));
        tiles.Clear(); cards.Clear(); content.Children.Clear();
        var perPage = PerPage;
        var pair = groups.Find(windows);
        var groupSlots = pair is null ? 0 : 1;
        var pages = Math.Max(1, (windows.Length + groupSlots + perPage - 1) / perPage);
        page = Math.Clamp(page, 0, pages - 1);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new());
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetColumn(row, 1); layout.Children.Add(row); content.Children.Add(layout);
        void AddImage(DockWindow window, Card card, double width, double height, Action click, bool composite)
        {
            var host = new Grid { Width = width, Height = height, Background = Brush("#202936"), VerticalAlignment = VerticalAlignment.Center };
            host.Children.Add(new Image { Source = icon, Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
            Action select = () => SelectPeek(card.Windows, click);
            host.MouseLeftButtonUp += (_, e) => { e.Handled = true; select(); };
            if (!previousTiles.Remove((window.Handle, composite), out var tile))
                tile = new Tile { Window = window, Host = host, Click = select, Group = card, Composite = composite };
            tile.Window = window; tile.Host = host; tile.Click = select; tile.Group = card;
            EnsureThumbnail(tile);
            if (!composite && tile.Thumbnail is { AspectRatio: > 0 } thumbnail)
            {
                var ratio = thumbnail.AspectRatio;
                host.Width = Math.Clamp(160 * ratio, 64, 320);
                host.Height = Math.Min(160, host.Width / ratio);
            }
            card.Images.Children.Add(host); tiles.Add(tile);
        }
        if (page == 0 && pair is not null)
        {
            var unionHeight = Math.Max(pair.LeftRect.Bottom, pair.RightRect.Bottom) - Math.Min(pair.LeftRect.Top, pair.RightRect.Top);
            var unionWidth = pair.RightRect.Right - pair.LeftRect.Left;
            var width = Math.Min(400, 160.0 * unionWidth / Math.Max(1, unionHeight));
            var height = width * unionHeight / Math.Max(1, unionWidth);
            Action restore = () => { SplitGroups.Restore(pair); dismiss(); };
            var card = MakeCard([pair.Left, pair.Right], true, restore);
            AddImage(pair.Left, card, width * (pair.LeftRect.Right - pair.LeftRect.Left) / Math.Max(1, unionWidth), height, restore, true);
            AddImage(pair.Right, card, width * (pair.RightRect.Right - pair.RightRect.Left) / Math.Max(1, unionWidth), height, restore, true);
            row.Children.Add(card.Surface);
        }
        foreach (var window in windows.Skip(Math.Max(0, page * perPage - groupSlots)).Take(perPage - (page == 0 ? groupSlots : 0)))
        {
            Action click = () => { activate(window); dismiss(); };
            var card = MakeCard([window], false, click);
            var ratio = 16.0 / 9;
            if (DockNative.GetWindowRect(window.Handle, out var bounds) && !DockNative.IsIconic(window.Handle) && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top)
                ratio = (double)(bounds.Right - bounds.Left) / (bounds.Bottom - bounds.Top);
            var width = Math.Clamp(160 * ratio, 64, 320);
            AddImage(window, card, width, Math.Min(160, width / ratio), click, false);
            row.Children.Add(card.Surface);
        }
        foreach (var obsolete in previousTiles.Values) obsolete.Thumbnail?.Dispose();
        var chrome = cards.Count * 16 + 12 + (pages > 1 ? 72 : 0);
        var rowWidth = tiles.Sum(tile => tile.Host.Width);
        var rowHeight = tiles.Count == 0 ? 160 : tiles.Max(tile => tile.Host.Height);
        var fit = Math.Clamp(Math.Min((SystemParameters.WorkArea.Width - 24 - chrome) / Math.Max(1, rowWidth),
            (SystemParameters.WorkArea.Height * .35 - 38) / Math.Max(1, rowHeight)), .1, 1);
        foreach (var tile in tiles) { tile.Host.Width *= fit; tile.Host.Height *= fit; }
        foreach (var card in cards)
            card.Surface.Width = tiles.Where(tile => ReferenceEquals(tile.Group, card)).Sum(tile => tile.Host.Width) + 10;
        RefreshTitles();
        if (pages > 1)
        {
            Button Pager(string glyph, bool enabled, string hint, Action action)
            {
                var button = new Button { Content = glyph, ToolTip = hint, IsEnabled = enabled, Focusable = false,
                    Width = 30, Height = 38, FontSize = 24, Margin = new Thickness(3),
                    VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("#CAE7F5"), Background = Brush("#293A49") };
                var frame = new FrameworkElementFactory(typeof(Border));
                frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(15));
                frame.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
                var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
                presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
                presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center); frame.AppendChild(presenter);
                button.Template = new ControlTemplate(typeof(Button)) { VisualTree = frame };
                button.Opacity = enabled ? 1 : .28;
                button.MouseEnter += (_, _) => { if (button.IsEnabled) button.Background = Brush("#3B586D"); };
                button.MouseLeave += (_, _) => button.Background = Brush("#293A49");
                button.Click += (_, _) => action(); return button;
            }
            var previous = Pager("‹", page > 0, "上一页", () => { page--; RenderPage(); });
            var next = Pager("›", page < pages - 1, "下一页", () => { page++; RenderPage(); });
            layout.Children.Add(previous); Grid.SetColumn(next, 2); layout.Children.Add(next);
        }
        PrepareLayout();
    }
    private void PrepareLayout()
    {
        if (finished || preparing) return;
        preparing = true;
        try
        {
            // Measure the content independently of the previous window's fixed dimensions.
            var root = (FrameworkElement)Content;
            root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Width = Math.Ceiling(root.DesiredSize.Width); Height = Math.Ceiling(root.DesiredSize.Height);
            Measure(new Size(Width, Height));
            Arrange(new Rect(0, 0, Width, Height));
            UpdateLayout(); Place();
            UpdateThumbnails(false);
        }
        finally { preparing = false; }
        if (ready) UpdateThumbnails();
    }
    private void UpdateThumbnails(bool present = true)
    {
        if (finished || (present && (!IsLoaded || !IsVisible))) return;
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        object? hoverGroup = null;
        // Before Show(), descendants have no PresentationSource even when the HWND exists.
        if (present && IsLoaded && DockNative.GetCursorPos(out var mouse))
        {
            foreach (var card in cards)
            {
                if (PresentationSource.FromVisual(card.Surface) == null) continue;
                var a = card.Surface.PointToScreen(new Point());
                var b = card.Surface.PointToScreen(new Point(card.Surface.ActualWidth, card.Surface.ActualHeight));
                if (mouse.X >= a.X && mouse.X < b.X && mouse.Y >= a.Y && mouse.Y < b.Y) { hoverGroup = card; break; }
            }
            foreach (var tile in tiles)
            {
                if (PresentationSource.FromVisual(tile.Host) == null) continue;
                var start = tile.Host.PointToScreen(new Point());
                var end = tile.Host.PointToScreen(new Point(tile.Host.ActualWidth, tile.Host.ActualHeight));
                if (tile.Thumbnail?.ContainsPointer() == true || (mouse.X >= start.X && mouse.X < end.X && mouse.Y >= start.Y && mouse.Y < end.Y)) { hoverGroup = tile.Group; break; }
            }
        }
        if (present) hoveredCard = hoverGroup as Card;
        foreach (var card in cards)
        {
            var hovered = ReferenceEquals(hoverGroup, card);
            if (card.Hover == hovered) continue;
            card.Hover = hovered;
            card.Surface.Background = Brush(hovered ? "#303E50" : "#202936");
            card.Surface.BorderBrush = Brush(hovered ? "#829AB5" : "#344252");
            card.Title.Foreground = Brush(hovered ? "#FFFFFF" : "#DDE5EF");
            card.Close.Visibility = hovered ? Visibility.Visible : Visibility.Hidden;
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
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    private static bool DesktopShortcutDown() =>
        ((GetAsyncKeyState(0x5B) | GetAsyncKeyState(0x5C)) & 0x8000) != 0 &&
        (GetAsyncKeyState(0x44) & 0x8000) != 0;
    private void UpdatePeek()
    {
        if (finished || !ready) return;

        // Keep preview controls above the full-size DWM view layers.
        if (peek.Active) RaisePreview();
        var card = hoveredCard;
        // Closing a window is an action, not a request to temporarily restore it.
        if (card is { Closing: true } || card?.Close.IsMouseOver == true) card = null;
        if (!ReferenceEquals(card, peekCandidate))
        {
            peek.Restore(); peekCandidate = card; blockedPeek = null;
            peekSince = Environment.TickCount64; peekAttempted = false;
        }
        if (card == null || ReferenceEquals(card, blockedPeek) || peekAttempted || Environment.TickCount64 - peekSince < 300) return;
        peekAttempted = true;
        if (source != null && peek.Begin(card.Windows, groups, () =>
        {
            if (finished || !ReferenceEquals(card, peekCandidate) || !peek.Active) return;
            SelectPeek(card.Windows, () =>
            {
                var pair = groups.Find(card.Windows);
                if (pair != null) SplitGroups.Restore(pair);
                else activate(card.Windows[0]);
                dismiss();
            });
        })) RaisePreview();
    }
    private void RaisePreview()
    {
        DockNative.SetWindowPos(new WindowInteropHelper(bar).Handle, -1, 0, 0, 0, 0, 0x1 | 0x2 | 0x10);
        if (source != null) DockNative.SetWindowPos(source.Handle, -1, 0, 0, 0, 0, 0x1 | 0x2 | 0x10);
        foreach (var tile in tiles) tile.Thumbnail?.Raise();
    }
    private void SelectPeek(DockWindow[] selected, Action click)
    {
        peek.Commit(selected);
        peekCandidate = blockedPeek = null; peekAttempted = false;
        click();
    }
    private void CancelPeek()
    {
        peek.Restore(); hoveredCard = peekCandidate = blockedPeek = null;
        peekAttempted = false;
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
            () => tile.Click());
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
        Left = Math.Clamp(center.X - Width / 2, area.Left + 4, Math.Max(area.Left + 4, area.Right - Width - 4));
        Top = Math.Max(area.Top + 4, bar.Top - Height - 6);
    }
    private static bool IsVisibleWindow(DockWindow window) =>
        DockNative.IsWindow(window.Handle) && DockNative.IsWindowVisible(window.Handle);

    internal void QueueWindowCheck(nint window)
    {
        if (finished || windowCheckQueued || !Array.Exists(windows, item => item.Handle == window)) return;
        windowCheckQueued = true;
        _ = Dispatcher.InvokeAsync(() =>
        {
            windowCheckQueued = false;
            if (!finished) RemoveUnavailableWindows();
        }, DispatcherPriority.Render);
    }

    private bool RemoveUnavailableWindows()
    {
        var live = Array.FindAll(windows, IsVisibleWindow);
        if (live.Length == windows.Length) return false;
        foreach (var window in windows.Except(live)) closeRequests.Remove(window.Handle);
        windows = live;
        if (windows.Length == 0) dismiss();
        else RenderPage();
        return true;
    }

    internal void SetWindows(DockWindow[] latest)
    {
        if (finished) return;
        // An enumeration snapshot can omit a window during minimize/activation transitions.
        // Retain only visible HWNDs (including minimized ones), never tray-hidden windows.
        if (ContainsInteractionPointer())
            latest = latest.Concat(windows).Where(IsVisibleWindow)
                .DistinctBy(w => w.Handle).OrderBy(w => w.Handle.ToInt64()).ToArray();
        else latest = Array.FindAll(latest, IsVisibleWindow);
        var compositeHandles = tiles.Where(tile => tile.Composite).Select(tile => tile.Window.Handle).ToArray();
        var nextGroup = page == 0 ? groups.Find(latest) : null;
        var nextHandles = nextGroup == null ? Array.Empty<nint>() : new[] { nextGroup.Left.Handle, nextGroup.Right.Handle };
        var changed = !compositeHandles.SequenceEqual(nextHandles) || !windows.Select(w => w.Handle).SequenceEqual(latest.Select(w => w.Handle));
        windows = latest;
        if (windows.Length == 0) { dismiss(); return; }
        if (changed) RenderPage();
        else RefreshTitles();
    }
    private void Release() { CancelPeek(); peek.Dispose(); foreach (var tile in tiles) tile.Thumbnail?.Dispose(); tiles.Clear(); cards.Clear(); }
}
