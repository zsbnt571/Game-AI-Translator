using System.Diagnostics;
using System.Text;

namespace ScreenshotTranslationUiTester;

// Experimental read-only catalogue. No Ruby runtime, deserialization hooks,
// archive unpacking, patch generation or writes to a game directory.
internal static class ExperimentalRpgCatalog
{
    internal sealed record Entry(string File,string Location,string Text)
    {
        internal string ContentHash {get;init;}="";
        internal string Kind {get;init;}="";
        internal string Encoding {get;init;}="";
        internal string SchemaFile {get;init;}="";
        internal string SchemaHash {get;init;}="";
        internal string Context {get;init;}="";
    }
    internal sealed record FileResult(string File,string Status,int Texts,string Detail);
    internal sealed record Result(string Engine,Entry[] Entries,FileResult[] Files,bool Complete,string Boundary)
    { internal RpgTextCatalogResult? SourceCatalog {get;init;} }
    internal static CatalogTextOrigin Origin(Entry e,string fallback)
        =>new(e.Text,e.File,e.Location,e.ContentHash,e.Kind.Length>0?e.Kind:fallback,e.Encoding,
            SchemaFile:e.SchemaFile,SchemaHash:e.SchemaHash,Context:e.Context);
    internal static string? Detect(string exe)
    {
        string root=Path.GetDirectoryName(exe)!;string data=Path.Combine(root,"Data");
        if(Directory.EnumerateFiles(root,"*.wolf",SearchOption.TopDirectoryOnly).Any()||Directory.Exists(Path.Combine(data,"BasicData"))&&Directory.Exists(Path.Combine(data,"MapData")))return "WOLF RPG Editor";
        if(RgssGameAdapter.Detect(exe) is {} configured)return "RPG Maker "+configured.Name;
        foreach(var pair in new[]{(".rvdata2","VX Ace"),(".rvdata","VX"),(".rxdata","XP")})
            if(Directory.Exists(data)&&Directory.EnumerateFiles(data,"*"+pair.Item1,SearchOption.TopDirectoryOnly).Any())return "RPG Maker "+pair.Item2;
        foreach(var pair in new[]{("*.rgss3a","VX Ace"),("*.rgss2a","VX"),("*.rgssad","XP")})
            if(Directory.EnumerateFiles(root,pair.Item1,SearchOption.TopDirectoryOnly).Any())return "RPG Maker "+pair.Item2;
        return null;
    }
    internal static Result Read(string exe,CancellationToken cancellation=default)
    {
        string engine=Detect(exe)??throw new IOException("未找到 WOLF 或 RPG Maker XP / VX / VX Ace 的资源结构。");
        if(engine=="WOLF RPG Editor")return WolfResourceCatalog.Read(exe,cancellation);
        string root=Path.GetDirectoryName(exe)!;List<Entry> entries=[];List<FileResult> reports=[];var timer=Stopwatch.StartNew();int files=0;bool complete=true;
        string? configuredScript=engine.StartsWith("RPG Maker ",StringComparison.Ordinal)?RgssGameAdapter.Detect(exe)?.Scripts.Replace('\\','/'):null;
        void Check(){cancellation.ThrowIfCancellationRequested();if(timer.Elapsed.TotalSeconds>20||entries.Count>=100000)throw new LimitException();}
        var pending=new Stack<string>();pending.Push(root);var paired=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while(pending.Count>0)
        {
            string directory=pending.Pop();IEnumerable<string> contents;
            try{var listed=Directory.EnumerateFileSystemEntries(directory).Take(10001).ToArray();contents=listed.Take(10000);if(listed.Length>10000){reports.Add(new(Path.GetRelativePath(root,directory),"达到限制",0,"单目录最多检查 10000 项"));complete=false;}}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){reports.Add(new(Path.GetRelativePath(root,directory),"读取失败",0,SafeDiagnosticOutput.ExceptionSummary(ex)));complete=false;continue;}
            foreach(string path in contents)
            {
                cancellation.ThrowIfCancellationRequested();
                if(timer.Elapsed.TotalSeconds>20||entries.Count>=100000){reports.Add(new(Path.GetRelativePath(root,directory),"达到限制",0,"达到 20 秒或 100000 条文本限制"));complete=false;pending.Clear();break;}
                string relative=Path.GetRelativePath(root,path);var attributes=File.GetAttributes(path);
                if((attributes&FileAttributes.ReparsePoint)!=0){reports.Add(new(relative,"格式未支持",0,"链接目录或文件未跟随"));complete=false;continue;}
                if((attributes&FileAttributes.Directory)!=0)
                {if(directory==root&&Path.GetFileName(path).Equals("Data",StringComparison.OrdinalIgnoreCase)||directory!=root&&relative.Split(Path.DirectorySeparatorChar).Length<8)pending.Push(path);continue;}
                string ext=Path.GetExtension(path).ToLowerInvariant();bool archive=ext is ".wolf" or ".rgssad" or ".rgss2a" or ".rgss3a";
                bool ruby=ext is ".rxdata" or ".rvdata" or ".rvdata2";
                if(ruby&&RgssTextCatalog.IsSaveFile(path)&&!relative.Replace('\\','/').Equals(configuredScript,StringComparison.OrdinalIgnoreCase))continue;
                bool evtext=ext==".txt"&&relative.Split(Path.DirectorySeparatorChar).Any(p=>p.Equals("Evtext",StringComparison.OrdinalIgnoreCase));
                bool binary=engine=="WOLF RPG Editor"&&ext is ".mps" or ".dat" or ".project";
                if(!archive&&!ruby&&!evtext&&!binary)continue;
                string dataPath=Path.ChangeExtension(path,".dat"),projectPath=Path.ChangeExtension(path,".project");
                bool database=binary&&(ext is ".dat" or ".project")&&(File.Exists(projectPath)||ext==".project");
                if(database&&!paired.Add(dataPath))continue;
                if(++files>1500){reports.Add(new(relative,"达到限制",0,"本轮最多读取 1500 个相关文件"));complete=false;pending.Clear();break;}
                bool wolfCommands=binary&&(ext==".mps"||Path.GetFileName(path).Equals("CommonEvent.dat",StringComparison.OrdinalIgnoreCase));
                bool wolfGame=binary&&Path.GetFileName(path).Equals("Game.dat",StringComparison.OrdinalIgnoreCase);
                if(archive||binary&&!wolfCommands&&!database&&!wolfGame){reports.Add(new(relative,"格式未支持",0,archive?"封包未解包；不能据此判断没有文字":"此 WOLF 数据类型尚未解析；不会把变量、条件或文件路径当成正文"));complete=false;continue;}
                int before=entries.Count;string fileHash="";
                try
                {
                    if(database)
                    {
                        if(!File.Exists(dataPath)||!File.Exists(projectPath))throw new NotSupportedException("WOLF 数据库缺少同名 .project 或 .dat 配对文件。");
                        try{AppDataPaths.RejectReparsePoints(dataPath);AppDataPaths.RejectReparsePoints(projectPath);}
                        catch(ArgumentException ex){throw new NotSupportedException("WOLF 数据库配对包含链接，未跟随读取。",ex);}
                        if(++files>1500)throw new LimitException("数据库配对读取达到 1500 个相关文件限制。");
                        if(new FileInfo(dataPath).Length>32*1024*1024||new FileInfo(projectPath).Length>32*1024*1024)throw new LimitException("WOLF 数据库配对文件超过 32 MB。");
                        byte[] dataImage=File.ReadAllBytes(dataPath),projectImage=File.ReadAllBytes(projectPath);
                        string dataFile=Path.GetRelativePath(root,dataPath),schemaFile=Path.GetRelativePath(root,projectPath);
                        string dataHash=CatalogOrigins.Hash(dataImage),schemaHash=CatalogOrigins.Hash(projectImage);
                        var parsed=WolfDatabaseCatalog.Read(projectImage,dataImage,Check,skipMalformedStrings:true);
                        foreach(var field in parsed.Fields)
                        {Check();entries.Add(new(dataFile,field.Location,field.Text){ContentHash=dataHash,Kind="wolf-database-string",Encoding=parsed.Encoding,SchemaFile=schemaFile,SchemaHash=schemaHash,Context=field.Context});}
                        reports.Add(new(dataFile,parsed.UnreadableStrings>0?"部分读取":"读取完成",parsed.Fields.Length,$"已配对结构读取数据库；{parsed.Excluded} 个键/非普通文本字段未送翻译；{parsed.UnreadableStrings} 处字符串编码异常已隔离；未改写数据库。"));
                        if(parsed.UnreadableStrings>0)complete=false;
                        reports.Add(new(schemaFile,"读取完成",0,"仅用作数据库字段、行名和类型上下文；未将编辑器名称当成对白。"));
                        continue;
                    }
                    if(new FileInfo(path).Length>32*1024*1024)throw new LimitException("单个文件超过 32 MB");
                    byte[] image=File.ReadAllBytes(path);fileHash=CatalogOrigins.Hash(image);
                    if(wolfGame)
                    {
                        foreach(var field in WolfTextCatalog.ReadGame(image,Check))Add(field.Location,field.Text);
                    }
                    else if(wolfCommands)
                    {
                        foreach(var field in WolfTextCatalog.Read(image,ext==".mps",Check))Add(field.Location,field.Text);
                    }
                    else if(evtext)
                    {
                        string[] lines=Decode(image).Split('\n');
                        var paragraph=new List<string>();int start=0,length=0;
                        void Flush(){if(paragraph.Count>0)Add($"line:{start+1}",string.Join("\n",paragraph));paragraph.Clear();length=0;}
                        for(int i=0;i<lines.Length;i++)
                        {
                            Check();string text=lines[i].Trim();
                            if(text.Length==0||"/@<#".Contains(text[0])){Flush();continue;}
                            if(length+text.Length+1>6000)Flush();if(paragraph.Count==0)start=i;paragraph.Add(text);length+=text.Length+1;
                        }
                        Flush();
                    }
                    else if(ruby&&(relative.Replace('\\','/').Equals(configuredScript,StringComparison.OrdinalIgnoreCase)
                        ||configuredScript is null&&Path.GetFileNameWithoutExtension(path).Equals("Scripts",StringComparison.OrdinalIgnoreCase)))
                    {
                        var scripts=RgssScriptCatalog.Read(new RubyMarshalData(image,Check).Read(),Check);
                        foreach(var field in scripts.Fields)
                        {Check();entries.Add(new(relative,field.Location,field.Text){ContentHash=fileHash,Kind="rgss-script-display",Encoding=field.Encoding,Context=field.Context});}
                        reports.Add(new(relative,"读取完成",scripts.Fields.Length,"只读取已支持的显示字面量，不执行脚本；动态与其他语法仍需运行时采集。"));
                        foreach(string issue in scripts.Issues){reports.Add(new(relative,"格式未支持",0,issue));complete=false;}
                        // Static sinks cannot establish complete game coverage.
                        complete=false;continue;
                    }
                    else
                    {
                        var reader=new RubyMarshalData(image,Check);var document=reader.Read();
                        var stack=new Stack<(RubyMarshalData.Node Node,string Path)>();stack.Push((document,"$"));var visited=new HashSet<RubyMarshalData.Node>(ReferenceEqualityComparer.Instance);
                        while(stack.Count>0)
                        {
                            Check();var (node,location)=stack.Pop();if(!visited.Add(node))continue;
                            if(node.Kind=='o'&&node.Name=="RPG::EventCommand"&&node.Fields.TryGetValue("@code",out var code)&&node.Fields.TryGetValue("@parameters",out var parameters))
                            {
                                if(code.Number is 401 or 405||code.Number==101&&parameters.Items.Count==1){if(parameters.Items.FirstOrDefault() is {} first&&first.Kind=='"')Add(location+"/@parameters/0",Decode(first.Bytes,first.Encoding));}
                                else if(code.Number==102&&parameters.Items.FirstOrDefault() is {} choices)for(int i=0;i<choices.Items.Count;i++)if(choices.Items[i].Kind=='"')Add(location+$"/@parameters/0/{i}",Decode(choices.Items[i].Bytes,choices.Items[i].Encoding));
                                // Script, plugin and file-name parameters deliberately remain opaque.
                                continue;
                            }
                            bool displayObject=node.Kind=='o'&&node.Name is "RPG::Actor" or "RPG::Class" or "RPG::Skill" or "RPG::Item" or "RPG::Weapon" or "RPG::Armor" or "RPG::Enemy" or "RPG::State" or "RPG::Map" or "RPG::System" or "RPG::System::Terms" or "RPG::System::Words";
                            foreach(var p in node.Fields)
                            {
                                string at=location+"/"+p.Key;
                                if(displayObject&&p.Value.Kind=='"'&&p.Key is "@name" or "@description" or "@nickname" or "@display_name" or "@game_title" or "@currency_unit" or "@message1" or "@message2" or "@message3" or "@message4")Add(at,Decode(p.Value.Bytes,p.Value.Encoding));
                                if(node.Name is "RPG::System::Terms" or "RPG::System::Words")
                                {if(p.Value.Kind=='"')Add(at,Decode(p.Value.Bytes,p.Value.Encoding));else for(int i=0;i<p.Value.Items.Count;i++)if(p.Value.Items[i].Kind=='"')Add(at+"/"+i,Decode(p.Value.Items[i].Bytes,p.Value.Items[i].Encoding));}
                                stack.Push((p.Value,at));
                            }
                            for(int i=node.Items.Count-1;i>=0;i--)stack.Push((node.Items[i],location+"/"+i));
                        }
                    }
                    reports.Add(new(relative,entries.Count==before?"未发现可提取字段":"读取完成",entries.Count-before,entries.Count==before?"已完成当前已支持字段的解析，不代表游戏没有其他文本":"已保留文件和字段位置；译文写回尚未接入"));
                }
                catch(LimitException ex){reports.Add(new(relative,"达到限制",entries.Count-before,ex.Message));complete=false;}
                catch(NotSupportedException ex){reports.Add(new(relative,"格式未支持",entries.Count-before,ex.Message));complete=false;}
                catch(Exception ex)when(ex is IOException or DecoderFallbackException or InvalidDataException or UnauthorizedAccessException or OverflowException)
                {reports.Add(new(relative,"解析失败",entries.Count-before,SafeDiagnosticOutput.ExceptionSummary(ex)));complete=false;}
                void Add(string location,string text){if(text.Length<=6000&&RpgTranslationText.HasTranslatableText(text,engine=="WOLF RPG Editor"?EmbeddedTextSyntax.Wolf:EmbeddedTextSyntax.Rpg))entries.Add(new(relative,location,text){ContentHash=fileHash});else if(text.Length>6000)throw new LimitException("文本超过 6000 字符，后续字段未读取");}
            }
        }
        return new(engine,entries.Distinct().ToArray(),reports.ToArray(),complete,"已支持的原文可准备译文并保存到缓存；游戏内写回与显示尚未接入。");
    }
    private static string Decode(byte[] bytes,string encoding="")
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if(encoding.Length>0)return Encoding.GetEncoding(encoding,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback).GetString(bytes);
        try{return new UTF8Encoding(false,true).GetString(bytes).TrimStart('\uFEFF');}
        catch(DecoderFallbackException){return Encoding.GetEncoding(932,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback).GetString(bytes);}
    }
    internal sealed class LimitException(string message="达到 20 秒或 100000 条文本限制"):IOException(message);
}

// Implements the documented Marshal 4.8 wire types as inert nodes, without
// invoking Ruby classes, user hooks or constructors. Reference cycles are data.
internal sealed class RubyMarshalData(byte[] bytes,Action check)
{
    internal sealed class Node(char kind)
    {internal char Kind=kind;internal string Name="",Encoding="";internal int Number;internal byte[] Bytes=[];internal List<Node> Items=[];internal Dictionary<string,Node> Fields=new(StringComparer.Ordinal);}
    private int offset,nodes;private readonly List<Node> objects=[];private readonly List<string> symbols=[];
    private byte Byte(){if(offset>=bytes.Length)throw new InvalidDataException("Marshal 文件截断");return bytes[offset++];}
    private int Int()
    {
        int first=unchecked((sbyte)Byte());if(first==0)return 0;if(first>4)return first-5;if(first< -4)return first+5;
        uint value=first<0?uint.MaxValue:0;int count=Math.Abs(first);for(int i=0;i<count;i++){uint mask=255u<<(8*i);value=(value&~mask)|((uint)Byte()<<(8*i));}return unchecked((int)value);
    }
    private int Count(int max=1000000){int count=Int();if(count<0||count>max)throw new ExperimentalRpgCatalog.LimitException("Marshal 容器数量超过限制");return count;}
    private byte[] Bytes(){int count=Count(32*1024*1024);if(count>bytes.Length-offset)throw new InvalidDataException("Marshal 字符串截断");byte[] result=bytes.AsSpan(offset,count).ToArray();offset+=count;return result;}
    private string Symbol(int depth)
    {var node=Value(depth+1);if(node.Kind!=':')throw new InvalidDataException("Marshal 字段名不是符号");return node.Name;}
    internal Node Read(){if(Byte()!=4||Byte()!=8)throw new NotSupportedException("只支持 Marshal 4.8 格式");var value=Value(0);if(offset!=bytes.Length)throw new InvalidDataException("Marshal 文件含未解析尾部数据");return value;}
    private Node Register(Node n){objects.Add(n);return n;}
    private Node Value(int depth)
    {
        check();if(depth>128||++nodes>1000000)throw new ExperimentalRpgCatalog.LimitException("Marshal 深度或节点数量达到限制");char type=(char)Byte();var n=new Node(type);
        switch(type)
        {
            case '0':case 'T':case 'F':return n;
            case 'i':n.Number=Int();return n;
            case ':':n.Name=Encoding.UTF8.GetString(Bytes());symbols.Add(n.Name);return n;
            case ';':int symbol=Count();if(symbol>=symbols.Count)throw new InvalidDataException("无效符号引用");return new(':'){Name=symbols[symbol]};
            case '@':int index=Count();if(index>=objects.Count)throw new InvalidDataException("无效对象引用");return objects[index];
            case '"':n.Bytes=Bytes();return Register(n);
            case 'I':
                n=Value(depth+1);int variables=Count();for(int i=0;i<variables;i++){string name=Symbol(depth);var v=Value(depth+1);if(name=="E")n.Encoding=v.Kind=='T'?"utf-8":v.Kind=='F'?"us-ascii":"";else if(name=="encoding"&&v.Kind=='"')n.Encoding=Encoding.ASCII.GetString(v.Bytes);else n.Fields[name]=v;}return n;
            case '[':Register(n);int length=Count();for(int i=0;i<length;i++)n.Items.Add(Value(depth+1));return n;
            case '{':case '}':Register(n);int pairs=Count();for(int i=0;i<pairs*2;i++)n.Items.Add(Value(depth+1));if(type=='}')n.Items.Add(Value(depth+1));return n;
            case 'o':case 'S':
                n.Name=Symbol(depth);Register(n);int fields=Count();for(int i=0;i<fields;i++){string name=Symbol(depth);n.Fields[name]=Value(depth+1);}return n;
            case 'u':n.Name=Symbol(depth);n.Bytes=Bytes();return Register(n);
            case 'f':case 'c':case 'm':case 'M':n.Bytes=Bytes();return Register(n);
            case '/':n.Bytes=Bytes();n.Number=Byte();return Register(n);
            case 'l':n.Number=Byte();int words=Count(16000000);if(words*2>bytes.Length-offset)throw new InvalidDataException("Marshal 整数截断");offset+=words*2;return Register(n);
            default:throw new NotSupportedException($"Marshal 类型 {type} 尚未支持；文件未执行或改写");
        }
    }
}
