namespace ScreenshotTranslationUiTester;

// A shared data-list surface. Header and scroll chrome use the same palette as
// the surrounding workspace instead of Windows' white native scroll track.
internal sealed class WorkspaceDataGrid:DataGridView
{
    internal int ActiveSortColumn {get;set;}=-1;
    internal bool SortDescending {get;set;}
    private bool dragging;
    private int dragAnchor;
    internal WorkspaceDataGrid()
    {
        DoubleBuffered=true;ScrollBars=ScrollBars.None;BorderStyle=BorderStyle.None;
        CellBorderStyle=DataGridViewCellBorderStyle.SingleHorizontal;
        ColumnHeadersBorderStyle=DataGridViewHeaderBorderStyle.None;EnableHeadersVisualStyles=false;
        ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        Scroll+=(_,_)=>Invalidate();RowsAdded+=(_,_)=>Invalidate();RowsRemoved+=(_,_)=>Invalidate();
    }
    private int S(int n)=>Math.Max(1,n*DeviceDpi/96);
    internal void ApplyPalette()
    {
        var p=UiTheme.Current;int h=Math.Max(S(26),Font.Height+S(8));bool resize=RowTemplate.Height!=h;
        if(resize){RowTemplate.Height=h;foreach(DataGridViewRow row in Rows)if(row.Height!=h)row.Height=h;}
        int header=Math.Max(S(32),Font.Height+S(12));if(ColumnHeadersHeight!=header)ColumnHeadersHeight=header;
        if(Columns.Count>0)Columns[Columns.Count-1].DefaultCellStyle.Padding=new(0,0,S(14),0);
        if(!resize&&BackgroundColor==p.Control&&GridColor==p.Border&&DefaultCellStyle.ForeColor==p.Text&&ColumnHeadersDefaultCellStyle.BackColor==p.Secondary)return;
        BackgroundColor=p.Control;GridColor=p.Border;
        DefaultCellStyle.BackColor=p.Control;DefaultCellStyle.ForeColor=p.Text;
        DefaultCellStyle.SelectionBackColor=p.Accent;DefaultCellStyle.SelectionForeColor=Color.White;
        ColumnHeadersDefaultCellStyle.BackColor=p.Secondary;ColumnHeadersDefaultCellStyle.ForeColor=p.Text;
        ColumnHeadersDefaultCellStyle.SelectionBackColor=p.Secondary;ColumnHeadersDefaultCellStyle.SelectionForeColor=p.Text;
        Invalidate();
    }
    protected override void OnFontChanged(EventArgs e){base.OnFontChanged(e);if(Columns is not null)ApplyPalette();}
    protected override void OnDpiChangedAfterParent(EventArgs e){base.OnDpiChangedAfterParent(e);ApplyPalette();}
    protected override void OnCellPainting(DataGridViewCellPaintingEventArgs e)
    {
        if(e.RowIndex!=-1||e.ColumnIndex<0){base.OnCellPainting(e);return;}
        var p=UiTheme.Current;var bounds=e.CellBounds;
        bool sorted=e.ColumnIndex==ActiveSortColumn;
        using(var brush=new SolidBrush(sorted?WorkspaceSkin.SoftAccent:p.Secondary))e.Graphics!.FillRectangle(brush,bounds);
        int right=sorted?S(28):S(8);
        var text=new Rectangle(bounds.Left+S(8),bounds.Top,Math.Max(0,bounds.Width-S(8)-right),bounds.Height-S(2));
        TextRenderer.DrawText(e.Graphics,Columns[e.ColumnIndex].HeaderText,Font,text,sorted?p.Accent:p.Text,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
        using(var pen=new Pen(p.Border))e.Graphics!.DrawLine(pen,bounds.Left,bounds.Bottom-1,bounds.Right,bounds.Bottom-1);
        if(sorted)
        {
            int x=bounds.Right-S(17),y=bounds.Top+bounds.Height/2;
            Point[] points=SortDescending?[new(x-S(5),y-S(3)),new(x+S(5),y-S(3)),new(x,y+S(4))]:[new(x-S(5),y+S(3)),new(x+S(5),y+S(3)),new(x,y-S(4))];
            using var accent=new SolidBrush(p.Accent);e.Graphics!.FillPolygon(accent,points);
            // Header tint and arrow express sorting without merging into the blue selected row.
        }
        e.Handled=true;
    }
    private int VisibleRows=>Math.Max(1,DisplayedRowCount(false));
    private Rectangle Track=>new(Math.Max(0,Width-S(13)),ColumnHeadersHeight,S(13),Math.Max(0,Height-ColumnHeadersHeight));
    private Rectangle Thumb
    {
        get
        {
            var t=Track;int visible=VisibleRows,h=Math.Min(t.Height,Math.Max(S(30),t.Height*visible/Math.Max(1,Rows.Count)));
            int y=t.Top+(t.Height-h)*Math.Max(0,FirstDisplayedScrollingRowIndex)/Math.Max(1,Rows.Count-visible);
            return new(t.X+S(4),y,S(5),h);
        }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);if(Rows.Count<=VisibleRows)return;
        var p=UiTheme.Current;using(var track=new SolidBrush(p.Control))e.Graphics.FillRectangle(track,Track);
        var thumb=Thumb;if(thumb.Height<=0)return;
        using var shape=WorkspaceDrawing.Round(thumb,S(2));using var brush=new SolidBrush(dragging?p.Accent:p.SecondaryText);e.Graphics.FillPath(brush,shape);
    }
    private void ScrollTo(int first)
    {if(Rows.Count>0)FirstDisplayedScrollingRowIndex=Math.Clamp(first,0,Math.Max(0,Rows.Count-VisibleRows));Invalidate();}
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if(e.Button==MouseButtons.Left&&Rows.Count>VisibleRows&&Track.Contains(e.Location))
        {
            var thumb=Thumb;if(thumb.Contains(e.Location)){dragging=true;dragAnchor=e.Y-thumb.Y;Capture=true;}
            else ScrollTo(Math.Max(0,FirstDisplayedScrollingRowIndex)+(e.Y<thumb.Y?-VisibleRows:VisibleRows));
            return;
        }
        if(e.Button==MouseButtons.Left&&HitTest(e.X,e.Y).Type==DataGridViewHitTestType.None)
        {Focus();ClearSelection();CurrentCell=null;return;}
        base.OnMouseDown(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if(e.KeyCode==Keys.Escape){ClearSelection();CurrentCell=null;e.Handled=e.SuppressKeyPress=true;return;}
        base.OnKeyDown(e);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if(dragging){var t=Track;var thumb=Thumb;ScrollTo((int)((long)Math.Clamp(e.Y-t.Y-dragAnchor,0,Math.Max(0,t.Height-thumb.Height))*Math.Max(0,Rows.Count-VisibleRows)/Math.Max(1,t.Height-thumb.Height)));return;}
        base.OnMouseMove(e);
    }
    protected override void OnMouseUp(MouseEventArgs e){if(dragging){dragging=false;Capture=false;Invalidate();return;}base.OnMouseUp(e);}
    protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(!Capture)dragging=false;}
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if(e is HandledMouseEventArgs h)h.Handled=true;
        int count=SystemInformation.MouseWheelScrollLines<0?VisibleRows:Math.Max(1,SystemInformation.MouseWheelScrollLines);
        ScrollTo(Math.Max(0,FirstDisplayedScrollingRowIndex)-Math.Sign(e.Delta)*Math.Max(1,Math.Abs(e.Delta)/120)*count);
    }
}
