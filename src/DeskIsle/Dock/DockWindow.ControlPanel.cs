using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Windows.Devices.Radios;
namespace DeskIsle.Dock;

public partial class DockBar
{
    private StackPanel? controlContent;
    private TextBlock? controlMessage, panelVolumeText;
    private Slider? panelVolume;
    private Button? panelMute;
    private bool volumeSync, wirelessBusy;
    private long panelVersion;
    private readonly Dictionary<RadioKind, (Button Toggle, TextBlock Status)> radioViews = [];
    private static SolidColorBrush ControlBrush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private Button ControlButton(object content, Action action, string? accessibleName = null)
    {
        var button = new Button { Content = content, Style = (Style)FindResource("ControlKey"), Width = double.NaN, Height = double.NaN, MinHeight = 38, Padding = new Thickness(10), Margin = new Thickness(2), HorizontalContentAlignment = HorizontalAlignment.Stretch, Foreground = ControlBrush("#C2D9E8") };
        // Keep the Dock's hover language, with a stretching content presenter for control cards.
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Button.Background)) { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 8, 8, 8));
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter); template.VisualTree = border;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(OpacityProperty, .8)); template.Triggers.Add(hover);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, .45)); template.Triggers.Add(disabled);
        button.Template = template;
        button.Background = ControlBrush("#2B3543");
        System.Windows.Automation.AutomationProperties.SetName(button, accessibleName ?? content.ToString() ?? "控制");
        button.Click += (_, _) => action();
        return button;
    }
    private static TextBlock ControlText(string text, double size = 12) => new() { Text = text, FontSize = size, Foreground = ControlBrush("#C2D9E8"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private void OpenControlPanel()
    {
        CloseSystemPanel(); ClosePreview();
        var content = new StackPanel { Width = 340 };
        controlContent = content;
        var shell = new Border { Child = content, Background = ControlBrush("#F5202632"), BorderBrush = ControlBrush("#647A8C"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = new Thickness(12), SnapsToDevicePixels = true, UseLayoutRounding = true };
        systemPopup = new Popup { Child = shell, PlacementTarget = StatusKey, Placement = PlacementMode.Custom, AllowsTransparency = true, StaysOpen = false };
        systemPopup.CustomPopupPlacementCallback = (size, target, offset) => [new CustomPopupPlacement(new Point(target.Width - size.Width, -size.Height - 10), PopupPrimaryAxis.Horizontal)];
        systemPopup.Closed += (_, _) => ResetControlPanel();
        ShowControlHome();
        systemPopup.IsOpen = true;
    }
    private void ResetControlPanel()
    {
        ++panelVersion; radioViews.Clear(); controlContent = null; controlMessage = null;
        panelVolume = null; panelVolumeText = null; panelMute = null;
    }
    private void SetPanelMessage(string text)
    {
        if (controlMessage == null) return;
        controlMessage.Text = text;
        controlMessage.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
    private void NewPanelPage()
    {
        ++panelVersion; radioViews.Clear(); panelVolume = null; panelVolumeText = null; panelMute = null;
        controlContent?.Children.Clear();
        controlMessage = ControlText(""); controlMessage.Margin = new Thickness(6); controlMessage.Visibility = Visibility.Collapsed;
    }
    private void ShowControlHome()
    {
        if (controlContent == null) return;
        NewPanelPage();
        var top = new Grid(); top.ColumnDefinitions.Add(new()); top.ColumnDefinitions.Add(new());
        AddRadio(top, 0, RadioKind.WiFi, "Wi-Fi", "M1,7 Q12,-2 23,7 M5,12 Q12,5 19,12 M9,17 Q12,14 15,17 M12,21 L12,22");
        AddRadio(top, 1, RadioKind.Bluetooth, "蓝牙", "M8,1 L18,8 L3,19 M8,23 L18,16 L3,5 M8,1 L8,23");
        controlContent.Children.Add(top);
        var volumeRow = new Grid { Margin = new Thickness(4, 12, 4, 12) };
        volumeRow.ColumnDefinitions.Add(new() { Width = new GridLength(44) }); volumeRow.ColumnDefinitions.Add(new()); volumeRow.ColumnDefinitions.Add(new() { Width = new GridLength(42) });
        panelMute = ControlButton("◖))", () => { DockAudio.Access(v => { v.GetMute(out var mute); v.SetMute(!mute, 0); }); ReadAudio(); }, "静音或取消静音");
        panelMute.FontSize = 16; volumeRow.Children.Add(panelMute);
        panelVolume = new Slider { Minimum = 0, Maximum = 100, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), Foreground = ControlBrush("#42BFE8") };
        panelVolume.ValueChanged += (_, _) =>
        {
            if (volumeSync || panelVolume == null) return;
            var value = panelVolume.Value;
            if (!DockAudio.Access(v => v.SetMasterVolumeLevelScalar((float)(value / 100), 0))) SetPanelMessage("音频设备暂不可用");
            ReadAudio();
        };
        Grid.SetColumn(panelVolume, 1); volumeRow.Children.Add(panelVolume);
        panelVolumeText = ControlText("—"); Grid.SetColumn(panelVolumeText, 2); volumeRow.Children.Add(panelVolumeText);
        controlContent.Children.Add(volumeRow);
        var tools = new UniformGrid { Columns = 3 };
        tools.Children.Add(ControlButton(ToolContent("M2,3 L22,3 L22,21 L2,21 Z M2,9 L22,9", "原生任务栏"), () => { CloseSystemPanel(); controller.ToggleNative(); }));
        tools.Children.Add(ControlButton(ToolContent("M2,3 L22,3 L22,21 L2,21 Z M5,14 L8,14 L10,8 L13,17 L16,11 L19,11", "任务管理器"), () => { CloseSystemPanel(); TaskManagerClick(this, new RoutedEventArgs()); }));
        tools.Children.Add(ControlButton(ToolContent("M4,2 L4,22 M12,2 L12,22 M20,2 L20,22 M1,7 L7,7 M9,16 L15,16 M17,9 L23,9", "设置"), () => { CloseSystemPanel(); controller.Configure(); }));
        controlContent.Children.Add(new Border { Height = 1, Background = ControlBrush("#405064"), Margin = new Thickness(4, 2, 4, 8) });
        controlContent.Children.Add(tools); controlContent.Children.Add(controlMessage!);
        ReadAudio(); _ = RefreshRadioViews();
    }
    private static StackPanel ToolContent(string path, string title)
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse(path), Stroke = ControlBrush("#B4D9ED"), StrokeThickness = 1.6, Width = 23, Height = 23, Stretch = Stretch.Uniform, Margin = new Thickness(0, 2, 0, 8) });
        panel.Children.Add(ControlText(title)); return panel;
    }
    private void AddRadio(Grid parent, int column, RadioKind kind, string title, string geometry)
    {
        var section = new StackPanel { Margin = new Thickness(2) };
        var split = new Grid(); split.ColumnDefinitions.Add(new()); split.ColumnDefinitions.Add(new() { Width = new GridLength(34) });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), Stroke = ControlBrush("#D8ECF5"), StrokeThickness = 1.6, Width = 22, Height = 22, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(ControlText(title, 14));
        var toggle = ControlButton(row, () => _ = ToggleRadio(kind), title + " 开启或关闭"); toggle.MinHeight = 52;
        var details = ControlButton("›", () => _ = ShowRadioDetails(kind), title + " 列表与设置"); details.MinHeight = 52; details.FontSize = 22;
        Grid.SetColumn(details, 1); split.Children.Add(toggle); split.Children.Add(details);
        var status = ControlText("读取中"); status.Margin = new Thickness(10, 4, 4, 0);
        radioViews[kind] = (toggle, status); section.Children.Add(split); section.Children.Add(status);
        Grid.SetColumn(section, column); parent.Children.Add(section);
    }
    private async Task RefreshRadioViews()
    {
        var version = panelVersion;
        foreach (var kind in radioViews.Keys.ToArray())
        {
            try
            {
                var state = await DockWireless.Read(kind);
                if (closed || version != panelVersion || !radioViews.TryGetValue(kind, out var view)) return;
                view.Toggle.Background = ControlBrush(state.On ? "#246E8B" : "#2B3543");
                view.Toggle.IsEnabled = state.Available && !state.Locked && !wirelessBusy;
                view.Status.Text = state.Locked ? "系统已禁用" : state.Label;
            }
            catch (Exception ex) when (WirelessError(ex))
            {
                if (version != panelVersion) return;
                if (radioViews.TryGetValue(kind, out var view)) { view.Status.Text = "状态不可用"; view.Toggle.IsEnabled = false; }
            }
        }
    }
    private static bool WirelessError(Exception ex) => ex is COMException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException or TimeoutException;
    private async Task ToggleRadio(RadioKind kind)
    {
        if (wirelessBusy) return;
        wirelessBusy = true; var version = panelVersion;
        foreach (var view in radioViews.Values) view.Toggle.IsEnabled = false;
        try
        {
            var state = await DockWireless.Read(kind);
            var error = await DockWireless.Set(kind, !state.On);
            if (version == panelVersion) SetPanelMessage(error ?? "");
        }
        catch (Exception ex) when (WirelessError(ex)) { if (version == panelVersion) SetPanelMessage("开关操作失败，请在系统设置中操作。"); }
        finally { wirelessBusy = false; await RefreshRadioViews(); }
    }
    private async Task ShowRadioDetails(RadioKind kind)
    {
        if (controlContent == null || wirelessBusy) return;
        NewPanelPage(); var version = panelVersion;
        var heading = new Grid(); heading.ColumnDefinitions.Add(new() { Width = new GridLength(42) }); heading.ColumnDefinitions.Add(new()); heading.ColumnDefinitions.Add(new() { Width = new GridLength(64) });
        heading.Children.Add(ControlButton("‹", ShowControlHome, "返回控制中心"));
        var title = ControlText(kind == RadioKind.WiFi ? "Wi-Fi" : "蓝牙", 18); Grid.SetColumn(title, 1); heading.Children.Add(title);
        var toggle = ControlButton("开关", () => _ = ToggleDetailRadio(kind, version), "切换无线开关"); Grid.SetColumn(toggle, 2); heading.Children.Add(toggle);
        controlContent.Children.Add(heading);
        var list = new StackPanel(); var scroll = new ScrollViewer { Content = list, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        controlContent.Children.Add(scroll);
        controlContent.Children.Add(controlMessage!); SetPanelMessage("正在读取…");
        controlContent.Children.Add(ControlButton(kind == RadioKind.WiFi ? "更多 Wi-Fi 设置  ›" : "更多蓝牙设置  ›", () => { CloseSystemPanel(); controller.Launch(kind == RadioKind.WiFi ? "ms-settings:network-wifi" : "ms-settings:bluetooth"); }));
        controlContent.Children.Add(ControlButton("刷新", () => _ = ShowRadioDetails(kind)));
        try
        {
            var state = await DockWireless.Read(kind);
            if (version != panelVersion || closed) return;
            toggle.Content = state.On ? "已开启" : "已关闭"; toggle.IsEnabled = state.Available && !state.Locked;
            if (!state.Available || !state.On) { SetPanelMessage(!state.Available ? "没有可用设备" : "无线已关闭，请先开启"); return; }
            if (kind == RadioKind.WiFi)
            {
                var networks = await DockWireless.Networks();
                if (version != panelVersion || closed) return;
                SetPanelMessage(networks.Length == 0 ? "未发现网络" : "");
                foreach (var network in networks)
                    list.Children.Add(ControlButton($"{network.Name}\n{(network.Connected ? "已连接" : $"信号 {network.Signal}/5")}  ›", () => _ = ConnectNetwork(network, version)));
            }
            else
            {
                var devices = await DockWireless.Devices();
                if (version != panelVersion || closed) return;
                SetPanelMessage(devices.Length == 0 ? "暂无已配对设备，请在更多蓝牙设置中添加。" : "");
                foreach (var device in devices)
                    list.Children.Add(ControlButton($"{device.Name}\n{(device.Connected ? "已连接" : "已配对")}  ›", () => { CloseSystemPanel(); controller.Launch("ms-settings:bluetooth"); }));
            }
        }
        catch (Exception ex) when (WirelessError(ex)) { if (version == panelVersion) SetPanelMessage("无法读取列表，请使用下方系统设置。Wi-Fi 扫描可能需要位置访问许可。"); }
    }
    private async Task ToggleDetailRadio(RadioKind kind, long version)
    {
        await ToggleRadio(kind);
        if (version == panelVersion && !closed) await ShowRadioDetails(kind);
    }
    private async Task ConnectNetwork(DockWireless.Network network, long version)
    {
        if (wirelessBusy) return;
        wirelessBusy = true;
        try
        {
            if (network.Connected) return;
            SetPanelMessage("正在连接…");
            var success = await DockWireless.Connect(network);
            if (closed || version != panelVersion) return;
            SetPanelMessage(success ? "已连接" : "连接未完成。需要密码或认证时，请到更多 Wi-Fi 设置中连接。");
        }
        catch (Exception ex) when (WirelessError(ex)) { if (version == panelVersion) SetPanelMessage("连接失败，请使用更多 Wi-Fi 设置完成认证。"); }
        finally { wirelessBusy = false; }
    }
}
