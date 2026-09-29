namespace ScreenshotTranslationUiTester.CorePipelineV2;

// Read-only source evidence for separate adornments. Neither path removes OCR
// records or invents an expanded meaning for an ambiguous mark.
internal static class SourceAdornmentEvidence
{
    internal static NormalizedOcrLine[] SeparateEmbeddedRailIcons(ReadOnlyBitmapPixelBuffer p,
        IReadOnlyList<NormalizedOcrLine> lines)
    {
        var rails=ObserveIconRail(p,lines);var revised=lines.ToArray();
        foreach(var rail in lines.Where(l=>rails.Contains(l.SourceId)).Take(8))
        {
            var h=rail.Bounds.Height;
            var label=lines.Where(q=>q.SourceId!=rail.SourceId&&q.SourceText.Count(char.IsLetter)>=3&&
                q.Bounds.Left>=rail.Bounds.Right-h*.15f&&q.Bounds.Left-rail.Bounds.Right<h*1.5f&&
                Math.Abs(q.Bounds.Top+q.Bounds.Height/2-rail.Bounds.Top-h/2)<h*.3f)
                .OrderBy(q=>q.Bounds.Left).FirstOrDefault();
            if(label is null)continue;
            for(var i=0;i<revised.Length;i++)
            {
                var q=revised[i];var b=Rectangle.Round(q.Bounds);
                if(q.SourceId==rail.SourceId||q.SourceText.Count(char.IsLetter)<3||q.SourceText.Length>40||q.SourceText.Any(char.IsWhiteSpace)||
                    Math.Abs(q.Bounds.Left-rail.Bounds.Left)>h*.2f||q.Bounds.Width<h*1.7f||
                    q.Bounds.Height/h is <.65f or >1.35f||Math.Abs(q.Bounds.Top-rail.Bounds.Top)>h*10)continue;
                var iconBox=Rectangle.Round(new RectangleF(rail.Bounds.Left,q.Bounds.Top+q.Bounds.Height/2-h/2,rail.Bounds.Width,h));
                if(!CompactSourceObject(p,iconBox))continue;
                var start=Math.Max(b.Left,(int)Math.Floor(rail.Bounds.Right-h*.25f));
                var end=Math.Min(b.Right-3,(int)Math.Ceiling(label.Bounds.Left+h*.35f));
                if(start<0||end>=p.Width||b.Top<0||b.Bottom>p.Height)continue;
                // Actual blank columns separate the rail object from the word.
                // Peer x positions nominate the search, never define the cut.
                var gaps=new List<(int Start,int End)>();int gap=-1;
                for(var x=start;x<=end;x++)
                {
                    var top=p.GetPixel(x,b.Top);var bottom=p.GetPixel(x,b.Bottom-1);
                    bool blank=Distance(top,bottom)<24;
                    for(var y=b.Top;y<b.Bottom&&blank;y++)blank=Distance(p.GetPixel(x,y),top)<32;
                    if(blank){if(gap<0)gap=x;}
                    else if(gap>=0){if(x-gap>=Math.Max(2,h*.08f))gaps.Add((gap,x));gap=-1;}
                }
                if(gap>=0&&end+1-gap>=Math.Max(2,h*.08f))gaps.Add((gap,end+1));
                if(gaps.Count==0)continue;
                var selected=gaps.OrderBy(g=>Math.Abs((g.Start+g.End)/2f-label.Bounds.Left)).First();
                var cut=(selected.Start+selected.End)/2f;
                if(cut-q.Bounds.Left<h*.65f||q.Bounds.Right-cut<h)continue;
                var updated=RectangleF.FromLTRB(cut,q.Bounds.Top,q.Bounds.Right,q.Bounds.Bottom);
                revised[i]=q with{Bounds=updated,Polygon=[new(updated.Left,updated.Top),new(updated.Right,updated.Top),
                    new(updated.Right,updated.Bottom),new(updated.Left,updated.Bottom)],
                    NormalizationTrace=q.NormalizationTrace+";SOURCE_ICON_RAIL_BLANK_COLUMN_SPLIT="+cut.ToString("R",System.Globalization.CultureInfo.InvariantCulture)};
            }
        }
        return revised;
    }
    private static int Distance(Color a,Color b)=>Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B)));

    internal static IReadOnlySet<string> ObserveInitialismMarks(ReadOnlyBitmapPixelBuffer p,
        IReadOnlyList<NormalizedOcrLine> lines,IReadOnlyDictionary<string,SourceCellGeometry.Cell> cells)
    {
        var result=new HashSet<string>(StringComparer.Ordinal);
        if(lines.Count<3)return result;
        var median=lines.Select(l=>l.Bounds.Height).Order().ElementAt(lines.Count/2);
        foreach(var l in lines.Where(l=>l.SourceText.Length is >=2 and <=4 &&
            l.SourceText.All(c=>c is >= 'A' and <= 'Z')&&!l.SourceText.Any(c=>"AEIOUY".Contains(c)) &&
            l.Bounds.Height>=Math.Max(48,median*2.5f)&&l.Bounds.Width/l.Bounds.Height is >=.4f and <=2.8f &&
            !cells.ContainsKey(l.SourceId)).Take(8))
        {
            var b=Rectangle.Intersect(Rectangle.Ceiling(l.Bounds),new(Point.Empty,p.Size));
            if(b.Width*b.Height>160000)continue;
            // Nearby captions, counts, and same-size controls establish language
            // context. An isolated source emblem has none of those attachments.
            var near=RectangleF.Inflate(l.Bounds,l.Bounds.Height*1.2f,l.Bounds.Height*1.2f);
            if(lines.Any(q=>q.SourceId!=l.SourceId&&near.IntersectsWith(q.Bounds)))continue;
            var colors=new List<Color>();
            for(var y=b.Top;y<b.Bottom;y++)for(var x=b.Left;x<b.Right;x++)
                if(NativeTextMaterial.Contains(l.Polygon,x+.5f,y+.5f))colors.Add(p.GetPixel(x,y));
            if(colors.Count<100)continue;
            var colored=colors.Count(c=>Math.Max(c.R,Math.Max(c.G,c.B))-Math.Min(c.R,Math.Min(c.G,c.B))>=45);
            var dark=colors.Count(c=>Math.Max(c.R,Math.Max(c.G,c.B))<=80);
            if(colored<colors.Count*.12f||dark<colors.Count*.08f)continue;
            var family=colors.Where(c=>Math.Max(c.R,Math.Max(c.G,c.B))-Math.Min(c.R,Math.Min(c.G,c.B))>=45)
                .GroupBy(c=>(c.R/24,c.G/24,c.B/24)).OrderByDescending(g=>g.Count()).First();
            if(family.Count()<colors.Count*.07f)continue;
            result.Add(l.SourceId);
        }
        return result;
    }

    internal static IReadOnlySet<string> ObserveIconRail(ReadOnlyBitmapPixelBuffer p,IReadOnlyList<NormalizedOcrLine> lines)
    {
        var result=new HashSet<string>(StringComparer.Ordinal);
        foreach(var icon in lines.Where(l=>l.SourceText.Length<=2&&l.Confidence<.92f&&
            l.Bounds.Height is >=12 and <=96&&l.Bounds.Width/l.Bounds.Height is >=.65f and <=1.35f).Take(16))
        {
            var h=icon.Bounds.Height;
            var label=lines.Where(q=>q.SourceId!=icon.SourceId&&q.SourceText.Count(char.IsLetter)>=3&&
                q.Bounds.Left>=icon.Bounds.Right-h*.15f&&q.Bounds.Left-icon.Bounds.Right<h*1.5f&&
                Math.Abs(q.Bounds.Top+q.Bounds.Height/2-icon.Bounds.Top-h/2)<h*.3f)
                .OrderBy(q=>q.Bounds.Left).FirstOrDefault();
            if(label is null)continue;
            var peers=lines.Where(q=>q.SourceId!=label.SourceId&&q.SourceId!=icon.SourceId&&
                q.SourceText.Count(char.IsLetter)>=3&&Math.Abs(q.Bounds.Left-label.Bounds.Left)<h*.4f&&
                q.Bounds.Height/h is >=.65f and <=1.35f&&
                Math.Abs(q.Bounds.Top-label.Bounds.Top) is var dy&&dy>h*1.3f&&dy<h*10).Take(8).ToArray();
            var supported=0;
            foreach(var q in peers)
            {
                var box=Rectangle.Round(new RectangleF(icon.Bounds.Left,q.Bounds.Top+q.Bounds.Height/2-h/2,icon.Bounds.Width,h));
                if(CompactSourceObject(p,box)&&++supported>=2)break;
            }
            if(supported>=2&&CompactSourceObject(p,Rectangle.Round(icon.Bounds)))result.Add(icon.SourceId);
        }
        return result;
    }

    private static bool CompactSourceObject(ReadOnlyBitmapPixelBuffer p,Rectangle requested)
    {
        var b=Rectangle.Intersect(requested,new(Point.Empty,p.Size));
        if(b.Width<8||b.Height<8||b!=requested)return false;
        var corners=new[]{p.GetPixel(b.Left,b.Top),p.GetPixel(b.Right-1,b.Top),
            p.GetPixel(b.Left,b.Bottom-1),p.GetPixel(b.Right-1,b.Bottom-1)};
        int M(Func<Color,int> channel)=>corners.Select(channel).Order().ElementAt(1);
        var back=Color.FromArgb(M(c=>c.R),M(c=>c.G),M(c=>c.B));
        var pts=new List<Point>();
        for(var y=b.Top;y<b.Bottom;y++)for(var x=b.Left;x<b.Right;x++)
        {
            var c=p.GetPixel(x,y);
            if(Math.Max(Math.Abs(c.R-back.R),Math.Max(Math.Abs(c.G-back.G),Math.Abs(c.B-back.B)))>=45)pts.Add(new(x,y));
        }
        if(pts.Count<b.Width*b.Height*.12f||pts.Count>b.Width*b.Height*.65f)return false;
        return pts.Max(q=>q.X)-pts.Min(q=>q.X)>=b.Width*.45f&&pts.Max(q=>q.Y)-pts.Min(q=>q.Y)>=b.Height*.45f;
    }
}
