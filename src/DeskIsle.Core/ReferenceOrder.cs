namespace DeskIsle.Core;

public static class ReferenceOrder
{
    /// <summary>Insert an existing item into a pre-removal gap; never mutates the input.</summary>
    public static List<T> Move<T>(IReadOnlyList<T> items, int fromIndex, int slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(fromIndex, items.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slot, items.Count);
        var result = items.ToList();
        var moved = result[fromIndex];
        result.RemoveAt(fromIndex);
        result.Insert(slot > fromIndex ? slot - 1 : slot, moved);
        return result;
    }
}
