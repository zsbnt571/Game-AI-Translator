namespace ScreenshotTranslationUiTester.CorePipelineV2;

// A straight material transition visible through multiple source-ink gaps can
// continue across glyph holes. No complete control is inferred from an endpoint.
internal static class GlyphBoundaryRecovery
{
    internal static int Apply(ReadOnlyBitmapPixelBuffer source,Bitmap target,
        SourceGlyphFamily.Evidence glyph,IEnumerable<SourceGlyphFamily.Evidence> allFamilies,bool[,] protection,CancellationToken token)
    {
        var scan=glyph.Scan;int w=scan.Width,h=scan.Height;
        var ink=new bool[w*h];
        foreach(var p in glyph.Core)for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
        {int x=p.X-scan.X+dx,y=p.Y-scan.Y+dy;if(x>=0&&y>=0&&x<w&&y<h)ink[y*w+x]=true;}
        var depthInk=(bool[])ink.Clone();
        foreach(var other in allFamilies)
            if(!ReferenceEquals(other,glyph))foreach(var p in other.Material)
                if(scan.Contains(p))depthInk[(p.Y-scan.Y)*w+p.X-scan.X]=true;
        int changed=0;
        // This observer handles a horizontal OCR run. Only the long axis has
        // independent gaps between several letters; a perpendicular candidate
        // can merely be the side of one outlined letter.
        for(int axis=0;axis<1;axis++)
        {
            int along=axis==0?w:h,across=axis==0?h:w;
            Point At(int a,int c)=>new(scan.X+(axis==0?a:c),scan.Y+(axis==0?c:a));
            bool Known(int a,int c){var p=At(a,c);return !ink[(p.Y-scan.Y)*w+p.X-scan.X]&&!protection[p.X,p.Y];}
            Color Read(int a,int c){var p=At(a,c);return source.GetPixel(p.X,p.Y);}
            (int Row,int Left,int Right,int Side,Color Color,int Score)? best=null;
            for(int c=5;c<across-5;c++)
            {
                token.ThrowIfCancellationRequested();
                var supports=new List<(int A,Color Before,Color After)>();
                for(int a=2;a<along-2;a+=2)
                {
                    if(!Known(a,c-3)||!Known(a,c+3)||!Known(a-2,c-3)||!Known(a+2,c+3))continue;
                    var before=Read(a,c-3);var after=Read(a,c+3);
                    if(Distance(before,after)<70||Distance(before,Read(a-2,c-3))>18||Distance(after,Read(a+2,c+3))>18)continue;
                    supports.Add((a,before,after));
                }
                int span=axis==0?glyph.InkBounds.Width:glyph.InkBounds.Height;
                if(supports.Count<Math.Max(12,span*.045f)||supports[^1].A-supports[0].A<span*.7f)continue;
                int center=axis==0?glyph.InkBounds.Left+glyph.InkBounds.Width/2-scan.Left:glyph.InkBounds.Top+glyph.InkBounds.Height/2-scan.Top;
                if(!supports.Any(s=>s.A<center-span*.2f)||!supports.Any(s=>s.A>center+span*.2f))continue;
                foreach(int side in new[]{-1,1})
                {
                    var colors=supports.Select(s=>side<0?s.Before:s.After).ToArray();
                    int M(Func<Color,int> f)=>colors.Select(f).Order().ElementAt(colors.Length/2);
                    var surface=Color.FromArgb(M(v=>v.R),M(v=>v.G),M(v=>v.B));
                    var consistent=supports.Where(s=>Distance(side<0?s.Before:s.After,surface)<=18).ToArray();
                    if(consistent.Length<supports.Count*.9f)continue;
                    // A second depth must confirm this material. A narrow rule
                    // alone is not evidence for a flat surface on either side.
                    int depth=Math.Clamp((axis==0?glyph.InkBounds.Height:glyph.InkBounds.Width)/3,8,30),good=0,total=0;
                    foreach(var s in consistent)
                    {
                        int cc=c+side*depth;if(cc<0||cc>=across||!Known(s.A,cc))continue;
                        total++;if(Distance(Read(s.A,cc),surface)<=22)good++;
                    }
                    if(total<8||good<total*.9f)continue;
                    int score=consistent.Length*(consistent[^1].A-consistent[0].A);
                    if(best is null||score>best.Value.Score)best=(c,consistent[0].A,consistent[^1].A,side,surface,score);
                }
            }
            if(best is not {} edge)continue;
            // A material observed at the edge is not permission to propagate
            // through an entire half-plane. Each destination depth needs its
            // own distributed, source-only witnesses between the same ends.
            // Stop at the first different or unobserved material, including an
            // inner decoration beyond an otherwise convincing outer border.
            var confirmedDepth=new bool[across];
            for(int c=edge.Row+edge.Side*3;c>=0&&c<across;c+=edge.Side)
            {
                int seen=0,matching=0,first=-1,last=-1;
                for(int a=edge.Left;a<=edge.Right;a+=2)
                {
                    var witness=At(a,c);
                    if(depthInk[(witness.Y-scan.Y)*w+witness.X-scan.X]||protection[witness.X,witness.Y])continue;
                    seen++;
                    if(Distance(Read(a,c),edge.Color)>22)continue;
                    matching++;if(first<0)first=a;last=a;
                }
                if(seen<8||matching<seen*.9f||last-first<(edge.Right-edge.Left)*.65f)break;
                confirmedDepth[c]=true;
            }
            // Continue only between observed supports; do not extrapolate a
            // flat band beyond the visible ends into a sloped outer edge.
            int extend=0;
            foreach(var p in glyph.Material)
            {
                int a=axis==0?p.X-scan.X:p.Y-scan.Y,c=axis==0?p.Y-scan.Y:p.X-scan.X;
                if(!confirmedDepth[c]||a<edge.Left-extend||a>edge.Right+extend||protection[p.X,p.Y])continue;
                target.SetPixel(p.X,p.Y,edge.Color);changed++;
            }
        }
        return changed;
    }
    private static int Distance(Color a,Color b)=>Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B)));
}
