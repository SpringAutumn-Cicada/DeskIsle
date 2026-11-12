using Windows.Devices.Radios;
using Windows.Devices.WiFi;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
namespace DeskIsle.Dock;

internal static class DockWireless
{
    internal sealed record State(bool Available, bool On, bool Locked, string Label);
    internal sealed record Network(WiFiAdapter Adapter, WiFiAvailableNetwork Source, string Name, byte Signal, bool Connected);
    internal sealed record Device(string Name, bool Connected);
    internal static async Task<State> Read(RadioKind kind)
    {
        var radios = (await Radio.GetRadiosAsync()).Where(r => r.Kind == kind).ToArray();
        return new(radios.Length > 0, radios.Any(r => r.State == RadioState.On), radios.Length > 0 && radios.All(r => r.State == RadioState.Disabled),
            radios.Length == 0 ? "无可用设备" : radios.Any(r => r.State == RadioState.On) ? "已开启" : "已关闭");
    }
    internal static async Task<string?> Set(RadioKind kind, bool enabled)
    {
        if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return "系统未授权修改无线开关，请在系统设置中操作。";
        var radios = (await Radio.GetRadiosAsync()).Where(r => r.Kind == kind).ToArray();
        if (radios.Length == 0) return "没有可用的无线设备。";
        foreach (var radio in radios)
        {
            if (radio.State == RadioState.Disabled) return "无线设备被硬件或系统策略禁用。";
            if (await radio.SetStateAsync(enabled ? RadioState.On : RadioState.Off) != RadioAccessStatus.Allowed) return "系统未接受开关操作，请检查硬件和权限。";
        }
        return null;
    }
    internal static async Task<Network[]> Networks()
    {
        if (await WiFiAdapter.RequestAccessAsync() != WiFiAccessStatus.Allowed)
            throw new UnauthorizedAccessException("读取 Wi-Fi 列表需要系统无线与位置访问许可，请到系统 Wi-Fi 设置中查看。");
        var result = new List<Network>();
        foreach (var adapter in await WiFiAdapter.FindAllAdaptersAsync())
        {
            await adapter.ScanAsync();
            var profile = await adapter.NetworkAdapter.GetConnectedProfileAsync();
            var ssid = profile?.WlanConnectionProfileDetails?.GetConnectedSsid();
            foreach (var network in adapter.NetworkReport.AvailableNetworks.Where(n => !string.IsNullOrEmpty(n.Ssid)))
                result.Add(new(adapter, network, network.Ssid, network.SignalBars, network.Ssid == ssid));
        }
        return result.OrderByDescending(n => n.Connected).ThenByDescending(n => n.Signal).DistinctBy(n => n.Name).ToArray();
    }
    internal static async Task<bool> Connect(Network network)
    {
        var result = await network.Adapter.ConnectAsync(network.Source, WiFiReconnectionKind.Automatic);
        return result.ConnectionStatus == WiFiConnectionStatus.Success;
    }
    internal static async Task<Device[]> Devices()
    {
        var selectors = new[] { BluetoothDevice.GetDeviceSelectorFromPairingState(true), BluetoothLEDevice.GetDeviceSelectorFromPairingState(true) };
        var result = new List<Device>();
        foreach (var selector in selectors)
            foreach (var device in await DeviceInformation.FindAllAsync(selector, new[] { "System.Devices.Aep.IsConnected" }))
                if (!string.IsNullOrWhiteSpace(device.Name))
                    result.Add(new(device.Name, device.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var value) && value is true));
        return result.DistinctBy(d => d.Name).OrderByDescending(d => d.Connected).ToArray();
    }
}
