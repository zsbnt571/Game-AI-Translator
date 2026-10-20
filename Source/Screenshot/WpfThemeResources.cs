using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;

namespace ScreenshotTranslationUiTester;

internal static class WpfThemeResources
{
    static WpfThemeResources()
    {
        // Single-line inputs also contain a ScrollViewer. Its default wheel
        // handler consumes input even when there is nothing inside to scroll.
        W.EventManager.RegisterClassHandler(typeof(WC.TextBox), System.Windows.Input.Mouse.PreviewMouseWheelEvent,
            new System.Windows.Input.MouseWheelEventHandler((sender, e) =>
            { if (sender is WC.TextBox { AcceptsReturn: false } input) WpfScrollRouting.RouteToParent(input, e); }));
        W.EventManager.RegisterClassHandler(typeof(WC.PasswordBox), System.Windows.Input.Mouse.PreviewMouseWheelEvent,
            new System.Windows.Input.MouseWheelEventHandler((sender, e) => WpfScrollRouting.RouteToParent((W.DependencyObject)sender, e)));
    }
    private sealed record ButtonPresentation(string Text,string? Icon,bool Primary,ThemePalette Palette,bool Trailing,bool Compact,bool CoverOverlay);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WC.Button,ButtonPresentation> ButtonPresentations=new();
    internal static W.ResourceDictionary Create() => (W.ResourceDictionary)W.Markup.XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style x:Key="{x:Static SystemParameters.FocusVisualStyleKey}" TargetType="Control">
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Control">
              <Border BorderBrush="{DynamicResource Fusion.Accent}" BorderThickness="1" CornerRadius="3" Margin="1" IsHitTestVisible="False"/>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="ScrollViewer"><Setter Property="FocusVisualStyle" Value="{x:Null}"/></Style>
          <Style TargetType="Button">
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
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
          <Style x:Key="Fusion.TabButton" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
              <Border x:Name="Surface" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}">
                <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Surface" Property="Background" Value="{DynamicResource Fusion.Control}"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Surface" Property="BorderBrush" Value="{DynamicResource Fusion.Accent}"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.5"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="Fusion.NavButton" TargetType="Button" BasedOn="{StaticResource Fusion.TabButton}"/>
          <Style x:Key="Fusion.StepperButton" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
            <Setter Property="Background" Value="Transparent"/><Setter Property="Padding" Value="0"/>
            <Setter Property="MinHeight" Value="0"/><Setter Property="Margin" Value="3"/>
            <Setter Property="BorderThickness" Value="0"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
              <Border x:Name="Surface" Background="{TemplateBinding Background}" CornerRadius="3">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Surface" Property="Background" Value="{DynamicResource Fusion.Control}"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Surface" Property="Background" Value="{DynamicResource Fusion.Control}"/><Setter TargetName="Surface" Property="BorderThickness" Value="1"/><Setter TargetName="Surface" Property="BorderBrush" Value="{DynamicResource Fusion.Accent}"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="TextBox">
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
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
          <Style TargetType="PasswordBox">
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Foreground" Value="{DynamicResource Fusion.Text}"/>
            <Setter Property="Background" Value="{DynamicResource Fusion.Main}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource Fusion.Border}"/>
            <Setter Property="CaretBrush" Value="{DynamicResource Fusion.Text}"/>
            <Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="10,6"/>
            <Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="PasswordBox">
              <Border x:Name="Outline" CornerRadius="6" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Background="{TemplateBinding Background}">
                <ScrollViewer x:Name="PART_ContentHost"/>
              </Border>
              <ControlTemplate.Triggers><Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="Outline" Property="BorderBrush" Value="{DynamicResource Fusion.Accent}"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="ComboBoxItem">
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Foreground" Value="{DynamicResource Fusion.Text}"/><Setter Property="Padding" Value="9,7"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem">
              <Border x:Name="Row" Padding="{TemplateBinding Padding}" Background="{DynamicResource Fusion.Secondary}"><ContentPresenter/></Border>
              <ControlTemplate.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Row" Property="Background" Value="{DynamicResource Fusion.Control}"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="ComboBox">
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Foreground" Value="{DynamicResource Fusion.Text}"/><Setter Property="MinHeight" Value="34"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox">
              <Grid>
                <ToggleButton Focusable="False" IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                  <ToggleButton.Template><ControlTemplate TargetType="ToggleButton">
                    <Border Background="{DynamicResource Fusion.Secondary}" BorderBrush="{DynamicResource Fusion.Border}" BorderThickness="1"><Path x:Name="DropDownChevron" Data="M 1,1 L 5,5 L 9,1" Width="10" Height="6" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,10,0" Stroke="{DynamicResource Fusion.Text}" StrokeThickness="1.5" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"/></Border>
                  </ControlTemplate></ToggleButton.Template>
                </ToggleButton>
                <ContentPresenter x:Name="SelectedContent" IsHitTestVisible="False" Margin="10,6,28,6" VerticalAlignment="Center" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"/>
                <TextBox x:Name="PART_EditableTextBox" Visibility="Collapsed" Margin="1,1,28,1" Padding="9,5" BorderThickness="0" IsReadOnly="{TemplateBinding IsReadOnly}"/>
                <Border x:Name="FocusOutline" IsHitTestVisible="False" BorderThickness="1" BorderBrush="Transparent"/>
                <Popup x:Name="PART_Popup" Placement="Bottom" AllowsTransparency="True" IsOpen="{TemplateBinding IsDropDownOpen}" PopupAnimation="None">
                  <Border MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}" MaxHeight="360" Background="{DynamicResource Fusion.Secondary}" BorderBrush="{DynamicResource Fusion.Accent}" BorderThickness="1"><ScrollViewer CanContentScroll="True"><ItemsPresenter/></ScrollViewer></Border>
                </Popup>
              </Grid>
              <ControlTemplate.Triggers><Trigger Property="IsEditable" Value="True"><Setter TargetName="SelectedContent" Property="Visibility" Value="Collapsed"/><Setter TargetName="PART_EditableTextBox" Property="Visibility" Value="Visible"/></Trigger><Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="FocusOutline" Property="BorderBrush" Value="{DynamicResource Fusion.Accent}"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="CheckBox">
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
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
          <Style TargetType="RichTextBox">
            <Setter Property="Foreground" Value="{DynamicResource Fusion.Text}"/><Setter Property="Background" Value="{DynamicResource Fusion.Main}"/><Setter Property="BorderBrush" Value="{DynamicResource Fusion.Border}"/><Setter Property="CaretBrush" Value="{DynamicResource Fusion.Text}"/>
          </Style>
          <Style TargetType="ListBox"><Setter Property="Background" Value="{DynamicResource Fusion.Secondary}"/><Setter Property="Foreground" Value="{DynamicResource Fusion.Text}"/><Setter Property="BorderBrush" Value="{DynamicResource Fusion.Border}"/><Setter Property="ScrollViewer.CanContentScroll" Value="True"/><Setter Property="VirtualizingPanel.IsVirtualizing" Value="True"/></Style>
          <Style TargetType="ListBoxItem">
            <Setter Property="Foreground" Value="{DynamicResource Fusion.Text}"/><Setter Property="Padding" Value="7,5"/><Setter Property="HorizontalContentAlignment" Value="Stretch"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListBoxItem">
              <Border x:Name="Row" Background="Transparent" BorderBrush="Transparent" BorderThickness="1" CornerRadius="5" Padding="{TemplateBinding Padding}"><ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"/></Border>
              <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Row" Property="Background" Value="{DynamicResource Fusion.Control}"/></Trigger><Trigger Property="IsSelected" Value="True"><Setter TargetName="Row" Property="Background" Value="{DynamicResource Fusion.Control}"/><Setter TargetName="Row" Property="BorderBrush" Value="{DynamicResource Fusion.Accent}"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.5"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="ContextMenu">
            <Setter Property="Foreground" Value="{DynamicResource Fusion.Text}"/><Setter Property="Background" Value="{DynamicResource Fusion.Secondary}"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ContextMenu"><Border Background="{TemplateBinding Background}" BorderBrush="{DynamicResource Fusion.Border}" BorderThickness="1" Padding="5" CornerRadius="6"><ScrollViewer MaxHeight="600" VerticalScrollBarVisibility="Auto"><ItemsPresenter/></ScrollViewer></Border></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="MenuItem">
            <Setter Property="Foreground" Value="{DynamicResource Fusion.Text}"/><Setter Property="Padding" Value="8,7"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="MenuItem">
              <Grid><Border x:Name="Row" Background="Transparent" CornerRadius="4" Padding="{TemplateBinding Padding}"><Grid><Grid.ColumnDefinitions><ColumnDefinition Width="20"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="16"/></Grid.ColumnDefinitions><TextBlock x:Name="Check" Text="✓" Visibility="Collapsed" VerticalAlignment="Center" Foreground="{DynamicResource Fusion.Accent}"/><ContentPresenter Grid.Column="1" ContentSource="Header" VerticalAlignment="Center"/><TextBlock Grid.Column="2" Text="{TemplateBinding InputGestureText}" Margin="20,0,0,0" Foreground="{DynamicResource Fusion.Muted}"/><TextBlock x:Name="Arrow" Grid.Column="3" Text="›" Visibility="Collapsed" HorizontalAlignment="Right"/></Grid></Border>
              <Popup x:Name="PART_Popup" IsOpen="{TemplateBinding IsSubmenuOpen}" Placement="Right" AllowsTransparency="True" Focusable="False"><Border Background="{DynamicResource Fusion.Secondary}" BorderBrush="{DynamicResource Fusion.Border}" BorderThickness="1" Padding="5" CornerRadius="6"><ScrollViewer MaxHeight="600" VerticalScrollBarVisibility="Auto"><ItemsPresenter/></ScrollViewer></Border></Popup></Grid>
              <ControlTemplate.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Row" Property="Background" Value="{DynamicResource Fusion.Control}"/></Trigger><Trigger Property="IsChecked" Value="True"><Setter TargetName="Check" Property="Visibility" Value="Visible"/></Trigger><Trigger Property="HasItems" Value="True"><Setter TargetName="Arrow" Property="Visibility" Value="Visible"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="Separator"><Setter Property="Margin" Value="5,4"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Separator"><Border Height="1" Background="{DynamicResource Fusion.Border}"/></ControlTemplate></Setter.Value></Setter></Style>
          <Style TargetType="TabControl"><Setter Property="Background" Value="{DynamicResource Fusion.Main}"/><Setter Property="Foreground" Value="{DynamicResource Fusion.Text}"/><Setter Property="BorderBrush" Value="{DynamicResource Fusion.Border}"/></Style>
          <Style TargetType="TabItem"><Setter Property="Foreground" Value="{DynamicResource Fusion.Text}"/><Setter Property="Padding" Value="14,8"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TabItem"><Border x:Name="Tab" Background="{DynamicResource Fusion.Main}" BorderBrush="Transparent" BorderThickness="0,0,0,2" Padding="{TemplateBinding Padding}"><ContentPresenter ContentSource="Header"/></Border><ControlTemplate.Triggers><Trigger Property="IsSelected" Value="True"><Setter TargetName="Tab" Property="BorderBrush" Value="{DynamicResource Fusion.Accent}"/><Setter Property="Foreground" Value="{DynamicResource Fusion.Accent}"/></Trigger><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Tab" Property="Background" Value="{DynamicResource Fusion.Control}"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
          <Style TargetType="RadioButton"><Setter Property="Foreground" Value="{DynamicResource Fusion.Text}"/><Setter Property="VerticalContentAlignment" Value="Center"/></Style>
          <Style TargetType="DataGridColumnHeader"><Setter Property="Foreground" Value="{DynamicResource Fusion.Text}"/><Setter Property="Background" Value="{DynamicResource Fusion.Control}"/><Setter Property="BorderBrush" Value="{DynamicResource Fusion.Border}"/><Setter Property="BorderThickness" Value="0,0,1,1"/><Setter Property="Padding" Value="8"/></Style>
          <Style TargetType="DataGridRow"><Setter Property="Background" Value="{DynamicResource Fusion.Secondary}"/><Setter Property="Foreground" Value="{DynamicResource Fusion.Text}"/><Style.Triggers><Trigger Property="IsSelected" Value="True"><Setter Property="Background" Value="{DynamicResource Fusion.Control}"/></Trigger></Style.Triggers></Style>
          <Style TargetType="DataGridCell"><Setter Property="BorderThickness" Value="0"/><Setter Property="Foreground" Value="{DynamicResource Fusion.Text}"/><Style.Triggers><Trigger Property="IsSelected" Value="True"><Setter Property="Background" Value="{DynamicResource Fusion.Control}"/><Setter Property="Foreground" Value="{DynamicResource Fusion.Text}"/></Trigger></Style.Triggers></Style>
          <Style TargetType="ScrollBar">
            <Setter Property="Width" Value="12"/><Setter Property="Background" Value="Transparent"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollBar">
              <Track x:Name="PART_Track" IsDirectionReversed="True" Orientation="{TemplateBinding Orientation}">
                <Track.DecreaseRepeatButton><RepeatButton x:Name="Decrease" Command="ScrollBar.PageUpCommand" Opacity="0" Focusable="False"/></Track.DecreaseRepeatButton>
                <Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType="Thumb"><Border Background="{DynamicResource Fusion.Border}" Margin="3,0" CornerRadius="3"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
                <Track.IncreaseRepeatButton><RepeatButton x:Name="Increase" Command="ScrollBar.PageDownCommand" Opacity="0" Focusable="False"/></Track.IncreaseRepeatButton>
              </Track>
              <ControlTemplate.Triggers><Trigger Property="Orientation" Value="Horizontal"><Setter TargetName="PART_Track" Property="IsDirectionReversed" Value="False"/><Setter TargetName="Decrease" Property="Command" Value="ScrollBar.PageLeftCommand"/><Setter TargetName="Increase" Property="Command" Value="ScrollBar.PageRightCommand"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
            <Style.Triggers><Trigger Property="Orientation" Value="Horizontal"><Setter Property="Width" Value="Auto"/><Setter Property="Height" Value="12"/></Trigger></Style.Triggers>
          </Style>
        </ResourceDictionary>
        """);

    internal static void Apply(W.ResourceDictionary resources)
    {
        var p = UiTheme.Current;
        foreach (var (name, color) in new[] { ("Main",p.Main),("Secondary",p.Secondary),("Control",p.Control),("Text",p.Text),("Muted",p.SecondaryText),("Accent",p.Accent),("Border",p.Border),("Sidebar",UiTheme.Sidebar) })
        {
            var key = "Fusion." + name;
            // Assigning even an unchanged resource invalidates all dynamic
            // references. Navigation does not require a theme-wide refresh.
            var ink = WM.Color.FromArgb(color.A, color.R, color.G, color.B);
            if (resources[key] is WM.SolidColorBrush existing && existing.Color == ink) continue;
            resources[key] = WpfDrawing.Brush(color);
        }
        // The stock ScrollViewer uses this system brush for the scrollbar junction.
        // Keep that small square in the active palette as well.
        var controlInk=WpfDrawing.Brush(p.Main);
        if(!ReferenceEquals(resources[W.SystemColors.ControlBrushKey],controlInk))resources[W.SystemColors.ControlBrushKey]=controlInk;
    }
    internal static WC.Button Button(string text,string? icon,Action action,bool primary=false,bool trailingIcon=false)
    {
        var result=new WC.Button();
        result.Click+=(_,_)=>action();
        SetButton(result,text,icon,primary,trailingIcon);
        return result;
    }
    internal static void SetButton(WC.Button button,string text,string? icon,bool primary=false,bool trailingIcon=false,bool compact=false,bool coverOverlay=false)
    {
        if (string.IsNullOrWhiteSpace(icon)) icon = null;
        var p=UiTheme.Current;var color=primary?Color.White:p.Text;
        var presentation=new ButtonPresentation(UiLanguage.Text(text),icon,primary,p,trailingIcon,compact,coverOverlay);
        if(ButtonPresentations.TryGetValue(button,out var previous)&&previous==presentation)return;
        ButtonPresentations.Remove(button);ButtonPresentations.Add(button,presentation);
        var row=new WC.StackPanel{Orientation=WC.Orientation.Horizontal};
        double size=compact?14:18,gap=compact?5:8;
        WC.Image? glyph=icon is not null?new(){Source=WpfDrawing.Icon(icon,primary||coverOverlay?Color.White:p.Accent),Width=size,Height=size,Margin=trailingIcon?new W.Thickness(text.Length>0?gap:0,0,0,0):new W.Thickness(0,0,text.Length>0?gap:0,0)}:null;
        if(glyph is not null&&!trailingIcon)row.Children.Add(glyph);
        if(text.Length>0)row.Children.Add(new NativeLocalizedTextBlock{Text=text,VerticalAlignment=W.VerticalAlignment.Center});
        if(glyph is not null&&trailingIcon)row.Children.Add(glyph);
        button.Content=row;
        if(primary||coverOverlay)button.Foreground=WM.Brushes.White;
        else button.SetResourceReference(WC.Control.ForegroundProperty,"Fusion.Text");
        button.SetResourceReference(WC.Control.BackgroundProperty,primary?"Fusion.Accent":"Fusion.Main");
        button.SetResourceReference(WC.Control.BorderBrushProperty,primary?"Fusion.Accent":"Fusion.Border");
        if(coverOverlay)
        {
            button.Background=WM.Brushes.Transparent;
            button.BorderBrush=WpfDrawing.Brush(Color.FromArgb(150,255,255,255));
            row.Effect=new System.Windows.Media.Effects.DropShadowEffect{Color=WM.Colors.Black,BlurRadius=3,ShadowDepth=1,Opacity=.85};
        }
        W.Automation.AutomationProperties.SetName(button,UiLanguage.Text(text));
    }
}
