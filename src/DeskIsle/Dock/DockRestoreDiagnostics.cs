using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace DeskIsle.Dock;

internal static class DockRestoreDiagnostics
{
    internal static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskIsle", "restore-diagnostics");
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static void Write(DockPin app, string stage, uint[] pids, DockTrayActivation.Candidate[] candidates)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            // No paths, window titles, tooltip text, chat content or credentials are written.
            var record = new { Version = "1.11.7", Time = DateTimeOffset.Now, OS = Environment.OSVersion.VersionString,
                App = Path.GetFileName(app.Executable ?? app.Path), Stage = stage, ProcessIds = pids,
                Candidates = candidates.Select(c => new { c.Key, c.ClassName, c.ProcessId, c.HasInvoke, c.HasPoint, c.Score }) };
            File.WriteAllText(Path.Combine(Folder, "last-restore.json"), JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
