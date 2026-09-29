using System.Diagnostics;

namespace ScreenshotTranslationUiTester;

public static class RecognitionCoverageFinalizer
{
    public static (List<SourceLineCoverage> Coverage, long ElapsedMs) Finalize(
        List<RecognitionRegion> regions, IReadOnlyList<OcrEngineBlock> sourceLines, Size imageSize, long generation)
    {
        var watch = Stopwatch.StartNew();
        var unique = sourceLines.GroupBy(x => x.Id, StringComparer.Ordinal).Select(x => x.First()).ToList();
        var owners = regions.SelectMany(r => r.DetectedBlocks.Select(b => (b.Id, Region: r)))
            .GroupBy(x => x.Id, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.First().Region, StringComparer.Ordinal);

        foreach (var line in unique.Where(x => !owners.ContainsKey(x.Id)))
        {
            var compatible = FindCompatibleOwner(line, regions);
            if (compatible is not null)
            {
                compatible.DetectedBlocks.Add(line);
                compatible.SourceBlockIds.Add(line.Id);
                var compatiblePolygon = ValidPolygon(line.Polygon, imageSize) ? line.Polygon.ToArray() : RectPolygon(line.BoundingBox, imageSize);
                compatible.SourceLinePolygons.Add(compatiblePolygon);
                compatible.SourceSegments.Add(new(line.Id, Text(line), compatiblePolygon.ToArray(), compatible.RoleType,
                    compatible.RoleConfidence, line.ReadingOrder, compatible.RegionId));
                owners[line.Id] = compatible;
                continue;
            }
            var polygon = ValidPolygon(line.Polygon, imageSize) ? line.Polygon.ToArray() : RectPolygon(line.BoundingBox, imageSize);
            var noise = !line.Enabled || string.IsNullOrWhiteSpace(Text(line)) || IsNoise(line);
            var residual = new RecognitionRegion
            {
                Polygon = polygon, SourceBlockIds = [line.Id], DetectedBlocks = [line], SourceLinePolygons = [polygon],
                SourceSegments = [new(line.Id, Text(line), polygon.ToArray(), RegionRoleType.Unknown, 0, line.ReadingOrder, line.Id)],
                OcrText = line.RawText, CorrectedText = Text(line), StructuredText = Text(line),
                RoleType = RegionRoleType.Unknown, ReadingOrder = line.ReadingOrder, RecognitionConfidence = line.Confidence ?? 0,
                RegionGeneration = generation, DetectionSources = [DetectionSource.OcrDetector],
                SourceImageRegion = GeometryV2.Bounds(polygon), RendererTargetRegion = GeometryV2.Bounds(polygon),
                IsIgnored = noise, CoverageValid = polygon.Length >= 3,
                CoverageFailureReason = polygon.Length >= 3 ? "" : "Residual source line has invalid geometry"
            };
            regions.Add(residual); owners[line.Id] = residual;
        }

        foreach (var region in regions)
        {
            var ordered = region.DetectedBlocks.GroupBy(x => x.Id, StringComparer.Ordinal).Select(x => x.First())
                .OrderBy(x => x.ReadingOrder).ThenBy(x => x.BoundingBox.Top).ThenBy(x => x.BoundingBox.Left).ToList();
            region.SourceBlockIds = ordered.Select(x => x.Id).ToList();
            region.SourceLinePolygons = ordered.Select(x => ValidPolygon(x.Polygon, imageSize)
                ? x.Polygon.ToArray() : RectPolygon(x.BoundingBox, imageSize)).ToList();
            var segmentById = region.SourceSegments.GroupBy(x => x.BlockId, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
            region.SourceSegments = ordered.Select((x, i) => segmentById.TryGetValue(x.Id, out var existing)
                ? existing with { Polygon = region.SourceLinePolygons[i].ToArray() }
                : new SourceSegmentGeometry(x.Id, Text(x), region.SourceLinePolygons[i].ToArray(), region.RoleType,
                    region.RoleConfidence, x.ReadingOrder, region.RegionId)).ToList();
            region.CoverageValid = region.SourceBlockIds.Count == region.SourceLinePolygons.Count &&
                region.SourceLinePolygons.All(x => ValidPolygon(x, imageSize));
            region.CoverageFailureReason = region.CoverageValid ? "" : "SourceLineIds and valid LinePolygons are not one-to-one";
        }

        var coverage = new List<SourceLineCoverage>();
        foreach (var line in unique)
        {
            if (!owners.TryGetValue(line.Id, out var region))
            {
                coverage.Add(new(line.Id, Text(line), "", "", SourceLineDisposition.Unaccounted, "No final owner", [], false, []));
                continue;
            }
            var index = region.SourceBlockIds.FindIndex(x => string.Equals(x, line.Id, StringComparison.Ordinal));
            var polygon = index >= 0 && index < region.SourceLinePolygons.Count ? region.SourceLinePolygons[index] : [];
            var rectFallback = line.Polygon.Length < 3 && polygon.Length >= 3;
            var disposition = region.IsIgnored ? SourceLineDisposition.IgnoredWithReason :
                region.PreserveOriginal ? SourceLineDisposition.PreserveOriginal : SourceLineDisposition.AssignedToTranslationUnit;
            var reason = disposition switch
            {
                SourceLineDisposition.IgnoredWithReason => IsNoise(line) ? "LowConfidenceNoise/DetectorArtifact" : "Explicitly ignored region",
                SourceLineDisposition.PreserveOriginal => "Explicit preserve policy",
                _ => region.CoverageValid ? "Owned by semantic region" : region.CoverageFailureReason
            };
            coverage.Add(new(line.Id, Text(line), region.RegionId, "", disposition, reason, polygon.ToArray(), rectFallback, []));
        }
        watch.Stop(); return (coverage, watch.ElapsedMilliseconds);
    }

    public static RecognitionCoverageSummary BindTranslationUnits(RecognitionDocumentV2 document)
    {
        document.SourceIdentities.Clear();
        for (var i = 0; i < document.SourceLineCoverage.Count; i++)
        {
            var item = document.SourceLineCoverage[i];
            var unit = document.TranslationUnits.FirstOrDefault(x => x.RegionIds.Contains(item.RegionId, StringComparer.Ordinal));
            document.SourceLineCoverage[i] = item with { TranslationUnitId = unit?.Id ?? "" };
            var region=document.Regions.FirstOrDefault(x=>x.RegionId==item.RegionId);
            var lineBounds=GeometryV2.Bounds(item.Polygon);
            document.SourceIdentities.Add(new SourceIdentity(item.SourceLineId,item.SourceText,item.Polygon.ToArray(),
                lineBounds,lineBounds,item.SourceLineId,region?.RoleType??RegionRoleType.Unknown,
                region?.ReadingOrder??i,"PAGE-001"));
        }
        var required = document.SourceLineCoverage.Where(x => x.Disposition == SourceLineDisposition.AssignedToTranslationUnit).ToArray();
        var valid = required.Count(x => ValidPolygon(x.Polygon, Size.Empty));
        return new(document.SourceLineCoverage.Count,
            document.SourceLineCoverage.Count(x => x.Disposition == SourceLineDisposition.AssignedToTranslationUnit),
            document.SourceLineCoverage.Count(x => x.Disposition == SourceLineDisposition.PreserveOriginal),
            document.SourceLineCoverage.Count(x => x.Disposition == SourceLineDisposition.IgnoredWithReason),
            document.SourceLineCoverage.Count(x => x.Disposition == SourceLineDisposition.Unaccounted),
            document.TranslationUnits.Count, required.Length, valid, required.Length - valid);
    }

    public static bool ValidPolygon(IReadOnlyList<PointF> polygon, Size imageSize)
    {
        if (polygon.Count < 3 || polygon.Any(p => float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsInfinity(p.X) || float.IsInfinity(p.Y))) return false;
        var bounds = GeometryV2.Bounds(polygon);
        if (bounds.Width <= .5f || bounds.Height <= .5f) return false;
        return imageSize.IsEmpty || RectangleF.Intersect(bounds, new(PointF.Empty, imageSize)) is { Width: > .5f, Height: > .5f };
    }
    private static PointF[] RectPolygon(RectangleF value, Size imageSize)
    {
        var clipped = RectangleF.Intersect(value, new(PointF.Empty, imageSize));
        return clipped.Width > .5f && clipped.Height > .5f ? GeometryV2.RectanglePolygon(clipped) : [];
    }
    private static string Text(OcrEngineBlock line) => string.IsNullOrWhiteSpace(line.CorrectedText) ? line.RawText : line.CorrectedText;
    private static bool IsNoise(OcrEngineBlock line) => Text(line).Trim() is "." or "," or "'" or "`" || line.SuspectedFalsePositive;
    private static RecognitionRegion? FindCompatibleOwner(OcrEngineBlock line, IReadOnlyList<RecognitionRegion> regions)
    {
        var box = line.BoundingBox; if (box.Width <= 0 || box.Height <= 0) return null;
        return regions.Where(r => !r.IsIgnored && r.RoleType is RegionRoleType.BodyParagraph or RegionRoleType.Dialogue or RegionRoleType.Narration or RegionRoleType.Unknown)
            .Select(r =>
            {
                var rb = r.BoundingBox; var intersection = RectangleF.Intersect(rb, box);
                var overlapArea = intersection.Width > 0 && intersection.Height > 0 ? intersection.Width * intersection.Height / Math.Max(1, box.Width * box.Height) : 0;
                var horizontal = Math.Max(0, Math.Min(rb.Right, box.Right) - Math.Max(rb.Left, box.Left)) / Math.Max(1, Math.Min(rb.Width, box.Width));
                var gap = Math.Max(0, Math.Max(rb.Top - box.Bottom, box.Top - rb.Bottom));
                var lineHeights = r.DetectedBlocks.Select(x => x.BoundingBox.Height).Where(x => x > 0).OrderBy(x => x).ToArray();
                var scale = lineHeights.Length == 0 ? box.Height : lineHeights[(lineHeights.Length - 1) / 2];
                var sameScale = box.Height >= scale * .25f && box.Height <= scale * 1.8f;
                var supported = overlapArea >= .45f || horizontal >= .65f && gap <= Math.Max(box.Height, scale) * 1.25f && sameScale;
                return (Region: r, Supported: supported, Score: overlapArea * 4 + horizontal * 2 - gap / Math.Max(1, scale));
            }).Where(x => x.Supported).OrderByDescending(x => x.Score).Select(x => x.Region).FirstOrDefault();
    }
}

public static class RecognitionCoverageValidator
{
    public static IReadOnlyList<string> Validate(RecognitionDocumentV2 document)
    {
        var errors = new List<string>(); var summary = document.CoverageSummary;
        if (summary is null) return ["Coverage summary is missing"];
        if (summary.TotalSourceLines != summary.Assigned + summary.Preserved + summary.Ignored + summary.Unaccounted)
            errors.Add("SourceLine disposition totals do not balance");
        if (summary.Unaccounted != 0) errors.Add($"Unaccounted SourceLines={summary.Unaccounted}");
        if (summary.MissingEraseGeometry != 0) errors.Add($"Missing required geometry={summary.MissingEraseGeometry}");
        foreach (var region in document.Regions.Where(x => !x.IsIgnored && !x.PreserveOriginal && !x.CoverageValid))
            errors.Add($"Region {region.RegionId}: {region.CoverageFailureReason}");
        return errors;
    }
}
