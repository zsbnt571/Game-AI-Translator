namespace ScreenshotTranslationUiTester;

internal interface ILibraryGridSession : IDisposable
{
    bool ShelfMode { get; set; }
    bool Portrait { get; set; }
    int CoverWidth { get; set; }
    bool ListMode { get; }
    string EmptyText { get; set; }
    int DeviceDpi { get; }
    int ShelfContentHeight { get; }
    RecentGame[] SelectedGames { get; }
    System.Windows.FrameworkElement NativeView { get; }
    event Action<RecentGame>? GameSelected;
    event Action<RecentGame>? GameActivated;
    event Action<RecentGame,Point>? ContextRequested;
    void UpdateRunning(IEnumerable<string> paths);
    void SetListMode(bool value);
    void SetTiles(LibraryTile[] tiles,string? selectedPath,Func<RecentGame,(Bitmap? Cover,Bitmap? Icon)>? loader=null);
    void SelectPath(string? path);
    void ScrollToStart();
    Point PointToScreen(Point point);
    Point PointToClient(Point point);
}
