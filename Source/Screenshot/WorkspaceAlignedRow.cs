namespace ScreenshotTranslationUiTester;

// One row shares a vertical centre regardless of native input heights or font metrics.
internal sealed class WorkspaceAlignedRow:TableLayoutPanel
{
    private bool sizing,positioning;
    internal WorkspaceAlignedRow(params Control[] controls)
    {
        AutoSize=true;AutoSizeMode=AutoSizeMode.GrowAndShrink;ColumnCount=controls.Length;RowCount=1;Margin=Padding.Empty;
        RowStyles.Add(new(SizeType.AutoSize));
        for(int i=0;i<controls.Length;i++)
        {
            var control=controls[i];ColumnStyles.Add(new(SizeType.AutoSize));control.Dock=DockStyle.None;control.Anchor=AnchorStyles.Left;
            control.Margin=new(0,0,i==controls.Length-1?0:10,0);
            if(control is Label label)label.TextAlign=ContentAlignment.MiddleLeft;
            Controls.Add(control,i,0);control.SizeChanged+=(_,_)=>UpdateHeight();control.VisibleChanged+=(_,_)=>UpdateHeight();
        }
        UpdateHeight();
    }
    internal void FlexibleColumn(int column,int maximumWidth)
    {
        ColumnStyles[column].SizeType=SizeType.Percent;ColumnStyles[column].Width=100;
        var control=GetControlFromPosition(column,0)!;control.Anchor=AnchorStyles.Left|AnchorStyles.Right;control.MaximumSize=new(maximumWidth,0);
    }
    private void UpdateHeight()
    {
        if(sizing)return;sizing=true;
        try{MinimumSize=new(0,Math.Max(36*DeviceDpi/96,Controls.Cast<Control>().Where(x=>x.Visible).Select(x=>x.Height+x.Margin.Vertical).DefaultIfEmpty(0).Max()));}
        finally{sizing=false;}
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if(positioning)return;positioning=true;
        try
        {
            WorkspaceControlRow.MatchHeights(Controls.Cast<Control>());
            // Native ComboBox can reject the height requested by TableLayoutPanel.
            // Centre using its final physical bounds, not the layout engine's cached height.
            foreach(Control control in Controls)
                control.Top=Padding.Top+Math.Max(0,(ClientSize.Height-Padding.Vertical-control.Height-control.Margin.Vertical)/2)+control.Margin.Top;
        }
        finally{positioning=false;}
    }
    protected override void OnDpiChangedAfterParent(EventArgs e){base.OnDpiChangedAfterParent(e);UpdateHeight();}
}
