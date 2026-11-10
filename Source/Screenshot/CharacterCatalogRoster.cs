using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

// Name discovery is independent from dialogue attribution. These records may
// populate the shared editor, but never enter the source-to-speaker maps.
internal static class CharacterCatalogRoster
{
    internal static IReadOnlyDictionary<string,string[]> Read(RpgTextCatalogResult catalog)
    {
        var result=catalog.CharacterRoster.Take(5000)
            .Where(p=>GameCharacterBinding.ValidId(p.Key)&&ValidLabel(p.Value))
            .ToDictionary(p=>p.Key,p=>new[]{p.Value},StringComparer.Ordinal);
        if(catalog.OriginsTruncated)return result;
        var fields=new List<(string Id,string Position,string Name)>();
        foreach(var origin in catalog.Origins.Take(50000))
        {
            if(origin.Kind!="wolf-database-string"||!ValidLabel(origin.Text)||GameTerminologyClassification.Kind(origin)!="人物"
                ||origin.File.Length==0||origin.SchemaFile.Length==0||!Hash(origin.ContentHash)||!Hash(origin.SchemaHash)
                ||!Regex.IsMatch(origin.Location,@"\Adatabase/type:[0-9]+/row:[0-9]+/field:[0-9]+\z",RegexOptions.CultureInvariant))continue;
            // Only explicitly language-labelled sibling name fields share an
            // identity. Different fields/rows, or merely equal names, do not.
            string aliases=GameTerminologyClassification.Identity(origin);
            string native=aliases.Length>0?aliases:origin.File.Replace('\\','/')+"/"+origin.Location;
            string key="wolf:"+GameCharacterBinding.Hash(origin.Container+"\0"+origin.SchemaFile.Replace('\\','/').ToUpperInvariant()+"\0"+native.ToUpperInvariant());
            fields.Add((key,origin.File+"\0"+origin.Location,origin.Text));
        }
        foreach(var group in fields.GroupBy(f=>f.Id,StringComparer.Ordinal).Take(5000-result.Count))
        {
            if(group.GroupBy(f=>f.Position,StringComparer.Ordinal).Any(g=>g.Select(f=>f.Name).Distinct(StringComparer.Ordinal).Count()>1))continue;
            result[group.Key]=group.Select(f=>f.Name).Distinct(StringComparer.Ordinal).Take(16).ToArray();
        }
        return result;
    }
    private static bool Hash(string value)=>value.Length==64&&value.All(Uri.IsHexDigit);
    private static bool ValidLabel(string? value)=>value is {Length:>0 and <=200}&&!value.Any(char.IsControl);
}
