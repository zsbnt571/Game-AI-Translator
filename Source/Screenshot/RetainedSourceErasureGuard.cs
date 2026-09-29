using System.Drawing;
namespace ScreenshotTranslationUiTester.CorePipelineV2;

/// <summary>Write permission against source that the current transaction cannot erase.</summary>
internal static class RetainedSourceErasureGuard
{
    internal const string Failure="CLEANUP_TOUCHES_RETAINED_SOURCE_BLOCK";
    internal const int MaximumPixels=1_000_000;
    internal sealed record Region(string BlockId,Rectangle Bounds,PointF[] Polygon);
    internal sealed record Conflict(string BlockId,string RetainedBlockId,int CheckedPixels,string Reason);
    internal sealed record HaloClip(string BlockId,int Pixels,int CheckedPixels,string Reason);
    internal static Conflict? Observe(string blockId,bool[,] mask,Rectangle scan,
        IReadOnlyList<Region> retained,ref int checkedPixels)
        =>Scan(blockId,mask,null,scan,retained,null,ref checkedPixels,null);
    internal static Conflict? Reconcile(string blockId,bool[,] mask,bool[,] closure,Rectangle scan,
        IReadOnlyList<Region> retained,IReadOnlyList<Region> owned,ref int checkedPixels,List<HaloClip> clips)
        =>Scan(blockId,mask,closure,scan,retained,owned,ref checkedPixels,clips);
    private static Conflict? Scan(string blockId,bool[,] mask,bool[,]? closure,Rectangle scan,
        IReadOnlyList<Region> retained,IReadOnlyList<Region>? owned,ref int checkedPixels,List<HaloClip>? clips)
    {
        var canvas=new Rectangle(0,0,mask.GetLength(0),mask.GetLength(1));
        var ownerPolygons=owned?.Where(line=>line.Polygon.Length>=3).Select(line=>line.Polygon).ToArray()??[];
        var hasOwners=ownerPolygons.Length>0;
        var canClipHalo=closure is not null&&clips is not null&&hasOwners;
        // Cleanup padding is not source text ownership. Stage all exclusions until
        // the entire conflict scan succeeds; a genuine source-polygon intersection
        // still withdraws the block without leaving a partly changed mask.
        var exclusions=new HashSet<Point>();
        foreach(var region in retained)
        {
            if(region.BlockId==blockId)continue;
            var common=Rectangle.Intersect(canvas,Rectangle.Intersect(scan,region.Bounds));
            if(common.Width<=0||common.Height<=0||region.Polygon.Length<3)continue;
            for(var y=common.Top;y<common.Bottom;y++)for(var x=common.Left;x<common.Right;x++)
            {
                if(checkedPixels>=MaximumPixels)
                    return new(blockId,region.BlockId,checkedPixels,"RETAINED_SOURCE_PERMISSION_BUDGET_EXHAUSTED");
                checkedPixels++;
                if(mask[x,y]&&ContainsPixelCenter(region.Polygon,x,y))
                {
                    if(canClipHalo&&!ownerPolygons.Any(p=>ContainsPixelCenter(p,x,y)))exclusions.Add(new(x,y));
                    else return new(blockId,region.BlockId,checkedPixels,Failure);
                }
            }
        }
        if(exclusions.Count>0)
        {
            foreach(var p in exclusions){mask[p.X,p.Y]=false;closure![p.X,p.Y]=false;}
            clips!.Add(new(blockId,exclusions.Count,checkedPixels,"CLEANUP_HALO_EXCLUDED_FROM_RETAINED_SOURCE"));
        }
        return null;
    }
    // GDI path visibility quantizes some slanted far edges. Write permission
    // uses the actual pixel centre for both retained and owned source polygons.
    internal static bool ContainsPixelCenter(PointF[] polygon,int x,int y)
    {
        double px=x+.5,py=y+.5;var inside=false;
        for(int i=0,j=polygon.Length-1;i<polygon.Length;j=i++)
        {
            var a=polygon[j];var b=polygon[i];
            if((a.Y>py)!=(b.Y>py)&&px<((double)b.X-a.X)*(py-a.Y)/((double)b.Y-a.Y)+a.X)inside=!inside;
        }
        return inside;
    }
}
