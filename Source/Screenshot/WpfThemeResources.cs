using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;

namespace ScreenshotTranslationUiTester;

internal static class WpfThemeResources
{
    private sealed record ButtonPresentation(string Text,string? Icon,bool Primary,ThemePalette Palette,bool Trailing,bool Compact);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WC.Button,ButtonPresentation> ButtonPresentations=new();
    internal static W.ResourceDictionary Create() => (W.ResourceDictionary)W.Markup.XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style TargetType="Button">
            <Setter Property="Background" Value="{DynamicResource Fusion.Main}"/>
            <Setter Property="Foreground" Value="{DynamicResource Fusion.Text}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource Fusion.Border}"/>
            <Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="12,6"/>
            <Setter Property="MinHeight" Value="34"/><Setter Property="Cursor" Value="Hand"/>
            <Setter Property="HorizontalContentAlignment" Value="Center"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
              <Border x:Name="Outline" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="7" Padding="{TemplateBinding Padding}">
                <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Outline" Property="BorderBrush" Value="{DynamicResource Fusion.Accent}"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Outline" Property="BorderBrush" Value="{DynamicResource Fusion.Accent}"/><Setter TargetName="Outline" Property="BorderThickness" Value="2"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.5"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="TextBox">
            <Setter Property="Foreground" Value="{DynamicResource Fusion.Text}"/>
            <Setter Property="Background" Value="{DynamicResource Fusion.Main}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource Fusion.Border}"/>
            <Setter Property="CaretBrush" Value="{DynamicResource Fusion.Text}"/>
            <Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="10,6"/>
            <Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TextBox">
              <Border x:Name="Outline" CornerRadius="6" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Background="{TemplateBinding Background}">
                <ScrollViewer x:Name="PART_ContentHost"/>
              </Border>
              <ControlTemplate.Triggers><Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="Outline" Property="BorderBrush" Value="{DynamicResource Fusion.Accent}"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="ComboBoxItem">
            <Setter Property="Foreground" Value="{DynamicResource Fusion.Text}"/><Setter Property="Padding" Value="9,7"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem">
              <Border x:Name="Row" Padding="{TemplateBinding Padding}" Background="{DynamicResource Fusion.Secondary}"><ContentPresenter/></Border>
              <ControlTemplate.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Row" Property="Background" Value="{DynamicResource Fusion.Control}"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="ComboBox">
            <Setter Property="Foreground" Value="{DynamicResource Fusion.Text}"/><Setter Property="MinHeight" Value="34"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox">
              <Grid>
                <ToggleButton Focusable="False" IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                  <ToggleButton.Template><ControlTemplate TargetType="ToggleButton">
                    <Border Background="{DynamicResource Fusion.Secondary}" BorderBrush="{DynamicResource Fusion.Border}" BorderThickness="1"><TextBlock Text="⌄" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,9,2" Foreground="{DynamicResource Fusion.Text}"/></Border>
                  </ControlTemplate></ToggleButton.Template>
                </ToggleButton>
                <ContentPresenter x:Name="SelectedContent" IsHitTestVisible="False" Margin="10,6,28,6" VerticalAlignment="Center" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"/>
                <TextBox x:Name="PART_EditableTextBox" Visibility="Collapsed" Margin="1,1,28,1" Padding="9,5" BorderThickness="0" IsReadOnly="{TemplateBinding IsReadOnly}"/>
                <Popup x:Name="PART_Popup" Placement="Bottom" AllowsTransparency="True" IsOpen="{TemplateBinding IsDropDownOpen}" PopupAnimation="None">
                  <Border MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}" MaxHeight="360" Background="{DynamicResource Fusion.Secondary}" BorderBrush="{DynamicResource Fusion.Accent}" BorderThickness="1"><ScrollViewer CanContentScroll="True"><ItemsPresenter/></ScrollViewer></Border>
                </Popup>
              </Grid>
              <ControlTemplate.Triggers><Trigger Property="IsEditable" Value="True"><Setter TargetName="SelectedContent" Property="Visibility" Value="Collapsed"/><Setter TargetName="PART_EditableTextBox" Property="Visibility" Value="Visible"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="CheckBox">
            <Setter Property="Foreground" Value="{DynamicResource Fusion.Text}"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="CheckBox">
              <Grid><Grid.ColumnDefinitions><ColumnDefinition Width="20"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                <Border x:Name="Box" Width="16" Height="16" VerticalAlignment="Center" CornerRadius="3" BorderThickness="1" BorderBrush="{DynamicResource Fusion.Border}" Background="{DynamicResource Fusion.Main}">
                  <TextBlock x:Name="Check" Text="✓" Visibility="Collapsed" Foreground="White" FontSize="12" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                </Border><ContentPresenter Grid.Column="1" Margin="7,0,0,0" VerticalAlignment="Center"/>
              </Grid>
              <ControlTemplate.Triggers>
                <Trigger Property="IsChecked" Value="True"><Setter TargetName="Check" Property="Visibility" Value="Visible"/><Setter TargetName="Box" Property="Background" Value="{DynamicResource Fusion.Accent}"/></Trigger>
                <Trigger Property="IsChecked" Value="{x:Null}"><Setter TargetName="Check" Property="Visibility" Value="Visible"/><Setter TargetName="Check" Property="Text" Value="−"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource Fusion.Accent}"/><Setter TargetName="Box" Property="BorderThickness" Value="2"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.5"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="ScrollBar">
            <Setter Property="Width" Value="12"/><Setter Property="Background" Value="Transparent"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollBar">
              <Track x:Name="PART_Track" IsDirectionReversed="True" Orientation="{TemplateBinding Orientation}">
                <Track.DecreaseRepeatButton><RepeatButton Command="ScrollBar.PageUpCommand" Opacity="0" Focusable="False"/></Track.DecreaseRepeatButton>
                <Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType="Thumb"><Border Background="{DynamicResource Fusion.Border}" Margin="3,0" CornerRadius="3"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
                <Track.IncreaseRepeatButton><RepeatButton Command="ScrollBar.PageDownCommand" Opacity="0" Focusable="False"/></Track.IncreaseRepeatButton>
              </Track>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
        </ResourceDictionary>
        """);

    internal static void Apply(W.ResourceDictionary resources)
    {
        var p = UiTheme.Current;
        foreach (var (name, color) in new[] { ("Main",p.Main),("Secondary",p.Secondary),("Control",p.Control),("Text",p.Text),("Muted",p.SecondaryText),("Accent",p.Accent),("Border",p.Border),("Sidebar",UiTheme.Sidebar) })
            resources["Fusion."+name]=WpfDrawing.Brush(color);
    }
    internal static WC.Button Button(string text,string? icon,Action action,bool primary=false,bool trailingIcon=false)
    {
        var result=new WC.Button();
        result.Click+=(_,_)=>action();
        SetButton(result,text,icon,primary,trailingIcon);
        return result;
    }
    internal static void SetButton(WC.Button button,string text,string? icon,bool primary=false,bool trailingIcon=false,bool compact=false)
    {
        var p=UiTheme.Current;var color=primary?Color.White:p.Text;
        text=UiLanguage.Text(text);
        var presentation=new ButtonPresentation(text,icon,primary,p,trailingIcon,compact);
        if(ButtonPresentations.TryGetValue(button,out var previous)&&previous==presentation)return;
        ButtonPresentations.Remove(button);ButtonPresentations.Add(button,presentation);
        var row=new WC.StackPanel{Orientation=WC.Orientation.Horizontal};
        double size=compact?14:18,gap=compact?5:8;
        WC.Image? glyph=icon is not null?new(){Source=WpfDrawing.Icon(icon,primary?Color.White:p.Accent),Width=size,Height=size,Margin=trailingIcon?new W.Thickness(text.Length>0?gap:0,0,0,0):new W.Thickness(0,0,text.Length>0?gap:0,0)}:null;
        if(glyph is not null&&!trailingIcon)row.Children.Add(glyph);
        if(text.Length>0)row.Children.Add(new WC.TextBlock{Text=text,VerticalAlignment=W.VerticalAlignment.Center});
        if(glyph is not null&&trailingIcon)row.Children.Add(glyph);
        button.Content=row;button.Foreground=WpfDrawing.Brush(color);
        button.Background=WpfDrawing.Brush(primary?p.Accent:p.Main);
        button.BorderBrush=WpfDrawing.Brush(primary?p.Accent:p.Border);
        W.Automation.AutomationProperties.SetName(button,UiLanguage.Text(text));
    }
}
