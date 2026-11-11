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
internal static class DockApps
{
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
            if (!DockNative.IsWindowVisible(w) || DockNative.GetWindow(w, 4) != 0 || (DockNative.GetWindowLong(w, -20) & 0x80) != 0) return true;
            DockNative.GetWindowThreadProcessId(w, out var pid);
            if (pid == Environment.ProcessId || DockNative.Class(w) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW") return true;
            var text = new StringBuilder(1024); DockNative.GetWindowText(w, text, text.Capacity);
            if (text.Length == 0) return true;
            // Cloaked UWP/virtual-desktop windows must not appear as active applications.
            if (DwmGetWindowAttribute(w, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            string? path = null;
            try { using var process = Process.GetProcessById((int)pid); path = process.MainModule?.FileName; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException) { }
            list.Add(new(w, text.ToString(), path)); return true;
        }, 0);
        return list.ToArray();
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
    [StructLayout(LayoutKind.Sequential)] private struct NativeBitmap
    { public int Type, Width, Height, Stride; public ushort Planes, BitsPerPixel; public nint Bits; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader
    { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, ImageSize; public int XPels, YPels; public uint Colors, Important; }
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObject(nint bitmap, int size, out NativeBitmap value);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint count, byte[] bits, ref BitmapHeader info, uint usage);
    private static BitmapSource ReadIconBitmap(nint bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<NativeBitmap>(), out var native) == 0 || native.Width <= 0 || native.Height <= 0 || native.Width > 4096 || native.Height > 4096)
            throw new IOException("图标位图尺寸无效。");
        var stride = checked(native.Width * 4);
        var data = new byte[checked(stride * native.Height)];
        var header = new BitmapHeader { Size = (uint)Marshal.SizeOf<BitmapHeader>(), Width = native.Width, Height = -native.Height, Planes = 1, BitCount = 32 };
        var dc = CreateCompatibleDC(0);
        if (dc == 0) throw new IOException("无法读取图标像素。");
        try
        {
            if (GetDIBits(dc, bitmap, 0, (uint)native.Height, data, ref header, 0) != native.Height) throw new IOException("图标像素读取不完整。");
        }
        finally { DeleteDC(dc); }
        // Legacy icons can omit alpha entirely. Modern Shell ARGB icons retain their transparency.
        var hasAlpha = false;
        for (var i = 3; i < data.Length; i += 4) if (data[i] != 0) { hasAlpha = true; break; }
        if (!hasAlpha) for (var i = 3; i < data.Length; i += 4) data[i] = 255;
        var source = BitmapSource.Create(native.Width, native.Height, 96, 96, PixelFormats.Pbgra32, null, data, stride);
        source.Freeze(); return source;
    }
    internal static ImageSource? WindowIcon(nint window)
    {
        SendMessageTimeout(window, 0x7F, 1, 0, 2, 100, out var icon);
        if (icon == 0) icon = GetClassLongPtr(window, -14);
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
    private static ImageSource? ExtractIcon(string path, int pixels)
    {
        IImageFactory? factory = null;
        nint bitmap = 0;
        try
        {
            var iid = typeof(IImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, 0, in iid, out factory) >= 0 &&
                factory.GetImage(new PixelSize { Width = pixels, Height = pixels }, 0x5, out bitmap) >= 0 && bitmap != 0)
            {
                return ReadIconBitmap(bitmap);
            }
        }
        catch (Exception ex) when (ex is COMException or IOException) { /* Fall back to the original system icon at its native resolution. */ }
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
