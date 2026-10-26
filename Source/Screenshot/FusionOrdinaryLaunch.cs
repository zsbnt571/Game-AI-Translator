namespace ScreenshotTranslationUiTester;

internal sealed partial class FusionAdapterInstaller
{
    internal void PrepareOrdinaryLaunch(GameInfo game)
    {
        if(ProcessState(game)!=FusionGameProcessState.Stopped)throw new InvalidOperationException("游戏进程状态未确认停止，未启动。");
        if(RpgMakerDataAdapter.Detect(game.ExePath) is not null){RpgMakerDataAdapter.Restore(game.ExePath);return;}
        if(game.AdapterId=="renpy"){if(RenpyGameAdapter.HasRecord(game.ExePath))RenpyGameAdapter.Restore(game.ExePath);return;}
        if(EmbeddedGameAdapters.Handles(game))
        {
            if(EmbeddedGameAdapters.HasRecord(game))EmbeddedGameAdapters.Restore(game);
            if(game.AdapterId is "unity-mono" or "unity-il2cpp")
            {
                UnityLegacyMigration.Recover(game.ExePath,newerInstalled:false);
                using var migration=UnityLegacyMigration.Begin(game.ExePath);migration.Commit();
                EnsureOrdinaryLaunchFiles(game);
            }
            return;
        }
        if(HasRecord(game))Restore(game);
        EnsureOrdinaryLaunchFiles(game);
    }
    internal static void EnsureOrdinaryLaunchFiles(GameInfo game)
    {
        // A restored backup may itself contain an older translator. Never call that a clean launch.
        var root=Path.GetDirectoryName(game.ExePath)!;
        foreach(var relative in new[]{UnityLegacyMigration.MgiPlugin,UnityLegacyMigration.CloudPlugin})
            if(File.Exists(Path.Combine(root,relative)))throw new InvalidOperationException("仍存在翻译插件文件，无法确认普通启动。请在游戏详情中核对安装或恢复记录。");
    }
}
