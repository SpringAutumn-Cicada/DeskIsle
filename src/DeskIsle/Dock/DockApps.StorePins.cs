using System.IO;
using System.Runtime.InteropServices;
using Windows.Management.Deployment;
namespace DeskIsle.Dock;

internal static partial class DockApps
{
    private sealed record StoreApp(string Name, string Family, string Alias, string RelativeExecutable)
    {
        internal string ShellPath => @"shell:AppsFolder\" + Family + "!App";
    }
    private static readonly StoreApp[] stableApps =
    [
        new("Microsoft.WindowsTerminal", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "wt.exe", "WindowsTerminal.exe"),
        new("Microsoft.WindowsNotepad", "Microsoft.WindowsNotepad_8wekyb3d8bbwe", "notepad.exe", @"Notepad\Notepad.exe")
    ];
    private static StoreApp? StoreEntry(string path) => stableApps.FirstOrDefault(app =>
        path.Contains(@"\WindowsApps\" + app.Name + "_", StringComparison.OrdinalIgnoreCase) ||
        path.Equals(app.ShellPath, StringComparison.OrdinalIgnoreCase));
    private static bool EntryExists(string path)
    {
        // App execution aliases are reparse entries; File.Exists alone is not launchability.
        try { return (File.GetAttributes(path) & FileAttributes.Directory) == 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }
    private static string? CurrentStoreExecutable(StoreApp app)
    {
        try
        {
            var package = new PackageManager().FindPackagesForUser(string.Empty)
                .FirstOrDefault(p => p.Id.FamilyName.Equals(app.Family, StringComparison.OrdinalIgnoreCase));
            if (package == null) return null;
            var path = Path.Combine(package.InstalledLocation.Path, app.RelativeExecutable);
            return EntryExists(path) ? path : null;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { return null; }
    }
    internal static bool Matches(DockPin pin, DockWindow window)
    {
        if (pin.Executable != null && string.Equals(pin.Executable, window.Executable, StringComparison.OrdinalIgnoreCase)) return true;
        var package = StoreEntry(pin.LaunchPath ?? pin.Executable ?? pin.Path);
        return package != null && window.Executable != null && StoreEntry(window.Executable)?.Family == package.Family;
    }
}
