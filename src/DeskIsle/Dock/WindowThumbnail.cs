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
    private WindowThumbnail(nint handle, nint owner, HwndSource surface)
    { this.handle = handle; this.owner = owner; this.surface = surface; }
    internal static WindowThumbnail? Create(nint destination, nint source)
    {
        if (!DockNative.IsWindow(source)) return null;
        // A native owned surface avoids WPF's opaque scene obscuring the DWM image.
        // Disabled/transparent hit testing lets clicks reach the WPF preview card below.
        var surface = new HwndSource(new HwndSourceParameters("DeskIsle thumbnail")
        {
            ParentWindow = destination, Width = 1, Height = 1,
            WindowStyle = unchecked((int)0x88000000), ExtendedWindowStyle = 0x080000A0
        });
        if (surface.CompositionTarget != null) surface.CompositionTarget.BackgroundColor = Color.FromRgb(21, 29, 41);
        if (DwmRegisterThumbnail(surface.Handle, source, out var thumbnail) < 0) { surface.Dispose(); return null; }
        return new(thumbnail, destination, surface);
    }
    internal bool Update(DockNative.Rect box)
    {
        if (handle == 0 || box.Right <= box.Left || box.Bottom <= box.Top) return false;
        if (DwmQueryThumbnailSourceSize(handle, out var size) < 0 || size.Width <= 0 || size.Height <= 0) return false;
        var origin = new DockNative.Point { X = box.Left, Y = box.Top };
        if (!ClientToScreen(owner, ref origin)) return false;
        var boxWidth = box.Right - box.Left; var boxHeight = box.Bottom - box.Top;
        SetWindowPos(surface.Handle, -1, origin.X, origin.Y, boxWidth, boxHeight, 0x10);
        box = new DockNative.Rect { Right = boxWidth, Bottom = boxHeight };
        var scale = Math.Min((double)(box.Right - box.Left) / size.Width, (double)(box.Bottom - box.Top) / size.Height);
        var width = Math.Max(1, (int)Math.Round(size.Width * scale));
        var height = Math.Max(1, (int)Math.Round(size.Height * scale));
        box.Left += (box.Right - box.Left - width) / 2; box.Top += (box.Bottom - box.Top - height) / 2;
        box.Right = box.Left + width; box.Bottom = box.Top + height;
        if (last is { } old && old.Equals(box)) { DockNative.ShowWindow(surface.Handle, 4); return true; }
        var properties = new Properties { Flags = 1 | 4 | 8 | 16, Destination = box, Opacity = 255, Visible = true, ClientOnly = false };
        if (DwmUpdateThumbnailProperties(handle, ref properties) < 0) return false;
        DockNative.ShowWindow(surface.Handle, 4);
        last = box; return true;
    }
    public void Dispose() { if (handle != 0) { DwmUnregisterThumbnail(handle); handle = 0; surface.Dispose(); } }
}
