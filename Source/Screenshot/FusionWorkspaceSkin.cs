using System.Drawing.Drawing2D;

namespace ScreenshotTranslationUiTester;

internal static class WorkspaceSkin
{
    internal static Color SoftAccent=>UiTheme.Current==ThemePalette.Day?Color.FromArgb(230,241,255):Color.FromArgb(39,64,93);
    internal static void Icon(Graphics g,string key,Rectangle box,Color color)
    {
        if(box.Width<2||box.Height<2)return;
        var state=g.Save();g.TranslateTransform(box.X,box.Y);g.ScaleTransform(box.Width/24f,box.Height/24f);
        using var p=new Pen(color,1.65f){StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round};
        using var b=new SolidBrush(color);
        void Line(params PointF[] points)=>g.DrawLines(p,points);
        switch(key)
        {
            case "brand":
                using(var gradient=new LinearGradientBrush(new Rectangle(0,0,24,24),Color.FromArgb(105,215,255),Color.FromArgb(0,91,255),90f))
                {g.FillPolygon(gradient,new Point[] {new(1,8),new(12,1),new(23,8),new(23,11),new(12,18),new(1,11)});g.FillPolygon(gradient,new Point[] {new(1,14),new(12,21),new(23,14),new(23,18),new(12,25),new(1,18)});}
                g.FillPolygon(Brushes.White,new Point[] {new Point(8,8),new(12,5),new(16,8),new(12,11)});break;
            case "game":
                Line(new(5,5),new(9,7),new(15,7),new(19,5),new(22,17),new(21,20),new(18,20),new(14,15),new(10,15),new(6,20),new(3,20),new(2,17),new(5,5));
                Line(new(5,10),new(9,10));Line(new(7,8),new(7,12));g.FillEllipse(b,16,8,2,2);g.FillEllipse(b,18,11,2,2);break;
            case "capture":
                Line(new(2,7),new(2,2),new(7,2));Line(new(17,2),new(22,2),new(22,7));Line(new(22,17),new(22,22),new(17,22));Line(new(7,22),new(2,22),new(2,17));g.DrawRectangle(p,6,8,4,7);g.DrawRectangle(p,13,8,4,7);break;
            case "settings":
                for(int i=0;i<8;i++){double a=i*Math.PI/4;Line(new(12+(float)Math.Cos(a)*8,12+(float)Math.Sin(a)*8),new(12+(float)Math.Cos(a)*10,12+(float)Math.Sin(a)*10));}
                g.DrawEllipse(p,4,4,16,16);g.DrawEllipse(p,9,9,6,6);break;
            case "search":g.DrawEllipse(p,3,3,13,13);Line(new(14,14),new(21,21));break;
            case "moon":
                using(var moon=new GraphicsPath())
                {
                    moon.AddBezier(20.7f,14.2f,19.7f,18.2f,16.4f,21,12,21);
                    moon.AddBezier(12,21,7,21,3,17,3,12);
                    moon.AddBezier(3,12,3,7.6f,5.8f,4.3f,9.8f,3.3f);
                    moon.AddBezier(9.8f,3.3f,8.2f,6.6f,8.9f,10.1f,11.3f,12.5f);
                    moon.AddBezier(11.3f,12.5f,13.7f,14.9f,17.4f,15.8f,20.7f,14.2f);
                    moon.CloseFigure();g.DrawPath(p,moon);
                }
                break;
            case "sun":
                g.DrawEllipse(p,7,7,10,10);
                for(int i=0;i<8;i++){double a=i*Math.PI/4;Line(new(12+(float)Math.Cos(a)*8,12+(float)Math.Sin(a)*8),new(12+(float)Math.Cos(a)*10.5f,12+(float)Math.Sin(a)*10.5f));}
                break;
            case "folder":Line(new(3,6),new(3,20),new(21,20),new(21,7),new(11,7),new(9,4),new(3,4),new(3,6));break;
            case "camera":g.DrawRectangle(p,2,6,20,15);Line(new(6,6),new(8,3),new(15,3),new(17,6));g.DrawEllipse(p,8,9,8,8);break;
            case "image":g.DrawRectangle(p,3,3,18,18);Line(new(4,18),new(10,11),new(14,15),new(17,10),new(21,14));g.DrawEllipse(p,7,6,3,3);break;
            case "plus":Line(new(12,4),new(12,20));Line(new(4,12),new(20,12));break;
            case "minus":Line(new(4,12),new(20,12));break;
            case "down":Line(new(6,9),new(12,15),new(18,9));break;
            case "check":Line(new(5,12),new(10,17),new(20,6));break;
            case "close":Line(new(5,5),new(19,19));Line(new(19,5),new(5,19));break;
            case "next":Line(new(9,5),new(16,12),new(9,19));break;
            case "back":Line(new(15,5),new(8,12),new(15,19));break;
            case "grid":foreach(int x in new[]{3,14})foreach(int y in new[]{3,14})g.DrawRectangle(p,x,y,7,7);break;
            case "list":foreach(int y in new[]{5,12,19}){g.FillEllipse(b,2,y-1,2,2);Line(new(7,y),new(21,y));}break;
            case "play":g.FillPolygon(b,new Point[] {new Point(6,3),new(21,12),new(6,21)});break;
            case "tool":Line(new(14,3),new(10,7),new(10,12),new(2,20),new(4,22),new(12,14),new(17,14),new(22,9),new(18,10),new(14,6),new(14,3));break;
            case "translate":Line(new(2,5),new(14,5));Line(new(7,2),new(9,5));Line(new(12,5),new(9,12),new(3,17));Line(new(5,8),new(12,16));Line(new(13,21),new(18,10),new(23,21));Line(new(15,17),new(21,17));break;
            case "export":Line(new(3,15),new(3,21),new(21,21),new(21,15));Line(new(12,17),new(12,2));Line(new(6,8),new(12,2),new(18,8));break;
            case "copy":g.DrawRectangle(p,7,7,14,15);Line(new(16,3),new(3,3),new(3,17));break;
            case "edit":Line(new(3,17),new(3,22),new(8,21),new(22,7),new(17,2),new(3,17));Line(new(14,5),new(19,10));break;
            case "refresh":
                // One open circular arrow, with a visible head at small sizes.
                g.DrawArc(p,4,4,16,16,35,290);
                Line(new(13.2f,7.4f),new(18.6f,7.4f),new(18.6f,2.4f));break;
            case "expand":Line(new(14,3),new(21,3),new(21,10));Line(new(21,3),new(14,10));Line(new(3,14),new(3,21),new(10,21));Line(new(3,21),new(10,14));break;
            case "calendar":g.DrawRectangle(p,3,5,18,17);Line(new(3,10),new(21,10));Line(new(7,2),new(7,7));Line(new(17,2),new(17,7));break;
            case "tag":Line(new(3,3),new(13,3),new(22,12),new(12,22),new(3,13),new(3,3));g.DrawEllipse(p,7,7,3,3);break;
            case "trash":Line(new(4,6),new(20,6));Line(new(9,6),new(9,3),new(15,3),new(15,6));Line(new(6,6),new(7,22),new(17,22),new(18,6));Line(new(10,10),new(10,18));Line(new(14,10),new(14,18));break;
        }
        g.Restore(state);
    }
}

internal sealed class WorkspaceBrand:Control
{
    internal WorkspaceBrand(){Height=80;SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.AllPaintingInWmPaint,true);AccessibleName="FUSION R1 · 你的游戏工作台";}
    protected override void OnPaint(PaintEventArgs e)
    {
        float d=DeviceDpi/96f;int S(int n)=>(int)(n*d);var p=UiTheme.Current;e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        WorkspaceSkin.Icon(e.Graphics,"brand",new(S(10),S(5),S(35),S(35)),p.Accent);
        using var title=new Font(Font.FontFamily,13.5f,FontStyle.Bold);using var sub=new Font(Font.FontFamily,9f);
        WorkspaceDrawing.Text(e.Graphics,"FUSION R1",title,new(S(57),S(3),Width-S(58),S(26)),p.Text);
        WorkspaceDrawing.Text(e.Graphics,"你的游戏工作台",sub,new(S(57),S(29),Width-S(58),S(23)),p.SecondaryText);
    }
}

internal sealed class WorkspaceNavButton:Button
{
    internal string IconKey {get;set;}="game";
    internal bool Selected {get;set;}
    private bool hover;
    internal WorkspaceNavButton(){FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;SetStyle(ControlStyles.OptimizedDoubleBuffer,true);}
    protected override void OnMouseEnter(EventArgs e){base.OnMouseEnter(e);hover=true;Invalidate();}
    protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);hover=false;Invalidate();}
    protected override void OnPaint(PaintEventArgs e)
    {
        int S(int n)=>n*DeviceDpi/96;var p=UiTheme.Current;var g=e.Graphics;g.Clear(Parent?.BackColor??p.Main);g.SmoothingMode=SmoothingMode.AntiAlias;
        if(Selected||hover){using var path=WorkspaceDrawing.Round(Rectangle.Inflate(ClientRectangle,-1,-1),S(6));using var b=new SolidBrush(Selected?WorkspaceSkin.SoftAccent:p.Control);g.FillPath(b,path);}
        var color=Selected?p.Accent:p.Text;WorkspaceSkin.Icon(g,IconKey,new(S(16),(Height-S(22))/2,S(22),S(22)),color);
        WorkspaceDrawing.Text(g,Text,Font,new(S(54),0,Width-S(58),Height),color);
        if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(g,Rectangle.Inflate(ClientRectangle,-4,-4));
    }
}

internal sealed class WorkspaceTab:Button
{
    internal bool Selected {get;set;}
    internal WorkspaceTab(string text){Text=text;FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;Height=38;}
    protected override void OnPaint(PaintEventArgs e)
    {
        var p=UiTheme.Current;e.Graphics.Clear(Parent?.BackColor??p.Main);var r=ClientRectangle;
        WorkspaceDrawing.Text(e.Graphics,Text,Font,r,Enabled?(Selected?p.Accent:p.Text):p.SecondaryText,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
        using var pen=new Pen(Selected?p.Accent:p.Border,Selected?2*DeviceDpi/96:1);e.Graphics.DrawLine(pen,0,Height-2,Width,Height-2);
    }
}

internal sealed class WorkspaceCover:PictureBox
{
    internal event PaintEventHandler? OverlayPaint;
    internal void PaintBackdrop(Graphics graphics)
    {
        var r=ClientRectangle;if(r.Width<=0||r.Height<=0)return;
        // FillRectangle respects the translated/dirty child clip; Clear and
        // the native PictureBox background pipeline do not provide that scope.
        using(var background=new SolidBrush(BackColor))graphics.FillRectangle(background,r);
        var state=graphics.Save();
        try
        {
            using var clip=WorkspaceDrawing.Round(r,6*DeviceDpi/96);
            graphics.SetClip(clip,CombineMode.Intersect);
            if(Image is {} image)
            {
                double scale=Math.Max((double)Width/image.Width,(double)Height/image.Height);
                int width=(int)Math.Ceiling(image.Width*scale),height=(int)Math.Ceiling(image.Height*scale);
                graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(image,new Rectangle((Width-width)/2,(Height-height)/2,width,height));
            }
            using var gradient=new LinearGradientBrush(r,Color.FromArgb(5,0,0,0),Color.FromArgb(185,0,0,0),90f);
            graphics.FillRectangle(gradient,r);
        }
        finally{graphics.Restore(state);}
    }
    protected override void OnInvalidated(InvalidateEventArgs e)
    {
        base.OnInvalidated(e);
        foreach(Control child in Controls)if(child is GameActionButton{CoverOverlay:true})child.Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        PaintBackdrop(e.Graphics);
        OverlayPaint?.Invoke(this,e);
    }
}

internal sealed class WorkspaceSurface:Panel
{
    internal bool Dashed {get;set;}
    internal WorkspaceSurface(){DoubleBuffered=true;Padding=new(14);}
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;using var p=new Pen(UiTheme.Current.Border){DashStyle=Dashed?DashStyle.Dash:DashStyle.Solid};using var path=WorkspaceDrawing.Round(Rectangle.Inflate(ClientRectangle,-1,-1),6*DeviceDpi/96);e.Graphics.DrawPath(p,path);}
}

internal sealed class WorkspaceTextField:Panel
{
    private readonly TextBox field;
    private readonly string icon;
    internal WorkspaceTextField(TextBox field,string icon)
    {
        this.field=field;this.icon=icon;TabStop=true;AccessibleName=field.PlaceholderText.Length>0?field.PlaceholderText:field.AccessibleName;Cursor=Cursors.IBeam;
        SetStyle(ControlStyles.Selectable|ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        field.BorderStyle=BorderStyle.None;field.Dock=DockStyle.None;field.TabStop=false;Controls.Add(field);
        field.TextChanged+=(_,_)=>UpdateCue();field.GotFocus+=(_,_)=>UpdateCue();field.LostFocus+=(_,_)=>UpdateCue();field.FontChanged+=(_,_)=>PerformLayout();UpdateCue();
    }
    private void UpdateCue(){field.Visible=field.ReadOnly||field.Focused||field.Text.Length>0;Invalidate();}
    private void Edit(){field.Visible=true;field.Focus();}
    protected override void OnEnter(EventArgs e){base.OnEnter(e);Edit();}
    protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(e.Button==MouseButtons.Left)Edit();}
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);if(field is null)return;int s=DeviceDpi;int height=field.PreferredHeight;
        field.Bounds=new(34*s/96,Math.Max(0,(Height-height)/2),Math.Max(1,Width-42*s/96),height);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);int s=DeviceDpi;var p=UiTheme.Current;var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
        using var border=new Pen(p.Border);using var shape=WorkspaceDrawing.Round(Rectangle.Inflate(ClientRectangle,-1,-1),6*s/96);g.DrawPath(border,shape);
        WorkspaceSkin.Icon(g,icon,new(10*s/96,(Height-17*s/96)/2,17*s/96,17*s/96),p.SecondaryText);
        if(!field.Visible)WorkspaceDrawing.Text(g,field.PlaceholderText,Font,new(34*s/96,0,Math.Max(0,Width-42*s/96),Height),p.SecondaryText);
    }
}

internal sealed class WorkspaceSwitch:CheckBox
{
    internal bool Unavailable {get;set;}
    internal WorkspaceSwitch(){AutoSize=false;Width=80;Height=34;AccessibleName="下次启动时启用翻译";SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);}
    protected override void OnPaint(PaintEventArgs e)
    {
        int S(int n)=>n*DeviceDpi/96;var p=UiTheme.Current;var g=e.Graphics;g.Clear(Parent?.BackColor??p.Main);g.SmoothingMode=SmoothingMode.AntiAlias;
        var r=new Rectangle(0,(Height-S(22))/2,S(40),S(22));using var path=WorkspaceDrawing.Round(r,S(11));using var fill=new SolidBrush(Checked?p.Accent:Color.FromArgb(179,188,200));g.FillPath(fill,path);g.FillEllipse(Brushes.White,Checked?r.Right-S(20):S(2),r.Y+S(2),S(18),S(18));
        WorkspaceDrawing.Text(g,Unavailable?"未接入":(Checked?"开启":"关闭"),Font,new(S(47),0,Width-S(47),Height),p.SecondaryText);
    }
}
