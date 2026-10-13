namespace ScreenshotTranslationUiTester;

internal sealed class GameDataIcon:Control
{
    internal Image? Atlas;
    internal int Index=-1;
    internal GameDataIcon(){DoubleBuffered=true;MinimumSize=new(64,64);}
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);if(Atlas is null||Index<0||((Index/16)+1)*32>Atlas.Height)return;
        e.Graphics.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode=System.Drawing.Drawing2D.PixelOffsetMode.Half;
        e.Graphics.DrawImage(Atlas,new Rectangle(4*DeviceDpi/96,4*DeviceDpi/96,56*DeviceDpi/96,56*DeviceDpi/96),new Rectangle(Index%16*32,Index/16*32,32,32),GraphicsUnit.Pixel);
    }
}

internal sealed class WorkspaceNumberBox:NumericUpDown
{
    internal WorkspaceNumberBox()
    {
        BorderStyle=BorderStyle.FixedSingle;
        Controls[0].Paint+=(_,e)=>
        {
            var p=UiTheme.Current;var r=Controls[0].ClientRectangle;using var b=new SolidBrush(p.Control);e.Graphics.FillRectangle(b,r);
            using var pen=new Pen(p.Border);e.Graphics.DrawLine(pen,0,0,0,r.Height);int d=Math.Max(1,DeviceDpi/96);
            using var ink=new Pen(Enabled?p.Text:p.SecondaryText,Math.Max(1,d));float x=r.Width/2f,y=r.Height/4f;
            e.Graphics.DrawLines(ink,new PointF[]{new(x-3*d,y+d),new(x,y-2*d),new(x+3*d,y+d)});
            y=r.Height*3/4f;e.Graphics.DrawLines(ink,new PointF[]{new(x-3*d,y-d),new(x,y+2*d),new(x+3*d,y-d)});
        };
    }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if(e is HandledMouseEventArgs handled)handled.Handled=true;
        for(Control? parent=Parent;parent is not null;parent=parent.Parent)if(parent is WorkspaceScrollView scroll){scroll.ScrollWheel(e.Delta);return;}
    }
}

// Keep the editable value and its action button on the same 36-dip control rhythm.
internal sealed class WorkspaceNumberField:Panel
{
    private readonly NumericUpDown number;
    private readonly Panel editorViewport=new(){Margin=Padding.Empty,TabStop=false};
    internal WorkspaceNumberField(NumericUpDown value)
    {
        number=value;Size=new(value.Width,36);Margin=new(0,2,8,2);TabStop=false;
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        value.BorderStyle=BorderStyle.None;value.Margin=Padding.Empty;value.Dock=DockStyle.None;
        // Keep the native composite editor intact. Hiding its spin child leaves a
        // black native-window slot on screen even when DrawToBitmap looks clean.
        // Clip that child outside the viewport; the themed arrows are drawn here.
        editorViewport.Controls.Add(value);Controls.Add(editorViewport);
        value.FontChanged+=(_,_)=>PerformLayout();value.EnabledChanged+=(_,_)=>Invalidate();
        value.BackColorChanged+=(_,_)=>editorViewport.BackColor=value.BackColor;
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);if(number is null)return;int d=DeviceDpi;
        editorViewport.Bounds=new(10*d/96,Math.Max(0,(Height-number.PreferredHeight)/2),Math.Max(12,Width-42*d/96),number.PreferredHeight);
        int spinWidth=number.Controls.Cast<Control>().Where(c=>c is not TextBox).Select(c=>c.Width).DefaultIfEmpty(SystemInformation.VerticalScrollBarWidth).Max();
        number.Bounds=new(0,0,editorViewport.Width+spinWidth+2*d/96,number.PreferredHeight);
        number.PerformLayout();
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);if(e.Button!=MouseButtons.Left||!number.Enabled)return;number.Focus();
        if(e.X>=Width-28*DeviceDpi/96)number.Value=Math.Clamp(number.Value+(e.Y<Height/2?number.Increment:-number.Increment),number.Minimum,number.Maximum);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);var p=UiTheme.Current;var g=e.Graphics;g.Clear(Parent?.BackColor??p.Main);g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var shape=WorkspaceDrawing.Round(Rectangle.Inflate(ClientRectangle,-1,-1),6*DeviceDpi/96);using var fill=new SolidBrush(p.Secondary);using var border=new Pen(p.Border);
        g.FillPath(fill,shape);g.DrawPath(border,shape);int split=Width-28*DeviceDpi/96;g.DrawLine(border,split,4*DeviceDpi/96,split,Height-4*DeviceDpi/96);
        float d=DeviceDpi/96f,x=split+(Width-split)/2f,y=Height/4f;using var ink=new Pen(Enabled?p.Text:p.SecondaryText,Math.Max(1,d));
        g.DrawLines(ink,new PointF[]{new(x-3*d,y+d),new(x,y-2*d),new(x+3*d,y+d)});y=Height*3/4f;
        g.DrawLines(ink,new PointF[]{new(x-3*d,y-d),new(x,y+2*d),new(x+3*d,y-d)});
    }
}
