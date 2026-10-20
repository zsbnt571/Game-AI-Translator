using W = System.Windows;
using WI = System.Windows.Input;
using WM = System.Windows.Media;

namespace ScreenshotTranslationUiTester;

internal sealed class NativeArtworkCrop : W.FrameworkElement, IDisposable
{
    private Bitmap? image;
    private WM.ImageSource? source;
    private double aspect = 16d / 9, zoom = 1, centerX = .5, centerY = .5;
    private W.Point? dragging;
    internal bool ShowOverview {get;set;}
    internal W.FrameworkElement BannerPreview {get;}
    private void Redraw(){InvalidateVisual();BannerPreview.InvalidateVisual();}
    internal double Aspect { get => aspect; set { aspect = value; Redraw(); } }
    internal bool HasImage => image is not null;
    internal CoverFraming Framing { get => new(centerX,centerY,zoom); set {var v=value.Normalize();centerX=v.X;centerY=v.Y;zoom=v.Zoom;Redraw();FramingChanged?.Invoke();} }
    internal Bitmap Original(int maxSide=0)=>image is null?throw new InvalidOperationException("请先选择图片。"):maxSide>0?GameWindowCover.Thumbnail(image,maxSide,maxSide):new Bitmap(image);
    internal Size PixelSize=>image?.Size??Size.Empty;
    internal event Action? FramingChanged;
    internal void Center()=>Framing=CoverFraming.Centered;
    internal void Pan(double dx,double dy)
    {
        if(image is null||aspect==0)return;
        var crop=CropRectangle();double scale=ShowOverview?OverviewBounds().Width/image.Width:Math.Min(Math.Max(1,ActualWidth-24)/crop.Width,Math.Max(1,ActualHeight-24)/crop.Height);
        double direction=ShowOverview?1:-1;
        centerX=Math.Clamp((crop.X+crop.Width/2+direction*dx/scale)/image.Width,0,1);centerY=Math.Clamp((crop.Y+crop.Height/2+direction*dy/scale)/image.Height,0,1);Redraw();FramingChanged?.Invoke();
    }
    internal NativeArtworkCrop() { BannerPreview=new ResultView(this);MinHeight = 240; Focusable = true; ClipToBounds = true; Cursor = WI.Cursors.Hand; }
    internal void SetImage(Bitmap? value) { var previous = image; image = value; source = value is null ? null : WpfDrawing.Image(value); previous?.Dispose(); zoom = 1; centerX = centerY = .5; Redraw(); }
    internal RectangleF CropRectangle()
    {
        if (image is null) return RectangleF.Empty;
        if(aspect==0)return new(0,0,image.Width,image.Height);
        double ratio = aspect;
        double width = image.Width, height = width / ratio;
        if (height > image.Height) { height = image.Height; width = height * ratio; }
        width /= zoom; height /= zoom;
        return new((float)Math.Clamp(image.Width * centerX - width / 2, 0, image.Width - width), (float)Math.Clamp(image.Height * centerY - height / 2, 0, image.Height - height), (float)width, (float)height);
    }
    internal Bitmap Export(int maxSide)
    {
        if (image is null) throw new InvalidOperationException("请先选择图片。");
        var crop = CropRectangle(); var scale = Math.Min(1f, maxSide / Math.Max(crop.Width, crop.Height));
        var result = new Bitmap(Math.Max(1, (int)(crop.Width * scale)), Math.Max(1, (int)(crop.Height * scale)));
        using var drawing = Graphics.FromImage(result); drawing.Clear(UiTheme.Current.Secondary); drawing.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; drawing.DrawImage(image, new Rectangle(0, 0, result.Width, result.Height), crop, GraphicsUnit.Pixel); return result;
    }
    internal void Zoom(double delta) { zoom = Math.Clamp(zoom + delta, 1, 5); Redraw(); FramingChanged?.Invoke(); }
    internal W.Rect OverviewBounds()
    {
        if(image is null)return W.Rect.Empty;
        double scale=Math.Min(Math.Max(1,ActualWidth-24)/image.Width,Math.Max(1,ActualHeight-24)/image.Height);
        return new((ActualWidth-image.Width*scale)/2,(ActualHeight-image.Height*scale)/2,image.Width*scale,image.Height*scale);
    }
    private void DrawCrop(WM.DrawingContext dc,W.Size size,W.Rect crop)
    {
        if(image is null||source is null)return;
        double scale=Math.Min(Math.Max(1,size.Width-24)/crop.Width,Math.Max(1,size.Height-24)/crop.Height);
        var dest=new W.Rect((size.Width-crop.Width*scale)/2,(size.Height-crop.Height*scale)/2,crop.Width*scale,crop.Height*scale);
        dc.PushClip(new WM.RectangleGeometry(dest));dc.DrawImage(source,new(dest.Left-crop.Left*scale,dest.Top-crop.Top*scale,image.Width*scale,image.Height*scale));dc.Pop();
    }
    protected override void OnRender(WM.DrawingContext dc)
    {
        dc.DrawRectangle(WpfDrawing.Brush(UiTheme.Current.Control), null, new(0, 0, ActualWidth, ActualHeight));
        if (image is null || source is null) return;
        if(ShowOverview)
        {
            var bounds=OverviewBounds();dc.DrawImage(source,bounds);
            if(aspect>0)
            {
                var selected=CropRectangle();double overviewScale=bounds.Width/image.Width;
                var frame=new W.Rect(bounds.Left+selected.X*overviewScale,bounds.Top+selected.Y*overviewScale,selected.Width*overviewScale,selected.Height*overviewScale);
                var outside=new WM.CombinedGeometry(WM.GeometryCombineMode.Exclude,new WM.RectangleGeometry(bounds),new WM.RectangleGeometry(frame));
                dc.DrawGeometry(new WM.SolidColorBrush(WM.Color.FromArgb(65,0,0,0)),null,outside);
                dc.DrawRectangle(null,new(WM.Brushes.White,3),frame);dc.DrawRectangle(null,new(WpfDrawing.Brush(UiTheme.Current.Accent),1),frame);
            }
            return;
        }
        var crop = CropRectangle(); double scale = Math.Min(Math.Max(1, ActualWidth - 24) / crop.Width, Math.Max(1, ActualHeight - 24) / crop.Height);
        var dest = new W.Rect((ActualWidth - crop.Width * scale) / 2, (ActualHeight - crop.Height * scale) / 2, crop.Width * scale, crop.Height * scale);
        dc.PushClip(new WM.RectangleGeometry(dest)); dc.DrawImage(source, new(dest.Left - crop.Left * scale, dest.Top - crop.Top * scale, image.Width * scale, image.Height * scale)); dc.Pop();
        dc.DrawRectangle(null, new(WpfDrawing.Brush(UiTheme.Current.Accent), 1), dest);
    }
    protected override void OnMouseLeftButtonDown(WI.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);Focus();dragging=e.GetPosition(this);
        if(ShowOverview&&aspect>0&&OverviewBounds() is var bounds&&bounds.Contains(dragging.Value))
        {
            var crop=CropRectangle();double scale=bounds.Width/image!.Width;
            var frame=new W.Rect(bounds.Left+crop.X*scale,bounds.Top+crop.Y*scale,crop.Width*scale,crop.Height*scale);
            if(!frame.Contains(dragging.Value))Framing=new((dragging.Value.X-bounds.Left)/bounds.Width,(dragging.Value.Y-bounds.Top)/bounds.Height,zoom);
        }
        CaptureMouse();e.Handled=true;
    }
    protected override void OnMouseMove(WI.MouseEventArgs e)
    {
        base.OnMouseMove(e); if (dragging is not { } previous || image is null) return;
        var point = e.GetPosition(this); var crop = CropRectangle(); double scale = Math.Min(Math.Max(1, ActualWidth - 24) / crop.Width, Math.Max(1, ActualHeight - 24) / crop.Height);
        Pan(point.X-previous.X,point.Y-previous.Y); dragging = point;
    }
    protected override void OnMouseLeftButtonUp(WI.MouseButtonEventArgs e) { base.OnMouseLeftButtonUp(e); dragging = null; ReleaseMouseCapture(); e.Handled = true; }
    protected override void OnLostMouseCapture(WI.MouseEventArgs e) { base.OnLostMouseCapture(e); dragging = null; }
    protected override void OnMouseWheel(WI.MouseWheelEventArgs e) { Zoom(e.Delta / 120d * .15); e.Handled = true; }
    protected override void OnKeyDown(WI.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        double step=ShowOverview?-6:6;
        switch(e.Key){case WI.Key.Left:Pan(step,0);break;case WI.Key.Right:Pan(-step,0);break;case WI.Key.Up:Pan(0,step);break;case WI.Key.Down:Pan(0,-step);break;default:return;}e.Handled=true;
    }
    private sealed class ResultView(NativeArtworkCrop owner):W.FrameworkElement
    {
        protected override W.Size MeasureOverride(W.Size available)
        {double width=double.IsFinite(available.Width)?Math.Max(1,available.Width):400;return new(width,(Math.Max(1,width-24)/3.2)+24);}
        protected override void OnRender(WM.DrawingContext dc)
        {
            dc.DrawRectangle(WpfDrawing.Brush(UiTheme.Current.Control),null,new(0,0,ActualWidth,ActualHeight));
            if(owner.image is {} bitmap)owner.DrawCrop(dc,RenderSize,owner.Framing.Crop(bitmap.Width,bitmap.Height,3.2));
        }
    }
    public void Dispose() { image?.Dispose(); image = null; source = null; }
}
