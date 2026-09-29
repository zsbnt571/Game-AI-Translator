namespace ScreenshotTranslationUiTester.CorePipelineV2;

internal sealed record SourceSurfaceAuthorityEvidence(string SourceId,Rectangle Bounds,bool Applied,
    int Before,int Added,string Reason,float[] SideSupport,string SurfaceRgb);

/// <summary>Bounded cleanup completion only where all four source sides prove one plain material.</summary>
internal static class SourceSurfaceAuthority
{
    internal static bool[,] Complete(ReadOnlyBitmapPixelBuffer source,VisualBlock block,bool[,] original,
        Rectangle search,bool[,] protection,List<SourceSurfaceAuthorityEvidence> trace)
    {
        bool[,]? result=null;
        foreach(var line in block.Lines)
        {
            var rect=Rectangle.Intersect(Rectangle.Inflate(Rectangle.Round(line.Bounds),2,2),search);
            if(rect.Width<rect.Height*2 || rect.Width<12 || rect.Height<6)continue;
            var sides=new List<Color>[] {[],[],[],[]};
            for(var d=1;d<=3;d++)
            {
                for(var x=rect.Left;x<rect.Right;x++)
                {Sample(x,rect.Top-d,sides[0]);Sample(x,rect.Bottom-1+d,sides[1]);}
                for(var y=rect.Top;y<rect.Bottom;y++)
                {Sample(rect.Left-d,y,sides[2]);Sample(rect.Right-1+d,y,sides[3]);}
            }
            void Sample(int x,int y,List<Color> into)
            {
                if(x>=0&&y>=0&&x<source.Width&&y<source.Height&&!protection[x,y]&&
                    !block.Lines.Any(l=>l.Bounds.Contains(x+.5f,y+.5f)))into.Add(source.GetPixel(x,y));
            }
            if(sides.Any(s=>s.Count<12))continue;
            var all=sides.SelectMany(x=>x).ToArray();
            var dominant=all.GroupBy(c=>(c.R/12,c.G/12,c.B/12)).OrderByDescending(g=>g.Count()).First().ToArray();
            var color=Color.FromArgb((int)dominant.Average(c=>c.R),(int)dominant.Average(c=>c.G),(int)dominant.Average(c=>c.B));
            bool Near(Color c)=>Math.Max(Math.Abs(c.R-color.R),Math.Max(Math.Abs(c.G-color.G),Math.Abs(c.B-color.B)))<=14;
            var support=sides.Select(s=>s.Count(Near)/(float)s.Count).ToArray();
            var before=0;var blocked=0;
            for(var y=rect.Top;y<rect.Bottom;y++)for(var x=rect.Left;x<rect.Right;x++)
            {if(original[x,y])before++;if(protection[x,y])blocked++;}
            var sourceNear=0; for(var y=rect.Top;y<rect.Bottom;y++)for(var x=rect.Left;x<rect.Right;x++)if(Near(source.GetPixel(x,y)))sourceNear++;
            var accepted=support.All(f=>f>=.85f)&&sourceNear>=rect.Width*rect.Height*.60 && before>=Math.Max(5,rect.Width*rect.Height*.006)&&blocked==0;
            var added=0;
            if(accepted)
            {
                result??=(bool[,])original.Clone();
                for(var y=rect.Top;y<rect.Bottom;y++)for(var x=rect.Left;x<rect.Right;x++)
                {if(!result[x,y])added++;result[x,y]=true;}
            }
            trace.Add(new(line.SourceId,rect,accepted,before,added,accepted?
                "FOUR_SIDED_SOURCE_MATERIAL_COMPLETE_CLEANUP":"SOURCE_SURFACE_OR_PROTECTION_GATE_REJECTED",
                support,$"#{color.R:X2}{color.G:X2}{color.B:X2}"));
        }
        return result??original;
    }
}
