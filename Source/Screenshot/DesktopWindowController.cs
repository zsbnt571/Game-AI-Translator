using W = System.Windows;
using WI = System.Windows.Input;

namespace ScreenshotTranslationUiTester;

// Window state and services are independent of Forms. A legacy window is
// allocated only by the explicit legacy path; native windows own their HWND.
public class DesktopForm : DesktopApplicationController
{
    internal WpfFormWindow? NativeWindow { get; private set; }
    protected override W.Window? NativeView => NativeWindow;
    protected override bool PreferNativeHost => UsesNativePresentation;
    internal virtual bool UsesNativePresentation => WpfWorkspace.Enabled;
    internal bool MeasuringNativeLayout;
    private bool loaded, shown;
    internal virtual W.FrameworkElement CreateNativeContent(WpfSettingsFields fields) => HasLegacyWindow ? fields.NativeLayout(LegacyWindow) : new System.Windows.Controls.Grid { IsHitTestVisible=false };
    internal W.FrameworkElement? NativeInputSurface { get; set; }
    internal bool NativeShowWithoutActivation => ShowWithoutActivation;
    public new bool Capture { get=>NativeInputSurface?.IsMouseCaptured??base.Capture;set { if(NativeInputSurface is { } surface){if(value)surface.CaptureMouse();else surface.ReleaseMouseCapture();}else base.Capture=value; } }
    internal virtual void RenderNativeBackground(System.Windows.Media.DrawingContext context, W.Size size) { }
    internal virtual bool HasNativeBackground => false;
    internal virtual bool NativeClickThrough => false;
    internal virtual bool NativeSnapshotPresentation => false;
    internal virtual void PrepareNativeShow() { }
    internal virtual bool NativeProcessKey(WI.Key key) => false;
    internal virtual bool RetainNativeDialogWindow => false;
    private WpfFormWindow Prepare(IWin32Window? owner)
    {
        ObjectDisposedException.ThrowIf(IsDisposed,this);
        if (NativeWindow is { } existing) return existing;
        var window = new WpfFormWindow(this); NativeWindow = window;
        var parent = NativeDialogWindow.OwnerWindow(owner);
        if (parent is not null && !ReferenceEquals(parent, window) && parent.IsVisible) window.Owner = parent;
        if (!loaded) { loaded = true; OnLoad(EventArgs.Empty); }
        shown = false;
        window.ContentRendered += (_, _) => { if (!shown) { shown = true; OnShown(EventArgs.Empty); } };
        return window;
    }
    public override IntPtr Handle => !UsesNativeHost ? base.Handle : new System.Windows.Interop.WindowInteropHelper(Prepare(null)).EnsureHandle();
    public new void Show() { if (!UsesNativeHost) base.Show(); else { var window = Prepare(null); PrepareNativeShow(); window.Show(); } }
    public void Show(IWin32Window? owner) { if (!UsesNativeHost) LegacyWindow.Show(owner); else { var window = Prepare(owner); PrepareNativeShow(); window.Show(); } }
    protected override void OnShown(EventArgs e) { shown = true; base.OnShown(e); }
    public DialogResult ShowDialog() => ShowDialog(null);
    public DialogResult ShowDialog(IWin32Window? owner)
    {
        if (!UsesNativeHost) return owner is null ? LegacyWindow.ShowDialog() : LegacyWindow.ShowDialog(owner);
        base.DialogResult = DialogResult.None; var window = Prepare(owner); shown = false; window.ModalView = true;
        try { PrepareNativeShow(); window.ShowDialog(); }
        finally { window.ModalView = false; }
        return base.DialogResult == DialogResult.None ? DialogResult.Cancel : base.DialogResult;
    }
    public new void Close() { if (NativeWindow is { } window) window.Close(); else base.Close(); }
    public new bool Visible { get => base.Visible; set { if (value) Show(); else Hide(); } }
    protected override void SetVisibleCore(bool value) { if (!UsesNativeHost) base.SetVisibleCore(value); else if(value)Show();else Hide(); }
    public new DialogResult DialogResult { get => base.DialogResult; set { base.DialogResult=value; FlushNativeResult(); } }
    internal void FlushNativeResult()
    {
        if (base.DialogResult == DialogResult.None || NativeWindow is not { ModalView: true } window) return;
        // Hiding exits WPF's modal loop while retaining the visual tree and HWND.
        // Reusable capture controllers must not re-parent their retained toolbar.
        if (RetainNativeDialogWindow) window.Hide(); else window.Close();
    }
    public new void DrawToBitmap(Bitmap bitmap,Rectangle targetBounds)
    {
        if(NativeWindow is not { } window){base.DrawToBitmap(bitmap,targetBounds);return;}
        var dpi=System.Windows.Media.VisualTreeHelper.GetDpi(window);
        var target=new System.Windows.Media.Imaging.RenderTargetBitmap(Math.Max(1,(int)Math.Ceiling(window.ActualWidth*dpi.DpiScaleX)),Math.Max(1,(int)Math.Ceiling(window.ActualHeight*dpi.DpiScaleY)),96*dpi.DpiScaleX,96*dpi.DpiScaleY,System.Windows.Media.PixelFormats.Pbgra32);target.Render(window);
        var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(target));using var stream=new MemoryStream();encoder.Save(stream);stream.Position=0;using var captured=new Bitmap(stream);using var drawing=Graphics.FromImage(bitmap);drawing.DrawImage(captured,targetBounds);
    }
    internal virtual void NativeVisibilityChanged()
    {
        OnVisibleChanged(EventArgs.Empty);
        if (!RetainNativeDialogWindow) return;
        if (!Visible) { shown = false; return; }
        // ContentRendered is not raised again when an existing WPF window is
        // unhidden. Each capture still needs its local focus/Shown lifecycle.
        NativeWindow?.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (Visible && !shown) { shown = true; OnShown(EventArgs.Empty); }
        }));
    }
    internal void NativeActivated(bool active) { if(active)OnActivated(EventArgs.Empty);else OnDeactivate(EventArgs.Empty); }
    internal bool NativeClosing() { var e=new FormClosingEventArgs(CloseReason.UserClosing,false);OnFormClosing(e);return !e.Cancel; }
    internal void NativeClosed() { bool modal=NativeWindow?.ModalView==true;NativeWindow=null;NativeHostReleased();OnVisibleChanged(EventArgs.Empty);CompleteClose(CloseReason.UserClosing,!modal); }
    internal void NativeKey(KeyEventArgs args)=>OnKeyDown(args);
    internal bool NativeCommandKey(Keys key) { var message=default(Message);return ProcessCmdKey(ref message,key); }
    internal void NativeHandleReady()=>NativeHostCreated();
    internal void NativeGotFocus()=>OnGotFocus(EventArgs.Empty);
    internal void NativeLostFocus()=>OnLostFocus(EventArgs.Empty);
    internal void NativeResizeStarted()=>OnResizeBegin(EventArgs.Empty);
    internal void NativeResizeFinished()=>OnResizeEnd(EventArgs.Empty);
    internal static void RunStandalone(DesktopForm controller)
    {
        if(controller.HasLegacyWindow){Application.Run(controller.LegacyWindow);return;}
        var app=W.Application.Current??new W.Application();app.ShutdownMode=W.ShutdownMode.OnExplicitShutdown;
        var window=controller.Prepare(null);var frame=new System.Windows.Threading.DispatcherFrame();
        EventHandler close=(_,_)=>frame.Continue=false;window.Closed+=close;
        try { window.Show();System.Windows.Threading.Dispatcher.PushFrame(frame); }
        finally { window.Closed-=close; }
    }
    internal void NativeMouse(string name,MouseEventArgs args)
    {
        switch(name){case "down":OnMouseDown(args);break;case "move":OnMouseMove(args);break;case "up":OnMouseUp(args);break;case "double":OnMouseDoubleClick(args);break;case "wheel":OnMouseWheel(args);break;}
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing&&NativeWindow is { } window){window.DisposeController();NativeWindow=null;}
        base.Dispose(disposing);
    }
}
