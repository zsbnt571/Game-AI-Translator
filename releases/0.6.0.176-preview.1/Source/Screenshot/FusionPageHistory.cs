namespace ScreenshotTranslationUiTester;

internal sealed class PageHistory<T> where T:notnull
{
    private readonly List<T> pages=[];
    private int index=-1;
    internal void Visit(T page)
    {
        if(index>=0&&EqualityComparer<T>.Default.Equals(pages[index],page))return;
        if(index+1<pages.Count)pages.RemoveRange(index+1,pages.Count-index-1);
        pages.Add(page);index++;
        if(pages.Count>100){pages.RemoveAt(0);index--;}
    }
    internal bool TryPeek(int direction,out T page)
    {int next=index+direction;if(direction is -1 or 1&&next>=0&&next<pages.Count){page=pages[next];return true;}page=default!;return false;}
    internal void Moved(int direction){if(TryPeek(direction,out _))index+=direction;}
}

public sealed partial class MainForm
{
    private sealed record PageLocation(string Page,string Game,int Section,int Settings,string Scope,string Tag,string Collection,string Engine,string Folder,int Filter,string Search);
    private readonly PageHistory<PageLocation> _pageHistory=new();
    private bool _historyPending,_historyMoving;
    private PageLocation CurrentLocation()
    {
        bool library=_currentMainPage=="内嵌翻译",detail=library&&_gameSessionPage?.Visible==true;
        return new(_currentMainPage,detail?_sessionGamePath??"":"",
            detail&&_selectedGame is {} game?_gameSessions.Get(game).Section:0,
            _currentMainPage=="设置"?_lastSettingsCategory:-1,
            library?_libraryScope:"",library?_libraryTag:"",library?_libraryCollection:"",
            library?_libraryEngine.SelectedItem?.ToString()??"全部引擎":"",
            library?_libraryFolder.SelectedItem?.ToString()??"全部文件夹":"",
            library?_libraryFilter.SelectedIndex:-1,library?_librarySearch.Text:"");
    }
    private void RecordPageLocation()
    {
        if(_historyMoving||IsDisposed||Disposing||_historyPending)return;
        if(!IsHandleCreated){_pageHistory.Visit(CurrentLocation());return;}
        _historyPending=true;
        BeginInvoke((Action)(()=>{_historyPending=false;if(!IsDisposed&&!_historyMoving)_pageHistory.Visit(CurrentLocation());}));
    }
    private bool HandlePageMouse(ref Message message)
    {
        if(message.Msg is not (0x020B or 0x020C)||_bindingCaptureState!=BindingCaptureState.Idle||_captureOpen
            ||DesktopInputRuntime.GetForegroundWindow()!=Handle||Control.FromChildHandle(message.HWnd)?.FindForm()!=this)return false;
        int button=(int)((message.WParam.ToInt64()>>16)&0xffff);
        if(button is not (1 or 2))return false;
        if(message.Msg==0x020C)_=MovePageHistoryAsync(button==1?-1:1);
        return true;
    }
    private async Task MovePageHistoryAsync(int direction)
    {
        if(_historyMoving)return;
        _pageHistory.Visit(CurrentLocation());
        if(!_pageHistory.TryPeek(direction,out var target))return;
        _historyMoving=true;
        try
        {
            ShowPage(target.Page);if(_currentMainPage!=target.Page)return;
            if(target.Page=="设置")
            {
                _settingsNavigation.SelectedIndex=target.Settings;
                if(_lastSettingsCategory!=target.Settings)return;
            }
            else if(target.Page=="内嵌翻译")
            {
                _libraryFiltersLoading=true;
                try
                {
                    _libraryScope=target.Scope;_libraryTag=target.Tag;_libraryCollection=target.Collection;
                    _libraryEngine.SelectedItem=target.Engine;_libraryFolder.SelectedItem=target.Folder;
                    _libraryFilter.SelectedIndex=target.Filter;_librarySearch.Text=target.Search;
                }
                finally{_libraryFiltersLoading=false;}
                if(target.Game.Length>0)
                {
                    await OpenLibraryGameAsync(target.Game);
                    if(!SameGame(target.Game,_selectedGame?.ExePath))return;
                    SelectGameSessionSection(target.Section);
                }
                else ShowLibraryHome();
                ScheduleLibraryRefresh();
            }
            _pageHistory.Moved(direction);
        }
        catch(Exception error){ShowFusionError(error);}
        finally{_historyMoving=false;}
    }
}
