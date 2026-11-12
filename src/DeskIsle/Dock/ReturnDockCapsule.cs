using System.Windows.Controls;
using System.Windows.Markup;
namespace DeskIsle.Dock;

internal static class ReturnDockCapsule
{
    internal static Button Create() => (Button)XamlReader.Parse("""
<Button xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="76" Height="38" Focusable="False" ToolTip="返回 Dock · 拖动可调整位置 · 右键关闭 Dock" AutomationProperties.Name="返回 Dock" UseLayoutRounding="True" SnapsToDevicePixels="True">
<Button.Template><ControlTemplate TargetType="Button">
<Grid Background="Transparent" Margin="2">
<Border x:Name="Shell" CornerRadius="17" BorderThickness="1" BorderBrush="#82BBD0"><Border.Background><LinearGradientBrush StartPoint="0,0" EndPoint="0,1"><GradientStop Color="#344D5C" Offset="0"/><GradientStop Color="#172731" Offset="1"/></LinearGradientBrush></Border.Background></Border>
<Border x:Name="Light" CornerRadius="17" Background="#2254D8EC" Opacity="0"/>
<Grid Margin="8,2"><Grid.ColumnDefinitions><ColumnDefinition Width="30"/><ColumnDefinition Width="7"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
<Viewbox Width="29" Height="29"><Canvas Width="34" Height="34">
<Path Data="M16,8 L17,3 L22,3" Stroke="#82E3EB" StrokeThickness="1.5" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
<Ellipse Canvas.Left="21" Canvas.Top="1.5" Width="3" Height="3" Fill="#9BEFF3"/>
<Path Data="M10,10 L23,10 Q29,10 29,16 L29,22 Q29,27 23,27 L10,27 Q5,27 5,22 L5,16 Q5,10 10,10 Z M2,17 L2,22 M32,17 L32,22" Stroke="#B4E2ED" StrokeThickness="1.6" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"/>
<Path Data="M10,19 Q13,13 16,19 M20,19 Q23,13 26,19" Stroke="#81E2EB" StrokeThickness="1.6" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
</Canvas></Viewbox>
<Border Grid.Column="1" Width=".7" Height="16" Background="#526B80"/>
<Viewbox Grid.Column="2" Width="18" Height="18"><Path Data="M8,3 L2,8 L8,13 M3,8 L13,8 Q21,8 21,15 Q21,21 14,21 L11,21" Stroke="#A6E6ED" StrokeThickness="2" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"/></Viewbox>
</Grid></Grid>
<ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Light" Property="Opacity" Value="1"/><Setter TargetName="Shell" Property="BorderBrush" Value="#B0F2F7"/></Trigger><Trigger Property="IsPressed" Value="True"><Setter TargetName="Light" Property="Opacity" Value=".4"/><Setter TargetName="Shell" Property="Opacity" Value=".75"/></Trigger></ControlTemplate.Triggers>
</ControlTemplate></Button.Template></Button>
""");
}
