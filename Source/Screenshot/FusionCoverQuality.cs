using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ScreenshotTranslationUiTester;

internal sealed record CoverQualityResult(bool Usable,string Code,int Width,int Height,int Edges=0,int Tiles=0,int Peak=0)
{
    internal string Diagnostic=>$"quality={CoverQuality.RuleVersion} reason={Code} image={Width}x{Height} edges={Edges} tiles={Tiles} peak={Peak}";
}

// The same policy is compiled into the desktop and the isolated capture worker.
// Operates on visible RGB, not alpha variation or a successful decoder return.
internal static class CoverQuality
{
    internal const string RuleVersion="visible-rgb-v1";
    internal const int MaxWidth=640,MaxHeight=360;
    private const int EdgeDelta=6,MinimumEdges=24,MinimumTiles=2,EdgesPerTile=3;

    internal static Bitmap VisibleThumbnail(Image image)
    {
        var scale=Math.Min(1d,Math.Min((double)MaxWidth/image.Width,(double)MaxHeight/image.Height));
        var result=new Bitmap(Math.Max(1,(int)(image.Width*scale)),Math.Max(1,(int)(image.Height*scale)),PixelFormat.Format24bppRgb);
        try{
            using var graphics=Graphics.FromImage(result);
            graphics.Clear(Color.Black);
            graphics.CompositingMode=CompositingMode.SourceOver;
            graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(image,new Rectangle(Point.Empty,result.Size));
            return result;
        }catch{result.Dispose();throw;}
    }

    internal static CoverQualityResult Analyze(Image image)
    {
        if(image.Width<32||image.Height<18||image.Width>1280||image.Height>1280)
            return new(false,"invalid-size",image.Width,image.Height);
        using var visible=VisibleThumbnail(image);
        var w=visible.Width;var h=visible.Height;
        var pixels=new byte[w*h*3];
        var bits=visible.LockBits(new Rectangle(0,0,w,h),ImageLockMode.ReadOnly,PixelFormat.Format24bppRgb);
        try{for(var y=0;y<h;y++)Marshal.Copy(bits.Scan0+y*bits.Stride,pixels,y*w*3,w*3);}
        finally{visible.UnlockBits(bits);}
        // The outer 1% cannot alone prove content (window outlines / resize padding).
        var insetX=Math.Max(1,w/100);var insetY=Math.Max(1,h/100);
        var edges=0;var peak=0;var tiles=new int[32];
        for(var y=insetY;y<h-insetY;y++)for(var x=insetX;x<w-insetX;x++)
        {
            var p=(y*w+x)*3;var delta=0;
            for(var c=0;c<3;c++)
            {
                peak=Math.Max(peak,pixels[p+c]);
                delta=Math.Max(delta,Math.Abs(pixels[p+c]-pixels[p-3+c]));
                delta=Math.Max(delta,Math.Abs(pixels[p+c]-pixels[p-w*3+c]));
            }
            if(delta<EdgeDelta)continue;
            edges++;tiles[Math.Min(3,y*4/h)*8+Math.Min(7,x*8/w)]++;
        }
        var active=tiles.Count(n=>n>=EdgesPerTile);
        var required=Math.Max(MinimumEdges,(w-2*insetX)*(h-2*insetY)/4000);
        var usable=edges>=required&&active>=MinimumTiles;
        return new(usable,usable?"content":"no-visible-detail",w,h,edges,active,peak);
    }
}
