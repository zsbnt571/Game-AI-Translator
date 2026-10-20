using System.ComponentModel;
using System.Windows.Threading;
using W = System.Windows;

namespace ScreenshotTranslationUiTester;

/// <summary>
/// Application lifetime and UI-thread dispatch, independent of a Forms window.
/// The optional legacy view exists only when the explicit legacy UI is selected.
/// </summary>
public abstract partial class DesktopApplicationController : Component, IWin32Window
{
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly LegacyApplicationWindow? legacy;
    private bool closed, disposed, disposing;
    private IntPtr nativeHandle;
    protected virtual W.Window? NativeView => null;
    protected virtual bool PreferNativeHost => WpfWorkspace.Enabled;
    protected bool UsesNativeHost { get; }
    internal bool HasLegacyWindow => legacy is not null;
    internal Form LegacyWindow => legacy ?? throw new InvalidOperationException("The native application has no legacy window.");
    internal static DesktopApplicationController? ControllerFor(Form window) => (window as LegacyApplicationWindow)?.Controller;
    protected DesktopApplicationController()
    {
        UsesNativeHost = PreferNativeHost;
        if (!UsesNativeHost) legacy = new(this);
        else SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        Controls = new(legacy);
    }
    public bool IsDisposed => disposed;
    public bool Disposing => disposing;
    // Forms permits this readiness check from workers. WindowInteropHelper does
    // not: even reading Handle verifies WPF thread ownership. Cache it on the UI
    // thread so persistence notifications can safely decide whether to dispatch.
    public bool IsHandleCreated
    {
        get
        {
            if(legacy is not null)return legacy.IsHandleCreated;
            if(disposed)return false;
            if(Volatile.Read(ref nativeHandle)!=IntPtr.Zero)return true;
            if(!dispatcher.CheckAccess()||NativeView is not {} view)return false;
            var value=new System.Windows.Interop.WindowInteropHelper(view).Handle;
            Volatile.Write(ref nativeHandle,value);return value!=IntPtr.Zero;
        }
    }
    public virtual IntPtr Handle => legacy is not null ? legacy.Handle : NativeView is { } view ? new System.Windows.Interop.WindowInteropHelper(view).EnsureHandle() : IntPtr.Zero;
    public bool InvokeRequired => legacy?.InvokeRequired ?? !dispatcher.CheckAccess();
    public IAsyncResult BeginInvoke(Delegate action, params object?[]? arguments) => legacy is not null
        ? legacy.BeginInvoke(action, arguments) : dispatcher.InvokeAsync(() => action.DynamicInvoke(arguments)).Task;
    public IAsyncResult BeginInvoke(Action action) => BeginInvoke((Delegate)action);
    public object? Invoke(Delegate action, params object?[]? arguments) => legacy is not null ? legacy.Invoke(action, arguments) : dispatcher.Invoke(() => action.DynamicInvoke(arguments));
    public void Invoke(Action action) { if (legacy is not null) legacy.Invoke(action); else dispatcher.Invoke(action); }
    public object? EndInvoke(IAsyncResult result) => result is Task<object?> task ? task.GetAwaiter().GetResult() : legacy!.EndInvoke(result);
    public void CreateControl() { if (legacy is not null) legacy.CreateControl(); else _ = Handle; }
    public void SuspendLayout() => legacy?.SuspendLayout();
    public void ResumeLayout(bool performLayout = true) => legacy?.ResumeLayout(performLayout);
    public void PerformLayout() => legacy?.PerformLayout();
    public void Invalidate() { if (legacy is not null) legacy.Invalidate(); else Invalidate(ClientRectangle); }
    public void Invalidate(bool invalidateChildren) { if (legacy is not null) legacy.Invalidate(invalidateChildren); else Invalidate(ClientRectangle); }
    public void Refresh() { if (legacy is not null) legacy.Refresh(); else NativeView?.UpdateLayout(); }
    public void Update() { if (legacy is not null) legacy.Update(); else NativeView?.UpdateLayout(); }
    public void Show() { if (legacy is not null) legacy.Show(); else NativeView?.Show(); }
    public void Hide() { if (legacy is not null) legacy.Hide(); else NativeView?.Hide(); }
    public void Activate() { if (legacy is not null) legacy.Activate(); else NativeView?.Activate(); }
    public void BringToFront() => Activate();
    public bool Focus() => legacy?.Focus() ?? NativeView?.Focus() ?? false;
    public bool ContainsFocus => legacy?.ContainsFocus ?? NativeView?.IsActive ?? false;
    public bool Focused => legacy?.Focused ?? NativeView?.IsActive ?? false;
    public bool Visible { get => legacy?.Visible ?? NativeView?.IsVisible ?? false; set { if (value) Show(); else Hide(); } }
    public void Close()
    {
        if (legacy is not null) { legacy.Close(); return; }
        if (closed || disposed) return;
        var request = new FormClosingEventArgs(CloseReason.UserClosing, false);
        OnFormClosing(request);
        if (request.Cancel) return;
        CompleteClose(CloseReason.UserClosing, true);
    }
    protected void CompleteClose(CloseReason reason, bool dispose)
    {
        if (!closed) { closed = true; OnFormClosed(new(reason)); }
        if (dispose) Dispose();
    }
    protected virtual void OnLoad(EventArgs e) => Load?.Invoke(this, e);
    protected virtual void OnShown(EventArgs e) => Shown?.Invoke(this, e);
    protected virtual void OnFormClosing(FormClosingEventArgs e) => FormClosing?.Invoke(this, e);
    protected virtual void OnFormClosed(FormClosedEventArgs e) => FormClosed?.Invoke(this, e);
    protected virtual void OnHandleCreated(EventArgs e) => HandleCreated?.Invoke(this, e);
    protected virtual void OnResize(EventArgs e) => Resize?.Invoke(this, e);
    protected virtual void OnSizeChanged(EventArgs e) => SizeChanged?.Invoke(this, e);
    protected virtual void OnResizeBegin(EventArgs e) => ResizeBegin?.Invoke(this, e);
    protected virtual void OnResizeEnd(EventArgs e) => ResizeEnd?.Invoke(this, e);
    protected virtual void OnVisibleChanged(EventArgs e) => VisibleChanged?.Invoke(this, e);
    protected virtual void OnPaint(PaintEventArgs e) => Paint?.Invoke(this, e);
    protected virtual void OnPaintBackground(PaintEventArgs e) => legacy?.DefaultPaintBackground(e);
    protected virtual CreateParams CreateParams => legacy?.DefaultCreateParams ?? new();
    protected virtual bool ProcessCmdKey(ref Message message, Keys key) => legacy?.DefaultCommandKey(ref message, key) ?? false;
    protected virtual void WndProc(ref Message message) => legacy!.DefaultWindowProc(ref message);
    protected override void Dispose(bool disposing)
    {
        if (disposed || this.disposing) return;
        this.disposing = disposing;
        try
        {
            if (disposing && !closed && UsesNativeHost) { closed = true; OnFormClosed(new(CloseReason.None)); }
            legacy?.Dispose();
            disposed = true;
            NativeHostReleased();
            base.Dispose(disposing);
        }
        finally { this.disposing = false; }
    }
    public event EventHandler? Load, Shown, HandleCreated, Activated, Deactivate, Resize, ResizeBegin, ResizeEnd, TextChanged, FontChanged, SizeChanged, LocationChanged, VisibleChanged;
    public event DpiChangedEventHandler? DpiChanged;
    public event FormClosingEventHandler? FormClosing;
    public event FormClosedEventHandler? FormClosed;
    public event PaintEventHandler? Paint;
    internal void NativeHostCreated()
    {
        Volatile.Write(ref nativeHandle,NativeView is {} view?new System.Windows.Interop.WindowInteropHelper(view).Handle:IntPtr.Zero);
        OnHandleCreated(EventArgs.Empty);
    }
    internal void NativeHostReleased()=>Volatile.Write(ref nativeHandle,IntPtr.Zero);
    internal void NativeHostActivated() => Activated?.Invoke(this, EventArgs.Empty);
    internal void NativeHostDeactivated() => Deactivate?.Invoke(this, EventArgs.Empty);
    internal void NativeHostResized() { CacheNativeGeometry(); OnResize(EventArgs.Empty); OnSizeChanged(EventArgs.Empty); ClientSizeChanged?.Invoke(this,EventArgs.Empty); }
    internal void NativeHostMoved() { CacheNativeGeometry(); LocationChanged?.Invoke(this,EventArgs.Empty); }
    private void CacheNativeGeometry() { if(NativeBounds is { } bounds){size=bounds.Size;location=bounds.Location;}if(NativeView is { } view)windowState=(FormWindowState)(int)view.WindowState; }
    internal void NativeHostVisibilityChanged() => OnVisibleChanged(EventArgs.Empty);
    private sealed partial class LegacyApplicationWindow : Form
    {
        private readonly DesktopApplicationController owner;
        internal DesktopApplicationController Controller => owner;
        internal LegacyApplicationWindow(DesktopApplicationController owner)
        {
            this.owner = owner;
            Load += (_, e) => owner.OnLoad(e); Shown += (_, e) => owner.OnShown(e);
            FormClosing += (_, e) => owner.OnFormClosing(e);
            FormClosed += (_, e) => { owner.closed = true; owner.OnFormClosed(e); };
            HandleCreated += (_, e) => owner.OnHandleCreated(e);
            Activated += (_, e) => owner.Activated?.Invoke(owner, e);
            DpiChanged += (_, e) => owner.DpiChanged?.Invoke(owner, e);
            Deactivate += (_, e) => owner.Deactivate?.Invoke(owner, e);
            Resize += (_, e) => owner.OnResize(e);
            ResizeBegin += (_, e) => owner.OnResizeBegin(e);
            ResizeEnd += (_, e) => owner.OnResizeEnd(e);
            SizeChanged += (_, e) => owner.OnSizeChanged(e);
            VisibleChanged += (_, e) => owner.OnVisibleChanged(e);
            LocationChanged += (_, e) => owner.LocationChanged?.Invoke(owner, e);
            TextChanged += (_, e) => owner.TextChanged?.Invoke(owner, e);
            FontChanged += (_, e) => owner.FontChanged?.Invoke(owner, e);
            Paint += (_, e) => owner.OnPaint(e);
            KeyDown += (_, e) => owner.OnKeyDown(e);
            MouseDown += (_, e) => owner.OnMouseDown(e); MouseMove += (_, e) => owner.OnMouseMove(e); MouseUp += (_, e) => owner.OnMouseUp(e);
            MouseDoubleClick += (_, e) => owner.OnMouseDoubleClick(e); MouseWheel += (_, e) => owner.OnMouseWheel(e);
            GotFocus += (_, e) => owner.OnGotFocus(e); LostFocus += (_, e) => owner.OnLostFocus(e);
            Layout += (_, e) => owner.OnLayout(e);
            BackColorChanged += (_, e) => owner.BackColorChanged?.Invoke(owner,e); ForeColorChanged += (_, e) => owner.ForeColorChanged?.Invoke(owner,e);
            CursorChanged += (_, e) => owner.CursorChanged?.Invoke(owner,e); ClientSizeChanged += (_, e) => owner.ClientSizeChanged?.Invoke(owner,e);
            Invalidated += (_, e) => owner.Invalidated?.Invoke(owner,e);
        }
        protected override void WndProc(ref Message m) { if (owner is null) base.WndProc(ref m); else owner.WndProc(ref m); }
        internal void DefaultWindowProc(ref Message m) => base.WndProc(ref m);
        protected override bool ProcessCmdKey(ref Message m, Keys key) => owner is null ? base.ProcessCmdKey(ref m,key) : owner.ProcessCmdKey(ref m,key);
        internal bool DefaultCommandKey(ref Message m, Keys key) => base.ProcessCmdKey(ref m,key);
        protected override CreateParams CreateParams => owner?.CreateParams ?? base.CreateParams;
        internal CreateParams DefaultCreateParams => base.CreateParams;
        protected override void OnPaintBackground(PaintEventArgs e) { if(owner is null)base.OnPaintBackground(e);else owner.OnPaintBackground(e); }
        internal void DefaultPaintBackground(PaintEventArgs e) => base.OnPaintBackground(e);
        internal void CenterView() => CenterToScreen();
    }
    public sealed class ViewControls : System.Collections.Generic.IEnumerable<Control>
    {
        private readonly Form? view;
        internal ViewControls(Form? view) => this.view = view;
        public int Count => view?.Controls.Count ?? 0;
        public void Add(Control control) => (view ?? throw new InvalidOperationException("Native controllers cannot own Forms controls.")).Controls.Add(control);
        public void AddRange(Control[] controls) { foreach (var item in controls) Add(item); }
        public void Clear() => view?.Controls.Clear();
        public void Remove(Control control) => view?.Controls.Remove(control);
        public IEnumerator<Control> GetEnumerator() => (view?.Controls.Cast<Control>() ?? []).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    public ViewControls Controls { get; }
    private string text = "";
    public string Text { get => legacy?.Text ?? text; set { if (legacy is not null) legacy.Text = value; else if (text != value) { text = value; TextChanged?.Invoke(this, EventArgs.Empty); } } }
    private Font font = SystemFonts.MessageBoxFont!;
    public Font Font { get => legacy?.Font ?? font; set { if (legacy is not null) legacy.Font = value; else { font = value; FontChanged?.Invoke(this, EventArgs.Empty); } } }
    public int DeviceDpi => legacy?.DeviceDpi ?? (NativeView is { } view ? (int)Math.Round(System.Windows.Media.VisualTreeHelper.GetDpi(view).PixelsPerInchX) : NativeDesktopDpi());
    private static int NativeDesktopDpi() { try { return (int)GetDpiForSystem(); } catch { return 96; } }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    private Size size = new(1440, 880), minimumSize, maximumSize;
    private Point location;
    private Rectangle? NativeBounds
    {
        get
        {
            if (NativeView is not { } view) return null;
            var handle = new System.Windows.Interop.WindowInteropHelper(view).Handle;
            return handle != IntPtr.Zero && GetWindowRect(handle, out var bounds) ? Rectangle.FromLTRB(bounds.Left,bounds.Top,bounds.Right,bounds.Bottom) : null;
        }
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { internal int Left,Top,Right,Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr handle, out NativeRect rectangle);
    public Size Size { get => legacy?.Size ?? NativeBounds?.Size ?? size; set { if (legacy is not null) legacy.Size = value; else { size=value; if (NativeView is { } view) { view.Width=value.Width*96d/DeviceDpi; view.Height=value.Height*96d/DeviceDpi; } } } }
    public Size ClientSize
    {
        get
        {
            if (legacy is not null) return legacy.ClientSize;
            if (IsHandleCreated && GetClientRect(Handle, out var bounds)) return new(bounds.Right-bounds.Left,bounds.Bottom-bounds.Top);
            return Size;
        }
        set { if (legacy is not null) legacy.ClientSize=value; else { var outer=Size;var client=ClientSize;Size=new(value.Width+outer.Width-client.Width,value.Height+outer.Height-client.Height); } }
    }
    public Rectangle ClientRectangle => new(Point.Empty, ClientSize);
    public Rectangle DisplayRectangle => legacy?.DisplayRectangle ?? ClientRectangle;
    public Size MinimumSize { get => legacy?.MinimumSize ?? minimumSize; set { if (legacy is not null) legacy.MinimumSize = value; else minimumSize = value; } }
    public Size MaximumSize { get => legacy?.MaximumSize ?? maximumSize; set { if (legacy is not null) legacy.MaximumSize = value; else maximumSize = value; } }
    public Point Location { get => legacy?.Location ?? NativeBounds?.Location ?? location; set { if (legacy is not null) legacy.Location = value; else { location=value; if (NativeView is { } view) { view.Left=value.X*96d/DeviceDpi; view.Top=value.Y*96d/DeviceDpi; } } } }
    public Rectangle Bounds { get => new(Location, Size); set { Location = value.Location; Size = value.Size; } }
    public int Width { get => Size.Width; set => Size = new(value, Height); }
    public int Height { get => Size.Height; set => Size = new(Width, value); }
    public int Left { get => Location.X; set => Location = new(value, Top); }
    public int Top { get => Location.Y; set => Location = new(Left, value); }
    public Point PointToScreen(Point point) { if (legacy is not null) return legacy.PointToScreen(point); var scale = DeviceDpi / 96d; var p = NativeView!.PointToScreen(new(point.X / scale, point.Y / scale)); return new((int)p.X, (int)p.Y); }
    public Point PointToClient(Point point) { if (legacy is not null) return legacy.PointToClient(point); var scale = DeviceDpi / 96d; var p = NativeView!.PointFromScreen(new(point.X, point.Y)); return new((int)(p.X * scale), (int)(p.Y * scale)); }
    public Rectangle RectangleToScreen(Rectangle rectangle) => new(PointToScreen(rectangle.Location), rectangle.Size);
    public Form[] OwnedForms => legacy?.OwnedForms ?? [];
    public void DrawToBitmap(Bitmap bitmap, Rectangle bounds) => LegacyWindow.DrawToBitmap(bitmap, bounds);
    public void Scale(SizeF factor) => LegacyWindow.Scale(factor);
    public void CenterToScreen() => legacy?.CenterView();
    public Form FindForm() => LegacyWindow;
    public Control? GetChildAtPoint(Point point) => legacy?.GetChildAtPoint(point);
    public static Keys ModifierKeys => Control.ModifierKeys;
    public static Point MousePosition => Control.MousePosition;
    public static MouseButtons MouseButtons => Control.MouseButtons;
    // Presentation settings are ordinary values until a view is attached.
    private Color backColor, foreColor;
    public Color BackColor { get => legacy?.BackColor ?? backColor; set { if (legacy is not null) legacy.BackColor = value; else if(backColor!=value){backColor = value;BackColorChanged?.Invoke(this,EventArgs.Empty);} } }
    public Color ForeColor { get => legacy?.ForeColor ?? foreColor; set { if (legacy is not null) legacy.ForeColor = value; else if(foreColor!=value){foreColor = value;ForeColorChanged?.Invoke(this,EventArgs.Empty);} } }
    public AutoScaleMode AutoScaleMode { get => legacy?.AutoScaleMode ?? AutoScaleMode.None; set { if (legacy is not null) legacy.AutoScaleMode = value; } }
    public SizeF AutoScaleDimensions { get => legacy?.AutoScaleDimensions ?? new(96, 96); set { if (legacy is not null) legacy.AutoScaleDimensions = value; } }
    private FormBorderStyle formBorderStyle=FormBorderStyle.Sizable;
    private FormStartPosition startPosition=FormStartPosition.WindowsDefaultLocation;
    public FormBorderStyle FormBorderStyle { get => legacy?.FormBorderStyle ?? formBorderStyle; set { if (legacy is not null) legacy.FormBorderStyle = value; else formBorderStyle=value; } }
    public FormStartPosition StartPosition { get => legacy?.StartPosition ?? startPosition; set { if (legacy is not null) legacy.StartPosition = value; else startPosition=value; } }
    public Padding Padding { get => legacy?.Padding ?? Padding.Empty; set { if (legacy is not null) legacy.Padding = value; } }
    public DockStyle Dock { get => legacy?.Dock ?? DockStyle.None; set { if (legacy is not null) legacy.Dock = value; } }
    public bool TopLevel { get => legacy?.TopLevel ?? true; set { if (legacy is not null) legacy.TopLevel = value; } }
    private bool showInTaskbar=true;
    private FormWindowState windowState;
    public bool ShowInTaskbar { get => legacy?.ShowInTaskbar ?? NativeView?.ShowInTaskbar ?? showInTaskbar; set { if (legacy is not null) legacy.ShowInTaskbar = value; else {showInTaskbar=value;if (NativeView is { } view) view.ShowInTaskbar = value;} } }
    public FormWindowState WindowState { get => legacy?.WindowState ?? (NativeView is { } view?(FormWindowState)(int)view.WindowState:windowState); set { if (legacy is not null) legacy.WindowState = value; else {windowState=value;if (NativeView is { } view) view.WindowState = (W.WindowState)(int)value;} } }
    private Icon? icon;
    public Icon? Icon { get => legacy?.Icon ?? icon; set { if (legacy is not null) legacy.Icon = value; else icon = value; } }
    public bool KeyPreview { get => legacy?.KeyPreview ?? true; set { if (legacy is not null) legacy.KeyPreview = value; } }
    private bool topMost;
    private double opacity = 1;
    public bool TopMost { get => legacy?.TopMost ?? NativeView?.Topmost ?? topMost; set { if (legacy is not null) legacy.TopMost = value; else { topMost = value; if (NativeView is { } view) view.Topmost = value; } } }
    public double Opacity { get => legacy?.Opacity ?? NativeView?.Opacity ?? opacity; set { if (legacy is not null) legacy.Opacity = value; else { opacity = value; if (NativeView is { } view) view.Opacity = value; } } }
    private bool enabled=true;
    public bool Enabled { get => legacy?.Enabled ?? NativeView?.IsEnabled ?? enabled; set { if (legacy is not null) legacy.Enabled=value; else {enabled=value;if (NativeView is { } view) view.IsEnabled=value;} } }
    private object? tag;
    private string name="";
    public object? Tag { get=>legacy?.Tag??tag;set { if(legacy is not null)legacy.Tag=value;else tag=value; } }
    public string Name { get=>legacy?.Name??name;set { if(legacy is not null)legacy.Name=value;else name=value; } }
}
