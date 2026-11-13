using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace DeskIsle.Dock;

internal sealed record DockWindow(nint Handle, string Title, string? Executable);
internal sealed record DockPin(string Path, string? Executable, string Name);
internal static partial class DockApps
{
    internal static ProcessStartInfo LaunchInfo(string path)
    {
        var executable = Path.GetFileName(path);
        var packagedTerminal = executable.Equals("WindowsTerminal.exe", StringComparison.OrdinalIgnoreCase)
            && path.Contains(@"\WindowsApps\Microsoft.WindowsTerminal", StringComparison.OrdinalIgnoreCase);
        if (!packagedTerminal) return new(path) { UseShellExecute = true };
        // A running Store application's executable path is not a supported launch entry.
        // The per-user execution alias follows package upgrades and preserves the default profile.
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        if (!File.Exists(alias)) throw new IOException("Windows Terminal 启动别名 wt.exe 不可用，请在系统‘应用执行别名’中启用，或将终端快捷方式固定到 Dock。");
        return new(alias) { UseShellExecute = true, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
    }
    internal static bool Supported(string path) => Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".lnk" or ".appref-ms";
    internal static DockPin Resolve(string path)
    {
        string? target = Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? path : null;
        if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            object? shell = null, link = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
                link = ((dynamic)shell!).CreateShortcut(path);
                target = ((dynamic)link).TargetPath as string;
            }
            catch (Exception ex) when (ex is COMException or System.Reflection.TargetInvocationException) { }
            finally { if (link != null) Marshal.FinalReleaseComObject(link); if (shell != null) Marshal.FinalReleaseComObject(shell); }
        }
        return new(path, string.IsNullOrWhiteSpace(target) ? null : target, System.IO.Path.GetFileNameWithoutExtension(path));
    }
    internal static DockWindow[] Windows()
    {
        var list = new List<DockWindow>();
        DockNative.EnumWindows((w, _) =>
        {
            if (!IsAppWindow(w)) return true;
            DockNative.GetWindowThreadProcessId(w, out var pid);
            var text = new StringBuilder(1024); DockNative.GetWindowText(w, text, text.Capacity);
            string? path = ProcessImage(pid);
            try { using var process = Process.GetProcessById((int)pid); path ??= process.MainModule?.FileName; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException) { }
            list.Add(new(w, text.ToString(), path)); return true;
        }, 0);
        return list.ToArray();
    }
    // Shared by app enumeration and attention: background/tool/cloaked windows cannot hold Dock open.
    internal static bool IsAppWindow(nint window)
    {
        if (!DockNative.IsWindow(window) || !DockNative.IsWindowVisible(window) ||
            DockNative.GetWindow(window, 4) != 0 || (DockNative.GetWindowLong(window, -20) & 0x80) != 0) return false;
        DockNative.GetWindowThreadProcessId(window, out var pid);
        if (pid == Environment.ProcessId || DockNative.Class(window) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW") return false;
        var text = new StringBuilder(1024);
        DockNative.GetWindowText(window, text, text.Capacity);
        if (text.Length == 0) return false;
        return DwmGetWindowAttribute(window, 14, out var cloaked, sizeof(int)) != 0 || cloaked == 0;
    }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct FileInfo { public nint Icon; public int Index; public uint Attributes; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string Type; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern nint SHGetFileInfo(string path, uint attributes, ref FileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [StructLayout(LayoutKind.Sequential)] private struct PixelSize { public int Width, Height; }
    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageFactory
    {
        [PreserveSig] int GetImage(PixelSize size, uint flags, out nint bitmap);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, in Guid iid, [MarshalAs(UnmanagedType.Interface)] out IImageFactory factory);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint bitmap);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("user32.dll")] private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] private static extern nint GetClassLongPtr(nint window, int index);
    internal static ImageSource? WindowIcon(nint window, int pixels = 96)
    {
        SendMessageTimeout(window, 0x7F, 1, (nint)Math.Max(96, pixels * 96 / 36), 2, 100, out var icon);
        if (icon == 0) icon = GetClassLongPtr(window, -14);
        if (icon == 0) SendMessageTimeout(window, 0x7F, 2, 0, 2, 100, out icon);
        if (icon == 0) return null;
        // The window owns this HICON. Freeze a copy without destroying the borrowed handle.
        var source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze(); return source;
    }
    internal static ImageSource? Icon(string path, int pixels)
    {
        var initialized = CoInitializeEx(0, 0);
        try { return ExtractIcon(path, pixels); }
        finally { if (initialized >= 0) CoUninitialize(); }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string path, int index, int width, int height, out nint icon, out uint id, uint count, uint flags);
    private static ImageSource? ExecutableIcon(string path, int pixels, int index = 0)
    {
        nint icon = 0;
        try
        {
            var count = PrivateExtractIcons(path, index, pixels, pixels, out icon, out _, 1, 0);
            if (count != 1 || icon == 0) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze(); return image;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or ArgumentException) { return null; }
        finally { if (icon != 0) DestroyIcon(icon); }
    }
    private static ImageSource? ExtractIcon(string path, int pixels)
    {
        // Weixin's Shell image can temporarily become a generic document during notification changes.
        // Prefer its stable executable icon resource; leave other applications' extraction unchanged.
        if (Path.GetFileName(path).Equals("Weixin.exe", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).Equals("WeChat.exe", StringComparison.OrdinalIgnoreCase))
        {
            var executable = ExecutableIcon(path, pixels);
            if (executable != null) return executable;
        }
        IImageFactory? factory = null;
        nint bitmap = 0;
        try
        {
            var iid = typeof(IImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, 0, in iid, out factory) >= 0 &&
                factory.GetImage(new PixelSize { Width = pixels, Height = pixels }, 0x5, out bitmap) >= 0 && bitmap != 0)
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze(); return source;
            }
        }
        catch (COMException) { /* Fall back to the original system icon at its native resolution. */ }
        finally
        {
            if (bitmap != 0) DeleteObject(bitmap);
            if (factory != null) Marshal.FinalReleaseComObject(factory);
        }
        var info = new FileInfo();
        try
        {
            if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<FileInfo>(), 0x100) == 0 || info.Icon == 0) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze(); return image;
        }
        finally { if (info.Icon != 0) DestroyIcon(info.Icon); }
    }
}
