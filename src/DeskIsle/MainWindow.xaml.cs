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
        Closed += (_, _) => { monitor.Stop(); loading?.Cancel(); host.Detach(); };
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
        Files.ItemsSource = null;
        Status.Text = "正在读取目录…";
        try
        {
            var path = directory;
            var entries = await Task.Run(() => FolderReader.Read(path, request.Token), request.Token);
            if (request.IsCancellationRequested) return;
            Files.ItemsSource = entries;
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
        if (Files.SelectedItem is FolderEntry entry) ShellOpen(entry.FullPath);
    }
    private void RevealClick(object sender, RoutedEventArgs e)
    {
        if (Files.SelectedItem is not FolderEntry entry) return;
        ShellOpen(Path.GetDirectoryName(entry.FullPath)!);
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
        ModeButton.Content = host.Attached ? "切换浮动模式" : "尝试桌面模式";
        ModeText.Text = reason ?? (host.Attached ? "桌面模式 · 兼容性待 Windows 验收" : "浮动模式 · Win+D 可能隐藏卡片");
    }
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
