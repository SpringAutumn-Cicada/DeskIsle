using System.IO;

namespace DeskIsle.Services;

public sealed partial class WorkspaceManager
{
    private CardState DefaultCard => state.Cards.First(card => card.Id == state.DefaultCardId);

    private void ChangeWorkspace(Action change)
    {
        if (store.ReadOnly) throw new IOException("配置处于保护模式，无法修改文件分组。");
        var previousCards = state.Cards.ToArray();
        var previousDefault = state.DefaultCardId;
        var previousImports = new Dictionary<string, string>(state.ImportedPaths, StringComparer.OrdinalIgnoreCase);
        var previous = previousCards.ToDictionary(card => card,
            card => (References: card.References.ToList(), card.FolderOrders, card.CreationDirectory));
        try
        {
            change();
            Save();
        }
        catch
        {
            state.Cards = previousCards.ToList();
            state.DefaultCardId = previousDefault;
            state.ImportedPaths = previousImports;
            foreach (var (card, snapshot) in previous)
                (card.References, card.FolderOrders, card.CreationDirectory) = snapshot;
            throw;
        }

        var affected = state.Cards.Where(card =>
            !previous[card].References.SequenceEqual(card.References, StringComparer.Ordinal)).ToArray();
        // Retire the old rows before any async refresh can display them in the destination.
        foreach (var card in affected)
        {
            if (startupHidePending) startupCards.Add(card.Id);
            if (windows.TryGetValue(card.Id, out var window)) window.RefreshCardOwnership();
        }
    }

    private void AssignDesktopItems(CardState target, IEnumerable<string> paths)
    {
        var items = paths.Select(FileMoveService.Normalize).Where(DesktopFiles.IsDesktopItem)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (items.Count == 0) return;
        foreach (var card in state.Cards)
        {
            var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            card.References.RemoveAll(path =>
            {
                var normalized = FileMoveService.Normalize(path);
                return items.Contains(normalized) && (card != target || !assigned.Add(normalized));
            });
            if (card == target) card.References.AddRange(items.Where(path => !assigned.Contains(path)));
        }
    }

    internal void AssignPublishedItems(Guid targetId, IEnumerable<string> paths)
    {
        var target = state.Cards.First(card => card.Id == targetId);
        var items = paths.Where(DesktopFiles.IsDesktopItem).ToArray();
        if (items.Length > 0) ChangeWorkspace(() => AssignDesktopItems(target, items));
    }
}
