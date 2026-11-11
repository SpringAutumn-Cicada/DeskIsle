using System.IO;
using System.Collections.Specialized;
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
    public static readonly DependencyProperty IsReorderingProperty = DependencyProperty.Register(
        nameof(IsReordering), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    public bool IsReordering
    {
        get => (bool)GetValue(IsReorderingProperty);
        private set => SetValue(IsReorderingProperty, value);
    }
    private const string ReorderFormat = "DeskIsle.ReferenceOrder.v1";
    private sealed record ReorderPayload(Guid CardId, string Path);
    private Point tilePressPoint;
    private Button? pressedTile;
    private FileTile? reorderSource;
    private int insertionIndex;
    private FileTile? folderDropTarget;
    private DateTime suppressOpenUntil;
    private ReorderAdorner? reorderAdorner;
    private AdornerLayer? reorderLayer;
    private ScrollViewer? reorderScroll;
    private Point reorderPointer;
    private readonly DispatcherTimer reorderTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };

    private void TilePress(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || HasPendingCreation) return;
        pressedTile = sender as Button;
        tilePressPoint = e.GetPosition(Files);
    }
    private void TileRelease(object sender, MouseButtonEventArgs e) => pressedTile = null;
    private void TileMove(object sender, MouseEventArgs e)
    {
        if (HasPendingCreation) { pressedTile = null; return; }
        if (pressedTile is not { DataContext: FileTile tile } button || e.LeftButton != MouseButtonState.Pressed || reorderSource != null) return;
        var current = e.GetPosition(Files);
        if (Math.Abs(current.X - tilePressPoint.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(current.Y - tilePressPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        e.Handled = true;
        if (!DesktopFiles.IsCardPath(tile.FullPath) || !DesktopFiles.Displayable(tile.FullPath) || !File.Exists(tile.FullPath) && !Directory.Exists(tile.FullPath))
        {
            pressedTile = null;
            Status.Text = "路径不可用，无法拖出文件。";
            return;
        }
        pressedTile = null;
        reorderSource = tile;
        IsReordering = true;
        button.ReleaseMouseCapture();
        button.Opacity = .35;
        try
        {
            var data = new DataObject();
            data.SetData(ReorderFormat, new ReorderPayload(state.Id, tile.FullPath));
            // Standard CF_HDROP lets browsers and native upload controls read the
            // original file. Copy-only prevents Explorer targets from moving it.
            data.SetFileDropList(new StringCollection { tile.FullPath });
            DragDrop.DoDragDrop(button, data, DragDropEffects.Copy);
        }
        finally
        {
            button.Opacity = 1;
            reorderSource = null;
            IsReordering = false;
            suppressOpenUntil = DateTime.UtcNow.AddMilliseconds(250);
            ClearReorderPreview();
        }
    }
    private bool IsReorder(DragEventArgs e) => e.Data.GetDataPresent(ReorderFormat) && e.Data.GetData(ReorderFormat) is ReorderPayload payload && payload.CardId == state.Id;
    private bool PointerInFiles(DragEventArgs e)
    {
        var point = e.GetPosition(Files);
        return point.X >= 0 && point.Y >= 0 && point.X <= Files.ActualWidth && point.Y <= Files.ActualHeight;
    }
    private bool AcceptReorder(DragEventArgs e) => !HasPendingCreation && PointerInFiles(e) && e.Data.GetData(ReorderFormat) is ReorderPayload payload && payload.CardId == state.Id && tiles.Any(x => x.FullPath.Equals(payload.Path, StringComparison.OrdinalIgnoreCase));
    private void ReorderOver(DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = AcceptReorder(e) && (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
        if (e.Effects == DragDropEffects.None) { HideReorderPreview(); return; }
        reorderPointer = e.GetPosition(Files);
        UpdateReorderPreview();
        if (folderDropTarget != null && reorderSource != null && FileMoveService.QuickError(reorderSource.FullPath, folderDropTarget.FullPath) != null) e.Effects = DragDropEffects.None;
        if (!reorderTimer.IsEnabled)
        {
            reorderScroll = FindScroll(Files);
            if (reorderScroll != null)
            {
                reorderScroll.ScrollChanged -= ReorderScrollChanged;
                reorderScroll.ScrollChanged += ReorderScrollChanged;
            }
            reorderTimer.Tick -= ReorderScrollTick;
            reorderTimer.Tick += ReorderScrollTick;
            reorderTimer.Start();
        }
    }
    private void ReorderLeave(DragEventArgs e)
    {
        e.Handled = true;
        // WPF raises DragLeave when crossing child visuals too. Only hide when
        // the pointer actually leaves the file viewport, retaining one adorner per drag.
        if (!PointerInFiles(e)) HideReorderPreview();
    }
    private void HideReorderPreview()
    {
        reorderTimer.Stop();
        if (reorderAdorner != null) reorderAdorner.Visibility = Visibility.Hidden;
    }
    private void ReorderScrollTick(object? sender, EventArgs e)
    {
        if (reorderScroll == null) return;
        if (reorderPointer.Y < 28) reorderScroll.ScrollToVerticalOffset(reorderScroll.VerticalOffset - 18);
        else if (reorderPointer.Y > Files.ActualHeight - 28) reorderScroll.ScrollToVerticalOffset(reorderScroll.VerticalOffset + 18);
        else return;
    }
    private void ReorderScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // ScrollToVerticalOffset is deferred by WPF; update against the actual layout,
        // not the requested offset, and ignore events from unchanged/hidden previews.
        if (e.VerticalChange != 0 && reorderAdorner?.Visibility == Visibility.Visible) UpdateReorderPreview();
    }
    private void UpdateReorderPreview()
    {
        var point = reorderPointer;
        var targetButton = FindTile(Files.InputHitTest(point) as DependencyObject);
        folderDropTarget = null;
        if (targetButton?.DataContext is FileTile { Entry.IsDirectory: true } folder)
        {
            var local = Files.TranslatePoint(point, targetButton);
            if (local.X >= targetButton.ActualWidth * .2 && local.X <= targetButton.ActualWidth * .8 && local.Y >= 0 && local.Y <= targetButton.ActualHeight)
                folderDropTarget = folder;
        }
        // Hover feedback follows the actual pointer hit, independent of the insertion slot.
        var hoverBounds = targetButton == null ? Rect.Empty : new Rect(targetButton.TranslatePoint(new Point(), Files), targetButton.RenderSize);
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
        // A slot has one canonical geometry, regardless of which adjacent tile was hit.
        var line = Rect.Empty;
        if (!bounds.IsEmpty)
        {
            var realized = VisibleTiles(Files).Where(b => b.DataContext is FileTile)
                .ToDictionary(b => (FileTile)b.DataContext, b => new Rect(b.TranslatePoint(new Point(), Files), b.RenderSize));
            var leftIndex = insertionIndex - 1;
            var rightIndex = insertionIndex;
            if (leftIndex >= 0 && rightIndex < tiles.Count && rightIndex % columns != 0 &&
                realized.TryGetValue(tiles[leftIndex], out var left) && realized.TryGetValue(tiles[rightIndex], out var right))
            {
                var x = (left.Right + right.Left) / 2;
                line = new Rect(x, right.Top + 5, 0, Math.Max(0, right.Height - 10));
            }
            else if (rightIndex < tiles.Count && realized.TryGetValue(tiles[rightIndex], out var next))
            {
                line = new Rect(next.Left - 3, next.Top + 5, 0, Math.Max(0, next.Height - 10));
            }
            else if (leftIndex >= 0 && realized.TryGetValue(tiles[leftIndex], out var previous))
            {
                line = new Rect(previous.Right + 3, previous.Top + 5, 0, Math.Max(0, previous.Height - 10));
            }
        }
        if (folderDropTarget != null) line = Rect.Empty;
        if (reorderAdorner == null)
        {
            reorderLayer = AdornerLayer.GetAdornerLayer(Files);
            reorderAdorner = new ReorderAdorner(Files) { IsHitTestVisible = false };
            reorderLayer?.Add(reorderAdorner);
        }
        var moveError = folderDropTarget != null && reorderSource != null ? FileMoveService.QuickError(reorderSource.FullPath, folderDropTarget.FullPath) : null;
        var caption = folderDropTarget == null ? null : moveError ?? $"移入「{folderDropTarget.Name}」";
        var changed = reorderAdorner.Caption != caption || reorderAdorner.Visibility != Visibility.Visible || reorderAdorner.Target != hoverBounds ||
            reorderAdorner.InsertionLine != line || reorderAdorner.Pointer != point || reorderAdorner.Tile != reorderSource;
        reorderAdorner.Caption = caption;
        reorderAdorner.IsFolderDrop = folderDropTarget != null;
        reorderAdorner.IsInvalid = moveError != null;
        reorderAdorner.Visibility = Visibility.Visible;
        reorderAdorner.Target = hoverBounds;
        reorderAdorner.InsertionLine = line;
        reorderAdorner.Pointer = point;
        reorderAdorner.Tile = reorderSource;
        if (changed) reorderAdorner.InvalidateVisual();
    }
    private void ReorderDrop(DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (!AcceptReorder(e) || e.Data.GetData(ReorderFormat) is not ReorderPayload payload) { ClearReorderPreview(); return; }
        reorderPointer = e.GetPosition(Files);
        UpdateReorderPreview();
        if (folderDropTarget is { } folder)
        {
            var sourcePath = payload.Path;
            var targetPath = folder.FullPath;
            ClearReorderPreview();
            if (FileMoveService.QuickError(sourcePath, targetPath) == null)
            {
                e.Effects = DragDropEffects.Copy; // Transport effect only; explicit internal operation follows confirmation.
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => MoveIntoFolder(sourcePath, targetPath)));
            }
            return;
        }
        var items = tiles.ToList();
        var index = items.FindIndex(x => x.FullPath.Equals(payload.Path, StringComparison.OrdinalIgnoreCase));
        var target = insertionIndex;
        items = DeskIsle.Core.ReferenceOrder.Move(items, index, target);
        if (index < target) target--;
        ClearReorderPreview();
        if (target != index)
        {
            ++viewVersion; // Any earlier asynchronous refresh must not overwrite this order.
            tiles = items;
            if (directory == null)
            {
                // Hidden/deleted desktop and pending external records are not discarded by sorting visible tiles.
                var visible = items.Select(x => x.FullPath).ToList();
                state.References = visible.Concat(state.References.Where(path => !visible.Contains(path, StringComparer.OrdinalIgnoreCase))).ToList();
            }
            else state.FolderOrders[directory] = items.Select(x => x.FullPath).ToList();
            var offset = FindScroll(Files)?.VerticalOffset ?? 0;
            RebuildRows();
            Files.UpdateLayout();
            FindScroll(Files)?.ScrollToVerticalOffset(offset);
            SaveCard();
        }
        e.Effects = DragDropEffects.Copy;
    }
    private void ClearReorderPreview()
    {
        reorderTimer.Stop();
        if (reorderAdorner != null) reorderLayer?.Remove(reorderAdorner);
        folderDropTarget = null;
        reorderAdorner = null;
        reorderLayer = null;
        if (reorderScroll != null) reorderScroll.ScrollChanged -= ReorderScrollChanged;
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
