using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeskIsle.Services;
namespace DeskIsle;

public partial class MainWindow
{
    private Window? newItemDialog;
    private NewItemKind newItemKind;
    private string newItemDirectory = "";
    private bool creatingItem;
    private int newItemVersion;
    internal bool HasPendingCreation => creatingItem || deletingItem || workspace.FileMoveInProgress;

    private void BeginNewItemClick(object sender, RoutedEventArgs e)
    {
        if (HasPendingCreation || sender is not MenuItem { Tag: string tag } || !Enum.TryParse<NewItemKind>(tag, out var kind)) return;
        FinishRename(true);
        var version = ++newItemVersion;
        newItemKind = kind;
        newItemDirectory = DesktopFiles.Root;
        NewItemHeading.Text = "新建" + NewItemService.Label(kind);
        NewItemLocation.Text = newItemDirectory;
        NewItemError.Text = "";
        NewItemPanel.Visibility = Visibility.Visible;
        Files.IsHitTestVisible = false;
        SetNewItemBusy(true);
        NewItemName.Text = "正在准备名称…";
        if (NewItemPanel.Parent is Panel parent) parent.Children.Remove(NewItemPanel);
        if (newItemDialog != null) newItemDialog.Content = null;
        newItemDialog = new Window
        {
            Owner = this, Title = NewItemHeading.Text, Width = 420, Height = 280,
            MaxWidth = SystemParameters.WorkArea.Width, MaxHeight = SystemParameters.WorkArea.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.None,
            AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,
            Foreground = Foreground, ShowInTaskbar = false, Content = NewItemPanel
        };
        var dialog = newItemDialog;
        var target = newItemDirectory;
        dialog.ContentRendered += async (_, _) =>
        {
            try
            {
                var name = await Task.Run(() => NewItemService.SuggestedName(target, kind));
                if (closing || version != newItemVersion || !dialog.IsVisible) return;
                NewItemName.Text = name;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                if (version != newItemVersion) return;
                NewItemName.Text = "新建" + NewItemService.Label(kind) + NewItemService.Extension(kind);
                NewItemError.Text = "桌面目录暂不可用，请检查后重试。";
            }
            finally { if (version == newItemVersion && !closing) SetNewItemBusy(false); }
            if (!dialog.IsVisible || closing || version != newItemVersion) return;
            NewItemName.Focus();
            NewItemName.Select(0, Math.Max(0, NewItemName.Text.Length - NewItemService.Extension(kind).Length));
        };
        TrackDialog(dialog);
        newItemDialog.Closing += (_, args) =>
        {
            if (creatingItem) { args.Cancel = true; return; }
            NewItemPanel.Visibility = Visibility.Collapsed;
            Files.IsHitTestVisible = true;
        };
        newItemDialog.ShowDialog();

    }
    private void SetNewItemBusy(bool busy)
    {
        creatingItem = busy;
        NewItemCreate.IsEnabled = !busy;
        NewItemName.IsEnabled = !busy;
        NewItemCancel.IsEnabled = !busy;
    }
    private static bool CleanupDraft(string work)
    {
        try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    private void CancelNewItem(object sender, RoutedEventArgs e)
    {
        if (creatingItem) return;
        ++newItemVersion;
        NewItemPanel.Visibility = Visibility.Collapsed;
        Files.IsHitTestVisible = true;
        newItemDialog?.Close();
    }
    private void NewItemKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CancelNewItem(sender, e); e.Handled = true; }
        else if (e.Key == Key.Enter && !creatingItem) { CreateNewItem(sender, e); e.Handled = true; }
    }
    private async void CreateNewItem(object sender, RoutedEventArgs e)
    {
        if (creatingItem) return;
        var name = NewItemName.Text;
        var kind = newItemKind;
        try { NewItemService.ValidateName(name, kind); }
        catch (ArgumentException ex) { NewItemError.Text = ex.Message; return; }
        SetNewItemBusy(true);
        NewItemError.Text = "";
        (string Path, string Work) draft;
        try { draft = await Task.Run(() => NewItemService.CreateDraft(name, kind)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetNewItemBusy(false);
            NewItemError.Text = ex is UnauthorizedAccessException ? "没有写入桌面的权限，请检查后重试。" : "创建失败：名称可能已存在，或保存位置不可用。请修改后重试。";
            return;
        }
        SetNewItemBusy(false);
        NewItemPanel.Visibility = Visibility.Collapsed;
        Files.IsHitTestVisible = true;
        newItemDialog?.Close();
        if (!BeginDesktopOperation()) { if (!CleanupDraft(draft.Work)) Status.Text = "草稿清理未完成，材料保留在桌面恢复目录。"; return; }
        TransferReport? report = null;
        string? warning = null;
        try
        {
            report = RunTransfer([draft.Path], DesktopFiles.Root, draft: true);
            foreach (var item in report.Roots.Where(item => item.Published))
                if (!state.References.Contains(item.Target, StringComparer.OrdinalIgnoreCase)) state.References.Add(item.Target);
            state.CreationDirectory = null;
            workspace.Save();
        }
        catch (Exception ex) when (FileTransfer.Recoverable(ex)) { warning = "保存布局未完成：" + ex.Message; }
        finally
        {
            workspace.EndFileMove();
            // Only the unique draft allocated above is removed, never a user source.
            if (!CleanupDraft(draft.Work)) warning ??= "草稿清理未完成，材料保留在桌面恢复目录。";
        }
        await workspace.RefreshAfterFileMove(DesktopFiles.Root, DesktopFiles.Root);
        if (closing) return;
        if (report != null) Status.Text = report.Summary;
        if (warning != null) Status.Text = warning;
        var path = report?.Roots.FirstOrDefault(item => item.Published)?.Target;
        selectedTile = tiles.FirstOrDefault(x => string.Equals(x.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (selectedTile != null)
        {
            var index = tiles.ToList().IndexOf(selectedTile);
            if (columns > 0 && Files.Items.Count > index / columns) Files.ScrollIntoView(Files.Items[index / columns]);
            Files.UpdateLayout();
            var button = VisibleTiles(Files).FirstOrDefault(b => ReferenceEquals(b.DataContext, selectedTile));
            button?.Focus();
        }
    }
}
