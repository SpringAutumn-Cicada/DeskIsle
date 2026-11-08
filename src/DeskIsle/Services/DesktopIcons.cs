using System.IO;
using System.Runtime.InteropServices;
namespace DeskIsle.Services;

/// <summary>Only changes Explorer's icon-view visibility, never file attributes or registry settings.</summary>
public sealed class DesktopIcons : IDisposable
{
    private readonly string marker = Path.Combine(CardStore.DataDirectory, "restore-desktop-icons");
    private bool hiddenByUs;
    public bool RecoveryPending => File.Exists(marker);
    public bool? Visible
    {
        get { var view = FindView(); return view == 0 ? null : IsWindowVisible(view); }
    }
    public bool Hide()
    {
        var view = FindView();
        if (view == 0) return false;
        if (!IsWindowVisible(view)) return true;
        Directory.CreateDirectory(CardStore.DataDirectory);
        File.WriteAllText(marker, "DeskIsle hid the desktop icon view; show it to recover.");
        ShowWindow(view, 0);
        if (IsWindowVisible(view)) { File.Delete(marker); return false; }
        hiddenByUs = true;
        return true;
    }
    public bool Restore()
    {
        var view = FindView();
        if (view == 0) return false;
        ShowWindow(view, 5);
        if (!IsWindowVisible(view)) return false;
        hiddenByUs = false;
        if (File.Exists(marker)) File.Delete(marker);
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
