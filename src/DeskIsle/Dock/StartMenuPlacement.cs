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
    private long openedAt;
    private readonly Dictionary<nint, (DockNative.Rect Rect, long Since)> stable = [];
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
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
        stable.Clear();
        openedAt = Environment.TickCount64;
        timer.Interval = TimeSpan.FromMilliseconds(100);
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
                && !(DwmGetWindowAttribute(window, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
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
        if (available < 200 || adjustments >= 8 || Environment.TickCount64 - openedAt < 500) return;
        foreach (var (handle, rect) in windows)
        {
            // Ignore a Start menu on another display.
            if (rect.Right <= monitor.Left || rect.Left >= monitor.Right) continue;
            var now = Environment.TickCount64;
            if (!stable.TryGetValue(handle, out var state) || !state.Rect.Equals(rect))
            {
                stable[handle] = (rect, now);
                continue;
            }
            if (now - state.Since < 250) continue;
            var height = Math.Min(rect.Bottom - rect.Top, available);
            var width = Math.Min(rect.Right - rect.Left, monitor.Right - monitor.Left - 2 * gap);
            var top = bottom - height;
            var left = Math.Clamp(rect.Left, monitor.Left + gap, Math.Max(monitor.Left + gap, monitor.Right - gap - width));
            if (Math.Abs(rect.Top - top) <= 1 && Math.Abs(rect.Bottom - bottom) <= 1 && rect.Left == left) continue;
            originals.TryAdd(handle, rect);
            // Wait for stable bounds before requesting placement, and re-read on subsequent ticks.
            // Keep cross-process positioning asynchronous so a busy Shell cannot block the Dock.
            var accepted = DockNative.SetWindowPos(handle, 0, left, top, width, height, 0x0014 | 0x4000);
            adjustments++;
            stable.Remove(handle);
            if (!accepted) continue;
            if (DockNative.GetWindowRect(handle, out var actual)) stable[handle] = (actual, now);

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
        stable.Clear();
    }

    public void Dispose() => Stop();
}
