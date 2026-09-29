namespace ScreenshotTranslationUiTester;

// A committed page, a hovered row and native keyboard focus are distinct states.
internal sealed class FusionNavigation : ListBox
{
    private int committed=-1,hover=-1;
    internal FusionNavigation()
    {
        Dock=DockStyle.Fill;BorderStyle=BorderStyle.None;IntegralHeight=false;
        SelectionMode=SelectionMode.One;DrawMode=DrawMode.OwnerDrawFixed;
        AccessibleName="设置分类";UpdateRowHeight();
    }
    internal void Commit(int index)
    {
        var old=committed;committed=index;
        Repaint(old);Repaint(index);
    }
    private void Repaint(int index){if(IsHandleCreated&&index>=0&&index<Items.Count)Invalidate(GetItemRectangle(index));}
    private void UpdateRowHeight()=>ItemHeight=Math.Max(Font.Height+14*DeviceDpi/96,38*DeviceDpi/96);
    protected override void OnFontChanged(EventArgs e){base.OnFontChanged(e);UpdateRowHeight();}
    protected override void OnDpiChangedAfterParent(EventArgs e){base.OnDpiChangedAfterParent(e);UpdateRowHeight();}
    protected override void OnMouseMove(MouseEventArgs e)
    {base.OnMouseMove(e);var next=IndexFromPoint(e.Location);if(next==hover)return;var old=hover;hover=next;Repaint(old);Repaint(hover);}
    protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);var old=hover;hover=-1;Repaint(old);}
    protected override void OnGotFocus(EventArgs e){base.OnGotFocus(e);Repaint(SelectedIndex);}
    protected override void OnLostFocus(EventArgs e){base.OnLostFocus(e);Repaint(SelectedIndex);}
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if(e.Index<0||e.Index>=Items.Count)return;
        var p=UiTheme.Current;
        var selected=e.Index==committed&&(e.State&DrawItemState.Selected)!=0;
        var bg=selected?p.Control:e.Index==hover?p.Secondary:p.Main;
        using var brush=new SolidBrush(bg);e.Graphics.FillRectangle(brush,e.Bounds);
        var inset=12*DeviceDpi/96;
        TextRenderer.DrawText(e.Graphics,Items[e.Index].ToString(),Font,
            new Rectangle(e.Bounds.X+inset,e.Bounds.Y,Math.Max(0,e.Bounds.Width-inset*2),e.Bounds.Height),
            Enabled?(selected?p.Accent:p.Text):p.SecondaryText,
            TextFormatFlags.VerticalCenter|TextFormatFlags.Left|TextFormatFlags.EndEllipsis);
        if(selected){using var accent=new SolidBrush(p.Accent);e.Graphics.FillRectangle(accent,e.Bounds.X,e.Bounds.Y,Math.Max(2,3*DeviceDpi/96),e.Bounds.Height);}
        if(Focused&&(e.State&DrawItemState.Focus)!=0)ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(e.Bounds,-2,-2),p.Text,bg);
    }
}
