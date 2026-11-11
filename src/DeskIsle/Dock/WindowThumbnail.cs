using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
namespace DeskIsle.Dock;

/// <summary>A DWM relationship, never a screenshot or a hook into the source process.</summary>
internal sealed class WindowThumbnail : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct Size { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct Properties
    {
        public uint Flags;
        public DockNative.Rect Destination, Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool ClientOnly;
    }
    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(nint thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(nint thumbnail, out Size size);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(nint thumbnail, ref Properties properties);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref DockNative.Point point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    private nint handle;
    private readonly nint owner;
    private readonly HwndSource surface;
    private DockNative.Rect? last;
    private DockNative.Rect? placement;
    private bool shown;
    private bool hovered;
    internal double AspectRatio
    {
        get { return handle != 0 && DwmQueryThumbnailSourceSize(handle, out var size) >= 0 && size.Height > 0 ? (double)size.Width / size.Height : 0; }
    }
    internal void Feedback(bool value)
    {
        if (hovered == value) return;
        hovered = value;
        if (surface.CompositionTarget != null) surface.CompositionTarget.BackgroundColor = value ? Color.FromRgb(115, 137, 164) : Color.FromRgb(49, 60, 76);
    }
    private readonly Action click;
    private readonly Action? close;
    private bool pressed;
    private WindowThumbnail(nint handle, nint owner, HwndSource surface, Action click, Action? close)
    { this.handle = handle; this.owner = owner; this.surface = surface; this.click = click; this.close = close; surface.AddHook(Input); }
    private nint Input(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x21) { handled = true; return 3; }
        if (message == 0x201) { pressed = true; handled = true;
            if (surface.CompositionTarget != null) surface.CompositionTarget.BackgroundColor = Color.FromRgb(148, 166, 190); }
        if (message == 0x202 && pressed)
        {
            pressed = false; handled = true;
            surface.Dispatcher.BeginInvoke(click);
        }
        return 0;
    }
    private int GetWidth() { DockNative.GetWindowRect(surface.Handle, out var rect); return rect.Right - rect.Left; }
    internal static WindowThumbnail? Create(nint destination, nint source, Action click, Action? close = null)
    {
        if (!DockNative.IsWindow(source)) return null;
        // A native owned surface avoids WPF's opaque scene obscuring the DWM image.
        // The native surface handles its own clicks instead of relying on cross-window hit testing.
        var surface = new HwndSource(new HwndSourceParameters("DeskIsle thumbnail")
        {
            ParentWindow = destination, Width = 1, Height = 1,
            WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080
        });
        if (surface.CompositionTarget != null) surface.CompositionTarget.BackgroundColor = Color.FromRgb(21, 29, 41);
        if (DwmRegisterThumbnail(surface.Handle, source, out var thumbnail) < 0) { surface.Dispose(); return null; }
        return new(thumbnail, destination, surface, click, close);
    }
    internal bool Update(DockNative.Rect box, bool present = true)
    {
        if (handle == 0 || box.Right <= box.Left || box.Bottom <= box.Top) return false;
        if (DwmQueryThumbnailSourceSize(handle, out var size) < 0 || size.Width <= 0 || size.Height <= 0) return false;
        var origin = new DockNative.Point { X = box.Left, Y = box.Top };
        if (!ClientToScreen(owner, ref origin)) return false;
        var boxWidth = box.Right - box.Left; var boxHeight = box.Bottom - box.Top;
        var position = new DockNative.Rect { Left = origin.X, Top = origin.Y, Right = origin.X + boxWidth, Bottom = origin.Y + boxHeight };
        if (placement is not { } previous || !previous.Equals(position))
        {
            SetWindowPos(surface.Handle, -1, origin.X, origin.Y, boxWidth, boxHeight, 0x10);
            placement = position;
        }
        box = new DockNative.Rect { Left = 2, Top = 2, Right = boxWidth - 2, Bottom = boxHeight - 2 };
        var scale = Math.Min((double)(box.Right - box.Left) / size.Width, (double)(box.Bottom - box.Top) / size.Height);
        var width = Math.Max(1, (int)Math.Round(size.Width * scale));
        var height = Math.Max(1, (int)Math.Round(size.Height * scale));
        box.Left += (box.Right - box.Left - width) / 2; box.Top += (box.Bottom - box.Top - height) / 2;
        box.Right = box.Left + width; box.Bottom = box.Top + height;
        if (last is { } old && old.Equals(box)) { if (present) Present(); return true; }
        var properties = new Properties { Flags = 1 | 4 | 8 | 16, Destination = box, Opacity = 255, Visible = true, ClientOnly = false };
        if (DwmUpdateThumbnailProperties(handle, ref properties) < 0) return false;
        if (present) Present();
        last = box; return true;
    }
    internal void Present() { if (!shown && handle != 0) { DockNative.ShowWindow(surface.Handle, 4); shown = true; } }
    public void Dispose() { if (handle != 0) { DwmUnregisterThumbnail(handle); handle = 0; surface.RemoveHook(Input); surface.Dispose(); } }
}
