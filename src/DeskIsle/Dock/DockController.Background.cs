using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
namespace DeskIsle.Dock;

internal sealed partial class DockController
{
    // Only windows observed as real application windows can be restored generically.
    private readonly Dictionary<nint, DockWindow> knownWindows = [];
    private readonly HashSet<string> opening = new(StringComparer.OrdinalIgnoreCase);
    private void RememberApplications()
    {
        foreach (var window in windows)
        {
            knownWindows[window.Handle] = window;
        }
        foreach (var stale in knownWindows.Keys.Where(h => !DockNative.IsWindow(h)).ToArray()) knownWindows.Remove(stale);
    }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    private static bool Uncloaked(nint handle) => DwmGetWindowAttribute(handle, 14, out var value, sizeof(int)) != 0 || value == 0;
    private sealed record RunningApp(bool Running, bool Uncertain, nint Handle, uint ProcessId);
    private static RunningApp FindRunning(DockPin pin, DockWindow[] remembered)
    {
        if (pin.Executable == null) return new(false, false, 0, 0);
        var ids = new HashSet<uint>();
        var uncertain = false;
        try
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(pin.Executable)))
            {
                using (process)
                {
                    try
                    {
                        if (string.Equals(process.MainModule?.FileName, pin.Executable, StringComparison.OrdinalIgnoreCase)) ids.Add((uint)process.Id);
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { uncertain = true; }
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return new(false, true, 0, 0); }
        nint handle = 0; uint owner = 0;
        foreach (var window in remembered.Where(w => string.Equals(w.Executable, pin.Executable, StringComparison.OrdinalIgnoreCase)))
        {
            if (!DockNative.IsWindow(window.Handle) || !DockNative.IsWindowVisible(window.Handle) || !DockNative.IsWindowEnabled(window.Handle) || !Uncloaked(window.Handle) || DockNative.GetWindow(window.Handle, 4) != 0) continue;
            DockNative.GetWindowThreadProcessId(window.Handle, out var pid);
            if (ids.Contains(pid)) { handle = window.Handle; owner = pid; break; }
        }
        return new(ids.Count > 0, uncertain, handle, owner);
    }
    internal async void OpenPinned(DockPin pin, bool launchIfMissing = true)
    {
        var key = pin.Executable ?? pin.Path;
        if (!opening.Add(key)) return;
        try
        {
            var remembered = knownWindows.Values.ToArray();
            var app = await Task.Run(() => FindRunning(pin, remembered));
            if (disposed || lease == null) return;
            if (app.Handle != 0 && DockNative.IsWindow(app.Handle))
            {
                DockNative.GetWindowThreadProcessId(app.Handle, out var pid);
                if (pid == app.ProcessId && DockNative.IsWindowVisible(app.Handle) && DockNative.IsWindowEnabled(app.Handle))
                {
                    if (DockNative.IsIconic(app.Handle)) DockNative.ShowWindowAsync(app.Handle, 9);
                    DockNative.SetForegroundWindow(app.Handle);
                    settle.Start(); return;
                }
            }
            if (app.Running || app.Uncertain)
            {
                await RestoreBackground(pin);
                return;
            }
            if (launchIfMissing) Launch(pin.Path);
            else settle.Start();
        }
        catch (Exception ex) when (Recoverable(ex))
        { MessageBox.Show("应用恢复未完成，请从原生托盘打开：" + ex.Message, "DeskIsle Dock"); }
        finally { opening.Remove(key); }
    }
    private DockWindow[] PreviewWindows()
    {
        var current = windows.Select(w => w.Handle).ToHashSet();
        var hidden = knownWindows.Values.Where(w => !current.Contains(w.Handle) && DockNative.IsWindow(w.Handle)
            && !DockNative.IsWindowVisible(w.Handle) && Uncloaked(w.Handle) && SameProcess(w))
            .Select(w => w with { Background = true });
        return windows.Concat(hidden).ToArray();
    }
    private static bool SameProcess(DockWindow window)
    {
        try
        {
            DockNative.GetWindowThreadProcessId(window.Handle, out var pid);
            using var process = Process.GetProcessById((int)pid);
            return string.Equals(process.MainModule?.FileName, window.Executable, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException) { return false; }
    }
    private CancellationTokenSource? restoring;
    private async Task RestoreBackground(DockPin pin)
    {
        if (lease == null || restoring != null) return;
        if (Path.GetFileNameWithoutExtension(pin.Executable ?? pin.Path).ToLowerInvariant() is not ("wechat" or "weixin"))
        { bar?.PreviewApplication(pin.Executable); return; }
        using var request = new CancellationTokenSource();
        restoring = request;
        var currentLease = lease;
        try
        {
            bar?.HideAnimated(true);
            currentLease.SetTrayAccess(true);
            await Task.Delay(150, request.Token);
            if (disposed || request.IsCancellationRequested) return;
            var invoked = await DockTrayActivation.TryWeChat(DockNative.Taskbars(), request.Token);
            if (!invoked) { MessageBox.Show("未能找到可点击的微信托盘图标，未启动新实例。", "DeskIsle"); return; }
            for (int i = 0; i < 12; i++)
            {
                await Task.Delay(150, request.Token);
                var live = await Task.Run(DockApps.Windows);
                if (request.IsCancellationRequested || disposed) return;
                var window = live.FirstOrDefault(w => string.Equals(w.Executable, pin.Executable, StringComparison.OrdinalIgnoreCase) && DockNative.IsWindowEnabled(w.Handle));
                if (window == null) continue;
                if (DockNative.IsIconic(window.Handle)) DockNative.ShowWindowAsync(window.Handle, 9);
                DockNative.SetForegroundWindow(window.Handle); settle.Start(); return;
            }
            MessageBox.Show("微信托盘已响应点击，但尚未发现可操作的主窗口。", "DeskIsle");
        }
        catch (OperationCanceledException) { }
        finally
        {
            currentLease.SetTrayAccess(false);
            if (!disposed && ReferenceEquals(lease, currentLease) && !currentLease.NativeMode) Reveal();
            if (ReferenceEquals(restoring, request)) restoring = null;
        }
    }
}
