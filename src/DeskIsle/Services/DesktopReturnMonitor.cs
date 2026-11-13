using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
namespace DeskIsle.Services;

/// <summary>One out-of-context WinEvent subscription for all cards; no Explorer injection.</summary>
public sealed class DesktopReturnMonitor : IDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly Action<bool> recover;
    private readonly WinEventProc callback;
    private readonly List<nint> hooks = [];
    private readonly DispatcherTimer settle;
    private bool queued;
    private int retries;
    private bool disposed;
    public bool Available => hooks.Count >= 2;
    public DesktopReturnMonitor(Dispatcher dispatcher, Action<bool> recover)
    {
        this.dispatcher = dispatcher;
        this.recover = recover;
        callback = OnEvent;
        // Run below WPF rendering; retries must not compete with presentation.
        settle = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        settle.Tick += (_, _) =>
        {
            if (disposed) { StopRecovery(); return; }
            recover(false); // Settling checks repair state without repainting healthy surfaces.
            RecoveryTrace.Mark("retry");
            if (++retries >= 10) StopRecovery();
            else settle.Interval = TimeSpan.FromMilliseconds(100);
        };
        AddHook(0x0003, 0x0003); // foreground changed, including a foreground application closing
        AddHook(0x0017, 0x0017); // minimize ended
        AddHook(0x0020, 0x0020); // desktop switch
        // Win+D can minimize our cards after the foreground event has already fired.
        // Observe our own minimize-start events without including our foreground/reorder repairs.
        var ownMinimize = SetWinEventHook(0x0016, 0x0016, 0, callback, (uint)Environment.ProcessId, 0, 0);
        if (ownMinimize != 0) hooks.Add(ownMinimize);
        AddHook(0x8004, 0x8004); // Explorer desktop Z-order changed
    }
    private void AddHook(uint first, uint last)
    {
        var hook = SetWinEventHook(first, last, 0, callback, 0, 0, 0x0002); // OUTOFCONTEXT | SKIPOWNPROCESS
        if (hook != 0) hooks.Add(hook);
    }
    private void OnEvent(nint hook, uint eventId, nint hwnd, int objectId, int childId, uint threadId, uint time)
    {
        if (disposed) return;
        if (eventId == 0x8004 && (objectId != 0 || childId != 0 || !IsShellDesktop(hwnd))) return;
        RecoveryTrace.Mark("event");
        // Shell reorder bursts are produced by our own Z-order repair as well.
        if (eventId == 0x8004 && settle.IsEnabled) return;
        // Keep native callback short and coalesce the transition burst on the WPF dispatcher.
        if (queued || dispatcher.HasShutdownStarted) return;
        queued = true;
        dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            queued = false;
            if (disposed) return;
            RecoveryTrace.Mark("dispatch");
            recover(eventId == 0x0003);
            RecoveryTrace.Mark("recover");
            // Keep the original bounded schedule during event bursts. A WPF frame
            // is not proof that Explorer/DWM has finished the desktop transition.
            if (!settle.IsEnabled)
            {
                retries = 0;
                settle.Interval = TimeSpan.FromMilliseconds(100);
                settle.Start();
            }
        }));
    }
    private void StopRecovery()
    {
        settle.Stop();
        RecoveryTrace.FlushLater(dispatcher);
    }
    public static nint ForegroundDesktop()
    {
        var foreground = GetForegroundWindow();
        var root = GetAncestor(foreground, 2);
        return IsShellDesktop(root) ? root : 0;
    }
    private static bool IsShellDesktop(nint hwnd)
    {
        if (hwnd == 0) return false;
        var name = new StringBuilder(128);
        GetClassName(hwnd, name, name.Capacity);
        if (name.ToString() is not ("Progman" or "WorkerW" or "SHELLDLL_DefView" or "SysListView32")) return false;
        var progman = FindWindow("Progman", null);
        if (progman == 0) return false;
        GetWindowThreadProcessId(progman, out var explorer);
        GetWindowThreadProcessId(hwnd, out var owner);
        return owner == explorer;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        StopRecovery();
        foreach (var hook in hooks) UnhookWinEvent(hook);
        hooks.Clear();
        GC.KeepAlive(callback);
    }
    private delegate void WinEventProc(nint hook, uint eventId, nint hwnd, int objectId, int childId, uint threadId, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint first, uint last, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder name, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string name, string? title);
}
