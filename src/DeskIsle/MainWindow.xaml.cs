using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeskIsle.Core;
using DeskIsle.Services;
using Microsoft.Win32;

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
    private string modeDetails = "浮动模式 · Win+D 可能隐藏卡片";
    private readonly DispatcherTimer monitor = new() { Interval = TimeSpan.FromSeconds(3) };
    private CancellationTokenSource? loading;
    private string? directory;
    private bool dragging;
    private DesktopHost.POINT dragPoint;
    private bool desktopRequested;
    private int recoveryAttempts;

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
        Loaded += async (_, _) => { await LoadReferences(); if (closing) return; UpdateDesktopButton(); if (workspace.Warning != null) Status.Text = workspace.Warning; if (workspace.DesktopIntegration) { SetDesktopMode(true); monitor.Start(); } };
        Closing += (_, e) => { if (HasPendingCreation) { e.Cancel = true; return; } closing = true; saveTimer.Stop(); ++viewVersion; FinishRename(true); SaveCard(); };
        monitor.Tick += (_, _) =>
        {
            UpdateDesktopButton();
            var desktop = DesktopReturnMonitor.ForegroundDesktop();
            if (desktop != 0) RecoverOnDesktop(desktop, false);
            if (!desktopRequested || host.IsAlive) return;
            host.Detach();
            if (++recoveryAttempts <= 3 && host.TryAttach(this)) return;
            desktopRequested = false;
            UpdateMode("桌面连接中断，已切换为浮动模式；可手动重试。");
        };
        Closed += (_, _) => { monitor.Stop(); ClearReorderPreview(); loading?.Cancel(); icons.Dispose(); host.Detach(); };
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
        var visible = desktopIcons.Visible;
        DesktopToggle.Header = visible == false ? "显示桌面图标" : "隐藏桌面图标";
        DesktopToggle.IsEnabled = visible.HasValue;
    }
    private void ToggleDesktopIcons(object sender, RoutedEventArgs e)
    {
        try
        {
            var success = desktopIcons.Visible == false ? desktopIcons.Restore() : desktopIcons.Hide();
            if (!success) MessageBox.Show(this, "未能更改桌面图标显示状态，可在桌面右键的“查看”菜单中恢复。", "DeskIsle");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, "无法保存恢复标记，未继续隐藏桌面图标。", "DeskIsle"); }
        UpdateDesktopButton();
    }
    private void RestoreDesktopIcons(object sender, RoutedEventArgs e)
    {
        try { if (!desktopIcons.Restore()) MessageBox.Show(this, "未找到桌面图标视图，请在桌面右键→查看→显示桌面图标。", "DeskIsle"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = "无法清除恢复标记，请检查配置目录权限。"; }
        UpdateDesktopButton();
    }
    private async Task LoadReferences()
    {
        loading?.Cancel();
        var version = ++viewVersion;
        directory = null;
        TitleText.ToolTip = null;
        TitleText.Text = state.Title;
        var paths = state.References.ToArray();
        var entries = await Task.Run(() => paths.Select(path => new FileTile(new FolderEntry(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : path, path, Directory.Exists(path))) { Availability = File.Exists(path) || Directory.Exists(path) ? "" : "路径失效 · 可右键重新定位" }).ToArray());
        if (directory != null || version != viewVersion || closing) return;
        if (closing) return;
        tiles = entries;
        RebuildRows();
        Status.Text = tiles.Count == 0 ? "拖入文件或文件夹" : "";
    }
    private void SaveCard()
    {
        state.Left = Left; state.Top = Top; state.Width = ActualWidth; state.Height = ActualHeight;
        try { workspace.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = "保存失败，请检查配置目录权限。"; }
    }
    private void FileDragOver(object sender, DragEventArgs e)
    {
        if (NewItemPanel.Visibility == Visibility.Visible) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        if (IsReorder(e)) { ReorderOver(e); return; }
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && (e.AllowedEffects & DragDropEffects.Link) != 0 ? DragDropEffects.Link : e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        Surface.BorderBrush = e.Effects == DragDropEffects.None ? new SolidColorBrush(Color.FromRgb(94, 106, 128)) : new SolidColorBrush(Color.FromRgb(114, 201, 193));
        Status.Text = e.Effects == DragDropEffects.None ? "不支持此拖入内容" : "添加到卡片 · 原文件位置不变";
    }
    private void FileDragLeave(object sender, DragEventArgs e)
    {
        if (IsReorder(e)) { ReorderLeave(e); return; }
        Surface.BorderBrush = new SolidColorBrush(Color.FromArgb(85, 94, 106, 128));
        Status.Text = tiles.Count == 0 ? "拖入文件或文件夹" : "";
    }
    private async void FileDrop(object sender, DragEventArgs e)
    {
        if (NewItemPanel.Visibility == Visibility.Visible) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        if (IsReorder(e)) { ReorderDrop(e); return; }
        FileDragLeave(sender, e);
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        e.Effects = (e.AllowedEffects & DragDropEffects.Link) != 0 ? DragDropEffects.Link : DragDropEffects.Copy;
        var skipped = 0;
        var normalized = await Task.Run(() => paths.Select(raw =>
        {
            try
            {
                var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
                return File.Exists(path) || Directory.Exists(path) ? path : null;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { return null; }
        }).ToArray());
        if (closing) return;
        foreach (var raw in normalized)
        {
            try
            {
                if (raw == null) { skipped++; continue; }
                var path = raw;
                if (state.References.Contains(path, StringComparer.OrdinalIgnoreCase)) { skipped++; continue; }
                state.References.Add(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { skipped++; }
        }
        SaveCard();
        await LoadReferences();
        if (skipped > 0) Status.Text += $" · 跳过{skipped}项重复或无效路径";
    }
    private async void CollectionClick(object sender, RoutedEventArgs e)
    {
        if (creatingItem) return;
        CancelNewItem(sender, e);
        await LoadReferences();
    }
    private async void RemoveReference(object sender, RoutedEventArgs e)
    {
        var tile = (sender as FrameworkElement)?.DataContext as FileTile ?? selectedTile;
        if (tile == null || directory != null) return;
        state.References.RemoveAll(x => string.Equals(x, tile.FullPath, StringComparison.OrdinalIgnoreCase));
        await LoadReferences(); SaveCard();
    }
    private async void RelocateReference(object sender, RoutedEventArgs e)
    {
        var tile = (sender as FrameworkElement)?.DataContext as FileTile ?? selectedTile;
        if (tile == null || directory != null) return;
        string? replacement = null;
        if (MessageBox.Show(this, "重新定位文件夹？选择“否”定位文件。", "重新定位", MessageBoxButton.YesNoCancel) is var choice && choice != MessageBoxResult.Cancel)
        {
            if (choice == MessageBoxResult.Yes) { var picker = new OpenFolderDialog(); if (picker.ShowDialog(this) == true) replacement = picker.FolderName; }
            else { var picker = new OpenFileDialog(); if (picker.ShowDialog(this) == true) replacement = picker.FileName; }
        }
        if (replacement == null || closing) return;
        state.References.RemoveAll(x => string.Equals(x, tile.FullPath, StringComparison.OrdinalIgnoreCase));
        if (!state.References.Contains(replacement, StringComparer.OrdinalIgnoreCase)) state.References.Add(replacement);
        await LoadReferences(); SaveCard();
    }
    private async void ChooseClick(object sender, RoutedEventArgs e)
    {
        if (creatingItem) return;
        CancelNewItem(sender, e);
        var picker = new OpenFolderDialog { Title = "选择要展示的文件夹" };
        if (picker.ShowDialog(this) != true) return;
        directory = picker.FolderName;
        TitleText.Text = state.Title;
        TitleText.ToolTip = directory;
        await LoadFolder();
    }

    private async Task LoadFolder()
    {
        if (directory == null) return;
        var version = ++viewVersion;
        loading?.Cancel();
        var request = new CancellationTokenSource();
        loading = request;
        tiles = [];
        Files.ItemsSource = null;
        Status.Text = "正在读取目录…";
        try
        {
            var path = directory;
            var entries = await Task.Run(() => FolderReader.Read(path, request.Token), request.Token);
            if (request.IsCancellationRequested || version != viewVersion || closing) return;
            IEnumerable<DeskIsle.Core.FolderEntry> ordered = entries;
            if (state.FolderOrders.TryGetValue(path, out var savedOrder))
            {
                var ranks = savedOrder.Select((value, index) => (value, index)).GroupBy(x => x.value, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First().index, StringComparer.OrdinalIgnoreCase);
                ordered = entries.OrderBy(x => ranks.GetValueOrDefault(x.FullPath, int.MaxValue));
            }
            tiles = ordered.Select(x => new FileTile(x)).ToArray();
            RebuildRows();
            Status.Text = entries.Count == 0 ? "目录为空。" : "";
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
        if (tile != null) ShellOpen(tile.FullPath);
    }
    private void RevealClick(object sender, RoutedEventArgs e)
    {
        var tile = (sender as FrameworkElement)?.DataContext as FileTile ?? selectedTile;
        if (tile != null) ShellOpen(Path.GetDirectoryName(tile.FullPath)!);
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
        var attached = host.Attached;
        host.RecoverOnDesktop(this, desktop, repaint);
        if (attached && !host.Attached)
        {
            desktopRequested = false;
            UpdateMode("桌面宿主暂不可用，已恢复为浮动模式。");
        }
    }
    private void ModeClick(object sender, RoutedEventArgs e) => SetDesktopMode(!host.Attached);
    private void SetDesktopMode(bool enabled)
    {
        recoveryAttempts = 0;
        desktopRequested = enabled;
        if (!enabled) host.Detach();
        else if (!host.TryAttach(this)) desktopRequested = false;
        UpdateMode(enabled && !host.Attached ? "桌面连接不可用，已使用浮动模式。" : null);
    }
    private void UpdateMode(string? reason)
    {
        ModeButton.Header = host.Attached ? "切换浮动模式" : "尝试桌面模式";
        modeDetails = reason ?? (host.Attached ? "桌面模式 · 兼容性待 Windows 验收" : "浮动模式 · Win+D 可能隐藏卡片");
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
    private void MoreClick(object sender, RoutedEventArgs e)
    {
        UpdateDesktopButton();
        if (sender is Button { ContextMenu: { } menu } button)
        { menu.PlacementTarget = button; menu.IsOpen = true; }
    }
    private void ModeInfoClick(object sender, RoutedEventArgs e) => MessageBox.Show(this, modeDetails, "显示模式");
    private void OpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Surface != null) Surface.Background = new SolidColorBrush(Color.FromArgb((byte)(255 * e.NewValue), 36, 41, 53));
        if (state != null && saveTimer != null) { state.BackgroundOpacity = e.NewValue; saveTimer.Stop(); saveTimer.Start(); }
    }
    private void BeginDrag(object sender, MouseButtonEventArgs e)
    {
        if (TitleEditor.Visibility == Visibility.Visible) return;
        if (e.OriginalSource is not TextBlock && !ReferenceEquals(e.OriginalSource, Header)) return;
        DesktopHost.GetCursorPos(out dragPoint);
        dragging = true;
        Header.CaptureMouse();
    }
    private void ContinueDrag(object sender, MouseEventArgs e)
    {
        if (!dragging) return;
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
