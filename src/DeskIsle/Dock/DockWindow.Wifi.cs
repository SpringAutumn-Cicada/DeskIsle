using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Windows.Devices.WiFi;

namespace DeskIsle.Dock;

public partial class DockBar
{
    private void PopulateWifiNetworks(StackPanel list, DockWireless.Network[] networks, long version)
    {
        SetPanelMessage(networks.Length == 0 ? "未发现网络" : "");
        var actions = new StackPanel { Margin = new Thickness(2, 6, 2, 0), Visibility = Visibility.Collapsed };
        var selection = ControlText("");
        var connect = ControlButton("连接", () => _ = ConnectSelected(), "连接所选 Wi-Fi 网络");
        connect.HorizontalAlignment = HorizontalAlignment.Right;
        connect.MinWidth = 80;
        actions.Children.Add(selection);
        actions.Children.Add(connect);
        var page = controlContent!;
        page.Children.Insert(page.Children.IndexOf(controlMessage!), actions);

        DockWireless.Network? selected = null;
        var rows = new Dictionary<DockWireless.Network, Button>();
        foreach (var network in networks)
        {
            var button = ControlButton(network.Name, () => Select(network), network.Name);
            button.Style = (Style)FindResource("PanelListAction");
            button.ToolTip = network.Name;
            // The row is a single hit target, including its status circle.
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            rows.Add(network, button);
            list.Children.Add(button);
        }
        UpdateRows();

        void Select(DockWireless.Network network)
        {
            if (wirelessBusy) return;
            selected = network;
            SetPanelMessage("");
            UpdateRows();
        }

        void UpdateRows()
        {
            foreach (var (network, button) in rows)
            {
                var isSelected = ReferenceEquals(network, selected);
                var status = network.Connected ? "已连接" : network.Access == DockWireless.NetworkAccess.Open ? "开放网络" : "已加密";
                var description = $"{status} · 信号 {network.Signal}/5";
                button.Content = DeviceRow(network.Name, description, "", network.Connected, network.Signal, isSelected);
                button.Background = PanelSurface(network.Connected || isSelected);
                button.BorderBrush = ControlBrush(isSelected ? "#8DDCEA" : "#40596A79");
                AutomationProperties.SetItemStatus(button, isSelected ? $"已选中，{description}" : description);
            }
            actions.Visibility = selected == null ? Visibility.Collapsed : Visibility.Visible;
            selection.Text = selected?.Name ?? "";
            connect.IsEnabled = selected is { Connected: false };
            connect.Content = selected switch
            {
                { Connected: true } => "已连接",
                { Access: DockWireless.NetworkAccess.WindowsSettings } => "在系统设置中连接",
                _ => "连接"
            };
        }

        async Task ConnectSelected()
        {
            if (wirelessBusy || selected is not { Connected: false } network) return;
            if (network.Access == DockWireless.NetworkAccess.WindowsSettings)
            {
                CloseSystemPanel();
                controller.Launch("ms-settings:network-wifi");
                return;
            }

            wirelessBusy = true;
            connect.IsEnabled = list.IsEnabled = false;
            string? password = null;
            var connected = false;
            try
            {
                if (network.Access == DockWireless.NetworkAccess.Password)
                {
                    password = RequestWifiPassword(network.Name);
                    if (password == null || closed || version != panelVersion) return;
                }
                SetPanelMessage("正在连接…");
                var result = await DockWireless.Connect(network, password);
                if (closed || version != panelVersion) return;
                connected = result == WiFiConnectionStatus.Success;
                SetPanelMessage(result switch
                {
                    WiFiConnectionStatus.Success => "已连接",
                    WiFiConnectionStatus.InvalidCredential => "密码不正确，请重新输入。",
                    WiFiConnectionStatus.NetworkNotAvailable => "网络已不可用，请刷新列表。",
                    WiFiConnectionStatus.AccessRevoked => "系统拒绝访问，请检查 Wi-Fi 和位置访问权限。",
                    WiFiConnectionStatus.UnsupportedAuthenticationProtocol => "此网络需要在 Windows Wi-Fi 设置中连接。",
                    _ => "未能连接，请重试或打开更多 Wi-Fi 设置。"
                });
            }
            catch (TimeoutException) { if (version == panelVersion) SetPanelMessage("连接超时，请重试。"); }
            catch (Exception ex) when (WirelessError(ex))
            { if (version == panelVersion) SetPanelMessage("连接失败，请重试或打开更多 Wi-Fi 设置。"); }
            finally
            {
                wirelessBusy = false;
                if (!closed && version == panelVersion)
                {
                    list.IsEnabled = true;
                    connect.IsEnabled = !connected;
                }
            }
            if (connected && !closed && version == panelVersion)
            {
                // Read the updated connection from Windows after a successful join.
                await ShowRadioDetails(Windows.Devices.Radios.RadioKind.WiFi);
            }
        }
    }

    private string? RequestWifiPassword(string ssid)
    {
        var popup = systemPopup;
        if (popup == null || !popup.IsOpen) return null;

        var dialog = new Window
        {
            Owner = this,
            Title = "连接 Wi-Fi",
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            Width = 350,
            SizeToContent = SizeToContent.Height,
            Background = ControlBrush("#202832"),
            Foreground = ControlBrush("#E1EDF4")
        };
        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(ControlText(ssid, 16));
        var label = ControlText("网络密码");
        label.Margin = new Thickness(0, 12, 0, 5);
        content.Children.Add(label);
        var input = new PasswordBox { Foreground = dialog.Foreground, Background = ControlBrush("#151C25"),
            BorderBrush = ControlBrush("#608398"), Padding = new Thickness(8), FontSize = 14, MaxLength = 256 };
        AutomationProperties.SetLabeledBy(input, label);
        content.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0) };
        var cancel = ControlButton("取消", () => dialog.DialogResult = false);
        cancel.IsCancel = true;
        var confirm = ControlButton("连接", () => dialog.DialogResult = true);
        confirm.IsDefault = true;
        confirm.IsEnabled = false;
        input.PasswordChanged += (_, _) =>
        {
            using var securePassword = input.SecurePassword;
            confirm.IsEnabled = securePassword.Length > 0;
        };
        cancel.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        content.Children.Add(buttons);
        dialog.Content = content;
        dialog.Loaded += (_, _) => input.Focus();

        // Release the popup mouse capture so the activating password window can receive input.
        var staysOpen = popup.StaysOpen;
        popup.StaysOpen = true;
        try
        {
            return dialog.ShowDialog() == true ? input.Password : null;
        }
        finally
        {
            input.Clear();
            if (ReferenceEquals(systemPopup, popup) && popup.IsOpen) popup.StaysOpen = staysOpen;
        }
    }
}
