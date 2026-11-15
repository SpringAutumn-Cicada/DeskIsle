using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
namespace DeskIsle.Dock;

// Full-size DWM views, never restore/move/minimize the source HWNDs during hover.
internal sealed class WindowPeek : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct Placement
    {
        public uint Length, Flags, ShowCommand;
        public DockNative.Point MinPosition, MaxPosition;
        public DockNative.Rect NormalPosition;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Properties
    {
        public uint Flags;
        public DockNative.Rect Destination, Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool ClientOnly;
    }
    private sealed class View : IDisposable
    {
        internal readonly HwndSource Surface;
        internal nint Thumbnail;
        internal View(nint source, DockNative.Rect rect, Action select)
        {
            Surface = new HwndSource(new HwndSourceParameters("DeskIsle hover view")
            {
                ParentWindow = 0, PositionX = rect.Left, PositionY = rect.Top,
                Width = rect.Right - rect.Left, Height = rect.Bottom - rect.Top,
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080
            });
            if (Surface.CompositionTarget != null) Surface.CompositionTarget.BackgroundColor = Colors.Black;
            Surface.AddHook((nint hwnd, int msg, nint wp, nint lp, ref bool handled) =>
            {
                if (msg == 0x21) { handled = true; return 3; } // MA_NOACTIVATE
                if (msg == 0x202) { handled = true; Surface.Dispatcher.BeginInvoke(select); }
                return 0;
            });
            if (DwmRegisterThumbnail(Surface.Handle, source, out Thumbnail) < 0) return;
            var props = new Properties { Flags = 1 | 4 | 8 | 16,
                Destination = new() { Right = rect.Right - rect.Left, Bottom = rect.Bottom - rect.Top },
                Opacity = 255, Visible = true, ClientOnly = false };
            if (DwmUpdateThumbnailProperties(Thumbnail, ref props) < 0) { DwmUnregisterThumbnail(Thumbnail); Thumbnail = 0; }
        }
        internal void Show() => DockNative.SetWindowPos(Surface.Handle, -1, 0, 0, 0, 0, 1 | 2 | 0x10 | 0x40);
        public void Dispose()
        {
            if (Thumbnail != 0) { DwmUnregisterThumbnail(Thumbnail); Thumbnail = 0; }
            Surface.Dispose();
        }
    }
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(nint window, ref Placement placement);
    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(nint thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(nint thumbnail, ref Properties properties);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);
    private readonly List<View> views = [];
    private nint foreground;
    internal bool Active => views.Count > 0;
    internal bool Begin(IEnumerable<DockWindow> windows, SplitGroups groups, Action select)
    {
        Restore();
        foreground = DockNative.GetForegroundWindow();
        try
        {
            foreach (var window in windows.DistinctBy(w => w.Handle))
            {
                var hwnd = window.Handle;
                if (!DockNative.IsWindow(hwnd) || !DockNative.IsWindowVisible(hwnd) || !DockNative.IsWindowEnabled(hwnd) ||
                    DwmGetWindowAttribute(hwnd, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
                { Restore(); return false; }
                DockNative.Rect rect;
                if (!DockNative.IsIconic(hwnd))
                {
                    if (!DockNative.GetWindowRect(hwnd, out rect)) { Restore(); return false; }
                }
                else if (!groups.TryGetPosition(hwnd, out rect))
                {
                    var placement = new Placement { Length = (uint)Marshal.SizeOf<Placement>() };
                    var info = new DockNative.MonitorInfo { Size = Marshal.SizeOf<DockNative.MonitorInfo>() };
                    if (!GetWindowPlacement(hwnd, ref placement) || !DockNative.GetMonitorInfo(DockNative.MonitorFromWindow(hwnd, 2), ref info))
                    { Restore(); return false; }
                    rect = placement.NormalPosition;
                    if ((DockNative.GetWindowLong(hwnd, -16) & 0x01000000) != 0) rect = info.Work;
                    else if ((DockNative.GetWindowLong(hwnd, -20) & 0x80) == 0)
                    {
                        var x = info.Work.Left - info.Monitor.Left; var y = info.Work.Top - info.Monitor.Top;
                        rect.Left += x; rect.Right += x; rect.Top += y; rect.Bottom += y;
                    }
                }
                if (rect.Right <= rect.Left || rect.Bottom <= rect.Top) { Restore(); return false; }
                var view = new View(hwnd, rect, select);
                views.Add(view);
                if (view.Thumbnail == 0) { Restore(); return false; }
            }
            // Publish only after all members are ready; no half-restored combination.
            foreach (var view in views) view.Show();
            return Active;
        }
        catch { Restore(); return false; }
    }
    internal void Commit(IEnumerable<DockWindow> windows) => Restore();
    internal bool CheckForeground()
    {
        if (!Active || DockNative.GetForegroundWindow() == foreground) return false;
        Restore(); return true;
    }
    internal void Restore()
    {
        foreach (var view in views) view.Dispose();
        views.Clear();
    }
    public void Dispose() => Restore();
}
