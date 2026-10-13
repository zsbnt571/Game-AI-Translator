using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace ScreenshotTranslationUiTester;
internal sealed class YukarTranslationConnection : IEmbeddedTranslationConnection
{
    private readonly string exe,folder,secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly CancellationTokenSource lifetime=new();private readonly SemaphoreSlim gate=new(1,1);
    private Process? peer,supervisor;private bool ready,disposed;private int id;
    internal YukarTranslationConnection(string exe){this.exe=Path.GetFullPath(exe);folder=Path.Combine(YukarGameAdapter.Storage(exe),"sessions",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);}
    public bool Connected=>ready&&!disposed&&peer is not null&&!peer.HasExited;
    public void PrimeStartup(IReadOnlyDictionary<string,string> entries)
    {
        var accepted=new Dictionary<string,string>(StringComparer.Ordinal);long size=1024;
        foreach(var e in entries){if(!RpgTranslationText.ValidCachedPair(e.Key,e.Value,EmbeddedTextSyntax.Yukar)||e.Key.Length is <1 or >6000||e.Value.Length is <1 or >12000)continue;long bytes=Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(e))+64;if(size+bytes>30*1024*1024||accepted.Count>=100000)break;accepted.Add(e.Key,e.Value);size+=bytes;}
        PortableDataStorage.WriteJson(Path.Combine(folder,"startup.json"),new{secret,entries=accepted});
    }
    public ProcessStartInfo StartInfo(bool translationAtStartup=true)
    {
        if(!YukarGameAdapter.IsInstalled(exe))throw new IOException("Bakin 组件已变化，请重新应用。");
        var layout=YukarGameAdapter.Detect(exe)??throw new IOException("游戏运行环境已变化。");
        var info=new ProcessStartInfo(exe){UseShellExecute=false,WorkingDirectory=layout.Directory,CreateNoWindow=true};
        GameTestCopyEnvironment.Apply(info);
        info.Environment["FUSION_YUKAR_SESSION"]=folder;info.Environment["FUSION_YUKAR_SECRET"]=secret;info.Environment["FUSION_YUKAR_START"]=translationAtStartup?"1":"0";
        if(File.Exists(Path.Combine(Path.GetDirectoryName(exe)!,GameTestCopyEnvironment.Marker)))info.Environment["FUSION_YUKAR_TEST_MUTE"]="1";else info.Environment.Remove("FUSION_YUKAR_TEST_MUTE");
        info.FileName=YukarGameAdapter.Runner(exe);info.ArgumentList.Add(layout.Launcher is null?"--player":"--launcher");info.ArgumentList.Add(layout.Player);if(layout.Launcher is not null)info.ArgumentList.Add(layout.Launcher);return info;
    }
    public void Launched(int pid){peer=Process.GetProcessById(pid);}
    public async Task AcceptAsync(CancellationToken cancellation=default)
    {
        using var ct=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,cancellation);ct.CancelAfter(60000);
        while(true){ct.Token.ThrowIfCancellationRequested();if(peer?.HasExited!=false)throw new IOException("游戏已退出；启动诊断保存在组件会话目录。");
            if(await GodotFileTransport.ReadJsonAsync(Path.Combine(folder,"hello.json"),ct.Token) is {} h){
                if(h.GetProperty("secret").GetString()!=secret||h.GetProperty("protocol").GetInt32()!=143||h.GetProperty("engine").GetString()!="yukar"||!string.Equals(h.GetProperty("player").GetString(),YukarGameAdapter.Detect(exe)?.Player,StringComparison.OrdinalIgnoreCase))throw new IOException("Bakin 连接校验失败。");
                int playerPid=h.GetProperty("pid").GetInt32();
                if(playerPid!=peer.Id){
                    if(!h.TryGetProperty("supervisor",out var parent)||parent.GetString()!=peer.Id.ToString())throw new IOException("Bakin 启动进程校验失败。");
                    var child=Process.GetProcessById(playerPid);
                    if(!string.Equals(child.MainModule?.FileName,YukarGameAdapter.Runner(exe),StringComparison.OrdinalIgnoreCase)){child.Dispose();throw new IOException("Bakin 播放器校验失败。");}
                    supervisor=peer;peer=child;
                }
                if(!string.Equals(peer.MainModule?.FileName,YukarGameAdapter.Runner(exe),StringComparison.OrdinalIgnoreCase))throw new IOException("Bakin 运行路径校验失败。");
                try{File.Delete(Path.Combine(folder,"startup.json"));}catch(IOException){}ready=true;return;}
            await Task.Delay(50,ct.Token);}
    }
    public async Task<JsonElement> RequestAsync(JsonObject request,CancellationToken cancellation=default)
    {
        using var ct=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,cancellation);ct.CancelAfter(15000);await gate.WaitAsync(ct.Token);
        try{if(!Connected)throw new IOException("游戏连接已断开。");int current=++id;var q=(JsonObject)request.DeepClone();q["id"]=current;q["secret"]=secret;string raw=q.ToJsonString();if(Encoding.UTF8.GetByteCount(raw)>262144)throw new IOException("请求超过限制。");await GodotFileTransport.PublishAsync(Path.Combine(folder,"request.json"),raw,ct.Token);string response=Path.Combine(folder,"response-"+current+".json");
            while(true){ct.Token.ThrowIfCancellationRequested();if(!Connected)throw new IOException("游戏已经退出。");if(await GodotFileTransport.ReadJsonAsync(response,ct.Token) is {} r){if(r.GetProperty("id").GetInt32()!=current||r.GetProperty("secret").GetString()!=secret)throw new IOException("响应校验失败。");try{File.Delete(response);}catch(IOException){}if(!r.GetProperty("ok").GetBoolean()){string message=r.TryGetProperty("error",out var error)&&error.ValueKind==JsonValueKind.String?error.GetString()??"":"";throw new IOException(message.Length is >0 and <=256?message:"游戏未接受请求。");}return r.GetProperty("result").Clone();}await Task.Delay(20,ct.Token);}
        }finally{gate.Release();}
    }
    public void Dispose(){if(disposed)return;disposed=true;try{File.WriteAllText(Path.Combine(folder,"stop"),secret);File.Delete(Path.Combine(folder,"startup.json"));}catch(IOException){}catch(UnauthorizedAccessException){}lifetime.Cancel();peer?.Dispose();supervisor?.Dispose();}
}
