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
    [StructLayout(LayoutKind.Sequential)] private struct Size { public int Width, Height; }
    private sealed class View : IDisposable
    {
        internal readonly HwndSource Surface;
        internal nint Thumbnail;
        internal View(nint source, DockNative.Rect outer, DockNative.Rect rect, int corner, Action select)
        {
            Surface = new HwndSource(new HwndSourceParameters("DeskIsle hover view")
            {
                ParentWindow = 0, PositionX = rect.Left, PositionY = rect.Top,
                Width = rect.Right - rect.Left, Height = rect.Bottom - rect.Top,
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080
            });
            DwmSetWindowAttribute(Surface.Handle, 33, ref corner, sizeof(int));
            var border = -2; // DWMWA_COLOR_NONE: no additional border on the host.
            DwmSetWindowAttribute(Surface.Handle, 34, ref border, sizeof(int));
            if (Surface.CompositionTarget != null) Surface.CompositionTarget.BackgroundColor = Colors.Transparent;
            Surface.AddHook((nint hwnd, int msg, nint wp, nint lp, ref bool handled) =>
            {
                if (msg == 0x21) { handled = true; return 3; } // MA_NOACTIVATE
                if (msg == 0x202) { handled = true; Surface.Dispatcher.BeginInvoke(select); }
                return 0;
            });
            if (DwmRegisterThumbnail(Surface.Handle, source, out Thumbnail) < 0) return;
            if (DwmQueryThumbnailSourceSize(Thumbnail, out var size) < 0 || size.Width <= 0 || size.Height <= 0)
            { DwmUnregisterThumbnail(Thumbnail); Thumbnail = 0; return; }
            var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
            var crop = new DockNative.Rect { Right = size.Width, Bottom = size.Height };
            var outerMatch = size.Width == outer.Right - outer.Left && size.Height == outer.Bottom - outer.Top;
            var frameMatch = size.Width == width && size.Height == height;
            if (outerMatch)
                crop = new() { Left = rect.Left - outer.Left, Top = rect.Top - outer.Top,
                    Right = rect.Right - outer.Left, Bottom = rect.Bottom - outer.Top };
            // Accept DWM's visible-frame representation as well as the outer surface.
            // Clip source pixels instead of submitting a negative destination rectangle.
            var destination = new DockNative.Rect { Right = crop.Right - crop.Left, Bottom = crop.Bottom - crop.Top };
            if (!outerMatch && !frameMatch)
            {
                // Never resample a minimized cache to a guessed restore size.
                // Keep all cached pixels at 1:1, adjusting only our local host.
                // Without a measured frame, recognize the symmetric side/bottom
                // inset only when the source/outer differences agree exactly.
                var dx = outer.Right - outer.Left - size.Width;
                var dy = outer.Bottom - outer.Top - size.Height;
                if (rect.Equals(outer) && dx > 0 && dy > 0 && dx == 2 * dy)
                {
                    rect.Left = outer.Left + dy;
                    rect.Right = rect.Left + size.Width;
                    rect.Bottom = rect.Top + size.Height;
                }
                else
                {
                    rect.Right = rect.Left + size.Width;
                    rect.Bottom = rect.Top + size.Height;
                }
                if (!DockNative.SetWindowPos(Surface.Handle, 0, rect.Left, rect.Top, size.Width, size.Height, 0x4 | 0x10))
                { DwmUnregisterThumbnail(Thumbnail); Thumbnail = 0; return; }
            }
            Diagnose(source, Surface.Handle, outer, rect, size, crop, destination, outerMatch || frameMatch);
            var props = new Properties { Flags = 1 | 2 | 4 | 8 | 16,
                Destination = destination, Source = crop,
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
    [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(nint thumbnail, out Size size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int FrameBounds(nint window, uint attribute, out DockNative.Rect rect, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool IsWindowArranged(nint window);
    internal static int CornerPreference(nint window)
    {
        if (DockNative.IsZoomed(window) || (DockNative.GetWindowLong(window, -16) & 0x01000000) != 0 || IsWindowArranged(window)) return 1;
        if (DwmGetWindowAttribute(window, 33, out var corner, sizeof(int)) == 0 && corner is >= 1 and <= 3) return corner;
        var style = DockNative.GetWindowLong(window, -16);
        return (style & 0x00C00000) == 0x00C00000 && (style & 0x00040000) != 0 ? 2 : 1;
    }
    private static void Diagnose(nint source, nint host, DockNative.Rect outer, DockNative.Rect frame,
        Size size, DockNative.Rect crop, DockNative.Rect destination, bool reliable)
    {
        if (Environment.GetEnvironmentVariable("DESKISLE_PEEK_DIAGNOSTICS") != "1") return;
        try
        {
            var directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskIsle");
            System.IO.Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, "peek-geometry.log");
            if (System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > 1024 * 1024)
                System.IO.File.WriteAllText(path, "");
            DockNative.GetWindowRect(host, out var actual);
            GetClientRect(host, out var client);
            var origin = new DockNative.Point();
            ClientToScreen(host, ref origin);
            static string R(DockNative.Rect r) => $"{r.Left},{r.Top},{r.Right},{r.Bottom}";
            System.IO.File.AppendAllText(path, $"{DateTimeOffset.Now:O} hwnd={source:X} minimized={DockNative.IsIconic(source)} outer={R(outer)} frame={R(frame)} sourceSize={size.Width},{size.Height} source={R(crop)} destination={R(destination)} reliable={reliable} host={R(actual)} client={R(client)} clientOrigin={origin.X},{origin.Y} sourceDpi={GetDpiForWindow(source)} hostDpi={GetDpiForWindow(host)}\n");
        }
        catch { /* Diagnostics must never interrupt preview cleanup. */ }
    }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out DockNative.Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref DockNative.Point point);
    private readonly List<View> views = [];
    private nint foreground;
    internal bool Active => views.Count > 0;
    internal bool Begin(IEnumerable<DockWindow> windows, SplitGroups groups, Action select)
    {
        Restore();
        foreground = DockNative.GetForegroundWindow();
        var members = windows.ToArray();
        groups.Observe(members);
        try
        {
            foreach (var window in members.DistinctBy(w => w.Handle))
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
                var frame = rect;
                if (!DockNative.IsIconic(hwnd))
                {
                    if (FrameBounds(hwnd, 9, out var visible, Marshal.SizeOf<DockNative.Rect>()) == 0) frame = visible;
                }
                else if (groups.TryGetFrame(hwnd, out var cachedFrame)) frame = cachedFrame;
                // Reject stale/invalid frame bounds instead of stretching to them.
                if (frame.Left < rect.Left || frame.Top < rect.Top || frame.Right > rect.Right || frame.Bottom > rect.Bottom ||
                    frame.Right <= frame.Left || frame.Bottom <= frame.Top) frame = rect;
                var corner = DockNative.IsIconic(hwnd) && groups.TryGetCorner(hwnd, out var cachedCorner)
                    ? cachedCorner : CornerPreference(hwnd);
                var view = new View(hwnd, rect, frame, corner, select);
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
