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
        if (e.Args.Contains("--taskbar-watch")) { DeskIsle.Dock.TaskbarLease.Watch(); Shutdown(); return; }
        if (e.Args.Contains("--restore-taskbar"))
        {
            try { DeskIsle.Dock.TaskbarLease.Restore(true); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "恢复任务栏"); }
            Shutdown(); return;
        }
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
