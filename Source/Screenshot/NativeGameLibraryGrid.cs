namespace ScreenshotTranslationUiTester;

internal sealed partial class NativeGameLibraryGrid:ILibraryGridSession
{
    private LibraryTile[] tiles=[];
    private string? selected;
    private int hover=-1;
    private HashSet<string> runningPaths=new(StringComparer.OrdinalIgnoreCase);
    public void UpdateRunning(IEnumerable<string> paths){var next=paths.ToHashSet(StringComparer.OrdinalIgnoreCase);if(!runningPaths.SetEquals(next)){runningPaths=next;Invalidate();}}
    public bool ShelfMode {get;set;}
    public bool Portrait {get;set;}=true;
    public int CoverWidth {get;set;}=158;
    public bool ListMode {get;private set;}
    public void SetListMode(bool value){ListMode=value;ScrollToStart();Reflow();Invalidate();}
    public event Action<RecentGame>? GameSelected;
    public event Action<RecentGame>? GameActivated;
    public event Action<RecentGame,Point>? ContextRequested;
    public string EmptyText {get;set;}="把游戏拖到这里\n或点击右上角“导入游戏”";
    internal NativeGameLibraryGrid(){InitializeWpfCanvas();}
    public void SetTiles(LibraryTile[] next,string? selectedPath,Func<RecentGame,(Bitmap? Cover,Bitmap? Icon)>? loader=null){var old=tiles;RetainTileImages(next,loader);tiles=next;selected=selectedPath;selection.IntersectWith(next.Select(t=>t.Game.Id));if(!selecting&&old.Length==0&&selection.Count==0&&next.FirstOrDefault(t=>SamePath(t.Game.ExePath,selectedPath)) is {} chosen)selection.Add(chosen.Game.Id);foreach(var tile in old)tile.Dispose();Reflow();Invalidate();}
    public void SelectPath(string? path){if(SamePath(selected,path))return;selected=path;if(tiles.FirstOrDefault(t=>SamePath(t.Game.ExePath,path)) is {} tile&&!selection.Contains(tile.Game.Id)){selection.Clear();selection.Add(tile.Game.Id);}Invalidate();}
    private int S(int n)=>(int)Math.Round(n*DeviceDpi/96f);
    // Reserve a stable scrollbar lane so appearing/disappearing scrollbars cannot
    // change the column count. Share remaining pixels across columns, not a blank strip.
    private int GridWidth=>Math.Max(1,ViewportSize.Width-S(16));
    private int Columns=>ListMode?1:ShelfMode?Math.Max(1,tiles.Length):Math.Max(1,(GridWidth+S(12))/(S(LibraryCoverSizes.Normalize(CoverWidth))+S(12)));
    private int ListWidth=>GridWidth;
    private int ListCoverWidth=>Math.Min(Math.Max(S(72),ListWidth-S(180)),(int)Math.Round(S(LibraryCoverSizes.ListWidths[LibraryCoverSizes.IndexFor(CoverWidth)])*Math.Clamp(ListWidth/(double)S(1200),.9,1.2)));
    private int ListCoverHeight=>(int)Math.Round(ListCoverWidth*9d/16);
    private int ShelfCellWidth(int viewportWidth)=>Math.Min(Math.Max(1,viewportWidth-S(16)),S(LibraryCoverSizes.ShelfWidths[LibraryCoverSizes.IndexFor(CoverWidth)]));
    private int CoverCellHeight(int width)=>(int)(width*(Portrait&&!ShelfMode?1.24f:.52f))+Math.Max(S(52),fontPixelHeight*2+S(12));
    private Size Cell
    {
        get
        {
            int width=ShelfMode?ShelfCellWidth(ViewportSize.Width):ListMode?ListWidth:Math.Max(1,(GridWidth-(Columns-1)*S(12))/Columns);
            return new(width,ListMode?Math.Max(ListCoverHeight+S(12),fontPixelHeight*3+S(24)):CoverCellHeight(width));
        }
    }
    private int ShelfContentWidthForCell(int cellWidth)=>S(4)+tiles.Length*cellWidth+Math.Max(0,tiles.Length-1)*S(12);
    private int ShelfContentWidth=>ShelfContentWidthForCell(Cell.Width);
    public int ShelfContentHeight=>ShelfHeightForWidth(ViewportSize.Width);
    private int ShelfHeightForWidth(int viewportWidth)
    {
        if(tiles.Length==0)return S(64);
        int width=ShelfCellWidth(viewportWidth);
        return CoverCellHeight(width)+S(6)+(ShelfContentWidthForCell(width)>viewportWidth?S(12):0);
    }
    private Rectangle BoundsFor(int i)
    {
        var c=Cell;int column=i%Columns;
        int remainder=ListMode||ShelfMode?0:Math.Max(0,GridWidth-(Columns-1)*S(12)-Columns*c.Width);
        return new(S(2)+(ListMode?Math.Max(0,(GridWidth-c.Width)/2):0)+column*(c.Width+S(12))+Math.Min(column,remainder)-scrollOffset.X,
            S(3)+(i/Columns)*(c.Height+S(12))+(pageScrollEnabled?pageLeadingHeight:0)-scrollOffset.Y,c.Width+(column<remainder?1:0),c.Height);
    }
    private void Reflow(){resizeAnchor=null;pageHeaderResizeAnchor=null;UpdateScrollRange();wpfCanvas?.InvalidateMeasure();Invalidate();NotifyPageScroll();}

    private int Hit(Point point){if(!ContentViewport.Contains(point))return -1;foreach(int i in VisibleTileIndices(ContentViewport))if(BoundsFor(i).Contains(point))return i;return -1;}
    private void OnMouseMove(MouseEventArgs e){if(selecting){UpdateBoxSelection(e.Location);return;}if(MoveScroll(e.Location))return;var i=Hit(e.Location);if(i!=hover){hover=i;Cursor=i>=0?Cursors.Hand:Cursors.Default;Invalidate();}}
    private void OnMouseLeave(EventArgs e){hover=-1;scrollHover=false;Invalidate();}
    private void OnMouseDown(MouseEventArgs e)
    {if(e.Button is not (MouseButtons.Left or MouseButtons.Right))return;Focus();if(BeginScroll(e))return;int i=Hit(e.Location);if(e.Button==MouseButtons.Right){if(i>=0&&!selection.Contains(tiles[i].Game.Id))SelectIndex(i,Keys.None);return;}if(i<0){BeginBoxSelection(e.Location,Control.ModifierKeys);return;}SelectIndex(i,Control.ModifierKeys);}
    private void OnMouseUp(MouseEventArgs e){if(selecting&&e.Button==MouseButtons.Left){UpdateBoxSelection(e.Location);selecting=false;Capture=false;Invalidate();return;}if(EndScroll())return;int i=Hit(e.Location);if(e.Button==MouseButtons.Right&&i>=0)ContextRequested?.Invoke(tiles[i].Game,e.Location);}
    private void OnMouseDoubleClick(MouseEventArgs e){int i=Hit(e.Location);if(e.Button==MouseButtons.Left&&i>=0)GameActivated?.Invoke(tiles[i].Game);}
    private void Choose(int index)=>SelectIndex(index,Keys.None);
    private void OnKeyDown(KeyEventArgs e)
    {
        if(tiles.Length==0){if(pageScrollEnabled&&e.KeyCode is Keys.Home or Keys.End){SetScrollValue(e.KeyCode==Keys.Home?0:scrollMaximum);e.Handled=e.SuppressKeyPress=true;}return;}if(e.Control&&e.KeyCode==Keys.A){selection=tiles.Select(t=>t.Game.Id).ToHashSet();Invalidate();e.Handled=e.SuppressKeyPress=true;return;}if(e.KeyCode==Keys.Escape){selection.Clear();Invalidate();e.Handled=true;return;}
        int i=Array.FindIndex(tiles,x=>StringComparer.OrdinalIgnoreCase.Equals(x.Game.ExePath,selected));
        if(i>=0&&e.KeyCode==Keys.Enter){GameActivated?.Invoke(tiles[i].Game);e.Handled=e.SuppressKeyPress=true;return;}
        if(i>=0&&(e.KeyCode==Keys.Apps||e.Shift&&e.KeyCode==Keys.F10)){var r=BoundsFor(i);ContextRequested?.Invoke(tiles[i].Game,new(r.Left+S(20),r.Top+S(20)));e.Handled=e.SuppressKeyPress=true;return;}
        int page=Math.Max(1,(ShelfMode?ViewportSize.Width:ViewportSize.Height)/(ShelfMode?Cell.Width:Cell.Height))*(ShelfMode?1:Columns);
        int n=e.KeyCode switch{Keys.Left=>i-1,Keys.Right=>i+1,Keys.Up=>i-Columns,Keys.Down=>i+Columns,Keys.Home=>0,Keys.End=>tiles.Length-1,Keys.PageUp=>i-page,Keys.PageDown=>i+page,_=>i};
        if(e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown)
        {n=Math.Clamp(n,0,tiles.Length-1);if(n!=i)SelectIndex(n,e.Modifiers);if(pageScrollEnabled&&e.KeyCode is Keys.Home or Keys.End)SetScrollValue(e.KeyCode==Keys.Home?0:scrollMaximum);else EnsureTileVisible(n);e.Handled=e.SuppressKeyPress=true;}
    }
    public void Dispose(){if(!IsDisposed){IsDisposed=true;wpfCanvas?.StopWheelMotion();ResetTileImages(null);DisposeInactiveImages();boxScrollTimer?.Stop();foreach(var tile in tiles)tile.Dispose();tiles=[];}}
}
