using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;
internal enum FusionGameProcessState { Stopped, Running, Unknown }
internal sealed partial class FusionAdapterInstaller
{
    internal const string PluginRelative=LegacyMgiAdapterInstaller.PluginRelative;
    internal const string ConfigRelative=LegacyMgiAdapterInstaller.ConfigRelative;
    readonly LegacyMgiAdapterInstaller mgi=new();
    const string Ledger=".fusion-translator-install.json";
    sealed class Entry
    {
        public string Path {get;set;}="";
        public string? Backup {get;set;}
        public string? BeforeHash {get;set;}
        public string AfterHash {get;set;}="";
        public string? ConfigHash {get;set;}
    }
    sealed class Journal
    {
        public int Schema {get;set;}=2;
        public string AdapterId {get;set;}="cloud-meadow";
        public string Version {get;set;}=BuildIdentity.BuildVersion;
        public string Exe {get;set;}="";
        public string BackupId {get;set;}=Guid.NewGuid().ToString("N");
        public bool Complete {get;set;}
        public List<Entry> Files {get;set;}=new();
        public List<string> RetainedSharedFiles {get;set;}=new();
    }
    static bool Cloud(GameInfo g)=>g.AdapterId=="cloud-meadow";
    internal bool HasRecord(GameInfo g)=>EmbeddedGameAdapters.Handles(g)?EmbeddedGameAdapters.HasRecord(g):g.AdapterId=="renpy"?RenpyGameAdapter.HasRecord(g.ExePath):File.Exists(Path.Combine(Path.GetDirectoryName(g.ExePath)!,Ledger));
    internal bool IsInstalled(GameInfo g)
    {
        if(EmbeddedGameAdapters.Handles(g))return EmbeddedGameAdapters.IsInstalled(g);
        if(g.AdapterId=="renpy")return RenpyGameAdapter.IsInstalled(g.ExePath);
        if(!Cloud(g))return g.AdapterId=="mgi"&&mgi.IsInstalled(g);
        try{return Read(g).Complete&&File.Exists(InRoot(g,FusionCloudPayload.Plugin));}catch{return false;}
    }
    [DllImport("kernel32.dll",SetLastError=true)]static extern IntPtr OpenProcess(uint access,bool inherit,int id);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder name,ref uint size);
    [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
    internal static FusionGameProcessState ProcessState(GameInfo game)
    {
        bool unknown=false;
        var paths=GameRuntimeExecutables.Resolve(game.ExePath);
        foreach(var process in paths.Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(name=>Process.GetProcessesByName(name!)))using(process)
        {
            var handle=OpenProcess(0x1000,false,process.Id);
            if(handle==IntPtr.Zero){try{if(!process.HasExited)unknown=true;}catch{unknown=true;}continue;}
            try{var name=new StringBuilder(32768);uint size=32768;if(!QueryFullProcessImageName(handle,0,name,ref size)){unknown=true;continue;}
                if(paths.Contains(Path.GetFullPath(name.ToString()),StringComparer.OrdinalIgnoreCase))return FusionGameProcessState.Running;
            }finally{CloseHandle(handle);}
        }
        return unknown?FusionGameProcessState.Unknown:FusionGameProcessState.Stopped;
    }
    internal static bool IsRunning(GameInfo g)=>ProcessState(g)!=FusionGameProcessState.Stopped;
    static void RequireStopped(GameInfo g)
    {
        var state=ProcessState(g);
        if(state!=FusionGameProcessState.Stopped)throw new InvalidOperationException(state==FusionGameProcessState.Running?"所选游戏仍在运行，请先退出。":"无法确认同名进程的实际路径，未操作；请先退出相关游戏。");
    }
    static string InRoot(GameInfo game,string relative)
    {
        var root=Path.GetFullPath(Path.GetDirectoryName(game.ExePath)!).TrimEnd(Path.DirectorySeparatorChar);
        var full=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
        if(relative.Contains(':')||!full.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("安装路径不属于所选游戏。");
        for(var part=full;part is not null&&part.Length>=root.Length;part=Path.GetDirectoryName(part))
            if((File.Exists(part)||Directory.Exists(part))&&(File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("安装路径包含链接，已停止。");
        return full;
    }
    static string Hash(byte[] data)=>Convert.ToHexString(SHA256.HashData(data));
    static string FileHash(string path){using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream));}
    static string ConfigurationHash(string text)
    {
        var parts=new List<string>();string section="";
        foreach(var raw in text.Split('\n')){var line=raw.Trim();if(line.Length==0||line.StartsWith('#')||line.StartsWith(';'))continue;if(line.StartsWith('[')&&line.EndsWith(']')){section=line;continue;}int equal=line.IndexOf('=');parts.Add(section+"|"+(equal<0?line:line[..equal].Trim()+"="+line[(equal+1)..].Trim()));}
        parts.Sort(StringComparer.Ordinal);return Hash(Encoding.UTF8.GetBytes(string.Join("\n",parts)));
    }
    static void AtomicWrite(string path,byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);var temp=path+".fusion-writing-"+Guid.NewGuid().ToString("N");
        try{File.WriteAllBytes(temp,data);File.Move(temp,path,true);}finally{if(File.Exists(temp))File.Delete(temp);}
    }
    static void Save(GameInfo g,Journal j)=>AtomicWrite(InRoot(g,Ledger),Encoding.UTF8.GetBytes(JsonSerializer.Serialize(j,new JsonSerializerOptions{WriteIndented=true})));
    static Journal Read(GameInfo g)
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(InRoot(g,Ledger)));
        if(!doc.RootElement.TryGetProperty("Schema",out var schema)||schema.GetInt32()!=2)throw new InvalidDataException("此账本不是 Cloud Meadow 新版安装账本，不自动接管。");
        var j=doc.RootElement.Deserialize<Journal>()??throw new InvalidDataException("安装账本无法读取。");
        if(j.AdapterId!="cloud-meadow"||!Path.GetFullPath(g.ExePath).Equals(j.Exe,StringComparison.OrdinalIgnoreCase)||j.Files.Select(x=>x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=j.Files.Count)throw new InvalidDataException("安装账本与所选游戏不匹配。");
        if(!Guid.TryParseExact(j.BackupId,"N",out _))throw new InvalidDataException("安装账本的备份标识无效。");
        static bool Digest(string? value)=>value is {Length:64}&&value.All(Uri.IsHexDigit);
        foreach(var e in j.Files)
        {
            if((!FusionCloudPayload.Files.ContainsKey(e.Path)&&e.Path!=FusionCloudPayload.Config)||!Digest(e.AfterHash))throw new InvalidDataException("账本包含不属于本适配的文件或无效校验值，已停止。");
            if(e.Backup is null?e.BeforeHash is not null:e.Backup!=".fusion-translator-backup/"+j.BackupId+"/"+e.Path||!Digest(e.BeforeHash))throw new InvalidDataException("账本备份路径或原始校验值无效，已停止。");
            if(e.ConfigHash is not null&&(e.Path!=FusionCloudPayload.Config||!Digest(e.ConfigHash)))throw new InvalidDataException("账本配置校验值无效，已停止。");
        }
        return j;
    }
    static bool OwnedMatches(GameInfo g,Entry e)
    {
        var path=InRoot(g,e.Path);if(!File.Exists(path))return e.Backup is null;
        var hash=FileHash(path);
        return hash==e.AfterHash||e.BeforeHash is not null&&hash==e.BeforeHash||e.ConfigHash is not null&&ConfigurationHash(File.ReadAllText(path))==e.ConfigHash;
    }
    static void CheckJournal(GameInfo g,Journal j)
    {
        foreach(var e in j.Files)
        {
            if(!OwnedMatches(g,e))throw new IOException("检测到外部修改，已停止并保留现状："+e.Path);
            if(e.Backup is not null&&(!File.Exists(InRoot(g,e.Backup))||FileHash(InRoot(g,e.Backup))!=e.BeforeHash))throw new IOException("备份缺失或发生改变，已停止："+e.Path);
        }
    }
    internal void Install(GameInfo g,ApiSettings settings,string key)
    {
        RequireStopped(g);
        if(!Cloud(g)){mgi.Install(g,settings,key);return;}
        if(GameDetector.Detect(g.ExePath).AdapterId!="cloud-meadow")throw new InvalidOperationException("游戏文件已变化或不符合当前适配条件，请重新选择。");
        var config=CloudConfiguration(settings,key);var j=HasRecord(g)?Read(g):new Journal{Exe=Path.GetFullPath(g.ExePath)};
        CheckJournal(g,j);
        using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Fusion.CloudMeadow.zip")??throw new InvalidDataException("缺少 Cloud Meadow 专用载荷。");
        using var zip=new ZipArchive(stream,ZipArchiveMode.Read);var files=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach(var expected in FusionCloudPayload.Files)
        {
            var entry=zip.GetEntry(expected.Key)??throw new InvalidDataException("载荷缺少文件："+expected.Key);
            using var data=entry.Open();using var memory=new MemoryStream();data.CopyTo(memory);var bytes=memory.ToArray();if(Hash(bytes)!=expected.Value)throw new InvalidDataException("载荷校验失败："+expected.Key);files.Add(expected.Key,bytes);
        }
        files.Add(FusionCloudPayload.Config,Encoding.UTF8.GetBytes(config));
        foreach(var file in files)
        {
            var dest=InRoot(g,file.Key);if(!j.Files.Any(e=>e.Path.Equals(file.Key,StringComparison.OrdinalIgnoreCase))&&File.Exists(dest)&&FileHash(dest)!=Hash(file.Value))throw new IOException("已有非本候选管理的文件与载荷冲突，未覆盖："+file.Key);
        }
        j.Complete=false;j.Version=BuildIdentity.BuildVersion;Save(g,j);
        foreach(var file in files)
        {
            RequireStopped(g);var dest=InRoot(g,file.Key);var e=j.Files.FirstOrDefault(x=>x.Path.Equals(file.Key,StringComparison.OrdinalIgnoreCase));
            bool privateFile=file.Key.StartsWith("BepInEx/plugins/CloudMeadowTranslator/",StringComparison.OrdinalIgnoreCase)||file.Key==FusionCloudPayload.Config;
            if(e is null&&File.Exists(dest)&&!privateFile)
            {
                if(FileHash(dest)!=Hash(file.Value))throw new IOException("安装期间共享加载器发生改变，已停止："+file.Key);
                continue; // Existing compatible shared loader remains unowned.
            }
            if(e is null)
            {
                e=new Entry{Path=file.Key};
                if(File.Exists(dest)){if(FileHash(dest)!=Hash(file.Value))throw new IOException("安装期间检测到新文件冲突，已停止。");e.Backup=".fusion-translator-backup/"+j.BackupId+"/"+file.Key;e.BeforeHash=FileHash(dest);var backup=InRoot(g,e.Backup);Directory.CreateDirectory(Path.GetDirectoryName(backup)!);File.Copy(dest,backup,false);}
                j.Files.Add(e);
            }
            else if(!OwnedMatches(g,e))throw new IOException("安装期间文件发生改变，已停止："+e.Path);
            e.AfterHash=Hash(file.Value);e.ConfigHash=file.Key==FusionCloudPayload.Config?ConfigurationHash(config):null;Save(g,j);
            AtomicWrite(dest,file.Value);
        }
        Verify(g);if(ConfigurationHash(File.ReadAllText(InRoot(g,FusionCloudPayload.Config)))!=ConfigurationHash(config))throw new IOException("配置写入校验失败。");
        j.Complete=true;Save(g,j);
    }
    internal void Verify(GameInfo g)
    {
        if(!Cloud(g)){mgi.Verify(g);return;}
        foreach(var file in FusionCloudPayload.Files){var p=InRoot(g,file.Key);if(!File.Exists(p)||FileHash(p)!=file.Value)throw new InvalidDataException("已安装文件校验不一致："+file.Key);}
    }
    internal void Restore(GameInfo g)
    {
        RequireStopped(g);
        // A known Cloud journal can still be restored if an external game update changes recognition.
        bool cloudLedger=HasRecord(g)&&File.ReadAllText(InRoot(g,Ledger)).Contains("\"cloud-meadow\"",StringComparison.Ordinal);
        if(!Cloud(g)&&!cloudLedger){mgi.Restore(g);return;}
        var j=Read(g);CheckJournal(g,j);
        var plugins=InRoot(g,"BepInEx/plugins");var own=InRoot(g,"BepInEx/plugins/CloudMeadowTranslator")+Path.DirectorySeparatorChar;
        bool others=Directory.Exists(plugins)&&Directory.EnumerateFiles(plugins,"*.dll",SearchOption.AllDirectories).Any(p=>!Path.GetFullPath(p).StartsWith(own,StringComparison.OrdinalIgnoreCase));
        var patchers=InRoot(g,"BepInEx/patchers");others|=Directory.Exists(patchers)&&Directory.EnumerateFiles(patchers,"*.dll",SearchOption.AllDirectories).Any();
        foreach(var e in j.Files.AsEnumerable().Reverse())
        {
            bool privateFile=e.Path.StartsWith("BepInEx/plugins/CloudMeadowTranslator/",StringComparison.OrdinalIgnoreCase)||e.Path==FusionCloudPayload.Config;
            if(others&&!privateFile){j.RetainedSharedFiles.Add(e.Path);continue;}
            RequireStopped(g);if(!OwnedMatches(g,e))throw new IOException("恢复期间文件改变，已停止："+e.Path);var dest=InRoot(g,e.Path);
            if(e.Backup is not null)AtomicWrite(dest,File.ReadAllBytes(InRoot(g,e.Backup)));else if(File.Exists(dest))File.Delete(dest);
        }
        Save(g,j);File.Move(InRoot(g,Ledger),InRoot(g,Ledger+".restored-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")));
        // Do not remove caches, saves, logs, other plugins, backup directories, or shared loader files still in use.
    }
    static string CloudConfiguration(ApiSettings s,string key)
    {
        // Use the existing effective prompt/language mapping; game config contains the actual key in plaintext.
        var text=BuildLegacyConfiguration(s,key).Replace("ConcurrentRequests = 3","ConcurrentRequests = 2").Replace("ScanIntervalSeconds = 0.6\n", "").Replace("BatchSize = 8\n", "");
        text=text.Replace("[API]",$"FirstByteTimeoutSeconds = {Math.Clamp(s.FirstByteTimeoutSeconds,5,Math.Clamp(s.RequestTimeoutSeconds,15,90))}\n\n[API]");
        text=text.Replace("[Translation]",$"AllowEmptyApiKey = {s.AllowEmptyApiKey.ToString().ToLowerInvariant()}\n\n[Translation]");
        return text+$"PreserveNumbers = {s.PreserveNumbers.ToString().ToLowerInvariant()}\nPreserveVariables = {s.PreserveVariables.ToString().ToLowerInvariant()}\nPreserveIdentifiers = {s.PreserveIdentifiers.ToString().ToLowerInvariant()}\n";
    }
    static string BuildLegacyConfiguration(ApiSettings s,string key)
    {
        if(!Uri.TryCreate(s.ApiUrl,UriKind.Absolute,out var u)||u.Scheme is not("https" or "http"))throw new ArgumentException("API 地址必须是 HTTP/HTTPS。");
        if(!Enumerable.Range(1,12).Select(i=>"F"+i).Contains(key))throw new ArgumentException("按键必须是 F1 至 F12。");
        foreach(var value in new[]{s.ApiUrl,s.ApiKey,s.Model})if(value.Contains('\r')||value.Contains('\n'))throw new ArgumentException("API 字段不能包含换行。");
        return $"[General]\nEnabled = true\nToggleKey = {key}\nScanIntervalSeconds = 0.6\nConcurrentRequests = 3\nBatchSize = 8\nRequestTimeoutSeconds = {Math.Clamp(s.RequestTimeoutSeconds,15,90)}\n\n[API]\nEndpoint = {s.ApiUrl.Trim()}\nApiKey = {s.ApiKey.Trim()}\nModel = {s.Model.Trim()}\n\n[Translation]\nTargetLanguage = {FusionConfiguration.NormalizeLanguage(s.TargetLanguage)}\nPromptBase64 = {Convert.ToBase64String(Encoding.UTF8.GetBytes(FusionConfiguration.PluginPrompt(s)))}\n";
    }
    internal static void WriteConfiguration(string path,ApiSettings settings,string key)=>LegacyMgiAdapterInstaller.WriteConfiguration(path,settings,key);
}
