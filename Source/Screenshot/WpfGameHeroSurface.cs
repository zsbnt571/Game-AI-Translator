using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using WB = System.Windows.Media.Imaging;
namespace ScreenshotTranslationUiTester;

internal sealed class WpfGameHeroSurface : WC.Panel
{
    private readonly WC.Border image;
    private readonly W.FrameworkElement text, edit;
    private readonly WC.Border shade, floor;
    private readonly WM.ImageBrush artwork;
    private readonly WC.WrapPanel toolbar = new() { HorizontalAlignment = W.HorizontalAlignment.Right };
    private readonly WC.Button adjust, save, cancel, reset, smaller, larger;
    private WB.BitmapSource? current;
    private W.Size arrangedSize;
    private int pixelWidth,pixelHeight;
    private W.Point? dragging;
    private CoverFraming committed = CoverFraming.Centered, frame = CoverFraming.Centered;
    private bool editing, saving;
    internal Func<CoverFraming,Task<bool>>? SaveFrame { get; set; }
    internal bool IsStacked => ActualWidth < 720;
    internal bool IsPortrait => current is not null && pixelWidth < pixelHeight;
    internal WC.Border ArtworkView => image;
    internal WB.BitmapSource? BackdropSource => null;
    internal CoverFraming Frame => frame;
    internal bool Editing => editing;

    internal WpfGameHeroSurface(WM.ImageBrush artwork, W.FrameworkElement text, W.FrameworkElement edit)
    {
        this.artwork=artwork;this.text=text;this.edit=edit;UseLayoutRounding=true;Focusable=true;
        image=new(){Background=artwork,IsHitTestVisible=false};
        shade=new(){Background=Gradient(false,(0,80),(.55,10),(1,0)),IsHitTestVisible=false};
        floor=new(){Background=Gradient(true,(0,20),(.45,20),(.7,150),(1,240)),IsHitTestVisible=false};
        Children.Add(image);Children.Add(shade);Children.Add(floor);Children.Add(text);Children.Add(toolbar);
        WC.Button Button(string label,Action action)
        {
            var b=WpfThemeResources.Button(label,null,action);b.Height=34;b.MinWidth=36;b.Margin=new(0,0,8,0);
            WpfThemeResources.SetButton(b,label,null,coverOverlay:true);toolbar.Children.Add(b);return b;
        }
        adjust=Button("调整位置",BeginAdjust);save=Button("保存位置",async()=>await CommitAsync());cancel=Button("取消",CancelAdjust);
        reset=Button("居中",()=>SetFrame(CoverFraming.Centered));smaller=Button("−",()=>SetFrame(frame with{Zoom=frame.Zoom-.15}));larger=Button("+",()=>SetFrame(frame with{Zoom=frame.Zoom+.15}));
        toolbar.Children.Add(edit);ShowTools();
        W.Automation.AutomationProperties.SetAutomationId(image,"GameHeroArtwork");
        W.Automation.AutomationProperties.SetAutomationId(text,"GameHeroTitleArea");
        W.Automation.AutomationProperties.SetAutomationId(adjust,"GameCoverPosition");
        W.Automation.AutomationProperties.SetAutomationId(save,"GameCoverPositionSave");
        W.Automation.AutomationProperties.SetName(smaller,UiLanguage.Text("缩小"));W.Automation.AutomationProperties.SetName(larger,UiLanguage.Text("放大"));
        WM.RenderOptions.SetBitmapScalingMode(image,WM.BitmapScalingMode.HighQuality);
    }
    private static WM.LinearGradientBrush Gradient(bool vertical,params(double Offset,byte Alpha)[] stops)
    {
        var b=new WM.LinearGradientBrush{StartPoint=new(0,0),EndPoint=vertical?new(0,1):new(1,0)};
        foreach(var s in stops)b.GradientStops.Add(new(WM.Color.FromArgb(s.Alpha,8,13,22),s.Offset));b.Freeze();return b;
    }
    internal void SetArtwork(Bitmap? bitmap,CoverFraming? value=null)
        =>SetArtworkSource(bitmap is null?null:WpfDrawing.Image(bitmap),value);
    internal void SetArtworkSource(WB.BitmapSource? bitmap,CoverFraming? value=null)
    {
        bool changed=!ReferenceEquals(current,bitmap);
        var normalized=(value??CoverFraming.Centered).Normalize();
        if(!changed&&committed==normalized)return;
        current=bitmap;
        if(changed){pixelWidth=bitmap?.PixelWidth??0;pixelHeight=bitmap?.PixelHeight??0;artwork.ImageSource=bitmap;editing=false;}
        committed=normalized;if(!editing)frame=committed;
        image.Visibility=shade.Visibility=floor.Visibility=bitmap is null?W.Visibility.Collapsed:W.Visibility.Visible;
        Background=bitmap is null?WM.Brushes.Transparent:WpfDrawing.Brush(Color.FromArgb(15,20,29));
        toolbar.Background=WM.Brushes.Transparent;
        ShowTools();InvalidateMeasure();UpdateCrop();
    }
    private void ShowTools()
    {
        adjust.Visibility=current is not null&&!editing?W.Visibility.Visible:W.Visibility.Collapsed;
        foreach(var b in new[]{save,cancel,reset,smaller,larger})b.Visibility=editing?W.Visibility.Visible:W.Visibility.Collapsed;
        edit.Visibility=editing?W.Visibility.Collapsed:W.Visibility.Visible;
        foreach(var pair in new[]{(adjust,"调整位置"),(save,"保存位置"),(cancel,"取消"),(reset,"居中")})
            WpfThemeResources.SetButton(pair.Item1,pair.Item2,null,coverOverlay:current is not null);
        Cursor=editing?WI.Cursors.SizeAll:WI.Cursors.Arrow;
        ToolTip=editing?UiLanguage.Text("拖动调整取景，方向键微调；保存后生效。"):null;
    }
    internal void BeginAdjust(){if(current is null)return;frame=committed;editing=true;ShowTools();Focus();InvalidateMeasure();}
    internal void CancelAdjust(){frame=committed;editing=false;dragging=null;ReleaseMouseCapture();ShowTools();UpdateCrop();InvalidateMeasure();}
    internal void SetFrame(CoverFraming value){frame=value.Normalize();UpdateCrop();}
    internal async Task CommitAsync()
    {
        if(!editing||saving)return;saving=true;toolbar.IsEnabled=false;
        try
        {
            if(SaveFrame is not null&&await SaveFrame(frame)){committed=frame;editing=false;ShowTools();InvalidateMeasure();}
        }
        finally{saving=false;toolbar.IsEnabled=true;}
    }
    internal void Pan(double x,double y)
    {if(current is not null)SetFrame(frame.Pan(x,y,ActualWidth,ActualHeight,pixelWidth,pixelHeight));}
    private void UpdateCrop()
    {
        if(current is null||arrangedSize.Width<1||arrangedSize.Height<1)return;
        var crop=frame.Crop(pixelWidth,pixelHeight,arrangedSize.Width/arrangedSize.Height);
        artwork.Stretch=WM.Stretch.Fill;artwork.ViewboxUnits=WM.BrushMappingMode.RelativeToBoundingBox;
        artwork.Viewbox=new(crop.X/pixelWidth,crop.Y/pixelHeight,crop.Width/pixelWidth,crop.Height/pixelHeight);
    }
    protected override W.Size MeasureOverride(W.Size available)
    {
        double w=double.IsFinite(available.Width)?Math.Max(1,available.Width):1100;
        toolbar.Measure(new(Math.Max(1,w-32),double.PositiveInfinity));
        text.Measure(new(Math.Max(1,w-(current is null?toolbar.DesiredSize.Width:0)-48),double.PositiveInfinity));
        double h=current is null?Math.Max(90,text.DesiredSize.Height+32):Math.Max(Math.Clamp(w/3.2,220,360),toolbar.DesiredSize.Height+text.DesiredSize.Height+60);
        foreach(var layer in new[]{image,shade,floor})layer.Measure(new(w,h));return new(w,h);
    }
    protected override W.Size ArrangeOverride(W.Size size)
    {
        // ActualHeight still describes the previous layout during ArrangeOverride.
        // Commit the crop and destination from the same layout, before the first paint.
        arrangedSize=size;UpdateCrop();
        var rect=new W.Rect(size);image.Arrange(rect);shade.Arrange(rect);floor.Arrange(rect);
        toolbar.Arrange(new(Math.Max(16,size.Width-toolbar.DesiredSize.Width-16),16,Math.Min(toolbar.DesiredSize.Width,size.Width-32),toolbar.DesiredSize.Height));
        double titleY=current is null?16:Math.Max(toolbar.DesiredSize.Height+28,size.Height-text.DesiredSize.Height-24);
        double offset=Math.Clamp((titleY-8)/size.Height,.05,.95);
        floor.Background=Gradient(true,(0,0),(Math.Max(0,offset-.28),0),(offset,210),(1,240));
        text.Arrange(new(24,titleY,Math.Max(1,size.Width-(current is null?toolbar.DesiredSize.Width:0)-48),text.DesiredSize.Height));
        var clip=new WM.RectangleGeometry(rect,8,8);clip.Freeze();Clip=clip;return size;
    }
    protected override void OnMouseLeftButtonDown(WI.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);if(!editing||saving||toolbar.IsMouseOver)return;
        dragging=e.GetPosition(this);Focus();CaptureMouse();e.Handled=true;
    }
    protected override void OnMouseMove(WI.MouseEventArgs e)
    {base.OnMouseMove(e);if(dragging is not {} p)return;var next=e.GetPosition(this);Pan(next.X-p.X,next.Y-p.Y);dragging=next;e.Handled=true;}
    protected override void OnMouseLeftButtonUp(WI.MouseButtonEventArgs e)
    {base.OnMouseLeftButtonUp(e);if(dragging is null)return;dragging=null;ReleaseMouseCapture();e.Handled=true;}
    protected override void OnLostMouseCapture(WI.MouseEventArgs e){base.OnLostMouseCapture(e);dragging=null;}
    protected override void OnKeyDown(WI.KeyEventArgs e)
    {
        base.OnKeyDown(e);if(!editing||saving)return;
        switch(e.Key){case WI.Key.Left:Pan(6,0);break;case WI.Key.Right:Pan(-6,0);break;case WI.Key.Up:Pan(0,6);break;case WI.Key.Down:Pan(0,-6);break;case WI.Key.Escape:CancelAdjust();break;default:return;}e.Handled=true;
    }
}
