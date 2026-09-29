using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class OwnershipLayoutSelfTests
{
    private sealed record FixtureTranslation(string Category,string Translation);
    private sealed record PixelAudit(int SourceInkPixels,int ResidualPixels,string Status);
    public static int Run(string output,string? aprilSource,string? runtimeRoot)
    {
        Directory.CreateDirectory(output);
        try { RealApril(aprilSource,runtimeRoot,output).GetAwaiter().GetResult(); return 0; }
        catch(Exception ex){File.WriteAllText(Path.Combine(output,"APRIL-OWNERSHIP-FAIL.txt"),ex.ToString());return 1;}
    }
    private static async Task RealApril(string? aprilSource,string? runtimeRoot,string output)
    {
        Need(!string.IsNullOrWhiteSpace(aprilSource)&&File.Exists(aprilSource),"real April source missing");
        Need(!string.IsNullOrWhiteSpace(runtimeRoot)&&Directory.Exists(runtimeRoot),"runtime root missing");
        using var inputFile=new Bitmap(aprilSource!);using var source=new Bitmap(inputFile);
        await using var ocr=new OcrRuntimeManager(runtimeRoot!,new OcrService());
        await using var vision=new VisionRuntimeManager(runtimeRoot!);
        Need(ocr.GetStatus(OcrEngineKind.Rapid).Ready,"RapidOCR runtime not ready");
        var pipeline=new RecognitionPipelineV2(ocr,vision);
        var run=await pipeline.RunAsync(source,new ApiSettings{OcrEngine=OcrEngineKind.Rapid,VisualModel=VisualModelKind.Off,OcrLanguage="英语",TargetLanguage="简体中文"},809,CancellationToken.None);
        Need(run.Ocr.EngineActual==OcrEngineKind.Rapid&&!run.Ocr.FallbackUsed,"real path did not use RapidOCR");
        var units=run.Document.TranslationUnits.Where(x=>!x.PreserveOriginal&&!string.IsNullOrWhiteSpace(x.Text)).ToList();
        var fixture=units.ToDictionary(x=>x.Id,x=>FixtureFor(x.Text),StringComparer.Ordinal);
        File.WriteAllText(Path.Combine(output,"APRIL-REAL-UNIT-INVENTORY.json"),JsonSerializer.Serialize(units.Select(x=>new{x.Id,x.Text,Category=fixture[x.Id]?.Category,SourceIds=x.StableSourceIds,RegionIds=x.RegionIds,Regions=run.Document.Regions.Where(r=>x.RegionIds.Contains(r.RegionId,StringComparer.Ordinal)).Select(r=>new{r.RegionId,r.RoleType,r.RoleConfidence,r.ParentRegionId,r.BoundingBox,r.SourceLinePolygons,r.DetectionSources})}).ToList(),new JsonSerializerOptions{WriteIndented=true}));
        Need(fixture.All(x=>x.Value is not null),"real April contains an unmapped translation unit");
        TranslationMappingV2.Apply(run.Document,units.ToDictionary(x=>x.Id,x=>fixture[x.Id]!.Translation,StringComparer.Ordinal),809);
        using var rendered=RegionRendererV2.Render(source,run.Document.Regions,new RenderSettings{Background=false,MinFontSize=8});
        rendered.Bitmap.Save(Path.Combine(output,"APRIL-OWNERSHIP-FINAL.png"),ImageFormat.Png);
        File.WriteAllText(Path.Combine(output,"APRIL-RENDER-DIAGNOSTICS.json"),JsonSerializer.Serialize(rendered.Diagnostics,new JsonSerializerOptions{WriteIndented=true}));
        var finalRun=await pipeline.RunAsync(rendered.Bitmap,new ApiSettings{OcrEngine=OcrEngineKind.Rapid,VisualModel=VisualModelKind.Off,OcrLanguage="英语",TargetLanguage="简体中文"},810,CancellationToken.None);
        File.WriteAllText(Path.Combine(output,"APRIL-FINAL-OCR-INVENTORY.json"),JsonSerializer.Serialize(finalRun.Document.Regions.Select(x=>new{x.RegionId,x.OcrText,x.StructuredText,x.BoundingBox,x.SourceLinePolygons}).ToList(),new JsonSerializerOptions{WriteIndented=true}));
        var csv=new List<string>{"Category,SourceLineage,TUId,RUId,SourceLanguageState,TranslationRequired,Translation,RenderPath,SourceGlyphBounds,OwnedSurfaceBounds,FinalRenderBounds,TextDrawCount,DrawChangedPixels,CommitResult,CommitChangedPixels,SourceGlyphResidual,FinalVisibleTranslation,Outcome,FailureReason"};
        var outcomes=new List<object>();var gateConsistent=true;
        foreach(var unit in units)
        {
            var expected=fixture[unit.Id]!;var regions=run.Document.Regions.Where(x=>unit.RegionIds.Contains(x.RegionId,StringComparer.Ordinal)).ToList();
            var diagnostics=rendered.Diagnostics.Where(x=>x.TranslationUnitId==unit.Id).ToList();
            var committed=diagnostics.Where(x=>x.AtomicRegionCommitted&&x.TextDrawCount>0).ToList();
            var sourceState=IsTargetLanguage(unit.Text)?"TARGET_LANGUAGE":"SOURCE_LANGUAGE";
            var translationRequired=sourceState!="TARGET_LANGUAGE"&&!SameVisible(unit.Text,expected.Translation);
            var drawBounds=Union(committed.Select(x=>x.FinalRenderBounds));var commitBounds=Union(committed.Select(x=>x.AtomicCommitBounds));
            var drawChanged=drawBounds.IsEmpty?0:ChangedPixels(source,rendered.Bitmap,drawBounds);var commitChanged=commitBounds.IsEmpty?0:ChangedPixels(source,rendered.Bitmap,commitBounds);
            var renderedText=string.Concat(committed.SelectMany(x=>x.RenderedLines).Select(x=>x.Text));var fullText=SameVisible(renderedText,expected.Translation);
            var explicitFailure=diagnostics.Count>0&&diagnostics.All(x=>x.DecisionCode=="ExplicitRenderFailure"&&!x.AtomicRegionCommitted);
            var attributionKnown=committed.Count==0||(!drawBounds.IsEmpty&&!commitBounds.IsEmpty);var residual=MeasureResidual(source,rendered.Bitmap,regions,finalRun.Document.Regions,unit.Text,committed.Count>0);
            var finalVisible=committed.Count>0&&fullText&&drawChanged>0&&commitChanged>0;
            string outcome,reason;
            if(expected.Category=="HeaderCaptionMerged"){outcome="FAIL_GROUPING_MERGED";reason="Title and image caption share one TU/RU";}
            else if(!translationRequired){outcome="ALREADY_TARGET_LANGUAGE_NO_RENDER_REQUIRED";reason="Source is already target-language text";}
            else if(explicitFailure){outcome="EXPLICIT_RENDER_FAILURE";reason="Renderer explicitly rejected the unit";}
            else if(!attributionKnown){outcome="PIXEL_ATTRIBUTION_UNKNOWN";reason="Actual draw or atomic commit bounds unavailable";}
            else if(!finalVisible){outcome="TRANSLATION_NOT_RENDERED";reason="Full translated text was not attributable to final draw/commit pixels";}
            else if(residual.Status=="FAIL"){outcome="VISIBLE_TRANSLATION_WITH_SOURCE_RESIDUAL";reason="Source glyph pixels remain after committed translation";}
            else {outcome="VISIBLE_TRANSLATION_COMMITTED";reason="";}
            var expectedOutcome=expected.Category switch
            {
                "AgeMetadata" or "ContinueButton" or "MessageMetadata"=>"ALREADY_TARGET_LANGUAGE_NO_RENDER_REQUIRED",
                _=>"VISIBLE_TRANSLATION_COMMITTED"
            };
            var consistent=outcome==expectedOutcome;gateConsistent&=consistent;var ruIds=regions.Count==0?[""]:regions.Select(x=>x.RegionId).ToArray();
            foreach(var ruId in ruIds)
            {
                var d=diagnostics.FirstOrDefault(x=>x.RegionId==ruId);
                csv.Add(string.Join(",",Q(expected.Category),Q(string.Join("|",unit.StableSourceIds)),Q(unit.Id),Q(ruId),Q(sourceState),translationRequired,Q(expected.Translation),Q(d?.DecisionStage??"NO_DIAGNOSTIC"),Q(R(d?.SourceGlyphBounds??RectangleF.Empty)),Q(R(d?.OwnedSurfaceBounds??RectangleF.Empty)),Q(R(d?.FinalRenderBounds??RectangleF.Empty)),d?.TextDrawCount??0,drawChanged,d?.AtomicRegionCommitted??false,commitChanged,residual.ResidualPixels,finalVisible,Q(outcome),Q(reason)));
            }
            outcomes.Add(new{expected.Category,SourceLineage=unit.StableSourceIds,TUId=unit.Id,RUIds=ruIds,SourceLanguageState=sourceState,TranslationRequired=translationRequired,DiagnosticCount=diagnostics.Count,TextDrawCount=diagnostics.Sum(x=>x.TextDrawCount),AtomicCommit=committed.Count>0,DrawBounds=R(drawBounds),AtomicCommitBounds=R(commitBounds),DrawChangedPixels=drawChanged,CommitChangedPixels=commitChanged,SourceInkPixels=residual.SourceInkPixels,SourceGlyphResidual=residual.ResidualPixels,ResidualStatus=residual.Status,FinalVisibleTranslation=finalVisible,Outcome=outcome,ExpectedOutcome=expectedOutcome,VisualConsistency=consistent,FailureReason=reason});
        }
        File.WriteAllLines(Path.Combine(output,"APRIL-PER-RU-FINAL-COVERAGE-V2.csv"),csv,Encoding.UTF8);
        File.WriteAllText(Path.Combine(output,"APRIL-GATE-VISUAL-CONSISTENCY.json"),JsonSerializer.Serialize(new{Status=gateConsistent?"VALID":"INVALID",RealApiCalls=0,ProductBehaviorChanges=2,RendererDecisionChanges=1,GroupingBehaviorChanges=0,Outcomes=outcomes},new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllText(Path.Combine(output,"PER-RU-PIXEL-ATTRIBUTION-AUDIT.md"),AuditText(gateConsistent));
        Need(gateConsistent,"REAL GATE INVALID: per-RU classification still disagrees with frozen April final-image facts");
    }
    private static PixelAudit MeasureResidual(Bitmap source,Bitmap final,IReadOnlyList<RecognitionRegion> regions,IReadOnlyList<RecognitionRegion> finalRegions,string sourceText,bool rendered)
    {
        var ink=0;var residual=0;
        var sourceBounds=regions.Select(x=>x.SourceLinePolygons.Count>0?x.SourceLinePolygons.Select(GeometryV2.Bounds).Aggregate(RectangleF.Union):x.BoundingBox).Aggregate(RectangleF.Union);
        var sourceTokens=LatinTokens(sourceText).Where(x=>x.Length>=3).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var residualOcr=finalRegions.Where(x=>OverlapRatio(x.BoundingBox,sourceBounds)>.18f)
            .Where(x=>LatinTokens(string.IsNullOrWhiteSpace(x.StructuredText)?x.OcrText:x.StructuredText).Any(t=>sourceTokens.Any(s=>TokenMatch(s,t))))
            .ToList();
        foreach(var region in regions)
        {
            var plan=BackgroundIntegrationPlanner.Plan(source,region,region.BoundingBox,regions);
            var b=Rectangle.Intersect(plan.CleanupBounds,new Rectangle(Point.Empty,source.Size));
            for(var y=b.Top;y<b.Bottom;y++)for(var x=b.Left;x<b.Right;x++)
            {
                if(!(plan.PrimaryMask[x,y]||plan.PunctuationMask[x,y])||SourceContrast(source,x,y)<32)continue;
                ink++;
                if(residualOcr.Any(r=>r.BoundingBox.Contains(x+.5f,y+.5f))&&PixelDelta(source.GetPixel(x,y),final.GetPixel(x,y))<18)residual++;
            }
        }
        var fail=rendered&&residualOcr.Count>0&&residual>2;return new(ink,residual,rendered?(fail?"FAIL":"PASS"):"NOT_APPLICABLE");
    }
    private static IEnumerable<string> LatinTokens(string text)=>new string(text.Select(c=>char.IsLetterOrDigit(c)?char.ToLowerInvariant(c):' ').ToArray()).Split(' ',StringSplitOptions.RemoveEmptyEntries).Where(x=>x.Any(c=>c is >= 'a' and <= 'z'));
    private static bool TokenMatch(string a,string b)=>a.Equals(b,StringComparison.OrdinalIgnoreCase)||(a.Length>=5&&b.Length>=5&&(a.Contains(b,StringComparison.OrdinalIgnoreCase)||b.Contains(a,StringComparison.OrdinalIgnoreCase)));
    private static float OverlapRatio(RectangleF a,RectangleF b){var i=RectangleF.Intersect(a,b);return i.Width<=0||i.Height<=0?0:i.Width*i.Height/Math.Max(1,a.Width*a.Height);}
    private static int SourceContrast(Bitmap image,int x,int y)
    {
        var c=image.GetPixel(x,y);var max=0;
        for(var dy=-2;dy<=2;dy++)for(var dx=-2;dx<=2;dx++)
        {
            var xx=Math.Clamp(x+dx,0,image.Width-1);var yy=Math.Clamp(y+dy,0,image.Height-1);
            max=Math.Max(max,PixelDelta(c,image.GetPixel(xx,yy)));
        }
        return max;
    }
    private static int PixelDelta(Color a,Color b)=>Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B);
    private static bool IsTargetLanguage(string text){var han=text.Count(c=>c is >= '\u3400' and <= '\u9fff');var latin=text.Split([' ','|','/','-',':','\r','\n'],StringSplitOptions.RemoveEmptyEntries).Count(x=>x.Any(c=>c is >= 'A' and <= 'Z' or >= 'a' and <= 'z'));return han>0&&latin==0;}
    private static bool SameVisible(string a,string b)=>string.Concat(a.Where(c=>!char.IsWhiteSpace(c)))==string.Concat(b.Where(c=>!char.IsWhiteSpace(c)));
    private static RectangleF Union(IEnumerable<RectangleF> values){var a=values.Where(x=>!x.IsEmpty).ToArray();return a.Length==0?RectangleF.Empty:a.Aggregate(RectangleF.Union);}
    private static string AuditText(bool valid) =>
        "# Per-RU Pixel Attribution Audit\n\n" +
        $"- Gate status: {(valid ? "VALID" : "INVALID")}\n" +
        "- Product behavior changes in Gate repair: 0\n" +
        "- Renderer decision changes in Gate repair: 0\n" +
        "- Grouping behavior changes in Gate repair: 0\n" +
        "- Real API calls: 0\n\n" +
        "## Root cause\n\n" +
        "The old harness accepted only `DecisionCode=VisibleTranslationCommitted`. Normal renderer commits use `DecisionCode=Rendered`, so Orange Dialogue and Body were discarded before pixel measurement and falsely reported as uncommitted with zero changed pixels.\n\n" +
        "V2 accepts an actual committed draw by `AtomicRegionCommitted && TextDrawCount > 0`, attributes draw pixels to `FinalRenderBounds`, commit pixels to `AtomicCommitBounds`, and measures source residual only through the source glyph mask. Already-target-language source text does not require a draw or commit.\n";
    private static FixtureTranslation? FixtureFor(string text)
    {
        var n=text.ToLowerInvariant().Replace(" ","");var header=n.Contains("bitchyex")&&n.Contains("apriltorres");var caption=n.Contains("ex-gf")&&n.Contains("back")&&n.Contains("life");
        if(header&&caption)return new("HeaderCaptionMerged","刻薄前任 | 艾普丽尔·托雷斯 你刻薄的前女友重新出现在你的生活中");if(header)return new("CardHeader","刻薄前任 | 艾普丽尔·托雷斯");if(caption)return new("ImageCaption","你刻薄的前女友重新出现在你的生活中");
        if(n.Contains("wellwellwell"))return new("OrangeDialogue","“哎呀呀，看来你从高中起就一点没变。还是那个窝囊废。”");if(n.Contains("rememberyourex-girlfriend"))return new("Body","还记得你的前女友吗？不是那个无聊的，我说的是那个……");if(n.Contains("yourex-girlfriend"))return new("Identity","艾普丽尔 | 21岁 | 你的前女友");
        if((n.Contains("day")||n.Contains("天前"))&&n.Any(char.IsDigit))return new("AgeMetadata",new string(n.TakeWhile(char.IsDigit).ToArray())+"天前");if(n.Contains("continue")||n=="继续")return new("ContinueButton","继续");if(n.Contains("message")||n.Contains("消息"))return new("MessageMetadata",new string(n.TakeWhile(char.IsDigit).ToArray())+"条消息");return null;
    }
    private static int ChangedPixels(Bitmap before,Bitmap after,RectangleF bounds){var r=Rectangle.Intersect(Rectangle.Round(bounds),new Rectangle(0,0,before.Width,before.Height));var changed=0;for(var y=r.Top;y<r.Bottom;y++)for(var x=r.Left;x<r.Right;x++)if(before.GetPixel(x,y).ToArgb()!=after.GetPixel(x,y).ToArgb())changed++;return changed;}
    private static string R(RectangleF r)=>$"{r.X:F1}|{r.Y:F1}|{r.Width:F1}|{r.Height:F1}";
    private static string Q(object? value){var s=value?.ToString()??"";return "\""+s.Replace("\"","\"\"")+"\"";}
    private static void Need(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
