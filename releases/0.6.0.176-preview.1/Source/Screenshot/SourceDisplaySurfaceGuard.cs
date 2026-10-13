using System.Drawing;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

/// <summary>A cleanup safety check, never a semantic logo/translation classifier.</summary>
internal static class SourceDisplaySurfaceGuard
{
    internal sealed record Evidence(bool Rejected,float CoveredFraction,int SourceRingSpread,string Reason);
    internal const string Failure="UNSAFE_COMPLEX_DISPLAY_ERASURE_SOURCE_PRESERVED";

    internal static Evidence Observe(ReadOnlyBitmapPixelBuffer source,VisualBlock block,bool[,] authority)
    {
        Evidence Pass(float fraction=0,int spread=0)=>new(false,fraction,spread,"DISPLAY_ERASURE_GUARD_NOT_TRIGGERED");
        // Paragraphs, source cells and small labels keep their established recovery.
        // Size only bounds this check; it never identifies artwork or skips translation.
        if(block.SourceRole?.Role is "Prose" or "Control" || block.SourceCell is not null ||
            block.Lines.Count!=1 || block.SourceText.Length>80 ||
            ParagraphAlignmentEvidence.ObserveShortSentence(block) is not null)return Pass();
        var rect=Rectangle.Intersect(new(0,0,source.Width,source.Height),Rectangle.Ceiling(block.Bounds));
        if(rect.Height<Math.Max(48,source.Height*.045f)||rect.Width<rect.Height*4)return Pass();
        long total=0,covered=0;
        for(var y=rect.Top;y<rect.Bottom;y++)for(var x=rect.Left;x<rect.Right;x++)
        {total++;if(authority[x,y])covered++;}
        var fraction=total==0?0:covered/(float)total;
        if(fraction<.9f)return Pass(fraction);
        var channels=new[]{new List<byte>(),new List<byte>(),new List<byte>()};
        var margin=Math.Clamp((int)Math.Ceiling(rect.Height*.03f),2,8);
        // Bounded source-only samples above and below the OCR run. A broad opaque
        // erase spanning incompatible materials has no proven glyph-only authority.
        for(var x=rect.Left;x<rect.Right;x+=Math.Max(1,rect.Width/128))
        foreach(var y in new[]{rect.Top-margin,rect.Top-1,rect.Bottom,rect.Bottom+margin-1})
        {
            if(y<0||y>=source.Height)continue;
            var c=source.GetPixel(x,y);channels[0].Add(c.R);channels[1].Add(c.G);channels[2].Add(c.B);
        }
        var spread=0;
        foreach(var values in channels)
        {
            if(values.Count<8)continue;values.Sort();
            spread=Math.Max(spread,values[(int)((values.Count-1)*.9)]-values[(int)((values.Count-1)*.1)]);
        }
        return spread>=96?new(true,fraction,spread,Failure):Pass(fraction,spread);
    }
}
