using System.Text.Json;

namespace ScreenshotTranslationUiTester;

public sealed record CleanupAudit(int SourceInkPixelCount,int CleanupPixelCount,float CleanupCoverageRatio,
    int ResidualSourcePixelEstimate,bool TranslationRendered,string FinalStatus);

public static class StableSourceAudit
{
    public static CleanupAudit MeasureCleanup(Bitmap source,Bitmap final,BackgroundIntegrationPlan plan,bool rendered)
    {
        var b=Rectangle.Intersect(plan.CleanupBounds,new(0,0,source.Width,source.Height));var ink=0;var cleanup=0;var residual=0;
        for(var y=b.Top;y<b.Bottom;y++)for(var x=b.Left;x<b.Right;x++)
        {
            var isInk=plan.PrimaryMask[x,y]||plan.PunctuationMask[x,y];if(isInk)ink++;
            if(plan.CleanupMask[x,y])cleanup++;
            if(isInk&&PixelDelta(source.GetPixel(x,y),final.GetPixel(x,y))<18)residual++;
        }
        var coverage=ink==0?1:Math.Min(1,cleanup/(float)ink);var significant=residual>Math.Max(4,ink*.08f);
        return new(ink,cleanup,coverage,residual,rendered,rendered&&coverage>=.92f&&!significant?"PASS":"FAIL");
    }

    public static void WriteMappingAudit(string path,RecognitionDocumentV2 document,
        IReadOnlyDictionary<string,RightTextRangeIdentity>? rightRanges=null,
        IReadOnlyList<RegionRenderDiagnostic>? diagnostics=null)
    {
        var units=document.TranslationUnits.ToDictionary(x=>x.Id,StringComparer.Ordinal);
        var rows=document.SourceIdentities.Select(source=>
        {
            var unit=document.TranslationUnits.FirstOrDefault(x=>x.StableSourceIds.Contains(source.SourceId,StringComparer.Ordinal));
            var region=unit is null?null:document.Regions.FirstOrDefault(x=>unit.RegionIds.Contains(x.RegionId,StringComparer.Ordinal));
            var render=region is null?null:diagnostics?.FirstOrDefault(x=>x.RegionId==region.RegionId);
            RightTextRangeIdentity? range=null;if(rightRanges is not null)rightRanges.TryGetValue(unit?.Id??"",out range);
            return new{source.SourceId,source.OriginalText,Role=source.SemanticRole.ToString(),BlockId=source.OriginalBlockId,
                SemanticGroupId=region?.GroupId??"",TranslationUnitId=unit?.Id??"",TranslatedText=region?.TranslationText??"",
                RenderUnitId=region?.RegionId??"",RightTextRange=range,HighlightSources=unit?.StableSourceIds??[],
                CleanupMaskBounds=render?.RenderBounds??RectangleF.Empty,RenderRect=region?.TranslationRenderRect??RectangleF.Empty};
        }).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
    }

    public static void WriteStyleAudit(string path,IReadOnlyList<VisualStyleGroup> groups)
    {Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,JsonSerializer.Serialize(groups,new JsonSerializerOptions{WriteIndented=true}));}

    private static int PixelDelta(Color a,Color b)=>Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B);
}
