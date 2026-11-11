using System.Diagnostics;
using System.IO;
using System.Windows;
using DeskIsle.Services;
namespace DeskIsle;
public partial class TransferProgressWindow : Window
{
    private readonly CancellationTokenSource cancellation = new();
    private bool finished;
    private bool started;
    private CollisionAction? filesDecision;
    private CollisionAction? foldersDecision;
    public TransferReport Report { get; private set; } = new();
    public TransferProgressWindow(Window owner, string heading, Func<FileTransfer, Task<TransferReport>> operation,
        Func<TransferCompletion, Task> committed, IReadOnlyDictionary<string, string>? imported = null)
    {
        InitializeComponent(); Owner = owner; Heading.Text = heading;
        MaxWidth = SystemParameters.WorkArea.Width; MaxHeight = SystemParameters.WorkArea.Height;
        Closing += (_, e) => { if (!finished) { e.Cancel = true; RequestCancel(); } };
        Closed += (_, _) => cancellation.Dispose();
        ContentRendered += async (_, _) =>
        {
            if (started) return; started = true;
            try
            {
                var engine = new FileTransfer(Ask, committed, UpdateProgress, cancellation.Token, imported);
                Report = await Task.Run(() => operation(engine));
            }
            catch (OperationCanceledException) { Report.Cancelled = true; }
            catch (Exception ex) { Report.Errors.Add("文件操作未完成：" + ex.Message); }
            finished = true;
            Heading.Text = Report.Cancelled ? "操作已取消" : "文件操作完成";
            Progress.IsIndeterminate = false; Progress.Value = 1;
            Summary.Text = Report.Summary;
            Details.Text = string.Join(Environment.NewLine, Report.Errors);
            CancelAction.IsEnabled = true; CancelAction.Content = "完成";
        };
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
            var dialog = new TransferConflict(this, collision);
            if (Owner is MainWindow main) main.TrackTransferDialog(dialog);
            dialog.ShowDialog();
            if (sameType && dialog.Remember && dialog.Choice != CollisionAction.Cancel)
            { if (collision.Incoming.Directory) foldersDecision = dialog.Choice; else filesDecision = dialog.Choice; }
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
    private void RequestCancel()
    {
        cancellation.Cancel(); CancelAction.Content = "正在取消…"; CancelAction.IsEnabled = false;
    }
    private void Cancel(object sender, RoutedEventArgs e) { if (finished) Close(); else RequestCancel(); }
    private void OpenRecovery(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(DesktopFiles.PrivateRoot)) Process.Start(new ProcessStartInfo(DesktopFiles.PrivateRoot) { UseShellExecute = true });
    }
}
