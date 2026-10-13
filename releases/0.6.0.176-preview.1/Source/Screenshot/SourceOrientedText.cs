using System.Drawing.Drawing2D;
namespace ScreenshotTranslationUiTester.CorePipelineV2;

// The source quadrilateral supplies the baseline and actual letter height.
// This is a text placement only; it grants no additional cleanup authority.
internal static class SourceOrientedText
{
    internal sealed record Evidence(float Angle,float Width,float Height,PointF Center);
    internal static Evidence? Observe(VisualBlock b)
    {
        if(b.Lines.Count!=1||b.SourceText.Length>24||b.Lines[0].Polygon.Length!=4)return null;
        var p=b.Lines[0].Polygon;
        var ux=p[1].X-p[0].X;var uy=p[1].Y-p[0].Y;
        var vx=p[3].X-p[0].X;var vy=p[3].Y-p[0].Y;
        var w=MathF.Sqrt(ux*ux+uy*uy);var h=MathF.Sqrt(vx*vx+vy*vy);
        var angle=MathF.Atan2(uy,ux)*180/MathF.PI;
        if(w<12||h<12||Math.Abs(angle)<7||Math.Abs(angle)>80||Math.Abs((ux*vx+uy*vy)/(w*h))>.12f)return null;
        if(Math.Abs(p[2].X-p[1].X-vx)>h*.1f||Math.Abs(p[2].Y-p[1].Y-vy)>h*.1f)return null;
        return new(angle,w,h,new(p.Average(q=>q.X),p.Average(q=>q.Y)));
    }
    internal static FinalTextPlacement.Plan? Plan(Graphics g,VisualBlock b,string text,FinalTextPlacement.Paint paint,Size canvas)
    {
        var e=Observe(b);if(e is null)return null;
        var content=text.Replace("\r","").Trim();
        if(content.Contains('\n'))return null;
        // Fit ink, not font em or the rotated axis-aligned OCR rectangle.
        using var probe=FontManager.CreatePixel(paint.Family,100,paint.Weight,true);
        using var path=new GraphicsPath();path.AddString(content,probe.FontFamily,(int)probe.Style,probe.Size,PointF.Empty,StringFormat.GenericTypographic);
        if(path.PointCount==0)return null;var ink=path.GetBounds();
        var size=Math.Min(e.Height*.78f/Math.Max(1,ink.Height),e.Width*.90f/Math.Max(1,ink.Width))*100;
        if(size<8||size>512)return null;
        var actual=paint with{Size=size,SourcePixelScale=true,RotationDegrees=e.Angle,RotationOrigin=e.Center};
        using var font=FontManager.CreatePixel(actual.Family,size,actual.Weight,true);
        using var measured=new GraphicsPath();measured.AddString(content,font.FontFamily,(int)font.Style,font.Size,PointF.Empty,StringFormat.GenericTypographic);
        var rect=measured.GetBounds();var origin=new PointF(e.Center.X-rect.Width/2-rect.X,e.Center.Y-rect.Height/2-rect.Y);
        // A generous axis-aligned box would allow crossing the tilted boundary.
        // Validate the unrotated painted extent in the narrower local corridor.
        var margin=(actual.Outline?Math.Max(actual.OutlineWidth,actual.OutlineWidth+actual.Glow*2):0)+
            (actual.Shadow?Math.Max(Math.Abs(actual.ShadowOffset.X),Math.Abs(actual.ShadowOffset.Y)):0)+2;
        if(rect.Width+margin*2>e.Width||rect.Height+margin*2>e.Height)return null;
        return FinalTextPlacement.Freeze(g,b.BlockId,text,[new(content,new(origin,new SizeF(rect.Width,rect.Height)))],
            b.Bounds,[],actual,canvas);
    }
}
