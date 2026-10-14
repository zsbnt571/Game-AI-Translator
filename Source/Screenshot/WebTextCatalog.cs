using System.Text.Json;
using System.Text.RegularExpressions;
namespace ScreenshotTranslationUiTester;
internal static class WebTextCatalog
{
    // Split the generated expression table without evaluating any JavaScript.
    private static string[] ArrayItems(string code,string marker)
    {
        int begin=code.IndexOf(marker,StringComparison.Ordinal);if(begin<0)return [];
        begin=code.IndexOf('[',begin+marker.Length);if(begin<0)return [];
        var items=new List<string>();int depth=0,start=begin+1;char quote='\0';bool escape=false;
        for(int i=start;i<code.Length;i++)
        {
            char c=code[i];if(quote!='\0'){if(escape)escape=false;else if(c=='\\')escape=true;else if(c==quote)quote='\0';continue;}
            if(c is '\'' or '"' or '`'){quote=c;continue;}
            if(c==']'&&depth==0){items.Add(code[start..i].Trim());return items.Count<=100000?items.ToArray():[];}
            if(c is '[' or '(' or '{')depth++;else if(c is ']' or ')' or '}')depth--;
            else if(c==','&&depth==0){items.Add(code[start..i].Trim());start=i+1;if(items.Count>=100000)return [];}
            if(depth<0||depth>128)return [];
        }
        return [];
    }
    internal static RpgTextCatalogResult Read(string exe,CancellationToken token,SourceLanguageMode mode,string preferredLanguage="")
    {
        var w=WebGameAdapter.Detect(exe)??throw new IOException("未识别网页资源包。");
        if(w.Engine.StartsWith("RPG Maker",StringComparison.Ordinal))return RpgTextCatalog.ReadPackaged(exe,w,token,mode,preferredLanguage);
        if(NovelWebCatalog.IsNovel(w.Engine))return NovelWebCatalog.Read(w,token,preferredLanguage);
        using var archive=new WebGameArchive(w.Archive);var texts=new HashSet<string>(StringComparer.Ordinal);var issues=new List<CatalogReadIssue>();int files=0,nodes=0;var watch=System.Diagnostics.Stopwatch.StartNew();
        // Resource selection follows the running game's locale, independently
        // of the translation allow-list. Scene display text has separate
        // non-pack provenance; it does not declare an active game locale.
        var localized=new UnityTextResourceLanguageSelection(SourceLanguageMode.Auto,preferredLanguage);
        void Check(){token.ThrowIfCancellationRequested();if(++nodes>2000000||watch.ElapsedMilliseconds>30000||texts.Count>100000)throw new InvalidDataException("目录读取达到本次数量或时间上限。");}
        void Add(string? s){Check();if(s is not null&&RpgTextCatalog.IsText(s))texts.Add(s);}
        void Values(string name,JsonElement values)
        {
            // Construct dictionary keys are arbitrary identifiers. A normal key
            // named language/name/key is not locale metadata and must not hide
            // its display value. Preserve the engine's value semantics while
            // attaching provenance only to explicit files/parallel locale maps.
            void Visit(JsonElement node,string? locale,int depth=0)
            {
                Check();if(depth>40)throw new InvalidDataException("字典层级超过读取上限。");
                if(node.ValueKind==JsonValueKind.String){localized.AddValue(name,locale,node.GetString()!);return;}
                if(node.ValueKind==JsonValueKind.Array){foreach(var item in node.EnumerateArray())Visit(item,locale,depth+1);return;}
                if(node.ValueKind!=JsonValueKind.Object)return;
                var fields=node.EnumerateObject().ToArray();
                var languages=fields.Select(p=>(Field:p,Language:UnityTextResourceLanguageSelection.Locale(p.Name))).Where(p=>p.Language is not null).ToArray();
                bool parallel=languages.Select(p=>p.Language).Distinct(StringComparer.Ordinal).Count()>=2;
                if(parallel)
                {
                    foreach(var item in languages)Visit(item.Field.Value,item.Language,depth+1);
                    foreach(var field in fields.Where(p=>UnityTextResourceLanguageSelection.Locale(p.Name) is null&&!Regex.IsMatch(p.Name,@"^(?:id|key|speaker|speakerId|context|comment|locale|language|languageCode|culture)$",RegexOptions.IgnoreCase)))Visit(field.Value,locale,depth+1);
                    return;
                }
                foreach(var field in fields)Visit(field.Value,locale,depth+1);
            }
            Visit(values,UnityTextResourceLanguageSelection.ResourceLocale(name));
        }
        try
        {
            string code=archive.Text("scripts/c3runtime.js",16*1024*1024);files++;
            var refs=ArrayItems(code,"C3_GetObjectRefTable=function");var expressions=ArrayItems(code,"C3_ExpressionFuncs=");
            if(refs.Length==0||expressions.Length==0)issues.Add(new(CatalogIssueKind.Unsupported,"scripts/c3runtime.js","生成的表达式表结构尚未识别，动态文本由运行时补译"));
            var constants=new Dictionary<int,string>();
            for(int i=0;i<expressions.Length;i++)
            {
                Check();string e=expressions[i];var m=Regex.Match(e,"^\\(\\)\\s*=>\\s*(\"(?:[^\"\\\\]|\\\\.)*\")$",RegexOptions.Singleline,TimeSpan.FromMilliseconds(100));
                if(m.Success)try{constants[i]=JsonSerializer.Deserialize<string>(m.Groups[1].Value)!;}catch(JsonException){}
            }
            using var data=JsonDocument.Parse(archive.Read("data.json"));files++;var project=data.RootElement.GetProperty("project");
            var types=new HashSet<int>();var objects=project[3];
            for(int i=0;i<objects.GetArrayLength();i++){var o=objects[i];if(o.ValueKind==JsonValueKind.Array&&o.GetArrayLength()>1&&o[1].TryGetInt32(out int refId)&&refId>=0&&refId<refs.Length&&Regex.IsMatch(refs[refId],@"\.Plugins\.(?:Text|Spritefont2)$"))types.Add(i);}
            // Instance property slots are defined by the corresponding engine plugins.
            foreach(var layout in project[5].EnumerateArray())foreach(var layer in layout[9].EnumerateArray())foreach(var inst in layer[14].EnumerateArray())
                if(inst.GetArrayLength()>5&&inst[1].TryGetInt32(out int type)&&types.Contains(type)&&inst[5].ValueKind==JsonValueKind.Array&&inst[5].GetArrayLength()>0&&inst[5][0].ValueKind==JsonValueKind.String)Add(inst[5][0].GetString());
            // Action parameters reference expression functions. Only text actions grant
            // display context; dictionary keys, event identifiers and code stay untouched.
            void Expr(JsonElement expr,int depth=0){Check();if(depth>32)return;if(expr.ValueKind!=JsonValueKind.Array||expr.GetArrayLength()==0)return;if(expr[0].TryGetInt32(out int id)&&constants.TryGetValue(id,out string? value))Add(value);}
            void Events(JsonElement node,int depth=0)
            {
                Check();if(depth>64)throw new InvalidDataException("事件层级超过读取上限。");if(node.ValueKind!=JsonValueKind.Array)return;
                if(node.GetArrayLength()>=7&&node[1].ValueKind==JsonValueKind.Number&&node[1].TryGetInt32(out int action)&&action>=0&&action<refs.Length&&Regex.IsMatch(refs[action],@"\.Plugins\.(?:Text|Spritefont2)\.Acts\.(?:SetText|AppendText|TypewriterText)$")&&node[6].ValueKind==JsonValueKind.Array)
                    foreach(var param in node[6].EnumerateArray())if(param.ValueKind==JsonValueKind.Array&&param.GetArrayLength()>1)Expr(param[1]);
                foreach(var child in node.EnumerateArray())if(child.ValueKind==JsonValueKind.Array)Events(child,depth+1);
            }
            Events(project[6]);
            foreach(string name in archive.Names.Where(n=>n.EndsWith(".json",StringComparison.OrdinalIgnoreCase)&&n is not ("package.json" or "data.json")).Take(2048))
            {
                Check();if(archive.Size(name)>16*1024*1024){issues.Add(new(CatalogIssueKind.Limited,name,"字典文件超过本次读取上限"));continue;}
                try{using var json=JsonDocument.Parse(archive.Read(name,16*1024*1024));files++;var root=json.RootElement;if(root.ValueKind==JsonValueKind.Object&&root.TryGetProperty("c2dictionary",out var dict)&&dict.ValueKind==JsonValueKind.True&&root.TryGetProperty("data",out var values))Values(name,values);else if(root.ValueKind==JsonValueKind.Object&&root.TryGetProperty("c2array",out var array)&&array.ValueKind==JsonValueKind.True&&root.TryGetProperty("data",out var cells))Values(name,cells);}
                catch(JsonException){issues.Add(new(CatalogIssueKind.Unsupported,name,"JSON 结构尚未识别"));}
            }
            issues.Add(new(CatalogIssueKind.Unsupported,"运行时表达式","变量拼接、插件和图片里的文字不能全部由静态目录确定；可见文本继续补译"));
        }
        catch(Exception e)when(e is IOException or InvalidDataException or JsonException or InvalidOperationException or IndexOutOfRangeException or KeyNotFoundException or RegexMatchTimeoutException){issues.Add(new(CatalogIssueKind.Failed,"网页资源包","部分文本结构未能完整读取"));}
        var unlocalized=new HashSet<string>(texts,StringComparer.Ordinal);
        try{localized.Complete(Add,preferredLanguage.Length>0?preferredLanguage:null);unlocalized.UnionWith(localized.UnlocalizedSourceTexts);}
        catch(IOException){issues.Add(new(CatalogIssueKind.Limited,"语言资源","语言候选达到本次读取上限，当前可见文本继续补译"));}
        return new(texts.ToArray(),files,issues.Select(i=>i.Source+"："+i.Reason).ToArray()){
            SelectedLanguage=localized.SelectedLanguage,TrustedSourceTexts=new HashSet<string>(localized.TrustedSourceTexts,StringComparer.Ordinal),UnlocalizedSourceTexts=unlocalized,
            SkippedByLanguage=localized.SkippedByLanguage,Report=new(false,issues.ToArray())};
    }
}
