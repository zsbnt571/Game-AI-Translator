using System.Text.Json;

namespace ScreenshotTranslationUiTester;

public sealed record RegionRenderDecisionRow(string TranslationUnitId, string RegionId, RegionRoleType Role,
    bool TranslationAvailable, bool RegionAvailable, string Stage, string Code, string Outcome,
    string Reason, int TextDrawCount, bool AtomicCommitted,
    IReadOnlyList<string>? SourceBlockIds=null,bool TranslationAuthorized=false,string SafeLayoutTarget="Unknown",
    string BackgroundStatus="Unknown",string TextColorStatus="Unknown",string TypographyStatus="Unknown");

public static class RegionRenderDecisionAudit
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static IReadOnlyList<RegionRenderDecisionRow> Build(RecognitionDocumentV2 document, RegionRenderResult rendered)
    {
        var diagnostics = (rendered.Diagnostics ?? []).ToDictionary(x => x.RegionId, StringComparer.Ordinal);
        var rows = new List<RegionRenderDecisionRow>();
        foreach (var unit in document.TranslationUnits)
        {
            if (unit.RegionIds.Count == 0)
                rows.Add(new(unit.Id, "", unit.RoleType, true, false, "RegionMapping", "NoRegionForTranslationUnit",
                    "NotRendered", "TranslationUnit has no RegionIds", 0, false));
            foreach (var regionId in unit.RegionIds)
            {
                var region = document.Regions.FirstOrDefault(x => x.RegionId == regionId);
                if (region is null)
                {
                    rows.Add(new(unit.Id, regionId, unit.RoleType, true, false, "RegionMapping", "RegionMissing",
                        "NotRendered", "TranslationUnit references a missing Region", 0, false));
                    continue;
                }
                if (!diagnostics.TryGetValue(regionId, out var d))
                {
                    rows.Add(new(unit.Id, regionId, region.RoleType, !string.IsNullOrWhiteSpace(region.TranslationText), true,
                        region.IsIgnored ? "Authorization" : "RendererDispatch", region.IsIgnored ? "IgnoredRegion" : "RendererSkipped",
                        "NotRendered", region.IsIgnored ? "Region is ignored" : "Region never reached renderer diagnostics", 0, false));
                    continue;
                }
                rows.Add(new(unit.Id, regionId, region.RoleType, !string.IsNullOrWhiteSpace(region.TranslationText), true,
                    d.DecisionStage, d.DecisionCode, d.AtomicRegionCommitted ? "Rendered" : "PreservedOriginal",
                    d.FallbackReason, d.TextDrawCount, d.AtomicRegionCommitted,region.SourceBlockIds,
                    !region.PreserveOriginal,
                    d.DecisionStage=="SafeLayoutTarget"?d.DecisionCode:"Passed",
                    d.DecisionStage=="BackgroundIntegration"?d.DecisionCode:d.AtomicRegionCommitted?"SafeToCommit":"NotReached",
                    d.AtomicRegionCommitted?d.TextColorReason:"NotReached",
                    d.DecisionStage=="Layout"?d.DecisionCode:d.AtomicRegionCommitted?$"Font={d.FontFamily}; Size={d.FontSize:0.##}":"NotReached"));
            }
        }
        foreach (var region in document.Regions.Where(x => !x.IsIgnored &&
                     document.TranslationUnits.All(u => !u.RegionIds.Contains(x.RegionId, StringComparer.Ordinal))))
            rows.Add(new(region.TranslationUnitId, region.RegionId, region.RoleType, !string.IsNullOrWhiteSpace(region.TranslationText), true,
                "TranslationMapping", "RegionWithoutTranslationUnit", "NotRendered", "Region is not mapped by any TranslationUnit", 0, false));
        return rows;
    }

    public static void Log(RecognitionDocumentV2 document, RegionRenderResult rendered, long totalMs)
    {
        var rows = Build(document, rendered);
        AppLog.Write("region-render-decisions", JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.Now, totalMs, regionCount = document.Regions.Count,
            translationUnitCount = document.TranslationUnits.Count,
            rendered = rows.Count(x => x.AtomicCommitted), preserved = rows.Count(x => !x.AtomicCommitted), rows
        }));
    }

    public static void Save(string path, RecognitionDocumentV2 document, RegionRenderResult rendered, long totalMs) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new { totalMs, rows = Build(document, rendered) }, Json));

    public static void SaveTitles(string path,RecognitionDocumentV2 document,RegionRenderResult rendered)
    {
        var titleIds=document.Regions.Where(r=>r.RoleType==RegionRoleType.Title).Select(r=>r.RegionId).ToHashSet(StringComparer.Ordinal);
        var rows=Build(document,rendered).Where(r=>titleIds.Contains(r.RegionId)).ToArray();
        File.WriteAllText(path,JsonSerializer.Serialize(new { titleCount=rows.Length,rendered=rows.Count(r=>r.AtomicCommitted),preserved=rows.Count(r=>!r.AtomicCommitted),rows },Json));
    }
}
