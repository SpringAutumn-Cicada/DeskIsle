using System.IO;
using System.Windows;
using DeskIsle.Services;
namespace DeskIsle;

public partial class MainWindow
{
    private async void MoveIntoFolder(string source, string target)
    {
        if (!BeginDesktopOperation()) return;
        TransferReport? report = null;
        string? warning = null;
        try
        {
            await Task.Run(() => FileMoveService.Validate(source, target, Path.GetFileName(FileMoveService.Normalize(source))));
            report = await RunTransfer([source], target, move: true);
        }
        catch (Exception ex) when (FileTransfer.Recoverable(ex)) { warning = "移动未完成：" + ex.Message; }
        finally { workspace.EndFileMove(); }
        await workspace.RefreshAfterFileMove(DesktopFiles.Root, DesktopFiles.Root);
        if (!closing && report != null) Status.Text = report.Errors.Count > 0 ? report.Errors[0] : "";
        if (!closing && warning != null) Status.Text = warning;
    }
    internal async Task RefreshAfterFileMove(string source, string destination)
    {
        if (closing) return;
        // All current cards are desktop groups; arbitrary directory browsing is removed.
        await LoadReferences();
    }
}
