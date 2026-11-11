using System.IO;
using System.Windows;
using DeskIsle.Services;
namespace DeskIsle;

public partial class MainWindow
{
    private async void MoveIntoFolder(string source, string target)
    {
        if (closing || HasPendingCreation) return;
        try { if (!workspace.BeginFileMove()) { Status.Text = "请先完成其他卡片的文件操作或关闭弹窗。"; return; } }
        catch (IOException ex) { Status.Text = ex.Message; return; }
        string? destination = null;
        string? warning = null;
        try
        {
            var dialog = new MoveConfirmation(this, source, target);
            TrackDialog(dialog);
            if (dialog.ShowDialog() != true) return;
            Status.Text = "正在移入文件夹…";
            destination = await Task.Run(() => FileMoveService.Move(source, target, dialog.ChosenName));
            try { workspace.ApplyFileMove(state.Id, source, destination); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { warning = $"文件已移入 {destination}，但布局未保存，请检查配置目录权限。"; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException or NotSupportedException)
        {
            warning = $"移动未完成：{ex.Message} 请检查原位置与目标目录后重试。";
        }
        finally { workspace.EndFileMove(); }
        if (destination != null) await workspace.RefreshAfterFileMove(source, destination);
        else ScheduleFileSync();
        if (!closing && warning != null) Status.Text = warning;
    }
    internal async Task RefreshAfterFileMove(string source, string destination)
    {
        if (closing) return;
        if (directory == null) await LoadReferences();
        else { directory = FileMoveService.Remap(directory, source, destination); await LoadFolder(); }
    }
}
