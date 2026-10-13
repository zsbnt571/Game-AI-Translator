using System.Diagnostics;
namespace ScreenshotTranslationUiTester;
public sealed partial class MainForm
{
    private readonly Dictionary<string,GameDataSession> _scriptTranslationSessions=new(StringComparer.OrdinalIgnoreCase);
    private GameDataSession? SelectedScriptTranslationSession()=>_selectedGame is {} game&&_scriptTranslationSessions.TryGetValue(game.ExePath,out var session)?session:null;
    private Task LaunchScriptTranslationSessionAsync(string action)=>!_gameDetecting&&_selectedGame is {} game?LaunchScriptTranslationSessionAsyncFor(game,action):Task.CompletedTask;
    private async Task ReconnectRenpyAsync(GameInfo game)
    {
        var session=_gameSessions.Get(game);if(session.Busy)return;
        session.Busy=true;session.Feedback="正在重新连接，游戏可继续运行…";RenderGameSessionState();
        RpgGameDataConnection? pipe=null;
        try
        {
            _fusion!.RequireGameReady(game.ExePath);
            if(_scriptTranslationSessions.Remove(game.ExePath,out var old))old.Dispose();
            pipe=new(game.ExePath);pipe.PrepareRenpyReconnect();
            await pipe.AcceptAsync(_desktopLifetime.Token);
            var settings=_fusion.GameSettings(UiSettings,game.ExePath);
            var data=new GameDataSession(pipe);_scriptTranslationSessions[game.ExePath]=data;
            data.Translation=new(pipe,game.ExePath,settings,async(text,profile,token)=>(await _translationService.TranslatePlainAsync(text,profile,token)).Text);
            await data.Translation.StartAsync();session.TranslationBlockedReason="";session.Feedback="已重新连接并继续翻译。";
        }
        catch(Exception error){pipe?.Dispose();session.Feedback=SafeDiagnosticOutput.ExceptionSummary(error);}
        finally{session.Busy=false;if(!IsDisposed){InvalidateGameState();RenderGameSessionState();}}
    }
    private async Task LaunchScriptTranslationSessionAsyncFor(GameInfo game,string action)
    {
        if(action is not ("plain" or "launch" or "modify" or "install" or "restore"))throw new ArgumentOutOfRangeException(nameof(action));
        var session=_gameSessions.Get(game);string root=RecentGameStore.Normalize(Path.GetDirectoryName(game.ExePath)!);
        if(session.Busy||session.LaunchPending||_gameRootOperations.ContainsKey(root))return;
        session.Busy=true;_gameRootOperations[root]=session;RenderGameSessionState();IGameDataConnection? connection=null;
        string engine="Ren’Py";
        try
        {
            ApiSettings? settings=null;
            string profileId=_fusion!.GameProfileId(game.ExePath),profileName=_fusion.Name(profileId);
            if(action=="launch"){_fusion!.RequireGameReady(game.ExePath);settings=_fusion.GameSettings(UiSettings,game.ExePath);}
            if(await Task.Run(()=>_gameLaunchOperations.ProcessState(game))!=FusionGameProcessState.Stopped)throw new InvalidOperationException("请先退出此游戏，再切换翻译启动方式。");
            if(_scriptTranslationSessions.Remove(game.ExePath,out var old))old.Dispose();
            if(action=="restore")await Task.Run(()=>{RenpyGameAdapter.Restore(game.ExePath);});
            else await Task.Run(()=>{RenpyGameAdapter.Install(game.ExePath);});
            if(action=="restore"){session.Feedback=engine+" 翻译组件已移出游戏，译文缓存保留。";return;}
            if(action=="install"){session.Feedback="翻译组件已准备，下次选择“翻译并启动”连接。";return;}
            if(await Task.Run(()=>_gameLaunchOperations.ProcessState(game))!=FusionGameProcessState.Stopped)throw new InvalidOperationException("游戏状态已改变，未重复启动。");
            var pipe=new RpgGameDataConnection(game.ExePath);
            connection=pipe;var data=new GameDataSession(connection);_scriptTranslationSessions[game.ExePath]=data;
            if(action=="launch")
            {
                session.Feedback="正在准备已有译文…";RenderGameSessionState();
                await Task.Run(()=>pipe.PrimeStartup(EmbeddedStartupCache.Select(
                    RpgTranslationCache.Effective(game.ExePath,settings!,EmbeddedTextSyntax.Renpy),settings!.SourceLanguage)),_desktopLifetime.Token);
            }
            using var process=Process.Start(pipe.StartInfo(action=="launch"))??throw new IOException("未能启动游戏。");
            session.Launched(action=="launch"?GameLaunchMode.Translation:action=="modify"?GameLaunchMode.Modification:GameLaunchMode.Ordinary,settings is null?null:profileId,settings is null?null:profileName,settings is null?null:FusionConfiguration.EditIdentity(settings));
            session.Feedback="正在等待 "+engine+" 连接…";RenderGameSessionState();await RecordGameStartedAsync(game,process.Id);
            // Ren'Py can replace its launcher with an authenticated runtime child.
            // Keep the process identity checks in the transport without treating
            // the launcher exiting as proof that the runtime has failed.
            await EmbeddedLaunchWait.AcceptAsync(pipe,process,_desktopLifetime.Token,progress=>
            {if(!IsDisposed&&!Disposing){session.Feedback=progress;RenderGameSessionState();}},observeLaunchedProcessExit:false);
            if(action is "modify" or "plain")
            {
                await connection.RequestAsync(new(){["op"]="translationDisable"});
                session.Feedback="";
                return;
            }
            data.Translation=new(connection,game.ExePath,settings!,async(text,profile,token)=>(await _translationService.TranslatePlainAsync(text,profile,token)).Text);
            await data.Translation.StartAsync();session.TranslationBlockedReason="";session.Feedback="";
        }
        catch(Exception ex)
        {
            connection?.Dispose();session.Feedback=ex is OperationCanceledException?"未收到翻译连接；游戏保持原样运行。":SafeDiagnosticOutput.ExceptionSummary(ex);
        }
        finally{session.Busy=false;_gameRootOperations.Remove(root);if(!IsDisposed){InvalidateGameState();RenderGameSessionState();}}
    }
}
