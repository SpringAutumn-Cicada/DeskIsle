using System.Runtime.InteropServices;
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
    private nint handle;
    private DockNative.Rect? last;
    private WindowThumbnail(nint handle) => this.handle = handle;
    internal static WindowThumbnail? Create(nint destination, nint source)
    {
        if (!DockNative.IsWindow(source) || DwmRegisterThumbnail(destination, source, out var thumbnail) < 0) return null;
        return new(thumbnail);
    }
    internal bool Update(DockNative.Rect box)
    {
        if (handle == 0 || box.Right <= box.Left || box.Bottom <= box.Top) return false;
        if (DwmQueryThumbnailSourceSize(handle, out var size) < 0 || size.Width <= 0 || size.Height <= 0) return false;
        var scale = Math.Min((double)(box.Right - box.Left) / size.Width, (double)(box.Bottom - box.Top) / size.Height);
        var width = Math.Max(1, (int)Math.Round(size.Width * scale));
        var height = Math.Max(1, (int)Math.Round(size.Height * scale));
        box.Left += (box.Right - box.Left - width) / 2; box.Top += (box.Bottom - box.Top - height) / 2;
        box.Right = box.Left + width; box.Bottom = box.Top + height;
        if (last is { } old && old.Equals(box)) return true;
        var properties = new Properties { Flags = 1 | 4 | 8 | 16, Destination = box, Opacity = 255, Visible = true, ClientOnly = false };
        if (DwmUpdateThumbnailProperties(handle, ref properties) < 0) return false;
        last = box; return true;
    }
    public void Dispose() { if (handle != 0) { DwmUnregisterThumbnail(handle); handle = 0; } }
}
