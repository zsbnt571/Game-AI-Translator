namespace ScreenshotTranslationUiTester.CorePipelineV2;

/// <summary>Repeated connected source ornament, not capitalization or size alone.</summary>
internal static class SourceRepeatedGraphicMark
{
    internal sealed record Observation(string SourceId,Rectangle Bounds,RectangleF Envelope,float[] Shape,int Pixels);
    internal sealed record Pair(string First,string Second,float Similarity);
    internal sealed record Evidence(IReadOnlySet<string> SourceIds,IReadOnlyList<Observation> Candidates,IReadOnlyList<Pair> Pairs,int ScannedPixels);
    internal static Evidence Observe(ReadOnlyBitmapPixelBuffer pixels,IReadOnlyList<NormalizedOcrLine> lines,
        IReadOnlySet<string> functionalHeadings,IReadOnlyDictionary<string,SourceCellGeometry.Cell> cells)
    {
        var candidates=new List<Observation>();var scanned=0;
        foreach(var line in lines.Where(l=>!cells.ContainsKey(l.SourceId)&&!functionalHeadings.Contains(l.SourceId)&&
            l.SourceText.Length is >=3 and <=18&&l.SourceText.All(c=>char.IsAsciiLetter(c)||char.IsWhiteSpace(c))&&
            l.Bounds.Height>=28&&l.Bounds.Width>=l.Bounds.Height*1.4f&&l.Bounds.Width<=l.Bounds.Height*8).Take(20))
        {
            var b=line.Bounds;var h=b.Height;
            var crop=Rectangle.Intersect(new(Point.Empty,pixels.Size),Rectangle.Ceiling(RectangleF.FromLTRB(
                b.Left-h*.5f,b.Top-h*1.25f,b.Right+h*.5f,b.Bottom+h*.6f)));
            var area=crop.Width*crop.Height;
            if(area<=0||area>240_000||scanned+area>1_500_000)continue;scanned+=area;
            var mask=new bool[area];var seen=new bool[area];
            for(var k=0;k<area;k++){
                var c=pixels.GetPixel(crop.Left+k%crop.Width,crop.Top+k/crop.Width);
                var lo=Math.Min(c.R,Math.Min(c.G,c.B));var hi=Math.Max(c.R,Math.Max(c.G,c.B));
                // Only clearly light, near-neutral source material is proved by
                // this path. Colored marks and dark-on-paper remain undecided.
                mask[k]=lo>110&&hi-lo<80;
            }
            Observation? best=null;
            for(var seed=0;seed<area;seed++){
                if(!mask[seed]||seen[seed])continue;
                var q=new Queue<int>();var component=new List<int>();q.Enqueue(seed);seen[seed]=true;
                var left=crop.Width;var top=crop.Height;var right=0;var bottom=0;var inside=0;var touches=false;
                while(q.Count>0){
                    var k=q.Dequeue();component.Add(k);var x=k%crop.Width;var y=k/crop.Width;
                    left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);
                    if(x==0||y==0||x==crop.Width-1||y==crop.Height-1)touches=true;
                    if(b.Contains(crop.Left+x,crop.Top+y))inside++;
                    for(var dy=-1;dy<=1;dy++)for(var dx=-1;dx<=1;dx++){
                        var xx=x+dx;var yy=y+dy;if(xx<0||yy<0||xx>=crop.Width||yy>=crop.Height)continue;
                        var next=yy*crop.Width+xx;if(seen[next]||!mask[next])continue;seen[next]=true;q.Enqueue(next);
                    }
                }
                var rect=new Rectangle(crop.Left+left,crop.Top+top,right-left+1,bottom-top+1);
                if(touches||inside<80||component.Count-inside<inside*.15f||
                    b.Top-rect.Top<h*.65f||rect.Bottom<b.Bottom-h*.1f||rect.Width<b.Width*.45f||
                    rect.Height<h*1.55f||component.Count<inside||best is not null&&best.Pixels>=component.Count)continue;
                const int n=24;var grid=new float[n*n];var counts=new int[n*n];
                for(var y=0;y<rect.Height;y++)for(var x=0;x<rect.Width;x++)counts[Math.Min(n-1,y*n/rect.Height)*n+Math.Min(n-1,x*n/rect.Width)]++;
                foreach(var k in component){var x=k%crop.Width-left;var y=k/crop.Width-top;grid[Math.Min(n-1,y*n/rect.Height)*n+Math.Min(n-1,x*n/rect.Width)]++;}
                for(var k=0;k<grid.Length;k++)grid[k]/=Math.Max(1,counts[k]);
                best=new(line.SourceId,rect,RectangleF.Union(b,rect),grid,component.Count);
            }
            if(best is not null)candidates.Add(best);
        }
        var found=new HashSet<string>(StringComparer.Ordinal);var pairs=new List<Pair>();
        for(var i=0;i<candidates.Count;i++)for(var j=i+1;j<candidates.Count;j++){
            var a=candidates[i];var b=candidates[j];
            if(a.Envelope.IntersectsWith(b.Envelope))continue;
            var scale=a.Bounds.Height/(float)b.Bounds.Height;
            if(scale>.8f&&scale<1.25f||scale<.3f||scale>3.3f)continue;
            var aspect=(a.Bounds.Width/(float)a.Bounds.Height)/(b.Bounds.Width/(float)b.Bounds.Height);
            if(aspect<.85f||aspect>1.18f)continue;
            var union=0f;var hit=0f;
            for(var k=0;k<a.Shape.Length;k++){hit+=Math.Min(a.Shape[k],b.Shape[k]);union+=Math.Max(a.Shape[k],b.Shape[k]);}
            var similarity=hit/Math.Max(.001f,union);if(similarity<.58f)continue;
            pairs.Add(new(a.SourceId,b.SourceId,similarity));
            foreach(var envelope in new[]{a.Envelope,b.Envelope})foreach(var line in lines){
                if(cells.ContainsKey(line.SourceId)||functionalHeadings.Contains(line.SourceId))continue;
                var r=line.Bounds;var intersect=RectangleF.Intersect(r,envelope);
                if(r.Width*r.Height>0&&intersect.Width*intersect.Height>=r.Width*r.Height*.95f)found.Add(line.SourceId);
            }
        }
        return new(found,candidates,pairs,scanned);
    }
}
