namespace ScreenshotTranslationUiTester.CorePipelineV2;

// Read-only glyph measurements. Occupied area cannot distinguish italic text
// from bold text. Stem width and a consistent slant are separate observations.
internal static class SourceGlyphTypography
{
    internal sealed record Line(string SourceId,int InkPixels,int InkHeight,float StrokePixels,
        float StrokeRatio,float Slant,float SlantGain)
    { public int BaselineHeight {get;init;} }
    internal sealed record Evidence(FontStyle? Weight,string Reason,IReadOnlyList<Line> Lines);
    internal static Evidence Observe(ReadOnlyBitmapPixelBuffer p,VisualBlock b,SourceStyleEvidenceR2 style)
    {
        var independentlyProved=style.DirectConfidence<.7f && style.VisualRole==SourceVisualRoleR2.Control && b.Lines.Count<=2 &&
            b.Lines.All(l=>{var m=NativeTextMaterial.Observe(p,l.Polygon);return m.Accepted&&m.Components>=3&&
                m.RingVariation<=20&&m.Polarity=="DarkOnLight";});
        if((style.DirectConfidence<.7f&&!independentlyProved) ||
            style.VisualRole is SourceVisualRoleR2.ArtisticTitle ||
            CorePipelineCorpusRunner.PreserveIndependentFieldBreaks(b))
            return new(null,"SOURCE_STYLE_NOT_PLAIN",[]);
        var fill=Color.FromArgb(style.DirectForegroundArgb);
        var lines=b.Lines.Select(l=>Measure(p,l,fill)).Where(l=>l is not null).Cast<Line>().ToArray();
        if(lines.Length==0)return new(null,"INSUFFICIENT_SOURCE_GLYPHS",lines);
        var ratios=lines.Select(l=>l.StrokeRatio).Order().ToArray();
        var ratio=ratios[ratios.Length/2];
        var slanted=lines.Count(l=>Math.Abs(l.Slant)>=.15f&&l.SlantGain>=1.15f);
        var italic=slanted>=Math.Ceiling(lines.Length*.8f) &&
            lines.Count(l=>l.SlantGain>=1.15f&&Math.Sign(l.Slant)==Math.Sign(lines[0].Slant))==slanted;
        if(ratios[^1]>ratios[0]*1.65f)return new(null,"MIXED_SOURCE_STROKES",lines);
        FontStyle? weight=ratio<=.09f?FontStyle.Regular:ratio>=.14f?FontStyle.Bold:null;
        if(italic && ratio<.115f)weight=FontStyle.Italic;
        else if(italic && weight is not null)weight|=FontStyle.Italic;
        return new(weight,weight is null?"SOURCE_WEIGHT_AMBIGUOUS":italic?"SOURCE_STEM_SLANT_AND_WIDTH":"SOURCE_NORMALIZED_STEM_WIDTH",lines);
    }
    internal static Line? Measure(ReadOnlyBitmapPixelBuffer p,NormalizedOcrLine line,Color fill)
    {
        var b=Rectangle.Intersect(Rectangle.Round(line.Bounds),new Rectangle(Point.Empty,p.Size));
        if(b.Height<12 || b.Height>Math.Max(100,p.Height*.1f) || b.Width<30 || line.SourceText.Count(char.IsLetter)<3)return null;
        var samples=new List<Color>();
        for(var y=0;y<b.Height;y++)for(var x=0;x<b.Width;x++)
        {
            var c=p.GetPixel(b.Left+x,b.Top+y);
            if(Math.Max(Math.Abs(c.R-fill.R),Math.Max(Math.Abs(c.G-fill.G),Math.Abs(c.B-fill.B)))<30)samples.Add(c);
        }
        if(samples.Count<30)return null;
        // Measure the solid source plateau; anti-alias coverage is not stroke thickness.
        var mode=samples.GroupBy(c=>(c.R/8,c.G/8,c.B/8)).OrderByDescending(g=>g.Count()).First().ToArray();
        int Median(Func<Color,int> channel)=>mode.Select(channel).Order().ElementAt(mode.Length/2);
        var solid=Color.FromArgb(Median(c=>c.R),Median(c=>c.G),Median(c=>c.B));
        var mask=new bool[b.Width,b.Height];var pts=new List<Point>();
        for(var y=0;y<b.Height;y++)for(var x=0;x<b.Width;x++)
        {
            var c=p.GetPixel(b.Left+x,b.Top+y);
            if(Math.Max(Math.Abs(c.R-solid.R),Math.Max(Math.Abs(c.G-solid.G),Math.Abs(c.B-solid.B)))>8)continue;
            mask[x,y]=true;pts.Add(new(x,y));
        }
        if(pts.Count<30 || pts.Count<b.Width*b.Height*.025f || pts.Count>b.Width*b.Height*.5f)return null;
        var ih=pts.Max(v=>v.Y)-pts.Min(v=>v.Y)+1;
        // A descender changes the line extent, not its font or stem weight.
        // Connected glyph bottoms supply the shared baseline independently of OCR height.
        var visited=new bool[b.Width,b.Height];var components=new List<Rectangle>();
        foreach(var point in pts)
        {
            if(visited[point.X,point.Y])continue;
            var queue=new Queue<Point>();queue.Enqueue(point);visited[point.X,point.Y]=true;
            int left=point.X,right=point.X,top=point.Y,bottom=point.Y,count=0;
            while(queue.Count>0)
            {
                var q=queue.Dequeue();count++;left=Math.Min(left,q.X);right=Math.Max(right,q.X);
                top=Math.Min(top,q.Y);bottom=Math.Max(bottom,q.Y);
                for(var dy=-1;dy<=1;dy++)for(var dx=-1;dx<=1;dx++)
                {
                    var xx=q.X+dx;var yy=q.Y+dy;
                    if(xx<0||yy<0||xx>=b.Width||yy>=b.Height||visited[xx,yy]||!mask[xx,yy])continue;
                    visited[xx,yy]=true;queue.Enqueue(new(xx,yy));
                }
            }
            if(count>=8&&bottom-top+1>=ih*.4f)components.Add(Rectangle.FromLTRB(left,top,right+1,bottom+1));
        }
        var baselineHeight=ih;
        if(components.Count>=3)
        {
            var baseline=components.Select(c=>c.Bottom).OrderByDescending(y=>components.Count(c=>Math.Abs(c.Bottom-y)<=1))
                .ThenBy(y=>y).First();
            var support=components.Where(c=>Math.Abs(c.Bottom-baseline)<=1).ToArray();
            if(support.Length>=Math.Ceiling(components.Count*.45f))
                baselineHeight=baseline-components.Min(c=>c.Top);
            else
            {
                // A tilted baseline moves whole glyphs vertically. Measure
                // their individual heights rather than treating that movement
                // or a descender as a larger font for the complete row.
                var heights=components.Select(c=>c.Height).Order().ToArray();
                baselineHeight=heights[(int)Math.Floor((heights.Length-1)*.75f)];
            }
        }
        var horizontal=new int[b.Width,b.Height];var widths=new List<int>(pts.Count);
        for(var y=0;y<b.Height;y++)for(var x=0;x<b.Width;)
        {
            if(!mask[x,y]){x++;continue;}var start=x;while(x<b.Width&&mask[x,y])x++;
            for(var k=start;k<x;k++)horizontal[k,y]=x-start;
        }
        for(var x=0;x<b.Width;x++)for(var y=0;y<b.Height;)
        {
            if(!mask[x,y]){y++;continue;}var start=y;while(y<b.Height&&mask[x,y])y++;
            for(var k=start;k<y;k++)widths.Add(Math.Min(y-start,horizontal[x,k]));
        }
        widths.Sort();var stem=widths[widths.Count/2];
        double Score(float slope)
        {
            var bins=new int[b.Width+2*b.Height];
            foreach(var v in pts)bins[(int)Math.Round(v.X+slope*(v.Y-b.Height/2f))+b.Height]++;
            return bins.Sum(v=>(double)v*v)/pts.Count;
        }
        var zero=Score(0);var best=zero;var slant=0f;
        for(var k=-8;k<=8;k++){var score=Score(k*.05f);if(score>best){best=score;slant=k*.05f;}}
        return new(line.SourceId,pts.Count,ih,stem,stem/(float)Math.Max(1,baselineHeight),slant,(float)(best/zero))
            {BaselineHeight=baselineHeight};
    }
}
