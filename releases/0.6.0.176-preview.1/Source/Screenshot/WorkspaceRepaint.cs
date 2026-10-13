using System.Runtime.InteropServices;

namespace ScreenshotTranslationUiTester;

// Local double buffering does not repaint native descendants when their parent
// HWND is moved and resized together. Publish the settled layout in this mouse
// message, including child windows, rather than waiting for the idle paint queue.
internal static class WorkspaceRepaint
{
    internal static void SetRedraw(Control control,bool enabled)
    {if(control.IsHandleCreated&&!control.IsDisposed)SendMessage(control.Handle,0xB,enabled?new IntPtr(1):IntPtr.Zero,IntPtr.Zero);}
    [DllImport("user32.dll")]private static extern IntPtr SendMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam);
    internal static void Complete(Control control)
    {
        if(!control.IsHandleCreated||control.IsDisposed||!control.Visible)return;
        RedrawWindow(control.Handle,IntPtr.Zero,IntPtr.Zero,0x1|0x4|0x80|0x100|0x400);
        // RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW | RDW_FRAME
    }
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr window,IntPtr update,IntPtr region,uint flags);
}

internal sealed class WorkspaceSplitPane:BufferedPage
{
    protected override void WndProc(ref Message message)
    {
        if(message.Msg==0x46&&message.LParam!=IntPtr.Zero) // WM_WINDOWPOSCHANGING
        {
            var position=Marshal.PtrToStructure<WindowPosition>(message.LParam);
            // A copied strip from the old x/width is not a valid child layout.
            if((position.Flags&3)!=3){position.Flags|=0x100;Marshal.StructureToPtr(position,message.LParam,false);} // SWP_NOCOPYBITS
        }
        base.WndProc(ref message);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition {internal IntPtr Window,After;internal int X,Y,Width,Height;internal uint Flags;}
}
