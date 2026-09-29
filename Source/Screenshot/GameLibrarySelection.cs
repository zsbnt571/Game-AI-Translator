namespace ScreenshotTranslationUiTester;

internal sealed partial class GameLibraryGrid
{
    private HashSet<string> selection=new(StringComparer.Ordinal);
    private HashSet<string> boxBefore=[];
    private int selectionAnchor=-1;
    private bool selecting;
    private Point boxStart,boxPointer;
    private Rectangle selectionBox;
    private System.Windows.Forms.Timer? boxScrollTimer;
    private static bool SamePath(string? a,string? b)=>string.Equals(a,b,StringComparison.OrdinalIgnoreCase);
    internal RecentGame[] SelectedGames=>tiles.Where(t=>selection.Contains(t.Game.Id)).Select(t=>t.Game).ToArray();
    internal void SelectIndex(int index,Keys modifiers)
    {
        if(index<0||index>=tiles.Length)return;
        if((modifiers&Keys.Shift)!=0&&selectionAnchor>=0&&selectionAnchor<tiles.Length)
        {if((modifiers&Keys.Control)==0)selection.Clear();for(int i=Math.Min(index,selectionAnchor);i<=Math.Max(index,selectionAnchor);i++)selection.Add(tiles[i].Game.Id);}
        else if((modifiers&Keys.Control)!=0){if(!selection.Add(tiles[index].Game.Id))selection.Remove(tiles[index].Game.Id);selectionAnchor=index;}
        else{selection.Clear();selection.Add(tiles[index].Game.Id);selectionAnchor=index;}
        selected=tiles[index].Game.ExePath;Invalidate();GameSelected?.Invoke(tiles[index].Game);
    }
    private void BeginBoxSelection(Point point,Keys modifiers)
    {
        boxBefore=(modifiers&Keys.Control)!=0?new(selection):[];
        selection=new(boxBefore);boxStart=new(point.X+scrollOffset.X,point.Y+scrollOffset.Y);boxPointer=point;
        selecting=true;Capture=true;selectionBox=Rectangle.Empty;
        boxScrollTimer??=new(){Interval=40};boxScrollTimer.Tick-=BoxScrollTick;boxScrollTimer.Tick+=BoxScrollTick;boxScrollTimer.Start();Invalidate();
    }
    private void BoxScrollTick(object? sender,EventArgs e)
    {
        if(!selecting){boxScrollTimer?.Stop();return;}
        int position=ShelfMode?boxPointer.X:boxPointer.Y,extent=ShelfMode?ContentViewport.Width:ContentViewport.Height;
        int step=position<S(18)?-S(16):position>extent-S(18)?S(16):0;
        if(step!=0){SetScrollValue(ScrollValue+step);UpdateBoxSelection(boxPointer);}
    }
    private void UpdateBoxSelection(Point point)
    {
        boxPointer=point;var end=new Point(point.X+scrollOffset.X,point.Y+scrollOffset.Y);
        var box=Rectangle.FromLTRB(Math.Min(boxStart.X,end.X),Math.Min(boxStart.Y,end.Y),Math.Max(boxStart.X,end.X),Math.Max(boxStart.Y,end.Y));
        selectionBox=new(box.X-scrollOffset.X,box.Y-scrollOffset.Y,box.Width,box.Height);selection=new(boxBefore);
        for(int i=0;i<tiles.Length;i++)if(selectionBox.IntersectsWith(BoundsFor(i)))selection.Add(tiles[i].Game.Id);
        Invalidate();
    }
    private void PaintSelectionRectangle(Graphics g)
    {
        if(!selecting||selectionBox.Width<1||selectionBox.Height<1)return;
        using var fill=new SolidBrush(Color.FromArgb(35,UiTheme.Current.Accent));using var border=new Pen(UiTheme.Current.Accent);
        g.FillRectangle(fill,selectionBox);g.DrawRectangle(border,selectionBox);
    }
}
