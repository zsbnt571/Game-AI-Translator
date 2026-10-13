using System.Text.Json;

namespace ScreenshotTranslationUiTester;

public enum PreserveClassification { None, SafePreserve, UnexpectedSkip }

public sealed record RoleCoverageRow(string Role, int TranslationAvailableRegionCount,
    int NormalRenderedRegionCount,int FallbackRenderedRegionCount,int PreservedOriginalRegionCount,
    int TranslatedCharacterCount, int CommittedCharacterCount);

public sealed record RenderCoverageSummary(string ImageId, int VisibleTextRegionCount,
    int TranslationAvailableRegionCount,int NormalRenderedRegions,int FallbackRenderedRegions,
    int PreservedOriginalRegions,int TranslationRenderedTotal,double TranslationCoverage,
    int CommittedRegionCount, int SafePreserveCount,int UnexpectedSkipCount, int AtomicAbortCount, int NoSafeLayoutCount,
    int MappingFailureCount, int BackgroundFailureCount, int TypographyFailureCount,
    double RegionCoverage, double TranslatedCharacterCoverage,
    IReadOnlyList<RoleCoverageRow> PerRole, IReadOnlyList<RegionCoverageDecision> Regions);

public sealed record RegionCoverageDecision(string RegionId, string TranslationUnitId,
    string Role, string SourceText, string TranslatedText, RectangleF SourceRect,
    RectangleF RenderRect, bool TranslationAvailable, bool AtomicCommitted,
    PreserveClassification Classification, string SafeLayout, string BackgroundStatus,
    string TypographyStatus, string AtomicStatus, string FinalReason,
    bool FallbackAttempted,bool FallbackRendered,string NormalRenderFailureReason,string FallbackFailureReason,string FinalPreserveReason);

public static class RenderCoverageAudit
{
    public static RenderCoverageSummary Build(string imageId, IReadOnlyList<RecognitionRegion> regions,
        IReadOnlyList<RegionRenderDiagnostic> diagnostics)
    {
        var byId=diagnostics.ToDictionary(x=>x.RegionId,StringComparer.Ordinal);
        var rows=new List<RegionCoverageDecision>();
        foreach(var region in regions.Where(x=>!x.IsIgnored))
        {
            byId.TryGetValue(region.RegionId,out var diagnostic);
            var available=!string.IsNullOrWhiteSpace(region.TranslationText);
            var committed=diagnostic?.AtomicRegionCommitted==true;
            var classification=Classify(region,diagnostic,available,committed);
            var stage=diagnostic?.DecisionStage??"MissingDiagnostic";
            rows.Add(new(region.RegionId,region.TranslationUnitId,region.RoleType.ToString(),
                string.IsNullOrWhiteSpace(region.StructuredText)?region.OcrText:region.StructuredText,
                region.TranslationText,region.BoundingBox,diagnostic?.TranslationRenderRect??RectangleF.Empty,
                available,committed,classification,
                stage=="SafeLayoutTarget"?diagnostic?.FallbackReason??"Failed":"Passed",
                stage=="BackgroundIntegration"?diagnostic?.FallbackReason??"Failed":committed?"Committed":"NotReached",
                stage is "Layout" or "Typography"?diagnostic?.FallbackReason??"Failed":committed?$"Font={diagnostic?.FontFamily}; Size={diagnostic?.FontSize:0.##}":"NotReached",
                committed?"Committed":diagnostic?.DecisionCode??"MissingDiagnostic",diagnostic?.FallbackReason??"Missing renderer diagnostic",
                diagnostic?.FallbackAttempted==true,diagnostic?.FallbackRendered==true,diagnostic?.NormalRenderFailureReason??"",diagnostic?.FallbackFailureReason??"",
                committed?"":diagnostic?.FallbackReason??"Missing renderer diagnostic"));
        }
        var translated=rows.Where(x=>x.TranslationAvailable).ToArray();
        var committedRows=translated.Where(x=>x.AtomicCommitted).ToArray();
        var normal=translated.Where(x=>x.AtomicCommitted&&!x.FallbackRendered).ToArray();var fallback=translated.Where(x=>x.FallbackRendered).ToArray();var preserved=translated.Where(x=>!x.AtomicCommitted).ToArray();
        var perRole=translated.GroupBy(x=>x.Role).OrderBy(x=>x.Key).Select(g=>new RoleCoverageRow(g.Key,g.Count(),
            g.Count(x=>x.AtomicCommitted&&!x.FallbackRendered),g.Count(x=>x.FallbackRendered),g.Count(x=>!x.AtomicCommitted),g.Sum(x=>x.TranslatedText.Length),
            g.Where(x=>x.AtomicCommitted).Sum(x=>x.TranslatedText.Length))).ToArray();
        var chars=translated.Sum(x=>x.TranslatedText.Length);
        return new(imageId,rows.Count,translated.Length,normal.Length,fallback.Length,preserved.Length,committedRows.Length,
            translated.Length==0?1:committedRows.Length/(double)translated.Length,committedRows.Length,
            translated.Count(x=>x.Classification==PreserveClassification.SafePreserve),
            translated.Count(x=>x.Classification==PreserveClassification.UnexpectedSkip),
            diagnostics.Count(x=>x.DecisionCode=="AtomicAbort"),diagnostics.Count(x=>x.DecisionCode=="NoSafeLayout"),
            diagnostics.Count(x=>x.DecisionStage is "TranslationMapping" or "Mapping"),
            diagnostics.Count(x=>x.DecisionStage=="BackgroundIntegration"),
            diagnostics.Count(x=>x.DecisionStage is "Layout" or "Typography"),
            translated.Length==0?1:committedRows.Length/(double)translated.Length,
            chars==0?1:committedRows.Sum(x=>x.TranslatedText.Length)/(double)chars,perRole,rows);
    }

    public static void Write(string path, RenderCoverageSummary summary)=>File.WriteAllText(path,
        JsonSerializer.Serialize(summary,new JsonSerializerOptions{WriteIndented=true}));

    public static bool PassesProductCoverage(RenderCoverageSummary summary)=>summary.UnexpectedSkipCount==0;

    private static PreserveClassification Classify(RecognitionRegion region,RegionRenderDiagnostic? diagnostic,bool available,bool committed)
    {
        if(!available||committed)return PreserveClassification.None;
        var sourceText=string.IsNullOrWhiteSpace(region.StructuredText)?region.OcrText:region.StructuredText;
        if(string.Equals(sourceText.Trim(),region.TranslationText.Trim(),StringComparison.Ordinal))return PreserveClassification.SafePreserve;
        if(diagnostic is null)return PreserveClassification.UnexpectedSkip;
        if(diagnostic.DecisionStage is "Layout" or "Typography")
        {
            var source=region.BoundingBox;var protectedArea=region.LayoutExclusionPolygons.Select(GeometryV2.Bounds)
                .Select(x=>RectangleF.Intersect(x,source)).Where(x=>x.Width>0&&x.Height>0).Sum(x=>x.Width*x.Height);
            if(protectedArea/Math.Max(1,source.Width*source.Height)>.15f)return PreserveClassification.SafePreserve;
            return PreserveClassification.UnexpectedSkip;
        }
        if(diagnostic.DecisionStage is "TranslationMapping" or "Mapping")return PreserveClassification.UnexpectedSkip;
        if(diagnostic.DecisionCode is "NoSafeLayout" or "CoverageIncomplete" or "EmptyTarget")
            return PreserveClassification.UnexpectedSkip;
        if(region.PreserveOriginal||diagnostic.DecisionStage is "Authorization" or "BackgroundIntegration" or "SafeLayoutTarget")
            return PreserveClassification.SafePreserve;
        return PreserveClassification.UnexpectedSkip;
    }
}
