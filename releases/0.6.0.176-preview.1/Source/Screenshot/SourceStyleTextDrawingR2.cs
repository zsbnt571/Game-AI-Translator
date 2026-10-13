using System.Drawing.Drawing2D;

namespace ScreenshotTranslationUiTester;

internal static class SourceStyleTextDrawingR2
{
    internal static void Draw(Graphics graphics,string text,Font font,PointF origin,Color foreground,
        bool outline,Color outlineColor,float outlineWidth,bool shadow,Color shadowColor)
        =>Draw(graphics,text,font,origin,foreground,outline,outlineColor,outlineWidth,shadow,shadowColor,
            new PointF(Math.Max(1,outlineWidth*.7f),Math.Max(1,outlineWidth*.8f)),0,255);

    internal static void Draw(Graphics graphics,string text,Font font,PointF origin,Color foreground,
        bool outline,Color outlineColor,float outlineWidth,bool shadow,Color shadowColor,PointF shadowOffset,float glow,byte alpha)
    {
        if(!outline&&!shadow)
        {
            using var brush=new SolidBrush(Color.FromArgb(alpha,foreground));
            graphics.DrawString(text,font,brush,origin,StringFormat.GenericTypographic);
            return;
        }
        using var path=new GraphicsPath();
        path.AddString(text,font.FontFamily,(int)font.Style,font.Size,origin,StringFormat.GenericTypographic);
        var old=graphics.SmoothingMode;graphics.SmoothingMode=SmoothingMode.AntiAlias;
        if(shadow)
        {
            using var shadowPath=(GraphicsPath)path.Clone();
            using var matrix=new Matrix();matrix.Translate(shadowOffset.X,shadowOffset.Y);
            shadowPath.Transform(matrix);using var shadowBrush=new SolidBrush(shadowColor);
            graphics.FillPath(shadowBrush,shadowPath);
        }
        if(outline&&outlineWidth>0)
        {
            if(glow>0)
            {
                using var glowPen=new Pen(Color.FromArgb(Math.Clamp((int)(90*glow),15,110),outlineColor),Math.Clamp(outlineWidth+glow*2,1,8)){LineJoin=LineJoin.Round};
                graphics.DrawPath(glowPen,path);
            }
            using var pen=new Pen(outlineColor,Math.Clamp(outlineWidth,.5f,6)){LineJoin=LineJoin.Round};
            graphics.DrawPath(pen,path);
        }
        using var brush2=new SolidBrush(Color.FromArgb(alpha,foreground));graphics.FillPath(brush2,path);graphics.SmoothingMode=old;
    }
}
