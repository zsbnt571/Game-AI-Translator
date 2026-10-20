using System.ComponentModel;
using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;

namespace ScreenshotTranslationUiTester;

internal sealed class WpfFormWindow : W.Window
{
    private readonly DesktopForm model;
    private readonly WpfSettingsFields fields = new() { RetainLayout = true };
    private bool disposing, syncingGeometry;
    private readonly WpfDesktopCanvas? canvas;
    internal bool ModalView { get; set; }
    internal WpfFormWindow(DesktopForm model)
    {
        this.model = model; Resources.MergedDictionaries.Add(WpfThemeResources.Create()); WpfThemeResources.Apply(Resources);
        var area = W.SystemParameters.WorkArea; double scale = model.DeviceDpi / 96d;
        Title = model.Text; Width = Math.Clamp(model.Width / scale, 180, area.Width); Height = Math.Clamp(model.Height / scale, 100, area.Height);
        MinWidth = Math.Min(area.Width, model.MinimumSize.Width / scale); MinHeight = Math.Min(area.Height, model.MinimumSize.Height / scale);
        Topmost = model.TopMost; ShowInTaskbar = model.ShowInTaskbar; Opacity = model.Opacity; ShowActivated=!model.NativeShowWithoutActivation; IsEnabled=model.Enabled;
        WindowStartupLocation = model.StartPosition == FormStartPosition.Manual ? W.WindowStartupLocation.Manual : W.WindowStartupLocation.CenterOwner;
        if (model.StartPosition == FormStartPosition.Manual) { Left = model.Left / scale; Top = model.Top / scale; }
        if (model.HasNativeBackground) { Left = model.Left / scale; Top = model.Top / scale; Width = model.Width / scale; Height = model.Height / scale; }
        ResizeMode = model.FormBorderStyle is FormBorderStyle.FixedDialog or FormBorderStyle.FixedSingle or FormBorderStyle.FixedToolWindow ? W.ResizeMode.NoResize : W.ResizeMode.CanResize;
        if (model.FormBorderStyle == FormBorderStyle.None) WindowStyle = W.WindowStyle.None;
        if (model.NativeSnapshotPresentation)
        {
            // Capture pixels cover the entire HWND. A resizable non-client frame
            // shrinks and offsets the canvas even with WindowStyle.None.
            ResizeMode = W.ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = System.Windows.Media.Brushes.Transparent;
        }
        if (model.NativeClickThrough) { AllowsTransparency = true; Background = System.Windows.Media.Brushes.Transparent; ShowActivated = false; SourceInitialized += (_, _) => ApplyClickThrough(); }
        UseLayoutRounding = true; SnapsToDevicePixels = true; FontFamily = new(model.Font.Name); FontSize = model.Font.SizeInPoints * 96 / 72;
        if(model.Icon is { } icon)
        {
            var bitmap=System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle,W.Int32Rect.Empty,System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());bitmap.Freeze();Icon=bitmap;
        }
        if (!model.NativeClickThrough && !model.NativeSnapshotPresentation) Background = WpfDrawing.Brush(model.BackColor); Foreground = WpfDrawing.Brush(model.ForeColor);
        var root = new WC.Grid();
        canvas = model.HasNativeBackground ? new(model) : null;
        if (canvas is not null) root.Children.Add(canvas);
        root.Children.Add(model.CreateNativeContent(fields)); Content = root;
        Activated += (_, _) => model.NativeActivated(true); Deactivated += (_, _) => model.NativeActivated(false);
        GotKeyboardFocus+=(_,_)=>model.NativeGotFocus();LostKeyboardFocus+=(_,_)=>model.NativeLostFocus();
        IsVisibleChanged += (_, _) => model.NativeVisibilityChanged();
        model.TextChanged += ModelChanged; model.BackColorChanged += ModelChanged;
        model.SizeChanged += ModelGeometryChanged; model.LocationChanged += ModelGeometryChanged;
        SizeChanged += (_, _) => SyncModelGeometry(); LocationChanged += (_, _) => { if(IsLoaded)model.NativeHostMoved(); };
        SourceInitialized += (_, _) =>
        {
            System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)?.AddHook(WindowMessage);
            model.NativeHandleReady();
        };
        Closing += (_, e) => { if (!disposing) e.Cancel = !model.NativeClosing(); };
        Closed += (_, _) => { fields.Dispose(); model.TextChanged -= ModelChanged; model.BackColorChanged -= ModelChanged; model.SizeChanged -= ModelGeometryChanged; model.LocationChanged -= ModelGeometryChanged; if (!disposing) model.NativeClosed(); };
        PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == WI.Key.System ? e.SystemKey : e.Key;
            if (model.NativeProcessKey(key)) { e.Handled = true; return; }
            if (model.NativeCommandKey((Keys)WI.KeyInterop.VirtualKeyFromKey(key) | Control.ModifierKeys)) { e.Handled = true; return; }
            var args = new KeyEventArgs((Keys)WI.KeyInterop.VirtualKeyFromKey(key) | Control.ModifierKeys); model.NativeKey(args);
            if (args.Handled || args.SuppressKeyPress) { e.Handled = true; return; }
            if (key == WI.Key.Escape && model.CancelButton is Button cancel) { WpfSettingsModel.Raise(cancel, "OnClick"); model.FlushNativeResult(); e.Handled = true; }
            else if (key == WI.Key.Enter && model.AcceptButton is Button accept && WI.Keyboard.FocusedElement is not WC.TextBox { AcceptsReturn: true } and not WC.RichTextBox) { WpfSettingsModel.Raise(accept, "OnClick"); model.FlushNativeResult(); e.Handled = true; }
        };
        WindowState = (W.WindowState)(int)model.WindowState;
    }
    private void ModelChanged(object? sender, EventArgs e) { Title = model.Text; WpfThemeResources.Apply(Resources); if (!model.NativeClickThrough && !model.NativeSnapshotPresentation) Background = WpfDrawing.Brush(model.BackColor); fields.Refresh(); }
    internal void PrepareSnapshotFrame(Rectangle screenBounds)
    {
        if (IsVisible) throw new InvalidOperationException("Prepare the capture frame before showing its window.");
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        // Native screen bounds are physical pixels, including negative origins.
        // Do not round them through a second DIP/monitor-coordinate conversion.
        if (!SetWindowPos(hwnd, IntPtr.Zero, screenBounds.X, screenBounds.Y, screenBounds.Width, screenBounds.Height, 0x0014))
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        canvas?.PrepareFrame(new(screenBounds.Width, screenBounds.Height));
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    private IntPtr WindowMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        // Forward the native resize loop to placement and scroll restoration.
        if (message==0x0231) model.NativeResizeStarted(); // WM_ENTERSIZEMOVE
        else if (message==0x0232) { SyncModelGeometry(); model.NativeResizeFinished(); }
        return IntPtr.Zero;
    }
    private void ModelGeometryChanged(object? sender, EventArgs e)
    {
        if (syncingGeometry || model.MeasuringNativeLayout || !IsLoaded || WindowState != W.WindowState.Normal) return;
        syncingGeometry = true;
        try { double scale = model.DeviceDpi / 96d; Left = model.Left / scale; Top = model.Top / scale; Width = Math.Max(MinWidth, model.Width / scale); Height = Math.Max(MinHeight, model.Height / scale); }
        finally { syncingGeometry = false; }
    }
    private void SyncModelGeometry()
    {
        if (syncingGeometry || !IsLoaded || WindowState != W.WindowState.Normal || !double.IsFinite(Left) || !double.IsFinite(Top)) return;
        syncingGeometry = true;
        try { model.NativeHostResized(); }
        finally { syncingGeometry = false; }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint="SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    private void ApplyClickThrough() { var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle; var styles = GetWindowLongPtr(hwnd, -20).ToInt64(); SetWindowLongPtr(hwnd, -20, new(styles | 0x20 | 0x80 | 0x08000000)); }
    internal void DisposeController() { disposing = true; Close(); }
}

internal sealed class WpfControllerCanvas : W.FrameworkElement
{
    private readonly Control model; private readonly Action<System.Windows.Media.DrawingContext, W.Size> draw;
    private readonly InvalidateEventHandler invalidated;
    internal WpfControllerCanvas(Control model, Action<System.Windows.Media.DrawingContext, W.Size> draw)
    {
        this.model = model; this.draw = draw; Focusable = true; ClipToBounds = true;
        void UpdateCursor() => Cursor = WpfControlCoordinates.Cursor(model.Cursor);
        EventHandler cursorChanged = (_, _) => UpdateCursor(); model.CursorChanged += cursorChanged; UpdateCursor();
        WpfControlCoordinates.Register(model, this);
        invalidated = (_, _) => InvalidateVisual(); model.Invalidated += invalidated;
        Loaded += (_, _) => { model.Invalidated -= invalidated; model.Invalidated += invalidated; model.CursorChanged -= cursorChanged; model.CursorChanged += cursorChanged; UpdateCursor(); };
        Unloaded += (_, _) => { model.Invalidated -= invalidated; model.CursorChanged -= cursorChanged; };
        MouseLeave += (_, _) => WpfSettingsModel.Raise(model, "OnMouseLeave");
        SizeChanged += (_, _) => { model.Size = new(Math.Max(1, (int)Math.Round(ActualWidth * model.DeviceDpi / 96)), Math.Max(1, (int)Math.Round(ActualHeight * model.DeviceDpi / 96))); InvalidateVisual(); };
        void Send(string name, WI.MouseEventArgs e, MouseButtons button, int clicks = 0, int delta = 0)
        {
            if (!WpfSettingsModel.CanDispatch(model)) return;
            var p = e.GetPosition(this); var args = new MouseEventArgs(button, clicks, (int)Math.Round(p.X * model.DeviceDpi / 96), (int)Math.Round(p.Y * model.DeviceDpi / 96), delta);
            var method = model.GetType().GetMethod(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, [typeof(MouseEventArgs)], null); method?.Invoke(model, [args]); InvalidateVisual();
        }
        MouseDown += (_, e) => { if (e.ChangedButton is WI.MouseButton.XButton1 or WI.MouseButton.XButton2) return; Focus(); CaptureMouse(); Send("OnMouseDown", e, e.ChangedButton == WI.MouseButton.Right ? MouseButtons.Right : MouseButtons.Left, e.ClickCount); if (e.ClickCount == 2) Send("OnMouseDoubleClick", e, MouseButtons.Left, 2); };
        MouseMove += (_, e) => Send("OnMouseMove", e, e.LeftButton == WI.MouseButtonState.Pressed ? MouseButtons.Left : e.RightButton == WI.MouseButtonState.Pressed ? MouseButtons.Right : MouseButtons.None);
        MouseUp += (_, e) => { if (e.ChangedButton is WI.MouseButton.XButton1 or WI.MouseButton.XButton2) return; var button = e.ChangedButton == WI.MouseButton.Right ? MouseButtons.Right : MouseButtons.Left; Send("OnMouseUp", e, button, 1); Send("OnMouseClick", e, button, 1); WpfSettingsModel.Raise(model, "OnClick"); ReleaseMouseCapture(); };
        MouseWheel += (_, e) => { Send("OnMouseWheel", e, MouseButtons.None, 0, e.Delta); e.Handled = true; };
    }
    protected override void OnRender(System.Windows.Media.DrawingContext dc) { dc.PushTransform(new System.Windows.Media.ScaleTransform(96d / model.DeviceDpi, 96d / model.DeviceDpi)); draw(dc, new(ActualWidth * model.DeviceDpi / 96, ActualHeight * model.DeviceDpi / 96)); dc.Pop(); }
}
