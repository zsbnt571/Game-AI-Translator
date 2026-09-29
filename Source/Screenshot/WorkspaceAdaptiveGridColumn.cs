namespace ScreenshotTranslationUiTester;

// The list owns the spare viewport height. Toolbars and the editor retain their
// measured height; expanding help or narrowing the window may scroll the page.
internal sealed class WorkspaceAdaptiveGridColumn:TableLayoutPanel
{
    internal Control? List { get; set; }
    private bool measuring;
    private WorkspaceScrollView? viewport;
    internal WorkspaceAdaptiveGridColumn()
    {
        ColumnCount=1;ColumnStyles.Add(new(SizeType.Percent,100));AutoSize=true;
        AutoSizeMode=AutoSizeMode.GrowAndShrink;Dock=DockStyle.Top;Margin=Padding.Empty;
        SetStyle(ControlStyles.OptimizedDoubleBuffer|ControlStyles.AllPaintingInWmPaint,true);
    }
    protected override void OnParentChanged(EventArgs e){base.OnParentChanged(e);FindViewport();}
    protected override void OnVisibleChanged(EventArgs e){base.OnVisibleChanged(e);FindViewport();}
    private void FindViewport()
    {
        var next=Parents(this).OfType<WorkspaceScrollView>().FirstOrDefault();
        if(ReferenceEquals(next,viewport))return;
        if(viewport is not null)viewport.SizeChanged-=ViewportChanged;
        viewport=next;if(viewport is not null)viewport.SizeChanged+=ViewportChanged;
    }
    private void ViewportChanged(object? sender,EventArgs e)
    {
        // Preferred height depends on the viewport, not on an auto-sized parent.
        PerformLayout();Parent?.PerformLayout();viewport?.PerformLayout();
    }
    internal static IEnumerable<Control> Parents(Control child)
    {for(var parent=child.Parent;parent is not null;parent=parent.Parent)yield return parent;}
    public override Size GetPreferredSize(Size proposedSize)
    {
        int width=proposedSize.Width>0?proposedSize.Width:Math.Max(1,Width);
        if(measuring||List is null)return base.GetPreferredSize(proposedSize);
        measuring=true;
        try
        {
            FindViewport();int used=Padding.Vertical,listRow=-1;
            for(int i=0;i<RowStyles.Count;i++)
            {
                var child=GetControlFromPosition(0,i);
                if(ReferenceEquals(child,List)){listRow=i;continue;}
                int h=child is not null&&child.Visible?MeasureHeight(child,Math.Max(1,width-Padding.Horizontal-child.Margin.Horizontal))+child.Margin.Vertical:0;
                RowStyles[i].SizeType=SizeType.Absolute;RowStyles[i].Height=h;used+=h;
            }
            int budget=viewport?.ClientSize.Height??0;
            if(viewport is not null)
            {
                Control branch=this;
                for(var parent=Parent;parent is not null;branch=parent,parent=parent.Parent)
                {
                    budget-=parent.Padding.Vertical+branch.Margin.Vertical;
                    if(ReferenceEquals(parent,viewport))break;
                    // Session columns are vertical; sibling rows precede/follow the list.
                    foreach(Control sibling in parent.Controls)
                        if(sibling.Visible&&!ReferenceEquals(sibling,branch))
                            budget-=MeasureHeight(sibling,Math.Max(1,parent.ClientSize.Width-parent.Padding.Horizontal-sibling.Margin.Horizontal))+sibling.Margin.Vertical;
                }
            }
            int min=220*DeviceDpi/96;
            int listHeight=Math.Max(min,budget-used-List.Margin.Vertical);
            if(listRow>=0){RowStyles[listRow].SizeType=SizeType.Absolute;RowStyles[listRow].Height=listHeight+List.Margin.Vertical;}
            return new(width,used+listHeight+List.Margin.Vertical);
        }
        finally{measuring=false;}
    }
    protected override void OnLayout(LayoutEventArgs e)
    {if(!measuring)GetPreferredSize(new(Math.Max(1,Width),0));base.OnLayout(e);}
    internal static int MeasureHeight(Control control,int width)
    {
        if(control is GameActionButton button)return GameActionButton.RowHeight(button,button.LogicalHeight);
        // A fill-docked label may have been clipped by its previous row. Its
        // current Height is not a stable measurement for the next layout pass.
        if(control is Label&&!control.AutoSize)return Math.Max(control.MinimumSize.Height,control.Font.Height+6*control.DeviceDpi/96);
        if(control is FlowLayoutPanel flow)
        {
            int used=0,line=0,total=flow.Padding.Vertical;
            foreach(Control child in flow.Controls)
            {
                if(!child.Visible)continue;
                int w=child.Width+child.Margin.Horizontal,h=MeasureHeight(child,Math.Max(1,child.Width))+child.Margin.Vertical;
                if(flow.WrapContents&&used>0&&used+w>width-flow.Padding.Horizontal){total+=line;used=0;line=0;}
                used+=w;line=Math.Max(line,h);
            }
            return total+line;
        }
        return control.AutoSize?control.GetPreferredSize(new(width,0)).Height:control.Height;
    }
    protected override void Dispose(bool disposing)
    {if(disposing&&viewport is not null)viewport.SizeChanged-=ViewportChanged;base.Dispose(disposing);}
}
