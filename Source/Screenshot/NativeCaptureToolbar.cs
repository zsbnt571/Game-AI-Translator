using W=System.Windows;
using WC=System.Windows.Controls;
using WM=System.Windows.Media;
namespace ScreenshotTranslationUiTester;

// Selection geometry stays in capture pixels; only the presenter converts to DIPs.
internal sealed class NativeCaptureToolbar(CaptureOverlay owner)
{
    private readonly WC.WrapPanel actions=new();
    private WC.Border? view;
    private Point location;
    internal int MaximumWidthInPixels { get; set; } = int.MaxValue;
    internal WC.Border View=>view??=new(){Child=actions,Padding=new(5),CornerRadius=new(5),Background=WpfDrawing.Brush(Color.FromArgb(235,18,21,28)),Visibility=W.Visibility.Hidden};
    private double Scale=>owner.DeviceDpi/96d;
    internal void Add(string caption,Action action,bool primary=false)
    {
        var button=new WC.Button{Content=UiLanguage.Text(caption),MinWidth=76,MinHeight=36,Margin=new(4),Padding=new(12,6,12,6),Background=WpfDrawing.Brush(primary?Color.FromArgb(32,155,104):Color.FromArgb(35,40,52)),Foreground=WM.Brushes.White};
        W.Automation.AutomationProperties.SetName(button,UiLanguage.Text(caption));button.Click+=(_,_)=>action();actions.Children.Add(button);
    }
    internal bool Visible{get=>View.Visibility==W.Visibility.Visible;set=>View.Visibility=value?W.Visibility.Visible:W.Visibility.Hidden;}
    internal Size PreferredSize{get{View.MaxWidth=Math.Max(120,Math.Min(owner.ClientSize.Width-16,MaximumWidthInPixels)/Scale);View.Measure(new(View.MaxWidth,double.PositiveInfinity));return new((int)Math.Ceiling(View.DesiredSize.Width*Scale),(int)Math.Ceiling(View.DesiredSize.Height*Scale));}}
    internal Point Location{get=>location;set{location=value;WC.Canvas.SetLeft(View,value.X/Scale);WC.Canvas.SetTop(View,value.Y/Scale);}}
    internal Rectangle Bounds=>new(location,PreferredSize);
    internal void PerformLayout()=>View.UpdateLayout();
    internal void BringToFront()=>WC.Panel.SetZIndex(View,1);
}

public sealed partial class CaptureOverlay
{
    internal override bool UsesNativePresentation=>true;
    internal override W.FrameworkElement CreateNativeContent(WpfSettingsFields fields)
    {
        var layer=new WC.Canvas();layer.Children.Add(_toolbar.View);return layer;
    }
}
