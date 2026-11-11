using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DeskIsle.Services;
namespace DeskIsle;

public partial class MainWindow
{
    private FileTile? ActionTile(object sender) => (sender as FrameworkElement)?.DataContext as FileTile ?? selectedTile;
    private async void RenameFileClick(object sender, RoutedEventArgs e)
    {
        var tile = ActionTile(sender);
        if (tile == null || !BeginDesktopOperation()) return;
        var source = tile.FullPath;
        string? destination = null;
        try
        {
            DesktopFiles.Require(source);
            var dialog = new RenameFileWindow(this, Path.GetFileName(source)); TrackDialog(dialog);
            if (dialog.ShowDialog() != true) return;
            destination = Path.Combine(Path.GetDirectoryName(source)!, dialog.FileName);
            if (string.Equals(source, destination, StringComparison.Ordinal)) return;
            // Case-only rename requires a reversible temporary hop; keep this operation explicit instead.
            if (FileMoveService.Same(source, destination)) throw new IOException("仅修改大小写时，请先改成另一个名称再修改。");
            await Task.Run(() =>
            {
                DesktopFiles.Require(source); DesktopFiles.Require(destination, missingAllowed: true);
                if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("此名称已存在，请换一个名称。");
                if (Directory.Exists(source)) Directory.Move(source, destination);
                else File.Move(source, destination, overwrite: false);
            });
            workspace.RecordDesktopRename(source, destination);
        }
        catch (Exception ex) when (FileTransfer.Recoverable(ex)) { Status.Text = "重命名未完成：" + ex.Message; }
        finally { workspace.EndFileMove(); }
        if (destination != null && !closing) await workspace.RefreshAfterFileMove(source, destination);
    }
    private void ClipboardFileClick(object sender, RoutedEventArgs e)
    {
        var tile = ActionTile(sender);
        if (tile == null || HasPendingCreation) return;
        try
        {
            DesktopFiles.Require(tile.FullPath);
            var mode = (sender as System.Windows.Controls.MenuItem)?.Tag as string;
            if (mode == "path") { Clipboard.SetText(tile.FullPath); return; }
            var data = new DataObject();
            data.SetFileDropList(new StringCollection { tile.FullPath });
            using var effect = new MemoryStream(BitConverter.GetBytes(mode == "cut" ? 2 : 1));
            data.SetData("Preferred DropEffect", effect);
            Clipboard.SetDataObject(data, true);
        }
        catch (Exception ex) when (FileTransfer.Recoverable(ex) || ex is ExternalException) { Status.Text = "复制未完成：" + ex.Message; }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHObjectProperties(nint owner, uint type, string name, string? page);
    private void FilePropertiesClick(object sender, RoutedEventArgs e)
    {
        var tile = ActionTile(sender);
        if (tile == null || HasPendingCreation) return;
        try
        {
            DesktopFiles.Require(tile.FullPath);
            if (!SHObjectProperties(new WindowInteropHelper(this).Handle, 2, tile.FullPath, null)) Status.Text = "无法打开系统属性，请检查路径是否可用。";
        }
        catch (Exception ex) when (FileTransfer.Recoverable(ex)) { Status.Text = ex.Message; }
    }
}
