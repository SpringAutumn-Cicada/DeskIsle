using System.Text.Json;
using DeskIsle.Services;
using Xunit;
namespace DeskIsle.Core.Tests;

public sealed class WorkspaceStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "DeskIsle-store-tests-" + Guid.NewGuid());
    private string Primary => Path.Combine(root, "cards.json");
    public WorkspaceStoreTests() => Directory.CreateDirectory(root);
    private WorkspaceStore Store() => new(root);
    private static WorkspaceState State(string title = "测试卡片") => new() { Cards = [new() { Title = title }] };
    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
    [Fact] public void FirstRunCreatesOneCardWithoutWritingFiles()
    {
        var state = Store().Load();
        Assert.Single(state.Cards); Assert.Empty(state.Cards[0].References); Assert.Empty(Directory.GetFiles(root));
    }
    [Fact] public void MultipleCardsRoundTripTheirIdentityOrderAndLayout()
    {
        var file = Path.Combine(root, "中文 空格 &.txt"); File.WriteAllText(file, "preserve");
        var state = State(); state.Cards.Add(new() { Title = "第二张", Left = 456, Top = 123, Width = 400, Height = 350, BackgroundOpacity = .63, References = [file] });
        state.Cards[0].FolderOrders[root] = [file]; Store().Save(state);
        var loaded = Store().Load();
        Assert.Equal(state.Cards.Select(x => x.Id), loaded.Cards.Select(x => x.Id));
        var second = loaded.Cards[1]; Assert.Equal("第二张", second.Title); Assert.Equal(456, second.Left); Assert.Equal(123, second.Top); Assert.Equal(400, second.Width); Assert.Equal(350, second.Height); Assert.Equal(.63, second.BackgroundOpacity); Assert.Equal([file], second.References); Assert.Equal([file], loaded.Cards[0].FolderOrders[root]); Assert.Equal("preserve", File.ReadAllText(file));
    }
    [Fact] public void SavesKeepPreviousValidVersionAsBackup()
    {
        Store().Save(State("前一版")); Store().Save(State("当前版"));
        var backup = JsonSerializer.Deserialize<WorkspaceState>(File.ReadAllText(Primary + ".bak"))!;
        Assert.Equal("前一版", backup.Cards[0].Title); Assert.Equal("当前版", Store().Load().Cards[0].Title);
    }
    [Fact] public void CorruptPrimaryRecoversBackupWithoutOverwritingOnlyValidBackup()
    {
        Store().Save(State("备份")); Store().Save(State("当前"));
        var backup = File.ReadAllBytes(Primary + ".bak"); File.WriteAllText(Primary, "{broken");
        var store = Store(); var state = store.Load(); Assert.False(store.ReadOnly); Assert.NotNull(store.Warning); Assert.Equal("备份", state.Cards[0].Title);
        state.Cards[0].Title = "已恢复"; store.Save(state);
        Assert.Equal(backup, File.ReadAllBytes(Primary + ".bak")); Assert.Equal("已恢复", Store().Load().Cards[0].Title);
    }
    [Fact] public void TwoDamagedFilesAreProtectedFromOverwrite()
    {
        File.WriteAllText(Primary, "broken"); File.WriteAllText(Primary + ".bak", "broken backup");
        var store = Store(); var state = store.Load(); Assert.True(store.ReadOnly); store.Save(state);
        Assert.Equal("broken", File.ReadAllText(Primary)); Assert.Equal("broken backup", File.ReadAllText(Primary + ".bak"));
    }
    [Fact] public void FutureSchemaIsProtectedEvenWhenAnOlderBackupExists()
    {
        Store().Save(State()); var future = "{\"SchemaVersion\":999,\"Cards\":[]}"; File.Copy(Primary, Primary + ".bak"); File.WriteAllText(Primary, future);
        var store = Store(); var state = store.Load(); Assert.True(store.ReadOnly); store.Save(state); Assert.Equal(future, File.ReadAllText(Primary));
    }
    [Fact] public void LegacyMigrationKeepsOriginalFileByteForByte()
    {
        var legacy = new CardStore(root); var card = new CardState { Title = "旧卡片", References = [Path.Combine(root, "missing.txt")] }; legacy.Save(card);
        var bytes = File.ReadAllBytes(Path.Combine(root, "card.json")); var store = Store(); var state = store.Load(); Assert.Equal("旧卡片", state.Cards[0].Title); store.Save(state);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(root, "card.json"))); Assert.Equal(card.References, Store().Load().Cards[0].References);
    }
    [Theory] [InlineData("duplicate-id")] [InlineData("relative-path")]
    public void InvalidPrimaryStructureFallsBackToValidBackup(string kind)
    {
        Store().Save(State("有效备份")); File.Copy(Primary, Primary + ".bak");
        var bad = State(); if (kind == "duplicate-id") bad.Cards.Add(bad.Cards[0]); else bad.Cards[0].References.Add("relative.txt");
        File.WriteAllText(Primary, JsonSerializer.Serialize(bad));
        var store = Store(); Assert.Equal("有效备份", store.Load().Cards[0].Title); Assert.NotNull(store.Warning);
    }
    [Fact] public void ReadOnlyPrimaryReportsFailureAndPreservesFile()
    {
        Store().Save(State("原值")); var bytes = File.ReadAllBytes(Primary); File.SetAttributes(Primary, FileAttributes.ReadOnly);
        var error = Record.Exception(() => Store().Save(State("新值")));
        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "Save unexpectedly succeeded");
        Assert.Equal(bytes, File.ReadAllBytes(Primary));
    }
    [Fact] public void RemovedReferencesNeverDeleteOrChangeRealFiles()
    {
        var file = Path.Combine(root, "项目.txt"); File.WriteAllText(file, "important"); var hash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)); var attributes = File.GetAttributes(file);
        var state = State(); state.Cards[0].References.Add(file); Store().Save(state); state.Cards[0].References.Clear(); Store().Save(state);
        Assert.Equal(hash, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))); Assert.Equal(attributes, File.GetAttributes(file));
    }
}
