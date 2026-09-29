namespace ScreenshotTranslationUiTester.CorePipelineV2;

// Opposed source witnesses on the text row constrain local repair. The scan's
// exterior colour cannot become a donor merely because it is nearby vertically.
internal static class GlyphRowBackground
{
    internal static int Apply(ReadOnlyBitmapPixelBuffer source,Bitmap target,
        SourceGlyphFamily.Evidence glyph,IEnumerable<SourceGlyphFamily.Evidence> families,
        bool[,] protection,CancellationToken token)
    {
        var b=glyph.InkBounds;var s=glyph.Scan;
        if(b.Height<48||b.Width<b.Height*4)return 0;
        int w=s.Width,h=s.Height;var mask=new bool[w*h];var excluded=new bool[w*h];var core=new bool[w*h];
        foreach(var p in glyph.Material)mask[(p.Y-s.Y)*w+p.X-s.X]=true;
        foreach(var p in glyph.Core)core[(p.Y-s.Y)*w+p.X-s.X]=true;
        foreach(var other in families)foreach(var p in other.Material)
            if(s.Contains(p))excluded[(p.Y-s.Y)*w+p.X-s.X]=true;
        int changed=0;var writes=new List<(int X,int Y,Color Color)>();
        for(int y=0;y<h;y++)
        {
            token.ThrowIfCancellationRequested();
            for(int x=0;x<w;x++)
            {
                if(!mask[y*w+x])continue;
                int start=x;while(x<w&&mask[y*w+x])x++;int end=x;
                int left=start-1,right=end;
                bool Known(int a)=>a>=0&&a<w&&!excluded[y*w+a]&&!protection[s.X+a,s.Y+y];
                while(left>=0&&!Known(left))left--;
                while(right<w&&!Known(right))right++;
                if(left<1||right>=w-1||start-left>b.Height*.25f||right-end>b.Height*.25f)continue;
                var c0=source.GetPixel(s.X+left,s.Y+y);var c1=source.GetPixel(s.X+right,s.Y+y);
                if(Distance(c0,c1)>48||Distance(c0,glyph.Fill)<48||Distance(c1,glyph.Fill)<48)continue;
                Color At(int a){float t=(a-left)/(float)(right-left);return Color.FromArgb((int)Math.Round(c0.R+(c1.R-c0.R)*t),(int)Math.Round(c0.G+(c1.G-c0.G)*t),(int)Math.Round(c0.B+(c1.B-c0.B)*t));}
                // Counter gaps inside a long erased span provide independent
                // counter-evidence: reject a visible second background material.
                int witnesses=0,bad=0;
                for(int a=start;a<end;a+=2)
                {
                    int i=y*w+a;if(core[i]||a==0||a+1>=w||core[i-1]||core[i+1])continue;
                    var c=source.GetPixel(s.X+a,s.Y+y);if(Distance(c,glyph.Fill)<48)continue;
                    witnesses++;
                    var background=At(a);
                    // Antialiasing and a coloured glow are mixtures of the
                    // witnessed background and the source letter body. They
                    // are source text material, not evidence of another panel.
                    if(Distance(c,background)>48&&!IsInkBlend(c,background,glyph.Fill))bad++;
                }
                if(end-start>b.Height*1.5f&&(witnesses<8||bad>witnesses*.1f))continue;
                for(int a=start;a<end;a++)
                    if(!protection[s.X+a,s.Y+y]){writes.Add((s.X+a,s.Y+y,At(a)));changed++;}
            }
        }
        // Commit a coherent owner or keep the mode's original reconstruction.
        // Mixing isolated successful rows with unrelated scenery creates bands.
        if(changed!=glyph.Material.Count(p=>!protection[p.X,p.Y]))return 0;
        token.ThrowIfCancellationRequested();
        foreach(var p in writes)target.SetPixel(p.X,p.Y,p.Color);
        return changed;
    }
    private static int Distance(Color a,Color b)=>Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B)));
    private static bool IsInkBlend(Color c,Color b,Color f)
    {
        double r=f.R-b.R,g=f.G-b.G,blue=f.B-b.B,d=r*r+g*g+blue*blue;
        if(d<1)return false;
        double t=((c.R-b.R)*r+(c.G-b.G)*g+(c.B-b.B)*blue)/d;
        return t is >=0 and <=1&&Math.Max(Math.Abs(c.R-b.R-t*r),Math.Max(Math.Abs(c.G-b.G-t*g),Math.Abs(c.B-b.B-t*blue)))<=18;
    }
}
