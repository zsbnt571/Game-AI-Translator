using System.Diagnostics;

namespace ScreenshotTranslationUiTester;

internal sealed record GameSpeedRuntimeStatus(double Rate,bool Active,bool Busy,string Message,string ContextKey)
{
    // Bounded, read-only evidence from this authorized session. Never a promise
    // that a game's story, rendering or animation actually ran at this rate.
    internal string Diagnostics { get; init; }="";
}
internal sealed record GameSpeedProcess(int Pid,ulong Created,string ExePath,bool X64);
internal sealed record GameSpeedTargets(GameSpeedProcess Root,IReadOnlyList<GameSpeedProcess> Processes,bool Chromium);

// This transport is deliberately independent of both translation and data editing.
// Tests replace this boundary with owned processes/channels, never installed games.
internal interface IGameSpeedPlatform
{
    Task<GameSpeedTargets> DiscoverAsync(GameInfo game,CancellationToken token,GameSpeedProcess? boundRoot=null);
    bool IsAlive(GameSpeedProcess process);
    Task<IGameSpeedChannel> AttachAsync(GameSpeedProcess process,CancellationToken token);
}
internal interface IGameSpeedChannel : IDisposable
{
    GameSpeedProcess Process { get; }
    bool Ready { get; }
    string Confirmation=>"";
    string Diagnostics=>"";
    bool ClockActivityObserved=>false;
    Task<double> SetAsync(double rate,CancellationToken token);
    // Chromium's lifecycle root may never read a game clock. All members must
    // acknowledge, but the runtime evaluates fresh activity across renderers.
    Task<double> SetAcknowledgedAsync(double rate,CancellationToken token)=>SetAsync(rate,token);
    void Heartbeat();
    void Stop();
}

internal sealed class GameSpeedRuntime : IDisposable
{
    private sealed class Session(GameInfo game)
    {
        internal readonly object Sync=new();
        internal readonly SemaphoreSlim Gate=new(1,1);
        internal readonly GameInfo Game=game;
        internal GameSpeedProcess? Root;
        internal CancellationTokenSource? Authorization;
        internal bool AuthorizationReady,Revoking;
        internal Task? Revocation;
        internal IGameSpeedChannel[] Channels=[];
        internal GameSpeedRuntimeStatus Status=new(1,false,false,"","");
        internal long LastDiscovery;
        internal bool Chromium;
    }
    private readonly object sync=new();
    private readonly Dictionary<string,Session> sessions=new(StringComparer.OrdinalIgnoreCase);
    private readonly IGameSpeedPlatform platform;
    private readonly CancellationTokenSource lifetime=new();
    private readonly TimeSpan monitorInterval;
    private readonly Task monitor;
    private bool disposed;
    internal event Action? Changed;

    internal GameSpeedRuntime():this(new WindowsGameSpeedPlatform()) { }
    internal GameSpeedRuntime(IGameSpeedPlatform platform,TimeSpan? monitorInterval=null)
    {
        this.platform=platform;this.monitorInterval=monitorInterval??TimeSpan.FromSeconds(1);
        monitor=Task.Run(MonitorAsync);
    }
    internal GameSpeedRuntimeStatus Status(string exe)
    {
        // Paths here are library identities; no filesystem/process query on UI refresh.
        lock(sync)
        {
            if(sessions.TryGetValue(exe,out var session))lock(session.Sync)return session.Status;
            return new(1,false,false,"","");
        }
    }
    internal bool Authorized(string exe)
    {
        lock(sync)return sessions.TryGetValue(exe,out var session)&&HasAuthorization(session);
    }
    private static bool HasAuthorization(Session session)
    {lock(session.Sync)return session.AuthorizationReady&&!session.Revoking&&session.Authorization is {IsCancellationRequested:false};}

    // An explicit modification action binds the process identity, but does not
    // load a speed component. SetAsync cannot create its own authorization.
    internal async Task AuthorizeAsync(GameInfo game,CancellationToken token=default)
    {
        Session session;CancellationTokenSource authorization;
        lock(sync)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(!sessions.TryGetValue(game.ExePath,out session!))sessions.Add(game.ExePath,session=new(game));
            lock(session.Sync)
            {
                if(session.Revoking)throw new IOException("正在关闭修改，请稍后重试。");
                if(session.Authorization is {IsCancellationRequested:false})
                {if(session.AuthorizationReady)return;throw new IOException("正在开启修改，请稍后重试。");}
                authorization=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token);
                session.Authorization=authorization;session.AuthorizationReady=false;
            }
        }
        bool entered=false;
        try
        {
            await session.Gate.WaitAsync(authorization.Token).ConfigureAwait(false);entered=true;
            var targets=await platform.DiscoverAsync(game,authorization.Token).ConfigureAwait(false);
            authorization.Token.ThrowIfCancellationRequested();
            if(!platform.IsAlive(targets.Root))throw new IOException("游戏进程已变化，请重新开启修改。");
            DefoldSpeedLaunch.RequirePrepared(game,targets.Root);
            session.Root=targets.Root;
            lock(session.Sync){authorization.Token.ThrowIfCancellationRequested();session.AuthorizationReady=true;}
        }
        catch
        {
            authorization.Cancel();
            if(entered)Clear(session);
            throw;
        }
        finally{if(entered)session.Gate.Release();}
    }

    // Revoke before the first await: a pending attach/write and maintenance all
    // share this token. Reset only owned channels; never discover another target.
    internal Task RevokeAsync(string exe)
    {
        Session? session;
        lock(sync){session=sessions.GetValueOrDefault(exe);if(session is null)return Task.CompletedTask;}
        TaskCompletionSource completion;
        lock(session.Sync)
        {
            if(session.Revoking)return session.Revocation!;
            completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Revocation=completion.Task;session.Revoking=true;session.AuthorizationReady=false;session.Authorization?.Cancel();
        }
        // Stop already owned channels immediately, even if an old write is stuck.
        // No new target is discovered as part of revocation.
        foreach(var channel in Channels(session))try{channel.Stop();}catch{}
        _=Finish();return completion.Task;
        async Task Finish()
        {
            await session.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach(var channel in Channels(session))
                    try{if(channel.Ready&&platform.IsAlive(channel.Process))await channel.SetAsync(1,CancellationToken.None).ConfigureAwait(false);}
                    catch(Exception){ /* Stop below is the owned-channel fallback. */ }
                string diagnostics=Read(session).Diagnostics;
                Clear(session);Publish(session,new(1,false,false,"",""){Diagnostics=Retired(diagnostics)});
            }
            finally
            {
                session.Gate.Release();
                lock(session.Sync){session.Revoking=false;completion.TrySetResult();}
            }
        }
    }
    internal async Task<GameSpeedRuntimeStatus> SetAsync(GameInfo game,double rate,CancellationToken token=default)
    {
        if(!double.IsFinite(rate)||rate<.25||rate>100)
            return new(1,false,false,"速度倍率须在 0.25× 到 100× 之间。","");
        Session session;
        lock(sync)
        {
            if(disposed)return new(1,false,false,"速度控制已关闭。","");
            if(!sessions.TryGetValue(game.ExePath,out session!))sessions.Add(game.ExePath,session=new(game));
        }
        CancellationToken authorization;
        lock(session.Sync)
        {
            if(rate!=1&&!HasAuthorization(session))
                return new(1,false,false,"请先开启修改，再调整游戏速度。","");
            authorization=rate==1?CancellationToken.None:session.Authorization!.Token;
        }
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token,authorization);
        bool entered=false;
        try
        {
            await session.Gate.WaitAsync(linked.Token).ConfigureAwait(false);entered=true;
            Publish(session,Read(session) with{Busy=true,Message=""});
            // Discovery, image validation and native helper launch must never run on
            // the WPF synchronization context, even if an awaited task was cached.
            return await Task.Run(async()=>
            {
                // Reset only the channels this session already owns. In particular,
                // clicking 1x must never discover or inject an otherwise idle game.
                if(rate==1)
                {
                    var owned=Channels(session);
                    foreach(var channel in owned)
                        if(platform.IsAlive(channel.Process)&&channel.Ready)
                        {
                            double actual=await channel.SetAsync(1,linked.Token).ConfigureAwait(false);
                            if(!double.IsFinite(actual)||Math.Abs(actual-1)>1e-9)
                                throw new IOException("游戏尚未确认恢复正常速度，已停止速度连接。");
                        }
                    foreach(var channel in owned)channel.Stop();
                    // Let the existing worker acknowledge stop before an immediate
                    // later speed request reconnects its already loaded component.
                    long stopDeadline=Environment.TickCount64+1000;
                    while(owned.Any(c=>c.Ready)&&Environment.TickCount64<stopDeadline)
                        await Task.Delay(20,linked.Token).ConfigureAwait(false);
                    string previous=Read(session).Diagnostics;
                    Clear(session,keepRoot:HasAuthorization(session));
                    var reset=new GameSpeedRuntimeStatus(1,false,false,"",""){Diagnostics=Retired(previous)};Publish(session,reset);return reset;
                }
                if(session.Root is {} pinned&&!platform.IsAlive(pinned))
                    throw new IOException("原游戏进程已退出或变化，速度连接已停止。请确认目标后重新设置。");
                var targets=await platform.DiscoverAsync(session.Game,linked.Token,session.Root).ConfigureAwait(false);
                if(session.Root is {} old&&old!=targets.Root)
                    throw new IOException("游戏进程已变化，速度连接已停止。请确认目标后重新设置。");
                var channels=await ConnectAsync(session,targets,linked.Token).ConfigureAwait(false);
                foreach(var channel in channels)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if(!platform.IsAlive(channel.Process))throw new IOException("游戏进程已变化，请重新设置速度。");
                    double actual=await SetMemberAsync(channel,rate,targets.Chromium,linked.Token).ConfigureAwait(false);
                    if(!double.IsFinite(actual)||Math.Abs(actual-rate)>1e-9)throw new IOException("游戏尚未确认所选速度，已恢复正常速度。");
                }
                await ConfirmRenderersAsync(targets,channels,linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                session.Root=targets.Root;session.LastDiscovery=Environment.TickCount64;
                var status=new GameSpeedRuntimeStatus(rate,true,false,"已检测到游戏计时调用；剧情和动画效果仍取决于游戏。",Key(targets.Root)){Diagnostics=Snapshot(session)};Publish(session,status);return status;
            },linked.Token).ConfigureAwait(false);
        }
        catch(Exception e)
        {
            if(entered)
            {
                lock(session.Sync)session.Authorization?.Cancel();
                string diagnostics=Snapshot(session);
                Clear(session);
                Publish(session,new(1,false,false,e is OperationCanceledException?"速度设置已取消，已请求恢复正常速度。":e.Message,""){Diagnostics=Retired(diagnostics)});
            }
            return Read(session);
        }
        finally{if(entered)session.Gate.Release();}
    }
    private async Task<IGameSpeedChannel[]> ConnectAsync(Session session,GameSpeedTargets targets,CancellationToken token)
    {
        if(targets.Processes.Count==0||targets.Processes.Count>64||!targets.Processes.Contains(targets.Root)||
            targets.Chromium&&(targets.Processes.Count<2||targets.Processes.Any(p=>!GameSpeedRuntimeTargets.SameImage(p,targets.Root))))
            throw new IOException("无法确认游戏的速度控制进程。");
        session.Chromium=targets.Chromium;
        var existing=Channels(session);var chosen=new List<IGameSpeedChannel>();
        foreach(var process in targets.Processes.Distinct())
        {
            token.ThrowIfCancellationRequested();
            if(!platform.IsAlive(process))throw new IOException("游戏进程已变化，请重新设置速度。");
            var channel=existing.FirstOrDefault(c=>c.Process==process&&c.Ready);
            if(channel is null)
            {
                channel=await platform.AttachAsync(process,token).ConfigureAwait(false);
                // Publish ownership immediately so a later attach/cancel failure
                // restores every partially attached process, not only the last one.
                lock(session.Sync)session.Channels=[..session.Channels,channel];
                token.ThrowIfCancellationRequested();
                if(channel.Process!=process)throw new IOException("速度组件返回的进程身份不匹配，已停止连接。");
            }
            chosen.Add(channel);
        }
        foreach(var obsolete in Channels(session).Except(chosen)){obsolete.Stop();obsolete.Dispose();}
        lock(session.Sync)session.Channels=chosen.ToArray();
        return chosen.ToArray();
    }
    private static Task<double> SetMemberAsync(IGameSpeedChannel channel,double rate,bool chromium,CancellationToken token)=>
        chromium?channel.SetAcknowledgedAsync(rate,token):channel.SetAsync(rate,token);
    private async Task ConfirmRenderersAsync(GameSpeedTargets targets,IGameSpeedChannel[] channels,CancellationToken token)
    {
        if(!targets.Chromium)
        {
            if(!platform.IsAlive(targets.Root)||channels.Any(c=>!platform.IsAlive(c.Process)||!c.Ready))
                throw new IOException("游戏进程或速度连接已变化，已请求恢复正常速度。");
            return;
        }
        var renderers=channels.Where(c=>c.Process!=targets.Root).ToArray();
        var clock=Stopwatch.StartNew();
        while(true)
        {
            token.ThrowIfCancellationRequested();
            if(!platform.IsAlive(targets.Root)||channels.Any(c=>!platform.IsAlive(c.Process)||!c.Ready))
                throw new IOException("游戏进程或速度连接已变化，已请求恢复正常速度。");
            // An active browser/root clock is not evidence that game logic in a
            // renderer uses the adjusted clock. An idle sibling renderer also
            // cannot veto the renderer actually running the game.
            if(renderers.Any(c=>c.ClockActivityObserved))return;
            if(clock.Elapsed>TimeSpan.FromSeconds(3))
                throw new IOException("已发送倍率，但未检测到游戏渲染进程调用可调速计时接口，已请求恢复 1×。");
            await Task.Delay(25,token).ConfigureAwait(false);
        }
    }
    private static string Snapshot(Session session)
    {
        var channels=Channels(session);if(channels.Length==0)return Read(session).Diagnostics;
        var lines=new List<string>{"使用方式：系统计时（实验）；只调整已授权游戏及已确认的渲染进程。",
            "验证范围：命令回执和设置后的游戏范围计时调用；实际剧情、动画速度未验证。",
            session.Chromium?"多进程判定：所有连接须确认倍率；至少一个游戏渲染进程须有新的计时调用，主进程可空闲。":"单进程判定：须有新的游戏范围计时调用。"};
        foreach(var channel in channels.Take(16))
        {
            string role=channel.Process==session.Root?(session.Chromium?"主进程（生命周期）":"游戏主进程"):"游戏渲染进程";
            string detail;
            try{detail=channel.Diagnostics;}catch(ObjectDisposedException){detail="连接已关闭";}catch(IOException){detail="诊断暂不可读";}
            lines.Add(role+" PID "+channel.Process.Pid);
            lines.Add(detail.Length==0?"暂无计时诊断":detail[..Math.Min(detail.Length,480)]);
        }
        if(channels.Length>16)lines.Add("已显示 16 / "+channels.Length+" 个已授权目标。");
        lines.Add("范围限制：游戏主程序及指定引擎模块；未覆盖的时钟、固定帧逻辑、语音或外部等待可能不随倍率变化。");
        string result=string.Join(Environment.NewLine,lines);return result[..Math.Min(result.Length,9000)];
    }
    private static string Retired(string diagnostics)=>diagnostics.Length==0?"":"最近一次诊断（速度连接已停止）："+Environment.NewLine+diagnostics.Replace("最近一次诊断（速度连接已停止）："+Environment.NewLine,"");
    private async Task MonitorAsync()
    {
        try
        {
            while(true)
            {
                await Task.Delay(monitorInterval,lifetime.Token).ConfigureAwait(false);
                Session[] current;lock(sync)current=sessions.Values.ToArray();
                // Renew all targets first; one slow process discovery must not let
                // another game's native watchdog expire.
                foreach(var session in current.Where(HasAuthorization))foreach(var channel in Channels(session))
                    try{channel.Heartbeat();}catch(ObjectDisposedException){}catch(IOException){}
                // Maintenance has its own nonblocking per-game gate. Do not await
                // it here: a slow attach must not delay the global heartbeat.
                foreach(var session in current)_=MaintainAsync(session);
            }
        }
        catch(OperationCanceledException) { }
    }
    private async Task MaintainAsync(Session session)
    {
        if(lifetime.IsCancellationRequested||!await session.Gate.WaitAsync(0).ConfigureAwait(false))return;
        try
        {
            var state=Read(session);if(session.Root is null||!HasAuthorization(session))return;
            CancellationToken authorization;lock(session.Sync)authorization=session.Authorization!.Token;
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(authorization,lifetime.Token);
            if(!platform.IsAlive(session.Root))
            {lock(session.Sync)session.Authorization?.Cancel();string diagnostics=Snapshot(session);Clear(session);Publish(session,new(1,false,false,"游戏已退出，速度已恢复为 1×。",""){Diagnostics=Retired(diagnostics)});return;}
            if(!state.Active)return;
            if(Channels(session).Any(c=>platform.IsAlive(c.Process)&&!c.Ready))
                throw new IOException("速度连接已断开，已请求恢复正常速度。");
            if(Environment.TickCount64-session.LastDiscovery<2000)return;
            session.LastDiscovery=Environment.TickCount64;
            var targets=await platform.DiscoverAsync(session.Game,linked.Token,session.Root).ConfigureAwait(false);
            if(targets.Root!=session.Root)throw new IOException("游戏进程已变化，请重新设置速度。");
            var old=Channels(session).ToHashSet();
            var channels=await ConnectAsync(session,targets,linked.Token).ConfigureAwait(false);
            foreach(var added in channels.Where(c=>!old.Contains(c)))
            {
                double actual=await SetMemberAsync(added,state.Rate,targets.Chromium,linked.Token).ConfigureAwait(false);
                if(!double.IsFinite(actual)||Math.Abs(actual-state.Rate)>1e-9)
                    throw new IOException("游戏尚未确认所选速度，已恢复正常速度。");
            }
            await ConfirmRenderersAsync(targets,channels,linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            // Refresh evidence when membership changes, without creating a UI
            // state change for every clock read or every timer poll.
            if(!old.SetEquals(channels))Publish(session,state with{Diagnostics=Snapshot(session)});
        }
        catch(Exception e)
        {lock(session.Sync)session.Authorization?.Cancel();string diagnostics=Snapshot(session);Clear(session);Publish(session,new(1,false,false,e is OperationCanceledException?"":e.Message,""){Diagnostics=Retired(diagnostics)});}
        finally{session.Gate.Release();}
    }
    private static string Key(GameSpeedProcess process)=>process.ExePath+"|"+process.Pid+"|"+process.Created;
    private static IGameSpeedChannel[] Channels(Session session){lock(session.Sync)return session.Channels;}
    private static GameSpeedRuntimeStatus Read(Session session){lock(session.Sync)return session.Status;}
    private void Publish(Session session,GameSpeedRuntimeStatus status)
    {
        bool notify;lock(session.Sync){notify=session.Status!=status;session.Status=status;}
        if(notify)try{Changed?.Invoke();}catch{ /* A closing view cannot terminate the safety heartbeat. */ }
    }
    private static void Clear(Session session,bool keepRoot=false)
    {
        IGameSpeedChannel[] owned;lock(session.Sync){owned=session.Channels;session.Channels=[];if(!keepRoot)session.Root=null;}
        foreach(var channel in owned){try{channel.Stop();}catch{}try{channel.Dispose();}catch{}}
    }
    public void Dispose()
    {
        Session[] current;lock(sync){if(disposed)return;disposed=true;current=sessions.Values.ToArray();}
        lifetime.Cancel();
        // Cancellation stops in-flight attaches. Cleanup waits their per-game gate
        // on a worker, while native owner/heartbeat checks remain the final fallback.
        _=Task.Run(async()=>
        {
            foreach(var session in current)
            {
                await session.Gate.WaitAsync().ConfigureAwait(false);
                try{Clear(session);Publish(session,new(1,false,false,"","") );}
                finally{session.Gate.Release();}
            }
        });
    }
}
