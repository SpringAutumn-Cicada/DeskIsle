using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
namespace DeskIsle;

public partial class MainWindow
{
    // C design: filled, two-tone silhouettes. Fixed 24-unit canvas prevents
    // each glyph's geometry bounds from changing its optical size/alignment.
    private static Image CMenuIcon(string kind, bool active)
    {
        var group = new DrawingGroup();
        Brush light = new LinearGradientBrush(Color.FromRgb(201, 239, 224), Color.FromRgb(133, 193, 180), 70);
        Brush dark = new LinearGradientBrush(Color.FromRgb(99, 166, 151), Color.FromRgb(45, 104, 102), 70);
        Brush mint = new LinearGradientBrush(Color.FromRgb(167, 246, 217), Color.FromRgb(82, 196, 171), 70);
        Brush white = new SolidColorBrush(Color.FromRgb(236, 242, 241));
        Brush ink = new SolidColorBrush(Color.FromRgb(34, 66, 64));
        using (var d = group.Open())
        {
            d.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 24, 24));
            void Shape(string path, Brush fill) => d.DrawGeometry(fill,
                new Pen(new SolidColorBrush(Color.FromArgb(40, 225, 255, 246)), .45) { LineJoin = PenLineJoin.Round }, Geometry.Parse(path));
            void StateShape(string path)
            {
                d.DrawGeometry(active ? mint : Brushes.Transparent,
                    new Pen(active ? light : white, active ? .6 : 1.9) { LineJoin = PenLineJoin.Round }, Geometry.Parse(path));
            }
            void Line(string path, Brush color, double width = 2.5) => d.DrawGeometry(null,
                new Pen(color, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, Geometry.Parse(path));
            switch (kind)
            {
                case "add":
                    d.DrawRoundedRectangle(dark, null, new Rect(6, 2, 17, 14), 3, 3);
                    d.DrawRoundedRectangle(light, null, new Rect(1, 6, 18, 15), 3, 3);
                    Line("M4,7 L15,7", new SolidColorBrush(Color.FromArgb(115, 236, 255, 247)), .65);
                    Line("M10,10 L10,17 M6.5,13.5 L13.5,13.5", ink, 1.9); break;
                case "pencil":
                    Shape("M3,16 L16,3 Q18,1 20,3 L22,5 Q23,7 21,9 L8,22 L2,23 Z", light);
                    Shape("M16,3 Q18,1 20,3 L22,5 Q23,7 21,9 Z", dark);
                    Line("M15.5,4 L20,8.5", white, 1.5); break;
                case "folder":
                    Shape("M1,6 Q1,3 4,3 L9,3 L12,6 L20,6 Q22,6 22,9 L22,20 L1,20 Z", light);
                    Shape("M5,10 L23,10 L21,21 L2,21 Z", dark); break;
                case "star":
                    StateShape("M12,1 Q12.6,1 13,2 L15.6,7.6 L22,8.5 Q24,8.8 22.5,10.3 L18,14.8 L19.1,21 Q19.4,23 17.6,22 L12,19 L6.4,22 Q4.6,23 4.9,21 L6,14.8 L1.5,10.3 Q0,8.8 2,8.5 L8.4,7.6 L11,2 Q11.4,1 12,1 Z"); break;
                case "pin":
                    StateShape("M15,2 Q16,1 17,2 L23,8 Q24,9 23,10 L20,11 L16,16 L16,19 L14,21 L9,16 L4,22 Q2,24 1,22 L7,15 L3,11 L5,9 L8,9 L13,5 Z"); break;
                case "monitor":
                    d.DrawRoundedRectangle(active ? mint : Brushes.Transparent,
                        new Pen(active ? light : white, 1.8), new Rect(2, 3, 20, 13), 2, 2);
                    if (active) Line("M5,6 L19,6", light, .8);
                    Line("M12,17 L12,20 M8,21 L16,21", active ? mint : white, 2);
                    if (!active) Line("M6,12 L18,6", white, 1.5);
                    break;
                case "palette":
                    Shape("M12,1 C3,1 0,8 2,15 C3,20 7,24 11,22 C14,20 11,17 16,16 C23,16 25,10 21,5 C19,2 16,1 12,1 Z", light);
                    foreach (var dot in new[] { new Point(7,7), new Point(13,5), new Point(18,9), new Point(6,13) }) d.DrawEllipse(ink, null, dot, 1.7, 1.7);
                    d.DrawEllipse(dark, null, new Point(11,18), 1.8, 1.8); break;
                case "trash":
                    Shape("M3,7 L21,7 L19.5,22 Q19,23 17,23 L7,23 Q5,23 4.5,21 Z", dark);
                    Line("M9,4 Q9,2 11,2 L14,2 Q16,2 16,4", light, 1.8);
                    Line("M2,6 L22,6", white, 2.8); break;
                case "sliders":
                    Line("M2,7 L22,7", white, 2.7); Line("M2,17 L22,17", mint, 2.7);
                    d.DrawEllipse(light, null, new Point(17,7), 3.7, 3.7);
                    d.DrawEllipse(dark, null, new Point(7,17), 3.7, 3.7); break;
                case "power":
                    Line("M6,5 A9,9 0 1 0 18,5", dark, 3.7);
                    Line("M12,2 L12,11", mint, 3.5); break;
                case "transfer":
                    Line("M3,7 L21,7 M17,3 L21,7 L17,11", light, 2.4);
                    Line("M21,17 L3,17 M7,13 L3,17 L7,21", dark, 2.4); break;
                default: // refresh, matching the white upper/teal lower arrows
                    Line("M4,10 A8.25,8.25 0 0 1 19,7.5", white, 2.6);
                    Shape("M14.5,4.3 L20.8,7.8 L14.8,10 Z", light);
                    Line("M20,14 A8.25,8.25 0 0 1 5,16.5", mint, 2.6);
                    Shape("M9.5,19.7 L3.2,16.2 L9.2,14 Z", dark); break;
            }
        }
        group.Freeze();
        var style = new Style(typeof(Image));
        style.Setters.Add(new Setter(OpacityProperty, 1.0));
        if (active && kind is "star" or "pin" or "monitor")
            style.Setters.Add(new Setter(EffectProperty, new System.Windows.Media.Effects.DropShadowEffect
                { Color = Color.FromRgb(113, 220, 193), BlurRadius = 2, ShadowDepth = 0, Opacity = .18 }));
        var hover = new DataTrigger { Binding = new Binding("IsHighlighted") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(MenuItem), 1) }, Value = true };
        hover.Setters.Add(new Setter(OpacityProperty, 1.0));
        hover.Setters.Add(new Setter(EffectProperty, new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(113, 220, 193), BlurRadius = 3, ShadowDepth = 0, Opacity = .22 })); style.Triggers.Add(hover);
        return new Image { Source = new DrawingImage(group), Width = 19, Height = 19,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Style = style };
    }
}
