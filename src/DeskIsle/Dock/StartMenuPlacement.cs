using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DeskIsle.Dock;

// Only adjust a Start menu requested by this Dock; never inject into Explorer.
internal sealed class StartMenuPlacement : IDisposable
{
    private readonly DockBar bar;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Dictionary<nint, DockNative.Rect> originals = [];
    private DateTime deadline;
    private bool seen;
    private int adjustments;
    internal bool IsOpen { get; private set; }

    internal StartMenuPlacement(DockBar bar)
    {
        this.bar = bar;
        timer.Tick += (_, _) => Update();
    }

    internal void Begin()
    {
        if (timer.IsEnabled) return; // A second click toggles the existing system menu.
        deadline = DateTime.UtcNow.AddSeconds(3);
        seen = false;
        adjustments = 0;
        IsOpen = true;
        timer.Start();
    }

    private void Update()
    {
        var hosts = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName("StartMenuExperienceHost"))
        {
            using (process)
            {
                try { hosts.Add((uint)process.Id); }
                catch (InvalidOperationException) { }
            }
        }
        var windows = new List<(nint Handle, DockNative.Rect Rect)>();
        DockNative.EnumWindows((window, _) =>
        {
            DockNative.GetWindowThreadProcessId(window, out var pid);
            if (hosts.Contains(pid) && DockNative.IsWindowVisible(window)
                && DockNative.GetWindowRect(window, out var rect)
                && rect.Right - rect.Left >= 200 && rect.Bottom - rect.Top >= 200)
                windows.Add((window, rect));
            return true;
        }, 0);
        if (windows.Count == 0)
        {
            if (seen || DateTime.UtcNow >= deadline) Stop();
            return;
        }
        seen = true;
        var dock = new WindowInteropHelper(bar).Handle;
        if (!DockNative.GetWindowRect(dock, out var dockRect)) return;
        var monitor = DockNative.Primary().Monitor;
        var dpi = VisualTreeHelper.GetDpi(bar);
        var gap = (int)Math.Round(12 * dpi.DpiScaleY);
        var bottom = dockRect.Top - gap;
        var topLimit = monitor.Top + gap;
        var available = bottom - topLimit;
        if (available < 200 || adjustments >= 12) return;
        foreach (var (handle, rect) in windows)
        {
            // Ignore a Start menu on another display.
            if (rect.Right <= monitor.Left || rect.Left >= monitor.Right) continue;
            var height = Math.Min(rect.Bottom - rect.Top, available);
            var top = bottom - height;
            if (Math.Abs(rect.Top - top) <= 1 && Math.Abs(rect.Bottom - bottom) <= 1) continue;
            originals.TryAdd(handle, rect);
            // Preserve focus and Z order; the Shell continues to own its contents and scrolling.
            DockNative.SetWindowPos(handle, 0, rect.Left, top, rect.Right - rect.Left, height, 0x0014 | 0x4000);
            adjustments++;
        }
        timer.Interval = TimeSpan.FromMilliseconds(150);
    }

    private void Stop()
    {
        timer.Stop();
        IsOpen = false;
        foreach (var (handle, rect) in originals)
            if (DockNative.IsWindow(handle) && !DockNative.IsWindowVisible(handle))
                DockNative.SetWindowPos(handle, 0, rect.Left, rect.Top, rect.Right - rect.Left,
                    rect.Bottom - rect.Top, 0x0014 | 0x4000);
        originals.Clear();
    }

    public void Dispose() => Stop();
}
