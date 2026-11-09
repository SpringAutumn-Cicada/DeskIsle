using System.IO;
using System.Windows;
using System.Windows.Input;
namespace DeskIsle;

public partial class DeleteConfirmation : Window
{
    public DeleteConfirmation(Window owner, string path)
    {
        InitializeComponent();
        Owner = owner;
        ItemName.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        ItemPath.Text = path;
        // Keep confirmation actions available on small work areas / high DPI.
        MaxHeight = SystemParameters.WorkArea.Height;
        Width = Math.Min(420, SystemParameters.WorkArea.Width);
        ContentRendered += (_, _) => CancelAction.Focus();
    }
    private void ConfirmDelete(object sender, RoutedEventArgs e) => DialogResult = true;
    private void DragHeading(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.TextBlock || ReferenceEquals(e.OriginalSource, sender)) DragMove();
    }
}
