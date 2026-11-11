using System.Windows;
using DeskIsle.Services;
namespace DeskIsle;

public partial class MainWindow
{
    private readonly Dictionary<Window, DesktopHost> dialogHosts = [];
    internal bool HasOpenFileDialog => dialogHosts.Count > 0;
    private void TrackDialog(Window dialog)
    {
        var recovery = new DesktopHost();
        dialog.SourceInitialized += (_, _) =>
        {
            recovery.SetHandle(dialog);
            dialogHosts[dialog] = recovery;
        };
        dialog.Closed += (_, _) => dialogHosts.Remove(dialog);
    }
}
