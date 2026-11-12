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

<!-- A fine light rim surrounds the dark outline, so both light and dark surfaces retain the silhouette. -->
<Path Data="M16,8 L17,3 L22,3 M2,17 L2,22 M32,17 L32,22 M10,10 L23,10 Q29,10 29,16 L29,22 Q29,27 23,27 L10,27 Q5,27 5,22 L5,16 Q5,10 10,10 Z" Stroke="#AEDAE6" StrokeThickness="4.2" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"/>
<Ellipse Canvas.Left="20" Canvas.Top=".5" Width="5" Height="5" Fill="#AEDAE6"/>
<!-- Preserve the dark under-strokes and opaque face from the light-background version. -->
<Path Data="M16,8 L17,3 L22,3 M2,17 L2,22 M32,17 L32,22" Stroke="#263E50" StrokeThickness="3.2" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"/>
<Path Data="M16,8 L17,3 L22,3" Stroke="#82E3EB" StrokeThickness="1.5" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
<Ellipse Canvas.Left="20.5" Canvas.Top="1" Width="4" Height="4" Fill="#9BEFF3" Stroke="#263E50" StrokeThickness=".7"/>
<Path Data="M10,10 L23,10 Q29,10 29,16 L29,22 Q29,27 23,27 L10,27 Q5,27 5,22 L5,16 Q5,10 10,10 Z" Stroke="#263E50" StrokeThickness="3.2"><Path.Fill><LinearGradientBrush StartPoint="0,0" EndPoint="0,1"><GradientStop Color="#2A4354" Offset="0"/><GradientStop Color="#142631" Offset="1"/></LinearGradientBrush></Path.Fill></Path>
<Path x:Name="HeadEdge" Data="M10,10 L23,10 Q29,10 29,16 L29,22 Q29,27 23,27 L10,27 Q5,27 5,22 L5,16 Q5,10 10,10 Z M2,17 L2,22 M32,17 L32,22" Stroke="#B4E2ED" StrokeThickness="1.4" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"/>
<Path Data="M10,19 Q13,13 16,19 M20,19 Q23,13 26,19" Stroke="#81E2EB" StrokeThickness="1.6" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
</Canvas></Viewbox>
</Grid>
<ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Light" Property="Opacity" Value="1"/><Setter TargetName="HeadEdge" Property="Stroke" Value="#E0FCFF"/><Setter TargetName="Shell" Property="Opacity" Value="1"/></Trigger><Trigger Property="IsPressed" Value="True"><Setter TargetName="Light" Property="Opacity" Value=".4"/><Setter TargetName="Shell" Property="Opacity" Value=".75"/></Trigger></ControlTemplate.Triggers>
</ControlTemplate></Button.Template></Button>
""";
}
