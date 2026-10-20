namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private sealed record PageLocation(string Page,string Game,int Section,int Settings,string Scope,string Tag,string Collection,string Engine,string Folder,int Filter,string Search);
    private PageNavigationController<PageLocation>? _pageNavigator;
    private PageNavigationController<PageLocation> PageNavigator
    {
        get
        {
            if (_pageNavigator is not null) return _pageNavigator;
            _pageNavigator = new(CurrentLocation, RestorePageLocationAsync,
                action => BeginInvoke(action), ShowFusionError);
            Disposed += (_, _) => _pageNavigator.Dispose();
            return _pageNavigator;
        }
    }
    private PageLocation CurrentLocation()
    {
        bool library=_currentMainPage=="内嵌翻译",detail=library&&(_wpfWindow is not null?_wpfDetails:_gameSessionPage?.Visible==true);
        return new(_currentMainPage,detail?_sessionGamePath??"":"",
            detail&&_selectedGame is {} game?_gameSessions.Get(game).Section:0,
            _currentMainPage=="设置"?_lastSettingsCategory:-1,
            library?_libraryScope:"",library?_libraryTag:"",library?_libraryCollection:"",
            library?LibraryEngineChoice?.ToString()??"全部引擎":"",
            library?LibraryFolderChoice?.ToString()??"全部文件夹":"",
            library?LibraryFilterIndex:-1,library?LibrarySearchText:"");
    }
    private void RecordPageLocation()
    {
        if (!IsDisposed && !Disposing) PageNavigator.Record(IsHandleCreated);
    }
    private bool HandlePageMouse(ref Message message)
    {
        if(message.Msg is not (0x020B or 0x020C)||_bindingCaptureState!=BindingCaptureState.Idle||_captureOpen
            ||DesktopInputRuntime.GetForegroundWindow()!=Handle||!HasLegacyWindow||!ReferenceEquals(Control.FromChildHandle(message.HWnd)?.FindForm(),LegacyWindow))return false;
        int button=(int)((message.WParam.ToInt64()>>16)&0xffff);
        if(button is not (1 or 2))return false;
        if(message.Msg==0x020C)_=MovePageHistoryAsync(button==1?-1:1);
        return true;
    }
    internal bool HandleNativePageMouse(System.Windows.Input.MouseButton button, bool released)
    {
        // WPF routes side buttons through the native window preview events.
        // Consume both halves before a canvas can mistake Mouse 4/5 for a click,
        // and keep navigation on release, as in the original desktop UI.
        if (button is not (System.Windows.Input.MouseButton.XButton1 or System.Windows.Input.MouseButton.XButton2)
            || _wpfWindow is not { IsActive: true, IsEnabled: true }
            || _bindingCaptureState != BindingCaptureState.Idle || _captureOpen) return false;
        if (released) _ = MovePageHistoryAsync(button == System.Windows.Input.MouseButton.XButton1 ? -1 : 1);
        return true;
    }
    private Task MovePageHistoryAsync(int direction) => PageNavigator.MoveAsync(direction);
    private async Task<bool> RestorePageLocationAsync(PageLocation target)
    {
        ShowPage(target.Page);if(_currentMainPage!=target.Page)return false;
        if(target.Page=="设置")
        {
            WpfSelectSettings(target.Settings);
            if(_lastSettingsCategory!=target.Settings)return false;
        }
        else if(target.Page=="内嵌翻译")
        {
            _libraryFiltersLoading=true;
            try
            {
                _libraryScope=target.Scope;_libraryTag=target.Tag;_libraryCollection=target.Collection;
                LibraryEngineChoice=target.Engine;LibraryFolderChoice=target.Folder;
                LibraryFilterIndex=target.Filter;LibrarySearchText=target.Search;
            }
            finally{_libraryFiltersLoading=false;}
            if(target.Game.Length>0)
            {
                await OpenLibraryGameAsync(target.Game);
                if(IsDisposed || !SameGame(target.Game,_selectedGame?.ExePath))return false;
                SelectGameSessionSection(target.Section);
            }
            else ShowLibraryHome();
            ScheduleLibraryRefresh();
        }
        return true;
    }
}
