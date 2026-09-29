namespace ScreenshotTranslationUiTester;

// Always recompute physical pixels from logical constants, never scale previous bounds.
internal sealed class GameActionButton:Button
{
    private bool hovering;
    internal string IconKey {get;set;}="";
    internal bool Primary {get;set;}
    internal bool NavigationStyle {get;set;}
    internal bool TabStyle {get;set;}
    internal bool CoverOverlay {get;set;}
    internal int MinimumLogicalWidth {get;set;}=84;
    internal int LogicalHeight {get;set;}=36;
    internal static int RowHeight(Control c,int logical=36)=>Math.Max(logical*c.DeviceDpi/96,c.Font.Height+8*c.DeviceDpi/96);
    internal GameActionButton(string text){SetStyle(ControlStyles.SupportsTransparentBackColor|ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);Text=text;AutoSize=false;FlatStyle=FlatStyle.Flat;UseVisualStyleBackColor=false;Margin=new(0,2,8,2);RefreshMetrics();}
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // A native Button may omit this stage for a local repaint/WM_PRINT.
        // Cover buttons compose their complete pixels in OnPaint instead.
        if(CoverOverlay)return;
        base.OnPaintBackground(e);
    }
    internal void RefreshMetrics()
    {
        var d=DeviceDpi/96f;Padding=new((int)(10*d),0,(int)(10*d),0);
        Size=new(Text.Length==0?(int)(MinimumLogicalWidth*d):Math.Max((int)(MinimumLogicalWidth*d),TextRenderer.MeasureText(Text,Font).Width+Padding.Horizontal+(int)((NavigationStyle?60:IconKey.Length>0?28:6)*d)),RowHeight(this,LogicalHeight));
    }
    protected override void OnFontChanged(EventArgs e){base.OnFontChanged(e);RefreshMetrics();}
    protected override void OnTextChanged(EventArgs e){base.OnTextChanged(e);RefreshMetrics();}
    protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);RefreshMetrics();}
    protected override void OnVisibleChanged(EventArgs e){base.OnVisibleChanged(e);if(Visible)RefreshMetrics();}
    protected override void OnDpiChangedAfterParent(EventArgs e){base.OnDpiChangedAfterParent(e);RefreshMetrics();}
    protected override void OnMouseEnter(EventArgs e){base.OnMouseEnter(e);hovering=true;Invalidate();}
    protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);hovering=false;Invalidate();}
    protected override void OnPaint(PaintEventArgs e)
    {
        var p=UiTheme.Current;var g=e.Graphics;if(!CoverOverlay)g.Clear(Parent?.BackColor??p.Main);g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if(CoverOverlay)
        {
            if(Parent is WorkspaceCover cover)
            {
                var state=g.Save();g.TranslateTransform(-Left,-Top);
                try{cover.PaintBackdrop(g);}finally{g.Restore(state);}
            }
            else g.Clear(Parent?.BackColor??p.Main);
            if(hovering||Focused&&ShowFocusCues)
            {using var fill=new SolidBrush(Color.FromArgb(65,0,0,0));using var shape=WorkspaceDrawing.Round(Rectangle.Inflate(ClientRectangle,-1,-1),6*DeviceDpi/96);g.FillPath(fill,shape);}
            var overlayInk=Enabled?Color.White:Color.FromArgb(170,220,225,235);int size=20*DeviceDpi/96;
            int total=size+8*DeviceDpi/96+TextRenderer.MeasureText(Text,Font).Width,left=Math.Max(7*DeviceDpi/96,(Width-total)/2);
            WorkspaceSkin.Icon(g,IconKey,new(left,(Height-size)/2,size,size),overlayInk);
            WorkspaceDrawing.Text(g,Text,Font,new(left+size+8*DeviceDpi/96,0,Math.Max(0,Width-left-size-12*DeviceDpi/96),Height),overlayInk);
            if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(g,Rectangle.Inflate(ClientRectangle,-3,-3),Color.White,Color.Black);
            return;
        }
        if(TabStyle)
        {
            using(var line=new Pen(p.Border))g.DrawLine(line,0,Height-1,Width,Height-1);
            var tabForeground=Enabled?(Primary?p.Accent:p.Text):p.SecondaryText;
            int size=IconKey.Length>0?20*DeviceDpi/96:0;
            int textWidth=TextRenderer.MeasureText(Text,Font).Width;
            int gap=size>0?8*DeviceDpi/96:0;
            int left=Math.Max(4*DeviceDpi/96,(Width-textWidth-size-gap)/2);
            if(size>0)WorkspaceSkin.Icon(g,IconKey,new(left,(Height-size)/2,size,size),tabForeground);
            WorkspaceDrawing.Text(g,Text,Font,new(left+size+gap,0,Math.Max(0,Width-left-size-gap-4*DeviceDpi/96),Height),tabForeground,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
            if(Primary){using var accent=new SolidBrush(p.Accent);g.FillRectangle(accent,0,Height-Math.Max(2,2*DeviceDpi/96),Width,Math.Max(2,2*DeviceDpi/96));}
            if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(g,Rectangle.Inflate(ClientRectangle,-4,-4));
            return;
        }
        if(NavigationStyle){using var line=new Pen(p.Border);g.DrawLine(line,0,0,Width,0);int d=DeviceDpi;WorkspaceSkin.Icon(g,IconKey,new(9*d/96,(Height-21*d/96)/2,21*d/96,21*d/96),p.Text);WorkspaceDrawing.Text(g,Text,Font,new(42*d/96,0,Width-75*d/96,Height),p.Accent);WorkspaceSkin.Icon(g,"next",new(Width-24*d/96,(Height-15*d/96)/2,15*d/96,15*d/96),p.SecondaryText);return;}
        var bounds=Rectangle.Inflate(ClientRectangle,-1,-1);using var path=WorkspaceDrawing.Round(bounds,7*DeviceDpi/96);
        bool primary=Primary||Equals(Tag,"workspace-selected");var color=Enabled?(primary?p.Accent:p.Main):p.Main;
        if(hovering&&Enabled)color=ControlPaint.Light(color,.08f);
        using(var brush=new SolidBrush(color))g.FillPath(brush,path);
        using(var border=new Pen(primary?p.Accent:p.Border))g.DrawPath(border,path);
        var foreground=Enabled?(primary?Color.White:p.Text):p.SecondaryText;
        var text=Rectangle.Inflate(bounds,-6,0);
        if(IconKey.Length>0){int s=20*DeviceDpi/96;int tw=TextRenderer.MeasureText(Text,Font).Width;int total=s+(Text.Length>0?8*DeviceDpi/96+tw:0);int x=Text.Length==0?(Width-s)/2:Math.Max(7*DeviceDpi/96,(Width-total)/2);WorkspaceSkin.Icon(g,IconKey,new(x,(Height-s)/2,s,s),Enabled?(primary?Color.White:p.Accent):p.SecondaryText);text=new(x+s+6*DeviceDpi/96,0,Math.Max(0,Width-x-s-12*DeviceDpi/96),Height);}
        WorkspaceDrawing.Text(g,Text,Font,text,foreground,(IconKey.Length>0?TextFormatFlags.Left:TextFormatFlags.HorizontalCenter)|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
        if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(g,Rectangle.Inflate(bounds,-4,-4),p.Text,color);
    }
}

internal sealed class GamePlanBox:ComboBox
{
    internal int LogicalHeight {get;set;}=36;
    internal GamePlanBox()
    {
        DropDownStyle=ComboBoxStyle.DropDownList;IntegralHeight=false;MaxDropDownItems=8;
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
    }
    protected override void OnDropDown(EventArgs e)
    {
        // Native owner-drawn combos can otherwise expand to the entire screen.
        var anchor=RectangleToScreen(ClientRectangle);var area=FindForm()?.RectangleToScreen(FindForm()!.ClientRectangle)??Screen.FromControl(this).WorkingArea;
        int room=Math.Max(area.Bottom-anchor.Bottom,anchor.Top-area.Top)-8*DeviceDpi/96;
        DropDownHeight=Math.Max(ItemHeight+2,Math.Min(8*ItemHeight+2,room));
        DropDownWidth=Width;
        var info=new ComboInfo{Size=System.Runtime.InteropServices.Marshal.SizeOf<ComboInfo>()};
        if(GetComboBoxInfo(Handle,ref info)&&info.List!=IntPtr.Zero)NativeMethods.SetWindowTheme(info.List,UiTheme.Current.Main.GetBrightness()<.5f?"DarkMode_Explorer":"Explorer",null);
        base.OnDropDown(e);
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ComboInfo {public int Size;public int ItemLeft,ItemTop,ItemRight,ItemBottom,ButtonLeft,ButtonTop,ButtonRight,ButtonBottom,ButtonState;public IntPtr Combo,Item,List;}
    [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern bool GetComboBoxInfo(IntPtr handle,ref ComboInfo info);
    internal void RefreshMetrics()
    {
        if(DrawMode!=DrawMode.OwnerDrawFixed)return;
        // Native combo border allowance is measured, not repeatedly scaled.
        var allowance=Math.Clamp(Height-ItemHeight,4,12*DeviceDpi/96);
        int height=Math.Max(Font.Height+2,GameActionButton.RowHeight(this,LogicalHeight)-allowance);
        if(ItemHeight!=height)ItemHeight=height;
    }
    protected override void OnFontChanged(EventArgs e){base.OnFontChanged(e);RefreshMetrics();}
    protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);RefreshMetrics();}
    protected override void OnDpiChangedAfterParent(EventArgs e){base.OnDpiChangedAfterParent(e);RefreshMetrics();}
    protected override void OnSelectedIndexChanged(EventArgs e){base.OnSelectedIndexChanged(e);Invalidate();}
    protected override void OnTextChanged(EventArgs e){base.OnTextChanged(e);Invalidate();}
    protected override void OnGotFocus(EventArgs e){base.OnGotFocus(e);Invalidate();}
    protected override void OnLostFocus(EventArgs e){base.OnLostFocus(e);Invalidate();}
    protected override void OnPaint(PaintEventArgs e)
    {
        var p=UiTheme.Current;var g=e.Graphics;int d=DeviceDpi;
        g.Clear(p.Secondary);
        int button=Math.Max(20,24*d/96),inset=Math.Max(1,d/96);
        var text=new Rectangle(9*d/96,0,Math.Max(0,Width-button-12*d/96),Height);
        TextRenderer.DrawText(g,SelectedItem is null?Text:GetItemText(SelectedItem),Font,text,Enabled?p.Text:p.SecondaryText,
            TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
        using(var border=new Pen(Focused?p.Accent:p.Border,inset))
            g.DrawRectangle(border,inset/2,inset/2,Math.Max(1,Width-inset),Math.Max(1,Height-inset));
        g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var arrow=new Pen(Enabled?p.Text:p.SecondaryText,1.7f*d/96f){StartCap=System.Drawing.Drawing2D.LineCap.Round,EndCap=System.Drawing.Drawing2D.LineCap.Round,LineJoin=System.Drawing.Drawing2D.LineJoin.Round};
        float x=Width-button/2f,y=Height/2f,size=4.5f*d/96f;
        g.DrawLines(arrow,new PointF[]{new(x-size,y-size/2),new(x,y+size/2),new(x+size,y-size/2)});
    }
}
