using W=System.Windows;
using C=System.Windows.Controls;

namespace ScreenshotTranslationUiTester;

internal static class WpfAiControls
{
    internal static void SetWorkspaceButton(C.Button button,string text,bool primary=false,bool light=false,bool example=false)
    {
        WpfThemeResources.SetButton(button,text,null,primary);
        button.Height=button.MinHeight=button.MaxHeight=example?40:36;
        button.MinWidth=0;button.Padding=light?new(6,0,6,0):new(10,0,10,0);button.VerticalAlignment=W.VerticalAlignment.Center;
        button.VerticalContentAlignment=W.VerticalAlignment.Center;button.ToolTip=text;
        button.Content=new C.TextBlock{Text=text,TextWrapping=example?W.TextWrapping.Wrap:W.TextWrapping.NoWrap,TextTrimming=W.TextTrimming.CharacterEllipsis,TextAlignment=W.TextAlignment.Center,VerticalAlignment=W.VerticalAlignment.Center};
        if(light){button.Background=System.Windows.Media.Brushes.Transparent;button.BorderBrush=System.Windows.Media.Brushes.Transparent;}
    }
    // Simple WPF borders and ticks also work with the compatibility renderer.
    internal static void ApplySlider(C.Slider slider)
    {
        slider.IsMoveToPointEnabled=true;
        slider.Template=(C.ControlTemplate)W.Markup.XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Slider">
          <Grid Margin="2,2,2,2" MinHeight="18">
            <Track x:Name="PART_Track" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{Binding Value, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
              <Track.DecreaseRepeatButton><RepeatButton Command="Slider.DecreaseLarge" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"><Border Height="4" VerticalAlignment="Center" CornerRadius="2" Background="{DynamicResource Fusion.Accent}"/></Border></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
              <Track.Thumb><Thumb Width="24" Height="24"><Thumb.Template><ControlTemplate TargetType="Thumb"><Border Background="Transparent"><Border Width="16" Height="16" VerticalAlignment="Center" HorizontalAlignment="Center" Background="{DynamicResource Fusion.Accent}" BorderBrush="{DynamicResource Fusion.Text}" BorderThickness="1" CornerRadius="8"/></Border></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
              <Track.IncreaseRepeatButton><RepeatButton Command="Slider.IncreaseLarge" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"><Border Height="4" VerticalAlignment="Center" CornerRadius="2" Background="{DynamicResource Fusion.Border}"/></Border></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
            </Track>
            <TickBar Fill="{DynamicResource Fusion.Muted}" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" TickFrequency="1" Placement="Bottom" Height="5" VerticalAlignment="Bottom" Margin="8,0,8,0" IsHitTestVisible="False"/>
          </Grid>
        </ControlTemplate>
        """);
    }
}
