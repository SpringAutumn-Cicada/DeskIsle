using System.Threading;
using DeskIsle.Services;
using System.Windows;
namespace DeskIsle;
public partial class App : Application
{
    private Mutex? instance;
    private WorkspaceManager? workspace;
    protected override void OnStartup(StartupEventArgs e)
    {
        instance = new Mutex(true, "Local\\DeskIsle-" + System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value, out var created);
        if (!created)
        {
            MessageBox.Show("DeskIsle 已在运行，请先退出已有窗口。", "DeskIsle");
            Shutdown(); return;
        }
        base.OnStartup(e);
        workspace = new WorkspaceManager();
        workspace.Start();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        workspace?.Dispose();
        instance?.Dispose();
        base.OnExit(e);
    }
}
