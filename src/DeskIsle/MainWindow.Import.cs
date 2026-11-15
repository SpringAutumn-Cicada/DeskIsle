using System.IO;
using System.Windows;
using DeskIsle.Services;
namespace DeskIsle;

public partial class MainWindow
{
    private TransferProgressWindow? currentTransfer;
    private void ViewTransferClick(object sender, RoutedEventArgs e) => currentTransfer?.RevealProgress();
    private async Task<TransferReport> RunTransfer(string[] sources, string target, bool move = false, string? name = null, bool draft = false)
    {
        var dialog = new TransferProgressWindow(this, move ? "正在移动桌面项目" : "正在导入到桌面",
            engine => engine.Run(sources.OrderBy(path => path.Length), target, move, name, draft),
            completion => Dispatcher.InvokeAsync(() => workspace.RecordTransfer(state.Id, completion)).Task,
            null, moving: move);
        TrackDialog(dialog);
        currentTransfer = dialog;
        try { return await dialog.RunAsync(); }
        finally { currentTransfer = null; }
    }
    private bool BeginDesktopOperation()
    {
        if (closing || HasPendingCreation) return false;
        try
        {
            if (!workspace.BeginFileMove()) { Status.Text = "请先完成其他卡片的操作或关闭弹窗。"; return false; }
            return true;
        }
        catch (Exception ex) when (FileTransfer.Recoverable(ex))
        { workspace.EndFileMove(); Status.Text = ex.Message; return false; }
    }
    private async void ImportDesktopFiles(string[] sources, string? explicitFolder = null)
    {
        if (!BeginDesktopOperation()) return;
        var report = new TransferReport();
        try
        {
            var target = explicitFolder ?? DesktopFiles.Root;
            var copies = new List<string>();
            var moves = new List<string>();
            foreach (var raw in sources)
            {
                try
                {
                    var path = FileMoveService.Normalize(raw);
                    if (explicitFolder == null && DesktopFiles.IsCardPath(path))
                    {
                        // Pull a desktop subfolder item out to the desktop, rather than retaining a nested reference.
                        if (!DesktopFiles.IsDesktopItem(path)) { moves.Add(path); continue; }
                        await Task.Run(() => DesktopFiles.Require(path));
                        if (!state.References.Contains(path, StringComparer.OrdinalIgnoreCase)) state.References.Add(path);
                        report.Completed++;
                    }
                    else copies.Add(path);
                }
                catch (Exception ex) when (FileTransfer.Recoverable(ex)) { report.Errors.Add(raw + "：" + ex.Message); }
            }
            foreach (var batch in new[] { (Paths: moves, Move: true), (Paths: copies, Move: false) })
            {
                if (report.Cancelled || batch.Paths.Count == 0) continue;
                var transferred = await RunTransfer(batch.Paths.ToArray(), target, move: batch.Move);
                report.Completed += transferred.Completed;
                report.Skipped += transferred.Skipped;
                report.Cancelled |= transferred.Cancelled;
                report.Errors.AddRange(transferred.Errors);
                report.Roots.AddRange(transferred.Roots);
                foreach (var item in transferred.Roots.Where(item => item.Published && DesktopFiles.IsDesktopItem(item.Target)))
                    if (!state.References.Contains(item.Target, StringComparer.OrdinalIgnoreCase)) state.References.Add(item.Target);
            }
            workspace.Save();
        }
        catch (Exception ex) when (FileTransfer.Recoverable(ex)) { report.Errors.Add(ex.Message); }
        finally { workspace.EndFileMove(); }
        await workspace.RefreshAfterFileMove(DesktopFiles.Root, DesktopFiles.Root);
        if (!closing) Status.Text = report.Errors.Count > 0 ? report.Errors[0] : "";
    }
    private FileTile? ExternalFolderTarget(DragEventArgs e)
    {
        var point = e.GetPosition(Files);
        var button = FindTile(Files.InputHitTest(point) as DependencyObject);
        if (button?.DataContext is not FileTile { Entry.IsDirectory: true } folder || !DesktopFiles.IsCardPath(folder.FullPath)) return null;
        var local = e.GetPosition(button);
        return local.X >= button.ActualWidth * .2 && local.X <= button.ActualWidth * .8 ? folder : null;
    }
}
