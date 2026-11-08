using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using DeskIsle.Core;
namespace DeskIsle;
public sealed class FileTile(FolderEntry entry) : INotifyPropertyChanged
{
    public FolderEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string Availability => System.IO.File.Exists(FullPath) || System.IO.Directory.Exists(FullPath) ? "" : "路径失效 · 可右键重新定位";
    public string FullPath => Entry.FullPath;
    public string Placeholder => Entry.IsDirectory ? "▰" : "▤";
    private ImageSource? icon;
    public ImageSource? Icon { get => icon; set { icon = value; Changed(); } }
    public bool IconRequested { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}
public sealed record TileRow(IReadOnlyList<FileTile> Items);
