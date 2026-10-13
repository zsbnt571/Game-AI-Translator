using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

public static class SemanticRegionGrouperV2
{
    private static readonly Regex Standalone = new(
        @"^(?:@\S+|(?:19|20)\d{2}.*|\d{1,2}:\d{2}.*|P(?:A|R)GE\s*\d+|(?:about\s*)?\d+\s*(?:minutes?|hours?|days?)\s*ago|(?:\u5927\u7ea6\s*)?\d+\s*(?:\u5206\u949f|\u5c0f\u65f6|\u5929)\u524d|\d+\s*\u6761(?:\u6d88\u606f|\u8bc4\u8bba)|continue|next|ok|cancel|\u7ee7\u7eed|\u786e\u5b9a|\u53d6\u6d88)$",
        RegexOptions.IgnoreCase);

    public static List<RecognitionRegion> Group(IReadOnlyList<RecognitionRegion> proposals, long generation)
    {
        var textLines = proposals.Where(x => x.SourceBlockIds.Count > 0 && !string.IsNullOrWhiteSpace(Text(x)))
            .OrderBy(x => x.BoundingBox.Top).ThenBy(x => x.BoundingBox.Left).ToList();
        var visualOnly = proposals.Where(x => x.SourceBlockIds.Count == 0).ToList();
        if (textLines.Count < 2) return proposals.ToList();

        var heights = textLines.Select(x => x.BoundingBox.Height).Where(x => x > 0).OrderBy(x => x).ToArray();
        var medianHeight = heights[(heights.Length - 1) / 2];
        var groups = new List<List<RecognitionRegion>>();
        foreach (var line in textLines)
        {
            var target = groups.Where(x => ShouldJoin(x, line, medianHeight))
                .OrderBy(x => Math.Max(0, line.BoundingBox.Top - x[^1].BoundingBox.Bottom))
                .ThenBy(x => Math.Abs(line.BoundingBox.Left - x[^1].BoundingBox.Left)).FirstOrDefault();
            if (target is null) groups.Add([line]);
            else target.Add(line);
        }

        var semantic = groups.Select((members, index) => Merge(members, generation, index + 1)).ToList();
        semantic.AddRange(visualOnly.Where(v => !semantic.Any(s => GeometryV2.IntersectionOverUnion(s.BoundingBox, v.BoundingBox) >= .65f)));
        return semantic.OrderBy(x => x.BoundingBox.Top).ThenBy(x => x.BoundingBox.Left).ToList();
    }

    private static bool ShouldJoin(IReadOnlyList<RecognitionRegion> current, RecognitionRegion next, float medianHeight)
    {
        var previous = current[^1]; var a = previous.BoundingBox; var b = next.BoundingBox;
        if (!SafeLayoutTargetPlanner.AreRolesCompatible(previous.RoleType, next.RoleType)) return false;
        var gap = b.Top - a.Bottom;
        var decorativePair = IsAllCapsLabel(Text(previous)) && IsAllCapsLabel(Text(next)) &&
            Math.Abs(a.Left-b.Left) <= Math.Max(a.Width,b.Width)*.35f;
        var maximumGap = decorativePair ? Math.Max(medianHeight * 2.8f, Math.Max(a.Height,b.Height)*1.4f) : medianHeight * .95f;
        if (gap < -Math.Min(a.Height, b.Height) * .35f || gap > maximumGap) return false;
        // A short, visibly larger heading followed by a smaller caption is a
        // typographic hierarchy, not a wrapped paragraph.  This boundary is
        // available from RapidOCR alone and keeps the product path independent
        // of the optional PP-S layout model.
        if (!decorativePair && current.Count == 1 && Text(previous).Trim().Length <= 48 &&
            a.Height >= b.Height * 1.30f && gap >= Math.Min(a.Height, b.Height) * .65f)
            return false;
        if (!decorativePair && IsLocalParagraphGap(current, next, medianHeight)) return false;
        if (Standalone.IsMatch(Text(previous).Trim()) || Standalone.IsMatch(Text(next).Trim())) return false;
        if (IsAllCapsLabel(Text(previous)) != IsAllCapsLabel(Text(next)) &&
            (IsAllCapsLabel(Text(previous)) || IsAllCapsLabel(Text(next)))) return false;
        if (Text(previous).Trim().Length <= 28 && (Text(next).TrimStart().StartsWith('"') || Text(next).TrimStart().StartsWith('\u201c'))) return false;
        if (IsProtectedRole(previous.RoleType) || IsProtectedRole(next.RoleType)) return false;
        if (!string.Equals(previous.ParentRegionId, next.ParentRegionId, StringComparison.Ordinal) &&
            previous.ParentRegionId is not null && next.ParentRegionId is not null) return false;

        var overlap = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        var overlapRatio = overlap / Math.Max(1, Math.Min(a.Width, b.Width));
        var leftTolerance = Math.Max(medianHeight * 1.1f, Math.Min(a.Width, b.Width) * .14f);
        var aligned = Math.Abs(a.Left - b.Left) <= leftTolerance;
        if (!aligned && overlapRatio < .72f) return false;

        var currentTop = current.Min(x => x.BoundingBox.Top);
        if (b.Top - currentTop > medianHeight * 14f) return false;
        var previousText = Text(previous).TrimEnd();
        var quoteOpen = current.Sum(x => Text(x).Count(c => c is '"' or '\u201c' or '\u201d')) % 2 == 1;
        var continuation = quoteOpen || previousText.Length == 0 || !Regex.IsMatch(previousText, @"[.!?\u3002\uff01\uff1f][\""'\u201d]?$");
        var wrapShape = previousText.Length >= 12 && (aligned || overlapRatio >= .85f);
        return continuation || wrapShape;
    }

    private static bool IsLocalParagraphGap(IReadOnlyList<RecognitionRegion> current, RecognitionRegion next, float medianHeight)
    {
        // Compare the candidate gap with this paragraph's own line rhythm.
        // No fixed pixel threshold is used, so DPI and font size remain neutral.
        if (current.Count < 2) return false;
        var gaps = current.Zip(current.Skip(1), (a, b) => b.BoundingBox.Top - a.BoundingBox.Bottom)
            .Where(x => x >= 0).OrderBy(x => x).ToArray();
        if (gaps.Length == 0) return false;
        var localMedian = gaps[(gaps.Length - 1) / 2];
        var candidate = next.BoundingBox.Top - current[^1].BoundingBox.Bottom;
        var localThreshold = Math.Max(localMedian * 1.75f, medianHeight * .55f);
        if (candidate <= localThreshold) return false;

        var previousText = Text(current[^1]).TrimEnd();
        var nextText = Text(next).TrimStart();
        var previousBlockComplete = Regex.IsMatch(previousText, @"[.!?\u3002\uff01\uff1f][\""'\u201d]?$");
        var nextCanStartBlock = nextText.Length > 0 &&
            (char.IsUpper(nextText[0]) || nextText[0] is '"' or '\u201c' or '\u2018');
        return previousBlockComplete || (current.Count >= 3 && nextCanStartBlock);
    }

    private static RecognitionRegion Merge(IReadOnlyList<RecognitionRegion> members, long generation, int order)
    {
        if (members.Count == 1) { var single = members[0]; single.ReadingOrder = order; return single; }
        var bounds = members.Select(x => x.BoundingBox).Aggregate(RectangleF.Union);
        var bestHint = members.OrderByDescending(x => x.RoleConfidence).First();
        var corrected = string.Join(" ", members.Select(Text).Select(x => x.Trim()).Where(x => x.Length > 0));
        return new RecognitionRegion
        {
            Polygon = GeometryV2.RectanglePolygon(bounds),
            SourceBlockIds = members.SelectMany(x => x.SourceBlockIds).Distinct(StringComparer.Ordinal).ToList(),
            DetectedBlocks = members.SelectMany(x => x.DetectedBlocks).DistinctBy(x => x.Id).OrderBy(x => x.ReadingOrder).ToList(),
            SourceLinePolygons = members.SelectMany(x => x.SourceLinePolygons).Select(x => x.ToArray()).ToList(),
            SourceSegments = members.SelectMany(x => x.SourceSegments).Select(x => x with { Polygon = x.Polygon.ToArray() }).ToList(),
            LayoutExclusionPolygons = members.SelectMany(x => x.LayoutExclusionPolygons).Select(x => x.ToArray()).ToList(),
            OcrText = string.Join("\n", members.Select(x => x.OcrText).Where(x => !string.IsNullOrWhiteSpace(x))),
            CorrectedText = corrected, StructuredText = corrected,
            RoleType = bestHint.RoleType, RoleConfidence = bestHint.RoleConfidence,
            ReadingOrder = order, RecognitionConfidence = members.Average(x => x.RecognitionConfidence),
            RegionGeneration = generation,
            DetectionSources = members.SelectMany(x => x.DetectionSources).Distinct().ToList(),
            ParentRegionId = members.Select(x => x.ParentRegionId).FirstOrDefault(x => x is not null),
            SourceImageRegion = bounds, RendererTargetRegion = bounds
        };
    }

    private static string Text(RecognitionRegion value) => string.IsNullOrWhiteSpace(value.StructuredText) ? value.CorrectedText : value.StructuredText;
    private static bool IsAllCapsLabel(string value)
    {
        var letters = value.Where(char.IsLetter).ToArray();
        return letters.Length is >= 2 and <= 24 && letters.All(char.IsUpper);
    }
    private static bool IsProtectedRole(RegionRoleType role) => role is RegionRoleType.CharacterName or RegionRoleType.Species or RegionRoleType.Metadata or RegionRoleType.Button
        or RegionRoleType.UILabel or RegionRoleType.Choice or RegionRoleType.Caption or RegionRoleType.Image or RegionRoleType.Illustration;
}
