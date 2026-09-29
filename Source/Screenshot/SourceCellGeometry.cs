namespace ScreenshotTranslationUiTester.CorePipelineV2;

/// <summary>Source-only cell geometry for dense label grids. No cleanup permission.</summary>
internal static class SourceCellGeometry
{
    internal static Color? SmallTextFill(ReadOnlyBitmapPixelBuffer pixels,VisualBlock block)
    {
        if(block.SourceCell is not {} cell||cell.Bounds.Height>16)return null;
        if(block.Lines.Count==1&&SourceControlMaterial.ObserveTinyCell(pixels,block,
            NativeTextMaterial.Observe(pixels,block.Lines[0].Polygon)) is {} tiny)return tiny.Fill;
        var b=Rectangle.Intersect(Rectangle.Ceiling(RectangleF.Intersect(block.Bounds,
            RectangleF.Inflate(cell.Bounds,-1,-1))),new Rectangle(0,0,pixels.Width,pixels.Height));
        var samples=new List<Color>();
        static int Contrast(Color a,Color b)=>Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B)));
        for(var y=b.Top;y<b.Bottom;y++)for(var x=b.Left;x<b.Right;x++)
        {
            var c=pixels.GetPixel(x,y);if(Contrast(c,cell.Surface)>=36)samples.Add(c);
        }
        if(samples.Count<3||samples.Count>Math.Max(1,b.Width*b.Height)*.65f)return null;
        var group=samples.GroupBy(c=>(c.R/24,c.G/24,c.B/24))
            .OrderByDescending(g=>g.Count()*Math.Pow(g.Average(c=>Contrast(c,cell.Surface)),1.5)).First().ToArray();
        return Color.FromArgb(group.Select(c=>(int)c.R).Order().ElementAt(group.Length/2),
            group.Select(c=>(int)c.G).Order().ElementAt(group.Length/2),
            group.Select(c=>(int)c.B).Order().ElementAt(group.Length/2));
    }
    internal sealed record Cell(RectangleF Bounds,Color Surface,string Proof);
    internal static IReadOnlyDictionary<string,Cell> ObserveGrid(ReadOnlyBitmapPixelBuffer pixels,
        IReadOnlyList<NormalizedOcrLine> lines)
    {
        var result=new Dictionary<string,Cell>(StringComparer.Ordinal);var budget=2_000_000;
        foreach(var line in lines.OrderBy(l=>l.ReadingOrder))
        {
            var b=line.Bounds;var h=b.Height;
            if(h<5||h>pixels.Height*.06f||b.Width>pixels.Width*.22f||b.Width<h*1.1f)continue;
            var peers=lines.Count(l=>l.SourceId!=line.SourceId&&l.Bounds.Height>=h*.5f&&l.Bounds.Height<=h*1.8f&&
                Math.Abs(l.Bounds.Top-b.Top)<h*1.5f&&Math.Abs(l.Bounds.Left-b.Left)<pixels.Width*.5f);
            if(peers<3)continue;
            var scan=Rectangle.Intersect(new(Point.Empty,pixels.Size),Rectangle.Round(RectangleF.Inflate(b,
                Math.Min(pixels.Width*.11f,Math.Max(h*5,b.Width*.6f)),h*2.5f)));
            var area=scan.Width*scan.Height;if(area<=0||area>90_000||budget<area)continue;budget-=area;
            var samples=new List<Color>();
            var core=Rectangle.Intersect(scan,Rectangle.Round(RectangleF.Inflate(b,0,2)));
            for(var y=core.Top;y<core.Bottom;y++)for(var x=core.Left;x<core.Right;x++)samples.Add(pixels.GetPixel(x,y));
            var candidates=samples.GroupBy(c=>(c.R/24,c.G/24,c.B/24)).OrderByDescending(g=>g.Count()).Take(3);
            Cell? best=null;float bestArea=float.MaxValue;
            foreach(var colors in candidates)
            {
                int Median(Func<Color,int> f)=>colors.Select(f).Order().ElementAt(colors.Count()/2);
                var color=Color.FromArgb(Median(c=>c.R),Median(c=>c.G),Median(c=>c.B));
                var seen=new bool[area];
                bool Near(int index){var c=pixels.GetPixel(scan.Left+index%scan.Width,scan.Top+index/scan.Width);return Math.Max(Math.Abs(c.R-color.R),Math.Max(Math.Abs(c.G-color.G),Math.Abs(c.B-color.B)))<=16;}
                for(var sy=core.Top;sy<core.Bottom;sy+=2)for(var sx=core.Left;sx<core.Right;sx+=3)
                {
                    var seed=(sy-scan.Top)*scan.Width+sx-scan.Left;if(seen[seed]||!Near(seed))continue;
                    var q=new Queue<int>();q.Enqueue(seed);seen[seed]=true;
                    var l=scan.Width;var t=scan.Height;var r=0;var bottom=0;var count=0;var boundary=false;
                    while(q.Count>0)
                    {
                        var v=q.Dequeue();var x=v%scan.Width;var y=v/scan.Width;count++;
                        l=Math.Min(l,x);r=Math.Max(r,x);t=Math.Min(t,y);bottom=Math.Max(bottom,y);
                        if(x==0||y==0||x==scan.Width-1||y==scan.Height-1)boundary=true;
                        void Add(int xx,int yy){if(xx<0||yy<0||xx>=scan.Width||yy>=scan.Height)return;var n=yy*scan.Width+xx;if(seen[n])return;seen[n]=true;if(Near(n))q.Enqueue(n);}
                        Add(x-1,y);Add(x+1,y);Add(x,y-1);Add(x,y+1);
                    }
                    var cell=new RectangleF(scan.Left+l,scan.Top+t,r-l+1,bottom-t+1);
                    var a=cell.Width*cell.Height;var hit=RectangleF.Intersect(cell,b);
                    if(boundary||a<12||count<a*.40f||cell.Height<h*.55f||cell.Height>h*4.5f||cell.Width<cell.Height*1.3f||
                        cell.Width>b.Width*2.8f||hit.Width*hit.Height<b.Width*b.Height*.60f||
                        b.Left<cell.Left-h*.3f||b.Right>cell.Right+h*.3f||a>=bestArea)continue;
                    // Multiple independent OCR rows may belong to this same card;
                    // adjacent cells keep their own material-connected rectangle.
                    best=new(cell,color,"DENSE_SOURCE_GRID_SEPARATE_ENCLOSED_MATERIAL_CELL");bestArea=a;
                }
            }
            if(best is not null)result[line.SourceId]=best;
        }
        ExtendObservedGrid(pixels,lines,result);
        CanonicalizeObservedCells(lines,result);
        return result;
    }

    internal static void CanonicalizeObservedCells(IReadOnlyList<NormalizedOcrLine> lines,
        Dictionary<string,Cell> result)
    {
        // Independent color samples of the same connected caption surface can
        // differ by one edge pixel. Preserve one owner, not two overlapping cells.
        var canonical=new List<Cell>();
        foreach(var pair in result.OrderBy(p=>p.Value.Bounds.Width*p.Value.Bounds.Height).ToArray())
        {
            var cell=pair.Value;var a=cell.Bounds;
            var same=canonical.FirstOrDefault(c=>{
                var b=c.Bounds;var hit=RectangleF.Intersect(a,b);
                var union=a.Width*a.Height+b.Width*b.Height-hit.Width*hit.Height;
                return union>0&&hit.Width*hit.Height/union>=.92f&&
                    Math.Max(Math.Abs(cell.Surface.R-c.Surface.R),Math.Max(Math.Abs(cell.Surface.G-c.Surface.G),Math.Abs(cell.Surface.B-c.Surface.B)))<=20;
            });
            if(same is null){canonical.Add(cell);same=cell;}
            result[pair.Key]=same;
        }
        // Another source row fully inside this already proved small cell shares
        // its owner. A neighboring card or a row crossing the border does not.
        foreach(var line in lines.Where(l=>!result.ContainsKey(l.SourceId)))
        {
            var b=line.Bounds;var area=b.Width*b.Height;if(area<=0)continue;
            var owners=canonical.Where(c=>{
                var hit=RectangleF.Intersect(c.Bounds,b);
                return c.Bounds.Height<=b.Height*3&&hit.Width*hit.Height>=area*.95f;
            }).Take(2).ToArray();
            if(owners.Length==1)result[line.SourceId]=owners[0];
        }
    }

    private static void ExtendObservedGrid(ReadOnlyBitmapPixelBuffer pixels,IReadOnlyList<NormalizedOcrLine> lines,
        Dictionary<string,Cell> result)
    {
        // Tiny raster letters can divide a cell's surface into disconnected
        // pieces. A separately observed cell supplies dimensions to search,
        // while every additional rectangle must prove its own closed edges.
        // Dimensions alone never authorize a new owner or any pixel edit.
        var seeds=result.Values.DistinctBy(c=>c.Bounds).ToArray();var work=0;
        static int Distance(Color a,Color b)=>Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B)));
        foreach(var line in lines)
        {
            if(result.ContainsKey(line.SourceId)||line.Bounds.Height>24)continue;
            var b=line.Bounds;Cell? best=null;var bestScore=0d;
            foreach(var seed in seeds.Where(c=>Math.Abs(c.Bounds.Top-b.Top)<c.Bounds.Height*1.6f&&
                c.Bounds.Width>=b.Width*.85f&&c.Bounds.Width<=Math.Max(b.Width*4,b.Height*6)&&
                c.Bounds.Height>=b.Height*.45f&&c.Bounds.Height<=b.Height*1.5f).Take(2))
            {
                var sw=(int)seed.Bounds.Width;var sh=(int)seed.Bounds.Height;
                var widthDelta=Math.Clamp(sw/14,1,5);
                for(var width=sw-widthDelta;width<=sw+widthDelta;width+=2)
                for(var top=(int)b.Top-(int)(b.Height*.3f);top<=(int)(b.Bottom-sh+b.Height*.15f);top++)
                for(var left=(int)Math.Floor(b.Right-width-b.Height*.15f);left<=(int)Math.Ceiling(b.Left+b.Height*.15f);left++)
                {
                    if(++work>60_000)return;
                    var rect=new Rectangle(left,top,width,sh);
                    if(rect.Left<1||rect.Top<1||rect.Right>=pixels.Width-1||rect.Bottom>=pixels.Height-1)continue;
                    var hit=RectangleF.Intersect(rect,b);if(hit.Width*hit.Height<b.Width*b.Height*.48f)continue;
                    var corners=new[]{pixels.GetPixel(left+1,top),pixels.GetPixel(rect.Right-2,top),
                        pixels.GetPixel(left+1,rect.Bottom-1),pixels.GetPixel(rect.Right-2,rect.Bottom-1)};
                    int Median(Func<Color,int> f)=>corners.Select(f).Order().ElementAt(2);
                    var color=Color.FromArgb(Median(c=>c.R),Median(c=>c.G),Median(c=>c.B));
                    if(corners.Count(c=>Distance(c,color)<=20)<3)continue;
                    var edge=0;var outside=0;var count=0;
                    void Sample(int x,int y,int ox,int oy){count++;if(Distance(pixels.GetPixel(x,y),color)<=22)edge++;
                        if(Distance(pixels.GetPixel(ox,oy),color)>=28)outside++;}
                    for(var x=left+1;x<rect.Right-1;x+=2){Sample(x,top,x,top-1);Sample(x,rect.Bottom-1,x,rect.Bottom);}
                    for(var y=top+1;y<rect.Bottom-1;y++){Sample(left,y,left-1,y);Sample(rect.Right-1,y,rect.Right,y);}
                    if(edge<count*.72f||outside<count*.65f)continue;
                    var score=(edge+outside)/(double)count;
                    if(score<=bestScore)continue;
                    best=new(rect,color,"SOURCE_GRID_PEER_DIMENSIONS_WITH_INDEPENDENT_CLOSED_CONTRAST_EDGES");bestScore=score;
                }
            }
            if(best is not null)result[line.SourceId]=best;
        }
    }
}
