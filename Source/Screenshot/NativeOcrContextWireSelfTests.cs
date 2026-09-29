using System.Drawing;
using System.Reflection;
using System.Text.Json;
using ScreenshotTranslationUiTester.CorePipelineV2;

namespace ScreenshotTranslationUiTester;

internal static class NativeOcrContextWireSelfTests
{
    internal static int Run(string output,string evidenceRoot)
    {
        Directory.CreateDirectory(output);
        var rows=new List<object>(); var failed=0; var totalCandidates=0; var contextItems=0;
        var method=typeof(PreviewForm).GetMethod("BuildCorePipelineV2",BindingFlags.Static|BindingFlags.NonPublic,
            null,[typeof(OcrEngineResult),typeof(Size)],null)!;
        foreach(var path in Directory.GetFiles(evidenceRoot,"*.json").OrderBy(x=>x,StringComparer.Ordinal))
        {
            using var document=JsonDocument.Parse(File.ReadAllText(path));
            var root=document.RootElement;
            if(!root.TryGetProperty("wire",out var wire)||!root.TryGetProperty("source",out var source))continue;
            var sourcePath=source.GetString()!;
            // Read-only registered evidence paths. Never use History loading/migration to obtain input.
            if(!Path.GetFullPath(sourcePath).StartsWith(ValidationPaths.RequiredRoot("ST_FIX_READONLY_INPUT_ROOT")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Wire evidence source must be below the registered read-only evidence root");
            using var image=Image.FromFile(sourcePath);
            var blocks=ExternalOcrEngine.ParseWireBlocksForTest(wire.GetRawText(),sourcePath,image.Size,OcrEngineKind.Rapid);
            var result=new OcrEngineResult {Blocks=blocks.ToList()};
            var core=(CorePipelineDocument)method.Invoke(null,[result,image.Size])!;
            var items=core.VisualBlocks.Select(x=>CoreTranslationItemFactory.Create(core,x)).ToArray();
            var candidates=blocks.SelectMany(x=>x.OcrAlternatives).ToArray();
            var contexts=items.Where(OcrTranslationContextContract.IsApplicable).ToArray();
            totalCandidates+=candidates.Length;contextItems+=contexts.Length;
            var primaryUntouched=blocks.Select(x=>x.RawText).SequenceEqual(wire.GetProperty("blocks").EnumerateArray()
                .Select(x=>x.GetProperty("text").GetString()));
            var sourceHash=OcrAlternativeEvidenceValidator.FileHash(sourcePath);
            var verified=candidates.All(x=>x.Verified);
            var primaryCopy=blocks.Select(x=>new OcrEngineBlock {Id=x.Id,RawText=x.RawText,
                NormalizedText=x.NormalizedText,BoundingBox=x.BoundingBox,Polygon=x.Polygon.ToArray(),
                ReadingOrder=x.ReadingOrder,LineIndex=x.LineIndex,Confidence=x.Confidence}).ToList();
            var oldCore=(CorePipelineDocument)method.Invoke(null,[new OcrEngineResult{Blocks=primaryCopy},image.Size])!;
            var stableIds=core.VisualBlocks.Select(x=>x.BlockId).SequenceEqual(oldCore.VisualBlocks.Select(x=>x.BlockId));
            var rawUnchanged=core.RawLines.Select(x=>x.RawText).SequenceEqual(oldCore.RawLines.Select(x=>x.RawText));
            var noFullBadName=contexts.All(x=>!TranslationService.BuildStructuredInputForTest([x]).Contains("opnella",StringComparison.OrdinalIgnoreCase));
            var pass=primaryUntouched&&verified&&stableIds&&rawUnchanged&&noFullBadName;
            if(!pass)failed++;
            rows.Add(new {Fixture=Path.GetFileNameWithoutExtension(path),SourcePath=sourcePath,SourceHash=sourceHash,
                Pass=pass,PrimaryUntouched=primaryUntouched,EveryCandidateVerified=verified,
                StableBlockIds=stableIds,RawUnchanged=rawUnchanged,
                AlternativeCount=candidates.Length,VerifiedCandidates=candidates.Count(x=>x.Verified),
                RejectedCandidates=candidates.Where(x=>!x.Verified).Select(x=>new{x.Text,x.ValidationReason}).ToArray(),
                Contexts=contexts.Select(x=>new{x.Id,x.Text,x.OcrContext}).ToArray(),
                FullDegradedNameNotOffered=noFullBadName,PromptRequests=0});
        }
        File.WriteAllText(Path.Combine(output,"ACTUAL-WIRE-CONTEXT.json"),JsonSerializer.Serialize(
            new{Files=rows.Count,Failed=failed,TotalCandidates=totalCandidates,ContextItems=contextItems,Rows=rows,
                EvidenceKind="Stored actual worker response through the same C# production parser and Preview Core builder; fixed replay, zero API."},
            new JsonSerializerOptions{WriteIndented=true}));
        return failed==0&&rows.Count>0?0:1;
    }
}
