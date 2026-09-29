using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

internal static class RendererProductPerformanceHarness
{
    private static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true,PropertyNameCaseInsensitive=true};

    internal static int Run(string outputRoot,string pairingManifestPath,string settingsPath,
        string replayRoot,string diagnosticReferenceRoot,int iterations)
    {
        if(iterations<1)throw new ArgumentOutOfRangeException(nameof(iterations));
        Directory.CreateDirectory(outputRoot);
        var inputs=JsonSerializer.Deserialize<List<PairingInput>>(File.ReadAllText(pairingManifestPath),JsonOptions)??[];
        if(inputs.Count!=37||inputs.Count(x=>x.Set=="OLD")!=18||inputs.Count(x=>x.Set=="NEW")!=19)
            throw new InvalidDataException("Renderer product gate requires exact OLD-18 + NEW-19 identity.");
        var settings=ConfigurationManager.Load(settingsPath,false);
        var profile=FontManager.ResolveTranslationImageProfile(settings);
        var rows=new List<Row>();
        var allPixelsEqual=true;var allNoWrites=true;var allDiagnosticsOff=true;var blockIdsStable=true;

        foreach(var input in inputs.OrderBy(x=>x.PairId,StringComparer.Ordinal))
        {
            Console.WriteLine($"PRODUCT-RENDER BEGIN {input.PairId}");
            var fixture=Path.Combine(outputRoot,"fixtures",input.PairId);Directory.CreateDirectory(fixture);
            using var source=new Bitmap(input.ProductInputPath);
            var prior=Path.Combine(replayRoot,"fixtures",input.PairId);
            var ocr=JsonSerializer.Deserialize<OcrEngineResult>(
                File.ReadAllText(Path.Combine(prior,"PRODUCT-OCR-REPLAY.json")),JsonOptions)
                ??throw new InvalidDataException($"Invalid OCR replay for {input.PairId}");
            var document=MegaVisualFidelityE2EHarness.BuildCore(ocr,source.Size);
            using var translated=JsonDocument.Parse(File.ReadAllText(Path.Combine(prior,"TRANSLATED-TEXT.json")));
            var replayItems=translated.RootElement.GetProperty("Items").EnumerateArray()
                .ToDictionary(x=>x.GetProperty("BlockId").GetString()??"",x=>x,StringComparer.Ordinal);
            var currentIds=document.VisualBlocks.Select(x=>x.BlockId).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
            var replayIds=replayItems.Keys.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
            var idsStable=currentIds.SequenceEqual(replayIds,StringComparer.Ordinal);blockIdsStable&=idsStable;
            foreach(var block in document.VisualBlocks)
            {
                var state=document.Translations[block.BlockId];
                if(block.TextSelection==TextSelectionAction.Preserve)
                {
                    state.TranslatedText=block.SourceText;
                    state.State=BlockTranslationState.Preserved;
                    state.FailureReason=block.TextSelectionReason;
                    continue;
                }
                if(!replayItems.TryGetValue(block.BlockId,out var item))
                {state.State=BlockTranslationState.Failed;state.FailureReason="REPLAY_BLOCK_ID_MISMATCH";continue;}
                state.TranslatedText=item.GetProperty("TranslatedText").GetString()??"";
                var priorState=item.GetProperty("State").GetString()??"Failed";
                state.State=Enum.TryParse<BlockTranslationState>(priorState,true,out var parsed)?parsed:BlockTranslationState.Failed;
                state.FailureReason=state.State==BlockTranslationState.Failed?"REPLAY_PRIOR_FAILED":"";
            }

            using(var warm=CorePipelineCorpusRunner.RenderProduct(source,document,profile).Bitmap){}
            for(var iteration=1;iteration<=iterations;iteration++)
            {
                var before=Directory.EnumerateFiles(fixture,"*",SearchOption.AllDirectories).Count();
                var wall=Stopwatch.StartNew();
                var rendered=CorePipelineCorpusRunner.RenderProduct(source,document,profile);
                wall.Stop();
                var after=Directory.EnumerateFiles(fixture,"*",SearchOption.AllDirectories).Count();
                var noWrites=before==after&&rendered.Timing.FullFrameDiagnosticPngWrites==0&&rendered.Timing.DiagnosticJsonWrites==0;
                var diagnosticsOff=!rendered.Timing.DiagnosticsEnabled;
                allNoWrites&=noWrites;allDiagnosticsOff&=diagnosticsOff;
                var productPath=Path.Combine(fixture,$"PRODUCT-{iteration:D2}.png");
                rendered.Bitmap.Save(productPath,ImageFormat.Png);
                var referencePath=Path.Combine(diagnosticReferenceRoot,"fixtures",input.PairId,"07-FINAL-TRANSLATED.png");
                var referencePresent=File.Exists(referencePath);
                var productHash=HashFile(productPath);
                var referenceHash=referencePresent?HashFile(referencePath):"MISSING";
                var pixelsEqual=referencePresent&&productHash==referenceHash;
                allPixelsEqual&=pixelsEqual;
                rows.Add(new(input.PairId,input.Set,iteration,source.Width,source.Height,document.VisualBlocks.Count,
                    rendered.Audits.Count(x=>x.AtomicCommit),rendered.Timing.StyleMs,rendered.Timing.BackgroundMs,
                    rendered.Timing.LayoutMs,rendered.Timing.DrawMs,rendered.Timing.EncodeMs,
                    rendered.Timing.TotalMs,wall.Elapsed.TotalMilliseconds,diagnosticsOff,
                    rendered.Timing.FullFrameDiagnosticPngWrites,rendered.Timing.DiagnosticJsonWrites,
                    noWrites,referencePresent,pixelsEqual,productHash,referenceHash,idsStable));
                rendered.Bitmap.Dispose();
            }
            Console.WriteLine($"PRODUCT-RENDER END {input.PairId}");
        }

        WriteCsv(Path.Combine(outputRoot,"RENDERER-PRODUCT-PERFORMANCE.csv"),rows);
        var measured=rows.Select(x=>x.WallMs).Order().ToArray();
        var summary=new
        {
            Images=inputs.Count,Iterations=iterations,Samples=rows.Count,
            DiagnosticsEnabled=false,FullFrameDiagnosticPngWritesDuringMeasuredRender=rows.Sum(x=>x.PngWrites),
            DiagnosticJsonWritesDuringMeasuredRender=rows.Sum(x=>x.JsonWrites),
            RendererMeanMs=measured.Average(),RendererMedianMs=Percentile(measured,.5),
            RendererP90Ms=Percentile(measured,.9),RendererMaxMs=measured.Max(),
            StyleMeanMs=rows.Average(x=>x.StyleMs),BackgroundMeanMs=rows.Average(x=>x.BackgroundMs),
            LayoutMeanMs=rows.Average(x=>x.LayoutMs),DrawMeanMs=rows.Average(x=>x.DrawMs),
            EncodeMeanMs=rows.Average(x=>x.EncodeMs),
            PixelIdentityVsDiagnosticReference=allPixelsEqual?"PASS":"FAIL",
            NoDiagnosticWrites=allNoWrites?"PASS":"FAIL",DiagnosticsOff=allDiagnosticsOff?"PASS":"FAIL",
            CoreBlockIdContract=blockIdsStable?"PASS":"FAIL",Candidate="NO",
            Status=allPixelsEqual&&allNoWrites&&allDiagnosticsOff&&blockIdsStable?"PASS":"FAIL"
        };
        File.WriteAllText(Path.Combine(outputRoot,"RUN-SUMMARY.json"),JsonSerializer.Serialize(summary,JsonOptions));
        return summary.Status=="PASS"?0:21;
    }

    private static string HashFile(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static double Percentile(double[] values,double fraction)
    {
        if(values.Length==0)return 0;var index=(values.Length-1)*fraction;var lo=(int)Math.Floor(index);var hi=(int)Math.Ceiling(index);
        return lo==hi?values[lo]:values[lo]+(values[hi]-values[lo])*(index-lo);
    }
    private static void WriteCsv(string path,IEnumerable<Row> rows)
    {
        static string Q(object value)=>'"'+Convert.ToString(value,CultureInfo.InvariantCulture)!.Replace("\"","\"\"")+'"';
        var header=new[]{"PairId","Set","Iteration","Width","Height","Blocks","AtomicCommits","StyleMs","BackgroundMs","LayoutMs","DrawMs","EncodeMs","MeasuredTotalMs","WallMs","DiagnosticsOff","PngWrites","JsonWrites","NoWrites","ReferencePresent","PixelsEqual","ProductSha256","ReferenceSha256","BlockIdsStable"};
        File.WriteAllLines(path,new[]{string.Join(',',header.Select(Q))}.Concat(rows.Select(x=>string.Join(',',new object[]{x.PairId,x.Set,x.Iteration,x.Width,x.Height,x.Blocks,x.AtomicCommits,x.StyleMs,x.BackgroundMs,x.LayoutMs,x.DrawMs,x.EncodeMs,x.MeasuredTotalMs,x.WallMs,x.DiagnosticsOff,x.PngWrites,x.JsonWrites,x.NoWrites,x.ReferencePresent,x.PixelsEqual,x.ProductSha256,x.ReferenceSha256,x.BlockIdsStable}.Select(Q)))),new UTF8Encoding(true));
    }
    private sealed record PairingInput(string PairId,string Set,string SourcePath,string ReferencePath,string ProductInputPath,string SourceFormat);
    private sealed record Row(string PairId,string Set,int Iteration,int Width,int Height,int Blocks,int AtomicCommits,
        double StyleMs,double BackgroundMs,double LayoutMs,double DrawMs,double EncodeMs,double MeasuredTotalMs,double WallMs,
        bool DiagnosticsOff,int PngWrites,int JsonWrites,bool NoWrites,bool ReferencePresent,bool PixelsEqual,
        string ProductSha256,string ReferenceSha256,bool BlockIdsStable);
}
