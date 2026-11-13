using System.Runtime.InteropServices;
namespace DeskIsle.Dock;

internal sealed partial class DockController
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool RegisterShellHookWindow(nint window);
    [DllImport("user32.dll")] private static extern bool DeregisterShellHookWindow(nint window);
    private uint shellMessage;
    private bool shellRegistered;
    private readonly HashSet<nint> attentionWindows = [];
    private readonly Dictionary<nint, string> attentionKeys = [];
    private readonly Dictionary<string, long> attentionStarted = new(StringComparer.OrdinalIgnoreCase);

    private void RegisterAttention()
    {
        if (messages == null) return;
        shellMessage = RegisterWindowMessage("SHELLHOOK");
        shellRegistered = shellMessage != 0 && RegisterShellHookWindow(messages.Handle);
        if (!shellRegistered)
            System.Windows.MessageBox.Show("系统窗口提醒监听未能启用，Dock 仍可使用，但无法自动提示应用消息。", "DeskIsle Dock");
    }
    private void HandleAttentionMessage(int message, nint code, nint window)
    {
        if (!shellRegistered || (uint)message != shellMessage || disposed || lease == null) return;
        if (code == 0x8006 && DockNative.IsWindow(window)) // HSHELL_FLASH, not ordinary redraw.
        {
            DockNative.GetWindowThreadProcessId(window, out var pid);
            if (pid == Environment.ProcessId || window == DockNative.GetForegroundWindow()) return;
            UpdateAttention();
            var app = windows.FirstOrDefault(w => w.Handle == window);
            var key = app?.Executable;
            if (key == null)
            {
                try { using var process = System.Diagnostics.Process.GetProcessById((int)pid); key = process.MainModule?.FileName; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException) { }
            }
            key ??= $"process:{pid}";
            var foregroundApp = windows.FirstOrDefault(w => w.Handle == DockNative.GetForegroundWindow());
            if (foregroundApp?.Executable != null && string.Equals(key, foregroundApp.Executable, StringComparison.OrdinalIgnoreCase)) return;
            attentionWindows.Add(window);
            attentionKeys[window] = key;
            var now = Environment.TickCount64;
            if (!attentionStarted.ContainsKey(key))
            {
                attentionStarted[key] = now;
                DeskIsle.Services.StartupDiagnostics.Mark($"attention-pending pid={pid} native={lease.NativeMode}");
                // A manually selected native taskbar remains in charge until the user returns.
                if (!lease.NativeMode) Reveal();
            }
            UpdateAttention();
            if (!settle.IsEnabled) settle.Start();
        }
        else if (code == 2) attentionWindows.Remove(window); // HSHELL_WINDOWDESTROYED
    }
    private void UpdateAttention()
    {
        var foreground = DockNative.GetForegroundWindow();
        var active = windows.FirstOrDefault(w => w.Handle == foreground);
        DockNative.GetWindowThreadProcessId(foreground, out var foregroundPid);
        var cleared = attentionWindows.RemoveWhere(handle =>
        {
            if (!DockNative.IsWindow(handle)) return true;
            // A failed Dock click never clears state: the application must actually be foreground.
            if (DockNative.IsIconic(foreground)) return false;
            DockNative.GetWindowThreadProcessId(handle, out var pid);
            return handle == foreground || (foregroundPid != 0 && pid == foregroundPid) ||
                (active?.Executable != null && attentionKeys.TryGetValue(handle, out var key) &&
                 string.Equals(key, active.Executable, StringComparison.OrdinalIgnoreCase));
        });
        if (cleared > 0) DeskIsle.Services.StartupDiagnostics.Mark($"attention-cleared count={cleared} remaining={attentionWindows.Count}");
        foreach (var handle in attentionKeys.Keys.Where(handle => !attentionWindows.Contains(handle)).ToArray()) attentionKeys.Remove(handle);
        var pendingApps = attentionKeys.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in attentionStarted.Keys.Where(key => !pendingApps.Contains(key)).ToArray()) attentionStarted.Remove(key);
        var strong = attentionWindows.Where(handle => attentionKeys.TryGetValue(handle, out var key)
            && attentionStarted.TryGetValue(key, out var started) && Environment.TickCount64 - started < 5000).ToArray();
        bar?.SetAttention(attentionWindows, strong);
    }
    private void StopAttention()
    {
        if (shellRegistered && messages != null) DeregisterShellHookWindow(messages.Handle);
        shellRegistered = false; shellMessage = 0;
        attentionWindows.Clear(); attentionKeys.Clear(); attentionStarted.Clear();
    }
}
