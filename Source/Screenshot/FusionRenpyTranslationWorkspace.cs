using System.Diagnostics;
namespace ScreenshotTranslationUiTester;
public sealed partial class MainForm
{
    private readonly Dictionary<string,GameDataSession> _scriptTranslationSessions=new(StringComparer.OrdinalIgnoreCase);
    private GameDataSession? SelectedScriptTranslationSession()=>_selectedGame is {} game&&_scriptTranslationSessions.TryGetValue(game.ExePath,out var session)?session:null;
    private Task LaunchScriptTranslationSessionAsync(string action)=>!_gameDetecting&&_selectedGame is {} game?LaunchScriptTranslationSessionAsyncFor(game,action):Task.CompletedTask;
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
            using var process=Process.Start(pipe.StartInfo(action=="launch"))??throw new IOException("未能启动游戏。");
            session.Launched(action=="launch"?GameLaunchMode.Translation:action=="modify"?GameLaunchMode.Modification:GameLaunchMode.Ordinary,settings is null?null:profileId,settings is null?null:profileName,settings is null?null:FusionConfiguration.EditIdentity(settings));
            session.Feedback="正在等待 "+engine+" 连接…";RenderGameSessionState();await RecordGameStartedAsync(game,process.Id);
            await pipe.AcceptAsync();
            if(action is "modify" or "plain")
            {
                await connection.RequestAsync(new(){["op"]="translationDisable"});
                session.Feedback="";
                return;
            }
            data.Translation=new(connection,game.ExePath,settings!,async(text,profile,token)=>(await _translationService.TranslatePlainAsync(text,profile,token)).Text);
            await data.Translation.StartAsync();session.Feedback="";
        }
        catch(Exception ex)
        {
            connection?.Dispose();session.Feedback=ex is OperationCanceledException?"未收到翻译连接；游戏保持原样运行。":SafeDiagnosticOutput.ExceptionSummary(ex);
        }
        finally{session.Busy=false;_gameRootOperations.Remove(root);if(!IsDisposed){InvalidateGameState();RenderGameSessionState();}}
    }
}
