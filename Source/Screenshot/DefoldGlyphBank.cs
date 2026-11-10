using System.Text;
using static ScreenshotTranslationUiTester.DefoldWire;
namespace ScreenshotTranslationUiTester;

// Defold 1.9.8 font_ddf.proto: the font map retains material/style settings;
// glyph bitmap/metrics live in a separately shared GlyphBank resource.
internal static class DefoldGlyphBank
{
    private static InvalidDataException Invalid(string detail)=>new("字体格式或大小暂不支持内嵌字库扩展。",new InvalidDataException(detail));
    private static readonly Dictionary<int,int> ToLegacy=new()
    {{1,1},{2,17},{3,20},{4,21},{5,8},{6,9},{10,10},{11,11},{12,12},{13,13},{14,14},{15,15},{16,16},{17,18},{18,19},{19,25}};
    internal static string? Resource(byte[] map)
    {
        var fields=Read(map);var first=fields.Where(f=>f.Number==1).ToArray();
        if(first.Length!=1||first[0].Kind!=2||first[0].Data.Length==0||first[0].Data[0]!=(byte)'/')return null;
        string path=new UTF8Encoding(false,true).GetString(first[0].Data);
        if(path.Length>512||!path.EndsWith(".glyph_bankc",StringComparison.Ordinal)||path.Contains('\\')||path.Split('/').Any(s=>s is "." or ".."))throw Invalid("Unsupported glyph bank reference.");
        if(fields.GroupBy(f=>f.Number).Any(g=>g.Count()!=1)||!fields.Any(f=>f.Number==2&&f.Kind==2)||!fields.Any(f=>f.Number==3&&f.Kind==0))throw Invalid("Invalid modern font map.");
        return path;
    }
    internal static byte[] Legacy(byte[] bank)
    {
        var fields=Read(bank);
        if(fields.Where(f=>f.Number!=1).GroupBy(f=>f.Number).Any(g=>g.Count()!=1))throw Invalid("Duplicate glyph bank fields.");
        var mapped=fields.Where(f=>ToLegacy.ContainsKey(f.Number)).Select(f=>f with{Number=ToLegacy[f.Number]}).ToArray();
        if(!mapped.Any(f=>f.Number==21&&f.Kind==2)||!mapped.Any(f=>f.Number==1&&f.Kind==2))throw Invalid("Invalid glyph bank.");
        return Write(mapped);
    }
    private static byte[] Restore(byte[] bank,byte[] legacy)
    {
        var reverse=ToLegacy.ToDictionary(p=>p.Value,p=>p.Key);
        var updated=Read(legacy).Select(f=>f with{Number=reverse[f.Number]}).ToArray();
        // Unknown future fields and the bank's metadata survive unchanged.
        return Write(Read(bank).Where(f=>!ToLegacy.ContainsKey(f.Number)).Concat(updated));
    }
    internal static DefoldFontBuilder.Extension Extend(byte[] bank,string path,IEnumerable<int>? characters=null)
    {
        var r=DefoldFontBuilder.ExtendWithCoverage(Legacy(bank),path,characters);byte[] bytes=Restore(bank,r.Bytes);
        return new(bytes,r.Coverage with{OutputHash=CatalogOrigins.Hash(bytes)});
    }
    internal static DefoldFontBuilder.Replacement Replace(byte[] bank,string path,IEnumerable<int>? characters=null)
    {
        var r=DefoldFontBuilder.ReplaceWithCoverage(Legacy(bank),path,characters);byte[] bytes=Restore(bank,r.Bytes);
        return new(bytes,r.Coverage with{OutputHash=CatalogOrigins.Hash(bytes)});
    }
}
