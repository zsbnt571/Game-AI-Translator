using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ScreenshotTranslationUiTester.CorePipelineV2;

namespace ScreenshotTranslationUiTester;

// This strategy consumes the final write permission, never translation text or
// an image identity. Its immutable source is the only donor. No model fallback.
internal static class LightweightBackgroundRecovery
{
    internal const string StrategyVersion = "light-local-boundary-v3";
    internal static Bitmap Recover(Bitmap source, ReadOnlyBitmapPixelBuffer pixels,
        bool[,] authority, bool[,] excluded, bool[,] protectedPixels,
        IEnumerable<Rectangle> ownerBounds, CancellationToken token)
    {
        using var stage=ProcessingTaskTrace.Current?.Timer.Stage("Lightweight Background");
        var w=source.Width;var h=source.Height;
        var output=source.Clone(new Rectangle(0,0,w,h),PixelFormat.Format32bppArgb);
        var changed=new Dictionary<int,int>();
        try
        {
            var directions=new (int X,int Y)[]{(-1,0),(1,0),(0,-1),(0,1),(-1,-1),(1,1),(-1,1),(1,-1)};
            foreach(var owner in ownerBounds)
            {
                token.ThrowIfCancellationRequested();
                var bounds=Rectangle.Intersect(Rectangle.Inflate(owner,24,24),new(0,0,w,h));
                var bw=bounds.Width;var bh=bounds.Height;if(bw==0||bh==0)continue;
                var safe=new bool[bw*bh];var holes=new List<int>();
                var structure=FindStraightBoundaries(pixels,bounds);
                var red=new float[safe.Length];var green=new float[safe.Length];var blue=new float[safe.Length];
                var sampleColors=new List<Color>();
                for(var y=0;y<bh;y++)for(var x=0;x<bw;x++)
                {
                    int sx=bounds.X+x,sy=bounds.Y+y,i=y*bw+x;
                    var c=pixels.GetPixel(sx,sy);red[i]=c.R;green[i]=c.G;blue[i]=c.B;
                    if(authority[sx,sy]&&owner.Contains(sx,sy)&&!structure[i])holes.Add(i);
                    // The declared exclusion includes all other source text.
                    // Reject sharp local edges as donors (borders/icons/dividers).
                    if(authority[sx,sy]||excluded[sx,sy]||protectedPixels[sx,sy]||structure[i])continue;
                    var edge=0;
                    for(var di=0;di<4;di++)
                    {
                        var d=directions[di];
                        int xx=sx+d.X,yy=sy+d.Y;if(xx<0||yy<0||xx>=w||yy>=h)continue;
                        var n=pixels.GetPixel(xx,yy);
                        edge=Math.Max(edge,Math.Max(Math.Abs(c.R-n.R),Math.Max(Math.Abs(c.G-n.G),Math.Abs(c.B-n.B))));
                    }
                    if(edge>42)continue;
                    safe[i]=true;if((x+y*bw)%5==0)sampleColors.Add(c);
                }
                if(holes.Count==0)continue;
                // A local robust surface is only an initializer for holes with
                // no ray donor. There is no whole-card or whole-dialogue fill.
                if(sampleColors.Count==0)throw new InvalidOperationException("轻量清理没有可靠的局部背景，原图已保留；可选择精细修复后重绘。");
                int Median(Func<Color,int> c){var a=sampleColors.Select(c).Order().ToArray();return a[a.Length/2];}
                var fallback=Color.FromArgb(Median(c=>c.R),Median(c=>c.G),Median(c=>c.B));
                var (components,surfaces)=LocalSurfaces(bw,bh,structure,safe,red,green,blue,fallback);
                foreach(var i in holes)
                {
                    if((i&255)==0)token.ThrowIfCancellationRequested();
                    var x=i%bw;var y=i/bw;float rr=0,gg=0,bb=0,weight=0;
                    var local=surfaces[components[i]];
                    foreach(var d in directions)
                    for(int step=1;step<=64;step++)
                    {
                        int xx=x+d.X*step,yy=y+d.Y*step;if(xx<0||yy<0||xx>=bw||yy>=bh)break;
                        int j=yy*bw+xx;if(structure[j])break;if(!safe[j])continue;
                        // A small saturated icon is not the surrounding surface.
                        if(components[j]!=components[i]||Math.Max(Math.Abs(red[j]-local.R),Math.Max(Math.Abs(green[j]-local.G),Math.Abs(blue[j]-local.B)))>80)continue;
                        float t=1f/(step*step*(d.X*d.X+d.Y*d.Y));
                        rr+=red[j]*t;gg+=green[j]*t;bb+=blue[j]*t;weight+=t;break;
                    }
                    red[i]=weight>0?rr/weight:local.R;green[i]=weight>0?gg/weight:local.G;blue[i]=weight>0?bb/weight:local.B;
                }
                var holeSet=new bool[safe.Length];foreach(var i in holes)holeSet[i]=true;
                for(int pass=0;pass<16;pass++)
                {
                    token.ThrowIfCancellationRequested();
                    foreach(var i in holes)
                    {
                        var x=i%bw;var y=i/bw;float rr=0,gg=0,bb=0;int n=0;
                        for(var di=0;di<4;di++)
                        {
                            var d=directions[di];
                            int xx=x+d.X,yy=y+d.Y;if(xx<0||yy<0||xx>=bw||yy>=bh)continue;
                            int j=yy*bw+xx;if(!holeSet[j]&&!safe[j])continue;
                            rr+=red[j];gg+=green[j];bb+=blue[j];n++;
                        }
                        if(n>0){red[i]=rr/n;green[i]=gg/n;blue[i]=bb/n;}
                    }
                }
                foreach(var i in holes)
                {
                    int sx=bounds.X+i%bw,sy=bounds.Y+i/bw;
                    changed[sy*w+sx]=Color.FromArgb(pixels.GetPixel(sx,sy).A,
                        Math.Clamp((int)MathF.Round(red[i]),0,255),Math.Clamp((int)MathF.Round(green[i]),0,255),Math.Clamp((int)MathF.Round(blue[i]),0,255)).ToArgb();
                }
            }
            token.ThrowIfCancellationRequested();
            var data=output.LockBits(new(0,0,w,h),ImageLockMode.ReadWrite,PixelFormat.Format32bppArgb);
            try{foreach(var p in changed)Marshal.WriteInt32(data.Scan0+(p.Key/w)*data.Stride+(p.Key%w)*4,p.Value);}
            finally{output.UnlockBits(data);}
            ProcessingTaskTrace.Current?.RecordLightweight();
            AppLog.Write("background-lightweight",$"strategy={StrategyVersion} operation={ProcessingTaskTrace.Current?.Timer.OperationId} pid={Environment.ProcessId} changed_pixels={changed.Count} background_model=false");
            return output;
        }
        catch{output.Dispose();throw;}
    }

    private static (int[],List<Color>) LocalSurfaces(int w,int h,bool[] structure,bool[] safe,float[] r,float[] g,float[] b,Color fallback)
    {
        var ids=new int[w*h];Array.Fill(ids,-1);var queue=new int[ids.Length];var surfaces=new List<Color>();
        for(int seed=0;seed<ids.Length;seed++)
        {
            if(structure[seed]||ids[seed]>=0)continue;
            int head=0,tail=1,id=surfaces.Count;queue[0]=seed;ids[seed]=id;
            var colors=new List<Color>();
            while(head<tail)
            {
                int i=queue[head++],x=i%w,y=i/w;
                if(safe[i]&&i%3==0)colors.Add(Color.FromArgb((int)r[i],(int)g[i],(int)b[i]));
                void Visit(int j){if(structure[j]||ids[j]>=0)return;ids[j]=id;queue[tail++]=j;}
                if(x>0)Visit(i-1);if(x+1<w)Visit(i+1);if(y>0)Visit(i-w);if(y+1<h)Visit(i+w);
            }
            int Median(Func<Color,int> c){var a=colors.Select(c).Order().ToArray();return a[a.Length/2];}
            surfaces.Add(colors.Count==0?fallback:Color.FromArgb(Median(c=>c.R),Median(c=>c.G),Median(c=>c.B)));
        }
        return (ids,surfaces);
    }

    // Only long, nearly constant, contrasting straight strokes qualify.
    // This protects observed border pixels and stops donors crossing them;
    // it does not infer a container or grant any new erase permission.
    private static bool[] FindStraightBoundaries(ReadOnlyBitmapPixelBuffer p,Rectangle b)
    {
        int w=b.Width,h=b.Height;var keep=new bool[w*h];
        static int Distance(Color a,Color c)=>Math.Max(Math.Abs(a.R-c.R),Math.Max(Math.Abs(a.G-c.G),Math.Abs(a.B-c.B)));
        for(int axis=0;axis<2;axis++)
        {
            int along=axis==0?w:h,across=axis==0?h:w;
            Color At(int a,int c)=>p.GetPixel(b.X+(axis==0?a:c),b.Y+(axis==0?c:a));
            for(int c=3;c<across-3;c++)
            {
                int a=0;
                while(a<along)
                {
                    int start=a;var color=At(a,c);a++;
                    while(a<along&&Distance(color,At(a,c))<=12)a++;
                    if(a-start<96)continue;
                    int strong=0,n=0;
                    for(int t=start;t<a;t+=4){n++;if(Distance(At(t,c),At(t,c-3))>=18&&Distance(At(t,c),At(t,c+3))>=18)strong++;}
                    if(strong*5<n*4)continue;
                    for(int t=start;t<a;t++)for(int d=-1;d<=1;d++)
                    {int x=axis==0?t:c+d,y=axis==0?c+d:t;keep[y*w+x]=true;}
                }
            }
        }
        return keep;
    }
}
