using System.Windows;
using System.Windows.Input;
namespace DeskIsle.Dock;

internal partial class DockSettingsWindow : Window
{
    private readonly DockController controller;
    private DeskIsle.Services.LoginStartup.Status? loginStatus;
    private bool repairingLogin;
    internal DockSettingsWindow(DockController controller)
    {
        this.controller = controller;
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height - 24;
        EnabledToggle.IsChecked = controller.Settings.Enabled;
        RevealSlider.Value = controller.Settings.RevealMs;
        HideSlider.Value = controller.Settings.HideMs;
        OpacitySlider.Value = controller.Settings.Opacity;
        HotKeyEditor.Text = ((char)controller.Settings.HotKey).ToString();
        LoginToggle.IsEnabled = false;
        Loaded += LoadLoginSetting;
        UpdateValues();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
    private async void LoadLoginSetting(object sender, RoutedEventArgs e)
    {
        Loaded -= LoadLoginSetting;
        try
        {
            ApplyLoginStatus(await Task.Run(DeskIsle.Services.LoginStartup.ReadStatus));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or System.IO.IOException or System.Xml.XmlException)
        { ErrorText.Text = "无法读取登录启动设置：" + ex.Message; ErrorText.Visibility = Visibility.Visible; }
    }
    private void ApplyLoginStatus(DeskIsle.Services.LoginStartup.Status status)
    {
        loginStatus = status;
        LoginToggle.IsChecked = status.Enabled && status.Issue == null;
        LoginToggle.IsEnabled = true;
        LoginIssue.Text = status.Issue ?? "";
        LoginIssue.Visibility = RepairLoginButton.Visibility = status.Issue == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void RepairLoginClick(object sender, RoutedEventArgs e)
    {
        if (repairingLogin) return;
        repairingLogin = true;
        LoginToggle.IsEnabled = RepairLoginButton.IsEnabled = false;
        try
        {
            await Task.Run(() => DeskIsle.Services.LoginStartup.Set(true));
            ApplyLoginStatus(await Task.Run(DeskIsle.Services.LoginStartup.ReadStatus));
            ErrorText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or System.IO.IOException or System.Xml.XmlException)
        { ErrorText.Text = "自启动修复未完成：" + ex.Message; ErrorText.Visibility = Visibility.Visible; }
        finally
        {
            repairingLogin = false;
            LoginToggle.IsEnabled = RepairLoginButton.IsEnabled = true;
        }
    }
    private void ValuesChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateValues();
    private void UpdateValues()
    {
        if (RevealValue == null || HideValue == null || OpacityValue == null || RevealSlider == null || HideSlider == null || OpacitySlider == null) return;
        RevealValue.Text = $"{RevealSlider.Value:0} ms";
        HideValue.Text = $"{HideSlider.Value:0} ms";
        OpacityValue.Text = $"{OpacitySlider.Value:P0}";
    }
    private void DragHeading(object sender, MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node != null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
            if (node is System.Windows.Controls.Primitives.ButtonBase) return;
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void CancelClick(object sender, RoutedEventArgs e) => Close();
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (repairingLogin) return;
        var text = HotKeyEditor.Text.Trim().ToUpperInvariant();
        if (text.Length != 1 || text[0] < 'A' || text[0] > 'Z')
        { ErrorText.Text = "快捷键请输入 A 到 Z 的一个字母。"; ErrorText.Visibility = Visibility.Visible; HotKeyEditor.Focus(); return; }
        controller.Settings.RevealMs = (int)RevealSlider.Value;
        controller.Settings.HideMs = (int)HideSlider.Value;
        controller.Settings.Opacity = OpacitySlider.Value;
        controller.Settings.HotKey = text[0];
        try
        {
            if (loginStatus is { } status && (LoginToggle.IsChecked == true
                ? !status.Enabled || status.Issue != null
                : status.Enabled && status.Issue == null))
            {
                DeskIsle.Services.LoginStartup.Set(LoginToggle.IsChecked == true);
                ApplyLoginStatus(DeskIsle.Services.LoginStartup.ReadStatus());
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or System.IO.IOException or System.Xml.XmlException)
        { ErrorText.Text = "登录启动设置未完成：" + ex.Message; ErrorText.Visibility = Visibility.Visible; return; }
        if (controller.Apply(EnabledToggle.IsChecked == true)) Close();
    }
}
