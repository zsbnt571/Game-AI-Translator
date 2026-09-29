using System.Diagnostics;
namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private readonly Dictionary<string,GameDataSession> _embeddedTranslationSessions=new(StringComparer.OrdinalIgnoreCase);
    private GameDataSession? SelectedTranslationSession()=>SelectedDataSession()??SelectedScriptTranslationSession()
        ??(_selectedGame is {} game?_embeddedTranslationSessions.GetValueOrDefault(game.ExePath):null);

    private async Task LaunchEmbeddedTranslationSessionAsyncFor(GameInfo game,string action)
    {
        if(action is not ("plain" or "plain-bare" or "launch" or "install" or "restore"))throw new ArgumentOutOfRangeException(nameof(action));
        var session=_gameSessions.Get(game);string root=RecentGameStore.Normalize(Path.GetDirectoryName(game.ExePath)!);
        if(session.Busy||session.LaunchPending||_gameRootOperations.ContainsKey(root))return;
        session.Busy=true;_gameRootOperations[root]=session;RenderGameSessionState();IEmbeddedTranslationConnection? connection=null;
        session.TranslationBlockedReason="";
        try
        {
            ApiSettings? settings=null;
            string profileId=_fusion!.GameProfileId(game.ExePath),profileName=_fusion.Name(profileId);
            if(action=="launch"){_fusion.RequireGameReady(game.ExePath);settings=_fusion.GameSettings(UiSettings,game.ExePath);}
            foreach(var sibling in _gameSessions.Snapshot().Where(x=>SameGame(Path.GetDirectoryName(x.Game.ExePath),root)).Select(x=>x.Game))
                if(await Task.Run(()=>_gameLaunchOperations.ProcessState(sibling))!=FusionGameProcessState.Stopped)
                    throw new IOException("此游戏目录中的程序仍在运行，请先退出后再安装或恢复翻译。");
            if(await Task.Run(()=>_gameLaunchOperations.ProcessState(game))!=FusionGameProcessState.Stopped)throw new IOException("请先退出此游戏，再切换启动方式。");
            if(_embeddedTranslationSessions.Remove(game.ExePath,out var old))old.Dispose();
            if(action=="plain-bare")
            {
                if(EmbeddedGameAdapters.HasRecord(game))await Task.Run(()=>EmbeddedGameAdapters.Restore(game));
                if(await Task.Run(()=>_gameLaunchOperations.ProcessState(game))!=FusionGameProcessState.Stopped)throw new IOException("游戏状态已改变，未重复启动。");
                int pid=await Task.Run(()=>_gameLaunchOperations.Launch(game));
                session.Launched(GameLaunchMode.Ordinary,null);session.LaunchedWithoutBridge=true;
                session.TranslationBlockedReason="本次选择了仅启动，未接入本软件翻译；已有游戏汉化保持原样。";
                session.Feedback="";await RecordGameStartedAsync(game,pid);return;
            }
            try
            {
                if(game.AdapterId=="unreal"&&action!="restore"){session.Feedback="正在准备游戏文本，完成后继续启动…";RenderGameSessionState();}
                await Task.Run(()=>{if(action=="restore")EmbeddedGameAdapters.Restore(game);else EmbeddedGameAdapters.Install(game);});
            }
            catch(Exception ex)when(action=="plain"&&ex is IOException or UnauthorizedAccessException)
            {
                if(EmbeddedGameAdapters.HasRecord(game)&&!EmbeddedGameAdapters.IsInstalled(game))
                    throw new IOException("翻译组件安装记录不完整或文件已变化。请先恢复或检查该副本，未启动游戏。",ex);
                // A rejected metadata format must not load our older incompatible
                // runtime. Remove only unchanged files recorded as ours.
                if(ex is EmbeddedCompatibilityException&&EmbeddedGameAdapters.HasRecord(game))
                    await Task.Run(()=>EmbeddedGameAdapters.Restore(game));
                // An existing mod/configuration may prevent our optional bridge from
                // being installed. Preserve those files and retain ordinary launch.
                // PrepareOrdinary would restore plugin files, which is inappropriate
                // here: the user asked to launch the existing installation unchanged.
                if(await Task.Run(()=>_gameLaunchOperations.ProcessState(game))!=FusionGameProcessState.Stopped)
                    throw new IOException("游戏状态已改变，未重复启动。",ex);
                int processId=await Task.Run(()=>_gameLaunchOperations.Launch(game));
                session.Launched(GameLaunchMode.Ordinary,null,null,null);
                session.LaunchedWithoutBridge=true;
                session.TranslationBlockedReason=SafeDiagnosticOutput.ExceptionSummary(ex);
                session.Feedback="已按现有配置启动。翻译组件未接入，本次无法在软件内开关翻译。原因："+SafeDiagnosticOutput.ExceptionSummary(ex);
                await RecordGameStartedAsync(game,processId);
                return;
            }
            if(action=="restore"){session.Feedback="翻译组件已恢复，译文缓存保留。";return;}
            if(action=="install"){session.Feedback="翻译组件已准备，可选择启动游戏或翻译并启动。";return;}
            connection=EmbeddedGameAdapters.Connect(game);
            // Older caches can contain every bundled locale. Apply entries only
            // after the session has selected a source or observed matching text.
            // An unfiltered startup prime would bypass that decision entirely.
            if(await Task.Run(()=>_gameLaunchOperations.ProcessState(game))!=FusionGameProcessState.Stopped)throw new IOException("游戏状态已改变，未重复启动。");
            var data=new GameDataSession(connection){TranslationOptions=EmbeddedGameAdapters.Options(game)};
            _embeddedTranslationSessions[game.ExePath]=data;
            using var process=Process.Start(connection.StartInfo(action=="launch"))??throw new IOException("未能启动游戏。");
            connection.Launched(process.Id);
            session.Launched(action=="launch"?GameLaunchMode.Translation:GameLaunchMode.Ordinary,settings is null?null:profileId,settings is null?null:profileName,settings is null?null:FusionConfiguration.EditIdentity(settings));
            session.Feedback="正在等待游戏翻译连接…";RenderGameSessionState();await RecordGameStartedAsync(game,process.Id);
            await EmbeddedLaunchWait.AcceptAsync(connection,process,_desktopLifetime.Token);
            if(action=="plain"){await connection.RequestAsync(new(){["op"]="translationDisable"});session.Feedback="";return;}
            data.Translation=new(connection,game.ExePath,settings!,async(text,profile,token)=>(await _translationService.TranslatePlainAsync(text,profile,token)).Text,data.TranslationOptions);
            await data.Translation.StartAsync();session.Feedback="";
        }
        catch(Exception ex)
        {
            if(ex is EmbeddedLaunchExitedException)session.LaunchExited();
            connection?.Dispose();
            if(_embeddedTranslationSessions.Remove(game.ExePath,out var failed))failed.Dispose();
            session.TranslationBlockedReason=ex is OperationCanceledException?"翻译连接已取消。":SafeDiagnosticOutput.ExceptionSummary(ex);
            session.Feedback=session.TranslationBlockedReason;
        }
        finally{session.Busy=false;_gameRootOperations.Remove(root);if(!IsDisposed){InvalidateGameState();RenderGameSessionState();}}
    }
}
