using System.Globalization;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// The database supplies identity/name only. A party member, face graphic or
// display-name match must never authorize applying gender to a message.
internal static class RpgCharacterRoster
{
    internal static IReadOnlyDictionary<string,string> Read(JsonElement root,bool mz,Action check)
    {
        var names=new Dictionary<string,string>(StringComparer.Ordinal);var duplicate=new HashSet<string>(StringComparer.Ordinal);var seen=new HashSet<string>(StringComparer.Ordinal);
        if(root.ValueKind!=JsonValueKind.Array)return names;
        foreach(var actor in root.EnumerateArray())
        {
            check();
            if(actor.ValueKind!=JsonValueKind.Object||!actor.TryGetProperty("id",out var id)||id.ValueKind!=JsonValueKind.Number||!id.TryGetInt32(out int number)||number<=0)continue;
            string key=(mz?"mz:":"mv:")+"actor:"+number.ToString(CultureInfo.InvariantCulture);
            if(!seen.Add(key))duplicate.Add(key);
            if(actor.TryGetProperty("name",out var name)&&name.ValueKind==JsonValueKind.String&&name.GetString() is {Length:>0 and <=200} label&&!label.Any(char.IsControl))names.TryAdd(key,label);
        }
        foreach(string key in duplicate)names.Remove(key);
        return names;
    }

    internal static IReadOnlyDictionary<string,string> ReadRgss(RubyMarshalData.Node root,string extension,Action check)
    {
        string family=extension.ToLowerInvariant() switch{".rxdata"=>"xp",".rvdata"=>"vx",".rvdata2"=>"vxace",_=>""};
        var result=new Dictionary<string,string>(StringComparer.Ordinal);var seen=new HashSet<string>(StringComparer.Ordinal);var duplicate=new HashSet<string>(StringComparer.Ordinal);
        if(family.Length==0||root.Kind!='['||root.Items.Count>5000)return result;
        foreach(var actor in root.Items)
        {
            check();if(actor.Kind!='o'||actor.Name!="RPG::Actor"||!actor.Fields.TryGetValue("@id",out var id)||id.Kind!='i'||id.Number<=0)continue;
            string key="rgss:"+family+":actor:"+id.Number.ToString(CultureInfo.InvariantCulture);if(!seen.Add(key))duplicate.Add(key);
            if(!actor.Fields.TryGetValue("@name",out var name)||name.Kind!='"')continue;
            string label=RgssTextCatalog.Decode(name);if(label.Length is >0 and <=200&&!label.Any(char.IsControl))result.TryAdd(key,label);
        }
        foreach(string key in duplicate)result.Remove(key);return result;
    }
}
