using System.Security.Cryptography;
using System.Text;

namespace ScreenshotTranslationUiTester;

public static class DocumentState
{
    public static IReadOnlyList<TranslationItem> CreateTranslationItems(OcrDocument document,
        TranslationTextSource source = TranslationTextSource.Organized) =>
        CreateStructuredTextGroups(document, source)
            .Select(x => new TranslationItem(x.GroupId, x.TranslationInputText, x.RoleType, x.SourceBlockIds)).ToArray();

    public static IReadOnlyList<StructuredTextGroup> CreateStructuredTextGroups(
        OcrDocument document, TranslationTextSource source = TranslationTextSource.Organized) =>
        StructuredTextGrouping.Build(document.Groups, source, SelectSource);

    public static IReadOnlyList<TranslationSemanticGroup> CreateTranslationSemanticGroups(
        OcrDocument document, TranslationTextSource source = TranslationTextSource.Organized) =>
        TranslationSemanticGrouping.Build(document.Groups, source, SelectSource);

    private static string SelectSource(SegmentGroup group, TranslationTextSource source) => source switch
    {
        TranslationTextSource.Raw => group.OriginalText,
        TranslationTextSource.Organized => string.IsNullOrWhiteSpace(group.OrganizedText) ? group.OriginalText : group.OrganizedText,
        _ => string.IsNullOrWhiteSpace(group.OrganizedText) ? group.OriginalText : group.OrganizedText
    };

    public static void ClearTranslations(OcrDocument document)
    {
        foreach (var group in document.Groups)
        {
            group.Translation = "";
            group.HasReliableTranslation = false;
        }
        document.FullTranslation = "";
        document.SegmentMappingReliable = false;
    }

    public static void ApplyTranslations(OcrDocument document, TranslationBatchResult result)
        => ApplyTranslations(document, result, CreateTranslationSemanticGroups(document));

    public static void ApplyTranslations(OcrDocument document, TranslationBatchResult result,
        IReadOnlyList<TranslationSemanticGroup> semanticGroups)
    {
        var sourceToTranslation = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var semantic in semanticGroups)
        {
            var translation = result.Translations.GetValueOrDefault(semantic.Id, "");
            if (semantic.SourceBlockIds.Count > 0)
                sourceToTranslation[semantic.SourceBlockIds[0]] = translation;
            foreach (var sourceId in semantic.SourceBlockIds.Skip(1)) sourceToTranslation[sourceId] = "";
        }
        foreach (var group in document.Groups)
        {
            group.Translation = sourceToTranslation.GetValueOrDefault(group.GroupId, "");
            group.HasReliableTranslation = result.SegmentMappingReliable &&
                                           !string.IsNullOrWhiteSpace(group.Translation);
        }
        document.FullTranslation = result.FullTranslation;
        document.SegmentMappingReliable = result.SegmentMappingReliable;
        document.TranslationStale = false;
    }

    public static IReadOnlyList<string> ResolveHighlightSourceBlockIds(string? selectedId,
        TextViewMode mode, IReadOnlyList<TranslationSemanticGroup> semanticGroups)
    {
        if (string.IsNullOrWhiteSpace(selectedId)) return [];
        var semantic = semanticGroups.FirstOrDefault(x => x.Id == selectedId ||
            x.SourceBlockIds.Contains(selectedId, StringComparer.Ordinal));
        if (semantic is null) return [selectedId];
        if (mode == TextViewMode.Translation || semantic.Id == selectedId)
            return semantic.SourceBlockIds.Distinct(StringComparer.Ordinal).ToArray();
        return [selectedId];
    }

    public static IReadOnlyList<string> ResolveStructuredHighlightSourceBlockIds(string? selectedId,
        TextViewMode mode, IReadOnlyList<StructuredTextGroup> groups)
    {
        if (string.IsNullOrWhiteSpace(selectedId)) return [];
        var group = groups.FirstOrDefault(x => x.GroupId == selectedId ||
            x.SourceBlockIds.Contains(selectedId, StringComparer.Ordinal));
        if (group is null) return [selectedId];
        if (mode == TextViewMode.Translation || group.GroupId == selectedId)
            return group.SourceBlockIds.Distinct(StringComparer.Ordinal).ToArray();
        return [selectedId];
    }

    public static string TranslationForSemanticGroup(OcrDocument document, TranslationSemanticGroup semantic)
    {
        // A semantic group owns every source ID. Translation storage remains compatible with
        // the frozen service contract, but consumers must never guess through FirstSourceId.
        return semantic.SourceBlockIds.Select(id => document.Groups.FirstOrDefault(x => x.GroupId == id)?.Translation)
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
    }

    public static string ComputeOrganizedHash(OcrDocument document)
    {
        var value = string.Join("\n", document.Groups.OrderBy(x => x.ReadingOrder)
            .Select(x => $"{x.GroupId}|{x.OrganizedText}|{x.Bounds.X},{x.Bounds.Y},{x.Bounds.Width},{x.Bounds.Height}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
