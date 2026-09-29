using System.Text.Json;
using System.Text.RegularExpressions;
namespace ScreenshotTranslationUiTester;

internal sealed record RpgTextCatalogResult(string[] Texts,int Files,string[] Unreadable)
{
    internal string SelectedLanguage {get;init;}="";
    internal int SkippedByLanguage {get;init;}
    // Null retains older catalogue contracts; a supplied set limits the locale
    // trust to those exact sources, leaving untagged resources subject to detection.
    internal IReadOnlySet<string>? TrustedSourceTexts {get;init;}
}
internal static class RpgTextCatalog
{
    private static readonly HashSet<string> Fields=new(StringComparer.OrdinalIgnoreCase){"name","nickname","profile","description","displayName","message1","message2","message3","message4","gameTitle","currencyUnit","terms","messages","basic","params","commands","elements","skillTypes","weaponTypes","armorTypes","equipTypes"};
    internal static bool IsText(string value)=>value.Length is >=2 and <=6000&&Regex.IsMatch(value,@"\p{L}")&&!Regex.IsMatch(value,@"^(?:[a-z]+://|[\w.-]+\.(?:png|jpg|ogg|m4a|js|json)$)",RegexOptions.IgnoreCase);
    private static string Locale(string key)
    {
        string value=key.Trim().ToLowerInvariant().Replace('-','_');
        value=value switch {"english" or "eng"=>"en","japanese" or "jp" or "jpn"=>"ja","chinese" or "simplifiedchinese" or "simplified_chinese" or "schinese"=>"zh","korean" or "kr" or "kor"=>"ko",_=>value};
        if(!Regex.IsMatch(value,@"^[a-z]{2}(?:_(?:latn|cyrl|arab|hans|hant|jpan|kore|deva|hebr|thai|grek))?(?:_(?:[a-z]{2}|[0-9]{3}))?$"))return "";
        value=value.Split('_')[0];
        return value is "en" or "ja" or "zh" or "ko" or "fr" or "de" or "es" or "pt" or "ru" or "it" or "pl" or "tr" or "uk" or "vi" or "th" or "id" or "ar" or "he" or "el" or "hi" or "nl" or "sv" or "fi" or "da" or "no" or "cs" or "hu"?value:"";
    }
    internal static RpgTextCatalogResult Read(string exe,CancellationToken token)=>Read(exe,token,SourceLanguageMode.Auto);
    internal static RpgTextCatalogResult Read(string exe,CancellationToken token,SourceLanguageMode mode)
    {
        var texts=new HashSet<string>(StringComparer.Ordinal);var excluded=new HashSet<string>(StringComparer.Ordinal);var bad=new List<string>();int files=0;
        var install=RpgMakerDataAdapter.Detect(exe);if(install is null)return new([],0,[]);
        void Add(string? value){if(value is not null&&IsText(value)&&texts.Count<100000)texts.Add(value);}
        void Exclude(JsonElement node,int depth=0)
        {
            token.ThrowIfCancellationRequested();if(depth>32)return;
            if(node.ValueKind==JsonValueKind.String){var value=node.GetString()!;if(IsText(value))excluded.Add(value);}
            else if(node.ValueKind==JsonValueKind.Array)foreach(var child in node.EnumerateArray())Exclude(child,depth+1);
            else if(node.ValueKind==JsonValueKind.Object)foreach(var field in node.EnumerateObject())Exclude(field.Value,depth+1);
        }
        bool LanguageValues(JsonElement node,out JsonElement[] values)
        {
            values=[];if(node.ValueKind!=JsonValueKind.Object)return false;
            // Only an explicit multilingual object establishes locale identity.
            // Untagged strings remain subject to the shared source-language gate.
            var fields=node.EnumerateObject().ToArray();
            var entries=fields.Select(p=>(Language:Locale(p.Name),p.Value)).Where(p=>p.Language.Length>0&&p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Array or JsonValueKind.Object).ToArray();
            if(entries.Select(p=>p.Language).Distinct(StringComparer.Ordinal).Count()<2||!entries.Any(p=>p.Language is not ("id" or "no")))return false;
            if(fields.Any(p=>Locale(p.Name).Length==0&&p.Name is not ("key" or "context" or "comment")))return false;
            string selected=mode switch {SourceLanguageMode.Mixed=>"*",SourceLanguageMode.English=>"en",SourceLanguageMode.Japanese=>"ja",SourceLanguageMode.SimplifiedChinese=>"zh",SourceLanguageMode.Korean=>"ko",_=>entries.Any(p=>p.Language=="en")?"en":entries.Any(p=>p.Language=="ja")?"ja":entries.Select(p=>p.Language).OrderBy(p=>p,StringComparer.Ordinal).First()};
            foreach(var entry in entries)if(selected!="*"&&entry.Language!=selected)Exclude(entry.Value);
            values=entries.Where(p=>selected=="*"||p.Language==selected).Select(p=>p.Value).ToArray();
            return true; // An explicitly requested but absent locale never falls back.
        }
        void Strings(JsonElement node,int depth=0)
        {
            if(depth>32)return;
            if(LanguageValues(node,out var localized)){foreach(var value in localized)Strings(value,depth+1);return;}
            if(node.ValueKind==JsonValueKind.String)Add(node.GetString());
            else if(node.ValueKind==JsonValueKind.Array)foreach(var child in node.EnumerateArray())Strings(child,depth+1);
            else if(node.ValueKind==JsonValueKind.Object)foreach(var field in node.EnumerateObject())Strings(field.Value,depth+1);
        }
        void Walk(JsonElement node,int depth=0)
        {
            token.ThrowIfCancellationRequested();if(depth>48)return;
            if(node.ValueKind==JsonValueKind.Array)
            {
                var lines=new List<string>();
                void Flush(){if(lines.Count>0){Add(string.Join("\n",lines));lines.Clear();}}
                foreach(var child in node.EnumerateArray())
                {
                    if(child.ValueKind==JsonValueKind.Object&&child.TryGetProperty("code",out var code)&&code.TryGetInt32(out int id)&&child.TryGetProperty("parameters",out var args)&&args.ValueKind==JsonValueKind.Array)
                    {
                        if(id is 401 or 405&&args.GetArrayLength()>0&&args[0].ValueKind==JsonValueKind.String){var line=args[0].GetString()!;lines.Add(line);Add(line);}
                        else{Flush();if(id==102&&args.GetArrayLength()>0)Strings(args[0]);else if(id is 320 or 324 or 325&&args.GetArrayLength()>1)Strings(args[1]);else if(id==101&&args.GetArrayLength()>4)Strings(args[4]);else if(id==357&&args.GetArrayLength()>3)Plugin(args[3]);}
                    }
                    Walk(child,depth+1);
                }
                Flush();return;
            }
            if(node.ValueKind!=JsonValueKind.Object)return;
            if(LanguageValues(node,out var localized)){foreach(var value in localized)Walk(value,depth+1);return;}
            foreach(var field in node.EnumerateObject())
            {if(Fields.Contains(field.Name))Strings(field.Value);else if(field.Name!="parameters"&&field.Name!="note")Walk(field.Value,depth+1);}
        }
        void Plugin(JsonElement node,int depth=0,bool textContext=false)
        {
            if(depth>12)return;
            if(LanguageValues(node,out var localized)){foreach(var value in localized)Plugin(value,depth+1,textContext);return;}
            if(node.ValueKind==JsonValueKind.Object)foreach(var field in node.EnumerateObject())Plugin(field.Value,depth+1,Regex.IsMatch(field.Name,@"text|message|caption|description|label|dialog|choice|title|help",RegexOptions.IgnoreCase));
            else if(node.ValueKind==JsonValueKind.Array)foreach(var child in node.EnumerateArray())Plugin(child,depth+1,textContext);
            else if(node.ValueKind==JsonValueKind.String)
            {
                var value=node.GetString()!;if(value.StartsWith('{')||value.StartsWith('[')){try{using var json=JsonDocument.Parse(value);Plugin(json.RootElement,depth+1,textContext);}catch(JsonException){}}
                else if(textContext)Add(value);
            }
        }
        foreach(var file in Directory.EnumerateFiles(Path.Combine(install.DataRoot,"data"),"*.json",SearchOption.TopDirectoryOnly).OrderBy(Path.GetFileName,StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try{if(new FileInfo(file).Length>32*1024*1024){bad.Add(Path.GetFileName(file));continue;}using var json=JsonDocument.Parse(File.ReadAllText(file));Walk(json.RootElement);files++;}
            catch(Exception ex)when(ex is IOException or JsonException or UnauthorizedAccessException){bad.Add(Path.GetFileName(file));}
        }
        var plugins=Path.Combine(install.DataRoot,"js","plugins.js");
        if(File.Exists(plugins))try
        {
            if(new FileInfo(plugins).Length<=8*1024*1024)
            {
                string content=File.ReadAllText(plugins);var assignment=Regex.Match(content,@"\$plugins\s*=\s*\[");
                if(assignment.Success){int start=assignment.Index+assignment.Length-1,end=content.LastIndexOf(']');using var json=JsonDocument.Parse(content[start..(end+1)]);foreach(var plugin in json.RootElement.EnumerateArray())if(plugin.TryGetProperty("status",out var status)&&status.ValueKind==JsonValueKind.True&&plugin.TryGetProperty("parameters",out var parameters))Plugin(parameters);files++;}
            }
        }catch(Exception ex)when(ex is IOException or JsonException or ArgumentOutOfRangeException){bad.Add("plugins.js");}
        // No catalogue-wide locale hint: ordinary fields may still be untagged.
        return new(texts.ToArray(),files,bad.ToArray()){SkippedByLanguage=excluded.Count};
    }
}
