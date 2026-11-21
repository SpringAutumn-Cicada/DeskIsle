using System.IO;
namespace DeskIsle.Services;

public sealed partial class WorkspaceManager
{
    private bool scanningDesktop;
    private long desktopChangedAt;
    private long desktopRevision;
    private readonly List<(string Source, string Destination)> desktopRenames = [];
    private readonly System.Windows.Threading.DispatcherTimer defaultSettle = new(System.Windows.Threading.DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(750) };
    internal void NotifyDesktopMutation()
    {
        if (!DesktopIntegration || Exiting || store.ReadOnly) return;
        ++desktopRevision;
        desktopChangedAt = Environment.TickCount64;
        defaultSettle.Stop(); defaultSettle.Start();
    }
    internal void NotifyDesktopRename(string source, string destination)
    {
        if (!DesktopIntegration || Exiting || store.ReadOnly) return;
        desktopRenames.Add((source, destination));
        NotifyDesktopMutation();
    }

    public bool IsDefaultCard(Guid id) => state.DefaultCardId == id;
    public void SetDefaultCard(Guid id)
    {
        if (store.ReadOnly) throw new IOException("配置处于保护模式，无法修改默认卡片。");
        if (state.DefaultCardId == id || !state.Cards.Any(c => c.Id == id)) return;
        ChangeWorkspace(() => state.DefaultCardId = id);
        NotifyDesktopMutation();
    }
    internal void MoveToDefaultCard(Guid sourceId, string path) =>
        TransferCardReference(sourceId, DefaultCard.Id, path);

    public void TransferCardReference(Guid sourceId, Guid targetId, string path)
    {
        if (Exiting || FileMoveInProgress || windows.Values.Any(w => w.HasPendingCreation || w.IsReordering)) throw new IOException("请先完成当前文件操作。");
        if (store.ReadOnly) throw new IOException("配置处于保护模式，无法转移文件分组。");
        var source = state.Cards.FirstOrDefault(c => c.Id == sourceId);
        var target = state.Cards.FirstOrDefault(c => c.Id == targetId);
        if (source == null || target == null || source == target || !source.References.Any(item => FileMoveService.Same(item, path))) return;
        DesktopFiles.Require(path);
        if (!DesktopFiles.IsDesktopItem(path) || !File.Exists(path) && !Directory.Exists(path)) throw new IOException("文件不可用，未改变分组。");
        ChangeWorkspace(() => AssignDesktopItems(target, [path]));
    }
    private bool DesktopOperationInProgress =>
        FileMoveInProgress || windows.Values.Any(window => window.HasPendingCreation || window.IsReordering);

    public async Task SyncDefaultDesktop()
    {
        if (!DesktopIntegration || Exiting || store.ReadOnly || scanningDesktop) return;
        if (DesktopOperationInProgress || Environment.TickCount64 - desktopChangedAt < 700)
        {
            defaultSettle.Start();
            return;
        }

        scanningDesktop = true;
        var revision = desktopRevision;
        var renames = desktopRenames.Distinct().ToArray();
        try
        {
            var (paths, verifiedRenames) = await Task.Run(() =>
            {
                var entries = Directory.GetFileSystemEntries(DesktopFiles.Root);
                var visible = entries.Where(DesktopFiles.VisibleDesktopItem).Select(FileMoveService.Normalize)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return (visible, VerifyDesktopRenames(renames, entries, visible));
            });
            if (Exiting || revision != desktopRevision) return;
            if (DesktopOperationInProgress) { defaultSettle.Start(); return; }

            // Read ownership on the dispatcher, after enumeration, so a drag or import can win.
            if (verifiedRenames.Length > 0 || !IsDesktopCoverageCurrent(paths))
            {
                ChangeWorkspace(() =>
                {
                    foreach (var (source, destination) in verifiedRenames)
                        RenameWorkspaceItem(source, destination);

                    var assigned = AssignedDesktopPaths();
                    var missing = paths.Where(path => !assigned.Contains(path)).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
                    DefaultCard.References.AddRange(missing);
                    state.SeenDesktopPaths = paths.ToList();
                });
            }
            desktopRenames.Clear();
            ReportDesktopEnumeration();
        }
        catch (Exception ex) when (FileTransfer.Recoverable(ex))
        { StartupDiagnostics.Mark("default-desktop-sync-error=" + ex.GetType().Name); }
        finally
        {
            scanningDesktop = false;
            if (!Exiting && revision != desktopRevision) defaultSettle.Start();
        }
    }

    private bool IsDesktopCoverageCurrent(HashSet<string> paths)
    {
        if (state.SeenDesktopPaths == null || !paths.SetEquals(state.SeenDesktopPaths)) return false;
        return paths.IsSubsetOf(AssignedDesktopPaths());
    }

    private HashSet<string> AssignedDesktopPaths() => state.Cards.SelectMany(card => card.References)
        .Where(DesktopFiles.IsDesktopItem).Select(FileMoveService.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static (string Source, string Destination)[] VerifyDesktopRenames(
        (string Source, string Destination)[] renames, string[] entries, HashSet<string> visible)
    {
        var actual = entries.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exact = entries.ToHashSet(StringComparer.Ordinal);
        var reachable = new HashSet<string>(visible, StringComparer.OrdinalIgnoreCase);
        var verified = new List<(string Source, string Destination)>();
        // Walk backwards so a sequence of renames can end at the one path still on disk.
        foreach (var (source, destination) in renames.Reverse())
        {
            if (!reachable.Contains(destination)) continue;
            if (FileMoveService.Same(source, destination) ? !exact.Contains(destination) : actual.Contains(source)) continue;
            verified.Add((source, destination));
            reachable.Add(source);
        }
        verified.Reverse();
        return verified.ToArray();
    }
    private void ReportDesktopEnumeration()
    {
        if (desktopEnumerated) return;
        desktopEnumerated = true;
        StartupDiagnostics.Mark("desktop-first-enumeration-ready");
    }
}
