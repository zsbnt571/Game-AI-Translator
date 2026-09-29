using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

internal static class NativeVisualLayoutSelfTests
{
    internal static int Run(string outputPath, string? realFixtureRoot = null, string? extraSourceManifest = null)
    {
        Directory.CreateDirectory(outputPath);
        var rows = new List<object>(); var failures = 0;
        void Test(string name, Action body)
        {
            try { body(); rows.Add(new { Name = name, Status = "PASS", Detail = "" }); }
            catch (Exception ex) { failures++; rows.Add(new { Name = name, Status = "FAIL", Detail = ex.Message }); }
        }
        static void Assert(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        const string longText = "简介绝对不是奥菲莉亚自己写的";
        var bounds = new RectangleF(1000, 620, 130, 16);
        var block = Block("note", bounds, "An independent attribution note");
        NativeVisualLayoutPlan Plan(Bitmap image, string text = longText, IReadOnlyList<VisualBlock>? blocks = null)
        {
            using var g = Graphics.FromImage(image);
            return NativeVisualLayout.Plan(g, image, block, blocks ?? [block], text, "Microsoft YaHei UI", FontStyle.Regular, 12.672f, "Left");
        }
        Test("Long Chinese uses two readable lines before the illustration barrier", () =>
        {
            using var source = Paper(); using (var g = Graphics.FromImage(source)) g.FillRectangle(Brushes.Black, 982, 555, 15, 90);
            var originalHash = PixelHash(source); var result = Plan(source);
            Assert(result.Applied && result.Lines.Count == 2 && result.FontSize >= 12, JsonSerializer.Serialize(result));
            Assert(string.Concat(result.Lines.Select(x => x.Text)) == longText, "Long accepted content changed");
            Assert(result.Lines.Max(x=>x.Bounds.Right) >= bounds.Right - 4 && result.SafeLineRects.Last().Bottom <= bounds.Bottom, "Paragraph trailing/bottom anchor drift");
            Assert(result.Lines.Max(x=>x.Bounds.Left)-result.Lines.Min(x=>x.Bounds.Left)<.1f, "Staircase paragraph returned");
            Assert(result.SafeLineRects.All(x => x.Left > 997 && x.Bottom <= bounds.Bottom), "Illustration barrier crossed");
            Assert(PixelHash(source) == originalHash, "Planner mutated source pixels");
            SavePlan(source, result, outputPath, "SYNTHETIC-LONG-TWO-LINE");
        });
        Test("Short footnote stays complete without a second line", () =>
        {
            using var source = Paper(); var result = Plan(source, "并非作者本人所写");
            Assert(result.Applied && result.Lines.Count == 1 && result.FontSize >= 12, result.Reason);
        });
        Test("Dark illustration blocks every readable extension", () =>
        {
            using var source = Paper(); using (var g = Graphics.FromImage(source))
            { g.FillRectangle(Brushes.Black, 780, 555, 218, 85); g.FillRectangle(Brushes.Black, 997, 550, 145, 69); }
            var result = Plan(source);
            Assert(!result.Applied, "Unsafe area admitted a long translated note");
        });
        Test("Source metadata field is not promoted to a footnote", () =>
        {
            using var source = Paper(); using var g = Graphics.FromImage(source);
            var field = Block("field", bounds, "Dislikes: rain and cold weather");
            var result = NativeVisualLayout.Plan(g, source, field, [field], longText, "Microsoft YaHei UI", FontStyle.Regular, 13, "Left");
            Assert(!result.Candidate && !result.Applied, "A metadata field changed owner");
        });
        Test("Neighbor text prevents detached-footnote ownership", () =>
        {
            using var source = Paper(); var neighbor = Block("other", new RectangleF(1005, 599, 120, 14), "Another line");
            Assert(!Plan(source, longText, [block, neighbor]).Candidate, "Adjacent paragraph line was treated as independent");
        });
        Test("Dark or chromatic source surfaces stay in their current owner", () =>
        {
            using var source = Paper(); using (var g = Graphics.FromImage(source)) g.Clear(Color.FromArgb(45, 55, 95));
            Assert(!Plan(source).Candidate, "Dark/chromatic surface qualified as white paper");
        });
        Test("Overflow preserves full input and refuses tiny or clipped output", () =>
        {
            using var source = Paper(); var text = string.Concat(Enumerable.Repeat(longText, 8)); var result = Plan(source, text);
            Assert(result.Candidate && !result.Applied && result.Lines.Count == 0 && result.LayoutInput == text, "Overflow was silently shortened or shrunk");
        });
        Test("Multi-line body center alignment is left with the existing owner", () =>
        {
            using var source = Paper(); using var g = Graphics.FromImage(source);
            var line2 = Block("second", new RectangleF(1000, 641, 130, 16), "Second body line").Lines[0];
            var body = new VisualBlock { BlockId = "body", Bounds = new(1000, 620, 130, 37), Lines = [block.Lines[0], line2], LayoutBehavior = BlockLayoutBehavior.Flow };
            var result = NativeVisualLayout.Plan(g, source, body, [body], longText, "Microsoft YaHei UI", FontStyle.Regular, 13, "Center");
            Assert(!result.Candidate && result.Alignment == "Center", "Existing body center alignment changed");
        });
        VisualBlock Paragraph(float[] widths, bool centered, float slope = 0, bool indent = false)
        {
            var lines=widths.Select((width,i)=>Block("P"+i,
                new RectangleF((centered?500-width/2:100)+slope*i*30+(indent&&i==0?32:0),100+i*30,width,24),
                "A source prose row with words").Lines[0]).ToArray();
            return new VisualBlock { BlockId="paragraph",Lines=lines,Bounds=lines.Select(l=>l.Bounds).Aggregate(RectangleF.Union),
                LayoutBehavior=BlockLayoutBehavior.Flow,RoleHint="PossibleFlowText" };
        }
        Test("Ragged left prose and a short tail do not become centered", () =>
        {
            var result=ParagraphAlignmentEvidence.Observe(Paragraph([600,650,640,605,620,280],false));
            Assert(result?.Alignment=="Left" && result.Slope==0,"Left paragraph evidence lost");
        });
        Test("Centered prose retains its common center", () =>
        {
            var result=ParagraphAlignmentEvidence.Observe(Paragraph([600,350,640,410,620,280],true));
            Assert(result?.Alignment=="Center" && Math.Abs(result.CenterAt(100)-500)<.1f,"Centered paragraph forced left");
        });
        Test("Slanted left prose is distinguished from centered text", () =>
        {
            var result=ParagraphAlignmentEvidence.Observe(Paragraph([600,350,640,410,620,280],false,.4f));
            Assert(result?.Alignment=="Left" && Math.Abs(result.LeftAt(160)-124)<.1f,"Perspective mistaken for centering");
        });
        Test("An indented first row does not move the whole body", () =>
        {
            var result=ParagraphAlignmentEvidence.Observe(Paragraph([600,650,640,605,620,280],false,0,true));
            Assert(result?.Alignment=="Left" && Math.Abs(result.LeftAt(100)-100)<.1f,"First-row indent moved the body");
        });
        Test("Oblique source edges alone cannot overturn a centered card presentation", () =>
        {
            Assert(ParagraphAlignmentEvidence.Observe(Paragraph([600,350,640,410,620,280],false,.4f),"Center") is null,
                "Perspective evidence overrode established centered presentation");
        });
        Test("Ambiguous equal-width rows and controls retain the existing hint", () =>
        {
            Assert(ParagraphAlignmentEvidence.Observe(Paragraph([600,600,600,600],false)) is null,"Invented alignment with no distinguishing evidence");
            var body=Paragraph([600,350,640,410,620,280],false);body.RoleHint="PossibleControl";
            Assert(ParagraphAlignmentEvidence.Observe(body) is null,"Control handled as prose");
        });
        Test("Prose owns alignment independently of a shared label style", () =>
        {
            var sentence=Block("sentence",new(100,100,900,30),"A long source sentence uses its own observed placement rather than a short label's alignment.");
            Assert(ParagraphAlignmentEvidence.OwnsProseAlignment(sentence),"Long sentence inherited label alignment");
            var label=Block("label",new(100,100,120,30),"Open settings");
            Assert(!ParagraphAlignmentEvidence.OwnsProseAlignment(label),"Short label lost its owner");
            sentence.RoleHint="PossibleControl";
            Assert(!ParagraphAlignmentEvidence.OwnsProseAlignment(sentence),"Control ownership changed");
        });
        IReadOnlyDictionary<string,UiLabelAlignmentEvidence> Labels(VisualBlock[] labels, bool mixedSurface=false,
            bool gradedSurface=false)
        {
            var styles=labels.Select((b,i)=>new SourceStyleEvidenceR2(b.BlockId,[b.BlockId],b.Bounds,
                SourceVisualRoleR2.Control,Color.Black.ToArgb(),"#000000",.9f,
                (gradedSurface?Color.FromArgb(230-i*20,230-i*20,230-i*20):
                    mixedSurface&&i==1?Color.Blue:Color.Beige).ToArgb(),"synthetic",.9f,
                SourcePolarityR2.DarkOnLight,false,0,"",0,0,false,0,"",0,SourceWeightR2.Regular,
                SourceRelativeSizeR2.Small,"Center",b.Bounds.Height,0,0,4,"Synthetic",[]))
                .ToDictionary(x=>x.BlockId,StringComparer.Ordinal);
            return UiLabelAlignmentEvidence.Analyze(labels,styles);
        }
        Test("Different-width icon labels keep the common source left edge at multiple scales", () =>
        {
            foreach(var scale in new[]{.5f,1f,2f})
            {
                var labels=new[]{120,200,65}.Select((width,i)=>Block("L"+i,
                    new(130*scale,(100+i*70)*scale,width*scale,20*scale),"Independent label "+i)).ToArray();
                var plans=Labels(labels);
                Assert(plans.Count==3 && plans.Values.All(x=>x.Alignment=="Left" && x.SourceLeft==130*scale),
                    "Left column changed with rendering scale");
            }
        });
        Test("Centered buttons, equal-width rows and different columns retain their existing alignment", () =>
        {
            var centered=new[]{120,200,65}.Select((width,i)=>Block("C"+i,
                new(300-width/2f,100+i*70,width,20),"Centered control "+i)).ToArray();
            Assert(Labels(centered).Count==0,"Centered controls forced left");
            var equal=Enumerable.Range(0,3).Select(i=>Block("E"+i,new(100,100+i*70,140,20),"Equal width")).ToArray();
            Assert(Labels(equal).Count==0,"Ambiguous equal-width controls reclassified");
            var columns=new[]{Block("A",new(100,100,140,20),"First column"),Block("B",new(400,170,80,20),"Second column")};
            Assert(Labels(columns).Count==0,"Different columns borrowed alignment");
        });
        Test("Unrelated surfaces and horizontal tab rows do not form a label column", () =>
        {
            var labels=new[]{Block("A",new(100,100,200,20),"First label"),Block("B",new(100,170,80,20),"Second label")};
            Assert(Labels(labels,true).Count==0,"Foreign material contributed alignment evidence");
            var tabs=new[]{Block("T1",new(100,100,200,20),"First tab"),Block("T2",new(101,110,80,20),"Second tab")};
            Assert(Labels(tabs).Count==0,"Overlapping horizontal rows formed a list");
        });
        Test("A continuous material gradient retains the same column at its last row", () =>
        {
            var labels=new[]{200,65,70}.Select((width,i)=>Block("G"+i,
                new(100,100+i*70,width,20),"Gradient label "+i)).ToArray();
            Assert(Labels(labels,gradedSurface:true).Count==3,"Coherent shading fragmented one source column");
        });
        Test("System word preferences preserve generic Chinese words and caller-owned names", () =>
        {
            const string text="物品每次都会刷新，并会奖励你。";
            var breaks=PreferredWordBreaks.Create(text);
            Assert(breaks.Offsets.Contains(0) && breaks.Offsets.Contains(text.Length),"Missing terminal boundaries");
            if(breaks.Engine=="WINDOWS_ICU_WORD_DICTIONARY")
                Assert(!breaks.Offsets.Contains(1) && !breaks.Offsets.Contains(text.IndexOf("奖励",StringComparison.Ordinal)+1),
                    "Dictionary split an ordinary two-character word");
            var named=PreferredWordBreaks.Create("这是测试姓名本人所写",["测试姓名"]);
            Assert(!named.Offsets.Contains(3) && !named.Offsets.Contains(4) && !named.Offsets.Contains(5),
                "Accepted context term was split internally");
            var unicode=PreferredWordBreaks.Create("完整署名𠮷e\u0301");
            var legal=System.Globalization.StringInfo.ParseCombiningCharacters("完整署名𠮷e\u0301").Append("完整署名𠮷e\u0301".Length).ToHashSet();
            Assert(unicode.Offsets.All(legal.Contains),"A boundary bisected a grapheme");
        });
        Test("Context name stays together when a complete safe footnote fit exists", () =>
        {
            using var source=Paper();using var g=Graphics.FromImage(source);
            const string text="这份简介并非测试姓名本人所写";
            var result=NativeVisualLayout.Plan(g,source,block,[block],text,"Microsoft YaHei UI",FontStyle.Regular,
                12.672f,"Left",["测试姓名"]);
            Assert(result.Applied && result.Lines.Any(x=>x.Text.Contains("测试姓名")) &&
                string.Concat(result.Lines.Select(x=>x.Text))==text,"Safe whole-name rendering lost");
        });
        Test("Explicit newline and supplementary characters remain intact", () =>
        {
            using var source = Paper(); var text = "作者并非此人\n完整署名𠮷"; var result = Plan(source, text);
            Assert(result.Applied && string.Concat(result.Lines.Select(x => x.Text)) == text.Replace("\n", ""), "Unicode text element lost");
        });
        Test("Footnote material rejects icon, dark paper and intersecting text", () =>
        {
            using var source = Paper(); var plan = Plan(source);
            using (var g = Graphics.FromImage(source)) g.FillRectangle(Brushes.Black, 1030, 624, 6, 6);
            Assert(!NativeFootnoteMaterial.Analyze(source, block, [block], plan).Applied, "An isolated icon was admitted as text");
            using (var g = Graphics.FromImage(source)) g.Clear(Color.FromArgb(50, 50, 50));
            Assert(!NativeFootnoteMaterial.Analyze(source, block, [block], plan).Applied, "Dark paper was erased");
            var neighbor = Block("neighbor", bounds, "Other owner");
            Assert(!NativeFootnoteMaterial.Analyze(source, block, [block, neighbor], plan).Applied, "An overlapping source owner was erased");
        });

        Test("Clipped exterior speck is protected while interior punctuation remains material", () =>
        {
            using var source = Paper(); var plan = Plan(source);
            using (var g = Graphics.FromImage(source))
                for (var i=0;i<10;i++) g.FillRectangle(Brushes.Black,1004+i*12,624,3,4);
            var edge = new Point((int)bounds.Left,(int)bounds.Bottom-1);
            source.SetPixel(edge.X,edge.Y,Color.Black);
            source.SetPixel(edge.X-1,edge.Y,Color.Black);
            source.SetPixel(edge.X,edge.Y+1,Color.Black);
            var punctuation = new Point(1010,633); source.SetPixel(punctuation.X,punctuation.Y,Color.Black);
            var material=NativeFootnoteMaterial.Analyze(source,block,[block],plan);
            Assert(material.Applied,"Distributed glyph sample declined");
            Assert(!material.AllPixels.Contains(edge),"Exterior art fragment gained cleanup authority");
            Assert(material.CorePixels.Contains(punctuation),"Interior punctuation was removed from glyph evidence");
            Assert(material.Components.Any(c=>c.Reason=="CLIPPED_EXTERIOR_MATERIAL_REJECTED"),"Missing exclusion evidence");
        });

        if (!string.IsNullOrWhiteSpace(realFixtureRoot))
        {
            foreach (var id in new[] { "UAT-OPHELIA-CURRENT", "UAT-OPHELIA-OLDER" })
                Test("Actual C3 long accepted replay: " + id, () => RealFixture(realFixtureRoot!, id, outputPath, Assert));
        }
        if (!string.IsNullOrWhiteSpace(extraSourceManifest))
            Test("Actual tight OCR and polluted paper context controls", () => TightFixture(extraSourceManifest!, outputPath, Assert));
        foreach (var family in new[] { "Arial", "Times New Roman" })
        foreach (var style in new[] { FontStyle.Regular, FontStyle.Bold })
        foreach (var size in new[] { 18, 28, 42, 60 })
            Test("Solid source weight calibration " + family + " " + style + " " + size, () =>
                WeightCalibration(outputPath, family, style, size, Assert));
        if (!string.IsNullOrWhiteSpace(realFixtureRoot))
            Test("Actual Ara solid-white source body weight", () => RealWeight(realFixtureRoot!, outputPath, Assert));
        NativeCardLayoutSelfTests.Run(outputPath,Test);
        File.WriteAllText(Path.Combine(outputPath, "NATIVE-VISUAL-LAYOUT-SELFTEST.json"), JsonSerializer.Serialize(new
        { Contract = "NATIVE_FOOTNOTE_GEOMETRY_V1", Pass = rows.Count - failures, Fail = failures, Rows = rows }, new JsonSerializerOptions { WriteIndented = true }));
        return failures == 0 ? 0 : 1;
    }

    private static void RealFixture(string fixtureRoot, string id, string output, Action<bool, string> assert)
    {
        var root = Path.Combine(fixtureRoot, id);
        using var source = new Bitmap(Path.Combine(root, "SOURCE.png"));
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var ocr = JsonSerializer.Deserialize<OcrEngineResult>(File.ReadAllText(Path.Combine(root, "PRODUCT-OCR-REPLAY.json")), options)!;
        var raw = ocr.Blocks.Where(x => x.Enabled && x.Polygon.Length >= 3).Select((x, i) => new RawOcrLine(
            x.Id, x.RawText, x.CorrectedText, x.Polygon, x.BoundingBox, x.Confidence ?? 0, i)).ToArray();
        var document = CorePipelineEngine.Analyze(source.Size, raw);
        using var g = Graphics.FromImage(source);
        var accepted = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "TEXT-FIT-TRACE.json")));
        var results = new List<object>(); var hits = 0;
        foreach (var block in document.VisualBlocks)
        {
            var trace = accepted.RootElement.EnumerateArray().FirstOrDefault(x => x.GetProperty("BlockId").GetString() == block.BlockId);
            if (trace.ValueKind == JsonValueKind.Undefined) continue;
            var text = trace.GetProperty("AcceptedMapping").GetString() ?? "";
            var preferred = trace.GetProperty("PreferredFontSize").GetSingle();
            var result = NativeVisualLayout.Plan(g, source, block, document.VisualBlocks, text, "Microsoft YaHei UI", FontStyle.Regular, preferred, "Left");
            if (!result.Candidate) continue;
            hits++;
            File.WriteAllText(Path.Combine(output, id + "-LAST-PLAN.json"), JsonSerializer.Serialize(result, options));
            assert(result.Applied, id + ": " + result.Reason);
            assert(result.FontSize >= 12 && result.Lines.Count <= 2 && result.CharactersPreserved, "Readable complete fit failed");
            results.Add(new { block.BlockId, block.SourceText, Accepted = text, Plan = result });
            SavePlan(source, result, output, id);
            VerifyRasterSafety(source,block,document.VisualBlocks,result,output,id,assert);
            var material = NativeFootnoteMaterial.Analyze(source, block, document.VisualBlocks, result);
            File.WriteAllText(Path.Combine(output, id + "-MATERIAL.json"), JsonSerializer.Serialize(material, options));
            assert(material.Applied, id + " material: " + material.Reason);
            assert(material.AllPixels.All(p => block.Bounds.Contains(p.X, p.Y)), "Material expanded outside source bounds");
            using (var erase = source.Clone(new Rectangle(Point.Empty,source.Size),PixelFormat.Format32bppArgb))
            using (var mask = new Bitmap(source.Width, source.Height))
            {
                foreach (var pixel in material.AllPixels) { erase.SetPixel(pixel.X, pixel.Y, material.Background); mask.SetPixel(pixel.X, pixel.Y, Color.Red); }
                var roi = Rectangle.Intersect(Rectangle.Ceiling(RectangleF.Inflate(block.Bounds, 15, 15)), new Rectangle(Point.Empty, source.Size));
                using var crop = erase.Clone(roi, PixelFormat.Format32bppArgb); crop.Save(Path.Combine(output, id + "-MATERIAL-ERASE-PROOF.png"), ImageFormat.Png);
                using var maskCrop = mask.Clone(roi, PixelFormat.Format32bppArgb); maskCrop.Save(Path.Combine(output, id + "-MATERIAL-MASK.png"), ImageFormat.Png);
            }
            foreach (var variant in new[] { (Id: "C3-LONG", Text: "简介绝对不是奥菲莉亚自己写的"),
                (Id: "USER-LONG-PUNCTUATED", Text: "简介绝对不是奥菲莉亚自己写的。"),
                (Id: "CONTROLLED-FULL17", Text: "这篇简介绝对不是奥菲莉亚本人写的。"),
                (Id: "CONTROLLED-FULL18", Text: "这篇简介绝对不是奥菲莉亚本人写下的。") })
            {
                var extra = NativeVisualLayout.Plan(g, source, block, document.VisualBlocks, variant.Text,
                    "Microsoft YaHei UI", FontStyle.Regular, preferred, "Left");
                SavePlan(source, extra, output, id + "-" + variant.Id);
                assert(extra.Applied && extra.CharactersPreserved, id + " " + variant.Id + ": " + extra.Reason);
                VerifyRasterSafety(source,block,document.VisualBlocks,extra,output,id+"-"+variant.Id,assert);
            }
        }
        assert(hits == 1, id + " candidate count: " + hits);
        File.WriteAllText(Path.Combine(output, id + "-PLAN.json"), JsonSerializer.Serialize(results, options));
    }

    private static void TightFixture(string manifestPath, string output, Action<bool, string> assert)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var input = manifest.RootElement.EnumerateArray().Single(x => x.GetProperty("Id").GetString() == "D-OPHELIA");
        using var source = new Bitmap(input.GetProperty("SourcePath").GetString()!);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var ocr = JsonSerializer.Deserialize<OcrEngineResult>(File.ReadAllText(input.GetProperty("OcrPath").GetString()!), options)!;
        var document = CorePipelineEngine.Analyze(source.Size, ocr.Blocks.Where(x => x.Enabled && x.Polygon.Length >= 3).Select((x, i) =>
            new RawOcrLine(x.Id, x.RawText, x.CorrectedText, x.Polygon, x.BoundingBox, x.Confidence ?? 0, i)).ToArray());
        using var g = Graphics.FromImage(source);
        const string text = "这篇简介绝对不是奥菲莉亚本人写下的。";
        var candidates = document.VisualBlocks.Select(block => (Block:block, Plan:NativeVisualLayout.Plan(g, source, block, document.VisualBlocks, text, "Microsoft YaHei UI", FontStyle.Regular, 9, "Left"))).Where(x => x.Plan.Candidate).ToArray();
        assert(candidates.Length == 1, "Expected exactly one actual tight footnote");
        var (block,plan) = candidates[0];
        File.WriteAllText(Path.Combine(output,"D-OPHELIA-TRUE18-PLAN.json"),JsonSerializer.Serialize(plan,options));
        assert(plan.Applied && plan.FontSize >= 12 && plan.Lines.Count <= 2 && plan.CharactersPreserved, "Tight true18: " + plan.Reason);
        SavePlan(source,plan,output,"D-OPHELIA-TRUE18");
        VerifyRasterSafety(source,block,document.VisualBlocks,plan,output,"D-OPHELIA-TRUE18",assert);
        var material = NativeFootnoteMaterial.Analyze(source,block,document.VisualBlocks,plan);
        File.WriteAllText(Path.Combine(output,"D-OPHELIA-MATERIAL.json"),JsonSerializer.Serialize(material,options));
        assert(material.Applied && material.UsedTightBoundsPaperEvidence && material.OuterRingPaperFraction >= .95f,"Tight material: "+material.Reason);
        assert(material.AllPixels.All(p=>block.Bounds.Contains(p)),"Tight authority left original bounds");
        var controls = new List<object>();
        var bounds=Rectangle.Ceiling(block.Bounds);
        foreach(var name in new[]{"near-border-ring","shoe-colored-ring","icon-ring","dense-source-ink"})
        {
            using var changed = source.Clone(new Rectangle(Point.Empty,source.Size),PixelFormat.Format32bppArgb);
            using(var cg=Graphics.FromImage(changed))
            {
                if(name=="near-border-ring")cg.FillRectangle(Brushes.Black,bounds.Left-2,bounds.Top-2,bounds.Width+4,2);
                else if(name=="shoe-colored-ring")cg.FillRectangle(Brushes.SaddleBrown,bounds.Left-2,bounds.Top-2,2,bounds.Height+4);
                else if(name=="icon-ring")cg.FillRectangle(Brushes.Black,bounds.Right,bounds.Top-2,2,bounds.Height+4);
                else cg.FillRectangle(Brushes.Black,bounds.Left,bounds.Top,bounds.Width,bounds.Height);
            }
            var decision=NativeFootnoteMaterial.Analyze(changed,block,document.VisualBlocks,plan);
            controls.Add(new { Name=name,decision.Applied,decision.Reason,decision.PaperFraction,decision.OuterRingPaperFraction });
            assert(!decision.Applied,"Polluted material admitted: "+name);
        }
        var lowLine=block.Lines[0] with { Confidence=.8f };
        var lowBlock=new VisualBlock { BlockId=block.BlockId,Bounds=block.Bounds,Lines=[lowLine],LayoutBehavior=block.LayoutBehavior };
        var low=NativeFootnoteMaterial.Analyze(source,lowBlock,[lowBlock],plan);
        controls.Add(new {Name="tight-low-ocr-confidence",low.Applied,low.Reason,low.PaperFraction,low.OuterRingPaperFraction});
        assert(!low.Applied,"Tight OCR below .85 admitted");
        File.WriteAllText(Path.Combine(output,"TIGHT-MATERIAL-NEGATIVES.json"),JsonSerializer.Serialize(controls,options));
    }

    private static void VerifyRasterSafety(Bitmap source,VisualBlock block,IReadOnlyList<VisualBlock> blocks,
        NativeVisualLayoutPlan plan,string output,string id,Action<bool,string> assert)
    {
        var results=new List<object>();
        using var font=FontManager.CreatePixel("Microsoft YaHei UI",plan.FontSize,FontStyle.Regular);
        foreach(var outline in new[]{false,true})
        foreach(var line in plan.Lines)
        {
            var offset=new Point((int)Math.Floor(line.Bounds.Left)-30,(int)Math.Floor(line.Bounds.Top)-30);
            using var raster=new Bitmap((int)Math.Ceiling(line.AdvanceWidth)+64,(int)Math.Ceiling(line.Bounds.Height)+64,PixelFormat.Format32bppArgb);
            using(var rg=Graphics.FromImage(raster))
            {
                rg.Clear(Color.Transparent);rg.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                SourceStyleTextDrawingR2.Draw(rg,line.Text,font,new PointF(line.Bounds.Left-offset.X,line.Bounds.Top-offset.Y),Color.Black,
                    outline,Color.White,1.25f,false,Color.Transparent);
            }
            var pixels=0;var unsafePixels=0;var neighborPixels=0;var outsidePlan=0;
            for(var y=0;y<raster.Height;y++)for(var x=0;x<raster.Width;x++)
            {
                if(raster.GetPixel(x,y).A==0)continue;
                pixels++;var px=x+offset.X;var py=y+offset.Y;
                if(!line.Bounds.Contains(px+.5f,py+.5f))outsidePlan++;
                if(blocks.Any(b=>b.BlockId!=block.BlockId&&RectangleF.Inflate(b.Bounds,3,3).Contains(px,py)))neighborPixels++;
                if(block.Bounds.Contains(px,py))continue;
                if(px<0||py<0||px>=source.Width||py>=source.Height){unsafePixels++;continue;}
                var c=source.GetPixel(px,py);
                if(Math.Min(c.R,Math.Min(c.G,c.B))<220||Math.Max(c.R,Math.Max(c.G,c.B))-Math.Min(c.R,Math.Min(c.G,c.B))>26)unsafePixels++;
            }
            results.Add(new{line.Text,Outline=outline,Pixels=pixels,UnsafeSourcePixels=unsafePixels,NeighborPixels=neighborPixels,PixelsOutsideNominalBounds=outsidePlan});
            assert(pixels>0&&unsafePixels==0&&neighborPixels==0,id+" actual glyph raster left safe paper");
        }
        File.WriteAllText(Path.Combine(output,id+"-RASTER-SAFETY.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
    }

    private static VisualBlock Block(string id, RectangleF bounds, string text) => new()
    {
        BlockId = id, Bounds = bounds, LayoutBehavior = BlockLayoutBehavior.Fixed,
        Lines = [new(id, text, [new(bounds.Left, bounds.Top), new(bounds.Right, bounds.Top), new(bounds.Right, bounds.Bottom), new(bounds.Left, bounds.Bottom)], bounds, .99f, 0, "SelfTest")]
    };
    private static SourceStyleBundle LightBody() => new(Color.White, Color.Transparent, 0, Color.Transparent,
        PointF.Empty, 0, 255, FontStyle.Regular, SourceStylePolarity.Light, RegionRoleType.BodyParagraph, .9f, "TestSource", "Synthetic");
    private static void WeightCalibration(string output, string family, FontStyle style, int size, Action<bool, string> assert)
    {
        using var source = new Bitmap(1800, 360); using var g = Graphics.FromImage(source); g.Clear(Color.FromArgb(45, 45, 45));
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = FontManager.CreatePixel(family, size, style);
        var lines = new List<NormalizedOcrLine>();
        foreach (var (text, index) in new[] { "A solid source line with several words", "Another independent line of body text", "Preserve its actual source stroke weight" }.Select((x, i) => (x, i)))
        {
            var top = 25 + index * 100; g.DrawString(text, font, Brushes.White, new PointF(25, top), StringFormat.GenericTypographic);
            var pixels = new List<Point>();
            for (var y = top; y < top + 90; y++) for (var x = 20; x < 1750; x++) if (source.GetPixel(x, y).R >= 220) pixels.Add(new(x, y));
            var bounds = RectangleF.FromLTRB(pixels.Min(x => x.X), pixels.Min(x => x.Y), pixels.Max(x => x.X) + 1, pixels.Max(x => x.Y) + 1);
            lines.Add(Block("S" + index, bounds, text).Lines[0]);
        }
        var block = new VisualBlock { BlockId = "calibration", Lines = lines, Bounds = lines.Select(x => x.Bounds).Aggregate(RectangleF.Union), LayoutBehavior = BlockLayoutBehavior.Flow };
        var result = NativeVisualTypography.RefineSourceWeight(source, block, LightBody());
        File.WriteAllText(Path.Combine(output, "WEIGHT-" + family.Replace(' ', '-') + "-" + style + "-" + size + ".json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        // At 18px the surviving raster cores cannot prove weight consistently;
        // refusal is the expected conservative outcome for both fonts.
        assert(result.Applied == (style == FontStyle.Bold && size >= 28), result.Reason);
    }
    private static void RealWeight(string fixtureRoot, string output, Action<bool, string> assert)
    {
        var root = Path.Combine(fixtureRoot, "UAT-ARA-CURRENT"); using var source = new Bitmap(Path.Combine(root, "SOURCE.png"));
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var ocr = JsonSerializer.Deserialize<OcrEngineResult>(File.ReadAllText(Path.Combine(root, "PRODUCT-OCR-REPLAY.json")), options)!;
        var document = CorePipelineEngine.Analyze(source.Size, ocr.Blocks.Where(x => x.Enabled && x.Polygon.Length >= 3).Select((x, i) =>
            new RawOcrLine(x.Id, x.RawText, x.CorrectedText, x.Polygon, x.BoundingBox, x.Confidence ?? 0, i)).ToArray());
        var block = document.VisualBlocks.OrderByDescending(x => x.Lines.Count).First();
        using var styleTrace = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "SOURCE-STYLE-BUNDLE-TRACE.json")));
        var saved = styleTrace.RootElement.GetProperty("Blocks").EnumerateArray().First(x => x.GetProperty("BlockId").GetString() == block.BlockId);
        var color = Color.FromArgb(unchecked((int)Convert.ToUInt32(saved.GetProperty("SourceFill").GetString()![1..], 16)));
        var actualBundle = LightBody() with
        {
            FillColor = color, Confidence = saved.GetProperty("Confidence").GetSingle(),
            Owner = saved.GetProperty("Owner").GetString()!, Evidence = saved.GetProperty("Evidence").GetString()!,
            Weight = (FontStyle)saved.GetProperty("Weight").GetInt32(), Role = (RegionRoleType)saved.GetProperty("Role").GetInt32()
        };
        var result = NativeVisualTypography.RefineSourceWeight(source, block, actualBundle);
        File.WriteAllText(Path.Combine(output, "WEIGHT-UAT-ARA-CURRENT.json"), JsonSerializer.Serialize(result, options));
        assert(result.Applied, result.Reason);
        foreach (var negative in new[]
        {
            actualBundle with { Owner = "UserTextColorOverride" },
            actualBundle with { FillColor = Color.Gray },
            actualBundle with { FillColor = Color.Black, Polarity = SourceStylePolarity.Dark },
            actualBundle with { FillColor = Color.Gold, Polarity = SourceStylePolarity.Chromatic },
            actualBundle with { Confidence = .3f },
            actualBundle with { Role = RegionRoleType.Title }
        }) assert(!NativeVisualTypography.RefineSourceWeight(source, block, negative).Applied, "Negative style authority overridden");
    }
    private static Bitmap Paper() { var b = new Bitmap(1200, 700); using var g = Graphics.FromImage(b); g.Clear(Color.FromArgb(248, 248, 248)); return b; }
    private static string PixelHash(Bitmap bitmap)
    { using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); return Convert.ToHexString(SHA256.HashData(stream.ToArray())); }
    private static void SavePlan(Bitmap source, NativeVisualLayoutPlan result, string output, string id)
    {
        using var overlay = new Bitmap(source); using var g = Graphics.FromImage(overlay);
        using var font = FontManager.CreatePixel("Microsoft YaHei UI", result.FontSize, FontStyle.Regular);
        foreach (var line in result.Lines)
        {
            g.DrawRectangle(Pens.Blue, Rectangle.Round(line.Bounds));
            g.DrawString(line.Text, font, Brushes.Black, line.Bounds.Location, StringFormat.GenericTypographic);
        }
        overlay.Save(Path.Combine(output, id + "-GEOMETRY-OVERLAY.png"), ImageFormat.Png);
        File.WriteAllText(Path.Combine(output, id + "-GEOMETRY.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}
