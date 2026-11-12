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
<Button xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="40" Height="40" Focusable="False" ToolTip="返回 Dock · 拖动可调整位置 · 右键关闭 Dock" AutomationProperties.Name="返回 Dock" UseLayoutRounding="True" SnapsToDevicePixels="True">
<Button.Template><ControlTemplate TargetType="Button">
<Grid Background="Transparent" Margin="2">
<Border x:Name="Shell" CornerRadius="12" Background="#1D3B4A" Opacity="1"/>
<Border x:Name="Light" CornerRadius="12" Background="#2254D8EC" Opacity="0"/>
<Viewbox Width="29" Height="29"><Canvas Width="34" Height="34">
<Path Data="M16,8 L17,3 L22,3" Stroke="#82E3EB" StrokeThickness="1.5" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
<Ellipse Canvas.Left="21" Canvas.Top="1.5" Width="3" Height="3" Fill="#9BEFF3"/>
<Path Data="M10,10 L23,10 Q29,10 29,16 L29,22 Q29,27 23,27 L10,27 Q5,27 5,22 L5,16 Q5,10 10,10 Z M2,17 L2,22 M32,17 L32,22" Stroke="#B4E2ED" StrokeThickness="1.6" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"/>
<Path Data="M10,19 Q13,13 16,19 M20,19 Q23,13 26,19" Stroke="#81E2EB" StrokeThickness="1.6" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
</Canvas></Viewbox>
</Grid>
<ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Light" Property="Opacity" Value="1"/><Setter TargetName="Shell" Property="Background" Value="#294D5E"/></Trigger><Trigger Property="IsPressed" Value="True"><Setter TargetName="Light" Property="Opacity" Value=".4"/><Setter TargetName="Shell" Property="Background" Value="#102A38"/></Trigger><Trigger Property="IsMouseCaptured" Value="True"><Setter TargetName="Shell" Property="Background" Value="#102A38"/><Setter TargetName="Light" Property="Opacity" Value=".35"/></Trigger></ControlTemplate.Triggers>
</ControlTemplate></Button.Template></Button>
""";
}
