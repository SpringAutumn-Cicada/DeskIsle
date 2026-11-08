namespace DeskIsle.Core;

public sealed record FolderEntry(string Name, string FullPath, bool IsDirectory);

public static class FolderReader
{
    public static IReadOnlyList<FolderEntry> Read(string path, CancellationToken cancellationToken)
    {
        var entries = new List<FolderEntry>();
        foreach (var item in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((item.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
            entries.Add(new(item.Name, item.FullName, (item.Attributes & FileAttributes.Directory) != 0));
        }
        return entries.OrderByDescending(x => x.IsDirectory)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
