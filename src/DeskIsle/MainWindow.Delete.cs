using DeskIsle.Services;
using System.IO;
using System.Windows;
namespace DeskIsle;

public partial class MainWindow
{
    private bool deletingItem;
    private async void DeleteFileClick(object sender, RoutedEventArgs e)
    {
        if (HasPendingCreation) return;
        var tile = (sender as FrameworkElement)?.DataContext as FileTile ?? selectedTile;
        if (tile == null) return;
        var path = tile.FullPath;
        try { await Task.Run(() => DesktopFiles.Require(path)); }
        catch (Exception ex) when (FileTransfer.Recoverable(ex)) { Status.Text = ex.Message; return; }
        if (HasPendingCreation) return;
        if (!File.Exists(path) && !Directory.Exists(path))
        { MessageBox.Show(this, "项目已不存在或路径不可用，请刷新后重试。", "无法删除"); return; }
        var operationStarted = false;
        deletingItem = true;
        try
        {
            var dialog = new DeleteConfirmation(this, path);
            TrackDialog(dialog);
            if (dialog.ShowDialog() != true) return;
            // The confirmation is closed; take the shared operation gate before yielding to Shell.
            deletingItem = false;
            operationStarted = workspace.BeginFileMove();
            deletingItem = true;
            if (!operationStarted) { Status.Text = "请先完成其他卡片的文件操作。"; return; }
            Status.Text = "正在移入回收站…";
            StartupDiagnostics.Mark("recycle-start");
            await RecycleService.Delete(path);
            StartupDiagnostics.Mark("recycle-completed");
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                state.References.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
                SaveCard();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException or System.ComponentModel.Win32Exception)
        { MessageBox.Show(this, "删除未完成，请检查文件占用、权限或目录连接。原引用保留，可刷新后重试。", "DeskIsle"); }
        finally { if (operationStarted) workspace.EndFileMove(); deletingItem = false; }
        if (closing) return;
        if (Status.Text == "正在移入回收站…") Status.Text = "";
        if (directory == null) RequestFileSync(); else await LoadFolder();
        SaveCard();
    }
}
