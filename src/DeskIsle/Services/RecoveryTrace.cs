using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
namespace DeskIsle.Services;

/// <summary>Opt-in, bounded transition timestamps. A WPF frame is not proof of physical presentation.</summary>
internal static class RecoveryTrace
{
    private static readonly bool enabled = Environment.GetEnvironmentVariable("DESKISLE_RECOVERY_TRACE") == "1";
    private static readonly Queue<string> entries = new();
    private static bool pending;
    public static void Mark(string stage)
    {
        if (!enabled) return;
        if (entries.Count >= 256) entries.Dequeue();
        entries.Enqueue($"{Stopwatch.GetTimestamp()},{stage}");
    }
    public static void FlushLater(Dispatcher dispatcher)
    {
        if (!enabled || pending || dispatcher.HasShutdownStarted) return;
        pending = true;
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            pending = false;
            var snapshot = entries.ToArray();
            _ = Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(CardStore.DataDirectory);
                    File.WriteAllLines(Path.Combine(CardStore.DataDirectory, "recovery-timing.csv"), new[] { $"frequency={Stopwatch.Frequency}", "timestamp,stage" }.Concat(snapshot));
                }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            });
        }));
    }
}
