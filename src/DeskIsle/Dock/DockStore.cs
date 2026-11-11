using System.IO;
using System.Text.Json;
using DeskIsle.Services;
namespace DeskIsle.Dock;

internal sealed class DockSettings
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; }
    public int RevealMs { get; set; } = 120;
    public int HideMs { get; set; } = 650;
    public double Opacity { get; set; } = .94;
    public uint HotKey { get; set; } = 0x44; // Ctrl+Alt+D
    public double? ReturnLeft { get; set; }
    public double? ReturnTop { get; set; }
    public List<string> Pins { get; set; } = [];
}
internal sealed class DockStore
{
    private readonly string path = Path.Combine(CardStore.DataDirectory, "dock.json");
    internal bool ReadOnly { get; private set; }
    internal string? Warning { get; private set; }
    internal DockSettings Load()
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var settings = JsonSerializer.Deserialize<DockSettings>(File.ReadAllText(candidate)) ?? throw new JsonException();
                if (settings.Version != 1) { ReadOnly = true; Warning = "Dock 配置版本不支持，保留原文件，禁止接管。"; return new(); }
                settings.Pins = (settings.Pins ?? []).Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList();
                settings.RevealMs = Math.Clamp(settings.RevealMs, 0, 1000);
                settings.HideMs = Math.Clamp(settings.HideMs, 200, 3000);
                settings.Opacity = double.IsFinite(settings.Opacity) ? Math.Clamp(settings.Opacity, .5, 1) : .94;
                if (candidate != path) Warning = "Dock 已从备份恢复。";
                return settings;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { Warning = "Dock 配置读取失败，尝试备份。"; }
        }
        if (File.Exists(path) || File.Exists(path + ".bak")) { ReadOnly = true; Warning = "Dock 配置损坏，保留原文件，禁止接管。"; }
        return new();
    }
    internal void Save(DockSettings settings)
    {
        if (ReadOnly) throw new IOException("Dock 配置处于保护模式。");
        Directory.CreateDirectory(CardStore.DataDirectory);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings));
        if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
        else File.Move(temp, path);
    }
}
