using System.Diagnostics;
using System.IO;
namespace DeskIsle.Services;

// Small, serial, best-effort log. Never record file names, window titles or user contents.
internal static class StartupDiagnostics
{
    private static readonly Stopwatch elapsed = Stopwatch.StartNew();
    private static readonly object gate = new();
    private static Task writer = Task.CompletedTask;
    internal static void Mark(string stage)
    {
        var entry = $"{DateTimeOffset.Now:O} pid={Environment.ProcessId} +{elapsed.ElapsedMilliseconds}ms {stage}";
        lock (gate) writer = writer.ContinueWith(_ =>
        {
            try
            {
                Directory.CreateDirectory(CardStore.DataDirectory);
                var path = Path.Combine(CardStore.DataDirectory, "startup-diagnostics.log");
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Move(path, path + ".previous", true);
                File.AppendAllText(path, entry + Environment.NewLine);
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }, TaskScheduler.Default);
    }
}
