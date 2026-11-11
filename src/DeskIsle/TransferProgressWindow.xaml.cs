using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DeskIsle.Services;
namespace DeskIsle;
public partial class TransferProgressWindow : Window
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly DispatcherTimer reveal = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly TaskCompletionSource dismissed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<FileTransfer, Task<TransferReport>> operation;
    private readonly Func<TransferCompletion, Task> committed;
    private readonly IReadOnlyDictionary<string, string>? imported;
    private readonly bool delayedProgress;
    private bool finished;
    private bool started;
    private CollisionAction? filesDecision;
    private CollisionAction? foldersDecision;
    public TransferReport Report { get; private set; } = new();
    public TransferProgressWindow(Window owner, string heading, Func<FileTransfer, Task<TransferReport>> operation,
        Func<TransferCompletion, Task> committed, IReadOnlyDictionary<string, string>? imported = null, bool moving = false)
    {
        InitializeComponent(); Owner = owner; Heading.Text = heading;
        this.operation = operation; this.committed = committed; this.imported = imported;
        Summary.Text = moving ? "正在移动桌面文件；仅同名时询问。" : "复制到桌面，原件保留；同名项目会询问。";
        ShowActivated = false;
        delayedProgress = !moving;
        MaxWidth = SystemParameters.WorkArea.Width; MaxHeight = SystemParameters.WorkArea.Height;
        reveal.Tick += (_, _) => { reveal.Stop(); if (!finished && !IsVisible) Show(); };
        Closing += (_, e) => { if (!finished) { e.Cancel = true; RequestCancel(); } };
        Closed += (_, _) => { reveal.Stop(); cancellation.Dispose(); dismissed.TrySetResult(); };
    }
    public async Task<TransferReport> RunAsync()
    {
        if (started) throw new InvalidOperationException("文件操作不能重复启动。");
        started = true;
        if (delayedProgress) reveal.Start();
        try
        {
            var engine = new FileTransfer(Ask, committed, UpdateProgress, cancellation.Token, imported);
            Report = await Task.Run(() => operation(engine));
        }
        catch (OperationCanceledException) { Report.Cancelled = true; }
        catch (Exception ex) { Report.Errors.Add("文件操作未完成：" + ex.Message); }
        finished = true;
        reveal.Stop();
        Heading.Text = Report.Cancelled ? "操作已取消" : "文件操作完成";
        Progress.IsIndeterminate = false; Progress.Value = 1;
        Summary.Text = Report.Summary;
        Details.Text = string.Join(Environment.NewLine, Report.Errors);
        CancelAction.IsEnabled = true; CancelAction.Content = "完成";
        if (Report.Errors.Count == 0 && !Report.Cancelled) { Close(); dismissed.TrySetResult(); }
        else if (!IsVisible) Show();
        await dismissed.Task;
        return Report;
    }
    private async Task<CollisionAction> Ask(Collision collision)
    {
        cancellation.Token.ThrowIfCancellationRequested();
        return await Dispatcher.InvokeAsync(() =>
        {
            if (cancellation.IsCancellationRequested) return CollisionAction.Cancel;
            var sameType = collision.Incoming.Directory == collision.Existing.Directory;
            var saved = collision.Incoming.Directory ? foldersDecision : filesDecision;
            if (sameType && saved.HasValue) return saved.Value;
            // Do not flash a progress window under a fast operation's conflict question.
            reveal.Stop();
            var dialog = new TransferConflict(IsVisible ? this : Owner, collision);
            if (Owner is MainWindow main) main.TrackTransferDialog(dialog);
            dialog.ShowDialog();
            if (sameType && dialog.Remember && dialog.Choice != CollisionAction.Cancel)
            { if (collision.Incoming.Directory) foldersDecision = dialog.Choice; else filesDecision = dialog.Choice; }
            if (delayedProgress && !finished && !IsVisible) reveal.Start();
            return dialog.Choice;
        });
    }
    private void UpdateProgress(TransferProgress value)
    {
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (finished) return;
            CurrentPath.Text = value.Path;
            Progress.IsIndeterminate = false;
            Progress.Value = value.Total == 0 ? 1 : (double)value.Bytes / value.Total;
            Summary.Text = $"当前文件 {value.Bytes:N0} / {value.Total:N0} 字节";
        }));
    }
    public void RevealProgress() { if (!finished && !IsVisible) Show(); }
    private void RequestCancel()
    {
        cancellation.Cancel(); CancelAction.Content = "正在取消…"; CancelAction.IsEnabled = false;
    }
    private void Cancel(object sender, RoutedEventArgs e) { if (finished) Close(); else RequestCancel(); }
    private void OpenRecovery(object sender, RoutedEventArgs e)
    {
        try { if (Directory.Exists(DesktopFiles.PrivateRoot)) Process.Start(new ProcessStartInfo(DesktopFiles.PrivateRoot) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException) { Details.Text = "无法打开恢复目录：" + ex.Message; }
    }
}
