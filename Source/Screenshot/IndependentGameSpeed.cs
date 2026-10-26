using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// UI-thread routing. Process discovery and attach live in GameSpeedRuntime;
// existing WOLF/RPG connections use their own clock, never a second clock hook.
public sealed partial class MainForm
{
    private sealed class IndependentSpeedValue
    {
        internal double Rate=1;
        internal DateTime NextPoll;
        internal bool Polling,Unavailable,Authorized,Confirmed;
        internal bool Touched,HasAcknowledgement,StatusCurrent;
        internal EngineSpeedAcknowledgement Status=EngineSpeedAcknowledgement.Legacy;
        internal string LastLogState="";
        internal readonly SemaphoreSlim Gate=new(1,1);
        internal long Generation;
    }
    // A requested multiplier can remain acknowledged while the game owns its
    // clock. Neither field is evidence of measured story or animation speed.
    private sealed record EngineSpeedAcknowledgement(string State,bool? Applied,double? EngineScale,double? BaselineScale,string Reason="")
    {
        internal static readonly EngineSpeedAcknowledgement Legacy=new("",null,null,null);
        internal bool Confirmed=>State is "" or "active";
        internal bool Unavailable=>State is "unknown" or "unavailable";
        internal static EngineSpeedAcknowledgement Read(JsonElement reply,double rate)
        {
            bool hasState=reply.TryGetProperty("speedState",out var state),hasApplied=reply.TryGetProperty("speedApplied",out var applied);
            if(!hasState&&!hasApplied)return Legacy;
            bool? appliedValue=applied.ValueKind is JsonValueKind.True or JsonValueKind.False?applied.GetBoolean():null;
            string value=state.ValueKind==JsonValueKind.String?state.GetString()??"unknown":"unknown";
            bool valid=value switch{"active"=>appliedValue==true,"idle"=>appliedValue==false&&rate==1,"paused" or "game-controlled" or "unavailable"=>appliedValue==false,_=>false};
            string explanation=reply.TryGetProperty("reason",out var reason)&&reason.ValueKind==JsonValueKind.String?reason.GetString()??"":"";
            if(explanation.Length>2048)explanation=explanation[..2048];
            return new(valid?value:"unknown",appliedValue,Metric(reply,"engineScale"),Metric(reply,"baselineScale"),explanation);
        }
        private static double? Metric(JsonElement reply,string key)=>reply.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.Number&&value.TryGetDouble(out double number)&&double.IsFinite(number)&&number>=0?number:null;
        internal string Feedback(double rate,bool current)
        {
            string multiplier=rate.ToString("0.##",System.Globalization.CultureInfo.InvariantCulture);
            if(!current||Unavailable)return "已保留 "+multiplier+"× 设置；当前状态尚未确认，可再次应用。";
            return State switch
            {
                "paused"=>"已设置 "+multiplier+"×；游戏暂停，恢复后继续。",
                "game-controlled"=>"游戏临时控制速度，已保留 "+multiplier+"× 设置。",
                "idle"=>"已恢复 1×。",
                _=>""
            };
        }
        internal string Diagnostics=>State.Length==0?"":"\nspeedState="+State+"\nspeedApplied="+(Applied?.ToString().ToLowerInvariant()??"unknown")+
            (EngineScale is {} scale?"\nengineScale="+scale.ToString("0.#####",System.Globalization.CultureInfo.InvariantCulture):"")+
            (BaselineScale is {} baseline?"\nbaselineScale="+baseline.ToString("0.#####",System.Globalization.CultureInfo.InvariantCulture):"");
    }
    private readonly ConditionalWeakTable<IGameDataConnection,IndependentSpeedValue> _independentEngineSpeeds=new();
    private readonly HashSet<string> _independentSpeedBusy=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,string> _independentSpeedMessages=new(StringComparer.OrdinalIgnoreCase);
    private GameSpeedRuntime? _independentSpeedRuntime;

    // These adapters require an explicit capability acknowledgement on the
    // current game's connection. Merely seeing a running process is not enough.
    private static bool EngineTimeSpeed(GameInfo game)=>game.AdapterId is "godot3" or "godot4" or "unity-mono" or "unity-il2cpp" or "web-construct" or "rgss" or "web-rpg";
    private static bool IntegerGameSpeed(GameInfo game)=>game.AdapterId is "rpg-maker-mv" or "rpg-maker-mz" or "web-rpg" or "rgss";
    private static bool RequiresRpgSpeedBridge(GameInfo game)=>game.AdapterId is "rpg-maker-mv" or "rpg-maker-mz" or "web-rpg" or "rgss" or "wolf";
    private GameDataSession? IndependentSpeedConnection(GameInfo game)
    {
        if(_independentSpeedRuntime?.Status(game.ExePath).Active==true)return null;
        if(EngineTimeSpeed(game))
        {
            if(!_modificationAuthorizations.TryGetValue(game.ExePath,out var grant)||grant.Connection is null)return null;
            var bound=_embeddedTranslationSessions.GetValueOrDefault(game.ExePath);
            return bound?.Connection.Connected==true&&ReferenceEquals(bound.Connection,grant.Connection)?bound:null;
        }
        var session=game.AdapterId=="wolf"?_embeddedTranslationSessions.GetValueOrDefault(game.ExePath):
            game.AdapterId is "rpg-maker-mv" or "rpg-maker-mz"?_dataSessions.GetValueOrDefault(game.ExePath):null;
        return session?.Connection.Connected==true?session:null;
    }

    internal ModificationSpeedState NativeGameSpeedState
    {
        get
        {
            if(_selectedGame is not {} game)return ModificationSpeedState.Unavailable;
            bool running=SameGame(game.ExePath,_desktopGameState?.Game.ExePath)&&_desktopGameState?.Process==FusionGameProcessState.Running;
            bool authorized=ModificationAuthorized(game);
            var connection=IndependentSpeedConnection(game)?.Connection;
            var native=_independentSpeedRuntime?.Status(game.ExePath);
            var saved=connection is null?null:_independentEngineSpeeds.GetValue(connection,_=>new());
            bool integer=IntegerGameSpeed(game);
            bool busy=_gameActionRunning||_gameDetecting||_independentSpeedBusy.Contains(game.ExePath)||native?.Busy==true;
            double rate=running?(saved?.Rate??native?.Rate??1):1;
            // A transport can appear during the first speed request. That is not
            // a new UI session and must not discard the in-flight preview.
            string context=game.ExePath+"|"+_gameSessions.Get(game).Revision+"|"+ModificationSpeedState.ContextFor(connection);
            string reason=!authorized?(!string.IsNullOrEmpty(native?.Message)?native.Message:"请先开启修改，再调整游戏速度。") :!running?"正在等待游戏启动…":
                _independentSpeedMessages.GetValueOrDefault(game.ExePath)??(connection is null?native?.Message:null)??"";
            if(authorized&&running&&reason.Length==0&&saved is {StatusCurrent:true})reason=saved.Status.Reason;
            if(busy)reason=game.AdapterId=="rgss"?"正在等待游戏确认速度；如未切回，请从任务栏返回游戏。":"正在应用游戏速度…";
            if(running&&!busy&&reason.Length==0)reason=connection is null?
                RequiresRpgSpeedBridge(game)?"当前游戏未连接专用速度组件，请保存并退出后点击开启修改并启动。":EngineTimeSpeed(game)?"当前未连接引擎速度控制；调整时将尝试通用计时。":"通用加速（实验）；效果取决于游戏。点击恢复 1× 返回正常速度。":
                integer?"加快游戏逻辑推进；高倍率受游戏负载限制，不加速音频。恢复 1× 返回原有速度。":EngineTimeSpeed(game)?"引擎时间倍率；不加速音频和使用真实时间的剧情。恢复 1× 会还原修改前的倍率。":"用于加快剧情和等待；点击恢复 1× 返回正常速度。";
            string backend=integer?"方式：逻辑步进":connection is not null&&EngineTimeSpeed(game)?"方式：引擎倍率":"方式：通用计时";
            bool confirmed=running&&authorized&&(saved is {Confirmed:true,Unavailable:false}||native?.Active==true);
            string feedback=running&&authorized&&saved is {HasAcknowledgement:true}?saved.Status.Feedback(rate,saved.StatusCurrent):"";
            string evidence=connection is null?native?.Diagnostics??"":
                "已连接当前游戏的速度控制。\n"+(saved is {Confirmed:true,Unavailable:false}?"已确认倍率："+rate.ToString("0.##",System.Globalization.CultureInfo.InvariantCulture)+"×":
                    saved is {HasAcknowledgement:true}?"已保留倍率："+rate.ToString("0.##",System.Globalization.CultureInfo.InvariantCulture)+"×":"尚未收到倍率确认。")+
                "\n仅确认设置，不代表剧情或动画已按该倍率推进。"+(saved?.Status.Diagnostics??"");
            string diagnostics=backend+"\n"+(evidence.Length>0?evidence:!authorized?"修改未开启；未建立新的速度连接。":"尚未建立速度控制连接。")+
                (reason.Length>0?"\n"+reason:"");
            return new(true,authorized&&running&&!busy,busy,rate,integer?1:.25,100,integer?1:.25,
                integer?[1,2,3,4,10,25,50,100]:[.25,.5,1,2,4,10,25,50,100],reason,context,integer)
                {BackendLabel=backend,RateConfirmed=confirmed,Feedback=feedback,Diagnostics=diagnostics};
        }
    }

    private GameSpeedRuntime IndependentSpeedRuntime()
    {
        if(_independentSpeedRuntime is not null)return _independentSpeedRuntime;
        _independentSpeedRuntime=new();
        _independentSpeedRuntime.Changed+=()=>
        {
            if(IsDisposed||Disposing)return;
            try{BeginInvoke(new Action(()=>{if(!IsDisposed&&!Disposing)NotifyNativeGameDetailChanged();}));}
            catch(InvalidOperationException){}
        };
        return _independentSpeedRuntime;
    }

    private static double AcceptedGameSpeed(JsonElement result,bool integer)
    {
        if(result.ValueKind==JsonValueKind.Object&&result.TryGetProperty("error",out var error)&&error.ValueKind==JsonValueKind.String)
            throw new IOException(error.GetString());
        if(result.ValueKind!=JsonValueKind.Object||!result.TryGetProperty("speed",out var speed)||
            speed.ValueKind!=JsonValueKind.Number||!speed.TryGetDouble(out double value)||!double.IsFinite(value)||
            value<(integer?1:.25)||value>100||integer&&value!=Math.Truncate(value))
            throw new IOException("未确认游戏速度。请保存并退出游戏，再从新版启动后重试。");
        return value;
    }

    private void LogIndependentSpeed(GameInfo game,IGameDataConnection? connection,IndependentSpeedValue? saved,string state,double requested,EngineSpeedAcknowledgement? status=null,string? error=null)
    {
        string key=state+"|"+requested.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+"|"+status?.State+"|"+status?.Applied;
        if(saved is not null){if(saved.LastLogState==key)return;saved.LastLogState=key;}
        string identity=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(RecentGameStore.Normalize(game.ExePath))));
        AppLog.Write("game-speed",JsonSerializer.Serialize(new{game=identity,adapter=game.AdapterId,revision=_gameSessions.Get(game).Revision,
            connection=ModificationSpeedState.ContextFor(connection),state,requested,engineScale=status?.EngineScale,baselineScale=status?.BaselineScale,error}));
    }

    private async Task SetIndependentGameSpeedAsync(double rate)
    {
        if(_selectedGame is not {} game||!NativeGameSpeedState.Enabled||!NativeGameSpeedState.Accepts(rate))return;
        var authorization=SpeedAuthorization(game);if(authorization is null)return;
        if(!_independentSpeedBusy.Add(game.ExePath))return;
        _independentSpeedMessages.Remove(game.ExePath);NotifyNativeGameDetailChanged();
        try
        {
            var session=IndependentSpeedConnection(game);
            if(session is not null)
            {
                var connection=session.Connection;
                var saved=_independentEngineSpeeds.GetValue(connection,_=>new());
                if(rate==1&&!saved.Authorized&&!saved.Touched&&!saved.Unavailable&&(saved.Status.State is "" or "idle"))return;
                await saved.Gate.WaitAsync(authorization.Cancellation.Token);
                try
                {
                    if(!CurrentSpeedAuthorization(game,authorization))return;
                    saved.Generation++;saved.Touched=true;saved.Confirmed=false;saved.StatusCurrent=false;
                    LogIndependentSpeed(game,connection,saved,"request",rate);
                    if(game.AdapterId is "rgss" or "web-rpg")
                    {
                        // These RPG bridges expire their leases while suspended.
                        // Only a new user action may renew that lease. Native
                        // RGSS additionally needs its owned window foreground.
                        var enableRequest=new System.Text.Json.Nodes.JsonObject{["op"]="speedEnable"};
                        if(game.AdapterId=="rgss")enableRequest["activateForSpeed"]=true;
                        var enabled=await connection.RequestAsync(enableRequest,authorization.Cancellation.Token);
                        if(enabled.TryGetProperty("error",out var error)&&error.ValueKind==JsonValueKind.String)throw new IOException(error.GetString());
                        if(!enabled.TryGetProperty("enabled",out var active)||active.ValueKind!=JsonValueKind.True)
                            throw new IOException("当前游戏组件未返回有效的速度能力确认。");
                        if(!CurrentSpeedAuthorization(game,authorization))return;
                    }
                    var request=new System.Text.Json.Nodes.JsonObject{["op"]="gameSpeed",["rate"]=rate};
                    if(game.AdapterId=="rgss")request["activateForSpeed"]=true;
                    var result=await connection.RequestAsync(request,authorization.Cancellation.Token);
                    double actual=AcceptedGameSpeed(result,IntegerGameSpeed(game));
                    if(CurrentSpeedAuthorization(game,authorization)&&connection.Connected&&ReferenceEquals(IndependentSpeedConnection(game),session))
                    {
                        var status=EngineSpeedAcknowledgement.Read(result,actual);
                        saved.Rate=actual;saved.Authorized=actual!=1;saved.Touched=actual!=1;saved.Unavailable=status.Unavailable;saved.Confirmed=status.Confirmed;saved.HasAcknowledgement=true;saved.StatusCurrent=true;
                        saved.Status=status;saved.NextPoll=DateTime.UtcNow.AddSeconds(2);
                        LogIndependentSpeed(game,connection,saved,status.State.Length>0?status.State:"confirmed",actual,status);
                        if(game.AdapterId=="wolf")_novelSpeeds.GetValue(connection,_=>new()).Value=actual;
                    }
                }
                finally{saved.Gate.Release();}
            }
            else if(rate!=1||_independentSpeedRuntime is not null)
            {
                // Do not replace a missing/stale RPG bridge with process-wide
                // clocks. In NW.js this can desynchronize rAF from game time.
                if(RequiresRpgSpeedBridge(game))throw new IOException("专用速度连接不可用。请保存并退出游戏，再点击开启修改并启动。");
                var runtime=IndependentSpeedRuntime();
                if(!runtime.Authorized(game.ExePath))
                {
                    if(authorization.RuntimeBound)throw new IOException("游戏进程已变化，请重新开启修改。");
                    await runtime.AuthorizeAsync(game,authorization.Cancellation.Token);authorization.RuntimeBound=true;
                }
                if(CurrentSpeedAuthorization(game,authorization))await runtime.SetAsync(game,rate,authorization.Cancellation.Token);
            }
        }
        catch(Exception ex)
        {
            if(CurrentSpeedAuthorization(game,authorization))_independentSpeedMessages[game.ExePath]="速度未应用："+SafeDiagnosticOutput.ExceptionSummary(ex);
            LogIndependentSpeed(game,IndependentSpeedConnection(game)?.Connection,null,"request-failed",rate,error:SafeDiagnosticOutput.ExceptionSummary(ex));
        }
        finally{_independentSpeedBusy.Remove(game.ExePath);if(!IsDisposed&&!Disposing)NotifyNativeGameDetailChanged();}
    }

    private async Task PollIndependentGameSpeedAsync()
    {
        // Authorized background games need their own real-time heartbeat too.
        // Selecting a different library item must not expire or retarget a grant.
        var games=_gameSessions.Snapshot().Select(s=>s.Game).Where(g=>_modificationAuthorizations.ContainsKey(g.ExePath))
            .Concat(_selectedGame is {} selected?[selected]:Array.Empty<GameInfo>()).DistinctBy(g=>g.ExePath,StringComparer.OrdinalIgnoreCase).ToArray();
        // A background RGSS window may pause its update loop. Its pending
        // receipt must not delay the heartbeat of another authorized game.
        await Task.WhenAll(games.Select(PollGameSpeedAsync));
    }

    private async Task PollGameSpeedAsync(GameInfo game)
    {
        if(_independentSpeedBusy.Contains(game.ExePath)||IndependentSpeedConnection(game) is not {} session)return;
        if(!ModificationAuthorized(game)||!_modificationAuthorizations.TryGetValue(game.ExePath,out var authorization)||!CurrentSpeedAuthorization(game,authorization))return;
        var connection=session.Connection;var saved=_independentEngineSpeeds.GetValue(connection,_=>new());
        if((!saved.Authorized&&!EngineTimeSpeed(game))||saved.Polling||DateTime.UtcNow<saved.NextPoll)return;
        saved.Polling=true;saved.NextPoll=DateTime.UtcNow.AddSeconds(2);
        long generation=saved.Generation;
        bool entered=false;
        try
        {
            await saved.Gate.WaitAsync(authorization.Cancellation.Token);entered=true;
            if(!CurrentSpeedAuthorization(game,authorization))return;
            // A heartbeat must neither wait through an inactive game nor steal
            // focus. A later poll can confirm the same bound connection again.
            var result=await connection.RequestAsync(new(){["op"]="gameSpeed",["deferIfPaused"]=true},authorization.Cancellation.Token);
            double rate=AcceptedGameSpeed(result,IntegerGameSpeed(game));
            if(CurrentSpeedAuthorization(game,authorization)&&generation==saved.Generation&&connection.Connected&&ReferenceEquals(session,IndependentSpeedConnection(game)))
            {
                var status=EngineSpeedAcknowledgement.Read(result,rate);
                bool changed=Math.Abs(saved.Rate-rate)>.001||saved.Unavailable!=status.Unavailable||saved.Confirmed!=status.Confirmed||saved.Status!=status||!saved.StatusCurrent;
                saved.Rate=rate;saved.Authorized=rate!=1;saved.Unavailable=status.Unavailable;saved.Confirmed=status.Confirmed;saved.HasAcknowledgement=true;saved.StatusCurrent=true;saved.Status=status;
                LogIndependentSpeed(game,connection,saved,status.State.Length>0?status.State:"confirmed",rate,status);
                changed|=_independentSpeedMessages.Remove(game.ExePath);
                if(changed&&!IsDisposed&&!Disposing)NotifyNativeGameDetailChanged();
            }
        }
        catch(Exception ex)
        {
            if(generation==saved.Generation&&CurrentSpeedAuthorization(game,authorization))
            {
                bool paused=ex is UnityMainThreadPausedException or UnityMainThreadPausedTimeoutException;
                string message=paused?"游戏在后台暂停；返回游戏后会继续确认速度。":"速度确认失败："+SafeDiagnosticOutput.ExceptionSummary(ex);
                bool changed=saved.Confirmed||saved.Unavailable!=!paused||_independentSpeedMessages.GetValueOrDefault(game.ExePath)!=message;
                saved.Unavailable=!paused;saved.Confirmed=false;saved.StatusCurrent=false;saved.NextPoll=DateTime.UtcNow.AddSeconds(paused?2:5);
                _independentSpeedMessages[game.ExePath]=message;
                LogIndependentSpeed(game,connection,saved,paused?"paused-unconfirmed":"query-failed",saved.Rate,error:SafeDiagnosticOutput.ExceptionSummary(ex));
                if(changed&&!IsDisposed&&!Disposing)NotifyNativeGameDetailChanged();
            }
        }
        finally{if(entered)saved.Gate.Release();saved.Polling=false;}
    }
}
