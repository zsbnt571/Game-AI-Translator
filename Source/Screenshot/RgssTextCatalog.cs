using System.Diagnostics;
using System.Text;

namespace ScreenshotTranslationUiTester;

internal static class RgssTextCatalog
{
    internal static RpgTextCatalogResult Read(string exe,CancellationToken token=default)
    {
        // Runtime attachment also validates PE/DLL/mkxp compatibility. Static
        // loose Data files do not need a runnable local RGSS installation.
        // Keep the adapter's archive selection/precedence when it is available.
        var engine=RgssGameAdapter.Detect(exe);
        string scripts=engine?.Scripts.Replace('\\','/')??StaticScripts(exe);
        string? archive=engine?.Archive;
        if(engine is null&&Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(exe))!).Any(p=>Path.GetExtension(p).ToLowerInvariant() is ".rgssad" or ".rgss2a" or ".rgss3a"))
            throw new IOException("RGSS 封包接入信息未确认，未改读可能过期的散装 Data 文件。");
        var texts=new HashSet<string>(StringComparer.Ordinal);var origins=new CatalogOrigins();var unlocalized=new HashSet<string>(StringComparer.Ordinal);var issues=new List<CatalogReadIssue>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);int files=0;var clock=Stopwatch.StartNew();
        int attempted=0;
        void Check(){token.ThrowIfCancellationRequested();if(clock.Elapsed.TotalSeconds>25||texts.Count>=100000)throw new IOException("RGSS 文本读取达到本轮限制。");}
        void Add(string value){if(value.Length<=6000&&RpgTranslationText.HasTranslatableText(value,EmbeddedTextSyntax.Rpg))texts.Add(value);}
        void ReadOne(string name,Func<byte[]> bytes)
        {
            Check();string canonical=name.Replace('\\','/');if(!canonical.StartsWith("Data/",StringComparison.OrdinalIgnoreCase))canonical="Data/"+canonical;
            bool script=scripts.Length>0?canonical.Equals(scripts,StringComparison.OrdinalIgnoreCase):Path.GetFileNameWithoutExtension(name).Equals("Scripts",StringComparison.OrdinalIgnoreCase);
            if(IsSaveFile(name)&&!script)return;
            if(!seen.Add(canonical))return;
            if(++attempted>1500){if(attempted==1501)issues.Add(new(CatalogIssueKind.Limited,"RGSS","本轮最多读取 1500 个相关文件。"));return;}
            try
            {
                bool baseData=System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(name),@"^(?:Actors|Classes|Skills|Items|Weapons|Armors|Enemies|Troops|States|System|CommonEvents|Map\d+)$",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                byte[] image=bytes();string hash=CatalogOrigins.Hash(image);var document=new RubyMarshalData(image,Check).Read();
                if(script)
                {
                    var scripts=RgssScriptCatalog.Read(document,Check);
                    foreach(var field in scripts.Fields)
                    {
                        Add(field.Text);if(!texts.Contains(field.Text))continue;unlocalized.Add(field.Text);
                        origins.Add(new(field.Text,name,field.Location,hash,"rgss-script-display",field.Encoding,archive??"",Context:field.Context));
                    }
                    foreach(string issue in scripts.Issues)issues.Add(new(CatalogIssueKind.Incomplete,name,issue));
                    files++;return;
                }
                foreach(var field in LocatedFields(document,Check))
                {Add(field.Text);if(baseData&&texts.Contains(field.Text))unlocalized.Add(field.Text);
                    if(texts.Contains(field.Text))origins.Add(new(field.Text,name,field.Location,hash,"rgss-display-field",field.Encoding,archive??""));}
                files++;
            }
            catch(Exception e)when(e is IOException or InvalidDataException or NotSupportedException or DecoderFallbackException or OverflowException){issues.Add(new(CatalogIssueKind.Failed,name,SafeDiagnosticOutput.ExceptionSummary(e)));}
        }
        string extension=Path.GetExtension(scripts);
        if(archive!=null)
        {
            using var pack=new RgssArchive(GameMakerGameAdapter.InGame(exe,archive),token);
            ReadOne(scripts,()=>pack.Read(scripts,32*1024*1024,token));
            foreach(var entry in pack.Entries.Values.Where(e=>e.Name.StartsWith("Data/",StringComparison.OrdinalIgnoreCase)&&e.Name.EndsWith(extension,StringComparison.OrdinalIgnoreCase)).Take(1500))
            {ReadOne(entry.Name,()=>pack.Read(entry.Name,32*1024*1024,token));}
        }
        else
        {
            string data=GameMakerGameAdapter.InGame(exe,"Data");
            if(!Directory.Exists(data))throw new IOException("未找到 RGSS Data 资源目录。");
            if(scripts.Length>0)ReadOne(scripts,()=>ReadLoose(scripts));
            foreach(string relative in LooseFiles(exe,data,Check,issues))
            {
                string ext=Path.GetExtension(relative).ToLowerInvariant();
                if(extension.Length>0?!ext.Equals(extension,StringComparison.OrdinalIgnoreCase):ext is not (".rxdata" or ".rvdata" or ".rvdata2"))continue;
                ReadOne(relative,()=>ReadLoose(relative));if(attempted>1500)break;
            }
            byte[] ReadLoose(string relative){string path=GameMakerGameAdapter.InGame(exe,relative);if(new FileInfo(path).Length>32*1024*1024)throw new IOException("数据文件超过限制。");return File.ReadAllBytes(path);}
            if(engine is null)issues.Add(new(CatalogIssueKind.Incomplete,"RGSS","仅按静态 Data 资源读取；未确认本地运行时或游戏接入兼容性。"));
        }
        issues.Add(new(CatalogIssueKind.Incomplete,"RGSS","已读取事件、数据库及受限脚本显示字面量；动态拼接、未支持的脚本语法及逐字显示仍在运行时补充。"));
        return new(texts.ToArray(),files,[]){Origins=origins.Items,OriginsTruncated=origins.Truncated,UnlocalizedSourceTexts=unlocalized,ReadCompleted=false,Report=new(false,issues.Take(128).ToArray())};
    }
    // Standard save-slot files are not script/event resources, even if a game
    // stores them alongside Data files with the same Marshal extension.
    internal static bool IsSaveFile(string path)=>System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(path),
        @"^(?:Save\d+|AutoSave\d*|QuickSave\d*)$",System.Text.RegularExpressions.RegexOptions.IgnoreCase|System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static string StaticScripts(string exe)
    {
        string ini=GameMakerGameAdapter.InGame(exe,RgssGameAdapter.HasRecord(exe)?RgssGameAdapter.Folder+"/Original.ini":"Game.ini");
        if(!File.Exists(ini))return "";
        if(new FileInfo(ini).Length>65536)throw new IOException("Game.ini 超过限制。");
        byte[] bytes=File.ReadAllBytes(ini);
        string text=bytes.AsSpan().StartsWith(new byte[]{255,254})?Encoding.Unicode.GetString(bytes,2,bytes.Length-2):bytes.AsSpan().StartsWith(new byte[]{239,187,191})?Encoding.UTF8.GetString(bytes,3,bytes.Length-3):Encoding.Latin1.GetString(bytes);
        var sections=System.Text.RegularExpressions.Regex.Matches(text,@"(?im)^\s*\[Game\]\s*\r?$",System.Text.RegularExpressions.RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
        if(sections.Count!=1)throw new IOException("Game.ini 的游戏配置区不唯一。");
        string section=text[(sections[0].Index+sections[0].Length)..];
        var next=System.Text.RegularExpressions.Regex.Match(section,@"(?m)^\s*\[",System.Text.RegularExpressions.RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));if(next.Success)section=section[..next.Index];
        var values=System.Text.RegularExpressions.Regex.Matches(section,@"(?im)^[ \t]*Scripts[ \t]*=[ \t]*(?<value>[^\r\n]*?)[ \t]*(?=\r?$)",System.Text.RegularExpressions.RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
        if(values.Count==0)return "";
        if(values.Count!=1)throw new IOException("Game.ini 的 Scripts 配置重复。");
        string scripts=values[0].Groups["value"].Value.Replace('\\','/');
        if(!scripts.StartsWith("Data/",StringComparison.OrdinalIgnoreCase)||scripts.Split('/').Any(p=>p is "" or "." or "..")||Path.GetExtension(scripts).ToLowerInvariant() is not (".rxdata" or ".rvdata" or ".rvdata2"))throw new IOException("静态 RGSS 脚本路径必须位于 Data 目录内。");
        _=GameMakerGameAdapter.InGame(exe,scripts);return scripts;
    }
    private static IEnumerable<string> LooseFiles(string exe,string data,Action check,List<CatalogReadIssue> issues)
    {
        string root=Path.GetDirectoryName(Path.GetFullPath(exe))!;var pending=new Stack<(string Path,int Depth)>();pending.Push((data,1));int entries=0;
        void Issue(CatalogIssueKind kind,string file,string reason){if(issues.Count<128)issues.Add(new(kind,file,reason));}
        while(pending.Count>0)
        {
            check();var folder=pending.Pop();string[] children;
            try{_=GameMakerGameAdapter.InGame(exe,Path.GetRelativePath(root,folder.Path));children=Directory.EnumerateFileSystemEntries(folder.Path).Take(10001).ToArray();}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){Issue(CatalogIssueKind.Failed,Path.GetRelativePath(root,folder.Path),SafeDiagnosticOutput.ExceptionSummary(ex));continue;}
            if(children.Length>10000)Issue(CatalogIssueKind.Limited,Path.GetRelativePath(root,folder.Path),"单目录最多检查 10000 项。");
            foreach(string path in children.Take(10000))
            {
                check();if(++entries>100000){Issue(CatalogIssueKind.Limited,"Data","达到静态目录遍历数量上限。");yield break;}
                string relative=Path.GetRelativePath(root,path).Replace('\\','/');FileAttributes attributes;
                try{attributes=File.GetAttributes(path);}
                catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){Issue(CatalogIssueKind.Failed,relative,SafeDiagnosticOutput.ExceptionSummary(ex));continue;}
                if((attributes&FileAttributes.ReparsePoint)!=0){Issue(CatalogIssueKind.Skipped,relative,"链接目录或文件未跟随读取。");continue;}
                if((attributes&FileAttributes.Directory)==0){yield return relative;continue;}
                if(folder.Depth>=8)Issue(CatalogIssueKind.Limited,relative,"达到静态目录深度上限。");else pending.Push((path,folder.Depth+1));
            }
        }
    }
    internal static IEnumerable<string> DisplayFields(RubyMarshalData.Node root,Action check)
        =>LocatedFields(root,check).Select(e=>e.Text);
    internal sealed record LocatedField(string Location,string Text,string Encoding);
    internal static IEnumerable<LocatedField> LocatedFields(RubyMarshalData.Node root,Action check)
    {
        var stack=new Stack<(RubyMarshalData.Node Node,string Path)>();var visited=new HashSet<RubyMarshalData.Node>(ReferenceEqualityComparer.Instance);stack.Push((root,"$"));
        while(stack.Count>0)
        {
            check();var (n,path)=stack.Pop();if(!visited.Add(n))continue;
            if(n.Kind=='o'&&n.Name=="RPG::EventCommand"&&n.Fields.TryGetValue("@code",out var code)&&n.Fields.TryGetValue("@parameters",out var parameters))
            {
                if((code.Number is 401 or 405||code.Number==101&&parameters.Items.Count==1)&&parameters.Items.FirstOrDefault() is {} first&&first.Kind=='"')
                    yield return new(path+"/@parameters/0",Decode(first),first.Encoding);
                else if(code.Number==102&&parameters.Items.FirstOrDefault() is {} choices)
                {
                    for(int i=0;i<choices.Items.Count;i++)if(choices.Items[i].Kind=='"')yield return new(path+"/@parameters/0/"+i,Decode(choices.Items[i]),choices.Items[i].Encoding);
                }
                else if(code.Number is 320 or 324 or 325&&parameters.Items.Count>1&&parameters.Items[1].Kind=='"')
                    yield return new(path+"/@parameters/1",Decode(parameters.Items[1]),parameters.Items[1].Encoding);
                continue;
            }
            bool display=n.Kind=='o'&&n.Name is "RPG::Actor" or "RPG::Class" or "RPG::Skill" or "RPG::Item" or "RPG::Weapon" or "RPG::Armor" or "RPG::Enemy" or "RPG::State" or "RPG::Map" or "RPG::System" or "RPG::System::Terms" or "RPG::System::Words";
            foreach(var p in n.Fields)
            {
                if(display&&p.Value.Kind=='"'&&p.Key is "@name" or "@description" or "@nickname" or "@display_name" or "@game_title" or "@currency_unit" or "@message1" or "@message2" or "@message3" or "@message4")yield return new(path+"/"+p.Key,Decode(p.Value),p.Value.Encoding);
                if(n.Name is "RPG::System::Terms" or "RPG::System::Words")
                {if(p.Value.Kind=='"')yield return new(path+"/"+p.Key,Decode(p.Value),p.Value.Encoding);else for(int i=0;i<p.Value.Items.Count;i++)if(p.Value.Items[i].Kind=='"')yield return new(path+"/"+p.Key+"/"+i,Decode(p.Value.Items[i]),p.Value.Items[i].Encoding);}
                stack.Push((p.Value,path+"/"+p.Key));
            }
            for(int i=n.Items.Count-1;i>=0;i--)stack.Push((n.Items[i],path+"/"+i));
        }
    }
    private static string Decode(RubyMarshalData.Node node)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if(node.Encoding.Length>0)return Encoding.GetEncoding(node.Encoding,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback).GetString(node.Bytes);
        try{return new UTF8Encoding(false,true).GetString(node.Bytes);}catch(DecoderFallbackException){return Encoding.GetEncoding(932,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback).GetString(node.Bytes);}
    }
}
