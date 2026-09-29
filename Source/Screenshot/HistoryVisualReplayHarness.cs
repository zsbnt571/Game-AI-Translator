using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;
using ScreenshotTranslationUiTester.CorePipelineV2;

namespace ScreenshotTranslationUiTester;

// Explicit local evidence entry. It never loads, migrates or updates the user's History store.
internal static class HistoryVisualReplayHarness
{
    private static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true,PropertyNameCaseInsensitive=true};
    private sealed record HistoryReplayCase(string Id,string SourcePath,
        [property:System.Text.Json.Serialization.JsonPropertyName("StoredCoreSnapshotPath")] string? SnapshotPath,
        string SourceSHA256,string? ReferencePath);

    internal static int Run(string output,string manifestPath,string settingsPath)
    {
        var cases=JsonSerializer.Deserialize<HistoryReplayCase[]>(File.ReadAllText(manifestPath),JsonOptions)
            ??throw new InvalidDataException("History replay manifest is missing");
        if(cases.Length==0||cases.Select(x=>x.Id).Distinct(StringComparer.Ordinal).Count()!=cases.Length)
            throw new InvalidDataException("History replay requires unique named cases");
        Directory.CreateDirectory(output);
        var settings=ConfigurationManager.Load(settingsPath,false);
        var profile=FontManager.ResolveTranslationImageProfile(settings);
        var rows=new List<object>();var completed=0;var failed=0;
        foreach(var item in cases)
        {
            if(item.Id!=Path.GetFileName(item.Id)||item.Id.IndexOfAny(Path.GetInvalidFileNameChars())>=0)
                throw new InvalidDataException("History case id must be a file name");
            var sourceHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(item.SourcePath)));
            if(!sourceHash.Equals(item.SourceSHA256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("History source identity changed: "+item.Id);
            if(string.IsNullOrWhiteSpace(item.SnapshotPath)||!File.Exists(item.SnapshotPath))
            {
                rows.Add(new{item.Id,Status="SKIP_NO_STORED_CORE",SourceSHA256=sourceHash,
                    Reason="No stored Core or accepted mapping; a current OCR/API replay is a separate experiment"});
                continue;
            }
            using var source=new Bitmap(item.SourcePath);
            var snapshot=JsonSerializer.Deserialize<HistoryCoreSnapshot>(File.ReadAllText(item.SnapshotPath),JsonOptions)
                ??throw new InvalidDataException("Invalid stored Core snapshot: "+item.Id);
            if(snapshot.CanvasWidth!=source.Width||snapshot.CanvasHeight!=source.Height)
                throw new InvalidDataException("Stored Core canvas differs from source; refusing implicit coordinate scaling");
            var core=snapshot.Rebuild(source.Size);
            var fixture=Path.Combine(output,"fixtures",item.Id);Directory.CreateDirectory(fixture);
            source.Save(Path.Combine(fixture,"SOURCE.png"),ImageFormat.Png);
            var audits=CorePipelineCorpusRunner.Render(source,core,fixture,profile);
            var product=CorePipelineCorpusRunner.RenderProduct(source,core,profile);
            using var productBitmap=product.Bitmap;
            using var final=new Bitmap(Path.Combine(fixture,"07-FINAL-TRANSLATED.png"));
            long difference=0;
            if(final.Size!=productBitmap.Size)throw new InvalidDataException("Diagnostic/product canvas differs");
            for(var y=0;y<final.Height;y++)for(var x=0;x<final.Width;x++)
                if(final.GetPixel(x,y).ToArgb()!=productBitmap.GetPixel(x,y).ToArgb())difference++;
            productBitmap.Save(Path.Combine(fixture,"PRODUCT-DIAGNOSTICS-OFF.png"),ImageFormat.Png);
            var currentIds=core.VisualBlocks.Select(x=>x.BlockId).ToHashSet(StringComparer.Ordinal);
            var unmatched=snapshot.AcceptedTranslations.Keys.Where(x=>!currentIds.Contains(x)).ToArray();
            var accepted=core.Translations.Values.Count(x=>x.State==BlockTranslationState.Accepted);
            File.WriteAllText(Path.Combine(fixture,"HISTORY-REPLAY-BOUNDARIES.json"),JsonSerializer.Serialize(new
            {
                SchemaVersion=2,item.Id,SourceSHA256=sourceHash,
                SnapshotSHA256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(item.SnapshotPath))),
                Provenance="Actual selected History Core snapshot reconstructed with the current product Rebuild method; source JPEG is not the original lossless capture",
                AcceptedReplaySnapshot=snapshot.AcceptedTranslations,
                ProductionRawCaptured=(string?)null,ParserReturned=(string?)null,
                UncapturedReason="Stored History has no original HTTP body or parser observation; this run performs zero API requests",
                AcceptedMapping=core.Translations.Where(x=>x.Value.State==BlockTranslationState.Accepted)
                    .ToDictionary(x=>x.Key,x=>x.Value.TranslatedText,StringComparer.Ordinal),
                LayoutInput="See TEXT-FIT-TRACE.json LayoutInput for actual normalized wrapping input",
                SourcePreservedOrFailed=core.Translations.Where(x=>x.Value.State!=BlockTranslationState.Accepted)
                    .Select(x=>new{x.Key,x.Value.State,x.Value.FailureReason}),
                UnmatchedStoredAcceptedIds=unmatched,RawLines=core.RawLines.Count,VisualBlocks=core.VisualBlocks.Count,
                DiagnosticsOff=product.Timing,RealApiCalls=0,FreshOcr=false,UserVisualAcceptance="NOT_PASSED"
            },JsonOptions));
            var pass=difference==0&&!product.Timing.DiagnosticsEnabled&&product.Timing.DiagnosticJsonWrites==0&&
                product.Timing.FullFrameDiagnosticPngWrites==0&&unmatched.Length==0;
            if(!pass)failed++;completed++;
            rows.Add(new{item.Id,Status=pass?"REPLAY_CONTRACT_PASS":"REPLAY_CONTRACT_FAIL",SourceSHA256=sourceHash,
                ChangedPixelsDiagnosticsOnOff=difference,AcceptedBlocks=accepted,UnmatchedStoredAcceptedIds=unmatched,
                AtomicCommits=audits.Count(x=>x.AtomicCommit),Output=fixture});
        }
        File.WriteAllText(Path.Combine(output,"HISTORY-REPLAY-SUMMARY.json"),JsonSerializer.Serialize(new
        {
            Cases=cases.Length,Completed=completed,Failed=failed,RealApiCalls=0,FreshOcr=false,Rows=rows,
            Scope="Stored Core/translation renderer isolation and diagnostics equivalence, not proof that a new OCR/API request reproduces the stored result",
            UserVisualAcceptance="NOT_PASSED"
        },JsonOptions));
        return completed>0&&failed==0?0:1;
    }
}
