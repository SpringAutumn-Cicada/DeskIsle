using System.IO;
using System.Runtime.InteropServices;
namespace DeskIsle.Services;

/// <summary>Changes Shell folder flags, keeping the desktop view alive for native file operations.</summary>
public sealed class DesktopIcons : IDisposable
{
    private readonly string marker = Path.Combine(CardStore.DataDirectory, "restore-desktop-icons");
    private bool hiddenByUs;
    public bool RecoveryPending => File.Exists(marker);
    private const string ShellMarker = "DeskIsle desktop folder flags v2";
    private bool? visibleCache;
    private long checkedAt;
    public bool? Visible
    {
        get
        {
            if (Environment.TickCount64 - checkedAt >= 1000 || checkedAt == 0)
            {
                visibleCache = ShellDesktopView.Access(null, out var visible) ? visible : null;
                checkedAt = Environment.TickCount64;
            }
            return visibleCache;
        }
    }
    public bool Hide()
    {
        if (!ShellDesktopView.Access(null, out var visible)) return false;
        if (!visible) return true;
        Directory.CreateDirectory(CardStore.DataDirectory);
        File.WriteAllText(marker, ShellMarker);
        if (!ShellDesktopView.Access(false, out _))
        {
            // Keep recovery information if the call partly succeeded; never force-hide HWNDs.
            return false;
        }
        hiddenByUs = true;
        visibleCache = false; checkedAt = Environment.TickCount64;
        StartupDiagnostics.Mark("desktop-icons-hidden-via-shell");
        return true;
    }
    public bool Restore()
    {
        // Only migrate the legacy version's raw ShowWindow hiding; new hides never use it.
        if (File.Exists(marker) && File.ReadAllText(marker) != ShellMarker)
        {
            var oldView = FindView();
            if (oldView == 0) return false;
            ShowWindow(oldView, 5);
        }
        if (!ShellDesktopView.Access(true, out _)) return false;
        hiddenByUs = false;
        visibleCache = true; checkedAt = Environment.TickCount64;
        if (File.Exists(marker)) File.Delete(marker);
        StartupDiagnostics.Mark("desktop-icons-restored-via-shell");
        return true;
    }
    public void Dispose()
    {
        // A restarted Explorer normally creates a visible icon view; never hide it again.
        if (hiddenByUs || RecoveryPending)
        {
            try { Restore(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    private static nint FindView()
    {
        nint result = 0;
        EnumWindows((top, _) =>
        {
            var desktop = FindWindowEx(top, 0, "SHELLDLL_DefView", null);
            if (desktop != 0) result = FindWindowEx(desktop, 0, "SysListView32", null);
            return result == 0;
        }, 0);
        return result;
    }
    private delegate bool EnumProc(nint hwnd, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string cls, string? name);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
}
