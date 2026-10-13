using System.Text.Json;
using System.Text.RegularExpressions;
namespace ScreenshotTranslationUiTester;

internal static class NovelWebCatalog
{
    internal static string? Detect(WebGameArchive files,string prefix)
    {
        if(files.Contains(prefix+"tyrano/tyrano.js")&&files.Contains(prefix+"tyrano/plugins/kag/kag.js"))return "TyranoScript";
        if(files.Contains(prefix+"data/ENGINE.js")&&files.Contains(prefix+"data/SCRIPTS.js")&&files.Contains(prefix+"data/SUMMARIES.json.js"))return "Visual Novel Maker";
        if(files.Contains(prefix+"scene.js")&&files.Contains(prefix+"navigator.js")&&files.Names.Any(n=>n.StartsWith(prefix+"scenes/",StringComparison.Ordinal)&&n.EndsWith(".txt",StringComparison.OrdinalIgnoreCase)))return "ChoiceScript";
        return null;
    }
    internal static bool IsNovel(string engine)=>engine is "TyranoScript" or "Visual Novel Maker" or "ChoiceScript";
    internal static string Id(string engine)=>engine switch{"TyranoScript"=>"tyrano","Visual Novel Maker"=>"vnmaker","ChoiceScript"=>"choicescript",_=>throw new ArgumentException("未知文字引擎")};
    // Only whole path segments and explicit filename suffixes are locale
    // evidence. A title merely containing "English" or "jp" is not a pack.
    private static string ResourceLocale(string name)
    {
        var parts=name.Replace('\\','/').Split('/');
        foreach(string part in parts.SkipLast(1).Reverse())
            if(UnityTextResourceLanguageSelection.Locale(part) is {} language)return language;
        string stem=Path.GetFileNameWithoutExtension(parts[^1]);
        if(stem.EndsWith(".json",StringComparison.OrdinalIgnoreCase))stem=Path.GetFileNameWithoutExtension(stem);
        if(UnityTextResourceLanguageSelection.Locale(stem) is {} whole)return whole;
        for(int i=0;i<stem.Length;i++)
            if((stem[i] is '.' or '_' or '-')&&UnityTextResourceLanguageSelection.Locale(stem[(i+1)..]) is {} suffix)return suffix;
        return "";
    }
    internal static RpgTextCatalogResult Read(WebGame game,CancellationToken token,string preferredLanguage="")
    {
        using var archive=new WebGameArchive(game.Archive);var texts=new HashSet<string>(StringComparer.Ordinal);var trusted=new HashSet<string>(StringComparer.Ordinal);var issues=new List<CatalogReadIssue>();int files=0,nodes=0;bool trustedSource=false;
        var watch=System.Diagnostics.Stopwatch.StartNew();
        void Check(){token.ThrowIfCancellationRequested();if(++nodes>1000000||watch.ElapsedMilliseconds>30000||texts.Count>100000)throw new InvalidDataException("目录读取达到数量或时间上限。");}
        void Add(string? text){Check();if(!string.IsNullOrWhiteSpace(text)&&text.Length<=6000&&RpgTextCatalog.IsText(text)){texts.Add(text);if(trustedSource)trusted.Add(text);}}
        foreach(string name in archive.Names.Where(n=>n.StartsWith(game.Prefix,StringComparison.Ordinal)).Take(100000))
        {
            Check();bool tyrano=game.Engine=="TyranoScript"&&name.EndsWith(".ks",StringComparison.OrdinalIgnoreCase);
            bool choice=game.Engine=="ChoiceScript"&&name.StartsWith(game.Prefix+"scenes/",StringComparison.Ordinal)&&name.EndsWith(".txt",StringComparison.OrdinalIgnoreCase);
            bool vn=game.Engine=="Visual Novel Maker"&&name.StartsWith(game.Prefix+"data/",StringComparison.Ordinal)&&name.EndsWith(".json.js",StringComparison.OrdinalIgnoreCase);
            if(!tyrano&&!choice&&!vn)continue;
            string locale=ResourceLocale(name[game.Prefix.Length..]);
            // Wait for an actual game locale before pretranslating a declared
            // pack. Current-screen text has independent runtime authority.
            if(locale.Length>0&&(preferredLanguage.Length==0||locale!=preferredLanguage))continue;
            trustedSource=locale.Length>0&&locale==preferredLanguage;
            try
            {
                string content=archive.Text(name,16*1024*1024);files++;
                if(vn)
                {
                    var match=Regex.Match(content,@"^\s*(?:(?:var\s+)?[\w.$]+\s*=\s*)?([\[{][\s\S]*[\]}])\s*;?\s*$",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1));
                    if(!match.Success)throw new InvalidDataException("数据包装尚未识别");
                    using var doc=JsonDocument.Parse(match.Groups[1].Value);
                    void Walk(JsonElement value,int depth=0)
                    {
                        Check();if(depth>48)throw new InvalidDataException("数据层级过深");
                        if(value.ValueKind==JsonValueKind.Array)foreach(var child in value.EnumerateArray())Walk(child,depth+1);
                        else if(value.ValueKind==JsonValueKind.Object)foreach(var prop in value.EnumerateObject())
                        {if(prop.Value.ValueKind==JsonValueKind.String&&prop.Name is "text" or "defaultText" or "message" or "title")Add(prop.Value.GetString());else if(prop.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)Walk(prop.Value,depth+1);}
                    }
                    Walk(doc.RootElement);continue;
                }
                bool script=false,comment=false;var paragraph=new List<string>();
                void Flush(){if(paragraph.Count>0){Add(string.Join("\n",paragraph));foreach(string line in paragraph)Add(line);paragraph.Clear();}}
                foreach(string raw in content.Replace("\r\n","\n").Split('\n'))
                {
                    Check();string line=raw.Trim();
                    if(choice)
                    {
                        if(line.Length==0){Flush();continue;}
                        if(line.StartsWith('*')){Flush();if(Regex.IsMatch(line,@"^\*(?:if|elseif|else|selectable_if)\b",RegexOptions.IgnoreCase)&&line.IndexOf('#') is int at&&at>=0)Add(line[(at+1)..].Trim());continue;}
                        if(line.StartsWith('#')){Flush();Add(line[1..].Trim());}else paragraph.Add(line);
                        continue;
                    }
                    if(line.StartsWith("/*",StringComparison.Ordinal))comment=true;
                    if(comment){Flush();if(line.Contains("*/",StringComparison.Ordinal))comment=false;continue;}
                    if(Regex.IsMatch(line,@"^(?:@|\[)(?:iscript|html)\b",RegexOptions.IgnoreCase)){Flush();script=true;continue;}
                    if(script){if(Regex.IsMatch(line,@"^(?:@|\[)(?:endscript|endhtml)\b",RegexOptions.IgnoreCase))script=false;continue;}
                    if(line.Length==0||line.StartsWith(';')||line.StartsWith('*')){Flush();continue;}
                    if(line.StartsWith('@')||line.StartsWith('['))
                    {
                        Flush();foreach(Match tag in Regex.Matches(line,"(?:@|\\[)(?:ptext|mtext|glink)\\b[^\\r\\n]*?\\btext\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)')",RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(250)))Add(tag.Groups[1].Success?tag.Groups[1].Value:tag.Groups[2].Value);
                        // Mixed tag/script lines need the engine parser; don't collect code as dialogue.
                        continue;
                    }
                    paragraph.Add(line);
                }
                Flush();
            }
            catch(Exception e)when(e is IOException or InvalidDataException or JsonException or RegexMatchTimeoutException or System.Text.DecoderFallbackException){issues.Add(new(CatalogIssueKind.Unsupported,name,"此脚本或数据结构尚未完整解析，已保留原文件。"));}
        }
        issues.Add(new(CatalogIssueKind.Incomplete,game.Engine,"已读取可确认的文字字段；动态表达式、插件及图片文字仍需运行时覆盖。"));
        return new(texts.ToArray(),files,issues.Select(i=>i.Source+"："+i.Reason).ToArray()){
            SelectedLanguage=preferredLanguage,TrustedSourceTexts=trusted,ReadCompleted=false,Report=new(false,issues.ToArray())};
    }
}
