namespace ScreenshotTranslationUiTester;

// A quiet, live divider; no native XOR drag line or persistent focus rectangle.
internal sealed class WorkspaceLibrarySplit:Panel
{
    private bool arranging,dragging,hover,ready;
    private int distance,grab;
    internal Panel Panel1 {get;}=new WorkspaceSplitPane();
    internal Panel Panel2 {get;}=new WorkspaceSplitPane();
    internal int LogicalSidebarWidth {get;set;}=206;
    internal event Action<int>? WidthCommitted;
    private int Gap=>Math.Max(4,4*DeviceDpi/96);
    private Rectangle Divider=>new(distance,0,Gap,Height);
    internal int SplitterDistance {get=>distance;set=>ChangeWidth(value,true);}
    internal WorkspaceLibrarySplit()
    {
        TabStop=true;AccessibleName="调整侧栏宽度";AccessibleRole=AccessibleRole.Separator;
        SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.AllPaintingInWmPaint|ControlStyles.ResizeRedraw|ControlStyles.Selectable,true);
        // The divider cursor is local to this surface; content must not inherit it.
        Panel1.Cursor=Cursors.Default;Panel2.Cursor=Cursors.Default;
        Controls.Add(Panel2);Controls.Add(Panel1);
    }
    private int ClampWidth(int value){int max=Math.Max(0,Math.Min(460*DeviceDpi/96,Width-400*DeviceDpi/96-Gap));return Math.Clamp(value,Math.Min(190*DeviceDpi/96,max),max);}
    private void ChangeWidth(int value,bool commit)
    {
        int next=ClampWidth(value);if(next==distance)return;distance=next;LogicalSidebarWidth=Math.Clamp((int)Math.Round(next*96d/DeviceDpi),190,460);PerformLayout();Invalidate();
        if(commit&&ready)WidthCommitted?.Invoke(LogicalSidebarWidth);
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        bool changed=false;
        if(!arranging&&Panel1 is not null&&Width>0)
        {
            arranging=true;Panel1.SuspendLayout();Panel2.SuspendLayout();
            try
            {
                distance=ClampWidth(LogicalSidebarWidth*DeviceDpi/96);
                var left=new Rectangle(0,0,distance,Height);var right=new Rectangle(distance+Gap,0,Math.Max(0,Width-distance-Gap),Height);
                changed=Panel1.Bounds!=left||Panel2.Bounds!=right;
                Panel1.Bounds=left;Panel2.Bounds=right;ready=true;
            }
            finally{Panel1.ResumeLayout(true);Panel2.ResumeLayout(true);arranging=false;}
        }
        base.OnLayout(e);
        // Layout can run several times within one resize message. Invalidate
        // here and let the workspace present the completed child layout once.
        if(changed)Invalidate(true);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        using(var fill=new SolidBrush(UiTheme.Sidebar))e.Graphics.FillRectangle(fill,Divider);
        {using var pen=new Pen(hover||dragging?Color.FromArgb(150,UiTheme.Current.Accent):UiTheme.Current.Border,Math.Max(1,DeviceDpi/96f));int x=distance+Gap-1;e.Graphics.DrawLine(pen,x,0,x,Height);}
    }
    private void ResetDividerCursor(){Cursor=Cursors.Default;hover=false;Invalidate(Divider);}
    protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(e.Button!=MouseButtons.Left||!Divider.Contains(e.Location))return;Focus();dragging=true;grab=e.X-distance;Capture=true;Cursor=Cursors.VSplit;Invalidate(Divider);}
    protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(dragging){ChangeWidth(e.X-grab,false);return;}bool next=Divider.Contains(e.Location);Cursor=next?Cursors.VSplit:Cursors.Default;if(hover!=next){hover=next;Invalidate(Divider);}}
    protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);if(!dragging)ResetDividerCursor();else{hover=false;Invalidate(Divider);}}
    protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);if(e.Button==MouseButtons.Left&&dragging){dragging=false;Capture=false;ResetDividerCursor();WidthCommitted?.Invoke(LogicalSidebarWidth);}}
    protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(!Capture){ResetDividerCursor();if(dragging){dragging=false;WidthCommitted?.Invoke(LogicalSidebarWidth);}}}
    protected override bool IsInputKey(Keys keyData)=>(keyData&Keys.KeyCode) is Keys.Left or Keys.Right||base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e){base.OnKeyDown(e);if(e.KeyCode is Keys.Left or Keys.Right){SplitterDistance=distance+(e.KeyCode==Keys.Left?-10:10)*DeviceDpi/96;e.Handled=true;}}
    protected override void OnDpiChangedAfterParent(EventArgs e){base.OnDpiChangedAfterParent(e);PerformLayout();}
}
public sealed partial class MainForm
{
    private WorkspaceLibrarySplit? _librarySplit;
}

internal sealed class LibrarySidebar:TableLayoutPanel
{internal LibrarySidebar(){SetStyle(ControlStyles.OptimizedDoubleBuffer|ControlStyles.AllPaintingInWmPaint,true);}}
