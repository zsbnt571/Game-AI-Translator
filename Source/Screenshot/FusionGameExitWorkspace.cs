namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private async Task RequestGameExitAsync(GameInfo game)
    {
        var session=_gameSessions.Get(game);
        string root=RecentGameStore.Normalize(Path.GetDirectoryName(game.ExePath)!);
        if(session.Busy||_gameRootOperations.ContainsKey(root))return;
        session.Busy=true;_gameRootOperations[root]=session;RenderGameSessionState();
        try
        {
            var state=await Task.Run(()=>_gameLaunchOperations.ProcessState(game));
            if(state==FusionGameProcessState.Stopped){session.Observe(state);session.Feedback="游戏已退出。";return;}
            // A close request uses only a verified window belonging to this game.
            // It never creates a bridge, reads game memory, or terminates a process.
            var result=await Task.Run(()=>GameWindowClose.Request(game.ExePath));
            if(!result.Requested){session.Feedback=result.Message;return;}
            _gameCrashMonitor?.MarkExpectedExit(game.ExePath,"软件请求正常退出游戏");
            session.Feedback="已发送退出请求，请返回游戏确认退出并保存进度。";
            RenderGameSessionState();
            for(int attempt=0;attempt<5;attempt++)
            {
                await Task.Delay(250,_desktopLifetime.Token);
                state=await Task.Run(()=>_gameLaunchOperations.ProcessState(game));
                if(state!=FusionGameProcessState.Stopped)continue;
                session.Observe(state);session.Feedback="游戏已退出。";break;
            }
        }
        catch(OperationCanceledException) when(_desktopLifetime.IsCancellationRequested){}
        catch(Exception ex)
        {
            AppLog.Write("game-exit",SafeDiagnosticOutput.ExceptionSummary(ex));
            session.Feedback="暂时无法请求退出，请返回游戏保存进度后正常退出。";
        }
        finally
        {
            session.Busy=false;_gameRootOperations.Remove(root);
            if(!IsDisposed)RenderGameSessionState();
        }
    }
}

internal static class GameWindowClose
{
    internal sealed record Result(bool Requested,string Message);
    internal static Result Request(string exe)=>Request(exe,
        path=>GameLibraryWindowIdentity.Find(path,forControl:true,requireSingleProcess:true).Target,
        target=>GameLibraryWindowIdentity.Matches(target,forControl:true),
        target=>NativeMethods.PostMessage(target.Window,0x10,IntPtr.Zero,IntPtr.Zero));

    internal static Result Request(string exe,Func<string,CoverTarget?> find,Func<CoverTarget,bool> matches,Func<CoverTarget,bool> close)
    {
        var target=find(exe);
        // Revalidate HWND, executable path, PID and process creation time after
        // discovery, immediately before the normal close message is sent.
        if(target is null||!matches(target))return new(false,"无法确认此游戏窗口，请返回游戏正常退出。");
        return close(target)?new(true,""):new(false,"游戏未接收退出请求，请返回游戏正常退出。");
    }
}
