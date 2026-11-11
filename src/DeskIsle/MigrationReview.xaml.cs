using System.Windows;
namespace DeskIsle;
public partial class MigrationReview : Window
{
    public MigrationReview(Window owner, string[] paths, bool remove = false)
    {
        InitializeComponent(); Owner = owner; Items.ItemsSource = paths;
        MaxWidth = SystemParameters.WorkArea.Width; MaxHeight = SystemParameters.WorkArea.Height;
        Heading.Text = $"{paths.Length} 项待" + (remove ? "移除旧记录" : "导入桌面");
        if (remove)
        {
            Explanation.Text = "只移除这些旧引用，不删除原文件。原配置备份仍保留。";
            ConfirmAction.Content = "移除旧记录";
        }
    }
    private void Confirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
