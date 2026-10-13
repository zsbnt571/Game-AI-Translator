namespace ScreenshotTranslationUiTester;

internal sealed partial class FusionAdapterInstaller
{
    internal void PrepareOrdinaryLaunch(GameInfo game)
    {
        if(ProcessState(game)!=FusionGameProcessState.Stopped)throw new InvalidOperationException("游戏进程状态未确认停止，未启动。");
        if(HasRecord(game))Restore(game);
        EnsureOrdinaryLaunchFiles(game);
    }
    internal static void EnsureOrdinaryLaunchFiles(GameInfo game)
    {
        // A restored backup may itself contain an older translator. Never call that a clean launch.
        var root=Path.GetDirectoryName(game.ExePath)!;
        foreach(var relative in new[]{PluginRelative,FusionCloudPayload.Plugin})
            if(File.Exists(Path.Combine(root,relative)))throw new InvalidOperationException("仍存在翻译插件文件，无法确认普通启动。请在游戏详情中核对安装或恢复记录。");
    }
}
