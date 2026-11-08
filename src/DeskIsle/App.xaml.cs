using System.Threading;
using System.Windows;
namespace DeskIsle;
public partial class App : Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        instance = new Mutex(true, "Local\\DeskIsle-" + System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value, out var created);
        if (!created)
        {
            MessageBox.Show("DeskIsle 已在运行，请先退出已有窗口。", "DeskIsle");
            Shutdown(); return;
        }
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
