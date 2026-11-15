using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeskIsle.Core;
using DeskIsle.Services;

namespace DeskIsle;

public partial class MainWindow : Window
{
    private readonly DesktopHost host = new();
    private readonly FileIconService icons = new();
    private readonly WorkspaceManager workspace;
    private readonly DesktopIcons desktopIcons;
    private readonly CardState state;
    private bool mouseSelecting;
    private int viewVersion;
    private bool closing;
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private IReadOnlyList<FileTile> tiles = [];
    private FileTile? selectedTile;
    private int columns;
    private readonly DispatcherTimer monitor = new() { Interval = TimeSpan.FromSeconds(3) };
    private CancellationTokenSource? loading;
    private string? directory;
    private bool dragging;
    private DesktopHost.POINT dragPoint;

    public MainWindow(WorkspaceManager workspace, CardState card)
    {
        this.workspace = workspace;
        state = card;
        desktopIcons = workspace.DesktopIcons;
        var backgroundOpacity = state.BackgroundOpacity;
        InitializeComponent();
        if (!workspace.DesktopIntegration) ShowActivated = false;
        OpacitySlider.Value = Math.Clamp(backgroundOpacity, .3, 1);
        Width = double.IsFinite(state.Width) ? Math.Clamp(state.Width, 260, Math.Max(260, SystemParameters.WorkArea.Width)) : 360;
        Height = double.IsFinite(state.Height) ? Math.Clamp(state.Height, 180, Math.Max(180, SystemParameters.WorkArea.Height)) : 430;
        Left = double.IsFinite(state.Left) ? Math.Clamp(state.Left, SystemParameters.WorkArea.Left, Math.Max(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Right - Width)) : SystemParameters.WorkArea.Left;
        Top = double.IsFinite(state.Top) ? Math.Clamp(state.Top, SystemParameters.WorkArea.Top, Math.Max(SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Bottom - Height)) : SystemParameters.WorkArea.Top;
        PreviewMouseDown += (_, _) =>
        {
            mouseSelecting = true;
            // Blank card regions do not take keyboard focus. Commit explicitly
            // before dispatching the click, without consuming its original action.
            if (TitleEditor.Visibility == Visibility.Visible && !TitleEditor.IsMouseOver) FinishRename(true);
        };
        Deactivated += (_, _) => FinishRename(true);
        PreviewKeyDown += (_, _) => mouseSelecting = false;
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); if (!closing) SaveCard(); };
        LocationChanged += (_, _) => { if (!closing) { saveTimer.Stop(); saveTimer.Start(); } };
        SizeChanged += (_, _) => { if (!closing) { saveTimer.Stop(); saveTimer.Start(); } };
        SourceInitialized += (_, _) => host.SetHandle(this);
        fileSync.Tick += (_, _) => { RebuildFileWatchers(); SyncFiles(null, EventArgs.Empty); };
        eventSync.Tick += (_, _) => { eventSync.Stop(); eventSync.Interval = TimeSpan.FromMilliseconds(40); SyncFiles(null, EventArgs.Empty); };
        ContentRendered += (_, _) => StartupDiagnostics.Mark("card-first-content-rendered");
        Loaded += async (_, _) => { await LoadReferences(); if (closing) return; fileSync.Start(); UpdateDesktopButton(); if (workspace.Warning != null) Status.Text = workspace.Warning; if (workspace.DesktopIntegration) { monitor.Start(); } };
        Closing += (_, e) => { if (HasPendingCreation) { e.Cancel = true; return; } closing = true; saveTimer.Stop(); ++viewVersion; FinishRename(true); SaveCard(); };
        monitor.Tick += (_, _) =>
        {
            UpdateDesktopButton();
            var desktop = DesktopReturnMonitor.ForegroundDesktop();
            if (desktop != 0) RecoverOnDesktop(desktop, false);

        };
        Closed += (_, _) => { StopFileSync(); monitor.Stop(); ClearReorderPreview(); loading?.Cancel(); icons.Dispose(); };
    }

    private void RenameClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        TitleText.Visibility = Visibility.Collapsed;
        TitleEditor.Text = state.Title;
        TitleEditor.Visibility = Visibility.Visible;
        TitleEditor.Focus();
        TitleEditor.SelectAll();
    }
    private void FinishRename(bool commit)
    {
        if (TitleEditor.Visibility != Visibility.Visible) return;
        var title = TitleEditor.Text.Trim();
        TitleEditor.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        if (commit && title.Length > 0)
        {
            state.Title = title;
            TitleText.Text = title;
            SaveCard();
        }
    }
    private void RenameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape) { FinishRename(e.Key == Key.Enter); e.Handled = true; }
    }
    private void RenameLostFocus(object sender, KeyboardFocusChangedEventArgs e) => FinishRename(true);
    private void UpdateDesktopButton()
    {
        DefaultCardToggle.IsChecked = workspace.IsDefaultCard(state.Id);
        DefaultCardToggle.Header = workspace.IsDefaultCard(state.Id) ? "default 卡片" : "设为 default 卡片";
        DefaultCardToggle.ToolTip = "桌面新增文件自动显示到唯一的 default 卡片";
        DefaultCardToggle.Icon = StateMenuIcon("M8,1 L10.1,5.4 L15,6.1 L11.5,9.5 L12.3,14.5 L8,12.2 L3.7,14.5 L4.5,9.5 L1,6.1 L5.9,5.4 Z", DefaultCardToggle.IsChecked);
        PositionLockToggle.IsChecked = state.PositionLocked;
        PositionLockToggle.Header = state.PositionLocked ? "取消固定卡片位置" : "固定卡片位置";
        PositionLockToggle.Icon = StateMenuIcon("M5,1 L11,1 L10,6 L13,9 L13,10 L9,10 L8,15 L7,10 L3,10 L3,9 L6,6 Z", state.PositionLocked);
        var visible = desktopIcons.Visible;
        DesktopToggle.Header = visible == false ? "显示桌面图标" : "隐藏桌面图标";
        DesktopToggle.IsEnabled = visible.HasValue;
        DesktopToggle.Icon = StateMenuIcon("M1,2 L15,2 L15,11 L1,11 Z M8,11 L8,15 M4,15 L12,15", visible == true);
        AddCardMenu.Icon = StateMenuIcon("M2,2 L14,2 L14,14 L2,14 Z M8,5 L8,11 M5,8 L11,8", false);
        RenameCardMenu.Icon = StateMenuIcon("M3,10 L11,2 L14,5 L6,13 L2,14 Z M9,4 L12,7", false);
        OpenDesktopMenu.Icon = StateMenuIcon("M1,4 L6,4 L8,6 L15,6 L14,14 L2,14 Z M1,4 L1,2 L6,2 L8,4 L14,4 L14,6", false);
        RefreshCardMenu.Icon = StateMenuIcon("M14,6 A6,6 0 1 0 14,10 M14,2 L14,6 L10,6", false);
        CurrentTransferMenu.Icon = StateMenuIcon("M2,5 L14,5 M11,2 L14,5 L11,8 M14,11 L2,11 M5,8 L2,11 L5,14", false);
        CardAppearanceMenu.Icon = StateMenuIcon("M8,1 A7,7 0 1 0 15,8 A7,7 0 0 0 8,1 Z M8,1 L8,15 M8,5 L13,5 M8,8 L15,8 M8,11 L13,11", false);
        ResetOpacityMenu.Icon = StateMenuIcon("M3,5 A6,6 0 1 1 2,10 M3,1 L3,5 L7,5", false);
        RemoveCardMenu.Icon = StateMenuIcon("M3,5 L13,5 L12,15 L4,15 Z M1,3 L15,3 M6,3 L6,1 L10,1 L10,3 M6,7 L6,12 M10,7 L10,12", false);
        DockSettingsMenu.Icon = StateMenuIcon("M3,1 L3,15 M8,1 L8,15 M13,1 L13,15 M1,4 L5,4 L5,7 L1,7 Z M6,9 L10,9 L10,12 L6,12 Z M11,3 L15,3 L15,6 L11,6 Z", false);
        ExitMenu.Icon = StateMenuIcon("M7,2 L2,2 L2,14 L7,14 M6,8 L15,8 M11,4 L15,8 L11,12", false);
    }
    private static System.Windows.Shapes.Path StateMenuIcon(string geometry, bool active)
    {
        var stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(active ? "#72D6C5" : "#9BAFC8"));
        var style = new Style(typeof(System.Windows.Shapes.Path));
        style.Setters.Add(new Setter(System.Windows.Shapes.Shape.StrokeProperty, stroke));
        style.Setters.Add(new Setter(System.Windows.Shapes.Shape.FillProperty, active ? stroke : Brushes.Transparent));
        var hover = new System.Windows.Data.Binding("IsHighlighted")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(MenuItem), 1)
        };
        var trigger = new DataTrigger { Binding = hover, Value = true };
        trigger.Setters.Add(new Setter(System.Windows.Shapes.Shape.StrokeProperty, Brushes.White));
        style.Triggers.Add(trigger);
        return new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), Width = 15, Height = 15,
            Stretch = Stretch.Uniform, StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Style = style };
    }
    internal void ShowStartupWarning(string message) => Status.Text = message;
    private void ToggleDesktopIcons(object sender, RoutedEventArgs e)
    {
        workspace.CancelStartupHide();
        try
        {
            var success = desktopIcons.Visible == false ? desktopIcons.Restore() : desktopIcons.Hide();
            if (!success) MessageBox.Show(this, "未能更改桌面图标显示状态，可在桌面右键的“查看”菜单中恢复。", "DeskIsle");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, "无法保存恢复标记，未继续隐藏桌面图标。", "DeskIsle"); }
        UpdateDesktopButton();
    }
    private async Task LoadReferences()
    {
        loading?.Cancel();
        var version = ++viewVersion;
        directory = null;
        RebuildFileWatchers();
        TitleText.ToolTip = null;
        TitleText.Text = state.Title;
        var paths = state.References.Where(DesktopFiles.IsDesktopItem).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var entries = await Task.Run(() => ReadReferences(paths));
        if (directory != null || version != viewVersion || closing) return;
        if (closing) return;
        tiles = entries;
        RebuildRows();
        Status.Text = "";
    }
    private void SaveCard()
    {
        state.Left = Left; state.Top = Top; state.Width = ActualWidth; state.Height = ActualHeight;
        try { workspace.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = "保存失败，请检查配置目录权限。"; }
    }
    private void FileDragOver(object sender, DragEventArgs e)
    {
        if (HasPendingCreation || NewItemPanel.Visibility == Visibility.Visible) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        if (IsReorder(e)) { ReorderOver(e); return; }
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        Surface.BorderBrush = e.Effects == DragDropEffects.None ? new SolidColorBrush(Color.FromRgb(94, 106, 128)) : new SolidColorBrush(Color.FromRgb(114, 201, 193));
        Status.Text = "";
    }
    private void FileDragLeave(object sender, DragEventArgs e)
    {
        if (IsReorder(e)) { ReorderLeave(e); return; }
        Surface.BorderBrush = new SolidColorBrush(Color.FromArgb(85, 94, 106, 128));
        Status.Text = "";
    }
    private void FileDrop(object sender, DragEventArgs e)
    {
        if (HasPendingCreation || NewItemPanel.Visibility == Visibility.Visible) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        if (IsReorder(e)) { ReorderDrop(e); return; }
        var folder = ExternalFolderTarget(e)?.FullPath;
        FileDragLeave(sender, e);
        e.Handled = true;
        if ((e.AllowedEffects & DragDropEffects.Copy) == 0 || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Copy;
        if (e.Data.GetData(ReorderFormat) is ReorderPayload payload && payload.CardId != state.Id)
        {
            if (folder != null)
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => MoveIntoFolder(payload.Path, folder)));
            else if (DesktopFiles.IsDesktopItem(payload.Path))
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
                {
                    try { await workspace.TransferCardReference(payload.CardId, state.Id, payload.Path); }
                    catch (Exception ex) when (FileTransfer.Recoverable(ex)) { Status.Text = ex.Message; }
                }));
            else
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => ImportDesktopFiles(paths, folder)));
            return;
        }
        var captured = paths.ToArray();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => ImportDesktopFiles(captured, folder)));
    }
    private async void RemoveReference(object sender, RoutedEventArgs e)
    {
        var tile = (sender as FrameworkElement)?.DataContext as FileTile ?? selectedTile;
        if (tile == null || directory != null) return;
        state.References.RemoveAll(x => string.Equals(x, tile.FullPath, StringComparison.OrdinalIgnoreCase));
        await LoadReferences(); SaveCard();
    }
    private void OpenDesktopClick(object sender, RoutedEventArgs e) => ShellOpen(DesktopFiles.Root);

    private async Task LoadFolder()
    {
        if (directory == null) return;
        RebuildFileWatchers();
        var version = ++viewVersion;
        loading?.Cancel();
        var request = new CancellationTokenSource();
        loading = request;
        tiles = [];
        Files.ItemsSource = null;
        Status.Text = "";
        try
        {
            var path = directory;
            var entries = await Task.Run(() => { DesktopFiles.Require(path); return FolderReader.Read(path, request.Token).Where(item => DesktopFiles.Displayable(item.FullPath)).ToArray(); }, request.Token);
            if (request.IsCancellationRequested || version != viewVersion || closing) return;
            IEnumerable<DeskIsle.Core.FolderEntry> ordered = entries;
            if (state.FolderOrders.TryGetValue(path, out var savedOrder))
            {
                var ranks = savedOrder.Select((value, index) => (value, index)).GroupBy(x => x.value, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First().index, StringComparer.OrdinalIgnoreCase);
                ordered = entries.OrderBy(x => ranks.GetValueOrDefault(x.FullPath, int.MaxValue));
            }
            tiles = ordered.Select(x => new FileTile(x)).ToArray();
            RebuildRows();
            Status.Text = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (!request.IsCancellationRequested) Status.Text = "目录不可用或没有访问权限。请刷新或重新选择目录。";
        }
        finally { if (ReferenceEquals(loading, request)) loading = null; request.Dispose(); }
    }

    private async void RefreshClick(object sender, RoutedEventArgs e) { if (directory == null) await LoadReferences(); else await LoadFolder(); }
    private void OpenClick(object sender, RoutedEventArgs e)
    {
        if (DateTime.UtcNow < suppressOpenUntil) return;
        var tile = (sender as FrameworkElement)?.DataContext as FileTile ?? selectedTile;
        if (tile != null)
        {
            try { DesktopFiles.Require(tile.FullPath); ShellOpen(tile.FullPath); }
            catch (Exception ex) when (FileTransfer.Recoverable(ex)) { Status.Text = ex.Message; }
        }
    }
    private void RevealClick(object sender, RoutedEventArgs e)
    {
        var tile = (sender as FrameworkElement)?.DataContext as FileTile ?? selectedTile;
        if (tile != null)
        {
            try { var parent = Path.GetDirectoryName(tile.FullPath)!; DesktopFiles.Require(parent); ShellOpen(parent); }
            catch (Exception ex) when (FileTransfer.Recoverable(ex)) { Status.Text = ex.Message; }
        }
    }
    private void ShellOpen(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { MessageBox.Show(this, "无法打开此项目，请检查路径和默认程序。", "DeskIsle"); }
    }
    internal void RecoverOnDesktop(nint desktop, bool repaint = true)
    {
        if (closing || !IsLoaded) return;
        host.RecoverOnDesktop(this, desktop, repaint);
        foreach (var pair in dialogHosts.ToArray()) pair.Value.RecoverOnDesktop(pair.Key, desktop, repaint);
    }
    private int GridColumnCount()
    {
        var viewport = FindScroll(Files)?.ViewportWidth ?? 0;
        return Math.Max(1, (int)((viewport > 0 ? viewport : Math.Max(0, Files.ActualWidth - 14)) / 86));
    }
    private void GridViewportChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Files != null && e.ViewportWidthChange != 0 && GridColumnCount() != columns) RebuildRows();
    }
    private void RebuildRows()
    {
        columns = GridColumnCount();
        Files.ItemsSource = tiles.Chunk(columns).Select(x => new TileRow(x, columns * 86)).ToArray();
    }
    private void TileBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        // Stop mouse focus requests before the inner ScrollViewer sees the event.
        if (mouseSelecting) e.Handled = true;
    }
    private void GridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Files != null && GridColumnCount() != columns) RebuildRows();
    }
    private void TileContextChanged(object sender, DependencyPropertyChangedEventArgs e) => TileLoaded(sender, new RoutedEventArgs());
    private async void TileLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FileTile tile } || tile.IconRequested) return;
        tile.IconRequested = true;
        tile.Icon = await icons.GetAsync(tile.Entry.IsDirectory, tile.FullPath);
        if (tile.Icon == null) tile.IconRequested = false;
    }
    private void TileRightClick(object sender, MouseButtonEventArgs e)
    {
        selectedTile = (sender as FrameworkElement)?.DataContext as FileTile;
        (sender as UIElement)?.Focus();
    }
    private void TileKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OpenClick(sender, e); e.Handled = true; }
    }
    private void DockSettingsClick(object sender, RoutedEventArgs e) => workspace.ConfigureDock();
    private void MoreClick(object sender, RoutedEventArgs e)
    {
        UpdateDesktopButton();
        CurrentTransferMenu.Visibility = currentTransfer == null ? Visibility.Collapsed : Visibility.Visible;
        if (sender is Button { ContextMenu: { } menu } button)
        { menu.PlacementTarget = button; menu.IsOpen = true; }
    }
    private void SetDefaultCardClick(object sender, RoutedEventArgs e)
    {
        try { workspace.SetDefaultCard(state.Id); UpdateDesktopButton(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = ex.Message; UpdateDesktopButton(); }
    }
    private void ResetCardOpacity(object sender, RoutedEventArgs e) => OpacitySlider.Value = CardState.DefaultBackgroundOpacity;
    private void OpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Surface != null) Surface.Background = new SolidColorBrush(Color.FromArgb((byte)(255 * e.NewValue), 36, 41, 53));
        if (state != null && saveTimer != null) { state.BackgroundOpacity = e.NewValue; saveTimer.Stop(); saveTimer.Start(); }
    }
    private void TogglePositionLock(object sender, RoutedEventArgs e)
    {
        state.PositionLocked = PositionLockToggle.IsChecked;
        dragging = false;
        if (Header.IsMouseCaptured) Header.ReleaseMouseCapture();
        UpdateDesktopButton();
        SaveCard();
    }
    private void BeginDrag(object sender, MouseButtonEventArgs e)
    {
        if (state.PositionLocked || TitleEditor.Visibility == Visibility.Visible) return;
        if (e.OriginalSource is not TextBlock && !ReferenceEquals(e.OriginalSource, Header)) return;
        DesktopHost.GetCursorPos(out dragPoint);
        dragging = true;
        Header.CaptureMouse();
    }
    private void ContinueDrag(object sender, MouseEventArgs e)
    {
        if (!dragging || state.PositionLocked) return;
        DesktopHost.GetCursorPos(out var next);
        host.MoveBy(next.X - dragPoint.X, next.Y - dragPoint.Y);
        dragPoint = next;
    }
    private void EndDrag(object sender, MouseButtonEventArgs e) { dragging = false; Header.ReleaseMouseCapture(); }
    private void LostDrag(object sender, MouseEventArgs e) => dragging = false;
    private void AddCardClick(object sender, RoutedEventArgs e)
    {
        try { workspace.Add(this); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = "保存新卡片失败，请检查配置目录权限。"; }
    }
    private void RemoveCardClick(object sender, RoutedEventArgs e)
    {
        try { workspace.Remove(this, state); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = "保存失败，未关闭卡片。"; }
    }
    private void ExitClick(object sender, RoutedEventArgs e) => workspace.Exit();
}
