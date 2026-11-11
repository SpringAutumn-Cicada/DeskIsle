namespace DeskIsle.Dock;

internal sealed record SplitPair(DockWindow Left, DockWindow Right, DockNative.Rect LeftRect, DockNative.Rect RightRect);
internal sealed class SplitGroups
{
    private readonly Dictionary<nint, DockNative.Rect> positions = [];
    internal void Observe(DockWindow[] windows)
    {
        foreach (var window in windows)
            if (!DockNative.IsIconic(window.Handle) && DockNative.GetWindowRect(window.Handle, out var rect)) positions[window.Handle] = rect;
        foreach (var key in positions.Keys.Where(key => !DockNative.IsWindow(key)).ToArray()) positions.Remove(key);
    }
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
            var work = info.Work; var middle = (work.Left + work.Right) / 2;
            // Require two full-height complementary halves, allowing resize frame borders.
            bool Near(int x, int y) => Math.Abs(x - y) <= 20;
            if (Near(a.Left, work.Left) && Near(a.Right, middle) && Near(b.Left, middle) && Near(b.Right, work.Right) &&
                Near(a.Top, work.Top) && Near(b.Top, work.Top) && Near(a.Bottom, work.Bottom) && Near(b.Bottom, work.Bottom))
                return new(left, right, a, b);
        }
        return null;
    }
    internal static void Restore(SplitPair pair)
    {
        if (!DockNative.IsWindow(pair.Left.Handle) || !DockNative.IsWindow(pair.Right.Handle)) return;
        foreach (var item in new[] { (pair.Left, pair.LeftRect), (pair.Right, pair.RightRect) })
        {
            if (DockNative.IsIconic(item.Item1.Handle)) DockNative.ShowWindow(item.Item1.Handle, 9);
            var r = item.Item2;
            DockNative.SetWindowPos(item.Item1.Handle, 0, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, 0x10);
        }
        DockNative.SetForegroundWindow(pair.Left.Handle);
    }
}
