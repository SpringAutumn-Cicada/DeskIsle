using System.IO;
using System.Windows;
using System.Windows.Threading;
using DeskIsle.Core;
namespace DeskIsle;

public partial class MainWindow
{
    private readonly DispatcherTimer fileSync = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
    private bool syncingFiles;

    private static FileTile[] ReadReferences(string[] paths)
    {
        // Only a successfully enumerated parent proves a referenced item disappeared.
        // An unavailable drive/share must not be treated as a deletion.
        var parents = new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);
        return paths.Where(path =>
        {
            var parent = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(parent)) return true;
            if (!parents.TryGetValue(parent, out var children))
            {
                try { children = Directory.EnumerateFileSystemEntries(parent).ToHashSet(StringComparer.OrdinalIgnoreCase); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { children = null; }
                parents[parent] = children;
            }
            return children == null || children.Contains(path);
        }).Select(path => new FileTile(new FolderEntry(
            Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : path,
            path, Directory.Exists(path)))
        { Availability = File.Exists(path) || Directory.Exists(path) ? "" : "路径不可用 · 可右键重新定位" }).ToArray();
    }

    private async void SyncFiles(object? sender, EventArgs e)
    {
        if (syncingFiles || closing || HasPendingCreation || IsReordering || loading != null || NewItemPanel.Visibility == Visibility.Visible) return;
        syncingFiles = true;
        var version = viewVersion;
        var folder = directory;
        var paths = state.References.ToArray();
        try
        {
            var updated = await Task.Run(() => folder == null ? ReadReferences(paths) : FolderReader.Read(folder, CancellationToken.None).Select(x => new FileTile(x)).ToArray());
            if (closing || version != viewVersion || folder != directory || IsReordering || HasPendingCreation || !paths.SequenceEqual(state.References, StringComparer.OrdinalIgnoreCase)) return;
            // Retain the user's custom order and avoid rebuilding unchanged controls.
            var byPath = updated.ToDictionary(x => x.FullPath, StringComparer.OrdinalIgnoreCase);
            var existing = tiles.Select(x => x.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ordered = tiles.Where(x => byPath.ContainsKey(x.FullPath)).Select(x => byPath[x.FullPath])
                .Concat(updated.Where(x => !existing.Contains(x.FullPath))).ToArray();
            if (tiles.Select(x => (x.FullPath, x.Availability)).SequenceEqual(ordered.Select(x => (x.FullPath, x.Availability)))) return;
            ClearReorderPreview();
            tiles = ordered;
            selectedTile = null;
            RebuildRows();
            Status.Text = tiles.Count == 0 ? (folder == null ? "拖入文件或文件夹" : "目录为空。") : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { /* Preserve the view during temporary access failures; retry on the next tick. */ }
        finally { syncingFiles = false; }
    }
}
