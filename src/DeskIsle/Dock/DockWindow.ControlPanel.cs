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
    private System.Windows.Shapes.Path? panelMuteGlyph;
    private Slider? panelVolume;
    private Button? panelMute;
    private bool volumeSync, wirelessBusy;
    private long panelVersion;
    private readonly Dictionary<RadioKind, (Button Toggle, TextBlock Status)> radioViews = [];
    private static SolidColorBrush ControlBrush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private Button ControlButton(object content, Action action, string? accessibleName = null)
    {
        var button = new Button { Content = content, Style = (Style)FindResource("PanelAction") };
        System.Windows.Automation.AutomationProperties.SetName(button, accessibleName ?? content.ToString() ?? "控制");
        button.Click += (_, _) => action();
        return button;
    }
    private static TextBlock ControlText(string text, double size = 12) => new() { Text = text, FontSize = size, Foreground = ControlBrush("#C2D9E8"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private void OpenControlPanel()
    {
        CloseSystemPanel(); ClosePreview();
        var content = new StackPanel { Width = 328 };
        controlContent = content;
        var shell = new ControlBubble { Child = content, SnapsToDevicePixels = true, UseLayoutRounding = true,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = .18 } };
        systemPopup = new Popup { Child = shell, PlacementTarget = StatusKey, Placement = PlacementMode.Custom, AllowsTransparency = true, StaysOpen = false };
        systemPopup.CustomPopupPlacementCallback = (size, target, offset) =>
        {
            var x = (target.Width - size.Width) / 2;
            if (PresentationSource.FromVisual(StatusKey)?.CompositionTarget is { } composition)
            {
                var transform = composition.TransformFromDevice;
                var monitor = DockNative.Primary().Monitor;
                var start = transform.Transform(StatusKey.PointToScreen(new Point()));
                var screenLeft = transform.Transform(new Point(monitor.Left, monitor.Top)).X;
                var screenRight = transform.Transform(new Point(monitor.Right, monitor.Bottom)).X;
                x = Math.Clamp(x, screenLeft - start.X + 8, Math.Max(screenLeft - start.X + 8, screenRight - start.X - size.Width - 8));
            }
            shell.AnchorX = target.Width / 2 - x;
            // The bubble has 8 DIP transparent shadow padding below the pointer: keep its tip 18 DIP above Dock.
            return [new CustomPopupPlacement(new Point(x, -size.Height - 10), PopupPrimaryAxis.Horizontal)];
        };
        var popup = systemPopup;
        popup.Closed += (_, _) =>
        {
            if (!ReferenceEquals(systemPopup, popup)) return;
            if (System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed && PointerOnStatusKey())
                statusDismissedAt = Environment.TickCount64;
            ResetControlPanel();
        };
        ShowControlHome();
        StatusKey.Tag = "PanelOpen";
        systemPopup.IsOpen = true;
    }
    private bool PointerOnStatusKey()
    {
        if (!StatusKey.IsLoaded || PresentationSource.FromVisual(StatusKey) == null || !DockNative.GetCursorPos(out var pointer)) return false;
        var a = StatusKey.PointToScreen(new Point());
        var b = StatusKey.PointToScreen(new Point(StatusKey.ActualWidth, StatusKey.ActualHeight));
        return pointer.X >= a.X && pointer.X < b.X && pointer.Y >= a.Y && pointer.Y < b.Y;
    }
    private void ResetControlPanel()
    {
        StatusKey.Tag = null;
        ++panelVersion; radioViews.Clear(); controlContent = null; controlMessage = null;
        panelVolume = null; panelVolumeText = null; panelMute = null; panelMuteGlyph = null;
    }
    private void SetPanelMessage(string text)
    {
        if (controlMessage == null) return;
        controlMessage.Text = text;
        controlMessage.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
    private void NewPanelPage()
    {
        ++panelVersion; radioViews.Clear(); panelVolume = null; panelVolumeText = null; panelMute = null; panelMuteGlyph = null;
        controlContent?.Children.Clear();
        controlMessage = ControlText(""); controlMessage.Margin = new Thickness(4, 8, 4, 2); controlMessage.Visibility = Visibility.Collapsed;
    }
    private void ShowControlHome()
    {
        if (controlContent == null) return;
        NewPanelPage();
        var top = new Grid(); top.ColumnDefinitions.Add(new()); top.ColumnDefinitions.Add(new());
        AddRadio(top, 0, RadioKind.WiFi, "Wi-Fi", "M1,7 Q12,-2 23,7 M5,12 Q12,5 19,12 M9,17 Q12,14 15,17 M12,21 L12,22");
        AddRadio(top, 1, RadioKind.Bluetooth, "蓝牙", "M8,1 L18,8 L3,19 M8,23 L18,16 L3,5 M8,1 L8,23");
        controlContent.Children.Add(top);
        var volumeRow = new Grid { Margin = new Thickness(8, 2, 8, 2) };
        volumeRow.ColumnDefinitions.Add(new() { Width = new GridLength(36) }); volumeRow.ColumnDefinitions.Add(new()); volumeRow.ColumnDefinitions.Add(new() { Width = new GridLength(42) });
        panelMuteGlyph = ControlIcon("M2,8 L7,8 L13,3 L13,21 L7,16 L2,16 Z M17,7 Q23,12 17,17", 21);
        panelMute = ControlButton(panelMuteGlyph, () => { DockAudio.Access(v => { v.GetMute(out var mute); v.SetMute(!mute, 0); }); ReadAudio(); }, "静音或取消静音");
        panelMute.Style = (Style)FindResource("PanelTool"); panelMute.Padding = new Thickness(6); volumeRow.Children.Add(panelMute);
        panelVolume = new Slider { Style = (Style)FindResource("PanelVolumeSlider"), Minimum = 0, Maximum = 100, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), Foreground = ControlBrush("#42BFE8") };
        panelVolume.ValueChanged += (_, _) =>
        {
            if (volumeSync || panelVolume == null) return;
            var value = panelVolume.Value;
            if (!DockAudio.Access(v => v.SetMasterVolumeLevelScalar((float)(value / 100), 0))) SetPanelMessage("音频设备暂不可用");
            ReadAudio();
        };
        Grid.SetColumn(panelVolume, 1); volumeRow.Children.Add(panelVolume);
        panelVolumeText = ControlText("—", 11); panelVolumeText.FontFamily = new FontFamily("Cascadia Mono, Consolas"); panelVolumeText.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(panelVolumeText, 2); volumeRow.Children.Add(panelVolumeText);
        controlContent.Children.Add(new Border { Child = volumeRow, CornerRadius = new CornerRadius(15), BorderBrush = ControlBrush("#345D788C"), BorderThickness = new Thickness(1), Background = ControlBrush("#0D83BDD7"), Margin = new Thickness(0, 12, 0, 10) });
        var tools = new UniformGrid { Columns = 3 };
        tools.Children.Add(ControlButton(ToolContent("M6,3 L18,3 Q22,3 22,7 L22,17 Q22,21 18,21 L6,21 Q2,21 2,17 L2,7 Q2,3 6,3 Z M5,7 L6,7 M9,7 L10,7 M13,7 L14,7", "原生任务栏"), () => { CloseSystemPanel(); controller.ToggleNative(); }));
        tools.Children.Add(ControlButton(ToolContent("M6,3 L18,3 Q22,3 22,7 L22,17 Q22,21 18,21 L6,21 Q2,21 2,17 L2,7 Q2,3 6,3 Z M5,13 L8,13 Q9,13 10,9 Q11,5 12,12 Q13,20 15,13 Q16,11 17,12 L19,12", "任务管理器"), () => { CloseSystemPanel(); TaskManagerClick(this, new RoutedEventArgs()); }));
        tools.Children.Add(ControlButton(ToolContent("M9,3 Q12,1 15,3 L15,5 L18,7 L20,7 L22,11 L20,13 L20,16 L17,20 L14,19 L11,21 L7,19 L7,17 L4,15 L2,12 L4,9 L4,6 L8,5 Z M15,12 A3,3 0 1 1 9,12 A3,3 0 1 1 15,12", "设置"), () => { CloseSystemPanel(); controller.Configure(); }));
        foreach (Button tool in tools.Children) tool.Style = (Style)FindResource("PanelTool");
        controlContent.Children.Add(tools); controlContent.Children.Add(controlMessage!);
        ReadAudio(); _ = RefreshRadioViews();
    }
    private static StackPanel ToolContent(string path, string title)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var icon = ControlIcon(path, 20); icon.StrokeThickness = 1.4; icon.Margin = new Thickness(0, 0, 6, 0); panel.Children.Add(icon);
        var label = ControlText(title, 10); label.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(label); return panel;
    }
    private static System.Windows.Shapes.Path ControlIcon(string geometry, double size = 20) => new()
    {
        Data = Geometry.Parse(geometry), Stroke = ControlBrush("#CAE7F5"), StrokeThickness = 1.6,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        Width = size, Height = size, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };
    private void AddRadio(Grid parent, int column, RadioKind kind, string title, string geometry)
    {
        var split = new Grid();
        split.ColumnDefinitions.Add(new()); split.ColumnDefinitions.Add(new() { Width = new GridLength(30) });
        var row = new Grid(); row.ColumnDefinitions.Add(new() { Width = new GridLength(28) }); row.ColumnDefinitions.Add(new());
        row.Children.Add(ControlIcon(geometry, 22));
        var copy = new StackPanel { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var name = ControlText(title, 13); name.FontWeight = FontWeights.SemiBold; copy.Children.Add(name);
        var status = ControlText("读取中", 10); status.Foreground = ControlBrush("#9FB6C8"); status.Margin = new Thickness(0, 3, 0, 0); copy.Children.Add(status);
        Grid.SetColumn(copy, 1); row.Children.Add(copy);
        var toggle = ControlButton(row, () => _ = ToggleRadio(kind), title + " 开启或关闭");
        toggle.Tag = "RadioLeft"; toggle.MinHeight = 56; toggle.Margin = new Thickness(0); toggle.Padding = new Thickness(10, 9, 5, 9); toggle.BorderThickness = new Thickness(0);
        var details = ControlButton(ControlIcon("M2,1 L8,7 L2,13", 8), () => _ = ShowRadioDetails(kind), title + " 列表与设置");
        details.Tag = "RadioRight"; details.Margin = new Thickness(0); details.Padding = new Thickness(8, 0, 8, 0); details.BorderThickness = new Thickness(0);
        details.SetBinding(BackgroundProperty, new System.Windows.Data.Binding(nameof(Button.Background)) { Source = toggle });
        Grid.SetColumn(details, 1); split.Children.Add(toggle); split.Children.Add(details);
        var divider = new Border { Width = 1, Height = 24, Background = ControlBrush("#41648194"), HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
        Grid.SetColumn(divider, 1); split.Children.Add(divider);
        var outline = new Border { Child = split, CornerRadius = new CornerRadius(18), BorderBrush = ControlBrush("#566C91A6"), BorderThickness = new Thickness(1), Margin = new Thickness(column == 0 ? 0 : 5, 0, column == 0 ? 5 : 0, 0) };
        radioViews[kind] = (toggle, status); Grid.SetColumn(outline, column); parent.Children.Add(outline);
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
                view.Toggle.Background = ControlBrush(state.On ? "#234B6171" : "#0D67869B");
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
        var back = ControlButton(ControlIcon("M8,1 L2,7 L8,13", 10), ShowControlHome, "返回控制中心"); back.Style = (Style)FindResource("PanelTool");
        heading.Children.Add(back);
        var title = ControlText(kind == RadioKind.WiFi ? "Wi-Fi" : "蓝牙", 16); title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(6, 0, 0, 0); Grid.SetColumn(title, 1); heading.Children.Add(title);
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
                    list.Children.Add(ControlButton(DeviceRow(network.Name, network.Connected ? "已连接" : $"信号 {network.Signal}/5", "M1,7 Q12,-2 23,7 M5,12 Q12,5 19,12 M9,17 Q12,14 15,17", network.Connected), () => _ = ConnectNetwork(network, version), network.Name));
            }
            else
            {
                var devices = await DockWireless.Devices();
                if (version != panelVersion || closed) return;
                SetPanelMessage(devices.Length == 0 ? "暂无已配对设备，请在更多蓝牙设置中添加。" : "");
                foreach (var device in devices)
                    list.Children.Add(ControlButton(DeviceRow(device.Name, device.Connected ? "已连接" : "已配对", "M8,1 L18,8 L3,19 M8,23 L18,16 L3,5 M8,1 L8,23", device.Connected), () => { CloseSystemPanel(); controller.Launch("ms-settings:bluetooth"); }, device.Name));
            }
        }
        catch (Exception ex) when (WirelessError(ex)) { if (version == panelVersion) SetPanelMessage("无法读取列表，请使用下方系统设置。Wi-Fi 扫描可能需要位置访问许可。"); }
    }
    private static Grid DeviceRow(string name, string state, string icon, bool connected)
    {
        var row = new Grid { Margin = new Thickness(2, 4, 2, 4) };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(30) }); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = new GridLength(18) });
        row.Children.Add(ControlIcon(icon, 20));
        var text = new StackPanel { Margin = new Thickness(9, 0, 4, 0) };
        var title = ControlText(name, 12); title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; title.MaxWidth = 208;
        text.Children.Add(title);
        var description = ControlText(state, 10); description.Foreground = ControlBrush(connected ? "#86D7EA" : "#94A6B7"); description.Margin = new Thickness(0, 4, 0, 0); text.Children.Add(description);
        Grid.SetColumn(text, 1); row.Children.Add(text);
        var chevron = ControlIcon("M2,1 L8,7 L2,13", 7); Grid.SetColumn(chevron, 2); row.Children.Add(chevron); return row;
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
