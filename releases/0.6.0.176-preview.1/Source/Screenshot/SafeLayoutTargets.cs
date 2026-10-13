namespace ScreenshotTranslationUiTester;

public enum SafeLayoutTargetStatus { Safe, UnsafeStructureTarget }

public sealed record GeometryIsland(string Id, RegionRoleType Role, IReadOnlyList<string> SourceBlockIds,
    IReadOnlyList<PointF[]> SourcePolygons, RectangleF Bounds);

public sealed record SafeLayoutTargetDecision(SafeLayoutTargetStatus Status, string Reason,
    IReadOnlyList<GeometryIsland> Islands, IReadOnlyList<RectangleF> Targets,
    IReadOnlyList<RectangleF> ExclusionZones, bool HasReliableSegmentMapping);

public sealed record ImageIntrusionMetrics(RectangleF ExclusionZone, float SourceImageOverlapArea,
    float NewTargetImageOverlapArea, float AddedIntrusionArea);

public static class SafeLayoutTargetPlanner
{
    public static bool AreRolesCompatible(RegionRoleType a, RegionRoleType b)
    {
        if (a == b) return true;
        var aUnknown = a is RegionRoleType.Unknown or RegionRoleType.Automatic;
        var bUnknown = b is RegionRoleType.Unknown or RegionRoleType.Automatic;
        if (aUnknown || bUnknown)
        {
            var known = aUnknown ? b : a;
            return known is RegionRoleType.Unknown or RegionRoleType.Automatic or RegionRoleType.BodyParagraph or RegionRoleType.Narration;
        }
        if (a is RegionRoleType.BodyParagraph or RegionRoleType.Narration && b is RegionRoleType.BodyParagraph or RegionRoleType.Narration) return true;
        return false;
    }

    public static SafeLayoutTargetDecision Plan(RecognitionRegion region)
    {
        var segments = (region.SourceSegments.Count > 0 ? region.SourceSegments : region.SourceBlockIds.Select((id, i) =>
        {
            var polygon = i < region.SourceLinePolygons.Count ? region.SourceLinePolygons[i] : region.Polygon;
            var text = i < region.DetectedBlocks.Count ? (string.IsNullOrWhiteSpace(region.DetectedBlocks[i].CorrectedText) ? region.DetectedBlocks[i].RawText : region.DetectedBlocks[i].CorrectedText) : "";
            var order = i < region.DetectedBlocks.Count ? region.DetectedBlocks[i].ReadingOrder : i;
            return new SourceSegmentGeometry(id, text, polygon, region.RoleType, region.RoleConfidence, order, region.RegionId);
        }).ToList()).Select(x => x.Role is RegionRoleType.Unknown or RegionRoleType.Automatic &&
            region.RoleType is not RegionRoleType.Unknown and not RegionRoleType.Automatic
                ? x with { Role = region.RoleType, RoleConfidence = region.RoleConfidence, OriginRegionId = region.RegionId }
                : x).ToList();
        var islands = BuildIslands(segments);
        // Exclusions are paragraph-local. A portrait or side image may constrain the
        // first paragraph without leaking its left edge into every later paragraph.
        var exclusions = ParagraphGeometryPlanner.LocalExclusions(region).ToArray();
        var reliable = islands.Count <= 1;

        if (segments.Select(x => x.Role).Where(x => x is not RegionRoleType.Unknown and not RegionRoleType.Automatic).Distinct().Count() > 1)
            return Unsafe("Incompatible source roles require separate translation mappings", islands, exclusions, false);
        if (islands.Count > 1)
            return Unsafe("Disconnected geometry islands have only one whole-unit translation", islands, exclusions, false);

        var target = islands.Count == 1 ? islands[0].Bounds : (region.RendererTargetRegion.IsEmpty ? region.BoundingBox : region.RendererTargetRegion);
        // For a single compact control, RendererTargetRegion is the owning button/tooltip
        // surface while the source island is only the OCR ink box. Use the containing surface
        // so ordinary source-sized text does not enter fallback merely because ascent/descent
        // lies outside the OCR polygon.
        var compactOwner=region.RoleType is RegionRoleType.Choice or RegionRoleType.Button or RegionRoleType.UILabel or RegionRoleType.Metadata or RegionRoleType.Header;
        if(compactOwner&&!region.RendererTargetRegion.IsEmpty&&islands.Count==1)
        {var owner=region.RendererTargetRegion;var ink=islands[0].Bounds;var contains=owner.Left<=ink.Left+1&&owner.Top<=ink.Top+1&&owner.Right>=ink.Right-1&&owner.Bottom>=ink.Bottom-1;if(contains&&owner.Width<=ink.Width*1.8f&&owner.Height<=ink.Height*2.2f)target=owner;}
        var sourceBounds = segments.Select(x => GeometryV2.Bounds(x.Polygon)).Where(x => !x.IsEmpty).ToArray();
        foreach (var exclusion in exclusions)
        {
            var metrics = AnalyzeImageIntrusion(target, sourceBounds, exclusion);
            if (metrics.AddedIntrusionArea > .5f)
                return Unsafe("Layout target adds image/illustration intrusion beyond source geometry", islands, exclusions, reliable);
        }
        return new(SafeLayoutTargetStatus.Safe, "Role-compatible connected text geometry", islands, [target], exclusions, reliable);
    }

    private static List<GeometryIsland> BuildIslands(IReadOnlyList<SourceSegmentGeometry> segments)
    {
        var result = new List<List<SourceSegmentGeometry>>();
        foreach (var segment in segments.OrderBy(x => x.ReadingOrder).ThenBy(x => GeometryV2.Bounds(x.Polygon).Top))
        {
            var box = GeometryV2.Bounds(segment.Polygon);
            var group = result.FirstOrDefault(g => g.Any(x => Connected(x, segment)));
            if (group is null) result.Add([segment]); else group.Add(segment);
        }
        return result.Select((g, i) => new GeometryIsland($"ISLAND-{i + 1:00}", DominantRole(g),
            g.Select(x => x.BlockId).Distinct(StringComparer.Ordinal).ToArray(), g.Select(x => x.Polygon.ToArray()).ToArray(),
            g.Select(x => GeometryV2.Bounds(x.Polygon)).Aggregate(RectangleF.Union))).ToList();
    }

    private static bool Connected(SourceSegmentGeometry a, SourceSegmentGeometry b)
    {
        if (!AreRolesCompatible(a.Role, b.Role)) return false;
        var x = GeometryV2.Bounds(a.Polygon); var y = GeometryV2.Bounds(b.Polygon);
        var verticalGap = Math.Max(0, Math.Max(x.Top, y.Top) - Math.Min(x.Bottom, y.Bottom));
        var horizontalGap = Math.Max(0, Math.Max(x.Left, y.Left) - Math.Min(x.Right, y.Right));
        var line = Math.Max(1, Math.Min(x.Height, y.Height));
        var horizontalOverlap = Math.Max(0, Math.Min(x.Right, y.Right) - Math.Max(x.Left, y.Left));
        var overlapRatio = horizontalOverlap / Math.Max(1, Math.Min(x.Width, y.Width));
        return verticalGap <= line * 1.25f && (horizontalGap <= line * .9f || overlapRatio >= .18f);
    }

    private static RegionRoleType DominantRole(IEnumerable<SourceSegmentGeometry> segments) => segments
        .OrderByDescending(x => x.RoleConfidence).Select(x => x.Role).FirstOrDefault();
    public static ImageIntrusionMetrics AnalyzeImageIntrusion(RectangleF target,
        IEnumerable<RectangleF> sourceBounds, RectangleF exclusion)
    {
        var targetOnImage = RectangleF.Intersect(target, exclusion);
        var sourcesOnImage = sourceBounds.Select(x => RectangleF.Intersect(x, exclusion)).Where(x => Area(x) > 0).ToArray();
        var sourceArea = UnionArea(sourcesOnImage);
        var coveredTarget = sourcesOnImage.Select(x => RectangleF.Intersect(x, targetOnImage)).Where(x => Area(x) > 0).ToArray();
        var added = Math.Max(0, Area(targetOnImage) - UnionArea(coveredTarget));
        return new(exclusion, sourceArea, Area(targetOnImage), added);
    }

    private static float UnionArea(IReadOnlyList<RectangleF> rectangles)
    {
        if (rectangles.Count == 0) return 0;
        var xs = rectangles.SelectMany(x => new[] { x.Left, x.Right }).Distinct().OrderBy(x => x).ToArray();
        float area = 0;
        for (var i = 0; i + 1 < xs.Length; i++)
        {
            var left = xs[i]; var right = xs[i + 1]; if (right <= left) continue;
            var spans = rectangles.Where(x => x.Left < right && x.Right > left).Select(x => (x.Top, x.Bottom)).OrderBy(x => x.Top).ToArray();
            if (spans.Length == 0) continue;
            var top = spans[0].Top; var bottom = spans[0].Bottom; float height = 0;
            foreach (var span in spans.Skip(1))
            {
                if (span.Top > bottom) { height += bottom - top; top = span.Top; bottom = span.Bottom; }
                else bottom = Math.Max(bottom, span.Bottom);
            }
            height += bottom - top; area += (right - left) * height;
        }
        return area;
    }
    private static SafeLayoutTargetDecision Unsafe(string reason, IReadOnlyList<GeometryIsland> islands,
        IReadOnlyList<RectangleF> exclusions, bool reliable) => new(SafeLayoutTargetStatus.UnsafeStructureTarget,
            reason, islands, [], exclusions, reliable);
    private static float Area(RectangleF value) => value.Width <= 0 || value.Height <= 0 ? 0 : value.Width * value.Height;
}
