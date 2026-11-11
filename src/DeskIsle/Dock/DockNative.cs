using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
namespace DeskIsle.Dock;

internal static class DockNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    internal delegate bool EnumProc(nint window, nint param);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, nint param);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint window, StringBuilder name, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint window, StringBuilder name, int size);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] internal static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint window, int id);
    internal static string Class(nint window) { var s = new StringBuilder(256); GetClassName(window, s, s.Capacity); return s.ToString(); }
    internal static MonitorInfo Primary()
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(MonitorFromPoint(new Point(), 1), ref info);
        return info;
    }
    internal static nint[] Taskbars()
    {
        var result = new List<nint>();
        EnumWindows((w, _) => { if (Class(w) == "Shell_TrayWnd") result.Add(w); return true; }, 0);
        return result.ToArray();
    }
    internal static bool FullScreen()
    {
        var w = GetForegroundWindow();
        if (w == 0 || IsIconic(w) || Class(w) is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        GetWindowThreadProcessId(w, out var pid);
        if (pid == Environment.ProcessId) return false;
        var monitor = MonitorFromWindow(w, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info) || (info.Flags & 1) == 0 || !GetWindowRect(w, out var r)) return false;
        return r.Left <= info.Monitor.Left && r.Top <= info.Monitor.Top && r.Right >= info.Monitor.Right && r.Bottom >= info.Monitor.Bottom;
    }
}
