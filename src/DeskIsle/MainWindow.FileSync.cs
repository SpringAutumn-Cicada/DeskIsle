using DeskIsle.Services;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DeskIsle.Core;
namespace DeskIsle;

public partial class MainWindow
{
    private readonly DispatcherTimer fileSync = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
    private bool syncingFiles;
    private bool syncPending;
    private readonly List<(string Old, string New)> renamedFiles = [];
    private int watcherGeneration;
    private int eventQueued;
    private readonly List<FileSystemWatcher> fileWatchers = [];
    private readonly DispatcherTimer eventSync = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };

    private void ScheduleFileSync()
    {
        if (closing) return;
        syncPending = true;
        if (!eventSync.IsEnabled) eventSync.Start();
    }
    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (e is RenamedEventArgs renamed && DesktopFiles.IsCardPath(renamed.OldFullPath) && DesktopFiles.IsCardPath(renamed.FullPath) && !Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(new Action(() => { renamedFiles.Add((renamed.OldFullPath, renamed.FullPath)); ScheduleFileSync(); }));
        QueueFileEvent();
    }
    private void OnWatcherError(object sender, ErrorEventArgs e) => QueueFileEvent();
    private void QueueFileEvent()
    {
        // Native notifications run on worker threads; queue at most one UI callback.
        if (Interlocked.Exchange(ref eventQueued, 1) != 0) return;
        if (Dispatcher.HasShutdownStarted) { Interlocked.Exchange(ref eventQueued, 0); return; }
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref eventQueued, 0);
            ScheduleFileSync();
        }));
    }
    private async void RebuildFileWatchers()
    {
        var generation = ++watcherGeneration;
        var folders = (directory == null ? state.References.Where(DesktopFiles.IsCardPath).Select(Path.GetDirectoryName).Append(DesktopFiles.Root) : new[] { directory })
            .Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var replacements = await Task.Run(() =>
        {
            var result = new List<FileSystemWatcher>();
            foreach (var folder in folders)
            {
                FileSystemWatcher? watcher = null;
                try
                {
                    watcher = new FileSystemWatcher(folder!) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes };
                    watcher.Created += OnFileEvent;
                    watcher.Deleted += OnFileEvent;
                    watcher.Renamed += OnFileEvent;
                    watcher.Changed += OnFileEvent;
                    watcher.Error += OnWatcherError;
                    watcher.EnableRaisingEvents = true;
                    result.Add(watcher);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
                { watcher?.Dispose(); }
            }
            return result;
        });
        if (closing || generation != watcherGeneration)
        { foreach (var watcher in replacements) watcher.Dispose(); return; }
        foreach (var watcher in fileWatchers) watcher.Dispose();
        fileWatchers.Clear();
        fileWatchers.AddRange(replacements);
        // Catch a mutation between taking the initial snapshot and installing watchers.
        ScheduleFileSync();
    }
    private void StopFileSync()
    {
        ++watcherGeneration;
        fileSync.Stop(); eventSync.Stop();
        foreach (var watcher in fileWatchers) watcher.Dispose();
        fileWatchers.Clear();
    }

    private static FileTile[] ReadReferences(string[] paths)
    {
        // Only a successfully enumerated parent proves a referenced item disappeared.
        // An unavailable drive/share must not be treated as a deletion.
        var parents = new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);
        return paths.Where(DesktopFiles.Displayable).Where(path =>
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
        { Availability = File.Exists(path) || Directory.Exists(path) ? "" : "桌面路径暂不可用" }).ToArray();
    }

    private async void SyncFiles(object? sender, EventArgs e)
    {
        if (closing) return;
        if (syncingFiles || HasPendingCreation || IsReordering || loading != null || NewItemPanel.Visibility == Visibility.Visible)
        { if (syncPending) { eventSync.Interval = TimeSpan.FromMilliseconds(100); eventSync.Start(); } return; }
        syncPending = false;
        syncingFiles = true;
        var version = viewVersion;
        var folder = directory;
        UpdateMigrationNotice();
        var paths = state.References.Where(DesktopFiles.IsCardPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        try
        {
            if (renamedFiles.Count > 0)
            {
                var changes = renamedFiles.ToArray(); renamedFiles.Clear();
                var verified = await Task.Run(() => changes.Where(change =>
                {
                    try { DesktopFiles.Require(change.New); return !File.Exists(change.Old) && !Directory.Exists(change.Old); }
                    catch (Exception ex) when (FileTransfer.Recoverable(ex)) { return false; }
                }).ToArray());
                if (HasPendingCreation) { renamedFiles.AddRange(verified); ScheduleFileSync(); return; }
                foreach (var change in verified) workspace.RecordDesktopRename(change.Old, change.New);
                if (verified.Length > 0) { ScheduleFileSync(); return; }
            }
            var updated = await Task.Run(() => folder == null ? ReadReferences(paths) : FolderReader.Read(folder, CancellationToken.None).Where(item => DesktopFiles.Displayable(item.FullPath)).Select(x => new FileTile(x)).ToArray());
            if (closing) return;
            if (version != viewVersion || folder != directory || IsReordering || HasPendingCreation || !paths.SequenceEqual(state.References.Where(DesktopFiles.IsCardPath).Distinct(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            { ScheduleFileSync(); return; }
            // Retain the user's custom order and avoid rebuilding unchanged controls.
            var byPath = updated.GroupBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var existing = tiles.Select(x => x.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ordered = tiles.Where(x => byPath.ContainsKey(x.FullPath)).Select(x => byPath[x.FullPath])
                .Concat(updated.Where(x => !existing.Contains(x.FullPath))).ToArray();
            if (tiles.Select(x => (x.FullPath, x.Availability)).SequenceEqual(ordered.Select(x => (x.FullPath, x.Availability)))) return;
            ClearReorderPreview();
            tiles = ordered;
            selectedTile = null;
            RebuildRows();
            Status.Text = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { /* Preserve the view during temporary access failures; retry on the next tick. */ }
        finally { syncingFiles = false; if (syncPending && !closing) ScheduleFileSync(); }
    }
}
