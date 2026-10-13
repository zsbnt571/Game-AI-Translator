using System.Drawing.Drawing2D;

namespace ScreenshotTranslationUiTester;

// Native fields keep their existing bindings. These containers own spacing,
// wrapping and the settings-only surface treatment.
internal sealed class SettingsContent : TableLayoutPanel
{
    private bool fitting;
    internal SettingsContent()
    {
        AutoSize=true; AutoSizeMode=AutoSizeMode.GrowAndShrink; ColumnCount=1;
        ColumnStyles.Add(new(SizeType.Percent,100)); Margin=Padding.Empty;
        Padding=new(28,24,28,28); Font=new Font("Microsoft YaHei UI",10);
        SetStyle(ControlStyles.OptimizedDoubleBuffer|ControlStyles.AllPaintingInWmPaint,true);
    }
    internal void AddBlock(Control child)
    {
        child.Dock=DockStyle.Top; child.Margin=new(0,0,0,16);
        RowStyles.Add(new(SizeType.AutoSize)); Controls.Add(child,0,RowCount++);
    }
    public override Size GetPreferredSize(Size proposedSize)
    {
        int width=MinimumSize.Width>0?MinimumSize.Width:Math.Max(180,proposedSize.Width>0&&proposedSize.Width<int.MaxValue?proposedSize.Width:Width);
        int inner=Math.Max(80,width-Padding.Horizontal),height=Padding.Vertical;
        foreach(Control block in Controls)height+=block.GetPreferredSize(new(inner,0)).Height+block.Margin.Vertical;
        return new(width,height);
    }
    internal void FitTo(SettingsPage page)
    {
        if(fitting)return;fitting=true;
        try
        {
        // Reserve scrollbar space even before the scroll range is known. This
        // avoids width/height feedback when a long translated label wraps.
        var d=DeviceDpi/96f;
        var width=Math.Min((int)(1060*d),Math.Max(180,page.ClientSize.Width-SystemInformation.VerticalScrollBarWidth-2));
        MinimumSize=new(width,0); MaximumSize=new(width,0);
        if(Width!=width)Width=width;
        Left=page.AutoScrollPosition.X; Top=page.AutoScrollPosition.Y;
        page.AutoScrollMinSize=new(0,Height);page.PerformLayout();page.SynchronizeScrollStyles();
        }
        finally{fitting=false;}
    }
}

internal sealed class SettingsSection : Panel
{
    private readonly Label heading;
    private readonly List<(Label label,Control value)> fields=[];
    private bool arranging;
    private int preferredWidth=-1,preferredHeight;
    internal SettingsSection(string title)
    {
        AutoSize=true;AutoSizeMode=AutoSizeMode.GrowAndShrink;Padding=new(20,20,20,8);
        SetStyle(ControlStyles.OptimizedDoubleBuffer|ControlStyles.AllPaintingInWmPaint|ControlStyles.ResizeRedraw,true);
        heading=new Label{Text=title,AutoSize=true,Font=new Font("Microsoft YaHei UI",11,FontStyle.Bold),Name="SettingsSectionTitle"};
        Controls.Add(heading);
        heading.TextChanged+=(_,_)=>preferredWidth=-1;
    }
    internal void AddField(string title,Control value)
    {
        var label=new Label{Text=title,AutoSize=true,Name="SettingsFieldLabel",UseMnemonic=false};
        fields.Add((label,value));value.Dock=DockStyle.None;value.Anchor=AnchorStyles.Left|AnchorStyles.Top;
        value.Margin=Padding.Empty;
        if(title.Length>0)value.AccessibleName=title;
        Controls.Add(label);Controls.Add(value);
        preferredWidth=-1;
        value.Layout+=(_,_)=>preferredWidth=-1;
        value.TextChanged+=(_,_)=>preferredWidth=-1;
        value.VisibleChanged+=(_,_)=>{preferredWidth=-1;if(Visible)PerformLayout();};
        value.SizeChanged+=(_,_)=>{preferredWidth=-1;if(!arranging)PerformLayout();};
    }
    private int Arrange(int width,bool apply)
    {
        int S(int n)=>n*DeviceDpi/96;
        int inner=Math.Max(S(80),width-Padding.Horizontal);
        bool stacked=inner<S(470);
        int labelWidth=stacked?0:Math.Clamp(inner/4,S(164),S(206));
        int available=Math.Max(S(70),inner-labelWidth);
        int y=Padding.Top;
        var hs=heading.GetPreferredSize(new(inner,0));
        if(apply)heading.SetBounds(Padding.Left,y,inner,hs.Height);
        y+=hs.Height+S(20);
        foreach(var (label,value) in fields)
        {
            if(Visible&&!value.Visible){if(apply)label.Visible=false;continue;}
            int vw=value is FlowLayoutPanel?available:Math.Min(available,value.MaximumSize.Width>0?value.MaximumSize.Width:available);
            if(value is ButtonBase)vw=Math.Min(vw,value.GetPreferredSize(new(vw,0)).Width);
            if(value is Label)vw=available;
            if(apply)
            {
                if(value is Label l)l.MaximumSize=new(vw,0);
                if(value is FlowLayoutPanel f){f.MaximumSize=new(vw,0);f.MinimumSize=new(vw,0);}
                label.Visible=label.Text.Length>0;label.MaximumSize=new(stacked?inner:labelWidth-S(16),0);
            }
            var vs=value.AutoSize?value.GetPreferredSize(new(vw,0)):new Size(vw,value.Height);
            if(value is ComboBox)vs.Height=Math.Max(S(36),value.Height);
            int lh=label.Text.Length==0?0:label.GetPreferredSize(new(stacked?inner:labelWidth-S(16),0)).Height;
            if(stacked&&lh>0)
            {
                if(apply)label.SetBounds(Padding.Left,y,inner,lh);
                y+=lh+S(8);
            }
            else if(apply)label.SetBounds(Padding.Left,y+Math.Max(0,(Math.Min(S(36),vs.Height)-lh)/2),Math.Max(1,labelWidth-S(16)),lh);
            if(apply)value.SetBounds(Padding.Left+labelWidth,y,vw,vs.Height);
            y+=Math.Max(vs.Height,stacked?0:lh)+S(label.Text.Length==0?12:16);
        }
        return y+Padding.Bottom;
    }
    public override Size GetPreferredSize(Size proposedSize)
    {
        if(heading is null)return base.GetPreferredSize(proposedSize);
        int width=proposedSize.Width>0&&proposedSize.Width<int.MaxValue?proposedSize.Width:Math.Max(180,Width);
        if(preferredWidth!=width){preferredHeight=Arrange(width,false);preferredWidth=width;}
        return new(width,preferredHeight);
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        if(arranging||heading is null)return;arranging=true;preferredWidth=-1;
        try{base.OnLayout(e);int h=Arrange(ClientSize.Width,true);if(Height!=h)Height=h;}
        finally{arranging=false;}
    }
    protected override void OnVisibleChanged(EventArgs e){preferredWidth=-1;base.OnVisibleChanged(e);if(Visible)PerformLayout();}
    protected override void OnFontChanged(EventArgs e){preferredWidth=-1;base.OnFontChanged(e);}
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(UiTheme.Current.Main);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        using var shape=WorkspaceDrawing.Round(Rectangle.Inflate(ClientRectangle,-1,-1),8*DeviceDpi/96);
        using var fill=new SolidBrush(UiTheme.Current.Secondary);e.Graphics.FillPath(fill,shape);
        using var border=new Pen(UiTheme.Current.Border);e.Graphics.DrawPath(border,shape);
    }
}

// A fixed leading column keeps values aligned regardless of checkbox label length.
internal sealed class SettingsValueRow : Panel
{
    private readonly Control leading,value;
    internal SettingsValueRow(Control leading,Control value)
    {
        this.leading=leading;this.value=value;MaximumSize=new(320,0);Height=36;
        Controls.Add(leading);Controls.Add(value);value.Enabled=((CheckBox)leading).Checked;
        ((CheckBox)leading).CheckedChanged+=(_,_)=>value.Enabled=((CheckBox)leading).Checked;
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);if(leading is null)return;int d=DeviceDpi;
        Height=Math.Max(36*d/96,value.Height);
        leading.SetBounds(0,Math.Max(0,(Height-leading.PreferredSize.Height)/2),100*d/96,leading.PreferredSize.Height);
        value.SetBounds(116*d/96,0,Math.Min(144*d/96,Math.Max(60,Width-116*d/96)),Height);
    }
}

internal sealed class SettingsInputFrame : Panel
{
    internal Control Field {get;}
    private readonly TextBox? numberText;
    private readonly Button? minus,plus;
    private bool updatingNumber;
    internal SettingsInputFrame(Control field)
    {
        Field=field;TabStop=false;Padding=new(10,6,10,6);MinimumSize=new(80,36);Height=36;
        Margin=Padding.Empty;SetStyle(ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        field.Margin=Padding.Empty;field.Anchor=AnchorStyles.Left|AnchorStyles.Right;
        if(field is TextBox text)text.BorderStyle=BorderStyle.None;
        if(field is NumericUpDown number)
        {
            // Keep the existing numeric model and its bindings, but give it one
            // visible editor. No native spinner is drawn inside the new stepper.
            number.Visible=false;number.TabStop=false;Controls.Add(number);
            numberText=new TextBox{BorderStyle=BorderStyle.None,TextAlign=HorizontalAlignment.Center,Name="NumericValue"};
            Controls.Add(numberText);
            void RefreshNumber(){if(updatingNumber)return;numberText.Text=number.Value.ToString("F"+number.DecimalPlaces,System.Globalization.CultureInfo.CurrentCulture);}
            number.ValueChanged+=(_,_)=>RefreshNumber();
            numberText.Validated+=(_,_)=>CommitNumber();
            numberText.KeyDown+=(_,e)=>
            {
                if(e.KeyCode is Keys.Up or Keys.Down){CommitNumber();if(e.KeyCode==Keys.Up)number.UpButton();else number.DownButton();e.SuppressKeyPress=true;}
                if(e.KeyCode==Keys.Enter){CommitNumber();numberText.SelectAll();e.SuppressKeyPress=true;}
            };
            numberText.TextChanged+=(_,_)=>
            {
                if(decimal.TryParse(numberText.Text,System.Globalization.NumberStyles.Number,System.Globalization.CultureInfo.CurrentCulture,out var n)&&n>=number.Minimum&&n<=number.Maximum)
                {updatingNumber=true;try{number.Value=n;}finally{updatingNumber=false;}}
            };
            number.EnabledChanged+=(_,_)=>{numberText.Enabled=number.Enabled;if(minus is not null)minus.Enabled=number.Enabled;if(plus is not null)plus.Enabled=number.Enabled;};
            RefreshNumber();
            Button Step(string text,Action step)
            {
                var b=new SettingsStepButton{Text=text,TabStop=true};
                b.FlatAppearance.BorderSize=0;b.Click+=(_,_)=>{CommitNumber();step();};Controls.Add(b);return b;
            }
            minus=Step("−",number.DownButton);plus=Step("+",number.UpButton);
            numberText.Enabled=minus.Enabled=plus.Enabled=number.Enabled;
            minus.AccessibleName="减小数值";plus.AccessibleName="增大数值";
            Width=144;MaximumSize=new(144,0);Anchor=AnchorStyles.Left;
        }
        else Controls.Add(field);
        field.Enter+=(_,_)=>Invalidate();field.Leave+=(_,_)=>Invalidate();field.FontChanged+=(_,_)=>PerformLayout();
        if(numberText is not null){numberText.Enter+=(_,_)=>Invalidate();numberText.Leave+=(_,_)=>Invalidate();}
    }
    private void CommitNumber()
    {
        if(Field is not NumericUpDown number||numberText is null)return;
        if(decimal.TryParse(numberText.Text,System.Globalization.NumberStyles.Number,System.Globalization.CultureInfo.CurrentCulture,out var n))
            number.Value=Math.Clamp(decimal.Round(n,number.DecimalPlaces),number.Minimum,number.Maximum);
        numberText.Text=number.Value.ToString("F"+number.DecimalPlaces,System.Globalization.CultureInfo.CurrentCulture);
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);if(Field is null)return;
        int d=DeviceDpi;var editor=(Control?)numberText??Field;
        Height=Math.Max(36*d/96,editor.PreferredSize.Height+12*d/96);
        int top=Math.Max(2*d/96,(Height-editor.PreferredSize.Height)/2);
        if(numberText is not null&&minus is not null&&plus is not null)
        {
            int step=32*d/96,inset=4*d/96;minus!.SetBounds(inset,inset,step-inset,Height-2*inset);plus!.SetBounds(Width-step,inset,step-inset,Height-2*inset);
            numberText.SetBounds(step+3,top,Math.Max(24,Width-2*step-6),numberText.PreferredSize.Height);
        }
        else Field.SetBounds(10*d/96,top,Math.Max(20,ClientSize.Width-20*d/96),Field.PreferredSize.Height);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        using var shape=WorkspaceDrawing.Round(Rectangle.Inflate(ClientRectangle,-1,-1),5*DeviceDpi/96);
        using var pen=new Pen(ContainsFocus?UiTheme.Current.Accent:UiTheme.Current.Border,ContainsFocus?2:1);
        e.Graphics.DrawPath(pen,shape);
    }
}

internal sealed class SettingsNote : Label
{
    internal SettingsNote(string text){Text=text;AutoSize=true;Margin=new(0,0,0,8);Name="SettingsNote";Font=new Font("Microsoft YaHei UI",9);}
}

internal sealed class SettingsDisclosure : TableLayoutPanel
{
    private readonly Control body;
    private readonly GameActionButton toggle;
    private readonly string title;
    internal bool Expanded {get;private set;}
    internal SettingsDisclosure(string title,Control body)
    {
        this.title=title;this.body=body;AutoSize=true;AutoSizeMode=AutoSizeMode.GrowAndShrink;ColumnCount=1;ColumnStyles.Add(new(SizeType.Percent,100));
        toggle=new GameActionButton(title+" ▾"){MinimumLogicalWidth=80,AccessibleName=title};
        toggle.Click+=(_,_)=>{Expanded=!Expanded;body.Visible=Expanded;toggle.Text=title+(Expanded?" ▴":" ▾");};
        Controls.Add(toggle,0,0);body.Dock=DockStyle.Top;body.Margin=new(0,10,0,0);body.Visible=false;Controls.Add(body,0,1);
    }
}
