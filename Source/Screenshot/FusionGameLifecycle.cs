namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private readonly Dictionary<string,CancellationTokenSource> _automaticGameCovers=new(StringComparer.OrdinalIgnoreCase);

    // Every engine calls this after starting its process; browsing elsewhere does
    // not cancel artwork belonging to the game that was actually launched.
    private async Task RecordGameStartedAsync(GameInfo game,int pid)
    {
        TrackGameDiagnostics(game,pid);
        if(_gameLaunchOperations.AutomaticCover)GameWindowCover.NoteLaunch(game.ExePath,pid);
        await RememberLaunchAsync(game);
        if(!IsDisposed&&_gameLaunchOperations.AutomaticCover)_=CaptureStartedGameCoverAsync(game);
    }

    private async Task CaptureStartedGameCoverAsync(GameInfo game)
    {
        string path=RecentGameStore.Normalize(game.ExePath);
        if(_manualCoverActive&&SameGame(path,_selectedGame?.ExePath))return;
        if(_automaticGameCovers.Remove(path,out var previous))previous.Cancel();
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(_desktopLifetime.Token);
        _automaticGameCovers[path]=cancel;
        try
        {
            var store=await RecentStore();var record=store.Find(path);
            if(!store.Writable||record is null)return;
            void Log(string message)=>AppLog.Write("game-cover","shared-launch record="+record.Id+" "+message);
            if(record.CoverManual){Log("event=manual-cover-protected");return;}
            if((record.CoverPreference??_libraryPreferences.CoverSource)==ArtworkSource.Online)
                await FillDefaultArtworkAsync([path]);
            record=store.Find(path);if(record is null||record.CoverManual)return;
            using(var existing=await Task.Run(()=>store.ReadImage(record,true),cancel.Token))if(existing is not null){Log("event=reuse-valid-cover");return;}
            var version=store.CoverIdentityVersion(record);
            Log("event=start");
            var result=await GameWindowCover.CaptureAsync(path,true,cancel.Token,Log);
            using var bitmap=result.Image;
            if(bitmap is null){Log("event=failed "+result.Diagnostic);return;}
            bool Valid()=>!cancel.IsCancellationRequested&&result.Target is not null&&GameLibraryWindowIdentity.Matches(result.Target);
            bool saved=await Task.Run(()=>store.SaveImage(record,bitmap,true,null,cancel.Token,Valid,version,manual:false,origin:"automatic"),cancel.Token);
            Log(saved?"event=committed":"event=commit-rejected reason=stale-record-or-window");
            if(saved&&!IsDisposed)
            {
                if(SameGame(path,_selectedGame?.ExePath))_currentRecent=store.Find(path);
                ScheduleLibraryRefresh();RenderGameSessionState();
            }
        }
        catch(OperationCanceledException){}
        catch(Exception ex){AppLog.Write("game-cover","shared-launch "+SafeDiagnosticOutput.ExceptionSummary(ex));}
        finally{if(_automaticGameCovers.TryGetValue(path,out var current)&&ReferenceEquals(current,cancel))_automaticGameCovers.Remove(path);}
    }

    private async Task RestartSelectedGameAsync()
    {
        if(_selectedGame is not {} game)return;
        if(_gameRestartRequests.TryGetValue(game.ExePath,out var pending)){pending.Cancel();return;}
        var session=_gameSessions.Get(game);if(session.Busy)return;
        if(AppDialog.Show(this,"重启游戏","请先在游戏中保存进度。确定后将正常退出游戏，再按当前翻译状态启动。若游戏需要退出确认，可返回游戏处理，也可以取消重启。",AppDialogKind.Confirmation)!=DialogResult.Yes)return;
        await RestartGameAsync(game);
    }
    private readonly Dictionary<string,CancellationTokenSource> _gameRestartRequests=new(StringComparer.OrdinalIgnoreCase);
    private bool IsGameRestartPending(GameInfo? game)=>game is not null&&_gameRestartRequests.ContainsKey(game.ExePath);
    private async Task RestartGameAsync(GameInfo game)
    {
        var session=_gameSessions.Get(game);if(session.Busy)return;
        string root=RecentGameStore.Normalize(Path.GetDirectoryName(game.ExePath)!);
        if(_gameRootOperations.ContainsKey(root))return;
        var data=_scriptTranslationSessions.GetValueOrDefault(game.ExePath)??_dataSessions.GetValueOrDefault(game.ExePath)??_embeddedTranslationSessions.GetValueOrDefault(game.ExePath);
        bool translate=data is not null?data.Translation?.Enabled==true:session.LaunchMode==GameLaunchMode.Translation;
        string action=translate?"launch":session.LaunchMode==GameLaunchMode.Modification?"modify":"plain";
        if(!translate&&session.LaunchedWithoutBridge&&EmbeddedGameAdapters.Handles(game))action="plain-bare";
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(_desktopLifetime.Token);
        _gameRestartRequests.Add(game.ExePath,cancel);
        bool restart=false,quitRequested=false;session.Busy=true;_gameRootOperations[root]=session;RenderGameSessionState();
        try
        {
            var state=await Task.Run(()=>_gameLaunchOperations.ProcessState(game));
            if(state!=FusionGameProcessState.Stopped)
            {
                bool engineQuit=await ConnectedGameLifecycle.TryRequestNormalQuitAsync(data?.Connection,cancel.Token,()=>
                {quitRequested=true;_gameCrashMonitor?.MarkExpectedExit(game.ExePath,"软件请求重启游戏");});
                state=await Task.Run(()=>_gameLaunchOperations.ProcessState(game));
                if(!engineQuit&&state!=FusionGameProcessState.Stopped)
                {
                    var found=await Task.Run(()=>GameLibraryWindowIdentity.Find(game.ExePath,forControl:true));
                    if(found.Target is not {} target||!GameLibraryWindowIdentity.Matches(target,forControl:true))throw new IOException("无法确认此游戏窗口："+found.Message+"（"+found.Code+"）。请先手动退出游戏，再重新启动。");
                    cancel.Token.ThrowIfCancellationRequested();
                    _gameCrashMonitor?.MarkExpectedExit(game.ExePath,"软件请求关闭窗口以重启游戏");
                    if(!NativeMethods.PostMessage(target.Window,0x10,IntPtr.Zero,IntPtr.Zero))throw new IOException("游戏未接收退出请求，请返回游戏退出后重试。");
                    quitRequested=true;
                }
                session.Feedback=engineQuit?"正在正常退出游戏，退出后自动重新启动。可取消后续启动。":"等待游戏退出确认：请点击“返回游戏”处理，退出后会自动重新启动；也可点击“取消重启”。";
                RenderGameSessionState();
                // A game's own confirmation is a pending user action, not an
                // IOException after 45 seconds. The visible restart action can
                // cancel this wait. Never force-kill or start a second instance.
                while(state!=FusionGameProcessState.Stopped)
                {
                    await Task.Delay(350,cancel.Token);
                    state=await Task.Run(()=>_gameLaunchOperations.ProcessState(game));
                }
            }
            cancel.Token.ThrowIfCancellationRequested();
            session.Observe(FusionGameProcessState.Stopped);session.Feedback="";restart=true;
        }
        catch(OperationCanceledException){session.Feedback="已取消重启；若游戏已经退出，可点击启动游戏。";}
        catch(Exception ex){AppLog.Write("game-restart",SafeDiagnosticOutput.ExceptionSummary(ex));session.Feedback=ex is IOException?ex.Message:"暂时无法完成重启，请返回游戏保存进度后重试。";}
        finally{if(!quitRequested)_gameCrashMonitor?.ClearExpectedExit(game.ExePath);_gameRestartRequests.Remove(game.ExePath);session.Busy=false;_gameRootOperations.Remove(root);if(!IsDisposed)RenderGameSessionState();}
        if(restart&&!IsDisposed)await RunGameActionForAsync(game,action);
    }
}
