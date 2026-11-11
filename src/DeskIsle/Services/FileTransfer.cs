using System.IO;
using System.Text.Json;
namespace DeskIsle.Services;

public enum CollisionAction { Replace, Skip, Cancel }
public sealed record EntryStamp(bool Directory, long Length, DateTime Modified)
{
    public static EntryStamp? Read(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("目标是链接或云占位文件，不能覆盖。");
            var directory = (attributes & FileAttributes.Directory) != 0;
            return new(directory, directory ? 0 : new FileInfo(path).Length, File.GetLastWriteTimeUtc(path));
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
public sealed record Collision(string Source, string Target, EntryStamp Incoming, EntryStamp Existing);
public sealed record TransferProgress(string Path, long Bytes, long Total);
public sealed record TransferCompletion(string Source, string Target, bool Moved, bool Directory);
public sealed record TransferRoot(string Source, string Target, bool Complete, bool Published);
public sealed class TransferReport
{
    public int Completed { get; set; }
    public int Skipped { get; set; }
    public bool Cancelled { get; set; }
    public List<string> Errors { get; } = [];
    public List<TransferRoot> Roots { get; } = [];
    public string Summary => $"完成 {Completed} 项 · 跳过 {Skipped} 项 · 失败 {Errors.Count} 项" + (Cancelled ? " · 已取消（保留已完成项目）" : "");
}

/// <summary>Serialized transfer session. Call on a worker thread; UI decisions are delegated.</summary>
public sealed class FileTransfer(
    Func<Collision, Task<CollisionAction>> conflict,
    Func<TransferCompletion, Task> committed,
    Action<TransferProgress> progress,
    CancellationToken cancellation,
    IReadOnlyDictionary<string, string>? imported = null)
{
    private readonly TransferReport report = new();
    private readonly Dictionary<string, string> completedPaths = imported == null ? new(StringComparer.OrdinalIgnoreCase) : new(imported, StringComparer.OrdinalIgnoreCase);
    private string? work;
    private string Work => work ??= DesktopFiles.WorkDirectory();

    public async Task<TransferReport> Run(IEnumerable<string> sources, string targetFolder, bool move, string? name = null, bool draft = false)
    {
        DesktopFiles.Require(targetFolder);
        _ = Work; // Allocate owned recovery storage before any filesystem mutation.
        foreach (var source in sources.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (cancellation.IsCancellationRequested) { report.Cancelled = true; break; }
            var normalized = source;
            string? pendingTarget = null;
            var before = report.Completed;
            try
            {
                normalized = FileMoveService.Normalize(source);
                var itemName = name ?? Path.GetFileName(normalized);
                NewItemService.ValidateName(itemName, NewItemKind.Folder);
                if (!move && completedPaths.TryGetValue(normalized, out var mapped) && DesktopFiles.IsCardPath(mapped) && EntryStamp.Read(mapped) != null)
                { DesktopFiles.Require(mapped); report.Roots.Add(new(normalized, mapped, true, true)); continue; }
                var target = Path.Combine(targetFolder, itemName);
                pendingTarget = target;
                var result = await Item(normalized, target, move, draft);
                report.Roots.Add(new(normalized, target, result.Complete, result.Published));
            }
            catch (OperationCanceledException)
            {
                if (pendingTarget != null && report.Completed > before && (File.Exists(pendingTarget) || Directory.Exists(pendingTarget))) report.Roots.Add(new(normalized, pendingTarget, false, true));
                report.Cancelled = true; break;
            }
            catch (Exception ex) when (Recoverable(ex))
            {
                if (pendingTarget != null && report.Completed > before && (File.Exists(pendingTarget) || Directory.Exists(pendingTarget))) report.Roots.Add(new(normalized, pendingTarget, false, true));
                report.Errors.Add(source + "：" + ex.Message);
            }
        }
        return report;
    }
    public static bool Recoverable(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;

    private async Task<(bool Complete, bool Published)> Item(string source, string target, bool move, bool draft)
    {
        cancellation.ThrowIfCancellationRequested();
        DesktopFiles.Require(target, missingAllowed: true);
        if (!draft) { if (move) DesktopFiles.Require(source); else DesktopFiles.RequireSource(source); }
        else if (!DesktopFiles.Private(source)) throw new IOException("无效的新建暂存路径。");
        if (FileMoveService.Same(source, target)) return (true, true);
        if (FileMoveService.Under(target, source)) throw new IOException("不能把目录导入到自身或子目录。");
        if (!move && completedPaths.TryGetValue(source, out var previous) && FileMoveService.Same(previous, target) && EntryStamp.Read(target) != null)
            return (true, true);
        var incoming = EntryStamp.Read(source) ?? throw new FileNotFoundException("来源已不存在。", source);
        if (incoming.Directory)
        {
            var existing = EntryStamp.Read(target);
            if (existing != null)
            {
                var answer = await conflict(new(source, target, incoming, existing));
                if (answer == CollisionAction.Cancel) throw new OperationCanceledException();
                if (answer == CollisionAction.Skip || !existing.Directory) { report.Skipped++; return (false, false); }
                DesktopFiles.Require(target);
                if (EntryStamp.Read(target) is not { Directory: true }) throw new IOException("目标目录已变化，请重试。");
            }
            else { Directory.CreateDirectory(target); DesktopFiles.Require(target); }
            var complete = true;
            // Enumerate source only: target-only children are preserved during a merge.
            foreach (var child in Directory.EnumerateFileSystemEntries(source))
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    var result = await Item(child, Path.Combine(target, Path.GetFileName(child)), move, draft);
                    complete &= result.Complete;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (Recoverable(ex)) { report.Errors.Add(child + "：" + ex.Message); complete = false; }
            }
            if (complete)
            {
                cancellation.ThrowIfCancellationRequested();
                if (move) Directory.Delete(source, recursive: false); // Never remove a non-empty/partially merged source.
                report.Completed++;
                await Completed(new(source, target, move, true));
            }
            return (complete, true);
        }
        var copied = await CopyFile(source, target, incoming, move);
        return (copied, copied);
    }
    private async Task Completed(TransferCompletion completion)
    {
        // Persist the operation result before changing card references. Recovery records remain local.
        var record = Path.Combine(Work, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(record, JsonSerializer.Serialize(completion));
        completedPaths[completion.Source] = completion.Target;
        await committed(completion);
    }
    private async Task<bool> CopyFile(string source, string target, EntryStamp incoming, bool move)
    {
        // Lock the source against writes/deletion while preparing its complete desktop copy.
        if (move && EntryStamp.Read(target) == null)
        {
            cancellation.ThrowIfCancellationRequested();
            DesktopFiles.Require(source); DesktopFiles.Require(target, missingAllowed: true);
            try
            {
                File.Move(source, target, overwrite: false);
            }
            catch (IOException) when (EntryStamp.Read(target) != null && File.Exists(source)) { return await CopyFile(source, target, incoming, move); }
            report.Completed++;
            await Completed(new(source, target, true, false));
            return true;
        }
        var temp = Path.Combine(Work, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
            {
                var total = input.Length; var done = 0L; var buffer = new byte[131072];
                var lastProgress = Environment.TickCount64;
                progress(new(source, 0, total));
                while (true)
                {
                    var count = await input.ReadAsync(buffer, cancellation);
                    if (count == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
                    done += count;
                    if (Environment.TickCount64 - lastProgress >= 100) { progress(new(source, done, total)); lastProgress = Environment.TickCount64; }
                }
                if (done != total) throw new IOException("来源长度在复制期间变化，未提交目标。");
                await output.FlushAsync(cancellation);
                output.Flush(flushToDisk: true);
            }
            cancellation.ThrowIfCancellationRequested();
            File.SetLastWriteTimeUtc(temp, incoming.Modified);
            while (true)
            {
                DesktopFiles.Require(target, missingAllowed: true);
                var existing = EntryStamp.Read(target);
                if (existing == null)
                {
                    try { File.Move(temp, target, overwrite: false); break; }
                    catch (IOException) when (EntryStamp.Read(target) != null) { continue; }
                }
                var decision = await conflict(new(source, target, incoming, existing));
                if (decision == CollisionAction.Cancel) throw new OperationCanceledException();
                if (decision == CollisionAction.Skip || existing.Directory) { report.Skipped++; return false; }
                cancellation.ThrowIfCancellationRequested();
                DesktopFiles.Require(target);
                // If the target changed while the question was open, ask again.
                if (EntryStamp.Read(target) != existing) continue;
                var backup = Path.Combine(Work, Guid.NewGuid().ToString("N") + ".backup");
                File.WriteAllText(backup + ".json", JsonSerializer.Serialize(new { Source = source, Target = target, Backup = backup }));
                // Stage, target and backup share the desktop volume. No truncate/delete fallback.
                File.Replace(temp, target, backup, ignoreMetadataErrors: false);
                break;
            }
            if (move)
            {
                // Detect edits after the source copy was closed. Never delete a changed original.
                if (EntryStamp.Read(source) != incoming) throw new IOException("副本已到达目标，但源文件发生变化，保留源文件；请检查两处。");
                DesktopFiles.Require(source);
                File.Delete(source);
            }
            report.Completed++;
            await Completed(new(source, target, move, false));
            return true;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); } // Only this session's own uncommitted temporary file.
    }
}
