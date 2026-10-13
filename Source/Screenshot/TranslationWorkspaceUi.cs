using System.Runtime.InteropServices;

namespace ScreenshotTranslationUiTester;

// The translation dialogs share Fusion's existing palette and controls. This
// deliberately does not modify the renderer or the application's global theme.
internal static class TranslationWorkspaceUi
{
    internal static Color SelectionColor
    {
        get{var p=UiTheme.Current;if(p==ThemePalette.Night||p==ThemePalette.Day)return WorkspaceSkin.SoftAccent;return Color.FromArgb((p.Secondary.R*4+p.Accent.R)/5,(p.Secondary.G*4+p.Accent.G)/5,(p.Secondary.B*4+p.Accent.B)/5);}
    }
    internal static Label Label(string text,bool muted=false)=>new(){Text=text,AutoSize=true,Margin=new(0,4,0,8),Tag=muted?"translation-muted":null};
    internal static TableLayoutPanel Column(params RowStyle[] rows)
    {
        var p=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=rows.Length,Margin=Padding.Empty};
        p.ColumnStyles.Add(new(SizeType.Percent,100));foreach(var row in rows)p.RowStyles.Add(row);return p;
    }
    internal static FlowLayoutPanel Bar(params Control[] controls)
    {
        var p=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=true,Margin=new(0,4,0,4)};
        p.Controls.AddRange(controls);return p;
    }
    internal static GameActionButton Button(string text,string icon="")=>new(text){IconKey=icon,AccessibleName=text,Margin=new(0,2,8,2)};
    internal static WorkspaceDataGrid Table(string name)
    {
        var g=new WorkspaceDataGrid{Dock=DockStyle.Fill,Name=name,AccessibleName=name,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,AllowUserToResizeRows=false,MultiSelect=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,RowHeadersVisible=false,Margin=Padding.Empty,ClipboardCopyMode=DataGridViewClipboardCopyMode.EnableWithoutHeaderText};
        g.Paint+=(_,e)=>{if(g.Rows.Count==0){var box=new Rectangle(16,g.ColumnHeadersHeight+16,Math.Max(1,g.Width-32),Math.Max(1,g.Height-g.ColumnHeadersHeight-32));WorkspaceDrawing.Text(e.Graphics,UiLanguage.Text("暂无条目"),g.Font,box,Muted(),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);}};
        return g;
    }
    internal static Control Field(TextBox field,int width,string icon)
    {
        var surface=new WorkspaceTextField(field,icon){Width=width,Height=36,Margin=new(0,2,12,2)};
        void Metrics(){surface.Height=GameActionButton.RowHeight(surface);surface.Invalidate();}
        surface.FontChanged+=(_,_)=>Metrics();surface.DpiChangedAfterParent+=(_,_)=>Metrics();surface.HandleCreated+=(_,_)=>Metrics();
        surface.Layout+=(_,_)=>{int wanted=GameActionButton.RowHeight(surface);if(surface.Height!=wanted)surface.Height=wanted;};
        surface.Paint+=(_,e)=>{if(field.Focused){using var pen=new Pen(UiTheme.Current.Accent,Math.Max(1,2*surface.DeviceDpi/96));using var path=WorkspaceDrawing.Round(Rectangle.Inflate(surface.ClientRectangle,-2,-2),6*surface.DeviceDpi/96);e.Graphics.DrawPath(pen,path);}};
        field.GotFocus+=(_,_)=>surface.Invalidate();field.LostFocus+=(_,_)=>surface.Invalidate();return surface;
    }
    internal static Control Editor(TextBox box,string name)
    {
        box.AccessibleName=name;box.BorderStyle=BorderStyle.None;box.Margin=Padding.Empty;
        var surface=new WorkspaceSurface{Dock=DockStyle.Fill,Padding=new(10),Margin=new(0,0,8,0),Name=name};surface.Controls.Add(box);
        box.GotFocus+=(_,_)=>surface.Invalidate();box.LostFocus+=(_,_)=>surface.Invalidate();
        surface.Paint+=(_,e)=>{if(box.Focused){using var pen=new Pen(UiTheme.Current.Accent,Math.Max(1,2*surface.DeviceDpi/96));using var path=WorkspaceDrawing.Round(Rectangle.Inflate(surface.ClientRectangle,-2,-2),6*surface.DeviceDpi/96);e.Graphics.DrawPath(pen,path);}};
        box.HandleCreated+=(_,_)=>NativeMethods.SetWindowTheme(box.Handle,UiTheme.Current.Main.GetBrightness()<.5f?"DarkMode_Explorer":"Explorer",null);
        return surface;
    }
    internal static void GridPalette(WorkspaceDataGrid grid)
    {
        grid.ApplyPalette();var p=UiTheme.Current;
        grid.BackgroundColor=p.Secondary;grid.DefaultCellStyle.BackColor=p.Secondary;
        grid.DefaultCellStyle.SelectionBackColor=SelectionColor;grid.DefaultCellStyle.SelectionForeColor=p.Text;
        grid.DefaultCellStyle.Padding=new(8,2,8,2);
        if(grid.Columns.Count>0)grid.Columns[^1].DefaultCellStyle.Padding=new(8,2,20,2);
    }
    internal static void Palette(Control root)
    {
        var p=UiTheme.Current;
        void Visit(Control c,bool field=false)
        {
            field|=c is WorkspaceTextField||c is WorkspaceSurface;
            c.BackColor=field?p.Secondary:p.Main;c.ForeColor=p.Text;
            if(c is Label l){l.BackColor=Color.Transparent;l.ForeColor=Equals(l.Tag,"translation-muted")?Muted():p.Text;}
            if(c is ComboBox combo){combo.BackColor=p.Secondary;combo.ForeColor=p.Text;}
            if(c is WorkspaceDataGrid grid){GridPalette(grid);return;}
            if(c is TextBoxBase text&&text.IsHandleCreated)NativeMethods.SetWindowTheme(text.Handle,p.Main.GetBrightness()<.5f?"DarkMode_Explorer":"Explorer",null);
            foreach(Control child in c.Controls)Visit(child,field);
        }
        UiLanguage.BindTree(root);Visit(root);if(root is Form form&&form.IsHandleCreated)Caption(form);root.Invalidate(true);
    }
    // The existing light secondary color is too faint for small helper labels.
    // Blend toward the theme's text rather than introducing another UI palette.
    internal static Color Muted()
    {
        var p=UiTheme.Current;var value=p.SecondaryText;
        for(int i=0;i<8&&Contrast(value,p.Main)<4.5;i++)value=Color.FromArgb((value.R*3+p.Text.R)/4,(value.G*3+p.Text.G)/4,(value.B*3+p.Text.B)/4);
        return value;
    }
    internal static double Contrast(Color a,Color b)
    {
        static double Lum(Color c){static double L(byte v){double s=v/255d;return s<=.04045?s/12.92:Math.Pow((s+.055)/1.055,2.4);}return .2126*L(c.R)+.7152*L(c.G)+.0722*L(c.B);}
        double x=Lum(a),y=Lum(b);return (Math.Max(x,y)+.05)/(Math.Min(x,y)+.05);
    }
    internal static void Prepare(Form form,ApiSettings settings)
    {
        AppBranding.Apply(form);FontManager.ApplyUi(form,settings);Palette(form);
        form.HandleCreated+=(_,_)=>Caption(form);form.Activated+=(_,_)=>Palette(form);
        void Fit()
        {
            if(form.IsDisposed)return;var area=Screen.FromControl(form.Owner??form).WorkingArea;
            form.MinimumSize=new(Math.Min(form.MinimumSize.Width,area.Width),Math.Min(form.MinimumSize.Height,area.Height));
            var size=new Size(Math.Min(form.Width,area.Width),Math.Min(form.Height,area.Height));
            form.Bounds=new(Math.Clamp(form.Left,area.Left,area.Right-size.Width),Math.Clamp(form.Top,area.Top,area.Bottom-size.Height),size.Width,size.Height);
        }
        form.Load+=(_,_)=>Fit();form.DpiChanged+=(_,_)=>{if(form.IsHandleCreated)form.BeginInvoke((Action)Fit);};
    }
    private static void Caption(Form form)
    {
        int dark=UiTheme.Current.Main.GetBrightness()<.5f?1:0,back=ColorTranslator.ToWin32(UiTheme.Current.Main),text=ColorTranslator.ToWin32(UiTheme.Current.Text);
        DwmSetWindowAttribute(form.Handle,20,ref dark,4);DwmSetWindowAttribute(form.Handle,35,ref back,4);DwmSetWindowAttribute(form.Handle,36,ref text,4);
    }
    [DllImport("dwmapi.dll")]private static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
}

// Use Fusion's underlined tabs, keeping keyboard navigation and tab semantics.
internal sealed class TranslationWorkspaceTabs:Panel
{
    private readonly FlowLayoutPanel navigation=TranslationWorkspaceUi.Bar();
    private readonly Panel body=new(){Dock=DockStyle.Fill,Margin=Padding.Empty};
    private readonly List<(GameActionButton Button,Panel Page)> pages=[];
    internal int SelectedIndex{get;private set;}=-1;
    internal Func<bool>? CanLeave {get;set;}
    internal TranslationWorkspaceTabs()
    {
        Dock=DockStyle.Fill;Margin=Padding.Empty;
        var root=TranslationWorkspaceUi.Column(new(SizeType.AutoSize),new(SizeType.Percent,100));Controls.Add(root);
        navigation.Margin=new(0,0,0,12);root.Controls.Add(navigation,0,0);root.Controls.Add(body,0,1);
    }
    internal Panel Add(string title,string icon="")
    {
        int index=pages.Count;var button=TranslationWorkspaceUi.Button(title,icon);button.TabStyle=true;button.AccessibleRole=AccessibleRole.PageTab;button.Margin=Padding.Empty;button.MinimumLogicalWidth=96;
        var page=new Panel{Dock=DockStyle.Fill,Visible=false,Name=title,Margin=Padding.Empty};pages.Add((button,page));navigation.Controls.Add(button);body.Controls.Add(page);
        button.Click+=(_,_)=>Select(index);button.KeyDown+=(_,e)=>{if(e.KeyCode is Keys.Left or Keys.Right or Keys.Home or Keys.End){int next=e.KeyCode==Keys.Home?0:e.KeyCode==Keys.End?pages.Count-1:(index+(e.KeyCode==Keys.Right?1:pages.Count-1))%pages.Count;Select(next,true);e.Handled=e.SuppressKeyPress=true;}};
        if(SelectedIndex<0)Select(0);return page;
    }
    internal bool Select(int index,bool focus=false)
    {
        if(index<0||index>=pages.Count)return false;
        if(index!=SelectedIndex&&SelectedIndex>=0&&CanLeave?.Invoke()==false)return false;
        SelectedIndex=index;
        for(int i=0;i<pages.Count;i++){var item=pages[i];item.Page.Visible=i==index;item.Button.Primary=i==index;item.Button.AccessibleDescription=i==index?"已选择":"";item.Button.Invalidate();}
        if(focus)pages[index].Button.Focus();return true;
    }
    internal void Next(bool backwards)=>Select((SelectedIndex+(backwards?pages.Count-1:1))%pages.Count,true);
}
