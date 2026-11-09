using System.Windows;
using DeskIsle.Services;
namespace DeskIsle;

public partial class MainWindow
{
    private readonly Dictionary<Window, DesktopHost> dialogHosts = [];
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
