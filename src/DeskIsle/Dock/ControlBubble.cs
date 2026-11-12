using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace DeskIsle.Dock;

// One continuous outline avoids a seam where the pointer meets the glass panel.
internal sealed class ControlBubble : Decorator
{
    private double anchorX = double.NaN;
    internal double AnchorX { get => anchorX; set { if (anchorX == value) return; anchorX = value; InvalidateVisual(); } }
    protected override Size MeasureOverride(Size available)
    {
        Child?.Measure(new Size(Math.Max(0, available.Width - 48), Math.Max(0, available.Height - 58)));
        var desired = Child?.DesiredSize ?? new Size();
        return new Size(desired.Width + 48, desired.Height + 58);
    }
    protected override Size ArrangeOverride(Size size)
    {
        Child?.Arrange(new Rect(24, 24, Math.Max(0, size.Width - 48), Math.Max(0, size.Height - 58)));
        return size;
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double left = 8, top = 8, right = ActualWidth - 8, bottom = ActualHeight - 18, radius = 18;
        if (right - left < 60 || bottom - top < 40) return;
        var pointer = Math.Clamp(double.IsFinite(anchorX) ? anchorX : ActualWidth / 2, left + 30, right - 30);
        var shape = new StreamGeometry();
        using (var g = shape.Open())
        {
            g.BeginFigure(new Point(left + radius, top), true, true);
            g.LineTo(new Point(right - radius, top), true, false);
            g.QuadraticBezierTo(new Point(right, top), new Point(right, top + radius), true, false);
            g.LineTo(new Point(right, bottom - radius), true, false);
            g.QuadraticBezierTo(new Point(right, bottom), new Point(right - radius, bottom), true, false);
            g.LineTo(new Point(pointer + 11, bottom), true, false);
            g.LineTo(new Point(pointer + 1, bottom + 9), true, false);
            g.QuadraticBezierTo(new Point(pointer, bottom + 10), new Point(pointer - 1, bottom + 9), true, false);
            g.LineTo(new Point(pointer - 11, bottom), true, false);
            g.LineTo(new Point(left + radius, bottom), true, false);
            g.QuadraticBezierTo(new Point(left, bottom), new Point(left, bottom - radius), true, false);
            g.LineTo(new Point(left, top + radius), true, false);
            g.QuadraticBezierTo(new Point(left, top), new Point(left + radius, top), true, false);
        }
        shape.Freeze();
        var fill = new LinearGradientBrush(Color.FromArgb(250, 38, 52, 65), Color.FromArgb(252, 30, 40, 51), new Point(0, 0), new Point(1, 1));
        dc.DrawGeometry(fill, new Pen(new SolidColorBrush(Color.FromArgb(170, 117, 153, 173)), 1), shape);
    }
}
