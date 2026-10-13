using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

public static class SourceStyleBundleSelfTests
{
    private sealed record TestRow(string Name,string Status,string Detail);

    public static int Run(string outputPath,string? realFixtureDirectory)
    {
        Directory.CreateDirectory(outputPath);
        var rows=new List<TestRow>();
        void Test(string name,Action body)
        {
            try{body();rows.Add(new(name,"PASS",""));Console.WriteLine("PASS "+name);}
            catch(Exception ex){rows.Add(new(name,"FAIL",ex.Message));Console.WriteLine("FAIL "+name+": "+ex.Message);}
        }
        static void Assert(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
        static SourceStyleBundle S(Color fill,Color outline,float width,RegionRoleType role=RegionRoleType.BodyParagraph,float confidence=.9f)=>
            new(fill,outline,width,Color.Transparent,PointF.Empty,0,fill.A,FontStyle.Regular,
                SourceStyleBundleOwner.ClassifyPolarity(fill),role,confidence,"TestSource","Synthetic");

        Test("Light fill and dark outline retain light polarity",()=>
        {
            var source=S(Color.FromArgb(242,242,238),Color.FromArgb(20,20,22),1.6f);
            var d=SourceStyleLegibilityGuard.Evaluate(source,Color.FromArgb(224,221,218));
            Assert(d.Effective.Polarity==SourceStylePolarity.Light&&!d.FillPolarityChanged,"light source fill was inverted");
            Assert(d.Effective.FillColor.R>=230&&d.CombinedReadability>=4.5,"light fill/outline bundle was not accepted");
        });
        Test("Dark fill and light outline retain dark polarity",()=>
        {
            var source=S(Color.FromArgb(19,21,24),Color.FromArgb(245,245,242),1.5f);
            var d=SourceStyleLegibilityGuard.Evaluate(source,Color.FromArgb(31,34,39));
            Assert(d.Effective.Polarity==SourceStylePolarity.Dark&&!d.FillPolarityChanged,"dark source fill was inverted");
            Assert(d.CombinedReadability>=4.5,"dark fill/light outline bundle was not accepted");
        });
        Test("Chromatic fill hue survives guard",()=>
        {
            var source=S(Color.FromArgb(232,112,84),Color.FromArgb(47,22,18),1.25f,RegionRoleType.Title);
            var d=SourceStyleLegibilityGuard.Evaluate(source,Color.FromArgb(202,95,72));
            Assert(d.Effective.Polarity==SourceStylePolarity.Chromatic&&!d.FillPolarityChanged,"chromatic role was flattened");
            Assert(Math.Abs(d.Effective.FillColor.R-source.FillColor.R)<=30,"chromatic fill moved outside bounded range");
        });
        Test("Readable flat dark control remains unchanged",()=>
        {
            var source=S(Color.FromArgb(18,18,18),Color.Transparent,0);
            var d=SourceStyleLegibilityGuard.Evaluate(source,Color.White);
            Assert(d.Adjustment=="SOURCE_BUNDLE_UNCHANGED"&&d.Effective.FillColor.ToArgb()==source.FillColor.ToArgb()&&!d.Effective.HasOutline,"negative control changed");
        });
        Test("Low contrast improves fill before adding edge support",()=>
        {
            var source=S(Color.FromArgb(128,128,128),Color.Transparent,0);
            var d=SourceStyleLegibilityGuard.Evaluate(source,Color.FromArgb(137,137,137));
            Assert(d.Adjustment.StartsWith("BOUNDED_LUMINANCE_FIRST",StringComparison.Ordinal),"fill was not the first adjustment");
            Assert(!d.FillPolarityChanged,"high-confidence source polarity was not preserved");
        });
        Test("Ordinary low-contrast text receives muted support, not a bright halo",()=>
        {
            var source=S(Color.FromArgb(20,17,13),Color.FromArgb(60,49,5),3.1f,RegionRoleType.BodyParagraph,.57f);
            var d=SourceStyleLegibilityGuard.Evaluate(source,Color.FromArgb(83,68,6));
            Assert(d.Effective.OutlineColor.R<220&&d.Effective.OutlineColor.G<220&&d.Effective.OutlineColor.B<220,
                $"ordinary support is still near-white: {SourceStyleBundleOwner.Hex(d.Effective.OutlineColor)}");
            Assert(d.Effective.OutlineWidth<=2.01f,"ordinary support remained excessively thick");
        });
        Test("Neutral grey CJK body panel avoids a bright thick halo",()=>
        {
            var source=S(Color.FromArgb(70,73,73),Color.Transparent,0,RegionRoleType.BodyParagraph,.57f);
            var d=SourceStyleLegibilityGuard.Evaluate(source,Color.FromArgb(61,71,74));
            Assert(TranslationTextColorResolver.Contrast(d.Effective.FillColor,Color.FromArgb(61,71,74))>=4.5,
                "neutral-panel fill remained unreadable");
            Assert(d.Effective.OutlineColor.A<=145,
                $"neutral-panel support remained too bright: {SourceStyleBundleOwner.Hex(d.Effective.OutlineColor)}");
            Assert(d.Effective.OutlineWidth<=1.151f,"neutral-panel support exceeded the ordinary-text cap");
        });
        Test("Final font-relative outline cap protects ordinary CJK",()=>
        {
            var source=S(Color.White,Color.Black,3.1f,RegionRoleType.Dialogue,.9f);
            var initial=SourceStyleLegibilityGuard.Evaluate(source,Color.FromArgb(92,92,92));
            var final=SourceStyleLegibilityGuard.FinalizeForFontSize(initial,Color.FromArgb(92,92,92),18);
            Assert(final.Effective.OutlineWidth<=.811f,"ordinary outline exceeds font-relative cap");
            Assert(final.Effective.OutlineColor.A<=165,"ordinary outline alpha exceeds restrained cap");
        });
        Test("Explicit user fill remains authoritative",()=>
        {
            var source=S(Color.White,Color.Black,1.2f);
            var explicitBundle=SourceStyleBundleOwner.WithExplicitSettings(source,new RenderSettings{TextColorMode=TextColorMode.Custom,CustomTextColor=Color.Gold});
            var d=SourceStyleLegibilityGuard.Evaluate(explicitBundle,Color.FromArgb(245,235,190));
            Assert(d.Source.Owner=="UserTextColorOverride"&&d.Effective.FillColor.ToArgb()==Color.Gold.ToArgb(),"explicit setting was replaced");
        });
        Test("Three deterministic replays are byte identical",()=>
        {
            var source=S(Color.FromArgb(236,239,244),Color.FromArgb(14,16,20),1.4f,RegionRoleType.Choice);
            var hashes=Enumerable.Range(0,3).Select(_=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(SourceStyleLegibilityGuard.Evaluate(source,Color.FromArgb(210,214,220)).Effective))))).ToArray();
            Assert(hashes.Distinct(StringComparer.Ordinal).Count()==1,"style decision is non-deterministic");
            File.WriteAllLines(Path.Combine(outputPath,"DETERMINISM-HASHES.txt"),hashes);
        });
        Test("Owner and guard stay within performance gate",()=>
        {
            using var image=CreateSynthetic(Color.FromArgb(54,57,62),Color.FromArgb(247,247,244),Color.FromArgb(18,18,20));
            var region=Region(new RectangleF(30,46,340,62),RegionRoleType.Choice,"translated");
            var ticks=new long[1200];
            for(var i=0;i<ticks.Length;i++){var t=Stopwatch.GetTimestamp();var b=SourceStyleBundleOwner.Create(image,region);_ = SourceStyleLegibilityGuard.Evaluate(b,Color.FromArgb(230,230,226));ticks[i]=Stopwatch.GetTimestamp()-t;}
            var ms=ticks.Select(x=>x*1000d/Stopwatch.Frequency).OrderBy(x=>x).ToArray();var mean=ms.Average();var p90=ms[(int)Math.Floor((ms.Length-1)*.9)];
            File.WriteAllText(Path.Combine(outputPath,"STYLE-BUNDLE-PERFORMANCE.json"),JsonSerializer.Serialize(new{Iterations=ticks.Length,MeanMs=mean,P90Ms=p90,GateMeanMs=2,GateP90Ms=5},new JsonSerializerOptions{WriteIndented=true}));
            Assert(mean<2&&p90<5,$"performance mean={mean:F3} p90={p90:F3}");
        });
        Test("Product renderer consumes complete source bundle",()=>
        {
            using var source=CreateSynthetic(Color.FromArgb(54,57,62),Color.FromArgb(248,248,244),Color.FromArgb(16,16,18));
            var region=Region(new RectangleF(30,46,340,62),RegionRoleType.Choice,"浅色译文保持浅色");
            var result=RegionRendererV2.Render(source,[region],new RenderSettings{MinFontSize=10,BackgroundStrategy=TranslationOverlayBackgroundStyle.Automatic});
            using var rendered=result.Bitmap;rendered.Save(Path.Combine(outputPath,"PRODUCT-LIGHT-FILL-DARK-OUTLINE.png"));
            var d=result.Diagnostics!.Single();
            Assert(d.AtomicRegionCommitted&&d.SourceStyleOwner.Length>0,"product path did not own a style bundle");
            Assert(d.SourcePolarity==SourceStylePolarity.Light.ToString()&&!d.FillPolarityChanged,$"product fill inverted: {d.SourceFillColor} -> {d.TextColor}");
            Assert(d.CombinedReadability>=3,"product composite readability missing");
        });

        if(!string.IsNullOrWhiteSpace(realFixtureDirectory))
            Test("Real screenshot light-fill controls retain polarity",()=>RunRealFixture(realFixtureDirectory!,outputPath,Assert));

        File.WriteAllText(Path.Combine(outputPath,"SOURCE-STYLE-BUNDLE-SELFTEST.json"),JsonSerializer.Serialize(new{
            Contract="SOURCE_STYLE_BUNDLE_PHASE_A_V1",Pass=rows.Count(x=>x.Status=="PASS"),Fail=rows.Count(x=>x.Status=="FAIL"),Rows=rows
        },new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllLines(Path.Combine(outputPath,"SOURCE-STYLE-BUNDLE-SELFTEST.txt"),rows.Select(x=>$"{x.Status}\t{x.Name}\t{x.Detail}"));
        return rows.Any(x=>x.Status=="FAIL")?1:0;
    }

    private static void RunRealFixture(string fixture,string output,Action<bool,string> assert)
    {
        var sourcePath=Path.Combine(fixture,"SOURCE.png");var restoredPath=Path.Combine(fixture,"06-BACKGROUND-RESTORED.png");var replayPath=Path.Combine(fixture,"PRODUCT-OCR-REPLAY.json");
        assert(File.Exists(sourcePath)&&File.Exists(restoredPath)&&File.Exists(replayPath),"real fixture evidence is incomplete");
        using var source=new Bitmap(sourcePath);using var restored=new Bitmap(restoredPath);
        var replay=JsonSerializer.Deserialize<OcrEngineResult>(File.ReadAllText(replayPath),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??throw new InvalidOperationException("OCR replay parse failed");
        var decisions=new List<object>();var positive=0;
        foreach(var block in replay.Blocks.Where(x=>x.Enabled&&x.Polygon.Length>=3))
        {
            var region=Region(block.BoundingBox,RegionRoleType.Choice,"译文",block.Id);region.Polygon=block.Polygon;region.SourceLinePolygons=[block.Polygon];region.SourceBlockIds=[block.Id];region.DetectedBlocks=[block];
            var bundle=SourceStyleBundleOwner.Create(source,region);var b=GeometryV2.Bounds(block.Polygon);var clean=BackgroundEstimator.Estimate(restored,b).Color;var d=SourceStyleLegibilityGuard.Evaluate(bundle,clean);
            var lightDark=bundle.Polarity==SourceStylePolarity.Light&&bundle.HasOutline&&SourceStyleBundleOwner.ClassifyPolarity(bundle.OutlineColor)==SourceStylePolarity.Dark;
            if(lightDark){positive++;assert(!d.FillPolarityChanged&&d.Effective.Polarity==SourceStylePolarity.Light,"real light fill was inverted");}
            decisions.Add(new{block.Id,Bounds=b,SourceFill=SourceStyleBundleOwner.Hex(bundle.FillColor),SourceOutline=SourceStyleBundleOwner.Hex(bundle.OutlineColor),bundle.OutlineWidth,bundle.Polarity,bundle.Confidence,bundle.Evidence,CleanBackground=SourceStyleBundleOwner.Hex(clean),d.CombinedReadability,d.Adjustment,d.FillPolarityChanged,PositiveControl=lightDark});
        }
        File.WriteAllText(Path.Combine(output,"REAL-FIXTURE-STYLE-DECISIONS.json"),JsonSerializer.Serialize(new{PositiveControls=positive,Decisions=decisions},new JsonSerializerOptions{WriteIndented=true}));
        assert(positive>=3,$"only {positive} light-fill/dark-outline positive controls were discovered from source pixels");
    }

    private static Bitmap CreateSynthetic(Color background,Color fill,Color outline)
    {
        var bitmap=new Bitmap(420,160);using var g=Graphics.FromImage(bitmap);g.Clear(background);using var font=new Font("Arial",34,FontStyle.Bold,GraphicsUnit.Pixel);
        SourceStyleTextDrawingR2.Draw(g,"SOURCE STYLE",font,new PointF(34,51),fill,true,outline,1.6f,false,Color.Transparent);
        return bitmap;
    }
    private static RecognitionRegion Region(RectangleF bounds,RegionRoleType role,string translation,string id="R")
    {
        var polygon=GeometryV2.RectanglePolygon(bounds);var block=new OcrEngineBlock{Id="B",RawText="source",CorrectedText="source",BoundingBox=bounds,Polygon=polygon,Enabled=true};
        return new RecognitionRegion{RegionId=id,Polygon=polygon,SourceBlockIds=[block.Id],DetectedBlocks=[block],SourceLinePolygons=[polygon],OcrText="source",CorrectedText="source",StructuredText="source",TranslationText=translation,RoleType=role,RoleConfidence=1,RecognitionConfidence=1,ReadingOrder=1,CoverageValid=true};
    }
}
