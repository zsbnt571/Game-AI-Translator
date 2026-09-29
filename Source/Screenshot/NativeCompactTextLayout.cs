using System.Drawing.Drawing2D;
namespace ScreenshotTranslationUiTester.CorePipelineV2;

/// <summary>Readable single-row labels on a verified local source surface.
/// This supplies text geometry only and never expands a cleanup domain.</summary>
internal static class NativeCompactTextLayout
{
    internal static NativeVisualLayoutPlan Plan(Graphics g,Bitmap source,VisualBlock block,
        IReadOnlyList<VisualBlock> blocks,string text,string family,FontStyle weight,float preferred,
        string alignment,SourceStyleBundle style,ReadOnlyBitmapPixelBuffer? pixels=null,bool sourceLeftAnchor=false)
    {
        var b=block.Bounds;
        NativeVisualLayoutPlan No(string reason,bool candidate=false)=>new(candidate,false,reason,
            "CompactSourceLabel",b,b,b,preferred,preferred,0,0,0,alignment,"SourceTop",[],0,true,true)
            {LayoutInput=text};
        if(block.Lines.Count!=1 || block.RoleHint=="PossibleTitle" || b.Height<8 || b.Height>source.Height*.06f ||
            b.Width>source.Width*.36f || b.Height>b.Width*.8f || preferred>=30 ||
            style.Confidence<.55f || !text.Any(c=>c is >= '\u3400' and <= '\u9fff') ||
            text.Length>64 || text.Contains('\n') || text.Contains('\\') ||
            block.SourceText.Contains('\\'))return No("NOT_COMPACT_SOURCE_LABEL");
        var other=blocks.Where(x=>x.BlockId!=block.BlockId).Select(x=>RectangleF.Inflate(x.Bounds,2,2)).ToArray();
        using var ownedPixels=pixels is null?ReadOnlyBitmapPixelBuffer.Create(source):null;
        var material=SourceControlMaterial.Observe(pixels??ownedPixels!,Rectangle.Round(b));
        var limit=material?.Bounds is { } control ? (RectangleF)control :
            RectangleF.Intersect(RectangleF.Inflate(b,Math.Min(b.Width*.5f,b.Height*3),b.Height*.55f),
                new RectangleF(PointF.Empty,source.Size));
        foreach(var neighbor in other)
        {
            if(neighbor.Bottom<=b.Top && neighbor.Right>b.Left && neighbor.Left<b.Right)
                limit=RectangleF.FromLTRB(limit.Left,Math.Max(limit.Top,neighbor.Bottom),limit.Right,limit.Bottom);
            if(neighbor.Top>=b.Bottom && neighbor.Right>b.Left && neighbor.Left<b.Right)
                limit=RectangleF.FromLTRB(limit.Left,limit.Top,limit.Right,Math.Min(limit.Bottom,neighbor.Top));
        }
        var surface=new NativeCardLayout.Surface(source,block,style,other);
        var desired=Math.Max(preferred,Math.Min(30,b.Height*1.06f));
        var floor=Math.Max(preferred,desired*.86f);
        var centered=!sourceLeftAnchor && (material is not null || alignment.Equals("Center",StringComparison.OrdinalIgnoreCase));
        var target=centered?(material is null?b.Left+b.Width/2:limit.Left+limit.Width/2):b.Left;
        if(material is null)
        {
            // A single OCR line cannot prove extra left-side space belongs to it.
            // Keep the observed center when applicable; expansion is right-only
            // for source-left labels, and bounded to the source width for center.
            limit=RectangleF.FromLTRB(b.Left,limit.Top,centered?b.Right:limit.Right,limit.Bottom);
        }
        for(var size=desired;size>=floor-.01f;size-=.5f)
        {
            using var font=FontManager.CreatePixel(family,size,weight);
            using var path=new GraphicsPath();
            path.AddString(text,font.FontFamily,(int)font.Style,size,PointF.Empty,StringFormat.GenericTypographic);
            var ink=path.GetBounds();if(ink.IsEmpty)continue;
            var top=material is null?b.Top:limit.Top+(limit.Height-ink.Height)/2;
            if(top<limit.Top+1 || top+ink.Height>limit.Bottom-1)continue;
            var span=surface.Span(limit.Left,limit.Right,top,ink.Height+2,target,centered);
            var advance=g.MeasureString(text,font,PointF.Empty,StringFormat.GenericTypographic).Width;
            if(span.Width<advance+2)continue;
            var x=centered?target-advance/2:Math.Max(target,span.Left+1-ink.Left);
            var origin=new PointF(x,top+1-ink.Top);
            var actual=new RectangleF(origin.X+ink.Left,top+1,ink.Width,ink.Height);
            if(!surface.Safe(RectangleF.Inflate(actual,1,1)))continue;
            var line=new NativeVisualLine(text,new(origin.X,origin.Y,advance,font.GetHeight(g))){AdvanceWidth=advance};
            return new(true,true,sourceLeftAnchor?"READABLE_SOURCE_LABEL_COLUMN_LEFT":
                material is null?"READABLE_SOURCE_LABEL_ANCHOR":"READABLE_VERIFIED_CONTROL_CENTER",
                "CompactSourceLabel",b,line.Bounds,limit,desired,size,font.GetHeight(g),advance,line.Bounds.Height,
                centered?"Center":"Left",material is null?"SourceTop":"ControlCenter",[line],surface.CheckedPixels,true,true)
                {LayoutInput=text,SafeLineRects=[span],BreakPolicy="IndependentSourceRow"};
        }
        return No("NO_LARGER_SAFE_LABEL_FIT",true);
    }
}
