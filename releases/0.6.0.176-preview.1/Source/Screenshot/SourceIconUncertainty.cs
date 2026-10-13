namespace ScreenshotTranslationUiTester.CorePipelineV2;

/// <summary>Contradictory OCR word geometry inside repeated source illustration slots.</summary>
internal static class SourceIconUncertainty
{
    internal static IReadOnlySet<string> ObserveContradictoryWordDirection(IReadOnlyList<NormalizedOcrLine> lines)
    {
        var result=new HashSet<string>(StringComparer.Ordinal);
        foreach(var line in lines)
        {
            if(line.Confidence>=.65f||line.SourceText.Length<4||!line.SourceText.All(char.IsAsciiLetter)||line.Polygon.Length!=4)continue;
            var p=line.Polygon;var u=new PointF(p[1].X-p[0].X,p[1].Y-p[0].Y);var v=new PointF(p[3].X-p[0].X,p[3].Y-p[0].Y);
            var width=MathF.Sqrt(u.X*u.X+u.Y*u.Y);var height=MathF.Sqrt(v.X*v.X+v.Y*v.Y);
            // This OCR record claims a horizontal Latin word, but assigns it an
            // upright narrow strip. A proven vertical baseline, CJK, single keys,
            // numbers and normal word proportions are not covered by this doubt.
            if(height>=20&&width<height*.45f&&Math.Abs(u.Y)<Math.Abs(u.X)*.15f&&
                Math.Abs(v.X)<Math.Abs(v.Y)*.15f)result.Add(line.SourceId);
        }
        return result;
    }
    internal static IReadOnlySet<string> ObserveAmbiguousGlyphNotation(IReadOnlyList<NormalizedOcrLine> lines,
        IReadOnlyDictionary<string,SourceCellGeometry.Cell> cells)
    {
        var result=new HashSet<string>(StringComparer.Ordinal);
        if(lines.Count<4)return result;
        var median=lines.Select(l=>l.Bounds.Height).Order().ElementAt(lines.Count/2);
        foreach(var l in lines)
        {
            // A low-confidence initial + dotted lowercase fragment is not a
            // confirmed word. In a large isolated glyph row, leave its meaning
            // unresolved instead of translating a guessed greeting or name.
            // Normal words, terminal punctuation, initials, numbers and URLs do
            // not match this combination. The OCR evidence remains available.
            if(l.Confidence>=.85f||cells.ContainsKey(l.SourceId)||l.Bounds.Height<Math.Max(64,median*2.5f)||
                l.Bounds.Width/l.Bounds.Height is <.8f or >3f||
                !System.Text.RegularExpressions.Regex.IsMatch(l.SourceText,@"^[A-Z][.·][a-z]{2,6}$"))continue;
            var similar=lines.Any(q=>q.SourceId!=l.SourceId&&q.Bounds.Height>=l.Bounds.Height*.7f&&
                Math.Abs(q.Bounds.Top-l.Bounds.Top)<l.Bounds.Height*.5f&&
                Math.Min(Math.Abs(q.Bounds.Left-l.Bounds.Right),Math.Abs(l.Bounds.Left-q.Bounds.Right))<l.Bounds.Height);
            if(!similar)result.Add(l.SourceId);
        }
        return result;
    }
    internal static IReadOnlySet<string> ObserveRewardIcons(ReadOnlyBitmapPixelBuffer pixels,IReadOnlyList<NormalizedOcrLine> lines)
    {
        var result=new HashSet<string>(StringComparer.Ordinal);
        var candidates=lines.Where(l=>l.Confidence<.85f&&l.SourceText.Length is >=2 and <=5&&
            l.SourceText.All(char.IsAsciiLetterOrDigit)&&l.SourceText.Any(char.IsAsciiLetter)&&
            l.Bounds.Height>=12&&l.Bounds.Width/l.Bounds.Height is >=.65f and <=1.4f&&
            lines.Any(q=>System.Text.RegularExpressions.Regex.IsMatch(q.SourceText,@"^[xX×]\s*\d[\d,]*$")&&
                q.Bounds.Left>=l.Bounds.Right-l.Bounds.Height*.15f&&q.Bounds.Left-l.Bounds.Right<l.Bounds.Height*2&&
                Math.Abs(q.Bounds.Top+q.Bounds.Height/2-l.Bounds.Top-l.Bounds.Height/2)<l.Bounds.Height*.6f))
            .Take(12).ToArray();
        foreach(var line in candidates)
        {
            var b=Rectangle.Intersect(Rectangle.Ceiling(line.Bounds),new(Point.Empty,pixels.Size));
            if(b.Width*b.Height>40000)continue;
            var colors=new Color[b.Width*b.Height];
            for(var y=0;y<b.Height;y++)for(var x=0;x<b.Width;x++)colors[y*b.Width+x]=pixels.GetPixel(b.X+x,b.Y+y);
            var groups=colors.GroupBy(c=>(c.R/24,c.G/24,c.B/24)).OrderByDescending(g=>g.Count()).ToArray();
            if(groups.Length<4||groups[0].Count()<colors.Length*.30f)continue;
            Color Mean(IEnumerable<Color> a)=>Color.FromArgb((int)a.Average(c=>c.R),(int)a.Average(c=>c.G),(int)a.Average(c=>c.B));
            int Distance(Color a,Color c)=>Math.Max(Math.Abs(a.R-c.R),Math.Max(Math.Abs(a.G-c.G),Math.Abs(a.B-c.B)));
            var background=Mean(groups[0]);
            var foregroundPixels=colors.Count(c=>Distance(c,background)>45);
            if(foregroundPixels<colors.Length*.1f||foregroundPixels>colors.Length*.55f)continue;
            var foreground=groups.Skip(1).Where(g=>g.Count()>=Math.Max(8,foregroundPixels*.035f)).Select(Mean).Where(c=>Distance(c,background)>45).ToArray();
            if(foreground.Length<3||!foreground.Any(c=>Math.Max(c.R,Math.Max(c.G,c.B))-Math.Min(c.R,Math.Min(c.G,c.B))>=65)||
                foreground.Max(c=>c.GetBrightness())-foreground.Min(c=>c.GetBrightness())<.20f)continue;
            var mask=colors.Select(c=>Distance(c,background)>45).ToArray();var seen=new bool[mask.Length];var components=0;
            for(var k=0;k<mask.Length;k++){
                if(!mask[k]||seen[k])continue;var queue=new Queue<int>();queue.Enqueue(k);seen[k]=true;var count=0;
                while(queue.Count>0){var at=queue.Dequeue();count++;var x=at%b.Width;var y=at/b.Width;
                    for(var dy=-1;dy<=1;dy++)for(var dx=-1;dx<=1;dx++){
                        var xx=x+dx;var yy=y+dy;if(xx<0||yy<0||xx>=b.Width||yy>=b.Height)continue;var next=yy*b.Width+xx;
                        if(mask[next]&&!seen[next]){seen[next]=true;queue.Enqueue(next);}}}
                if(count>=colors.Length*.025f)components++;
            }
            if(components==1)result.Add(line.SourceId);
        }
        return result;
    }
    internal static IReadOnlySet<string> Observe(IReadOnlyList<NormalizedOcrLine> lines,
        IReadOnlyDictionary<string,SourceCellGeometry.Cell> cells)
    {
        var uncertain=new HashSet<string>(StringComparer.Ordinal);
        var captions=cells.Values.DistinctBy(c=>c.Bounds).Where(c=>c.Bounds.Width>=24&&
            c.Bounds.Height>=12&&c.Bounds.Height<=c.Bounds.Width*.8f).ToArray();
        foreach(var line in lines)
        {
            var text=line.SourceText;
            if(cells.ContainsKey(line.SourceId)||line.Confidence>=.9f||text.Length is <2 or >12||
                !text.All(c=>c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')||
                line.Bounds.Height<16||line.Bounds.Width/(text.Length*line.Bounds.Height)>=.4f)continue;
            foreach(var caption in captions)
            {
                var b=caption.Bounds;var raw=line.Bounds;
                if(raw.Left<b.Left||raw.Right>b.Right||raw.Bottom>b.Top||
                    raw.Top<b.Top-b.Width*1.2f||raw.Height>b.Width||raw.Height<b.Height*.6f)continue;
                var peers=captions.Count(c=>Math.Abs(c.Bounds.Top-b.Top)<b.Height*.25f&&
                    c.Bounds.Width>=b.Width*.75f&&c.Bounds.Width<=b.Width*1.3f&&
                    c.Bounds.Height>=b.Height*.7f&&c.Bounds.Height<=b.Height*1.3f);
                if(peers<3)continue;
                uncertain.Add(line.SourceId);break;
            }
        }
        return uncertain;
    }
}
