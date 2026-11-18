using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Windows.Devices.Radios;
using Windows.Devices.WiFi;

namespace DeskIsle.Dock;

public partial class DockBar
{
    private void PopulateWifiNetworks(StackPanel list, DockWireless.Network[] networks, long version)
    {
        SetPanelMessage(networks.Length == 0 ? "未发现网络" : "");

        var editor = new StackPanel { Margin = new Thickness(11, 4, 11, 9) };
        var label = ControlText("网络密码", 11);
        label.Margin = new Thickness(0, 0, 0, 5);
        var password = new PasswordBox
        {
            Style = (Style)FindResource("WifiPasswordField"),
            FontSize = 13,
            MaxLength = 256
        };
        AutomationProperties.SetLabeledBy(password, label);
        // Dock popups normally avoid activation. Give the popup keyboard input
        // only when the user explicitly clicks into the password field.
        password.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (PresentationSource.FromVisual(password) is HwndSource source)
                DockNative.SetForegroundWindow(source.Handle);
            Keyboard.Focus(password);
        };
        list.Unloaded += (_, _) => password.Clear();
        editor.Children.Add(label);
        editor.Children.Add(password);

        var message = ControlText("", 11);
        message.Margin = new Thickness(0, 3, 0, 0);
        message.Visibility = Visibility.Collapsed;
        editor.Children.Add(message);
        var connect = new Button
        {
            Content = "连接",
            Style = (Style)FindResource("PanelAction"),
            HorizontalAlignment = HorizontalAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = ControlBrush("#295463"),
            BorderBrush = ControlBrush("#508697"),
            Margin = new Thickness(0),
            Padding = new Thickness(12, 4, 12, 4),
            MinHeight = 28,
            MinWidth = 64
        };
        AutomationProperties.SetName(connect, "连接所选 Wi-Fi 网络");
        var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(connect, System.Windows.Controls.Dock.Right);
        footer.Children.Add(connect);
        var remember = new CheckBox
        {
            Content = "保存密码",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)FindResource("WifiRememberPassword")
        };
        AutomationProperties.SetHelpText(remember, "连接成功后保存在此电脑，下次选中该网络时自动填入。");
        footer.Children.Add(remember);
        editor.Children.Add(footer);
        var hint = ControlText("下次连接时自动填入", 10);
        hint.Foreground = ControlBrush("#A8BFCD");
        hint.Margin = new Thickness(21, 3, 0, 0);
        editor.Children.Add(hint);


        DockWireless.Network? selected = null;
        StackPanel? expandedRow = null;
        var rows = new Dictionary<DockWireless.Network, (StackPanel Content, Button Select)>();
        foreach (var network in networks)
        {
            var button = ControlButton(network.Name, () => Select(network), network.Name);
            button.Style = (Style)FindResource("PanelListAction");
            button.ToolTip = network.Name;
            button.Padding = new Thickness(10, 6, 10, 6);
            button.Margin = new Thickness(0);
            var content = new StackPanel();
            content.Children.Add(button);
            list.Children.Add(new Border { Child = content, Margin = new Thickness(0, 2, 0, 2),
                CornerRadius = new CornerRadius(14), Background = PanelSurface(false) });
            rows.Add(network, (content, button));
        }

        connect.Click += (_, _) => _ = ConnectSelected();
        password.PasswordChanged += (_, _) => UpdateConnectButton();
        password.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || !connect.IsEnabled) return;
            e.Handled = true;
            _ = ConnectSelected();
        };
        UpdateRows();

        void Select(DockWireless.Network network)
        {
            if (wirelessBusy || ReferenceEquals(network, selected)) return;
            expandedRow?.Children.Remove(editor);
            expandedRow = null;
            password.Clear();
            SetConnectionMessage("");
            remember.IsChecked = false;
            selected = network;
            UpdateRows();
            if (!network.Connected)
            {
                var needsPassword = network.Access == DockWireless.NetworkAccess.Password;
                label.Visibility = password.Visibility = remember.Visibility = hint.Visibility =
                    needsPassword ? Visibility.Visible : Visibility.Collapsed;
                if (needsPassword)
                {
                    try
                    {
                        var saved = WifiCredentials.Read(network);
                        if (!string.IsNullOrEmpty(saved))
                        {
                            password.Password = saved;
                            remember.IsChecked = true;
                        }
                    }
                    catch (Win32Exception ex)
                    {
                        LogCredentialError("read", ex);
                        SetConnectionMessage("无法读取已保存的密码，请手动输入。");
                    }
                }
                connect.Content = network.Access == DockWireless.NetworkAccess.WindowsSettings ? "在系统设置中连接" : "连接";
                expandedRow = rows[network].Content;
                expandedRow.Children.Add(editor);
                // Keep the newly expanded controls reachable even for the last visible row.
                _ = Dispatcher.InvokeAsync(() =>
                {
                    if (version == panelVersion && ReferenceEquals(network, selected)) editor.BringIntoView();
                }, System.Windows.Threading.DispatcherPriority.Loaded);
            }
            UpdateConnectButton();
        }

        void UpdateRows()
        {
            foreach (var (network, row) in rows)
            {
                var isSelected = ReferenceEquals(network, selected);
                var status = network.Connected ? "已连接" : network.Access switch
                {
                    DockWireless.NetworkAccess.Open => "开放网络",
                    DockWireless.NetworkAccess.Password => "需要密码",
                    _ => "系统认证"
                };
                var description = $"{status} · 信号 {network.Signal}/5";
                row.Select.Content = DeviceRow(network.Name, description, "", network.Connected, network.Signal, isSelected);
                row.Select.Background = PanelSurface(network.Connected || isSelected);
                row.Select.BorderBrush = ControlBrush(isSelected ? "#8DDCEA" : "#40596A79");
                AutomationProperties.SetItemStatus(row.Select, isSelected ? $"已选中，{description}" : description);
            }
        }

        void SetConnectionMessage(string text)
        {
            message.Text = text;
            message.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        void UpdateConnectButton()
        {
            using var secret = password.SecurePassword;
            connect.IsEnabled = !wirelessBusy && selected is { Connected: false } network &&
                (network.Access != DockWireless.NetworkAccess.Password || secret.Length > 0);
        }

        async Task ConnectSelected()
        {
            if (!connect.IsEnabled || wirelessBusy || selected is not { Connected: false } network) return;
            if (network.Access == DockWireless.NetworkAccess.WindowsSettings)
            {
                CloseSystemPanel();
                controller.Launch("ms-settings:network-wifi");
                return;
            }

            wirelessBusy = true;
            list.IsEnabled = false;
            UpdateConnectButton();
            var entered = network.Access == DockWireless.NetworkAccess.Password ? password.Password : null;
            var savePassword = remember.IsChecked == true;
            password.Clear();
            SetConnectionMessage("正在连接…");
            var connected = false;
            string? credentialWarning = null;
            try
            {
                var result = await DockWireless.Connect(network, entered);
                connected = result == WiFiConnectionStatus.Success;
                if (connected && entered != null)
                {
                    try { WifiCredentials.Update(network, savePassword ? entered : null); }
                    catch (Win32Exception ex)
                    {
                        LogCredentialError(savePassword ? "save" : "remove", ex);
                        credentialWarning = savePassword ? "已连接，但无法保存密码。" : "已连接，但未能移除已保存的密码。";
                    }
                }
                if (closed || version != panelVersion) return;
                SetConnectionMessage(result switch
                {
                    WiFiConnectionStatus.Success => "已连接",
                    WiFiConnectionStatus.InvalidCredential => "密码不正确，请重新输入。",
                    WiFiConnectionStatus.NetworkNotAvailable => "网络已不可用，请刷新列表。",
                    WiFiConnectionStatus.AccessRevoked => "系统拒绝访问，请检查 Wi-Fi 和位置访问权限。",
                    WiFiConnectionStatus.UnsupportedAuthenticationProtocol => "请在 Windows Wi-Fi 设置中完成认证。",
                    _ => "未能连接，请重试。"
                });
            }
            catch (TimeoutException) { if (version == panelVersion) SetConnectionMessage("连接超时，请重试。"); }
            catch (Exception ex) when (WirelessError(ex))
            { if (version == panelVersion) SetConnectionMessage("连接失败，请重试或打开更多 Wi-Fi 设置。"); }
            finally
            {
                wirelessBusy = false;
                if (!closed && version == panelVersion)
                {
                    list.IsEnabled = true;
                    UpdateConnectButton();
                }
            }
            if (connected && !closed && version == panelVersion)
            {
                var refreshed = ShowRadioDetails(RadioKind.WiFi);
                var refreshedVersion = panelVersion;
                await refreshed;
                if (credentialWarning != null && !closed && panelVersion == refreshedVersion)
                    SetPanelMessage(credentialWarning);
            }
        }
    }

    private static void LogCredentialError(string step, Win32Exception error) =>
        DeskIsle.Services.StartupDiagnostics.Mark($"wireless wifi-credential-{step} error={error.NativeErrorCode}");
}
