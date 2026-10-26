using System.Text.RegularExpressions;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// A readable field name can suggest a useful variable; it is not proof of a
// game's schema. Candidates retain their original path and never auto-write.
internal static class ModificationCommonCandidates
{
    internal sealed record Candidate(JsonElement Source,string Caption,string SectionKey,string SectionTitle,string GroupKey,string GroupTitle,bool Verified=false,string AttributeGroup="",string AttributeTitle="",int Order=0);

    // These are presentation hints for a live snapshot, not an engine schema.
    // A CDB cell can be live memory and still belong to an unused/template row.
    internal static Candidate[] Build(IEnumerable<JsonElement> source,string? adapterId=null,IReadOnlyDictionary<string,string>? namingContext=null)
    {
        var rows=source.ToArray();
        var sampleStarts=new Dictionary<int,int>();
        var names=namingContext??rows.GroupBy(row=>Text(row,"id")).ToDictionary(group=>group.Key,group=>Text(group.First(),"name"));
        foreach(var entry in names)
        {
            if(!DatabaseIdentity(entry.Key,out int table,out int record))continue;
            var parts=Parts(entry.Value);
            if(parts.Length>=3&&Regex.IsMatch(parts[^2],@"(?:以下|ここから|\bbelow\b|\bfollowing\b).*(?:サンプル|样例|示例|sample|template)|(?:sample|template).*(?:below|following)",RegexOptions.IgnoreCase))
                sampleStarts[table]=Math.Min(sampleStarts.GetValueOrDefault(table,int.MaxValue),record);
        }
        var candidates=new List<Candidate>();
        var resourceObjects=names.Where(entry=>
        {
            var path=Parts(entry.Value);var attribute=ModificationFieldSemantics.Character(path[^1]);
            return path.Length>=2&&attribute?.Group=="resources";
        }).Select(entry=>DatabaseIdentity(entry.Key,out int table,out int record)?$"db:{table}:{record}":"path:"+string.Join(" / ",Parts(entry.Value)[..^1])).ToHashSet(StringComparer.Ordinal);
        foreach(var row in rows)
        {
            if(!row.TryGetProperty("value",out var value))continue;
            bool numeric=Text(row,"type")=="number"&&value.ValueKind==JsonValueKind.Number&&value.TryGetDecimal(out _);
            bool boolean=Text(row,"type")=="boolean"&&value.ValueKind is JsonValueKind.True or JsonValueKind.False;
            if(!numeric&&!boolean||row.TryGetProperty("readOnly",out var readOnly)&&readOnly.ValueKind==JsonValueKind.True
                ||row.TryGetProperty("canWrite",out var canWrite)&&canWrite.ValueKind==JsonValueKind.False)continue;
            string id=Text(row,"id"),name=Text(row,"name");var parts=Parts(name);
            bool database=DatabaseIdentity(id,out int table,out int record);
            if(database&&(parts.Length<3||sampleStarts.TryGetValue(table,out int start)&&record>=start))continue;
            string collection=ExcludedContext(name)?"":ModificationFieldSemantics.Recollection(parts[^1],parts);
            if(collection.Length>0&&parts.Length>=2)
            {
                if(!UsableObject(parts[^2])&&!string.IsNullOrWhiteSpace(parts[^2]))continue;
                bool registration=collection=="回想登记值";
                // An unnamed database record is still addressable, but it is
                // not a named CG or event. Show its stable location explicitly.
                string collectionOwner=UsableObject(parts[^2])?string.Join(" / ",parts[..^1]):parts[0];
                string objectName=database?$"{collectionOwner} [CDB {table}:{record}]":collectionOwner;
                candidates.Add(new(row,collection,registration?"recollection-registration":"recollection",
                    registration?"回想登记（用途待核对）":"可能的 CG / 回想状态",database?$"db:{table}:{record}":"path:"+objectName,objectName));
                continue;
            }
            if(database&&!UsableObject(parts[^2]))continue;
            if(!numeric)continue;
            string label=Label(name);bool trustedParty=adapterId=="yukar-bakin"&&name=="队伍.金钱";
            if(label.Length==0&&parts.Length==1&&name.Contains('.')&&!ExcludedContext(name)
                &&!Regex.IsMatch(name,@"(?:^|[._])(?:max|maximum|price|cost|spent|reward|gain|earned|total|limit|loss|damage)(?:[._]|$)",RegexOptions.IgnoreCase))
                label=Label(name[(name.LastIndexOf('.')+1)..]);
            if(trustedParty)label="金钱";
            if(label.Length==0)continue;
            if(label=="金钱")
            {
                // Only a bridge-owned typed field is verified. A single direct
                // money variable is a useful suggestion; database cells remain
                // explicitly tentative even when the name says 'current'.
                bool direct=trustedParty||!database&&parts.Length==1;
                candidates.Add(new(row,label,direct?"":"currency",direct?"":"金钱（待核对）","","",trustedParty));
                continue;
            }
            string owner="",group="";
            if(database)
            {owner=string.Join(" / ",parts[..^1]);group=$"db:{table}:{record}";}
            else if(parts.Length>=2&&UsableObject(parts[^2]))
            {owner=string.Join(" / ",parts[..^1]);group="path:"+owner;}
            else
            {
                var path=Regex.Match(name,@"^(.*?)[._]([^. _]+)$");
                if(path.Success&&Label(path.Groups[2].Value).Length>0&&UsableObject(path.Groups[1].Value))
                {owner=path.Groups[1].Value;group="path:"+owner;}
            }
            // Unknown ownership stays in the full variables/database view;
            // a matching HP word alone does not create a character category.
            if(owner.Length==0)continue;
            bool relationship=label=="好感 / 关系";
            var attribute=ModificationFieldSemantics.Character(parts.Length>1?parts[^1]:name[(Math.Max(name.LastIndexOf('.'),name.LastIndexOf('_'))+1)..]);
            // Attack/level can also be an item's requirements. New non-resource
            // attributes need an actor schema or resource fields on that object.
            if(attribute is not null)
            {
                string schema=parts.Length>=3?parts[0]:"";
                if(Regex.IsMatch(schema,@"アイテム|武器|防具|道具|物品|スキル|魔法|\b(?:items?|weapons?|armou?rs?|skills?|spells?)\b",RegexOptions.IgnoreCase))continue;
                if(attribute.Group!="resources"&&!resourceObjects.Contains(group)
                    &&!Regex.IsMatch(schema,@"主人公|キャラ|ステータス|角色|人物|隊員|队员|\b(?:actors?|characters?|party|enemies|enemy|stats|status)\b",RegexOptions.IgnoreCase))continue;
            }
            candidates.Add(new(row,label,relationship?"relationships":"characters",relationship?"关系（按对象）":"角色属性（按对象）",group,owner,
                AttributeGroup:attribute?.Group??"",AttributeTitle:attribute?.GroupTitle??"",Order:attribute?.Order??0));
        }
        // Multiple possible wallets must not silently become multiple 'the
        // current money' controls. Keep every candidate and its original ID.
        if(candidates.Count(item=>item.Caption=="金钱")>1)
            for(int i=0;i<candidates.Count;i++)
                if(candidates[i].Caption=="金钱"&&!candidates[i].Verified)
                    candidates[i]=candidates[i] with{SectionKey="currency",SectionTitle="金钱（待核对）"};
        return candidates.OrderBy(item=>item.SectionKey switch{""=>0,"currency"=>1,"recollection"=>2,"recollection-registration"=>3,"characters"=>4,_=>5}).ToArray();
    }

    internal static JsonElement[] BoardRows(IEnumerable<Candidate> candidates,Func<string,string>? display=null)=>candidates.Select(item=>JsonSerializer.SerializeToElement(new {
        key=Text(item.Source,"id"),name=Text(item.Source,"name"),caption=item.Caption,
        source=display?.Invoke(Text(item.Source,"name"))??Text(item.Source,"name"),original=Text(item.Source,"name"),type=Text(item.Source,"type"),value=item.Source.GetProperty("value"),
        locked=item.Source.TryGetProperty("locked",out var locked)&&locked.ValueKind==JsonValueKind.True,
        canLock=!item.Source.TryGetProperty("canLock",out var canLock)||canLock.ValueKind!=JsonValueKind.False,
        min=item.Source.TryGetProperty("min",out var min)&&min.TryGetDecimal(out var lower)?lower:decimal.MinValue,
        max=item.Source.TryGetProperty("max",out var max)&&max.TryGetDecimal(out var upper)?upper:decimal.MaxValue,
        sectionKey=item.SectionKey,sectionTitle=item.SectionTitle,sectionExpanded=item.SectionKey is "characters" or "relationships",groupKey=item.GroupKey,groupTitle=DisplayGroup(item.GroupTitle,display),groupOriginal=item.GroupTitle,verified=item.Verified,
        attributeGroup=item.AttributeGroup,attributeTitle=item.AttributeTitle,attributeOrder=item.Order
    })).ToArray();
    private static string DisplayGroup(string title,Func<string,string>? display)
    {
        if(display is null)return title;
        var identity=Regex.Match(title,@"\s*(\[CDB [^\]]+\])$");
        return identity.Success?display(title[..identity.Index])+" "+identity.Groups[1].Value:display(title);
    }
    private static string Text(JsonElement row,string key)=>row.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()??"":"";
    private static string[] Parts(string name)=>Regex.Replace(name,@"\s*\[CDB [^\]]+\]\s*$","").Split(" / ",StringSplitOptions.TrimEntries);
    private static bool DatabaseIdentity(string id,out int table,out int row)
    {var parts=id.Split(':');table=row=-1;return parts.Length==4&&parts[0]=="db"&&int.TryParse(parts[1],out table)&&int.TryParse(parts[2],out row);}
    private static bool UsableObject(string name)=>!string.IsNullOrWhiteSpace(name)&&!Regex.IsMatch(name,@"^(?:未使用|未設定|未设定|空白|unused|none|null|戻る|[-_=─━・\s]+)$",RegexOptions.IgnoreCase);
    private static bool ExcludedContext(string name)=>Regex.IsMatch(Regex.Replace(name,@"([a-z])([A-Z])","$1 $2").Replace('_',' '),@"クリアデータ|履歴|初期|バックアップ|サンプル|テンプレート|存档备份|历史|初始|样例|示例|模板|\b(?:clear(?:ed)? data|backup|history|initial (?:values?|settings?|data|stats)|default(?:s| values?)?|sample(?:s)?|template(?:s)?)\b",RegexOptions.IgnoreCase);
    internal static string Label(string name)
    {
        var parts=Parts(name);
        if(ExcludedContext(name)||parts.Length>=3&&!UsableObject(parts[^2]))return "";
        string field=Regex.Replace(parts.LastOrDefault()??name,@"\s*\[CDB [^\]]+\]\s*$","");
        // Some databases use one row per statistic and a generic primary value
        // column. A second/maximum/reward column must not inherit the row label.
        if(parts.Length>=3&&Regex.IsMatch(field,@"^(?:数|数値|值|数值|value|amount)$",RegexOptions.IgnoreCase))field=parts[^2];
        var attribute=ModificationFieldSemantics.Character(field);
        if(attribute is not null)return attribute.Caption;
        field = Regex.Replace(field, @"([a-z])([A-Z])", "$1 $2").Replace('_', ' ').Replace('.', ' ').ToLowerInvariant().Trim();
        if (Regex.IsMatch(field, @"\b(max|maximum|price|cost|spent|reward|gain|earned|total|limit|loss|damage)\b|上限|最大|価格|報酬|奖励|消費|消耗|増加|增加|減少|减少|获得|获得量")) return "";
        if (Regex.IsMatch(field, @"^(?:(?:player|party|current|hero|mc)\s+)?(?:money|gold|cash|funds|currency|wallet|coins?)(?:\s+(?:amount|value|count|balance))?$|^(?:所持金|所持金額|持有金钱|金钱|金币|资金|資金|お金|ゴールド)$")) return "金钱";
        if (Regex.IsMatch(field, @"(?:^|\s)(?:affection|relationship|friendship)(?:\s|$)|好感度|親密度|亲密度|友好度")) return "好感 / 关系";
        return "";
    }
    internal static string InventoryScope(string name)
    {
        var parts=name.Split(" / ",StringSplitOptions.TrimEntries);
        if(parts.Length<3||string.IsNullOrWhiteSpace(parts[^2])||Regex.IsMatch(parts[^2],@"^(?:未使用|unused|none|戻る|-)$",RegexOptions.IgnoreCase))return "";
        string field=Regex.Replace(parts[^1],@"\s*\[CDB [^\]]+\]\s*$","");
        if(!Regex.IsMatch(field,@"^(?:所持個数|所持数|所持|拥有数量|持有数量|数量|quantity|count|owned|amount)$",RegexOptions.IgnoreCase))return "";
        if(Regex.IsMatch(parts[0],@"武器|weapon",RegexOptions.IgnoreCase))return "weapons";
        if(Regex.IsMatch(parts[0],@"防具|armor|armour",RegexOptions.IgnoreCase))return "armors";
        return Regex.IsMatch(parts[0],@"アイテム|道具|物品|item|inventory",RegexOptions.IgnoreCase)?"items":"";
    }

}
