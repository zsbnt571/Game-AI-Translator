namespace ScreenshotTranslationUiTester.CorePipelineV2;

/// <summary>Repeated colored glyphs on a locally plain surface, independent of an adjacent icon.</summary>
internal sealed record SourceGlyphRowEvidence(Rectangle Bounds,Rectangle GlyphBounds,Color Fill,
    Color Background,IReadOnlyList<Point> Pixels,int GlyphCount,float PlainSurfaceFraction)
{
    internal static SourceGlyphRowEvidence? Observe(ReadOnlyBitmapPixelBuffer source,NormalizedOcrLine line)
    {
        var b=Rectangle.Intersect(Rectangle.Round(line.Bounds),new Rectangle(Point.Empty,source.Size));
        var letters=line.SourceText.Count(char.IsAsciiLetter);
        if(letters<4 || letters>28 || line.SourceText.Any(c=>!(char.IsAsciiLetterOrDigit(c)||char.IsWhiteSpace(c)||"+-?!'’".Contains(c))) ||
            b.Height<10 || b.Height>source.Height*.09f || b.Width<b.Height*2 || b.Width>source.Width*.36f)return null;
        var colors=new Color[b.Width*b.Height];
        for(var y=0;y<b.Height;y++)for(var x=0;x<b.Width;x++)colors[y*b.Width+x]=source.GetPixel(b.X+x,b.Y+y);
        var groups=colors.GroupBy(c=>(c.R/16,c.G/16,c.B/16)).OrderByDescending(g=>g.Count()).Take(12).ToArray();
        Color Median(Color[] a){int M(Func<Color,int> f)=>a.Select(f).Order().ElementAt(a.Length/2);return Color.FromArgb(M(c=>c.R),M(c=>c.G),M(c=>c.B));}
        var background=Median(groups[0].ToArray());
        var plain=colors.Count(c=>Distance(c,background)<=18)/(float)colors.Length;
        if(plain<.5f)return null;
        // A frequent color alone does not prove a flat source. Require stable
        // exposed-material medians in each horizontal and vertical third.
        var medians=new List<Color>();
        for(var axis=0;axis<2;axis++)for(var band=0;band<3;band++)
        {
            var samples=colors.Where((c,i)=>Distance(c,background)<=18 &&
                (axis==0?i%b.Width:i/b.Width)*3>=(axis==0?b.Width:b.Height)*band &&
                (axis==0?i%b.Width:i/b.Width)*3<(axis==0?b.Width:b.Height)*(band+1)).ToArray();
            if(samples.Length<12)return null;
            medians.Add(Median(samples));
        }
        if(medians.Any(a=>medians.Any(c=>Distance(a,c)>6)))return null;
        SourceGlyphRowEvidence? best=null;
        foreach(var group in groups.Skip(1))
        {
            var fill=Median(group.ToArray());
            var channelDelta=new[]{fill.R-background.R,fill.G-background.G,fill.B-background.B};
            if(Distance(fill,background)<55 || channelDelta.Max()-channelDelta.Min()<40 ||
                TranslationTextColorResolver.Contrast(fill,background)<3)continue;
            var near=colors.Select(c=>Distance(c,fill)<=24).ToArray();
            var components=Components(near,b.Width,b.Height);
            Rectangle Bounds(List<int> c)=>Rectangle.FromLTRB(c.Min(i=>i%b.Width),c.Min(i=>i/b.Width),c.Max(i=>i%b.Width)+1,c.Max(i=>i/b.Width)+1);
            var glyphs=components.Where(c=>{
                var q=Bounds(c);return c.Count>=8 && q.Width<=b.Height*.9f && q.Height>=b.Height*.32f &&
                    q.Height<=b.Height*1.05f && c.Count<q.Width*q.Height*.88f;
            }).ToArray();
            if(glyphs.Length<Math.Max(4,letters*.6f) || glyphs.Length>letters*1.45f)continue;
            var boxes=glyphs.Select(Bounds).OrderBy(q=>q.Left).ToArray();
            if(boxes.Max(q=>q.Bottom)-boxes.Min(q=>q.Bottom)>b.Height*.35f)continue;
            var glyphBounds=boxes.Aggregate(Rectangle.Union);
            if(glyphBounds.Width<b.Width*.45f || glyphBounds.Height>b.Height*.9f)continue;
            var region=Rectangle.Intersect(Rectangle.Inflate(glyphBounds,3,Math.Max(3,(int)Math.Ceiling(b.Height*.2f))),new Rectangle(0,0,b.Width,b.Height));
            // Detached descenders may extend beyond the main components, but a
            // continuous horizontal frame is not text. Stop before its fringe.
            bool FrameRow(int y)=>Enumerable.Range(region.Left,region.Width)
                .Count(x=>Distance(colors[y*b.Width+x],background)>=45)>=region.Width*.8f;
            var regionTop=region.Top;var regionBottom=region.Bottom;
            for(var y=glyphBounds.Top-1;y>=region.Top;y--)
                if(FrameRow(y)){regionTop=Math.Min(glyphBounds.Top,y+3);break;}
            for(var y=glyphBounds.Bottom;y<region.Bottom;y++)
                if(FrameRow(y)){regionBottom=Math.Max(glyphBounds.Bottom,y-2);break;}
            region=Rectangle.FromLTRB(region.Left,regionTop,region.Right,regionBottom);
            var dr=fill.R-background.R;var dg=fill.G-background.G;var db=fill.B-background.B;
            var norm=(float)(dr*dr+dg*dg+db*db);var pixels=new List<Point>();
            for(var y=region.Top;y<region.Bottom;y++)for(var x=region.Left;x<region.Right;x++)
            {
                var color=colors[y*b.Width+x];var t=((color.R-background.R)*dr+(color.G-background.G)*dg+(color.B-background.B)*db)/norm;
                var error=Math.Max(Math.Abs(color.R-background.R-t*dr),Math.Max(Math.Abs(color.G-background.G-t*dg),Math.Abs(color.B-background.B-t*db)));
                if(t>=.035f&&t<=1.3f&&error<=20)pixels.Add(new(b.Left+x,b.Top+y));
            }
            if(pixels.Count<glyphs.Sum(c=>c.Count) || pixels.Count>region.Width*region.Height*.6f)continue;
            // Compression and antialiasing can create dark, differently colored
            // pixels around a colored stroke. Include a bounded two-pixel stroke
            // neighborhood, still inside the observed text run and away from icons.
            var completed=new HashSet<Point>(pixels);
            foreach(var seed in pixels)
                for(var dy=-2;dy<=2;dy++)for(var dx=-2;dx<=2;dx++)
                {
                    if(dx*dx+dy*dy>5)continue;
                    var q=new Point(seed.X+dx,seed.Y+dy);
                    if(region.Contains(q.X-b.Left,q.Y-b.Top))completed.Add(q);
                }
            pixels=completed.ToList();
            glyphBounds.Offset(b.Location);
            var candidate=new SourceGlyphRowEvidence(b,glyphBounds,fill,background,pixels,glyphs.Length,plain);
            if(best is null || candidate.GlyphCount>best.GlyphCount)best=candidate;
        }
        return best;
    }
    private static int Distance(Color a,Color b)=>Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B)));
    private static List<List<int>> Components(bool[] mask,int w,int h)
    {
        var seen=new bool[mask.Length];var all=new List<List<int>>();
        for(var seed=0;seed<mask.Length;seed++)
        {
            if(!mask[seed]||seen[seed])continue;
            var c=new List<int>();var queue=new Queue<int>();queue.Enqueue(seed);seen[seed]=true;
            while(queue.Count>0){var i=queue.Dequeue();c.Add(i);var x=i%w;var y=i/w;
                for(var dy=-1;dy<=1;dy++)for(var dx=-1;dx<=1;dx++){
                    var xx=x+dx;var yy=y+dy;if(xx<0||yy<0||xx>=w||yy>=h)continue;
                    var j=yy*w+xx;if(!mask[j]||seen[j])continue;seen[j]=true;queue.Enqueue(j);
                }
            }
            all.Add(c);
        }
        return all;
    }
}
