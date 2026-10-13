using System.Drawing.Drawing2D;

namespace ScreenshotTranslationUiTester;

internal sealed class WorkspaceThemeButton : Button
{
    private bool moon=true,hover,pressed;
    internal bool ShowMoon { get=>moon;set { if(moon==value)return;moon=value;Invalidate(); } }
    internal WorkspaceThemeButton()
    {
        Size=new(36,36);FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;
        AccessibleName="切换到夜间模式";Text="";
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
    }
    protected override void OnMouseEnter(EventArgs e){base.OnMouseEnter(e);hover=true;Invalidate();}
    protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);hover=false;pressed=false;Invalidate();}
    protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(e.Button==MouseButtons.Left){pressed=true;Invalidate();}}
    protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);pressed=false;Invalidate();}
    protected override void OnPaint(PaintEventArgs e)
    {
        var p=UiTheme.Current;var g=e.Graphics;g.Clear(p.Main);g.SmoothingMode=SmoothingMode.AntiAlias;
        int S(int n)=>(int)Math.Round(n*DeviceDpi/96f);
        if(hover||pressed)
        {
            using var shape=WorkspaceDrawing.Round(Rectangle.Inflate(ClientRectangle,-S(2),-S(2)),S(7));
            using var fill=new SolidBrush(pressed?WorkspaceSkin.SoftAccent:p.Control);g.FillPath(fill,shape);
        }
        int size=S(20);WorkspaceSkin.Icon(g,moon?"moon":"sun",new((Width-size)/2,(Height-size)/2,size,size),hover?p.Accent:p.Text);
        if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(g,Rectangle.Inflate(ClientRectangle,-S(4),-S(4)));
    }
}
