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
                ShowBackgroundPreview(pin);
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
    private void ShowBackgroundPreview(DockPin pin)
    {
        settle.Start();
        bar?.PreviewApplication(pin.Executable);
    }
}
