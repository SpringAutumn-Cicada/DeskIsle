using DeskIsle.Core;
using Xunit;
namespace DeskIsle.Core.Tests;
public sealed class FolderReaderTests
{
    [Fact]
    public void ReadsOnlyDirectChildrenAndDoesNotChangeFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeskIsle-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "z文件夹"));
            File.WriteAllText(Path.Combine(root, "z文件夹", "nested.txt"), "nested");
            File.WriteAllText(Path.Combine(root, "a 空格 &.txt"), "preserve");
            var entries = FolderReader.Read(root, CancellationToken.None);
            Assert.Equal(2, entries.Count);
            Assert.True(entries[0].IsDirectory);
            Assert.Equal("a 空格 &.txt", entries[1].Name);
            Assert.Equal("preserve", File.ReadAllText(entries[1].FullPath));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void MissingDirectoryReportsFailure() => Assert.Throws<DirectoryNotFoundException>(() => FolderReader.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), CancellationToken.None));
    [Fact]
    public void CancellationIsObserved()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeskIsle-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "a"), "");
            Assert.Throws<OperationCanceledException>(() => FolderReader.Read(root, new CancellationToken(true)));
        }
        finally { Directory.Delete(root, true); }
    }
}
