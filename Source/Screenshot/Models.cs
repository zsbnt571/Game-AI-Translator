using System.Text.Json.Serialization;

namespace ScreenshotTranslationUiTester;

public enum PreviewMode { OcrOnly, OcrAndTranslate, OcrCompare }
public enum PreviewWindowSizingMode { CurrentAuto, FollowPrevious, Fixed }
public enum UiFontMode { CurrentDefault, Custom }
public enum PreviewTextFontMode { CurrentDefault, FollowUiFont, Custom }
public enum OverlayFontMode { CurrentDefault, Custom }
public enum TranslationOverlayBackgroundStyle { Automatic, LightOverlay, Solid }
public enum OcrMode { Auto, English, SimplifiedChinese, Japanese, Korean, Mixed }
public enum TextColorMode { Auto, White, Black, Custom }
public enum CleanupStrength { Off, Normal, Strict }
public enum BackgroundComputeDevice { Cpu, Gpu }
public enum BackgroundTreatment { FineRepair, Lightweight }
public enum OcrLoad { Standard, Low }
public enum SegmentType { Title, ImageCaption, Dialogue, CharacterInfo, Body, UserName, Button, Time, MessageCount, Tag, NumericInfo, Label, Other }
public enum ImageViewMode { Original, Translated }
public enum TextViewMode { RawOcr, Organized, Translation, Hidden }
public enum PreviewDefaultImage { Original, Translated }
public enum PreviewDefaultText { RawOcr, Organized, Translation, Hidden }
public enum PreviewChromeMode { GameCompact, Full }
public enum ApplicationThemeMode { Day, Night, Custom }
public enum CloseMainWindowBehavior { Exit, MinimizeToTray }
public enum HistoryThumbnailSize { Small, Medium, Large }
public enum TranslationTextSource { Auto, Organized, Raw }
public enum TranslationStyle { Natural, Literal, GameLocalization, Custom }
public enum TranslationProviderKind { OpenAiCompatible }
public enum OcrEngineKind { Windows, Rapid, Paddle }
public enum OcrComparisonMode { Standard, ExperimentalWindows }
public enum VisualModelKind { Off, PPDocLayoutS, PPDocLayoutM, PPDocLayoutPlusL, PaddleOcrVl16, Qwen3Vl4BInstruct, Florence2BaseFt }
public enum SourceLanguageMode { Auto, English, SimplifiedChinese, Japanese, Korean, Mixed }
public enum OcrScriptKind { Latin, Cjk, Japanese, Digits, Symbols, Mixed, Unknown }
public enum OcrSemanticType { NaturalText, Numeric, Percentage, Currency, Time, Fraction, UnitValue, Button, PersonNameCandidate, Identifier, Mixed, Unknown }

public sealed record OcrCorrection(string Original, string Corrected, string Reason, string Confidence,
    string RuleId = "CommonSafe", OcrEngineKind? Engine = null);

public sealed record CaptureResult(Bitmap Image, PreviewMode Mode)
{
    public bool ImageOwnershipTransferred { get; set; }
}
public sealed record PreviewWindowPlacement(int X, int Y, int Width, int Height, bool Maximized);

public sealed class ApiSettings
{
    public int SettingsSchemaVersion { get; set; } = 10;
    public int TypographyConfigVersion { get; set; }
    public string ApiUrl { get; set; } = "https://api.deepseek.com/v1/chat/completions";
    public string ApiKey { get; set; } = "";
    public bool AllowEmptyApiKey { get; set; }
    public string Model { get; set; } = "deepseek-v4-flash";
    public string TargetLanguage { get; set; } = "Simplified Chinese";
    public string OcrLanguage { get; set; } = "自动";
    public int FirstByteTimeoutSeconds { get; set; } = 30;
    [JsonPropertyName("CompleteRequestTimeoutSeconds")]
    public int RequestTimeoutSeconds { get; set; } = 90;
    public CleanupStrength CleanupStrength { get; set; } = CleanupStrength.Normal;
    public OcrEngineKind OcrEngine { get; set; } = OcrEngineKind.Rapid;
    public VisualModelKind VisualModel { get; set; } = VisualModelKind.Off;
    public bool StructuredGroupingEnabled { get; set; } = true;
    public bool RoleClassificationEnabled { get; set; } = true;
    public bool ReadingOrderEnabled { get; set; } = true;
    public bool AutoFontSize { get; set; } = true;
    public bool AutoWrapping { get; set; } = true;
    public SourceLanguageMode SourceLanguage { get; set; } = SourceLanguageMode.Auto;
    public string HotKeyModifiers { get; set; } = "Ctrl + Alt";
    public string HotKeyKey { get; set; } = "Z";
    public InputBinding? StartCaptureBinding { get; set; }
    public InputBinding? CancelCaptureBinding { get; set; }
    public InputBinding? ToggleResultsBinding { get; set; }
    public InputBinding? PreviewZoomInBinding { get; set; }
    public InputBinding? PreviewZoomOutBinding { get; set; }
    public InputBinding? PreviewResetFitBinding { get; set; }
    public bool ImageTranslationEnabled { get; set; }
    public BackgroundComputeDevice BackgroundComputeDevice { get; set; } = BackgroundComputeDevice.Cpu;
    public BackgroundTreatment BackgroundTreatment { get; set; } = BackgroundTreatment.FineRepair;
    public OcrLoad OcrLoad { get; set; } = OcrLoad.Standard;
    public bool OcrCacheEnabled { get; set; } = true;
    public bool TranslationCacheEnabled { get; set; } = true;
    public int HistoryLimit { get; set; } = 20;
    public string TranslationProvider { get; set; } = "DeepSeek";
    public TranslationProviderKind TranslationProviderKind { get; set; } = TranslationProviderKind.OpenAiCompatible;
    public string ProviderDisplayName { get; set; } = "DeepSeek (OpenAI Compatible)";
    public TranslationStyle TranslationStyle { get; set; } = TranslationStyle.GameLocalization;
    public string CustomTranslationPrompt { get; set; } = "";
    public bool PreserveIdentifiers { get; set; } = true;
    public bool PreserveNumbers { get; set; } = true;
    public bool PreserveVariables { get; set; } = true;
    public string TranslationMode { get; set; } = "BatchCompactJson";
    public string TranslationPromptProfile { get; set; } = "NaturalGameTranslation";
    public string TranslationPromptVersion { get; set; } = "1";
    public string GlossaryVersion { get; set; } = "none";
    public PreviewDefaultImage PreviewDefaultImage { get; set; } = PreviewDefaultImage.Original;
    public PreviewDefaultText PreviewDefaultText { get; set; } = PreviewDefaultText.Organized;
    public bool PreviewTextPanelVisible { get; set; } = true;
    public TranslationTextSource TranslationTextSource { get; set; } = TranslationTextSource.Auto;
    public bool RememberLastPreviewSelection { get; set; }
    public PreviewChromeMode PreviewChromeMode { get; set; } = PreviewChromeMode.GameCompact;
    public bool PreviewAlwaysOnTop { get; set; }
    public bool HideMainWindowDuringCapture { get; set; } = true;
    public bool HidePreviewWindowsDuringCapture { get; set; } = true;
    public bool HasPreviewWindowPlacement { get; set; }
    public int PreviewWindowX { get; set; }
    public int PreviewWindowY { get; set; }
    public int PreviewWindowWidth { get; set; } = 1220;
    public int PreviewWindowHeight { get; set; } = 800;
    public bool PreviewWindowMaximized { get; set; }
    public PreviewWindowSizingMode PreviewWindowSizingMode { get; set; } = PreviewWindowSizingMode.CurrentAuto;
    public int FixedPreviewWidth { get; set; } = 1220;
    public int FixedPreviewHeight { get; set; } = 800;
    public int LastPreviewWidth { get; set; } = 1220;
    public int LastPreviewHeight { get; set; } = 800;
    public bool HasLastUserPreviewBounds { get; set; }
    public int LastUserPreviewX { get; set; }
    public int LastUserPreviewY { get; set; }
    public int LastUserPreviewWidth { get; set; } = 1220;
    public int LastUserPreviewHeight { get; set; } = 800;
    public bool LastUserPreviewMaximized { get; set; }
    public ApplicationThemeMode ThemeMode { get; set; } = ApplicationThemeMode.Night;
    public string CustomThemeMainBackground { get; set; } = "#121722";
    public string CustomThemeSecondaryBackground { get; set; } = "#1D222C";
    public string CustomThemeText { get; set; } = "#F4F7FB";
    public string CustomThemeSecondaryText { get; set; } = "#BECDDF";
    public string CustomThemeAccent { get; set; } = "#4EA1FF";
    public string CustomThemeBorder { get; set; } = "#46546A";
    public CloseMainWindowBehavior CloseMainWindowBehavior { get; set; } = CloseMainWindowBehavior.Exit;
    public bool HistoryCleanupByCount { get; set; } = true;
    public bool HistoryCleanupByAge { get; set; } = true;
    public int HistoryRetentionDays { get; set; } = 30;
    public bool HistoryCleanupBySpace { get; set; } = true;
    public int HistoryMaximumMegabytes { get; set; } = 512;
    public HistoryThumbnailSize HistoryThumbnailSize { get; set; } = HistoryThumbnailSize.Medium;
    public float HistoryTextSize { get; set; } = 10F;
    public UiFontMode UiFontMode { get; set; } = UiFontMode.CurrentDefault;
    public string UiFontFamily { get; set; } = "";
    public float UiFontSize { get; set; } = 10F;
    public PreviewTextFontMode PreviewTextFontMode { get; set; } = PreviewTextFontMode.CurrentDefault;
    public string PreviewTextFontFamily { get; set; } = "";
    public float PreviewTextFontSize { get; set; } = 10F;
    public OverlayFontMode OverlayFontMode { get; set; } = OverlayFontMode.CurrentDefault;
    public string OverlayFontFamily { get; set; } = "";
    public float OverlayFontSize { get; set; }
    public TranslationOverlayBackgroundStyle OverlayBackgroundStyle { get; set; } = TranslationOverlayBackgroundStyle.Automatic;
    public int OverlayBackgroundOpacity { get; set; } = 105;
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? ExtensionData { get; set; }
}

public static class OcrEngineUiMapping
{
    public static readonly OcrEngineKind[] DisplayOrder =
        [OcrEngineKind.Rapid, OcrEngineKind.Paddle, OcrEngineKind.Windows];

    public static OcrEngineKind FromSelectedIndex(int index) => index switch
    {
        0 => OcrEngineKind.Rapid,
        1 => OcrEngineKind.Paddle,
        2 => OcrEngineKind.Windows,
        _ => OcrEngineKind.Rapid
    };

    public static int ToSelectedIndex(OcrEngineKind engine) => engine switch
    {
        OcrEngineKind.Rapid => 0,
        OcrEngineKind.Paddle => 1,
        OcrEngineKind.Windows => 2,
        _ => 0
    };
}

public sealed class OcrEngineBlock
{
    public string Id { get; set; } = "";
    public string RawText { get; set; } = "";
    public string NormalizedText { get; set; } = "";
    public string CorrectedText { get; set; } = "";
    public float? Confidence { get; set; }
    public RectangleF BoundingBox { get; set; }
    public PointF[] Polygon { get; set; } = [];
    public int ReadingOrder { get; set; }
    public int LineIndex { get; set; }
    public bool Enabled { get; set; } = true;
    public OcrScriptKind Script { get; set; }
    public OcrSemanticType SemanticType { get; set; }
    public bool LowConfidence { get; set; }
    public bool SuspectedFalsePositive { get; set; }
    public bool PreprocessRetrySuggested { get; set; }
    public string PreprocessCandidateText { get; set; } = "";
    public float? PreprocessCandidateConfidence { get; set; }
    public bool UnresolvedAmbiguity { get; set; }
    public string AmbiguityCandidates { get; set; } = "";
    public string OcrRequestImageSha256 { get; set; } = "";
    public IReadOnlyList<OcrAlternativeEvidence> OcrAlternatives { get; set; } = [];
    public List<OcrCorrection> Corrections { get; set; } = [];
}

public sealed class OcrEngineResult
{
    public string SourceStagesJson { get; set; } = "";
    public string CoverageRecoveryJson { get; set; } = "";
    public string ImageSessionId { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string ImageHash { get; set; } = "";
    public OcrEngineKind EngineRequested { get; set; }
    public OcrEngineKind EngineActual { get; set; }
    public string EngineInstanceId { get; set; } = "";
    public string ModelName { get; set; } = "";
    public string ModelVersion { get; set; } = "";
    public DateTimeOffset ResultTimestamp { get; set; } = DateTimeOffset.UtcNow;
    public bool FallbackUsed { get; set; }
    public string RawText { get; set; } = "";
    public string NormalizedText { get; set; } = "";
    public string CorrectedText { get; set; } = "";
    public List<OcrEngineBlock> Blocks { get; set; } = [];
    public long TotalMilliseconds { get; set; }
    public long ModelLoadMilliseconds { get; set; }
    public bool CacheHit { get; set; }
    public string FallbackReason { get; set; } = "";
    public string Error { get; set; } = "";
    public long WorkingSetBytes { get; set; }
    public string ResultObjectId { get; set; } = Guid.NewGuid().ToString("N");
    public int? WorkerPid { get; set; }
    public int InputWidth { get; set; }
    public int InputHeight { get; set; }
    public long InputBytes { get; set; }
    public long SendMilliseconds { get; set; }
    public long ReceiveMilliseconds { get; set; }
    public long CorrectionMilliseconds { get; set; }
    public long PreprocessRetryMilliseconds { get; set; }
    public int PreprocessRetryCount { get; set; }
    public int LowConfidenceBlockCount { get; set; }
    public int CorrectedBlockCount { get; set; }
    public int CorrectionCount { get; set; }
    public int FalsePositiveSuspectCount { get; set; }
    public int AmbiguousBlockCount { get; set; }
    public int ConfidenceAvailableBlockCount { get; set; }
    public int ConfidenceUnavailableBlockCount { get; set; }
    public bool CharacterTopKAvailable { get; set; }
    public bool QualityPipelineApplied { get; set; }
    public int WindowsLegacyRuleInvocationCount { get; set; }
    public bool StylizedTitleRecoveryChecked { get; set; }
    public bool StylizedTitleRecoveryTriggered { get; set; }
    public double StylizedTitleRecoveryMilliseconds { get; set; }
    public int StylizedTitleRecoveryRawCandidateCount { get; set; }
    public int StylizedTitleRecoveryAcceptedCandidateCount { get; set; }
    public int StylizedTitleRecoveryLocalRecognitionCount { get; set; }
    public string StylizedTitleRecoveryReason { get; set; } = "";
    public string StylizedTitleRecoveryTraceJson { get; set; } = "";
    public List<string> StylizedTitleRecoveryProposalIds { get; set; } = [];
    public string BoundaryFastPathStatus { get; set; } = "NOT_REPORTED";
    public int BoundaryDeepAnalysisCount { get; set; }
    public int BoundaryHighProposalCount { get; set; }
    public int BoundaryReOcrLineCount { get; set; }
    public int BoundaryCropCallCount { get; set; }
    public int BoundaryChangedLineCount { get; set; }
    public double BoundaryFastPathMilliseconds { get; set; }
    public string BoundaryFastPathTraceJson { get; set; } = "";
    public string RetryOriginalRawText { get; set; } = "";
    public string RetryRawText { get; set; } = "";
    public string RetryChosenText { get; set; } = "";
    public string RetryReason { get; set; } = "";
    public string RetryPreprocessType { get; set; } = "";
    public string RetryConfirmationEvidenceJson { get; set; } = "";
    public bool Success => string.IsNullOrWhiteSpace(Error) && Blocks.Count > 0;
    public bool ValidForQualityComparison => Success && !FallbackUsed && EngineRequested == EngineActual;
}

public sealed class RenderSettings
{
    internal bool LegacyTextRasterizationForDiagnostics { get; set; }
    internal bool DiagnosticSkipCleanup { get; set; }
    internal bool DiagnosticSkipText { get; set; }
    internal bool DiagnosticMaskOnly { get; set; }
    internal bool DiagnosticUsePostCleanupContrast { get; set; }
    internal bool DiagnosticUseLegacySourceTextColor { get; set; }
    internal bool DiagnosticUseLegacyRoleTypography { get; set; }
    public TextColorMode TextColorMode { get; set; } = TextColorMode.Auto;
    public Color CustomTextColor { get; set; } = Color.White;
    public bool Outline { get; set; }
    public bool AutomaticOutlineColor { get; set; } = true;
    public Color OutlineColor { get; set; } = Color.Black;
    public bool Background { get; set; } = true;
    public Color BackgroundColor { get; set; } = Color.Black;
    public int BackgroundOpacity { get; set; } = 105;
    public float FontScale { get; set; } = 1F;
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public float MinFontSize { get; set; } = 8F;
    public float LineSpacingScale { get; set; } = 1.12F;
    public float CompactLineSpacingScale { get; set; } = 1F;
    public float SafeExpansionScale { get; set; } = 1.16F;
    public bool Shadow { get; set; }
    public RendererStrokeWidth StrokeWidth { get; set; } = RendererStrokeWidth.Thin;
    public TranslationOverlayBackgroundStyle BackgroundStrategy { get; set; } = TranslationOverlayBackgroundStyle.Automatic;
}

public enum RendererStrokeWidth { None, Thin, Medium }

public sealed class OcrRegion
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public RectangleF Bounds { get; set; }
    public float Confidence { get; set; }
    public string Language { get; set; } = "";
    public string Translation { get; set; } = "";
    public string SegmentId { get => Id; set => Id = value; }
    public SegmentType SegmentType { get; set; } = SegmentType.Other;
    public List<OcrRegion> RawLines { get; set; } = [];
    public string RawText { get; set; } = "";
    public string OrganizedText { get => Text; set => Text = value; }
    public int ReadingOrder { get; set; }
    public bool HasReliableTranslation { get; set; }
    public bool CanOverlay { get; set; } = true;
}

public sealed record OcrLineSnapshot(
    string SegmentId, string Text, RectangleF Bounds, int ReadingOrder,
    string Language, float Confidence);

public sealed record OcrCandidateSnapshot(
    string Name, string Language, int CharacterCount, int WordCount,
    float EstimatedConfidence, float NoiseRatio, int SingleCharacterNoise,
    int FragmentedWordCount, double Score, bool Selected);

public sealed record OcrSnapshot(
    IReadOnlyList<OcrLineSnapshot> Lines,
    string RawText,
    string OcrLanguage,
    IReadOnlyList<OcrCandidateSnapshot> Candidates);

public sealed class SegmentGroup
{
    public string GroupId { get; init; } = "";
    public SegmentType GroupType { get; init; }
    public IReadOnlyList<string> SourceSegmentIds { get; init; } = [];
    public string OriginalText { get; init; } = "";
    public string OrganizedText { get; init; } = "";
    public string Translation { get; set; } = "";
    public RectangleF Bounds { get; init; }
    public int ReadingOrder { get; init; }
    public bool HasReliableTranslation { get; set; }
    public bool CanOverlay { get; init; } = true;
    public string SourceText => OriginalText;
    public string CleanedText => OrganizedText;
    public RectangleF SourceRectangle => Bounds;
    public RectangleF RenderRectangle { get; set; }
    public bool IsReliable { get => HasReliableTranslation; set => HasReliableTranslation = value; }
    public bool CanRenderOnImage => CanOverlay;
}

public sealed class OcrDocument
{
    public string Text { get; set; } = "";
    public List<OcrRegion> Regions { get; set; } = [];
    public List<OcrRegion> RawLines { get; set; } = [];
    public OcrSnapshot? OriginalSnapshot { get; set; }
    public List<SegmentGroup> Groups { get; set; } = [];
    public string RawText { get; set; } = "";
    public bool SegmentMappingReliable { get; set; }
    public string FullTranslation { get; set; } = "";
    public bool TranslationStale { get; set; }
    public int SourceWidth { get; set; }
    public int SourceHeight { get; set; }
    public string SelectedCandidate { get; set; } = "";
    public Bitmap? DebugImage { get; set; }
    public List<OcrCandidateDiagnostic> CandidateDiagnostics { get; set; } = [];
    public TimingMetrics Metrics { get; set; } = new();
}

public sealed class OcrCandidateDiagnostic
{
    public string Name { get; set; } = "";
    public string Language { get; set; } = "";
    public int CharacterCount { get; set; }
    public int WordCount { get; set; }
    public float EstimatedConfidence { get; set; }
    public float NoiseRatio { get; set; }
    public int SingleCharacterNoise { get; set; }
    public int FragmentedWordCount { get; set; }
    public double Score { get; set; }
    public bool Selected { get; set; }
}

public sealed class TimingMetrics
{
    public long CaptureMs { get; set; }
    public long PreprocessMs { get; set; }
    public long OcrMs { get; set; }
    public long MergeMs { get; set; }
    public int ApiRequests { get; set; }
    public long ApiWaitMs { get; set; }
    public long BackgroundMs { get; set; }
    public long LayoutMs { get; set; }
    public long DrawMs { get; set; }
    public long EncodeMs { get; set; }
    public long TotalMs { get; set; }
    public long TotalWallClockMs { get; set; }
    public override string ToString() =>
        $"截图 {CaptureMs}ms｜预处理 {PreprocessMs}ms｜OCR {OcrMs}ms｜合并 {MergeMs}ms｜API {ApiRequests}次/{ApiWaitMs}ms｜背景 {BackgroundMs}ms｜排版 {LayoutMs}ms｜绘制 {DrawMs}ms｜编码 {EncodeMs}ms｜总计 {(TotalWallClockMs > 0 ? TotalWallClockMs : TotalMs)}ms";
}

internal sealed class OcrJsonDocument
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("regions")] public List<OcrJsonRegion> Regions { get; set; } = [];
}

internal sealed class OcrJsonRegion
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("x")] public float X { get; set; }
    [JsonPropertyName("y")] public float Y { get; set; }
    [JsonPropertyName("width")] public float Width { get; set; }
    [JsonPropertyName("height")] public float Height { get; set; }
}

public enum StructuredTextRole
{
    Title, CharacterName, Header, BodyParagraph, Dialogue, Narration, Metadata, Button, UILabel, Choice, Caption, Unknown, Species
}

public enum TranslationIdentityContract
{
    LegacyAllocationV1,
    CoreV2Block
}

public sealed record StructuredTextGroup(
    string GroupId, StructuredTextRole RoleType, IReadOnlyList<string> SourceBlockIds,
    string SourceText, RectangleF BoundingBox, int ReadingOrderIndex,
    string TranslationInputText, string TranslationOutputText = "");

public sealed record TranslationItem(string Id, string Text,
    StructuredTextRole RoleType = StructuredTextRole.Unknown,
    IReadOnlyList<string>? SourceIds = null,
    TranslationIdentityContract IdentityContract = TranslationIdentityContract.LegacyAllocationV1)
{
    public OcrTranslationContext? OcrContext { get; init; }
    public string SemanticPurpose { get; init; } = "";
    public IReadOnlyList<SourceNeighborText> SourceNeighbors { get; init; } = [];
    public IReadOnlyList<string> StableSourceIds => SourceIds ?? [];
}
public sealed record SourceNeighborText(string SourceId,string Text,string Relation);

public sealed record TranslationAllocationSegment(
    string TranslationUnitId,
    IReadOnlyList<string> SourceIds,
    string TranslatedText,
    int Sequence,
    double MappingConfidence,
    string SegmentId = "");
public sealed record TranslationSemanticGroup(
    string Id, string Text, IReadOnlyList<string> SourceBlockIds,
    SegmentType GroupType, RectangleF Bounds, int ReadingOrder);
public sealed record RightTextRangeIdentity(int Start,int Length,string TranslationUnitId,
    IReadOnlyList<string> SourceIds,IReadOnlyList<string>? RenderUnitIds=null,
    StructuredTextRole Role=StructuredTextRole.Unknown)
{
    public IReadOnlyList<string> StableRenderUnitIds=>RenderUnitIds??[];
}
public enum TranslationStage
{
    OrganizingParagraphs,
    BuildingRequest,
    SendingRequest,
    ResponseReceived,
    ReadingResponse,
    RecoveringResponse,
    ParsingResponse,
    RequestFinished
}

public sealed record TranslationProgress(
    TranslationStage Stage,
    int RequestCount,
    long TotalWaitMs,
    string Message,
    int? HttpStatusCode = null);

public sealed record TranslationBatchResult(
    Dictionary<string, string> Translations,
    int RequestCount,
    long WaitMs,
    IReadOnlyList<string> MissingIds,
    bool UsedResponseFormat,
    string FullTranslation = "",
    bool SegmentMappingReliable = true,
    TranslationRecoveryStats? RecoveryStats = null,
    Dictionary<string, TranslationAllocationSegment[]>? Allocations = null,
    TranslationTransportStats? TransportStats = null)
{
    public IReadOnlyDictionary<string, TranslationAllocationSegment[]> StableAllocations =>
        Allocations ?? EmptyAllocations;
    public bool HasSourceAlignedAllocations => StableAllocations.Count > 0;
    private static readonly IReadOnlyDictionary<string, TranslationAllocationSegment[]> EmptyAllocations =
        new Dictionary<string, TranslationAllocationSegment[]>(StringComparer.Ordinal);
}

public sealed record TranslationTransportStats(
    IReadOnlyList<int> HttpStatusCodes,
    int PromptTokens = 0,
    int CompletionTokens = 0,
    int TotalTokens = 0,
    int HttpFailureCount = 0,
    int TransportFailureCount = 0);

public sealed class TranslationTimeoutException(string message, Exception? inner = null)
    : TimeoutException(message, inner);

public sealed class TranslationFirstByteTimeoutException(string message, Exception? inner = null)
    : TimeoutException(message, inner);

public sealed class TranslationConnectionTimeoutException(string message, Exception? inner = null)
    : TimeoutException(message, inner);

public class BatchJsonException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

public sealed record PlainTranslationResult(string Text, int RequestCount, long WaitMs, int HttpStatusCode);
