using System.IO;
namespace DeskIsle.Services;

public sealed partial class WorkspaceManager
{
    private bool scanningDesktop;
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
    public async Task SyncDefaultDesktop()
    {
        if (scanningDesktop || Exiting || store.ReadOnly || FileMoveInProgress || windows.Values.Any(w => w.HasPendingCreation || w.IsReordering)) return;
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
            if (added.Length > 0) await RefreshAfterFileMove(DesktopFiles.Root, DesktopFiles.Root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { StartupDiagnostics.Mark("default-desktop-sync-error=" + ex.GetType().Name); }
        finally { scanningDesktop = false; }
    }
}
