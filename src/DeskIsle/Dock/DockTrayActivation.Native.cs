using System.Runtime.InteropServices;
namespace DeskIsle.Dock;

internal static partial class DockTrayActivation
{
    // Use the current Windows COM client directly. The older managed UIA client can
    // expose an empty tree for the WinUI notification island on newer Explorer builds.
    private static async Task<Outcome> ActivateNative(nint[] taskbars, string[] aliases, uint[] pids,
        string? selection, bool invokeOnly, CancellationToken token)
    {
        using var client = new NativeTrayClient();
        var targets = new List<(NativeElement Element, Candidate Info)>();
        var chevrons = new List<NativeElement>();
        var nodesRead = 0;
        void Scan(nint hwnd, bool overflow)
        {
            NativeElement root;
            try { root = client.Keep(client.Automation.ElementFromHandle(hwnd)); }
            catch (COMException) { return; }
            var pending = new Stack<(NativeElement Element, bool Tray, int Depth)>();
            pending.Push((root, overflow, 0));
            var visited = 0;
            while (pending.Count > 0 && visited++ < 1024)
            {
                token.ThrowIfCancellationRequested();
                nodesRead++;
                var (element, tray, depth) = pending.Pop();
                try
                {
                    var name = element.Value(30005) as string ?? "";
                    var id = element.Value(30011) as string ?? "";
                    var cls = element.Value(30012) as string ?? "";
                    tray |= cls == "TrayNotifyWnd" || cls.StartsWith("SystemTray.", StringComparison.Ordinal)
                        || id == "SystemTrayFrame";
                    var visible = element.Value(30022) is false && element.Value(30010) is true;
                    var chevron = IsChevron(name) || id == "SystemTrayChevron" || cls == "SystemTray.ChevronIconView";
                    if (visible && chevron) chevrons.Add(element);
                    var type = element.Value(30003) is int control ? control : 0;
                    // Native tray providers may use Custom controls instead of Button.
                    if (tray && visible && !chevron && name.Length > 0 && type is 50000 or 50011 or 50025)
                    {
                        var pid = element.Value(30002) is int process ? process : 0;
                        var score = pids.Contains((uint)pid) ? 100 : aliases.Any(a => name.Equals(a, StringComparison.OrdinalIgnoreCase) || NameMatches(name, a)) ? 50 : 0;
                        var key = DockRestoreDiagnostics.Hash("native|" + id + "|" + name + "|" + cls + "|" + pid);
                        var box = element.Value(30001) as double[];
                        targets.Add((element, new(key, name, cls, pid, element.Value(30031) is true,
                            box is { Length: 4 } && box[2] > 0 && box[3] > 0, score)));
                    }
                    if (depth >= 24) continue;
                    var child = client.KeepNullable(client.Walker.First(element));
                    while (child != null)
                    {
                        token.ThrowIfCancellationRequested();
                        pending.Push((child, tray, depth + 1));
                        if (pending.Count >= 1024) break;
                        child = client.KeepNullable(client.Walker.Next(child));
                    }
                }
                catch (COMException) { /* A disappearing item must not discard the other icons. */ }
            }
        }
        void ScanIsland(nint hwnd, bool overflow)
        {
            Scan(hwnd, overflow);
            // Some shell builds expose the provider only on the composition child HWND.
            var bridges = new List<nint>();
            EnumChildWindows(hwnd, (child, _) =>
            {
                if (DockNative.Class(child) == "Windows.UI.Composition.DesktopWindowContentBridge") bridges.Add(child);
                return !token.IsCancellationRequested;
            }, 0);
            foreach (var bridge in bridges) { token.ThrowIfCancellationRequested(); Scan(bridge, overflow); }
        }
        void ScanOverflow()
        {
            var roots = new List<nint>();
            DockNative.EnumWindows((hwnd, _) =>
            {
                if (DockNative.Class(hwnd) is "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland" or "SystemTray_Main") roots.Add(hwnd);
                return !token.IsCancellationRequested;
            }, 0);
            foreach (var root in roots) { token.ThrowIfCancellationRequested(); ScanIsland(root, true); }
        }
        (NativeElement Element, Candidate Info)? ChooseNative()
        {
            var unique = targets.DistinctBy(t => t.Info.Key).ToArray();
            if (selection != null)
            {
                var match = unique.Where(t => t.Info.Key == selection).ToArray();
                return match.Length == 1 ? match[0] : null;
            }
            var owned = unique.Where(t => t.Info.Score >= 100).ToArray();
            if (owned.Length > 0) return owned.Length == 1 ? owned[0] : null;
            var named = unique.Where(t => t.Info.Score >= 50).ToArray();
            return named.Length == 1 ? named[0] : null;
        }
        foreach (var hwnd in taskbars) ScanIsland(hwnd, false);
        ScanOverflow();
        var selected = ChooseNative();
        if (selected == null)
        {
            foreach (var chevron in chevrons.Distinct().ToArray())
            {
                token.ThrowIfCancellationRequested();
                if (!client.Perform(chevron, token, false, false)) continue;
                for (var i = 0; i < 10; i++)
                {
                    await Task.Delay(100, token).ConfigureAwait(false);
                    ScanOverflow(); selected = ChooseNative();
                    if (selected != null) break;
                }
                if (selected != null) break;
            }
        }
        var candidates = targets.DistinctBy(t => t.Info.Key).Take(64).Select(t => t.Info).ToArray();
        if (selected == null) return new(false, candidates.Length == 0 ? "native-empty-tray-tree-nodes-" + nodesRead : "native-ownership-ambiguous", candidates);
        return new(client.Perform(selected.Value.Element, token, true, invokeOnly), "native-tray-action", candidates);
    }
    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint parent, DockNative.EnumProc callback, nint param);
    private sealed class NativeTrayClient : IDisposable
    {
        private readonly HashSet<object> references = new(ReferenceEqualityComparer.Instance);
        internal readonly NativeAutomation Automation;
        internal readonly NativeWalker Walker;
        internal NativeTrayClient()
        {
            Automation = Keep((NativeAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new("e22ad333-b25f-460c-83d0-0581107395c9"), true)!)!);
            Walker = Keep(Automation.RawWalker());
        }
        internal T Keep<T>(T value) where T : class { references.Add(value); return value; }
        internal T? KeepNullable<T>(T? value) where T : class => value == null ? null : Keep(value);
        internal bool Perform(NativeElement element, CancellationToken token, bool twice, bool invokeOnly)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (element.Value(30022) is not false || element.Value(30010) is not true) return false;
                if (!invokeOnly && element.Value(30001) is double[] { Length: 4 } box && box[2] > 0 && box[3] > 0)
                {
                    var point = new DockNative.Point { X = (int)(box[0] + box[2] / 2), Y = (int)(box[1] + box[3] / 2) };
                    var hit = KeepNullable(Automation.ElementFromPoint(point));
                    for (var i = 0; hit != null && i < 8; i++, hit = KeepNullable(Walker.Parent(hit)))
                    {
                        if (!Automation.Same(hit, element)) continue;
                        token.ThrowIfCancellationRequested();
                        if (Click(point.X, point.Y, twice)) return true;
                        break;
                    }
                }
                token.ThrowIfCancellationRequested();
                var iid = typeof(NativeInvoke).GUID;
                var invoke = Keep((NativeInvoke)element.Pattern(10000, ref iid));
                invoke.Invoke(); return true;
            }
            catch (COMException) { return false; }
        }
        public void Dispose()
        {
            foreach (var value in references.Reverse())
                if (Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
            references.Clear();
        }
    }
    // Prefixes of the SDK IUnknown interfaces; unused methods retain their vtable slots.
    // Source: Microsoft WinSDK UIAutomationClient.h (IUIAutomation/Element/TreeWalker).
    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface NativeAutomation
    {
        [return: MarshalAs(UnmanagedType.Bool)] bool Same(NativeElement first, NativeElement second);
        void CompareRuntimeIds(); void GetRootElement();
        NativeElement ElementFromHandle(nint hwnd);
        NativeElement ElementFromPoint(DockNative.Point point);
        void GetFocusedElement(); void GetRootElementBuildCache(); void ElementFromHandleBuildCache();
        void ElementFromPointBuildCache(); void GetFocusedElementBuildCache(); void CreateTreeWalker();
        void ControlWalker(); void ContentWalker(); NativeWalker RawWalker();
    }
    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface NativeElement
    {
        void SetFocus(); void GetRuntimeId(); void FindFirst(); void FindAll(); void FindFirstBuildCache();
        void FindAllBuildCache(); void BuildUpdatedCache();
        [return: MarshalAs(UnmanagedType.Struct)] object Value(int property);
        void GetCurrentPropertyValueEx(); void GetCachedPropertyValue(); void GetCachedPropertyValueEx();
        [return: MarshalAs(UnmanagedType.IUnknown)] object Pattern(int pattern, ref Guid iid);
    }
    [ComImport, Guid("4042c624-389c-4afc-a630-9df854a541fc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface NativeWalker
    {
        NativeElement? Parent(NativeElement element); NativeElement? First(NativeElement element);
        void Last(); NativeElement? Next(NativeElement element);
    }
    [ComImport, Guid("fb377fbe-8ea6-46d5-9c73-6499642d3059"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface NativeInvoke { void Invoke(); }
}
