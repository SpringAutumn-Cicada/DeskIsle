using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DeskIsle.Services;

// Floating card placement and desktop return recovery; no Explorer reparenting.
public sealed class DesktopHost
{
    private nint handle;
    public void MoveBy(int dx, int dy)
    {
        if (!GetWindowRect(handle, out var rect)) return;
        var point = new POINT { X = rect.Left + dx, Y = rect.Top + dy };
        SetWindowPos(handle, 0, point.X, point.Y, 0, 0, 0x0015);
    }

    public void RecoverOnDesktop(Window window, nint desktop, bool repaint)
    {
        // Never recreate closed windows, activate cards, or show cards on a different virtual desktop.
        if (handle == 0 || !IsWindow(handle) || window.Visibility != Visibility.Visible) return;
        if (DwmGetWindowAttribute(handle, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return;
        if (desktop == 0)
        {
            // A full-screen app can close onto a smaller app, leaving wallpaper exposed.
            // Refresh already-visible cards without changing visibility, focus or Z-order.
            if (repaint && IsWindowVisible(handle) && !IsIconic(handle))
            {
                window.InvalidateVisual();
                RedrawWindow(handle, 0, 0, 0x0081);
            }
            return;
        }
        var changed = false;
        if (IsIconic(handle) || !IsWindowVisible(handle)) { ShowWindow(handle, 4); changed = true; } // SW_SHOWNOACTIVATE
        if (!IsAboveDesktop(desktop))
        {
            changed = true;
            // Insert immediately above Explorer's foreground desktop, below ordinary applications.
            // HWND_TOPMOST and foreground activation are intentionally never used.
            var previous = GetWindow(desktop, 3); // GW_HWNDPREV
            if (previous != 0 && (GetWindowLongPtr(previous, -20).ToInt64() & 0x8) != 0) previous = 0;
            if (previous != handle) SetWindowPos(handle, previous, 0, 0, 0, 0, 0x0213);
        }
        // Healthy cards need no repaint when Explorer exposes the desktop.
        // Only redraw a card whose visibility or Z-order actually needed repair.
        if (!changed) return;
        StartupDiagnostics.Mark("desktop-card-state-repaired");
        window.InvalidateVisual();
        RedrawWindow(handle, 0, 0, 0x0081); // invalidate this window and its children, asynchronously
    }

    private bool IsAboveDesktop(nint desktop)
    {
        // Compare native Z-order without changing an already-correct floating window.
        for (var next = GetWindow(handle, 2); next != 0; next = GetWindow(next, 2))
            if (next == desktop) return true;
        return false;
    }

    public void SetHandle(Window window) => handle = new WindowInteropHelper(window).EnsureHandle();
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(nint hwnd, nint rect, nint region, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out int value, int size);
}
