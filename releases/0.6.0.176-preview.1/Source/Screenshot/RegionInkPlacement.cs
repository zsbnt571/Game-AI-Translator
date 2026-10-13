using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ScreenshotTranslationUiTester.CorePipelineV2;

namespace ScreenshotTranslationUiTester;

internal static class RegionInkPlacement
{
    // Use the product painter, including outlines, shadows and AA. A rectangle
    // enclosing ink is not proof that every enclosed pixel is occupied.
    internal static Point[] Raster(FinalTextPlacement.Plan plan)
    {
        var box=Rectangle.Inflate(Rectangle.Ceiling(plan.Ink),8,8);
        if(box.Width<1||box.Height<1)return [];
        using var bitmap=new Bitmap(box.Width,box.Height,PixelFormat.Format32bppArgb);
        using(var g=Graphics.FromImage(bitmap))
        {
            g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.TranslateTransform(-box.X,-box.Y);
            FinalTextPlacement.Draw(g,plan);
        }
        var bits=bitmap.LockBits(new(Point.Empty,bitmap.Size),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        var points=new List<Point>();
        try
        {
            var pixels=new int[box.Width];
            for(int y=0;y<box.Height;y++)
            {
                Marshal.Copy(bits.Scan0+y*bits.Stride,pixels,0,box.Width);
                for(int x=0;x<box.Width;x++)if((uint)pixels[x]>>24!=0)points.Add(new(x+box.X,y+box.Y));
            }
        }
        finally{bitmap.UnlockBits(bits);}
        return points.ToArray();
    }

    internal static FinalTextPlacement.Plan Place(FinalTextPlacement.Plan original,
        ObservedRegionRecovery.Interior interior,out string decision)
    {
        decision="UNCHANGED";
        if(!original.Valid||original.Rows.Count==0||original.Rows.Any(r=>r.Paint.RotationDegrees!=0))return original;
        int limit=Math.Clamp((int)Math.Ceiling(original.Rows.Max(r=>r.Ink.Height)*.35f),3,12);
        var shifts=new List<(int X,int Y)>();
        for(int y=-limit;y<=limit;y++)for(int x=-limit;x<=limit;x++)shifts.Add((x,y));
        shifts.Sort((a,b)=>(a.X*a.X+a.Y*a.Y).CompareTo(b.X*b.X+b.Y*b.Y));
        int width=interior.Scan.Width,height=interior.Scan.Height;
        var clear=new bool[interior.Domain.Length];
        for(int y=1;y<height-1;y++)for(int x=1;x<width-1;x++)
        {
            bool allowed=true;
            for(int dy=-1;dy<=1&&allowed;dy++)for(int dx=-1;dx<=1;dx++)
                if(!interior.Domain[(y+dy)*width+x+dx]){allowed=false;break;}
            clear[y*width+x]=allowed;
        }
        bool Fits(Point[] ink,int dx,int dy)
        {
            if(ink.Length==0)return false;
            foreach(var p in ink)
            {
                int sx=p.X+dx,sy=p.Y+dy;
                if(!interior.Scan.Contains(sx,sy)||!clear[(sy-interior.Scan.Top)*width+sx-interior.Scan.Left])return false;
            }
            return true;
        }
        FinalTextPlacement.Plan Move(FinalTextPlacement.Plan plan,int dx,int dy)
        {
            var rows=plan.Rows.Select(r=>{var ink=r.Ink;ink.Offset(dx,dy);return r with{Origin=new(r.Origin.X+dx,r.Origin.Y+dy),Ink=ink,Safe=interior.Scan};}).ToArray();
            var union=plan.Ink;union.Offset(dx,dy);return plan with{Rows=rows,Ink=union};
        }
        var firstInk=Raster(original);
        if(Fits(firstInk,0,0)){decision="RASTER_INK_AND_ONE_PIXEL_CLEARANCE_VALID";return original;}
        foreach(var d in shifts)
            if(Fits(firstInk,d.X,d.Y))
            {decision=$"RASTER_POSITION dx={d.X} dy={d.Y}";return Move(original,d.X,d.Y) with{Reason=original.Reason+";"+decision};}

        // A closed label may have inherited a wrap or excessive leading from
        // a narrower estimated rectangle. Reconsider only that label's rows,
        // using its actual raster space; never move prose between owners.
        bool compact=(interior.Closed||interior.OpposedRules)&&original.Rows.Count is >1 and <=3&&
            original.Rows.All(r=>r.Paint.Family==original.Rows[0].Paint.Family&&r.Paint.Weight==original.Rows[0].Paint.Weight&&
                r.Paint.Fill==original.Rows[0].Paint.Fill&&Math.Abs(r.Paint.Size-original.Rows[0].Paint.Size)<.01f);
        FinalTextPlacement.Plan PackRows(FinalTextPlacement.Plan plan)
        {
            var rows=new List<FinalTextPlacement.Row>();float next=0;
            foreach(var row in plan.Rows)
            {
                var ink=Raster(plan with{Rows=[row],Ink=row.Ink});
                float dy=next-ink.Min(p=>p.Y);
                var bounds=row.Ink;bounds.Offset(0,dy);
                rows.Add(row with{Origin=new(row.Origin.X,row.Origin.Y+dy),Ink=bounds});
                next+=ink.Max(p=>p.Y)-ink.Min(p=>p.Y)+2;
            }
            var union=rows.Select(r=>r.Ink).Aggregate(RectangleF.Union);
            int offset=(int)MathF.Round(original.Ink.Top+original.Ink.Height/2-union.Top-union.Height/2);
            return Move(plan with{Rows=rows,Ink=union},0,offset);
        }
        FinalTextPlacement.Plan? TryCompact(FinalTextPlacement.Plan plan,out string kind)
        {
            kind="";if(!compact)return null;
            var packed=PackRows(plan);var ink=Raster(packed);
            // The observed source run is also owned space. An earlier narrow
            // wrap rectangle must not discard space occupied by the source.
            // This uses no inferred control edge or horizontal rule endpoint.
            float left=Math.Min(original.Rows.Min(r=>r.Safe.Left),interior.SourceText.Left);
            float right=Math.Max(original.Rows.Max(r=>r.Safe.Right),interior.SourceText.Right);
            bool OwnedX(Point[] pixels,int dx)=>interior.Closed||pixels.All(p=>p.X+dx>=left&&p.X+dx<right);
            foreach(var d in shifts)if(OwnedX(ink,d.X)&&Fits(ink,d.X,d.Y)){kind="COMPACT_ROW_GAPS";return Move(packed,d.X,d.Y);}
            using var bitmap=new Bitmap(1,1);using var measure=Graphics.FromImage(bitmap);
            var paint=plan.Rows[0].Paint;
            var line=new NativeVisualLine(original.Text,new RectangleF(original.Rows[0].Origin,interior.Scan.Size));
            var single=FinalTextPlacement.Freeze(measure,original.Owner,original.Text,[line],interior.Scan,[],paint,new Size(int.MaxValue/2,int.MaxValue/2));
            if(single.Rows.Count!=1)return null;
            var row=single.Rows[0];var bounds=row.Ink;
            float dx=original.Ink.Left+original.Ink.Width/2-bounds.Left-bounds.Width/2;
            float dy=original.Ink.Top+original.Ink.Height/2-bounds.Top-bounds.Height/2;
            bounds.Offset(dx,dy);
            single=single with{Valid=true,Rows=[row with{Origin=new(row.Origin.X+dx,row.Origin.Y+dy),Ink=bounds}],Ink=bounds};
            ink=Raster(single);
            foreach(var d in shifts)if(OwnedX(ink,d.X)&&Fits(ink,d.X,d.Y)){kind="REMOVE_UNNECESSARY_LABEL_WRAP";return Move(single,d.X,d.Y);}
            return null;
        }
        if(TryCompact(original,out var repack) is {} compacted)
        {decision=$"RASTER_{repack};FONT_UNCHANGED;AA_CLEARANCE=1";return compacted with{Reason=original.Reason+";"+decision};}

        // Search a bounded, minimal em reduction, keeping row content and count.
        // A failed search remains an explicit unresolved result, never English.
        float initial=original.Rows.Min(r=>r.Paint.Size);
        float maximum=Math.Min(5,initial*.15f);
        using var measuring=new Bitmap(1,1);using var g=Graphics.FromImage(measuring);
        for(float decrease=.25f;decrease<=maximum+.001f;decrease+=.25f)
        {
            var rows=new List<FinalTextPlacement.Row>();var union=RectangleF.Empty;
            foreach(var row in original.Rows)
            {
                var paint=row.Paint with{Size=row.Paint.Size-decrease};
                if(paint.Size<9)break;
                var line=new NativeVisualLine(row.Text,new RectangleF(row.Origin,new SizeF(interior.Scan.Width,interior.Scan.Height)));
                var probe=FinalTextPlacement.Freeze(g,original.Owner,row.Text,[line],interior.Scan,[],paint,new Size(int.MaxValue/2,int.MaxValue/2));
                if(probe.Rows.Count!=1)break;
                var r=probe.Rows[0];var dx=row.Ink.Left+row.Ink.Width/2-r.Ink.Left-r.Ink.Width/2;
                var dy=row.Ink.Top+row.Ink.Height/2-r.Ink.Top-r.Ink.Height/2;
                var ink=r.Ink;ink.Offset(dx,dy);
                r=r with{Origin=new(r.Origin.X+dx,r.Origin.Y+dy),Ink=ink};rows.Add(r);
                union=union.IsEmpty?ink:RectangleF.Union(union,ink);
            }
            if(rows.Count!=original.Rows.Count)continue;
            var candidate=original with{Rows=rows,Ink=union};var pixels=Raster(candidate);
            foreach(var d in shifts)
                if(Fits(pixels,d.X,d.Y))
                {decision=$"RASTER_MINIMUM_SIZE_ADAPTATION emDelta={-decrease:F2} dx={d.X} dy={d.Y};AA_CLEARANCE=1";return Move(candidate,d.X,d.Y) with{Reason=original.Reason+";"+decision};}
            if(TryCompact(candidate,out repack) is {} adapted)
            {decision=$"RASTER_{repack};emDelta={-decrease:F2};AA_CLEARANCE=1";return adapted with{Reason=original.Reason+";"+decision};}
        }
        decision="RASTER_CLEARANCE_UNRESOLVED_TEXT_RETAINED_NOT_ACCEPTED";
        return original;
    }
}
