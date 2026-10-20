using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

// Background collection after translation is requested, never inspection-triggered. The
// original runtime receives only its own disposable copy. The source catalogue
// is independent of API settings; translations keep their existing identities.
internal static class WolfEngineCollection
{
    internal const int Version=2;
    internal sealed record Raw(string File,string Text);
    internal sealed record MapResult(string File,bool Ok);
    internal sealed record Document(int Version,string Fingerprint,bool MapsComplete,Raw[] Records,MapResult[] Maps,int Rejected,int RawRecords=0);
    private static readonly ConcurrentDictionary<string,SemaphoreSlim> jobs=new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions json=new(){PropertyNameCaseInsensitive=true,WriteIndented=true};
    private static readonly Regex resources=new(@"(?:https?://|[A-Za-z]:[\\/]|[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}|\.(?:png|jpe?g|bmp|gif|webp|ogg|wav|mp3|m4a|mid|midi|avi|mp4|mps|dat|project|wolf|txt|csv|exe|dll|ttf|otf)(?:$|[\s\""']))",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant|RegexOptions.NonBacktracking,TimeSpan.FromMilliseconds(50));
    private static readonly Regex assignmentLines=new(@"(?m)^\s*[A-Za-z_][A-Za-z0-9_]*\s*=",RegexOptions.CultureInvariant|RegexOptions.NonBacktracking,TimeSpan.FromMilliseconds(50));
    private static readonly Regex enumLines=new(@"(?m)^\s*\d+\s*[：:]",RegexOptions.CultureInvariant|RegexOptions.NonBacktracking,TimeSpan.FromMilliseconds(50));
    internal static bool Accept(string text)
    {
        if(text.Length is <2 or >6000||text.Any(c=>char.IsControl(c)&&c is not ('\n' or '\r' or '\t'))||resources.IsMatch(text))return false;
        if(assignmentLines.Matches(text).Count>=2||enumLines.Matches(text).Count>=3)return false;
        return WolfTextCatalog.AssignmentProse(text)&&RpgTranslationText.HasTranslatableText(text,EmbeddedTextSyntax.Wolf);
    }
    private static bool Accept(Raw r)=>r.File=="engine-display"?r.Text.Length is >=2 and <=6000&&!r.Text.Any(c=>char.IsControl(c)&&c is not ('\r' or '\n' or '\t'))&&!resources.IsMatch(r.Text)&&RpgTranslationText.HasTranslatableText(r.Text,EmbeddedTextSyntax.Wolf):Accept(r.Text);
    internal static bool Supported(string exe)
    {
        try
        {
            if(!OperatingSystem.IsWindows())return false;
            var version=FileVersionInfo.GetVersionInfo(exe);if(version.FileMajorPart is not (2 or 3))return false;
            using var file=File.OpenRead(exe);if(file.Length>128L*1024*1024)return false;
            using var pe=new PEReader(file);if(pe.PEHeaders.CoffHeader.Machine!=Machine.I386)return false;
            if(version.FileMajorPart==2)
            {
                if(version.FileMinorPart!=271)return false;
                var old=Calls(pe,"55 8b ec 6a ff 68 ?? ?? ?? ?? 64 a1 00 00 00 00 50 64 89 25 00 00 00 00 81 ec f0 04 00 00",-1);
                var length=Calls(pe,"8B 4C 24 04 F7 C1 03 00 00 00 74 24 8A 01 83 C1 01 84 C0 74 4E F7 C1 03 00 00 00 75 EF 05 00 00 00 00 8D A4 24 00 00 00 00 8D A4 24 00 00 00 00 8B 01",-1);
                return old.Length==1&&length.Length==1;
            }
            // Corroborating call sites must agree on the same parser, not merely
            // a short prologue somewhere in a customized executable.
            var a=Calls(pe,"8d 4d ?? e8 ?? ?? ?? ?? 50 8d 8e ?? ?? ?? ?? e8 ?? ?? ?? ?? 84 c0 75 ??",16);
            var b=Calls(pe,"6a 01 8d 4d ?? c6 45 ?? 01 e8 ?? ?? ?? ?? 50 8d 8d ?? ?? ?? ?? e8 ?? ?? ?? ?? 84 c0 75 0c",22);
            if(a.Length==1&&b.Length==1&&a[0]==b[0])return Prologue(a[0]);
            if(a.Length==0&&b.Length==0&&FileVersionInfo.GetVersionInfo(exe).FileMinorPart==714)
            {
                var newer=Calls(pe,"c6 45 fc 01 8d 45 ?? 83 7d ?? 0f 8d 8d ?? ?? ?? ff 6a 01",25);
                return newer.Length==1&&Prologue(newer[0]);
            }
            return false;
            bool Prologue(int rva)=>pe.GetSectionData(rva).GetContent(0,5).AsSpan().SequenceEqual(new byte[]{0x55,0x8b,0xec,0x6a,0xff});
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException or InvalidOperationException){return false;}
    }
    private static int[] Calls(PEReader pe,string value,int displacement)
    {
        int[] pattern=value.Split(' ').Select(s=>s=="??"?-1:Convert.ToInt32(s,16)).ToArray();var result=new List<int>();
        foreach(var section in pe.PEHeaders.SectionHeaders.Where(s=>(s.SectionCharacteristics&SectionCharacteristics.MemExecute)!=0))
        {
            var bytes=pe.GetSectionData(section.VirtualAddress).GetContent();
            for(int p=0;p<=bytes.Length-pattern.Length;p++)
            {
                bool match=true;for(int i=0;i<pattern.Length;i++)if(pattern[i]>=0&&bytes[p+i]!=pattern[i]){match=false;break;}
                if(!match)continue;int target=displacement<0?section.VirtualAddress+p:checked(section.VirtualAddress+p+displacement+4+System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p+displacement,4)));
                if(pe.PEHeaders.SectionHeaders.Any(s=>(s.SectionCharacteristics&SectionCharacteristics.MemExecute)!=0&&target>=s.VirtualAddress&&target<s.VirtualAddress+s.VirtualSize))result.Add(target);
                if(result.Count>1)return result.ToArray();
            }
        }return result.ToArray();
    }
    internal static string Fingerprint(string exe,CancellationToken token)
    {
        exe=Path.GetFullPath(exe);string root=Path.GetDirectoryName(exe)!;using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string s)=>hash.AppendData(Encoding.UTF8.GetBytes(s+"\n"));
        Add("WolfEngineCollection/"+Version);int count=0;long total=0;
        string collector=Path.Combine(AppContext.BaseDirectory,"adapters","wolf","FusionWolf.dll");if(File.Exists(collector)){using var runtime=File.OpenRead(collector);Add(Convert.ToHexString(SHA256.HashData(runtime)));}
        foreach(string path in InputFiles(root).Where(p=>p.Equals(exe,StringComparison.OrdinalIgnoreCase)||Path.GetExtension(p).ToLowerInvariant() is ".mps" or ".dat" or ".project" or ".wolf" or ".ini").Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();AppDataPaths.RejectReparsePoints(path);if(++count>100000)throw new IOException("WOLF 来源文件数量超过限制。");
            var before=new FileInfo(path);total+=before.Length;if(total>16L*1024*1024*1024)throw new IOException("WOLF 收集来源超过 16 GB 限制。");
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);byte[] block=new byte[131072];using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);int n;
            while((n=stream.Read(block))>0){token.ThrowIfCancellationRequested();digest.AppendData(block,0,n);}
            var after=new FileInfo(path);if(before.Length!=after.Length||before.LastWriteTimeUtc!=after.LastWriteTimeUtc)throw new IOException("读取期间游戏资源发生变化，请稍后重试。");
            Add(Path.GetRelativePath(root,path).Replace('\\','/').ToUpperInvariant()+":"+before.Length+":"+Convert.ToHexString(digest.GetHashAndReset()));
        }return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static IEnumerable<string> InputFiles(string root)
    {
        var pending=new Stack<(string,int)>();pending.Push((root,0));int count=0;
        while(pending.TryPop(out var item))foreach(string p in Directory.EnumerateFileSystemEntries(item.Item1).Order(StringComparer.OrdinalIgnoreCase))
        {
            if(++count>100000)throw new IOException("WOLF 副本文件数量超过限制。");AppDataPaths.RejectReparsePoints(p);string name=Path.GetFileName(p);
            if(name.StartsWith(".fusion",StringComparison.OrdinalIgnoreCase)||name.Equals("Save",StringComparison.OrdinalIgnoreCase)||name.Equals("SaveData",StringComparison.OrdinalIgnoreCase))continue;
            if(Directory.Exists(p))
            {
                if(item.Item2>=16)throw new IOException("WOLF 副本目录层级超过限制。");
                if(item.Item2>0||name.Equals("Data",StringComparison.OrdinalIgnoreCase))pending.Push((p,item.Item2+1));
            }
            else if(!name.EndsWith(".sav",StringComparison.OrdinalIgnoreCase)&&!name.EndsWith(".bak",StringComparison.OrdinalIgnoreCase))yield return p;
        }
    }
    private static string DirectoryFor(string exe)=>Path.Combine(AppDataPaths.Root,"wolf-engine-catalog",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(exe).ToUpperInvariant()))));
    internal static Document? Load(string exe,string fingerprint)
    {
        string file=Path.Combine(DirectoryFor(exe),"catalog.json");
        try
        {
            AppDataPaths.RejectReparsePoints(file);if(!File.Exists(file)||new FileInfo(file).Length>40L*1024*1024)return null;
            var d=JsonSerializer.Deserialize<Document>(File.ReadAllText(file),json);
            return d is not null&&d.Version==Version&&d.Fingerprint==fingerprint&&d.Records is {Length:<=200000}&&d.Maps is {Length:<=5000}&&d.Records.All(r=>r is not null&&ValidFile(r.File)&&r.Text is {Length:<=24000}&&!r.Text.Contains('\0'))&&d.Maps.All(m=>m is not null&&ValidFile(m.File))&&d.Maps.Select(m=>m.File).Distinct(StringComparer.OrdinalIgnoreCase).Count()==d.Maps.Length&&(!d.MapsComplete||d.Maps.Length>0&&d.Maps.All(m=>m.Ok))?d:null;
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or ArgumentException){return null;}
    }
    private static bool ValidFile(string name)=>name is "engine-initialization" or "engine-display"||name is {Length:>0 and <1024}&&!name.Any(char.IsControl)&&name.StartsWith("Data/",StringComparison.Ordinal)&&!name.Contains(':')&&!name.Contains('\\')&&name.Split('/').All(s=>s.Length>0&&s is not ("." or ".."))&&name.EndsWith(".mps",StringComparison.OrdinalIgnoreCase);
    internal static ExperimentalRpgCatalog.Result Read(string exe,CancellationToken token,bool collect)
    {
        var original=ExperimentalRpgCatalog.Read(exe,token);
        if(!Supported(exe))return original with{Files=[..original.Files,new("引擎收集","格式未支持",0,"当前运行器尚未验证地图加载接口；保留静态提取与实时补充。") ]};
        string fingerprint=Fingerprint(exe,token);var gate=jobs.GetOrAdd(DirectoryFor(exe),_=>new(1,1));gate.Wait(token);
        try
        {
            Document? document=Load(exe,fingerprint);string failure="";
            if((document is null||!document.MapsComplete)&&collect)
            {
                try{document=Collect(exe,fingerprint,token);}
                catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or System.ComponentModel.Win32Exception or TimeoutException){failure=SafeDiagnosticOutput.ExceptionSummary(e);}
            }
            if(document is null)return original with{Files=[..original.Files,new("引擎收集","部分读取",0,failure.Length>0?failure:"尚未建立引擎收集目录，请在下次翻译启动前准备文本。") ]};
            var added=document.Records.Where(Accept).Select((r,i)=>new ExperimentalRpgCatalog.Entry(r.File,"engine-read:"+i,r.Text){ContentHash=fingerprint,Kind=r.File=="engine-display"?"wolf-engine-display-observed":"wolf-engine-read-candidate",Encoding="utf-8",Context="由原游戏解析器读取；仅用于显示缓存，不写回变量或事件。"}).ToArray();
            return original with{Entries=[..original.Entries,..added],Files=[..MergeFileResults(original,document,added),new("引擎收集",document.MapsComplete?"读取完成":"部分读取",added.Select(e=>e.Text).Distinct(StringComparer.Ordinal).Count(),$"原加载器读取 {document.Maps.Count(m=>m.Ok):N0} / {document.Maps.Length:N0} 张地图；收集 {document.RawRecords:N0} 处字符串记录，过滤后加入 {added.Length:N0} 处候选。初始化字符串不代表已验证显示字段。")],Complete=original.Complete&&document.MapsComplete,Boundary="静态字段与原引擎收集目录合并；预译单独保存，启动时复用。动态构造和尚未加载的插件文字仍可能需要补译。"};
        }
        finally{gate.Release();}
    }
    internal static ExperimentalRpgCatalog.FileResult[] MergeFileResults(ExperimentalRpgCatalog.Result original,Document document,ExperimentalRpgCatalog.Entry[] added)
    {
        var maps=document.Maps.ToDictionary(m=>m.File,StringComparer.OrdinalIgnoreCase);
        var additions=added.GroupBy(e=>e.File,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.Count(),StringComparer.OrdinalIgnoreCase);
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows=new List<ExperimentalRpgCatalog.FileResult>();
        foreach(var row in original.Files)
        {
            seen.Add(row.File);
            if(!maps.TryGetValue(row.File,out var map)){rows.Add(row);continue;}
            int count=additions.GetValueOrDefault(row.File);
            rows.Add(row with
            {
                Texts=row.Texts+count,
                Status=count>0?"已补充引擎候选":row.Status,
                Detail=$"静态读取：{row.Status}，{row.Texts:N0} 处；引擎读取：{(map.Ok?"成功":"未完成")}，补充 {count:N0} 处候选。"+row.Detail
            });
        }
        foreach(var map in document.Maps.Where(m=>!seen.Contains(m.File)))
        {
            int count=additions.GetValueOrDefault(map.File);
            rows.Add(new(map.File,count>0?"已补充引擎候选":map.Ok?"未发现候选":"部分读取",count,$"原引擎读取{(map.Ok?"成功":"未完成")}。候选字符串仍需按当前翻译方案筛选；地图加载成功不代表所有显示文字均已覆盖。"));
        }
        return rows.ToArray();
    }
    private static Document Collect(string exe,string fingerprint,CancellationToken token)
    {
        string root=Path.GetDirectoryName(Path.GetFullPath(exe))!,run=Guid.NewGuid().ToString("N");
        // Original 32-bit games are not long-path aware. Nesting their copies
        // under an AppData/game-hash/run-hash chain can prevent initialization
        // before the first map hook, even though .NET copied every file.
        string staging=StagingRoot(),work=Path.Combine(staging,run),copy=Path.Combine(work,"game"),session=Path.Combine(work,"session");
        string diagnostics=Path.Combine(DirectoryFor(exe),"runs",run,"session");
        IntPtr handle=IntPtr.Zero;Process? launcher=null;
        try
        {
        AppDataPaths.RejectReparsePoints(work);Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work,".fusion-collection"),"FusionWolfCollection/1");
        if(Path.Combine(session,"collection-progress.json.tmp").Length>=248)throw new IOException("临时收集目录路径过长，请将系统临时目录设为较短的位置后重试。");
        Directory.CreateDirectory(copy);Directory.CreateDirectory(session);long bytes=0;
        File.WriteAllText(Path.Combine(copy,GameTestCopyEnvironment.Marker),"FusionTranslationTestCopy/1");
        foreach(string source in InputFiles(root))
        {
            token.ThrowIfCancellationRequested();var before=new FileInfo(source);bytes+=before.Length;if(bytes>16L*1024*1024*1024)throw new IOException("WOLF 收集副本超过 16 GB 限制。");
            string target=Path.Combine(copy,Path.GetRelativePath(root,source));
            if(target.Length>=248)throw new IOException("游戏资源的收集副本路径过长，未启动原引擎收集。静态文本仍可使用。");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read);using var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None);byte[] block=new byte[131072];int n;
            while((n=input.Read(block))>0){token.ThrowIfCancellationRequested();output.Write(block,0,n);}
            var after=new FileInfo(source);if(before.Length!=after.Length||before.LastWriteTimeUtc!=after.LastWriteTimeUtc)throw new IOException("复制期间游戏发生变化，收集已取消。");
        }
        string copiedExe=Path.Combine(copy,Path.GetFileName(exe));if(Fingerprint(copiedExe,token)!=fingerprint)throw new IOException("游戏副本校验不符，未启动收集。");
        string ini=Path.Combine(copy,"Game.ini");if(File.Exists(ini)&&new FileInfo(ini).Length<=65536)
        {
            // ASCII replacements preserve a legacy CP932 configuration file.
            byte[] data=File.ReadAllBytes(ini);string text=Encoding.Latin1.GetString(data);
            text=Regex.Replace(text,@"(?m)^SEandBGM=\d+","SEandBGM=0");text=Regex.Replace(text,@"(?m)^ClipBoard_Use=\d+","ClipBoard_Use=0");text=Regex.Replace(text,@"(?m)^WindowModeFlag=\d+","WindowModeFlag=1");
            // Collection only needs the parser. Software drawing avoids device
            // initialization failures on the private, non-interactive desktop.
            text=Regex.Replace(text,@"(?m)^SoftModeFlag=\d+","SoftModeFlag=1");File.WriteAllBytes(ini,Encoding.Latin1.GetBytes(text));
        }
        string secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),desktop="FusionWolfTest_"+Guid.NewGuid().ToString("N");handle=CreateDesktop(desktop,IntPtr.Zero,IntPtr.Zero,0,0x10000000,IntPtr.Zero);
        if(handle==IntPtr.Zero)throw new IOException("未能建立独立收集桌面，未启动游戏。");
            string payload=Path.Combine(AppContext.BaseDirectory,"adapters","wolf");var info=new ProcessStartInfo(copiedExe){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=copy};
            GameTestCopyEnvironment.Apply(info);info.FileName=Path.Combine(payload,"FusionWolfLaunch.exe");info.ArgumentList.Add(copiedExe);info.ArgumentList.Add(Path.Combine(payload,"FusionWolf.dll"));
            info.Environment["FUSION_WOLF_COLLECT"]="1";info.Environment["FUSION_WOLF_SESSION"]=session;info.Environment["FUSION_WOLF_SECRET"]=secret;info.Environment["FUSION_WOLF_OWNER_PID"]=Environment.ProcessId.ToString();info.Environment["FUSION_WOLF_TEST_DESKTOP"]=desktop;
            if(FileVersionInfo.GetVersionInfo(exe).FileMinorPart==714)info.Environment["FUSION_WOLF_COLLECTION_PROFILE"]="3.714";
            if(FileVersionInfo.GetVersionInfo(exe).FileMajorPart==2)info.Environment["FUSION_WOLF_COLLECTION_PROFILE"]="2.271";
            launcher=Process.Start(info)??throw new IOException("未能启动独立引擎收集。");try{launcher.PriorityClass=ProcessPriorityClass.BelowNormal;}catch(InvalidOperationException){}
            var timer=Stopwatch.StartNew();string result=Path.Combine(session,"collection.json");
            while(!File.Exists(result))
            {
                token.ThrowIfCancellationRequested();if(launcher.HasExited)throw new IOException("独立收集未完成；静态目录和已有译文仍可使用。请查看引擎收集运行记录。");
                if(timer.Elapsed.TotalSeconds>150)throw new TimeoutException("引擎收集超时；未把未完成目录标为完整。");token.WaitHandle.WaitOne(50);
            }
            if(new FileInfo(result).Length>40L*1024*1024)throw new IOException("引擎收集结果超过限制。");
            using var raw=JsonDocument.Parse(File.ReadAllText(result));var node=raw.RootElement;
            if(node.ValueKind!=JsonValueKind.Object||!node.TryGetProperty("secret",out var replySecret)||replySecret.ValueKind!=JsonValueKind.String||replySecret.GetString()!=secret||!node.TryGetProperty("version",out var replyVersion)||!replyVersion.TryGetInt32(out int v)||v!=Version)throw new IOException("引擎收集结果身份验证失败。");
            if(node.TryGetProperty("error",out _))throw new IOException("游戏解析器未能完成独立收集，未把结果标为完整。");
            if(!node.TryGetProperty("method",out var method)||method.ValueKind!=JsonValueKind.String||method.GetString()!="engine-map-loader"||!node.TryGetProperty("maps",out var mapNodes)||mapNodes.ValueKind!=JsonValueKind.Array||!node.TryGetProperty("records",out var recordNodes)||recordNodes.ValueKind!=JsonValueKind.Array||!node.TryGetProperty("complete",out var completeNode)||completeNode.ValueKind is not (JsonValueKind.True or JsonValueKind.False)||!node.TryGetProperty("rejected",out var rejectedNode)||!rejectedNode.TryGetInt32(out _))throw new IOException("引擎收集结果格式无效。");
            var records=JsonSerializer.Deserialize<Raw[]>(node.GetProperty("records"),json)??[];var maps=JsonSerializer.Deserialize<MapResult[]>(node.GetProperty("maps"),json)??[];
            if(records.Length>200000||maps.Length>5000||records.Any(r=>r is null||!ValidFile(r.File)||r.Text is null||r.Text.Length>24000)||maps.Any(m=>m is null||!ValidFile(m.File))||maps.Select(m=>m.File).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=maps.Length)throw new IOException("引擎收集结果范围无效。");
            bool complete=node.GetProperty("complete").GetBoolean()&&maps.Length>0&&maps.All(m=>m.Ok);
            var document=new Document(Version,fingerprint,complete,records.Where(Accept).ToArray(),maps,node.GetProperty("rejected").GetInt32(),records.Length);
            if(Fingerprint(exe,token)!=fingerprint)throw new IOException("收集期间来源发生变化，结果未保存。");
            string cache=Path.Combine(DirectoryFor(exe),"catalog.json");TranslationStorage.WriteJson(cache,document);
            if(Load(exe,fingerprint) is not {} verified)throw new IOException("引擎收集目录保存校验失败。");return verified;
        }
        finally
        {
            if(launcher is not null){try{if(!launcher.HasExited){launcher.Kill();launcher.WaitForExit(5000);}}finally{launcher.Dispose();}}
            if(handle!=IntPtr.Zero)CloseDesktop(handle);
            SaveDiagnostics(session,diagnostics);
            CleanupCopy(staging,work);
        }
    }
    private static string StagingRoot()
    {
        string root=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"FusionWolf"));
        AppDataPaths.RejectReparsePoints(root);Directory.CreateDirectory(root);
        var shortName=new StringBuilder(32768);uint length=GetShortPathName(root,shortName,shortName.Capacity);
        return length>0&&length<shortName.Capacity?Path.GetFullPath(shortName.ToString()):root;
    }
    private static void SaveDiagnostics(string session,string target)
    {
        try
        {
            AppDataPaths.RejectReparsePoints(target);Directory.CreateDirectory(target);
            foreach(string name in new[]{"launch.json","collection-progress.json","collection.json"})
            {
                string source=Path.Combine(session,name);AppDataPaths.RejectReparsePoints(source);
                if(File.Exists(source)&&new FileInfo(source).Length<=40L*1024*1024)File.Copy(source,Path.Combine(target,name),overwrite:true);
            }
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException){AppLog.Write("wolf-collection-diagnostics",SafeDiagnosticOutput.ExceptionSummary(e));}
    }
    private static void CleanupCopy(string staging,string work)
    {
        // Resolve and check the final absolute target before recursive removal.
        // Never touch the original root or a folder with a link in its path.
        try
        {
            string target=Path.GetFullPath(work),marker=Path.Combine(target,".fusion-collection");
            if(!string.Equals(Path.GetDirectoryName(target),Path.GetFullPath(staging),StringComparison.OrdinalIgnoreCase)||!Regex.IsMatch(Path.GetFileName(target),"^[0-9a-f]{32}$")||!File.Exists(marker)||File.ReadAllText(marker)!="FusionWolfCollection/1")return;
            AppDataPaths.RejectReparsePoints(target);var pending=new Stack<string>();pending.Push(target);
            while(pending.TryPop(out string? dir))foreach(string p in Directory.EnumerateFileSystemEntries(dir)){AppDataPaths.RejectReparsePoints(p);if(Directory.Exists(p))pending.Push(p);}
            Directory.Delete(target,recursive:true);
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException){AppLog.Write("wolf-collection-cleanup",SafeDiagnosticOutput.ExceptionSummary(e));}
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,EntryPoint="GetShortPathNameW",SetLastError=true)]private static extern uint GetShortPathName(string path,StringBuilder shortPath,int capacity);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr CreateDesktop(string desktop,IntPtr device,IntPtr mode,uint flags,uint access,IntPtr attrs);
    [DllImport("user32.dll")]private static extern bool CloseDesktop(IntPtr desktop);
}
