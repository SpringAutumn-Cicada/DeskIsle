using System.IO;
using System.Text.Json;
namespace DeskIsle.Services;

public sealed class CardState
{
    public int SchemaVersion { get; set; } = 1;
    public Guid Id { get; set; } = Guid.NewGuid();
    public double BackgroundOpacity { get; set; } = .85;
    public string Title { get; set; } = "我的卡片";
    public List<string> References { get; set; } = [];
    public Dictionary<string, List<string>> FolderOrders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public double Left { get; set; } = 24;
    public double Top { get; set; } = 48;
    public double Width { get; set; } = 360;
    public double Height { get; set; } = 430;
}

public sealed class CardStore
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskIsle");
    private readonly string dataDirectory;
    private readonly string path;
    public CardStore(string? dataDirectory = null)
    {
        this.dataDirectory = dataDirectory ?? DataDirectory;
        path = Path.Combine(this.dataDirectory, "card.json");
    }
    public string? Warning { get; private set; }
    public bool ReadOnly { get; private set; }
    public CardState Load()
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var state = JsonSerializer.Deserialize<CardState>(File.ReadAllText(candidate)) ?? throw new JsonException();
                if (state.SchemaVersion != 1) { ReadOnly = true; Warning = "配置版本较新，本次不覆盖配置。"; return new(); }
                if (state.References == null || state.References.Any(string.IsNullOrWhiteSpace)) throw new JsonException();
                if (candidate != path) Warning = "已从备份恢复卡片。";
                return state;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { Warning = "配置无法读取，保留原文件并尝试备份。"; }
        }
        if (File.Exists(path) || File.Exists(path + ".bak")) ReadOnly = true;
        return new();
    }
    public void Save(CardState state)
    {
        if (ReadOnly) return;
        Directory.CreateDirectory(dataDirectory);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state));
        if (File.Exists(path))
        {
            // Preserve a valid backup when recovering from a damaged primary.
            try
            {
                var previous = JsonSerializer.Deserialize<CardState>(File.ReadAllText(path));
                if (previous is { SchemaVersion: 1, References: not null }) File.Copy(path, path + ".bak", true);
            }
            catch (JsonException) { }
        }
        File.Move(temp, path, true);
    }
}
