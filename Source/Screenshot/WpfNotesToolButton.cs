using W=System.Windows;
using C=System.Windows.Controls;
using P=System.Windows.Controls.Primitives;
using M=System.Windows.Media;

namespace ScreenshotTranslationUiTester;

internal sealed class WpfNotesToolButton : P.ToggleButton
{
    private static readonly W.Style NoteStyle=(W.Style)W.Markup.XamlReader.Parse("""
      <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ToggleButton">
        <Setter Property="Width" Value="32"/><Setter Property="Height" Value="30"/>
        <Setter Property="Foreground" Value="{DynamicResource Fusion.Text}"/><Setter Property="Background" Value="Transparent"/>
        <Setter Property="BorderBrush" Value="Transparent"/><Setter Property="BorderThickness" Value="1"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ToggleButton">
          <Border x:Name="Frame" CornerRadius="4" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="Background" Value="{DynamicResource Fusion.Secondary}"/></Trigger>
            <Trigger Property="IsChecked" Value="True"><Setter Property="Foreground" Value="{DynamicResource Fusion.Accent}"/><Setter TargetName="Frame" Property="Background" Value="#223D9CFF"/><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource Fusion.Accent}"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Frame" Property="Opacity" Value="0.35"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      """);
    internal WpfNotesToolButton(string id,Action action)
    {
        Style=NoteStyle;Focusable=false;Margin=new(1,0,1,0);
        W.Automation.AutomationProperties.SetAutomationId(this,id);
        Content=Glyph(id);Click+=(_,_)=>action();
    }
    private static W.FrameworkElement Glyph(string id)
    {
        if(id is "GameNotesBold" or "GameNotesItalic" or "GameNotesUnderline")
            return new C.TextBlock{Text=id=="GameNotesBold"?"B":id=="GameNotesItalic"?"I":"U",FontFamily=new("Segoe UI"),FontSize=16,
                FontWeight=id=="GameNotesBold"?W.FontWeights.Bold:W.FontWeights.Normal,FontStyle=id=="GameNotesItalic"?W.FontStyles.Italic:W.FontStyles.Normal,
                TextDecorations=id=="GameNotesUnderline"?W.TextDecorations.Underline:null};
        string data=id switch{
            "GameNotesHighlight"=>"M 4,12 L 10,2 L 16,6 L 10,16 Z M 4,12 L 10,16 M 2,18 L 10,18",
            "GameNotesBullets"=>"M 2,4 L 3,4 M 2,9 L 3,9 M 2,14 L 3,14 M 7,4 L 18,4 M 7,9 L 18,9 M 7,14 L 18,14",
            "GameNotesNumbering"=>"M 1,3 L 3,2 L 3,6 M 1,6 L 5,6 M 1,10 C 5,7 6,11 2,13 L 1,15 L 5,15 M 8,4 L 18,4 M 8,10 L 18,10 M 8,16 L 18,16",
            "GameNotesUndo"=>"M 7,3 L 2,7 L 7,11 M 2,7 L 10,7 C 16,7 17,15 11,17",
            _=>"M 11,3 L 16,7 L 11,11 M 16,7 L 8,7 C 2,7 1,15 7,17"};
        var glyph=new W.Shapes.Path{Data=M.Geometry.Parse(data),Width=18,Height=18,Stretch=M.Stretch.Uniform,StrokeThickness=1.5,StrokeStartLineCap=M.PenLineCap.Round,StrokeEndLineCap=M.PenLineCap.Round,StrokeLineJoin=M.PenLineJoin.Round};
        glyph.SetBinding(W.Shapes.Shape.StrokeProperty,new W.Data.Binding("Foreground"){RelativeSource=new(W.Data.RelativeSourceMode.FindAncestor,typeof(WpfNotesToolButton),1)});return glyph;
    }
    internal void Caption(string label,string shortcut)
    {
        string text=UiLanguage.Text(label);ToolTip=shortcut.Length>0?text+"  "+shortcut:text;
        W.Automation.AutomationProperties.SetName(this,text);
    }
}
