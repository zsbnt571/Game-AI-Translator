using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Runtime metadata records where a bridge found text; it is not a file-system
// capability. Never resolve these paths, supply a file hash, or use these rows
// to authorize translation scheduling or resource writeback.
internal sealed class RemoteCatalogOrigins
{
    internal sealed record Page(CatalogTextOrigin[] Origins,CatalogReadIssue[] Issues);
    private readonly CatalogOrigins origins=new();
    private readonly HashSet<CatalogReadIssue> issues=[];
    internal int IssueCount=>issues.Count;
    internal static Page Read(JsonElement page,JsonElement rows,string expectedEngine="")
    {
        var values=new List<CatalogTextOrigin>();var warnings=new HashSet<CatalogReadIssue>();
        // Optional provenance can be absent or from an unknown bridge schema.
        // That leaves positions unknown, rather than invalidating text reading.
        if(page.TryGetProperty("metadata",out var metadata)&&metadata.ValueKind==JsonValueKind.Array
            &&rows.ValueKind==JsonValueKind.Array&&metadata.GetArrayLength()==rows.GetArrayLength()&&metadata.GetArrayLength()<=2048)
        {
            int index=0;
            foreach(var item in metadata.EnumerateArray())
            {
                var row=rows[index++];
                if(item.ValueKind!=JsonValueKind.Object||row.ValueKind!=JsonValueKind.String)continue;
                string text=row.GetString()!;
                if(text.Length>6000||text.Contains('\0')||!item.TryGetProperty("source",out var source)
                    ||source.ValueKind!=JsonValueKind.String||source.GetString()!=text)continue;
                string file=Text(item,"path",2048),key=Text(item,"key",2048),kind=Text(item,"kind",64);
                if(!ResourcePath(file)||kind is not ("scene" or "scene_binary" or "resource_text" or "script" or "script_tr" or "script_display" or "script_display_unconfirmed"
                    or "json" or "translation_text" or "gettext" or "gettext_source" or "optimized_value" or "translation" or "translation_plural"))continue;
                // Requested locale is diagnostic context, never the language of
                // a TranslationServer fallback result or scheduling authority.
                var localeContext=new Dictionary<string,string>();
                foreach(string name in new[]{"resourceLocale","requestedLocale","resolvedLocale","languageEvidence"})
                    if(item.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String&&value.GetString()!.Length<=64&&!value.GetString()!.Any(char.IsControl))
                        localeContext[name]=value.GetString()!;
                values.Add(new(text,file,key,"","remote-"+kind,Container:"godot-runtime-catalog",Language:Text(item,"language",64),
                    Context:localeContext.Count>0?JsonSerializer.Serialize(localeContext):""));
            }
        }
        // Display-name evidence is shared across adapters, but each receiving
        // session must opt in with its own engine identity. Never infer a name
        // from capitalization, an internal variable, or a different page.
        values.AddRange(ReadNames(page,rows,expectedEngine));
        if(page.TryGetProperty("warnings",out var warningRows))
        {
            if(warningRows.ValueKind!=JsonValueKind.Array)
                warnings.Add(new(CatalogIssueKind.Incomplete,"runtime-catalog","远程目录返回的读取限制信息无法识别。"));
            else
            {
                foreach(var warning in warningRows.EnumerateArray().Take(256))
                {
                    string reason=warning.ValueKind==JsonValueKind.String?warning.GetString()??"":"";
                    if(string.IsNullOrWhiteSpace(reason))
                    {
                        if(warning.ValueKind!=JsonValueKind.String)warnings.Add(new(CatalogIssueKind.Incomplete,"runtime-catalog","远程目录返回的读取限制信息无法识别。"));
                        continue;
                    }
                    reason=new string(reason.Where(c=>!char.IsControl(c)).Take(1024).ToArray());
                    if(reason.Length>0)warnings.Add(new(CatalogIssueKind.Incomplete,"runtime-catalog",reason));
                }
                if(warningRows.GetArrayLength()>256)warnings.Add(new(CatalogIssueKind.Limited,"runtime-catalog","远程目录的读取限制记录达到保存上限。"));
            }
        }
        return new(values.ToArray(),warnings.ToArray());
    }
    private static IEnumerable<CatalogTextOrigin> ReadNames(JsonElement page,JsonElement rows,string expectedEngine)
    {
        if(expectedEngine.Length is <1 or >32||expectedEngine.Any(c=>c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
            ||Text(page,"nameOriginContract",64)!="fusion-display-names-1"||Text(page,"nameOriginEngine",32)!=expectedEngine
            ||rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength()>2048
            ||!page.TryGetProperty("nameOrigins",out var names)||names.ValueKind!=JsonValueKind.Array||names.GetArrayLength()>2048)yield break;
        var sources=rows.EnumerateArray().Where(v=>v.ValueKind==JsonValueKind.String).Select(v=>v.GetString()!).ToHashSet(StringComparer.Ordinal);
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var item in names.EnumerateArray())
        {
            if(item.ValueKind!=JsonValueKind.Object)continue;
            string source=Text(item,"source",80),kind=Text(item,"kind",32),key=Text(item,"key",128),context=Text(item,"context",64);
            if(source.Length<2||!sources.Contains(source)||kind!="display-name"||key.Length==0||context!="character-display-name"
                ||!item.TryGetProperty("path",out var path)||path.ValueKind!=JsonValueKind.String)continue;
            string file=path.GetString()!;
            if(file.Length>512||file.Any(char.IsControl)||file.Contains('\\')||file.Contains(':')
                ||file.Length>0&&file.Split('/').Any(part=>part.Length==0||part is "." or ".."))continue;
            var origin=new CatalogTextOrigin(source,file,key,"",kind,Container:expectedEngine+"-runtime-catalog",Context:context);
            if(seen.Add(CatalogOrigins.Identity(origin)))yield return origin;
        }
    }
    internal void Add(Page page)
    {
        foreach(var origin in page.Origins)origins.Add(origin);
        foreach(var issue in page.Issues)
            if(issues.Count<255)issues.Add(issue);
            else if(!issues.Contains(issue))issues.Add(new(CatalogIssueKind.Limited,"runtime-catalog","远程目录的读取限制记录达到保存上限。"));
    }
    internal RpgTextCatalogResult Complete(IEnumerable<string> texts)=>new(texts.Distinct(StringComparer.Ordinal).ToArray(),0,[])
    {
        ReadCompleted=true,Origins=origins.Items,OriginsTruncated=origins.Truncated,
        Report=new(issues.Count==0,issues.ToArray())
    };
    private static string Text(JsonElement item,string name,int maximum)
        =>item.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String&&value.GetString() is {} text
            &&text.Length<=maximum&&!text.Any(char.IsControl)?text:"";
    private static bool ResourcePath(string value)
    {
        if(!value.StartsWith("res://",StringComparison.Ordinal)||value.Length<=6||value.Contains('\\')||value.IndexOf(':',6)>=0)return false;
        return value[6..].Split('/').All(part=>part.Length>0&&part is not ("." or ".."));
    }
}
