namespace ScreenshotTranslationUiTester.CorePipelineV2;

// Local adaptation for newly recovered thin curved letters. It changes only
// admitted glyph pixels. Source neighbors are the boundary conditions, never
// text layout bounds, a translated bitmap, or a sampled whole-panel color.
internal static class SourceStrokeInterpolation
{
    internal sealed record Evidence(bool Applied,string Reason,int Pixels,int Iterations,Rectangle Bounds);
    internal static Evidence Apply(ReadOnlyBitmapPixelBuffer source,Bitmap candidate,bool[,] authority,
        Rectangle bounds,CancellationToken token)
    {
        bounds=Rectangle.Intersect(Rectangle.Inflate(bounds,14,14),new(0,0,source.Width,source.Height));
        if(bounds.Width*bounds.Height>180000)return new(false,"LOCAL_PIXEL_BUDGET",0,0,bounds);
        var width=bounds.Width;var height=bounds.Height;
        var holes=new List<int>();var mask=new bool[width*height];
        var red=new float[mask.Length];var green=new float[mask.Length];var blue=new float[mask.Length];
        for(var y=0;y<height;y++)for(var x=0;x<width;x++)
        {
            var i=y*width+x;var c=source.GetPixel(bounds.X+x,bounds.Y+y);
            red[i]=c.R;green[i]=c.G;blue[i]=c.B;
            if(authority[bounds.X+x,bounds.Y+y]){mask[i]=true;holes.Add(i);}
        }
        if(holes.Count is <12 or >24000)return new(false,"THIN_STROKE_PIXEL_BUDGET",holes.Count,0,bounds);
        foreach(var i in holes)
        {
            var x=i%width;var y=i/width;var samples=new List<(int Index,float Weight,int Dx,int Dy)>();
            foreach(var direction in new[]{(1,0),(-1,0),(0,1),(0,-1),(1,1),(-1,-1),(1,-1),(-1,1)})
            for(var step=1;step<=12;step++)
            {
                var xx=x+direction.Item1*step;var yy=y+direction.Item2*step;
                if(xx<0||yy<0||xx>=width||yy>=height)break;
                var next=yy*width+xx;if(mask[next])continue;
                samples.Add((next,1f/(step*step*(direction.Item1*direction.Item1+direction.Item2*direction.Item2)),direction.Item1,direction.Item2));break;
            }
            // Thick or clipped erasures cannot be reconstructed from this local
            // evidence. Refuse the entire owner before any candidate is changed.
            // Initial values need a nearby source sample, not an opposing ray
            // for every junction pixel. The relaxation below uses all four
            // source boundaries and refuses any hole reaching the scan edge.
            if(samples.Count==0)
                return new(false,"NO_NEAR_SOURCE_BOUNDARY",holes.Count,0,bounds);
            var weight=samples.Sum(s=>s.Weight);
            red[i]=samples.Sum(s=>red[s.Index]*s.Weight)/weight;
            green[i]=samples.Sum(s=>green[s.Index]*s.Weight)/weight;
            blue[i]=samples.Sum(s=>blue[s.Index]*s.Weight)/weight;
        }
        for(var pass=0;pass<64;pass++)
        {
            token.ThrowIfCancellationRequested();
            foreach(var i in holes)
            {
                if(i<width||i>=mask.Length-width||i%width==0||i%width==width-1)
                    return new(false,"SOURCE_EDGE_HOLE",holes.Count,pass,bounds);
                red[i]=(red[i-1]+red[i+1]+red[i-width]+red[i+width])*.25f;
                green[i]=(green[i-1]+green[i+1]+green[i-width]+green[i+width])*.25f;
                blue[i]=(blue[i-1]+blue[i+1]+blue[i-width]+blue[i+width])*.25f;
            }
        }
        token.ThrowIfCancellationRequested();
        foreach(var i in holes)candidate.SetPixel(bounds.X+i%width,bounds.Y+i/width,
            Color.FromArgb(Math.Clamp((int)MathF.Round(red[i]),0,255),Math.Clamp((int)MathF.Round(green[i]),0,255),Math.Clamp((int)MathF.Round(blue[i]),0,255)));
        return new(true,"SOURCE_BOUNDARY_THIN_STROKE_INTERPOLATION",holes.Count,64,bounds);
    }
}
