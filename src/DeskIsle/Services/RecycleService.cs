using System.IO;
using Microsoft.VisualBasic.FileIO;
namespace DeskIsle.Services;

internal static class RecycleService
{
    // Shell extensions and recycle dialogs may wait on Explorer. Keep them off WPF's dispatcher.
    internal static Task Delete(string path)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                DesktopFiles.Require(path);
                if (Directory.Exists(path)) FileSystem.DeleteDirectory(path, UIOption.AllDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
                else FileSystem.DeleteFile(path, UIOption.AllDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
                result.SetResult();
            }
            catch (Exception ex) { result.SetException(ex); }
        }) { IsBackground = true, Name = "DeskIsle recycle" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }
}
