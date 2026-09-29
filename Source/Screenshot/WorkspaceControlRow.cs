namespace ScreenshotTranslationUiTester;

// Native combo heights settle only after handle creation. Match the other controls
// to that final height and centre labels without introducing larger cards.
internal sealed class WorkspaceControlRow:FlowLayoutPanel
{
    private bool arranging;
    internal WorkspaceControlRow(){DoubleBuffered=true;}
    internal static void MatchHeights(IEnumerable<Control> children)
    {
        var inputs=children.Where(c=>c.Visible&&c is GameActionButton or GamePlanBox or WorkspaceNumberField or WorkspaceSwitch).ToArray();
        if(inputs.Length==0)return;
        foreach(var combo in inputs.OfType<GamePlanBox>())combo.RefreshMetrics();
        int height=inputs.Max(c=>c is GamePlanBox?c.Height:GameActionButton.RowHeight(c,c is GameActionButton b?b.LogicalHeight:inputs.OfType<GameActionButton>().FirstOrDefault()?.LogicalHeight??28));
        foreach(var c in inputs)if(c is not GamePlanBox&&c.Height!=height)c.Height=height;
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        if(arranging){base.OnLayout(e);return;}arranging=true;
        try
        {
            MatchHeights(Controls.Cast<Control>());base.OnLayout(e);
            foreach(var line in Controls.Cast<Control>().Where(c=>c.Visible).GroupBy(c=>c.Top-c.Margin.Top))
            {
                int height=line.Max(c=>c.Height+c.Margin.Vertical);
                foreach(var c in line)c.Top=line.Key+(height-c.Height-c.Margin.Vertical)/2+c.Margin.Top;
            }
        }
        finally{arranging=false;}
    }
}
