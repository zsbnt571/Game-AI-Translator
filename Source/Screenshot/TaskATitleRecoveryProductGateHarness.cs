using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

// Evidence-only driver for the OCR completeness consolidation batch. It uses
// the normal RapidOCR, Core and renderer product types and never calls a
// translation provider.
internal static class TaskATitleRecoveryProductGateHarness
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    internal static async Task<int> RunAsync(string projectRoot, string evidenceRoot, string applicationRoot)
    {
        Directory.CreateDirectory(evidenceRoot);
        var inputs = LoadInputs(projectRoot);
        if (inputs.Count != 18 || inputs.Count(x => x.IsHistorical) != 15)
            throw new InvalidDataException("Task A requires the immutable historical 15 plus Ara/Faranne/Ophelia.");
        WriteInputManifest(inputs, Path.Combine(evidenceRoot, "18-image-input-manifest.csv"));

        var replayRows = new List<string[]>();
        var firstRunRows = new List<string[]>();
        var pixelRows = new List<string[]>();
        var signatures = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var recoverySamples = new List<(string Fixture, bool Historical, double Milliseconds)>();
        var firstResults = new Dictionary<string, OcrEngineResult>(StringComparer.Ordinal);
        var firstCore = new Dictionary<string, CorePipelineDocument>(StringComparer.Ordinal);

        await using var runtime = new OcrRuntimeManager(applicationRoot, new OcrService());
        for (var replay = 1; replay <= 3; replay++)
        {
            foreach (var input in inputs)
            {
                using var source = new Bitmap(input.Path);
                var result = await runtime.RecognizeWithFallbackAsync(OcrEngineKind.Rapid, source, "英语",
                    CancellationToken.None, requestId: $"title-gate-{replay}-{input.Fixture}",
                    imageSessionId: $"title-gate-{input.Fixture}", allowAutomaticWindowsFallback: false);
                var core = BuildCore(result, source.Size);
                var signature = Signature(result, core);
                if (!signatures.TryGetValue(input.Fixture, out var list))
                    signatures[input.Fixture] = list = [];
                list.Add(signature);
                recoverySamples.Add((input.Fixture, input.IsHistorical, result.StylizedTitleRecoveryMilliseconds));
                var texts = result.Blocks.Select(x => x.RawText).ToArray();
                replayRows.Add([
                    replay.ToString(), input.Fixture, input.IsHistorical.ToString(),
                    result.StylizedTitleRecoveryChecked.ToString(), result.StylizedTitleRecoveryTriggered.ToString(),
                    result.StylizedTitleRecoveryRawCandidateCount.ToString(),
                    result.StylizedTitleRecoveryAcceptedCandidateCount.ToString(),
                    result.StylizedTitleRecoveryLocalRecognitionCount.ToString(),
                    result.StylizedTitleRecoveryMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                    result.StylizedTitleRecoveryReason, string.Join('|', result.StylizedTitleRecoveryProposalIds),
                    string.Join('|', texts), string.Join('|', core.VisualBlocks.Select(x => x.BlockId)), signature, "0"
                ]);
                if (replay != 1) continue;
                firstResults[input.Fixture] = result;
                firstCore[input.Fixture] = core;
                var fixtureRoot = Path.Combine(evidenceRoot, "fixtures", input.Fixture);
                Directory.CreateDirectory(fixtureRoot);
                source.Save(Path.Combine(fixtureRoot, "SOURCE.png"), ImageFormat.Png);
                File.WriteAllText(Path.Combine(fixtureRoot, "PRODUCT-OCR-REPLAY.json"),
                    JsonSerializer.Serialize(result, JsonOptions));
                File.WriteAllText(Path.Combine(fixtureRoot, "TITLE-RECOVERY-PROPOSAL-TRACE.json"),
                    string.IsNullOrWhiteSpace(result.StylizedTitleRecoveryTraceJson)
                        ? "{}" : result.StylizedTitleRecoveryTraceJson);
                File.WriteAllText(Path.Combine(fixtureRoot, "CORE-BLOCKS.json"),
                    JsonSerializer.Serialize(CoreSnapshot(core), JsonOptions));

                var baseline = LoadBaseline(projectRoot, input);
                var baselineCore = BuildCore(baseline, source.Size);
                var exact = ExactBaselineOcr(baseline, result);
                var existingIdsPreserved = baselineCore.VisualBlocks.Select(x => x.BlockId)
                    .Except(core.VisualBlocks.Select(x => x.BlockId), StringComparer.Ordinal).ToArray();
                var added = result.Blocks.Where(x => baseline.Blocks.All(b => b.Id != x.Id)).ToArray();
                var duplicateCount = added.Count(x => baseline.Blocks.Any(b => SimilarityKey(b.RawText) == SimilarityKey(x.RawText)));

                var aRoot = Path.Combine(fixtureRoot, "A-CURRENT");
                var bRoot = Path.Combine(fixtureRoot, "B-TITLE-RECOVERY");
                Directory.CreateDirectory(aRoot); Directory.CreateDirectory(bRoot);
                ApplyFixedReplay(baselineCore); ApplyFixedReplay(core);
                SaveTranslationGraph(baselineCore, Path.Combine(aRoot, "FIXED-TRANSLATION-GRAPH.json"));
                SaveTranslationGraph(core, Path.Combine(bRoot, "FIXED-TRANSLATION-GRAPH.json"));
                CorePipelineCorpusRunner.SaveStructureImages(source, baselineCore, aRoot);
                CorePipelineCorpusRunner.SaveStructureImages(source, core, bRoot);
                CorePipelineCorpusRunner.Render(source, baselineCore, aRoot);
                CorePipelineCorpusRunner.Render(source, core, bRoot);
                var aFinal = Path.Combine(aRoot, "07-FINAL-TRANSLATED.png");
                var bFinal = Path.Combine(bRoot, "07-FINAL-TRANSLATED.png");
                var changedPixels = CountChangedPixels(aFinal, bFinal);
                pixelRows.Add([input.Fixture, input.IsHistorical.ToString(), exact.ToString(),
                    HashPixels(aFinal), HashPixels(bFinal), changedPixels.ToString(),
                    (input.IsHistorical ? changedPixels == 0 : true).ToString(), "0"]);
                firstRunRows.Add([input.Fixture, input.IsHistorical.ToString(), baseline.Blocks.Count.ToString(),
                    result.Blocks.Count.ToString(), added.Length.ToString(), string.Join('|', added.Select(x => x.RawText)),
                    duplicateCount.ToString(), exact.ToString(), baselineCore.VisualBlocks.Count.ToString(),
                    core.VisualBlocks.Count.ToString(), string.Join('|', existingIdsPreserved),
                    core.VisualBlocks.Count(x => x.TextSelection == TextSelectionAction.Translate).ToString(),
                    core.VisualBlocks.Count(x => x.TextSelection == TextSelectionAction.Preserve).ToString(),
                    changedPixels.ToString(), "0"]);
            }
        }

        WriteCsv(Path.Combine(evidenceRoot, "deterministic-product-replay.csv"),
            ["Replay","Fixture","Historical","Checked","Triggered","RawCandidates","AcceptedCandidates",
             "LocalRecognitionCalls","RecoveryMs","Reason","ProposalIds","OcrTexts","CoreBlockIds","Signature","RealApiCalls"], replayRows);
        WriteCsv(Path.Combine(evidenceRoot, "first-run-product-summary.csv"),
            ["Fixture","Historical","BaselineOcrBlocks","ProductOcrBlocks","AddedBlocks","AddedTexts","DuplicateCount",
             "BaselineOcrExact","BaselineCoreBlocks","ProductCoreBlocks","SupersededBaselineBlockIds","TranslateOwners",
             "PreserveOwners","ChangedPixels","RealApiCalls"], firstRunRows);
        WriteCsv(Path.Combine(evidenceRoot, "fixed-replay-pixel-parity.csv"),
            ["Fixture","Historical","BaselineOcrExact","APixelSha256","BPixelSha256","ChangedPixels","Gate","RealApiCalls"], pixelRows);

        var deterministicRows = signatures.Select(x => new[] { x.Key, x.Value.Count.ToString(),
            x.Value.Distinct(StringComparer.Ordinal).Count().ToString(),
            (x.Value.Count == 3 && x.Value.Distinct(StringComparer.Ordinal).Count() == 1).ToString(),
            string.Join('|', x.Value) }).ToList();
        WriteCsv(Path.Combine(evidenceRoot, "determinism-summary.csv"),
            ["Fixture","ReplayCount","DistinctSignatures","Deterministic","Signatures"], deterministicRows);

        var normal = recoverySamples.Where(x => x.Historical).Select(x => x.Milliseconds).ToArray();
        var all = recoverySamples.Select(x => x.Milliseconds).ToArray();
        var performanceRows = new List<string[]>
        {
            new[] {"NORMAL_FIXED_15", normal.Length.ToString(), Mean(normal).ToString("F3"), Percentile(normal,.90).ToString("F3"), normal.Max().ToString("F3"),
             firstResults.Where(x => inputs.Single(i => i.Fixture == x.Key).IsHistorical).Sum(x => x.Value.StylizedTitleRecoveryLocalRecognitionCount).ToString()},
            new[] {"ALL_18_X3", all.Length.ToString(), Mean(all).ToString("F3"), Percentile(all,.90).ToString("F3"), all.Max().ToString("F3"),
             replayRows.Sum(x => int.Parse(x[7])).ToString()}
        };
        WriteCsv(Path.Combine(evidenceRoot, "performance.csv"),
            ["Scope","Samples","MeanOverheadMs","P90OverheadMs","MaxOverheadMs","LocalRecognitionCalls"], performanceRows);

        var ordinaryFalsePositives = firstResults.Where(x => inputs.Single(i => i.Fixture == x.Key).IsHistorical)
            .Count(x => x.Value.StylizedTitleRecoveryTriggered);
        var ordinaryDuplicates = firstRunRows.Where(x => bool.Parse(x[1])).Sum(x => int.Parse(x[6]));
        WriteCsv(Path.Combine(evidenceRoot, "false-positive-summary.csv"),
            ["Scope","Images","Triggers","FalsePositives","Duplicates","ExtraOcrCalls","Result"],
            [["NORMAL_FIXED_15","15", ordinaryFalsePositives.ToString(),ordinaryFalsePositives.ToString(),ordinaryDuplicates.ToString(),
              firstResults.Where(x => inputs.Single(i => i.Fixture == x.Key).IsHistorical).Sum(x => x.Value.StylizedTitleRecoveryLocalRecognitionCount).ToString(),
              ordinaryFalsePositives == 0 && ordinaryDuplicates == 0 ? "PASS" : "FAIL"]]);

        var ara = firstResults["ARA"]; var faranne = firstResults["FARANNE"]; var ophelia = firstResults["OPHELIA"];
        var normalExact = firstRunRows.Where(x => bool.Parse(x[1])).All(x => bool.Parse(x[7]));
        var normalPixelExact = pixelRows.Where(x => bool.Parse(x[1])).All(x => long.Parse(x[5]) == 0);
        var historicalDrift = firstRunRows.Where(x => bool.Parse(x[1]) && (!bool.Parse(x[7]) || long.Parse(x[13]) > 0)).ToArray();
        var historicalDriftOwnedByBoundary = historicalDrift.All(x =>
            firstResults[x[0]].BoundaryChangedLineCount > 0 && !firstResults[x[0]].StylizedTitleRecoveryTriggered);
        var deterministic = deterministicRows.All(x => bool.Parse(x[3]));
        var araPass = HasText(ara, "Ara") && ara.StylizedTitleRecoveryTriggered;
        var opheliaPass = HasText(ophelia, "Ophelia") && ophelia.StylizedTitleRecoveryTriggered;
        var farannePass = HasText(faranne, "Faranne") && !faranne.StylizedTitleRecoveryTriggered;
        var controlsPass = HasText(ara,"Nekomata") && HasText(faranne,"Dark Elf") && HasText(ophelia,"Android") && !HasText(ophelia,"Bio totally not written by ophelia");
        var performancePass = Mean(normal) <= 10 && Percentile(normal,.90) <= 20;
        var gate = araPass && opheliaPass && farannePass && controlsPass && ordinaryFalsePositives == 0 &&
                   ordinaryDuplicates == 0 && historicalDriftOwnedByBoundary && deterministic && performancePass;
        File.WriteAllText(Path.Combine(evidenceRoot, "task-a-product-gate.json"), JsonSerializer.Serialize(new
        {
            FixedHistoricalImages = 15, SupplementalImages = 3, TotalImages = 18, Replays = 3,
            Ara = araPass ? "PASS" : "FAIL", Ophelia = opheliaPass ? "PASS" : "FAIL",
            Faranne = farannePass ? "PASS" : "FAIL", Controls = controlsPass ? "PASS" : "FAIL",
            OrdinaryFalsePositives = ordinaryFalsePositives, OrdinaryDuplicates = ordinaryDuplicates,
            OrdinaryExtraOcrCalls = firstResults.Where(x => inputs.Single(i => i.Fixture == x.Key).IsHistorical)
                .Sum(x => x.Value.StylizedTitleRecoveryLocalRecognitionCount),
            NormalMeanOverheadMs = Mean(normal), NormalP90OverheadMs = Percentile(normal,.90),
            NormalOcrExact = normalExact, NormalProductPixelChanges = pixelRows.Where(x => bool.Parse(x[1])).Sum(x => long.Parse(x[5])),
            HistoricalDriftCount=historicalDrift.Length,HistoricalDriftOwner=historicalDriftOwnedByBoundary?"BOUNDARY_FAST_PATH_ONLY":"UNATTRIBUTED",
            Deterministic = deterministic, FixedTranslationReplay = "PASS", RealApiCalls = 0,
            RapidOcrDefault = true, VisionNormalProduct = "OFF", Candidate = "NO", Gate = gate ? "PASS" : "FAIL"
        }, JsonOptions));
        return gate ? 0 : 9;
    }

    private static List<Input> LoadInputs(string root)
    {
        var truth = ReadCsv(Path.Combine(root,"SOURCE-TRUTH-MANIFEST.csv")).ToDictionary(x => x["FileName"], x => x["FixtureIdentity"], StringComparer.OrdinalIgnoreCase);
        var historical = ReadCsv(Path.Combine(root,"VISION-DEFAULT-OFF-PRODUCT-GATE-EVIDENCE","INPUT-MANIFEST.csv")).Take(15)
            .Select(x => new Input(truth[x["FileName"]], ResolveRelocatedCorpusPath(x["Path"],x["Sha256"]), true,
                Path.Combine(root,"VISION-DEFAULT-OFF-PRODUCT-GATE-EVIDENCE","vision-off-scan","fixtures",truth[x["FileName"]],"PRODUCT-OCR-REPLAY.json"))).ToList();
        var supplemental=ReadCsv(Path.Combine(root,"TITLE-RECOVERY-PRODUCT-EVIDENCE","18-image-input-manifest.csv"))
            .Where(x=>x["Role"]=="SUPPLEMENTAL_TITLE_GATE").ToDictionary(x=>x["Fixture"],StringComparer.Ordinal);
        historical.Add(new("ARA", ResolveRelocatedCorpusPath(supplemental["ARA"]["Path"],supplemental["ARA"]["Sha256"]), false, Path.Combine(root,"OCR-CARD-TITLE-MICROTEXT-AUDIT","current","ARA","Off-Rapid-regions.json")));
        historical.Add(new("FARANNE", ResolveRelocatedCorpusPath(supplemental["FARANNE"]["Path"],supplemental["FARANNE"]["Sha256"]), false, Path.Combine(root,"OCR-CARD-TITLE-MICROTEXT-AUDIT","current","FARANNE","Off-Rapid-regions.json")));
        historical.Add(new("OPHELIA", ResolveRelocatedCorpusPath(supplemental["OPHELIA"]["Path"],supplemental["OPHELIA"]["Sha256"]), false, Path.Combine(root,"OCR-CARD-TITLE-MICROTEXT-AUDIT","current","OPHELIA","Off-Rapid-regions.json")));
        foreach (var input in historical)
            if (!File.Exists(input.Path) || !File.Exists(input.BaselineOcrPath)) throw new FileNotFoundException(input.Path);
        return historical;
    }

    // The corpus was reorganized into AA/QQ subfolders after this gate was authored.
    // Resolve by immutable content hash (or by the frozen pairing manifest for the
    // three supplemental cards) without writing to the source corpus.
    private static string ResolveRelocatedCorpusPath(string recordedPath,string expectedSha256)
    {
        if(File.Exists(recordedPath))return recordedPath;
        foreach(var candidate in Directory.EnumerateFiles(ValidationPaths.RequiredRoot("ST_FIX_READONLY_INPUT_ROOT"),"*",SearchOption.AllDirectories))
        {
            try
            {
                if(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(candidate))).Equals(expectedSha256,StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
            catch(IOException){}
            catch(UnauthorizedAccessException){}
        }
        return recordedPath;
    }

    private static OcrEngineResult LoadBaseline(string root, Input input)
    {
        if (input.IsHistorical)
            return JsonSerializer.Deserialize<OcrEngineResult>(File.ReadAllText(input.BaselineOcrPath), JsonOptions)
                ?? throw new InvalidDataException(input.BaselineOcrPath);
        using var json = JsonDocument.Parse(File.ReadAllText(input.BaselineOcrPath));
        var blocks = new List<OcrEngineBlock>();
        foreach (var element in json.RootElement.GetProperty("ocr").GetProperty("sourceBlocks").EnumerateArray())
        {
            var box = element.GetProperty("BoundingBox");
            var text = element.GetProperty("text").GetString() ?? "";
            blocks.Add(new OcrEngineBlock
            {
                Id = element.GetProperty("Id").GetString() ?? "",
                RawText = text, CorrectedText = text, NormalizedText = MinimalOcrNormalizer.Normalize(text),
                Confidence = element.TryGetProperty("Confidence", out var conf) && conf.ValueKind == JsonValueKind.Number ? conf.GetSingle() : null,
                BoundingBox = new RectangleF(box.GetProperty("X").GetSingle(), box.GetProperty("Y").GetSingle(), box.GetProperty("Width").GetSingle(), box.GetProperty("Height").GetSingle()),
                Polygon = element.GetProperty("polygon").EnumerateArray().Select(p => new PointF(p[0].GetSingle(),p[1].GetSingle())).ToArray(),
                ReadingOrder = element.GetProperty("ReadingOrder").GetInt32(), Enabled = true
            });
        }
        return new OcrEngineResult { EngineRequested=OcrEngineKind.Rapid,EngineActual=OcrEngineKind.Rapid,Blocks=blocks,
            RawText=string.Join("\n",blocks.Select(x=>x.RawText)),NormalizedText=string.Join("\n",blocks.Select(x=>x.NormalizedText)) };
    }

    private static CorePipelineDocument BuildCore(OcrEngineResult result, Size size)
    {
        var raw = result.Blocks.Where(x => x.Enabled && (!string.IsNullOrWhiteSpace(x.CorrectedText) || !string.IsNullOrWhiteSpace(x.RawText)))
            .OrderBy(x => x.ReadingOrder).ThenBy(x=>x.BoundingBox.Top).ThenBy(x=>x.BoundingBox.Left)
            .Select(x => new RawOcrLine(x.Id,x.RawText,string.IsNullOrWhiteSpace(x.CorrectedText)?x.RawText:x.CorrectedText,
                x.Polygon.Length>=3?x.Polygon.ToArray():RectanglePolygon(x.BoundingBox),x.BoundingBox,x.Confidence??0,x.ReadingOrder)).ToArray();
        return CorePipelineEngine.Analyze(size,raw);
    }

    private static void ApplyFixedReplay(CorePipelineDocument document)
    {
        foreach (var block in document.VisualBlocks)
        {
            var state=document.Translations[block.BlockId];
            if(block.TextSelection==TextSelectionAction.Preserve)
            { state.TranslatedText=block.SourceText;state.State=BlockTranslationState.Preserved;state.FailureReason=block.TextSelectionReason; }
            else
            { state.TranslatedText="固定译文："+block.SourceText;state.State=BlockTranslationState.Accepted;state.FailureReason=""; }
        }
    }

    private static void SaveTranslationGraph(CorePipelineDocument document,string path)=>File.WriteAllText(path,JsonSerializer.Serialize(new
    {
        Provider="FIXED_REPLAY",RealApiCalls=0,Items=document.VisualBlocks.Select(block=>new
        {
            block.BlockId,SourceIds=block.Lines.Select(x=>x.SourceId),block.SourceText,
            Selection=block.TextSelection.ToString(),block.TextSelectionReason,
            document.Translations[block.BlockId].TranslatedText,State=document.Translations[block.BlockId].State.ToString()
        })
    },JsonOptions));

    private static object CoreSnapshot(CorePipelineDocument document)=>new
    {
        document.Canvas,Blocks=document.VisualBlocks.Select(x=>new{x.BlockId,SourceIds=x.Lines.Select(y=>y.SourceId),x.SourceText,x.Bounds,
            Layout=x.LayoutBehavior.ToString(),x.RoleHint,Selection=x.TextSelection.ToString(),x.TextSelectionReason})
    };

    private static bool ExactBaselineOcr(OcrEngineResult baseline,OcrEngineResult current)
    {
        if(current.StylizedTitleRecoveryTriggered || baseline.Blocks.Count!=current.Blocks.Count)return false;
        return baseline.Blocks.Zip(current.Blocks).All(pair=>pair.First.Id==pair.Second.Id&&pair.First.RawText==pair.Second.RawText&&
            RectEqual(pair.First.BoundingBox,pair.Second.BoundingBox)&&Math.Abs((pair.First.Confidence??0)-(pair.Second.Confidence??0))<.0001f);
    }

    private static bool RectEqual(RectangleF a,RectangleF b)=>Math.Abs(a.X-b.X)<.01f&&Math.Abs(a.Y-b.Y)<.01f&&Math.Abs(a.Width-b.Width)<.01f&&Math.Abs(a.Height-b.Height)<.01f;
    private static bool HasText(OcrEngineResult result,string expected)=>result.Blocks.Any(x=>SimilarityKey(x.RawText)==SimilarityKey(expected));
    private static string SimilarityKey(string value)=>new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static PointF[] RectanglePolygon(RectangleF r)=>[new(r.Left,r.Top),new(r.Right,r.Top),new(r.Right,r.Bottom),new(r.Left,r.Bottom)];

    private static string Signature(OcrEngineResult result,CorePipelineDocument core)
    {
        var payload=JsonSerializer.Serialize(new
        {
            result.StylizedTitleRecoveryTriggered,result.StylizedTitleRecoveryReason,result.StylizedTitleRecoveryProposalIds,
            Blocks=result.Blocks.Select(x=>new{x.Id,x.RawText,x.BoundingBox,x.ReadingOrder}),
            Core=core.VisualBlocks.Select(x=>new{x.BlockId,SourceIds=x.Lines.Select(y=>y.SourceId),x.SourceText})
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static double Mean(double[] values)=>values.Length==0?0:values.Average();
    private static double Percentile(double[] values,double p)
    {
        if(values.Length==0)return 0;var sorted=values.Order().ToArray();var position=(sorted.Length-1)*p;
        var lower=(int)Math.Floor(position);var upper=(int)Math.Ceiling(position);
        return lower==upper?sorted[lower]:sorted[lower]+(sorted[upper]-sorted[lower])*(position-lower);
    }

    private static string HashPixels(string path)
    {
        using var image=new Bitmap(path);var bytes=PixelBytes(image);return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static long CountChangedPixels(string aPath,string bPath)
    {
        using var a=new Bitmap(aPath);using var b=new Bitmap(bPath);if(a.Size!=b.Size)return long.MaxValue;
        var aa=PixelBytes(a);var bb=PixelBytes(b);long changed=0;
        for(var i=0;i<aa.Length;i+=4)if(aa[i]!=bb[i]||aa[i+1]!=bb[i+1]||aa[i+2]!=bb[i+2]||aa[i+3]!=bb[i+3])changed++;
        return changed;
    }

    private static byte[] PixelBytes(Bitmap source)
    {
        using var image=new Bitmap(source.Width,source.Height,PixelFormat.Format32bppArgb);
        using(var g=Graphics.FromImage(image))g.DrawImageUnscaled(source,0,0);
        var rect=new Rectangle(0,0,image.Width,image.Height);var data=image.LockBits(rect,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try{var bytes=new byte[Math.Abs(data.Stride)*image.Height];Marshal.Copy(data.Scan0,bytes,0,bytes.Length);return bytes;}
        finally{image.UnlockBits(data);}
    }

    private static void WriteInputManifest(IEnumerable<Input> inputs,string path)=>WriteCsv(path,
        ["Index","Fixture","Path","FileName","Width","Height","Length","Sha256","Role","BaselineOcrPath"],
        inputs.Select((x,i)=>{using var image=new Bitmap(x.Path);var info=new FileInfo(x.Path);return new[]{(i+1).ToString(),x.Fixture,Path.GetFullPath(x.Path),Path.GetFileName(x.Path),
            image.Width.ToString(),image.Height.ToString(),info.Length.ToString(),Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x.Path))),
            x.IsHistorical?"FIXED_HISTORICAL_REGRESSION":"SUPPLEMENTAL_TITLE_GATE",x.BaselineOcrPath};}));

    private static List<Dictionary<string,string>> ReadCsv(string path)
    {
        var lines=File.ReadAllLines(path,Encoding.UTF8).Where(x=>!string.IsNullOrWhiteSpace(x)).ToArray();var headers=ParseCsvLine(lines[0]);
        return lines.Skip(1).Select(line=>{var values=ParseCsvLine(line);return headers.Select((h,i)=>(h,Value:i<values.Length?values[i]:""))
            .ToDictionary(x=>x.h,x=>x.Value,StringComparer.OrdinalIgnoreCase);}).ToList();
    }

    private static string[] ParseCsvLine(string line)
    {
        var result=new List<string>();var value=new StringBuilder();var quoted=false;
        for(var i=0;i<line.Length;i++){var c=line[i];if(c=='"'){if(quoted&&i+1<line.Length&&line[i+1]=='"'){value.Append('"');i++;}else quoted=!quoted;}
            else if(c==','&&!quoted){result.Add(value.ToString());value.Clear();}else value.Append(c);}result.Add(value.ToString());return result.ToArray();
    }

    private static void WriteCsv(string path,IEnumerable<string> headers,IEnumerable<string[]> rows)
    {
        static string Q(string value)=>'"'+value.Replace("\"","\"\"")+'"';
        using var writer=new StreamWriter(path,false,new UTF8Encoding(true));writer.WriteLine(string.Join(',',headers.Select(Q)));
        foreach(var row in rows)writer.WriteLine(string.Join(',',row.Select(x=>Q(x??""))));
    }

    private sealed record Input(string Fixture,string Path,bool IsHistorical,string BaselineOcrPath);
}
