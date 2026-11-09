using DeskIsle.Core;
using Xunit;
namespace DeskIsle.Core.Tests;
public sealed class FolderSafetyTests
{
    [Fact]
    public void HiddenAndSystemEntriesAreExcludedAndThousandDirectEntriesCanLoad()
    {
        var root=Path.Combine(Path.GetTempPath(),"DeskIsle-files-"+Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            for(var i=0;i<1000;i++) File.WriteAllText(Path.Combine(root,$"项目 {i:D4}.txt"),"safe");
            var hidden=Path.Combine(root,"hidden.txt");File.WriteAllText(hidden,"");File.SetAttributes(hidden,FileAttributes.Hidden);
            var system=Path.Combine(root,"system.txt");File.WriteAllText(system,"");File.SetAttributes(system,FileAttributes.System);
            var files=FolderReader.Read(root,CancellationToken.None);Assert.Equal(1000,files.Count); Assert.Equal("项目 0000.txt",files[0].Name);
            Assert.Equal(FileAttributes.Hidden, File.GetAttributes(hidden)&FileAttributes.Hidden);
        }
        finally {foreach(var file in Directory.EnumerateFiles(root))File.SetAttributes(file,FileAttributes.Normal);Directory.Delete(root,true);}
    }
    [Fact]
    public void EmptyDirectoryHonorsCancellation()
    {
        var root=Path.Combine(Path.GetTempPath(),"DeskIsle-empty-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try {Assert.Throws<OperationCanceledException>(()=>FolderReader.Read(root,new CancellationToken(true)));}
        finally {Directory.Delete(root);}
    }
}
