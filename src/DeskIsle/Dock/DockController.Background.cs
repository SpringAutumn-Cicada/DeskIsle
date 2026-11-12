using System.Diagnostics;
using System.IO;
using System.Text;
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
            if (!DockNative.IsWindow(window.Handle) || !Uncloaked(window.Handle) || DockNative.GetWindow(window.Handle, 4) != 0) continue;
            DockNative.GetWindowThreadProcessId(window.Handle, out var pid);
            if (ids.Contains(pid)) { handle = window.Handle; owner = pid; break; }
        }
        // WeChat recreates its main window after closing to the tray. Do not reveal arbitrary Qt/helper windows.
        if (handle == 0 && ids.Count > 0 && Path.GetFileNameWithoutExtension(pin.Executable).ToLowerInvariant() is "weixin" or "wechat")
        {
            DockNative.EnumWindows((window, _) =>
            {
                DockNative.GetWindowThreadProcessId(window, out var pid);
                if (!ids.Contains(pid) || !Uncloaked(window) || DockNative.GetWindow(window, 4) != 0 || (DockNative.GetWindowLong(window, -20) & 0x80) != 0) return true;
                var title = new StringBuilder(256); DockNative.GetWindowText(window, title, title.Capacity);
                var text = title.ToString().Trim();
                var cls = DockNative.Class(window);
                if ((text is "微信" or "WeChat" or "Weixin") && (cls.StartsWith("Qt", StringComparison.Ordinal) || cls.StartsWith("WeChat", StringComparison.Ordinal)))
                { handle = window; owner = pid; return false; }
                return true;
            }, 0);
        }
        return new(ids.Count > 0, uncertain, handle, owner);
    }
    internal async void OpenPinned(DockPin pin)
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
                if (pid == app.ProcessId)
                {
                    DockNative.ShowWindowAsync(app.Handle, DockNative.IsIconic(app.Handle) ? 9 : 5);
                    DockNative.SetForegroundWindow(app.Handle);
                    settle.Start(); return;
                }
            }
            if (app.Running || app.Uncertain)
            {
                MessageBox.Show("应用已在后台运行，或当前权限无法确认其主窗口。请使用上层 Dock 的后台应用入口，打开原生托盘恢复。需要另开实例时，可右键选择“打开新窗口”。", "DeskIsle Dock");
                return;
            }
            Launch(pin.Path);
        }
        finally { opening.Remove(key); }
    }
    internal async Task<DockPin[]> BackgroundApps()
    {
        var candidates = knownApplications.Values.Where(p => !windows.Any(w => string.Equals(w.Executable, p.Executable, StringComparison.OrdinalIgnoreCase))).ToArray();
        return await Task.Run(() => candidates.Where(p => FindRunning(p, []).Running).ToArray());
    }
}
