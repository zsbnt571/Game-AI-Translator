using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

internal sealed class RgssTranslationConnection : IEmbeddedTranslationConnection
{
    private readonly string exe,folder,secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly SemaphoreSlim gate=new(1,1);private readonly CancellationTokenSource lifetime=new();
    private Process? peer;private int id;private bool ready,disposed;
    private readonly bool native;private readonly Func<int,bool> foreground,activateForSpeed;
    private readonly TimeSpan responseBudget;
    private sealed record PendingRequest(int Id,string Operation,string Published,string Response);
    private PendingRequest? pending;
    internal RgssTranslationConnection(string exe,Func<int,bool>? foreground=null,TimeSpan? responseBudget=null,Func<int,bool>? activateForSpeed=null)
    {
        this.exe=Path.GetFullPath(exe);native=RgssGameAdapter.Detect(exe)?.Mkxp==false;this.foreground=foreground??Foreground;this.activateForSpeed=activateForSpeed??ActivateOwnedWindow;
        this.responseBudget=responseBudget??TimeSpan.FromSeconds(15);
        if(this.responseBudget<=TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(responseBudget));
        folder=NativeGameSessionFiles.Create(exe,"rgss",compact:true);if(folder.Length>225)throw new EmbeddedCompatibilityException("数据目录路径过长，旧版 RPG 无法读取通信文件。请使用较短的数据目录。");
    }
    public bool Connected=>ready&&!disposed&&peer is not null&&!peer.HasExited;
    public void PrimeStartup(IReadOnlyDictionary<string,string> entries)
    {
        var accepted=new Dictionary<string,string>(StringComparer.Ordinal);long bytes=1024;
        foreach(var p in entries){if(p.Key.Length is <1 or >6000||p.Value.Length is <1 or >12000||p.Key.Contains('\0')||p.Value.Contains('\0')||!RpgTranslationText.ValidCachedPair(p.Key,p.Value,EmbeddedTextSyntax.Rpg))continue;long cost=Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(p))+64;if(bytes+cost>900L*1024||accepted.Count>=2000)break;accepted[p.Key]=p.Value;bytes+=cost;}
        File.WriteAllText(Path.Combine(folder,"startup.json"),JsonSerializer.Serialize(new{secret,entries=accepted}),new UTF8Encoding(false));
    }
    public ProcessStartInfo StartInfo(bool translationAtStartup=true)
    {
        if(!RgssGameAdapter.IsInstalled(exe))throw new IOException("翻译组件不完整或游戏已更新，请重新应用翻译。");
        var info=new ProcessStartInfo(exe){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(exe),WindowStyle=ProcessWindowStyle.Normal};
        GameLaunchEnvironment.ClearSession(info);
        GameFontSettings.ApplyLaunch(info,exe,translationAtStartup);
        GameTestCopyEnvironment.Apply(info);NativeGameSessionFiles.ApplySteamIdentity(info);
        info.Environment["FUSION_RGSS_SESSION"]=folder;info.Environment["FUSION_RGSS_SECRET"]=secret;
        info.Environment["FUSION_RGSS_TRANSLATION"]=translationAtStartup?"1":"0";info.Environment["FUSION_RGSS_OWNER_PID"]=Environment.ProcessId.ToString();return info;
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder path,ref uint length);
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]private static extern bool ShowWindow(IntPtr window,int command);
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(IntPtr window);
    private static bool Foreground(int pid){GetWindowThreadProcessId(GetForegroundWindow(),out uint active);return active==(uint)pid;}
    private bool ActivateOwnedWindow(int pid)
    {
        // The stored Process was bound to this exact executable at launch.
        // Never discover a similarly named game or activate a different PID.
        if(!Connected||peer is null||peer.Id!=pid)return false;
        peer.Refresh();IntPtr window=peer.MainWindowHandle;
        if(window==IntPtr.Zero||!IsWindowVisible(window))return false;
        GetWindowThreadProcessId(window,out uint owner);if(owner!=(uint)pid||peer.HasExited)return false;
        if(IsIconic(window))ShowWindow(window,9);
        return SetForegroundWindow(window);
    }
    public void Launched(int pid){var process=Process.GetProcessById(pid);try{var image=new StringBuilder(32768);uint length=(uint)image.Capacity;if(process.HasExited||!QueryFullProcessImageName(process.Handle,0,image,ref length)||!Path.GetFullPath(image.ToString()).Equals(exe,StringComparison.OrdinalIgnoreCase))throw new IOException("RGSS 游戏进程验证失败。");peer=process;}catch{process.Dispose();throw;}}
    public async Task AcceptAsync(CancellationToken cancellation=default)
    {
        using var ct=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);ct.CancelAfter(15000);
        while(true)
        {
            ct.Token.ThrowIfCancellationRequested();if(peer?.HasExited!=false)throw new IOException("游戏已退出。");
            string errorPath=Path.Combine(folder,"bridge-error.txt");
            if(File.Exists(errorPath))throw new IOException("旧版 RPG 翻译组件启动失败，诊断记录："+errorPath);
            if(await GodotFileTransport.ReadJsonAsync(Path.Combine(folder,"hello.json"),ct.Token) is {} hello)
            {
                if(hello.GetProperty("secret").GetString()!=secret||hello.GetProperty("protocol").GetInt32()!=157||hello.GetProperty("engine").GetString()!="rgss"||hello.GetProperty("pid").GetInt32()!=peer.Id)throw new IOException("RGSS 翻译连接验证失败。");
                ready=true;return;
            }
            await Task.Delay(25,ct.Token);
        }
    }
    public async Task<JsonElement> RequestAsync(JsonObject request,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        string? operation=request["op"]?.GetValue<string>();
        bool explicitSpeed=request["activateForSpeed"]?.GetValue<bool>()==true&&
            (operation is "speedEnable" or "speedDisable"||operation=="gameSpeed"&&request.ContainsKey("rate"));
        // Once per explicit user action only. Read-only heartbeats and deferred
        // retries must never continually pull focus away from another game.
        if(native&&explicitSpeed&&Connected&&!foreground(peer!.Id))activateForSpeed(peer.Id);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        if(explicitSpeed)deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try{return await UnityRequestRecovery.ExecuteAsync(token=>SendAsync(request,token),operation is "translationDisable" or "speedDisable",request["deferIfPaused"]?.GetValue<bool>()==true,deadline.Token,TimeSpan.FromSeconds(30)).ConfigureAwait(false);}
        catch(OperationCanceledException)when(explicitSpeed&&deadline.IsCancellationRequested&&!cancellation.IsCancellationRequested&&!lifetime.IsCancellationRequested)
        {throw new IOException("等待游戏确认速度超时，尚未确认是否应用。请返回游戏后再次应用倍率。");}
    }
    private async Task<JsonElement> SendAsync(JsonObject request,CancellationToken cancellation=default)
    {
        using var ct=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);await gate.WaitAsync(ct.Token);
        try
        {
            if(!Connected)throw new IOException("游戏连接已关闭。");
            if(native&&request["deferIfPaused"]?.GetValue<bool>()==true&&!foreground(peer!.Id))throw new UnityMainThreadPausedException();
            // A caller can cancel after publication (for example when turning
            // translation off). Consume that exact receipt before publishing
            // anything else, so the bridge cannot skip an older request ID.
            if(pending is {} previous)await ReceiveAsync(previous,ct.Token);
            // Native RGSS suspends the Ruby interpreter when inactive. A request
            // deferred here has not been published and can be retried safely.
            if(native&&!foreground(peer!.Id))throw new UnityMainThreadPausedException();
            int current=++id;var q=(JsonObject)request.DeepClone();q.Remove("deferIfPaused");q.Remove("activateForSpeed");q["id"]=current;q["secret"]=secret;
            string text=q.ToJsonString();if(Encoding.UTF8.GetByteCount(text)>262144)throw new IOException("翻译请求超过限制。");
            var sent=new PendingRequest(current,request["op"]?.GetValue<string>()??"",Path.Combine(folder,"request-"+current+".json"),Path.Combine(folder,"response-"+current+".json"));
            pending=sent;
            try{await GodotFileTransport.PublishAsync(sent.Published,text,ct.Token);}
            catch{if(!File.Exists(sent.Published))pending=null;throw;}
            return await ReceiveAsync(sent,ct.Token);
        }
        finally{gate.Release();}
    }
    private async Task<JsonElement> ReceiveAsync(PendingRequest sent,CancellationToken cancellation)
    {
        var clock=Stopwatch.StartNew();TimeSpan previous=TimeSpan.Zero,activeWait=TimeSpan.Zero;
        bool wasActive=!native||foreground(peer!.Id),reportedPause=false;
        while(true)
        {
            cancellation.ThrowIfCancellationRequested();if(!Connected)throw new IOException("游戏连接已关闭。");
            bool active=!native||foreground(peer!.Id);var now=clock.Elapsed;
            if(active&&wasActive)activeWait+=now-previous;
            previous=now;wasActive=active;
            if(!active&&!reportedPause)
            {
                reportedPause=true;
                AppLog.Write("rgss-connection",$"request={sent.Id} op={sent.Operation} waiting-for-game-foreground; published request retained");
            }
            // Poll even while inactive: a reply already written by the game is
            // authoritative. Otherwise retain this request and exclude the
            // interpreter's background suspension from its response budget.
            if(await GodotFileTransport.ReadJsonAsync(sent.Response,cancellation) is {} r)
            {
                if(r.GetProperty("id").GetInt32()!=sent.Id||r.GetProperty("secret").GetString()!=secret)throw new IOException("游戏响应验证失败。");
                bool ok=r.GetProperty("ok").GetBoolean();var result=r.GetProperty("result").Clone();
                File.Delete(sent.Response);try{File.Delete(sent.Published);}catch(IOException){}catch(UnauthorizedAccessException){}
                pending=null;
                if(!ok)
                {
                    string message=result.TryGetProperty("message",out var m)&&m.ValueKind==JsonValueKind.String?m.GetString()??"":"";
                    throw new IOException(message.Length is >0 and <=500?message:"游戏未接受此操作。");
                }
                if(reportedPause)AppLog.Write("rgss-connection",$"request={sent.Id} op={sent.Operation} receipt-confirmed; elapsedMs={clock.ElapsedMilliseconds} activeWaitMs={(long)activeWait.TotalMilliseconds}");
                return result;
            }
            if(activeWait>=responseBudget)
            {
                AppLog.Write("rgss-connection",$"request={sent.Id} op={sent.Operation} active-response-timeout; elapsedMs={clock.ElapsedMilliseconds} activeWaitMs={(long)activeWait.TotalMilliseconds}");
                throw new IOException("游戏在前台运行时仍未及时回应翻译请求；请求结果尚未确认，已有译文保留。");
            }
            await Task.Delay(20,cancellation);
        }
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        try{File.WriteAllText(Path.Combine(folder,"stop.json"),JsonSerializer.Serialize(new{secret}),new UTF8Encoding(false));}catch(IOException){}catch(UnauthorizedAccessException){}
        lifetime.Cancel();peer?.Dispose();
    }
}
