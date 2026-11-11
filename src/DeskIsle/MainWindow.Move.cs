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
        try
        {
            var dialog = new MoveConfirmation(this, source, target);
            TrackDialog(dialog);
            if (dialog.ShowDialog() != true) return;
            report = RunTransfer([source], target, move: true, name: dialog.ChosenName);
        }
        catch (Exception ex) when (FileTransfer.Recoverable(ex)) { Status.Text = "移动未完成：" + ex.Message; }
        finally { workspace.EndFileMove(); }
        await workspace.RefreshAfterFileMove(DesktopFiles.Root, DesktopFiles.Root);
        if (!closing && report != null) Status.Text = report.Summary + (report.Errors.Count > 0 ? " · " + report.Errors[0] : "");
    }
    internal async Task RefreshAfterFileMove(string source, string destination)
    {
        if (closing) return;
        // All current cards are desktop groups; arbitrary directory browsing is removed.
        await LoadReferences();
    }
}
