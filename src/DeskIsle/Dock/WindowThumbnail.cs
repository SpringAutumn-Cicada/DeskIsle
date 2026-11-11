using System.Runtime.InteropServices;
using System.Windows;
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
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint window, uint color, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref DockNative.Point point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    private nint handle;
    private readonly nint owner;
    private readonly HwndSource surface;
    private DockNative.Rect? last;
    private DockNative.Rect? placement;
    private DockNative.Rect? closePlacement;
    [DllImport("user32.dll")] private static extern nint SetCapture(nint window);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    internal bool ContainsPointer()
    {
        return DockNative.IsWindowVisible(surface.Handle) && DockNative.GetCursorPos(out var p) && DockNative.GetWindowRect(surface.Handle, out var r)
            && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    }
    private bool hovered;
    internal double AspectRatio
    {
        get { return handle != 0 && DwmQueryThumbnailSourceSize(handle, out var size) >= 0 && size.Height > 0 ? (double)size.Width / size.Height : 0; }
    }
    internal void Feedback(bool value)
    {
        var changed = hovered != value;
        hovered = value;
        if (closeSurface != null && DockNative.GetCursorPos(out var pointer) && DockNative.GetWindowRect(closeSurface.Handle, out var closeRect))
            DrawClose(closePressed || (pointer.X >= closeRect.Left && pointer.X < closeRect.Right && pointer.Y >= closeRect.Top && pointer.Y < closeRect.Bottom));
        if (changed && surface.CompositionTarget != null)
            surface.CompositionTarget.BackgroundColor = value ? Color.FromRgb(48, 62, 80) : Color.FromRgb(32, 41, 54);
        SyncCloseVisibility();
    }
    private void SyncCloseVisibility()
    {
        if (closeSurface == null || handle == 0) return;
        var show = (hovered || closePressed) && DockNative.IsWindowVisible(surface.Handle);
        if (show != DockNative.IsWindowVisible(closeSurface.Handle)) DockNative.ShowWindow(closeSurface.Handle, show ? 4 : 0);
    }
    private readonly Action click;
    private readonly Action? close;
    private bool pressed;
    private readonly HwndSource? closeSurface;
    private bool closePressed;
    private readonly DrawingVisual closeVisual = new();
    private double closeScale = 1;
    private double drawnScale = double.NaN;
    private bool closeActive;
    private WindowThumbnail(nint handle, nint owner, HwndSource surface, Action click, Action? close)
    {
        this.handle = handle; this.owner = owner; this.surface = surface; this.click = click; this.close = close;
        surface.AddHook(Input);
        if (close != null)
        {
            // A separate owned surface stays above the native DWM image (WPF siblings cannot).
            closeSurface = new HwndSource(new HwndSourceParameters("DeskIsle preview close")
            {
                ParentWindow = surface.Handle, Width = 1, Height = 1,
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08080080
            });
            var disableTransitions = 1;
            DwmSetWindowAttribute(closeSurface.Handle, 3, ref disableTransitions, sizeof(int));
            SetLayeredWindowAttributes(closeSurface.Handle, 0, 225, 2);
            closeSurface.RootVisual = closeVisual;
            closeSurface.AddHook(CloseInput);
            DrawClose(false);
        }
    }
    private void DrawClose(bool active)
    {
        if (closeActive == active && drawnScale == closeScale) return;
        closeActive = active; drawnScale = closeScale;
        using var drawing = closeVisual.RenderOpen();
        drawing.PushTransform(new ScaleTransform(closeScale, closeScale));
        if (closeSurface != null) SetLayeredWindowAttributes(closeSurface.Handle, 0, active ? (byte)255 : (byte)225, 2);
        drawing.DrawRectangle(new SolidColorBrush(active ? Color.FromRgb(176, 64, 75) : Color.FromRgb(27, 35, 47)), null, new Rect(0, 0, 22, 22));
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(240, 244, 250)), 1.5);
        drawing.DrawLine(pen, new Point(6, 6), new Point(16, 16));
        drawing.DrawLine(pen, new Point(16, 6), new Point(6, 16));
        drawing.Pop();
    }
    private nint CloseInput(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x84) { handled = true; return 1; }
        if (message == 0x21) { handled = true; return 3; }
        if (message == 0x201) { closePressed = true; SetCapture(hwnd); DrawClose(true); handled = true; }
        if (message == 0x215) { closePressed = false; DrawClose(false); }
        if (message == 0x202 && closePressed)
        {
            closePressed = false; ReleaseCapture(); DrawClose(false); handled = true;
            if (DockNative.GetCursorPos(out var p) && DockNative.GetWindowRect(hwnd, out var r)
                && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom)
                surface.Dispatcher.BeginInvoke(new Action(() => { if (handle != 0) close?.Invoke(); }));
        }
        return 0;
    }
    private nint Input(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x84) { handled = true; return 1; } // HTCLIENT
        if (message == 0x21) { handled = true; return 3; }
        if (message == 0x201) { pressed = true; SetCapture(hwnd); handled = true;
            if (surface.CompositionTarget != null) surface.CompositionTarget.BackgroundColor = Color.FromRgb(48, 62, 80); }
        if (message == 0x215) { pressed = false; }
        if (message == 0x202 && pressed)
        {
            pressed = false; ReleaseCapture(); handled = true;
            if (ContainsPointer()) surface.Dispatcher.BeginInvoke(new Action(() => { if (handle != 0) click(); }));
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
        if (surface.CompositionTarget != null) surface.CompositionTarget.BackgroundColor = Color.FromRgb(32, 41, 54);
        var disableTransitions = 1;
        DwmSetWindowAttribute(surface.Handle, 3, ref disableTransitions, sizeof(int));
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
            if (!SetWindowPos(surface.Handle, -1, origin.X, origin.Y, boxWidth, boxHeight, 0x10)) return false;
            placement = position;
        }
        if (closeSurface != null)
        {
            var dpi = HwndSource.FromHwnd(owner)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
            var side = Math.Min(Math.Min(boxWidth, boxHeight), (int)Math.Round(22 * dpi));
            var offset = (int)Math.Round(4 * dpi);
            // DrawingVisual coordinates are DIPs; HwndSource supplies the monitor scale.
            closeScale = side / (22 * (closeSurface.CompositionTarget?.TransformToDevice.M11 ?? 1));
            DrawClose(closeActive);
            var x = origin.X + Math.Max(0, boxWidth - side - offset);
            var y = origin.Y + Math.Min(offset, Math.Max(0, boxHeight - side));
            var closeBox = new DockNative.Rect { Left = x, Top = y, Right = x + side, Bottom = y + side };
            if (closePlacement is not { } priorClose || !priorClose.Equals(closeBox))
            {
                if (!SetWindowPos(closeSurface.Handle, -1, x, y, side, side, 0x10)) return false;
                closePlacement = closeBox;
            }
        }
        var inset = Math.Clamp((int)Math.Round(2 * (HwndSource.FromHwnd(owner)?.CompositionTarget?.TransformToDevice.M11 ?? 1)), 2, 8);
        box = new DockNative.Rect { Left = inset, Top = inset, Right = boxWidth - inset, Bottom = boxHeight - inset };
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
    internal void Present()
    {
        if (handle == 0) return;
        if (!DockNative.IsWindowVisible(surface.Handle)) DockNative.ShowWindow(surface.Handle, 4);
        SyncCloseVisibility();
    }
    public void Dispose() { if (handle != 0) { DwmUnregisterThumbnail(handle); handle = 0; closeSurface?.RemoveHook(CloseInput); closeSurface?.Dispose(); surface.RemoveHook(Input); surface.Dispose(); } }
}
