using System.Text.Json;
using System.Text.RegularExpressions;
namespace ScreenshotTranslationUiTester;

internal sealed record RpgTextCatalogResult(string[] Texts,int Files,string[] Unreadable)
{
    // Explicit evidence that the bounded scan exhausted its intended inputs.
    // An empty result or a completed Task alone must never enable screen capture.
    internal bool ReadCompleted {get;init;}
    internal string SelectedLanguage {get;init;}="";
    internal int SkippedByLanguage {get;init;}
    // Null retains older catalogue contracts; a supplied set limits the locale
    // trust to those exact sources, leaving untagged resources subject to detection.
    internal IReadOnlySet<string>? TrustedSourceTexts {get;init;}
    // Optional static extraction provenance. This never grants runtime writeback
    // or full-buffer authority on its own; older adapters may leave it empty.
    internal IReadOnlyList<EmbeddedCatalogSource> Sources {get;init;}=[];
    internal CatalogReadReport? Report {get;init;}
}
internal static class RpgTextCatalog
{
    internal sealed record Limits(int Texts=100000,int Files=10000,int JsonBytes=32*1024*1024,int PluginBytes=8*1024*1024,int Milliseconds=30000,int Nodes=4000000);
    private sealed class ScanLimit : Exception {}
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
    internal static RpgTextCatalogResult Read(string exe,CancellationToken token,SourceLanguageMode mode,Limits? limits=null,string preferredLanguage="")
        =>ReadCore(exe,token,mode,limits,null,"",preferredLanguage);
    internal static RpgTextCatalogResult ReadPackaged(string exe,WebGame package,CancellationToken token,SourceLanguageMode mode,string preferredLanguage="")
    {using var archive=new WebGameArchive(package.Archive);return ReadCore(exe,token,mode,null,archive,package.Prefix,preferredLanguage);}
    private static RpgTextCatalogResult ReadCore(string exe,CancellationToken token,SourceLanguageMode mode,Limits? limits,WebGameArchive? archive,string prefix,string preferredLanguage="")
    {
        if(mode==SourceLanguageMode.Mixed)mode=SourceLanguageMode.Auto; // A combination does not select every language pack.
        limits??=new();var watch=System.Diagnostics.Stopwatch.StartNew();token.ThrowIfCancellationRequested();
        var texts=new HashSet<string>(StringComparer.Ordinal);var excluded=new HashSet<string>(StringComparer.Ordinal);var trusted=new HashSet<string>(StringComparer.Ordinal);var selectedLanguages=new HashSet<string>(StringComparer.Ordinal);var issues=new HashSet<CatalogReadIssue>();int files=0,nodes=0;string current="游戏目录";bool trustedSource=false;
        void Issue(CatalogIssueKind kind,string reason){if(issues.Count<256)issues.Add(new(kind,current,reason));}
        if(archive?.UnreadableEntries>0)Issue(CatalogIssueKind.Unsupported,"资源包中部分索引超出文件范围，已跳过这些条目");
        void Check(bool countNode=true)
        {
            token.ThrowIfCancellationRequested();
            if(countNode&&++nodes>limits.Nodes||watch.ElapsedMilliseconds>limits.Milliseconds){Issue(CatalogIssueKind.Limited,"达到本次遍历数量或时间上限，剩余来源未读取");throw new ScanLimit();}
        }
        void Add(string? value)
        {
            if(value is null||!IsText(value))return;
            if(trustedSource)trusted.Add(value);
            if(texts.Contains(value))return;
            if(texts.Count>=limits.Texts){Issue(CatalogIssueKind.Limited,"达到本次文本条数上限，已提取文字保留");throw new ScanLimit();}
            texts.Add(value);
        }
        var scriptText=new RpgScriptCatalog(()=>Check(false),()=>{Issue(CatalogIssueKind.Limited,"脚本文字关系达到本次读取上限，动态文字由实时接入继续补充");throw new ScanLimit();});
        RpgTextCatalogResult Finish()=>new(texts.ToArray(),files,issues.Select(i=>i.Source+"："+i.Reason).ToArray()){
            SelectedLanguage=preferredLanguage.Length>0?preferredLanguage:selectedLanguages.Count==1?selectedLanguages.Single():"",
            TrustedSourceTexts=trusted,SkippedByLanguage=excluded.Except(texts).Count(),Report=new(issues.Count==0,issues.ToArray())};
        RpgMakerInstallation? install;
        try{install=archive is null?RpgMakerDataAdapter.Detect(exe):new("","",prefix,"packaged");}
        catch(Exception ex)when(ex is IOException or JsonException or UnauthorizedAccessException){Issue(CatalogIssueKind.Failed,"游戏入口资料未能读取");return Finish();}
        if(install is null){Issue(CatalogIssueKind.Unsupported,"未识别到可读取的 RPG Maker MV/MZ 数据结构");return Finish();}
        void Exclude(JsonElement node,int depth=0)
        {
            Check();if(depth>32){Issue(CatalogIssueKind.Limited,"语言字段层级超过本次读取上限");return;}
            if(node.ValueKind==JsonValueKind.String){var value=node.GetString()!;if(IsText(value))excluded.Add(value);}
            else if(node.ValueKind==JsonValueKind.Array)foreach(var child in node.EnumerateArray())Exclude(child,depth+1);
            else if(node.ValueKind==JsonValueKind.Object)foreach(var field in node.EnumerateObject())Exclude(field.Value,depth+1);
        }
        bool LanguageValues(JsonElement node,Action<JsonElement> visit,Action<JsonElement>? sharedVisit=null)
        {
            if(node.ValueKind!=JsonValueKind.Object)return false;
            // Only an explicit multilingual object establishes locale identity.
            // Untagged strings remain subject to the shared source-language gate.
            var fields=node.EnumerateObject().ToArray();
            var entries=fields.Select(p=>(Language:Locale(p.Name),p.Value)).Where(p=>p.Language.Length>0&&p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Array or JsonValueKind.Object).ToArray();
            if(entries.Select(p=>p.Language).Distinct(StringComparer.Ordinal).Count()<2||!entries.Any(p=>p.Language is not ("id" or "no")))return false;
            string selected=preferredLanguage.Length>0?preferredLanguage:mode switch {SourceLanguageMode.Mixed=>"*",SourceLanguageMode.English=>"en",SourceLanguageMode.Japanese=>"ja",SourceLanguageMode.SimplifiedChinese=>"zh",SourceLanguageMode.Korean=>"ko",_=>entries.Any(p=>p.Language=="en")?"en":entries.Any(p=>p.Language=="ja")?"ja":entries.Select(p=>p.Language).OrderBy(p=>p,StringComparer.Ordinal).First()};
            foreach(var entry in entries)if(selected!="*"&&entry.Language!=selected)Exclude(entry.Value);
            foreach(var entry in entries.Where(p=>selected=="*"||p.Language==selected))
            {
                selectedLanguages.Add(entry.Language);
                bool previous=trustedSource;trustedSource=true;
                try{visit(entry.Value);}finally{trustedSource=previous;}
            }
            // Metadata does not invalidate explicit locale branches. Shared
            // display fields keep their own provenance; speaker/key/context
            // metadata must not recursively reopen the excluded language rows.
            foreach(var field in fields.Where(p=>Locale(p.Name).Length==0&&(Fields.Contains(p.Name)||UnityTextResourceLanguageSelection.IsTextField(p.Name))))(sharedVisit??visit)(field.Value);
            return true; // An explicitly requested but absent locale never falls back.
        }
        void Strings(JsonElement node,int depth=0)
        {
            Check();if(depth>32){Issue(CatalogIssueKind.Limited,"文字字段层级超过本次读取上限");return;}
            if(LanguageValues(node,value=>Strings(value,depth+1)))return;
            if(node.ValueKind==JsonValueKind.String)Add(node.GetString());
            else if(node.ValueKind==JsonValueKind.Array)foreach(var child in node.EnumerateArray())Strings(child,depth+1);
            else if(node.ValueKind==JsonValueKind.Object)foreach(var field in node.EnumerateObject())Strings(field.Value,depth+1);
        }
        void Walk(JsonElement node,int depth=0)
        {
            // Tile coordinates and other primitive metadata carry no candidate
            // text. Keep time/cancellation checks, but do not let millions of
            // numeric cells consume the structured text-node allowance.
            Check(node.ValueKind is JsonValueKind.Array or JsonValueKind.Object);if(depth>48){Issue(CatalogIssueKind.Limited,"事件字段层级超过本次读取上限");return;}
            if(node.ValueKind==JsonValueKind.Array)
            {
                var lines=new List<string>();var scriptLines=new List<string>();
                void Flush(){if(lines.Count>0){string message=string.Join("\n",lines);Add(message);scriptText.Display(message);lines.Clear();}}
                void FlushScript(){if(scriptLines.Count>0){scriptText.Script(string.Join("\n",scriptLines));scriptLines.Clear();}}
                foreach(var child in node.EnumerateArray())
                {
                    if(child.ValueKind==JsonValueKind.Object&&child.TryGetProperty("code",out var malformed)&&malformed.ValueKind!=JsonValueKind.Number)
                        Issue(CatalogIssueKind.Unsupported,"部分事件的指令编号不是数字，已跳过该指令");
                    if(child.ValueKind==JsonValueKind.Object&&child.TryGetProperty("code",out var code)&&code.ValueKind==JsonValueKind.Number&&code.TryGetInt32(out int id)&&child.TryGetProperty("parameters",out var args)&&args.ValueKind==JsonValueKind.Array)
                    {
                        if(id!=655)FlushScript();
                        if(id is 355 or 655&&args.GetArrayLength()>0&&args[0].ValueKind==JsonValueKind.String)scriptLines.Add(args[0].GetString()!);
                        // Control Variables: Set, Script operand. Read its literal
                        // expression without running the event or evaluating JS.
                        if(id==122&&args.GetArrayLength()>=5&&Enumerable.Range(0,4).All(n=>args[n].ValueKind==JsonValueKind.Number)
                            &&args[0].TryGetInt32(out int first)&&args[1].TryGetInt32(out int last)
                            &&args[2].TryGetInt32(out int operation)&&operation==0&&args[3].TryGetInt32(out int operand)&&operand==4
                            &&args[4].ValueKind==JsonValueKind.String&&first>0&&last<1000000&&last>=first&&last-first<256)
                            for(int variable=first;variable<=last;variable++)scriptText.Variable(variable,args[4].GetString()!);
                        if(id is 401 or 405&&args.GetArrayLength()>0&&args[0].ValueKind==JsonValueKind.String){var line=args[0].GetString()!;lines.Add(line);Add(line);}
                        else{Flush();if(id==102&&args.GetArrayLength()>0)Strings(args[0]);else if(id is 320 or 324 or 325&&args.GetArrayLength()>1)Strings(args[1]);else if(id==101&&args.GetArrayLength()>4)Strings(args[4]);else if(id==357&&args.GetArrayLength()>3)Plugin(args[3]);}
                    }
                    Walk(child,depth+1);
                }
                Flush();FlushScript();return;
            }
            if(node.ValueKind!=JsonValueKind.Object)return;
            if(LanguageValues(node,value=>Walk(value,depth+1),value=>Strings(value,depth+1)))return;
            foreach(var field in node.EnumerateObject())
            {if(Fields.Contains(field.Name))Strings(field.Value);else if(field.Name!="parameters"&&field.Name!="note")Walk(field.Value,depth+1);}
        }
        void Plugin(JsonElement node,int depth=0,bool textContext=false)
        {
            Check();if(depth>12){Issue(CatalogIssueKind.Limited,"插件参数层级超过本次读取上限");return;}
            if(LanguageValues(node,value=>Plugin(value,depth+1,textContext),value=>Plugin(value,depth+1,true)))return;
            if(node.ValueKind==JsonValueKind.Object)foreach(var field in node.EnumerateObject())Plugin(field.Value,depth+1,Regex.IsMatch(field.Name,@"text|message|caption|description|label|dialog|choice|title|help|^name$",RegexOptions.IgnoreCase)&&!Regex.IsMatch(field.Name,@"script|code|eval|function|filename|path|font",RegexOptions.IgnoreCase));
            else if(node.ValueKind==JsonValueKind.Array)foreach(var child in node.EnumerateArray())Plugin(child,depth+1,textContext);
            else if(node.ValueKind==JsonValueKind.String)
            {
                var value=node.GetString()!;var trimmed=value.TrimStart();if(trimmed.StartsWith('{')||trimmed.StartsWith('[')||trimmed.StartsWith('"')){try{using var json=JsonDocument.Parse(value);Plugin(json.RootElement,depth+1,textContext);}catch(JsonException){if(textContext){if(trimmed.StartsWith('"'))Add(value);else Issue(CatalogIssueKind.Unsupported,"部分插件文字参数不是可识别的 JSON 结构");}}}
                else if(textContext)Add(value);
            }
        }
        try
        {
            var paths=new List<string>();
            foreach(var path in archive is null?Directory.EnumerateFiles(Path.Combine(install.DataRoot,"data"),"*.json",SearchOption.TopDirectoryOnly):archive.Names.Where(n=>n.StartsWith(prefix+"data/",StringComparison.Ordinal)&&n.EndsWith(".json",StringComparison.OrdinalIgnoreCase)&&!n[(prefix.Length+5)..].Contains('/')))
            {
                Check();if(paths.Count>=limits.Files){Issue(CatalogIssueKind.Limited,"数据文件数量达到本次上限");break;}paths.Add(path);
            }
            foreach(var file in paths.OrderBy(Path.GetFileName,StringComparer.OrdinalIgnoreCase))
            {
                current=Path.GetFileName(file);Check();
                try
                {
                    if((archive?.Size(file)??new FileInfo(file).Length)>limits.JsonBytes){Issue(CatalogIssueKind.Limited,"文件超过本次大小上限");continue;}
                    using var json=JsonDocument.Parse(archive?.Text(file,limits.JsonBytes)??File.ReadAllText(file));files++;
                    string? fileLocale=UnityTextResourceLanguageSelection.ResourceLocale(current);
                    if(fileLocale is not null&&preferredLanguage.Length>0&&fileLocale!=preferredLanguage){Exclude(json.RootElement);continue;}
                    trustedSource=fileLocale is not null;
                    if(fileLocale is not null)selectedLanguages.Add(fileLocale);
                    try{Walk(json.RootElement);}finally{trustedSource=false;}
                }
                catch(Exception ex)when(ex is IOException or JsonException or UnauthorizedAccessException){Issue(CatalogIssueKind.Failed,ex is JsonException?"JSON 解析失败":"文件未能读取");}
            }
            var plugins=archive is null?Path.Combine(install.DataRoot,"js","plugins.js"):prefix+"js/plugins.js";current="js/plugins.js";
            if(archive?.Contains(plugins)??File.Exists(plugins))try
            {
                Check();
                if((archive?.Size(plugins)??new FileInfo(plugins).Length)>limits.PluginBytes)Issue(CatalogIssueKind.Limited,"插件列表超过本次大小上限");
                else
                {
                    string content=archive?.Text(plugins,limits.PluginBytes)??File.ReadAllText(plugins);var assignment=Regex.Match(content,@"\$plugins\s*=\s*\[");
                    if(!assignment.Success)Issue(CatalogIssueKind.Unsupported,"插件列表不是可识别的静态数组，未执行其中的脚本");
                    else
                    {
                        int start=assignment.Index+assignment.Length-1;
                        // Plugin lists are JavaScript JSON literals and may
                        // contain disabled rows as comments or a trailing comma.
                        // Stop at the array's end; never execute following code.
                        var reader=new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(content[start..]),
                            new JsonReaderOptions{CommentHandling=JsonCommentHandling.Skip,AllowTrailingCommas=true});
                        using var json=JsonDocument.ParseValue(ref reader);files++;
                        foreach(var plugin in json.RootElement.EnumerateArray())
                        {
                            Check();if(plugin.ValueKind!=JsonValueKind.Object){Issue(CatalogIssueKind.Unsupported,"插件列表包含非对象条目，已跳过该条目");continue;}
                            if(plugin.TryGetProperty("status",out var status)&&status.ValueKind==JsonValueKind.True&&plugin.TryGetProperty("parameters",out var parameters))Plugin(parameters);
                        }
                    }
                }
            }catch(Exception ex)when(ex is IOException or JsonException or ArgumentOutOfRangeException or UnauthorizedAccessException){Issue(CatalogIssueKind.Failed,ex is JsonException or ArgumentOutOfRangeException?"插件列表解析失败":"插件列表未能读取");}
            current="事件脚本文字";scriptText.Emit(Add);
        }
        catch(ScanLimit){}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){Issue(CatalogIssueKind.Failed,"数据目录未能完整读取");}
        // No catalogue-wide locale hint: ordinary fields may still be untagged.
        return Finish();
    }
}
