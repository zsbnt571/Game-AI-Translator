using ScreenshotTranslationUiTester.CorePipelineV2;
namespace ScreenshotTranslationUiTester;
public sealed record CoreProcessingProvenance
{
    public string SourcePixelHash { get; init; } = "";
    public string OcrCoverageContract { get; init; } = "";
    public string CoreContract { get; init; } = "";
    public string ContentContract { get; init; } = "";
    public string TranslationContext { get; init; } = "";
    public bool ManualSourceCorrection { get; init; }
    public BackgroundExecutionIdentity? BackgroundExecution { get; init; }
    public BackgroundTreatment BackgroundTreatment { get; init; } = BackgroundTreatment.FineRepair;
    public string RenderStrategyVersion { get; init; } = "fix3-fine";
}
internal sealed record HistoryRegenerationDecision(bool Blocked, bool ReOcr, string Reason);
internal static class HistoryRegenerationPolicy
{
    internal const string OcrCoverageContract = "ocr-c9-source-field-upright6";
    internal const string CoreContract = "core-text-v2-independent-inline-heading22";
    internal static string TranslationContext(ApiSettings settings) => TranslationPromptBuilder.Sha256(
        string.Join("|", TranslationProviderRegistry.Get(settings.TranslationProviderKind).Id,
            TranslationCacheKeyBuilder.NormalizeBaseUrl(settings.ApiUrl), settings.Model.Trim(),
            TranslationPromptBuilder.NormalizeTarget(settings.TargetLanguage), settings.TranslationStyle,
            TranslationPromptBuilder.Sha256(settings.CustomTranslationPrompt?.Trim() ?? ""),
            TranslationPromptBuilder.Version, settings.PreserveIdentifiers, settings.PreserveNumbers,
            settings.PreserveVariables));
    internal static CoreProcessingProvenance FromOcr(string pixelHash, bool manual = false) => new()
    {
        SourcePixelHash=pixelHash, OcrCoverageContract=manual ? "MANUAL" : OcrCoverageContract,
        CoreContract=CoreContract, ManualSourceCorrection=manual
    };
    internal static bool HasProtectedSourceCorrection(HistoryCoreSnapshot? snapshot)
    {
        if(snapshot?.Provenance?.ManualSourceCorrection==true)return true;
        // Older snapshots may predate provenance. Preserve meaningful persisted
        // corrections; normalization alone is not evidence of a manual change.
        static string Normal(string text)=>System.Text.RegularExpressions.Regex.Replace(
            (text??"").Normalize(System.Text.NormalizationForm.FormKC),@"\s+"," ").Trim();
        return snapshot?.Lines.Any(x=>!string.IsNullOrWhiteSpace(x.CorrectedText)&&
            Normal(x.RawText)!=Normal(x.CorrectedText))==true;
    }
    internal static HistoryRegenerationDecision Evaluate(HistoryCoreSnapshot? snapshot,
        Size canvas, string sourceHash, bool sourceAvailable = true)
    {
        if(!sourceAvailable || string.IsNullOrWhiteSpace(sourceHash))
            return new(true,false,"SOURCE_UNAVAILABLE");
        var p=snapshot?.Provenance;
        var manual=HasProtectedSourceCorrection(snapshot);
        if(snapshot is null || snapshot.Lines.Count==0)
            return new(false,true,"OCR_GEOMETRY_MISSING");
        bool Valid(HistoryCoreLine line) => !string.IsNullOrWhiteSpace(line.SourceId) &&
            float.IsFinite(line.X) && float.IsFinite(line.Y) && float.IsFinite(line.Width) &&
            float.IsFinite(line.Height) && line.Width>0 && line.Height>0 &&
            line.X>=0 && line.Y>=0 && line.X+line.Width<=canvas.Width+1 &&
            line.Y+line.Height<=canvas.Height+1;
        if(snapshot.CanvasWidth!=canvas.Width || snapshot.CanvasHeight!=canvas.Height ||
            snapshot.Lines.Any(x=>!Valid(x)) ||
            snapshot.Lines.Select(x=>x.SourceId).Distinct(StringComparer.Ordinal).Count()!=snapshot.Lines.Count)
            return new(manual,!manual,manual?"MANUAL_GEOMETRY_CONFLICT":"OCR_GEOMETRY_INCOMPATIBLE");
        if(p is not null && !string.IsNullOrEmpty(p.SourcePixelHash) &&
            !string.Equals(p.SourcePixelHash,sourceHash,StringComparison.Ordinal))
            return new(manual,!manual,manual?"MANUAL_SOURCE_IDENTITY_CONFLICT":"SOURCE_IDENTITY_CHANGED");
        // Manual edits have priority over automatic coverage upgrades.
        if(manual) return new(false,false,"MANUAL_SOURCE_PRESERVED_REBUILD_CORE");
        if(p is null || p.SourcePixelHash.Length==0 || p.OcrCoverageContract!=OcrCoverageContract)
            return new(false,true,"OCR_COVERAGE_UNKNOWN_OR_OLD");
        return new(false,false,p.CoreContract==CoreContract
            ? "CURRENT_OCR_RECHECK_CONTENT" : "VALID_OCR_REBUILD_CURRENT_CORE");
    }
    internal static CorePipelineDocument RebuildForRegeneration(HistoryCoreSnapshot snapshot,
        Size canvas, ApiSettings settings, string requestId,Bitmap? sourceImage=null)
    {
        var core=snapshot.Rebuild(canvas,sourceImage);
        var contextMatches=snapshot.Provenance?.TranslationContext==TranslationContext(settings);
        foreach(var block in core.VisualBlocks)
        {
            var state=core.Translations[block.BlockId];
            var text=state.TranslatedText;
            state.State=BlockTranslationState.Pending;state.TranslatedText="";state.FailureReason="";
            if(!contextMatches || !snapshot.AcceptedSources.TryGetValue(block.BlockId,out var oldSource) ||
                oldSource!=block.SourceText || string.IsNullOrWhiteSpace(text))continue;
            var item=CoreTranslationItemFactory.Create(core,block);
            var checkedText=CoreTranslationContentValidator.Validate(item,text,requestId,settings.TargetLanguage);
            if(!checkedText.Accepted){state.FailureReason=checkedText.Reason;continue;}
            state.State=BlockTranslationState.Accepted;state.TranslatedText=checkedText.Text;
        }
        return core;
    }
}
