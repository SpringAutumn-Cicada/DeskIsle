using System.IO;
using System.Windows;
using DeskIsle.Services;
namespace DeskIsle;

public partial class MoveConfirmation : Window
{
    private readonly string source;
    private readonly string folder;
    private bool validating;
    public string ChosenName { get; private set; } = "";
    public MoveConfirmation(Window owner, string source, string folder)
    {
        InitializeComponent();
        Owner = owner;
        this.source = source; this.folder = folder;
        SourcePath.Text = source; TargetPath.Text = folder;
        DestinationName.Text = Path.GetFileName(FileMoveService.Normalize(source));
        MaxWidth = SystemParameters.WorkArea.Width; MaxHeight = SystemParameters.WorkArea.Height;
        ContentRendered += (_, _) => CancelAction.Focus();
        Closing += (_, e) => e.Cancel = validating;
    }
    private async void ConfirmMove(object sender, RoutedEventArgs e)
    {
        if (validating) return;
        var name = DestinationName.Text;
        validating = true;
        MoveAction.IsEnabled = CancelAction.IsEnabled = CloseAction.IsEnabled = DestinationName.IsEnabled = false;
        ErrorText.Text = "正在检查目标…";
        var valid = false;
        try { await Task.Run(() => FileMoveService.Validate(source, folder, name)); valid = true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException or NotSupportedException)
        { ErrorText.Text = ex is UnauthorizedAccessException ? "没有权限访问源文件或目标文件夹。" : ex.Message; }
        finally
        {
            validating = false;
            MoveAction.IsEnabled = CancelAction.IsEnabled = CloseAction.IsEnabled = DestinationName.IsEnabled = true;
        }
        if (valid) { ChosenName = name; DialogResult = true; }
        else { DestinationName.Focus(); DestinationName.SelectAll(); }
    }
}
