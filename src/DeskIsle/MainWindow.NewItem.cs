using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeskIsle.Services;
using Microsoft.Win32;
namespace DeskIsle;

public partial class MainWindow
{
    private Window? newItemDialog;
    private NewItemKind newItemKind;
    private string newItemDirectory = "";
    private bool creatingItem;
    private bool newItemInFolder;
    private int newItemVersion;
    internal bool HasPendingCreation => creatingItem || deletingItem;

    private async void BeginNewItemClick(object sender, RoutedEventArgs e)
    {
        if (creatingItem || deletingItem || sender is not MenuItem { Tag: string tag } || !Enum.TryParse<NewItemKind>(tag, out var kind)) return;
        FinishRename(true);
        var version = ++newItemVersion;
        newItemKind = kind;
        newItemInFolder = directory != null;
        newItemDirectory = directory ?? state.CreationDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        NewItemHeading.Text = kind switch { NewItemKind.Folder => "新建文件夹", NewItemKind.Text => "新建文本文档", _ => "新建 Markdown 文档" };
        NewItemLocation.Text = newItemDirectory;
        NewItemError.Text = "";
        NewItemPanel.Visibility = Visibility.Visible;
        Files.IsHitTestVisible = false;
        NewItemBrowse.IsEnabled = !newItemInFolder;
        SetNewItemBusy(true);
        try
        {
            var target = newItemDirectory;
            var name = await Task.Run(() => NewItemService.SuggestedName(target, kind));
            if (closing || version != newItemVersion) return;
            SetNewItemBusy(false);
            NewItemName.Text = name;
            NewItemName.Focus();
            NewItemName.Select(0, name.Length - NewItemService.Extension(kind).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { if (version == newItemVersion) NewItemError.Text = "无法读取保存目录，请更换位置。"; }
        finally { if (version == newItemVersion && !closing) SetNewItemBusy(false); }
        if (closing || version != newItemVersion || NewItemPanel.Visibility != Visibility.Visible) return;
        if (NewItemPanel.Parent is Panel parent) parent.Children.Remove(NewItemPanel);
        if (newItemDialog != null) newItemDialog.Content = null;
        newItemDialog = new Window
        {
            Owner = this, Title = NewItemHeading.Text, Width = 400, Height = 360,
            MaxWidth = SystemParameters.WorkArea.Width, MaxHeight = SystemParameters.WorkArea.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.None,
            AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,
            Foreground = Foreground, ShowInTaskbar = false, Content = NewItemPanel
        };
        newItemDialog.ContentRendered += (_, _) => { NewItemName.Focus(); NewItemName.Select(0, Math.Max(0, NewItemName.Text.Length - NewItemService.Extension(newItemKind).Length)); };
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
        NewItemBrowse.IsEnabled = !busy && !newItemInFolder;
        NewItemCancel.IsEnabled = !busy;
    }
    private void BrowseNewItemLocation(object sender, RoutedEventArgs e)
    {
        if (creatingItem || newItemInFolder) return;
        var picker = new OpenFolderDialog { Title = "选择新建文件保存位置" };
        if (picker.ShowDialog(newItemDialog ?? this) == true)
        {
            newItemDirectory = picker.FolderName;
            NewItemLocation.Text = newItemDirectory;
            NewItemError.Text = "";
        }
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
        var target = newItemDirectory;
        var name = NewItemName.Text;
        var kind = newItemKind;
        try { NewItemService.ValidateName(name, kind); }
        catch (ArgumentException ex) { NewItemError.Text = ex.Message; return; }
        SetNewItemBusy(true);
        NewItemError.Text = "";
        string path;
        try { path = await Task.Run(() => NewItemService.Create(target, name, kind)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetNewItemBusy(false);
            NewItemError.Text = ex is UnauthorizedAccessException ? "没有写入权限，请更换保存位置。" : "创建失败：名称可能已存在，或保存位置不可用。请修改后重试。";
            return;
        }
        // Once creation succeeds, retain the reference even if the window is being closed.
        if (!newItemInFolder)
        {
            state.CreationDirectory = target;
            if (!state.References.Contains(path, StringComparer.OrdinalIgnoreCase)) state.References.Add(path);
        }
        SaveCard();
        SetNewItemBusy(false);
        NewItemPanel.Visibility = Visibility.Collapsed;
        Files.IsHitTestVisible = true;
        newItemDialog?.Close();
        if (closing) return;
        if (newItemInFolder && directory != null) await LoadFolder(); else await LoadReferences();
        SaveCard();
        if (closing) return;
        selectedTile = tiles.FirstOrDefault(x => x.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase));
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
