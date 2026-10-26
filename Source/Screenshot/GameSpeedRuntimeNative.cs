using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal sealed class GameSpeedNativeChannel : IGameSpeedChannel
{
    private readonly object sync=new();
    private readonly SemaphoreSlim writer=new(1,1);
    private readonly MemoryMappedFile mapping;
    private readonly MemoryMappedViewAccessor view;
    private bool disposed;
    private int sequence;
    private string confirmation="";
    private string lastDiagnostics="";
    internal string MappingName { get; }
    internal string Token { get; }
    public GameSpeedProcess Process { get; }
    public string Confirmation { get{lock(sync)return confirmation;} }
    public bool ClockActivityObserved { get{lock(sync)return !disposed&&ActivityObserved();} }
    public string Diagnostics
    {
        get
        {
            lock(sync)
            {
                if(disposed)return lastDiagnostics;
                bool acknowledged=sequence>0&&view.ReadInt32(108)==sequence;
                int error=view.ReadInt32(144),modules=view.ReadInt32(160);
                ulong total=0,mask=view.ReadUInt64(224),baseline=view.ReadUInt64(216);
                string[] names=["QPC","Tick","Tick64","timeGetTime","FileTime","PreciseFileTime"];
                var calls=new List<string>();
                for(int i=0;i<6;i++)
                {
                    ulong count=view.ReadUInt64(168+i*8);total+=count;
                    if(count>0)calls.Add(names[i]+"="+count);
                }
                ulong fresh=total>=baseline?total-baseline:0;
                string used=string.Join(",",names.Where((_,i)=>(mask&(1ul<<i))!=0));
                string receipt=!acknowledged?"等待回执":error!=0?"已拒绝（错误 "+error+"）":"已确认命令";
                lastDiagnostics=receipt+"；新计时读取="+fresh+"；本次接口="+(used.Length==0?"-":used)+
                    "；范围模块="+Math.Clamp(modules,0,128)+"；累计调用="+(calls.Count==0?"-":string.Join(",",calls));
                return lastDiagnostics;
            }
        }
    }
    private bool ActivityObserved()
    {
        if(sequence<=0||view.ReadUInt32(28)!=1||view.ReadInt32(108)!=sequence||view.ReadInt32(144)!=0)return false;
        double requested=view.ReadDouble(112),actual=view.ReadDouble(120);
        if(requested==1||!double.IsFinite(requested)||!double.IsFinite(actual)||Math.Abs(actual-requested)>1e-9)return false;
        ulong total=0;for(int i=0;i<6;i++)total+=view.ReadUInt64(168+i*8);
        ulong baseline=view.ReadUInt64(216),mask=view.ReadUInt64(224);
        return total>=baseline&&total-baseline>=2&&(mask&0x3fu)!=0;
    }
    public bool Ready
    {
        get{lock(sync){if(disposed||view.ReadUInt32(28)!=1)return false;long age=Environment.TickCount64-(long)view.ReadUInt64(136);return age is >=0 and <5000;}}
    }
    internal GameSpeedNativeChannel(GameSpeedProcess process)
    {
        Process=process;MappingName="Local\\FusionSpeed_"+Guid.NewGuid().ToString("N");Token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        mapping=MemoryMappedFile.CreateNew(MappingName,256,MemoryMappedFileAccess.ReadWrite);
        view=mapping.CreateViewAccessor(0,256,MemoryMappedFileAccess.ReadWrite);
        using var owner=System.Diagnostics.Process.GetCurrentProcess();
        view.Write(0,0x46535044u);view.Write(4,2u);view.Write(8,256u);view.Write(12,(uint)Environment.ProcessId);
        view.Write(16,(ulong)owner.StartTime.ToUniversalTime().ToFileTimeUtc());view.Write(24,(uint)process.Pid);view.Write(32,process.Created);
        byte[] tokenBytes=Encoding.ASCII.GetBytes(Token);view.WriteArray(40,tokenBytes,0,64);
        view.Write(112,1d);view.Write(120,1d);view.Write(128,(ulong)Environment.TickCount64);view.Write(164,1u);
        Thread.MemoryBarrier();
    }
    internal static async Task<IGameSpeedChannel> AttachAsync(GameSpeedProcess process,CancellationToken token)
    {
        string helper=Path.Combine(AppContext.BaseDirectory,"adapters","speed","FusionSpeedHost."+(process.X64?"x64":"x86")+".exe");
        if(!File.Exists(helper))throw new IOException("速度控制组件缺失，请使用完整版本。");
        var channel=new GameSpeedNativeChannel(process);
        try
        {
            var start=new ProcessStartInfo(helper){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(helper)};
            foreach(var item in new[]{"--pid",process.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture),"--created",process.Created.ToString(System.Globalization.CultureInfo.InvariantCulture),"--exe",process.ExePath,"--mapping",channel.MappingName,"--token",channel.Token})start.ArgumentList.Add(item);
            using var child=System.Diagnostics.Process.Start(start)??throw new IOException("无法启动速度控制组件。");
            // Drain bounded native output concurrently. No credentials, command line
            // or token is surfaced to UI/logs; helper returns only a small receipt.
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var stdout=ReadBoundedAsync(child.StandardOutput,timeout.Token);var stderr=ReadBoundedAsync(child.StandardError,timeout.Token);
            try
            {
                var exit=child.WaitForExitAsync(timeout.Token);
                while(!exit.IsCompleted)
                {
                    await Task.WhenAny(exit,Task.Delay(500,timeout.Token)).ConfigureAwait(false);
                    timeout.Token.ThrowIfCancellationRequested();channel.Heartbeat();
                }
                await exit.ConfigureAwait(false);
            }
            catch(OperationCanceledException)
            {
                // This process is our short-lived helper, never the target game.
                // Leaving an attach running after cancellation could apply a stale
                // request, so invalidate its map immediately; native verifies mode.
                channel.Stop();timeout.Cancel();
                try{await Task.WhenAll(stdout,stderr).ConfigureAwait(false);}catch(OperationCanceledException){}
                token.ThrowIfCancellationRequested();
                throw new IOException("速度控制连接超时，未确认速度更改。");
            }
            string receipt=await stdout.ConfigureAwait(false);_=await stderr.ConfigureAwait(false);
            if(child.ExitCode!=0)throw new IOException(ReceiptError(receipt));
            try
            {
                using var json=JsonDocument.Parse(receipt);
                if(!json.RootElement.TryGetProperty("ok",out var ok)||ok.ValueKind!=JsonValueKind.True)throw new IOException(ReceiptError(receipt));
            }
            catch(JsonException){throw new IOException("速度控制组件返回了无效结果。");}
            var clock=Stopwatch.StartNew();
            while(!channel.Ready)
            {
                token.ThrowIfCancellationRequested();channel.Heartbeat();
                if(clock.Elapsed>TimeSpan.FromSeconds(3))throw new IOException("游戏未确认速度控制连接。");
                await Task.Delay(25,token).ConfigureAwait(false);
            }
            return channel;
        }
        catch{channel.Dispose();throw;}
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader,CancellationToken token)
    {
        var output=new StringBuilder();var buffer=new char[1024];int read;
        while((read=await reader.ReadAsync(buffer.AsMemory(),token).ConfigureAwait(false))>0)
            if(output.Length<8192)output.Append(buffer,0,Math.Min(read,8192-output.Length));
        return output.ToString();
    }
    private static string ReceiptError(string receipt)
    {
        try
        {
            using var json=JsonDocument.Parse(receipt);
            if(json.RootElement.TryGetProperty("errorCode",out var error)&&error.TryGetInt32(out int code))return code switch
            {
                1 or 2 or 14=>"速度控制会话无效，请重试。",
                3 or 12=>"游戏进程已变化，请重新设置速度。",
                4=>"速度组件与游戏位数不匹配，请使用完整版本。",
                5=>"无法对该进程使用游戏速度控制。",
                6=>"此游戏已有速度组件，请通过现有游戏连接调速。",
                7=>"系统未允许连接速度组件，游戏保持原样运行。",
                8 or 16=>"速度控制连接超时，未确认速度更改。",
                9=>"速度控制连接尚在恢复，请稍后重试。",
                10=>"未找到可用的游戏计时接口，此游戏暂不支持调速。",
                11=>"速度控制连接已关闭，请重试。",
                13=>"速度倍率须在 0.25× 到 100× 之间。",
                15=>"无法启动速度控制组件。",
                17=>"此游戏已连接另一版本的速度组件，请退出游戏后重试。",
                18=>"游戏中仍驻留旧版速度组件，请正常退出游戏后重新启动。",
                _=>"此游戏暂时无法连接速度控制，请查看组件状态。"
            };
        }
        catch(JsonException){}
        return "此游戏暂时无法连接速度控制，请查看组件状态。";
    }
    public Task<double> SetAsync(double rate,CancellationToken token)=>SetCoreAsync(rate,token,true);
    public Task<double> SetAcknowledgedAsync(double rate,CancellationToken token)=>SetCoreAsync(rate,token,false);
    private async Task<double> SetCoreAsync(double rate,CancellationToken token,bool requireClockActivity)
    {
        if(!double.IsFinite(rate)||rate<.25||rate>100)throw new IOException("速度倍率须在 0.25× 到 100× 之间。");
        await writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            int request;
            lock(sync)
            {
                ObjectDisposedException.ThrowIf(disposed,this);
                if(view.ReadUInt32(28)!=1)throw new IOException("速度连接已断开，已请求恢复正常速度。");
                if(view.ReadInt32(108)!=sequence)throw new IOException("上一条速度设置尚未确认，请稍后重试。");
                confirmation="";
                view.Write(112,rate);view.Write(164,1u);view.Write(128,(ulong)Environment.TickCount64);
                Thread.MemoryBarrier();request=++sequence;view.Write(104,request);
            }
            var clock=Stopwatch.StartNew();bool acknowledged=false;
            while(true)
            {
                token.ThrowIfCancellationRequested();
                lock(sync)
                {
                    ObjectDisposedException.ThrowIf(disposed,this);
                    if(view.ReadUInt32(28)!=1)throw new IOException("速度连接已断开，已请求恢复正常速度。");
                    view.Write(128,(ulong)Environment.TickCount64);
                    if(view.ReadInt32(108)==request)
                    {
                        Thread.MemoryBarrier();
                        if(view.ReadInt32(144)!=0)throw new IOException("游戏拒绝了速度设置，未确认所选倍率。");
                        acknowledged=true;double actual=view.ReadDouble(120);
                        // Restoring 1x must work even when the game is idle. For an
                        // accelerated rate, receipt alone proves only delivery. The
                        // native command resets its baseline after setting the rate;
                        // require new game-scoped clock reads, excluding Sleep calls,
                        // our worker's original clocks and earlier sessions' counts.
                        if(rate==1||!double.IsFinite(actual)||Math.Abs(actual-rate)>1e-9)return actual;
                        if(ActivityObserved())
                        {
                            confirmation="计时接口已应用；实际剧情和动画速度取决于游戏。";
                            return actual;
                        }
                        // Acknowledgement-only mode is exclusively aggregated by
                        // the runtime with actual renderer activity. It must never
                        // manufacture clock evidence for an idle lifecycle root.
                        if(!requireClockActivity)return actual;
                    }
                }
                if(clock.Elapsed>TimeSpan.FromSeconds(3))throw new IOException(acknowledged?"未检测到游戏调用可调速的计时接口，已请求恢复 1×。":"游戏尚未确认所选速度，已恢复正常速度。");
                await Task.Delay(25,token).ConfigureAwait(false);
            }
        }
        finally{writer.Release();}
    }
    public void Heartbeat(){lock(sync){if(!disposed)view.Write(128,(ulong)Environment.TickCount64);}}
    public void Stop()
    {
        lock(sync)
        {
            if(disposed)return;
            confirmation="";
            view.Write(112,1d);view.Write(164,2u);view.Write(128,(ulong)Environment.TickCount64);
            Thread.MemoryBarrier();view.Write(104,++sequence);
        }
    }
    public void Dispose()
    {
        lock(sync)
        {
            if(disposed)return;Stop();disposed=true;view.Dispose();mapping.Dispose();
        }
    }
}
