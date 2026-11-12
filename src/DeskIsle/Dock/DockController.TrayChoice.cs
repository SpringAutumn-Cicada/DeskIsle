using System.Windows;
using System.Windows.Controls;
namespace DeskIsle.Dock;
internal sealed partial class DockController
{
    internal void OpenRestoreDiagnostics()
    {
        try { System.IO.Directory.CreateDirectory(DockRestoreDiagnostics.Folder); Launch(DockRestoreDiagnostics.Folder); }
        catch (Exception ex) when (Recoverable(ex)) { MessageBox.Show("无法打开诊断目录：" + ex.Message, "DeskIsle"); }
    }
    private string? SelectTrayTarget(DockTrayActivation.Candidate[] candidates)
    {
        if (candidates.Length == 0)
        {
            MessageBox.Show("未发现可访问的托盘项目，已保存诊断记录。未启动新实例。可右键应用图标打开恢复诊断目录。", "DeskIsle");
            return null;
        }
        string? selected = null;
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "无法可靠确认托盘归属，请选择该应用的图标。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var dialog = new Window { Title = "选择恢复目标", Content = panel, Width = 380, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = false, Topmost = true };
        var list = new StackPanel();
        foreach (var candidate in candidates.OrderByDescending(c => c.Score))
        {
            var button = new Button { Content = candidate.Name, Padding = new Thickness(8), Margin = new Thickness(0, 2, 0, 2), HorizontalContentAlignment = HorizontalAlignment.Left };
            button.Click += (_, _) => { selected = candidate.Key; dialog.Close(); }; list.Children.Add(button);
        }
        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var cancel = new Button { Content = "取消", Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(8) };
        cancel.Click += (_, _) => dialog.Close(); panel.Children.Add(cancel);
        dialog.ShowDialog(); return selected;
    }
}
