namespace ScreenshotTranslationUiTester;

public abstract partial class DesktopApplicationController
{
    public event EventHandler? GotFocus, LostFocus, BackColorChanged, ForeColorChanged, CursorChanged, ClientSizeChanged;
    public event LayoutEventHandler? Layout;
    public event KeyEventHandler? KeyDown;
    public event MouseEventHandler? MouseDown, MouseMove, MouseUp, MouseDoubleClick, MouseWheel;
    public event InvalidateEventHandler? Invalidated;
    protected virtual bool ShowWithoutActivation => false;
    protected virtual void OnActivated(EventArgs e) => Activated?.Invoke(this,e);
    protected virtual void OnDeactivate(EventArgs e) => Deactivate?.Invoke(this,e);
    protected virtual void OnGotFocus(EventArgs e) => GotFocus?.Invoke(this,e);
    protected virtual void OnLostFocus(EventArgs e) => LostFocus?.Invoke(this,e);
    protected virtual void OnLayout(LayoutEventArgs e) => Layout?.Invoke(this,e);
    protected virtual void OnKeyDown(KeyEventArgs e) => KeyDown?.Invoke(this,e);
    protected virtual void OnMouseDown(MouseEventArgs e) => MouseDown?.Invoke(this,e);
    protected virtual void OnMouseMove(MouseEventArgs e) => MouseMove?.Invoke(this,e);
    protected virtual void OnMouseUp(MouseEventArgs e) => MouseUp?.Invoke(this,e);
    protected virtual void OnMouseDoubleClick(MouseEventArgs e) => MouseDoubleClick?.Invoke(this,e);
    protected virtual void OnMouseWheel(MouseEventArgs e) => MouseWheel?.Invoke(this,e);
    protected virtual bool ProcessDialogKey(Keys key) => legacy?.DefaultDialogKey(key) ?? false;
    protected virtual void SetVisibleCore(bool value) { if(legacy is not null)legacy.Visible=value;else if(value)Show();else Hide(); }
    protected void SetStyle(ControlStyles style,bool enabled)=>legacy?.ApplyStyle(style,enabled);
    protected void UpdateStyles()=>legacy?.ApplyStyles();
    public void SetBounds(int x,int y,int width,int height)=>Bounds=new(x,y,width,height);
    public void Invalidate(Rectangle rectangle) { if(legacy is not null)legacy.Invalidate(rectangle);else { NativeView?.InvalidateVisual();Invalidated?.Invoke(this,new(rectangle)); } }
    public void Invalidate(Region region) { if(legacy is not null)legacy.Invalidate(region);else Invalidate(); }
    public Rectangle RestoreBounds
    {
        get
        {
            if(legacy is not null)return legacy.RestoreBounds;
            if(NativeView is { RestoreBounds: var r }&&!r.IsEmpty&&double.IsFinite(r.X))return new((int)Math.Round(r.X*DeviceDpi/96d),(int)Math.Round(r.Y*DeviceDpi/96d),(int)Math.Round(r.Width*DeviceDpi/96d),(int)Math.Round(r.Height*DeviceDpi/96d));
            return Bounds;
        }
    }
    private Cursor cursor=Cursors.Default;
    public Cursor Cursor { get=>legacy?.Cursor??cursor;set { if(legacy is not null)legacy.Cursor=value;else if(cursor!=value){cursor=value;CursorChanged?.Invoke(this,EventArgs.Empty);} } }
    private DialogResult dialogResult;
    public DialogResult DialogResult { get=>legacy?.DialogResult??dialogResult;set { if(legacy is not null)legacy.DialogResult=value;else dialogResult=value; } }
    private IButtonControl? acceptButton,cancelButton;
    public IButtonControl? AcceptButton { get=>legacy?.AcceptButton??acceptButton;set { if(legacy is not null)legacy.AcceptButton=value;else acceptButton=value; } }
    public IButtonControl? CancelButton { get=>legacy?.CancelButton??cancelButton;set { if(legacy is not null)legacy.CancelButton=value;else cancelButton=value; } }
    public Form? Owner { get=>legacy?.Owner;set { if(legacy is not null)legacy.Owner=value; } }
    public bool Modal=>legacy?.Modal??NativeView is WpfFormWindow { ModalView:true };
    public bool Capture { get=>legacy?.Capture??NativeView?.IsMouseCaptured??false;set { if(legacy is not null)legacy.Capture=value;else if(NativeView is { } view){if(value)view.CaptureMouse();else view.ReleaseMouseCapture();} } }
    public bool ShowIcon { get=>legacy?.ShowIcon??true;set { if(legacy is not null)legacy.ShowIcon=value; } }
    public bool MaximizeBox { get=>legacy?.MaximizeBox??true;set { if(legacy is not null)legacy.MaximizeBox=value; } }
    public bool MinimizeBox { get=>legacy?.MinimizeBox??true;set { if(legacy is not null)legacy.MinimizeBox=value; } }
    public Color TransparencyKey { get=>legacy?.TransparencyKey??Color.Empty;set { if(legacy is not null)legacy.TransparencyKey=value; } }
    protected bool DoubleBuffered { get=>legacy?.Buffered??true;set { if(legacy is not null)legacy.Buffered=value; } }
    private sealed partial class LegacyApplicationWindow
    {
        internal void ApplyStyle(ControlStyles style,bool enabled)=>SetStyle(style,enabled);
        internal void ApplyStyles()=>UpdateStyles();
        internal bool Buffered { get=>DoubleBuffered;set=>DoubleBuffered=value; }
        internal bool DefaultDialogKey(Keys key)=>base.ProcessDialogKey(key);
        protected override bool ProcessDialogKey(Keys key)=>owner is null?base.ProcessDialogKey(key):owner.ProcessDialogKey(key);
        protected override bool ShowWithoutActivation=>owner?.ShowWithoutActivation??base.ShowWithoutActivation;
    }
}
