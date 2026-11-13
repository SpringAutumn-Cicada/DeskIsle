using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
namespace DeskIsle.Services;

internal static class LoginStartup
{
    private static string TaskName => "DeskIsle-Login-" + WindowsIdentity.GetCurrent().User!.Value;
    private static string BackupDirectory => Path.Combine(CardStore.DataDirectory, "startup-backup");
    private static string BackupLinks => Path.Combine(BackupDirectory, "links");
    internal static bool Enabled()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        try
        {
            service.Connect();
            dynamic folder = service.GetFolder("\\");
            try { dynamic task = folder.GetTask(TaskName); try { return (bool)task.Enabled; } finally { Marshal.FinalReleaseComObject(task); } }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002)) { return false; }
            finally { Marshal.FinalReleaseComObject(folder); }
        }
        finally { Marshal.FinalReleaseComObject(service); }
    }
    internal static void Set(bool enabled)
    {
        Directory.CreateDirectory(BackupDirectory);
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        try
        {
            service.Connect();
            dynamic folder = service.GetFolder("\\");
            try
            {
                string? previous = null;
                try { dynamic old = folder.GetTask(TaskName); try { previous = (string)old.Xml; } finally { Marshal.FinalReleaseComObject(old); } }
                catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002)) { }
                if (previous != null) File.WriteAllText(Path.Combine(BackupDirectory, $"task-{DateTime.Now:yyyyMMddHHmmssfff}.xml"), previous);
                if (!enabled)
                {
                    if (previous != null) folder.DeleteTask(TaskName, 0);
                    RestoreLinks(); return;
                }
                dynamic definition = service.NewTask(0);
                try
                {
                    var sid = WindowsIdentity.GetCurrent().User!.Value;
                    definition.RegistrationInfo.Description = "DeskIsle 当前用户登录后立即启动";
                    definition.Principal.UserId = sid;
                    definition.Principal.LogonType = 3; // Interactive token, no saved password.
                    definition.Principal.RunLevel = 0;
                    definition.Settings.DisallowStartIfOnBatteries = false;
                    definition.Settings.StopIfGoingOnBatteries = false;
                    definition.Settings.ExecutionTimeLimit = "PT0S";
                    definition.Settings.StartWhenAvailable = true;
                    definition.Settings.MultipleInstances = 2;
                    dynamic trigger = definition.Triggers.Create(9); trigger.UserId = sid;
                    Marshal.FinalReleaseComObject(trigger);
                    dynamic action = definition.Actions.Create(0);
                    action.Path = Environment.ProcessPath!;
                    action.Arguments = "--login";
                    action.WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath!)!;
                    Marshal.FinalReleaseComObject(action);
                    dynamic registered = folder.RegisterTaskDefinition(TaskName, definition, 6, sid, null, 3);
                    Marshal.FinalReleaseComObject(registered);
                    try { BackupLegacyLinks(); }
                    catch
                    {
                        if (previous == null) folder.DeleteTask(TaskName, 0);
                        else { dynamic restored = folder.RegisterTask(TaskName, previous, 6, sid, null, 3); Marshal.FinalReleaseComObject(restored); }
                        RestoreLinks(); throw;
                    }
                    StartupDiagnostics.Mark("login-task-registered");
                }
                finally { Marshal.FinalReleaseComObject(definition); }
            }
            finally { Marshal.FinalReleaseComObject(folder); }
        }
        finally { Marshal.FinalReleaseComObject(service); }
    }
    private static void BackupLegacyLinks()
    {
        var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        try
        {
            foreach (var path in Directory.EnumerateFiles(startup, "*.lnk"))
            {
                dynamic link = shell.CreateShortcut(path);
                string target;
                try { target = (string)link.TargetPath; } finally { Marshal.FinalReleaseComObject(link); }
                if (!Path.GetFileName(target).Equals("DeskIsle.exe", StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(BackupLinks);
                var destination = Path.Combine(BackupLinks, Path.GetFileName(path));
                if (File.Exists(destination)) throw new IOException("已有同名自启快捷方式备份，请先处理备份后重试。");
                File.Move(path, destination);
            }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }
    private static void RestoreLinks()
    {
        if (!Directory.Exists(BackupLinks)) return;
        foreach (var path in Directory.EnumerateFiles(BackupLinks, "*.lnk"))
        {
            var destination = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Path.GetFileName(path));
            if (File.Exists(destination)) throw new IOException("启动目录存在同名快捷方式，已保留备份，请手动恢复。");
            File.Move(path, destination);
        }
    }
}
