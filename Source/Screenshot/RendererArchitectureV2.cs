using System.Security.Cryptography;
using System.Text;

namespace ScreenshotTranslationUiTester;

public enum VisualClusterRenderOutcome
{
    DRAW_SUCCESS_CLEAN, DRAW_SUCCESS_RESIDUAL_PRESENT, TRANSLATION_ALLOCATION_FAILED,
    TRANSLATION_ALLOCATION_SPANS_MULTIPLE_CLUSTERS, LAYOUT_FAILED, CLEANUP_FAILED,
    OWNERSHIP_REJECTED, COLLISION_REJECTED, READABILITY_REJECTED, COMMIT_FAILED,
    SOURCE_REMOVED_WITHOUT_TRANSLATION
}

public sealed record VisualClusterSourceLine(string SourceId,string Text,PointF[] Polygon,
    RectangleF GlyphBounds,RegionRoleType Role,int ReadingOrder,string OriginRegionId);
public sealed record VisualClusterV2(string VisualClusterId,string TranslationUnitId,
    IReadOnlyList<string> SourceIds,IReadOnlyList<VisualClusterSourceLine> SourceLines,
    IReadOnlyList<RectangleF> SourceGlyphBounds,RectangleF OwnedSurfaceBounds,
    IReadOnlyList<string> AllocationSegmentIds,RegionRoleType Role,int ReadingOrder);
public sealed record VisualClusterPlanDiagnostic(string VisualClusterId,string TranslationUnitId,
    IReadOnlyList<string> SourceIds,IReadOnlyList<string> AllocationSegmentIds,
    VisualClusterRenderOutcome Outcome,string Reason,RectangleF SourceGlyphBounds,
    RectangleF OwnedSurfaceBounds);
public sealed record VisualClusterRenderPlan(IReadOnlyList<RecognitionRegion> Regions,
    IReadOnlyList<VisualClusterV2> Clusters,IReadOnlyList<VisualClusterPlanDiagnostic> Diagnostics,
    bool SourceAligned);

public static class VisualClusterRenderPlannerV2
{
    public static VisualClusterRenderPlan Plan(RecognitionDocumentV2 document,Size imageSize)
    {
        if(!string.Equals(document.TranslationMappingMode,"SOURCE_ALIGNED_MAPPING_V1",StringComparison.Ordinal))
            return new(document.Regions.Select(x=>x.Clone()).ToArray(),[],[],false);

        var output=new List<RecognitionRegion>();var clusters=new List<VisualClusterV2>();
        var diagnostics=new List<VisualClusterPlanDiagnostic>();
        var regionBySource=document.Regions.SelectMany(r=>r.SourceBlockIds.Select(id=>(id,r)))
            .GroupBy(x=>x.id,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.OrderBy(x=>x.r.SourceBlockIds.Count).First().r,StringComparer.Ordinal);
        var identityBySource=document.SourceIdentities.GroupBy(x=>x.SourceId,StringComparer.Ordinal)
            .ToDictionary(g=>g.Key,g=>g.First(),StringComparer.Ordinal);

        foreach(var unit in document.TranslationUnits.OrderBy(x=>x.ReadingOrder))
        {
            if(!document.TranslationAllocations.TryGetValue(unit.Id,out var allocations)||allocations.Length==0)
            {
                diagnostics.Add(new("",unit.Id,unit.StableSourceIds,[],VisualClusterRenderOutcome.TRANSLATION_ALLOCATION_FAILED,
                    "No validated allocation segments",RectangleF.Empty,RectangleF.Empty));continue;
            }
            var lines=new List<VisualClusterSourceLine>();
            foreach(var sourceId in unit.StableSourceIds.Distinct(StringComparer.Ordinal))
            {
                if(identityBySource.TryGetValue(sourceId,out var identity))
                    lines.Add(new(sourceId,identity.OriginalText,identity.OriginalPolygon,identity.OriginalInkBounds,
                        identity.SemanticRole,identity.ReadingOrder,identity.OriginalBlockId));
                else if(regionBySource.TryGetValue(sourceId,out var region))
                {
                    var index=region.SourceBlockIds.FindIndex(x=>x==sourceId);
                    var polygon=index>=0&&index<region.SourceLinePolygons.Count?region.SourceLinePolygons[index]:GeometryV2.RectanglePolygon(region.BoundingBox);
                    var segment=region.SourceSegments.FirstOrDefault(x=>x.BlockId==sourceId);
                    lines.Add(new(sourceId,segment?.Text??region.OcrText,polygon,GeometryV2.Bounds(polygon),
                        segment?.Role??region.RoleType,segment?.ReadingOrder??region.ReadingOrder,region.RegionId));
                }
            }
            if(lines.Count!=unit.StableSourceIds.Distinct(StringComparer.Ordinal).Count())
            { diagnostics.Add(new("",unit.Id,unit.StableSourceIds,allocations.Select(x=>x.SegmentId).ToArray(),VisualClusterRenderOutcome.TRANSLATION_ALLOCATION_FAILED,
                "Stable SourceId has no source-line geometry",RectangleF.Empty,RectangleF.Empty));continue; }

            var unitClusters=Cluster(unit.Id,lines,imageSize);
            clusters.AddRange(unitClusters);
            var clusterBySource=unitClusters.SelectMany(c=>c.SourceIds.Select(id=>(id,c)))
                .ToDictionary(x=>x.id,x=>x.c,StringComparer.Ordinal);
            foreach(var allocation in allocations.OrderBy(x=>x.Sequence))
            {
                var owners=allocation.SourceIds.Where(clusterBySource.ContainsKey).Select(x=>clusterBySource[x]).DistinctBy(x=>x.VisualClusterId).ToArray();
                if(owners.Length==0)
                { diagnostics.Add(new("",unit.Id,allocation.SourceIds,[allocation.SegmentId],VisualClusterRenderOutcome.TRANSLATION_ALLOCATION_FAILED,
                    "Allocation SourceIds do not resolve to a visual cluster",RectangleF.Empty,RectangleF.Empty));continue; }
                if(owners.Length>1&&!CanShareLocalSurface(owners))
                {
                    var glyph=Union(owners.SelectMany(x=>x.SourceGlyphBounds));
                    diagnostics.Add(new("",unit.Id,allocation.SourceIds,[allocation.SegmentId],VisualClusterRenderOutcome.TRANSLATION_ALLOCATION_SPANS_MULTIPLE_CLUSTERS,
                        "Translated segment spans spatially separated visual clusters",glyph,glyph));continue;
                }
                var selected=owners.Length==1?owners[0]:Combine(unit.Id,owners,[allocation.SegmentId]);
                var key=selected.VisualClusterId;
                var existing=output.FirstOrDefault(x=>x.RegionId==key);
                if(existing is not null)
                { existing.TranslationText+=allocation.TranslatedText;continue; }
                var template=selected.SourceIds.Select(id=>regionBySource.GetValueOrDefault(id)).FirstOrDefault(x=>x is not null);
                if(template is null)continue;
                var local=template.Clone();
                local=new RecognitionRegion{
                    RegionId=key,Polygon=GeometryV2.RectanglePolygon(selected.OwnedSurfaceBounds),
                    SourceBlockIds=selected.SourceIds.ToList(),SourceLinePolygons=selected.SourceLines.Select(x=>x.Polygon.ToArray()).ToList(),
                    SourceSegments=selected.SourceLines.Select(x=>new SourceSegmentGeometry(x.SourceId,x.Text,x.Polygon.ToArray(),x.Role,1,x.ReadingOrder,x.OriginRegionId)).ToList(),
                    LayoutExclusionPolygons=template.LayoutExclusionPolygons.Select(x=>x.ToArray()).ToList(),OcrText=string.Join("\n",selected.SourceLines.Select(x=>x.Text)),
                    CorrectedText=string.Join("\n",selected.SourceLines.Select(x=>x.Text)),StructuredText=string.Join("\n",selected.SourceLines.Select(x=>x.Text)),
                    RoleType=selected.Role,RoleConfidence=template.RoleConfidence,ReadingOrder=selected.ReadingOrder,GroupId=unit.Id,TranslationUnitId=unit.Id,
                    TranslationText=allocation.TranslatedText,SourceImageRegion=selected.OwnedSurfaceBounds,RendererTargetRegion=selected.OwnedSurfaceBounds,
                    RecognitionConfidence=template.RecognitionConfidence,RegionGeneration=template.RegionGeneration,DetectionSources=[..template.DetectionSources],
                    ParentRegionId=template.ParentRegionId,CoverageValid=true
                };
                output.Add(local);
                diagnostics.Add(new(key,unit.Id,selected.SourceIds,[allocation.SegmentId],VisualClusterRenderOutcome.DRAW_SUCCESS_CLEAN,
                    "Admitted to cluster-local renderer",Union(selected.SourceGlyphBounds),selected.OwnedSurfaceBounds));
            }
        }
        return new(output,clusters,diagnostics,true);
    }

    private static IReadOnlyList<VisualClusterV2> Cluster(string unitId,List<VisualClusterSourceLine> lines,Size imageSize)
    {
        var ordered=lines.OrderBy(x=>x.ReadingOrder).ThenBy(x=>x.GlyphBounds.Top).ThenBy(x=>x.GlyphBounds.Left).ToArray();
        var groups=new List<List<VisualClusterSourceLine>>();
        foreach(var line in ordered)
        {
            var group=groups.LastOrDefault();
            if(group is null||!ShouldJoin(group[^1],line))groups.Add([line]);else group.Add(line);
        }
        return groups.Select(g=>Create(unitId,g,imageSize,[])).ToArray();
    }
    private static bool ShouldJoin(VisualClusterSourceLine a,VisualClusterSourceLine b)
    {
        if(RoleFamily(a.Role)!=RoleFamily(b.Role))return false;
        if(a.Role is RegionRoleType.Button or RegionRoleType.Choice or RegionRoleType.Metadata or RegionRoleType.Title or RegionRoleType.Header or RegionRoleType.CharacterName)return false;
        var h=Math.Max(1,(a.GlyphBounds.Height+b.GlyphBounds.Height)/2);var vertical=b.GlyphBounds.Top-a.GlyphBounds.Bottom;
        var horizontalOverlap=Math.Max(0,Math.Min(a.GlyphBounds.Right,b.GlyphBounds.Right)-Math.Max(a.GlyphBounds.Left,b.GlyphBounds.Left));
        var overlapRatio=horizontalOverlap/Math.Max(1,Math.Min(a.GlyphBounds.Width,b.GlyphBounds.Width));
        var aligned=Math.Abs(a.GlyphBounds.Left-b.GlyphBounds.Left)<=Math.Max(12,h*1.5f);
        var widthRatio=Math.Min(a.GlyphBounds.Width,b.GlyphBounds.Width)/Math.Max(1,Math.Max(a.GlyphBounds.Width,b.GlyphBounds.Width));
        var centersClose=Math.Abs((a.GlyphBounds.Left+a.GlyphBounds.Width/2)-(b.GlyphBounds.Left+b.GlyphBounds.Width/2))<=h*1.5f;
        return vertical>=-h*.35f&&vertical<=h*1.45f&&(aligned||(overlapRatio>=.55f&&widthRatio>=.65f&&centersClose));
    }
    private static string RoleFamily(RegionRoleType role)=>role switch
    {RegionRoleType.BodyParagraph or RegionRoleType.Dialogue or RegionRoleType.Narration or RegionRoleType.Caption=>"TEXT",
     RegionRoleType.Title or RegionRoleType.Header=>"TITLE",RegionRoleType.Button or RegionRoleType.Choice=>"CONTROL",_=>role.ToString()};
    private static VisualClusterV2 Create(string unitId,IReadOnlyList<VisualClusterSourceLine> lines,Size imageSize,IReadOnlyList<string> segments)
    {
        var ids=lines.Select(x=>x.SourceId).Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        var glyphs=lines.Select(x=>x.GlyphBounds).ToArray();var owned=RectangleF.Intersect(new(PointF.Empty,imageSize),Union(glyphs));
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(unitId+"|"+string.Join("|",ids))))[..12];
        return new($"VCL-{hash}",unitId,ids,lines,glyphs,owned,segments,lines.GroupBy(x=>x.Role).OrderByDescending(x=>x.Count()).First().Key,lines.Min(x=>x.ReadingOrder));
    }
    private static bool CanShareLocalSurface(IReadOnlyList<VisualClusterV2> clusters)
    {
        var union=Union(clusters.Select(x=>x.OwnedSurfaceBounds));var area=clusters.Sum(x=>x.OwnedSurfaceBounds.Width*x.OwnedSurfaceBounds.Height);
        var median=clusters.SelectMany(x=>x.SourceGlyphBounds).Select(x=>x.Height).OrderBy(x=>x).ElementAt(clusters.SelectMany(x=>x.SourceGlyphBounds).Count()/2);
        return union.Width*union.Height<=area*2.2f&&clusters.OrderBy(x=>x.OwnedSurfaceBounds.Top).Zip(clusters.OrderBy(x=>x.OwnedSurfaceBounds.Top).Skip(1),(a,b)=>b.OwnedSurfaceBounds.Top-a.OwnedSurfaceBounds.Bottom).All(x=>x<=median*1.5f);
    }
    private static VisualClusterV2 Combine(string unitId,IReadOnlyList<VisualClusterV2> clusters,IReadOnlyList<string> segments)=>
        Create(unitId,clusters.SelectMany(x=>x.SourceLines).ToArray(),new Size((int)Math.Ceiling(clusters.Max(x=>x.OwnedSurfaceBounds.Right)),(int)Math.Ceiling(clusters.Max(x=>x.OwnedSurfaceBounds.Bottom))),segments);
    private static RectangleF Union(IEnumerable<RectangleF> values){var a=values.Where(x=>!x.IsEmpty).ToArray();return a.Length==0?RectangleF.Empty:a.Skip(1).Aggregate(a[0],RectangleF.Union);}
}
