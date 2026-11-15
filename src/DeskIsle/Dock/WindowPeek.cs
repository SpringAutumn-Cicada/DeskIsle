using System.Runtime.InteropServices;
namespace DeskIsle.Dock;

// One reversible hover session. No foreground activation and no changes to unrelated windows.
internal sealed class WindowPeek : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct Placement
    {
        public uint Length, Flags, ShowCommand;
        public DockNative.Point MinPosition, MaxPosition;
        public DockNative.Rect NormalPosition;
    }
    private sealed record Snapshot(nint Handle, uint Pid, uint Thread, Placement Placement, nint Previous, bool Minimized, bool Topmost);
    private readonly List<Snapshot> snapshots = [];
    private nint foreground;
    internal bool Active => snapshots.Count > 0;
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(nint window, ref Placement placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(nint window, in Placement placement);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);
    private static bool SameWindow(Snapshot item) => DockNative.IsWindow(item.Handle) &&
        DockNative.GetWindowThreadProcessId(item.Handle, out var pid) == item.Thread && pid == item.Pid;
    internal bool Begin(IEnumerable<DockWindow> windows)
    {
        Restore();
        foreground = DockNative.GetForegroundWindow();
        var next = new List<Snapshot>();
        foreach (var window in windows.DistinctBy(w => w.Handle))
        {
            var handle = window.Handle;
            if (!DockNative.IsWindow(handle) || !DockNative.IsWindowVisible(handle) || !DockNative.IsWindowEnabled(handle) ||
                handle == foreground || DwmGetWindowAttribute(handle, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
            var placement = new Placement { Length = (uint)Marshal.SizeOf<Placement>() };
            if (!GetWindowPlacement(handle, ref placement)) return false;
            var thread = DockNative.GetWindowThreadProcessId(handle, out var pid);
            next.Add(new(handle, pid, thread, placement, DockNative.GetWindow(handle, 3), DockNative.IsIconic(handle), (DockNative.GetWindowLong(handle, -20) & 8) != 0));
        }
        snapshots.AddRange(next);
        foreach (var item in snapshots)
        {
            if (item.Minimized)
            {
                var show = item.Placement;
                show.Flags |= 4; // WPF_ASYNCWINDOWPLACEMENT: never wait on another app's dispatcher.
                show.ShowCommand = 4; // SW_SHOWNOACTIVATE
                if (!SetWindowPlacement(item.Handle, in show)) { Restore(); return false; }
            }
            if (!DockNative.SetWindowPos(item.Handle, item.Topmost ? -1 : 0, 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x200 | 0x4000))
            { Restore(); return false; }
        }
        return Active;
    }
    // Called before the existing click action closes the preview. Committed windows must stay open.
    internal void Commit(IEnumerable<DockWindow> windows)
    {
        var selected = windows.Select(w => w.Handle).ToHashSet();
        foreach (var item in snapshots.Where(s => selected.Contains(s.Handle)).ToArray())
        {
            if (SameWindow(item) && item.Minimized && (item.Placement.Flags & 2) != 0)
            {
                var placement = item.Placement; placement.Flags |= 4; placement.ShowCommand = 3;
                SetWindowPlacement(item.Handle, in placement); // Explicit click may activate/maximize.
            }
            snapshots.Remove(item);
        }
        Restore();
    }
    // Return true when the user changed foreground; do not immediately reopen the same hovered card.
    internal bool CheckForeground()
    {
        if (!Active) return false;
        var current = DockNative.GetForegroundWindow();
        if (current == foreground) return false;
        var selected = snapshots.Where(s => s.Handle == current || DockNative.GetAncestor(current, 3) == s.Handle)
            .Select(s => new DockWindow(s.Handle, "", null)).ToArray();
        // Directly clicking a real window is also an explicit selection, not an unviewed hover.
        Commit(selected);
        return true;
    }
    internal void Restore()
    {
        var items = snapshots.ToArray(); snapshots.Clear();
        foreach (var item in items.Reverse())
        {
            if (!SameWindow(item)) continue;
            if (item.Minimized)
            {
                var placement = item.Placement; placement.Flags |= 4; placement.ShowCommand = 7; // SW_SHOWMINNOACTIVE
                SetWindowPlacement(item.Handle, in placement);
            }
            var previous = item.Previous;
            if (previous == item.Handle || !DockNative.IsWindow(previous) ||
                !item.Topmost && (DockNative.GetWindowLong(previous, -20) & 8) != 0) previous = item.Topmost ? -1 : 0;
            DockNative.SetWindowPos(item.Handle, previous, 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x200 | 0x4000);
        }
    }
    public void Dispose() => Restore();
}
