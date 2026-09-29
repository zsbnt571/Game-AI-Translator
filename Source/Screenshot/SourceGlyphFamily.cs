namespace ScreenshotTranslationUiTester.CorePipelineV2;

// Independent repeated-glyph evidence. The OCR run bounds the search, but never
// becomes a solid erase rectangle. Components, coverage and local colour jointly
// distinguish source letters from a surrounding panel or a continuous border.
internal static class SourceGlyphFamily
{
    internal sealed record Evidence(Rectangle Scan, Rectangle InkBounds, Color Fill,
        int Components, float StrokeRatio, Point[] Core, Point[] Material, string Reason);
    internal static Evidence? Observe(ReadOnlyBitmapPixelBuffer source, NormalizedOcrLine line, bool[,]? protection=null, Action<object>? trace=null,CancellationToken token=default)
        =>ObserveWithForeground(source,line,protection,trace,token,null);
    internal static Evidence? ObserveWithForeground(ReadOnlyBitmapPixelBuffer source, NormalizedOcrLine line, bool[,]? protection, Action<object>? trace,CancellationToken token,Color? establishedForeground,bool reusePolygonMembership=false)
    {
        int letters=line.SourceText.Count(char.IsLetterOrDigit);
        var box=Rectangle.Intersect(Rectangle.Ceiling(line.Bounds),new(Point.Empty,source.Size));
        if(letters<3||letters>100||line.Confidence<.85f||box.Height<16||box.Height>220||box.Width<box.Height*1.6f||box.Width*(long)box.Height>220000)return null;
        int halo=Math.Clamp((int)Math.Ceiling(box.Height*.12f),3,18);
        int searchMargin=Math.Clamp((int)Math.Ceiling(box.Height*.28f),6,40);
        var scan=Rectangle.Intersect(Rectangle.Inflate(box,searchMargin,searchMargin),new(Point.Empty,source.Size));
        int w=scan.Width,h=scan.Height,n=w*h;
        var colors=new int[n];var hist=new int[4096];
        for(int y=0;y<h;y++)for(int x=0;x<w;x++)
        {var c=source.GetPixel(scan.X+x,scan.Y+y);colors[y*w+x]=c.ToArgb();if(box.Contains(scan.X+x,scan.Y+y))hist[(c.R>>4)*256+(c.G>>4)*16+(c.B>>4)]++;}
        var peaks=Enumerable.Range(0,hist.Length).OrderByDescending(i=>hist[i]).Take(12).ToArray();
        var ring=new List<Color>();
        for(int y=0;y<h;y+=2)for(int x=0;x<w;x+=2)
            if(!box.Contains(scan.X+x,scan.Y+y))ring.Add(Color.FromArgb(colors[y*w+x]));
        if(ring.Count<12)return null;
        int RingMedian(Func<Color,int> f)=>ring.Select(f).Order().ElementAt(ring.Count/2);
        var backdrop=Color.FromArgb(RingMedian(c=>c.R),RingMedian(c=>c.G),RingMedian(c=>c.B));
        var mask=new bool[n];var seen=new bool[n];var queue=new int[n];
        var morphA=new bool[n];var morphB=new bool[n];
        // One immutable polygon and pixel centre per observation. Cache the exact
        // predicate, not a raster approximation. Zero means not evaluated; the
        // local array is owned by this call and never escapes or enters a pool.
        var polygonMembership=reusePolygonMembership?new byte[n]:null;
        bool BelongsToSource(int i)
        {
            byte result=polygonMembership![i];
            if(result==0)
                polygonMembership[i]=result=NativeTextMaterial.Contains(line.Polygon,
                    scan.X+i%w+.5f,scan.Y+i/w+.5f)?(byte)2:(byte)1;
            return result==2;
        }
        bool[]? bestCore=null;Rectangle bestInk=default;int bestGlyphCount=0;double bestScore=0;
        int bestR=0,bestG=0,bestB=0;
        foreach(int peak in peaks)
        {
            if(hist[peak]<Math.Max(8,box.Width*box.Height*.002))continue;
            int cr=(peak/256)*16+8,cg=(peak/16%16)*16+8,cb=(peak%16)*16+8;
            // Apply the existing polarity contradiction before ranking palettes.
            // An outline/background candidate must not displace a valid light
            // letter body and then cause the entire observation to be discarded.
            if(establishedForeground is {} established&&
                Math.Max(Math.Abs(cr-established.R),Math.Max(Math.Abs(cg-established.G),Math.Abs(cb-established.B)))>60&&
                Color.FromArgb(cr,cg,cb).GetBrightness()<established.GetBrightness())continue;
            foreach(int tolerance in new[]{20,44,72})
            {
                token.ThrowIfCancellationRequested();
                Array.Clear(seen);Array.Clear(mask);
                for(int y=0;y<h;y++)for(int x=0;x<w;x++)
                {
                    int i=y*w+x,c=colors[i];
                    mask[i]=
                        Math.Max(Math.Abs(((c>>16)&255)-cr),Math.Max(Math.Abs(((c>>8)&255)-cg),Math.Abs((c&255)-cb)))<=tolerance;
                }
                if(box.Height>=48)
                {
                    int radius=Math.Clamp((int)Math.Round(box.Height*.018f),1,3);
                    void Morph(bool[] from,bool[] to,bool horizontal,bool dilate)
                    {
                        for(int y=0;y<h;y++)for(int x=0;x<w;x++)
                        {
                            bool value=!dilate;
                            for(int d=-radius;d<=radius;d++)
                            {int xx=x+(horizontal?d:0),yy=y+(horizontal?0:d);bool v=xx>=0&&yy>=0&&xx<w&&yy<h&&from[yy*w+xx];if(dilate? v:!v){value=dilate;break;}}
                            to[y*w+x]=value;
                        }
                    }
                    // Fine hatching is part of the glyph material. Close only
                    // source-scale gaps before judging complete component geometry.
                    Morph(mask,morphA,true,true);Morph(morphA,morphB,false,true);
                    Morph(morphB,morphA,true,false);Morph(morphA,mask,false,false);
                }
                var components=new List<(int[] Pixels,Rectangle Bounds,int Units)>();
                var smallParts=new List<(int[] Pixels,Rectangle Bounds)>();
                for(int seed=0;seed<n;seed++)
                {
                    if(!mask[seed]||seen[seed])continue;
                    int head=0,tail=1,l=w,t=h,r=0,b=0;queue[0]=seed;seen[seed]=true;
                    while(head<tail)
                    {
                        int i=queue[head++],x=i%w,y=i/w;l=Math.Min(l,x);r=Math.Max(r,x+1);t=Math.Min(t,y);b=Math.Max(b,y+1);
                        for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
                        {int xx=x+dx,yy=y+dy;if(xx<0||yy<0||xx>=w||yy>=h)continue;int j=yy*w+xx;if(mask[j]&&!seen[j]){seen[j]=true;queue[tail++]=j;}}
                    }
                    var bnd=Rectangle.FromLTRB(l,t,r,b);
                    var inImage=bnd;inImage.Offset(scan.Location);
                    if(!inImage.IntersectsWith(box))continue;
                    // Search padding can contain neighbouring text. Mere rectangle
                    // intersection is insufficient: most of each glyph body must
                    // belong to this OCR polygon. Padding is reserved for its halo.
                    int owned=0;
                    foreach(int i in queue.AsSpan(0,tail))
                        if(reusePolygonMembership?BelongsToSource(i):NativeTextMaterial.Contains(line.Polygon,scan.X+i%w+.5f,scan.Y+i/w+.5f))owned++;
                    if(owned<tail*.6f)continue;
                    int units=1;
                    if(bnd.Width>box.Height*1.55f&&bnd.Width<=box.Width&&bnd.Height>=box.Height*.55f&&tail<bnd.Width*bnd.Height*.7f)
                    {
                        var local=new bool[bnd.Width*bnd.Height];foreach(int i in queue.AsSpan(0,tail))local[(i/w-t)*bnd.Width+i%w-l]=true;
                        int holes=0;var visited=new bool[local.Length];var todo=new int[local.Length];
                        for(int seed2=0;seed2<local.Length;seed2++)
                        {
                            if(local[seed2]||visited[seed2])continue;
                            int qh=0,qt=1;todo[0]=seed2;visited[seed2]=true;bool outside=false;
                            while(qh<qt){int j=todo[qh++],xx=j%bnd.Width,yy=j/bnd.Width;
                                if(xx==0||yy==0||xx==bnd.Width-1||yy==bnd.Height-1)outside=true;
                                void V(int k){if(!local[k]&&!visited[k]){visited[k]=true;todo[qt++]=k;}}
                                if(xx>0)V(j-1);if(xx+1<bnd.Width)V(j+1);if(yy>0)V(j-bnd.Width);if(yy+1<bnd.Height)V(j+bnd.Width);
                            }
                            if(!outside&&qt>=Math.Max(4,box.Height*box.Height*.002))holes++;
                        }
                        // Connected display lettering may share extrusion/serifs.
                        // Several enclosed counters distinguish it from a solid
                        // panel or a frame with a single interior.
                        if(holes>=3)units=Math.Min(letters,holes+2);
                        trace?.Invoke(new{Wide=bnd,holes,units});
                    }
                    if(tail>=4&&bnd.Height<box.Height*.25f&&bnd.Width<box.Height*.3f)
                        smallParts.Add((queue.AsSpan(0,tail).ToArray(),bnd));
                    if(tail>=Math.Max(6,box.Height*.2f)&&bnd.Height>=box.Height*.25f&&bnd.Height<=box.Height*1.35f&&
                        (bnd.Width<=box.Height*1.55f||units>1)&&bnd.Width>=2&&(tail<bnd.Width*bnd.Height*.94f||bnd.Width<box.Height*.25f))
                        components.Add((queue.AsSpan(0,tail).ToArray(),bnd,units));
                }
                trace?.Invoke(new{line.SourceId,cr,cg,cb,tolerance,Components=components.Select(c=>new{c.Bounds,Pixels=c.Pixels.Length})});
                // OCR can include an isolated strip of scenery beyond a word.
                // Separate disconnected row islands before judging their combined
                // width; an unrelated same-colour object cannot complete a word.
                var islands=new List<List<(int[] Pixels,Rectangle Bounds,int Units)>>();
                foreach(var c in components.OrderBy(c=>c.Bounds.Left))
                {
                    if(islands.Count==0||c.Bounds.Left-islands[^1].Max(v=>v.Bounds.Right)>box.Height*.75f)
                        islands.Add([]);
                    islands[^1].Add(c);
                }
                if(islands.Count>1)
                {
                    var primary=islands.OrderByDescending(a=>a.Sum(c=>c.Pixels.Length)).First();
                    var primaryBox=primary.Select(c=>c.Bounds).Aggregate(Rectangle.Union);
                    // A word gap is not the end of an OCR row. Keep another
                    // island when it independently contains repeated glyphs on
                    // the same baseline. A lone similarly coloured scene object
                    // still cannot be used to complete the text span.
                    components=islands.Where(a=>ReferenceEquals(a,primary)||
                        (a.Sum(c=>c.Units)>=3&&Math.Abs(a.Max(c=>c.Bounds.Bottom)-primaryBox.Bottom)<=box.Height*.3f))
                        .SelectMany(a=>a).ToList();
                }
                int glyphCount=components.Sum(c=>c.Units);
                if(glyphCount<Math.Max(3,letters*.55f)||glyphCount>letters*1.8f)continue;
                var ink=components.Select(c=>c.Bounds).Aggregate(Rectangle.Union);
                if(ink.Width<box.Width*.70f||ink.Height<box.Height*.44f||components.Max(c=>c.Bounds.Bottom)-components.Min(c=>c.Bounds.Bottom)>box.Height*.48f)continue;
                int count=components.Sum(c=>c.Pixels.Length);
                if(count>box.Width*box.Height*.58f)continue;
                // Prefer complete, tall repeated characters over only their bright
                // edge. A larger colour tolerance is not itself better evidence.
                int contrast=Math.Max(Math.Abs(cr-backdrop.R),Math.Max(Math.Abs(cg-backdrop.G),Math.Abs(cb-backdrop.B)));
                if(contrast<48)continue;
                int solid=0;
                foreach(var c in components)foreach(int i in c.Pixels)
                    if(i%w>0&&i%w<w-1&&i>=w&&i<n-w&&mask[i-1]&&mask[i+1]&&mask[i-w]&&mask[i+w])solid++;
                // A thin offset shadow/outline may have more colour contrast than
                // the actual fill on a light panel. Repeated solid stroke interiors
                // distinguish the letter body from that secondary decoration.
                double solidity=solid/(double)count;
                double score=ink.Width/(double)box.Width+ink.Height/(double)box.Height+Math.Min(1,glyphCount/(double)letters)+contrast/200.0+solidity*2-tolerance*.003;
                if(score<=bestScore)continue;
                var core=new bool[n];foreach(var c in components)foreach(int i in c.Pixels)core[i]=true;
                foreach(var part in smallParts)
                    if(part.Bounds.Left>=box.Left-scan.Left&&part.Bounds.Right<=box.Right-scan.Left&&
                        part.Bounds.Top>=ink.Top-ink.Height*.2f&&part.Bounds.Bottom<=ink.Bottom+ink.Height*.2f&&
                        components.Any(c=>
                            (part.Bounds.Left<c.Bounds.Right&&part.Bounds.Right>c.Bounds.Left&&
                             part.Bounds.Bottom<=c.Bounds.Top&&c.Bounds.Top-part.Bounds.Bottom<=ink.Height*.2f)||
                            (part.Bounds.Top>=ink.Top&&part.Bounds.Bottom<=ink.Bottom+ink.Height*.2f&&
                             Rectangle.Inflate(c.Bounds,(int)(ink.Height*.2f),0).IntersectsWith(part.Bounds))||
                            // Baseline punctuation may be disconnected from a
                            // hook or separated by the advance of a full stop.
                            // It still needs the OCR row, fill and nearby glyph
                            // support; small scenery above the row is excluded.
                            (line.SourceText.Any(char.IsPunctuation)&&
                             part.Bounds.Top>=ink.Bottom-ink.Height*.4f&&
                             part.Bounds.Bottom<=ink.Bottom+ink.Height*.2f&&
                             Math.Max(0,Math.Max(c.Bounds.Left-part.Bounds.Right,part.Bounds.Left-c.Bounds.Right))<=ink.Height*.65f)))
                        foreach(int i in part.Pixels)core[i]=true;
                bool conflict=false;
                if(protection is not null)for(int i=0;i<n&&!conflict;i++)if(core[i]&&protection[scan.X+i%w,scan.Y+i/w])conflict=true;
                if(conflict)continue;
                bestCore=core;bestInk=ink;bestGlyphCount=glyphCount;bestScore=score;
                bestR=cr;bestG=cg;bestB=cb;
            }
        }
        if(bestCore is null)return null;
        // A broader palette may join a light letter to a similarly coloured
        // background and thereby reject that whole component. Recover only
        // tightly coloured glyph-shaped components inside the witnessed row,
        // with the same baseline and actual glyph anchors on both sides.
        // This is source material permission, never a background fill rectangle.
        Array.Clear(seen);
        for(int i=0;i<n;i++){int c=colors[i];mask[i]=Math.Max(Math.Abs(((c>>16)&255)-bestR),Math.Max(Math.Abs(((c>>8)&255)-bestG),Math.Abs((c&255)-bestB)))<=20;}
        for(int seed=0;seed<n;seed++)
        {
            if(!mask[seed]||seen[seed])continue;
            token.ThrowIfCancellationRequested();
            int head=0,tail=1,l=w,t=h,r=0,b=0;queue[0]=seed;seen[seed]=true;bool conflict=false;
            while(head<tail)
            {
                int i=queue[head++],x=i%w,y=i/w;l=Math.Min(l,x);r=Math.Max(r,x+1);t=Math.Min(t,y);b=Math.Max(b,y+1);
                if(protection is not null&&protection[scan.X+x,scan.Y+y])conflict=true;
                for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
                {int xx=x+dx,yy=y+dy;if(xx<0||yy<0||xx>=w||yy>=h)continue;int j=yy*w+xx;if(mask[j]&&!seen[j]){seen[j]=true;queue[tail++]=j;}}
            }
            if(!conflict&&tail>=Math.Max(6,box.Height*.2f)&&(tail<(r-l)*(b-t)*.94f||r-l<box.Height*.25f)&&l>bestInk.Left&&r<bestInk.Right&&r-l>=2&&r-l<=box.Height*1.55f&&
                b-t>=box.Height*.25f&&b-t<=box.Height*1.35f&&Math.Abs(b-bestInk.Bottom)<=box.Height*.3f&&t>=bestInk.Top-box.Height*.2f)
                foreach(int i in queue.AsSpan(0,tail))bestCore[i]=true;
        }
        // Material expansion and paint statistics are needed only for the winning
        // observation. They are not regenerated for each improving palette trial.
        var material=new bool[n];var widths=new List<int>();
        var rh=new int[256];var gh=new int[256];var bh=new int[256];int countCore=0;
        for(int y=0;y<h;y++)
        {
            token.ThrowIfCancellationRequested();
            for(int x=0;x<w;x++)
            {
                int i=y*w+x;if(!bestCore[i])continue;countCore++;
                int c=colors[i];rh[(c>>16)&255]++;gh[(c>>8)&255]++;bh[c&255]++;
                int hr=1,vr=1;while(x+hr<w&&bestCore[i+hr])hr++;while(y+vr<h&&bestCore[i+vr*w])vr++;
                widths.Add(Math.Min(hr,vr));
                for(int dy=-halo;dy<=halo;dy++)for(int dx=-halo;dx<=halo;dx++)
                {int xx=x+dx,yy=y+dy;if(dx*dx+dy*dy>halo*halo||xx<0||yy<0||xx>=w||yy>=h)continue;
                    if(protection is null||!protection[scan.X+xx,scan.Y+yy])material[yy*w+xx]=true;}
            }
        }
        widths.Sort();float ratio=widths[widths.Count/2]*2f/Math.Max(1,bestInk.Height);
        int Median(int[] hist){int total=0;for(int i=0;i<256;i++){total+=hist[i];if(total>countCore/2)return i;}return 255;}
        Point[] Points(bool[] a)=>Enumerable.Range(0,n).Where(i=>a[i]).Select(i=>new Point(scan.X+i%w,scan.Y+i/w)).ToArray();
        var fill=Color.FromArgb(Median(rh),Median(gh),Median(bh));bestInk.Offset(scan.Location);
        return new(scan,bestInk,fill,bestGlyphCount,ratio,Points(bestCore),Points(material),"REPEATED_SOURCE_GLYPH_COLOR_COMPONENTS_WITH_SCALE_BOUNDED_HALO");
    }
}
