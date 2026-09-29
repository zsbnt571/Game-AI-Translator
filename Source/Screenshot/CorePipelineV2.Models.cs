using System.Drawing;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

internal enum BlockLayoutBehavior { Fixed, Flow, Auto }
internal enum BlockTranslationState { Pending, Accepted, Preserved, Failed }
internal enum TextSelectionAction { Auto, Translate, Preserve }
internal enum CharacterNamePolicy { Auto, Translate, PreserveOriginal }

internal sealed record RawOcrLine(
    string SourceId, string RawText, string CorrectedText, PointF[] Polygon,
    RectangleF Bounds, float Confidence, int ReadingOrder)
{
    public string OcrRequestImageSha256 { get; init; } = "";
    public IReadOnlyList<OcrAlternativeEvidence> OcrAlternatives { get; init; } = [];
}

internal sealed record NormalizedOcrLine(
    string SourceId, string SourceText, PointF[] Polygon, RectangleF Bounds,
    float Confidence, int ReadingOrder, string NormalizationTrace);

internal sealed record VisualEvidenceRegion(string RegionId, RectangleF Bounds, string Role, float Confidence);

internal sealed record RegionOwnerEvidence(
    string SourceId, string RegionOwnerId, float AnchorX, int AnchorMemberCount,
    bool StrongOwner, string RoleHint, string Evidence);

internal sealed record GroupingEdgeEvidence(
    string SourceId, string TargetId, string SourceRegionOwnerId, string TargetRegionOwnerId,
    bool SameContainer, bool SameColumn, float VerticalOverlap, float HorizontalOverlap,
    float VerticalGap, float HorizontalGap, bool ContinuationEvidence, bool BoundaryEvidence,
    bool Admitted, string FinalAdmissionReason);

internal sealed class VisualBlock
{
    public required string BlockId { get; init; }
    public required IReadOnlyList<NormalizedOcrLine> Lines { get; init; }
    public required RectangleF Bounds { get; init; }
    public required BlockLayoutBehavior LayoutBehavior { get; set; }
    public bool PreserveExplicitLineBreaks { get; set; }
    public string RoleHint { get; set; } = "Unknown";
    public TextSelectionAction TextSelection { get; set; } = TextSelectionAction.Auto;
    public string TextSelectionReason { get; set; } = "AUTO_DEFAULT";
    public SourceCellGeometry.Cell? SourceCell { get; set; }
    public RectangleF? SourceCaptionCorridor { get; set; }
    public RectangleF? SourceControlCorridor { get; set; }
    public SourceTextRoleEvidence? SourceRole { get; set; }
    public string SourceText => string.Join("\n", Lines.Select(x => x.SourceText));
}

internal sealed class BlockTranslation
{
    public required string BlockId { get; init; }
    public required string SourceText { get; init; }
    public string TranslatedText { get; set; } = "";
    public BlockTranslationState State { get; set; } = BlockTranslationState.Pending;
    public string FailureReason { get; set; } = "";
}

internal sealed record BlockRenderAudit(
    string BlockId, IReadOnlyList<string> SourceIds, RectangleF OwnerBounds,
    BlockTranslationState TranslationState, bool CleanupCommitted,
    bool TextDrawn, bool AtomicCommit, int ChangedPixels, string Result);

internal sealed record CorePipelineDocument(
    Size Canvas, IReadOnlyList<RawOcrLine> RawLines,
    IReadOnlyList<NormalizedOcrLine> NormalizedLines,
    IReadOnlyList<VisualBlock> VisualBlocks,
    IReadOnlyDictionary<string, BlockTranslation> Translations,
    IReadOnlyList<VisualEvidenceRegion> VisualEvidence,
    IReadOnlyList<RegionOwnerEvidence> RegionOwners,
    IReadOnlyList<GroupingEdgeEvidence> GroupingEdges);
