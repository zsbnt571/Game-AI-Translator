namespace ScreenshotTranslationUiTester;

internal sealed partial class GameLibraryGrid
{
    private Point scrollOffset;
    private int scrollMaximum,scrollGrab;
    private bool scrollDragging,scrollHover;
    internal bool HasScrollBar=>scrollMaximum>0;
    internal Point ScrollOffset=>scrollOffset;
    internal int ScrollMaximum=>scrollMaximum;
    private int ScrollValue=>ShelfMode?scrollOffset.X:scrollOffset.Y;
    private Rectangle ContentViewport=>new(0,0,Math.Max(0,ClientSize.Width-(!ShelfMode&&HasScrollBar?S(12):0)),Math.Max(0,ClientSize.Height-(ShelfMode&&HasScrollBar?S(12):0)));
    private Rectangle ScrollTrack=>!HasScrollBar?Rectangle.Empty:ShelfMode?
        new(S(4),ClientSize.Height-S(12),Math.Max(0,ClientSize.Width-S(8)),S(12)):
        new(ClientSize.Width-S(12),S(4),S(12),Math.Max(0,ClientSize.Height-S(8)));
    private Rectangle ScrollThumb
    {
        get
        {
            var track=ScrollTrack;if(track.IsEmpty)return Rectangle.Empty;
            int length=ShelfMode?track.Width:track.Height,viewport=ShelfMode?ContentViewport.Width:ContentViewport.Height;
            int thumb=Math.Clamp((int)((long)length*viewport/Math.Max(1,viewport+scrollMaximum)),Math.Min(S(32),length),length);
            int start=(int)((long)(length-thumb)*ScrollValue/Math.Max(1,scrollMaximum));
            return ShelfMode?new(track.X+start,track.Y,thumb,track.Height):new(track.X,track.Y+start,track.Width,thumb);
        }
    }
    private void UpdateScrollRange()
    {
        int rows=(int)Math.Ceiling((double)tiles.Length/Columns);
        int total=ShelfMode?ShelfContentWidth:S(6)+rows*Cell.Height+Math.Max(0,rows-1)*S(12);
        scrollMaximum=tiles.Length==0?0:Math.Max(0,total-(ShelfMode?ClientSize.Width:ClientSize.Height));
        int value=Math.Clamp(ScrollValue,0,scrollMaximum);scrollOffset=ShelfMode?new(value,0):new(0,value);
    }
    private void SetScrollValue(int value)
    {
        value=Math.Clamp(value,0,scrollMaximum);var next=ShelfMode?new Point(value,0):new Point(0,value);
        if(next==scrollOffset)return;scrollOffset=next;hover=-1;Invalidate();
    }
    private void PaintScrollBar(Graphics graphics)
    {
        if(!HasScrollBar)return;var track=ScrollTrack;var thumb=ScrollThumb;var p=UiTheme.Current;
        using(var background=new SolidBrush(p.Main))graphics.FillRectangle(background,track);
        int thickness=S(scrollDragging||scrollHover?6:4);
        var ink=ShelfMode?new Rectangle(thumb.X,thumb.Y+(thumb.Height-thickness)/2,thumb.Width,thickness):new Rectangle(thumb.X+(thumb.Width-thickness)/2,thumb.Y,thickness,thumb.Height);
        using var shape=WorkspaceDrawing.Round(ink,Math.Max(1,thickness/2));
        using var brush=new SolidBrush(Color.FromArgb(scrollDragging?230:scrollHover?185:115,p.SecondaryText));graphics.FillPath(brush,shape);
    }
    private bool BeginScroll(MouseEventArgs e)
    {
        if(!ScrollTrack.Contains(e.Location))return false;
        if(e.Button!=MouseButtons.Left)return true;
        var thumb=ScrollThumb;int position=ShelfMode?e.X:e.Y;
        if(thumb.Contains(e.Location))scrollGrab=position-(ShelfMode?thumb.X:thumb.Y);
        else
        {
            scrollGrab=(ShelfMode?thumb.Width:thumb.Height)/2;
            SetScrollFromPointer(position);thumb=ScrollThumb;scrollGrab=position-(ShelfMode?thumb.X:thumb.Y);
        }
        scrollDragging=true;Capture=true;Cursor=Cursors.Default;Invalidate();return true;
    }
    private void SetScrollFromPointer(int position)
    {
        var track=ScrollTrack;var thumb=ScrollThumb;int start=ShelfMode?track.X:track.Y;
        int travel=ShelfMode?track.Width-thumb.Width:track.Height-thumb.Height;
        SetScrollValue((int)Math.Clamp((long)(position-start-scrollGrab)*scrollMaximum/Math.Max(1,travel),0,scrollMaximum));
    }
    private bool MoveScroll(Point point)
    {
        bool hot=ScrollTrack.Contains(point);if(scrollHover!=hot){scrollHover=hot;Invalidate();}
        if(scrollDragging){SetScrollFromPointer(ShelfMode?point.X:point.Y);return true;}
        if(hot){hover=-1;Cursor=Cursors.Default;return true;}return false;
    }
    private bool EndScroll()
    {
        if(!scrollDragging)return false;scrollDragging=false;Capture=false;Invalidate();return true;
    }
    private void EnsureTileVisible(int index)
    {
        var tile=BoundsFor(index);var view=ContentViewport;
        if(ShelfMode)
        {if(tile.Left<view.Left)SetScrollValue(ScrollValue+tile.Left-view.Left-S(2));else if(tile.Right>view.Right)SetScrollValue(ScrollValue+tile.Right-view.Right+S(2));}
        else
        {if(tile.Top<view.Top)SetScrollValue(ScrollValue+tile.Top-view.Top-S(3));else if(tile.Bottom>view.Bottom)SetScrollValue(ScrollValue+tile.Bottom-view.Bottom+S(3));}
    }
    protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(!Capture){scrollDragging=false;selecting=false;boxScrollTimer?.Stop();Invalidate();}}
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if(e is HandledMouseEventArgs h)h.Handled=true;if(!HasScrollBar)return;
        int lines=SystemInformation.MouseWheelScrollLines;
        if(lines==0)return;
        int step=lines<0?(ShelfMode?ContentViewport.Width:ContentViewport.Height):Math.Max(S(24),Font.Height)*lines;
        SetScrollValue(ScrollValue-(int)Math.Round((double)e.Delta/120*step));
        if(e is HandledMouseEventArgs handled)handled.Handled=true;
    }
    protected override void WndProc(ref Message message)
    {
        const int MouseHorizontalWheel=0x020e;
        if(message.Msg==MouseHorizontalWheel&&ShelfMode&&HasScrollBar)
        {int delta=(short)((message.WParam.ToInt64()>>16)&0xffff);SetScrollValue(ScrollValue+(int)Math.Round(delta/120d*S(72)));message.Result=IntPtr.Zero;return;}
        base.WndProc(ref message);
    }
    protected override void OnDpiChangedAfterParent(EventArgs e){base.OnDpiChangedAfterParent(e);Reflow();}
}
