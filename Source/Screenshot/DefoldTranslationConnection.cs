using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

internal sealed class DefoldTranslationConnection : IEmbeddedTranslationConnection
{
    private readonly string exe,folder,secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly SemaphoreSlim gate=new(1,1);private readonly CancellationTokenSource lifetime=new();
    private Process? peer;private int id;private bool ready,disposed;
    internal DefoldTranslationConnection(string exe){this.exe=Path.GetFullPath(exe);folder=NativeGameSessionFiles.Create(exe,"defold");}
    public bool Connected=>ready&&!disposed&&peer is not null&&!peer.HasExited;
    public void PrimeStartup(IReadOnlyDictionary<string,string> entries)
    {
        var accepted=new Dictionary<string,string>(StringComparer.Ordinal);long bytes=1024;
        foreach(var p in entries){if(p.Key.Length is <1 or >6000||p.Value.Length is <1 or >12000||p.Key.Contains('\0')||p.Value.Contains('\0')||!RpgTranslationText.ValidCachedPair(p.Key,p.Value,EmbeddedTextSyntax.Generic))continue;long cost=Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(p))+64;if(bytes+cost>30L*1024*1024||accepted.Count>=100000)break;accepted[p.Key]=p.Value;bytes+=cost;}
        File.WriteAllText(Path.Combine(folder,"startup.json"),JsonSerializer.Serialize(new{secret,entries=accepted}),new UTF8Encoding(false));
    }
    public ProcessStartInfo StartInfo(bool translationAtStartup=true)
    {
        if(!DefoldGameAdapter.IsInstalled(exe))throw new IOException("翻译资源不完整或游戏已更新，请重新应用翻译。");
        var info=new ProcessStartInfo(exe){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(exe)};GameLaunchEnvironment.ClearSession(info);GameTestCopyEnvironment.Apply(info);NativeGameSessionFiles.ApplySteamIdentity(info);
        info.ArgumentList.Add(DefoldGameAdapter.InGame(exe,DefoldGameAdapter.Folder+"/game.projectc"));
        info.Environment["FUSION_DEFOLD_SESSION"]=folder.Replace('\\','/');info.Environment["FUSION_DEFOLD_SECRET"]=secret;info.Environment["FUSION_DEFOLD_TRANSLATION"]=translationAtStartup?"1":"0";return info;
    }
    public void Launched(int pid){peer=Process.GetProcessById(pid);}
    public async Task AcceptAsync(CancellationToken cancellation=default)
    {
        using var ct=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);ct.CancelAfter(60000);
        while(true){ct.Token.ThrowIfCancellationRequested();if(peer?.HasExited!=false)throw new IOException("游戏已退出。");
            if(await GodotFileTransport.ReadJsonAsync(Path.Combine(folder,"hello.json"),ct.Token) is {} h){if(h.GetProperty("secret").GetString()!=secret||h.GetProperty("protocol").GetInt32()!=133||h.GetProperty("engine").GetString()!="defold")throw new IOException("Defold 翻译连接验证失败。");peer.Refresh();if(!string.Equals(peer.MainModule?.FileName,exe,StringComparison.OrdinalIgnoreCase))throw new IOException("游戏进程路径不匹配。");ready=true;return;}await Task.Delay(50,ct.Token);}
    }
    public async Task<JsonElement> RequestAsync(JsonObject request,CancellationToken cancellation=default)
    {
        using var ct=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);ct.CancelAfter(15000);await gate.WaitAsync(ct.Token);
        try{if(!Connected)throw new IOException("游戏连接已关闭。");int current=++id;var q=(JsonObject)request.DeepClone();q["id"]=current;q["secret"]=secret;string text=q.ToJsonString();if(Encoding.UTF8.GetByteCount(text)>262144)throw new IOException("翻译请求超过限制。");
            await GodotFileTransport.PublishAsync(Path.Combine(folder,"request.json"),text,ct.Token);string response=Path.Combine(folder,"response-"+current+".json");
            while(true){ct.Token.ThrowIfCancellationRequested();if(!Connected)throw new IOException("游戏连接已关闭。");if(await GodotFileTransport.ReadJsonAsync(response,ct.Token) is {} r){if(r.GetProperty("id").GetInt32()!=current||r.GetProperty("secret").GetString()!=secret||!r.GetProperty("ok").GetBoolean())throw new IOException("游戏未接受翻译请求。");File.Delete(response);return r.GetProperty("result").Clone();}await Task.Delay(20,ct.Token);}
        }finally{gate.Release();}
    }
    public void Dispose(){if(disposed)return;disposed=true;try{File.WriteAllText(Path.Combine(folder,"stop.json"),JsonSerializer.Serialize(new{secret}),new UTF8Encoding(false));}catch(IOException){}catch(UnauthorizedAccessException){}lifetime.Cancel();peer?.Dispose();}
}
