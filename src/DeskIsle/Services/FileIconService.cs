using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskIsle.Services;

/// <summary>Serial STA Shell worker; bounded cache. No file contents or thumbnails are read.</summary>
public sealed class FileIconService : IDisposable
{
    private readonly BlockingCollection<(string Key, TaskCompletionSource<ImageSource?> Result)> queue = new(128);
    private readonly Dictionary<string, ImageSource?> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Thread worker;
    private bool disposed;
    public FileIconService()
    {
        worker = new Thread(Run) { IsBackground = true, Name = "DeskIsle icons" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }
    public Task<ImageSource?> GetAsync(bool directory, string path)
    {
        // Type icons intentionally avoid touching remote/offline file contents.
        var key = directory ? "folder" : System.IO.Path.GetExtension(path).ToLowerInvariant();
        var result = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (disposed || !queue.TryAdd((key, result))) result.TrySetResult(null);
        return result.Task;
    }
    private void Run()
    {
        var initialized = CoInitializeEx(0, 2) >= 0;
        try
        {
            foreach (var (key, result) in queue.GetConsumingEnumerable())
            {
                try
                {
                    if (!cache.TryGetValue(key, out var image))
                    {
                        var info = new SHFILEINFO();
                        var icon = SHGetFileInfo(key == "folder" ? "folder" : "file" + key,
                            key == "folder" ? 0x10u : 0x80u, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x100 | 0x10);
                        if (icon != 0 && info.Icon != 0)
                        {
                            try
                            {
                                image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(48, 48));
                                image.Freeze();
                            }
                            finally { DestroyIcon(info.Icon); }
                        }
                        if (cache.Count >= 256) cache.Clear();
                        cache[key] = image;
                    }
                    result.TrySetResult(image);
                }
                catch { result.TrySetResult(null); }
            }
        }
        finally { if (initialized) CoUninitialize(); }
    }
    public void Dispose() { if (disposed) return; disposed = true; queue.CompleteAdding(); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public nint Icon; public int Index; public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern nint SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
}
