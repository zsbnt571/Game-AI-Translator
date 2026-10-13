namespace ScreenshotTranslationUiTester.CorePipelineV2;

/// <summary>Placement evidence from an observed curved glyph corridor; never an erase rectangle.</summary>
internal static class SourceCurvedText
{
    internal sealed record Evidence(RectangleF Bounds,float GlyphHeight,float CenterY);
    internal static Evidence? Observe(VisualBlock block)
    {
        if(block.Lines.Count!=1)return null;
        var polygon=block.Lines[0].Polygon;
        if(polygon.Length<12||polygon.Length%2!=0)return null;
        var half=polygon.Length/2;var upper=polygon.Take(half).ToArray();
        var lower=polygon.Skip(half).Reverse().ToArray();
        // Adjacent observed glyphs each have one source pixel of padding;
        // their corridor endpoints can overlap by two pixels without reversing order.
        if(upper.Zip(upper.Skip(1),(a,b)=>b.X-a.X).Any(dx=>dx< -2))return null;
        if(upper.Zip(lower,(a,b)=>Math.Abs(a.X-b.X)>1||b.Y<=a.Y).Any(v=>v))return null;
        var heights=upper.Zip(lower,(a,b)=>b.Y-a.Y).Order().ToArray();
        var height=heights[half/2];
        if(height<12||block.Bounds.Width<height*4||block.Bounds.Height>height*3)return null;
        var centers=upper.Zip(lower,(a,b)=>(a.Y+b.Y)/2).ToArray();
        var edge=(centers[0]+centers[^1])/2;var middle=centers.Skip(half/3).Take(Math.Max(2,half/3)).Average();
        if(Math.Abs(edge-middle)<height*.2f||Math.Abs(centers[0]-centers[^1])>height)return null;
        return new(block.Bounds,height,centers.Order().ElementAt(half/2));
    }
}
