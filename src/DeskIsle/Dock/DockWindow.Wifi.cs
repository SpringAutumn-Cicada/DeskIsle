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
    private sealed class WifiSnapshot(DockWireless.Network[] networks)
    {
        internal DockWireless.Network[] Networks { get; } = networks;
        internal HashSet<DockWireless.Network> Connected { get; } = networks.Where(network => network.Connected).ToHashSet();
        internal long UpdatedAt { get; set; } = Environment.TickCount64;

        internal void UpdateConnection(WiFiAdapter adapter, string? ssid)
        {
            Connected.RemoveWhere(item => ReferenceEquals(item.Adapter, adapter));
            foreach (var item in Networks)
                if (ReferenceEquals(item.Adapter, adapter) && item.Name == ssid)
                    Connected.Add(item);
        }
    }

    private WifiSnapshot? wifiSnapshot;
    private DockWireless.Network? wifiPendingNetwork;
    private string? wifiPendingMessage;
    private (DockWireless.Network Network, string Message, bool InvalidPassword)? wifiFeedback;
    private Action<DockWireless.Network, bool, string>? wifiConnectionCompleted;

    private void ClearWifiSnapshot()
    {
        wifiSnapshot = null;
        wifiFeedback = null;
    }

    private void PopulateWifiNetworks(StackPanel list, WifiSnapshot snapshot, long version, Action<bool> setRadioControlsEnabled)
    {
        var networks = snapshot.Networks;
        var connected = snapshot.Connected;
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
        DockPanel.SetDock(remember, System.Windows.Controls.Dock.Left);
        footer.Children.Add(remember);
        var hint = ControlText("下次连接时自动填入", 10);
        hint.Foreground = ControlBrush("#A8BFCD");
        hint.Margin = new Thickness(8, 0, 6, 0);
        hint.TextWrapping = TextWrapping.NoWrap;
        hint.TextTrimming = TextTrimming.CharacterEllipsis;
        footer.Children.Add(hint);
        editor.Children.Add(footer);

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

        connect.Click += (_, _) => _ = ChangeConnection();
        password.PasswordChanged += (_, _) => UpdateActionButton();
        password.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || !connect.IsEnabled) return;
            e.Handled = true;
            _ = ChangeConnection();
        };
        UpdateRows();
        wifiConnectionCompleted = (network, connectionChanged, feedback) =>
        {
            UpdateRows();
            if (ReferenceEquals(network, selected))
            {
                SetConnectionMessage(feedback);
                if (connectionChanged) UpdateEditor(network);
            }
            SetBusy(false);
        };

        if (wifiPendingNetwork is { } pending && rows.ContainsKey(pending))
            Select(pending, restoring: true, feedback: wifiPendingMessage, skipSavedPassword: true);
        else if (wifiFeedback is { } last && rows.ContainsKey(last.Network))
            Select(last.Network, restoring: true, feedback: last.Message, skipSavedPassword: last.InvalidPassword);
        SetBusy(wirelessBusy);

        void Select(DockWireless.Network network, bool restoring = false, string? feedback = null, bool skipSavedPassword = false)
        {
            if ((!restoring && wirelessBusy) || ReferenceEquals(network, selected)) return;
            if (!restoring) wifiFeedback = null;
            expandedRow?.Children.Remove(editor);
            selected = network;
            UpdateRows();
            SetConnectionMessage("");
            UpdateEditor(network, skipSavedPassword);
            if (!string.IsNullOrEmpty(feedback)) SetConnectionMessage(feedback);
            expandedRow = rows[network].Content;
            expandedRow.Children.Add(editor);
            // Keep the newly expanded controls reachable even for the last visible row.
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (version == panelVersion && ReferenceEquals(network, selected)) editor.BringIntoView();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        void UpdateEditor(DockWireless.Network network, bool skipSavedPassword = false)
        {
            var isConnected = connected.Contains(network);
            var needsPassword = !isConnected && network.Access == DockWireless.NetworkAccess.Password;
            password.Clear();
            remember.IsChecked = false;
            label.Visibility = password.Visibility = remember.Visibility = hint.Visibility =
                needsPassword ? Visibility.Visible : Visibility.Collapsed;
            if (needsPassword && !skipSavedPassword)
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
            connect.Content = isConnected ? "断开连接" :
                network.Access == DockWireless.NetworkAccess.WindowsSettings ? "在系统设置中连接" : "连接";
            AutomationProperties.SetName(connect, $"{connect.Content} {network.Name}");
            UpdateActionButton();
        }

        void UpdateRows()
        {
            foreach (var (network, row) in rows)
            {
                var isSelected = ReferenceEquals(network, selected);
                var isConnected = connected.Contains(network);
                var status = isConnected ? "已连接" : network.Access switch
                {
                    DockWireless.NetworkAccess.Open => "开放网络",
                    DockWireless.NetworkAccess.Password => "需要密码",
                    _ => "系统认证"
                };
                var description = $"{status} · 信号 {network.Signal}/5";
                row.Select.Content = DeviceRow(network.Name, description, "", isConnected, network.Signal, isSelected);
                row.Select.Background = PanelSurface(isConnected || isSelected);
                row.Select.BorderBrush = ControlBrush(isSelected ? "#8DDCEA" : "#40596A79");
                AutomationProperties.SetItemStatus(row.Select, isSelected ? $"已选中，{description}" : description);
            }
        }

        void SetConnectionMessage(string text)
        {
            message.Text = text;
            message.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        void UpdateActionButton()
        {
            using var secret = password.SecurePassword;
            connect.IsEnabled = !wirelessBusy && selected is { } network &&
                (connected.Contains(network) || network.Access != DockWireless.NetworkAccess.Password || secret.Length > 0);
        }

        void SetBusy(bool busy)
        {
            list.IsEnabled = !busy;
            setRadioControlsEnabled(!busy);
            UpdateActionButton();
        }

        async Task ChangeConnection()
        {
            if (!connect.IsEnabled || wirelessBusy || selected is not { } network) return;
            var disconnecting = connected.Contains(network);
            if (!disconnecting && network.Access == DockWireless.NetworkAccess.WindowsSettings)
            {
                CloseSystemPanel();
                controller.Launch("ms-settings:network-wifi");
                return;
            }

            var entered = !disconnecting && network.Access == DockWireless.NetworkAccess.Password ? password.Password : null;
            var savePassword = remember.IsChecked == true;
            wirelessBusy = true;
            ++wirelessRevision;
            wifiFeedback = null;
            wifiPendingNetwork = network;
            wifiPendingMessage = disconnecting ? "正在断开…" : "正在连接…";
            SetBusy(true);
            SetConnectionMessage(wifiPendingMessage);
            var connectionChanged = false;
            var invalidPassword = false;
            var feedback = "";
            try
            {
                if (disconnecting)
                {
                    var ssid = await DockWireless.Disconnect(network);
                    snapshot.UpdateConnection(network.Adapter, ssid);
                    connectionChanged = ssid != network.Name;
                    if (!connectionChanged) feedback = "未能断开连接，请重试。";
                    return;
                }

                var result = await DockWireless.Connect(network, entered);
                if (result == WiFiConnectionStatus.Success)
                {
                    snapshot.UpdateConnection(network.Adapter, network.Name);
                    connectionChanged = true;
                    if (entered != null)
                    {
                        try { WifiCredentials.Update(network, savePassword ? entered : null); }
                        catch (Win32Exception ex)
                        {
                            LogCredentialError(savePassword ? "save" : "remove", ex);
                            feedback = savePassword ? "已连接，但无法保存密码。" : "已连接，但未能移除已保存的密码。";
                        }
                    }
                    return;
                }
                invalidPassword = result == WiFiConnectionStatus.InvalidCredential;
                feedback = result switch
                {
                    WiFiConnectionStatus.InvalidCredential => "密码不正确，请重新输入。",
                    WiFiConnectionStatus.NetworkNotAvailable => "网络已不可用，请刷新列表。",
                    WiFiConnectionStatus.AccessRevoked => "系统拒绝访问，请检查 Wi-Fi 和位置访问权限。",
                    WiFiConnectionStatus.UnsupportedAuthenticationProtocol => "请在 Windows Wi-Fi 设置中完成认证。",
                    _ => "未能连接，请重试。"
                };
            }
            catch (TimeoutException)
            {
                feedback = disconnecting ? "确认断开超时，请重试。" : "连接超时，请重试。";
            }
            catch (Exception ex) when (WirelessError(ex))
            {
                feedback = disconnecting ? "断开失败，请重试或打开更多 Wi-Fi 设置。" : "连接失败，请重试或打开更多 Wi-Fi 设置。";
            }
            finally
            {
                wirelessBusy = false;
                wifiPendingNetwork = null;
                wifiPendingMessage = null;
                snapshot.UpdatedAt = Environment.TickCount64;
                wifiFeedback = (network, feedback, invalidPassword);
                if (!closed)
                {
                    if (connectionChanged && radioStates.TryGetValue(RadioKind.WiFi, out var state))
                        radioStates[RadioKind.WiFi] = state with { Label = snapshot.Connected.Count > 0 ? "已连接" : "未连接" };
                    wifiConnectionCompleted?.Invoke(network, connectionChanged, feedback);
                    foreach (var kind in radioViews.Keys) ApplyRadioState(kind);
                    _ = RefreshRadioViews();
                }
            }
        }
    }

    private static void LogCredentialError(string step, Win32Exception error) =>
        DeskIsle.Services.StartupDiagnostics.Mark($"wireless wifi-credential-{step} error={error.NativeErrorCode}");
}
