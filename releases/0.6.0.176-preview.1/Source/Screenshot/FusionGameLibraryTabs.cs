namespace ScreenshotTranslationUiTester;

// Fixed-height header: visibility of the right-hand entry never participates in layout sizing.
internal sealed class GameLibraryTabs:Panel
{
    private readonly LibraryTab saved=new(){Text="已保存游戏",AccessibleName="已保存游戏",TabIndex=0};
    private readonly LibraryTab recent=new(){Text="最近游戏",AccessibleName="最近游戏",TabIndex=1};
    private readonly LinkLabel retention=new(){AutoSize=false,TextAlign=ContentAlignment.MiddleRight,TabIndex=2,AccessibleName="设置最近游戏保留数量"};
    private int recentLimit=20;
    internal event EventHandler? SelectedIndexChanged,RetentionRequested;
    internal int SelectedIndex {get=>recent.Checked?1:0;set{if(value==1)recent.Checked=true;else saved.Checked=true;}}
    internal int RecentLimit {get=>recentLimit;set{recentLimit=value;PerformLayout();}}
    internal Point RetentionAnchor=>new(Math.Max(0,Width-(int)(340*DeviceDpi/96f)),Height);
    internal GameLibraryTabs()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer,true);AutoSize=false;
        Controls.AddRange([saved,recent,retention]);saved.Checked=true;
        saved.CheckedChanged+=Changed;recent.CheckedChanged+=Changed;
        retention.LinkClicked+=(_,_)=>RetentionRequested?.Invoke(this,EventArgs.Empty);
    }
    private void Changed(object? sender,EventArgs args)
    {
        if(sender is not RadioButton {Checked:true})return;
        retention.Visible=recent.Checked;Invalidate(true);SelectedIndexChanged?.Invoke(this,EventArgs.Empty);
    }
    protected override void OnFontChanged(EventArgs e){base.OnFontChanged(e);PerformLayout();}
    protected override void OnDpiChangedAfterParent(EventArgs e){base.OnDpiChangedAfterParent(e);PerformLayout();}
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if(saved is null||recent is null||retention is null)return;
        var scale=DeviceDpi/96f;var pad=Math.Max(4,(int)(8*scale));
        var height=Math.Max((int)(40*scale),Font.Height+pad*2);
        if(Height!=height)Height=height;
        var a=TextRenderer.MeasureText("已保存游戏",Font).Width+pad*2;
        var b=TextRenderer.MeasureText("最近游戏",Font).Width+pad*2;
        var full=$"保留 {recentLimit} 项";
        var entry=TextRenderer.MeasureText(full,Font).Width+pad*2;
        var available=Math.Max(0,ClientSize.Width-pad*2);
        if(a+b+entry>available){a=TextRenderer.MeasureText("已保存",Font).Width+pad*2;b=TextRenderer.MeasureText("最近",Font).Width+pad*2;}
        saved.Text=a<TextRenderer.MeasureText("已保存游戏",Font).Width+pad*2?"已保存":"已保存游戏";
        recent.Text=b<TextRenderer.MeasureText("最近游戏",Font).Width+pad*2?"最近":"最近游戏";
        var remaining=Math.Max(1,available-a-b);
        var compact=$"{recentLimit} 项";
        retention.Text=entry<=remaining?full:TextRenderer.MeasureText(compact,Font).Width<=remaining?compact:"⋯";
        retention.LinkColor=UiTheme.Current.Accent;retention.ActiveLinkColor=UiTheme.Current.Text;
        saved.SetBounds(pad,0,a,height);recent.SetBounds(pad+a,0,b,height);
        retention.SetBounds(pad+a+b,0,remaining,height);
        retention.Visible=recent.Checked;
    }
    private sealed class LibraryTab:RadioButton
    {
        private bool hover;
        internal LibraryTab(){Appearance=Appearance.Button;AutoSize=false;FlatStyle=FlatStyle.Flat;SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer,true);}
        protected override void OnMouseEnter(EventArgs e){base.OnMouseEnter(e);hover=true;Invalidate();}
        protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);hover=false;Invalidate();}
        protected override void OnCheckedChanged(EventArgs e){base.OnCheckedChanged(e);Invalidate();}
        protected override void OnGotFocus(EventArgs e){base.OnGotFocus(e);Invalidate();}
        protected override void OnLostFocus(EventArgs e){base.OnLostFocus(e);Invalidate();}
        protected override void OnPaint(PaintEventArgs e)
        {
            var p=UiTheme.Current;
            using var brush=new SolidBrush(hover?p.Secondary:p.Main);e.Graphics.FillRectangle(brush,ClientRectangle);
            TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,Checked?p.Accent:p.Text,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix);
            if(Checked){using var pen=new Pen(p.Accent,Math.Max(2,2*DeviceDpi/96f));e.Graphics.DrawLine(pen,4,Height-2,Width-4,Height-2);}
            if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(ClientRectangle,-4,-5),p.Text,p.Main);
        }
    }
}
