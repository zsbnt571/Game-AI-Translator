using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;

namespace ScreenshotTranslationUiTester;

internal sealed class UnrealTranslationConnection : IEmbeddedTranslationConnection
{
    private readonly string runtime,folder,secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly CancellationTokenSource lifetime=new();
    private Process? peer;private int id;private bool accepted,disposed,retainStartup;
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder path,ref uint size);
    internal UnrealTranslationConnection(string exe)
    {
        runtime=(UnrealGameAdapter.Detect(exe)??throw new IOException("无法确认虚幻入口。")).Runtime;
        folder=Path.Combine(UnrealGameAdapter.Storage(exe),"sessions",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(UnrealGameAdapter.Storage(exe),"catalog.json"),Path.Combine(folder,"catalog.json"));
    }
    internal static bool ValidPair(string source,string text)=>source.Length is >=1 and <=6000&&text.Length is >=1 and <=12000
        &&!source.Contains('\0')&&!text.Contains('\0')
        &&EmbeddedTranslationText.Protect(source).Codes.Order(StringComparer.Ordinal).SequenceEqual(EmbeddedTranslationText.Protect(text).Codes.Order(StringComparer.Ordinal));
    public void PrimeStartup(IReadOnlyDictionary<string,string> entries)
    {
        var result=new Dictionary<string,string>();long bytes=1024;
        foreach(var p in entries)
        {
            if(!ValidPair(p.Key,p.Value))continue;
            long cost=Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(p.Key))+Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(p.Value))+256;
            if(bytes+cost>31L*1024*1024)continue;bytes+=cost;result[p.Key]=p.Value;if(result.Count>=100000)break;
        }
        PortableDataStorage.WriteJson(Path.Combine(folder,"startup.json"),new{secret,entries=result});
        retainStartup=true;
    }
    public ProcessStartInfo StartInfo(bool translationAtStartup=true)
    {
        var info=new ProcessStartInfo(runtime){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(runtime)};
        info.Environment["FUSION_UNREAL_SESSION"]=folder;info.Environment["FUSION_UNREAL_SECRET"]=secret;
        info.Environment["FUSION_UNREAL_TRANSLATION"]=translationAtStartup?"1":"0";
        GameTestCopyEnvironment.Apply(info);return info;
    }
    public void Launched(int pid)
    {
        peer=Process.GetProcessById(pid);
        var path=new StringBuilder(32768);uint size=(uint)path.Capacity;
        if(!QueryFullProcessImageName(peer.Handle,0,path,ref size)||!string.Equals(Path.GetFullPath(path.ToString()),runtime,StringComparison.OrdinalIgnoreCase))
        {peer.Dispose();peer=null;throw new IOException("无法验证刚启动的虚幻游戏进程。");}
    }
    public bool Connected=>accepted&&!disposed&&peer is not null&&!peer.HasExited;
    public async Task AcceptAsync(CancellationToken cancellation=default)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);timeout.CancelAfter(60000);
        try
        {
            while(true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if(peer is null||peer.HasExited)throw new IOException("虚幻游戏已退出，未建立翻译连接。");
                if(await GodotFileTransport.ReadJsonAsync(Path.Combine(folder,"hello.json"),timeout.Token) is {} hello)
                {
                    if(hello.GetProperty("protocol").GetInt32()!=76||hello.GetProperty("secret").GetString()!=secret)throw new IOException("虚幻翻译连接验证失败。");
                    if(hello.TryGetProperty("error",out var error))throw new IOException("虚幻运行时接入检查未通过："+SafeDiagnosticOutput.Summary(error.GetString()??""));
                    accepted=true;return;
                }
                await Task.Delay(50,timeout.Token);
            }
        }
        catch(OperationCanceledException)when(!cancellation.IsCancellationRequested&&!lifetime.IsCancellationRequested)
        {throw new IOException("虚幻翻译组件未能在 60 秒内连接；此构建的运行时接入未通过。可退出后选择“仅启动”恢复原游戏。");}
    }
    public async Task<JsonElement> RequestAsync(JsonObject request,CancellationToken cancellation=default)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);timeout.CancelAfter(20000);
        bool entered=false;
        try
        {
            await gate.WaitAsync(timeout.Token);entered=true;
            if(!Connected)throw new IOException("虚幻翻译连接已关闭。");
            if(request["op"]?.GetValue<string>()=="translationPrepare")
            {request["retainStartup"]=retainStartup;retainStartup=false;}
            if(request["op"]?.GetValue<string>()=="translationApply"&&request["entries"] is JsonArray rows)
            {
                var valid=new JsonArray();foreach(var r in rows)
                {string source=r?["source"]?.GetValue<string>()??"",text=r?["text"]?.GetValue<string>()??"";if(ValidPair(source,text))valid.Add(r!.DeepClone());}
                request["entries"]=valid;
            }
            int current=++id;request["id"]=current;request["secret"]=secret;string value=request.ToJsonString();
            if(Encoding.UTF8.GetByteCount(value)>262144)throw new IOException("虚幻翻译请求超过限制。");
            await GodotFileTransport.PublishAsync(Path.Combine(folder,"request.json"),value,timeout.Token);
            string responsePath=Path.Combine(folder,$"response-{current}.json");
            while(true)
            {
                timeout.Token.ThrowIfCancellationRequested();if(!Connected)throw new IOException("虚幻游戏已退出。");
                if(await GodotFileTransport.ReadJsonAsync(responsePath,timeout.Token) is {} response)
                {
                    if(response.GetProperty("id").GetInt32()!=current||response.GetProperty("secret").GetString()!=secret)throw new IOException("虚幻回包验证失败。");
                    File.Delete(responsePath);
                    if(!response.GetProperty("ok").GetBoolean())throw new IOException("虚幻文本更新失败："+SafeDiagnosticOutput.Summary(response.GetProperty("error").GetString()??""));
                    return response.GetProperty("result").Clone();
                }
                await Task.Delay(20,timeout.Token);
            }
        }
        catch(OperationCanceledException ex)when(!cancellation.IsCancellationRequested&&!lifetime.IsCancellationRequested)
        {
            AppLog.Write("unreal-bridge","request-timeout; completion-and-restoration-unconfirmed");
            throw new IOException("虚幻翻译组件未响应，本次操作未确认完成；如果正在关闭翻译，也不能确认原文和字体已经恢复。请保存进度并正常退出游戏后重试。",ex);
        }
        finally{if(entered)gate.Release();}
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;try{File.WriteAllText(Path.Combine(folder,"stop"),secret);}catch(IOException){}catch(UnauthorizedAccessException){}
        lifetime.Cancel();peer?.Dispose();
    }
}
