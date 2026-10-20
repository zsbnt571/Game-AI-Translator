using System.Diagnostics;
using System.Text;

namespace ScreenshotTranslationUiTester;

// A logical Data tree, backed by loose files or independently decoded archive
// members. No plaintext copies or extraction artefacts enter a game directory.
internal sealed class WolfResources : IDisposable
{
    internal sealed record Resource(string Path,string Origin,Func<byte[]> Read);
    internal List<Resource> Files {get;}=[];
    internal List<ExperimentalRpgCatalog.FileResult> Issues {get;}=[];
    private readonly List<WolfArchive> archives=[];
    private WolfResources(){}
    internal static WolfResources Open(string exe,Action check)
    {
        var result=new WolfResources();string root=Path.GetDirectoryName(Path.GetFullPath(exe))!,data=Path.Combine(root,"Data");
        try
        {
            foreach(string directory in new[]{root,data}.Where(Directory.Exists))
            foreach(string file in Directory.EnumerateFiles(directory,"*.wolf",SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase).Take(129))
            {
                check();string rel=Path.GetRelativePath(root,file).Replace('\\','/');
                if(result.archives.Count>=128)throw new InvalidDataException("WOLF 封包数量超过限制。");
                try
                {
                    var archive=new WolfArchive(file,check);result.archives.Add(archive);
                    string name=Path.GetFileNameWithoutExtension(file);string prefix=name.Equals("Data",StringComparison.OrdinalIgnoreCase)&&directory==root?"Data/":"Data/"+name+"/";
                    foreach(var entry in archive.Entries)
                    {check();string logical=prefix+entry.Path;result.Files.Add(new(logical,rel+"!"+entry.Path,()=>archive.Read(entry)));}
                    result.Issues.Add(new(rel,"读取完成",0,$"只读打开 DXArchive v8 / 加密类型 {archive.Seal}，索引 {archive.Entries.Length} 个资源；按需读取文本文件，未展开图片或音频。"));
                }
                catch(Exception e)when(e is IOException or InvalidDataException or NotSupportedException or DecoderFallbackException or ArgumentException)
                {result.Issues.Add(new(rel,"格式未支持",0,SafeDiagnosticOutput.ExceptionSummary(e)));}
            }
            if(Directory.Exists(data))
            {
                var pending=new Stack<(string,int)>();pending.Push((data,0));int files=0;
                while(pending.Count>0)
                {
                    var (dir,depth)=pending.Pop();AppDataPaths.RejectReparsePoints(dir);
                    foreach(string p in Directory.EnumerateFileSystemEntries(dir))
                    {
                        check();if(++files>100000)throw new InvalidDataException("WOLF Data 目录数量超过限制。");
                        if((File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0){result.Issues.Add(new(Path.GetRelativePath(root,p),"格式未支持",0,"资源链接未跟随。"));continue;}
                        if(Directory.Exists(p)){if(depth>=16)throw new InvalidDataException("WOLF 资源目录层级超过限制。");pending.Push((p,depth+1));continue;}
                        string logical=Path.GetRelativePath(root,p).Replace('\\','/');result.Files.Add(new(logical,logical,()=>ReadLoose(p)));
                    }
                }
            }
            return result;
        }
        catch{result.Dispose();throw;}
        byte[] ReadLoose(string path)
        {check();AppDataPaths.RejectReparsePoints(path);using var s=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);if(s.Length>WolfArchive.MaxFile)throw new InvalidDataException("WOLF 单个文本资源超过 32 MB。");byte[] b=new byte[(int)s.Length];s.ReadExactly(b);return b;}
    }
    internal static byte[] GameImage(string exe)
    {
        using var set=Open(exe,()=>{});var candidates=set.Files.Where(f=>f.Path.Equals("Data/BasicData/Game.dat",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(set.Issues.Any(i=>i.File.EndsWith(".wolf",StringComparison.OrdinalIgnoreCase)&&i.Status!="读取完成"))throw new IOException("存在未能验证的 WOLF 封包，未猜测游戏实际使用的 Game.dat。");
        if(candidates.Length==0)throw new IOException("未找到可读取的 WOLF Game.dat。");
        byte[] b=candidates[0].Read();string hash=CatalogOrigins.Hash(b);
        if(candidates.Skip(1).Any(c=>CatalogOrigins.Hash(c.Read())!=hash))throw new IOException("WOLF 封包与散文件 Game.dat 冲突，未猜测游戏加载顺序。");return b;
    }
    internal static string Stamp(string exe)
    {
        string root=Path.GetDirectoryName(exe)!;var paths=new List<string>{Path.Combine(root,"Data/BasicData/Game.dat")};
        foreach(var dir in new[]{root,Path.Combine(root,"Data")}.Where(Directory.Exists))paths.AddRange(Directory.EnumerateFiles(dir,"*.wolf"));
        return string.Join("|",paths.Order(StringComparer.OrdinalIgnoreCase).Select(p=>{var f=new FileInfo(p);return p+":"+(f.Exists?f.Length+":"+f.LastWriteTimeUtc.Ticks:"missing");}));
    }
    public void Dispose(){foreach(var a in archives)a.Dispose();}
}

internal static class WolfResourceCatalog
{
    internal static ExperimentalRpgCatalog.Result Read(string exe,CancellationToken token)
    {
        var timer=Stopwatch.StartNew();var entries=new List<ExperimentalRpgCatalog.Entry>();var reports=new List<ExperimentalRpgCatalog.FileResult>();long bytesRead=0;bool complete=true;
        void Check(){token.ThrowIfCancellationRequested();if(timer.Elapsed.TotalSeconds>90||entries.Count>=100000||bytesRead>256L*1024*1024)throw new ExperimentalRpgCatalog.LimitException("WOLF 读取达到 90 秒、100000 条或 256 MB 限制。");}
        using var set=WolfResources.Open(exe,Check);reports.AddRange(set.Issues);complete=!set.Issues.Any(i=>i.Status!="读取完成");
        var relevant=set.Files.Where(f=>Path.GetExtension(f.Path).ToLowerInvariant() is ".dat" or ".project" or ".mps"||f.Path.Contains("/Evtext/",StringComparison.OrdinalIgnoreCase)&&Path.GetExtension(f.Path).Equals(".txt",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(relevant.Length>5000){reports.Add(new("Data","达到限制",0,"WOLF 相关资源超过 5000 个。"));relevant=relevant.Take(5000).ToArray();complete=false;}
        var byPath=set.Files.GroupBy(f=>f.Path,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.OrdinalIgnoreCase);
        var paired=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var source in relevant)
        {
            Check();string name=Path.GetFileName(source.Path),ext=Path.GetExtension(name).ToLowerInvariant();
            if(ext==".project")
            {
                if(!byPath.TryGetValue(Path.ChangeExtension(source.Path,".dat"),out var dataFiles)||!dataFiles.Any(d=>Container(d.Origin)==Container(source.Origin)))
                {reports.Add(new(source.Origin,"格式未支持",0,"WOLF 数据库缺少同一来源的 .dat 配对文件。"));complete=false;}
                continue;
            }
            bool database=ext==".dat"&&byPath.ContainsKey(Path.ChangeExtension(source.Path,".project"));
            if(database&&!paired.Add(source.Origin))continue;
            int before=entries.Count;
            try
            {
                byte[] image=source.Read();bytesRead+=image.Length;Check();string hash=CatalogOrigins.Hash(image);
                if(database)
                {
                    var schema=byPath[Path.ChangeExtension(source.Path,".project")];
                    var matching=schema.Where(s=>Container(s.Origin)==Container(source.Origin)).ToArray();
                    if(matching.Length!=1)throw new InvalidDataException("WOLF 数据库缺少同一来源的唯一结构配对，未混用封包与散文件。");
                    byte[] project=matching[0].Read();bytesRead+=project.Length;string schemaHash=CatalogOrigins.Hash(project);
                    var parsed=WolfDatabaseCatalog.Read(project,image,Check,skipMalformedStrings:true);
                    foreach(var field in parsed.Fields){Check();entries.Add(new(source.Origin,field.Location,field.Text){ContentHash=hash,Kind="wolf-database-string",Encoding=parsed.Encoding,SchemaFile=matching[0].Origin,SchemaHash=schemaHash,Context=field.Context});}
                    reports.Add(new(matching[0].Origin,"读取完成",0,"仅提供数据库结构上下文，未翻译键、条件和资源路径。"));
                    reports.Add(new(source.Origin,parsed.UnreadableStrings>0?"部分读取":"读取完成",parsed.Fields.Length,$"配对数据库；排除 {parsed.Excluded} 个非正文项，隔离 {parsed.UnreadableStrings} 处编码异常。"));if(parsed.UnreadableStrings>0)complete=false;continue;
                }
                WolfTextCatalog.TextField[] fields;
                if(name.Equals("Game.dat",StringComparison.OrdinalIgnoreCase))fields=WolfTextCatalog.ReadGame(image,Check);
                else if(ext==".mps"||name.Equals("CommonEvent.dat",StringComparison.OrdinalIgnoreCase))fields=WolfTextCatalog.Read(image,ext==".mps",Check);
                else if(ext==".txt")fields=Evtext(image,Check);
                else{reports.Add(new(source.Origin,"格式未支持",0,"非事件或配对数据库类型未解析；不据此判定不存在文字。"));complete=false;continue;}
                foreach(var field in fields){Check();entries.Add(new(source.Origin,field.Location,field.Text){ContentHash=hash,Kind=field.Location.Contains(":122/",StringComparison.Ordinal)?"wolf-display-string-assignment":"wolf-display-command",Encoding=image.Length>16&&image[16]==0x55||image.Length>9&&image[9]==0x55||image.Length>6&&image[6]==0x55?"utf-8":"cp932"});}
                reports.Add(new(source.Origin,fields.Length==0?"未发现可提取字段":"读取完成",fields.Length,"只读解析已知显示字段并保留封包、成员和位置；译文另存缓存。"));
            }
            catch(ExperimentalRpgCatalog.LimitException e){reports.Add(new(source.Origin,"达到限制",entries.Count-before,e.Message));complete=false;break;}
            catch(NotSupportedException e){reports.Add(new(source.Origin,"格式未支持",entries.Count-before,SafeDiagnosticOutput.ExceptionSummary(e)));complete=false;}
            catch(Exception e)when(e is IOException or InvalidDataException or NotSupportedException or DecoderFallbackException or OverflowException)
            {reports.Add(new(source.Origin,"解析失败",entries.Count-before,SafeDiagnosticOutput.ExceptionSummary(e)));complete=false;}
        }
        return new("WOLF RPG Editor",entries.Distinct().ToArray(),reports.ToArray(),complete,"封包及散文件按结构只读提取；预译单独保存，未知动态构造在运行时补充。");
        static string Container(string s)=>s.Contains('!')?s[..s.IndexOf('!')]:"loose";
    }
    private static WolfTextCatalog.TextField[] Evtext(byte[] image,Action check)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);string text;try{text=new UTF8Encoding(false,true).GetString(image).TrimStart('\uFEFF');}catch(DecoderFallbackException){text=Encoding.GetEncoding(932,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback).GetString(image);}
        var result=new List<WolfTextCatalog.TextField>();var paragraph=new List<string>();int start=0,length=0;string[] lines=text.Split('\n');
        void Flush(){if(paragraph.Count>0){string p=string.Join("\n",paragraph);if(RpgTranslationText.HasTranslatableText(p,EmbeddedTextSyntax.Wolf))result.Add(new("line:"+(start+1),p));}paragraph.Clear();length=0;}
        for(int i=0;i<lines.Length;i++){check();string s=lines[i].Trim();if(s.Length==0||"/@<#".Contains(s[0])){Flush();continue;}if(s.Length>6000)throw new InvalidDataException("WOLF 外部文本行过长。");if(length+s.Length+1>6000)Flush();if(paragraph.Count==0)start=i;paragraph.Add(s);length+=s.Length+1;}Flush();return result.ToArray();
    }
}
