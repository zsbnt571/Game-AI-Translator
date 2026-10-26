using System.Diagnostics;

namespace ScreenshotTranslationUiTester;

// Exercises the real shared launch/session policy without writing a game,
// starting a process, or using an API in controlled desktop regression tests.
internal interface IGameDormantRuntimeOperations
{
    void Install(GameInfo game);
    IEmbeddedTranslationConnection Connect(GameInfo game);
    Task<Process> LaunchAsync(IEmbeddedTranslationConnection connection,CancellationToken cancellation);
    Task AcceptAsync(GameInfo game,IEmbeddedTranslationConnection connection,Process process,CancellationToken cancellation,Action<string> progress);
}

internal sealed class DesktopGameDormantRuntimeOperations : IGameDormantRuntimeOperations
{
    public void Install(GameInfo game)
    {
        if(game.AdapterId is "rpg-maker-mv" or "rpg-maker-mz")RpgMakerDataAdapter.Install(game.ExePath);
        else if(game.AdapterId=="renpy")RenpyGameAdapter.Install(game.ExePath);
        else if(game.AdapterId=="unreal")UnrealGameAdapter.Install(game.ExePath,prepareCatalog:false);
        else EmbeddedGameAdapters.Install(game);
    }
    public IEmbeddedTranslationConnection Connect(GameInfo game)
        =>game.AdapterId=="unreal"?new UnrealTranslationConnection(game.ExePath,deferCatalog:true)
            :game.AdapterId is "rpg-maker-mv" or "rpg-maker-mz" or "renpy"
            ?new RpgGameDataConnection(game.ExePath):EmbeddedGameAdapters.Connect(game);
    public Task<Process> LaunchAsync(IEmbeddedTranslationConnection connection,CancellationToken cancellation)
        =>connection.LaunchAsync(translationAtStartup:false,cancellation);
    public Task AcceptAsync(GameInfo game,IEmbeddedTranslationConnection connection,Process process,CancellationToken cancellation,Action<string> progress)
        =>EmbeddedLaunchWait.AcceptAsync(connection,process,cancellation,progress,observeLaunchedProcessExit:game.AdapterId!="renpy");
}

public sealed partial class MainForm
{
    private IGameDormantRuntimeOperations _gameDormantRuntimeOperations=new DesktopGameDormantRuntimeOperations();

    private async Task LaunchDormantGameRuntimeAsyncFor(GameInfo game)
    {
        var session=_gameSessions.Get(game);
        string root=RecentGameStore.Normalize(Path.GetDirectoryName(game.ExePath)!);
        if(session.Busy||session.LaunchPending||_gameRootOperations.ContainsKey(root))return;
        var operations=_gameLaunchOperations;var runtime=_gameDormantRuntimeOperations;
        var sessions=game.AdapterId is "rpg-maker-mv" or "rpg-maker-mz"?_dataSessions
            :game.AdapterId=="renpy"?_scriptTranslationSessions:_embeddedTranslationSessions;
        GameDataSession? data=null;IEmbeddedTranslationConnection? connection=null;
        session.Busy=true;session.TranslationRuntimePending=true;session.TranslationBlockedReason="";
        _gameRootOperations[root]=session;_gameQueryEpoch++;RenderGameSessionState();
        try
        {
            foreach(var sibling in _gameSessions.Snapshot().Where(x=>!ReferenceEquals(x,session)&&SameGame(Path.GetDirectoryName(x.Game.ExePath),root)).Select(x=>x.Game))
                if(await Task.Run(()=>operations.ProcessState(sibling))!=FusionGameProcessState.Stopped)
                    throw new IOException("此目录中的另一个游戏仍在运行，未更改共享插件文件。");
            var state=await Task.Run(()=>operations.ProcessState(game));session.Observe(state);
            if(state!=FusionGameProcessState.Stopped)
                throw new IOException(state==FusionGameProcessState.Running?"此游戏已经运行，未重复启动或更改其插件。":"尚不能确认此游戏的进程路径，未执行操作。");
            foreach(var existing in new[]{_dataSessions,_scriptTranslationSessions,_embeddedTranslationSessions})
                if(existing.Remove(game.ExePath,out var previous)){ResetNovelSpeed(previous.Connection);previous.Dispose();}
            session.Feedback="正在准备游戏连接，翻译保持关闭。";RenderGameSessionState();
            await Task.Run(()=>runtime.Install(game));
            connection=runtime.Connect(game);
            // Do not read a translation profile, prime a cache, enumerate text,
            // or create a translation worker during ordinary startup.
            data=new(connection){TranslationOptions=EmbeddedGameAdapters.Handles(game)?EmbeddedGameAdapters.Options(game):null};
            sessions[game.ExePath]=data;
            if(await Task.Run(()=>operations.ProcessState(game))!=FusionGameProcessState.Stopped)
                throw new IOException("游戏状态已改变，未重复启动。");
            using var process=await runtime.LaunchAsync(connection,_desktopLifetime.Token);
            session.Launched(GameLaunchMode.Ordinary,null);
            connection.Launched(process.Id);
            session.Feedback=GameTranslationActivation.ConnectingHint;RenderGameSessionState();
            await RecordGameStartedAsync(game,process.Id);
            await runtime.AcceptAsync(game,connection,process,_desktopLifetime.Token,progress=>
            {if(!IsDisposed&&!Disposing){session.Feedback=progress;RenderGameSessionState();}});
            if(!connection.Connected)throw new IOException("游戏连接尚未确认，翻译保持关闭。");
            session.Feedback="游戏已连接，翻译关闭。可在游玩时开启，无需重启。";
        }
        catch(Exception ex)
        {
            if(ex is EmbeddedLaunchExitedException)session.LaunchExited();
            bool restorePending=data?.Translation?.RestorePending==true&&sessions.TryGetValue(game.ExePath,out var pending)&&ReferenceEquals(pending,data);
            if(!restorePending)
            {
                if(data is not null&&sessions.TryGetValue(game.ExePath,out var current)&&ReferenceEquals(current,data))sessions.Remove(game.ExePath);
                if(data is not null)data.Dispose();else connection?.Dispose();
            }
            session.TranslationBlockedReason=ex is OperationCanceledException?"游戏连接已取消，未重复启动游戏。":SafeDiagnosticOutput.ExceptionSummary(ex);
            session.Feedback=session.TranslationBlockedReason;
            // A failed or late bridge never causes an automatic second launch.
        }
        finally
        {
            session.TranslationRuntimePending=false;session.Busy=false;_gameRootOperations.Remove(root);
            if(!IsDisposed&&!Disposing){InvalidateGameState();ScheduleLibraryRefresh();RenderGameSessionState();}
        }
    }
}
