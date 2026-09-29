using System.Diagnostics;

namespace ScreenshotTranslationUiTester;

// The boundary allows exercising interleaved UI actions without starting games,
// writing into real installations, taking screenshots or using translation APIs.
internal interface IGameLaunchOperations
{
    FusionGameProcessState ProcessState(GameInfo game);
    void PrepareOrdinary(GameInfo game);
    void Install(GameInfo game,ApiSettings settings,string key);
    void Restore(GameInfo game);
    int Launch(GameInfo game);
    bool AutomaticCover { get; }
}
internal sealed class DesktopGameLaunchOperations : IGameLaunchOperations
{
    public bool AutomaticCover=>true;
    public FusionGameProcessState ProcessState(GameInfo game)=>FusionAdapterInstaller.ProcessState(game);
    public void PrepareOrdinary(GameInfo game)=>new FusionAdapterInstaller().PrepareOrdinaryLaunch(game);
    public void Install(GameInfo game,ApiSettings settings,string key)=>new FusionAdapterInstaller().Install(game,settings,key);
    public void Restore(GameInfo game)=>new FusionAdapterInstaller().Restore(game);
    public int Launch(GameInfo game)
    {
        var start=new ProcessStartInfo(game.ExePath){WorkingDirectory=Path.GetDirectoryName(game.ExePath),UseShellExecute=true};
        if(RpgMakerDataAdapter.Detect(game.ExePath) is not null){start.UseShellExecute=false;start.Environment.Remove("FUSION_RPG_PIPE");start.Environment.Remove("FUSION_RPG_SECRET");}
        if(game.AdapterId=="renpy"){start.UseShellExecute=false;start.Environment.Remove("FUSION_RENPY_PIPE");start.Environment.Remove("FUSION_RENPY_SECRET");}
        GameTestCopyEnvironment.Apply(start,game.AdapterId=="renpy");
        using var process=Process.Start(start);
        return process?.Id??throw new InvalidOperationException("未能确认启动请求。");
    }
}

public sealed partial class MainForm
{
    private IGameLaunchOperations _gameLaunchOperations = new DesktopGameLaunchOperations();
    private Task RunGameActionAsync(string action)=>!_gameDetecting&&_selectedGame is {} game?RunGameActionForAsync(game,action):Task.CompletedTask;
    private async Task RunGameActionForAsync(GameInfo game,string action)
    {
        if(RpgMakerDataAdapter.Detect(game.ExePath) is not null)
        {await LaunchRpgSessionAsyncFor(game,action);return;}
        if(game.AdapterId=="renpy"){await LaunchScriptTranslationSessionAsyncFor(game,action);return;}
        if(EmbeddedGameAdapters.Handles(game)){await LaunchEmbeddedTranslationSessionAsyncFor(game,action);return;}
        var session=_gameSessions.Get(game);
        if(session.Busy||session.LaunchPending)return;
        var root=RecentGameStore.Normalize(Path.GetDirectoryName(game.ExePath)!);
        if(_gameRootOperations.ContainsKey(root)){session.Feedback="此游戏目录正在处理，请稍后重试。";RenderGameState();return;}
        if(action is not ("plain" or "launch" or "install" or "restore"))throw new ArgumentOutOfRangeException(nameof(action));
        var operations=_gameLaunchOperations;
        var key=_fusion!.State.EmbeddedToggleKey;
        var profileId=_fusion.GameProfileId(game.ExePath);
        var profileName=_fusion.Name(profileId);
        ApiSettings? settings=action is "launch" or "install"?_fusion.GameSettings(UiSettings,game.ExePath):null;
        bool Current()=>!IsDisposed&&!Disposing&&SameGame(game.ExePath,_selectedGame?.ExePath);
        if(action=="restore"&&AppDialog.Show(this,"恢复安装前状态","按此游戏的安装记录恢复原文件。缓存与备份保留，游戏须已退出。",AppDialogKind.Confirmation)!=DialogResult.Yes)return;
        if(IsDisposed)return;
        session.Busy=true; _gameRootOperations[root]=session; _gameQueryEpoch++;RenderGameState();
        try
        {
            // Validate before the first await: the plan and configuration below
            // belong to this game even if the user changes profiles while we work.
            if(action is "launch" or "install")_fusion.RequireGameReady(game.ExePath);
            // A second executable in the same installation must not restore files
            // currently used by a different session in that directory.
            var shared=_gameSessions.Snapshot().Where(x=>!ReferenceEquals(x,session)&&StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(x.Game.ExePath),Path.GetDirectoryName(game.ExePath))).Select(x=>x.Game).ToArray();
            foreach(var other in shared)if(await Task.Run(()=>operations.ProcessState(other))!=FusionGameProcessState.Stopped)
                throw new InvalidOperationException("此目录中的另一个游戏仍在运行，未更改共享插件文件。");
            var process=await Task.Run(()=>operations.ProcessState(game));
            session.Observe(process);
            if(process!=FusionGameProcessState.Stopped)throw new InvalidOperationException(process==FusionGameProcessState.Running?"此游戏已经运行，未重复启动或更改其插件。":"尚不能确认此游戏的进程路径，未执行操作。");
            if(action=="restore")
            {
                await Task.Run(()=>operations.Restore(game));session.Feedback="已恢复此游戏的安装前状态，缓存与备份保留。";
            }
            else
            {
                if(action=="plain")await Task.Run(()=>operations.PrepareOrdinary(game));
                else await Task.Run(()=>operations.Install(game,settings!,key));
                if(action=="install")session.Feedback="已写入此游戏的翻译方案，下次启动生效。";
                else
                {
                    if(await Task.Run(()=>operations.ProcessState(game))!=FusionGameProcessState.Stopped)throw new InvalidOperationException("启动前此游戏的进程状态已改变，未重复启动。");
                    int processId=await Task.Run(()=>operations.Launch(game));
                    session.Launched(action=="launch"?GameLaunchMode.Translation:GameLaunchMode.Ordinary,action=="launch"?profileId:null,action=="launch"?profileName:null,settings is null?null:FusionConfiguration.EditIdentity(settings));
                    
                    // Completion belongs to the captured game even after browsing elsewhere.
                    // Launch does not navigate. Details are entered explicitly by
                    // double-click/menu, so returning to the library stays there.
                    if(Current())RenderGameSessionState();
                    await RecordGameStartedAsync(game,processId);
                }
            }
        }
        catch(Exception ex)
        {
            session.Feedback=SafeDiagnosticOutput.ExceptionSummary(ex);
            if(Current())ShowFusionError(ex);
        }
        finally
        {
            session.Busy=false;_gameRootOperations.Remove(root);
            if(Current()){_gameOperationResult=session.Feedback;InvalidateGameState();}
            if(!IsDisposed){ScheduleLibraryRefresh();RenderGameSessionState();}
        }
    }
}
