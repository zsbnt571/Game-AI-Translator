using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ScreenshotTranslationUiTester.CorePipelineV2;

namespace ScreenshotTranslationUiTester;

// All decisions come from the immutable raster inside the observed text area.
// A line segment is never treated as a complete control rectangle. Regions are
// connected source material with enclosed holes, not the largest exterior colour.
internal static class ObservedRegionRecovery
{
    internal sealed record Evidence(string Owner,Rectangle Scan,Rectangle Text,
        bool Closed,double InnerSupport,double Coverage,double PlaneError95,
        int Donors,int Preserved,int Filled,string Reason,int CompletedSourcePixels=0);
    internal sealed record Interior(Rectangle Scan,bool[] Domain,bool Closed=false,bool OpposedRules=false,Rectangle SourceText=default);
    internal sealed record Result(bool[,] ModelAuthority,RegionWriteBuffer Pixels,List<Evidence> Evidence,
        Dictionary<string,Interior> Interiors)
    {
        internal bool[,] ModelAuthority {get;private set;}=ModelAuthority;
        internal List<object> Rejections {get;}=[];
        internal long BackgroundBufferBytes=>Buffer.ByteLength(ModelAuthority)+Pixels.BufferBytes;
        internal long LayoutBufferBytes=>Interiors.Values.Sum(i=>(long)i.Domain.Length);
        internal void ReleaseBackgroundBuffers()
        {
            // These are no longer used after compositing. Do not retain the
            // image-sized authority or per-pixel dictionary during typography.
            Pixels.Release();ModelAuthority=new bool[0,0];
        }
        internal void ReleaseLayoutBuffers(){Interiors.Clear();Interiors.TrimExcess();}
        internal FinalTextPlacement.Plan Place(FinalTextPlacement.Plan original,out string decision)
        {
            decision="UNCHANGED";
            if(!original.Valid||!Interiors.TryGetValue(original.Owner,out var interior)||
                original.Rows.Any(r=>r.Paint.RotationDegrees!=0))return original;
            return RegionInkPlacement.Place(original,interior,out decision);
        }

        internal void Composite(Bitmap? target,CancellationToken token)
        {
            if(target is null||Pixels.Count==0)return;
            var b=target.LockBits(new(Point.Empty,target.Size),ImageLockMode.ReadWrite,PixelFormat.Format32bppArgb);
            try{int n=0;foreach(var p in Pixels){if((n++&4095)==0)token.ThrowIfCancellationRequested();Marshal.WriteInt32(b.Scan0+p.Key/target.Width*b.Stride+p.Key%target.Width*4,p.Value);}}
            finally{target.UnlockBits(b);}
        }
    }
    private sealed record Region(Rectangle Scan,Rectangle Text,RegionColor[] Colors,bool[] Domain,bool[] Donors,
        bool Closed,double Support,double Coverage,double Error,bool[] Rules);
    internal static Result Build(ReadOnlyBitmapPixelBuffer source,IReadOnlyList<VisualBlock> blocks,
        Dictionary<string,(bool[,] Authority,bool[,] Closure)> masks,Dictionary<string,Rectangle> bounds,
        bool[,] authority,bool[,] protectedPixels,Func<string,bool[,]> sourceCore,CancellationToken token,string? diagnostics=null,
        IReadOnlyDictionary<string,Point[]>? completeSourceMaterial=null)
    {
        using var stage=ProcessingTaskTrace.Current?.Timer.Stage("Observed Region Recovery");
        using var workspace=new RegionWorkspace();
        var rejected=new List<object>();var writes=new RegionWriteBuffer(checked(source.Width*source.Height));var rows=new List<Evidence>();var interiors=new Dictionary<string,Interior>();
        foreach(var block in blocks)
        {
            token.ThrowIfCancellationRequested();
            if(!masks.TryGetValue(block.BlockId,out var mask)||!bounds.TryGetValue(block.BlockId,out var owner))continue;
            var region=ObserveWithWorkspace(source,Rectangle.Ceiling(block.Bounds),token,workspace);
            if(region is null)continue;
            var scan=region.Scan;int w=scan.Width,h=scan.Height;
            var trustedCenter=RegionColor.Median(region.Colors,region.Donors);
            var material=RegionMaterialEvidence.Check(scan,region.Text,region.Colors,region.Domain,region.Donors,mask.Authority,block.Lines.Min(l=>l.Bounds.Height),token,trustedCenter);
            if(material.Reject)
            {
                rejected.Add(new{Owner=block.BlockId,region.Scan,region.Text,Decision=material,MaskUnchanged=true,LayoutUsesBaseline=true});
                continue;
            }
            var rules=region.Rules;
            var domain=RegionSourceStrokes.Complete(scan,region.Text,region.Colors,region.Domain,region.Donors,rules,
                mask.Authority,sourceCore(block.BlockId),protectedPixels,token,trustedCenter);
            int completed=0;for(int i=0;i<domain.Length;i++)if(domain[i]&&!region.Domain[i])completed++;
            if(completeSourceMaterial?.TryGetValue(block.BlockId,out var proven)==true&&
                proven.Any(p=>!scan.Contains(p)||!domain[(p.Y-scan.Top)*w+p.X-scan.Left]))
            {
                // Reject before changing any shared permission or layout. A
                // connected background cannot clip independently proven letters.
                rejected.Add(new{Owner=block.BlockId,Reason="REGION_CLIPS_PROVEN_SOURCE_GLYPH_MATERIAL",
                    MaskUnchanged=true,LayoutUsesBaseline=true});continue;
            }
            var layout=RegionSourceStrokes.Layout(scan,region.Colors,domain,region.Donors,protectedPixels,trustedCenter);
            if(diagnostics is not null)
            {
                using var diag=ProcessingTaskTrace.Current?.Timer.Stage("Region Diagnostic Files");
                var dir=Path.Combine(diagnostics,"REGION-MAPS",block.BlockId);Directory.CreateDirectory(dir);
                void Save(bool[] map,string name)
                {
                    using var image=new Bitmap(w,h,PixelFormat.Format32bppArgb);
                    var bits=image.LockBits(new(Point.Empty,image.Size),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
                    try{var row=new int[w];for(int y=0;y<h;y++){for(int x=0;x<w;x++)row[x]=map[y*w+x]?Color.White.ToArgb():Color.Black.ToArgb();Marshal.Copy(row,0,bits.Scan0+y*bits.Stride,w);}}
                    finally{image.UnlockBits(bits);}image.Save(Path.Combine(dir,name));
                }
                Save(region.Donors,"DONORS.png");Save(region.Domain,"R4-CONNECTED-DOMAIN.png");Save(rules,"RULES.png");
                Save(domain,"SOURCE-CLEANUP-DOMAIN.png");Save(layout,"LAYOUT-DOMAIN.png");
                File.WriteAllText(Path.Combine(dir,"GEOMETRY.json"),System.Text.Json.JsonSerializer.Serialize(new{Scan=scan,Text=region.Text,CompletedSourcePixels=completed}));
            }
            // An open surface proves only the observed text and its antialias halo.
            // It cannot claim a neighbouring icon or turn a rule endpoint into a box.
            var textHalo=Rectangle.Inflate(region.Text,3,3);
            var allowed=new List<int>();int kept=0;
            for(int y=owner.Top;y<owner.Bottom;y++)for(int x=owner.Left;x<owner.Right;x++)
            {
                if(!mask.Authority[x,y])continue;
                int i=(y-scan.Top)*w+x-scan.Left;
                if(!scan.Contains(x,y)||!domain[i]||protectedPixels[x,y]||(!region.Closed&&!textHalo.Contains(x,y)))
                {mask.Authority[x,y]=false;mask.Closure[x,y]=false;kept++;}
                else allowed.Add(i);
            }
            if(allowed.Count==0)continue;
            bool opposed=block.Lines.Count==1&&HasOpposedRules(rules,scan,region.Text);
            interiors.Add(block.BlockId,new(scan,layout,region.Closed,opposed,region.Text));
            var repaired=RecoverWithWorkspace(region with{Domain=domain},token,workspace);
            foreach(var i in allowed)
            {
                var sx=scan.X+i%w;var sy=scan.Y+i/w;
                writes.TryAdd(sy*source.Width+sx,repaired[i]);
            }
            rows.Add(new(block.BlockId,scan,region.Text,region.Closed,region.Support,region.Coverage,
                region.Error,region.Donors.Count(v=>v),kept,allowed.Count,
                region.Closed?"CONNECTED_INTERIOR_WITH_SOURCE_CONTOUR":"SOURCE_SUPPORTED_OPEN_PLANAR_TEXT_SURFACE",completed));
        }
        // Rebuild the union after per-owner contour clipping. A neighbour retains
        // its own permission; one owner's region cannot silently erase its mask.
        Array.Clear(authority);
        foreach(var pair in masks)
        {var b=bounds[pair.Key];for(int y=b.Top;y<b.Bottom;y++)for(int x=b.Left;x<b.Right;x++)if(pair.Value.Authority[x,y])authority[x,y]=true;}
        var model=(bool[,])authority.Clone();foreach(var pixel in writes)model[pixel.Key%source.Width,pixel.Key/source.Width]=false;
        var result=new Result(model,writes,rows,interiors);result.Rejections.AddRange(rejected);return result;
    }

    private static Region? Observe(ReadOnlyBitmapPixelBuffer source,Rectangle text,CancellationToken token)
    {
        using var workspace=new RegionWorkspace();
        return ObserveWithWorkspace(source,text,token,workspace);
    }

    private static Region? ObserveWithWorkspace(ReadOnlyBitmapPixelBuffer source,Rectangle text,CancellationToken token,RegionWorkspace workspace)
    {
        text=Rectangle.Intersect(text,new(Point.Empty,source.Size));
        if(text.Width<12||text.Height<7||text.Width*text.Height>500000)return null;
        int margin=Math.Clamp(text.Height,16,64);
        var scan=Rectangle.Intersect(Rectangle.Inflate(text,margin,margin),new(Point.Empty,source.Size));
        int w=scan.Width,h=scan.Height,n=w*h;
        var colors=new RegionColor[n];var hist=new int[4096];
        for(int y=0;y<h;y++)for(int x=0;x<w;x++)
        {var c=source.GetPixel(scan.X+x,scan.Y+y);colors[y*w+x]=c;if(text.Contains(scan.X+x,scan.Y+y))hist[(c.R/16)*256+(c.G/16)*16+c.B/16]++;}
        var rules=ObservedRules(colors,scan,text);
        var peaks=Enumerable.Range(0,hist.Length).OrderByDescending(i=>hist[i]).Take(3);
        Span<int> channels=stackalloc int[768];
        // Candidate colors share the same scan. Scratch belongs to this call;
        // each live prefix is reset before reuse and never escapes as a cache.
        bool[]? domain=null,donors=null;
        foreach(var peak in peaks)
        {
            token.ThrowIfCancellationRequested();
            if(hist[peak]<text.Width*text.Height*.06)continue;
            channels.Clear();int sampleCount=0;
            for(int y=text.Top;y<text.Bottom;y++)for(int x=text.Left;x<text.Right;x++)
            {
                var c=colors[(y-scan.Top)*w+x-scan.Left];
                if((c.R/16)*256+(c.G/16)*16+c.B/16!=peak)continue;
                channels[c.R]++;channels[256+c.G]++;channels[512+c.B]++;sampleCount++;
            }
            var center=RegionColor.FromHistogram(channels,sampleCount);
            workspace.Observe(n);
            var near=workspace.Near;var expanded=workspace.Expanded;var closed=workspace.Closed;
            for(int i=0;i<n;i++)near[i]=Distance(colors[i],center)<=32;
            Array.Clear(expanded,0,n);Close(near,w,h,expanded,closed);
            // Closing may connect antialiased background islands, but cannot
            // bridge an independently observed thin source rule. Keep just its
            // raster pixels; no rectangle is inferred from its endpoints.
            for(int i=0;i<n;i++)if(rules[i])closed[i]=false;
            var labels=workspace.Labels;Array.Fill(labels,-1,0,n);var queue=workspace.Queue;
            int next=0,best=-1,bestSupport=0;
            for(int seed=0;seed<n;seed++)
            {
                if(!closed[seed]||labels[seed]>=0)continue;
                int head=0,tail=1,support=0;queue[0]=seed;labels[seed]=next;
                while(head<tail)
                {
                    int i=queue[head++],x=i%w,y=i/w;
                    if(near[i]&&text.Contains(scan.X+x,scan.Y+y))support++;
                    void Visit(int j){if(closed[j]&&labels[j]<0){labels[j]=next;queue[tail++]=j;}}
                    if(x>0)Visit(i-1);if(x+1<w)Visit(i+1);if(y>0)Visit(i-w);if(y+1<h)Visit(i+w);
                }
                if(support>bestSupport){bestSupport=support;best=next;}next++;
            }
            double supportRatio=bestSupport/(double)(text.Width*text.Height);
            if(best<0||supportRatio<.38)continue;
            var plate=workspace.Plate;Array.Clear(plate,0,n);bool touches=false;
            for(int i=0;i<n;i++)if(labels[i]==best){plate[i]=true;int x=i%w,y=i/w;if(x==0||x==w-1||y==0||y==h-1)touches=true;}
            // Flood every exterior edge; rounded ends and disconnected borders
            // remain real source topology, not a guessed rectangle.
            var exterior=workspace.Exterior;Array.Clear(exterior,0,n);int qh=0,qt=0;
            void Seed(int i){if(!plate[i]&&!exterior[i]){exterior[i]=true;queue[qt++]=i;}}
            for(int x=0;x<w;x++){Seed(x);Seed((h-1)*w+x);}for(int y=0;y<h;y++){Seed(y*w);Seed(y*w+w-1);}
            while(qh<qt){int i=queue[qh++],x=i%w,y=i/w;if(x>0)Seed(i-1);if(x+1<w)Seed(i+1);if(y>0)Seed(i-w);if(y+1<h)Seed(i+w);}
            domain??=new bool[n];int coverage=0;
            for(int i=0;i<n;i++){domain[i]=!exterior[i]&&!rules[i];if(domain[i]&&text.Contains(scan.X+i%w,scan.Y+i/w))coverage++;}
            double rate=coverage/(double)(text.Width*text.Height);
            if(rate<.82)continue;
            donors??=new bool[n];Array.Clear(donors);int donorCount=0;
            for(int y=1;y<h-1;y++)for(int x=1;x<w-1;x++)
            {
                int i=y*w+x;if(!plate[i]||!near[i])continue;
                bool smooth=true;
                for(int dy=-1;dy<=1&&smooth;dy++)for(int dx=-1;dx<=1;dx++)
                    if(!near[i+dy*w+dx]||Distance(colors[i],colors[i+dy*w+dx])>16){smooth=false;break;}
                if(smooth){donors[i]=true;donorCount++;}
            }
            if(donorCount<Math.Max(24,text.Width*text.Height*.12))continue;
            double error=PlaneError(colors,donors,w,h);
            if(touches&&error>5.5)error=QuadraticError(colors,donors,w,h);
            if(touches&&(rate<.94||supportRatio<.65||error>5.5))continue;
            return new(scan,text,colors,domain,donors,!touches,supportRatio,rate,error,rules);
        }
        return null;
    }

    private static int[] Recover(Region region,CancellationToken token)
    {
        using var workspace=new RegionWorkspace();
        return RecoverWithWorkspace(region,token,workspace);
    }

    private static int[] RecoverWithWorkspace(Region region,CancellationToken token,RegionWorkspace workspace)
    {
        int w=region.Scan.Width,h=region.Scan.Height,n=w*h;
        workspace.Recover(n);
        var ids=workspace.Labels;Array.Fill(ids,-1,0,n);var queue=workspace.Queue;int head=0,tail=0;
        for(int i=0;i<n;i++)if(region.Donors[i]){ids[i]=i;queue[tail++]=i;}
        while(head<tail)
        {
            if((head&4095)==0)token.ThrowIfCancellationRequested();
            int i=queue[head++],x=i%w,y=i/w;
            void Visit(int j){if(region.Domain[j]&&ids[j]<0){ids[j]=ids[i];queue[tail++]=j;}}
            if(x>0)Visit(i-1);if(x+1<w)Visit(i+1);if(y>0)Visit(i-w);if(y+1<h)Visit(i+w);
        }
        var red=workspace.Red;var green=workspace.Green;var blue=workspace.Blue;int holes=0;
        for(int i=0;i<n;i++)
        {
            var c=region.Colors[ids[i]>=0?ids[i]:i];red[i]=c.R;green[i]=c.G;blue[i]=c.B;
            // The propagation queue is exhausted. Reuse its prefix for holes,
            // in the same ascending order as the original List<int>.
            if(region.Domain[i]&&!region.Donors[i]&&ids[i]>=0)queue[holes++]=i;
        }
        for(int pass=0;pass<16;pass++)
        {
            token.ThrowIfCancellationRequested();
            for(int hole=0;hole<holes;hole++)
            {
                int i=queue[hole];
                int x=i%w,y=i/w,count=0;float rr=0,gg=0,bb=0;
                void Add(int j){if(!region.Domain[j]||ids[j]<0)return;rr+=red[j];gg+=green[j];bb+=blue[j];count++;}
                if(x>0)Add(i-1);if(x+1<w)Add(i+1);if(y>0)Add(i-w);if(y+1<h)Add(i+w);
                if(count>0){red[i]=rr/count;green[i]=gg/count;blue[i]=bb/count;}
            }
        }
        var output=new int[n];for(int i=0;i<n;i++)output[i]=Color.FromArgb(region.Colors[i].A,
            Math.Clamp((int)MathF.Round(red[i]),0,255),Math.Clamp((int)MathF.Round(green[i]),0,255),Math.Clamp((int)MathF.Round(blue[i]),0,255)).ToArgb();
        return output;
    }
    private static void Close(bool[] input,int w,int h,bool[] expanded,bool[] result)
    {
        for(int y=0;y<h;y++)for(int x=0;x<w;x++)
        {for(int dy=-1;dy<=1&&!expanded[y*w+x];dy++)for(int dx=-1;dx<=1;dx++){int xx=x+dx,yy=y+dy;if(xx>=0&&yy>=0&&xx<w&&yy<h&&input[yy*w+xx]){expanded[y*w+x]=true;break;}}}
        for(int y=0;y<h;y++)for(int x=0;x<w;x++)
        {bool keep=true;for(int dy=-1;dy<=1&&keep;dy++)for(int dx=-1;dx<=1;dx++){int xx=Math.Clamp(x+dx,0,w-1),yy=Math.Clamp(y+dy,0,h-1);if(!expanded[yy*w+xx]){keep=false;break;}}result[y*w+x]=keep;}
    }
    private static bool[] ObservedRules(RegionColor[] colors,Rectangle scan,Rectangle text)
    {
        int w=scan.Width,h=scan.Height;var keep=new bool[w*h];
        int minimum=Math.Clamp((int)Math.Ceiling(text.Height*1.6f),24,96);
        for(int axis=0;axis<2;axis++)
        {
            int along=axis==0?w:h,across=axis==0?h:w;
            Color At(int a,int c)=>colors[axis==0?c*w+a:a*w+c];
            for(int c=3;c<across-3;c++)
            {
                // A glyph's middle crossbar is not an outer contour.
                int absolute=c+(axis==0?scan.Top:scan.Left);
                float lo=axis==0?text.Top:text.Left,hi=axis==0?text.Bottom:text.Right;
                float inset=axis==0?text.Height*.25f:Math.Min(text.Height*.4f,text.Width*.15f);
                if(absolute>lo+inset&&absolute<hi-inset)continue;
                int a=0;
                while(a<along)
                {
                    int start=a;var color=At(a,c);a++;
                    while(a<along&&Distance(color,At(a,c))<=12)a++;
                    if(a-start<minimum)continue;
                    int strong=0,count=0;
                    for(int t=start;t<a;t+=2){count++;if(Distance(At(t,c),At(t,c-3))>=12&&Distance(At(t,c),At(t,c+3))>=12)strong++;}
                    if(strong*5<count*4)continue;
                    for(int t=start;t<a;t++)for(int d=-1;d<=1;d++)
                    {int x=axis==0?t:c+d,y=axis==0?c+d:t;keep[y*w+x]=true;}
                }
            }
        }
        return keep;
    }
    private static double PlaneError(RegionColor[] colors,bool[] donors,int w,int h)
    {
        Span<double> v=stackalloc double[3];var m=new double[3,6];int step=Math.Max(1,colors.Length/6000);var samples=new List<int>();
        for(int i=0;i<colors.Length;i+=step)
        {
            if(!donors[i])continue;samples.Add(i);v[0]=1;v[1]=(i%w-w*.5)/w;v[2]=(i/w-h*.5)/h;
            for(int a=0;a<3;a++){for(int b=0;b<3;b++)m[a,b]+=v[a]*v[b];m[a,3]+=v[a]*colors[i].R;m[a,4]+=v[a]*colors[i].G;m[a,5]+=v[a]*colors[i].B;}
        }
        if(samples.Count<12)return double.MaxValue;
        for(int k=0;k<3;k++)
        {
            if(Math.Abs(m[k,k])<1e-8)return double.MaxValue;double pivot=m[k,k];for(int b=k;b<6;b++)m[k,b]/=pivot;
            for(int a=0;a<3;a++)if(a!=k){double ratio=m[a,k];for(int b=k;b<6;b++)m[a,b]-=ratio*m[k,b];}
        }
        var errors=new List<double>();foreach(var i in samples)
        {double x=(i%w-w*.5)/w,y=(i/w-h*.5)/h;errors.Add(Math.Max(Math.Abs(m[0,3]+x*m[1,3]+y*m[2,3]-colors[i].R),Math.Max(Math.Abs(m[0,4]+x*m[1,4]+y*m[2,4]-colors[i].G),Math.Abs(m[0,5]+x*m[1,5]+y*m[2,5]-colors[i].B))));}
        errors.Sort();return errors[(int)((errors.Count-1)*.95)];
    }
    private static double QuadraticError(RegionColor[] colors,bool[] donors,int w,int h)
    {
        Span<double> v=stackalloc double[6];var m=new double[6,9];var samples=new List<int>();int step=Math.Max(1,colors.Length/6000);
        for(int i=0;i<colors.Length;i+=step)
        {
            if(!donors[i])continue;samples.Add(i);double x=(i%w-w*.5)/w,y=(i/w-h*.5)/h;v[0]=1;v[1]=x;v[2]=y;v[3]=x*x;v[4]=x*y;v[5]=y*y;
            for(int a=0;a<6;a++){for(int b=0;b<6;b++)m[a,b]+=v[a]*v[b];m[a,6]+=v[a]*colors[i].R;m[a,7]+=v[a]*colors[i].G;m[a,8]+=v[a]*colors[i].B;}
        }
        if(samples.Count<48)return double.MaxValue;
        for(int k=0;k<6;k++)
        {
            if(Math.Abs(m[k,k])<1e-8)return double.MaxValue;double pivot=m[k,k];for(int b=k;b<9;b++)m[k,b]/=pivot;
            for(int a=0;a<6;a++)if(a!=k){double ratio=m[a,k];for(int b=k;b<9;b++)m[a,b]-=ratio*m[k,b];}
        }
        var errors=new List<double>();foreach(var i in samples)
        {
            double x=(i%w-w*.5)/w,y=(i/w-h*.5)/h;v[0]=1;v[1]=x;v[2]=y;v[3]=x*x;v[4]=x*y;v[5]=y*y;double error=0;
            for(int b=0;b<3;b++){double pred=0;for(int a=0;a<6;a++)pred+=v[a]*m[a,b+6];error=Math.Max(error,Math.Abs(pred-(b==0?colors[i].R:b==1?colors[i].G:colors[i].B)));}errors.Add(error);
        }
        errors.Sort();return errors[(int)((errors.Count-1)*.95)];
    }
    private static int Distance(Color a,Color b)=>Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B)));
    private static bool HasOpposedRules(bool[] rules,Rectangle scan,Rectangle text)
    {
        bool Across(int y)
        {
            int count=0;for(int x=text.Left;x<text.Right;x++)if(scan.Contains(x,y)&&rules[(y-scan.Top)*scan.Width+x-scan.Left])count++;
            return count>=text.Width*.7f;
        }
        // Two independently observed rails can constrain rows without proving
        // rounded ends or a closed rectangle. Their endpoints are never used
        // to extend horizontal ownership.
        bool above=false,below=false;int margin=Math.Max(3,text.Height/2);
        for(int y=Math.Max(scan.Top,text.Top-margin);y<=text.Top+text.Height/4;y++)if(Across(y))above=true;
        for(int y=text.Bottom-text.Height/4;y<Math.Min(scan.Bottom,text.Bottom+margin);y++)if(Across(y))below=true;
        return above&&below;
    }
}
