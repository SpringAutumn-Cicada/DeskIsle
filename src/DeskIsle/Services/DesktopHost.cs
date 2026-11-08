using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DeskIsle.Services;

// Explorer's WorkerW hierarchy is undocumented. All dependency on it stays here.
public sealed class DesktopHost
{
    private nint parent;
    private nint originalStyle;
    private nint handle;
    public bool Attached => parent != 0;
    public bool IsAlive => !Attached || IsWindow(parent);

    public bool TryAttach(Window window)
    {
        Detach();
        handle = new WindowInteropHelper(window).EnsureHandle();
        var progman = FindWindow("Progman", null);
        if (progman == 0) return false;
        SendMessageTimeout(progman, 0x052C, 0, 0, 2, 1000, out _);
        nint worker = 0;
        EnumWindows((top, _) =>
        {
            if (FindWindowEx(top, 0, "SHELLDLL_DefView", null) != 0)
                worker = FindWindowEx(0, top, "WorkerW", null);
            return worker == 0;
        }, 0);
        if (worker == 0 || !GetWindowRect(handle, out var rect)) return false;
        originalStyle = GetWindowLongPtr(handle, -16);
        SetWindowLongPtr(handle, -16, (nint)(((long)originalStyle & ~0x80000000L) | 0x40000000L));
        SetParent(handle, worker);
        if (GetParent(handle) != worker)
        {
            SetWindowLongPtr(handle, -16, originalStyle);
            return false;
        }
        parent = worker;
        var point = new POINT { X = rect.Left, Y = rect.Top };
        ScreenToClient(parent, ref point);
        SetWindowPos(handle, 0, point.X, point.Y, 0, 0, 0x0035);
        return true;
    }

    public void Detach()
    {
        if (!Attached) return;
        GetWindowRect(handle, out var rect);
        SetParent(handle, 0);
        SetWindowLongPtr(handle, -16, originalStyle);
        SetWindowPos(handle, 0, rect.Left, rect.Top, 0, 0, 0x0035);
        parent = 0;
    }

    public void MoveBy(int dx, int dy)
    {
        if (!GetWindowRect(handle, out var rect)) return;
        var point = new POINT { X = rect.Left + dx, Y = rect.Top + dy };
        if (Attached) ScreenToClient(parent, ref point);
        SetWindowPos(handle, 0, point.X, point.Y, 0, 0, 0x0015);
    }

    public void SetHandle(Window window) => handle = new WindowInteropHelper(window).EnsureHandle();
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    private delegate bool EnumProc(nint hwnd, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string cls, string? name);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint data);
    [DllImport("user32.dll")] private static extern nint SendMessageTimeout(nint hwnd, uint msg, nint w, nint l, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll")] private static extern nint SetParent(nint child, nint parent);
    [DllImport("user32.dll")] private static extern nint GetParent(nint child);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint hwnd, ref POINT point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
}
