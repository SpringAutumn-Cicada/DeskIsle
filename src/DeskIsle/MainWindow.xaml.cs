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

    public MainWindow()
    {
        InitializeComponent();
        Left = SystemParameters.WorkArea.Left + 24;
        Top = SystemParameters.WorkArea.Top + 48;
        SourceInitialized += (_, _) => host.SetHandle(this);
        Loaded += (_, _) => { SetDesktopMode(true); monitor.Start(); };
        monitor.Tick += (_, _) =>
        {
            if (!desktopRequested || host.IsAlive) return;
            host.Detach();
            if (++recoveryAttempts <= 3 && host.TryAttach(this)) return;
            desktopRequested = false;
            UpdateMode("桌面连接中断，已切换为浮动模式；可手动重试。");
        };
        Closed += (_, _) => { monitor.Stop(); loading?.Cancel(); icons.Dispose(); host.Detach(); };
    }

    private async void ChooseClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择要展示的文件夹" };
        if (picker.ShowDialog(this) != true) return;
        directory = picker.FolderName;
        TitleText.Text = new DirectoryInfo(directory).Name;
        TitleText.ToolTip = directory;
        await LoadFolder();
    }

    private async Task LoadFolder()
    {
        if (directory == null) return;
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
            if (request.IsCancellationRequested) return;
            tiles = entries.Select(x => new FileTile(x)).ToArray();
            RebuildRows();
            Status.Text = entries.Count == 0 ? "目录为空。" : $"{entries.Count} 个项目 · 双击打开";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (!request.IsCancellationRequested) Status.Text = "目录不可用或没有访问权限。请刷新或重新选择目录。";
        }
        finally { if (ReferenceEquals(loading, request)) loading = null; request.Dispose(); }
    }

    private async void RefreshClick(object sender, RoutedEventArgs e) => await LoadFolder();
    private void OpenClick(object sender, RoutedEventArgs e)
    {
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
        ModeText.Text = host.Attached ? "桌面模式" : "浮动模式 · 详情见菜单";
    }
    private void RebuildRows()
    {
        columns = Math.Max(1, (int)((Files.ActualWidth - 24) / 100));
        Files.ItemsSource = tiles.Chunk(columns).Select(x => new TileRow(x)).ToArray();
    }
    private void GridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Files != null && Math.Max(1, (int)((Files.ActualWidth - 24) / 100)) != columns) RebuildRows();
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
        if (sender is Button { ContextMenu: { } menu } button)
        { menu.PlacementTarget = button; menu.IsOpen = true; }
    }
    private void ModeInfoClick(object sender, RoutedEventArgs e) => MessageBox.Show(this, modeDetails, "显示模式");
    private void OpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Surface != null) Surface.Background = new SolidColorBrush(Color.FromArgb((byte)(255 * e.NewValue), 36, 41, 53));
    }
    private void BeginDrag(object sender, MouseButtonEventArgs e)
    {
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
    private void ExitClick(object sender, RoutedEventArgs e) => Close();
}
