using System.Diagnostics;
using System.Drawing;
using System.Security.Cryptography;
using System.Text;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

internal static partial class CorePipelineCorpusRunner
{
    private sealed record VerifiedContainerSurfaceModel(
        string SurfaceModelId,string ContainerId,string ContainerType,Rectangle Bounds,
        IReadOnlyList<string> BlockIds,int SharedLineCount,PlanarSurface Surface,SharedResidualGrid ResidualGrid,
        bool[,] CleanDonorExclusion,
        int SamplePixels,int ExcludedGlyphPixels,int ExcludedEdgePixels,int ExcludedProtectedPixels,
        double FitError,double P90Error,double Confidence,double ElapsedMs,string ModelType);

    private sealed record SharedResidualGrid(Rectangle Bounds,PlanarSurface Plane,int Columns,int Rows,
        float[,] Red,float[,] Green,float[,] Blue)
    {
        public Color At(int x,int y)
        {
            var u=Bounds.Width<=1?0:Math.Clamp((x-Bounds.Left)/(double)(Bounds.Width-1)*(Columns-1),0,Columns-1);
            var v=Bounds.Height<=1?0:Math.Clamp((y-Bounds.Top)/(double)(Bounds.Height-1)*(Rows-1),0,Rows-1);
            var x0=Math.Clamp((int)Math.Floor(u),0,Columns-1);var y0=Math.Clamp((int)Math.Floor(v),0,Rows-1);
            var x1=Math.Min(Columns-1,x0+1);var y1=Math.Min(Rows-1,y0+1);
            var tx=(float)(u-x0);var ty=(float)(v-y0);
            float Bilerp(float[,] values)
            {
                var top=values[x0,y0]*(1-tx)+values[x1,y0]*tx;
                var bottom=values[x0,y1]*(1-tx)+values[x1,y1]*tx;
                return top*(1-ty)+bottom*ty;
            }
            var canonical=Plane.At(x,y);
            return Color.FromArgb(
                Math.Clamp((int)MathF.Round(canonical.R+Bilerp(Red)),0,255),
                Math.Clamp((int)MathF.Round(canonical.G+Bilerp(Green)),0,255),
                Math.Clamp((int)MathF.Round(canonical.B+Bilerp(Blue)),0,255));
        }
    }

    private static IReadOnlyDictionary<string,VerifiedContainerSurfaceModel> BuildVerifiedContainerSurfaces(
        Bitmap source,CorePipelineDocument document,IReadOnlyDictionary<string,RenderPlanR2> plans,
        IReadOnlyDictionary<string,BackgroundSurfaceOwnerR2> surfaceOwners,
        IReadOnlyDictionary<string,SourceStyleEvidenceR2> styleEvidence,
        IReadOnlyDictionary<string,GlyphMaskResult> glyphs,bool[,] protectedPixels,
        out IReadOnlyList<VerifiedContainerSurfaceModel> acceptedModels)
    {
        var blocks=document.VisualBlocks.ToDictionary(x=>x.BlockId,StringComparer.Ordinal);
        var blockToModel=new Dictionary<string,VerifiedContainerSurfaceModel>(StringComparer.Ordinal);
        var models=new List<VerifiedContainerSurfaceModel>();
        foreach(var ownerGroup in glyphs.Keys.GroupBy(id=>plans[id].SurfaceOwnerId,StringComparer.Ordinal)
                    .OrderBy(x=>x.Key,StringComparer.Ordinal))
        {
            var surfaceOwner=surfaceOwners[ownerGroup.Key];
            var eligible=ownerGroup.Select(id=>blocks[id]).Where(block=>
            {
                var plan=plans[block.BlockId];
                return plan.VisualRole!=SourceVisualRoleR2.ArtisticTitle&&
                    (block.Lines.Count>=2||surfaceOwner.UnderlyingBlockIds.Count>1||
                     plan.VisualRole is SourceVisualRoleR2.Control or SourceVisualRoleR2.Dialogue);
            }).OrderBy(x=>x.Bounds.Left).ThenBy(x=>x.Bounds.Top).ToArray();
            foreach(var cluster in ConnectedContainerClusters(eligible,source.Size))
            {
                if(cluster.Count==0)continue;
                var model=TryBuildVerifiedContainerSurface(source,cluster,surfaceOwner,styleEvidence,glyphs,
                    protectedPixels);
                if(model is null)continue;
                models.Add(model);
                foreach(var block in cluster)blockToModel[block.BlockId]=model;
            }
        }
        acceptedModels=models.OrderBy(x=>x.SurfaceModelId,StringComparer.Ordinal).ToArray();
        return blockToModel;
    }

    private static IReadOnlyList<IReadOnlyList<VisualBlock>> ConnectedContainerClusters(
        IReadOnlyList<VisualBlock> blocks,Size canvas)
    {
        var result=new List<IReadOnlyList<VisualBlock>>();
        var remaining=new HashSet<string>(blocks.Select(x=>x.BlockId),StringComparer.Ordinal);
        var byId=blocks.ToDictionary(x=>x.BlockId,StringComparer.Ordinal);
        foreach(var seed in blocks.OrderBy(x=>x.BlockId,StringComparer.Ordinal))
        {
            if(!remaining.Remove(seed.BlockId))continue;
            var cluster=new List<VisualBlock>{seed};var queue=new Queue<VisualBlock>();queue.Enqueue(seed);
            while(queue.Count>0)
            {
                var current=queue.Dequeue();
                foreach(var id in remaining.ToArray())
                {
                    var candidate=byId[id];
                    if(!SharesVerifiedContainerGeometry(current.Bounds,candidate.Bounds,canvas))continue;
                    remaining.Remove(id);cluster.Add(candidate);queue.Enqueue(candidate);
                }
            }
            result.Add(cluster.OrderBy(x=>x.Bounds.Top).ThenBy(x=>x.Bounds.Left).ToArray());
        }
        return result;
    }

    private static bool SharesVerifiedContainerGeometry(RectangleF a,RectangleF b,Size canvas)
    {
        var horizontalOverlap=Math.Max(0,Math.Min(a.Right,b.Right)-Math.Max(a.Left,b.Left));
        var verticalOverlap=Math.Max(0,Math.Min(a.Bottom,b.Bottom)-Math.Max(a.Top,b.Top));
        var horizontalRatio=horizontalOverlap/Math.Max(1,Math.Min(a.Width,b.Width));
        var verticalRatio=verticalOverlap/Math.Max(1,Math.Min(a.Height,b.Height));
        var verticalGap=Math.Max(0,Math.Max(a.Top,b.Top)-Math.Min(a.Bottom,b.Bottom));
        var horizontalGap=Math.Max(0,Math.Max(a.Left,b.Left)-Math.Min(a.Right,b.Right));
        var verticalLimit=Math.Max(18,Math.Min(110,Math.Max(Math.Min(a.Height,b.Height)*1.65f,canvas.Height*.018f)));
        var horizontalLimit=Math.Max(8,Math.Min(28,canvas.Width*.008f));
        return horizontalRatio>=.14f&&verticalGap<=verticalLimit ||
               verticalRatio>=.62f&&horizontalGap<=horizontalLimit;
    }

    private static VerifiedContainerSurfaceModel? TryBuildVerifiedContainerSurface(Bitmap source,
        IReadOnlyList<VisualBlock> cluster,BackgroundSurfaceOwnerR2 owner,
        IReadOnlyDictionary<string,SourceStyleEvidenceR2> evidence,
        IReadOnlyDictionary<string,GlyphMaskResult> glyphs,bool[,] protectedPixels)
    {
        var timer=Stopwatch.StartNew();
        var union=Rectangle.Round(cluster[0].Bounds);
        foreach(var block in cluster.Skip(1))union=Rectangle.Union(union,Rectangle.Round(block.Bounds));
        var medianHeight=cluster.SelectMany(x=>x.Lines).Select(x=>Math.Max(1f,x.Bounds.Height)).OrderBy(x=>x).ToArray();
        var lineHeight=medianHeight.Length==0?12:medianHeight[medianHeight.Length/2];
        var margin=Math.Clamp((int)MathF.Round(lineHeight*.28f),4,18);
        var domain=Clamp(Rectangle.Inflate(union,margin,margin),source.Size);
        if(domain.Width<8||domain.Height<8)return null;

        var localExclusion=new bool[domain.Width,domain.Height];
        foreach(var block in cluster)
        {
            var glyph=glyphs[block.BlockId];
            var scan=Rectangle.Intersect(domain,glyph.SearchBounds);
            for(var y=scan.Top;y<scan.Bottom;y++)for(var x=scan.Left;x<scan.Right;x++)
                if(glyph.Admitted[x,y])localExclusion[x-domain.Left,y-domain.Top]=true;
        }
        // Donor selection must stand outside the complete core/AA/outline family.
        // This exclusion is sampling-only: it does not enlarge mutation authority.
        var dilation=Math.Clamp((int)MathF.Round(lineHeight*.055f),2,4);
        var dilated=(bool[,])localExclusion.Clone();
        for(var y=0;y<domain.Height;y++)for(var x=0;x<domain.Width;x++)if(localExclusion[x,y])
            for(var yy=Math.Max(0,y-dilation);yy<=Math.Min(domain.Height-1,y+dilation);yy++)
            for(var xx=Math.Max(0,x-dilation);xx<=Math.Min(domain.Width-1,x+dilation);xx++)
                if((xx-x)*(xx-x)+(yy-y)*(yy-y)<=dilation*dilation)dilated[xx,yy]=true;

        // Source glyph pixels are not valid donors even when the colour classifier did
        // not admit every outline/shadow pixel.  OCR line geometry is sampling evidence
        // only here: it never grants a rectangular write.  Keeping the exclusion local
        // to this verified container also prevents neighbouring cards from sharing
        // donors (the NEW-006 dialogue/task-card separation contract).
        var cleanDonorExclusion=new bool[domain.Width,domain.Height];
        foreach(var line in cluster.SelectMany(x=>x.Lines))
        {
            var h=Math.Max(1f,line.Bounds.Height);
            var lineMargin=Math.Clamp((int)MathF.Ceiling(h*.075f),2,6);
            var lineBox=Rectangle.Intersect(domain,Clamp(Rectangle.Inflate(Rectangle.Round(line.Bounds),
                lineMargin,lineMargin),source.Size));
            for(var y=lineBox.Top;y<lineBox.Bottom;y++)for(var x=lineBox.Left;x<lineBox.Right;x++)
                cleanDonorExclusion[x-domain.Left,y-domain.Top]=true;
        }

        var backgrounds=cluster.Select(x=>Color.FromArgb(evidence[x.BlockId].BackgroundArgb)).ToArray();
        int Median(Func<Color,int> selector)=>backgrounds.Select(selector).OrderBy(x=>x).ElementAt(backgrounds.Length/2);
        var reference=Color.FromArgb(Median(c=>c.R),Median(c=>c.G),Median(c=>c.B));
        var samples=new List<Point>();var excludedGlyph=0;var excludedEdge=0;var excludedProtected=0;
        var step=Math.Clamp((int)Math.Sqrt(domain.Width*(double)domain.Height/90000d),1,4);
        var colorGate=owner.SurfaceClass switch
        {
            SourceSurfaceClassR2.Flat=>126,
            SourceSurfaceClassR2.Gradient=>174,
            SourceSurfaceClassR2.Textured=>144,
            _=>118
        };
        for(var y=domain.Top;y<domain.Bottom;y+=step)for(var x=domain.Left;x<domain.Right;x+=step)
        {
            if(cleanDonorExclusion[x-domain.Left,y-domain.Top]){excludedGlyph++;continue;}
            if(protectedPixels[x,y]){excludedProtected++;continue;}
            if(LocalEdgeStrength(source,x,y)>112){excludedEdge++;continue;}
            var c=source.GetPixel(x,y);
            if(Math.Abs(c.R-reference.R)+Math.Abs(c.G-reference.G)+Math.Abs(c.B-reference.B)>colorGate)continue;
            samples.Add(new(x,y));
        }
        if(samples.Count<48)return null;
        if(!TryFitBcpsPlane(source,samples,out var plane))return null;
        var residuals=samples.Select(p=>
        {
            var actual=source.GetPixel(p.X,p.Y);var predicted=plane.At(p.X,p.Y);
            return (double)(Math.Abs(actual.R-predicted.R)+Math.Abs(actual.G-predicted.G)+Math.Abs(actual.B-predicted.B));
        }).OrderBy(x=>x).ToArray();
        var fitError=residuals[residuals.Length/2];
        var p90=residuals[(int)Math.Floor((residuals.Length-1)*.90)];
        var sampleSupport=Math.Min(1d,samples.Count/240d);
        var fitConfidence=Math.Max(0,1-fitError/105d)*Math.Max(0,1-p90/260d);
        var confidence=sampleSupport*fitConfidence*Math.Clamp(owner.Confidence,.45f,1f);
        var type=InferVerifiedContainerType(cluster,domain,source.Size);
        var maxP90=owner.SurfaceClass is SourceSurfaceClassR2.Flat or SourceSurfaceClassR2.Gradient?150:185;
        // A flattened translucent profile can legitimately contain scene variation.
        // Keep the shared low-order surface as its canonical model, while the
        // glyph-local residual reconstruction below retains the visible texture.
        var minimumConfidence=type=="PROFILE_CONTAINER"?.14:.32;
        var maximumMedian=type=="PROFILE_CONTAINER"?92:58;
        var maximumP90=type=="PROFILE_CONTAINER"?235:maxP90;
        if(confidence<minimumConfidence||fitError>maximumMedian||p90>maximumP90)return null;
        var ids=cluster.Select(x=>x.BlockId).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        var payload=string.Join("|",ids)+$"|{domain.X}:{domain.Y}:{domain.Width}:{domain.Height}";
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..12];
        var residualGrid=BuildSharedResidualGrid(source,domain,samples,plane,lineHeight);
        timer.Stop();
        return new($"SURFACE-M3H-{hash}",$"CONTAINER-M3H-{hash}",type,domain,ids,
            cluster.Sum(x=>x.Lines.Count),plane,residualGrid,cleanDonorExclusion,samples.Count,excludedGlyph,excludedEdge,excludedProtected,
            fitError,p90,confidence,timer.Elapsed.TotalMilliseconds,
            "ROBUST_CONTAINER_SHARED_SURFACE_PLUS_CLEAN_LINE_DONOR_CONTINUATION");
    }

    private static SharedResidualGrid BuildSharedResidualGrid(Bitmap source,Rectangle domain,IReadOnlyList<Point> samples,
        PlanarSurface plane,float lineHeight)
    {
        var columns=Math.Clamp((int)MathF.Ceiling(domain.Width/Math.Max(42f,lineHeight*1.35f)),4,14);
        var rows=Math.Clamp((int)MathF.Ceiling(domain.Height/Math.Max(36f,lineHeight*1.15f)),4,12);
        var red=new float[columns,rows];var green=new float[columns,rows];var blue=new float[columns,rows];
        var cellWidth=domain.Width/(float)Math.Max(1,columns-1);
        var cellHeight=domain.Height/(float)Math.Max(1,rows-1);
        var radiusX=Math.Max(18f,cellWidth*1.45f);var radiusY=Math.Max(16f,cellHeight*1.45f);
        for(var gy=0;gy<rows;gy++)for(var gx=0;gx<columns;gx++)
        {
            var cx=domain.Left+gx*cellWidth;var cy=domain.Top+gy*cellHeight;
            var local=samples.Where(p=>Math.Abs(p.X-cx)<=radiusX&&Math.Abs(p.Y-cy)<=radiusY)
                .OrderBy(p=>Math.Abs(p.X-cx)/radiusX+Math.Abs(p.Y-cy)/radiusY)
                .Take(1200).ToArray();
            if(local.Length<20)local=samples.OrderBy(p=>Math.Abs(p.X-cx)+Math.Abs(p.Y-cy)).Take(160).ToArray();
            float MedianResidual(Func<Color,int> channel)
            {
                if(local.Length==0)return 0;
                var values=local.Select(p=>channel(source.GetPixel(p.X,p.Y))-channel(plane.At(p.X,p.Y)))
                    .OrderBy(value=>value).ToArray();
                return values[values.Length/2];
            }
            red[gx,gy]=MedianResidual(c=>c.R);green[gx,gy]=MedianResidual(c=>c.G);blue[gx,gy]=MedianResidual(c=>c.B);
        }
        return new(domain,plane,columns,rows,red,green,blue);
    }

    private static string InferVerifiedContainerType(IReadOnlyList<VisualBlock> cluster,Rectangle bounds,Size canvas)
    {
        var lines=cluster.Sum(x=>x.Lines.Count);var aspect=bounds.Width/(float)Math.Max(1,bounds.Height);
        if(cluster.Count==1&&lines>=6&&bounds.Height>=canvas.Height*.28f&&
           aspect<=1.90f&&bounds.Top<canvas.Height*.72f)return "PROFILE_CONTAINER";
        if(bounds.Top>=canvas.Height*.68f&&bounds.Width>=canvas.Width*.42f&&lines<=5)return "DIALOGUE_PANEL";
        if(bounds.Top>=canvas.Height*.66f&&aspect>=2.05f&&lines is >=2 and <=6)return "DIALOGUE_PANEL";
        if(bounds.Width<=canvas.Width*.38f&&bounds.Height<=canvas.Height*.32f&&lines>=2)return "TASK_CARD";
        if(lines>=5&&bounds.Top<canvas.Height*.72f)return "INFO_PANEL";
        if(cluster.Count>=2&&bounds.Width<=canvas.Width*.38f)return "TASK_CARD";
        if(cluster.Any(x=>x.Lines.Count>=3))return "PANEL";
        return "FLAT_CARD_OR_CONTROL";
    }

    private static int RestoreVerifiedContainerSurface(Bitmap target,Bitmap source,bool[,] mask,Rectangle bounds,
        bool[,] materialClosure,VisualBlock block,VerifiedContainerSurfaceModel model,RestorationStageCapture? capture=null)
    {
        var profile=model.ContainerType=="PROFILE_CONTAINER";
        if(profile)
            return RestoreFeatheredLocalSurface(target,source,mask,materialClosure,bounds,
                (x,y)=>model.ResidualGrid.At(x,y),capture);
        var changed=0;var scan=Rectangle.Intersect(bounds,model.Bounds);
        capture?.DonorExclusion(model.CleanDonorExclusion,model.Bounds.Location,true,"Verified container clean donor exclusion, with model bounds as origin");
        // The shared plane/grid are a stable fallback.  Actual pixels come from a
        // directionally continuous surface whose endpoints are outside every source
        // line in this container, so old glyph material cannot become its own donor.
        for(var y=scan.Top;y<scan.Bottom;y++)for(var x=scan.Left;x<scan.Right;x++)
        {
            if(!mask[x,y])continue;
            var replacement=CleanLineDonorContinuation(source,model,x,y);
            capture?.Candidate(x,y,replacement,1f);
            if(target.GetPixel(x,y).ToArgb()!=replacement.ToArgb())
            {target.SetPixel(x,y,replacement);changed++;}
        }
        return changed;
    }

    private static Color CleanLineDonorContinuation(Bitmap source,VerifiedContainerSurfaceModel model,int x,int y)
    {
        bool Excluded(int xx,int yy)=>!model.Bounds.Contains(xx,yy)||
            model.CleanDonorExclusion[xx-model.Bounds.Left,yy-model.Bounds.Top];
        return DirectionalCleanContinuation(source,model.Bounds,Excluded,x,y,model.ResidualGrid.At(x,y));
    }

    private static Color DirectionalCleanContinuation(Bitmap source,Rectangle domain,Func<int,int,bool> excluded,
        int x,int y,Color fallback)
    {
        var radius=Math.Clamp(Math.Max(24,Math.Min(domain.Width,domain.Height)/3),24,112);
        Point? Find(int dx,int dy)
        {
            for(var distance=1;distance<=radius;distance++)
            {
                var xx=x+dx*distance;var yy=y+dy*distance;
                if(!domain.Contains(xx,yy))break;
                if(excluded(xx,yy))continue;
                return new(xx,yy);
            }
            return null;
        }
        static float Difference(Color a,Color b)=>Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B);
        static Color Mix(Color a,Color b,float t)=>Color.FromArgb(
            Math.Clamp((int)MathF.Round(a.R*(1-t)+b.R*t),0,255),
            Math.Clamp((int)MathF.Round(a.G*(1-t)+b.G*t),0,255),
            Math.Clamp((int)MathF.Round(a.B*(1-t)+b.B*t),0,255));
        (Color Color,float Score)? Axis(Point? first,Point? second,bool horizontal)
        {
            if(first is null||second is null)return null;
            var a=first.Value;var b=second.Value;
            var ca=source.GetPixel(a.X,a.Y);var cb=source.GetPixel(b.X,b.Y);
            var t=horizontal?(x-a.X)/(float)Math.Max(1,b.X-a.X):(y-a.Y)/(float)Math.Max(1,b.Y-a.Y);
            var span=Math.Abs(b.X-a.X)+Math.Abs(b.Y-a.Y);
            return(Mix(ca,cb,Math.Clamp(t,0,1)),Difference(ca,cb)+span*.22f);
        }
        var horizontal=Axis(Find(-1,0),Find(1,0),true);
        var vertical=Axis(Find(0,-1),Find(0,1),false);
        if(horizontal is null&&vertical is null)return fallback;
        if(horizontal is null)return vertical!.Value.Color;
        if(vertical is null)return horizontal.Value.Color;
        var hWeight=1f/(1f+horizontal.Value.Score);var vWeight=1f/(1f+vertical.Value.Score);
        var total=hWeight+vWeight;
        return Color.FromArgb(
            Math.Clamp((int)MathF.Round((horizontal.Value.Color.R*hWeight+vertical.Value.Color.R*vWeight)/total),0,255),
            Math.Clamp((int)MathF.Round((horizontal.Value.Color.G*hWeight+vertical.Value.Color.G*vWeight)/total),0,255),
            Math.Clamp((int)MathF.Round((horizontal.Value.Color.B*hWeight+vertical.Value.Color.B*vWeight)/total),0,255));
    }

    private static bool[,] BuildSemanticTextMaterialAuthority(bool[,] admitted,Rectangle searchBounds,
        VisualBlock block,SourceStyleEvidenceR2 evidence,Size canvas,bool[,] protectedPixels,
        VerifiedContainerSurfaceModel? model,out bool[,] materialClosure)
    {
        var lineHeights=block.Lines.Select(x=>Math.Max(1f,x.Bounds.Height)).OrderBy(x=>x).ToArray();
        if(lineHeights.Length==0){materialClosure=admitted;return admitted;}
        var medianHeight=lineHeights[lineHeights.Length/2];
        var radius=Math.Clamp((int)MathF.Ceiling(Math.Max(evidence.OutlineWidth+1f,medianHeight*.035f)),2,5);
        var grown=DilateMask(admitted,searchBounds,radius);
        var envelopes=block.Lines.Select(line=>Rectangle.Intersect(searchBounds,
            Clamp(Rectangle.Inflate(Rectangle.Round(line.Bounds),radius+1,radius+1),canvas))).ToArray();
        materialClosure=(bool[,])admitted.Clone();
        foreach(var envelope in envelopes)
            for(var y=envelope.Top;y<envelope.Bottom;y++)for(var x=envelope.Left;x<envelope.Right;x++)
                if(grown[x,y]&&!protectedPixels[x,y])materialClosure[x,y]=true;
        if(model is not null&&model.ContainerType!="PROFILE_CONTAINER")
        {
            var lineAuthority=(bool[,])materialClosure.Clone();
            foreach(var envelope in envelopes)
                for(var y=envelope.Top;y<envelope.Bottom;y++)for(var x=envelope.Left;x<envelope.Right;x++)
                    if(!protectedPixels[x,y])lineAuthority[x,y]=true;
            return lineAuthority;
        }
        if(model is null&&!IsWideSceneSubtitle(block,canvas))return admitted;
        var feathered=DilateMask(materialClosure,searchBounds,10);
        var featherScan=Rectangle.Intersect(new Rectangle(0,0,canvas.Width,canvas.Height),
            Rectangle.Inflate(searchBounds,10,10));
        for(var y=featherScan.Top;y<featherScan.Bottom;y++)for(var x=featherScan.Left;x<featherScan.Right;x++)
            if(protectedPixels[x,y]&&!admitted[x,y])feathered[x,y]=false;
        return feathered;
    }

    private static bool IsWideSceneSubtitle(VisualBlock block,Size canvas)=>block.Lines.Count==1&&
        block.Bounds.Top>=canvas.Height*.70f&&block.Bounds.Width>=canvas.Width*.35f;

    private static int RestoreBlockLineSurface(Bitmap target,Bitmap source,bool[,] mask,Rectangle bounds,
        bool[,] materialClosure,VisualBlock block,Color fallback,RestorationStageCapture? capture=null)
    {
        return RestoreFeatheredLocalSurface(target,source,mask,materialClosure,bounds,(_,_)=>fallback,capture);
    }

    private static int RestoreFeatheredLocalSurface(Bitmap target,Bitmap source,bool[,] authority,
        bool[,] materialClosure,Rectangle bounds,Func<int,int,Color> fallbackAt,RestorationStageCapture? capture=null)
    {
        const int feather=10;var changed=0;var donorExclusion=DilateMask(materialClosure,bounds,2);
        capture?.DonorExclusion(donorExclusion,Point.Empty,true,"Actual local-continuation exclusion: materialClosure dilated by two; not a proof of globally clean donors");
        var scan=Rectangle.Intersect(new Rectangle(0,0,source.Width,source.Height),Rectangle.Inflate(bounds,feather,feather));
        int DistanceToMaterial(int x,int y)
        {
            if(materialClosure[x,y])return 0;
            for(var distance=1;distance<=feather;distance++)
                for(var oy=-distance;oy<=distance;oy++)for(var ox=-distance;ox<=distance;ox++)
                {
                    if(Math.Abs(ox)!=distance&&Math.Abs(oy)!=distance)continue;
                    var xx=x+ox;var yy=y+oy;
                    if(xx>=scan.Left&&xx<scan.Right&&yy>=scan.Top&&yy<scan.Bottom&&materialClosure[xx,yy])
                        return distance;
                }
            return feather+1;
        }
        for(var y=scan.Top;y<scan.Bottom;y++)for(var x=scan.Left;x<scan.Right;x++)
        {
            if(!authority[x,y])continue;
            var distance=DistanceToMaterial(x,y);
            var weight=distance==0?1f:Math.Clamp(1f-distance/(float)(feather+1),0f,1f);
            weight*=weight*(3f-2f*weight);
            var reconstructed=LocalContinuation(source,donorExclusion,x,y,48,fallbackAt(x,y));
            capture?.Candidate(x,y,reconstructed,weight);
            var original=source.GetPixel(x,y);
            var replacement=Color.FromArgb(
                Math.Clamp((int)MathF.Round(original.R*(1-weight)+reconstructed.R*weight),0,255),
                Math.Clamp((int)MathF.Round(original.G*(1-weight)+reconstructed.G*weight),0,255),
                Math.Clamp((int)MathF.Round(original.B*(1-weight)+reconstructed.B*weight),0,255));
            if(target.GetPixel(x,y).ToArgb()!=replacement.ToArgb())
            {target.SetPixel(x,y,replacement);changed++;}
        }
        return changed;
    }

    private static int RestoreExemplarTextSurface(Bitmap target,Bitmap source,bool[,] mask,Rectangle bounds,
        Rectangle domain,Func<int,int,Color> fallbackAt)
    {
        var changed=0;var donorExclusion=DilateMask(mask,bounds,2);var componentId=0;
        foreach(var component in EnumerateMaskComponents(mask,bounds))
        {
            componentId++;
            var componentBounds=Rectangle.FromLTRB(component.Min(p=>p.X),component.Min(p=>p.Y),
                component.Max(p=>p.X)+1,component.Max(p=>p.Y)+1);
            var samples=SampleDonors(component,Math.Min(96,component.Count));
            var boundary=new List<(Point Inside,Point Outside)>();
            foreach(var p in component)
            {
                foreach(var (dx,dy) in new[]{(-1,0),(1,0),(0,-1),(0,1)})
                {
                    var q=new Point(p.X+dx,p.Y+dy);
                    if(!domain.Contains(q)||mask[q.X,q.Y])continue;
                    boundary.Add((p,q));break;
                }
            }
            if(boundary.Count>96)
            {
                var reduced=new List<(Point Inside,Point Outside)>(96);
                for(var i=0;i<96;i++)reduced.Add(boundary[(int)((long)i*boundary.Count/96)]);
                boundary=reduced;
            }
            var maxOffset=Math.Clamp(Math.Max(componentBounds.Width,componentBounds.Height)/2+32,40,128);
            var offsets=new HashSet<(int X,int Y)>();
            for(var dy=-maxOffset;dy<=maxOffset;dy+=8)for(var dx=-maxOffset;dx<=maxOffset;dx+=8)
                if(dx!=0||dy!=0)offsets.Add((dx,dy));
            for(var d=4;d<=maxOffset;d+=4)
            {
                offsets.Add((d,0));offsets.Add((-d,0));offsets.Add((0,d));offsets.Add((0,-d));
            }
            (int X,int Y)? best=null;double bestCost=double.PositiveInfinity;
            foreach(var offset in offsets)
            {
                var valid=true;
                foreach(var p in samples)
                {
                    var xx=p.X+offset.X;var yy=p.Y+offset.Y;
                    if(!domain.Contains(xx,yy)||donorExclusion[xx,yy]){valid=false;break;}
                }
                if(!valid)continue;
                var cost=0d;
                foreach(var pair in boundary)
                {
                    var xx=pair.Inside.X+offset.X;var yy=pair.Inside.Y+offset.Y;
                    if(!domain.Contains(xx,yy)||donorExclusion[xx,yy]){valid=false;break;}
                    var a=source.GetPixel(pair.Outside.X,pair.Outside.Y);var b=source.GetPixel(xx,yy);
                    cost+=Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B);
                }
                if(!valid)continue;
                cost/=Math.Max(1,boundary.Count);
                cost+=(Math.Abs(offset.X)+Math.Abs(offset.Y))*.035;
                if(cost<bestCost){bestCost=cost;best=offset;}
            }
            if(best is not { } chosen)
            {
                foreach(var p in component)
                {
                    var replacement=fallbackAt(p.X,p.Y);
                    if(target.GetPixel(p.X,p.Y).ToArgb()!=replacement.ToArgb())
                    {target.SetPixel(p.X,p.Y,replacement);changed++;}
                }
                continue;
            }
            var redDelta=new List<int>();var greenDelta=new List<int>();var blueDelta=new List<int>();
            foreach(var pair in boundary)
            {
                var a=source.GetPixel(pair.Outside.X,pair.Outside.Y);
                var b=source.GetPixel(pair.Inside.X+chosen.X,pair.Inside.Y+chosen.Y);
                redDelta.Add(a.R-b.R);greenDelta.Add(a.G-b.G);blueDelta.Add(a.B-b.B);
            }
            static int MedianDelta(List<int> values)
            {if(values.Count==0)return 0;values.Sort();return Math.Clamp(values[values.Count/2],-48,48);}
            var dr=MedianDelta(redDelta);var dg=MedianDelta(greenDelta);var db=MedianDelta(blueDelta);
            foreach(var p in component)
            {
                var xx=p.X+chosen.X;var yy=p.Y+chosen.Y;
                var replacement=domain.Contains(xx,yy)&&!donorExclusion[xx,yy]
                    ?source.GetPixel(xx,yy):fallbackAt(p.X,p.Y);
                replacement=Color.FromArgb(Math.Clamp(replacement.R+dr,0,255),
                    Math.Clamp(replacement.G+dg,0,255),Math.Clamp(replacement.B+db,0,255));
                if(target.GetPixel(p.X,p.Y).ToArgb()!=replacement.ToArgb())
                {target.SetPixel(p.X,p.Y,replacement);changed++;}
            }
        }
        return changed;
    }


    private static Color SharedSurfaceWithLocalResidual(Bitmap source,bool[,] exclusion,
        VerifiedContainerSurfaceModel model,int x,int y,Color canonical)
    {
        static int Delta(Color a,Color b)=>Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B);
        var radius=Math.Clamp((int)MathF.Round(Math.Min(model.Bounds.Width,model.Bounds.Height)*.105f),18,52);
        Point? Find(int dx,int dy)
        {
            var outside=0;var strongEdges=0;
            for(var d=1;d<=radius;d++)
            {
                var xx=x+dx*d;var yy=y+dy*d;
                if(!model.Bounds.Contains(xx,yy))break;
                if(exclusion[xx,yy])continue;
                outside++;
                if(LocalEdgeStrength(source,xx,yy)>126)
                {
                    if(++strongEdges>=3)break;
                    continue;
                }
                if(outside>=1)return new(xx,yy);
            }
            return null;
        }
        var left=Find(-1,0);var right=Find(1,0);var top=Find(0,-1);var bottom=Find(0,1);
        (float R,float G,float B,float Score)? Axis(Point? a,Point? b,bool horizontal)
        {
            if(a is null||b is null)return null;
            var p=a.Value;var q=b.Value;var actualP=source.GetPixel(p.X,p.Y);var actualQ=source.GetPixel(q.X,q.Y);
            var modelP=model.ResidualGrid.At(p.X,p.Y);var modelQ=model.ResidualGrid.At(q.X,q.Y);
            var t=horizontal?(x-p.X)/(float)Math.Max(1,q.X-p.X):(y-p.Y)/(float)Math.Max(1,q.Y-p.Y);
            var rr=(actualP.R-modelP.R)*(1-t)+(actualQ.R-modelQ.R)*t;
            var gg=(actualP.G-modelP.G)*(1-t)+(actualQ.G-modelQ.G)*t;
            var bb=(actualP.B-modelP.B)*(1-t)+(actualQ.B-modelQ.B)*t;
            var distance=Math.Abs(q.X-p.X)+Math.Abs(q.Y-p.Y);
            return(rr,gg,bb,Delta(actualP,actualQ)+distance*.35f);
        }
        var h=Axis(left,right,true);var v=Axis(top,bottom,false);
        var chosen=h is null?v:v is null?h:h.Value.Score<=v.Value.Score?h:v;
        if(chosen is null)return canonical;
        var residual=chosen.Value;
        var confidence=residual.Score<=58?1f:residual.Score<=118?.82f:.58f;
        int Apply(int value,float correction)=>Math.Clamp((int)MathF.Round(value+Math.Clamp(correction,-96,96)*confidence),0,255);
        return Color.FromArgb(Apply(canonical.R,residual.R),Apply(canonical.G,residual.G),Apply(canonical.B,residual.B));
    }

    private static void WriteVerifiedContainerEvidence(string output,IReadOnlyList<VerifiedContainerSurfaceModel> models)
    {
        WriteCsvRows(Path.Combine(output,"CONTAINER-SURFACE.csv"),
            ["Fixture","ContainerId","ContainerType","Bounds","SamplePixels","ExcludedGlyphPixels","ExcludedEdgePixels",
             "ExcludedProtectedPixels","ModelType","FitError","P90Error","Confidence","SharedBlockCount","SharedLineCount","PerContainerMs","BlockIds"],
            models.Select(x=>new[]{Path.GetFileName(output.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)),
                x.ContainerId,x.ContainerType,R(x.Bounds),I(x.SamplePixels),I(x.ExcludedGlyphPixels),I(x.ExcludedEdgePixels),
                I(x.ExcludedProtectedPixels),x.ModelType,F(x.FitError),F(x.P90Error),F(x.Confidence),I(x.BlockIds.Count),
                I(x.SharedLineCount),F(x.ElapsedMs),string.Join('|',x.BlockIds)}));
    }

    private static void WriteProfilePanelLineEvidence(string output,Bitmap source,Bitmap restored,Bitmap final,
        CorePipelineDocument document,IReadOnlyDictionary<string,GlyphMaskResult> glyphs,
        IReadOnlyDictionary<string,VerifiedContainerSurfaceModel> surfaceByBlock,
        IReadOnlyList<BlockRenderAudit> audits)
    {
        var fixture=Path.GetFileName(output.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar));
        var auditByBlock=audits.ToDictionary(x=>x.BlockId,StringComparer.Ordinal);
        var profileBlocks=document.VisualBlocks.Where(block=>
            surfaceByBlock.TryGetValue(block.BlockId,out var model)&&model.ContainerType=="PROFILE_CONTAINER"&&
            glyphs.ContainsKey(block.BlockId)).ToArray();
        var lineRows=new List<string[]>();
        foreach(var block in profileBlocks)
        {
            var model=surfaceByBlock[block.BlockId];var glyph=glyphs[block.BlockId];
            var translation=document.Translations[block.BlockId];
            var translatedLines=translation.TranslatedText.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
            var ordered=block.Lines.OrderBy(x=>x.ReadingOrder).ToArray();
            for(var index=0;index<ordered.Length;index++)
            {
                var line=ordered[index];var scan=Rectangle.Intersect(glyph.SearchBounds,
                    Clamp(Rectangle.Inflate(Rectangle.Round(line.Bounds),5,5),source.Size));
                var basePixels=0;var haloPixels=0;var finalAuthority=0;var changed=0;var survivors=0;
                for(var y=scan.Top;y<scan.Bottom;y++)for(var x=scan.Left;x<scan.Right;x++)
                {
                    if(glyph.BaseMaterial[x,y])basePixels++;
                    if(glyph.HaloCandidate[x,y])haloPixels++;
                    if(!glyph.Admitted[x,y])continue;
                    finalAuthority++;
                    if(source.GetPixel(x,y).ToArgb()!=restored.GetPixel(x,y).ToArgb())changed++;
                    if(glyph.BaseMaterial[x,y])
                    {
                        var a=source.GetPixel(x,y);var b=final.GetPixel(x,y);
                        if(Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B)<=36)survivors++;
                    }
                }
                var translatedField=translatedLines.Length==0?"":translatedLines[
                    Math.Min(translatedLines.Length-1,(int)((long)index*translatedLines.Length/Math.Max(1,ordered.Length)))];
                var render=auditByBlock.GetValueOrDefault(block.BlockId);
                lineRows.Add([fixture,block.BlockId,line.SourceId,I(index+1),line.SourceText,translatedField,
                    translation.State==BlockTranslationState.Accepted?"YES":"NO",I(basePixels),I(haloPixels),
                    I(finalAuthority),model.SurfaceModelId,F(model.Confidence),I(changed),
                    F(survivors/(double)Math.Max(1,basePixels)),translation.FailureReason,
                    render?.AtomicCommit==true?"YES":"NO",$"CLEANUP:{block.BlockId}"]);
            }
        }
        WriteCsvRows(Path.Combine(output,"PROFILE-LINES.csv"),
            ["Fixture","BlockId","SourceId","LineIndex","SourceText","TranslatedField","TranslationAccepted",
             "BaseMaterialPixels","HaloPixels","FinalAuthorityPixels","SurfaceModelId","SurfaceConfidence",
             "ActualChangedPixels","ResidualScore","FallbackReason","FinalDrawCommitted","CleanupOwnerId"],lineRows);
        WriteCsvRows(Path.Combine(output,"PROFILE-TRANSLATION.csv"),
            ["Fixture","BlockId","SourceIds","SourceText","TranslatedText","TranslationAccepted","SurfaceModelId",
             "SharedLineCount","WholeProfileSourcePreserve","FinalDrawCommitted","CleanupOwnerId"],
            profileBlocks.Select(block=>
            {
                var translation=document.Translations[block.BlockId];var model=surfaceByBlock[block.BlockId];
                var render=auditByBlock.GetValueOrDefault(block.BlockId);
                return new[]{fixture,block.BlockId,string.Join('|',block.Lines.Select(x=>x.SourceId)),block.SourceText,
                    translation.TranslatedText,translation.State==BlockTranslationState.Accepted?"YES":"NO",model.SurfaceModelId,
                    I(model.SharedLineCount),"NO",render?.AtomicCommit==true?"YES":"NO",$"CLEANUP:{block.BlockId}"};
            }));
    }
}
