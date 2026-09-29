using System.Drawing.Imaging;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class CleanupTrace0612
{
    internal static int Run(string sourcePath,string recognitionPath,string output)
    {
        Directory.CreateDirectory(output);using var source=new Bitmap(sourcePath);using var json=JsonDocument.Parse(File.ReadAllText(recognitionPath));
        var regions=json.RootElement.GetProperty("regions").EnumerateArray().Select(ReadRegion).ToArray();var traces=new List<object>();
        foreach(var region in regions.Where(x=>x.OcrText.Contains("How did you know",StringComparison.OrdinalIgnoreCase)||x.OcrText.Trim().Equals("Bye",StringComparison.OrdinalIgnoreCase)))
        {
            var plan=BackgroundIntegrationPlanner.Plan(source,region,region.BoundingBox,regions);using var cleaned=BackgroundIntegrationExecutor.Execute(source,plan);var audit=StableSourceAudit.MeasureCleanup(source,cleaned.Bitmap,plan,true);
            var slug=region.OcrText.StartsWith("Bye",StringComparison.OrdinalIgnoreCase)?"bye":"name";cleaned.Bitmap.Save(Path.Combine(output,slug+"-cleanup.png"),ImageFormat.Png);
            using(var mask=new Bitmap(source.Width,source.Height)){for(var y=plan.CleanupBounds.Top;y<plan.CleanupBounds.Bottom;y++)for(var x=plan.CleanupBounds.Left;x<plan.CleanupBounds.Right;x++)if(plan.CleanupMask[x,y])mask.SetPixel(x,y,Color.White);mask.Save(Path.Combine(output,slug+"-mask.png"),ImageFormat.Png);}
            traces.Add(new{region.RegionId,region.OcrText,region.SourceBlockIds,SourcePolygons=region.SourceLinePolygons,SourceInkBounds=plan.CleanupBounds,plan.MaskBasis,plan.BackgroundType,plan.ReconstructionStrategy,plan.Confidence,plan.SafeToCommit,plan.FailureReason,cleaned.Committed,cleaned.ValidationFailure,ActualPixelsModified=cleaned.ChangedPixels,RenderRect=region.TranslationRenderRect,FinalResidual=audit});
        }
        File.WriteAllText(Path.Combine(output,"CLEANUP-TRACE.json"),JsonSerializer.Serialize(traces,new JsonSerializerOptions{WriteIndented=true}));return traces.Count==2?0:2;
    }
    private static RecognitionRegion ReadRegion(JsonElement e)
    {
        var roleText=e.GetProperty("role").GetString()??"Unknown";Enum.TryParse<RegionRoleType>(roleText,true,out var role);
        PointF[] Polygon(JsonElement a)=>a.EnumerateArray().Select(p=>new PointF(p[0].GetSingle(),p[1].GetSingle())).ToArray();
        var polygon=Polygon(e.GetProperty("polygon"));var region=new RecognitionRegion{RegionId=e.GetProperty("RegionId").GetString()!,Polygon=polygon,RendererTargetRegion=GeometryV2.Bounds(polygon),RoleType=role,OcrText=e.GetProperty("OcrText").GetString()??"",StructuredText=e.GetProperty("StructuredText").GetString()??"",ReadingOrder=e.GetProperty("ReadingOrder").GetInt32(),CoverageValid=true};
        foreach(var id in e.GetProperty("SourceBlockIds").EnumerateArray())region.SourceBlockIds.Add(id.GetString()!);foreach(var line in e.GetProperty("sourceLinePolygons").EnumerateArray())region.SourceLinePolygons.Add(Polygon(line));return region;
    }
}
