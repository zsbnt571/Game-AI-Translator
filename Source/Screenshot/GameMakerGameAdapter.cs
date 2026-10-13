using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

internal sealed record GameMakerEngine(string Resource,string Version,bool Candidate);

internal static class GameMakerGameAdapter
{
    internal const string Patched=".fusion-gamemaker.win";
    private static readonly string[] Owned=[Patched,".fusion-gamemaker/bridge-font.otf",".fusion-gamemaker/catalog.json"];
    private sealed record Ledger(string Exe,string SourceHash,string ExeHash,string BridgeHash,Dictionary<string,string> Files,string? SaveName=null);
    internal static string ToolRoot=>Path.Combine(AppContext.BaseDirectory,"adapters","gamemaker");
    private static readonly Lazy<string> PayloadHash=new(()=>HashText(string.Concat(new[]{"GameMakerPatch.csx","GameMakerTranslationBridge.gml","bridge-font.otf","UndertaleModLib.dll","Underanalyzer.dll"}.Select(p=>Hash(Path.Combine(ToolRoot,p))))));
    internal static string Storage(string exe)=>Path.Combine(AppDataPaths.Root,"gamemaker-adapters",HashText(Path.GetFullPath(exe).ToUpperInvariant()));
    private static string Record(string exe)=>Path.Combine(Storage(exe),"install.json");
    internal static string Hash(string path){using var input=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(input));}
    private static string HashText(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static string InGame(string exe,string relative)
    {
        string root=Path.GetDirectoryName(Path.GetFullPath(exe))!,path=Path.GetFullPath(Path.Combine(root,relative));
        if(!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("组件路径不属于游戏目录。");
        for(string? p=path;p is not null;p=Path.GetDirectoryName(p))
            if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("游戏路径包含链接，未写入组件。");
        return path;
    }
    internal static GameMakerEngine? Detect(string exe)
    {
        try
        {
            string path=InGame(exe,"data.win");if(!File.Exists(path))return null;
            using var stream=File.OpenRead(path);using var reader=new BinaryReader(stream);
            if(stream.Length<80||stream.Length>2L*1024*1024*1024||reader.ReadUInt32()!=0x4D524F46||reader.ReadUInt32()+8L!=stream.Length)return null;
            bool gen=false,str=false,code=false,sequence=false,curves=false;byte bytecode=0;uint major=0,minor=0,release=0,build=0;int chunks=0;
            while(stream.Position<stream.Length&&++chunks<=128)
            {
                if(stream.Length-stream.Position<8)return null;
                uint tag=reader.ReadUInt32(),size=reader.ReadUInt32();long start=stream.Position,end=start+size;
                if(end>stream.Length)return null;
                if(tag==0x384E4547&&size>=64){gen=true;stream.Position=start+1;bytecode=reader.ReadByte();stream.Position=start+44;major=reader.ReadUInt32();minor=reader.ReadUInt32();release=reader.ReadUInt32();build=reader.ReadUInt32();}
                if(tag==0x47525453&&size>=4)str=true;
                if(tag==0x45444F43&&size>=4)code=reader.ReadUInt32()>0;
                if(tag==0x4E514553)sequence=true;
                if(tag==0x56524341)curves=true;
                stream.Position=end;
            }
            if(!gen||!str||stream.Position!=stream.Length)return null;
            // Modern packages commonly retain 2.0.0.0 in GEN8. The SEQN/ACRV
            // chunks establish the modern format; the full reader validates it at install.
            bool modern=major==2&&(minor>=3||sequence&&curves)||major is >=2022 and <=2026;
            return new(path,major==2&&minor<3&&modern?"2.3+（资源结构）":$"{major}.{minor}.{release}.{build}",code&&bytecode==17&&modern);
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException){return null;}
    }
    private static void Stopped(string exe)
    {
        if(FusionAdapterInstaller.ProcessState(new(exe,"GameMaker","GameMaker","","",SupportLevel.Candidate,null,"gamemaker"))!=FusionGameProcessState.Stopped)
            throw new IOException("请先退出游戏，再准备或恢复翻译。");
    }
    internal static bool HasRecord(string exe)=>File.Exists(Record(exe));
    private static Ledger ReadLedger(string exe)
    {
        if(new FileInfo(Record(exe)).Length>16384)throw new IOException("组件记录过大。");
        var record=JsonSerializer.Deserialize<Ledger>(File.ReadAllText(Record(exe)))??throw new IOException("组件记录无效。");
        if(!record.Exe.Equals(Path.GetFullPath(exe),StringComparison.OrdinalIgnoreCase)||record.Files.Count!=Owned.Length||Owned.Any(p=>!record.Files.ContainsKey(p)))throw new IOException("组件记录不属于所选游戏。");
        return record;
    }
    internal static string SaveName(string exe){
        string? name=ReadLedger(exe).SaveName;
        if(string.IsNullOrWhiteSpace(name)||name.Length>160||name is "." or ".."||name.IndexOfAny(Path.GetInvalidFileNameChars())>=0||name.EndsWith('.')||name.EndsWith(' '))throw new IOException("游戏存储目录信息无效，请重新应用翻译。");
        return name;
    }
    internal static bool IsInstalled(string exe)
    {
        try{var r=ReadLedger(exe);var engine=Detect(exe);return engine?.Candidate==true&&r.BridgeHash==PayloadHash.Value&&r.SourceHash==Hash(engine.Resource)&&r.ExeHash==Hash(exe)&&r.Files.All(p=>Hash(InGame(exe,p.Key))==p.Value);}
        catch{return false;}
    }
    internal static string InspectionStamp(string exe)=>exe+"|"+string.Join("|",new[]{Record(exe),InGame(exe,"data.win"),InGame(exe,Patched),InGame(exe,".fusion-gamemaker/bridge-font.otf")}.Select(p=>{var f=new FileInfo(p);return f.Exists?f.Length+":"+f.LastWriteTimeUtc.Ticks:"missing";}));
    internal static void Install(string exe)
    {
        Stopped(exe);var engine=Detect(exe)??throw new IOException("未识别 GameMaker 资源。");
        if(!engine.Candidate)throw new EmbeddedCompatibilityException("此 GameMaker 编译格式尚未接入；当前候选支持 2.3+ 的 VM 脚本格式。");
        string script=Path.Combine(ToolRoot,"GameMakerPatch.csx"),bridge=Path.Combine(ToolRoot,"GameMakerTranslationBridge.gml"),font=Path.Combine(ToolRoot,"bridge-font.otf");
        string bridgeHash=PayloadHash.Value;
        if(HasRecord(exe)){if(IsInstalled(exe)&&ReadLedger(exe).BridgeHash==bridgeHash)return;Restore(exe);}
        foreach(string p in Owned)if(File.Exists(InGame(exe,p)))throw new IOException("游戏已有同名组件文件，已保留，未覆盖。");
        string stage=Path.Combine(Storage(exe),"build-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
        string sourceHash=Hash(engine.Resource),exeHash=Hash(exe),output=Path.Combine(stage,"patched.win");
        var info=new ProcessStartInfo(Path.Combine(ToolRoot,"UndertaleModCli.exe")){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=ToolRoot,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in new[]{"load",engine.Resource,"-s",script,"-o",output})info.ArgumentList.Add(arg);
        info.Environment["FUSION_GM_BUILD"]=stage;info.Environment["FUSION_GM_BRIDGE"]=bridge;
        string marker=InGame(exe,GameTestCopyEnvironment.Marker);
        if(File.Exists(marker)){
            if(new FileInfo(marker).Length>128||File.ReadAllText(marker).Trim()!="FusionTranslationTestCopy/1")throw new IOException("测试副本标记无效。");
            info.Environment["FUSION_GM_TEST_NAME"]="FusionGameMakerTest_"+HashText(Path.GetFullPath(exe))[..16];
        }else info.Environment.Remove("FUSION_GM_TEST_NAME");
        using(var process=Process.Start(info)??throw new IOException("无法启动资源处理组件。")){
            var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
            if(!process.WaitForExit(120000)){process.Kill(true);throw new IOException("GameMaker 资源处理超时，原资源未更改。");}
            string log=stdout.GetAwaiter().GetResult()+stderr.GetAwaiter().GetResult();File.WriteAllText(Path.Combine(stage,"build.log"),log);
            if(process.ExitCode!=0||!File.Exists(output))throw new IOException("GameMaker 资源处理失败，原资源未更改。"+SafeDiagnosticOutput.Summary(log.Length>1000?log[^1000..]:log));
        }
        if(sourceHash!=Hash(engine.Resource)||exeHash!=Hash(exe))throw new IOException("游戏在处理期间更新，已停止安装。");
        var inputs=new[]{output,font,Path.Combine(stage,"catalog.json")};
        using var patch=JsonDocument.Parse(File.ReadAllText(Path.Combine(stage,"patch.json")));
        string saveName=patch.RootElement.GetProperty("saveName").GetString()??"";
        var record=new Ledger(Path.GetFullPath(exe),sourceHash,exeHash,bridgeHash,Owned.Select((p,i)=>(p,i)).ToDictionary(x=>x.p,x=>Hash(inputs[x.i])),saveName);
        PortableDataStorage.WriteJson(Record(exe),record);
        try {for(int i=0;i<Owned.Length;i++){string dest=InGame(exe,Owned[i]);Directory.CreateDirectory(Path.GetDirectoryName(dest)!);File.Copy(inputs[i],dest,false);}}
        catch{try{Restore(exe);}catch{}throw;}
        // Discard only this build's generated resource after the verified game copy is in place.
        if(Hash(output)==record.Files[Patched])File.Delete(output);
    }
    internal static void Restore(string exe)
    {
        if(!HasRecord(exe))return;Stopped(exe);var r=ReadLedger(exe);
        foreach(string p in Owned){string f=InGame(exe,p);if(File.Exists(f)&&Hash(f)!=r.Files[p])throw new IOException("翻译组件被其他程序改变，已保留文件。");}
        foreach(string p in Owned){string f=InGame(exe,p);if(File.Exists(f))File.Delete(f);}
        File.Move(Record(exe),Path.Combine(Storage(exe),"restored-"+Guid.NewGuid().ToString("N")+".json"));
    }
    internal static RpgTextCatalogResult ReadCatalog(string exe,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();string path=InGame(exe,".fusion-gamemaker/catalog.json");
        if(new FileInfo(path).Length>32*1024*1024)throw new IOException("文本目录过大。");
        using var doc=JsonDocument.Parse(File.ReadAllText(path));
        var texts=doc.RootElement.GetProperty("texts").EnumerateArray().Select(x=>x.GetString()??"").Where(RpgTextCatalog.IsText).Take(100000).Distinct(StringComparer.Ordinal).ToArray();
        return new(texts,1,[]){ReadCompleted=false,Report=new(false,[new(CatalogIssueKind.Incomplete,"GameMaker","已读取脚本文字常量；动态拼接和自定义绘制仍需运行时补译。")])};
    }
}

internal sealed class GameMakerTranslationConnection : IEmbeddedTranslationConnection
{
    private readonly string exe,folder,io,sessionId=Guid.NewGuid().ToString("N"),secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly SemaphoreSlim gate=new(1,1);private readonly CancellationTokenSource lifetime=new();
    private Process? peer;private int id;private bool ready,disposed;
    internal GameMakerTranslationConnection(string exe){
        this.exe=Path.GetFullPath(exe);folder=GameMakerGameAdapter.InGame(exe,".fusion-gamemaker/sessions/"+sessionId);Directory.CreateDirectory(folder);
        var env=new ProcessStartInfo(exe);GameTestCopyEnvironment.Apply(env);
        string local=env.Environment["LOCALAPPDATA"]??Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        io=Path.Combine(local,GameMakerGameAdapter.SaveName(exe),"fusion-translation",sessionId);
        for(string? p=io;p is not null;p=Path.GetDirectoryName(p))
            if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("游戏通信目录包含链接，未启动。");
        Directory.CreateDirectory(io);
    }
    // GML serializes whole numbers as 130.0; expose the integral protocol fields
    // in the same form as the other engine transports, without rounding fractions.
    private static JsonElement NormalizeNumbers(JsonElement value){
        JsonNode? ConvertNode(JsonElement v)=>v.ValueKind switch{
            JsonValueKind.Object=>new JsonObject(v.EnumerateObject().Select(p=>new KeyValuePair<string,JsonNode?>(p.Name,ConvertNode(p.Value)))),
            JsonValueKind.Array=>new JsonArray(v.EnumerateArray().Select(ConvertNode).ToArray()),
            JsonValueKind.Number when v.TryGetDecimal(out var n)&&n==decimal.Truncate(n)&&n>=long.MinValue&&n<=long.MaxValue=>JsonValue.Create((long)n),
            _=>JsonNode.Parse(v.GetRawText())};
        return JsonSerializer.SerializeToElement(ConvertNode(value));
    }
    private void RemoveStartup(){try{File.Delete(Path.Combine(folder,"startup.json"));}catch(IOException){}catch(UnauthorizedAccessException){}}
    public bool Connected=>ready&&!disposed&&peer is not null&&!peer.HasExited;
    public void PrimeStartup(IReadOnlyDictionary<string,string> entries){
        var accepted=new Dictionary<string,string>(StringComparer.Ordinal);long bytes=1024;
        foreach(var p in entries){if(p.Key.Length is <1 or >6000||p.Value.Length is <1 or >12000||p.Key.Contains('\0')||p.Value.Contains('\0'))continue;long cost=Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(p))+64;if(bytes+cost>30L*1024*1024||accepted.Count>=100000)break;accepted[p.Key]=p.Value;bytes+=cost;}
        File.WriteAllText(Path.Combine(folder,"startup.json"),JsonSerializer.Serialize(new{secret,entries=accepted}),new UTF8Encoding(false));
    }
    public ProcessStartInfo StartInfo(bool translationAtStartup=true){
        if(!GameMakerGameAdapter.IsInstalled(exe))throw new IOException("翻译资源不完整或游戏已更新，请重新应用翻译。");
        var info=new ProcessStartInfo(exe){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(exe)};
        info.ArgumentList.Add("-game");info.ArgumentList.Add(GameMakerGameAdapter.InGame(exe,GameMakerGameAdapter.Patched));
        GameTestCopyEnvironment.Apply(info);
        info.Environment["FUSION_GAMEMAKER_SESSION"]=folder.Replace('\\','/');info.Environment["FUSION_GAMEMAKER_SESSION_ID"]=sessionId;info.Environment["FUSION_GAMEMAKER_SECRET"]=secret;info.Environment["FUSION_GAMEMAKER_TRANSLATION"]=translationAtStartup?"1":"0";
        return info;
    }
    public void Launched(int pid){peer=Process.GetProcessById(pid);}
    public async Task AcceptAsync(CancellationToken cancellation=default){
        using var ct=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);ct.CancelAfter(60000);
        while(true){ct.Token.ThrowIfCancellationRequested();if(peer?.HasExited!=false)throw new IOException("游戏已退出。");
            if(await GodotFileTransport.ReadJsonAsync(Path.Combine(io,"hello.json"),ct.Token) is {} raw){var h=NormalizeNumbers(raw);if(h.GetProperty("secret").GetString()!=secret||h.GetProperty("protocol").GetInt32()!=130)throw new IOException("GameMaker 翻译连接验证失败。");peer.Refresh();if(!string.Equals(peer.MainModule?.FileName,exe,StringComparison.OrdinalIgnoreCase))throw new IOException("游戏进程路径不匹配。");RemoveStartup();ready=true;return;}
            await Task.Delay(50,ct.Token);
        }
    }
    public async Task<JsonElement> RequestAsync(JsonObject request,CancellationToken cancellation=default){
        using var ct=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);ct.CancelAfter(15000);await gate.WaitAsync(ct.Token);
        try{if(!Connected)throw new IOException("游戏连接已关闭。");int current=++id;request["id"]=current;request["secret"]=secret;string text=request.ToJsonString();if(Encoding.UTF8.GetByteCount(text)>262144)throw new IOException("翻译请求超过限制。");
            await GodotFileTransport.PublishAsync(Path.Combine(io,"request.json"),text,ct.Token);
            string response=Path.Combine(io,"response-"+current+".json");
            while(true){ct.Token.ThrowIfCancellationRequested();if(!Connected)throw new IOException("游戏连接已关闭。");if(await GodotFileTransport.ReadJsonAsync(response,ct.Token) is {} raw){var r=NormalizeNumbers(raw);if(r.GetProperty("id").GetInt32()==current){if(r.GetProperty("secret").GetString()!=secret||!r.GetProperty("ok").GetBoolean())throw new IOException("游戏未接受翻译请求。");try{File.Delete(response);}catch(IOException){}return r.GetProperty("result").Clone();}}await Task.Delay(20,ct.Token);}
        }finally{gate.Release();}
    }
    public void Dispose(){if(disposed)return;disposed=true;try{File.WriteAllText(Path.Combine(io,"stop"),secret);}catch(IOException){}catch(UnauthorizedAccessException){}RemoveStartup();lifetime.Cancel();peer?.Dispose();}
}
