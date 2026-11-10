namespace ScreenshotTranslationUiTester;

// A registered library item remains browsable when its executable is offline.
// This identity is never a launch or adapter candidate.
internal static class LibraryGameAvailability
{
    internal const string MissingAdapter = "missing-library-entry";
    internal const string Hint = "找不到游戏程序，笔记和资料仍可查看。重新定位后即可启动游戏。";
    internal static bool Missing(GameInfo? game) => game?.AdapterId == MissingAdapter;
    internal static GameInfo Unavailable(string path,string name) => new(path,name,"游戏文件不可用","—",Hint,SupportLevel.Unknown,null,MissingAdapter);
    internal static DesktopGameState State(GameInfo game) => new(Unavailable(game.ExePath,game.Name),false,false,"未核对","应用翻译",null,Hint,FusionGameProcessState.Unknown,Hint);
}
