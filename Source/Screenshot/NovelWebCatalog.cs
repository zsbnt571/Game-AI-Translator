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
        using var archive=new WebGameArchive(game.Archive);var texts=new HashSet<string>(StringComparer.Ordinal);var trusted=new HashSet<string>(StringComparer.Ordinal);var unlocalized=new HashSet<string>(StringComparer.Ordinal);var issues=new List<CatalogReadIssue>();var origins=new CatalogOrigins();int files=0,nodes=0;bool trustedSource=false;
        var watch=System.Diagnostics.Stopwatch.StartNew();
        void Check(){token.ThrowIfCancellationRequested();if(++nodes>1000000||watch.ElapsedMilliseconds>30000||texts.Count>100000)throw new InvalidDataException("目录读取达到数量或时间上限。");}
        void Add(string? text){Check();if(!string.IsNullOrWhiteSpace(text)&&text.Length<=6000&&RpgTextCatalog.IsText(text)){texts.Add(text);if(trustedSource)trusted.Add(text);}}
        bool preserveTyranoWhitespace=false;string configName=game.Prefix+"data/system/Config.tjs";
        if(game.Engine=="TyranoScript"&&archive.Contains(configName))
        {
            try{preserveTyranoWhitespace=TyranoTextCatalog.PreservesAttributeWhitespace(archive.Text(configName,1024*1024),Check);files++;}
            catch(Exception e)when(e is IOException or InvalidDataException or System.Text.DecoderFallbackException)
            {issues.Add(new(CatalogIssueKind.Unsupported,configName,"标签空白配置未能完整读取，属性按默认规则收集，游戏内仍可补译。"));}
        }
        foreach(string name in archive.Names.Where(n=>n.StartsWith(game.Prefix,archive.PathComparison)).Take(100000))
        {
            Check();bool tyrano=game.Engine=="TyranoScript"&&name.EndsWith(".ks",StringComparison.OrdinalIgnoreCase);
            bool choice=game.Engine=="ChoiceScript"&&name.StartsWith(game.Prefix+"scenes/",archive.PathComparison)&&name.EndsWith(".txt",StringComparison.OrdinalIgnoreCase);
            bool vn=game.Engine=="Visual Novel Maker"&&name.StartsWith(game.Prefix+"data/",archive.PathComparison)&&name.EndsWith(".json.js",StringComparison.OrdinalIgnoreCase);
            if(!tyrano&&!choice&&!vn)continue;
            string locale=ResourceLocale(name[game.Prefix.Length..]);
            // Wait for an actual game locale before pretranslating a declared
            // pack. Current-screen text has independent runtime authority.
            if(locale.Length>0&&(preferredLanguage.Length==0||locale!=preferredLanguage))continue;
            trustedSource=locale.Length>0&&locale==preferredLanguage;
            try
            {
                byte[] image=archive.Read(name,16*1024*1024);string content=new System.Text.UTF8Encoding(false,true).GetString(image).TrimStart('\uFEFF');files++;
                string hash=CatalogOrigins.Hash(image);var pending=new List<(string Text,string Location,string Kind)>();
                void Stage(string? text,string at,string kind)
                {Check();if(text is not null&&text.Length<=6000&&RpgTextCatalog.IsText(text)){if(pending.Count>=100000)throw new InvalidDataException("单个脚本文本数量达到上限。");pending.Add((text,at,kind));}}
                void Commit()
                {foreach(var field in pending){Add(field.Text);if(locale.Length==0)unlocalized.Add(field.Text);origins.Add(new(field.Text,name,field.Location,hash,field.Kind,"utf-8",game.Archive,locale));}}
                if(vn)
                {
                    var match=Regex.Match(content,@"^\s*(?:(?:var\s+)?[\w.$]+\s*=\s*)?([\[{][\s\S]*[\]}])\s*;?\s*$",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1));
                    if(!match.Success)throw new InvalidDataException("数据包装尚未识别");
                    using var doc=JsonDocument.Parse(match.Groups[1].Value);
                    string Escape(string part)=>part.Replace("~","~0").Replace("/","~1");
                    void Walk(JsonElement value,string at="",int depth=0)
                    {
                        Check();if(depth>48)throw new InvalidDataException("数据层级过深");
                        if(value.ValueKind==JsonValueKind.Array){int i=0;foreach(var child in value.EnumerateArray())Walk(child,at+"/"+i++,depth+1);}
                        else if(value.ValueKind==JsonValueKind.Object)foreach(var prop in value.EnumerateObject())
                        {string pointer=at+"/"+Escape(prop.Name);if(prop.Value.ValueKind==JsonValueKind.String&&prop.Name is "text" or "defaultText" or "message" or "title")Stage(prop.Value.GetString(),pointer,"vnmaker-display-field");else if(prop.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)Walk(prop.Value,pointer,depth+1);}
                    }
                    Walk(doc.RootElement);Commit();continue;
                }
                if(tyrano)
                {foreach(var field in TyranoTextCatalog.Read(content,Check,preserveTyranoWhitespace))Stage(field.Text,field.Location,field.Kind);Commit();continue;}
                var paragraph=new List<string>();int lineNo=0,start=0;
                void Flush(){if(paragraph.Count>0){Stage(string.Join("\n",paragraph),$"line:{start}/paragraph","choicescript-paragraph");foreach(var (line,index) in paragraph.Select((line,index)=>(line,index)))Stage(line,$"line:{start+index}","choicescript-text");paragraph.Clear();}}
                foreach(string raw in content.Replace("\r\n","\n").Split('\n'))
                {
                    Check();lineNo++;string line=raw.Trim();
                    if(line.Length==0){Flush();continue;}
                    if(line.StartsWith('*')){Flush();if(Regex.IsMatch(line,@"^\*(?:if|elseif|else|selectable_if)\b",RegexOptions.IgnoreCase)&&line.IndexOf('#') is int at&&at>=0)Stage(line[(at+1)..].Trim(),$"line:{lineNo}/choice","choicescript-choice");continue;}
                    if(line.StartsWith('#')){Flush();Stage(line[1..].Trim(),$"line:{lineNo}/choice","choicescript-choice");}
                    else{if(paragraph.Count==0)start=lineNo;paragraph.Add(line);}
                }
                Flush();Commit();
            }
            catch(Exception e)when(e is IOException or InvalidDataException or JsonException or RegexMatchTimeoutException or System.Text.DecoderFallbackException){issues.Add(new(CatalogIssueKind.Unsupported,name,"此脚本或数据结构尚未完整解析，已保留原文件。"));}
        }
        issues.Add(new(CatalogIssueKind.Incomplete,game.Engine,"已读取可确认的文字字段；动态表达式、插件及图片文字仍需运行时覆盖。"));
        return new(texts.ToArray(),files,issues.Select(i=>i.Source+"："+i.Reason).ToArray()){
            SelectedLanguage=preferredLanguage,TrustedSourceTexts=trusted,UnlocalizedSourceTexts=unlocalized,Origins=origins.Items,OriginsTruncated=origins.Truncated,ReadCompleted=false,Report=new(false,issues.ToArray())};
    }
}
