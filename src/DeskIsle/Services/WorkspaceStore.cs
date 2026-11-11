using System.IO;
using System.Text.Json;
namespace DeskIsle.Services;

public sealed class WorkspaceState
{
    public int SchemaVersion { get; set; } = 3;
    public bool DesktopMigrationPrompted { get; set; }
    public Dictionary<string, string> ImportedPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<CardState> Cards { get; set; } = [];
}

/// <summary>One atomic workspace document. The legacy single-card document is never overwritten.</summary>
public sealed class WorkspaceStore
{
    private readonly string dataDirectory;
    private readonly string path;
    public WorkspaceStore(string? dataDirectory = null)
    {
        this.dataDirectory = dataDirectory ?? CardStore.DataDirectory;
        path = Path.Combine(this.dataDirectory, "cards.json");
    }
    public string? Warning { get; private set; }
    public bool ReadOnly { get; private set; }
    public WorkspaceState Load()
    {
        var found = false;
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            found = true;
            try
            {
                var state = JsonSerializer.Deserialize<WorkspaceState>(File.ReadAllText(candidate)) ?? throw new JsonException();
                if (state.SchemaVersion is not (2 or 3))
                {
                    ReadOnly = true; Warning = "配置版本不受支持，本次只读，不覆盖原配置。";
                    return new() { Cards = [new()] };
                }
                Validate(state);
                state.SchemaVersion = 3;
                if (candidate != path) Warning = "已从有效备份恢复多卡片配置。";
                return state;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            { Warning = "多卡片配置无法读取，保留原文件并尝试备份。"; }
        }
        if (found)
        {
            ReadOnly = true;
            Warning = "配置和备份都无法读取，本次不覆盖原文件。";
            return new() { Cards = [new()] };
        }
        var legacy = new CardStore(dataDirectory);
        var card = legacy.Load();
        ReadOnly = legacy.ReadOnly;
        Warning = legacy.Warning;
        var migrated = new WorkspaceState { Cards = [card] };
        try { Validate(migrated); }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            ReadOnly = true; Warning = "旧配置存在无效记录，本次不覆盖原文件。";
            return new() { Cards = [new()] };
        }
        return migrated;
    }
    private static void Validate(WorkspaceState state)
    {
        if (state.Cards == null) throw new JsonException();
        state.ImportedPaths = new Dictionary<string, string>((state.ImportedPaths ?? []).Where(pair => Path.IsPathFullyQualified(pair.Key) && Path.IsPathFullyQualified(pair.Value)), StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<Guid>();
        foreach (var card in state.Cards)
        {
            if (card == null || card.Id == Guid.Empty || !ids.Add(card.Id) || card.References == null || card.References.Any(x => string.IsNullOrWhiteSpace(x) || !Path.IsPathFullyQualified(x))) throw new JsonException();
            card.FolderOrders = new Dictionary<string, List<string>>((card.FolderOrders ?? new Dictionary<string, List<string>>()).Where(x => x.Value != null).ToDictionary(x => x.Key, x => x.Value.Where(p => !string.IsNullOrWhiteSpace(p)).ToList()), StringComparer.OrdinalIgnoreCase);
            card.Title = string.IsNullOrWhiteSpace(card.Title) ? "我的卡片" : card.Title.Trim()[..Math.Min(40, card.Title.Trim().Length)];
            if (!double.IsFinite(card.BackgroundOpacity)) card.BackgroundOpacity = .85;
            card.BackgroundOpacity = Math.Clamp(card.BackgroundOpacity, .3, 1);
        }
    }
    public string BackupForDesktopMigration(WorkspaceState state)
    {
        Directory.CreateDirectory(dataDirectory);
        var backup = Path.Combine(dataDirectory, "cards.before-desktop-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json");
        if (File.Exists(path)) File.Copy(path, backup, overwrite: false);
        else File.WriteAllText(backup, JsonSerializer.Serialize(state));
        return backup;
    }
    public void Save(WorkspaceState state)
    {
        if (ReadOnly) return;
        Validate(state);
        Directory.CreateDirectory(dataDirectory);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state));
        if (File.Exists(path))
        {
            try
            {
                var previous = JsonSerializer.Deserialize<WorkspaceState>(File.ReadAllText(path)) ?? throw new JsonException();
                Validate(previous);
                if (previous.SchemaVersion is 2 or 3) File.Copy(path, path + ".bak", true);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException) { }
            File.Replace(temp, path, null);
        }
        else File.Move(temp, path);
    }
}
