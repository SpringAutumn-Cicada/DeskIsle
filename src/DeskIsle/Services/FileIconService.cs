using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
namespace DeskIsle.Services;

/// <summary>Original vector artwork. No bitmap upscaling, Shell handles or file content access.</summary>
public sealed class FileIconService : IDisposable
{
    private readonly Dictionary<string, ImageSource> cache = new(StringComparer.OrdinalIgnoreCase);
    public Task<ImageSource?> GetAsync(bool directory, string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var kind = directory ? "folder" : extension switch
        {
            ".txt" or ".log" => "TXT", ".md" => "MD", ".pdf" => "PDF",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg" => "IMG",
            ".zip" or ".7z" or ".rar" => "ZIP", ".doc" or ".docx" => "DOC",
            ".xls" or ".xlsx" or ".csv" => "XLS", ".cs" or ".js" or ".py" or ".json" or ".html" => "CODE",
            ".exe" or ".lnk" => "APP", _ => "FILE"
        };
        if (!cache.TryGetValue(kind, out var icon)) { icon = Draw(kind); cache[kind] = icon; }
        return Task.FromResult<ImageSource?>(icon);
    }
    private static DrawingImage Draw(string kind)
    {
        var color = (Color)ColorConverter.ConvertFromString(kind switch
        {
            "folder" => "#EBC16E", "MD" => "#AF9CEB", "IMG" => "#72C9C1", "ZIP" => "#DDA57F",
            "PDF" => "#E58D9C", "XLS" => "#82C6A2", "CODE" => "#91ACED", _ => "#90B8D9"
        });
        var fill = new LinearGradientBrush(Color.FromArgb(255, (byte)Math.Min(255, color.R + 25), (byte)Math.Min(255, color.G + 25), (byte)Math.Min(255, color.B + 25)), color, 90);
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            // Fixed vector viewport keeps folder and document optical sizes consistent.
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 64, 64));
            var outline = new Pen(new SolidColorBrush(Color.FromArgb(65, 255, 255, 255)), 1);
            if (kind == "folder")
            {
                dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(173, 130, 65)), outline, Geometry.Parse("M5,17 Q5,12 10,12 L25,12 L31,18 L54,18 Q59,18 59,23 L59,48 Q59,53 54,53 L10,53 Q5,53 5,48 Z"));
                dc.DrawRoundedRectangle(fill, outline, new Rect(5, 23, 54, 31), 5, 5);
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 2), new Point(12, 29), new Point(36, 29));
            }
            else
            {
                dc.DrawGeometry(fill, outline, Geometry.Parse("M17,6 L39,6 L51,18 L51,53 Q51,58 46,58 L17,58 Q12,58 12,53 L12,11 Q12,6 17,6 Z"));
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), null, Geometry.Parse("M39,6 L39,18 L51,18 Z"));
                var ink = new SolidColorBrush(Color.FromArgb(180, 35, 48, 70));
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), null, new Rect(18, 25, 27, 22), 4, 4);
                var text = new FormattedText(kind, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), kind.Length > 3 ? 8 : 11, ink, 1);
                dc.DrawText(text, new Point(32 - text.Width / 2, 29));
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), 2), new Point(20, 51), new Point(38, 51));
            }
        }
        var image = new DrawingImage(drawing); image.Freeze(); return image;
    }
    public void Dispose() => cache.Clear();
}
