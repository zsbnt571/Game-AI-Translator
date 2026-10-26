namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private static bool CanResetMachineTranslations(GameInfo game)=>EmbeddedGameAdapters.Handles(game)
        ||game.AdapterId=="renpy"||RpgMakerDataAdapter.Detect(game.ExePath) is not null||CatalogTranslationPreparation.Supports(game);
    private async Task ResetMachineTranslationsAsync(GameInfo game)
    {
        var session=_gameSessions.Get(game);string root=RecentGameStore.Normalize(Path.GetDirectoryName(game.ExePath)!);
        if(!CanResetMachineTranslations(game)||session.Busy||session.LaunchPending||_gameRootOperations.ContainsKey(root))return;
        if(AppDialog.Show(this,"清空机器译文并重译",
            "清空此游戏各翻译方案当前使用的机器译文与准备译文。下次开启翻译时重新请求，可能产生翻译用量。\n\n人工译文、术语、游戏文件和存档保留。旧机器缓存作为备份封存，不会自动回填。请先退出游戏。",
            AppDialogKind.Confirmation)!=DialogResult.Yes)return;
        if(IsDisposed)return;
        session.Busy=true;_gameRootOperations[root]=session;RenderGameSessionState();
        try
        {
            foreach(var sibling in _gameSessions.Snapshot().Where(x=>SameGame(Path.GetDirectoryName(x.Game.ExePath),root)).Select(x=>x.Game).Append(game).Distinct())
                if(await Task.Run(()=>_gameLaunchOperations.ProcessState(sibling))!=FusionGameProcessState.Stopped)
                    throw new IOException("请先退出此游戏目录中的所有程序，再清空机器译文。");
            // Running workers retain their old cache file paths. Even a delayed
            // network response can only finish into the archived generation.
            var reset=await Task.Run(()=>TranslationCacheReset.Reset(game.ExePath));
            session.Feedback=$"机器译文已清空；下次按 {reset.Build} 重新生成。人工译文和术语保留，旧缓存已封存。";
        }
        catch(Exception ex){session.Feedback=SafeDiagnosticOutput.ExceptionSummary(ex);}
        finally{session.Busy=false;_gameRootOperations.Remove(root);if(!IsDisposed){InvalidateGameState();RenderGameSessionState();}}
    }
}
