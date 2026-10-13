namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private sealed record ManualScreenSession(GameDataSession Data,GameScreenTranslationOverlay Overlay,string ProfileName);
    // Side-by-side ownership: screen capture never replaces a native session or
    // becomes the target of the embedded translation switch/profile controls.
    private readonly Dictionary<string,ManualScreenSession> _manualScreenSessions=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,string> _manualScreenFeedback=new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _manualScreenOperations=new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<GameDataSession> _manualScreenOwnedData=[];
    private bool _manualScreenClosing;

    private void DisposeManualScreenSessions()
    {
        _manualScreenClosing=true;
        var sessions=_manualScreenSessions.Values.ToArray();_manualScreenSessions.Clear();
        var owned=_manualScreenOwnedData.ToArray();_manualScreenOwnedData.Clear();
        foreach(var active in sessions)active.Overlay.Close();
        // Include sessions whose overlay has already closed and queues still
        // finishing startup. Do not abandon their final save at process exit.
        var cleanup=Task.Run(()=>Task.WhenAll(owned.Select(async data=>
        {
            try{if(data.Translation is {} translation)await translation.StopAsync();}
            catch(Exception ex){AppLog.Write("screen-translation","Screen queue shutdown failed",ex);}
            finally{data.Dispose();}
        })));
        if(!cleanup.Wait(TimeSpan.FromSeconds(5)))
            AppLog.Write("screen-translation","Screen cache shutdown exceeded its five-second budget; cleanup remains in progress.");
    }

    private async Task ToggleScreenTranslationForAsync(GameInfo game)
    {
        if(_manualScreenClosing||!_manualScreenOperations.Add(game.ExePath))return;
        var session=_gameSessions.Get(game);
        string root=RecentGameStore.Normalize(Path.GetDirectoryName(game.ExePath)!);
        bool ownsRoot=false;GameDataSession? pending=null;
        try
        {
            if(_manualScreenSessions.Remove(game.ExePath,out var active))
            {
                active.Overlay.Close();
                try{if(active.Data.Translation is {} translation)await translation.StopAsync();}
                finally{active.Data.Dispose();_manualScreenOwnedData.Remove(active.Data);}
                _manualScreenFeedback[game.ExePath]="屏幕翻译已关闭。"+(active.Data.Translation?.CacheWarning??"");
                return;
            }
            if(session.Busy||session.LaunchPending||_gameRootOperations.ContainsKey(root))
            {_manualScreenFeedback[game.ExePath]="游戏正在处理，请稍后开启屏幕翻译。";return;}
            _fusion!.RequireGameReady(game.ExePath);
            var settings=ApiSettingsSnapshot.Copy(_fusion.GameSettings(UiSettings,game.ExePath));
            string profileName=_fusion.Name(_fusion.GameProfileId(game.ExePath));
            session.Busy=true;_gameRootOperations[root]=session;ownsRoot=true;
            _manualScreenFeedback[game.ExePath]="正在开启屏幕翻译…";RenderGameSessionState();
            int? launched=await ManualScreenTranslationLaunch.EnsureRunningAsync(game,_gameLaunchOperations,_desktopLifetime.Token);
            if(launched is {} pid)
            {
                session.Launched(GameLaunchMode.Ordinary,null);
                await RecordGameStartedAsync(game,pid);
            }
            _desktopLifetime.Token.ThrowIfCancellationRequested();
            var connection=new ScreenTranslationConnection(game.ExePath,settings);
            // Screen and native queues can run concurrently. They must never
            // save independent dictionaries over the same cache file.
            var options=new EmbeddedTranslationOptions(EmbeddedTextSyntax.Generic,false){CacheScope=TranslationCacheScope.Screen};
            pending=new GameDataSession(connection){TranslationOptions=options};
            _manualScreenOwnedData.Add(pending);
            pending.Translation=new(connection,game.ExePath,settings,
                async(text,profile,token)=>(await _translationService.TranslatePlainAsync(text,profile,token)).Text,options);
            await pending.Translation.StartAsync();
            _desktopLifetime.Token.ThrowIfCancellationRequested();
            var overlay=new GameScreenTranslationOverlay(connection,pending);
            var registered=new ManualScreenSession(pending,overlay,profileName);
            _manualScreenSessions[game.ExePath]=registered;pending=null;
            overlay.Disposed+=async(_,_)=>await EndScreenTranslationAfterExitAsync(game,registered);
            _manualScreenFeedback.Remove(game.ExePath);
        }
        catch(OperationCanceledException)when(_desktopLifetime.IsCancellationRequested){}
        catch(Exception ex){_manualScreenFeedback[game.ExePath]="屏幕翻译未开启："+SafeDiagnosticOutput.ExceptionSummary(ex);}
        finally
        {
            if(pending is not null)
            {
                try{if(pending.Translation is {} translation)await translation.StopAsync();}
                catch(Exception ex){AppLog.Write("screen-translation","Failed screen start cleanup",ex);}
                finally{pending.Dispose();_manualScreenOwnedData.Remove(pending);}
            }
            _manualScreenOperations.Remove(game.ExePath);
            if(ownsRoot){session.Busy=false;_gameRootOperations.Remove(root);}
            if(!IsDisposed&&!Disposing){InvalidateGameState();RenderGameSessionState();}
        }
    }

    private async Task EndScreenTranslationAfterExitAsync(GameInfo game,ManualScreenSession active)
    {
        if(!_manualScreenSessions.TryGetValue(game.ExePath,out var current)||!ReferenceEquals(current,active))return;
        _manualScreenSessions.Remove(game.ExePath);_manualScreenOperations.Add(game.ExePath);
        try
        {
            // Drain the old queue's final cache save before another manual
            // session for the same game can read/write its screen cache.
            if(active.Data.Translation is {} translation)await translation.StopAsync();
            _manualScreenFeedback[game.ExePath]="游戏窗口已关闭，屏幕翻译已结束。"+(active.Data.Translation?.CacheWarning??"");
        }
        catch(Exception ex){_manualScreenFeedback[game.ExePath]="屏幕翻译已结束："+SafeDiagnosticOutput.ExceptionSummary(ex);}
        finally
        {
            active.Data.Dispose();_manualScreenOwnedData.Remove(active.Data);_manualScreenOperations.Remove(game.ExePath);
            if(!IsDisposed&&!Disposing)RenderGameSessionState();
        }
    }

    private void RenderScreenTranslationState(GameInfo? game,bool running,bool stopped,bool busy,bool ready)
    {
        if(_sessionScreenTranslation is null)return;
        var active=game is null?null:_manualScreenSessions.GetValueOrDefault(game.ExePath);
        bool changing=game is not null&&_manualScreenOperations.Contains(game.ExePath);
        _sessionScreenTranslation.Visible=game is not null;
        _sessionScreenTranslation.Enabled=game is not null&&!changing&&(active is not null||!busy&&ready&&(running||stopped));
        _sessionScreenTranslation.Text=active is not null?"关闭屏幕翻译":running?"开启屏幕翻译":"屏幕翻译并启动";
        string message=game is null?"":_manualScreenFeedback.GetValueOrDefault(game.ExePath,"手动开启后，在游戏画面上显示译文。");
        if(active is not null)
        {
            var translation=active.Data.Translation!;
            message=translation.Enabled?"屏幕翻译已开启 · "+active.ProfileName+" · "+((ScreenTranslationConnection)active.Data.Connection).Status:translation.Status;
            if(translation.Enabled&&translation.FailedCount>0)message+=$" · {translation.FailedCount} 条翻译失败，可关闭后重新开启重试";
            if(translation.CacheWarning.Length>0)message+=" · "+translation.CacheWarning;
        }
        SetLabelText(_sessionScreenTranslationState,message);
    }
}
