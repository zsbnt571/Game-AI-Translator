namespace ScreenshotTranslationUiTester;

// This route never invokes installers, restorers or native bridge preparation.
// A running game is attached to as-is. Only an explicit stopped-game request
// may launch the existing installation; an unknown identity is not stopped.
internal static class ManualScreenTranslationLaunch
{
    internal static async Task<int?> EnsureRunningAsync(GameInfo game,IGameLaunchOperations operations,CancellationToken cancellation)
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
            return operations.Launch(game);
        },cancellation);
    }
}
