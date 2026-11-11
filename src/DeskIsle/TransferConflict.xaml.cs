using System.IO;
using System.Windows;
using DeskIsle.Services;
namespace DeskIsle;
public partial class TransferConflict : Window
{
    public CollisionAction Choice { get; private set; } = CollisionAction.Cancel;
    public bool Remember => ApplyAll.IsChecked == true;
    public TransferConflict(Window owner, Collision collision)
    {
        InitializeComponent(); Owner = owner;
        MaxWidth = SystemParameters.WorkArea.Width; MaxHeight = SystemParameters.WorkArea.Height;
        NameLabel.Text = Path.GetFileName(collision.Target);
        SourcePath.Text = collision.Source; TargetPath.Text = collision.Target;
        SourceMeta.Text = Meta(collision.Incoming); TargetMeta.Text = Meta(collision.Existing);
        if (collision.Incoming.Directory != collision.Existing.Directory)
        {
            Heading.Text = "文件和文件夹重名";
            Explanation.Text = "类型不同，不会整项替换。请跳过，或取消后修改来源名称再导入。";
            ReplaceAction.IsEnabled = false; ApplyAll.Visibility = Visibility.Collapsed;
        }
        else if (collision.Incoming.Directory)
        {
            Heading.Text = "目标已有同名文件夹"; ReplaceAction.Content = "合并文件夹";
            ReplaceAction.Background = System.Windows.Media.Brushes.DarkSlateGray;
            Explanation.Text = "保留目标独有内容，内部同名文件将另行询问，不删除整个目标文件夹。";
        }
    }
    private static string Meta(EntryStamp stamp) => (stamp.Directory ? "文件夹" : $"{stamp.Length:N0} 字节") + " · " + stamp.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    private void Skip(object sender, RoutedEventArgs e) { Choice = CollisionAction.Skip; DialogResult = true; }
    private void Replace(object sender, RoutedEventArgs e) { Choice = CollisionAction.Replace; DialogResult = true; }
}
