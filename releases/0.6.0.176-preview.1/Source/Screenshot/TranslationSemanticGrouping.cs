namespace ScreenshotTranslationUiTester;

public static class TranslationSemanticGrouping
{
    public static IReadOnlyList<TranslationSemanticGroup> Build(
        IReadOnlyList<SegmentGroup> source, TranslationTextSource textSource,
        Func<SegmentGroup, TranslationTextSource, string> selectText)
    {
        var ordered = source.OrderBy(x => x.ReadingOrder).ToList();
        var typicalGapRatios = EstimateTypicalGapRatios(ordered);
        var output = new List<TranslationSemanticGroup>();
        var index = 0;
        while (index < ordered.Count)
        {
            var members = new List<SegmentGroup> { ordered[index++] };
            while (index < ordered.Count && CanMerge(members[^1], ordered[index],
                       selectText(members[^1], textSource), selectText(ordered[index], textSource),
                       typicalGapRatios.GetValueOrDefault(ordered[index].GroupType, .75f)))
                members.Add(ordered[index++]);

            var first = members[0];
            var id = members.Count == 1 ? first.GroupId : $"TSG{output.Count + 1:000}";
            var text = string.Join(" ", members.Select(x => NormalizeSemanticText(
                    selectText(x, textSource), first.GroupType))
                .Where(x => x.Length > 0));
            var bounds = members.Select(x => x.Bounds).Aggregate(RectangleF.Union);
            output.Add(new TranslationSemanticGroup(id, text,
                members.Select(x => x.GroupId).ToArray(), first.GroupType, bounds, first.ReadingOrder));
        }
        return output;
    }

    private static bool CanMerge(SegmentGroup a, SegmentGroup b, string aText, string bText,
        float typicalGapRatio)
    {
        if (a.GroupType != b.GroupType ||
            a.GroupType is not (SegmentType.Body or SegmentType.Dialogue or SegmentType.ImageCaption))
            return false;
        if (IsProtected(aText) || IsProtected(bText) || !HasCompatibleGeometry(a, b)) return false;

        var heightRatio = Math.Max(a.Bounds.Height, b.Bounds.Height) /
                          Math.Max(1f, Math.Min(a.Bounds.Height, b.Bounds.Height));
        var gap = b.Bounds.Top - a.Bounds.Bottom;
        var averageHeight = Math.Max(1f, (a.Bounds.Height + b.Bounds.Height) / 2f);
        var gapRatio = Math.Max(0, gap) / averageHeight;
        if (heightRatio > 1.55f || gap < -averageHeight * .15f || gapRatio > 2.5f) return false;

        // Paragraph boundaries are spacing discontinuities relative to nearby lines, not fixed pixels.
        var boundaryThreshold = Math.Max(typicalGapRatio * 1.8f, typicalGapRatio + .65f);
        if (gapRatio > boundaryThreshold) return false;

        // Width and punctuation are only supporting signals. Narrow cards have uneven line widths,
        // and one visual paragraph can contain several complete sentences.
        return true;
    }

    private static Dictionary<SegmentType, float> EstimateTypicalGapRatios(IReadOnlyList<SegmentGroup> ordered)
    {
        var ratios = new Dictionary<SegmentType, List<float>>();
        for (var i = 1; i < ordered.Count; i++)
        {
            var previous = ordered[i - 1];
            var current = ordered[i];
            if (previous.GroupType != current.GroupType ||
                previous.GroupType is not (SegmentType.Body or SegmentType.Dialogue or SegmentType.ImageCaption) ||
                !HasCompatibleGeometry(previous, current)) continue;
            var averageHeight = Math.Max(1f, (previous.Bounds.Height + current.Bounds.Height) / 2f);
            var ratio = Math.Max(0, current.Bounds.Top - previous.Bounds.Bottom) / averageHeight;
            if (!ratios.TryGetValue(current.GroupType, out var values))
                ratios[current.GroupType] = values = [];
            values.Add(ratio);
        }
        return ratios.ToDictionary(x => x.Key, x => LowerHalfMedian(x.Value));
    }

    private static float LowerHalfMedian(List<float> values)
    {
        if (values.Count == 0) return .75f;
        var lower = values.OrderBy(x => x).Take(Math.Max(1, (values.Count + 1) / 2)).ToArray();
        var middle = lower.Length / 2;
        return lower.Length % 2 == 1 ? lower[middle] : (lower[middle - 1] + lower[middle]) / 2f;
    }

    private static bool HasCompatibleGeometry(SegmentGroup a, SegmentGroup b)
    {
        var minWidth = Math.Max(1f, Math.Min(a.Bounds.Width, b.Bounds.Width));
        var overlap = Math.Max(0, Math.Min(a.Bounds.Right, b.Bounds.Right) - Math.Max(a.Bounds.Left, b.Bounds.Left));
        var lineHeight = Math.Max(a.Bounds.Height, b.Bounds.Height);
        var leftAligned = Math.Abs(a.Bounds.Left - b.Bounds.Left) <= lineHeight * 1.25f;
        var centerAligned = Math.Abs((a.Bounds.Left + a.Bounds.Right) / 2f -
                                     (b.Bounds.Left + b.Bounds.Right) / 2f) <= lineHeight * 1.5f;
        return overlap / minWidth >= .5f || leftAligned || centerAligned;
    }

    private static string NormalizeSemanticText(string value, SegmentType type)
    {
        var trimmed = value.Trim();
        if (type is not (SegmentType.Body or SegmentType.Dialogue or SegmentType.ImageCaption))
            return trimmed;
        return string.Join(" ", trimmed.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries));
    }

    private static bool IsProtected(string text)
    {
        var value = text.Trim();
        if (value.StartsWith('@') || value.Length == 0) return true;
        if (value.All(c => char.IsDigit(c) || char.IsWhiteSpace(c) || "%$€£¥₩,:.-".Contains(c))) return true;
        return System.Text.RegularExpressions.Regex.IsMatch(value,
            @"^\p{Sc}?\s*\d[\d.,]*\s*(万|萬|亿|億|K|M|B|条消息|條消息|views?|likes?)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
}
