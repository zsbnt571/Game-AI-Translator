using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

internal sealed class EmbeddedTransportBusyException : IOException
{internal EmbeddedTransportBusyException(Exception inner):base("游戏连接暂时忙，正在等待恢复。",inner){}}

internal sealed class WolfTranslationConnection : IEmbeddedTranslationConnection
{
    private readonly string exe,folder,secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly SemaphoreSlim gate=new(1,1);private readonly CancellationTokenSource lifetime=new();
    private Process? peer,launcher;private int id;private bool ready,disposed;
    internal WolfTranslationConnection(string exe){this.exe=Path.GetFullPath(exe);folder=NativeGameSessionFiles.Create(exe,"wolf");}
    public bool Connected=>ready&&!disposed&&peer is not null&&!peer.HasExited;
    public void PrimeStartup(IReadOnlyDictionary<string,string> entries)
    {
        var accepted=new Dictionary<string,string>(StringComparer.Ordinal);long bytes=1024;
        foreach(var p in entries){if(p.Key.Length is <1 or >6000||p.Value.Length is <1 or >12000||p.Key.Contains('\0')||p.Value.Contains('\0')||!RpgTranslationText.ValidCachedPair(p.Key,p.Value,EmbeddedTextSyntax.Wolf))continue;long cost=Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(p))+64;if(bytes+cost>30L*1024*1024||accepted.Count>=100000)break;accepted[p.Key]=WolfDisplayWrapping.Fit(p.Key,p.Value);bytes+=cost;}
        File.WriteAllText(Path.Combine(folder,"startup.json"),JsonSerializer.Serialize(new{secret,entries=accepted}),new UTF8Encoding(false));
    }
    public ProcessStartInfo StartInfo(bool translationAtStartup=true)
    {
        if(!WolfGameAdapter.IsInstalled(exe))throw new IOException("翻译组件不完整或游戏已更新，请重新应用翻译。");
        string component=GameMakerGameAdapter.InGame(exe,WolfGameAdapter.Folder);
        var info=new ProcessStartInfo(exe){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(exe),CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
        GameTestCopyEnvironment.Apply(info);NativeGameSessionFiles.ApplySteamIdentity(info);
        info.FileName=Path.Combine(component,"FusionWolfLaunch.exe");info.ArgumentList.Add(exe);info.ArgumentList.Add(Path.Combine(component,"FusionWolf.dll"));
        // Optional, resource-verified whole-display source contract. Unknown
        // games continue through the existing renderer/cache path unchanged.
        WolfSentenceProfile.WriteSession(exe,folder,secret);
        info.Environment["FUSION_WOLF_SESSION"]=folder;info.Environment["FUSION_WOLF_SECRET"]=secret;
        info.Environment["FUSION_WOLF_TRANSLATION"]=translationAtStartup?"1":"0";info.Environment["FUSION_WOLF_OWNER_PID"]=Environment.ProcessId.ToString();return info;
    }
    public async Task<Process> LaunchAsync(bool translationAtStartup=true,CancellationToken cancellation=default)
    {
        using var ct=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);ct.CancelAfter(35000);
        launcher=Process.Start(StartInfo(translationAtStartup))??throw new IOException("未能准备游戏启动。");
        try
        {
            while(true)
            {
                ct.Token.ThrowIfCancellationRequested();
                if(await GodotFileTransport.ReadJsonAsync(Path.Combine(folder,"launch.json"),ct.Token) is {} reply)
                {
                    if(reply.GetProperty("secret").GetString()!=secret)throw new IOException("游戏启动记录验证失败。");
                    if(!reply.GetProperty("ok").GetBoolean())throw new IOException("WOLF 内嵌组件未能加载（阶段 "+reply.GetProperty("error").GetInt32()+"），原游戏文件未改动。");
                    var actual=Process.GetProcessById(reply.GetProperty("pid").GetInt32());
                    if(!string.Equals(actual.MainModule?.FileName,exe,StringComparison.OrdinalIgnoreCase)){actual.Dispose();throw new IOException("游戏进程路径不匹配。");}
                    return actual;
                }
                if(launcher.HasExited)throw new IOException("WOLF 启动组件已退出，未建立游戏连接。");
                await Task.Delay(25,ct.Token);
            }
        }
        catch{if(!launcher.HasExited)launcher.Kill();throw;}
    }
    public void Launched(int pid){peer=Process.GetProcessById(pid);}
    public async Task AcceptAsync(CancellationToken cancellation=default)
    {
        using var ct=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);ct.CancelAfter(15000);
        while(true)
        {
            ct.Token.ThrowIfCancellationRequested();if(peer?.HasExited!=false)throw new IOException("游戏已退出。");
            if(await GodotFileTransport.ReadJsonAsync(Path.Combine(folder,"hello.json"),ct.Token) is {} hello)
            {
                if(hello.GetProperty("secret").GetString()!=secret||hello.GetProperty("protocol").GetInt32()!=133||hello.GetProperty("engine").GetString()!="wolf"||hello.GetProperty("pid").GetInt32()!=peer.Id)throw new IOException("WOLF 翻译连接验证失败。");
                ready=true;return;
            }
            await Task.Delay(25,ct.Token);
        }
    }
    public async Task<JsonElement> RequestAsync(JsonObject request,CancellationToken cancellation=default)
    {
        using var ct=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);ct.CancelAfter(15000);bool entered=false;
        try
        {
            await gate.WaitAsync(ct.Token);entered=true;
            if(!Connected)throw new IOException("游戏连接已关闭。");int current=++id;var q=(JsonObject)request.DeepClone();q["id"]=current;q["secret"]=secret;
            string text=q.ToJsonString();if(Encoding.UTF8.GetByteCount(text)>262144)throw new IOException("翻译请求超过限制。");
            await GodotFileTransport.PublishAsync(Path.Combine(folder,"request.json"),text,ct.Token);string response=Path.Combine(folder,"response-"+current+".json");
            while(true)
            {
                ct.Token.ThrowIfCancellationRequested();if(!Connected)throw new IOException("游戏连接已关闭。");
                if(await GodotFileTransport.ReadJsonAsync(response,ct.Token) is {} r)
                {
                    if(r.GetProperty("id").GetInt32()!=current||r.GetProperty("secret").GetString()!=secret)throw new IOException("游戏响应验证失败。");
                    if(!r.GetProperty("ok").GetBoolean())
                    {
                        TryDeleteResponse(response);
                        string message=r.TryGetProperty("result",out var detail)&&detail.TryGetProperty("message",out var m)&&m.ValueKind==JsonValueKind.String?m.GetString()??"":"";
                        throw new IOException(message.Length is >0 and <=500?message:"游戏未接受此操作。");
                    }
                    TryDeleteResponse(response);return r.GetProperty("result").Clone();
                }
                await Task.Delay(20,ct.Token);
            }
        }
        catch(Exception ex)when(Connected&&request["op"]?.GetValue<string>().StartsWith("translation",StringComparison.Ordinal)==true&&Transient(ex))
        {throw new EmbeddedTransportBusyException(ex);}
        catch(OperationCanceledException ex)when(!cancellation.IsCancellationRequested&&!lifetime.IsCancellationRequested&&Connected&&request["op"]?.GetValue<string>().StartsWith("translation",StringComparison.Ordinal)==true)
        {throw new EmbeddedTransportBusyException(ex);}
        finally{if(entered)gate.Release();}
    }
    private static bool Transient(Exception e)=>(e is IOException or UnauthorizedAccessException or JsonException)&&
        (e is JsonException||(e.HResult&0xffff) is 5 or 32 or 33||e.InnerException is not null&&Transient(e.InnerException));
    private static void TryDeleteResponse(string response)
    {
        // Consumption succeeded. Cleanup is not part of the game operation;
        // an external reader holding the file must not stop translation.
        try{File.Delete(response);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        try{File.WriteAllText(Path.Combine(folder,"stop.json"),JsonSerializer.Serialize(new{secret}),new UTF8Encoding(false));}catch(IOException){}catch(UnauthorizedAccessException){}
        lifetime.Cancel();peer?.Dispose();launcher?.Dispose();
    }
}
