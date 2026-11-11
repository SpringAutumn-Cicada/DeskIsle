using System.IO;
namespace DeskIsle.Services;

/// <summary>One explicit, non-overwriting filesystem move. Never merge or recursively copy/delete.</summary>
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
        var attributes = File.GetAttributes(source);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("目标文件夹不可用。");
        RejectReparseAncestors(source);
        RejectReparseAncestors(folder);
        var destination = Path.Combine(folder, name);
        // GetAttributes distinguishes absence from access failure, including hidden/dangling links.
        try { File.GetAttributes(destination); throw new IOException("同名项目已存在，请更改名称后重试；不会覆盖或合并。"); }
        catch (FileNotFoundException) { }
        if ((attributes & FileAttributes.Directory) != 0 && !string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
            throw new IOException("暂不支持跨磁盘移动文件夹，请使用资源管理器。文件未移动。");
        return destination;
    }
    private static void RejectReparseAncestors(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("暂不支持经符号链接、目录联接或云占位路径移动，请使用资源管理器。");
        }
    }
    public static string Move(string source, string folder, string name)
    {
        var destination = Validate(source, folder, name); // Recheck after confirmation; never trust an old preview.
        if ((File.GetAttributes(source) & FileAttributes.Directory) != 0) Directory.Move(source, destination);
        else File.Move(source, destination, overwrite: false);
        return destination;
    }
    public static string Remap(string path, string source, string destination)
    {
        if (Same(path, source)) return destination;
        return Under(path, source) ? destination + Normalize(path)[Normalize(source).Length..] : path;
    }
}
