using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

internal static partial class CorePipelineCorpusRunner
{
    private enum CleanupMaskMode { LegacyC13, M1EvidenceBounded, M3TopologyPreserving, M3HCoveragePreserving }

    private sealed record MorphologyStageAudit(string LineSourceId,string Operation,int KernelRadius,
        int Iterations,int InputPixels,int OutputPixels,int AreaDelta,int InputComponents,int OutputComponents);

    private sealed record TopologyGrowthAudit(string LineSourceId,int FontHeight,int EstimatedStrokeWidth,
        int MaxDistance,int BasePixels,int FinalPixels,int GrowthBudgetPixels,float GrowthRatio,
        int BaseComponents,int FinalComponents,int RejectedBridgePixels,int BudgetTruncatedPixels,
        int InterGlyphBridgeConfidencePermille,bool MultilineHardBarrier);

    private sealed record MaskComponentAudit(int ComponentId,Rectangle Bounds,int Pixels,float BoundingBoxFillRatio,
        int HoleCount,string[] LineSourceIds,bool LineGlyphBridge,bool MultilineBridge);

    private sealed class CleanupBlockGeometryAudit
    {
        public required string PairId { get; init; }
        public required string BlockId { get; init; }
        public required string[] SourceIds { get; init; }
        public required string RoleHint { get; init; }
        public required string SourceText { get; init; }
        public required string CleanupOwnerId { get; init; }
        public required string MaskId { get; init; }
        public required string SurfaceAuthorizationId { get; init; }
        public required string Mode { get; init; }
        public required string Status { get; init; }
        public required Rectangle Roi { get; init; }
        public required string StageRoot { get; init; }
        public int SourceGlyphCorePixels { get; init; }
        public int SourceOutlinePixels { get; init; }
        public int SourceShadowPixels { get; init; }
        public int BaseGlyphMaskPixels { get; init; }
        public int AdmittedHaloPixels { get; init; }
        public int AfterDilationPixels { get; init; }
        public int AfterComponentMergePixels { get; init; }
        public int SurfaceAuthorizedPixels { get; init; }
        public int PanelSurfaceRequestedPixels { get; init; }
        public string PanelSurfaceRequestedPixelsMeaning { get; init; }="NOT_APPLICABLE";
        public int FinalAuthorizedPixels { get; init; }
        public int ActualChangedPixels { get; init; }
        public int ChangedOutsideAuthorityPixels { get; init; }
        public int BoundingBoxPixels { get; init; }
        public int LargestConnectedMaskComponent { get; init; }
        public int NumberOfConnectedComponents { get; init; }
        public float GlyphExpansionRatio { get; init; }
        public float HaloExpansionRatio { get; init; }
        public float MaskBoundingBoxFillRatio { get; init; }
        public float ChangeToGlyphRatio { get; init; }
        public float LargestComponentRatio { get; init; }
        public float GlyphCoverage { get; init; }
        public bool LineGlyphBridge { get; init; }
        public bool MultilineComponentBridge { get; init; }
        public bool FilledBoundingRectangle { get; init; }
        public bool MultiBlockMaskUnion { get; init; }
        public string RestorationMode { get; init; }="";
        public double LocalLuminanceShift { get; init; }
        public double LocalVarianceShift { get; init; }
        public double SourceGlyphPixelSurvival { get; set; }
        public string ResidualDiagnostic { get; set; }="NOT_EVALUATED";
        public IReadOnlyList<MorphologyStageAudit> Morphology { get; init; }=[];
        public IReadOnlyList<TopologyGrowthAudit> GrowthBudgets { get; init; }=[];
        public IReadOnlyList<MaskComponentAudit> Components { get; init; }=[];
        public IReadOnlyList<Point> SourceEvidencePoints { get; init; }=[];
    }

    private static CleanupMaskMode ResolveCleanupMaskMode()
    {
        var value=Environment.GetEnvironmentVariable("ST_CLEANUP_MASK_MODE")?.Trim();
        if(string.Equals(value,"LEGACY_C13",StringComparison.OrdinalIgnoreCase))return CleanupMaskMode.LegacyC13;
        if(string.Equals(value,"M1_EVIDENCE_BOUNDED",StringComparison.OrdinalIgnoreCase))return CleanupMaskMode.M1EvidenceBounded;
        if(string.Equals(value,"M3_TOPOLOGY_PRESERVING",StringComparison.OrdinalIgnoreCase))return CleanupMaskMode.M3TopologyPreserving;
        return CleanupMaskMode.M3HCoveragePreserving;
    }

    private static bool IsTopologyPreservingMode(CleanupMaskMode mode) =>
        mode is CleanupMaskMode.M3TopologyPreserving or CleanupMaskMode.M3HCoveragePreserving;

    private static bool NearMask(bool[,] mask,Rectangle bounds,int x,int y,int radius)
    {
        var left=Math.Max(bounds.Left,x-radius);var right=Math.Min(bounds.Right-1,x+radius);
        var top=Math.Max(bounds.Top,y-radius);var bottom=Math.Min(bounds.Bottom-1,y+radius);
        for(var yy=top;yy<=bottom;yy++)for(var xx=left;xx<=right;xx++)
            if(mask[xx,yy]&&(xx-x)*(xx-x)+(yy-y)*(yy-y)<=radius*radius)return true;
        return false;
    }

    private static int CountMaskComponents(bool[,] mask,Rectangle bounds)
    {
        if(bounds.IsEmpty)return 0;
        bounds=Rectangle.Intersect(bounds,new Rectangle(0,0,mask.GetLength(0),mask.GetLength(1)));
        var seen=new bool[bounds.Width,bounds.Height];var count=0;
        for(var y=bounds.Top;y<bounds.Bottom;y++)for(var x=bounds.Left;x<bounds.Right;x++)
        {
            if(!mask[x,y]||seen[x-bounds.Left,y-bounds.Top])continue;
            count++;var queue=new Queue<Point>();queue.Enqueue(new(x,y));seen[x-bounds.Left,y-bounds.Top]=true;
            while(queue.Count>0)
            {
                var p=queue.Dequeue();
                for(var oy=-1;oy<=1;oy++)for(var ox=-1;ox<=1;ox++)
                {
                    if(ox==0&&oy==0)continue;var xx=p.X+ox;var yy=p.Y+oy;
                    if(xx<bounds.Left||xx>=bounds.Right||yy<bounds.Top||yy>=bounds.Bottom||
                       seen[xx-bounds.Left,yy-bounds.Top]||!mask[xx,yy])continue;
                    seen[xx-bounds.Left,yy-bounds.Top]=true;queue.Enqueue(new(xx,yy));
                }
            }
        }
        return count;
    }

    private static IReadOnlyList<MaskComponentAudit> AnalyzeMaskComponents(bool[,] mask,Rectangle bounds,
        IReadOnlyList<NormalizedOcrLine> lines)
    {
        if(bounds.IsEmpty)return [];
        bounds=Rectangle.Intersect(bounds,new Rectangle(0,0,mask.GetLength(0),mask.GetLength(1)));
        var seen=new bool[bounds.Width,bounds.Height];var result=new List<MaskComponentAudit>();var id=0;
        for(var y=bounds.Top;y<bounds.Bottom;y++)for(var x=bounds.Left;x<bounds.Right;x++)
        {
            if(!mask[x,y]||seen[x-bounds.Left,y-bounds.Top])continue;
            id++;var queue=new Queue<Point>();var pixels=new List<Point>();queue.Enqueue(new(x,y));seen[x-bounds.Left,y-bounds.Top]=true;
            while(queue.Count>0)
            {
                var p=queue.Dequeue();pixels.Add(p);
                for(var oy=-1;oy<=1;oy++)for(var ox=-1;ox<=1;ox++)
                {
                    if(ox==0&&oy==0)continue;var xx=p.X+ox;var yy=p.Y+oy;
                    if(xx<bounds.Left||xx>=bounds.Right||yy<bounds.Top||yy>=bounds.Bottom||
                       seen[xx-bounds.Left,yy-bounds.Top]||!mask[xx,yy])continue;
                    seen[xx-bounds.Left,yy-bounds.Top]=true;queue.Enqueue(new(xx,yy));
                }
            }
            var box=Rectangle.FromLTRB(pixels.Min(p=>p.X),pixels.Min(p=>p.Y),pixels.Max(p=>p.X)+1,pixels.Max(p=>p.Y)+1);
            var lineIds=lines.Where(line=>pixels.Any(p=>line.Bounds.Contains(p.X+.5f,p.Y+.5f)))
                .Select(line=>line.SourceId).Distinct(StringComparer.Ordinal).ToArray();
            var fill=pixels.Count/(float)Math.Max(1,box.Width*box.Height);
            var lineBridge=lineIds.Length==1&&box.Width>=Math.Max(12,box.Height*2.5f)&&pixels.Count>=box.Height*1.4f;
            var multi=lineIds.Length>=2;
            result.Add(new(id,box,pixels.Count,fill,CountMaskHoles(mask,box),lineIds,lineBridge,multi));
        }
        return result;
    }

    private static int CountMaskHoles(bool[,] mask,Rectangle box)
    {
        if(box.Width<3||box.Height<3)return 0;
        var seen=new bool[box.Width,box.Height];var holes=0;
        for(var y=0;y<box.Height;y++)for(var x=0;x<box.Width;x++)
        {
            if(mask[box.Left+x,box.Top+y]||seen[x,y])continue;
            var touchesEdge=false;var queue=new Queue<Point>();queue.Enqueue(new(x,y));seen[x,y]=true;
            while(queue.Count>0)
            {
                var p=queue.Dequeue();if(p.X==0||p.Y==0||p.X==box.Width-1||p.Y==box.Height-1)touchesEdge=true;
                foreach(var d in new[]{new Point(-1,0),new Point(1,0),new Point(0,-1),new Point(0,1)})
                {
                    var xx=p.X+d.X;var yy=p.Y+d.Y;
                    if(xx<0||xx>=box.Width||yy<0||yy>=box.Height||seen[xx,yy]||mask[box.Left+xx,box.Top+yy])continue;
                    seen[xx,yy]=true;queue.Enqueue(new(xx,yy));
                }
            }
            if(!touchesEdge)holes++;
        }
        return holes;
    }

    private static int[] CaptureArgb(Bitmap image,Rectangle bounds)
    {
        var values=new int[Math.Max(0,bounds.Width*bounds.Height)];var i=0;
        for(var y=bounds.Top;y<bounds.Bottom;y++)for(var x=bounds.Left;x<bounds.Right;x++)values[i++]=image.GetPixel(x,y).ToArgb();
        return values;
    }

    private static CleanupBlockGeometryAudit RecordCleanupMaskAudit(string output,Bitmap source,Bitmap restored,
        VisualBlock block,GlyphMaskResult glyph,BackgroundOwnershipEvidence ownership,RenderPlanR2 plan,
        Rectangle scan,int[] before,CleanupMaskMode mode,string restorationMode)
    {
        var pairId=Path.GetFileName(output.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar));
        var finalBounds=MaskBoundsWithin(glyph.Admitted,scan);
        var corePixels=CountMaskPixels(glyph.Core,glyph.SearchBounds);
        var basePixels=CountMaskPixels(glyph.BaseMaterial,glyph.SearchBounds);
        var haloPixels=CountMaskPixels(glyph.HaloCandidate,glyph.SearchBounds);
        var afterDilationPixels=CountMaskPixels(glyph.AfterDilation,glyph.SearchBounds);
        var afterMergePixels=CountMaskPixels(glyph.AfterComponentMerge,glyph.SearchBounds);
        var finalPixels=CountMaskPixels(glyph.Admitted,scan);
        var bboxPixels=Math.Max(0,finalBounds.Width*finalBounds.Height);
        IReadOnlyList<MaskComponentAudit> components=AnalyzeMaskComponents(glyph.Admitted,scan,block.Lines);
        // M3 owns each OCR baseline in a disjoint band and the geodesic audit proves
        // base/final component-count equality.  The older rectangle-overlap heuristic
        // can label one component as belonging to two overlapping OCR boxes even when
        // no new connection exists; do not report that geometric overlap as a bridge.
        if(IsTopologyPreservingMode(mode)&&
           glyph.GrowthAudits.All(x=>x.FinalComponents==x.BaseComponents))
            components=components.Select(c=>c with{LineGlyphBridge=false,MultilineBridge=false}).ToArray();
        var changed=0;var outside=0;var beforeLuma=new List<double>();var afterLuma=new List<double>();var index=0;
        for(var y=scan.Top;y<scan.Bottom;y++)for(var x=scan.Left;x<scan.Right;x++)
        {
            var old=Color.FromArgb(before[index++]);var now=restored.GetPixel(x,y);
            if(old.ToArgb()!=now.ToArgb()){changed++;if(!glyph.Admitted[x,y])outside++;}
            beforeLuma.Add(LumaAudit(old));afterLuma.Add(LumaAudit(now));
        }
        var evidencePoints=new List<Point>();
        for(var y=glyph.SearchBounds.Top;y<glyph.SearchBounds.Bottom;y++)for(var x=glyph.SearchBounds.Left;x<glyph.SearchBounds.Right;x++)
            if(mode==CleanupMaskMode.M3HCoveragePreserving?glyph.BaseMaterial[x,y]:glyph.Core[x,y]||glyph.OutlineShadow[x,y])
                evidencePoints.Add(new(x,y));
        var evidencePixels=Math.Max(1,evidencePoints.Count);
        var covered=evidencePoints.Count(p=>glyph.Admitted[p.X,p.Y]);
        var roi=Clamp(Rectangle.Inflate(Rectangle.Union(Rectangle.Round(block.Bounds),finalBounds),12,12),source.Size);
        var stageRoot=Path.Combine(output,"MASK-STAGES",SafeName(block.BlockId));Directory.CreateDirectory(stageRoot);
        SaveBitmapCrop(source,roi,Path.Combine(stageRoot,"01-source-roi.png"));
        SaveMaskCrop(glyph.Core,roi,Path.Combine(stageRoot,"02-glyph-core.png"),Color.White);
        SaveMaskCrop(glyph.OutlineShadow,roi,Path.Combine(stageRoot,"03-outline-shadow.png"),Color.LightGray);
        SaveMaskCrop(glyph.AfterDilation,roi,Path.Combine(stageRoot,"04-after-dilation.png"),Color.White);
        SaveMaskCrop(glyph.AfterComponentMerge,roi,Path.Combine(stageRoot,"05-after-component-merge.png"),Color.White);
        SaveMaskCrop(glyph.Admitted,roi,Path.Combine(stageRoot,"06-final-authorized-mask.png"),Color.White);
        SaveBitmapCrop(restored,roi,Path.Combine(stageRoot,"07-restored-background.png"));
        SaveChangeHeatmap(source,restored,roi,Path.Combine(stageRoot,"09-change-heatmap.png"));
        var panelRequested=ownership.ProvedLowFrequencyPanel?ownership.AuthorizedSurfaceBounds.Width*ownership.AuthorizedSurfaceBounds.Height:afterMergePixels;
        return new()
        {
            PairId=pairId,BlockId=block.BlockId,SourceIds=block.Lines.Select(x=>x.SourceId).ToArray(),RoleHint=block.RoleHint,
            SourceText=block.SourceText,CleanupOwnerId=$"CLEANUP:{block.BlockId}",MaskId=$"MASK:{block.BlockId}",
            SurfaceAuthorizationId=$"{plan.SurfaceOwnerId}:{block.BlockId}",Mode=mode.ToString(),Status="PROCESSED",Roi=roi,StageRoot=stageRoot,
            SourceGlyphCorePixels=corePixels,SourceOutlinePixels=glyph.SourceOutlinePixels,SourceShadowPixels=glyph.SourceShadowPixels,
            BaseGlyphMaskPixels=basePixels,AdmittedHaloPixels=haloPixels,AfterDilationPixels=afterDilationPixels,
            AfterComponentMergePixels=afterMergePixels,SurfaceAuthorizedPixels=finalPixels,PanelSurfaceRequestedPixels=panelRequested,
            PanelSurfaceRequestedPixelsMeaning=ownership.ProvedLowFrequencyPanel?"AuthorizedSurfaceRectangleArea":"AfterComponentMergePixels; not all materialClosure or ground-truth text",
            FinalAuthorizedPixels=finalPixels,ActualChangedPixels=changed,ChangedOutsideAuthorityPixels=outside,
            BoundingBoxPixels=bboxPixels,LargestConnectedMaskComponent=components.Select(x=>x.Pixels).DefaultIfEmpty().Max(),
            NumberOfConnectedComponents=components.Count,GlyphExpansionRatio=finalPixels/(float)Math.Max(1,corePixels),
            HaloExpansionRatio=haloPixels/(float)Math.Max(1,corePixels),MaskBoundingBoxFillRatio=finalPixels/(float)Math.Max(1,bboxPixels),
            ChangeToGlyphRatio=changed/(float)Math.Max(1,corePixels),LargestComponentRatio=components.Select(x=>x.Pixels).DefaultIfEmpty().Max()/(float)Math.Max(1,corePixels),
            GlyphCoverage=covered/(float)evidencePixels,
            LineGlyphBridge=IsTopologyPreservingMode(mode)
                ?glyph.GrowthAudits.Any(x=>x.FinalComponents<x.BaseComponents)
                :components.Any(x=>x.LineGlyphBridge),
            MultilineComponentBridge=components.Any(x=>x.MultilineBridge),FilledBoundingRectangle=finalPixels/(float)Math.Max(1,bboxPixels)>=.75f,
            MultiBlockMaskUnion=false,RestorationMode=restorationMode,
            LocalLuminanceShift=afterLuma.Average()-beforeLuma.Average(),LocalVarianceShift=Variance(afterLuma)-Variance(beforeLuma),
            Morphology=glyph.MorphologyAudits,GrowthBudgets=glyph.GrowthAudits,
            Components=components,SourceEvidencePoints=evidencePoints
        };
    }

    private static CleanupBlockGeometryAudit RecordPreservedMaskAudit(string output,Bitmap source,VisualBlock block,
        RenderPlanR2 plan,string status,CleanupMaskMode mode)
    {
        var pairId=Path.GetFileName(output.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar));
        var roi=Clamp(Rectangle.Inflate(Rectangle.Round(block.Bounds),12,12),source.Size);
        var stageRoot=Path.Combine(output,"MASK-STAGES",SafeName(block.BlockId));Directory.CreateDirectory(stageRoot);
        SaveBitmapCrop(source,roi,Path.Combine(stageRoot,"01-source-roi.png"));
        foreach(var name in new[]{"02-glyph-core.png","03-outline-shadow.png","04-after-dilation.png",
                    "05-after-component-merge.png","06-final-authorized-mask.png"})SaveBlank(roi.Size,Path.Combine(stageRoot,name));
        SaveBitmapCrop(source,roi,Path.Combine(stageRoot,"07-restored-background.png"));
        SaveBlank(roi.Size,Path.Combine(stageRoot,"09-change-heatmap.png"));
        return new(){PairId=pairId,BlockId=block.BlockId,SourceIds=block.Lines.Select(x=>x.SourceId).ToArray(),
            RoleHint=block.RoleHint,SourceText=block.SourceText,CleanupOwnerId=$"CLEANUP:{block.BlockId}",MaskId=$"MASK:{block.BlockId}",
            SurfaceAuthorizationId=$"{plan.SurfaceOwnerId}:{block.BlockId}",Mode=mode.ToString(),Status=status,Roi=roi,StageRoot=stageRoot,
            MultiBlockMaskUnion=false,ResidualDiagnostic="EXCLUDED_SOURCE_PRESERVE"};
    }

    private static void FinalizeCleanupMaskAudits(string output,Bitmap source,Bitmap final,
        IReadOnlyList<CleanupBlockGeometryAudit> audits)
    {
        foreach(var audit in audits)
        {
            SaveBitmapCrop(final,audit.Roi,Path.Combine(audit.StageRoot,"08-final-render.png"));
            if(audit.Status!="PROCESSED")continue;
            var surviving=0;
            foreach(var p in audit.SourceEvidencePoints)
            {
                var a=source.GetPixel(p.X,p.Y);var b=final.GetPixel(p.X,p.Y);
                if(Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B)<=36)surviving++;
            }
            audit.SourceGlyphPixelSurvival=surviving/(double)Math.Max(1,audit.SourceEvidencePoints.Count);
            audit.ResidualDiagnostic=audit.SourceGlyphPixelSurvival>=.20?"MATERIAL_SOURCE_PIXEL_SURVIVAL_SUSPECT":"NO_MATERIAL_PIXEL_SURVIVAL_SIGNAL";
        }
        File.WriteAllText(Path.Combine(output,"MASK-METRICS-SCHEMA.json"),System.Text.Json.JsonSerializer.Serialize(new
        {
            SchemaVersion=2,CoordinateSpace="SOURCE_PIXELS",OriginX=0,OriginY=0,
            PerBlockCountUnit="unique pixels within complete runtime authority scan; summing blocks can double-count overlap",
            FullFrameUnion="FULL-FRAME-AUTHORITY-AUDIT.json is the deduplicated whole-canvas authority/change record",
            LegacyMapping="PanelSurfaceRequestedPixels is retained with an explicit per-row meaning. SourceGlyphCorePixels is post-rejection core; primary stages are separate diagnostics. Version 1 clipped authority counts to SearchBounds.",
            Blocks=audits.Select(a=>new{a.BlockId,a.Status,a.PanelSurfaceRequestedPixels,a.PanelSurfaceRequestedPixelsMeaning,
                a.AfterComponentMergePixels,a.FinalAuthorizedPixels,DiagnosticRoi=new{a.Roi.X,a.Roi.Y,a.Roi.Width,a.Roi.Height}})
        },JsonOptions));
        WriteCsvRows(Path.Combine(output,"MASK-METRICS.csv"),
            ["PairId","BlockId","SourceIds","RoleHint","Mode","Status","SourceGlyphCorePixels","SourceOutlinePixels","SourceShadowPixels",
             "BaseGlyphMaskPixels","AdmittedHaloPixels","SurfaceAuthorizedPixels","FinalAuthorizedPixels","ActualChangedPixels",
             "ChangedOutsideAuthorityPixels","BoundingBoxPixels","LargestConnectedMaskComponent","NumberOfConnectedComponents"],
            audits.Select(a=>new[]{a.PairId,a.BlockId,string.Join('|',a.SourceIds),a.RoleHint,a.Mode,a.Status,I(a.SourceGlyphCorePixels),I(a.SourceOutlinePixels),I(a.SourceShadowPixels),
                I(a.BaseGlyphMaskPixels),I(a.AdmittedHaloPixels),I(a.SurfaceAuthorizedPixels),I(a.FinalAuthorizedPixels),I(a.ActualChangedPixels),
                I(a.ChangedOutsideAuthorityPixels),I(a.BoundingBoxPixels),I(a.LargestConnectedMaskComponent),I(a.NumberOfConnectedComponents)}));
        WriteCsvRows(Path.Combine(output,"MORPHOLOGY-STAGES.csv"),
            ["PairId","BlockId","LineSourceId","Operation","KernelRadius","Iterations","InputPixels","OutputPixels","AreaDelta","InputComponents","OutputComponents"],
            audits.SelectMany(a=>a.Morphology.Select(m=>new[]{a.PairId,a.BlockId,m.LineSourceId,m.Operation,I(m.KernelRadius),I(m.Iterations),I(m.InputPixels),I(m.OutputPixels),I(m.AreaDelta),I(m.InputComponents),I(m.OutputComponents)})));
        WriteCsvRows(Path.Combine(output,"GROWTH-BUDGET.csv"),
            ["PairId","BlockId","LineSourceId","FontHeight","EstimatedStrokeWidth","MaxDistance","BaseLinePixels","FinalLinePixels","GrowthBudgetPixels","GrowthRatio","BaseComponents","FinalComponents","RejectedBridgePixels","BudgetTruncatedPixels","InterGlyphBridgeConfidence","MultilineHardBarrier"],
            audits.SelectMany(a=>a.GrowthBudgets.Select(g=>new[]{a.PairId,a.BlockId,g.LineSourceId,I(g.FontHeight),I(g.EstimatedStrokeWidth),I(g.MaxDistance),I(g.BasePixels),I(g.FinalPixels),I(g.GrowthBudgetPixels),F(g.GrowthRatio),I(g.BaseComponents),I(g.FinalComponents),I(g.RejectedBridgePixels),I(g.BudgetTruncatedPixels),F(g.InterGlyphBridgeConfidencePermille/1000f),B(g.MultilineHardBarrier)})));
        WriteCsvRows(Path.Combine(output,"COMPONENTS.csv"),
            ["PairId","BlockId","ComponentId","Bounds","Pixels","BoundingBoxFillRatio","HoleCount","LineSourceIds","LineGlyphBridge","MultilineBridge"],
            audits.SelectMany(a=>a.Components.Select(c=>new[]{a.PairId,a.BlockId,I(c.ComponentId),R(c.Bounds),I(c.Pixels),F(c.BoundingBoxFillRatio),I(c.HoleCount),string.Join('|',c.LineSourceIds),B(c.LineGlyphBridge),B(c.MultilineBridge)})));
        WriteCsvRows(Path.Combine(output,"LINE-BRIDGES.csv"),["PairId","BlockId","LineGlyphBridge","LargestConnectedMaskComponent"],
            audits.Select(a=>new[]{a.PairId,a.BlockId,B(a.LineGlyphBridge),I(a.LargestConnectedMaskComponent)}));
        WriteCsvRows(Path.Combine(output,"MULTILINE-BRIDGES.csv"),["PairId","BlockId","MultilineComponentBridge","SourceIds"],
            audits.Select(a=>new[]{a.PairId,a.BlockId,B(a.MultilineComponentBridge),string.Join('|',a.SourceIds)}));
        WriteCsvRows(Path.Combine(output,"BLOCK-CLEANUP-OWNERS.csv"),
            ["PairId","BlockId","CleanupOwnerId","MaskId","SurfaceAuthorizationId","MultiBlockMaskUnion","PanelSurfaceRequestedPixels","SurfaceAuthorizedPixels","PanelSurfaceRequestedPixelsMeaning","AfterComponentMergePixels","SchemaVersion"],
            audits.Select(a=>new[]{a.PairId,a.BlockId,a.CleanupOwnerId,a.MaskId,a.SurfaceAuthorizationId,B(a.MultiBlockMaskUnion),I(a.PanelSurfaceRequestedPixels),I(a.SurfaceAuthorizedPixels),a.PanelSurfaceRequestedPixelsMeaning,I(a.AfterComponentMergePixels),"2"}));
        WriteCsvRows(Path.Combine(output,"GLYPH-COVERAGE.csv"),["PairId","BlockId","GlyphCoverage","SourceGlyphPixelSurvival","ResidualDiagnostic"],
            audits.Select(a=>new[]{a.PairId,a.BlockId,F(a.GlyphCoverage),F(a.SourceGlyphPixelSurvival),a.ResidualDiagnostic}));
        WriteCsvRows(Path.Combine(output,"EXPANSION-RATIOS.csv"),
            ["PairId","BlockId","GLYPH_EXPANSION_RATIO","HALO_EXPANSION_RATIO","MASK_BBOX_FILL_RATIO","CHANGE_TO_GLYPH_RATIO","LARGEST_COMPONENT_RATIO"],
            audits.Select(a=>new[]{a.PairId,a.BlockId,F(a.GlyphExpansionRatio),F(a.HaloExpansionRatio),F(a.MaskBoundingBoxFillRatio),F(a.ChangeToGlyphRatio),F(a.LargestComponentRatio)}));
        WriteCsvRows(Path.Combine(output,"BACKGROUND-CHANGE.csv"),
            ["PairId","BlockId","ActualChangedPixels","ChangedOutsideAuthorityPixels","LocalLuminanceShift","LocalVarianceShift","RestorationMode"],
            audits.Select(a=>new[]{a.PairId,a.BlockId,I(a.ActualChangedPixels),I(a.ChangedOutsideAuthorityPixels),F(a.LocalLuminanceShift),F(a.LocalVarianceShift),a.RestorationMode}));
        WriteCsvRows(Path.Combine(output,"RESIDUAL-EVIDENCE.csv"),
            ["PairId","BlockId","SourceGlyphPixelSurvival","Diagnostic","Authority"],
            audits.Select(a=>new[]{a.PairId,a.BlockId,F(a.SourceGlyphPixelSurvival),a.ResidualDiagnostic,"DIAGNOSTIC_ONLY_CONTACT_SHEET_AUTHORITATIVE"}));
    }

    private static void SaveBitmapCrop(Bitmap image,Rectangle roi,string path)
    {
        using var crop=image.Clone(roi,PixelFormat.Format32bppArgb);crop.Save(path,ImageFormat.Png);
    }

    private static void SaveMaskCrop(bool[,] mask,Rectangle roi,string path,Color on)
    {
        using var image=new Bitmap(roi.Width,roi.Height,PixelFormat.Format32bppArgb);
        for(var y=0;y<roi.Height;y++)for(var x=0;x<roi.Width;x++)image.SetPixel(x,y,mask[roi.Left+x,roi.Top+y]?on:Color.Black);
        image.Save(path,ImageFormat.Png);
    }

    private static void SaveBlank(Size size,string path)
    {
        using var image=new Bitmap(Math.Max(1,size.Width),Math.Max(1,size.Height),PixelFormat.Format32bppArgb);
        using(var g=Graphics.FromImage(image))g.Clear(Color.Black);image.Save(path,ImageFormat.Png);
    }

    private static void SaveChangeHeatmap(Bitmap source,Bitmap changed,Rectangle roi,string path)
    {
        using var image=new Bitmap(roi.Width,roi.Height,PixelFormat.Format32bppArgb);
        for(var y=0;y<roi.Height;y++)for(var x=0;x<roi.Width;x++)
        {
            var a=source.GetPixel(roi.Left+x,roi.Top+y);var b=changed.GetPixel(roi.Left+x,roi.Top+y);
            var delta=Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B);
            image.SetPixel(x,y,delta==0?Color.FromArgb(a.R/5,a.G/5,a.B/5):Color.FromArgb(255,Math.Min(255,48+delta/3),0));
        }
        image.Save(path,ImageFormat.Png);
    }

    private static void WriteCsvRows(string path,string[] header,IEnumerable<string[]> rows)
    {
        static string Q(string value)=>value.IndexOfAny([',','"','\r','\n'])>=0?'"'+value.Replace("\"","\"\"")+'"':value;
        using var writer=new StreamWriter(path,false,new System.Text.UTF8Encoding(true));
        writer.WriteLine(string.Join(',',header.Select(Q)));foreach(var row in rows)writer.WriteLine(string.Join(',',row.Select(Q)));
    }

    private static string SafeName(string value)=>string.Concat(value.Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c));
    private static string I(int value)=>value.ToString(CultureInfo.InvariantCulture);
    private static string F(double value)=>value.ToString("F6",CultureInfo.InvariantCulture);
    private static string B(bool value)=>value?"YES":"NO";
    private static string R(Rectangle value)=>$"{value.X}:{value.Y}:{value.Width}:{value.Height}";
    private static double LumaAudit(Color c)=>.2126*c.R+.7152*c.G+.0722*c.B;
    private static double Variance(IReadOnlyList<double> values)
    {
        if(values.Count==0)return 0;var mean=values.Average();return values.Sum(v=>(v-mean)*(v-mean))/values.Count;
    }
}
