using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

internal sealed record GodotEngine(int Major,int Minor,int Patch,string PackPath,bool Embedded)
{
    internal string Version=>$"{Major}.{Minor}.{Patch}";
    internal string AdapterId=>"godot"+Major;
}

// The PCK header selects the script dialect; no game names or filenames select support.
internal static class GodotGameAdapter
{
    internal const string Script="fusion_translation.gd";
    private const uint Magic=0x43504447;
    internal static GodotEngine? Detect(string exe)
    {
        foreach(var embedded in new[]{false,true})
        {
            string path=embedded?exe:Path.ChangeExtension(exe,".pck");
            try
            {
                if(!File.Exists(path))continue;
                using var f=File.OpenRead(path);using var r=new BinaryReader(f);
                long offset=0;
                if(embedded)
                {
                    if(f.Length<12)continue;f.Position=f.Length-12;ulong size=r.ReadUInt64();
                    if(r.ReadUInt32()!=Magic||size>(ulong)(f.Length-12))continue;
                    offset=f.Length-12-(long)size;
                }
                if(f.Length-offset<88)continue;f.Position=offset;
                if(r.ReadUInt32()!=Magic)continue;
                uint format=r.ReadUInt32();int major=r.ReadInt32(),minor=r.ReadInt32(),patch=r.ReadInt32();
                if((major==3&&minor>=5&&format==1||major==4&&format is 2 or 3 or 4&&f.Length-offset>=100)&&minor is >=0 and <=100&&patch is >=0 and <=100)
                    return new(major,minor,patch,Path.GetFullPath(path),embedded);
            }
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
        }
        return null;
    }
    internal static string Storage(string exe)=>Path.Combine(AppDataPaths.Root,"godot-adapters",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(exe).ToUpperInvariant()))));
    private sealed record Ledger(string Exe,string ScriptHash,string ConfigHash,string Version);
    private static string Record(string exe)=>Path.Combine(Storage(exe),"install.json");
    internal static bool HasRecord(string exe)=>File.Exists(Record(exe));
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string InGame(string exe,string file)
    {
        string path=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe))!,file);
        for(string? part=path;part is not null;part=Path.GetDirectoryName(part))
            if((File.Exists(part)||Directory.Exists(part))&&(File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException("游戏路径包含链接，未写入组件。");
        return path;
    }
    private static void Stopped(string exe)
    {
        var engine=Detect(exe);
        if(FusionAdapterInstaller.ProcessState(new(exe,"Godot","Godot","","",SupportLevel.Candidate,null,engine?.AdapterId))!=FusionGameProcessState.Stopped)
            throw new IOException("请先退出此游戏，再安装或恢复翻译。");
    }
    internal static bool IsInstalled(string exe)
    {
        try{var record=JsonSerializer.Deserialize<Ledger>(File.ReadAllText(Record(exe)));return record is not null&&record.Exe.Equals(Path.GetFullPath(exe),StringComparison.OrdinalIgnoreCase)&&Hash(InGame(exe,Script))==record.ScriptHash&&Hash(InGame(exe,"override.cfg"))==record.ConfigHash;}catch{return false;}
    }
    internal static void Install(string exe)
    {
        var engine=Detect(exe)??throw new IOException("未识别到可接入的 Godot 3.5+ / 4.x 游戏包。");Stopped(exe);
        if(HasRecord(exe))Restore(exe);
        string script=InGame(exe,Script),cfg=InGame(exe,"override.cfg");
        if(File.Exists(script)||File.Exists(cfg))throw new IOException("游戏已有 override.cfg 或同名翻译组件，已保留原文件；此安装方式暂不与现有配置合并。");
        string resource=engine.Major==3?"Fusion.GodotTranslationBridge.gd":"Fusion.Godot4TranslationBridge.gd";
        using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)??throw new IOException("缺少 Godot 翻译组件。");using var reader=new StreamReader(stream);
        string payload=reader.ReadToEnd(),config="[autoload]\nFusionTranslation=\"*"+script.Replace('\\','/').Replace("\"","\\\"")+"\"\n";
        string marker=InGame(exe,GameTestCopyEnvironment.Marker);
        if(File.Exists(marker))
        {
            if(new FileInfo(marker).Length>128||File.ReadAllText(marker).Trim()!="FusionTranslationTestCopy/1")throw new IOException("测试副本标记无效。");
            // Test copies must never inherit an absolute custom user-data folder.
            config+="\n[application]\nconfig/use_custom_user_dir=false\nconfig/custom_user_dir_name=\"\"\n";
        }
        var record=new Ledger(Path.GetFullPath(exe),Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(config))),engine.Version);
        // Write ownership before files, so an interrupted partial install can be restored.
        PortableDataStorage.WriteJson(Record(exe),record);
        try
        {
            using(var output=new FileStream(script,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(Encoding.UTF8.GetBytes(payload));output.Flush(true);}
            using(var output=new FileStream(cfg,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(Encoding.UTF8.GetBytes(config));output.Flush(true);}
        }
        catch{try{Restore(exe);}catch{}throw;}
    }
    internal static void Restore(string exe)
    {
        if(!HasRecord(exe))return;Stopped(exe);
        var record=JsonSerializer.Deserialize<Ledger>(File.ReadAllText(Record(exe)))??throw new IOException("组件记录无效。");
        if(!record.Exe.Equals(Path.GetFullPath(exe),StringComparison.OrdinalIgnoreCase))throw new IOException("组件记录不属于所选游戏。");
        foreach(var pair in new[]{(Script,record.ScriptHash),("override.cfg",record.ConfigHash)})
            if(File.Exists(InGame(exe,pair.Item1))&&Hash(InGame(exe,pair.Item1))!=pair.Item2)throw new IOException("组件或配置被其他程序改变，未覆盖。");
        string archive=Path.Combine(Storage(exe),"restored-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(archive);
        foreach(string name in new[]{Script,"override.cfg"})if(File.Exists(InGame(exe,name)))File.Move(InGame(exe,name),Path.Combine(archive,name));
        File.Move(Record(exe),Path.Combine(archive,"install.json"));
    }
}

internal sealed class GodotTranslationConnection : IEmbeddedTranslationConnection
{
    private readonly string exe,folder,secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly SemaphoreSlim gate=new(1,1);private readonly CancellationTokenSource lifetime=new();
    private Process? peer;private int id;private bool disposed;
    internal GodotTranslationConnection(string exe)
    {this.exe=Path.GetFullPath(exe);folder=Path.Combine(GodotGameAdapter.Storage(exe),"sessions",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);}
    public bool Connected=>!disposed&&peer is not null&&!peer.HasExited;
    public void PrimeStartup(IReadOnlyDictionary<string,string> entries)
    {
        // The bridge reads at most 32 MiB. Budget escaped JSON rather than UTF-16
        // lengths, including a conservative allowance for the storage writer's indentation.
        var accepted=new Dictionary<string,string>(StringComparer.Ordinal);long bytes=1024;
        foreach(var pair in entries)
        {
            if(pair.Key.Length is <1 or >6000||pair.Value is null||pair.Value.Length>12000)continue;
            long cost=Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(pair.Key))+Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(pair.Value))+256;
            if(bytes+cost>31L*1024*1024)continue;
            accepted[pair.Key]=pair.Value;bytes+=cost;if(accepted.Count>=100000)break;
        }
        PortableDataStorage.WriteJson(Path.Combine(folder,"startup.json"),new{secret,entries=accepted});
    }
    public ProcessStartInfo StartInfo(bool translationAtStartup=true)
    {
        var info=new ProcessStartInfo(exe){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(exe)};
        info.Environment["FUSION_GODOT_SESSION"]=folder;info.Environment["FUSION_GODOT_SECRET"]=secret;
        info.Environment["FUSION_GODOT_TRANSLATION"]=translationAtStartup?"1":"0";
        GameTestCopyEnvironment.Apply(info);return info;
    }
    public async Task AcceptAsync(CancellationToken cancellation=default)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);timeout.CancelAfter(60000);
        while(true)
        {
            timeout.Token.ThrowIfCancellationRequested();var hello=await ReadAsync("hello.json",timeout.Token);
            if(hello is {} h)
            {
                if(h.GetProperty("secret").GetString()!=secret||h.GetProperty("protocol").GetInt32()!=73)throw new IOException("游戏连接验证失败或翻译组件版本不一致。");
                var process=Process.GetProcessById(h.GetProperty("pid").GetInt32());
                if(!string.Equals(process.MainModule?.FileName,exe,StringComparison.OrdinalIgnoreCase)){process.Dispose();throw new IOException("连接来自另一程序。");}
                peer=process;return;
            }
            await Task.Delay(50,timeout.Token);
        }
    }
    private Task<JsonElement?> ReadAsync(string name,CancellationToken cancellation)=>GodotFileTransport.ReadJsonAsync(Path.Combine(folder,name),cancellation);
    public async Task<JsonElement> RequestAsync(JsonObject request,CancellationToken cancellation=default)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);timeout.CancelAfter(15000);
        await gate.WaitAsync(timeout.Token);
        try
        {
            if(!Connected)throw new IOException("游戏连接已关闭。");int current=++id;request["id"]=current;request["secret"]=secret;
            string text=request.ToJsonString();if(Encoding.UTF8.GetByteCount(text)>262144)throw new IOException("翻译请求超过限制。");
            await GodotFileTransport.PublishAsync(Path.Combine(folder,"request.json"),text,timeout.Token);
            while(true)
            {
                timeout.Token.ThrowIfCancellationRequested();if(!Connected)throw new IOException("游戏连接已关闭。");
                if(await ReadAsync("response.json",timeout.Token) is {} response&&response.GetProperty("id").GetInt32()==current)
                {if(!response.GetProperty("ok").GetBoolean())throw new IOException("游戏未接受翻译请求。");return response.GetProperty("result").Clone();}
                await Task.Delay(20,timeout.Token);
            }
        }
        finally{gate.Release();}
    }
    public void Dispose(){if(disposed)return;disposed=true;try{File.WriteAllText(Path.Combine(folder,"stop"),secret);}catch{}lifetime.Cancel();peer?.Dispose();}
}

// Windows readers may allow writes but still deny rename/delete. File IPC must
// tolerate that short overlap without re-executing a game command or blocking UI.
internal static class GodotFileTransport
{
    private static readonly TimeSpan RetryBudget=TimeSpan.FromSeconds(2);
    internal static async Task PublishAsync(string path,string text,CancellationToken cancellation,TimeSpan? retryBudget=null)
    {
        // A unique temporary name also keeps a cancelled publication from being
        // mistaken for a later request. Only the final path is read by the game.
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        var clock=Stopwatch.StartNew();var budget=retryBudget??RetryBudget;
        try
        {
            await RetryAsync("写入请求",path,async()=>
            {
                await File.WriteAllTextAsync(temporary,text,new UTF8Encoding(false),cancellation);
                return true;
            },clock,budget,cancellation);
            await RetryAsync("发布请求",path,()=>
            {
                File.Move(temporary,path,true);
                return Task.FromResult(true);
            },clock,budget,cancellation);
        }
        finally
        {
            try{File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }

    internal static Task<JsonElement?> ReadJsonAsync(string path,CancellationToken cancellation,TimeSpan? retryBudget=null)=>
        RetryAsync<JsonElement?>("读取回包",path,async()=>
        {
            try
            {
                // Length belongs to this open snapshot. Do not query metadata
                // first: the producer may be publishing its next snapshot.
                using var input=OpenReadSnapshot(path);
                if(input.Length>8*1024*1024)throw new InvalidDataException("游戏回包超过 8 MiB 限制。");
                using var document=await JsonDocument.ParseAsync(input,cancellationToken:cancellation);
                return document.RootElement.Clone();
            }
            catch(FileNotFoundException){return null;}
            catch(DirectoryNotFoundException){return null;}
        },Stopwatch.StartNew(),retryBudget??RetryBudget,cancellation);

    internal static FileStream OpenReadSnapshot(string path)=>new(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,4096,FileOptions.Asynchronous|FileOptions.SequentialScan);

    private static bool Transient(Exception error)=>error is JsonException||
        error is UnauthorizedAccessException||error is IOException&&(error.HResult&0xffff) is 5 or 32 or 33;

    private static async Task<T> RetryAsync<T>(string stage,string path,Func<Task<T>> action,Stopwatch clock,TimeSpan budget,CancellationToken cancellation)
    {
        int attempt=0;
        while(true)
        {
            cancellation.ThrowIfCancellationRequested();
            try{return await action();}
            catch(OperationCanceledException){throw;}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException)
            {
                if(!Transient(error)||clock.Elapsed>=budget)
                    throw new IOException($"Godot {stage}失败（{Path.GetFileName(path)}，HResult 0x{error.HResult:X8}，{error.GetType().Name}）。",error);
                var remaining=budget-clock.Elapsed;
                int delay=(int)Math.Min(Math.Min(100,10*(1<<Math.Min(attempt++,4))),Math.Max(1,remaining.TotalMilliseconds));
                await Task.Delay(delay,cancellation);
            }
        }
    }
}
