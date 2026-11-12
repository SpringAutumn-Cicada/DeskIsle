using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace DeskIsle.Dock;

// Invoke the real Explorer-owned tray button so the application handles its own hidden state.
// Never reconstruct private tray callbacks or force-show an application's hidden HWND.
internal static class DockTrayActivation
{
    private static readonly SemaphoreSlim workerSlot = new(1);
    internal static async Task<bool> TryWeChat(nint[] taskbars, CancellationToken request)
    {
        if (!workerSlot.Wait(0)) return false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(request);
        cancellation.CancelAfter(TimeSpan.FromSeconds(2));
        var token = cancellation.Token;
        var work = Task.Run(async () =>
        {
            try
            {
                foreach (var hwnd in taskbars)
                {
                    if (token.IsCancellationRequested) return false;
                    var root = AutomationElement.FromHandle(hwnd);
                    if (root != null && InvokeNamed(root, IsWeChat, token)) return true;
                }
                if (InvokeOverflow(IsWeChat, token)) return true;
                foreach (var hwnd in taskbars)
                {
                    if (token.IsCancellationRequested) return false;
                    var root = AutomationElement.FromHandle(hwnd);
                    if (root == null || !InvokeNamed(root, IsChevron, token)) continue;
                    await Task.Delay(150, token).ConfigureAwait(false);
                    return InvokeOverflow(IsWeChat, token);
                }
                return false;
            }
            catch (Exception ex) when (ex is COMException or ElementNotAvailableException or ElementNotEnabledException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException) { return false; }
            finally { workerSlot.Release(); }
        });
        try { return await work.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { cancellation.Cancel(); return false; }
    }
    private static bool IsWeChat(string name)
    {
        foreach (var prefix in new[] { "微信", "WeChat", "Weixin" })
            if (name.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.Length > prefix.Length && " ,，\n\r(:：（".Contains(name[prefix.Length]))) return true;
        return false;
    }
    private static bool IsChevron(string name) => name.Trim() is "显示隐藏的图标" or "隐藏的图标菜单" or "Show hidden icons" or "Hidden icon menu";
    private static bool InvokeOverflow(Func<string, bool> matches, CancellationToken token)
    {
        // These shell class names vary by Windows release. Unknown layouts use manual fallback.
        var roots = AutomationElement.RootElement.FindAll(TreeScope.Children, new OrCondition(
            new PropertyCondition(AutomationElement.ClassNameProperty, "NotifyIconOverflowWindow"),
            new PropertyCondition(AutomationElement.ClassNameProperty, "TopLevelWindowForOverflowXamlIsland")));
        foreach (AutomationElement root in roots)
        {
            if (token.IsCancellationRequested) return false;
            if (InvokeNamed(root, matches, token)) return true;
        }
        return false;
    }
    private static bool InvokeNamed(AutomationElement root, Func<string, bool> matches, CancellationToken token)
    {
        var buttons = root.FindAll(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
            new PropertyCondition(AutomationElement.IsEnabledProperty, true)));
        foreach (AutomationElement button in buttons)
        {
            if (token.IsCancellationRequested) return false;
            if (!matches(button.Current.Name)) continue;
            if (token.IsCancellationRequested) return false;
            // Win11 tray providers do not consistently implement InvokePattern.
            // Use the real button's on-screen point, preserving the pointer if the user has not moved it.
            if (button.TryGetClickablePoint(out var point))
                return Click(point.X, point.Y, matches == (Func<string, bool>)IsWeChat);
            if (button.TryGetCurrentPattern(InvokePattern.Pattern, out var action))
            { ((InvokePattern)action).Invoke(); return true; }
        }
        return false;
    }
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct MouseInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(20)] public uint Flags;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, MouseInput[] inputs, int size);
    private static bool Click(double x, double y, bool twice)
    {
        if (!DockNative.GetCursorPos(out var original)) return false;
        int px = (int)Math.Round(x), py = (int)Math.Round(y);
        if (!SetCursorPos(px, py)) return false;
        var down = new MouseInput { Flags = 2 };
        var up = new MouseInput { Flags = 4 };
        var inputs = twice ? new[] { down, up, down, up } : new[] { down, up };
        try
        {
            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<MouseInput>());
            if (sent != inputs.Length) { SendInput(1, [up], Marshal.SizeOf<MouseInput>()); return false; }
            return true;
        }
        finally
        {
            if (DockNative.GetCursorPos(out var current) && current.X == px && current.Y == py)
                SetCursorPos(original.X, original.Y);
        }
    }

}
