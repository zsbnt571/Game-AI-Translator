using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

internal sealed record RpgMakerInstallation(string Root,string Manifest,string DataRoot,string Engine);
internal static class RpgMakerDataAdapter
{
    internal const string ScriptName=".fusion-rpg-data.js";
    internal static RpgMakerInstallation? Detect(string exe)
    {
        var root=Path.GetDirectoryName(Path.GetFullPath(exe))!;
        foreach(var folder in new[]{root,Path.Combine(root,"www")})
        {
            string? engine=File.Exists(Path.Combine(folder,"js","rmmz_core.js"))?"MZ":File.Exists(Path.Combine(folder,"js","rpg_core.js"))?"MV":null;
            if(engine is null||!File.Exists(Path.Combine(folder,"data","System.json")))continue;
            foreach(var manifest in new[]{Path.Combine(root,"package.json"),Path.Combine(folder,"package.json")}.Distinct())
            {
                if(!File.Exists(manifest)||new FileInfo(manifest).Length>131072)continue;
                var doc=JsonNode.Parse(File.ReadAllText(manifest)) as JsonObject;
                if(doc?["main"] is not JsonValue main||!main.TryGetValue<string>(out var value))continue;
                if(Uri.TryCreate(value,UriKind.Absolute,out _)||Path.IsPathRooted(value))continue;
                var entry=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifest)!,value));
                if(!entry.Equals(Path.Combine(folder,"index.html"),StringComparison.OrdinalIgnoreCase)||!File.Exists(entry))continue;
                return new(root,manifest,folder,engine);
            }
        }
        return null;
    }
    internal static string Storage(string exe)=>Path.Combine(AppDataPaths.Root,"game-data-adapters",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(exe).ToUpperInvariant()))).ToLowerInvariant());
    internal static Bitmap? ReadIconAtlas(string exe)
    {
        var install=Detect(exe);if(install is null)return null;
        string file=Path.Combine(install.DataRoot,"img","system","IconSet.png");
        if(!File.Exists(file))file=Path.Combine(install.DataRoot,"img","system",install.Engine=="MV"?"IconSet.rpgmvp":"IconSet.png_");
        if(!File.Exists(file)||new FileInfo(file).Length>8*1024*1024)return null;
        var bytes=File.ReadAllBytes(file);
        if(!file.EndsWith(".png",StringComparison.OrdinalIgnoreCase))
        {
            byte[] header=Convert.FromHexString("5250474D560000000003010000000000");
            if(bytes.Length<32||!bytes.AsSpan(0,16).SequenceEqual(header))return null;
            using var system=JsonDocument.Parse(File.ReadAllText(Path.Combine(install.DataRoot,"data","System.json")));
            if(!system.RootElement.TryGetProperty("encryptionKey",out var value)||value.GetString() is not {Length:32} key)return null;
            byte[] encryption=Convert.FromHexString(key);bytes=bytes[16..];for(int i=0;i<16;i++)bytes[i]^=encryption[i];
        }
        using var stream=new MemoryStream(bytes);using var image=Image.FromStream(stream);if(image.Width>4096||image.Height>16384)return null;return new Bitmap(image);
    }
    internal static string Payload()
    {
        using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Fusion.RpgMakerDataBridge.js")??throw new InvalidOperationException("缺少 RPG 修改组件。");
        using var translation=Assembly.GetExecutingAssembly().GetManifestResourceStream("Fusion.RpgMakerTranslationBridge.js")??throw new InvalidOperationException("缺少 RPG 翻译组件。");
        using var advanced=Assembly.GetExecutingAssembly().GetManifestResourceStream("Fusion.RpgMakerAdvancedBridge.js")??throw new InvalidOperationException("缺少 RPG 地图组件。");
        using var advancedReader=new StreamReader(advanced);using var translationReader=new StreamReader(translation);using var reader=new StreamReader(stream);return advancedReader.ReadToEnd()+"\n"+translationReader.ReadToEnd()+"\n"+reader.ReadToEnd();
    }
    internal static string Hash(byte[] data)=>Convert.ToHexString(SHA256.HashData(data));
    private sealed record Journal(string Exe,string Manifest,string Before,string After,string ScriptHash);
    private static string CheckedPath(string root,string path)
    {
        root=Path.GetFullPath(root);path=Path.GetFullPath(path);
        if(!path.StartsWith(root.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("组件路径不属于所选游戏。");
        for(var p=path;p is not null&&p.Length>=root.Length;p=Path.GetDirectoryName(p))
            if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("游戏组件路径包含链接，未改动文件。");
        return path;
    }
    internal static bool Installed(string exe)
    {
        var journal=Path.Combine(Storage(exe),"install.json");
        if(!File.Exists(journal))return false;
        var j=JsonSerializer.Deserialize<Journal>(File.ReadAllText(journal))??throw new InvalidDataException("组件安装记录无效。");
        var root=Path.GetDirectoryName(exe)!;CheckedPath(root,j.Manifest);var script=CheckedPath(root,Path.Combine(Path.GetDirectoryName(j.Manifest)!,ScriptName));
        return j.Exe.Equals(Path.GetFullPath(exe),StringComparison.OrdinalIgnoreCase)&&File.Exists(j.Manifest)&&File.Exists(script)&&Hash(File.ReadAllBytes(j.Manifest))==j.After&&Hash(File.ReadAllBytes(script))==j.ScriptHash;
    }
    internal static void Install(string exe)
    {
        RequireStopped(exe);
        // Upgrade only a verified installation, through the existing reversible restore path.
        if(Installed(exe))
        {
            var previous=JsonSerializer.Deserialize<Journal>(File.ReadAllText(Path.Combine(Storage(exe),"install.json")))!;
            if(previous.ScriptHash==Hash(Encoding.UTF8.GetBytes(Payload())))return;
            Restore(exe);
        }
        var install=Detect(exe)??throw new InvalidOperationException("此游戏暂不支持 RPG Maker MV/MZ 数据连接。");
        var manifest=CheckedPath(install.Root,install.Manifest);var script=CheckedPath(install.Root,Path.Combine(Path.GetDirectoryName(manifest)!,ScriptName));
        var storage=Storage(exe);Directory.CreateDirectory(storage);
        using var gate=new FileStream(Path.Combine(storage,"install.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var journalPath=Path.Combine(storage,"install.json");
        if(File.Exists(journalPath)){if(Installed(exe))return;throw new InvalidOperationException("安装后的文件发生变化，请先检查或恢复组件，未覆盖。");}
        if(File.Exists(script))throw new InvalidOperationException("游戏中已有同名组件，未覆盖。");
        var original=File.ReadAllBytes(manifest);var doc=JsonNode.Parse(original) as JsonObject??throw new InvalidDataException("游戏启动配置无效。");
        if(doc.ContainsKey("inject_js_end"))throw new InvalidOperationException("游戏已有启动脚本，当前适配不覆盖它。");
        doc["inject_js_end"]=ScriptName;
        var next=Encoding.UTF8.GetBytes(doc.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));var payload=Encoding.UTF8.GetBytes(Payload());
        var backup=Path.Combine(storage,"package.original.json");
        if(File.Exists(backup)&&!File.ReadAllBytes(backup).SequenceEqual(original))throw new IOException("已有不同的原始备份，未覆盖。");
        File.WriteAllBytes(backup,original);
        PortableDataStorage.WriteJson(journalPath,new Journal(Path.GetFullPath(exe),manifest,Hash(original),Hash(next),Hash(payload)));
        // Record recovery before touching the game; restore also accepts the
        // unchanged original manifest if interruption occurred before replace.
        using(var output=new FileStream(script,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(payload);output.Flush(true);}
        if(Hash(File.ReadAllBytes(manifest))!=Hash(original))throw new IOException("游戏配置在安装过程中改变，已停止。");
        var temporary=manifest+".fusion-"+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllBytes(temporary,next);File.Move(temporary,manifest,true);}finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    internal static void Restore(string exe)
    {
        RequireStopped(exe);
        var storage=Storage(exe);var path=Path.Combine(storage,"install.json");if(!File.Exists(path))return;
        using var gate=new FileStream(Path.Combine(storage,"install.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var j=JsonSerializer.Deserialize<Journal>(File.ReadAllText(path))??throw new InvalidDataException("组件安装记录无效。");
        if(!j.Exe.Equals(Path.GetFullPath(exe),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("安装记录不属于此游戏。");
        var root=Path.GetDirectoryName(exe)!;CheckedPath(root,j.Manifest);var script=CheckedPath(root,Path.Combine(Path.GetDirectoryName(j.Manifest)!,ScriptName));
        var original=File.ReadAllBytes(Path.Combine(storage,"package.original.json"));var now=Hash(File.ReadAllBytes(j.Manifest));
        if(Hash(original)!=j.Before||(now!=j.After&&now!=j.Before)||(File.Exists(script)&&Hash(File.ReadAllBytes(script))!=j.ScriptHash))throw new IOException("组件或原始备份已改变，未覆盖游戏文件。");
        if(now==j.After){var temp=j.Manifest+".fusion-"+Guid.NewGuid().ToString("N")+".tmp";try{File.WriteAllBytes(temp,original);File.Move(temp,j.Manifest,true);}finally{if(File.Exists(temp))File.Delete(temp);}}
        if(File.Exists(script))File.Delete(script);
        File.Move(path,Path.Combine(storage,"install.restored-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")+".json"));
    }
    private static void RequireStopped(string exe)
    {
        var game=new GameInfo(exe,"","RPG Maker","","",SupportLevel.Candidate,null);
        if(FusionAdapterInstaller.ProcessState(game)!=FusionGameProcessState.Stopped)throw new InvalidOperationException("请先退出此游戏，再安装或恢复修改组件。");
    }
}

internal interface IGameDataConnection : IDisposable
{
    bool Connected {get;}
    Task<JsonElement> RequestAsync(JsonObject request,CancellationToken cancellation=default);
}
internal sealed class RpgGameDataConnection : IEmbeddedTranslationConnection
{
    private readonly NamedPipeServerStream pipe;
    private readonly string exe,secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private readonly SemaphoreSlim requests=new(1,1);
    private readonly CancellationTokenSource lifetime=new();
    private StreamReader? reader;private StreamWriter? writer;private int id;private bool disposed;
    private readonly string? environmentPrefix;
    private string? startupFile;
    internal string PipeName {get;}="fusion-rpg-"+Guid.NewGuid().ToString("N");
    public bool Connected=>!disposed&&reader is not null&&pipe.IsConnected;
    internal RpgGameDataConnection(string executable,string? environmentPrefix=null)
    {
        if(environmentPrefix is not (null or "UNITY"))throw new ArgumentException("未知连接类型。",nameof(environmentPrefix));
        this.environmentPrefix=environmentPrefix;
        exe=Path.GetFullPath(executable);
        pipe=new NamedPipeServerStream(PipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
    }
    public void PrimeStartup(IReadOnlyDictionary<string,string> entries)
    {
        if(environmentPrefix!="UNITY")return;
        string folder=Path.Combine(AppDataPaths.Root,"embedded-sessions",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        startupFile=Path.Combine(folder,"startup.json");
        PortableDataStorage.WriteJson(startupFile,new{enabled=true,entries=entries.Select(p=>new{source=p.Key,text=p.Value}).ToArray()});
        if(new FileInfo(startupFile).Length>32*1024*1024)throw new IOException("启动译文缓存超过限制，未启动游戏。");
    }
    public ProcessStartInfo StartInfo(bool translationAtStartup=true)
    {
        var info=new ProcessStartInfo(exe){WorkingDirectory=Path.GetDirectoryName(exe),UseShellExecute=false};
        if(environmentPrefix=="UNITY")
        {
            info.Environment["FUSION_UNITY_PIPE"]=PipeName;info.Environment["FUSION_UNITY_SECRET"]=secret;
            info.Environment["FUSION_UNITY_TRANSLATION_START"]=translationAtStartup?"1":"0";
            if(startupFile is not null)info.Environment["FUSION_UNITY_STARTUP_FILE"]=startupFile;
            GameTestCopyEnvironment.Apply(info);return info;
        }
        if(RenpyGameAdapter.Detect(exe) is not null)
        {info.Environment["FUSION_RENPY_PIPE"]=PipeName;info.Environment["FUSION_RENPY_SECRET"]=secret;info.Environment["FUSION_RENPY_TRANSLATION_START"]=translationAtStartup?"1":"0";info.Environment["FUSION_RENPY_DATA_LOG"]=Path.Combine(RenpyGameAdapter.Storage(exe),"modifications");GameTestCopyEnvironment.Apply(info,true);return info;}
        info.Environment["FUSION_RPG_DATA_ROOT"]=(RpgMakerDataAdapter.Detect(exe)??throw new IOException("未识别 RPG 游戏数据目录。")).DataRoot;
        info.Environment["FUSION_RPG_PIPE"]=PipeName;info.Environment["FUSION_RPG_SECRET"]=secret;
        Directory.CreateDirectory(RpgMakerDataAdapter.Storage(exe));info.Environment["FUSION_RPG_LOG"]=Path.Combine(RpgMakerDataAdapter.Storage(exe),"bridge.log");return info;
    }
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe,out uint pid);
    public async Task AcceptAsync(CancellationToken cancellation=default)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);timeout.CancelAfter(TimeSpan.FromSeconds(environmentPrefix=="UNITY"?180:45));
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            if(!GetNamedPipeClientProcessId(pipe.SafePipeHandle,out var pid))throw new IOException("无法核对游戏连接。");
            using var process=Process.GetProcessById(checked((int)pid));
            if(!string.Equals(process.MainModule?.FileName,exe,StringComparison.OrdinalIgnoreCase)&&RenpyGameAdapter.Detect(exe)?.RuntimeExecutables.Contains(process.MainModule?.FileName??"",StringComparer.OrdinalIgnoreCase)!=true)throw new IOException("连接来自另一程序，已拒绝。");
            reader=new StreamReader(pipe,new UTF8Encoding(false,true),false,4096,true);writer=new StreamWriter(pipe,new UTF8Encoding(false),4096,true){AutoFlush=true};
            using var hello=JsonDocument.Parse(await ReadLineAsync(timeout.Token).ConfigureAwait(false));
            if(hello.RootElement.GetProperty("hello").GetInt32()!=1||hello.RootElement.GetProperty("secret").GetString()!=secret||hello.RootElement.GetProperty("pid").GetUInt32()!=pid)throw new IOException("游戏连接验证失败。");
        }
        catch{Dispose();throw;}
    }
    private readonly char[] _readBuffer=new char[8192];
    private int _readPosition,_readCount;
    private async Task<string> ReadLineAsync(CancellationToken cancellation)
    {
        var line=new StringBuilder();
        while(line.Length<=8*1024*1024)
        {
            if(_readPosition>=_readCount){_readCount=await reader!.ReadAsync(_readBuffer.AsMemory(),cancellation).ConfigureAwait(false);_readPosition=0;if(_readCount==0)throw new IOException("游戏连接已关闭。");}
            int end=Array.IndexOf(_readBuffer,'\n',_readPosition,_readCount-_readPosition);
            int count=(end<0?_readCount:end)-_readPosition;line.Append(_readBuffer,_readPosition,count);_readPosition+=count;
            if(end>=0){_readPosition++;if(line.Length>8*1024*1024)break;return line.ToString();}
        }
        throw new IOException("游戏返回的数据超过限制。");
    }
    public async Task<JsonElement> RequestAsync(JsonObject request,CancellationToken cancellation=default)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await requests.WaitAsync(timeout.Token).ConfigureAwait(false);
        bool requestSent=false;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            // Once sent, drain this response even when its caller cancels. The next
            // request (including Disable) must not consume a previous response.
            using var transport=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);transport.CancelAfter(TimeSpan.FromSeconds(8));
            if(!Connected)throw new IOException("此游戏尚未连接。");
            request=request.DeepClone().AsObject();request["id"]=++id;
            requestSent=true;
            await writer!.WriteLineAsync(request.ToJsonString().AsMemory(),transport.Token).ConfigureAwait(false);
            using var response=JsonDocument.Parse(await ReadLineAsync(transport.Token).ConfigureAwait(false));var obj=response.RootElement;
            if(obj.GetProperty("id").GetInt32()!=id)throw new IOException("游戏响应顺序不一致，已断开连接。");
            if(!obj.GetProperty("ok").GetBoolean())throw new GameDataOperationException(obj.GetProperty("error").GetString()??"游戏没有接受此操作。");
            return obj.GetProperty("result").Clone();
        }
        catch(GameDataOperationException){throw;}
        catch(OperationCanceledException)when(!requestSent&&cancellation.IsCancellationRequested&&!lifetime.IsCancellationRequested){throw;}
        catch{Dispose();throw;}
        finally{requests.Release();}
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;lifetime.Cancel();pipe.Dispose();
        try{reader?.Dispose();writer?.Dispose();}catch(Exception ex) when(ex is IOException or ObjectDisposedException){}
    }
}
internal sealed class GameDataOperationException(string message):Exception(message);

