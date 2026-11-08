using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeskIsle.Services;
namespace DeskIsle;

public partial class MainWindow
{
    private const string ReorderFormat = "DeskIsle.ReferenceOrder.v1";
    private sealed record ReorderPayload(Guid CardId, string Path);
    private Point tilePressPoint;
    private Button? pressedTile;
    private FileTile? reorderSource;
    private int insertionIndex;
    private DateTime suppressOpenUntil;
    private ReorderAdorner? reorderAdorner;
    private AdornerLayer? reorderLayer;
    private ScrollViewer? reorderScroll;
    private Point reorderPointer;
    private readonly DispatcherTimer reorderTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };

    private void TilePress(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        pressedTile = sender as Button;
        tilePressPoint = e.GetPosition(Files);
    }
    private void TileRelease(object sender, MouseButtonEventArgs e) => pressedTile = null;
    private void TileMove(object sender, MouseEventArgs e)
    {
        if (pressedTile is not { DataContext: FileTile tile } button || e.LeftButton != MouseButtonState.Pressed || reorderSource != null) return;
        var current = e.GetPosition(Files);
        if (Math.Abs(current.X - tilePressPoint.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(current.Y - tilePressPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        e.Handled = true;
        pressedTile = null;
        reorderSource = tile;
        button.ReleaseMouseCapture();
        button.Opacity = .35;
        try
        {
            var data = new DataObject();
            data.SetData(ReorderFormat, new ReorderPayload(state.Id, tile.FullPath));
            DragDrop.DoDragDrop(button, data, DragDropEffects.Move);
        }
        finally
        {
            button.Opacity = 1;
            reorderSource = null;
            suppressOpenUntil = DateTime.UtcNow.AddMilliseconds(250);
            ClearReorderPreview();
        }
    }
    private bool IsReorder(DragEventArgs e) => e.Data.GetDataPresent(ReorderFormat);
    private bool PointerInFiles(DragEventArgs e)
    {
        var point = e.GetPosition(Files);
        return point.X >= 0 && point.Y >= 0 && point.X <= Files.ActualWidth && point.Y <= Files.ActualHeight;
    }
    private bool AcceptReorder(DragEventArgs e) => PointerInFiles(e) && e.Data.GetData(ReorderFormat) is ReorderPayload payload && payload.CardId == state.Id && tiles.Any(x => x.FullPath.Equals(payload.Path, StringComparison.OrdinalIgnoreCase));
    private void ReorderOver(DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = AcceptReorder(e) && (e.AllowedEffects & DragDropEffects.Move) != 0 ? DragDropEffects.Move : DragDropEffects.None;
        if (e.Effects == DragDropEffects.None) { ClearReorderPreview(); return; }
        reorderPointer = e.GetPosition(Files);
        UpdateReorderPreview();
        if (!reorderTimer.IsEnabled)
        {
            reorderScroll = FindScroll(Files);
            reorderTimer.Tick -= ReorderScrollTick;
            reorderTimer.Tick += ReorderScrollTick;
            reorderTimer.Start();
        }
    }
    private void ReorderScrollTick(object? sender, EventArgs e)
    {
        if (reorderScroll == null) return;
        if (reorderPointer.Y < 28) reorderScroll.ScrollToVerticalOffset(reorderScroll.VerticalOffset - 18);
        else if (reorderPointer.Y > Files.ActualHeight - 28) reorderScroll.ScrollToVerticalOffset(reorderScroll.VerticalOffset + 18);
        else return;
        UpdateReorderPreview();
    }
    private void UpdateReorderPreview()
    {
        var point = reorderPointer;
        var targetButton = FindTile(Files.InputHitTest(point) as DependencyObject);
        // Gaps and unused row space choose the closest realized tile, not an arbitrary last item.
        if (targetButton == null)
        {
            var bestDistance = double.MaxValue;
            foreach (var candidate in VisibleTiles(Files))
            {
                var rect = new Rect(candidate.TranslatePoint(new Point(), Files), candidate.RenderSize);
                if (rect.Bottom < 0 || rect.Top > Files.ActualHeight) continue;
                var dx = Math.Max(Math.Max(rect.Left - point.X, 0), point.X - rect.Right);
                var dy = Math.Max(Math.Max(rect.Top - point.Y, 0), point.Y - rect.Bottom);
                var distance = dx * dx + dy * dy;
                if (distance < bestDistance) { bestDistance = distance; targetButton = candidate; }
            }
        }
        var bounds = Rect.Empty;
        var after = true;
        insertionIndex = tiles.Count;
        if (targetButton?.DataContext is FileTile target)
        {
            bounds = new Rect(targetButton.TranslatePoint(new Point(), Files), targetButton.RenderSize);
            after = point.Y >= bounds.Bottom || point.Y >= bounds.Top && point.X >= bounds.Left + bounds.Width / 2;
            insertionIndex = tiles.ToList().IndexOf(target) + (after ? 1 : 0);
        }
        if (reorderAdorner == null)
        {
            reorderLayer = AdornerLayer.GetAdornerLayer(Files);
            reorderAdorner = new ReorderAdorner(Files) { IsHitTestVisible = false };
            reorderLayer?.Add(reorderAdorner);
        }
        reorderAdorner.Target = bounds;
        reorderAdorner.After = after;
        reorderAdorner.Pointer = point;
        reorderAdorner.Tile = reorderSource;
        reorderAdorner.InvalidateVisual();
    }
    private void ReorderDrop(DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (!AcceptReorder(e) || e.Data.GetData(ReorderFormat) is not ReorderPayload payload) { ClearReorderPreview(); return; }
        reorderPointer = e.GetPosition(Files);
        UpdateReorderPreview();
        var items = tiles.ToList();
        var index = items.FindIndex(x => x.FullPath.Equals(payload.Path, StringComparison.OrdinalIgnoreCase));
        var target = insertionIndex;
        var moved = items[index];
        items.RemoveAt(index);
        if (index < target) target--;
        items.Insert(Math.Clamp(target, 0, items.Count), moved);
        ClearReorderPreview();
        if (target != index)
        {
            ++viewVersion; // Any earlier asynchronous refresh must not overwrite this order.
            tiles = items;
            if (directory == null) state.References = items.Select(x => x.FullPath).ToList();
            else state.FolderOrders[directory] = items.Select(x => x.FullPath).ToList();
            var offset = FindScroll(Files)?.VerticalOffset ?? 0;
            RebuildRows();
            Files.UpdateLayout();
            FindScroll(Files)?.ScrollToVerticalOffset(offset);
            SaveCard();
        }
        e.Effects = DragDropEffects.Move;
    }
    private void ClearReorderPreview()
    {
        reorderTimer.Stop();
        if (reorderAdorner != null) reorderLayer?.Remove(reorderAdorner);
        reorderAdorner = null;
        reorderLayer = null;
        reorderScroll = null;
    }
    private Button? FindTile(DependencyObject? node)
    {
        while (node != null && node != Files)
        {
            if (node is Button { DataContext: FileTile } button) return button;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : (node as FrameworkContentElement)?.Parent;
        }
        return null;
    }
    private static IEnumerable<Button> VisibleTiles(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button { DataContext: FileTile } button) yield return button;
            else foreach (var nested in VisibleTiles(child)) yield return nested;
        }
    }
    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        if (parent is ScrollViewer viewer) return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindScroll(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }
}
