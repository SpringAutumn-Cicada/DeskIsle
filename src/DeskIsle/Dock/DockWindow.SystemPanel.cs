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
    private long statusDismissedAt = long.MinValue;
    private void InitializeSystemPanel()
    {
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
            if (systemPopup?.IsOpen == true && radioViews.Count > 0) await RefreshRadioViews();
        }
        finally { statusBusy = false; }
    }
    private void ReadAudio()
    {
        float level = 0; bool muted = false;
        var available = DockAudio.Access(v => { v.GetMasterVolumeLevelScalar(out level); v.GetMute(out muted); });
        AudioKey.Opacity = available ? 1 : .5;
        AudioKey.ToolTip = available ? muted ? "已静音" : $"音量 {Math.Round(level * 100)}%" : "音频设备暂不可用 · 系统快捷设置";
        volumeSync = true;
        if (panelVolume != null)
        {
            panelVolume.IsEnabled = available;
            if (!panelVolume.IsMouseCaptureWithin) panelVolume.Value = level * 100;
        }
        if (panelVolumeText != null) panelVolumeText.Text = available ? $"{Math.Round(level * 100)}%" : "—";
        if (panelMute != null) { panelMute.IsEnabled = available; panelMute.ToolTip = muted ? "取消静音" : "静音"; panelMute.Opacity = muted ? .55 : 1; }
        volumeSync = false;
        if (panelMuteGlyph != null) panelMuteGlyph.Data = Geometry.Parse(muted || !available
            ? "M2,8 L7,8 L13,3 L13,21 L7,16 L2,16 Z M17,8 L24,16 M24,8 L17,16"
            : "M2,8 L7,8 L13,3 L13,21 L7,16 L2,16 Z M17,7 Q23,12 17,17");
        AudioGlyph.Data = Geometry.Parse(muted || !available
            ? "M2,8 L7,8 L13,3 L13,21 L7,16 L2,16 Z M17,8 L24,16 M24,8 L17,16"
            : "M2,8 L7,8 L13,3 L13,21 L7,16 L2,16 Z M17,7 Q23,12 17,17 M20,3 Q30,12 20,21");
    }
    private void CloseSystemPanel()
    {
        if (systemPopup != null) systemPopup.IsOpen = false;
        systemPopup = null;
        ResetControlPanel();
    }
    private void QuickSettingsClick(object sender, RoutedEventArgs e)
    {
        if (systemPopup?.IsOpen == true) { CloseSystemPanel(); return; }
        // StaysOpen=false dismisses on mouse-down before Button.Click reaches the Dock.
        // Consume that same gesture instead of immediately creating another popup.
        var dismissedAt = statusDismissedAt; statusDismissedAt = long.MinValue;
        if (dismissedAt != long.MinValue && Environment.TickCount64 - dismissedAt < 600) return;
        OpenControlPanel();
    }
}
