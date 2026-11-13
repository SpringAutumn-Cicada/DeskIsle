using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
namespace DeskIsle.Dock;

internal static partial class DockApps
{
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    private static string? ProcessImage(uint pid)
    {
        var process = OpenProcess(0x1000, false, pid); // Query-limited information, including many elevated apps.
        if (process == 0) return null;
        try
        {
            var name = new StringBuilder(32768); uint size = (uint)name.Capacity;
            return QueryFullProcessImageName(process, 0, name, ref size) ? name.ToString() : null;
        }
        finally { CloseHandle(process); }
    }
    private static bool HostIcon(string? path) => Path.GetFileName(path)?.ToLowerInvariant() is "mmc.exe" or "applicationframehost.exe";
    internal static string IconCacheKey(string? path, DockWindow[] windows) =>
        (path ?? "window") + (path == null || HostIcon(path) ? "|" + string.Join(";", windows.Select(w => $"{w.Handle}:{w.Title}")) : "");
    private static bool SettingsWindow(string? path, DockWindow[] windows) =>
        Path.GetFileName(path)?.Equals("SystemSettings.exe", StringComparison.OrdinalIgnoreCase) == true ||
        (path == null || Path.GetFileName(path)?.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) == true) &&
            windows.Any(w => w.Title is "设置" or "Settings");
    internal static ImageSource? ApplicationIcon(string? path, DockWindow[] windows, int pixels)
    {
        if (SettingsWindow(path, windows))
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ImmersiveControlPanel", "Images");
            // Prefer unplated native assets, not a small EXE or a high-contrast monochrome variant.
            foreach (var name in new[] { "logo.targetsize-256_altform-unplated.png", "logo.targetsize-256.png", "logo.scale-400.png", "logo.png" })
            {
                var image = ReadBitmap(Path.Combine(folder, name), pixels);
                if (image != null) return image;
            }
        }
        if (Path.GetFileName(path)?.Equals("mmc.exe", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (windows.Any(w => w.Title.Contains("磁盘管理", StringComparison.OrdinalIgnoreCase) || w.Title.Contains("Disk Management", StringComparison.OrdinalIgnoreCase)))
            {
                var image = ManagementIcon("diskmgmt.msc", pixels);
                if (image != null) return image;
            }
            // Each MMC snap-in sets its own icon. The host executable's icon is unrelated.
            foreach (var window in windows)
            {
                var image = WindowIcon(window.Handle, pixels);
                if (image != null) return image;
            }
        }
        if (path != null)
        {
            var image = Icon(path, pixels);
            if (image != null) return image;
        }
        foreach (var window in windows)
        {
            var image = WindowIcon(window.Handle, pixels);
            if (image != null) return image;
        }
        return null;
    }
    private static ImageSource? ManagementIcon(string console, int pixels)
    {
        try
        {
            var document = XDocument.Load(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), console));
            var icon = document.Root?.Element("VisualAttributes")?.Element("Icon");
            var file = (string?)icon?.Attribute("File");
            if (file == null || !int.TryParse((string?)icon?.Attribute("Index"), out var index)) return null;
            return ExecutableIcon(Environment.ExpandEnvironmentVariables(file), pixels, index);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or ArgumentException) { return null; }
    }
    private static ImageSource? ReadBitmap(string path, int pixels)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage(); image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = pixels; image.StreamSource = stream;
            image.EndInit(); image.Freeze(); return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { return null; }
    }
}
