using System.IO;
using System.Windows;
using Microsoft.VisualBasic.FileIO;
namespace DeskIsle;

public partial class MainWindow
{
    private bool deletingItem;
    private async void DeleteFileClick(object sender, RoutedEventArgs e)
    {
        if (deletingItem || creatingItem) return;
        var tile = (sender as FrameworkElement)?.DataContext as FileTile ?? selectedTile;
        if (tile == null) return;
        var path = tile.FullPath;
        if (!File.Exists(path) && !Directory.Exists(path))
        { MessageBox.Show(this, "项目已不存在或路径不可用，可刷新或移出引用。", "无法删除"); return; }
        if (MessageBox.Show(this, $"删除真实项目？\n\n{path}\n\n将请求Windows移入回收站；不能回收时请在系统提示中取消。\n若只想从卡片移除，请取消并使用“移出卡片”。", "删除文件", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        deletingItem = true;
        try
        {
            // Shell dialogs preserve Windows' recycle/error/cancel behavior. Never
            // retry using File.Delete or recursively delete on a failed recycle.
            if (Directory.Exists(path)) FileSystem.DeleteDirectory(path, UIOption.AllDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
            else FileSystem.DeleteFile(path, UIOption.AllDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                state.References.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
                SaveCard();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException or System.ComponentModel.Win32Exception)
        { MessageBox.Show(this, "删除未完成，请检查文件占用、权限或目录连接。原引用保留，可刷新后重试。", "DeskIsle"); }
        finally { deletingItem = false; }
        if (closing) return;
        if (directory == null) await LoadReferences(); else await LoadFolder();
        SaveCard();
    }
}
