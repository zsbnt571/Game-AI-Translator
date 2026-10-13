namespace ScreenshotTranslationUiTester.CorePipelineV2;

/// <summary>Source-proved enclosed dark dialogue. Pixel ownership, never translated layout.</summary>
internal static class SourceDialogueMaterial
{
    internal sealed record Surface(int X,int Y,int Width,int Height,string[] SourceIds,float LineHeight,int GlowRadius)
    { internal Rectangle Bounds=>new(X,Y,Width,Height); }
    internal sealed record Result(IReadOnlyList<Surface> Surfaces,IReadOnlyDictionary<string,Point[]> Materials,
        IReadOnlyDictionary<string,Point[]> GlyphMaterials);
    internal static Result Observe(ReadOnlyBitmapPixelBuffer p,CorePipelineDocument document,bool[,] protectedPixels)
    {
        var surfaces=new List<Surface>();var materials=new Dictionary<string,Point[]>(StringComparer.Ordinal);
        var glyphMaterials=new Dictionary<string,Point[]>(StringComparer.Ordinal);
        foreach(var seed in document.VisualBlocks.Where(b=>b.Lines.Count>=3))
        {
            var panel=SourceTextPanel.Observe(p,seed);
            if(panel is null||panel.Proof!="FOUR_SIDED_BORDER"||panel.Coverage<.9f)continue;
            var bounds=Rectangle.Round(panel.Interior);
            if(surfaces.Any(s=>Rectangle.Intersect(s.Bounds,bounds).Width>0&&Rectangle.Intersect(s.Bounds,bounds).Height>0))continue;
            var members=document.VisualBlocks.Where(b=>b.Lines.All(l=>
                bounds.Contains((int)(l.Bounds.Left+l.Bounds.Width/2),(int)(l.Bounds.Top+l.Bounds.Height/2))&&
                RectangleF.Intersect(bounds,l.Bounds).Width*l.Bounds.Height>=l.Bounds.Width*l.Bounds.Height*.97f)).ToArray();
            var local=new Dictionary<string,Point[]>();var ids=new List<string>();var valid=true;
            foreach(var block in members)
            {
                var points=new HashSet<Point>();
                foreach(var line in block.Lines)
                {
                    var material=NativeTextMaterial.Observe(p,line.Polygon,protectedPixels,lightHaloPasses:
                        Math.Clamp((int)Math.Ceiling(line.Bounds.Height*.1f),2,10));
                    if(!material.Accepted||material.Polarity!="LightOnDark"||material.RingBackground>80||material.RingVariation>14)
                    {valid=false;break;}
                    var lineRect=RectangleF.Intersect(RectangleF.Inflate(bounds,-Math.Max(3,line.Bounds.Height*.1f),0),
                        RectangleF.Inflate(line.Bounds,line.Bounds.Height*.45f,0));
                    // Trailing punctuation may lie just beyond a detected word box.
                    // The enclosing source frame and row band still own those strokes.
                    PointF[] polygon=[new(lineRect.Left,lineRect.Top),new(lineRect.Right,lineRect.Top),new(lineRect.Right,lineRect.Bottom),new(lineRect.Left,lineRect.Bottom)];
                    var extended=NativeTextMaterial.Observe(p,polygon,protectedPixels,lightHaloPasses:
                        Math.Clamp((int)Math.Ceiling(line.Bounds.Height*.1f),2,10));
                    foreach(var pt in material.Material)if(bounds.Contains(pt))points.Add(pt);
                    if(extended.Accepted&&extended.Polarity==material.Polarity&&extended.RingBackground<=80&&extended.RingVariation<=14)
                        foreach(var pt in extended.Material)if(bounds.Contains(pt))points.Add(pt);
                    ids.Add(line.SourceId);
                }
                if(!valid)break;
                local[block.BlockId]=points.ToArray();
            }
            if(!valid||ids.Count<3||local.Values.Sum(v=>v.Length)>bounds.Width*bounds.Height*.5f)continue;
            var height=members.SelectMany(b=>b.Lines).Select(l=>l.Bounds.Height).Order().ElementAt(ids.Count/2);
            var radius=Math.Clamp((int)Math.Ceiling(height*.34f),3,40);
            surfaces.Add(new(bounds.X,bounds.Y,bounds.Width,bounds.Height,ids.ToArray(),height,radius));
            foreach(var item in local)
            {
                glyphMaterials[item.Key]=item.Value;
                var distance=new byte[bounds.Width,bounds.Height];var queue=new Queue<Point>();var expanded=new List<Point>();
                foreach(var pt in item.Value){var x=pt.X-bounds.Left;var y=pt.Y-bounds.Top;if(distance[x,y]!=0)continue;distance[x,y]=1;queue.Enqueue(pt);expanded.Add(pt);}
                while(queue.Count>0)
                {
                    var at=queue.Dequeue();var d=distance[at.X-bounds.Left,at.Y-bounds.Top];if(d>radius)continue;
                    for(var dy=-1;dy<=1;dy++)for(var dx=-1;dx<=1;dx++)
                    {
                        var pt=new Point(at.X+dx,at.Y+dy);if(!bounds.Contains(pt)||protectedPixels[pt.X,pt.Y])continue;
                        var x=pt.X-bounds.Left;var y=pt.Y-bounds.Top;if(distance[x,y]!=0)continue;
                        distance[x,y]=(byte)(d+1);queue.Enqueue(pt);expanded.Add(pt);
                    }
                }
                materials[item.Key]=expanded.ToArray();
            }
        }
        return new(surfaces,materials,glyphMaterials);
    }
}
