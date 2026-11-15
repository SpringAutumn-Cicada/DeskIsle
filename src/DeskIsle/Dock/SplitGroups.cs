using System.Runtime.InteropServices;
namespace DeskIsle.Dock;

internal sealed record SplitPair(DockWindow Left, DockWindow Right, DockNative.Rect LeftRect, DockNative.Rect RightRect);
internal sealed class SplitGroups
{
    private readonly Dictionary<nint, DockNative.Rect> positions = [];
    private readonly Dictionary<nint, DockNative.Rect> frames = [];
    private readonly Dictionary<nint, int> corners = [];
    internal bool TryGetCorner(nint window, out int corner) => corners.TryGetValue(window, out corner);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int FrameBounds(nint window, uint attribute, out DockNative.Rect rect, int size);
    internal bool TryGetFrame(nint window, out DockNative.Rect rect) => frames.TryGetValue(window, out rect);
    internal void Observe(DockWindow[] windows)
    {
        foreach (var window in windows)
            if (!DockNative.IsIconic(window.Handle) && DockNative.GetWindowRect(window.Handle, out var rect))
            {

                if (FrameBounds(window.Handle, 9, out var frame, Marshal.SizeOf<DockNative.Rect>()) == 0 &&
                    frame.Right > frame.Left && frame.Bottom > frame.Top && frame.Left >= rect.Left &&
                    frame.Top >= rect.Top && frame.Right <= rect.Right && frame.Bottom <= rect.Bottom)
                {
                    // Update outer/frame/corner together; a transient failed DWM read
                    // must not erase the last complete pre-minimize snapshot.
                    positions[window.Handle] = rect;
                    frames[window.Handle] = frame;
                    corners[window.Handle] = WindowPeek.CornerPreference(window.Handle);
                }
            }
        foreach (var key in positions.Keys.Where(key => !DockNative.IsWindow(key)).ToArray()) { positions.Remove(key); frames.Remove(key); corners.Remove(key); }
    }
    // GetWindowPlacement can contain the pre-snap restore rectangle. Retain the
    // last actual screen bounds while minimized or in a reversible hover session.
    internal bool TryGetPosition(nint window, out DockNative.Rect rect) => positions.TryGetValue(window, out rect);
    internal SplitPair? Find(DockWindow[] windows)
    {
        foreach (var left in windows)
        foreach (var right in windows)
        {
            if (left.Handle == right.Handle || left.Executable == null || !string.Equals(left.Executable, right.Executable, StringComparison.OrdinalIgnoreCase)) continue;
            if (!positions.TryGetValue(left.Handle, out var a) || !positions.TryGetValue(right.Handle, out var b)) continue;
            var monitor = DockNative.MonitorFromWindow(left.Handle, 2);
            if (monitor != DockNative.MonitorFromWindow(right.Handle, 2)) continue;
            var info = new DockNative.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<DockNative.MonitorInfo>() };
            if (!DockNative.GetMonitorInfo(monitor, ref info)) continue;
            var work = info.Work; var middle = (a.Right + b.Left) / 2;
            // Require two full-height complementary halves, allowing resize frame borders.
            bool Near(int x, int y) => Math.Abs(x - y) <= Math.Max(24, (work.Right - work.Left) / 80);
            if (Near(a.Left, work.Left) && Near(a.Right, b.Left) && middle > work.Left + (work.Right - work.Left) * .2 && middle < work.Right - (work.Right - work.Left) * .2 && Near(b.Right, work.Right) &&
                Near(a.Top, work.Top) && Near(b.Top, work.Top) && Near(a.Bottom, work.Bottom) && Near(b.Bottom, work.Bottom))
                return new(left, right, a, b);
        }
        return null;
    }
    [DllImport("user32.dll")] private static extern nint BeginDeferWindowPos(int count);
    [DllImport("user32.dll")] private static extern nint DeferWindowPos(nint batch, nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool EndDeferWindowPos(nint batch);
    internal static void Restore(SplitPair pair)
    {
        if (!DockNative.IsWindow(pair.Left.Handle) || !DockNative.IsWindow(pair.Right.Handle)) return;
        var items = new[] { (pair.Left, pair.LeftRect), (pair.Right, pair.RightRect) };
        // Dispatch both restore requests without waiting on the other application's UI thread.
        foreach (var item in items)
            if (DockNative.IsIconic(item.Item1.Handle)) DockNative.ShowWindowAsync(item.Item1.Handle, 9);
        var batch = BeginDeferWindowPos(2);
        if (batch != 0)
        {
            foreach (var item in items)
            {
                var r = item.Item2;
                batch = DeferWindowPos(batch, item.Item1.Handle, 0, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, 0x10 | 0x4000);
                if (batch == 0) break;
            }
            if (batch != 0 && EndDeferWindowPos(batch)) { DockNative.SetForegroundWindow(pair.Left.Handle); return; }
        }
        foreach (var item in items)
        {
            var r = item.Item2;
            DockNative.SetWindowPos(item.Item1.Handle, 0, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, 0x10 | 0x4000);
        }
        DockNative.SetForegroundWindow(pair.Left.Handle);
    }
}
