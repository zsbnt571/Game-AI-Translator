using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace ScreenshotTranslationUiTester;
internal sealed class WebTranslationConnection : IEmbeddedTranslationConnection
{
    private readonly string exe,folder,secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly CancellationTokenSource lifetime=new();private readonly SemaphoreSlim gate=new(1,1);
    private Process? peer;private bool ready,disposed;private int id;
    internal WebTranslationConnection(string exe){this.exe=Path.GetFullPath(exe);folder=Path.Combine(WebGameAdapter.Storage(exe),"sessions",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);}
    public bool Connected=>ready&&!disposed&&peer is not null&&!peer.HasExited;
    public void PrimeStartup(IReadOnlyDictionary<string,string> entries)
    {
        var accepted=new Dictionary<string,string>(StringComparer.Ordinal);long size=1024;
        foreach(var e in entries){if(e.Key.Length is <1 or >6000||e.Value.Length is <1 or >12000)continue;long bytes=Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(e))+64;if(size+bytes>30*1024*1024||accepted.Count>=150000)break;accepted.Add(e.Key,e.Value);size+=bytes;}
        PortableDataStorage.WriteJson(Path.Combine(folder,"startup.json"),new{secret,entries=accepted});
    }
    public ProcessStartInfo StartInfo(bool translationAtStartup=true)
    {
        if(!WebGameAdapter.IsInstalled(exe))throw new IOException("网页翻译组件或资源包已变化，请重新应用翻译。");
        var info=new ProcessStartInfo(exe){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(exe)};
        info.Environment["FUSION_WEB_SESSION"]=folder;info.Environment["FUSION_WEB_SECRET"]=secret;info.Environment["FUSION_WEB_START"]=translationAtStartup?"1":"0";GameTestCopyEnvironment.Apply(info);
        if(File.Exists(WebGameAdapter.InGame(exe,GameTestCopyEnvironment.Marker))){string profile=WebGameAdapter.InGame(exe,".fusion-test-data/web-profile");Directory.CreateDirectory(profile);info.ArgumentList.Add("--user-data-dir="+profile);info.ArgumentList.Add("--mute-audio");info.Environment["FUSION_WEB_TEST_DATA"]=profile;}
        else{info.Environment.Remove("FUSION_WEB_TEST_DATA");info.Environment.Remove("FUSION_WEB_TEST_HIDDEN");}return info;
    }
    public void Launched(int pid){peer=Process.GetProcessById(pid);}
    public async Task AcceptAsync(CancellationToken cancellation=default)
    {
        using var ct=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,cancellation);ct.CancelAfter(60000);
        while(true){ct.Token.ThrowIfCancellationRequested();if(peer?.HasExited!=false)throw new IOException("游戏已经退出。");if(await GodotFileTransport.ReadJsonAsync(Path.Combine(folder,"hello.json"),ct.Token) is {} h){if(h.GetProperty("secret").GetString()!=secret||h.GetProperty("protocol").GetInt32()!=131)throw new IOException("网页翻译连接校验失败。");if(!string.Equals(peer.MainModule?.FileName,exe,StringComparison.OrdinalIgnoreCase))throw new IOException("游戏进程不匹配。");try{File.Delete(Path.Combine(folder,"startup.json"));}catch(IOException){}ready=true;return;}await Task.Delay(50,ct.Token);}
    }
    public async Task<JsonElement> RequestAsync(JsonObject request,CancellationToken cancellation=default)
    {
        using var ct=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,cancellation);ct.CancelAfter(15000);await gate.WaitAsync(ct.Token);
        try{if(!Connected)throw new IOException("网页翻译连接已断开。");int current=++id;request["id"]=current;request["secret"]=secret;string raw=request.ToJsonString();if(Encoding.UTF8.GetByteCount(raw)>262144)throw new IOException("翻译请求超过限制。");await GodotFileTransport.PublishAsync(Path.Combine(folder,"request.json"),raw,ct.Token);string response=Path.Combine(folder,"response-"+current+".json");
            while(true){ct.Token.ThrowIfCancellationRequested();if(!Connected)throw new IOException("游戏已经退出。");if(await GodotFileTransport.ReadJsonAsync(response,ct.Token) is {} r){if(r.GetProperty("id").GetInt32()!=current||r.GetProperty("secret").GetString()!=secret)throw new IOException("游戏连接响应校验失败。");try{File.Delete(response);}catch(IOException){}if(!r.GetProperty("ok").GetBoolean()){string message=r.TryGetProperty("error",out var error)&&error.ValueKind==JsonValueKind.String?error.GetString()??"":"";throw new IOException(message.Length is >0 and <=256?message:"游戏未接受请求。");}return r.GetProperty("result").Clone();}await Task.Delay(20,ct.Token);}
        }finally{gate.Release();}
    }
    public void Dispose(){if(disposed)return;disposed=true;try{File.WriteAllText(Path.Combine(folder,"stop"),secret);File.Delete(Path.Combine(folder,"startup.json"));}catch(IOException){}catch(UnauthorizedAccessException){}lifetime.Cancel();peer?.Dispose();}
}
