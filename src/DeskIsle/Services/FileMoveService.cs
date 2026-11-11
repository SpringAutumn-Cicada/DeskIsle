using System.IO;
namespace DeskIsle.Services;

/// <summary>Path identity and preflight for desktop-only transfers. FileTransfer owns all writes.</summary>
public static class FileMoveService
{
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static bool Same(string left, string right) => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    public static bool Under(string path, string parent) => Normalize(path).StartsWith(Normalize(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static string? QuickError(string source, string folder)
    {
        if (Same(source, folder)) return "不能移入自身";
        if (Under(folder, source)) return "不能移入自己的子目录";
        var parent = Path.GetDirectoryName(Normalize(source));
        return parent != null && Same(parent, folder) ? "项目已在此文件夹中" : null;
    }
    public static string Validate(string source, string folder, string name)
    {
        NewItemService.ValidateName(name, NewItemKind.Folder);
        source = Normalize(source); folder = Normalize(folder);
        if (QuickError(source, folder) is { } reason) throw new IOException(reason);
        if (Path.GetPathRoot(source) is { } root && Same(root, source)) throw new IOException("不能移动磁盘根目录。");
        DesktopFiles.Require(source);
        DesktopFiles.Require(folder);
        var destination = Path.Combine(folder, name);
        DesktopFiles.Require(destination, missingAllowed: true);
        return destination;
    }
    public static string Remap(string path, string source, string destination)
    {
        if (Same(path, source)) return destination;
        return Under(path, source) ? destination + Normalize(path)[Normalize(source).Length..] : path;
    }
}
