namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private void InitializeNativeLibrary(Panel? legacyPage=null)
    {
        BuildGameSessionWorkspace(legacyPage!);
        _libraryGrid.Portrait=_libraryPreferences.Portrait;
        _libraryGrid.CoverWidth=_recentShelf.CoverWidth=_libraryPreferences.CoverWidth;
        foreach(var grid in new[]{_libraryGrid,_recentShelf})
        {
            grid.GameSelected+=game=>SelectFusionGame(game.ExePath,recordSelection:false);
            grid.GameActivated+=async game=>await OpenLibraryGameAsync(game.ExePath);
            grid.ContextRequested+=(game,point)=>{_libraryContextSource=grid;ShowLibraryGameContextMenu(game,point,null,false,grid.NativeView);};
        }
        _libraryRefreshTimer.Tick+=async(_,_)=>{_libraryRefreshTimer.Stop();await RefreshLibraryAsync();};
        InitializeRecentUi();RenderGameState();Shown+=(_,_)=>ScheduleLibraryRefresh();
        FormClosed+=(_,_)=>
        {
            _libraryContextMenu.Dispose();_gameLaunchMenu.Dispose();_sectionMenu.Dispose();
            _libraryRefreshTimer.Stop();_libraryRefreshTimer.Dispose();
            if(_librarySubscribedStore is not null)_librarySubscribedStore.Changed-=LibraryStoreChanged;
            _libraryGrid.Dispose();_recentShelf.Dispose();_libraryNavigation.Dispose();GameCoverImage?.Dispose();GameCoverImage=null;
            _libraryCoverCache.Clear();_libraryArtworkSource=null;
        };
    }
    internal void NativeLibraryPresentation()
    {
        if(_libraryNavigation is NativeLibraryNavigation navigation)navigation.UpdatePresentation(Font,DeviceDpi);
        foreach(var session in new[]{_libraryGrid,_recentShelf})
            if(session is NativeGameLibraryGrid native)native.UpdatePresentation(Font,DeviceDpi);
    }
}
