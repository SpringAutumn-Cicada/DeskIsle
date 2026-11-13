using System.Runtime.InteropServices;
using System.Windows;
using DeskIsle.Services;
namespace DeskIsle.Dock;

internal static class WindowActivation
{
    private static readonly HashSet<nint> pending = [];
    [DllImport("user32.dll")] private static extern nint GetLastActivePopup(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll")] private static extern bool GetTokenInformation(nint token, int kind, out int value, int size, out int returned);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    private static string Elevated(uint pid)
    {
        var process = OpenProcess(0x1000, false, pid);
        if (process == 0) return "unknown";
        nint token = 0;
        try { return OpenProcessToken(process, 8, out token) && GetTokenInformation(token, 20, out var value, 4, out _) ? (value != 0 ? "yes" : "no") : "unknown"; }
        finally { if (token != 0) CloseHandle(token); CloseHandle(process); }
    }
    internal static async Task Restore(nint handle)
    {
        if (!DockNative.IsWindow(handle) || !pending.Add(handle)) return;
        try
        {
            var target = GetLastActivePopup(handle); // Last active owned dialog may be the usable window.
            if (DockNative.IsWindowEnabled(handle)) target = handle;
            if (target == 0 || !DockNative.IsWindowEnabled(target)) target = handle;
            DockNative.GetWindowThreadProcessId(target, out var pid);
            StartupDiagnostics.Mark($"activate hwnd={target} elevated={Elevated(pid)} self-elevated={Elevated((uint)Environment.ProcessId)} minimized={DockNative.IsIconic(target)}");
            if (DockNative.IsIconic(target))
            {
                var sent = ShowWindowAsync(target, 9);
                StartupDiagnostics.Mark($"restore-request accepted={sent} error={Marshal.GetLastWin32Error()}");
            }
            var activated = DockNative.SetForegroundWindow(target);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await Task.Delay(120);
                if (!DockNative.IsWindow(target)) return;
                if (!DockNative.IsIconic(target) && DockNative.GetForegroundWindow() == target) return;
                if (DockNative.IsIconic(target) && attempt == 0)
                {
                    var sent = PostMessage(target, 0x0112, 0xF120, 0); // SC_RESTORE, bounded asynchronous fallback.
                    StartupDiagnostics.Mark($"restore-fallback accepted={sent} error={Marshal.GetLastWin32Error()}");
                }
                // Do not steal focus from an application the user selected during the wait.
                if (DockNative.IsIconic(target)) continue;
                break;
            }
            StartupDiagnostics.Mark($"activate-result accepted={activated} minimized={DockNative.IsIconic(target)} foreground={DockNative.GetForegroundWindow() == target}");
            if (DockNative.IsIconic(target) || DockNative.GetForegroundWindow() != target)
                MessageBox.Show("未能将此窗口恢复到前台。若该程序以管理员权限运行，Windows 可能限制普通权限 Dock 控制它；请先通过原生任务栏恢复。", "DeskIsle Dock");
        }
        finally { pending.Remove(handle); }
    }
}
