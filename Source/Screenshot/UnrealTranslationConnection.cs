using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;

namespace ScreenshotTranslationUiTester;

internal sealed class UnrealCulturePendingException : IOException
{
    internal UnrealCulturePendingException() : base("游戏正在切换语言；待原语言恢复后自动继续翻译，已有译文保留。") {}
}


internal sealed class UnrealTranslationConnection : IEmbeddedTranslationConnection
{
    private readonly string runtime,folder,secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly UnrealEngine engine;
    private readonly string executable;
    private bool deferredCatalog;
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly CancellationTokenSource lifetime=new();
    private readonly object stateGate=new();
    private Process? peer;private int id;private bool accepted,disposed,retainStartup;
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder path,ref uint size);
    internal UnrealTranslationConnection(string exe,bool deferCatalog=false)
    {
        executable=exe;deferredCatalog=deferCatalog;
        engine=UnrealGameAdapter.Detect(exe)??throw new IOException("无法确认虚幻入口。");runtime=engine.Runtime;
        folder=Path.Combine(UnrealGameAdapter.Storage(exe),"sessions",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        if(!deferCatalog)File.Copy(Path.Combine(UnrealGameAdapter.Storage(exe),"catalog.json"),Path.Combine(folder,"catalog.json"));
    }
    internal static bool ValidPair(string source,string text)=>!string.IsNullOrWhiteSpace(source)&&!string.IsNullOrWhiteSpace(text)&&source.Length is >=1 and <=6000&&text.Length is >=1 and <=12000
        &&!source.Contains('\0')&&!text.Contains('\0')
        &&EmbeddedTranslationText.Protect(source).Codes.Order(StringComparer.Ordinal).SequenceEqual(EmbeddedTranslationText.Protect(text).Codes.Order(StringComparer.Ordinal));
    public void PrimeStartup(IReadOnlyDictionary<string,string> entries)
        =>PrimeStartup(entries,null);
    internal void PrimeStartup(IReadOnlyDictionary<string,string> entries,IReadOnlyDictionary<string,string>? displayEntries)
    {
        lock(stateGate)ObjectDisposedException.ThrowIf(disposed,this);
        var result=new Dictionary<string,string>();long bytes=1024;
        foreach(var p in entries)
        {
            if(!ValidPair(p.Key,p.Value))continue;
            long cost=Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(p.Key))+Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(p.Value))+256;
            if(bytes+cost>31L*1024*1024)continue;bytes+=cost;result[p.Key]=p.Value;if(result.Count>=100000)break;
        }
        var display=new Dictionary<string,string>();
        if(displayEntries is not null)foreach(var p in displayEntries)
        {
            if(result.ContainsKey(p.Key)||!ValidPair(p.Key,p.Value))continue;
            long cost=Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(p.Key))+Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(p.Value))+256;
            if(bytes+cost>31L*1024*1024)continue;bytes+=cost;display[p.Key]=p.Value;if(display.Count>=100000)break;
        }
        // Separate, untrusted display cache. Only a live, independently checked
        // widget or native binding result may use it; it never registers identities.
        PortableDataStorage.WriteJson(Path.Combine(folder,"startup.json"),new{secret,entries=result,displayEntries=display});
        lock(stateGate){ObjectDisposedException.ThrowIf(disposed,this);retainStartup=true;}
    }
    public ProcessStartInfo StartInfo(bool translationAtStartup=true)
    {
        var info=new ProcessStartInfo(runtime){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(runtime)};
        GameLaunchEnvironment.ClearSession(info);
        foreach(string argument in engine.LaunchArguments)info.ArgumentList.Add(argument);
        info.Environment["FUSION_UNREAL_SESSION"]=folder;info.Environment["FUSION_UNREAL_SECRET"]=secret;
        info.Environment["FUSION_UNREAL_TRANSLATION"]=translationAtStartup?"1":"0";
        // Diagnostic version metadata only: Lua never chooses native offsets or
        // FText storage sizes. The pinned loader retains that responsibility.
        info.Environment["FUSION_UNREAL_ENGINE"]=engine.Version;
        GameTestCopyEnvironment.Apply(info);return info;
    }
    public void Launched(int pid)
    {
        var launched=Process.GetProcessById(pid);
        try
        {
            var path=new StringBuilder(32768);uint size=(uint)path.Capacity;
            if(!QueryFullProcessImageName(launched.Handle,0,path,ref size)||!string.Equals(Path.GetFullPath(path.ToString()),runtime,StringComparison.OrdinalIgnoreCase))
                throw new IOException("无法验证刚启动的虚幻游戏进程。");
            lock(stateGate)
            {
                ObjectDisposedException.ThrowIf(disposed,this);
                if(peer is not null)throw new IOException("虚幻翻译连接已绑定游戏进程。");
                peer=launched;
            }
        }
        catch{launched.Dispose();throw;}
    }
    private bool PeerRunning()
    {
        // Dispose detaches the Process under this lock before closing its handle.
        lock(stateGate)
        {
            if(disposed||peer is null)return false;
            try{return !peer.HasExited;}catch(InvalidOperationException){return false;}
        }
    }
    public bool Connected{get{lock(stateGate)return accepted&&PeerRunning();}}
    private static bool StringProperty(JsonElement value,string name,out string? text)
    {
        text=null;
        if(value.ValueKind!=JsonValueKind.Object||!value.TryGetProperty(name,out var property)||property.ValueKind!=JsonValueKind.String)return false;
        text=property.GetString();return true;
    }
    private static bool IntegerProperty(JsonElement value,string name,out int number)
    {
        number=0;return value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(name,out var property)
            &&property.ValueKind==JsonValueKind.Number&&property.TryGetInt32(out number);
    }
    private static bool BooleanProperty(JsonElement value,string name,out bool flag)
    {
        flag=false;
        if(value.ValueKind!=JsonValueKind.Object||!value.TryGetProperty(name,out var property)||property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))return false;
        flag=property.GetBoolean();return true;
    }
    public async Task AcceptAsync(CancellationToken cancellation=default)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);timeout.CancelAfter(60000);
        try
        {
            while(true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if(!PeerRunning())throw new IOException("虚幻游戏已退出，未建立翻译连接。");
                if(await GodotFileTransport.ReadJsonAsync(Path.Combine(folder,"hello.json"),timeout.Token) is {} hello)
                {
                    if(!IntegerProperty(hello,"protocol",out var protocol)||protocol!=76||!StringProperty(hello,"secret",out var receivedSecret)||receivedSecret!=secret)
                        throw new IOException("虚幻翻译连接验证失败。");
                    if(hello.TryGetProperty("error",out _))
                    {
                        if(!StringProperty(hello,"error",out var error))throw new IOException("虚幻翻译连接回包格式无效。");
                        throw new IOException("虚幻运行时接入检查未通过："+SafeDiagnosticOutput.Summary(error));
                    }
                    lock(stateGate){if(disposed)throw new OperationCanceledException(lifetime.Token);accepted=true;}return;
                }
                await Task.Delay(50,timeout.Token);
            }
        }
        catch(OperationCanceledException)when(!cancellation.IsCancellationRequested&&!lifetime.IsCancellationRequested)
        {throw new IOException("虚幻翻译组件未能在 60 秒内连接；此构建的运行时接入未通过。可退出后选择“仅启动”恢复原游戏。");}
    }
    public async Task<JsonElement> RequestAsync(JsonObject request,CancellationToken cancellation=default)
    {
        using var waiting=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);
        bool entered=false,publicationAttempted=false,responseConfirmed=false;
        try
        {
            await gate.WaitAsync(waiting.Token);entered=true;
            cancellation.ThrowIfCancellationRequested();
            if(!Connected)throw new IOException("虚幻翻译连接已关闭。");
            // Session credentials and filtering must never mutate the caller's object.
            var outgoing=request.DeepClone().AsObject();
            if(outgoing["op"] is not JsonValue operation||!operation.TryGetValue<string>(out var op))throw new IOException("虚幻翻译请求格式无效。");
            if(deferredCatalog&&op is "translationPrepare" or "translationEnable")
            {
                // Ordinary startup supplies only the dormant transport. Catalog
                // extraction and publication belong to the first explicit enable.
                await Task.Run(()=>UnrealGameAdapter.PrepareCatalog(executable,engine),waiting.Token);
                waiting.Token.ThrowIfCancellationRequested();
                string staged=Path.Combine(folder,"catalog.json.tmp");
                File.Copy(Path.Combine(UnrealGameAdapter.Storage(executable),"catalog.json"),staged,true);
                File.Move(staged,Path.Combine(folder,"catalog.json"),true);deferredCatalog=false;
            }
            if(op=="translationPrepare")
            {lock(stateGate)outgoing["retainStartup"]=retainStartup;}
            if(op=="translationApply")
            {
                if(outgoing["entries"] is not JsonArray rows)throw new IOException("虚幻译文列表格式无效。");
                var valid=new JsonArray();foreach(var r in rows)
                {
                    if(r is JsonObject row&&row["source"] is JsonValue sourceValue&&sourceValue.TryGetValue<string>(out var source)
                        &&row["text"] is JsonValue textValue&&textValue.TryGetValue<string>(out var text)&&ValidPair(source,text))valid.Add(row.DeepClone());
                }
                outgoing["entries"]=valid;
            }
            int current=++id;outgoing["id"]=current;outgoing["secret"]=secret;string value=outgoing.ToJsonString();
            if(Encoding.UTF8.GetByteCount(value)>262144)throw new IOException("虚幻翻译请求超过限制。");
            cancellation.ThrowIfCancellationRequested();
            // Once publication starts, drain its reply before allowing the next single-file
            // request. Caller cancellation cannot safely retract an observed game command.
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);timeout.CancelAfter(20000);
            publicationAttempted=true;
            await GodotFileTransport.PublishAsync(Path.Combine(folder,"request.json"),value,timeout.Token);
            string responsePath=Path.Combine(folder,$"response-{current}.json");
            while(true)
            {
                timeout.Token.ThrowIfCancellationRequested();if(!Connected)throw new IOException("虚幻游戏已退出。");
                if(await GodotFileTransport.ReadJsonAsync(responsePath,timeout.Token) is {} response)
                {
                    if(!IntegerProperty(response,"id",out var responseId)||responseId!=current||!StringProperty(response,"secret",out var receivedSecret)||receivedSecret!=secret
                        ||!BooleanProperty(response,"ok",out var ok))throw new IOException("虚幻回包验证失败。");
                    JsonElement result=default;string? error=null;
                    if(ok)
                    {
                        if(!response.TryGetProperty("result",out result)||result.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))throw new IOException("虚幻回包结果格式无效。");
                    }
                    else if(!StringProperty(response,"error",out error))throw new IOException("虚幻回包错误格式无效。");
                    responseConfirmed=true;
                    // Cleanup is not a game operation; a brief reader lock must not turn a
                    // validated successful update into an unknown completion or retry.
                    try{File.Delete(responsePath);}catch(IOException){}catch(UnauthorizedAccessException){}
                    if(!ok)
                    {
                        bool restorationPending=StringProperty(response,"errorCode",out var errorCode)&&errorCode=="restore-culture-not-active"
                            &&BooleanProperty(response,"recoverable",out var recoverable)&&recoverable;
                        AppLog.Write("unreal-bridge",restorationPending?"request-rejected; restore-pending-inactive-culture":"request-rejected; runtime-reported-failure");
                        if(restorationPending)throw new UnrealCulturePendingException();
                        Dispose();
                        throw new IOException("虚幻文本更新失败："+SafeDiagnosticOutput.Summary(error));
                    }
                    if(op=="translationPrepare"){lock(stateGate)retainStartup=false;}
                    cancellation.ThrowIfCancellationRequested();
                    return result.Clone();
                }
                await Task.Delay(20,timeout.Token);
            }
        }
        catch(OperationCanceledException ex)when(publicationAttempted&&!responseConfirmed&&!lifetime.IsCancellationRequested)
        {
            AppLog.Write("unreal-bridge","request-timeout; completion-and-restoration-unconfirmed");
            Dispose();
            throw new IOException("虚幻翻译组件未响应，本次操作未确认完成；如果正在关闭翻译，也不能确认原文和字体已经恢复。请保存进度并正常退出游戏后重试。",ex);
        }
        catch
        {
            if(publicationAttempted&&!responseConfirmed)
            {
                AppLog.Write("unreal-bridge","request-failed; completion-and-restoration-unconfirmed");Dispose();
            }
            throw;
        }
        finally{if(entered)gate.Release();}
    }
    public void Dispose()
    {
        Process? closed;
        lock(stateGate){if(disposed)return;disposed=true;accepted=false;closed=peer;peer=null;}
        lifetime.Cancel();
        try{File.WriteAllText(Path.Combine(folder,"stop"),secret);}catch(IOException){}catch(UnauthorizedAccessException){}
        closed?.Dispose();
        // Active waiters still use these objects until their finally blocks release gate.
    }
}
