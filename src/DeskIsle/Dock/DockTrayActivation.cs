using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace DeskIsle.Dock;

// Invoke the real Explorer-owned tray button so the application handles its own hidden state.
// Never reconstruct private tray callbacks or force-show an application's hidden HWND.
internal static class DockTrayActivation
{
    private static readonly SemaphoreSlim workerSlot = new(1);
    internal sealed record Candidate(string Key, string Name, string ClassName, int ProcessId, bool HasInvoke, bool HasPoint, int Score);
    internal sealed record Outcome(bool Invoked, string Stage, Candidate[] Candidates);
    private sealed record Target(AutomationElement Element, Candidate Info);
    internal static async Task<Outcome> TryActivate(nint[] taskbars, DockPin app, uint[] processIds, CancellationToken request, string? selection = null, bool invokeOnly = false)
    {
        if (!workerSlot.Wait(0)) return new(false, "worker-busy", []);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(request);
        cancellation.CancelAfter(TimeSpan.FromSeconds(4));
        var token = cancellation.Token;
        var work = Task.Run(async () =>
        {
            var targets = new List<Target>();
            try
            {
                var aliases = Aliases(app);
                foreach (var hwnd in taskbars)
                {
                    token.ThrowIfCancellationRequested();
                    var root = AutomationElement.FromHandle(hwnd);
                    if (root == null) continue;
                    // Restrict traversal to notification-area containers; never score taskbar application buttons.
                    var regions = root.FindAll(TreeScope.Descendants, new OrCondition(
                        new PropertyCondition(AutomationElement.ClassNameProperty, "TrayNotifyWnd"),
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemTrayFrame"),
                        new PropertyCondition(AutomationElement.ClassNameProperty, "SystemTray.SystemTrayFrame")));
                    foreach (AutomationElement region in regions) Collect(region, targets, aliases, processIds, token);
                }
                CollectOverflow(targets, aliases, processIds, token);
                var selected = Choose(targets, selection);
                if (selected == null)
                {
                    foreach (var hwnd in taskbars)
                    {
                        token.ThrowIfCancellationRequested();
                        var root = AutomationElement.FromHandle(hwnd);
                        // Expand only an exact accessible chevron name or the shell chevron ID.
                        var buttons = root?.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                        if (buttons == null) continue;
                        foreach (AutomationElement button in buttons)
                        {
                            var chevron = button.Current;
                            if ((!IsChevron(chevron.Name) && chevron.AutomationId != "SystemTrayChevron") || chevron.IsOffscreen) continue;
                            if (!Perform(button, token, false)) continue;
                            for (var attempt = 0; attempt < 8; attempt++)
                            {
                                await Task.Delay(100, token).ConfigureAwait(false);
                                CollectOverflow(targets, aliases, processIds, token);
                                if (Choose(targets, selection) != null) break;
                            }
                            break;
                        }
                    }
                    selected = Choose(targets, selection);
                }
                var candidates = targets.DistinctBy(t => t.Info.Key).Take(64).Select(t => t.Info).ToArray();
                if (selected == null) return new Outcome(false, targets.Count == 0 ? "no-accessible-tray-items" : "ownership-ambiguous", candidates);
                token.ThrowIfCancellationRequested();
                return new Outcome(Perform(selected.Element, token, true, invokeOnly), invokeOnly ? "tray-invoke-action" : "tray-double-click", candidates);
            }
            catch (Exception ex) when (ex is COMException or ElementNotAvailableException or ElementNotEnabledException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
            { return new Outcome(false, "uia-" + ex.GetType().Name, targets.Select(t => t.Info).Take(64).ToArray()); }
            finally { cancellation.Dispose(); workerSlot.Release(); }
        });
        try { return await work.WaitAsync(TimeSpan.FromSeconds(4)); }
        catch (TimeoutException)
        {
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
            return new(false, "uia-timeout", []);
        }
    }
    private static string[] Aliases(DockPin app)
    {
        var names = new List<string> { System.IO.Path.GetFileNameWithoutExtension(app.Executable ?? app.Path), app.Name };
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(app.Executable ?? app.Path);
            if (info.ProductName is { Length: > 0 } product) names.Add(product);
            if (info.FileDescription is { Length: > 0 } description) names.Add(description);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException) { }
        var executable = System.IO.Path.GetFileNameWithoutExtension(app.Executable ?? app.Path);
        if (executable.Equals("Weixin", StringComparison.OrdinalIgnoreCase) || executable.Equals("WeChat", StringComparison.OrdinalIgnoreCase))
            names.AddRange(["微信", "Weixin", "WeChat"]);
        return names.Where(n => n.Length >= 2 && n.Length <= 80).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static bool IsChevron(string name) => name.Trim() is "显示隐藏的图标" or "隐藏的图标菜单" or "Show hidden icons" or "Hidden icon menu";
    private static Target? Choose(List<Target> targets, string? selection)
    {
        var unique = targets.DistinctBy(t => t.Info.Key).ToArray();
        if (selection != null) return unique.SingleOrDefault(t => t.Info.Key == selection);
        // Explorer hosts notification buttons, so its PID cannot identify the application.
        // A single exact product/name match inside the tray is also a usable target.
        var owned = unique.Where(t => t.Info.Score >= 100).ToArray();
        if (owned.Length > 0) return owned.Length == 1 ? owned[0] : null;
        var named = unique.Where(t => t.Info.Score >= 50).ToArray();
        return named.Length == 1 ? named[0] : null;
    }
    private static void CollectOverflow(List<Target> targets, string[] aliases, uint[] pids, CancellationToken token)
    {
        var roots = AutomationElement.RootElement.FindAll(TreeScope.Children, new OrCondition(
            new PropertyCondition(AutomationElement.ClassNameProperty, "NotifyIconOverflowWindow"),
            new PropertyCondition(AutomationElement.ClassNameProperty, "TopLevelWindowForOverflowXamlIsland")));
        foreach (AutomationElement root in roots) Collect(root, targets, aliases, pids, token);
    }
    private static void Collect(AutomationElement root, List<Target> targets, string[] aliases, uint[] pids, CancellationToken token)
    {
        var elements = root.FindAll(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
            new PropertyCondition(AutomationElement.IsEnabledProperty, true),
            new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem))));
        foreach (AutomationElement element in elements)
        {
            token.ThrowIfCancellationRequested();
            var info = element.Current;
            if (string.IsNullOrWhiteSpace(info.Name) || IsChevron(info.Name)) continue;
            var score = pids.Contains((uint)info.ProcessId) ? 100 : aliases.Any(a => info.Name.Equals(a, StringComparison.OrdinalIgnoreCase) || NameMatches(info.Name, a)) ? 50 : 0;
            var key = DockRestoreDiagnostics.Hash(info.AutomationId + "|" + info.Name + "|" + info.ClassName + "|" + info.ProcessId);
            targets.Add(new(element, new(key, info.Name, info.ClassName, info.ProcessId,
                element.TryGetCurrentPattern(InvokePattern.Pattern, out _), element.TryGetClickablePoint(out _), score)));
        }
    }
    private static bool NameMatches(string name, string alias)
    {
        if (!name.StartsWith(alias, StringComparison.OrdinalIgnoreCase) || name.Length <= alias.Length) return false;
        var boundary = name[alias.Length];
        return char.IsWhiteSpace(boundary) || boundary is ':' or '：' or '(' or '（' or '[' or '【' or '-' or '·' or ',' or '，' or '\n' or '\r';
    }
    private static bool Perform(AutomationElement element, CancellationToken token, bool twice, bool invokeOnly = false)
    {
        token.ThrowIfCancellationRequested();
        // A notification icon may expose Invoke but only restore on an actual double click.
        if (!invokeOnly && element.TryGetClickablePoint(out var point))
        {
            token.ThrowIfCancellationRequested();
            var hit = AutomationElement.FromPoint(point);
            // Do not inject a click if a dialog or another window now covers the tray item.
            var walker = TreeWalker.RawViewWalker;
            var belongs = false;
            for (var i = 0; hit != null && i < 8; i++, hit = walker.GetParent(hit))
                if (Automation.Compare(hit, element)) { belongs = true; break; }
            if (belongs && Click(point.X, point.Y, twice)) return true;
        }
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var action))
        { token.ThrowIfCancellationRequested(); ((InvokePattern)action).Invoke(); return true; }
        if (!twice && element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
        { token.ThrowIfCancellationRequested(); ((ExpandCollapsePattern)expand).Expand(); return true; }
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
