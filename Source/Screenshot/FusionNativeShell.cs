namespace ScreenshotTranslationUiTester;

internal sealed class DesktopNavigationItem(string caption,Font font)
{
    internal string Text => UiLanguage.Text(caption);
    internal Font Font { get; set; } = font;
}

public sealed partial class MainForm
{
    private readonly Dictionary<string,DesktopNavigationItem> _nativeNavigation = new();
    private static bool IsNativePage(string page) => page is "内嵌翻译" or "主页" or "设置";
    private void InitializeNativeShell()
    {
        try{_libraryPreferences=LibraryPreferencesStore.Load();}catch(Exception ex){AppLog.Write("library-settings",SafeDiagnosticOutput.ExceptionSummary(ex));}
        foreach(var pair in new[]{("内嵌翻译","游戏库"),("主页","截图翻译"),("设置","设置")})_nativeNavigation[pair.Item1]=new(pair.Item2,Font);
        _libraryNavigation.Navigate+=NavigateLibrary;_libraryNavigation.ContextRequested+=ShowLibraryFolderContext;
        FormClosed+=(_,_)=>_folderContext.Dispose();
        InitializeNativeScreenshotWorkspace();InitializeNativeLibrary();
        DesktopStatus="就绪";_currentMainPage="内嵌翻译";
        _trayIcon.Text="游戏翻译 · Fusion R1";
        _gameStateTimer.Tick+=(_,_)=>{UpdateGameDiagnosticSnapshots();if(DesktopVisible&&!_workspaceResizing&&_currentMainPage=="内嵌翻译"){RefreshGameState();_=RefreshOtherGameSessionsAsync();_=RefreshLibraryWindowNamesAsync();_=RefreshGameDataStatusAsync();}};
        _gameStateTimer.Start();FormClosed+=(_,_)=>_gameStateTimer.Dispose();
    }
    private void RefreshNativeFusionChrome()
    {
        _wpfWindow?.RefreshChrome();if(_fusion is null)return;
        UpdateGalleryChrome();var embedded=EmbeddedSettings;
        var plan=FusionConfiguration.EditIdentity(embedded)+"|"+_fusion.State.EmbeddedToggleKey;
        if(_gamePlanIdentity!=plan){_gamePlanIdentity=plan;InvalidateGameState();if(_desktopReady)ScheduleLibraryRefresh();}
    }
}
