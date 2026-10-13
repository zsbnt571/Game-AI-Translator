using System.Drawing;
using System.Text.Json;
using ScreenshotTranslationUiTester.CorePipelineV2;

namespace ScreenshotTranslationUiTester;

internal static class FinalProductFixWaveSelfTests
{
    private sealed record Gate(string Area,string Name,bool Pass,string Evidence);

    internal static int Run(string outputRoot,string sourceRoot)
    {
        Directory.CreateDirectory(outputRoot);
        var gates=new List<Gate>();
        void Check(string area,string name,bool pass,string evidence)=>gates.Add(new(area,name,pass,evidence));

        var selection=CorePipelineEngine.Analyze(new Size(1920,1080),new[]
        {
            Raw("AT","@Krasue",20,30,130,24,0),
            Raw("URL","https://example.com/path?q=1",20,100,420,24,1),
            Raw("TAG","#bestfriend",20,170,180,24,2),
            Raw("PERF","FPS 160 (1%L) GPU 24% CPU 11%",20,240,470,24,3),
            Raw("HIDE","HIDE",20,310,80,24,4),
            Raw("BOTTOM","Q.Save",20,380,110,24,5)
        });
        TextSelectionAction Action(string id)=>selection.VisualBlocks.Single(block=>block.Lines.Any(line=>line.SourceId==id)).TextSelection;
        Check("SELECTION","AT_USERNAME_PRESERVED",Action("AT")==TextSelectionAction.Preserve,"@username is an identifier");
        Check("SELECTION","HTTP_URL_PRESERVED",Action("URL")==TextSelectionAction.Preserve,"complete http/https URL remains byte-preserved");
        Check("SELECTION","GENERIC_HASHTAG_TRANSLATED",Action("TAG")==TextSelectionAction.Translate,"semantic hashtag body is translatable while # is retained by prompt contract");
        Check("SELECTION","PERFORMANCE_OVERLAY_PRESERVED",Action("PERF")==TextSelectionAction.Preserve,"FPS/GPU/CPU diagnostic overlay is protected");
        Check("COMPLETENESS","HIDE_TRANSLATABLE",Action("HIDE")==TextSelectionAction.Translate,"compact vertical control reaches Core translation");
        Check("COMPLETENESS","BOTTOM_CONTROL_TRANSLATABLE",Action("BOTTOM")==TextSelectionAction.Translate,"bottom control reaches Core translation");

        var numericRows=new[]
        {
            ("6th-gen","第六代",true,"ordinal localization"),
            ("5,000 defeated","击败五千只",true,"Chinese magnitude equivalence"),
            ("30,000 USA Monsters","击败三万只美国怪物",true,"Chinese magnitude equivalence"),
            ("30,000 USA Monsters","击败3,000只美国怪物",false,"real magnitude loss"),
            ("33/5000 x 2","33/5000 × 2",true,"unchanged Arabic values"),
            ("2026/6/28 11:31","2026/6/28 11:31",true,"date and time preservation"),
            ("31 damage","造成13点伤害",false,"digit transposition"),
            ("G-5YNC","G-SYNC",true,"OCR-confusable identifier correction"),
            ("0C\nMultiple","原创角色\n多人",true,"OCR-confusable OC tag localization"),
            ("v2","v3",false,"real identifier version change"),
            ("B2","B3",false,"real alphanumeric identifier change")
        };
        foreach(var row in numericRows)
        {
            var actual=CorePipelineEngine.NumericTokensMatch(row.Item1,row.Item2);
            Check("NUMERIC",row.Item4,actual==row.Item3,$"source={row.Item1};target={row.Item2};expected={row.Item3};actual={actual}");
        }

        var history=CorePipelineEngine.Analyze(new Size(800,450),new[]
        {
            Raw("H001","Open",40,50,100,24,0),Raw("H002","Settings",40,130,150,24,1)
        });
        CorePipelineEngine.ApplyDeterministicTranslations(history,block=>(true,block.SourceText=="Open"?"打开":"设置",""));
        var snapshot=HistoryCoreSnapshot.Capture(history);var rebuilt=snapshot.Rebuild(history.Canvas);
        Check("HISTORY","CORE_BLOCK_IDS_STABLE",
            history.VisualBlocks.Select(x=>x.BlockId).SequenceEqual(rebuilt.VisualBlocks.Select(x=>x.BlockId)),
            string.Join("|",rebuilt.VisualBlocks.Select(x=>x.BlockId)));
        Check("HISTORY","ACCEPTED_TRANSLATIONS_RESTORED",
            rebuilt.Translations.Values.All(value=>value.State==BlockTranslationState.Accepted),
            $"accepted={rebuilt.Translations.Values.Count(value=>value.State==BlockTranslationState.Accepted)}");

        ProductControlPlaneAuthority.ResetForTests();var tripped=false;
        using(ProductControlPlaneAuthority.Begin("SELF_TEST"))
        {
            try{ProductControlPlaneAuthority.LegacyReached("SemanticGrouping");}
            catch(InvalidOperationException exception){tripped=exception.Message=="LEGACY_PRODUCT_REACHABILITY:SemanticGrouping";}
        }
        var legacy=ProductControlPlaneAuthority.SnapshotLegacyExecutions();
        Check("AUTHORITY","LEGACY_TRIPWIRE",tripped&&legacy.GetValueOrDefault("SemanticGrouping")==1,
            $"tripped={tripped};counter={legacy.GetValueOrDefault("SemanticGrouping")}");
        ProductControlPlaneAuthority.ResetForTests();
        Check("AUTHORITY","PRODUCT_COUNTER_CLEAN",ProductControlPlaneAuthority.SnapshotLegacyExecutions().Count==0,
            "no legacy execution remains after controlled tripwire reset");
        var coreEnvelope=JsonSerializer.Serialize(new{choices=new[]{new{message=new{content="{\"translations\":{\"BLK-A\":\"译文\"},\"allocations\":{\"BLK-A\":[{\"source\":\"legacy\"}]}}"}}}});
        var coreParsed=TranslationService.ParseCoreV2PartialResponseForRecovery(coreEnvelope,
            new HashSet<string>(["BLK-A"],StringComparer.Ordinal),"core-v2-selftest");
        Check("AUTHORITY","CORE_TRANSLATION_BYPASSES_ALLOCATION_V1",
            coreParsed.Translations.GetValueOrDefault("BLK-A")=="译文"&&coreParsed.Allocations.Count==0,
            $"translations={coreParsed.Translations.Count};legacyAllocations={coreParsed.Allocations.Count}");

        foreach(var sample in new[]{"完整中文","English"})
        {
            var scale=CorePipelineCorpusRunner.ResolveTargetLanguageScale(sample);
            using var font=FontManager.CreatePixel("Microsoft YaHei",64*scale,FontStyle.Regular,sourcePixelScale:true);
            using var ink=new System.Drawing.Drawing2D.GraphicsPath();
            ink.AddString(sample,font.FontFamily,(int)font.Style,font.Size,PointF.Empty,StringFormat.GenericTypographic);
            Check("GLYPH_HEIGHT","MEASURED_TARGET_INK_"+sample,Math.Abs(ink.GetBounds().Height-64)<1,$"measured={ink.GetBounds().Height:F2}");
        }

        var routes=new[]
        {
            ("SIMPLE_PANEL",CorePipelineCorpusRunner.SelectBackgroundRestorationRoute(SourceSurfaceClassR2.Flat,.96f,false,.7f,.7f,true),"SURFACE_EVIDENCE_BOUNDARY_DIFFUSION"),
            ("TRANSLUCENT_CHOICE",CorePipelineCorpusRunner.SelectBackgroundRestorationRoute(SourceSurfaceClassR2.Gradient,.84f,true,.8f,.79f,false),"TRANSLUCENT_UI_MINIMAL_GLYPH_DIFFUSION"),
            ("PROVED_SHARED_PANEL",CorePipelineCorpusRunner.SelectBackgroundRestorationRoute(SourceSurfaceClassR2.Flat,.89f,false,.84f,.79f,false,true),"PROVED_LOW_FREQUENCY_PANEL_RESURFACE"),
            ("VERIFIED_HORIZONTAL",CorePipelineCorpusRunner.SelectBackgroundRestorationRoute(SourceSurfaceClassR2.Textured,.86f,false,.91f,.76f,false),"PATTERN_VERIFIED_HORIZONTAL_CONTINUATION"),
            ("LOW_CONFIDENCE_HORIZONTAL_BLOCKED",CorePipelineCorpusRunner.SelectBackgroundRestorationRoute(SourceSurfaceClassR2.Textured,.86f,false,.91f,.76f,false,false,.64f),"UNCERTAIN_GLYPH_LOCAL_DIFFUSION"),
            ("PROFILE_DENSE_FLOW",CorePipelineCorpusRunner.SelectBackgroundRestorationRoute(SourceSurfaceClassR2.Complex,.88f,false,.78f,.77f,false),"UNCERTAIN_GLYPH_LOCAL_DIFFUSION")
        };
        foreach(var route in routes)Check("BACKGROUND",route.Item1,route.Item2==route.Item3,$"route={route.Item2};expected={route.Item3}");
        var corpusSource=File.ReadAllText(Path.Combine(sourceRoot,"CorePipelineV2.CorpusRunner.cs"));
        Check("BACKGROUND","DENSE_FLOW_FULL_RECT_REMOVED",!corpusSource.Contains("DENSE_FLOW_FEATHERED_PANEL_RESURFACE",StringComparison.Ordinal),
            "no dense-flow full-rectangle restoration branch exists in the product renderer");

        var urlRows=new[]
        {
            ("http://example.com",true),("https://example.com/a?b=1",true),("https://example.com/end).",true),
            ("file:///C:/secret.txt",false),("javascript:alert(1)",false),("ftp://example.com",false)
        };
        foreach(var row in urlRows)
        {
            var pass=PreviewForm.TryNormalizeSafeHttpUrl(row.Item1,out var normalized);
            Check("URL",row.Item1,pass==row.Item2,$"accepted={pass};normalized={normalized}");
        }

        var repairRoot=Path.Combine(outputRoot,"repair-isolation");Directory.CreateDirectory(repairRoot);
        var required=Path.Combine(repairRoot,"required-worker.bin");File.WriteAllText(required,"exact-version-component");
        var quarantined=ModelManagerOperations.MoveToQuarantine(required);
        var restored=ModelManagerOperations.RestoreFromQuarantine(required);
        Check("REPAIR","QUARANTINE_EXACT_COPY_RESTORE",quarantined&&restored&&File.ReadAllText(required)=="exact-version-component",
            "honest Check and Restore capability restores the local exact copy");
        Check("PP_S","OPTIONAL_ABSENCE_NOT_REQUIRED",ModelManagerOperations.RequiredStartupComponentsReady(true,true),
            "required startup readiness depends on RapidOCR and hotkey, not PP-S");

        var sourceFiles=new[]{"PreviewForm.cs","MainForm.cs","ModelManagerForm.cs"}.ToDictionary(name=>name,
            name=>File.ReadAllText(Path.Combine(sourceRoot,name)),StringComparer.Ordinal);
        Check("UI","REPAIR_HONESTLY_RENAMED",sourceFiles["ModelManagerForm.cs"].Contains("检查并恢复",StringComparison.Ordinal)&&
            !sourceFiles["ModelManagerForm.cs"].Contains("修复 / 重新安装",StringComparison.Ordinal),"button capability matches label");
        Check("PP_S","NORMAL_UI_HAS_NO_PP_S_ENABLE",sourceFiles["MainForm.cs"].Contains("关闭（产品模式）",StringComparison.Ordinal)&&
            !sourceFiles["MainForm.cs"].Contains("可按需启用 PP-DocLayout-S",StringComparison.Ordinal),"normal settings expose no unusable PP-S enable action");

        WriteCsv(Path.Combine(outputRoot,"CORE-TRIPWIRE.csv"),new[]{"test,pass,evidence"}.Concat(gates.Where(x=>x.Area=="AUTHORITY").Select(Row)));
        WriteCsv(Path.Combine(outputRoot,"HISTORY-CORE.csv"),new[]{"test,pass,evidence"}.Concat(gates.Where(x=>x.Area=="HISTORY").Select(Row)));
        WriteCsv(Path.Combine(outputRoot,"RENDER-COMPLETENESS.csv"),new[]{"test,pass,evidence"}.Concat(gates.Where(x=>x.Area is "COMPLETENESS" or "SELECTION").Select(Row)));
        WriteCsv(Path.Combine(outputRoot,"NUMERIC-FIDELITY.csv"),new[]{"test,pass,evidence"}.Concat(gates.Where(x=>x.Area=="NUMERIC").Select(Row)));
        WriteCsv(Path.Combine(outputRoot,"BACKGROUND-ROUTES.csv"),new[]{"test,pass,evidence"}.Concat(gates.Where(x=>x.Area=="BACKGROUND").Select(Row)));
        WriteCsv(Path.Combine(outputRoot,"CJK-SCALE.csv"),new[]{"test,pass,evidence"}.Concat(gates.Where(x=>x.Area=="CJK").Select(Row)));
        WriteCsv(Path.Combine(outputRoot,"PP-S-STATUS.csv"),new[]{"test,pass,evidence"}.Concat(gates.Where(x=>x.Area=="PP_S").Select(Row)));
        WriteCsv(Path.Combine(outputRoot,"REPAIR-TESTS.csv"),new[]{"test,pass,evidence"}.Concat(gates.Where(x=>x.Area is "REPAIR" or "UI").Select(Row)));
        WriteCsv(Path.Combine(outputRoot,"URL-TESTS.csv"),new[]{"test,pass,evidence"}.Concat(gates.Where(x=>x.Area=="URL").Select(Row)));

        var status=gates.All(gate=>gate.Pass)?"PASS":"FAIL";
        File.WriteAllText(Path.Combine(outputRoot,"FINAL-PRODUCT-FIX-WAVE-SELF-TESTS.json"),JsonSerializer.Serialize(new{Status=status,Gates=gates},new JsonSerializerOptions{WriteIndented=true}));
        return status=="PASS"?0:2;
    }

    private static string Row(Gate gate)=>Csv(gate.Name)+","+(gate.Pass?"PASS":"FAIL")+","+Csv(gate.Evidence);
    private static void WriteCsv(string path,IEnumerable<string> rows)=>File.WriteAllLines(path,rows);
    private static string Csv(string value)=>"\""+value.Replace("\"","\"\"")+"\"";
    private static RawOcrLine Raw(string id,string text,float x,float y,float width,float height,int order)
    {
        var bounds=new RectangleF(x,y,width,height);
        return new(id,text,text,[new(bounds.Left,bounds.Top),new(bounds.Right,bounds.Top),new(bounds.Right,bounds.Bottom),new(bounds.Left,bounds.Bottom)],bounds,.99f,order);
    }
}
