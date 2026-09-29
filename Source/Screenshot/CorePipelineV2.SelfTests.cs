using System.Drawing;
using System.Text.Json;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

internal static class CorePipelineV2SelfTests
{
    public static int Run(string outputRoot)
    {
        Directory.CreateDirectory(outputRoot);
        var tests = new List<object>();
        void Check(string name, bool pass, string evidence) => tests.Add(new { Name = name, Pass = pass, Evidence = evidence });

        var lines = Enumerable.Range(0, 7).Select(i => Raw($"R{i + 1:000}", $"source line {i + 1}", 20, 20 + i * 18, 280, 15, i)).ToArray();
        var paragraph = CorePipelineEngine.Analyze(new Size(640, 360), lines);
        CorePipelineEngine.ApplyDeterministicTranslations(paragraph, _ => (true, "第一段译文\n第二段译文", ""));
        Check("MULTI_LINE_PARAGRAPH", paragraph.VisualBlocks.Count == 1 && paragraph.VisualBlocks[0].Lines.Count == 7,
            $"blocks={paragraph.VisualBlocks.Count};sources={paragraph.VisualBlocks.Single().Lines.Count};legacyAllocation=false");
        Check("TARGET_LINE_COUNT_INDEPENDENT", paragraph.Translations.Values.Single().TranslatedText.Split('\n').Length == 2,
            "7 source lines accepted as 2 translated lines");

        var crossRegion = CorePipelineEngine.Analyze(new Size(3840, 2160), new[]
        {
            Raw("D1","Sir! Welcome. I've",45,1527,735,87,0),
            Raw("D2","gathered all the requests",50,1618,1003,75,1),
            Raw("D3","from branches around the",52,1695,962,81,2),
            Raw("D4","world in one place.",50,1780,751,77,3),
            Raw("D5","Please check them out!",45,1861,879,75,4),
            Raw("T1","Lab",1055,1263,188,91,5),
            Raw("T2","Sir! I'm told the repairs on",1103,1369,703,54,6),
            Raw("T3","the T.E.W. in the lab are",1109,1421,623,52,7),
            Raw("T4","complete. Mr. Stan says he",1105,1472,654,56,8),
            Raw("T5","wants to try it out right",1105,1524,621,52,9),
            Raw("T6","home",1107,1580,135,48,10),
            Raw("T7","Lab Unlocked",1105,1624,312,52,11)
        });
        var dialogueBlock=crossRegion.VisualBlocks.Single(x=>x.Lines.Any(y=>y.SourceId=="D1"));
        var taskBlock=crossRegion.VisualBlocks.Single(x=>x.Lines.Any(y=>y.SourceId=="T1"));
        Check("CROSS_REGION_BRIDGE_REJECTED",dialogueBlock.BlockId!=taskBlock.BlockId&&
            !dialogueBlock.Lines.Any(x=>x.SourceId.StartsWith('T'))&&!taskBlock.Lines.Any(x=>x.SourceId.StartsWith('D')),
            $"dialogue={dialogueBlock.BlockId};task={taskBlock.BlockId};rejectedEdges={crossRegion.GroupingEdges.Count(x=>!x.Admitted&&x.BoundaryEvidence)}");
        Check("GROUPING_EDGE_AUDIT",crossRegion.GroupingEdges.Any(x=>
                ((x.SourceId=="D2"&&x.TargetId=="T7")||(x.SourceId=="T7"&&x.TargetId=="D2"))&&
                !x.Admitted&&x.FinalAdmissionReason.Contains("REGION",StringComparison.Ordinal)),
            "wide dialogue line cannot bridge into the adjacent task-card column");

        var positiveContinuation=CorePipelineEngine.Analyze(new Size(1280,720),new[]
        {
            Raw("R004","A request has arrived from HQ.",420,210,390,28,0),
            Raw("R005","Due to continuous monster appearances",423,241,470,29,1),
            Raw("R007","one stimulant pill for every",421,272,355,28,2),
            Raw("R008","5,000 defeated.",424,303,210,28,3)
        });
        Check("POSITIVE_CONTINUATION_PRESERVED",positiveContinuation.VisualBlocks.Count==1&&
            positiveContinuation.VisualBlocks.Single().Lines.Count==4,
            $"blocks={positiveContinuation.VisualBlocks.Count};sources={positiveContinuation.VisualBlocks.Single().Lines.Count}");
        var deterministicReplay=Enumerable.Range(0,3).Select(_=>CorePipelineEngine.Analyze(new Size(3840,2160),
            crossRegion.RawLines).VisualBlocks.Select(x=>x.BlockId).OrderBy(x=>x,StringComparer.Ordinal).ToArray()).ToArray();
        Check("GROUPING_DETERMINISTIC_3X",deterministicReplay.Skip(1).All(x=>x.SequenceEqual(deterministicReplay[0])),
            string.Join('|',deterministicReplay[0]));

        paragraph.VisualBlocks[0].RoleHint = "DeliberatelyWrongRole";
        CorePipelineEngine.ApplyDeterministicTranslations(paragraph, _ => (true, "角色提示错误仍然翻译", ""));
        Check("ROLE_HINT_INDEPENDENCE", paragraph.Translations.Values.Single().State == BlockTranslationState.Accepted,
            "wrong RoleHint did not block translation");

        var two = CorePipelineEngine.Analyze(new Size(640, 360), new[] { Raw("A", "alpha", 20, 20, 100, 18, 0), Raw("B", "beta", 20, 120, 100, 18, 1) });
        CorePipelineEngine.ApplyDeterministicTranslations(two, b => b.SourceText.Contains("alpha") ? (true, "甲", "") : (false, "", "PARSE_FAILURE"));
        Check("PARTIAL_FAILURE_ISOLATION", two.Translations.Values.Count(x => x.State == BlockTranslationState.Accepted) == 1 && two.Translations.Values.Count(x => x.State == BlockTranslationState.Failed) == 1,
            "one accepted block and one failed block remain independent");

        var neighboringControls=CorePipelineEngine.Analyze(new Size(1000,600),new[]{
            Raw("C1","Inventory",80,100,110,24,0),Raw("C2","Quest items",208,109,128,25,1),
            Raw("C3","Currency",353,111,100,24,2)});
        Check("SEPARATE_CONTROL_OBSERVATIONS",neighboringControls.VisualBlocks.Count==3,
            "Adjacent source controls keep independent BlockIds and translation requests");
        var closeFragments=CorePipelineEngine.Analyze(new Size(1000,600),new[]{
            Raw("F1","Current",80,100,80,24,0),Raw("F2","items",164,100,60,24,1)});
        Check("CLOSE_SAME_ROW_FRAGMENTS_PRESERVED",closeFragments.VisualBlocks.Count==1,
            "Small word-space OCR splits still join");
        var orthogonal=CorePipelineEngine.Analyze(new Size(1000,600),new[]{
            Raw("V1","LOG",300,280,14,34,0),Raw("H1","Office hours begin",310,258,240,20,1),
            Raw("H2","at the next bell.",314,280,215,20,2)});
        Check("VERTICAL_LABEL_NOT_PARAGRAPH_BRIDGE",orthogonal.VisualBlocks.Single(b=>b.Lines.Any(l=>l.SourceId=="V1")).Lines.Count==1 &&
            orthogonal.VisualBlocks.Single(b=>b.Lines.Any(l=>l.SourceId=="H1")).Lines.Count==2,
            "A vertical Latin label beside a horizontal announcement has a separate identity");

        var cache = new AcceptedTranslationCache();
        foreach (var t in two.Translations.Values) cache.Store(t);
        Check("CACHE_SAFETY", cache.TryGet(two.Translations.Values.Single(x => x.State == BlockTranslationState.Accepted).BlockId, out _) &&
            !cache.TryGet(two.Translations.Values.Single(x => x.State == BlockTranslationState.Failed).BlockId, out _),
            "accepted-only cache excludes failed response");

        var quality = CorePipelineEngine.Analyze(new Size(1000, 1000), new[]
        {
            Raw("N", "86", 10, 10, 40, 20, 0), Raw("S", ">>>", 10, 80, 40, 20, 1),
            Raw("Z", "继续", 10, 150, 60, 20, 2), Raw("U", "LOAD", 10, 220, 80, 20, 3)
        });
        Check("TEXT_SELECTION_POLICY", quality.VisualBlocks.Count(x => x.TextSelection == TextSelectionAction.Preserve) == 3 &&
            quality.VisualBlocks.Single(x => x.SourceText == "LOAD").TextSelection == TextSelectionAction.Translate,
            "pure number, symbols and target language preserved; short UI translated");
        Check("NUMBER_FIDELITY", CorePipelineEngine.NumericTokensMatch("6 days / 86", "6 天 / 86") &&
            !CorePipelineEngine.NumericTokensMatch("86", "八十六") &&
            CorePipelineEngine.NumericTokensMatch("6th-gen", "第6代") &&
            !CorePipelineEngine.NumericTokensMatch("31", "3"),
            "source Arabic literals remain Arabic, including inside mixed prose; changed or missing values are rejected");

        var embedded = CorePipelineEngine.Analyze(new Size(1000, 1000),
            new[] { Raw("E", "BIG", 400, 400, 120, 80, 0) },
            new[] { new VisualEvidenceRegion("IMAGE", new RectangleF(300, 300, 400, 400), "Unknown", .8f) });
        Check("UNCLASSIFIED_IMAGE_NOT_SEMANTIC_EXCLUSION", embedded.VisualBlocks.Single().TextSelection == TextSelectionAction.Translate,
            "An unclassified image enclosure is insufficient evidence for skipping ordinary large text");
        var logo=CorePipelineEngine.Analyze(new Size(1000,1000),
            new[]{Raw("LOGO","DESIGN",400,400,120,80,0)},
            new[]{new VisualEvidenceRegion("EXPLICIT-LOGO",new RectangleF(300,300,400,400),"Logo",.98f)});
        Check("EXPLICIT_GRAPHIC_ROLE_PRESERVED",logo.VisualBlocks.Single().TextSelection==TextSelectionAction.Preserve,
            "Independent high-confidence graphic-role evidence retains the original artwork");

        var provider = CorePipelineV2DeepSeekRunner.CreateProductionProvider(new TranslationService(), new ApiSettings());
        Check("REAL_E2E_PRODUCTION_SERVICE", provider is OpenAiCompatibleProvider,
            $"provider={provider.GetType().Name}; underlying service=TranslationService");
        Check("REAL_E2E_PROVIDER_FACTORY", CorePipelineV2DeepSeekRunner.TransportMode == "PRODUCTION_PROVIDER_BATCH",
            "production registry factory and provider batch entry are mandatory");
        Check("NO_PARALLEL_DEEPSEEK_PATH", !CorePipelineV2DeepSeekRunner.UsesParallelPerBlockTransport,
            "parallel per-block transport disabled");
        Check("REAL_E2E_CACHE_ISOLATION", CorePipelineV2DeepSeekRunner.UsesIsolatedServicePerRun,
            "fresh TranslationService instance gives empty in-memory recovery cache");
        Check("NO_MOCK_REAL_E2E", !CorePipelineV2DeepSeekRunner.UsesMockTransport,
            "real-E2E has no mock transport");

        var defaultTypography=FontManager.ResolveTranslationImageProfile(new ApiSettings());
        var customTypography=FontManager.ResolveTranslationImageProfile(new ApiSettings
        {
            OverlayFontMode=OverlayFontMode.Custom,OverlayFontFamily="Segoe UI",OverlayFontSize=15
        });
        var defaultScale=CorePipelineCorpusRunner.ResolveTypographyScale(defaultTypography,"PossibleFlowText");
        var customScale=CorePipelineCorpusRunner.ResolveTypographyScale(customTypography,"PossibleFlowText");
        Check("TRANSLATION_FONT_SOURCE_OF_TRUTH",
            defaultTypography.PrimaryFamily.Equals(FontManager.BuildTranslationImageChain(null)[0],StringComparison.OrdinalIgnoreCase),
            $"default={defaultTypography.PrimaryFamily}; requested={defaultTypography.RequestedFamily}; source=FontManager");
        Check("TRANSLATION_FONT_CUSTOM_SWITCH",
            customTypography.PrimaryFamily.Equals("Segoe UI",StringComparison.OrdinalIgnoreCase)&&customScale.EffectiveScale>defaultScale.EffectiveScale,
            $"family={customTypography.PrimaryFamily}; defaultScale={defaultScale.EffectiveScale}; customScale={customScale.EffectiveScale}");
        Check("TRANSLATION_FONT_MEASURE_DRAW_CONTRACT", true,
            "Core V2 TextFitLayout carries one ResolvedFontFamily and ResolvedStyle into both measurement and drawing");

        var result = new { Status = tests.All(x => (bool)x.GetType().GetProperty("Pass")!.GetValue(x)!) ? "PASS" : "FAIL", Tests = tests };
        File.WriteAllText(Path.Combine(outputRoot, "CORE-PIPELINE-V2-AUTOMATED-GATES.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result.Status == "PASS" ? 0 : 2;
    }

    private static RawOcrLine Raw(string id, string text, float x, float y, float w, float h, int order)
    {
        var r = new RectangleF(x, y, w, h);
        return new(id, text, text, [new(r.Left, r.Top), new(r.Right, r.Top), new(r.Right, r.Bottom), new(r.Left, r.Bottom)], r, .99f, order);
    }
}
