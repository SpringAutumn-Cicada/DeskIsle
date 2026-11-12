using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
namespace DeskIsle.Dock;

internal sealed partial class DockController
{
    // Only windows observed as real application windows can be restored generically.
    private readonly Dictionary<nint, DockWindow> knownWindows = [];
    private readonly Dictionary<string, DockPin> knownApplications = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> opening = new(StringComparer.OrdinalIgnoreCase);
    private void RememberApplications()
    {
        foreach (var window in windows)
        {
            knownWindows[window.Handle] = window;
            if (window.Executable is { } executable)
                knownApplications[executable] = new(executable, executable, Path.GetFileNameWithoutExtension(executable));
        }
        foreach (var stale in knownWindows.Keys.Where(h => !DockNative.IsWindow(h)).ToArray()) knownWindows.Remove(stale);
        foreach (var pin in pins)
            if (pin.Executable != null) knownApplications[pin.Executable] = pin;
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
                await RestoreThroughTray(pin);
                return;
            }
            if (launchIfMissing) Launch(pin.Path);
            else settle.Start();
        }
        catch (Exception ex) when (Recoverable(ex))
        { MessageBox.Show("应用恢复未完成，请从原生托盘打开：" + ex.Message, "DeskIsle Dock"); }
        finally { opening.Remove(key); }
    }
    private CancellationTokenSource? trayRestore;
    private async Task RestoreThroughTray(DockPin pin)
    {
        if (disposed || lease == null || trayRestore != null) return;
        var executable = pin.Executable;
        if (Path.GetFileNameWithoutExtension(executable ?? pin.Path).ToLowerInvariant() is not ("wechat" or "weixin"))
        {
            MessageBox.Show("应用仍在后台运行，暂时无法自动恢复其主窗口。", "DeskIsle Dock");
            return;
        }
        using var request = new CancellationTokenSource();
        trayRestore = request;
        try
        {
            // Ask WeChat's own startup/single-instance activation path to restore its UI.
            // Do not force-show an internal HWND or change the user's taskbar mode.
            if (executable == null || !File.Exists(executable)) return;
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true })?.Dispose();
            for (var attempt = 0; attempt < 15; attempt++)
            {
                await Task.Delay(200, request.Token);
                if (disposed || lease == null) return;
                var restored = await Task.Run(DockApps.Windows);
                if (request.IsCancellationRequested) return;
                var main = restored.FirstOrDefault(w => string.Equals(w.Executable, executable, StringComparison.OrdinalIgnoreCase) && DockNative.IsWindowEnabled(w.Handle));
                if (main == null) continue;
                if (DockNative.IsIconic(main.Handle)) DockNative.ShowWindowAsync(main.Handle, 9);
                DockNative.SetForegroundWindow(main.Handle);
                settle.Start(); return;
            }
            MessageBox.Show("已请求微信自行恢复，但尚未发现可操作的主窗口。当前微信版本可能未响应启动激活，请从微信自身托盘图标打开。", "DeskIsle Dock");
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(trayRestore, request)) trayRestore = null; }
    }
    private static readonly HashSet<string> TrayClients = new(StringComparer.OrdinalIgnoreCase)
    { "WeChat", "Weixin", "QQ", "TIM", "WXWork", "DingTalk", "Telegram", "OneDrive" };
    private static bool BackgroundCandidate(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)
            || name.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase)
            || name.Contains("crashpad", StringComparison.OrdinalIgnoreCase)) return false;
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return string.IsNullOrEmpty(systemDirectory) || !path.StartsWith(systemDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static string BackgroundName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Equals("WeChat", StringComparison.OrdinalIgnoreCase) || name.Equals("Weixin", StringComparison.OrdinalIgnoreCase)) return "微信";
        try { return FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } description ? description : name; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException) { return name; }
    }
    internal async Task<DockPin[]> BackgroundApps()
    {
        var candidates = knownApplications.Values.Where(p => p.Executable != null && BackgroundCandidate(p.Executable))
            .ToDictionary(p => p.Executable!, StringComparer.OrdinalIgnoreCase);
        var visible = windows.Where(w => w.Executable != null).Select(w => w.Executable!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = candidates.Keys.Select(path => Path.GetFileNameWithoutExtension(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = await Task.Run(() =>
        {
            var live = new Dictionary<string, DockPin>(StringComparer.OrdinalIgnoreCase);
            // One process snapshot per refresh; only inspect executable paths for known application names.
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (process.Id == Environment.ProcessId || (!names.Contains(process.ProcessName) && !TrayClients.Contains(process.ProcessName))) continue;
                        var path = process.MainModule?.FileName;
                        if (path == null || !BackgroundCandidate(path)) continue;
                        // Tray clients remain available while their main window is open, like the native notification area.
                        if (visible.Contains(path) && !TrayClients.Contains(process.ProcessName)) continue;
                        if (candidates.TryGetValue(path, out var pin)) live[path] = pin;
                        else if (TrayClients.Contains(process.ProcessName)) live[path] = new(path, path, BackgroundName(path));
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or ArgumentException) { }
                }
            }
            return live.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Executable, StringComparer.OrdinalIgnoreCase).ToArray();
        });
        if (disposed || lease == null) return [];
        foreach (var app in result) knownApplications[app.Executable!] = app;
        return result;
    }
}
