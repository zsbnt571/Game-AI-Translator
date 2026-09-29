using System.Diagnostics;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class RealPathDiagnosticTrace
{
    private static readonly object Gate=new();
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    // Resolve on access: an early type touch must not cache the pre-initialization default.
    internal static string Root=>Path.Combine(
        AppDataPaths.HasExplicitRoot?AppDataPaths.LogsRoot:AppContext.BaseDirectory,"diagnostics");
    private static string Session(string area){var p=Path.Combine(Root,area);Directory.CreateDirectory(p);return p;}
    internal static void Stage(string stage,IEnumerable<object> rows)
    {lock(Gate){var p=Path.Combine(Session("pipeline"),$"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{stage}.json");File.WriteAllText(p,JsonSerializer.Serialize(new{timestamp=DateTimeOffset.Now,stage,rows=rows.ToArray()},Json));}}
    internal static object OcrRow(OcrEngineBlock b)=>new{Text=string.IsNullOrWhiteSpace(b.CorrectedText)?b.RawText:b.CorrectedText,SourceIds=new[]{b.Id},RegionIds=Array.Empty<string>(),Role="OCR",OwnerId=b.Id,Polygon=b.Polygon,Bounds=b.BoundingBox};
    internal static object RegionRow(RecognitionRegion r)=>new{Text=string.IsNullOrWhiteSpace(r.StructuredText)?r.CorrectedText:r.StructuredText,SourceIds=r.SourceBlockIds,RegionIds=new[]{r.RegionId},Role=r.RoleType.ToString(),OwnerId=r.RegionId,Polygons=r.SourceLinePolygons,Bounds=r.BoundingBox,r.TranslationRenderRect,r.PreserveOriginal,TranslationUnitId=r.TranslationUnitId,Translation=r.TranslationText};
    internal static object UnitRow(TranslationUnitV2 u)=>new{Text=u.Text,SourceIds=u.StableSourceIds,RegionIds=u.RegionIds,Role=u.RoleType.ToString(),OwnerId=u.Id};
    internal static void Highlight(object row){lock(Gate)File.AppendAllText(Path.Combine(Session("highlight"),"ownership.jsonl"),JsonSerializer.Serialize(row)+Environment.NewLine);}
    internal static string Hash(Bitmap b){using var ms=new MemoryStream();b.Save(ms,ImageFormat.Png);return Convert.ToHexString(SHA256.HashData(ms.ToArray()));}
    internal static string ChoiceDir(string regionId){var p=Path.Combine(Session("ophelia-choice"),DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-"+Safe(regionId));Directory.CreateDirectory(p);return p;}
    internal static void SaveMask(bool[,] mask,string path){using var b=new Bitmap(mask.GetLength(0),mask.GetLength(1),PixelFormat.Format32bppArgb);for(var y=0;y<b.Height;y++)for(var x=0;x<b.Width;x++)b.SetPixel(x,y,mask[x,y]?Color.White:Color.Black);b.Save(path);}
    internal static int Count(bool[,] mask){var n=0;foreach(var v in mask)if(v)n++;return n;}
    internal static void WriteJson(string path,object value){lock(Gate)File.WriteAllText(path,JsonSerializer.Serialize(value,Json));}
    internal static void Pointer(object row){lock(Gate){var path=Path.Combine(Session("pointer"),"events.jsonl");File.AppendAllText(path,JsonSerializer.Serialize(row)+Environment.NewLine);}}
    private static string Safe(string s)=>string.Concat(s.Select(c=>char.IsLetterOrDigit(c)||c is '-' or '_'?c:'_'));
}
