using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ScreenshotTranslationUiTester;

// A wheel scroll belongs to the page; choosing a different option requires a click or key.
internal sealed class WorkspaceWheelGuard : NativeWindow
{
    private static readonly ConditionalWeakTable<ComboBox,WorkspaceWheelGuard> Owners=new();
    private readonly ComboBox owner;
    private WorkspaceWheelGuard(ComboBox box)
    {
        owner=box;box.HandleCreated+=(_,_)=>AssignHandle(box.Handle);box.HandleDestroyed+=(_,_)=>ReleaseHandle();
        if(box.IsHandleCreated)AssignHandle(box.Handle);
    }
    internal static void Attach(ComboBox box)=>Owners.GetValue(box,b=>new(b));
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle,int message,IntPtr wParam,IntPtr lParam);
    protected override void WndProc(ref Message message)
    {
        if(message.Msg==0x20A&&!owner.DroppedDown)
        {
            for(Control? p=owner.Parent;p is not null;p=p.Parent)
                if(p is WorkspaceScrollView view){view.ScrollWheel((short)((long)message.WParam>>16));break;}
                else if(p is ScrollableControl scroll&&scroll.AutoScroll&&scroll.IsHandleCreated){SendMessage(p.Handle,message.Msg,message.WParam,message.LParam);break;}
            message.Result=IntPtr.Zero;return;
        }
        base.WndProc(ref message);
        // GamePlanBox owns its entire buffered face, including its arrow. The
        // legacy overlay would paint a second arrow and a split focus border.
        if(owner is not GamePlanBox&&(message.Msg==0xF||((message.Msg is 0x317 or 0x318)&&message.WParam!=IntPtr.Zero))&&owner.IsHandleCreated&&!owner.IsDisposed)
        {
            using var graphics=message.Msg==0xF?Graphics.FromHwnd(owner.Handle):Graphics.FromHdc(message.WParam);var p=UiTheme.Current;
            int width=24*owner.DeviceDpi/96;var r=new Rectangle(Math.Max(1,owner.Width-width-1),1,width,Math.Max(1,owner.Height-2));
            using var background=new SolidBrush(p.Secondary);graphics.FillRectangle(background,r);
            WorkspaceSkin.Icon(graphics,"down",new(r.X+(r.Width-12*owner.DeviceDpi/96)/2,(owner.Height-12*owner.DeviceDpi/96)/2,12*owner.DeviceDpi/96,12*owner.DeviceDpi/96),owner.Enabled?p.Text:p.SecondaryText);
            using var border=new Pen(p.Border);graphics.DrawRectangle(border,0,0,Math.Max(0,owner.Width-1),Math.Max(0,owner.Height-1));
        }
    }
}
