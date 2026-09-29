namespace ScreenshotTranslationUiTester;

// Single-column measurement keeps nested auto-sized editor rows at their content height.
// FlowLayoutPanel otherwise retains a height measured before its final column width.
internal sealed class WorkspaceContentColumn:TableLayoutPanel
{
    private readonly HashSet<RowStyle> measured=[];
    private bool measuring;
    private readonly Dictionary<Control,(int Width,int Height)> sizes=[];
    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        if(e.Control is not {} child)return;
        child.SizeChanged+=ChildChanged;child.TextChanged+=ChildChanged;child.FontChanged+=ChildChanged;child.VisibleChanged+=ChildChanged;
        child.Layout+=ChildLayout;
    }
    protected override void OnControlRemoved(ControlEventArgs e)
    {
        if(e.Control is {} child){sizes.Remove(child);child.SizeChanged-=ChildChanged;child.TextChanged-=ChildChanged;child.FontChanged-=ChildChanged;child.VisibleChanged-=ChildChanged;child.Layout-=ChildLayout;}
        base.OnControlRemoved(e);
    }
    private void ChildChanged(object? sender,EventArgs e){if(sender is Control c)sizes.Remove(c);}
    private void ChildLayout(object? sender,LayoutEventArgs e){if(sender is Control c&&e.AffectedProperty is not "Bounds" and not "Location")sizes.Remove(c);}
    internal WorkspaceContentColumn(){ColumnCount=1;RowCount=0;ColumnStyles.Add(new(SizeType.Percent,100));Margin=Padding.Empty;Dock=DockStyle.Fill;AutoSize=true;AutoSizeMode=AutoSizeMode.GrowAndShrink;}
    public override Size GetPreferredSize(Size proposedSize)
    {
        int width=proposedSize.Width>0?proposedSize.Width:Math.Max(1,Width);
        if(!AutoSize)return base.GetPreferredSize(proposedSize);
        MeasureRows(width);
        return new(width,Padding.Vertical+(int)Math.Ceiling(RowStyles.Cast<RowStyle>().Sum(s=>s.SizeType==SizeType.Absolute?s.Height:0)));
    }
    protected override void OnLayout(LayoutEventArgs e){MeasureRows(Math.Max(1,Width));base.OnLayout(e);}
    private void MeasureRows(int width)
    {
        if(measuring||!AutoSize)return;measuring=true;
        try
        {
            for(int i=0;i<RowStyles.Count;i++)
            {
                var row=RowStyles[i];
                if(row.SizeType==SizeType.AutoSize)measured.Add(row);
                if(!measured.Contains(row))continue;
                var child=GetControlFromPosition(0,i);
                int available=Math.Max(1,width-Padding.Horizontal-(child?.Margin.Horizontal??0));
                float height=0;
                if(child is not null&&child.Visible)
                {
                    if(!sizes.TryGetValue(child,out var size)||size.Width!=available)
                    {size=(available,ContentHeight(child,available));sizes[child]=size;}
                    height=size.Height+child.Margin.Vertical;
                }
                row.SizeType=SizeType.Absolute;if(Math.Abs(row.Height-height)>.1f)row.Height=height;
            }
        }
        finally{measuring=false;}
    }
    private static int ContentHeight(Control control,int width)
    {
        if(control is GameActionButton button)return Math.Max(button.LogicalHeight*button.DeviceDpi/96,button.Font.Height+8*button.DeviceDpi/96);
        if(control is NumericUpDown number)return number.PreferredHeight;
        if(control is WorkspaceAlignedRow aligned)
            return Math.Max(36*control.DeviceDpi/96,aligned.Controls.Cast<Control>().Where(c=>c.Visible).Select(c=>ContentHeight(c,Math.Min(width,Math.Max(1,c.Width)))+c.Margin.Vertical).DefaultIfEmpty(0).Max());
        if(control is FlowLayoutPanel flow)
        {
            int x=0,line=0,total=flow.Padding.Vertical;int available=Math.Max(1,width-flow.Padding.Horizontal);
            foreach(Control child in flow.Controls)
            {
                if(!child.Visible)continue;
                int w=child.Width+child.Margin.Horizontal,h=ContentHeight(child,Math.Max(1,child.Width))+child.Margin.Vertical;
                if(flow.WrapContents&&x>0&&x+w>available){total+=line;x=0;line=0;}
                x+=w;line=Math.Max(line,h);
            }
            return total+line;
        }
        if(control.AutoSize)return control.GetPreferredSize(new(width,0)).Height;
        return control.Height;
    }
}
