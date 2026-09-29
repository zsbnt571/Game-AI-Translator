using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private WorkspaceCaption? _workspaceCaption;
    private bool _workspaceResizing;
    private bool _workspaceWasMinimized;
    private bool _workspaceSizingLayout;
    protected override void OnResize(EventArgs e)
    {
        // Native children can request immediate painting while nested layouts
        // are still moving their siblings. Batch this single synchronous size
        // transaction; never keep redraw disabled between input messages.
        var host=_mainContentHost;
        if(_workspaceSizingLayout||host is null||!host.IsHandleCreated||!host.Visible||WindowState==FormWindowState.Minimized)
        {base.OnResize(e);return;}
        _workspaceSizingLayout=true;
        WorkspaceRepaint.SetRedraw(host,false);
        try{base.OnResize(e);}
        finally{WorkspaceRepaint.SetRedraw(host,true);host.Invalidate(true);_workspaceSizingLayout=false;}
    }
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        bool minimized=WindowState==FormWindowState.Minimized;
        if(_workspaceWasMinimized&&!minimized)WorkspaceRepaint.Complete(this);
        _workspaceWasMinimized=minimized;
    }
    protected override void OnResizeBegin(EventArgs e){_workspaceResizing=true;base.OnResizeBegin(e);}
    protected override void OnResizeEnd(EventArgs e){base.OnResizeEnd(e);_workspaceResizing=false;QueueModificationSize();_dataMap.Invalidate();WorkspaceRepaint.Complete(this);}
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if(Visible&&IsHandleCreated)WorkspaceRepaint.Complete(this);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var color=UiTheme.Current.Main.GetBrightness()>.5f?Color.FromArgb(139,159,184):Color.FromArgb(89,111,140);
        using var pen=new Pen(color,Math.Max(1,DeviceDpi/96f));
        e.Graphics.DrawRectangle(pen,0,0,Math.Max(0,ClientSize.Width-1),Math.Max(0,ClientSize.Height-1));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SuppressNativeWorkspaceFrame();
    }

    private void SuppressNativeWorkspaceFrame()
    {
        // WS_THICKFRAME keeps resize/taskbar semantics, but Fusion owns all
        // visible frame pixels, including the active and inactive states.
        int disabled=1; // DWMNCRP_DISABLED
        DwmSetWindowAttribute(Handle,2,ref disabled,sizeof(int));
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
    [DllImport("user32.dll",EntryPoint="DefWindowProcW")] private static extern IntPtr WorkspaceDefWindowProc(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam);

    // Keep native sizing, taskbar commands and keyboard window management. Only
    // the caption is painted by Fusion; this does not register global input.
    protected override CreateParams CreateParams
    {
        get
        {
            var cp=base.CreateParams;
            cp.Style|=0x40000|0x20000|0x10000|0x80000;cp.Style&=~0xC00000;
            // The whole-form WS_EX_COMPOSITED surface can retain unpainted parent
            // regions after restore/show: children appear over the desktop. Keep
            // native window presentation; pages and custom controls buffer locally.
            cp.ExStyle&=~0x02000000; // WS_EX_COMPOSITED
            return cp;
        }
    }

    private bool HandleWorkspaceFrame(ref Message message)
    {
        // Keep the client origin stable. Locally composed workspace children
        // repaint their changed bounds; invalidating the entire native frame
        // on every sizing message needlessly erases unchanged controls.
        if(message.Msg==0x83){message.Result=IntPtr.Zero;return true;}
        if(message.Msg==0x31E){SuppressNativeWorkspaceFrame();return false;} // DWM composition changed
        if(WindowState!=FormWindowState.Minimized)
        {
            if(message.Msg==0x85){message.Result=IntPtr.Zero;return true;} // WM_NCPAINT
            if(message.Msg==0x86)
            {
                // Preserve native activation bookkeeping without repainting
                // the standard thick border (-1 is the documented sentinel).
                message.Result=WorkspaceDefWindowProc(message.HWnd,message.Msg,message.WParam,new IntPtr(-1));
                return true;
            }
        }
        if(message.Msg==0x84&&WindowState==FormWindowState.Normal)
        {
            long packed=message.LParam.ToInt64();var pt=PointToClient(new Point(unchecked((short)(packed&0xffff)),unchecked((short)((packed>>16)&0xffff))));
            int edge=Math.Max(4,5*DeviceDpi/96);bool left=pt.X<edge,right=pt.X>=ClientSize.Width-edge,top=pt.Y<edge,bottom=pt.Y>=ClientSize.Height-edge;
            int hit=top?(left?13:right?14:12):bottom?(left?16:right?17:15):left?10:right?11:0;
            if(hit!=0){message.Result=new IntPtr(hit);return true;}
        }
        return false;
    }

    private void ApplyWorkspaceMaximizedBounds(ref Message message)
    {
        if(message.LParam==IntPtr.Zero)return;
        var screen=Screen.FromHandle(message.HWnd);var info=Marshal.PtrToStructure<WorkspaceMinMaxInfo>(message.LParam);
        info.MaxPosition=new Point(screen.WorkingArea.Left-screen.Bounds.Left,screen.WorkingArea.Top-screen.Bounds.Top);
        info.MaxSize=new Point(screen.WorkingArea.Width,screen.WorkingArea.Height);
        info.MinTrackSize=new Point(MinimumSize.Width,MinimumSize.Height);
        Marshal.StructureToPtr(info,message.LParam,false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WorkspaceMinMaxInfo { public Point Reserved,MaxSize,MaxPosition,MinTrackSize,MaxTrackSize; }
}

internal sealed class WorkspaceCaption:Control
{
    private readonly Form owner;
    internal readonly WorkspaceCaptionButton MinimizeButton,MaximizeButton,CloseButton;
    private readonly ToolTip tips=new();
    internal WorkspaceCaption(Form owner)
    {
        this.owner=owner;TabStop=false;
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        MinimizeButton=new("minimize","最小化");MaximizeButton=new("maximize","最大化");CloseButton=new("close","关闭");
        Controls.AddRange([MinimizeButton,MaximizeButton,CloseButton]);
        MinimizeButton.Click+=(_,_)=>owner.WindowState=FormWindowState.Minimized;
        MaximizeButton.Click+=(_,_)=>ToggleMaximize();CloseButton.Click+=(_,_)=>owner.Close();
        owner.Resize+=OwnerResize;
        tips.SetToolTip(MinimizeButton,"最小化");tips.SetToolTip(MaximizeButton,"最大化 / 还原");tips.SetToolTip(CloseButton,"关闭");
        FontManager.MarkPreviewTextRoot(this);
    }
    private void OwnerResize(object? sender,EventArgs e){MaximizeButton.Restored=owner.WindowState==FormWindowState.Maximized;MaximizeButton.AccessibleName=MaximizeButton.Restored?"还原":"最大化";MaximizeButton.Invalidate();}
    private void ToggleMaximize()=>owner.WindowState=owner.WindowState==FormWindowState.Maximized?FormWindowState.Normal:FormWindowState.Maximized;
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);int w=44*DeviceDpi/96;
        if(CloseButton is null)return;
        CloseButton.Bounds=new(Width-w,0,w,Height);MaximizeButton.Bounds=new(Width-w*2,0,w,Height);MinimizeButton.Bounds=new(Width-w*3,0,w,Height);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var p=UiTheme.Current;var g=e.Graphics;g.Clear(p.Main);g.SmoothingMode=SmoothingMode.AntiAlias;int s=DeviceDpi;
        int icon=18*s/96;WorkspaceSkin.Icon(g,"brand",new(10*s/96,(Height-icon)/2,icon,icon),p.Accent);
        using var font=new Font("Segoe UI",9);WorkspaceDrawing.Text(g,"FUSION R1",font,new(36*s/96,0,83*s/96,Height),p.Text);
        using var versionFont=new Font("Segoe UI",8);WorkspaceDrawing.Text(g,BuildIdentity.BuildVersion,versionFont,new(123*s/96,0,106*s/96,Height),p.SecondaryText);
        using var border=new Pen(p.Border);g.DrawLine(border,0,Height-1,Width,Height-1);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if(e.Button==MouseButtons.Left){if(e.Clicks>1){ToggleMaximize();return;}ReleaseCapture();SendMessage(owner.Handle,0xA1,new IntPtr(2),IntPtr.Zero);}
        else if(e.Button==MouseButtons.Right)
        {
            var pt=PointToScreen(e.Location);var menu=GetSystemMenu(owner.Handle,false);
            int command=TrackPopupMenu(menu,0x100|0x2,pt.X,pt.Y,0,owner.Handle,IntPtr.Zero);
            if(command!=0)SendMessage(owner.Handle,0x112,new IntPtr(command),IntPtr.Zero);
        }
    }
    [DllImport("user32.dll")]private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]private static extern IntPtr SendMessage(IntPtr handle,int message,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll")]private static extern IntPtr GetSystemMenu(IntPtr handle,bool revert);
    [DllImport("user32.dll")]private static extern int TrackPopupMenu(IntPtr menu,uint flags,int x,int y,int reserved,IntPtr owner,IntPtr rect);
    protected override void Dispose(bool disposing){if(disposing){owner.Resize-=OwnerResize;tips.Dispose();}base.Dispose(disposing);}
}

internal sealed class WorkspaceCaptionButton:Button
{
    private readonly string kind;
    private bool hover;
    internal bool Restored {get;set;}
    internal WorkspaceCaptionButton(string kind,string name)
    {
        this.kind=kind;AccessibleName=name;Text="";FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;TabStop=false;Margin=Padding.Empty;
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
    }
    protected override void OnMouseEnter(EventArgs e){base.OnMouseEnter(e);hover=true;Invalidate();}
    protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);hover=false;Invalidate();}
    protected override void OnPaint(PaintEventArgs e)
    {
        var p=UiTheme.Current;var g=e.Graphics;g.Clear(hover?(kind=="close"?Color.FromArgb(220,45,58):p.Control):p.Main);
        using(var border=new Pen(p.Border))g.DrawLine(border,0,Height-1,Width,Height-1);
        float s=DeviceDpi/96f;using var pen=new Pen(hover&&kind=="close"?Color.White:p.Text,Math.Max(1,s));
        float size=10*s,x=(Width-size)/2,y=(Height-size)/2;g.SmoothingMode=SmoothingMode.AntiAlias;
        if(kind=="minimize")g.DrawLine(pen,x,y+size/2,x+size,y+size/2);
        else if(kind=="close"){g.DrawLine(pen,x,y,x+size,y+size);g.DrawLine(pen,x+size,y,x,y+size);}
        else if(Restored){g.DrawLines(pen,new PointF[]{new(x+2*s,y+size-2*s),new(x+2*s,y),new(x+size,y),new(x+size,y+size-2*s)});using var fill=new SolidBrush(hover?p.Control:p.Main);g.FillRectangle(fill,x,y+2*s,size-2*s,size-2*s);g.DrawRectangle(pen,x,y+2*s,size-2*s,size-2*s);}
        else g.DrawRectangle(pen,x,y,size,size);
    }
}
