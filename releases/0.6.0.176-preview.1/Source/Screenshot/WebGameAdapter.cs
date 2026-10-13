using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace ScreenshotTranslationUiTester;

internal sealed record WebGame(string Archive,string Host,string Engine,string Prefix,string Main,string? Preload,bool Candidate,string Detail);
internal static class WebGameAdapter
{
    internal static string Hash(string path){using var s=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(s));}
    internal static string Storage(string exe)=>Path.Combine(AppDataPaths.Root,"web-adapters",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(exe).ToUpperInvariant()))));
    internal static string InGame(string exe,string relative)=>GameMakerGameAdapter.InGame(exe,relative);
    internal static string Resource(string name){using var s=Assembly.GetExecutingAssembly().GetManifestResourceStream("Fusion."+name)??throw new IOException("缺少网页游戏翻译组件。");using var r=new StreamReader(s);return r.ReadToEnd();}
    private static readonly string[] Scripts=["WebGamePreload.js","WebGameMain.js","WebGameTransport.js","WebGameTranslation.js","RpgMakerTranslationBridge.js","NovelWebTranslation.js","NovelGameData.js","NovelStateBridge.js"];
    private static string Renderer(WebGame game)=>(game.Engine.StartsWith("RPG Maker",StringComparison.Ordinal)?Resource("RpgMakerTranslationBridge.js")+"\n":NovelWebCatalog.IsNovel(game.Engine)?"window.__fusionNovelEngine="+JsonSerializer.Serialize(NovelWebCatalog.Id(game.Engine))+";\n"+Resource("NovelWebTranslation.js")+"\n"+Resource("NovelGameData.js")+"\n"+Resource("NovelStateBridge.js")+"\n":"")+Resource("WebGameTranslation.js");
    internal static string Startup(WebGame game)=>Resource("WebGameTransport.js")+"\n(function(){var t=createFusionWebTransport();if(t){window.__fusionWebTransport=t;}})();\n"+Renderer(game);
    internal static string PayloadHash()=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(Scripts.Select(Resource)))));
    private static bool IntegrityEnabled(string exe)
    {
        // Respect Electron's integrity fuse. Do not disable it or alter the executable.
        byte[] marker=Encoding.ASCII.GetBytes("dL7pKGdnNz796PbbjQWNKmHXBZaB9tsX");
        using var f=File.OpenRead(exe);byte[] b=new byte[1024*1024+256];int carry=0,n;
        while((n=f.Read(b,carry,b.Length-carry))>0){int count=carry+n;int at=b.AsSpan(0,count).IndexOf(marker);if(at>=0&&at+marker.Length+7<=count)return b[at+marker.Length]==1&&b[at+marker.Length+1]>=5&&b[at+marker.Length+6]==(byte)'1';carry=Math.Min(256,count);Buffer.BlockCopy(b,count-carry,b,0,carry);}return false;
    }
    internal static WebGame? Detect(string exe)
    {
        try
        {
            string root=Path.GetDirectoryName(exe)!,archive=InGame(exe,"resources/app.asar"),host="Electron";
            if(!File.Exists(archive)&&File.Exists(InGame(exe,"resources/app/package.json")))archive=InGame(exe,"resources/app");
            if(!File.Exists(archive)&&!Directory.Exists(archive)){
                archive=InGame(exe,"package.nw");host="NW.js";
                if(!File.Exists(InGame(exe,"nw.dll")))return null;
                if(!File.Exists(archive)&&!Directory.Exists(archive)){if(!File.Exists(InGame(exe,"package.json")))return null;archive=root;}
            }
            using var a=new WebGameArchive(archive);if(!a.Contains("package.json"))return null;
            using var json=JsonDocument.Parse(a.Read("package.json",131072));
            if(!json.RootElement.TryGetProperty("main",out var prop)||prop.ValueKind!=JsonValueKind.String)return null;
            string main=WebGameArchive.SafeName(prop.GetString()!);if(!a.Contains(main))return null;
            string entryPrefix=main[..(main.LastIndexOf('/')+1)];
            if(NovelWebCatalog.Detect(a,entryPrefix) is {} novel)
            {
                bool candidate=host=="NW.js"&&main.EndsWith(".html",StringComparison.OrdinalIgnoreCase)||host=="Electron"&&Directory.Exists(archive)&&LooseElectronGameAdapter.Candidate(exe,a,main);
                return new(archive,host,novel,entryPrefix,main,null,candidate,candidate?"剧情文字预译与内嵌显示测试接入":"已识别文字引擎；此启动结构尚未接入");
            }
            foreach(string core in a.Names.Where(n=>n.EndsWith("js/rmmz_core.js",StringComparison.Ordinal)||n.EndsWith("js/rpg_core.js",StringComparison.Ordinal)).Take(16))
            {
                string prefix=core[..core.LastIndexOf("js/",StringComparison.Ordinal)];
                if(!a.Contains(prefix+"data/System.json")||!a.Contains(prefix+"index.html"))continue;
                string engine=core.EndsWith("rmmz_core.js",StringComparison.Ordinal)?"RPG Maker MZ":"RPG Maker MV";
                string? preload=null;bool capable=false;
                if(host=="NW.js")capable=main==prefix+"index.html";
                else if(main.EndsWith(".js",StringComparison.Ordinal))
                {
                    string code=a.Text(main,1024*1024);
                    var m=Regex.Match(code,"preload\\s*:\\s*(?:\\w+\\.)?(?:join|resolve)\\(\\s*(app\\.getAppPath\\(\\)|__dirname)\\s*,\\s*[\"']([^\"']+)[\"']\\s*\\)");
                    if(m.Success){preload=WebGameArchive.SafeName((m.Groups[1].Value=="__dirname"?main[..(main.LastIndexOf('/')+1)]:"")+m.Groups[2].Value);capable=a.Contains(preload)&&a.Text(preload,1024*1024).Contains("contextBridge",StringComparison.Ordinal)&&!Regex.IsMatch(code,@"sandbox\s*:\s*true");}
                    if(capable&&IntegrityEnabled(exe))capable=false;
                }
                return new(archive,host,engine,prefix,main,preload,capable,capable?"资源包剧情预译与内嵌显示测试接入":"已识别资源包；此启动或安全配置尚未接入");
            }
            if(host=="NW.js"&&a.Contains("scripts/c3runtime.js")&&a.Contains("scripts/main.js")&&a.Contains("data.json"))
            {
                string code=a.Text("scripts/main.js",4*1024*1024);
                bool candidate=!Directory.Exists(archive)&&main=="index.html"&&Regex.IsMatch(code,@"useWorker\s*:\s*(?:!1|false)")&&a.Text("scripts/c3runtime.js",16*1024*1024).Contains("Plugins.Text.Instance",StringComparison.Ordinal);
                return new(archive,host,"Construct 3","",main,null,candidate,candidate?"界面文字与剧情资源预译测试接入":"已识别 Construct 3；此线程或渲染结构尚未接入");
            }
            return new(archive,host,host,"",main,null,false,"已识别网页资源包；内部文本引擎尚未接入");
        }
        catch(Exception e)when(e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException){return null;}
    }
    internal static GameInfo? Game(string exe,string architecture)
    {
        var w=Detect(exe);return w is null?null:new(exe,Path.GetFileNameWithoutExtension(exe),w.Engine+" / "+w.Host,architecture,w.Detail,w.Candidate?SupportLevel.Candidate:SupportLevel.Planned,Directory.Exists(w.Archive)?w.Archive:Path.GetDirectoryName(w.Archive),w.Candidate?(w.Engine.StartsWith("RPG Maker",StringComparison.Ordinal)?"web-rpg":NovelWebCatalog.IsNovel(w.Engine)?"web-"+NovelWebCatalog.Id(w.Engine):"web-construct"):null);
    }
    private sealed record Ledger(string Exe,string Archive,string Before,string After,string Payload,Dictionary<string,string>? Files=null);
    private static string Record(string exe)=>Path.Combine(Storage(exe),"install.json");
    internal static bool HasRecord(string exe)=>File.Exists(Record(exe))||LooseElectronGameAdapter.HasRecord(exe);
    private static Ledger Read(string exe)
    {
        if(new FileInfo(Record(exe)).Length>16384)throw new IOException("安装记录无效。");
        var r=JsonSerializer.Deserialize<Ledger>(File.ReadAllText(Record(exe)))??throw new IOException("安装记录无效。");
        if(r.Exe!=Path.GetFullPath(exe)||r.Archive is not ("package.nw" or "resources/app.asar" or "package.json" or "package.nw/package.json"))throw new IOException("安装记录不属于此游戏。");
        if(r.Archive.EndsWith("/package.json",StringComparison.Ordinal)||r.Archive=="package.json"){
            string script=r.Archive[..^"package.json".Length]+".fusion-web-start.js";
            if(r.Files is null||r.Files.Count!=1||!r.Files.ContainsKey(script))throw new IOException("启动组件记录无效。");
        }else if(r.Files is not null)throw new IOException("资源包记录无效。");
        return r;
    }
    internal static bool IsInstalled(string exe){if(LooseElectronGameAdapter.HasRecord(exe))return LooseElectronGameAdapter.IsInstalled(exe);try{var r=Read(exe);return r.Payload==PayloadHash()&&Hash(InGame(exe,r.Archive))==r.After&&(r.Files is null||r.Files.All(p=>Hash(InGame(exe,p.Key))==p.Value));}catch{return false;}}
    internal static string InspectionStamp(string exe)=>exe+"|"+LooseElectronGameAdapter.Stamp(exe)+"|"+string.Join("|",new[]{Record(exe),InGame(exe,"package.nw"),InGame(exe,"resources/app.asar"),InGame(exe,"package.json"),InGame(exe,"package.nw/package.json"),InGame(exe,".fusion-web-start.js"),InGame(exe,"package.nw/.fusion-web-start.js")}.Select(p=>{var f=new FileInfo(p);return f.Exists?f.Length+":"+f.LastWriteTimeUtc.Ticks:"missing";}));
    private static void Stopped(string exe){if(FusionAdapterInstaller.ProcessState(new(exe,"","网页游戏","","",SupportLevel.Candidate,null))!=FusionGameProcessState.Stopped)throw new IOException("请先退出游戏，再准备或恢复翻译。");}
    internal static void Install(string exe)
    {
        Stopped(exe);if(HasRecord(exe)){if(IsInstalled(exe))return;Restore(exe);}
        var w=Detect(exe);if(w?.Candidate!=true)throw new EmbeddedCompatibilityException("此资源包的启动结构尚未接入。");
        if(w.Host=="Electron"&&Directory.Exists(w.Archive)){LooseElectronGameAdapter.Install(exe,w);return;}
        string storage=Storage(exe);Directory.CreateDirectory(storage);using var gate=new FileStream(Path.Combine(storage,"install.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        if(Directory.Exists(w.Archive)){InstallLoose(exe,w,storage);return;}
        string before=Hash(w.Archive),backup=Path.Combine(storage,"original.bin"),stage=w.Archive+".fusion-"+Guid.NewGuid().ToString("N")+".tmp";
        if(File.Exists(backup)&&Hash(backup)!=before)throw new IOException("已有不同的游戏资源备份，未覆盖。");
        var changes=new Dictionary<string,byte[]>(StringComparer.Ordinal);
        void Add(string name,string text)=>changes.Add(name,Encoding.UTF8.GetBytes(text));
        try
        {
            using(var a=new WebGameArchive(w.Archive))
            {
                string script=w.Prefix+".fusion-web-renderer.js",pre=w.Preload is null?"":w.Preload[..(w.Preload.LastIndexOf('/')+1)]+".fusion-web-preload.js";
                if(a.Names.Any(n=>n.Split('/').Last().StartsWith(".fusion-web-",StringComparison.Ordinal)))throw new IOException("资源包已有同名组件，未覆盖。");
                string renderer=Renderer(w);
                Add(script,renderer);
                if(w.Host=="Electron")
                {
                    Add(pre,Resource("WebGameTransport.js")+"\n"+Resource("WebGamePreload.js"));
                    Add(w.Preload!,a.Text(w.Preload!,1024*1024)+"\nrequire('./.fusion-web-preload.js');\n");
                    string bootstrap=w.Main[..(w.Main.LastIndexOf('/')+1)]+".fusion-web-main.js";Add(bootstrap,Resource("WebGameMain.js"));
                    Add(w.Main,"require('./.fusion-web-main.js');\n"+a.Text(w.Main,1024*1024));
                    string html=w.Prefix+"index.html",content=a.Text(html,1024*1024);var match=Regex.Match(content,"<head(?:\\s[^>]*)?>",RegexOptions.IgnoreCase);if(!match.Success)throw new IOException("未找到可接入的页面入口。");
                    Add(html,content.Insert(match.Index+match.Length,"\n<script src=\".fusion-web-renderer.js\"></script>\n"));
                }
                else
                {
                    var manifest=JsonNode.Parse(a.Text("package.json",131072))!.AsObject();
                    if(manifest.ContainsKey("inject_js_start"))throw new IOException("游戏已有启动注入脚本，本次保留该脚本，未覆盖。");
                    Add(".fusion-web-start.js",Startup(w));
                    manifest["inject_js_start"]=".fusion-web-start.js";Add("package.json",manifest.ToJsonString());
                    // This runs at module completion, before Construct starts its runtime.
                    if(w.Engine=="Construct 3")Add("scripts/c3runtime.js",a.Text("scripts/c3runtime.js",16*1024*1024)+"\nif(self.__fusionWebInstall)self.__fusionWebInstall();\n");
                }
                if(!File.Exists(backup)){File.Copy(w.Archive,backup);if(Hash(backup)!=before)throw new IOException("资源备份校验失败。");}
                a.Patch(stage,changes);
            }
            string after=Hash(stage);if(Hash(w.Archive)!=before)throw new IOException("游戏资源正在变化，未替换。");
            PortableDataStorage.WriteJson(Record(exe),new Ledger(Path.GetFullPath(exe),Path.GetRelativePath(Path.GetDirectoryName(exe)!,w.Archive).Replace('\\','/'),before,after,PayloadHash()));
            File.Move(stage,w.Archive,true);
        }
        finally{if(File.Exists(stage))File.Delete(stage);}
    }
    internal static void Restore(string exe)
    {
        if(LooseElectronGameAdapter.HasRecord(exe)){LooseElectronGameAdapter.Restore(exe);return;}
        Stopped(exe);if(!HasRecord(exe))return;string storage=Storage(exe);using var gate=new FileStream(Path.Combine(storage,"install.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var r=Read(exe);string target=InGame(exe,r.Archive),backup=Path.Combine(storage,"original.bin"),now=Hash(target);
        if(Hash(backup)!=r.Before||(now!=r.After&&now!=r.Before))throw new IOException("组件或原始备份已被修改，未覆盖游戏资源。");
        if(r.Files is not null&&r.Files.Any(p=>!File.Exists(InGame(exe,p.Key))||Hash(InGame(exe,p.Key))!=p.Value))throw new IOException("启动组件已被修改，未覆盖。");
        if(now==r.After){string temp=target+".fusion-"+Guid.NewGuid().ToString("N")+".tmp";try{File.Copy(backup,temp);if(Hash(temp)!=r.Before||Hash(target)!=now)throw new IOException("恢复前校验失败。");File.Move(temp,target,true);}finally{if(File.Exists(temp))File.Delete(temp);}}
        if(r.Files is not null)foreach(var pair in r.Files)File.Delete(InGame(exe,pair.Key));
        File.Move(Record(exe),Path.Combine(storage,"restored-"+DateTime.UtcNow.Ticks+".json"));
    }
    private static void InstallLoose(string exe,WebGame game,string storage)
    {
        string relative=Path.GetRelativePath(Path.GetDirectoryName(exe)!,game.Archive).Replace('\\','/');if(relative==".")relative="";else relative+="/";
        string manifestName=relative+"package.json",scriptName=relative+".fusion-web-start.js";
        string manifestPath=InGame(exe,manifestName),scriptPath=InGame(exe,scriptName),backup=Path.Combine(storage,"original.bin"),before=Hash(manifestPath);
        var manifest=JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        if(manifest.ContainsKey("inject_js_start")||File.Exists(scriptPath))throw new IOException("游戏已有启动脚本，未覆盖。");
        if(File.Exists(backup)&&Hash(backup)!=before)throw new IOException("已有不同的启动配置备份，未覆盖。");
        manifest["inject_js_start"]=".fusion-web-start.js";
        string temp=manifestPath+".fusion-"+Guid.NewGuid().ToString("N")+".tmp";bool created=false,committed=false,recorded=false;string? scriptHash=null;
        try
        {
            if(!File.Exists(backup))File.Copy(manifestPath,backup);if(Hash(backup)!=before)throw new IOException("启动配置备份不一致。");
            using(var script=new FileStream(scriptPath,FileMode.CreateNew,FileAccess.Write,FileShare.None)){created=true;var bytes=Encoding.UTF8.GetBytes(Startup(game));script.Write(bytes);script.Flush(true);}
            File.WriteAllText(temp,manifest.ToJsonString(),new UTF8Encoding(false));
            scriptHash=Hash(scriptPath);var files=new Dictionary<string,string>{{scriptName,scriptHash}};
            if(Hash(manifestPath)!=before)throw new IOException("启动配置已变化，未替换。");Stopped(exe);
            PortableDataStorage.WriteJson(Record(exe),new Ledger(Path.GetFullPath(exe),manifestName,before,Hash(temp),PayloadHash(),files));recorded=true;
            File.Move(temp,manifestPath,true);committed=true;
        }
        finally
        {
            if(File.Exists(temp))File.Delete(temp);
            if(!committed){if(created&&File.Exists(scriptPath)&&scriptHash is not null&&Hash(scriptPath)==scriptHash)File.Delete(scriptPath);if(recorded&&File.Exists(Record(exe)))File.Delete(Record(exe));}
        }
    }
}
