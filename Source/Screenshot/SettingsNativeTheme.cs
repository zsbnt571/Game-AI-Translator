using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ScreenshotTranslationUiTester;

// Associate only our controls with the current palette, including native
// non-client scrollbars and handles recreated when a setting changes.
internal static class SettingsNativeTheme
{
    private sealed class State { internal nint Handle; internal bool Dark,Applying; }
    private static readonly ConditionalWeakTable<Control,State> States=new();
    internal static void Apply(Control control)
    {
        if(control.IsDisposed)return;
        if(!States.TryGetValue(control,out var state))
        {
            state=new();States.Add(control,state);
            control.HandleCreated+=(_,_)=>Apply(control);
            control.BackColorChanged+=(_,_)=>Apply(control);
        }
        if(!control.IsHandleCreated||state.Applying)return;
        bool dark=UiTheme.Current.Main.GetBrightness()<.5f;
        if(state.Handle==control.Handle&&state.Dark==dark)return;
        state.Applying=true;
        try{ApplyHandle(control.Handle,dark);state.Handle=control.Handle;state.Dark=dark;}
        finally{state.Applying=false;}
    }
    internal static void ApplyHandle(nint handle,bool dark)
    {
        if(handle==0)return;
        NativeMethods.SetWindowTheme(handle,dark?"DarkMode_Explorer":"Explorer",null);
        RedrawWindow(handle,0,0,0x0001|0x0004|0x0400);
    }
    internal static void Caption(Form form)
    {
        if(!form.IsHandleCreated)return;
        int dark=UiTheme.Current.Main.GetBrightness()<.5f?1:0;
        DwmSetWindowAttribute(form.Handle,20,ref dark,sizeof(int));
        int color=ColorTranslator.ToWin32(UiTheme.Current.Secondary);
        DwmSetWindowAttribute(form.Handle,35,ref color,sizeof(int));
        color=ColorTranslator.ToWin32(UiTheme.Current.Text);
        DwmSetWindowAttribute(form.Handle,36,ref color,sizeof(int));
    }
    [DllImport("user32.dll")]private static extern bool RedrawWindow(nint handle,nint rect,nint region,uint flags);
    [DllImport("dwmapi.dll")]private static extern int DwmSetWindowAttribute(nint handle,int attr,ref int value,int size);
}

internal sealed class SettingsTextArea : Panel
{
    internal TextBox Field {get;}
    internal SettingsTextArea(TextBox field)
    {
        Field=field;TabStop=false;Height=112;MaximumSize=new(620,0);
        SetStyle(ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        field.MinimumSize=field.MaximumSize=Size.Empty;field.BorderStyle=BorderStyle.None;
        field.Margin=Padding.Empty;Controls.Add(field);SettingsNativeTheme.Apply(field);
        field.Enter+=(_,_)=>Invalidate();field.Leave+=(_,_)=>Invalidate();
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);if(Field is null)return;
        int pad=12*DeviceDpi/96;Height=Math.Max(112*DeviceDpi/96,Font.Height*4+pad*2);
        Field.SetBounds(pad,pad,Math.Max(1,Width-pad*2),Math.Max(1,Height-pad*2));
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var shape=WorkspaceDrawing.Round(Rectangle.Inflate(ClientRectangle,-1,-1),5*DeviceDpi/96);
        using var border=new Pen(ContainsFocus?SettingsColors.AccentText:UiTheme.Current.Border,ContainsFocus?2:1);
        e.Graphics.DrawPath(border,shape);
    }
}

// The parent owns the entire stepper outline. These are accessible hit targets
// with no separate box, border or native button surface.
internal sealed class SettingsStepButton : Button
{
    private bool hover,pressed;
    internal SettingsStepButton()
    {
        FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;UseVisualStyleBackColor=false;
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);
    }
    protected override void OnMouseEnter(EventArgs e){base.OnMouseEnter(e);hover=true;Invalidate();}
    protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);hover=pressed=false;Invalidate();}
    protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);pressed=true;Invalidate();}
    protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);pressed=false;Invalidate();}
    protected override void OnPaint(PaintEventArgs e)
    {
        var p=UiTheme.Current;var g=e.Graphics;g.Clear(p.Secondary);
        g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if(Enabled&&(hover||pressed))
        {
            using var shape=WorkspaceDrawing.Round(Rectangle.Inflate(ClientRectangle,-1,-1),3*DeviceDpi/96);
            using var fill=new SolidBrush(pressed?WorkspaceSkin.SoftAccent:p.Control);g.FillPath(fill,shape);
        }
        WorkspaceSkin.Icon(g,Text=="+"?"plus":"minus",new((Width-12*DeviceDpi/96)/2,(Height-12*DeviceDpi/96)/2,12*DeviceDpi/96,12*DeviceDpi/96),Enabled?p.Text:p.SecondaryText);
        if(Focused&&ShowFocusCues)
        {
            using var focus=new Pen(SettingsColors.AccentText,2*DeviceDpi/96f);
            using var shape=WorkspaceDrawing.Round(Rectangle.Inflate(ClientRectangle,-2,-2),3*DeviceDpi/96);g.DrawPath(focus,shape);
        }
    }
}
