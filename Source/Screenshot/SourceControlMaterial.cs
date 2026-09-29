namespace ScreenshotTranslationUiTester.CorePipelineV2;

/// <summary>Read-only proof of glyph holes enclosed by a small, solid source control.</summary>
internal static class SourceControlMaterial
{
    internal sealed record Evidence(Rectangle Bounds,Color Surface,Color Fill,IReadOnlyList<Point> Pixels,
        int SurfacePixels,int SolidInteriorPixels,int GlyphComponents,string Reason);
    internal static Evidence? ObserveTinyCell(ReadOnlyBitmapPixelBuffer source,VisualBlock block,
        NativeTextMaterial.Evidence material)
    {
        if(block.SourceCell is not {} cell||cell.Bounds.Height is <5 or >16||block.Lines.Count!=1||
            !material.Accepted||material.Components<2||material.Primary.Count<4)return null;
        var bounds=Rectangle.Intersect(Rectangle.Round(cell.Bounds),new(Point.Empty,source.Size));
        var primary=material.Primary.Where(p=>bounds.Contains(p)).ToArray();
        if(primary.Length<material.Primary.Count*.9f)return null;
        static int Distance(Color a,Color b)=>Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B)));
        var samples=new List<Color>();
        for(var y=bounds.Top;y<bounds.Bottom;y++)for(var x=bounds.Left;x<bounds.Right;x++)
        {var c=source.GetPixel(x,y);if(Distance(c,cell.Surface)<=20)samples.Add(c);}
        if(samples.Count<bounds.Width*bounds.Height*.4f)return null;
        Color Median(IEnumerable<Color> colors){var a=colors.ToArray();int M(Func<Color,int> f)=>a.Select(f).Order().ElementAt(a.Length/2);return Color.FromArgb(M(c=>c.R),M(c=>c.G),M(c=>c.B));}
        var surface=Median(samples);
        // Compression fringes can have broad individual color error while the
        // exposed cell surface is spatially uniform. Compare source bands rather
        // than mistaking per-pixel JPEG noise for an intended gradient.
        var medians=new List<Color>();
        for(var axis=0;axis<2;axis++)for(var band=0;band<3;band++)
        {
            var local=new List<Color>();
            for(var y=bounds.Top;y<bounds.Bottom;y++)for(var x=bounds.Left;x<bounds.Right;x++)
            {
                var at=axis==0?x-bounds.Left:y-bounds.Top;var extent=axis==0?bounds.Width:bounds.Height;
                if(at*3<band*extent||at*3>=(band+1)*extent)continue;
                var color=source.GetPixel(x,y);if(Distance(color,cell.Surface)<=20)local.Add(color);
            }
            if(local.Count<5)return null;medians.Add(Median(local));
        }
        if(medians.Any(a=>medians.Any(b=>Distance(a,b)>12)))return null;
        var fill=Median(primary.Select(p=>source.GetPixel(p.X,p.Y)));
        // These are already independent source glyphs. A display accessibility
        // ratio is not evidence that a pale native glyph does not exist.
        if(Distance(fill,surface)<55)return null;
        var strokes=material.Material.Where(p=>bounds.Contains(p)&&Distance(source.GetPixel(p.X,p.Y),surface)>=8).ToArray();
        return new(bounds,surface,fill,strokes,samples.Count,0,material.Components,
            "SOURCE_TINY_CLOSED_CELL_AND_INDEPENDENT_GLYPH_STROKES");
    }
    internal static Evidence? Observe(ReadOnlyBitmapPixelBuffer source,Rectangle bounds)
    {
        
        if(bounds.Width<bounds.Height*2.3f || bounds.Width>source.Width*.26f ||
            bounds.Height<8 || bounds.Height>source.Height*.09f)return null;
        var margin=Math.Clamp(bounds.Height/3,5,16);
        var scan=Rectangle.Intersect(new Rectangle(Point.Empty,source.Size),Rectangle.Inflate(bounds,margin,margin));
        var w=scan.Width;var h=scan.Height;var colors=new Color[w*h];
        for(var y=0;y<h;y++)for(var x=0;x<w;x++)colors[y*w+x]=source.GetPixel(scan.X+x,scan.Y+y);
        var groups=colors.GroupBy(c=>(c.R/24,c.G/24,c.B/24)).OrderByDescending(g=>g.Count()).Take(5).ToArray();
        Evidence? best=null;
        foreach(var group in groups)
        {
            int Median(Func<Color,int> channel){var a=group.Select(channel).Order().ToArray();return a[a.Length/2];}
            var color=Color.FromArgb(Median(c=>c.R),Median(c=>c.G),Median(c=>c.B));
            var near=colors.Select(c=>MaxDistance(c,color)<=18).ToArray();
            foreach(var comp in Components(near,w,h))
            {
                var minX=comp.Min(i=>i%w);var maxX=comp.Max(i=>i%w);
                var minY=comp.Min(i=>i/w);var maxY=comp.Max(i=>i/w);
                var cw=maxX-minX+1;var ch=maxY-minY+1;
                if(minX==0||minY==0||maxX==w-1||maxY==h-1||cw<bounds.Width*.70f||
                    ch<bounds.Height*.35f||cw<ch*2.1f||comp.Count<cw*ch*.35f)continue;
                var plate=new bool[near.Length];foreach(var i in comp)plate[i]=true;
                var solid=0;
                var solidIndices=new List<int>();
                foreach(var i in comp)
                {
                    var x=i%w;var y=i/w;var inside=true;
                    for(var dy=-1;dy<=1&&inside;dy++)for(var dx=-1;dx<=1;dx++)
                        if(!plate[(y+dy)*w+x+dx]){inside=false;break;}
                    if(inside){solid++;solidIndices.Add(i);}
                }
                if(solid<cw*ch*.16f)continue;
                // Containment alone cannot make a gradient plaque a solid surface.
                // Spatial medians distinguish coherent native color changes from
                // local compression/antialias noise around genuine flat controls.
                var coherentSurface=true;
                for(var axis=0;axis<2&&coherentSurface;axis++)
                {
                    var medians=new List<Color>();
                    for(var band=0;band<3;band++)
                    {
                        var samples=solidIndices.Where(i=>{
                            var position=axis==0?i%w-minX:i/w-minY;
                            var extent=axis==0?cw:ch;
                            return position*3>=band*extent&&position*3<(band+1)*extent;
                        }).Select(i=>colors[i]).ToArray();
                        if(samples.Length<8){coherentSurface=false;break;}
                        int ChannelMedian(Func<Color,int> channel){var values=samples.Select(channel).Order().ToArray();return values[values.Length/2];}
                        medians.Add(Color.FromArgb(ChannelMedian(c=>c.R),ChannelMedian(c=>c.G),ChannelMedian(c=>c.B)));
                    }
                    for(var i=0;i<medians.Count;i++)for(var j=i+1;j<medians.Count;j++)
                        if(MaxDistance(medians[i],medians[j])>8)coherentSurface=false;
                }
                if(!coherentSurface)continue;
                // Flood only exterior non-material pixels. Enclosed holes are
                // possible glyphs; the exterior and original contour never become ink.
                var exterior=new bool[near.Length];var queue=new Queue<int>();
                void Seed(int i){if(!plate[i]&&!exterior[i]){exterior[i]=true;queue.Enqueue(i);}}
                for(var x=0;x<w;x++){Seed(x);Seed((h-1)*w+x);}
                for(var y=0;y<h;y++){Seed(y*w);Seed(y*w+w-1);}
                while(queue.Count>0)
                {
                    var i=queue.Dequeue();var x=i%w;var y=i/w;
                    if(x>0)Seed(i-1);if(x+1<w)Seed(i+1);if(y>0)Seed(i-w);if(y+1<h)Seed(i+w);
                }
                var holes=Enumerable.Range(0,near.Length).Select(i=>!plate[i]&&!exterior[i]).ToArray();
                var components=Components(holes,w,h).Where(c=>c.Count>=2).ToArray();
                if(components.Length<2)continue;
                // A row of square icons also makes holes in a dark plate. Require
                // several irregular glyph shapes; compact filled symbols are not text.
                var irregular=components.Count(c=>{
                    var rw=c.Max(i=>i%w)-c.Min(i=>i%w)+1;
                    var rh=c.Max(i=>i/w)-c.Min(i=>i/w)+1;
                    return c.Count>=8&&rw>=3&&rh>=3&&c.Count<rw*rh*.82f;
                });
                if(irregular<Math.Max(2,(int)Math.Ceiling(components.Length*.25)))continue;
                var material=new List<Point>();var nonGlyph=0;
                foreach(var c in components)
                {
                    var rw=c.Max(i=>i%w)-c.Min(i=>i%w)+1;
                    var rh=c.Max(i=>i/w)-c.Min(i=>i/w)+1;
                    if(rw>bounds.Height*1.6f || rh>bounds.Height*1.05f){nonGlyph+=c.Count;continue;}
                    material.AddRange(c.Select(i=>new Point(scan.X+i%w,scan.Y+i/w)));
                }
                var holeCount=holes.Count(x=>x);
                if(material.Count<bounds.Width*bounds.Height*.035f || nonGlyph>holeCount*.08f ||
                    material.Count>cw*ch*.45f)continue;
                var fills=material.Select(p=>source.GetPixel(p.X,p.Y)).Where(c=>MaxDistance(c,color)>45)
                    .GroupBy(c=>(c.R/16,c.G/16,c.B/16)).OrderByDescending(g=>g.Count()).FirstOrDefault();
                if(fills is null)continue;
                var fill=Color.FromArgb((int)fills.Average(c=>c.R),(int)fills.Average(c=>c.G),(int)fills.Average(c=>c.B));
                if(TranslationTextColorResolver.Contrast(fill,color)<3)continue;
                // Enclosed holes prove the control and glyph family, but a descender
                // may touch its contour. Complete that same color family only between
                // source-material endpoints on each row, never into exterior paper.
                var vr=fill.R-color.R;var vg=fill.G-color.G;var vb=fill.B-color.B;
                var norm=(float)(vr*vr+vg*vg+vb*vb);
                var family=new bool[near.Length];var seeds=new bool[near.Length];var pending=new Queue<int>();
                for(var row=minY;row<=maxY;row++)
                {
                    var endpoints=comp.Where(i=>i/w==row).Select(i=>i%w).ToArray();
                    if(endpoints.Length<2)continue;
                    var left=endpoints.Min();var right=endpoints.Max();
                    for(var x=left;x<=right;x++)
                    {
                        var i=row*w+x;var value=colors[i];
                        var alpha=((value.R-color.R)*vr+(value.G-color.G)*vg+(value.B-color.B)*vb)/norm;
                        var error=Math.Max(Math.Abs(value.R-color.R-alpha*vr),
                            Math.Max(Math.Abs(value.G-color.G-alpha*vg),Math.Abs(value.B-color.B-alpha*vb)));
                        family[i]=alpha>=.008f&&alpha<=1.15f&&error<=12;
                        if(family[i]&&alpha>=.15f){seeds[i]=true;pending.Enqueue(i);}
                    }
                }
                while(pending.Count>0)
                {
                    var i=pending.Dequeue();var x=i%w;var y=i/w;
                    for(var dy=-1;dy<=1;dy++)for(var dx=-1;dx<=1;dx++)
                    {
                        var xx=x+dx;var yy=y+dy;if(xx<0||yy<0||xx>=w||yy>=h)continue;
                        var j=yy*w+xx;if(!family[j]||seeds[j])continue;seeds[j]=true;pending.Enqueue(j);
                    }
                }
                var completed=Enumerable.Range(0,seeds.Length).Where(i=>seeds[i])
                    .Select(i=>new Point(scan.X+i%w,scan.Y+i/w)).Concat(material).Distinct().ToList();
                if(completed.Count<material.Count*.8f || completed.Count>cw*ch*.65f)continue;
                material=completed;
                var evidence=new Evidence(new Rectangle(scan.X+minX,scan.Y+minY,cw,ch),color,fill,material,
                    comp.Count,solid,components.Length,"SOLID_SOURCE_CONTROL_WITH_ENCLOSED_GLYPH_HOLES");
                if(best is null || solid>best.SolidInteriorPixels)best=evidence;
            }
        }
        return best;
    }
    private static int MaxDistance(Color a,Color b)=>Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B)));
    private static IEnumerable<List<int>> Components(bool[] mask,int w,int h)
    {
        var seen=new bool[mask.Length];
        for(var seed=0;seed<mask.Length;seed++)
        {
            if(!mask[seed]||seen[seed])continue;
            var list=new List<int>();var q=new Queue<int>();q.Enqueue(seed);seen[seed]=true;
            while(q.Count>0)
            {
                var i=q.Dequeue();list.Add(i);var x=i%w;var y=i/w;
                for(var dy=-1;dy<=1;dy++)for(var dx=-1;dx<=1;dx++)
                {
                    var xx=x+dx;var yy=y+dy;
                    if(xx<0||yy<0||xx>=w||yy>=h)continue;
                    var j=yy*w+xx;if(!mask[j]||seen[j])continue;seen[j]=true;q.Enqueue(j);
                }
            }
            yield return list;
        }
    }
}

