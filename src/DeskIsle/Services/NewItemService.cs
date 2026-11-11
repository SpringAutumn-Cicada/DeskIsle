using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
namespace DeskIsle.Services;

public enum NewItemKind { Folder, Text, Markdown, Word, Excel, Pdf, PowerPoint }

public static class NewItemService
{
    public static string Extension(NewItemKind kind) => kind switch { NewItemKind.Text => ".txt", NewItemKind.Markdown => ".md", NewItemKind.Word => ".docx", NewItemKind.Excel => ".xlsx", NewItemKind.Pdf => ".pdf", NewItemKind.PowerPoint => ".pptx", _ => "" };
    public static string Label(NewItemKind kind) => kind switch { NewItemKind.Folder => "文件夹", NewItemKind.Text => "文本文档", NewItemKind.Markdown => "Markdown 文档", NewItemKind.Word => "Word 文档", NewItemKind.Excel => "Excel 工作簿", NewItemKind.Pdf => "PDF 文档", NewItemKind.PowerPoint => "PowerPoint 演示文稿", _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
    public static string SuggestedName(string directory, NewItemKind kind)
    {
        var stem = "新建" + Label(kind);
        var extension = Extension(kind);
        for (var index = 1; index < 10000; index++)
        {
            var name = stem + (index == 1 ? "" : $" ({index})") + extension;
            var path = Path.Combine(directory, name);
            if (!File.Exists(path) && !Directory.Exists(path)) return name;
        }
        throw new IOException("同名项目过多，请手动输入其他名称。");
    }
    public static string ValidateName(string name, NewItemKind kind)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("请输入名称。");
        if (name.Length > 200) throw new ArgumentException("名称过长，请缩短到200个字符以内。");
        if (name is "." or ".." || name.EndsWith(' ') || name.EndsWith('.') || name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c))) throw new ArgumentException("名称不能包含特殊字符，或以空格、句点结尾。");
        var stem = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3])) throw new ArgumentException("这是Windows保留名称，请使用其他名称。");
        var extension = Extension(kind);
        if (extension.Length > 0 && !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"请保留 {extension} 文件扩展名。");
        return name;
    }
    public static string Create(string directory, string name, NewItemKind kind)
    {
        DesktopFiles.Require(directory);
        return CreateCore(directory, name, kind);
    }
    public static (string Path, string Work) CreateDraft(string name, NewItemKind kind)
    {
        ValidateName(name, kind);
        var work = DesktopFiles.WorkDirectory();
        return (CreateCore(work, name, kind), work);
    }
    private static string CreateCore(string directory, string name, NewItemKind kind)
    {
        ValidateName(name, kind);
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) throw new DirectoryNotFoundException("保存目录不可用，请重新选择。");
        var path = Path.Combine(Path.GetFullPath(directory), name);
        if (kind == NewItemKind.Folder)
        {
            // Unlike Directory.CreateDirectory, this fails atomically if the name already exists.
            if (!CreateDirectory(path, 0))
            {
                var error = Marshal.GetLastWin32Error();
                if (error is 80 or 183) throw new IOException("同名项目已存在，请更改名称。");
                if (error == 5) throw new UnauthorizedAccessException("没有在该目录创建文件夹的权限。");
                throw new IOException("无法创建文件夹，请检查目录是否可用。", new Win32Exception(error));
            }
        }
        else
        {
            // Real format templates are embedded source assets, not empty files
            // renamed with an Office/PDF extension. No Office installation needed.
            using var template = kind is NewItemKind.Word or NewItemKind.Excel or NewItemKind.Pdf or NewItemKind.PowerPoint
                ? typeof(NewItemService).Assembly.GetManifestResourceStream("DeskIsle.Templates.Blank" + Extension(kind))
                    ?? throw new IOException("内置文档模板不可用。")
                : null;
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            template?.CopyTo(file);
        }
        return path;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectory(string path, nint securityAttributes);
}
