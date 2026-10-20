using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class WolfGameAdapter
{
    internal const string Folder=".fusion-wolf";
    private static readonly string[] Owned=["FusionWolf.dll","FusionWolfLaunch.exe","bridge-font.otf"];
    private sealed record Ledger(int Version,string ExeHash,string DataHash,Dictionary<string,string> Files);
    private static string InGame(string exe,string path)=>GameMakerGameAdapter.InGame(exe,path);
    private static string Hash(string file){using var s=File.OpenRead(file);return Convert.ToHexString(SHA256.HashData(s));}
    private static string GameHash(string exe)=>Convert.ToHexString(SHA256.HashData(WolfResources.GameImage(exe)));
    private static int[] Pattern(string name){
        using var stream=typeof(WolfGameAdapter).Assembly.GetManifestResourceStream("Fusion.Wolf"+name+".txt")!;
        using var reader=new StreamReader(stream);return reader.ReadToEnd().Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).Select(x=>x=="??"?-1:Convert.ToInt32(x,16)).ToArray();
    }
    internal static readonly Lazy<int[]> LayoutPattern=new(()=>Pattern("LayoutPattern"));
    private static readonly Lazy<int[]> LegacyLayoutPattern=new(()=>Pattern("LegacyLayoutPattern")),LegacyConvertPattern=new(()=>Pattern("LegacyConvertPattern"));
    private static readonly Lazy<int[]> Layout31Pattern=new(()=>Pattern("Layout31Pattern"));
    internal static int FindLayoutRva(string exe)
    {
        int current=FindPatternRva(exe,LayoutPattern.Value);if(current!=0)return current;
        // Separate 3.100 candidate ABI: the 24-byte payload at this+24 is
        // checked against compare/c_str/length callees. Startup transport is
        // tested separately; this is not a claim of complete scene coverage.
        var version=FileVersionInfo.GetVersionInfo(exe);
        return version.FileMajorPart==3&&version.FileMinorPart==100?FindPatternRva(exe,Layout31Pattern.Value):0;
    }
    internal static int FindLegacyLayoutRva(string exe)=>FindPatternRva(exe,LegacyLayoutPattern.Value);
    private static int FindPatternRva(string exe,int[] pattern)
    {
        using var file=File.OpenRead(exe);if(file.Length>128L*1024*1024)return 0;
        using var pe=new PEReader(file);var headers=pe.PEHeaders;
        if(headers.CoffHeader.Machine!=Machine.I386||headers.PEHeader?.Magic!=PEMagic.PE32||headers.SectionHeaders.Length>96)return 0;
        int result=0;
        if(pattern.Length is <64 or >256)return 0;
        foreach(var section in headers.SectionHeaders)
        {
            if((section.SectionCharacteristics&SectionCharacteristics.MemExecute)==0)continue;
            if(section.SizeOfRawData<pattern.Length||section.SizeOfRawData>128*1024*1024)continue;
            var data=pe.GetSectionData(section.VirtualAddress).GetContent();
            for(int offset=0;offset<=data.Length-pattern.Length;offset++)
            {
                if(data[offset]!=pattern[0])continue;bool match=true;
                for(int i=1;i<pattern.Length;i++)if(pattern[i]>=0&&data[offset+i]!=pattern[i]){match=false;break;}
                if(match){if(result!=0)return 0;result=section.VirtualAddress+offset;}
            }
        }
        return result;
    }
    internal static bool Candidate(string exe)
    {
        try
        {
            var version=FileVersionInfo.GetVersionInfo(exe);if(version.FileMajorPart is not (2 or 3))return false;
            // A typed read-only archive reader establishes the same Game.dat
            // format contract as a loose project. Renderer signatures remain
            // mandatory; no address or native loader is inferred from a seal.
            byte[] image=WolfResources.GameImage(exe);if(image.Length<10)return false;var magic=image.AsSpan(0,10);
            if(!magic[..9].SequenceEqual(new byte[]{0,0x57,0,0,0x4f,0x4c,0,0x46,0x4d}))return false;
            if(version.FileMajorPart==3&&magic[9]==0x55)return FindLayoutRva(exe)!=0;
            return version.FileMajorPart==2&&magic[9]==0&&FindLegacyLayoutRva(exe)!=0&&FindPatternRva(exe,LegacyConvertPattern.Value)!=0;
        }
        catch(Exception e)when(e is IOException or InvalidDataException or UnauthorizedAccessException or BadImageFormatException or ArgumentException or NotSupportedException){return false;}
    }
    private static void Stopped(string exe)
    {
        if(FusionAdapterInstaller.ProcessState(new(exe,"WOLF","WOLF RPG Editor","32 位","",SupportLevel.Candidate,null,"wolf"))!=FusionGameProcessState.Stopped)
            throw new IOException("请先退出游戏，再准备或恢复翻译。");
    }
    internal static bool HasRecord(string exe)=>File.Exists(InGame(exe,Folder+"/install.json"));
    private static Ledger ReadLedger(string exe)
    {
        string path=InGame(exe,Folder+"/install.json");if(new FileInfo(path).Length>16384)throw new IOException("组件记录过大。");
        var record=JsonSerializer.Deserialize<Ledger>(File.ReadAllText(path))??throw new IOException("组件记录无效。");
        bool Digest(string s)=>s is {Length:64}&&s.All(Uri.IsHexDigit);
        if(record.Version!=133||!Digest(record.ExeHash)||!Digest(record.DataHash)||record.Files is null||record.Files.Count!=Owned.Length||
           Owned.Any(n=>!record.Files.ContainsKey(n))||record.Files.Values.Any(v=>!Digest(v)))throw new IOException("组件记录的文件范围无效。");
        return record;
    }
    internal static bool IsInstalled(string exe)
    {
        try{var r=ReadLedger(exe);return Hash(exe)==r.ExeHash&&GameHash(exe)==r.DataHash&&r.Files.All(p=>Hash(InGame(exe,Folder+"/"+p.Key))==p.Value&&Hash(Payload(p.Key))==p.Value);}
        catch(Exception e)when(e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException){return false;}
    }
    internal static string InspectionStamp(string exe)=>exe+"|"+WolfResources.Stamp(exe)+"|"+string.Join("|",new[]{Folder+"/install.json",Folder+"/FusionWolf.dll"}.Select(n=>{var f=new FileInfo(InGame(exe,n));return f.Exists?f.Length+":"+f.LastWriteTimeUtc.Ticks:"missing";}));
    internal static void Install(string exe)
    {
        Stopped(exe);if(IsInstalled(exe))return;
        if(!Candidate(exe))throw new EmbeddedCompatibilityException("此 WOLF 版本尚未找到已验证的文字入口，未修改或启动游戏。");
        GameFontCoverage.RequireChinesePayload(Payload("bridge-font.otf"));
        string folder=InGame(exe,Folder);string? priorHash=null;
        if(Directory.Exists(folder)){ValidateOwned(exe,Folder,ReadLedger(exe));priorHash=Hash(InGame(exe,Folder+"/install.json"));}
        string stage=InGame(exe,Folder+"-stage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
        try
        {
            string exeHash=Hash(exe),dataHash=GameHash(exe);
            foreach(string n in Owned)File.Copy(Payload(n),Path.Combine(stage,n));
            var files=Owned.ToDictionary(n=>n,n=>Hash(Path.Combine(stage,n)),StringComparer.Ordinal);
            if(Hash(exe)!=exeHash||GameHash(exe)!=dataHash)throw new IOException("准备期间游戏文件发生变化，未安装。");
            File.WriteAllText(Path.Combine(stage,"install.json"),JsonSerializer.Serialize(new Ledger(133,exeHash,dataHash,files)),new UTF8Encoding(false));
            Stopped(exe);
            if(priorHash is null){if(Directory.Exists(folder))throw new IOException("翻译目录在准备期间出现，未覆盖。");Directory.Move(stage,folder);}
            else{
                if(Hash(InGame(exe,Folder+"/install.json"))!=priorHash)throw new IOException("组件记录发生变化，未覆盖。");
                var prior=ReadLedger(exe);ValidateOwned(exe,Folder,prior);
                string previousName=Folder+"-previous-"+Guid.NewGuid().ToString("N"),previous=InGame(exe,previousName);
                Directory.Move(folder,previous);
                try{Directory.Move(stage,folder);}catch{if(!Directory.Exists(folder))Directory.Move(previous,folder);throw;}
                try{ValidateOwned(exe,previousName,prior);if(Hash(InGame(exe,previousName+"/install.json"))==priorHash){foreach(string n in Owned)File.Delete(InGame(exe,previousName+"/"+n));File.Delete(InGame(exe,previousName+"/install.json"));Directory.Delete(previous);}}
                catch(IOException){/* Retain a changed previous copy. */}catch(UnauthorizedAccessException){/* Retain an inaccessible previous copy. */}
            }
        }
        finally{if(Directory.Exists(stage)){foreach(string p in Directory.EnumerateFiles(stage))File.Delete(p);Directory.Delete(stage);}}
    }
    private static string Payload(string name)=>Path.Combine(AppContext.BaseDirectory,"adapters",name=="bridge-font.otf"?"gamemaker":"wolf",name);
    private static void ValidateOwned(string exe,string directory,Ledger ledger){
        if(ledger.Files.Any(p=>!File.Exists(InGame(exe,directory+"/"+p.Key))||Hash(InGame(exe,directory+"/"+p.Key))!=p.Value))throw new IOException("翻译组件已被修改，未覆盖。");
        foreach(string path in Directory.EnumerateFileSystemEntries(InGame(exe,directory))){string name=Path.GetFileName(path);_ = InGame(exe,directory+"/"+name);if(Directory.Exists(path)||name!="install.json"&&!Owned.Contains(name))throw new IOException("翻译目录含有额外文件，未替换。");}
    }
    internal static void Restore(string exe)
    {
        Stopped(exe);if(!HasRecord(exe))return;var record=ReadLedger(exe);
        if(record.Files.Any(p=>!File.Exists(InGame(exe,Folder+"/"+p.Key))||Hash(InGame(exe,Folder+"/"+p.Key))!=p.Value))throw new IOException("翻译组件已被改变，未删除。");
        foreach(string n in Owned)File.Delete(InGame(exe,Folder+"/"+n));File.Delete(InGame(exe,Folder+"/install.json"));
        if(!Directory.EnumerateFileSystemEntries(InGame(exe,Folder)).Any())Directory.Delete(InGame(exe,Folder));
    }
    internal static ExperimentalRpgCatalog.Result DisplayBranches(ExperimentalRpgCatalog.Result r)=>r with{Entries=r.Entries.SelectMany(e=>WolfLanguageBranches.Sources(e.Text).Distinct(StringComparer.Ordinal).Select(text=>e with{Text=text})).ToArray()};
    internal static RpgTextCatalogResult ReadCatalog(string exe,CancellationToken token,bool collect=false)
    {
        var r=collect?WolfEngineCollection.Read(exe,token,collect:true):ExperimentalRpgCatalog.Read(exe,token);
        // Queue the alternatives actually displayed by the game, instead of a
        // bilingual storage value which can never match a visible dialogue.
        r=DisplayBranches(r);
        var issues=r.Files.Where(f=>f.Status is not ("读取完成" or "未发现可提取字段")).Select(f=>new CatalogReadIssue(f.Status=="格式未支持"?CatalogIssueKind.Unsupported:f.Status=="达到限制"?CatalogIssueKind.Limited:CatalogIssueKind.Failed,f.File,f.Detail)).ToList();
        issues.Insert(0,new(CatalogIssueKind.Incomplete,"WOLF","已读取支持的事件、游戏显示设置和配对数据库普通文本；已知格式及变量模板会优先复用预译，未知或有歧义的动态文字在游戏中补译。"));
        string Logical(string file)
        {
            file=file.Replace('\\','/');int split=file.IndexOf('!');if(split<0)return file;
            string archive=file[..split],name=Path.GetFileNameWithoutExtension(archive);
            return (archive.Equals("Data.wolf",StringComparison.OrdinalIgnoreCase)?"Data/":"Data/"+name+"/")+file[(split+1)..];
        }
        var unlocalized=r.Entries.Where(e=>UnityTextResourceLanguageSelection.ResourceLocale(Logical(e.File)) is null&&
            (e.File is "engine-initialization" or "engine-display"||System.Text.RegularExpressions.Regex.IsMatch(Logical(e.File),@"^Data/(?:MapData/[^/]+\.mps|BasicData/[^/]+\.dat)$",System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            .Select(e=>e.Text).ToHashSet(StringComparer.Ordinal);
        var priority=r.Entries.Where(e=>e.Location.Contains(":102/",StringComparison.Ordinal)||e.Location.Contains("/display-db",StringComparison.Ordinal)||
            e.Text.Length<=48&&!e.Text.Any(c=>c is '\n' or '\r' or '。' or '！' or '？' or '!' or '?' or '.')&&
            e.Text.Count(c=>c==' ')<=5).Select(e=>e.Text).ToHashSet(StringComparer.Ordinal);
        return new(r.Entries.Select(e=>e.Text).Distinct(StringComparer.Ordinal).ToArray(),r.Files.Count(f=>f.Status is "读取完成" or "未发现可提取字段"),[]){
            Origins=r.Entries.Select(e=>ExperimentalRpgCatalog.Origin(e,"wolf-display-command")).ToArray(),
            UnlocalizedSourceTexts=unlocalized,PrioritySourceTexts=priority,ReadCompleted=false,Report=new(false,issues.ToArray())};
    }
}
