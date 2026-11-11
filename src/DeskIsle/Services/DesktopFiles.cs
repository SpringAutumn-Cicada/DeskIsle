using System.IO;
namespace DeskIsle.Services;

/// <summary>All card business files must physically live below the actual Windows desktop.</summary>
public static class DesktopFiles
{
    public static string Root => FileMoveService.Normalize(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
    public const string PrivateName = ".DeskIsle-data";
    public static string PrivateRoot => Path.Combine(Root, PrivateName);
    public static bool Within(string path) => FileMoveService.Same(path, Root) || FileMoveService.Under(path, Root);
    public static bool Private(string path) => FileMoveService.Same(path, PrivateRoot) || FileMoveService.Under(path, PrivateRoot);
    public static bool IsCardPath(string path)
    {
        try { return Within(path) && !Private(path) && !FileMoveService.Same(path, Root); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }
    public static void Require(string path, bool missingAllowed = false)
    {
        path = FileMoveService.Normalize(path);
        if (!Within(path) || Private(path)) throw new IOException("卡片文件必须位于实际桌面或其普通子目录中。");
        for (string? part = path; part != null && !FileMoveService.Same(part, Root); part = Path.GetDirectoryName(part))
        {
            try
            {
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new IOException("不支持链接、目录联接或云占位路径，请先将实际文件下载到桌面。");
            }
            catch (FileNotFoundException) when (missingAllowed) { }
            catch (DirectoryNotFoundException) when (missingAllowed) { }
        }
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException("Windows桌面目录不可用。");
    }
    public static bool Displayable(string path)
    {
        if (!IsCardPath(path)) return false;
        try { Require(path, missingAllowed: true); return true; }
        catch (UnauthorizedAccessException) { return true; } // Retain inaccessible desktop references, never open external ones.
        catch (IOException) { return false; }
    }
    public static void RequireSource(string path)
    {
        if (Within(path)) { Require(path); return; }
        for (string? part = FileMoveService.Normalize(path); part != null; part = Path.GetDirectoryName(part))
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new IOException("来源经过链接或云占位路径，请先下载实际文件再导入。");
    }
    public static string WorkDirectory()
    {
        var marker = Path.Combine(PrivateRoot, "owner.txt");
        if (Directory.Exists(PrivateRoot))
        {
            if (!File.Exists(marker) || File.ReadAllText(marker) != "DeskIsle private transfer storage v1")
                throw new IOException("桌面上的.DeskIsle-data目录已被占用；未改动该目录。");
            if ((File.GetAttributes(PrivateRoot) & FileAttributes.ReparsePoint) != 0) throw new IOException("内部恢复目录不能是链接。");
        }
        else
        {
            Directory.CreateDirectory(PrivateRoot);
            File.WriteAllText(marker, "DeskIsle private transfer storage v1");
            File.SetAttributes(PrivateRoot, File.GetAttributes(PrivateRoot) | FileAttributes.Hidden);
        }
        var work = Path.Combine(PrivateRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        return work;
    }
}
