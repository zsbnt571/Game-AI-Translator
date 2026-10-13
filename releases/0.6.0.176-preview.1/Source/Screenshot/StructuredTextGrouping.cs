using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

public static class StructuredTextGrouping
{
    public static IReadOnlyList<StructuredTextGroup> Build(
        IReadOnlyList<SegmentGroup> source, TranslationTextSource textSource,
        Func<SegmentGroup, TranslationTextSource, string> selectText)
    {
        var semantic = TranslationSemanticGrouping.Build(source, textSource, selectText);
        var byId = source.ToDictionary(x => x.GroupId, StringComparer.Ordinal);
        return semantic.Select(group =>
        {
            var members = group.SourceBlockIds.Where(byId.ContainsKey).Select(x => byId[x]).ToArray();
            var role = StructuredTextRoleClassifier.Classify(group, members);
            return new StructuredTextGroup(group.Id, role, group.SourceBlockIds, group.Text,
                group.Bounds, group.ReadingOrder, group.Text);
        }).ToArray();
    }
}

public static class StructuredTextRoleClassifier
{
    private static readonly Regex HeaderPattern = new(
        @"(?:\b\d{1,2}:\d{2}\b|\b(?:19|20)\d{2}[-/.年]\d{1,2}|\b(?:January|February|March|April|May|June|July|August|September|October|November|December)\b|\|)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex MetadataPattern = new(
        @"^\p{Sc}?\s*\d[\d.,]*\s*(?:万|萬|亿|億|K|M|B|views?|likes?|comments?|messages?|条消息|條消息|天前|小时前|小時前|minutes?|hours?|days?)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static StructuredTextRole Classify(TranslationSemanticGroup group,
        IReadOnlyList<SegmentGroup> members)
    {
        var text = group.Text.Trim();
        var primary = members.FirstOrDefault()?.GroupType ?? group.GroupType;
        if (primary == SegmentType.Title) return StructuredTextRole.Title;
        if (primary is SegmentType.UserName or SegmentType.CharacterInfo || text.StartsWith('@'))
            return StructuredTextRole.CharacterName;
        if (primary == SegmentType.Time || HeaderPattern.IsMatch(text)) return StructuredTextRole.Header;
        if (primary is SegmentType.MessageCount or SegmentType.NumericInfo || MetadataPattern.IsMatch(text))
            return StructuredTextRole.Metadata;
        if (primary == SegmentType.Button) return StructuredTextRole.Button;
        if (primary is SegmentType.Label or SegmentType.Tag or SegmentType.ImageCaption)
            return StructuredTextRole.UILabel;
        if (primary == SegmentType.Dialogue || LooksLikeDialogue(text)) return StructuredTextRole.Dialogue;
        if (primary == SegmentType.Body) return StructuredTextRole.BodyParagraph;
        return StructuredTextRole.Unknown;
    }

    private static bool LooksLikeDialogue(string text) => text.StartsWith('"') || text.StartsWith('“') ||
        text.StartsWith('「') || text.StartsWith('『');
}
