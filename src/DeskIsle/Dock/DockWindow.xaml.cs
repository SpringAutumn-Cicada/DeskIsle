using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
namespace DeskIsle.Dock;

public partial class DockBar : Window
{
    private readonly DockController controller;
    private readonly Dictionary<string, ImageSource?> icons = new(StringComparer.OrdinalIgnoreCase);
    private Point down;
    private string? dragPin;
    private bool dragging;
    private string signature = "";
    internal bool Interacting => dragging || menuOpen;
    private bool menuOpen;
    internal DockBar(DockController controller)
    {
        this.controller = controller;
        InitializeComponent();
        Surface.Opacity = controller.Settings.Opacity;
        SizeChanged += (_, _) => Place();
    }
    internal void Place()
    {
        var area = SystemParameters.WorkArea;
        // WPF converts the main monitor geometry into the window DPI coordinate space.
        var primary = DockNative.Primary();
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var a = scale.Transform(new Point(primary.Monitor.Left, primary.Monitor.Top));
        var b = scale.Transform(new Point(primary.Monitor.Right, primary.Monitor.Bottom));
        Scroller.MaxWidth = Math.Max(120, b.X - a.X - 180);
        Left = a.X + (b.X - a.X - ActualWidth) / 2;
        Top = b.Y - ActualHeight - 12;
    }
    internal void SetApps(DockPin[] pins, DockWindow[] windows)
    {
        var key = string.Join("|", pins.Select(x => x.Path)) + "#" + string.Join("|", windows.Select(x => $"{x.Handle}:{x.Title}:{x.Executable}"));
        if (key == signature || Interacting) return;
        signature = key;
        Apps.Children.Clear();
        foreach (var pin in pins)
            AddButton(pin.Name, pin.Path, pin, windows.Where(w => pin.Executable != null && string.Equals(w.Executable, pin.Executable, StringComparison.OrdinalIgnoreCase)).ToArray());
        var extra = windows.Where(w => !pins.Any(p => p.Executable != null && string.Equals(p.Executable, w.Executable, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(w => w.Executable ?? $"window:{w.Handle}", StringComparer.OrdinalIgnoreCase);
        if (extra.Any() && pins.Length > 0) Apps.Children.Add(new Border { Width = 1, Background = Brushes.SlateGray, Margin = new Thickness(7, 10, 7, 10) });
        foreach (var group in extra) AddButton(group.First().Title, group.First().Executable, null, group.ToArray());
        if (pins.Length == 0 && windows.Length == 0)
            Apps.Children.Add(new TextBlock { Text = "拖入应用快捷方式", Foreground = Brushes.LightSlateGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12) });
    }
    private async void AddButton(string title, string? iconPath, DockPin? pin, DockWindow[] windows)
    {
        var button = new Button { Width = 50, Height = 52, Padding = new Thickness(3), ToolTip = title, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        var panel = new Grid();
        var image = new Image { Width = 36, Height = 36, Margin = new Thickness(0, 0, 0, 5), Stretch = Stretch.Uniform };
        panel.Children.Add(new TextBlock { Text = "▧", FontSize = 28, Foreground = Brushes.LightSteelBlue, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(image);
        if (windows.Length > 0) panel.Children.Add(new Border { Width = 5, Height = 3, CornerRadius = new CornerRadius(2), Background = windows.Any(w => w.Handle == DockNative.GetForegroundWindow()) ? Brushes.Aquamarine : Brushes.LightSlateGray, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center });
        button.Content = panel;
        var transform = new ScaleTransform(1, 1);
        image.RenderTransform = transform; image.RenderTransformOrigin = new Point(.5, .5);
        button.MouseEnter += (_, _) => Animate(1.12);
        button.MouseLeave += (_, _) => Animate(1);
        void Animate(double value) { var animation = new DoubleAnimation(value, TimeSpan.FromMilliseconds(100)); transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation); transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation); }
        button.Click += (_, _) =>
        {
            if (windows.Length == 1) Activate(windows[0]);
            else if (windows.Length > 1) OpenWindows(button, windows);
            else if (pin != null) controller.Launch(pin.Path);
        };
        button.PreviewMouseLeftButtonDown += (_, e) => { down = e.GetPosition(this); dragPin = pin?.Path; };
        button.PreviewMouseMove += (_, e) =>
        {
            if (dragging || pin == null || dragPin != pin.Path || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(this);
            if (Math.Abs(point.X - down.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - down.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            dragging = true;
            try { var data = new DataObject("DeskIsle.Dock.Pin", pin.Path); DragDrop.DoDragDrop(button, data, DragDropEffects.Move); }
            finally { dragging = false; dragPin = null; }
        };
        button.AllowDrop = true;
        button.Drop += (_, e) =>
        {
            if (pin != null && e.Data.GetData("DeskIsle.Dock.Pin") is string source) { controller.Reorder(source, pin.Path); e.Handled = true; }
        };
        var menu = new ContextMenu();
        Add(menu, "打开新窗口", () => { if (pin != null) controller.Launch(pin.Path); else if (iconPath != null) controller.Launch(iconPath); });
        if (pin != null)
        {
            Add(menu, "取消固定", () => controller.Unpin(pin.Path));
            Add(menu, "重新定位…", () => controller.Relocate(pin.Path));
            Add(menu, "打开所在位置", () => controller.Reveal(pin.Path));
        }
        else if (iconPath != null) Add(menu, "固定到 Dock", () => controller.Pin([iconPath]));
        foreach (var window in windows)
        {
            var item = new MenuItem { Header = window.Title.Length > 60 ? window.Title[..60] + "…" : window.Title };
            Add(item, "切换到窗口", () => Activate(window));
            Add(item, "关闭窗口", () => DockNative.PostMessage(window.Handle, 0x0010, 0, 0));
            menu.Items.Add(item);
        }
        menu.Opened += (_, _) => menuOpen = true;
        menu.Closed += (_, _) => menuOpen = false;
        button.ContextMenu = menu;
        Apps.Children.Add(button);
        if (pin != null && !File.Exists(pin.Path)) { button.Opacity = .45; button.ToolTip = title + "（路径不可用，右键重新定位）"; }
        if (iconPath == null) return;
        if (!icons.TryGetValue(iconPath, out var icon))
        {
            try { icon = await Task.Run(() => DockApps.Icon(iconPath)); }
            catch (Exception ex) when (ex is IOException or ArgumentException or System.Runtime.InteropServices.COMException) { icon = null; }
            if (icons.Count >= 256) icons.Clear();
            icons[iconPath] = icon;
        }
        image.Source = icon;
    }
    internal static void Add(ItemsControl menu, string header, Action action)
    { var item = new MenuItem { Header = header }; item.Click += (_, _) => action(); menu.Items.Add(item); }
    private void OpenWindows(Button button, DockWindow[] windows)
    {
        var menu = new ContextMenu { PlacementTarget = button };
        foreach (var window in windows) Add(menu, window.Title, () => Activate(window));
        menu.Opened += (_, _) => menuOpen = true; menu.Closed += (_, _) => menuOpen = false;
        menu.IsOpen = true;
    }
    private static void Activate(DockWindow window)
    {
        if (DockNative.IsIconic(window.Handle)) DockNative.ShowWindowAsync(window.Handle, 9);
        DockNative.SetForegroundWindow(window.Handle);
    }
    private void NativeClick(object sender, RoutedEventArgs e) => controller.ToggleNative();
    private void SettingsClick(object sender, RoutedEventArgs e) => controller.Configure();
    private void DropPins(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) controller.Pin(paths);
        e.Handled = true;
    }
    private void DragOverPins(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("DeskIsle.Dock.Pin") ? DragDropEffects.Move : e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
}
