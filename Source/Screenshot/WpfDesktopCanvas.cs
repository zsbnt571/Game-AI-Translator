using W=System.Windows;
using WI=System.Windows.Input;
using WM=System.Windows.Media;
namespace ScreenshotTranslationUiTester;

internal sealed class WpfDesktopCanvas : W.FrameworkElement
{
    private readonly DesktopForm model;
    private WM.DrawingGroup? preparedFrame;
    private readonly WM.DrawingVisual? snapshotVisual;
    protected override int VisualChildrenCount => snapshotVisual is null ? 0 : 1;
    protected override WM.Visual GetVisualChild(int index) => index == 0 && snapshotVisual is not null ? snapshotVisual : throw new ArgumentOutOfRangeException(nameof(index));
    internal WpfDesktopCanvas(DesktopForm model)
    {
        this.model=model;model.NativeInputSurface=this;Focusable=true;ClipToBounds=true;
        if(model.NativeSnapshotPresentation){snapshotVisual=new();AddVisualChild(snapshotVisual);}
        void UpdateCursor()=>Cursor=WpfControlCoordinates.Cursor(model.Cursor);
        InvalidateEventHandler paint=(_,_)=>{preparedFrame=null;InvalidateVisual();};EventHandler cursor=(_,_)=>UpdateCursor();
        model.Invalidated+=paint;model.CursorChanged+=cursor;UpdateCursor();
        Unloaded+=(_,_)=>{model.Invalidated-=paint;model.CursorChanged-=cursor;};
        Loaded+=(_,_)=>{model.Invalidated-=paint;model.Invalidated+=paint;model.CursorChanged-=cursor;model.CursorChanged+=cursor;InvalidateVisual();};
        void Send(string kind,WI.MouseEventArgs e,MouseButtons button,int clicks=0,int delta=0)
        {
            if(model.IsDisposed||!model.Enabled)return;
            var p=e.GetPosition(this);model.NativeMouse(kind,new(button,clicks,(int)Math.Round(p.X*model.DeviceDpi/96d),(int)Math.Round(p.Y*model.DeviceDpi/96d),delta));InvalidateVisual();
        }
        MouseDown+=(_,e)=>{if(e.ChangedButton is WI.MouseButton.XButton1 or WI.MouseButton.XButton2)return;Focus();Send("down",e,e.ChangedButton==WI.MouseButton.Right?MouseButtons.Right:MouseButtons.Left,e.ClickCount);if(e.ClickCount==2)Send("double",e,MouseButtons.Left,2);};
        MouseMove+=(_,e)=>Send("move",e,e.LeftButton==WI.MouseButtonState.Pressed?MouseButtons.Left:e.RightButton==WI.MouseButtonState.Pressed?MouseButtons.Right:MouseButtons.None);
        MouseUp+=(_,e)=>{if(e.ChangedButton is WI.MouseButton.XButton1 or WI.MouseButton.XButton2)return;Send("up",e,e.ChangedButton==WI.MouseButton.Right?MouseButtons.Right:MouseButtons.Left,1);};
        MouseWheel+=(_,e)=>{Send("wheel",e,MouseButtons.None,0,e.Delta);e.Handled=true;};
    }
    internal void PrepareFrame(W.Size physicalSize)
    {
        var drawing = new WM.DrawingGroup();
        using (var context = drawing.Open()) model.RenderNativeBackground(context, physicalSize);
        drawing.Freeze();
        preparedFrame = drawing;
        // Record actual visual content while the HWND is still hidden. Merely
        // invalidating OnRender leaves the first visible frame unprepared.
        if(snapshotVisual is not null)
        {
            using var context=snapshotVisual.RenderOpen();
            context.PushTransform(new WM.ScaleTransform(96d/model.DeviceDpi,96d/model.DeviceDpi));
            context.DrawDrawing(drawing);context.Pop();
        }
        InvalidateVisual();
    }
    protected override void OnRender(WM.DrawingContext context)
    {
        if(snapshotVisual is not null)
        {
            if(preparedFrame is not null){preparedFrame=null;return;}
            using var frame=snapshotVisual.RenderOpen();
            frame.PushTransform(new WM.ScaleTransform(96d/model.DeviceDpi,96d/model.DeviceDpi));
            model.RenderNativeBackground(frame,new(ActualWidth*model.DeviceDpi/96d,ActualHeight*model.DeviceDpi/96d));frame.Pop();return;
        }
        context.PushTransform(new WM.ScaleTransform(96d/model.DeviceDpi,96d/model.DeviceDpi));
        if (preparedFrame is { } ready) { context.DrawDrawing(ready); preparedFrame = null; }
        else model.RenderNativeBackground(context,new(ActualWidth*model.DeviceDpi/96d,ActualHeight*model.DeviceDpi/96d));context.Pop();
    }
}
