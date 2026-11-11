using System.IO;
using System.Windows;
using DeskIsle.Services;
namespace DeskIsle;

public partial class MainWindow
{
    private TransferProgressWindow? currentTransfer;
    private void ViewTransferClick(object sender, RoutedEventArgs e) => currentTransfer?.RevealProgress();
    private async Task<TransferReport> RunTransfer(string[] sources, string target, bool move = false, string? name = null, bool migration = false, bool draft = false)
    {
        var dialog = new TransferProgressWindow(this, move ? "正在移入桌面文件夹" : "正在导入到桌面",
            engine => engine.Run(sources.OrderBy(path => path.Length), target, move, name, draft),
            completion => Dispatcher.InvokeAsync(() => workspace.RecordTransfer(state.Id, completion)).Task,
            migration ? workspace.ImportedPaths : null, moving: move);
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
            if (workspace.PendingDesktopPaths.Length > 0) workspace.EnsureMigrationBackup();
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
            foreach (var raw in sources)
            {
                try
                {
                    var path = FileMoveService.Normalize(raw);
                    if (explicitFolder == null && DesktopFiles.IsCardPath(path))
                    {
                        await Task.Run(() => DesktopFiles.Require(path));
                        if (!state.References.Contains(path, StringComparer.OrdinalIgnoreCase)) state.References.Add(path);
                        report.Completed++;
                    }
                    else copies.Add(path);
                }
                catch (Exception ex) when (FileTransfer.Recoverable(ex)) { report.Errors.Add(raw + "：" + ex.Message); }
            }
            if (copies.Count > 0)
            {
                var transferred = await RunTransfer(copies.ToArray(), target);
                transferred.Completed += report.Completed; transferred.Errors.AddRange(report.Errors);
                report = transferred;
                foreach (var item in report.Roots.Where(item => item.Published))
                    if (!state.References.Contains(item.Target, StringComparer.OrdinalIgnoreCase)) state.References.Add(item.Target);
            }
            workspace.Save();
        }
        catch (Exception ex) when (FileTransfer.Recoverable(ex)) { report.Errors.Add(ex.Message); }
        finally { workspace.EndFileMove(); }
        await workspace.RefreshAfterFileMove(DesktopFiles.Root, DesktopFiles.Root);
        if (!closing) Status.Text = report.Errors.Count > 0 ? report.Errors[0] : "";
    }
    private void UpdateMigrationNotice()
    {
        var count = workspace.PendingDesktopPaths.Length;
        MigrationMenu.Header = count == 0 ? "导入待迁移旧记录…" : $"导入待迁移旧记录（{count}）…";
        MigrationMenu.IsEnabled = RemovePendingMenu.IsEnabled = count > 0;
    }
    internal void OfferDesktopMigration() => MigrateDesktopClick(this, new RoutedEventArgs());
    private async void MigrateDesktopClick(object sender, RoutedEventArgs e)
    {
        if (!BeginDesktopOperation()) return;
        TransferReport? report = null;
        string? warning = null;
        try
        {
            var pending = workspace.PendingDesktopPaths;
            if (pending.Length == 0) return;
            workspace.MarkDesktopMigrationPrompted();
            var review = new MigrationReview(this, pending);
            TrackDialog(review);
            if (review.ShowDialog() != true) return;
            report = await RunTransfer(pending, DesktopFiles.Root, migration: true);
            workspace.PublishMigrationRoots(report);
        }
        catch (Exception ex) when (FileTransfer.Recoverable(ex)) { warning = "迁移未完成：" + ex.Message; }
        finally { workspace.EndFileMove(); UpdateMigrationNotice(); }
        await workspace.RefreshAfterFileMove(DesktopFiles.Root, DesktopFiles.Root);
        if (!closing && report != null) Status.Text = report.Errors.Count > 0 ? report.Errors[0] : "";
        if (!closing && warning != null) Status.Text = warning;
    }
    private async void RemovePendingClick(object sender, RoutedEventArgs e)
    {
        if (workspace.PendingDesktopPaths.Length == 0 || !BeginDesktopOperation()) return;
        try
        {
            var review = new MigrationReview(this, workspace.PendingDesktopPaths, remove: true);
            TrackDialog(review);
            if (review.ShowDialog() == true) workspace.RemovePendingDesktopPaths();
        }
        catch (Exception ex) when (FileTransfer.Recoverable(ex)) { Status.Text = ex.Message; }
        finally { workspace.EndFileMove(); UpdateMigrationNotice(); }
        await workspace.RefreshAfterFileMove(DesktopFiles.Root, DesktopFiles.Root);
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
