using System.Runtime.InteropServices;
namespace DeskIsle.Dock;

internal sealed partial class DockController
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool RegisterShellHookWindow(nint window);
    [DllImport("user32.dll")] private static extern bool DeregisterShellHookWindow(nint window);
    private uint shellMessage;
    private bool shellRegistered;
    private long attentionUntil;
    private readonly HashSet<nint> attentionWindows = [];
    private readonly Dictionary<string, long> lastAttention = new(StringComparer.OrdinalIgnoreCase);

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
            attentionWindows.Add(window);
            var app = windows.FirstOrDefault(w => w.Handle == window);
            var key = app?.Executable ?? $"window:{window}";
            var now = Environment.TickCount64;
            if (!lastAttention.TryGetValue(key, out var last) || now - last >= 5000)
            {
                lastAttention[key] = now;
                attentionUntil = now + 5000;
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
        attentionWindows.RemoveWhere(handle => !DockNative.IsWindow(handle) || handle == foreground ||
            (active?.Executable != null && windows.Any(w => w.Handle == handle &&
                string.Equals(w.Executable, active.Executable, StringComparison.OrdinalIgnoreCase))));
        if (attentionWindows.Count == 0) attentionUntil = 0;
        foreach (var key in lastAttention.Where(pair => Environment.TickCount64 - pair.Value > 60000).Select(pair => pair.Key).ToArray()) lastAttention.Remove(key);
        bar?.SetAttention(attentionWindows);
    }
    private void StopAttention()
    {
        if (shellRegistered && messages != null) DeregisterShellHookWindow(messages.Handle);
        shellRegistered = false; shellMessage = 0; attentionUntil = 0;
        attentionWindows.Clear(); lastAttention.Clear();
    }
}
