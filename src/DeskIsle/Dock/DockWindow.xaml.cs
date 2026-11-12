using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
namespace DeskIsle.Dock;

public partial class DockBar : Window
{
    private sealed class AppView
    {
        internal required Button Button;
        internal required Image Image;
        internal required TextBlock Placeholder;
        internal required Border Indicator;
        internal bool Alerting, StrongAlert;
        internal DockWindow[] Windows = [];
        internal DockPin? Pin;
        internal string? IconPath, IconKey;
        internal long IconVersion, IconRetryAt;
        internal nint RecentWindow;
        internal SplitPair? MinimizedPair;
    }
    private readonly DockController controller;
    private readonly DispatcherTimer clockTick = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, ImageSource?> icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AppView> views = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim iconSlots = new(2);
    private readonly SplitGroups splitGroups = new();
    private readonly List<string> runningOrder = [];
    private Point down;
    private string? dragPin;
    private nint pressedForeground;
    private bool dragging, menuOpen, closed;
    private HwndSource? source;
    private readonly DispatcherTimer previewDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private WindowPreview? preview;
    private AppView? hoveredView;
    internal bool Interacting => dragging || menuOpen || preview?.IsVisible == true || previewDelay.IsEnabled || systemPopup?.IsOpen == true;
    internal DockBar(DockController controller)
    {
        this.controller = controller;
        InitializeComponent();
        Surface.Opacity = controller.Settings.Opacity;
        InitializeSystemPanel();
        UpdateClock();
        clockTick.Tick += (_, _) => UpdateClock();
        IsVisibleChanged += (_, _) => { if (IsVisible) { UpdateClock(); clockTick.Start(); } else clockTick.Stop(); };
        previewDelay.Tick += (_, _) =>
        {
            previewDelay.Stop();
            if (hoveredView is { } view && view.Button.IsMouseOver && !dragging && !menuOpen) ShowPreview(view);
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) { ClosePreview(); CloseSystemPanel(); } };
        SizeChanged += (_, _) => Place();
        DpiChanged += (_, _) =>
        {
            icons.Clear();
            foreach (var view in views.Values) { view.IconKey = null; LoadIcon(view); }
            Place();
        };
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        var hwnd = new WindowInteropHelper(this).Handle;
        DockNative.SetWindowLong(hwnd, -20, DockNative.GetWindowLong(hwnd, -20) | 0x08000000 | 0x80);
        source?.AddHook(MouseActivation);
    }
    private nint MouseActivation(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // MA_NOACTIVATE: preserve the application's foreground state through a Dock click.
        if (message == 0x21) { pressedForeground = controller.ForegroundForClick(); handled = true; return 3; }
        return 0;
    }
    protected override void OnClosed(EventArgs e)
    { closed = true; CloseSystemPanel(); statusTick.Stop(); foreach (var view in views.Values) view.Indicator.BeginAnimation(OpacityProperty, null); clockTick.Stop(); ClosePreview(); source?.RemoveHook(MouseActivation); base.OnClosed(e); }
    internal void Place()
    {
        var primary = DockNative.Primary();
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var a = scale.Transform(new Point(primary.Monitor.Left, primary.Monitor.Top));
        var b = scale.Transform(new Point(primary.Monitor.Right, primary.Monitor.Bottom));
        Scroller.MaxWidth = Math.Max(0, b.X - a.X - 230);
        DockAssembly.MaxWidth = Math.Max(100, b.X - a.X - 16);
        Left = a.X + (b.X - a.X - ActualWidth) / 2;
        Top = b.Y - ActualHeight - 12;
    }
    private readonly Border appSeparator = new() { Width = 1, Background = Brushes.SlateGray, Margin = new Thickness(7, 10, 7, 10) };
    private DockPin[]? pendingPins;
    private DockWindow[]? pendingWindows;
    private void FlushApps()
    {
        if (pendingPins == null || pendingWindows == null || dragging || menuOpen) return;
        var pins = pendingPins; var windows = pendingWindows;
        pendingPins = null; pendingWindows = null;
        SetApps(pins, windows);
    }
    internal void SetApps(DockPin[] pins, DockWindow[] windows)
    {
        if (closed) return;
        if (dragging || menuOpen) { pendingPins = pins; pendingWindows = windows; return; }
        splitGroups.Observe(windows);
        windows = windows.OrderBy(w => w.Handle.ToInt64()).ToArray();
        var pinned = new List<UIElement>();
        var running = new List<UIElement>();
        var alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Use(string id, string title, string? iconPath, DockPin? pin, DockWindow[] group)
        {
            alive.Add(id);
            if (!views.TryGetValue(id, out var view)) { view = CreateView(); views.Add(id, view); }
            view.Windows = group; view.Pin = pin; view.IconPath = iconPath;
            var foreground = controller.ForegroundForClick();
            if (group.FirstOrDefault(w => w.Handle == foreground) is { } current) view.RecentWindow = current.Handle;
            ToolTipService.SetIsEnabled(view.Button, group.Length == 0);
            view.Button.ToolTip = pin != null && !File.Exists(pin.Path) ? title + "（路径不可用，右键重新定位）" : title;
            view.Button.Opacity = pin != null && !File.Exists(pin.Path) ? .45 : 1;
            view.Indicator.Visibility = group.Length > 0 ? Visibility.Visible : Visibility.Hidden;
            view.Indicator.Background = group.Any(w => w.Handle == DockNative.GetForegroundWindow()) ? Brushes.Aquamarine : Brushes.LightSlateGray;
            ApplyAttention(view);
            LoadIcon(view);
            (pin != null ? pinned : running).Add(view.Button);
        }
        foreach (var pin in pins)
            Use("pin:" + pin.Path, pin.Name, pin.Path, pin, windows.Where(w => pin.Executable != null && string.Equals(w.Executable, pin.Executable, StringComparison.OrdinalIgnoreCase)).ToArray());
        var extra = windows.Where(w => !pins.Any(p => p.Executable != null && string.Equals(p.Executable, w.Executable, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(w => w.Executable ?? $"window:{w.Handle}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var id in extra.Keys)
            if (!runningOrder.Contains(id, StringComparer.OrdinalIgnoreCase)) runningOrder.Add(id);
        if (runningOrder.Count > 256) runningOrder.RemoveAll(id => !extra.ContainsKey(id));
        foreach (var id in runningOrder.Where(extra.ContainsKey))
        { var group = extra[id]; Use("running:" + id, group[0].Title, group[0].Executable, null, group); }
        var desired = new List<UIElement>(pinned);
        if (pinned.Count > 0 && running.Count > 0) desired.Add(appSeparator);
        desired.AddRange(running);
        // Retain existing controls and hover state while restoring the single application row.
        static void Reconcile(StackPanel panel, List<UIElement> desired)
        {
            for (var i = panel.Children.Count - 1; i >= 0; i--)
                if (!desired.Contains(panel.Children[i])) panel.Children.RemoveAt(i);
            for (var i = 0; i < desired.Count; i++)
            {
                if (i < panel.Children.Count && ReferenceEquals(panel.Children[i], desired[i])) continue;
                if (panel.Children.Contains(desired[i])) panel.Children.Remove(desired[i]);
                panel.Children.Insert(i, desired[i]);
            }
        }
        Reconcile(Apps, desired);
        Place();
        preview?.SetWindows(hoveredView?.Windows ?? []);
        if (preview == null && hoveredView is { Windows.Length: > 0 } hovered
            && hovered.Button.IsMouseOver && !previewDelay.IsEnabled) previewDelay.Start();
        foreach (var id in views.Keys.Where(id => !alive.Contains(id)).ToArray())
        { views[id].IconVersion++; views[id].Indicator.BeginAnimation(OpacityProperty, null); views.Remove(id); }
    }
    private readonly HashSet<nint> attentionHandles = [];
    private readonly HashSet<nint> strongAttentionHandles = [];
    internal void SetAttention(IEnumerable<nint> handles, IEnumerable<nint> strong)
    {
        attentionHandles.Clear(); attentionHandles.UnionWith(handles);
        strongAttentionHandles.Clear(); strongAttentionHandles.UnionWith(strong);
        foreach (var view in views.Values) ApplyAttention(view);
    }
    private void ApplyAttention(AppView view)
    {
        var alerting = view.Windows.Any(w => attentionHandles.Contains(w.Handle));
        var strong = alerting && view.Windows.Any(w => strongAttentionHandles.Contains(w.Handle));
        if (view.Alerting != alerting || view.StrongAlert != strong)
        {
            view.Alerting = alerting; view.StrongAlert = strong;
            view.Indicator.BeginAnimation(OpacityProperty, null);
            view.Indicator.Effect = alerting ? new System.Windows.Media.Effects.DropShadowEffect
            { Color = Color.FromRgb(255, 210, 138), BlurRadius = 7, ShadowDepth = 0, Opacity = .55 } : null;
            view.Indicator.Opacity = 1;
            if (strong)
                view.Indicator.BeginAnimation(OpacityProperty, new DoubleAnimation(.35, 1, TimeSpan.FromMilliseconds(800))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
        view.Indicator.Width = alerting ? 4 : 5;
        view.Indicator.Height = alerting ? 4 : 3;
        view.Indicator.Background = alerting ? new SolidColorBrush(Color.FromRgb(255, 210, 138)) :
            view.Windows.Any(w => w.Handle == DockNative.GetForegroundWindow()) ? Brushes.Aquamarine : Brushes.LightSlateGray;
    }
    private AppView CreateView()
    {
        var button = new Button { Width = 50, Height = 52, Padding = new Thickness(3), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        var image = new Image { Width = 36, Height = 36, Margin = new Thickness(0, 0, 0, 5), Stretch = Stretch.Uniform };
        var placeholder = new TextBlock { Text = "▧", FontSize = 28, Foreground = Brushes.LightSteelBlue, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var indicator = new Border { Width = 5, Height = 3, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center };
        var view = new AppView { Button = button, Image = image, Placeholder = placeholder, Indicator = indicator };
        var panel = new Grid(); panel.Children.Add(placeholder); panel.Children.Add(image); panel.Children.Add(indicator); button.Content = panel;
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var transform = new ScaleTransform(1, 1);
        image.RenderTransform = transform; image.RenderTransformOrigin = new Point(.5, .5);
        var hovered = false;
        button.MouseEnter += (_, _) =>
        {
            if (!hovered) { hovered = true; Animate(1.12); }
            previewDelay.Stop();
            if (!ReferenceEquals(hoveredView, view)) ClosePreview();
            hoveredView = view;
            if (view.Windows.Length > 0 && preview?.IsVisible != true) previewDelay.Start();
        };
        button.MouseLeave += (_, _) =>
        {
            if (hovered) { hovered = false; Animate(1); }
            previewDelay.Stop();
        };
        void Animate(double value)
        {
            var animation = new DoubleAnimation(value, TimeSpan.FromMilliseconds(100));
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }
        button.Focusable = false;
        button.Click += (_, _) =>
        {
            ClosePreview();
            var foreground = pressedForeground != 0 ? pressedForeground : controller.ForegroundForClick();
            var root = DockNative.GetAncestor(foreground, 3);
            var active = view.Windows.FirstOrDefault(w => (w.Handle == foreground || w.Handle == root || DockNative.GetAncestor(w.Handle, 3) == root) && DockNative.IsWindow(w.Handle) && !DockNative.IsIconic(w.Handle));
            pressedForeground = 0;
            if (active != null)
            {
                splitGroups.Observe(view.Windows);
                view.MinimizedPair = splitGroups.Find(view.Windows);
                // Snapshot the whole app group before minimizing its foreground window.
                foreach (var handle in view.Windows.Select(w => w.Handle).Distinct().Where(handle => DockNative.IsWindow(handle) && !DockNative.IsIconic(handle)).ToArray())
                    DockNative.PostMessage(handle, 0x0112, 0xF020, 0);
            }
            else if (view.MinimizedPair is { } pair &&
                DockNative.IsWindow(pair.Left.Handle) && DockNative.IsWindow(pair.Right.Handle) &&
                DockNative.IsIconic(pair.Left.Handle) && DockNative.IsIconic(pair.Right.Handle))
            {
                view.MinimizedPair = null;
                SplitGroups.Restore(pair);
            }
            else if (view.Windows.Length == 1) Activate(view.Windows[0]);
            else if (view.Windows.Length > 1)
            {
                var recent = view.Windows.FirstOrDefault(w => w.Handle == view.RecentWindow) ?? view.Windows.FirstOrDefault(w => !DockNative.IsIconic(w.Handle)) ?? view.Windows[0];
                view.RecentWindow = recent.Handle; Activate(recent);
            }
            else if (view.Pin != null) controller.OpenPinned(view.Pin);
        };
        button.PreviewMouseLeftButtonDown += (_, e) => { pressedForeground = controller.ForegroundForClick(); down = e.GetPosition(this); dragPin = view.Pin?.Path; };
        button.PreviewMouseMove += (_, e) =>
        {
            var pin = view.Pin;
            if (dragging || pin == null || dragPin != pin.Path || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(this);
            if (Math.Abs(point.X - down.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - down.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            ClosePreview();
            dragging = true;
            try { DragDrop.DoDragDrop(button, new DataObject("DeskIsle.Dock.Pin", pin.Path), DragDropEffects.Move); }
            finally { dragging = false; dragPin = null; FlushApps(); }
        };
        button.AllowDrop = true;
        button.DragEnter += (_, e) =>
        {
            if (view.Pin != null && e.Data.GetDataPresent("DeskIsle.Dock.Pin"))
            { button.BorderThickness = new Thickness(2, 0, 0, 0); button.BorderBrush = Brushes.Aquamarine; }
        };
        button.DragLeave += (_, _) => button.BorderThickness = new Thickness(0);
        button.Drop += (_, e) =>
        {
            button.BorderThickness = new Thickness(0);
            if (view.Pin != null && e.Data.GetData("DeskIsle.Dock.Pin") is string origin) { controller.Reorder(origin, view.Pin.Path); e.Handled = true; }
        };
        var menu = new ContextMenu();
        button.ContextMenu = menu;
        button.ContextMenuOpening += (_, _) => { ClosePreview(); FillMenu(menu, view); };
        menu.Opened += (_, _) => menuOpen = true;
        menu.Closed += (_, _) => { menuOpen = false; FlushApps(); };
        // A nonempty menu is needed for WPF to raise ContextMenuOpening on first use.
        Add(menu, "打开", () => { });
        return view;
    }
    private void FillMenu(ContextMenu menu, AppView view)
    {
        menu.Items.Clear();
        Add(menu, "打开新窗口", () => { if (view.Pin != null) controller.Launch(view.Pin.Path); else if (view.IconPath != null) controller.Launch(view.IconPath); });
        if (view.Pin is { } pin)
        {
            Add(menu, "取消固定", () => controller.Unpin(pin.Path));
            Add(menu, "重新定位…", () => controller.Relocate(pin.Path));
            Add(menu, "打开所在位置", () => controller.Reveal(pin.Path));
        }
        else if (view.IconPath is { } path) Add(menu, "固定到 Dock", () => controller.Pin([path]));
        if (view.Windows.Length > 0)
            Add(menu, "关闭全部窗口", () =>
            {
                foreach (var window in view.Windows.DistinctBy(window => window.Handle))
                    if (DockNative.IsWindow(window.Handle)) DockNative.PostMessage(window.Handle, 0x0010, 0, 0);
            });
    }
    private async void LoadIcon(AppView view)
    {
        if (closed) return;
        var windows = view.Windows;
        var path = view.IconPath;
        if (path == null && windows.Length == 0) return;
        var pixels = (int)Math.Clamp(Math.Ceiling(36 * VisualTreeHelper.GetDpi(this).DpiScaleX * 1.12), 96, 256);
        var key = (path ?? $"window:{windows[0].Handle}") + "|" + pixels;
        if (view.IconKey == key || Environment.TickCount64 < view.IconRetryAt) return;
        view.IconKey = key;
        var version = ++view.IconVersion;
        if (!icons.TryGetValue(key, out var icon))
        {
            try
            {
                await iconSlots.WaitAsync();
                try
                {
                    if (closed || version != view.IconVersion) return;
                    icon = await Task.Run(() => path != null ? DockApps.Icon(path, pixels) ?? (windows.Length > 0 ? DockApps.WindowIcon(windows[0].Handle) : null) : DockApps.WindowIcon(windows[0].Handle));
                }
                finally { iconSlots.Release(); }
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or System.Runtime.InteropServices.COMException) { icon = null; }
            if (icons.Count >= 256) icons.Clear();
            if (icon != null) icons[key] = icon;
        }
        if (closed || version != view.IconVersion) return;
        if (icon == null)
        {
            view.IconKey = null; view.IconRetryAt = Environment.TickCount64 + 3000;
            if (view.Image.Source != null) return; // Keep the last valid image during a transient Shell failure.
        }
        else view.IconRetryAt = 0;
        view.Image.Source = icon;
        view.Placeholder.Visibility = icon != null ? Visibility.Hidden : Visibility.Visible;
    }
    internal static void Add(ItemsControl menu, string header, Action action)
    { var item = new MenuItem { Header = header }; item.Click += (_, _) => action(); menu.Items.Add(item); }
    private void ShowPreview(AppView view)
    {
        var live = view.Windows.Where(w => DockNative.IsWindow(w.Handle)).ToArray();
        if (closed || !IsVisible || live.Length == 0) return;
        CloseSystemPanel();
        ClosePreview();
        hoveredView = view;
        preview = new WindowPreview(this, view.Button, live, view.Image.Source,
            window => { view.RecentWindow = window.Handle; ClosePreview(); Activate(window); }, ClosePreview, splitGroups);
        preview.Show();
    }
    private void ClosePreview()
    {
        previewDelay.Stop();
        var previous = preview; preview = null; previous?.Close();
    }
    private static void Activate(DockWindow window)
    {
        if (DockNative.IsIconic(window.Handle)) DockNative.ShowWindow(window.Handle, 9);
        DockNative.SetForegroundWindow(window.Handle);
    }
    private void ShowDesktopClick(object sender, RoutedEventArgs e)
    {
        ClosePreview();
        if (!DockNative.ShowDesktop()) MessageBox.Show("系统未接受显示桌面快捷键，请按 Win+D。", "DeskIsle");
    }
    private void UpdateClock()
    {
        var text = DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        if (ClockText.Text != text) ClockText.Text = text;
    }
    private void NativeClick(object sender, RoutedEventArgs e) => controller.ToggleNative();
    private void TaskManagerClick(object sender, RoutedEventArgs e)
    {
        ClosePreview();
        controller.Launch(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Taskmgr.exe"));
    }
    private void SettingsClick(object sender, RoutedEventArgs e) => controller.Configure();
    private void DropPins(object sender, DragEventArgs e)
    { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) controller.Pin(paths); e.Handled = true; }
    private void DragOverPins(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("DeskIsle.Dock.Pin") ? DragDropEffects.Move : e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
}
