using System.IO;
namespace DeskIsle.Services;

public sealed partial class WorkspaceManager
{
    private bool scanningDesktop;
    private long desktopChangedAt;
    private readonly System.Windows.Threading.DispatcherTimer defaultSettle = new(System.Windows.Threading.DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(750) };
    internal void NotifyDesktopMutation()
    {
        if (Exiting) return;
        desktopChangedAt = Environment.TickCount64;
        defaultSettle.Stop(); defaultSettle.Start();
    }
    public bool IsDefaultCard(Guid id) => state.DefaultCardId == id;
    public void SetDefaultCard(Guid id)
    {
        if (store.ReadOnly) throw new IOException("配置处于保护模式，无法修改默认卡片。");
        if (!state.Cards.Any(c => c.Id == id)) return;
        var previous = state.DefaultCardId;
        state.DefaultCardId = id;
        try { Save(); }
        catch { state.DefaultCardId = previous; throw; }
    }
    public async Task TransferCardReference(Guid sourceId, Guid targetId, string path)
    {
        if (Exiting || FileMoveInProgress || windows.Values.Any(w => w.HasPendingCreation)) throw new IOException("请先完成当前文件操作。");
        if (store.ReadOnly) throw new IOException("配置处于保护模式，无法转移文件分组。");
        var source = state.Cards.FirstOrDefault(c => c.Id == sourceId);
        var target = state.Cards.FirstOrDefault(c => c.Id == targetId);
        if (source == null || target == null || source == target || !source.References.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        DesktopFiles.Require(path);
        if (!DesktopFiles.IsDesktopItem(path) || !File.Exists(path) && !Directory.Exists(path)) throw new IOException("文件不可用，未改变分组。");
        var previousSource = source.References.ToList();
        var previousTarget = target.References.ToList();
        source.References.RemoveAll(p => FileMoveService.Same(p, path));
        if (!target.References.Contains(path, StringComparer.OrdinalIgnoreCase)) target.References.Add(path);
        try { Save(); }
        catch { source.References = previousSource; target.References = previousTarget; throw; }
        await RefreshAfterFileMove(DesktopFiles.Root, DesktopFiles.Root);
    }
    public async Task SyncDefaultDesktop()
    {
        if (Environment.TickCount64 - desktopChangedAt < 700 || scanningDesktop || Exiting || store.ReadOnly || FileMoveInProgress || windows.Values.Any(w => w.HasPendingCreation || w.IsReordering)) return;
        scanningDesktop = true;
        try
        {
            var paths = await Task.Run(() => Directory.EnumerateFileSystemEntries(DesktopFiles.Root)
                .Where(DesktopFiles.VisibleDesktopItem).ToHashSet(StringComparer.OrdinalIgnoreCase));
            if (Exiting || FileMoveInProgress || windows.Values.Any(w => w.HasPendingCreation || w.IsReordering)) return;
            var target = state.Cards.FirstOrDefault(c => c.Id == state.DefaultCardId);
            if (target == null) return;
            var previousSeen = state.SeenDesktopPaths;
            var seen = previousSeen?.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (seen != null && seen.SetEquals(paths)) return;
            var assigned = state.Cards.SelectMany(c => c.References).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // First upgrade establishes a baseline, preserving existing desktop organization.
            var added = seen == null ? [] : paths.Where(p => !seen.Contains(p) && !assigned.Contains(p)).ToArray();
            var previousReferences = target.References.ToList();
            target.References.AddRange(added);
            state.SeenDesktopPaths = paths.ToList();
            try { Save(); }
            catch { target.References = previousReferences; state.SeenDesktopPaths = previousSeen; throw; }
            if (added.Length > 0 && windows.TryGetValue(target.Id, out var window))
            {
                StartupDiagnostics.Mark($"default-desktop-added count={added.Length}");
                window.RequestFileSync();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { StartupDiagnostics.Mark("default-desktop-sync-error=" + ex.GetType().Name); }
        finally { scanningDesktop = false; }
    }
}
