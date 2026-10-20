using System.Drawing;
using System.Runtime.InteropServices;

namespace FusionCoverCapture;

// WGC includes non-client chrome. Validate the game's pixels, never its title bar.
internal static class CoverClientArea
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X,Y; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window,out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window,out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window,ref NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window,int attribute,out Rect rect,int size);

    internal static bool TryGet(IntPtr window,Size frame,out Rectangle crop)
    {
        crop=Rectangle.Empty;
        // DWM coordinates are physical; make user32 coordinates physical as well.
        var prior=SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            if(!GetClientRect(window,out var client))return false;
            var origin=new NativePoint{X=client.Left,Y=client.Top};
            var end=new NativePoint{X=client.Right,Y=client.Bottom};
            if(!ClientToScreen(window,ref origin)||!ClientToScreen(window,ref end))return false;
            var content=new Rect{Left=origin.X,Top=origin.Y,Right=end.X,Bottom=end.Y};
            if(DwmGetWindowAttribute(window,9,out var visible,Marshal.SizeOf<Rect>())==0 &&
                TryMap(visible,content,frame,out crop))return true;
            return GetWindowRect(window,out var whole)&&TryMap(whole,content,frame,out crop);
        }
        finally{if(prior!=IntPtr.Zero)SetThreadDpiAwarenessContext(prior);}
    }

    internal static bool TryMap(Rect window,Rect client,Size frame,out Rectangle crop)
    {
        crop=Rectangle.Empty;
        // Refuse a stale resize frame; guessing an offset can retain title chrome.
        if((long)window.Right-window.Left!=frame.Width||(long)window.Bottom-window.Top!=frame.Height)return false;
        var x=(long)client.Left-window.Left;var y=(long)client.Top-window.Top;
        var width=(long)client.Right-client.Left;var height=(long)client.Bottom-client.Top;
        if(x<0||y<0||width<32||height<18||x+width>frame.Width||y+height>frame.Height)return false;
        crop=new Rectangle((int)x,(int)y,(int)width,(int)height);return true;
    }
}
