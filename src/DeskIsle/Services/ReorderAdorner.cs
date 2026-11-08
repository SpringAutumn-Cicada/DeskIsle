using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
namespace DeskIsle.Services;

public sealed class ReorderAdorner(UIElement element) : Adorner(element)
{
    public Rect Target { get; set; }
    public bool After { get; set; }
    public Point Pointer { get; set; }
    public FileTile? Tile { get; set; }
    protected override void OnRender(DrawingContext dc)
    {
        if (Tile == null) return;
        dc.PushClip(new RectangleGeometry(new Rect(AdornedElement.RenderSize)));
        var accent = new SolidColorBrush(Color.FromRgb(131, 213, 205));
        if (!Target.IsEmpty)
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(24, 131, 213, 205)), null, Target, 8, 8);
            var x = After ? Target.Right + 1 : Target.Left - 1;
            dc.DrawLine(new Pen(accent, 2), new Point(x, Target.Top + 5), new Point(x, Target.Bottom - 5));
            dc.DrawEllipse(accent, null, new Point(x, Target.Top + 5), 2.5, 2.5);
            dc.DrawEllipse(accent, null, new Point(x, Target.Bottom - 5), 2.5, 2.5);
        }
        var left = Math.Clamp(Pointer.X + 14, 0, Math.Max(0, AdornedElement.RenderSize.Width - 170));
        var top = Math.Clamp(Pointer.Y + 16, 0, Math.Max(0, AdornedElement.RenderSize.Height - 42));
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(240, 43, 54, 73)), new Pen(new SolidColorBrush(Color.FromArgb(150, 131, 213, 205)), 1), new Rect(left, top, 168, 40), 9, 9);
        if (Tile.Icon != null) dc.DrawImage(Tile.Icon, new Rect(left + 8, top + 7, 26, 26));
        var text = new FormattedText(Tile.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = 120, MaxTextHeight = 24, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(text, new Point(left + 40, top + 12));
        dc.Pop();
    }
}
