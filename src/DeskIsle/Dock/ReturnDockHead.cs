using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
namespace DeskIsle.Dock;

internal static class ReturnDockHead
{
    internal static Button Create() => (Button)XamlReader.Parse(Markup);
    internal static Viewbox Icon(double size = 26)
    {
        var element = XElement.Parse(Markup).Descendants().First(e => e.Name.LocalName == "Viewbox");
        var icon = (Viewbox)XamlReader.Parse(element.ToString());
        icon.Width = size; icon.Height = size;
        return icon;
    }
    private const string Markup = """
<Button xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="40" Height="40" Focusable="False" ToolTip="返回 Dock · 拖动可调整位置 · 右键关闭 Dock" AutomationProperties.Name="返回 Dock" UseLayoutRounding="False" SnapsToDevicePixels="False">
<Button.Template><ControlTemplate TargetType="Button">
<Grid Background="Transparent" Margin="2">
<Border x:Name="Shell" CornerRadius="12" Background="#142333" Opacity="0"/>
<Border x:Name="Light" CornerRadius="12" Background="#2254D8EC" Opacity="0"/>
<Viewbox Width="29" Height="29"><Canvas Width="34" Height="34">

<!-- Two continuous contours replace several subpixel rings and tiny end caps. -->
<Path Data="M16,8 L17,3 L22,3 M2,17 L2,22 M32,17 L32,22" Stroke="#344F60" StrokeThickness="3" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"/>
<Path Data="M16,8 L17,3 L22,3 M2,17 L2,22 M32,17 L32,22" Stroke="#A7DCE7" StrokeThickness="1.7" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"/>
<Ellipse Canvas.Left="20.5" Canvas.Top="1.5" Width="3" Height="3" Fill="#B9EEF3"/>
<Path Data="M10,10 L23,10 C27,10 29,12 29,16 L29,22 C29,25 27,27 23,27 L10,27 C7,27 5,25 5,22 L5,16 C5,12 7,10 10,10 Z" Fill="#1C3443" Stroke="#344F60" StrokeThickness="3.6" StrokeLineJoin="Round"/>
<Path x:Name="HeadEdge" Data="M10,10 L23,10 C27,10 29,12 29,16 L29,22 C29,25 27,27 23,27 L10,27 C7,27 5,25 5,22 L5,16 C5,12 7,10 10,10 Z" Stroke="#B4E2ED" StrokeThickness="1.8" StrokeLineJoin="Round"/>
<Path Data="M10,19 Q13,13 16,19 M20,19 Q23,13 26,19" Stroke="#81E2EB" StrokeThickness="1.7" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
</Canvas></Viewbox>
</Grid>
<ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Light" Property="Opacity" Value="1"/><Setter TargetName="HeadEdge" Property="Stroke" Value="#E0FCFF"/><Setter TargetName="Shell" Property="Opacity" Value="1"/></Trigger><Trigger Property="IsPressed" Value="True"><Setter TargetName="Light" Property="Opacity" Value=".4"/><Setter TargetName="Shell" Property="Opacity" Value=".75"/></Trigger></ControlTemplate.Triggers>
</ControlTemplate></Button.Template></Button>
""";
}
