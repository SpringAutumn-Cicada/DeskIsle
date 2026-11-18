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
    private bool volumeSync, wirelessBusy, radioRefreshBusy;
    private long wirelessRevision;
    private long panelVersion;
    private readonly Dictionary<RadioKind, DockWireless.State> radioStates = [];
    private readonly Dictionary<RadioKind, (Button Toggle, TextBlock Status, Grid Switch)> radioViews = [];
    private static SolidColorBrush ControlBrush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private Button ControlButton(object content, Action action, string? accessibleName = null)
    {
        var button = new Button { Content = content, Style = (Style)FindResource("PanelAction") };
        var label = accessibleName ?? (content is StackPanel stack ? stack.Children.OfType<TextBlock>().LastOrDefault()?.Text : null) ?? content.ToString() ?? "控制";
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        return button;
    }
    private static TextBlock ControlText(string text, double size = 12) => new() { Text = text, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI"), FontSize = size, Foreground = ControlBrush("#E1EDF4"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private void OpenControlPanel()
    {
        CloseSystemPanel(); ClosePreview();
        var content = new StackPanel { Width = 288 };
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
        wifiConnectionCompleted = null;
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
        wifiConnectionCompleted = null;
        ++panelVersion; radioViews.Clear(); panelVolume = null; panelVolumeText = null; panelMute = null; panelMuteGlyph = null;
        controlContent?.Children.Clear();
        controlMessage = ControlText(""); controlMessage.Margin = new Thickness(4, 8, 4, 2); controlMessage.Visibility = Visibility.Collapsed;
    }
    private void ShowControlHome()
    {
        if (controlContent == null) return;
        NewPanelPage();
        var top = new Grid(); top.ColumnDefinitions.Add(new()); top.ColumnDefinitions.Add(new());
        AddRadio(top, 0, RadioKind.WiFi, "Wi-Fi");
        AddRadio(top, 1, RadioKind.Bluetooth, "蓝牙");
        controlContent.Children.Add(top);
        var volumeRow = new Grid { Margin = new Thickness(8, 2, 8, 2) };
        volumeRow.ColumnDefinitions.Add(new() { Width = new GridLength(42) }); volumeRow.ColumnDefinitions.Add(new()); volumeRow.ColumnDefinitions.Add(new() { Width = new GridLength(50) });
        panelMuteGlyph = ControlIcon("M2,8 L7,8 L13,3 L13,21 L7,16 L2,16 Z M17,7 Q23,12 17,17", 21);
        panelMuteGlyph.Stretch = Stretch.None; panelMuteGlyph.Width = 42; panelMuteGlyph.Height = 28;
        panelMuteGlyph.LayoutTransform = new ScaleTransform(.82, .82);
        panelMute = ControlButton(panelMuteGlyph, () => { DockAudio.Access(v => { v.GetMute(out var mute); v.SetMute(!mute, 0); }); ReadAudio(); }, "静音或取消静音");
        panelMute.Style = (Style)FindResource("PanelTool"); panelMute.Padding = new Thickness(2); volumeRow.Children.Add(panelMute);
        panelVolume = new Slider { Style = (Style)FindResource("PanelVolumeSlider"), Minimum = 0, Maximum = 100, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0), Foreground = ControlBrush("#42BFE8") };
        panelVolume.ValueChanged += (_, _) =>
        {
            if (volumeSync || panelVolume == null) return;
            var value = panelVolume.Value;
            if (!DockAudio.Access(v => v.SetMasterVolumeLevelScalar((float)(value / 100), 0))) SetPanelMessage("音频设备暂不可用");
            ReadAudio();
        };
        Grid.SetColumn(panelVolume, 1); volumeRow.Children.Add(panelVolume);
        panelVolumeText = ControlText("—", 15); panelVolumeText.FontWeight = FontWeights.Medium; TextOptions.SetTextFormattingMode(panelVolumeText, TextFormattingMode.Ideal); panelVolumeText.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(panelVolumeText, 2); volumeRow.Children.Add(panelVolumeText);
        controlContent.Children.Add(new Border { Child = volumeRow, CornerRadius = new CornerRadius(15), BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Margin = new Thickness(0, 10, 0, 8), Padding = new Thickness(0, 3, 0, 3) });
        var tools = new UniformGrid { Columns = 3, Height = 40 };
        tools.Children.Add(ToolButton("M7,3 L17,3 Q22,3 22,8 L22,16 Q22,21 17,21 L7,21 Q2,21 2,16 L2,8 Q2,3 7,3 Z M6,13 L9,13 Q10,13 11,8 Q12,5 13,13 Q14,19 15,13 L18,13", "任务管理器", () => { CloseSystemPanel(); TaskManagerClick(this, new RoutedEventArgs()); }));
        tools.Children.Add(ToolButton("M6,4 L18,4 Q22,4 22,8 L22,16 Q22,20 18,20 L6,20 Q2,20 2,16 L2,8 Q2,4 6,4 Z M3,15 L21,15 M7,18 L11,18 M14,18 L17,18", "原生任务栏", () => { CloseSystemPanel(); controller.ToggleNative(); }));
        tools.Children.Add(ToolButton("M5,2 L5,7 M5,13 L5,22 M12,2 L12,13 M12,19 L12,22 M19,2 L19,5 M19,11 L19,22 M2,7 L8,7 L8,13 L2,13 Z M9,13 L15,13 L15,19 L9,19 Z M16,5 L22,5 L22,11 L16,11 Z", "设置", () => { CloseSystemPanel(); controller.Configure(); }));
        controlContent.Children.Add(new Border { Child = tools, CornerRadius = new CornerRadius(14), Background = ControlBrush("#303B4B"), Padding = new Thickness(2, 4, 2, 4) }); controlContent.Children.Add(controlMessage!);
        ReadAudio(); _ = RefreshRadioViews();
    }
    private Button ToolButton(string path, string title, Action action)
    {
        FrameworkElement icon;
        if (title == "原生任务栏")
        {
            icon = NativeTaskbarIcon();
        }
        else if (title == "任务管理器") icon = ControlIcon("M2,13 L7,13 L10,5 L14,21 L17,11 L22,11", 23);
        else icon = ControlIcon(path, 23);
        var button = ControlButton(icon, action, title);
        button.ToolTip = title;
        button.Style = (Style)FindResource("PanelCompactTool");
        return button;
    }
    private static Viewbox NativeTaskbarIcon()
    {
        var brush = ControlBrush("#BDEAF3");
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M5,2 L19,2 Q22,2 22,5 L22,19 Q22,22 19,22 L5,22 Q2,22 2,19 L2,5 Q2,2 5,2 Z M2,16 L22,16"),
            Stroke = brush, StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round
        });
        foreach (var left in new[] { 5.0, 10.5, 16.0 })
        {
            var tile = new System.Windows.Shapes.Rectangle { Width = 3, Height = 2.7, RadiusX = .6, RadiusY = .6, Fill = brush };
            Canvas.SetLeft(tile, left); Canvas.SetTop(tile, 17.7);
            canvas.Children.Add(tile);
        }
        return new Viewbox { Width = 27, Height = 27, Child = canvas, Stretch = Stretch.Uniform };
    }
    private static Grid RadioIcon(RadioKind kind)
    {
        // Fixed canvas keeps Bluetooth from stretching into the full Wi-Fi width.
        var glyph = new Grid { Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(kind == RadioKind.WiFi
                ? "M1,12 A15,15 0 0 1 25,12 M5,16 A10,10 0 0 1 21,16 M9,20 A5,5 0 0 1 17,20"
                : "M11,3 Q11,2 12,3 L19,8.5 Q19.5,9 19,9.5 L7,19 M11,23 Q11,24 12,23 L19,17.5 Q19.5,17 19,16.5 L7,7 M11,3 L11,23"),
            Stroke = ControlBrush("#BDEAF3"), StrokeThickness = kind == RadioKind.WiFi ? 2.9 : 1.9,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, Stretch = Stretch.None
        };
        glyph.Children.Add(path);
        if (kind == RadioKind.WiFi)
            glyph.Children.Add(new System.Windows.Shapes.Ellipse { Width = 4, Height = 4, Fill = ControlBrush("#BDEAF3"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(11, 22, 0, 0) });
        return glyph;
    }
    private static System.Windows.Shapes.Path ControlIcon(string geometry, double size = 20) => new()
    {
        Data = Geometry.Parse(geometry), Stroke = ControlBrush("#CAE7F5"), StrokeThickness = 1.6,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        Width = size, Height = size, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };
    private void AddRadio(Grid parent, int column, RadioKind kind, string title)
    {
        var card = new Grid { Height = 98 };
        var toggleContent = new Grid();
        toggleContent.RowDefinitions.Add(new() { Height = new GridLength(32) });
        toggleContent.RowDefinitions.Add(new() { Height = new GridLength(2) });
        toggleContent.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var glyph = RadioIcon(kind); glyph.HorizontalAlignment = HorizontalAlignment.Left; glyph.VerticalAlignment = VerticalAlignment.Top; glyph.Margin = new Thickness(0, 0, 0, 0);
        toggleContent.Children.Add(glyph);
        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        Grid.SetRow(copy, 2);
        var name = ControlText(title, 15); name.FontWeight = FontWeights.Medium; copy.Children.Add(name);
        var status = ControlText("读取中", 11); status.Margin = new Thickness(0, 2, 0, 0); copy.Children.Add(status);
        toggleContent.Children.Add(copy);
        var toggle = ControlButton(toggleContent, () => _ = ToggleRadio(kind), title + " 开启或关闭");
        toggle.IsEnabled = false;
        toggle.Margin = new Thickness(0); toggle.Padding = new Thickness(12, 12, 28, 10); toggle.BorderThickness = new Thickness(0);
        card.Children.Add(toggle);
        var radioSwitch = PowerState(false); radioSwitch.HorizontalAlignment = HorizontalAlignment.Right; radioSwitch.VerticalAlignment = VerticalAlignment.Top; radioSwitch.Margin = new Thickness(0, 16, 18, 0); radioSwitch.IsHitTestVisible = false;
        card.Children.Add(radioSwitch);
        var details = ControlButton(ControlIcon("M2,1 L8,7 L2,13", 8), () => _ = ShowRadioDetails(kind), title + " 列表与设置");
        details.Style = (Style)FindResource("PanelTool"); details.Width = 32; details.Height = 32; details.MinHeight = 32;
        details.HorizontalAlignment = HorizontalAlignment.Right; details.VerticalAlignment = VerticalAlignment.Bottom; details.Margin = new Thickness(0, 0, 4, 2);
        card.Children.Add(details);
        var outline = new Border { Child = card, Margin = new Thickness(column == 0 ? 0 : 5, 0, column == 0 ? 5 : 0, 0) };
        radioViews[kind] = (toggle, status, radioSwitch); Grid.SetColumn(outline, column); parent.Children.Add(outline);
        ApplyRadioState(kind);
    }

    private void ApplyRadioState(RadioKind kind)
    {
        if (!radioViews.TryGetValue(kind, out var view) || !radioStates.TryGetValue(kind, out var state)) return;
        view.Toggle.Background = new LinearGradientBrush(Color.FromRgb(48, 60, 74), Color.FromRgb(39, 49, 63), new Point(0, 0), new Point(1, 1));
        view.Status.Foreground = ControlBrush(state.Label == "已连接" ? "#CDEAF2" : "#A6B9CA");
        var replacement = PowerState(state.On);
        view.Switch.Children.Clear();
        foreach (var child in replacement.Children.Cast<UIElement>().ToArray())
        { replacement.Children.Remove(child); view.Switch.Children.Add(child); }
        view.Toggle.IsEnabled = state.Available && !state.Locked && !wirelessBusy;
        view.Status.Text = state.Locked ? "系统已禁用" : kind == RadioKind.WiFi ? wifiPendingMessage ?? state.Label : state.Label;
    }

    private async Task RefreshRadioViews()
    {
        if (radioRefreshBusy || (wirelessBusy && wifiPendingNetwork == null) || radioViews.Count == 0) return;
        radioRefreshBusy = true;
        try
        {
            var revision = wirelessRevision;
            var snapshot = DockWireless.ReadRadios();
            await Task.WhenAll(radioViews.Keys.Where(kind => kind != RadioKind.WiFi || wifiPendingNetwork == null)
                .ToArray().Select(Refresh));

            async Task Refresh(RadioKind kind)
            {
                try
                {
                    var state = await DockWireless.Read(kind, connection: true, snapshot: snapshot);
                    if (closed || revision != wirelessRevision) return;
                    radioStates[kind] = state;
                    if (kind == RadioKind.WiFi && !state.On) ClearWifiSnapshot();
                    ApplyRadioState(kind);
                }
                catch (Exception ex) when (WirelessError(ex))
                {
                    if (closed || revision != wirelessRevision || radioStates.ContainsKey(kind)) return;
                    if (radioViews.TryGetValue(kind, out var view)) { view.Status.Text = "暂时无法读取"; view.Toggle.IsEnabled = false; }
                }
            }
        }
        finally { radioRefreshBusy = false; }
    }
    private static bool WirelessError(Exception ex) => ex is COMException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException or TimeoutException or OperationCanceledException;
    private async Task ToggleRadio(RadioKind kind)
    {
        if (wirelessBusy) return;
        if (kind == RadioKind.WiFi) ClearWifiSnapshot();
        wirelessBusy = true; ++wirelessRevision; var version = panelVersion;
        if (radioViews.TryGetValue(kind, out var pending)) { pending.Status.Text = "正在切换…"; pending.Switch.Opacity = .55; }
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
        foreach (var view in radioViews.Values) view.Toggle.IsEnabled = false;
        try
        {
            var state = await DockWireless.Read(kind);
            var error = await DockWireless.Set(kind, !state.On);
            if (version == panelVersion)
            {
                SetPanelMessage(error ?? "");
                var actual = await DockWireless.Read(kind);
                if (version == panelVersion && radioViews.TryGetValue(kind, out var updated))
                {
                    var visual = PowerState(actual.On); updated.Switch.Children.Clear();
                    foreach (var child in visual.Children.Cast<UIElement>().ToArray()) { visual.Children.Remove(child); updated.Switch.Children.Add(child); }
                    updated.Status.Text = actual.On ? "正在确认连接…" : "未连接";
                }
            }
        }
        catch (Exception ex) when (WirelessError(ex)) { if (version == panelVersion) SetPanelMessage("开关操作失败，请在系统设置中操作。"); }
        finally
        {
            wirelessBusy = false;
            foreach (var view in radioViews.Values) { view.Switch.Opacity = 1; }
            await RefreshRadioViews();
        }
    }
    private async Task ShowRadioDetails(RadioKind kind, bool rescan = false)
    {
        var isWifi = kind == RadioKind.WiFi;
        // Reuse the scan for a pending action or a recent retry. The refresh button always requests a new scan.
        var cached = isWifi && !rescan && wifiSnapshot is { } snapshot &&
            (wifiPendingNetwork != null || (wifiFeedback != null && Environment.TickCount64 - snapshot.UpdatedAt < 30_000))
            ? snapshot : null;
        if (controlContent == null || (wirelessBusy && (wifiPendingNetwork == null || (isWifi && cached == null)))) return;
        if (isWifi && cached == null) ClearWifiSnapshot();
        NewPanelPage(); var version = panelVersion;
        var heading = new Grid(); heading.ColumnDefinitions.Add(new() { Width = new GridLength(42) }); heading.ColumnDefinitions.Add(new()); heading.ColumnDefinitions.Add(new() { Width = new GridLength(64) });
        var back = ControlButton(ControlIcon("M8,1 L2,7 L8,13", 10), ShowControlHome, "返回控制中心"); back.Style = (Style)FindResource("PanelTool");
        heading.Children.Add(back);
        var title = ControlText(kind == RadioKind.WiFi ? "Wi-Fi" : "蓝牙", 16); title.FontWeight = FontWeights.Medium; title.Margin = new Thickness(6, 0, 0, 0); Grid.SetColumn(title, 1); heading.Children.Add(title);
        var toggle = ControlButton("开关", () => _ = ToggleDetailRadio(kind, version), "切换无线开关"); Grid.SetColumn(toggle, 2); heading.Children.Add(toggle); toggle.IsEnabled = false;
        controlContent.Children.Add(heading);
        controlContent.Children.Add(new Border { Height = 1, Background = ControlBrush("#344F63"), Margin = new Thickness(2, 10, 2, 6) });
        var list = new StackPanel();
        var scroll = new ScrollViewer { Content = list, MaxHeight = 256, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 2, 0, 6), PanningMode = PanningMode.VerticalOnly };
        scroll.Resources.Add(typeof(ScrollBar), FindResource("SoftScrollBar"));
        controlContent.Children.Add(scroll);
        controlContent.Children.Add(controlMessage!); SetPanelMessage("正在读取…");
        var footer = new Grid { Margin = new Thickness(0, 8, 0, 0) }; footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = new GridLength(40) });
        var settings = ControlButton(DeviceRow(kind == RadioKind.WiFi ? "更多 Wi-Fi 设置" : "更多蓝牙设置", "", "M5,2 L5,7 M5,13 L5,22 M12,2 L12,13 M12,19 L12,22 M19,2 L19,5 M19,11 L19,22 M2,7 L8,7 L8,13 L2,13 Z M9,13 L15,13 L15,19 L9,19 Z M16,5 L22,5 L22,11 L16,11 Z", false), () => { CloseSystemPanel(); controller.Launch(kind == RadioKind.WiFi ? "ms-settings:network-wifi" : "ms-settings:bluetooth"); }, "打开系统无线设置");
        settings.Style = (Style)FindResource("PanelListAction"); footer.Children.Add(settings);
        var refresh = ControlButton(ControlIcon("M20,8 A9,9 0 1 0 21,15 M20,3 L20,9 L14,9", 17), () => _ = ShowRadioDetails(kind, rescan: true), "刷新设备列表");
        refresh.Style = (Style)FindResource("PanelTool"); refresh.ToolTip = "刷新"; Grid.SetColumn(refresh, 1); footer.Children.Add(refresh);
        controlContent.Children.Add(new Border { Height = 1, Background = ControlBrush("#344F63"), Margin = new Thickness(2, 4, 2, 0) }); controlContent.Children.Add(footer);
        try
        {
            var state = cached != null && radioStates.TryGetValue(kind, out var known) && known.On
                ? known : await DockWireless.Read(kind);
            if (version != panelVersion || closed) return;
            toggle.Content = PowerState(state.On); toggle.Background = Brushes.Transparent; toggle.BorderThickness = new Thickness(0); toggle.ToolTip = state.On ? "点击关闭" : "点击开启"; toggle.IsEnabled = state.Available && !state.Locked;
            if (!state.Available || !state.On) { SetPanelMessage(!state.Available ? "没有可用设备" : "无线已关闭，请先开启"); return; }
            if (kind == RadioKind.WiFi)
            {
                var current = cached ?? new WifiSnapshot(await DockWireless.Networks());
                if (version != panelVersion || closed) return;
                wifiSnapshot = current;
                radioStates[kind] = state with { Label = current.Connected.Count > 0 ? "已连接" : "未连接" };
                PopulateWifiNetworks(list, current, version, enabled =>
                {
                    toggle.IsEnabled = enabled && state.Available && !state.Locked;
                    refresh.IsEnabled = enabled;
                });
            }
            else
            {
                var devices = await DockWireless.Devices();
                if (version != panelVersion || closed) return;
                SetPanelMessage(devices.Length == 0 ? "暂无已配对设备，请在更多蓝牙设置中添加。" : "");
                foreach (var device in devices)
                    AddDeviceButton(list, device.Name, device.Connected ? "已连接" : "已配对", "M8,1 L18,8 L3,19 M8,23 L18,16 L3,5 M8,1 L8,23", device.Connected, () => { CloseSystemPanel(); controller.Launch("ms-settings:bluetooth"); });
            }
        }
        catch (Exception ex) when (WirelessError(ex)) { if (version == panelVersion) SetPanelMessage("无法读取列表，请使用下方系统设置。Wi-Fi 扫描可能需要位置访问许可。"); }
    }
    private static Grid SignalIcon(byte signal)
    {
        var icon = new Grid { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var paths = new[] { "M11.6,23 L12.4,23", "M8,19 A5,5 0 0 1 16,19", "M4.5,15 A9.5,9.5 0 0 1 19.5,15", "M1,11 A14,14 0 0 1 23,11" };
        var thresholds = new[] { 0, 1, 3, 4 };
        for (var i = 0; i < paths.Length; i++)
            icon.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse(paths[i]), Stroke = ControlBrush(signal >= thresholds[i] ? "#BDDDEA" : "#405365"), StrokeThickness = 2.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Stretch = Stretch.None });
        return icon;
    }
    private static LinearGradientBrush PanelSurface(bool active) => new(
        (Color)ColorConverter.ConvertFromString(active ? "#303E47" : "#252C33"),
        (Color)ColorConverter.ConvertFromString(active ? "#202B33" : "#1C232A"), new Point(0, 0), new Point(1, 1));
    private static Grid PowerState(bool on)
    {
        var track = new Grid { Width = 30, Height = 16, HorizontalAlignment = HorizontalAlignment.Left };
        track.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = on ? new LinearGradientBrush(Color.FromRgb(32, 179, 204), Color.FromRgb(98, 220, 234), 0) : ControlBrush("#485460"), BorderBrush = ControlBrush(on ? "#7BDCE8" : "#677784"), BorderThickness = new Thickness(.6) });
        track.Children.Add(new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, Fill = ControlBrush("#DEF3FA"), HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(2) });
        return track;
    }
    private void AddDeviceButton(StackPanel list, string name, string state, string icon, bool connected, Action action, byte? signal = null)
    {
        var button = ControlButton(DeviceRow(name, state, icon, connected, signal), action, name);
        button.Style = (Style)FindResource("PanelListAction");
        button.ToolTip = name;
        button.Background = PanelSurface(connected);
        var frame = new Grid();
        frame.Children.Add(button);
        if (connected) frame.Children.Add(new Border { Width = 3, CornerRadius = new CornerRadius(1.5), Background = ControlBrush("#59D9E6"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(1, 10, 0, 10), IsHitTestVisible = false });
        list.Children.Add(frame);
    }
    private static Grid DeviceRow(string name, string state, string icon, bool connected, byte? signal = null, bool selected = false)
    {
        var row = new Grid { Margin = new Thickness(2, 4, 2, 4) };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(24) }); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = new GridLength(18) });
        row.Children.Add(signal.HasValue ? SignalIcon(signal.Value) : icon.StartsWith("M8,1", StringComparison.Ordinal) ? RadioIcon(RadioKind.Bluetooth) : ControlIcon(icon, 20));
        var text = new StackPanel { Margin = new Thickness(9, 0, 4, 0) };
        var title = ControlText(name, 14); title.FontWeight = FontWeights.SemiBold; TextOptions.SetTextFormattingMode(title, TextFormattingMode.Ideal); title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; title.MaxWidth = 222;
        text.Children.Add(title);
        var description = ControlText(state, 11); description.FontWeight = FontWeights.Medium; description.Foreground = ControlBrush(connected ? "#A3E8F2" : "#B2C4D2"); description.Margin = new Thickness(0, 4, 0, 0); if (!string.IsNullOrEmpty(state)) text.Children.Add(description);
        Grid.SetColumn(text, 1); row.Children.Add(text);
        FrameworkElement trailing;
        if (!string.IsNullOrEmpty(state))
        {
            var ring = new Grid { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            ring.Children.Add(new System.Windows.Shapes.Ellipse
                { Stroke = ControlBrush(selected ? "#D5F6FF" : connected ? "#5AE0EE" : "#8B9CA9"), StrokeThickness = selected ? 1.8 : 1.2 });
            if (selected || connected) ring.Children.Add(new System.Windows.Shapes.Ellipse
                { Width = 8, Height = 8, Fill = ControlBrush(selected ? "#D5F6FF" : "#5AE0EE") });
            trailing = ring;
        }
        else trailing = ControlIcon("M2,1 L8,7 L2,13", 7);
        Grid.SetColumn(trailing, 2); row.Children.Add(trailing); return row;
    }
    private async Task ToggleDetailRadio(RadioKind kind, long version)
    {
        if (wirelessBusy) return;
        await ToggleRadio(kind);
        if (version == panelVersion && !closed) await ShowRadioDetails(kind);
    }

}
