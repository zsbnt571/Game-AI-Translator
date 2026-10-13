using System.Drawing.Imaging;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

// Final plans share one rendering contract. Test intersecting ink at the source
// pixel scale, including antialiasing, outline and shadow; rectangles only prune.
internal static class FinalInkSeparation
{
    internal sealed record Observation(string First,string Second,int Pixels,string Action);
    internal sealed record Result(IReadOnlyList<Observation> Observations,int RasterPixels,int Probes);
    internal static Result Validate(IDictionary<string,FinalTextPlacement.Plan> aliases,Size canvas,
        CancellationToken cancellationToken)
    {
        var unique=aliases.Values.Where(p=>p.Valid).DistinctBy(p=>p.Owner)
            .OrderBy(p=>p.Ink.Top).ThenBy(p=>p.Ink.Left).ToArray();
        var current=unique.ToDictionary(p=>p.Owner,StringComparer.Ordinal);
        var observations=new List<Observation>();int pixels=0,probes=0;
        // Only local intersections allocate raster images. A refused budget is
        // explicit failure, never permission to paint an unchecked overlap.
        int Intersections(FinalTextPlacement.Plan a,FinalTextPlacement.Plan b)
        {
            var overlap=Rectangle.Intersect(Rectangle.Ceiling(RectangleF.Intersect(
                RectangleF.Inflate(a.Ink,1,1),RectangleF.Inflate(b.Ink,1,1))),new(Point.Empty,canvas));
            if(overlap.Width<=0||overlap.Height<=0)return 0;
            var area=(long)overlap.Width*overlap.Height;
            if(area>1_000_000||pixels+area*2>8_000_000||probes>=512)return -1;
            cancellationToken.ThrowIfCancellationRequested();pixels+=(int)(area*2);probes++;
            using var one=Raster(a,overlap);using var two=Raster(b,overlap);
            using var first=ReadOnlyBitmapPixelBuffer.Create(one);using var second=ReadOnlyBitmapPixelBuffer.Create(two);
            var count=0;
            for(var y=0;y<overlap.Height;y++)for(var x=0;x<overlap.Width;x++)
                if(first.GetPixel(x,y).A>0&&second.GetPixel(x,y).A>0)count++;
            return count;
        }
        bool Clear(FinalTextPlacement.Plan candidate)
        {
            foreach(var q in current.Values.Where(q=>q.Valid&&q.Owner!=candidate.Owner))
                if(Intersections(candidate,q)!=0)return false;
            return true;
        }
        for(var i=0;i<unique.Length;i++)for(var j=i+1;j<unique.Length;j++)
        {
            var a=current[unique[i].Owner];var b=current[unique[j].Owner];
            if(!a.Valid||!b.Valid)continue;
            var count=Intersections(a,b);if(count==0)continue;
            var moved=false;
            if(count>0)
            {
                var horizontal=Math.Abs((a.Ink.Left+a.Ink.Width/2)-(b.Ink.Left+b.Ink.Width/2))>
                    Math.Abs((a.Ink.Top+a.Ink.Height/2)-(b.Ink.Top+b.Ink.Height/2));
                foreach(var plan in new[]{b,a})
                {
                    var peer=plan.Owner==a.Owner?b:a;
                    var direction=horizontal?Math.Sign(plan.Ink.Left-peer.Ink.Left):Math.Sign(plan.Ink.Top-peer.Ink.Top);
                    if(direction==0)direction=1;
                    var limit=Math.Min(4,plan.Rows.Min(r=>r.Paint.Size)*.12f);
                    for(var amount=1;amount<=limit;amount++)
                    {
                        var candidate=Shift(plan,horizontal?direction*amount:0,horizontal?0:direction*amount,canvas);
                        if(candidate is null||!Clear(candidate))continue;
                        current[plan.Owner]=candidate;moved=true;break;
                    }
                    if(moved)break;
                }
            }
            if(moved)observations.Add(new(a.Owner,b.Owner,count,"SOURCE_SAFE_INSET_SHIFT_AT_MOST_FOUR_PIXELS"));
            else
            {
                var why=count<0?"FINAL_INK_PAIR_BUDGET_EXHAUSTED":"FINAL_INDEPENDENT_INK_COLLISION";
                current[a.Owner]=a with{Valid=false,Reason=why};current[b.Owner]=b with{Valid=false,Reason=why};
                observations.Add(new(a.Owner,b.Owner,count,why));
            }
        }
        foreach(var key in aliases.Keys.ToArray())
            if(current.TryGetValue(aliases[key].Owner,out var plan))aliases[key]=plan;
        return new(observations,pixels,probes);
    }

    private static Bitmap Raster(FinalTextPlacement.Plan plan,Rectangle box)
    {
        var bitmap=new Bitmap(box.Width,box.Height,PixelFormat.Format32bppArgb);
        try
        {
            using var g=Graphics.FromImage(bitmap);g.Clear(Color.Transparent);
            g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.TranslateTransform(-box.Left,-box.Top);
            FinalTextPlacement.Draw(g,plan);return bitmap;
        }
        catch{bitmap.Dispose();throw;}
    }
    private static FinalTextPlacement.Plan? Shift(FinalTextPlacement.Plan plan,int dx,int dy,Size canvas)
    {
        var rows=plan.Rows.Select(row=>
        {
            var ink=row.Ink;ink.Offset(dx,dy);
            var rotation=new PointF(row.Paint.RotationOrigin.X+dx,row.Paint.RotationOrigin.Y+dy);
            return row with{Origin=new(row.Origin.X+dx,row.Origin.Y+dy),Ink=ink,
                Paint=row.Paint with{RotationOrigin=rotation}};
        }).ToArray();
        if(rows.Any(r=>r.Ink.Left<r.Safe.Left||r.Ink.Top<r.Safe.Top||r.Ink.Right>r.Safe.Right||
            r.Ink.Bottom>r.Safe.Bottom||r.Ink.Left<0||r.Ink.Top<0||r.Ink.Right>canvas.Width||r.Ink.Bottom>canvas.Height))return null;
        var whole=plan.Ink;whole.Offset(dx,dy);
        return plan with{Rows=rows,Ink=whole,Reason=plan.Reason+";FINAL_INK_SAFE_INSET_SEPARATION"};
    }
}
