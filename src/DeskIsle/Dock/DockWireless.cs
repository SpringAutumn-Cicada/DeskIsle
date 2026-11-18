using System.Diagnostics;
using Windows.Foundation;
using Windows.Devices.Radios;
using Windows.Devices.WiFi;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
namespace DeskIsle.Dock;

internal static class DockWireless
{
    private static async Task<T> Observe<T>(string step, Func<Task<T>> start, int seconds)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await start().WaitAsync(TimeSpan.FromSeconds(seconds));
        }
        catch (Exception ex)
        {
            DeskIsle.Services.StartupDiagnostics.Mark($"wireless {step} elapsed={stopwatch.ElapsedMilliseconds}ms {ex.GetType().Name} hr=0x{ex.HResult:X8}");
            throw;
        }
    }

    private static async Task<T> Await<T>(string step, Func<IAsyncOperation<T>> start, int seconds = 4)
    {
        IAsyncOperation<T>? operation = null;
        try { return await Observe(step, () => (operation = start()).AsTask(), seconds); }
        catch (TimeoutException)
        {
            try { operation?.Cancel(); }
            catch (Exception ex) { DeskIsle.Services.StartupDiagnostics.Mark($"wireless {step} cancel hr=0x{ex.HResult:X8}"); }
            throw;
        }
    }

    internal static Task<IReadOnlyList<Radio>> ReadRadios() => Await("enumerate-radios", Radio.GetRadiosAsync);

    internal sealed record State(bool Available, bool On, bool Locked, string Label);
    internal sealed record Network(WiFiAdapter Adapter, WiFiAvailableNetwork Source, string Name, byte Signal, bool Connected);
    internal sealed record Device(string Name, bool Connected);
    internal static async Task<State> Read(RadioKind kind, bool connection = false, Task<IReadOnlyList<Radio>>? snapshot = null)
    {
        var radios = (await (snapshot ?? ReadRadios())).Where(r => r.Kind == kind).ToArray();
        var on = radios.Any(r => r.State == RadioState.On);
        var connected = false;
        if (connection && on)
        {
            if (kind == RadioKind.WiFi)
                connected = await Observe("wifi-connection-status", () => Task.Run(() => Windows.Networking.Connectivity.NetworkInformation.GetConnectionProfiles().Any(p =>
                    p.IsWlanConnectionProfile && p.GetNetworkConnectivityLevel() != Windows.Networking.Connectivity.NetworkConnectivityLevel.None)), 4);
            else if (kind == RadioKind.Bluetooth)
            {
                var selectors = new[] { BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
                    BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected) };
                foreach (var selector in selectors)
                    if ((await Await("bluetooth-connection-status", () => DeviceInformation.FindAllAsync(selector))).Count > 0)
                    { connected = true; break; }
            }
        }
        return new(radios.Length > 0, on, radios.Length > 0 && radios.All(r => r.State == RadioState.Disabled),
            radios.Length == 0 ? "无可用设备" : connected ? "已连接" : "未连接");
    }
    internal static async Task<string?> Set(RadioKind kind, bool enabled)
    {
        var access = await Await("radio-request-access", Radio.RequestAccessAsync);
        if (access != RadioAccessStatus.Allowed)
        {
            DeskIsle.Services.StartupDiagnostics.Mark($"wireless {kind} access={access}");
            return "系统未授权修改无线开关，请在系统设置中操作。";
        }
        var radios = (await ReadRadios()).Where(r => r.Kind == kind).ToArray();
        if (radios.Length == 0) return "没有可用的无线设备。";
        if (radios.All(radio => radio.State == RadioState.Disabled)) return "无线设备被硬件或系统策略禁用。";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var radio in radios.Where(radio => radio.State != RadioState.Disabled))
        {
            if (deadline.IsCancellationRequested) throw new TimeoutException();
            var status = await Observe($"{kind}-set-state", () =>
                SetRadioState(radio, enabled, deadline.Token), 5);
            if (status != RadioAccessStatus.Allowed)
            {
                DeskIsle.Services.StartupDiagnostics.Mark($"wireless {kind} set-state={status}");
                return "系统未接受开关操作，请检查硬件和权限。";
            }
        }
        var actual = await Read(kind);
        for (var attempt = 0; actual.On != enabled && attempt < 2; attempt++)
        { await Task.Delay(80); actual = await Read(kind); }
        return actual.On == enabled ? null : "无线状态尚未达到目标，请检查系统开关。";
    }
    private static async Task<RadioAccessStatus> SetRadioState(Radio radio, bool enabled, CancellationToken token)
    {
        try { return await radio.SetStateAsync(enabled ? RadioState.On : RadioState.Off).AsTask(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw new TimeoutException("无线切换超时。"); }
    }

    internal static async Task<Network[]> Networks()
    {
        if (await Await("wifi-request-access", WiFiAdapter.RequestAccessAsync) != WiFiAccessStatus.Allowed)
            throw new UnauthorizedAccessException("读取 Wi-Fi 列表需要系统无线与位置访问许可，请到系统 Wi-Fi 设置中查看。");
        var result = new List<Network>();
        foreach (var adapter in await Await("wifi-enumerate-adapters", WiFiAdapter.FindAllAdaptersAsync))
        {
            await Observe("wifi-scan", async () =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(11));
                await adapter.ScanAsync().AsTask(timeout.Token);
                return true;
            }, 12);
            var profile = await Await("wifi-connected-profile", adapter.NetworkAdapter.GetConnectedProfileAsync);
            var ssid = profile?.WlanConnectionProfileDetails?.GetConnectedSsid();
            foreach (var network in adapter.NetworkReport.AvailableNetworks.Where(n => !string.IsNullOrEmpty(n.Ssid)))
                result.Add(new(adapter, network, network.Ssid, network.SignalBars, network.Ssid == ssid));
        }
        return result.OrderByDescending(n => n.Connected).ThenByDescending(n => n.Signal).DistinctBy(n => n.Name).ToArray();
    }
    internal static async Task<bool> Connect(Network network)
    {
        var result = await Await("wifi-connect", () => network.Adapter.ConnectAsync(network.Source, WiFiReconnectionKind.Automatic), 15);
        return result.ConnectionStatus == WiFiConnectionStatus.Success;
    }
    internal static async Task<Device[]> Devices()
    {
        var selectors = new[] { BluetoothDevice.GetDeviceSelectorFromPairingState(true), BluetoothLEDevice.GetDeviceSelectorFromPairingState(true) };
        var result = new List<Device>();
        foreach (var selector in selectors)
            foreach (var device in await Await("bluetooth-paired-devices", () => DeviceInformation.FindAllAsync(selector, new[] { "System.Devices.Aep.IsConnected" }), 8))
                if (!string.IsNullOrWhiteSpace(device.Name))
                    result.Add(new(device.Name, device.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var value) && value is true));
        return result.DistinctBy(d => d.Name).OrderByDescending(d => d.Connected).ToArray();
    }
}
