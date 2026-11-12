using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
namespace DeskIsle.Dock;

public partial class DockBar
{
    private readonly DispatcherTimer statusTick = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
    private Popup? systemPopup;
    private bool statusBusy;
    private void InitializeSystemPanel()
    {
        TerminalSurface.Opacity = controller.Settings.Opacity;
        statusTick.Tick += async (_, _) => await RefreshSystemStatus();
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible) { statusTick.Start(); await RefreshSystemStatus(); }
            else statusTick.Stop();
        };
    }
    private async Task RefreshSystemStatus()
    {
        if (closed || statusBusy) return;
        statusBusy = true;
        try
        {
            var network = await Task.Run(() =>
            {
                try
                {
                    var adapters = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)).ToArray();
                    var routed = adapters.Where(n => n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any) && !g.Address.Equals(System.Net.IPAddress.IPv6Any))).ToArray();
                    var adapter = routed.FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet) ?? routed.FirstOrDefault() ?? adapters.FirstOrDefault();
                    return adapter == null ? (0, "网络未连接") : (adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 2, adapter.Description);
                }
                catch (NetworkInformationException) { return (-1, "网络状态暂不可用"); }
            });
            if (closed) return;
            NetworkKey.ToolTip = network.Item2 + " · 系统快捷设置";
            NetworkGlyph.Data = Geometry.Parse(network.Item1 switch
            {
                1 => "M1,7 Q12,-2 23,7 M5,12 Q12,5 19,12 M9,17 Q12,14 15,17 M12,21 L12,22",
                2 => "M2,3 L22,3 L22,16 L2,16 Z M12,16 L12,22 M6,22 L18,22",
                _ => "M2,3 L22,3 L22,16 L2,16 Z M8,20 L16,20 M3,1 L22,23"
            });
            NetworkKey.Opacity = network.Item1 > 0 ? 1 : .55;
            var powerAvailable = DockPower.GetSystemPowerStatus(out var power);
            BatteryKey.Visibility = powerAvailable && power.Flags != 255 && (power.Flags & 128) != 0 ? Visibility.Collapsed : Visibility.Visible;
            ChargingGlyph.Visibility = powerAvailable && power.Flags != 255 && (power.Flags & 8) != 0 ? Visibility.Visible : Visibility.Collapsed;
            var batteryColor = powerAvailable && power.Percent <= 20 && power.AC == 0 ? Brushes.SandyBrown : new SolidColorBrush(Color.FromRgb(180, 217, 237));
            BatteryGlyph.Stroke = batteryColor; BatteryText.Foreground = batteryColor; ChargingGlyph.Stroke = batteryColor;
            BatteryText.Text = powerAvailable && power.Percent <= 100 ? $"{power.Percent}" : "—";
            BatteryKey.ToolTip = !powerAvailable || power.Percent > 100 ? "电量状态暂不可用" : $"电量 {power.Percent}% · " + (power.Flags != 255 && (power.Flags & 8) != 0 ? "正在充电" : power.AC == 1 ? "已接通电源" : "使用电池");
            ReadAudio();
            await RefreshBackgroundApps();
        }
        finally { statusBusy = false; }
    }
    private void ReadAudio()
    {
        float level = 0; bool muted = false;
        var available = DockAudio.Access(v => { v.GetMasterVolumeLevelScalar(out level); v.GetMute(out muted); });
        AudioKey.Opacity = available ? 1 : .5;
        AudioKey.ToolTip = available ? muted ? "已静音" : $"音量 {Math.Round(level * 100)}%" : "音频设备暂不可用 · 系统快捷设置";
        AudioGlyph.Data = Geometry.Parse(muted || !available
            ? "M2,8 L7,8 L13,3 L13,21 L7,16 L2,16 Z M17,8 L24,16 M24,8 L17,16"
            : "M2,8 L7,8 L13,3 L13,21 L7,16 L2,16 Z M17,7 Q23,12 17,17 M20,3 Q30,12 20,21");
    }
    private void CloseSystemPanel()
    {
        if (systemPopup != null) systemPopup.IsOpen = false;
        systemPopup = null;
    }
    private void OpenSystemPanel(Button anchor, UIElement content)
    {
        CloseSystemPanel(); ClosePreview();
        var border = new Border { Background = new SolidColorBrush(Color.FromRgb(32, 38, 50)), BorderBrush = new SolidColorBrush(Color.FromRgb(88, 130, 151)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14), Child = content };
        systemPopup = new Popup { Child = border, PlacementTarget = anchor, Placement = PlacementMode.Custom, VerticalOffset = -12, AllowsTransparency = true, StaysOpen = false };
        systemPopup.CustomPopupPlacementCallback = (popupSize, targetSize, offset) =>
            [new CustomPopupPlacement(new Point((targetSize.Width - popupSize.Width) / 2, -popupSize.Height - 10), PopupPrimaryAxis.Horizontal)];
        systemPopup.IsOpen = true;
    }
    private static TextBlock PanelText(string text) => new() { Text = text, Foreground = Brushes.LightSteelBlue, Margin = new Thickness(0, 4, 0, 10), TextWrapping = TextWrapping.Wrap, MaxWidth = 270 };
    private static Button PanelButton(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 3, 0, 3), HorizontalContentAlignment = HorizontalAlignment.Left, Foreground = Brushes.LightSteelBlue, Background = new SolidColorBrush(Color.FromRgb(43, 57, 73)), BorderBrush = Brushes.Transparent };
        button.Click += (_, _) => action(); return button;
    }
    private void QuickSettingsClick(object sender, RoutedEventArgs e)
    {
        CloseSystemPanel(); ClosePreview();
        if (!DockNative.OpenQuickSettings())
            MessageBox.Show("系统未接受快捷设置快捷键，请按 Win+A 打开。", "DeskIsle");
    }
}
