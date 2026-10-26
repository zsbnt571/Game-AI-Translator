namespace ScreenshotTranslationUiTester;

// A running game is captured as-is. An explicit stopped-game request uses the
// ordinary launch preparation to remove only our recorded native components,
// so requesting screen capture cannot silently restart an embedded translator.
// Unknown process identity is never treated as stopped.
internal static class ManualScreenTranslationLaunch
{
    internal static async Task<int?> EnsureRunningAsync(GameInfo game,IGameLaunchOperations operations,CancellationToken cancellation,Action? beforePrepare=null)
    {
        cancellation.ThrowIfCancellationRequested();
        var state=await Task.Run(()=>operations.ProcessState(game),cancellation);
        cancellation.ThrowIfCancellationRequested();
        if(state==FusionGameProcessState.Running)return null;
        if(state!=FusionGameProcessState.Stopped)throw new IOException("尚不能确认游戏进程，屏幕翻译未开启。");
        return await Task.Run<int?>(()=>
        {
            cancellation.ThrowIfCancellationRequested();
            // Recheck after asynchronous UI work so a second launch cannot race
            // the game becoming visible during preparation.
            var current=operations.ProcessState(game);
            cancellation.ThrowIfCancellationRequested();
            if(current==FusionGameProcessState.Running)return null;
            if(current!=FusionGameProcessState.Stopped)throw new IOException("游戏状态尚未确认，未重复启动。");
            beforePrepare?.Invoke();
            cancellation.ThrowIfCancellationRequested();
            operations.PrepareOrdinary(game);
            cancellation.ThrowIfCancellationRequested();
            current=operations.ProcessState(game);
            cancellation.ThrowIfCancellationRequested();
            if(current==FusionGameProcessState.Running)return null;
            if(current!=FusionGameProcessState.Stopped)throw new IOException("游戏状态尚未确认，未重复启动。");
            return operations.Launch(game);
        },cancellation);
    }
}
