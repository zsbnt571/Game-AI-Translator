namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private sealed class ModificationAuthorization(int revision,IGameDataConnection? connection)
    {
        internal readonly int Revision=revision;
        internal readonly IGameDataConnection? Connection=connection;
        internal readonly CancellationTokenSource Cancellation=new();
        internal bool Generic,RuntimeBound;
    }
    private readonly Dictionary<string,ModificationAuthorization> _modificationAuthorizations=new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _commonModificationBusy=new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _modificationRevoking=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,Task> _modificationRevocations=new(StringComparer.OrdinalIgnoreCase);
    private Button? _commonModificationToggle;
    private Label? _commonModificationStatus;

    private static bool HasDataModification(GameInfo game)=>game.AdapterId is "rpg-maker-mv" or "rpg-maker-mz" or "renpy"||NovelModification(game);
    private GameDataSession? ModificationSession(GameInfo game)=>game.AdapterId is "rpg-maker-mv" or "rpg-maker-mz"?_dataSessions.GetValueOrDefault(game.ExePath):
        game.AdapterId=="renpy"?_scriptTranslationSessions.GetValueOrDefault(game.ExePath):NovelModification(game)?_embeddedTranslationSessions.GetValueOrDefault(game.ExePath):null;

    // Local authorization only. Rendering never discovers or attaches a process.
    private bool ModificationAuthorized(GameInfo game)
    {
        if(_modificationRevoking.Contains(game.ExePath))return false;
        if(HasDataModification(game))return ModificationSession(game) is {Enabled:true,Connection.Connected:true}
            &&(!_modificationAuthorizations.TryGetValue(game.ExePath,out var dataGrant)||!dataGrant.RuntimeBound||_independentSpeedRuntime?.Authorized(game.ExePath)==true);
        return _modificationAuthorizations.TryGetValue(game.ExePath,out var grant)&&grant.Generic&&!grant.Cancellation.IsCancellationRequested
            &&grant.Revision==_gameSessions.Get(game).Revision&&(grant.Connection is not null
                ?grant.Connection.Connected&&ReferenceEquals(_embeddedTranslationSessions.GetValueOrDefault(game.ExePath)?.Connection,grant.Connection)
                :_independentSpeedRuntime?.Authorized(game.ExePath)==true);
    }
    private ModificationAuthorization? SpeedAuthorization(GameInfo game)
    {
        if(!ModificationAuthorized(game))return null;
        var connection=ModificationSession(game)?.Connection??(EngineTimeSpeed(game)?IndependentSpeedConnection(game)?.Connection:null);int revision=_gameSessions.Get(game).Revision;
        if(_modificationAuthorizations.TryGetValue(game.ExePath,out var old))
        {
            if(old.Revision==revision&&ReferenceEquals(old.Connection,connection)&&!old.Cancellation.IsCancellationRequested)return old;
            _=RevokeModificationSpeedAsync(game);return null;
        }
        var grant=new ModificationAuthorization(revision,connection);_modificationAuthorizations[game.ExePath]=grant;return grant;
    }
    private bool CurrentSpeedAuthorization(GameInfo game,ModificationAuthorization grant)=>ModificationAuthorized(game)
        &&_modificationAuthorizations.TryGetValue(game.ExePath,out var current)&&ReferenceEquals(current,grant)
        &&grant.Revision==_gameSessions.Get(game).Revision&&!grant.Cancellation.IsCancellationRequested
        &&(!HasDataModification(game)||ReferenceEquals(ModificationSession(game)?.Connection,grant.Connection));

    internal DesktopActionState NativeCommonModificationAction
    {
        get
        {
            var game=_selectedGame;bool current=game is not null&&SameGame(game.ExePath,_desktopGameState?.Game.ExePath);
            bool running=current&&_desktopGameState?.Process==FusionGameProcessState.Running;
            bool stopped=current&&_desktopGameState?.Process==FusionGameProcessState.Stopped;
            bool active=game is not null&&ModificationAuthorized(game);
            bool busy=game is null||_gameDetecting||_gameSessions.Get(game).Busy||_gameSessions.Get(game).LaunchPending
                ||_commonModificationBusy.Contains(game.ExePath)||_modificationRevoking.Contains(game.ExePath);
            return new(active?"关闭修改":stopped?"开启修改并启动":"开启修改")
            {Text=active?"关闭修改":stopped?"开启修改并启动":"开启修改",Enabled=!busy&&(running||stopped),Primary=true};
        }
    }
    internal string NativeCommonModificationStatus=>_selectedGame is {} game&&ModificationAuthorized(game)
        ?"修改已开启 · 翻译可独立开关":"修改未开启。开启后才能调整游戏速度。";

    private void RenderCommonModificationAuthorization()
    {
        if(_selectedGame is {} game&&SameGame(game.ExePath,_desktopGameState?.Game.ExePath)
            &&_desktopGameState?.Process==FusionGameProcessState.Stopped&&!_gameSessions.Get(game).LaunchPending
            &&_modificationAuthorizations.ContainsKey(game.ExePath))_=RevokeModificationSpeedAsync(game);
        if(_commonModificationToggle is null)return;
        var state=NativeCommonModificationAction;_commonModificationToggle.Text=state.Text;_commonModificationToggle.Enabled=state.Enabled;
        if(_commonModificationStatus is not null)_commonModificationStatus.Text=NativeCommonModificationStatus;
    }
    private async Task ToggleCommonModificationAsync()
    {
        if(_selectedGame is not {} game||HasDataModification(game)||!NativeCommonModificationAction.Enabled||!_commonModificationBusy.Add(game.ExePath))return;
        var workspace=_gameSessions.Get(game);
        try
        {
            if(ModificationAuthorized(game)){await RevokeModificationSpeedCoreAsync(game,true);workspace.Feedback="修改已关闭，游戏速度已请求恢复为 1×。";return;}
            if(_desktopGameState?.Process==FusionGameProcessState.Stopped)
            {
                int priorRevision=workspace.Revision;
                if(EngineTimeSpeed(game))
                {
                    await LaunchEmbeddedTranslationSessionAsyncFor(game,"speed");
                    if(workspace.LaunchMode!=GameLaunchMode.Modification||_embeddedTranslationSessions.GetValueOrDefault(game.ExePath)?.Connection.Connected!=true)return;
                }
                else await LaunchOrdinaryGameSessionAsyncFor(game,true);
                // A failed launch must not leave a grant for a later unrelated process.
                if(!EngineTimeSpeed(game)&&(workspace.Revision==priorRevision||workspace.LaunchMode!=GameLaunchMode.Ordinary||workspace.TranslationBlockedReason.Length>0))return;
            }
            await RevokeModificationSpeedAsync(game);
            var engine=EngineTimeSpeed(game)?_embeddedTranslationSessions.GetValueOrDefault(game.ExePath)?.Connection:null;
            if(engine?.Connected!=true)engine=null;
            if(engine is null&&RequiresRpgSpeedBridge(game))
                throw new IOException("当前游戏没有连接专用速度组件。请保存并退出游戏，再点击开启修改并启动。");
            var grant=new ModificationAuthorization(workspace.Revision,engine);_modificationAuthorizations[game.ExePath]=grant;
            if(engine is not null)
            {
                var enableRequest=new System.Text.Json.Nodes.JsonObject{["op"]="speedEnable"};
                if(game.AdapterId=="rgss")enableRequest["activateForSpeed"]=true;
                var result=await engine.RequestAsync(enableRequest,grant.Cancellation.Token);
                grant.Cancellation.Token.ThrowIfCancellationRequested();
                if(grant.Revision!=workspace.Revision||!_modificationAuthorizations.TryGetValue(game.ExePath,out var current)||!ReferenceEquals(current,grant))
                    throw new OperationCanceledException("游戏会话已变化，未开启速度控制。");
                if(result.ValueKind!=System.Text.Json.JsonValueKind.Object)
                    throw new IOException("当前游戏组件未返回有效的速度能力确认。");
                // Only a positive, no-side-effect unsupported reply can select
                // the generic backend. A timeout, error or partial write must
                // never silently attach a second speed component.
                if(game.AdapterId=="web-construct"&&result.TryGetProperty("supported",out var supported)&&supported.ValueKind==System.Text.Json.JsonValueKind.False&&
                    result.TryGetProperty("enabled",out var inactive)&&inactive.ValueKind==System.Text.Json.JsonValueKind.False&&
                    result.TryGetProperty("fallbackSafe",out var safe)&&safe.ValueKind==System.Text.Json.JsonValueKind.True)
                {
                    grant.Cancellation.Cancel();
                    grant=new ModificationAuthorization(workspace.Revision,null);_modificationAuthorizations[game.ExePath]=grant;
                    engine=null;
                    await IndependentSpeedRuntime().AuthorizeAsync(game,grant.Cancellation.Token);
                    _independentSpeedMessages[game.ExePath]="此游戏未提供可用的引擎倍率；将使用通用计时，效果取决于游戏。";
                }
                else
                {
                    if(result.TryGetProperty("error",out var error)&&error.ValueKind==System.Text.Json.JsonValueKind.String)
                        throw new IOException(error.GetString());
                    if(!result.TryGetProperty("enabled",out var enabled)||enabled.ValueKind!=System.Text.Json.JsonValueKind.True)
                        throw new IOException("当前游戏组件尚不支持引擎加速，请保存后退出并重新开启修改。");
                }
            }
            else await IndependentSpeedRuntime().AuthorizeAsync(game,grant.Cancellation.Token);
            if(grant.Revision!=workspace.Revision||grant.Cancellation.IsCancellationRequested)
            {await RevokeModificationSpeedAsync(game);return;}
            grant.Generic=true;grant.RuntimeBound=engine is null;workspace.Feedback="修改已开启 · 翻译可独立开关";
        }
        catch(Exception ex){await RevokeModificationSpeedAsync(game);workspace.Feedback=SafeDiagnosticOutput.ExceptionSummary(ex);}
        finally{_commonModificationBusy.Remove(game.ExePath);if(!IsDisposed&&!Disposing)RenderGameSessionState();}
    }

    // Call before sending dataDisable: revoke locally even if that request fails.
    // Only this game's owned speed channels are reset; translation is untouched.
    private Task RevokeModificationSpeedAsync(GameInfo game)=>RevokeModificationSpeedCoreAsync(game,false);
    private Task RevokeModificationSpeedCoreAsync(GameInfo game,bool activateForSpeed)
    {
        if(_modificationRevocations.TryGetValue(game.ExePath,out var pending))return pending;
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _modificationRevocations[game.ExePath]=completion.Task;_modificationRevoking.Add(game.ExePath);
        if(_modificationAuthorizations.Remove(game.ExePath,out var grant))grant.Cancellation.Cancel();
        var connection=grant?.Connection??IndependentSpeedConnection(game)?.Connection??ModificationSession(game)?.Connection;
        IndependentSpeedValue? saved=null;
        if(connection is not null&&_independentEngineSpeeds.TryGetValue(connection,out saved))
        {saved.Authorized=false;saved.Generation++;}
        var nativeStop=_independentSpeedRuntime?.RevokeAsync(game.ExePath)??Task.CompletedTask;
        _=Finish();return completion.Task;
        async Task Finish()
        {
        try
        {
            if(saved is not null&&connection is not null)
            {
                await saved.Gate.WaitAsync();
                try
                {
                    if(saved.Touched&&connection.Connected)
                    {
                        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        var restoreRequest=new System.Text.Json.Nodes.JsonObject{["op"]="gameSpeed",["rate"]=1d};
                        if(activateForSpeed&&game.AdapterId=="rgss")restoreRequest["activateForSpeed"]=true;
                        var result=await connection.RequestAsync(restoreRequest,timeout.Token);
                        if(AcceptedGameSpeed(result,IntegerGameSpeed(game))!=1)throw new IOException("未确认恢复正常游戏速度。");
                    }
                    saved.Rate=1;saved.Touched=false;saved.Unavailable=false;saved.Confirmed=false;saved.HasAcknowledgement=false;saved.StatusCurrent=false;saved.Status=EngineSpeedAcknowledgement.Legacy;saved.LastLogState="";
                }
                catch(Exception ex){_independentSpeedMessages[game.ExePath]=SafeDiagnosticOutput.ExceptionSummary(ex);}
                finally{saved.Gate.Release();}
            }
            if(EngineTimeSpeed(game)&&grant?.Connection is {} engine&&engine.Connected)
            {
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var disableRequest=new System.Text.Json.Nodes.JsonObject{["op"]="speedDisable"};
                if(activateForSpeed&&game.AdapterId=="rgss")disableRequest["activateForSpeed"]=true;
                var result=await engine.RequestAsync(disableRequest,timeout.Token);
                if(AcceptedGameSpeed(result,false)!=1)throw new IOException("未确认关闭引擎加速。");
            }
            await nativeStop;
        }
        catch(Exception ex){_independentSpeedMessages[game.ExePath]=SafeDiagnosticOutput.ExceptionSummary(ex);}
        finally
        {
            _modificationRevoking.Remove(game.ExePath);_modificationRevocations.Remove(game.ExePath);
            completion.TrySetResult();if(!IsDisposed&&!Disposing)NotifyNativeGameDetailChanged();
        }
        }
    }
}
