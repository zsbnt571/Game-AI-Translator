namespace ScreenshotTranslationUiTester;

public enum RegionRoleType { Automatic, Title, CharacterName, Header, BodyParagraph, Dialogue, Narration, Metadata, Button, UILabel, Choice, Caption, Image, Illustration, Unknown, Species }
public enum DetectionSource { TraditionalCv, VisualModel, OcrDetector, CropOcr, Manual }
public enum RegionOverlayLayer { OcrBlocks, VisualRegions, RecognitionRegions, SemanticGroups, RoleLabels, ReadingOrder, TranslationMapping, Coverage }
public enum SourceLineDisposition { AssignedToTranslationUnit, PreserveOriginal, IgnoredWithReason, Unaccounted }

public sealed record SourceIdentity(
    string SourceId,
    string OriginalText,
    PointF[] OriginalPolygon,
    RectangleF OriginalInkBounds,
    RectangleF OriginalLineBounds,
    string OriginalBlockId,
    RegionRoleType SemanticRole,
    int ReadingOrder,
    string PageId);

public static class VisualModelUiMapping
{
    public static readonly (VisualModelKind Kind, string Label)[] Options =
    [
        (VisualModelKind.Off, "关闭"),
        (VisualModelKind.PPDocLayoutS, "PP-DocLayout-S（快速 / 轻量）"),
        (VisualModelKind.PPDocLayoutM, "PP-DocLayout-M（均衡 / 高质量）"),
        (VisualModelKind.PPDocLayoutPlusL, "PP-DocLayout-plus-L（复杂版面 / 高精度）"),
        (VisualModelKind.PaddleOcrVl16, "PaddleOCR-VL-1.6（视觉理解 / 高质量）"),
        (VisualModelKind.Qwen3Vl4BInstruct, "Qwen3-VL-4B-Instruct（智能分类 / 高精度）"),
        (VisualModelKind.Florence2BaseFt, "Florence-2-base-ft（实验 / 轻量视觉）")
    ];
}

public sealed record RecognitionContext(OcrEngineKind OcrEngine, VisualModelKind VisualModel, long VisionGeneration, Size SourceSize);
public sealed record RoleHint(string VisualRegionId, RegionRoleType Role, float Confidence);
public sealed record ReadingOrderHint(string VisualRegionId, int Order, float Confidence);
public sealed record LayoutRelation(string ParentId, string ChildId, string Relation, float Confidence);
public sealed record ModelDiagnostics(string Model, long ElapsedMs, string Status, string Detail = "");

public sealed class VisualRegion
{
    public string VisualRegionId { get; init; } = $"VIS-{Guid.NewGuid():N}";
    public PointF[] Polygon { get; init; } = [];
    public RectangleF BoundingBox => GeometryV2.Bounds(Polygon);
    public RegionRoleType VisualRoleHint { get; init; } = RegionRoleType.Unknown;
    public float Confidence { get; init; }
    public int? ReadingOrderHint { get; init; }
    public string? ParentContainerHint { get; init; }
    public string SourceModel { get; init; } = "";
    public string? OptionalTextHint { get; init; }
}

public sealed record VisualAnalysisResult(IReadOnlyList<VisualRegion> VisualRegions,
    IReadOnlyList<RoleHint> RoleHints, IReadOnlyList<ReadingOrderHint> ReadingOrderHints,
    IReadOnlyList<LayoutRelation> LayoutRelations, ModelDiagnostics ModelDiagnostics);

public interface IVisualAnalyzer
{
    VisualModelKind Kind { get; }
    Task<VisualAnalysisResult> AnalyzeAsync(Bitmap sourceBitmap, RecognitionContext context, CancellationToken cancellationToken);
}

public sealed class RecognitionRegion
{
    public string RegionId { get; init; } = $"REG-{Guid.NewGuid():N}";
    public PointF[] Polygon { get; set; } = [];
    public RectangleF BoundingBox => GeometryV2.Bounds(Polygon);
    public List<string> SourceBlockIds { get; set; } = [];
    public List<OcrEngineBlock> DetectedBlocks { get; set; } = [];
    public List<PointF[]> SourceLinePolygons { get; set; } = [];
    public List<SourceSegmentGeometry> SourceSegments { get; set; } = [];
    public List<PointF[]> LayoutExclusionPolygons { get; set; } = [];
    public string OcrText { get; set; } = "";
    public string CorrectedText { get; set; } = "";
    public string StructuredText { get; set; } = "";
    public RegionRoleType RoleType { get; set; } = RegionRoleType.Unknown;
    public float RoleConfidence { get; set; }
    public int ReadingOrder { get; set; }
    public string GroupId { get; set; } = "";
    public string TranslationUnitId { get; set; } = "";
    public string TranslationText { get; set; } = "";
    public RectangleF SourceImageRegion { get; set; }
    public RectangleF RendererTargetRegion { get; set; }
    public RectangleF? TranslationRenderRect { get; set; }
    public float RecognitionConfidence { get; set; }
    public long RegionGeneration { get; set; }
    public List<DetectionSource> DetectionSources { get; set; } = [];
    public string? ParentRegionId { get; set; }
    public bool IsIgnored { get; set; }
    public bool PreserveOriginal { get; set; }
    public bool CoverageValid { get; set; } = true;
    public string CoverageFailureReason { get; set; } = "";
    public RegionManualOverrideFlags ManualOverrideFlags { get; set; }
    public RecognitionRegion Clone() => new() { RegionId = RegionId, Polygon = [.. Polygon], SourceBlockIds = [.. SourceBlockIds],
        DetectedBlocks = [.. DetectedBlocks], SourceLinePolygons = SourceLinePolygons.Select(x => x.ToArray()).ToList(),
        SourceSegments = SourceSegments.Select(x => x with { Polygon = x.Polygon.ToArray() }).ToList(),
        LayoutExclusionPolygons = LayoutExclusionPolygons.Select(x => x.ToArray()).ToList(), OcrText = OcrText, CorrectedText = CorrectedText, StructuredText = StructuredText,
        RoleType = RoleType, RoleConfidence = RoleConfidence, ReadingOrder = ReadingOrder, GroupId = GroupId,
        TranslationUnitId = TranslationUnitId, TranslationText = TranslationText, SourceImageRegion = SourceImageRegion,
        RendererTargetRegion = RendererTargetRegion, TranslationRenderRect = TranslationRenderRect, RecognitionConfidence = RecognitionConfidence,
        RegionGeneration = RegionGeneration, DetectionSources = [.. DetectionSources], ParentRegionId = ParentRegionId,
        IsIgnored = IsIgnored, PreserveOriginal = PreserveOriginal, CoverageValid = CoverageValid,
        CoverageFailureReason = CoverageFailureReason, ManualOverrideFlags = ManualOverrideFlags };
}

public sealed record SourceSegmentGeometry(string BlockId, string Text, PointF[] Polygon,
    RegionRoleType Role, float RoleConfidence, int ReadingOrder, string OriginRegionId);

[Flags] public enum RegionManualOverrideFlags { None = 0, Geometry = 1, Role = 2, ReadingOrder = 4, Ignore = 8, PreserveOriginal = 16, Text = 32 }
public sealed record TranslationUnitV2(string Id, IReadOnlyList<string> RegionIds, RegionRoleType RoleType,
    string Text, int ReadingOrder, bool PreserveOriginal, IReadOnlyList<string>? SourceIds = null)
{
    public IReadOnlyList<string> StableSourceIds => SourceIds ?? [];
}
public sealed record RecognitionPipelineDiagnostics(long VisionMs, long OcrMs, long FusionMs, long GroupingMs, long RoleMs,
    long TotalMs, string VisualModel, OcrEngineKind OcrEngine, int VisualRegionCount, int OcrBlockCount,
    int RecognitionRegionCount, int TranslationUnitCount, int CropOcrCount = 0, long CropOcrMs = 0,
    long TraditionalCvMs = 0, long TranslationUnitBuildMs = 0, int RoleClassificationCount = 0,
    long CoverageFinalizeMs = 0, int UnaccountedSourceLines = 0, int MissingRequiredGeometry = 0);

public sealed record SourceLineCoverage(string SourceLineId, string SourceText, string RegionId, string TranslationUnitId,
    SourceLineDisposition Disposition, string Reason, PointF[] Polygon, bool UsedBoundingRectFallback,
    IReadOnlyList<string> MergedFromIds);
public sealed record RecognitionCoverageSummary(int TotalSourceLines, int Assigned, int Preserved, int Ignored,
    int Unaccounted, int TranslationUnits, int RequiredEraseLines, int ValidEraseGeometry, int MissingEraseGeometry);

public sealed class RecognitionDocumentV2
{
    public List<RecognitionRegion> Regions { get; } = [];
    public List<TranslationUnitV2> TranslationUnits { get; } = [];
    public List<SourceIdentity> SourceIdentities { get; } = [];
    public long VisionGeneration { get; set; }
    public long OcrGeneration { get; set; }
    public long RegionGeneration { get; set; }
    public long TranslationGeneration { get; set; }
    public RecognitionPipelineDiagnostics? Diagnostics { get; set; }
    public List<SourceLineCoverage> SourceLineCoverage { get; } = [];
    public RecognitionCoverageSummary? CoverageSummary { get; set; }
    public string TranslationMappingMode { get; set; } = "LEGACY_MAPPING";
    public Dictionary<string,TranslationAllocationSegment[]> TranslationAllocations { get; } = new(StringComparer.Ordinal);
}

public static class GeometryV2
{
    public static PointF[] RectanglePolygon(RectangleF value) => [new(value.Left, value.Top), new(value.Right, value.Top), new(value.Right, value.Bottom), new(value.Left, value.Bottom)];
    public static RectangleF Bounds(IReadOnlyList<PointF> points)
    {
        if (points.Count == 0) return RectangleF.Empty;
        return RectangleF.FromLTRB(points.Min(x => x.X), points.Min(x => x.Y), points.Max(x => x.X), points.Max(x => x.Y));
    }
    public static float IntersectionOverUnion(RectangleF a, RectangleF b)
    {
        var i = RectangleF.Intersect(a, b); if (i.Width <= 0 || i.Height <= 0) return 0;
        return i.Width * i.Height / Math.Max(1, a.Width * a.Height + b.Width * b.Height - i.Width * i.Height);
    }
}
