using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ScreenshotTranslationUiTester;

public sealed class CaptureOverlay : Form
{
    private const int RepaintIntervalMs = 8;
    private enum PointerOperation { None, Create, Move, Resize }
    internal enum ResizeDirection { None, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }
    private Rectangle _virtualBounds;
    private Bitmap _frozenScreen;
    private Bitmap? _staticFreezeLayer;
    private bool _initialHoverReadyBeforeVisible;
    private int _startupDeferredHoverInvalidateCount;
    private readonly Panel _toolbar = new();
    private Size _toolbarPreferredSize;
    private readonly SolidBrush _shadeBrush;
    private readonly SolidBrush _promptBackgroundBrush = new(Color.FromArgb(210, 20, 23, 31));
    private readonly SolidBrush _sizeBackgroundBrush = new(Color.FromArgb(220, 20, 23, 31));
    private readonly Pen _selectionBorderPen = new(Color.FromArgb(45, 185, 255), 3F);
    private readonly Font _promptFont = new("Microsoft YaHei UI", 12F, FontStyle.Bold);
    private readonly Font _sizeFont = new("Segoe UI", 9F);
    private readonly Stopwatch _interactionClock = Stopwatch.StartNew();
    private Point _dragStart;
    private Point _moveOffset;
    private Rectangle _selectionAtMouseDown;
    private Rectangle _selection;
    private Rectangle _hoveredWindow;
    private Rectangle _mouseDownWindow;
    private Rectangle _visualAtMouseDown;
    private bool _dragging;
    private bool _hasSelection;
    private bool _cancelled;
    private bool _pendingCreate;
    private bool _resourcesDisposed;
    private PointerOperation _pointerOperation;
    private ResizeDirection _resizeDirection;
    private long _lastPaintRequestMs;
    private int _copyFromScreenCount;
    private int _fullBitmapCopiesDuringMouseMove = 0;
    private int _mouseMoveCount;
    private int _paintCount;
    private int _oldRectangleInvalidations;
    private int _newRectangleInvalidations;
    private long _dirtyAreaTotal;
    private int _dirtyRegionCount;
    private int _hoverInvalidationCount;
    private long _hoverDirtyAreaTotal;
    private int _fullClientInteractionInvalidations;
    private readonly List<double> _mouseMoveHandlerMs=[];
    private readonly List<double> _overlayPaintMs=[];
    private int _fullOverlayInvalidateCount;
    private int _fullFreezeBitmapRedrawCount;
    private double _maxScreenshotOverlayUiBlockMs;
    private Size _cachedLabelSelectionSize;
    private string _cachedSizeText = "";
    private long _captureStartedAt;
    private long _captureRequestedAt;
    private long _freezeCompletedAt;
    private long _firstVisibleAt;
    private long _firstPaintAt;
    private int _showCount;
    private int _hideCount;
    private readonly bool _showWithoutActivation;
    private readonly bool _reusableLifecycle;
    private bool _hasReusableCapture;
    private int _diagnosticEnumWindowsCount;
    private double _diagnosticLastPaintMs;
    private double _staticComposeMs;
    private double _firstPaintMs;
    private Rectangle _diagnosticLastInvalidateRect;
    private string _diagnosticThrottleDecision="NONE";

    public CaptureResult? Result { get; private set; }
    internal Action<CaptureResult>? ResultPrepared { get; set; }

    public CaptureOverlay() : this(SystemInformation.VirtualScreen, null, Stopwatch.GetTimestamp(), false, false, true, false)
    {
    }

    internal CaptureOverlay(long captureRequestedAt) :
        this(SystemInformation.VirtualScreen, null, captureRequestedAt, false, false, true, false)
    {
    }

    internal CaptureOverlay(Rectangle virtualBounds, Bitmap? testFrozenScreen) :
        this(virtualBounds, testFrozenScreen, Stopwatch.GetTimestamp(), false, false, true, false)
    {
    }

    internal CaptureOverlay(long captureRequestedAt, bool noActivate, bool noShade) :
        this(SystemInformation.VirtualScreen, null, captureRequestedAt, noActivate, noShade, true, false) { }

    internal CaptureOverlay(Rectangle virtualBounds, Bitmap testFrozenScreen, bool noActivate, bool noShade) :
        this(virtualBounds, testFrozenScreen, Stopwatch.GetTimestamp(), noActivate, noShade, true, false) { }

    internal CaptureOverlay(bool reusableLifecycle) :
        this(SystemInformation.VirtualScreen, null, Stopwatch.GetTimestamp(), false, false, false, reusableLifecycle) { }

    private CaptureOverlay(Rectangle virtualBounds, Bitmap? testFrozenScreen, long captureRequestedAt,
        bool noActivate, bool noShade, bool captureImmediately, bool reusableLifecycle)
    {
        _showWithoutActivation = noActivate;
        _reusableLifecycle = reusableLifecycle;
        _shadeBrush = new SolidBrush(noShade ? Color.Transparent : Color.FromArgb(115, 0, 0, 0));
        _captureRequestedAt = captureRequestedAt;
        _captureStartedAt = Stopwatch.GetTimestamp();
        _virtualBounds = virtualBounds;
        if (!captureImmediately)
        {
            _frozenScreen = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        }
        else if (testFrozenScreen is null)
        {
            _frozenScreen = CaptureVirtualScreen(_virtualBounds);
            _copyFromScreenCount = 1;
        }
        else
        {
            _frozenScreen = new Bitmap(testFrozenScreen);
        }
        var initialCompose=Stopwatch.GetTimestamp();_staticFreezeLayer=BuildStaticFreezeLayer(_frozenScreen);
        _staticComposeMs=Stopwatch.GetElapsedTime(initialCompose).TotalMilliseconds;
        _freezeCompletedAt = Stopwatch.GetTimestamp();
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = _virtualBounds;
        TopMost = true;
        ShowInTaskbar = false;
        KeyPreview = true;
        DoubleBuffered = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        UpdateStyles();
        Cursor = Cursors.Cross;
        BackColor = Color.Black;
        BuildToolbar();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) CancelCapture(); };
        CaptureFlashDiagnosticLog.Write($"Overlay constructed bounds={Bounds} topMost={TopMost} border={FormBorderStyle} taskbar={ShowInTaskbar}");
    }

    internal void PrewarmHandle()
    {
        if (IsHandleCreated) return;
        CreateControl();
        CaptureFlashDiagnosticLog.Write($"Overlay prewarmed handle={Handle} visible={Visible}");
    }

    internal void PrepareReusableCapture(long captureRequestedAt)
    {
        if (!_reusableLifecycle) throw new InvalidOperationException("Overlay is not reusable.");
        var bounds = SystemInformation.VirtualScreen;
        if (Bounds != bounds) Bounds = bounds;
        _virtualBounds = bounds;
        _captureStartedAt = Stopwatch.GetTimestamp();
        var captured=CaptureVirtualScreen(bounds);
        try{ReplaceReusableFrozenBitmap(captured,captureRequestedAt);}
        catch{captured.Dispose();throw;}
        _copyFromScreenCount++;
    }

    internal void PrepareReusableCapture(Bitmap captured,long captureRequestedAt)
    {
        if(!_reusableLifecycle)throw new InvalidOperationException("Overlay is not reusable.");
        var bounds=SystemInformation.VirtualScreen;if(Bounds!=bounds)Bounds=bounds;_virtualBounds=bounds;
        _captureStartedAt=Stopwatch.GetTimestamp();ReplaceReusableFrozenBitmap(captured,captureRequestedAt);_copyFromScreenCount++;
    }

    internal void PrepareReusableCaptureForTest(Bitmap source, long captureRequestedAt)
    {
        if (!_reusableLifecycle) throw new InvalidOperationException("Overlay is not reusable.");
        var captured=new Bitmap(source);
        try{ReplaceReusableFrozenBitmap(captured,captureRequestedAt);}
        catch{captured.Dispose();throw;}
    }
    internal void SetSelectionForRealE2ETest(Rectangle screenRectangle)
    {
        _selection=new Rectangle(screenRectangle.X-_virtualBounds.X,screenRectangle.Y-_virtualBounds.Y,
            screenRectangle.Width,screenRectangle.Height);_hasSelection=_selection.Width>0&&_selection.Height>0;PositionToolbar();Invalidate();
    }
    internal void ConfirmSelectionForRealE2ETest(PreviewMode mode)=>FinishSelection(mode);

    private void ReplaceReusableFrozenBitmap(Bitmap fresh, long captureRequestedAt)
    {
        var replaceStarted=Stopwatch.GetTimestamp();
        var composeStarted=Stopwatch.GetTimestamp();
        Bitmap? preparedStatic=BuildStaticFreezeLayer(fresh);
        var composeMs=Stopwatch.GetElapsedTime(composeStarted).TotalMilliseconds;
        try
        {
            _initialHoverReadyBeforeVisible=false;
            _startupDeferredHoverInvalidateCount=0;
            ResetReusableState();
            var hoverStarted=Stopwatch.GetTimestamp();PrepareInitialHover(Cursor.Position, FindWindowAt);var hoverMs=Stopwatch.GetElapsedTime(hoverStarted).TotalMilliseconds;

            // Ownership transfers only after every preparation step has succeeded.
            // Once committed, this method must not throw: the caller will otherwise
            // dispose fresh while the overlay still owns and references it.
            var previous=_frozenScreen;
            var previousStatic=_staticFreezeLayer;
            _frozenScreen=fresh;
            _staticFreezeLayer=preparedStatic;
            preparedStatic=null;
            _hasReusableCapture=true;
            if(CursorCaptureStageDump.Enabled)CursorCaptureStageDump.RecordFrozenBitmap(_frozenScreen);
            var disposeStarted=Stopwatch.GetTimestamp();
            try{previous.Dispose();previousStatic?.Dispose();}catch(Exception ex){AppLog.Write("capture","Previous reusable capture disposal failed",ex);}
            var disposeMs=Stopwatch.GetElapsedTime(disposeStarted).TotalMilliseconds;
            _staticComposeMs=composeMs;
            _freezeCompletedAt = Stopwatch.GetTimestamp();
            _firstVisibleAt = 0;
            _firstPaintAt = 0;
            _captureRequestedAt = captureRequestedAt;
            try{RealPathDiagnosticTrace.Pointer(new{timestamp=DateTimeOffset.Now,Event="FREEZE_READY",BitmapSize=fresh.Size,DisposePreviousBitmapMs=disposeMs,StaticComposeMs=composeMs,PrepareInitialHoverMs=hoverMs,TotalMs=Stopwatch.GetElapsedTime(replaceStarted).TotalMilliseconds,FullSizeBitmapCount=2,FullSizeCloneCount=0,Visible});}
            catch(Exception ex){AppLog.Write("capture","Reusable capture ready trace failed",ex);}
        }
        finally{preparedStatic?.Dispose();}
    }

    private Bitmap BuildStaticFreezeLayer(Bitmap source)
    {
        var layer=new Bitmap(source.Width,source.Height,PixelFormat.Format32bppPArgb);
        using var graphics=Graphics.FromImage(layer);
        graphics.CompositingMode=CompositingMode.SourceCopy;
        graphics.DrawImageUnscaled(source,Point.Empty);
        graphics.CompositingMode=CompositingMode.SourceOver;
        graphics.FillRectangle(_shadeBrush,new Rectangle(Point.Empty,source.Size));
        return layer;
    }

    internal void ReleaseReusableCapture()
    {
        if (!_reusableLifecycle || !_hasReusableCapture) return;
        var placeholder=new Bitmap(1,1,PixelFormat.Format32bppArgb);
        Bitmap? placeholderStatic=null;
        try{placeholderStatic=BuildStaticFreezeLayer(placeholder);}
        catch{placeholder.Dispose();throw;}
        var previous=_frozenScreen;
        var previousStatic=_staticFreezeLayer;
        _frozenScreen=placeholder;
        _staticFreezeLayer=placeholderStatic;
        _hasReusableCapture=false;
        try{previous.Dispose();previousStatic?.Dispose();}catch(Exception ex){AppLog.Write("capture","Reusable capture release disposal failed",ex);}
    }

    private void ResetReusableState()
    {
        Result = null;
        DialogResult = DialogResult.None;
        _cancelled = false;
        Capture = false;
        _dragging = false;
        _pointerOperation = PointerOperation.None;
        _resizeDirection = ResizeDirection.None;
        _hasSelection = false;
        _selection = Rectangle.Empty;
        _hoveredWindow = Rectangle.Empty;
        _mouseDownWindow = Rectangle.Empty;
        _visualAtMouseDown = Rectangle.Empty;
        _pendingCreate = false;
        _toolbar.Visible = false;
        Cursor = Cursors.Cross;
    }

    private void PrepareInitialHover(Point screenPoint, Func<Point, Rectangle> windowFinder)
    {
        if (Visible)
            throw new InvalidOperationException("Initial hover must be prepared before the overlay becomes visible.");

        // This is a state-only preparation step. The overlay is still hidden, so
        // do not invalidate or expose a null-hover frame before the first Show.
        _hoveredWindow = windowFinder(screenPoint);
        _initialHoverReadyBeforeVisible = true;
        CaptureFlashDiagnosticLog.Write(
            $"Initial Hover ready before Show cursor={screenPoint} hovered={_hoveredWindow} visible={Visible}");
    }

    internal void PrepareInitialHoverForTest(Point screenPoint, Func<Point, Rectangle> windowFinder) =>
        PrepareInitialHover(screenPoint, windowFinder);

    internal static Bitmap CaptureVirtualScreenForDiagnostic(Rectangle bounds) => CaptureVirtualScreen(bounds);

    private static Bitmap CaptureVirtualScreen(Rectangle bounds)
    {
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        RealExeE2ETrace.AddBitmapCopy(bounds.Size);
        return bitmap;
    }

    protected override bool ShowWithoutActivation => _showWithoutActivation;

    internal void ShowNoActivateForDiagnostic()
    {
        var before = NativeMethods.GetForegroundWindow();
        CreateControl();
        NativeMethods.ShowWindow(Handle, NativeMethods.SwShowNoActivate);
        Invalidate();
        CaptureFlashDiagnosticLog.Write(
            $"Overlay ShowWindow(SW_SHOWNOACTIVATE) foregroundBefore={before} foregroundAfter={NativeMethods.GetForegroundWindow()}");
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            if (_showWithoutActivation) parameters.ExStyle |= (int)NativeMethods.WsExNoActivate;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        CaptureFlashDiagnosticLog.Write($"Overlay.HandleCreated handle={Handle} noActivate={_showWithoutActivation}");
    }

    protected override void OnLoad(EventArgs e)
    {
        CaptureFlashDiagnosticLog.Write("Overlay.OnLoad enter");
        base.OnLoad(e);
        CaptureFlashDiagnosticLog.Write("Overlay.OnLoad exit");
    }

    protected override void OnShown(EventArgs e)
    {
        CaptureFlashDiagnosticLog.Write("Overlay.OnShown enter");
        base.OnShown(e);
        if (!_showWithoutActivation)
        {
            NativeMethods.SetForegroundWindow(Handle);
            Activate();
            Focus();
            CaptureFlashDiagnosticLog.Write(
                $"Overlay keyboard ready active={Form.ActiveForm == this} containsFocus={ContainsFocus} focused={Focused}");
        }
        CaptureFlashDiagnosticLog.Write("Overlay.OnShown exit");
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) ReleasePointerInteraction();
    }

    private void ReleasePointerInteraction()
    {
        Capture = false;
        _dragging = false;
        _pointerOperation = PointerOperation.None;
        _resizeDirection = ResizeDirection.None;
        _pendingCreate = false;
    }

    // Esc belongs to local window handling / the foreground input lease, never
    // a global RegisterHotKey registration that survives switching applications.
    internal (bool Registered, int Error) GetEscapeHotKeyStateForTest() => (false, 0);

    protected override void OnDeactivate(EventArgs e)
    {
        ReleasePointerInteraction();
        base.OnDeactivate(e);
    }

    protected override void OnActivated(EventArgs e)
    {
        CaptureFlashDiagnosticLog.Write("Overlay.OnActivated");
        base.OnActivated(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        CaptureFlashDiagnosticLog.Write("Overlay.OnGotFocus");
        base.OnGotFocus(e);
    }

    private static Button MakeButton(string text) => new()
    {
        Text = text, AutoSize = true, MinimumSize = new Size(76, 36),
        Margin = new Padding(4), FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(35, 40, 52), ForeColor = Color.White,
        Cursor = Cursors.Hand, Font = new Font("Microsoft YaHei UI", 9F)
    };

    private void BuildToolbar()
    {
        _toolbar.AutoSize = true;
        _toolbar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _toolbar.Padding = new Padding(5);
        _toolbar.BackColor = Color.FromArgb(235, 18, 21, 28);
        _toolbar.Visible = false;
        var flow = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false, Dock = DockStyle.Fill
        };
        var confirm = MakeButton("确认");
        confirm.BackColor = Color.FromArgb(32, 155, 104);
        confirm.Click += (_, _) => FinishSelection(PreviewMode.OcrAndTranslate);
        var ocr = MakeButton("识别文字");
        ocr.Click += (_, _) => FinishSelection(PreviewMode.OcrOnly);
        var ocrCompare = MakeButton("OCR 对比");
        ocrCompare.Click += (_, _) => FinishSelection(PreviewMode.OcrCompare);
        var translate = MakeButton("翻译");
        translate.Click += (_, _) => FinishSelection(PreviewMode.OcrAndTranslate);
        var cancel = MakeButton("取消");
        cancel.Click += (_, _) => Close();
        var reselect = MakeButton("重新选择");
        reselect.Click += (_, _) => ResetSelection();
        var copy = MakeButton("复制截图");
        copy.Click += (_, _) => CopySelection();
        var save = MakeButton("保存截图");
        save.Click += (_, _) => SaveSelection();
        flow.Controls.AddRange([confirm, ocr, ocrCompare, translate, cancel, reselect, copy, save]);
        flow.MouseDown += ToolbarMouseDown;
        foreach (Control child in flow.Controls) child.MouseDown += ToolbarMouseDown;
        _toolbar.Controls.Add(flow);
        _toolbar.MouseDown += ToolbarMouseDown;
        Controls.Add(_toolbar);
        _toolbar.BringToFront();
    }

    private void ToolbarMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) CancelCapture();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            CancelCapture();
            return;
        }
        if (e.Button == MouseButtons.Left && !_toolbar.Bounds.Contains(e.Location))
        {
            _dragStart = e.Location;
            var resizeDirection = _hasSelection ? HitTestResize(e.Location) : ResizeDirection.None;
            if (resizeDirection != ResizeDirection.None)
            {
                _pointerOperation = PointerOperation.Resize;
                _resizeDirection = resizeDirection;
                _selectionAtMouseDown = _selection;
                _mouseDownWindow = Rectangle.Empty;
            }
            else if (_hasSelection && _selection.Contains(e.Location))
            {
                _pointerOperation = PointerOperation.Move;
                _selectionAtMouseDown = _selection;
                _moveOffset = new Point(e.X - _selection.Left, e.Y - _selection.Top);
                _mouseDownWindow = Rectangle.Empty;
            }
            else
            {
                _pointerOperation = PointerOperation.None;
                _mouseDownWindow = _hoveredWindow;
                _visualAtMouseDown = _hasSelection ? _selection : _hoveredWindow;
                _pendingCreate = true;
            }
            if (_pointerOperation is PointerOperation.Move or PointerOperation.Resize)
            {
                var oldToolbar = _toolbar.Visible ? _toolbar.Bounds : Rectangle.Empty;
                _toolbar.Visible = false;
                RequestDynamicPaint(_selection, _selection, oldToolbar, Rectangle.Empty, true);
            }
            _dragging = true;
            Capture = true;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var handlerStarted=Stopwatch.GetTimestamp();
        var findMs=0d;var cursorMs=0d;_diagnosticEnumWindowsCount=0;_diagnosticThrottleDecision="NONE";_diagnosticLastInvalidateRect=Rectangle.Empty;
        try
        {
        _mouseMoveCount++;
        if (_pendingCreate)
        {
            if (!ExceededDragThreshold(_dragStart, e.Location))
            {
                base.OnMouseMove(e);
                return;
            }

            var oldToolbar = _toolbar.Visible ? _toolbar.Bounds : Rectangle.Empty;
            _pendingCreate = false;
            _pointerOperation = PointerOperation.Create;
            _mouseDownWindow = Rectangle.Empty;
            _hasSelection = false;
            _selection = Rectangle.Intersect(ClientRectangle, NormalizeRectangle(_dragStart, e.Location));
            _toolbar.Visible = false;
            RequestVisualTransitionPaint(_visualAtMouseDown, _selection, oldToolbar, Rectangle.Empty, true);
            base.OnMouseMove(e);
            return;
        }
        if (_dragging && _interactionClock.ElapsedMilliseconds - _lastPaintRequestMs < RepaintIntervalMs)
        {
            _diagnosticThrottleDecision="SKIP_8MS";
            base.OnMouseMove(e);
            return;
        }
        if (_dragging)
        {
            var previousSelection = _selection;
            if (_pointerOperation == PointerOperation.Move)
            {
                _selection = MoveSelectionWithinBounds(_selectionAtMouseDown,
                    new Point(e.X - _moveOffset.X, e.Y - _moveOffset.Y), ClientRectangle);
                _hasSelection = true;
            }
            else if (_pointerOperation == PointerOperation.Resize)
            {
                _selection = ResizeSelectionWithinBounds(_selectionAtMouseDown, e.Location,
                    _resizeDirection, ClientRectangle, 16);
                _hasSelection = true;
            }
            else
            {
                _selection = Rectangle.Intersect(ClientRectangle, NormalizeRectangle(_dragStart, e.Location));
            }
            RequestDynamicPaint(previousSelection, _selection, Rectangle.Empty, Rectangle.Empty);
        }
        else if (!_hasSelection)
        {
            if (_interactionClock.ElapsedMilliseconds - _lastPaintRequestMs >= 16)
            {
                var fs=Stopwatch.GetTimestamp();UpdateHoveredWindow(FindWindowAt(PointToScreen(e.Location)));findMs=Stopwatch.GetElapsedTime(fs).TotalMilliseconds;
            }
            var cs=Stopwatch.GetTimestamp();Cursor = Cursors.Cross;cursorMs=Stopwatch.GetElapsedTime(cs).TotalMilliseconds;
        }
        else
        {
            var cs=Stopwatch.GetTimestamp();Cursor = CursorForPoint(e.Location);cursorMs=Stopwatch.GetElapsedTime(cs).TotalMilliseconds;
        }
        base.OnMouseMove(e);
        }
        finally
        {
            var elapsed=Stopwatch.GetElapsedTime(handlerStarted).TotalMilliseconds;
            _mouseMoveHandlerMs.Add(elapsed);_maxScreenshotOverlayUiBlockMs=Math.Max(_maxScreenshotOverlayUiBlockMs,elapsed);
            RealPathDiagnosticTrace.Pointer(new{timestamp=DateTimeOffset.Now,MouseMoveTotalMs=elapsed,FindWindowAtMs=findMs,EnumWindowsCount=_diagnosticEnumWindowsCount,CursorUpdateMs=cursorMs,InvalidateRect=_diagnosticLastInvalidateRect,OnPaintMs=_diagnosticLastPaintMs,ThrottleDecision=_diagnosticThrottleDecision,MouseButtons=e.Button.ToString(),SelectionState=new{_hasSelection,_pendingCreate,_dragging,Operation=_pointerOperation.ToString(),Selection=_selection},ResizeState=_resizeDirection.ToString(),Location=e.Location});
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging && e.Button == MouseButtons.Left)
        {
            Capture = false;
            _dragging = false;
            if (_pendingCreate)
            {
                _pendingCreate = false;
                _pointerOperation = PointerOperation.None;
                var oldVisual = _visualAtMouseDown;
                var oldToolbar = _toolbar.Visible ? _toolbar.Bounds : Rectangle.Empty;
                if (!_mouseDownWindow.IsEmpty)
                {
                    _selection = _mouseDownWindow;
                }
                else
                {
                    var screen = Screen.FromPoint(PointToScreen(e.Location)).Bounds;
                    _selection = new Rectangle(
                        screen.Left - _virtualBounds.Left, screen.Top - _virtualBounds.Top,
                        screen.Width, screen.Height);
                    CaptureFlashDiagnosticLog.Write($"Full-screen selection chosen from FrozenBitmap rect={_selection}");
                }
                _hasSelection = _selection.Width > 0 && _selection.Height > 0;
                _hoveredWindow = Rectangle.Empty;
                if (_hasSelection) PositionToolbar();
                RequestVisualTransitionPaint(oldVisual, _selection, oldToolbar,
                    _toolbar.Visible ? _toolbar.Bounds : Rectangle.Empty, true);
                _mouseDownWindow = Rectangle.Empty;
                _visualAtMouseDown = Rectangle.Empty;
                base.OnMouseUp(e);
                return;
            }
            var operation = _pointerOperation;
            _pointerOperation = PointerOperation.None;
            if (operation is PointerOperation.Move or PointerOperation.Resize)
            {
                var previousSelection = _selection;
                _selection = operation == PointerOperation.Move
                    ? MoveSelectionWithinBounds(_selectionAtMouseDown,
                        new Point(e.X - _moveOffset.X, e.Y - _moveOffset.Y), ClientRectangle)
                    : ResizeSelectionWithinBounds(_selectionAtMouseDown, e.Location,
                        _resizeDirection, ClientRectangle, 16);
                _hasSelection = true;
                PositionToolbar();
                Cursor = CursorForPoint(e.Location);
                RequestDynamicPaint(previousSelection, _selection, Rectangle.Empty, _toolbar.Bounds, true);
                base.OnMouseUp(e);
                return;
            }
            var dragged = ExceededDragThreshold(_dragStart, e.Location);
            if (dragged)
            {
                _selection = Rectangle.Intersect(ClientRectangle, NormalizeRectangle(_dragStart, e.Location));
            }
            else if (!_mouseDownWindow.IsEmpty)
            {
                _selection = _mouseDownWindow;
            }
            else
            {
                var screen = Screen.FromPoint(PointToScreen(e.Location)).Bounds;
                _selection = new Rectangle(
                    screen.Left - _virtualBounds.Left, screen.Top - _virtualBounds.Top,
                    screen.Width, screen.Height);
                CaptureFlashDiagnosticLog.Write($"Full-screen selection chosen from FrozenBitmap rect={_selection}");
            }
            if (_selection.Width > 0 && _selection.Height > 0)
            {
                _hasSelection = true;
                PositionToolbar();
            }
            RequestDynamicPaint(Rectangle.Empty, _selection, Rectangle.Empty,
                _toolbar.Visible ? _toolbar.Bounds : Rectangle.Empty, true);
        }
        base.OnMouseUp(e);
    }

    private Rectangle FindWindowAt(Point screenPoint)
    {
        _diagnosticEnumWindowsCount=0;
        Rectangle found = Rectangle.Empty;
        NativeMethods.EnumWindows((handle, _) =>
        {
            _diagnosticEnumWindowsCount++;
            if (handle == Handle || !NativeMethods.IsWindowVisible(handle) || NativeMethods.IsIconic(handle))
                return true;
            var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle);
            if ((style & (NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate | NativeMethods.WsExTransparent)) != 0)
                return true;
            if ((style & NativeMethods.WsExLayered) != 0 &&
                NativeMethods.GetLayeredWindowAttributes(handle, out var colorKey, out var alpha, out var flags) &&
                (flags & NativeMethods.LwaAlpha) != 0 && alpha == 0)
                return true;
            NativeRect native;
            if (NativeMethods.DwmGetWindowAttribute(handle, NativeMethods.DwmwaExtendedFrameBounds,
                    out native, System.Runtime.InteropServices.Marshal.SizeOf<NativeRect>()) != 0 &&
                !NativeMethods.GetWindowRect(handle, out native))
                return true;
            var rect = Rectangle.Intersect(native.ToRectangle(), _virtualBounds);
            if (rect.Width < 30 || rect.Height < 30 || !rect.Contains(screenPoint))
                return true;
            found = new Rectangle(rect.Left - _virtualBounds.Left, rect.Top - _virtualBounds.Top,
                rect.Width, rect.Height);
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private bool UpdateHoveredWindow(Rectangle current)
    {
        var previous = _hoveredWindow;
        if (previous == current) return false;
        _hoveredWindow = current;
        RequestHoverPaint(previous, current);
        return true;
    }

    internal bool UpdateHoveredWindowForTest(Rectangle current) => UpdateHoveredWindow(current);

    protected override void OnPaint(PaintEventArgs e)
    {
        var paintStarted=Stopwatch.GetTimestamp();
        var firstPaint=_firstPaintAt==0;double frozenDrawMs=0,shadeMs=0,activeDrawMs=0,promptMs=0;var clipForTrace=Rectangle.Empty;var activeForTrace=Rectangle.Empty;
        try
        {
        if (_firstPaintAt == 0) CaptureFlashDiagnosticLog.Write("Overlay first OnPaint");
        if (_firstPaintAt == 0) _firstPaintAt = Stopwatch.GetTimestamp();
        _paintCount++;
        var clip=Rectangle.Intersect(ClientRectangle,e.ClipRectangle);clipForTrace=clip;
        if(clip.IsEmpty)return;
        if(clip==ClientRectangle)_fullFreezeBitmapRedrawCount++;
        var phase=Stopwatch.GetTimestamp();e.Graphics.DrawImage(_staticFreezeLayer??_frozenScreen,clip,clip,GraphicsUnit.Pixel);frozenDrawMs=Stopwatch.GetElapsedTime(phase).TotalMilliseconds;
        var active = _pendingCreate && !_mouseDownWindow.IsEmpty
            ? _mouseDownWindow
            : (_dragging || _hasSelection) ? _selection : _hoveredWindow;
        activeForTrace=active;if (!active.IsEmpty)
        {
            phase=Stopwatch.GetTimestamp();
            var state = e.Graphics.Save();
            e.Graphics.SetClip(Rectangle.Intersect(active,clip), CombineMode.Intersect);
            e.Graphics.DrawImage(_frozenScreen,clip,clip,GraphicsUnit.Pixel);
            e.Graphics.Restore(state);
            e.Graphics.DrawRectangle(_selectionBorderPen, active);
            DrawSizeLabel(e.Graphics, active);
            activeDrawMs=Stopwatch.GetElapsedTime(phase).TotalMilliseconds;
        }
        // Until the system drag threshold is exceeded, mouse-down is only a
        // pending click. Preserve the complete prior hover frame, including its
        // prompt, so an intermediate all-shaded frame is never exposed.
        if ((!_dragging || _pendingCreate) && !_hasSelection)
        {phase=Stopwatch.GetTimestamp();DrawPrompt(e.Graphics);promptMs=Stopwatch.GetElapsedTime(phase).TotalMilliseconds;}
        }
        finally
        {
            var elapsed=Stopwatch.GetElapsedTime(paintStarted).TotalMilliseconds;
            _diagnosticLastPaintMs=elapsed;
            _overlayPaintMs.Add(elapsed);_maxScreenshotOverlayUiBlockMs=Math.Max(_maxScreenshotOverlayUiBlockMs,elapsed);
            if(firstPaint)
            {
                _firstPaintMs=elapsed;
                RealPathDiagnosticTrace.Pointer(new{timestamp=DateTimeOffset.Now,Event="FIRST_OVERLAY_PAINT",TotalMs=elapsed,FrozenBitmapDrawMs=frozenDrawMs,ShadeFillMs=shadeMs,DynamicDrawMs=activeDrawMs+promptMs,ActiveRegionDrawMs=activeDrawMs,PromptDrawMs=promptMs,Clip=clipForTrace,Active=activeForTrace,FullInvalidate=clipForTrace==ClientRectangle,FrozenBitmapSize=_frozenScreen.Size,StaticLayerReady=_staticFreezeLayer is not null,DoubleBuffered});
            }
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // OnPaint restores every invalidated pixel from the immutable frozen screenshot.
    }

    protected override void SetVisibleCore(bool value)
    {
        var changed = value != Visible;
        base.SetVisibleCore(value);
        if (!changed) return;
        if (value)
        {
            CaptureFlashDiagnosticLog.Write("Overlay Visible=true");
            _showCount++;
            if (_firstVisibleAt == 0) _firstVisibleAt = Stopwatch.GetTimestamp();
            RealPathDiagnosticTrace.Pointer(new{timestamp=DateTimeOffset.Now,Event="OVERLAY_VISIBLE",Bounds,ClientSize,DoubleBuffered,Cursor=Cursor?.ToString(),SystemCursor=true});
        }
        else
        {
            CaptureFlashDiagnosticLog.Write("Overlay Visible=false");
            _hideCount++;
        }
    }

    private void DrawPrompt(Graphics graphics)
    {
        const string prompt = "移动鼠标识别窗口并单击；拖动选择区域；空白处单击选择当前屏幕；Esc 取消";
        var box=PromptBounds(graphics,prompt);
        graphics.FillRoundedRectangle(_promptBackgroundBrush, box, 8);
        graphics.DrawString(prompt, _promptFont, Brushes.White, box.X + 16, box.Y + 9);
    }

    private RectangleF PromptBounds(Graphics? graphics=null,string? text=null)
    {
        const string fallback="移动鼠标识别窗口并单击；拖动选择区域；空白处单击选择当前屏幕；Esc 取消";
        var value=text??fallback;SizeF size;
        if(graphics is not null)size=graphics.MeasureString(value,_promptFont);
        else using(var measure=CreateGraphics())size=measure.MeasureString(value,_promptFont);
        return new((ClientSize.Width-size.Width)/2-16,ClientSize.Height-70,size.Width+32,size.Height+18);
    }

    private void DrawSizeLabel(Graphics graphics, Rectangle rect)
    {
        var box = GetSizeLabelBounds(rect);
        graphics.FillRectangle(_sizeBackgroundBrush, box);
        graphics.DrawString(_cachedSizeText, _sizeFont, Brushes.White, box.X + 6, box.Y + 2);
    }

    private void ResetSelection()
    {
        CaptureFlashDiagnosticLog.Write("ResetSelection; no Hide/Show/CopyFromScreen");
        _hasSelection = false;
        _selection = Rectangle.Empty;
        _hoveredWindow = Rectangle.Empty;
        _pendingCreate = false;
        _toolbar.Visible = false;
        Invalidate();
    }

    private void PositionToolbar()
    {
        if (_toolbarPreferredSize.IsEmpty)
        {
            _toolbar.PerformLayout();
            _toolbarPreferredSize = _toolbar.PreferredSize;
        }
        var preferred = _toolbarPreferredSize;
        var x = Math.Clamp(_selection.Right - preferred.Width, 8,
            Math.Max(8, ClientSize.Width - preferred.Width - 8));
        var below = _selection.Bottom + 10;
        var y = below + preferred.Height <= ClientSize.Height
            ? below : Math.Max(8, _selection.Top - preferred.Height - 10);
        _toolbar.Location = new Point(x, y);
        _toolbar.Visible = true;
        _toolbar.BringToFront();
    }

    private Bitmap? GetSelectedBitmap()
    {
        if (!_hasSelection || _selection.Width <= 0 || _selection.Height <= 0)
            return null;
        var crop=Stopwatch.StartNew();
        var result = new Bitmap(_selection.Width, _selection.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(result);
        graphics.DrawImage(_frozenScreen, new Rectangle(Point.Empty, result.Size), _selection, GraphicsUnit.Pixel);
        if(CursorCaptureStageDump.Enabled)
        {
            var screenRect=new Rectangle(_selection.X+_virtualBounds.X,_selection.Y+_virtualBounds.Y,_selection.Width,_selection.Height);
            CursorCaptureStageDump.RecordSelectionCrop(result,screenRect,_selection);
        }
        crop.Stop();PostConfirmPerformanceTrace.Write("FinalCrop",new{Resolution=$"{result.Width}x{result.Height}",Bytes=(long)result.Width*result.Height*4,ThreadId=Environment.CurrentManagedThreadId,DurationMs=crop.Elapsed.TotalMilliseconds});
        RealExeE2ETrace.AddBitmapCopy(result.Size);
        return result;
    }

    private void FinishSelection(PreviewMode mode)
    {
        var confirmAudit=Stopwatch.StartNew();
        PostConfirmPerformanceTrace.Write("SelectionConfirm",new{ThreadId=Environment.CurrentManagedThreadId,Selection=_selection});
        RealExeE2ETrace.Mark("T_CONFIRM SelectionConfirmPressed");
        CaptureFlashDiagnosticLog.Write($"FinishSelection crop begin mode={mode} rect={_selection}");
        RealExeE2ETrace.Mark("T_CAPTURE_FINALIZE_BEGIN");
        var selected = GetSelectedBitmap();
        if (selected is null) return;
        try
        {
            Result = new CaptureResult(selected, mode);
            RealExeE2ETrace.Mark("T_CAPTURE_FINALIZE_END");
            RealExeE2ETrace.Mark("T4 SelectionCompleted");
            CaptureFlashDiagnosticLog.Write($"FinishSelection crop complete size={selected.Width}x{selected.Height}; Overlay end begins");
            RealExeE2ETrace.Mark("T_OVERLAY_DISPOSE_BEGIN");
            DialogResult = DialogResult.OK;
            if (_reusableLifecycle) Hide(); else Close();
            confirmAudit.Stop();PostConfirmPerformanceTrace.Write("SelectionConfirmHandler",new{SelectionConfirmHandlerMs=confirmAudit.Elapsed.TotalMilliseconds});
            RealExeE2ETrace.Mark("T_OVERLAY_DISPOSE_END");
        }
        catch (Exception ex)
        {
            selected.Dispose();
            AppDialog.Show(this, "截图失败", AppDialog.UserFacingError(ex), AppDialogKind.Error, ex.ToString());
        }
    }

    private void CopySelection()
    {
        using var selected = GetSelectedBitmap();
        if (selected is null) return;
        try
        {
            ClipboardHelper.SetImage(selected);
            ToastNotifier.Show("已复制到剪贴板");
        }
        catch (Exception ex)
        {
            AppDialog.Show(this, "复制失败", AppDialog.UserFacingError(ex), AppDialogKind.Error, ex.ToString());
        }
    }

    private void SaveSelection()
    {
        using var selected = GetSelectedBitmap();
        if (selected is null) return;
        using var dialog = new SaveFileDialog
        {
            Title = "保存截图", Filter = "PNG 图片|*.png|JPEG 图片|*.jpg",
            DefaultExt = "png", AddExtension = true,
            FileName = $"截图_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            selected.Save(dialog.FileName,
                Path.GetExtension(dialog.FileName).Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                    ? ImageFormat.Jpeg : ImageFormat.Png);
            ToastNotifier.Show("保存成功");
        }
        catch (Exception ex)
        {
            AppDialog.Show(this, "保存失败", AppDialog.UserFacingError(ex), AppDialogKind.Error, ex.ToString());
        }
    }

    private static Rectangle NormalizeRectangle(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    internal static bool ExceededDragThreshold(Point start, Point current)
    {
        var drag = SystemInformation.DragSize;
        return Math.Abs(current.X - start.X) >= Math.Max(1, drag.Width / 2) ||
               Math.Abs(current.Y - start.Y) >= Math.Max(1, drag.Height / 2);
    }

    internal static Rectangle MoveSelectionWithinBounds(Rectangle selection, Point requestedLocation, Rectangle bounds)
    {
        var maxX = Math.Max(bounds.Left, bounds.Right - selection.Width);
        var maxY = Math.Max(bounds.Top, bounds.Bottom - selection.Height);
        return new Rectangle(
            Math.Clamp(requestedLocation.X, bounds.Left, maxX),
            Math.Clamp(requestedLocation.Y, bounds.Top, maxY),
            selection.Width, selection.Height);
    }

    internal static Rectangle ResizeSelectionWithinBounds(Rectangle original, Point pointer,
        ResizeDirection direction, Rectangle bounds, int minimumSize)
    {
        var left = original.Left; var right = original.Right;
        var top = original.Top; var bottom = original.Bottom;
        if (direction is ResizeDirection.Left or ResizeDirection.TopLeft or ResizeDirection.BottomLeft)
            left = Math.Clamp(pointer.X, bounds.Left, right - minimumSize);
        if (direction is ResizeDirection.Right or ResizeDirection.TopRight or ResizeDirection.BottomRight)
            right = Math.Clamp(pointer.X, left + minimumSize, bounds.Right);
        if (direction is ResizeDirection.Top or ResizeDirection.TopLeft or ResizeDirection.TopRight)
            top = Math.Clamp(pointer.Y, bounds.Top, bottom - minimumSize);
        if (direction is ResizeDirection.Bottom or ResizeDirection.BottomLeft or ResizeDirection.BottomRight)
            bottom = Math.Clamp(pointer.Y, top + minimumSize, bounds.Bottom);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private int ResizeHitSize => Math.Clamp((int)Math.Round(8d * DeviceDpi / 96d), 6, 20);

    private ResizeDirection HitTestResize(Point point)
    {
        if (!_hasSelection) return ResizeDirection.None;
        var hit = ResizeHitSize;
        var nearLeft = Math.Abs(point.X - _selection.Left) <= hit;
        var nearRight = Math.Abs(point.X - _selection.Right) <= hit;
        var nearTop = Math.Abs(point.Y - _selection.Top) <= hit;
        var nearBottom = Math.Abs(point.Y - _selection.Bottom) <= hit;
        var inHorizontalRange = point.X >= _selection.Left - hit && point.X <= _selection.Right + hit;
        var inVerticalRange = point.Y >= _selection.Top - hit && point.Y <= _selection.Bottom + hit;
        if (nearLeft && nearTop) return ResizeDirection.TopLeft;
        if (nearRight && nearTop) return ResizeDirection.TopRight;
        if (nearLeft && nearBottom) return ResizeDirection.BottomLeft;
        if (nearRight && nearBottom) return ResizeDirection.BottomRight;
        if (nearLeft && inVerticalRange) return ResizeDirection.Left;
        if (nearRight && inVerticalRange) return ResizeDirection.Right;
        if (nearTop && inHorizontalRange) return ResizeDirection.Top;
        if (nearBottom && inHorizontalRange) return ResizeDirection.Bottom;
        return ResizeDirection.None;
    }

    private Cursor CursorForPoint(Point point) => HitTestResize(point) switch
    {
        ResizeDirection.Left or ResizeDirection.Right => Cursors.SizeWE,
        ResizeDirection.Top or ResizeDirection.Bottom => Cursors.SizeNS,
        ResizeDirection.TopLeft or ResizeDirection.BottomRight => Cursors.SizeNWSE,
        ResizeDirection.TopRight or ResizeDirection.BottomLeft => Cursors.SizeNESW,
        _ => _selection.Contains(point) ? Cursors.SizeAll : Cursors.Cross
    };

    private Rectangle GetSizeLabelBounds(Rectangle rect)
    {
        if (_cachedLabelSelectionSize != rect.Size)
        {
            _cachedLabelSelectionSize = rect.Size;
            _cachedSizeText = $"{rect.Width} × {rect.Height}";
        }
        var size = TextRenderer.MeasureText(_cachedSizeText, _sizeFont, Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        return new Rectangle(Math.Max(4, rect.Left), Math.Max(4, rect.Top - size.Height - 8),
            size.Width + 12, size.Height + 4);
    }

    private Rectangle DynamicBounds(Rectangle selection, Rectangle toolbar)
    {
        var bounds = Rectangle.Empty;
        if (!selection.IsEmpty)
        {
            var border = selection; border.Inflate(5, 5);
            bounds = Rectangle.Union(border, GetSizeLabelBounds(selection));
        }
        if (!toolbar.IsEmpty) bounds = bounds.IsEmpty ? toolbar : Rectangle.Union(bounds, toolbar);
        bounds.Inflate(4, 4);
        return Rectangle.Intersect(ClientRectangle, bounds);
    }

    private void RequestDynamicPaint(Rectangle oldSelection, Rectangle newSelection,
        Rectangle oldToolbar, Rectangle newToolbar, bool immediate = false)
    {
        var oldBounds = DynamicBounds(oldSelection, oldToolbar);
        var newBounds = DynamicBounds(newSelection, newToolbar);
        var dirty = Rectangle.Union(oldBounds, newBounds);
        if (dirty.IsEmpty) return;
        var now = _interactionClock.ElapsedMilliseconds;
        if (!immediate && now - _lastPaintRequestMs < RepaintIntervalMs) return;
        _lastPaintRequestMs = now;
        _oldRectangleInvalidations++;
        _newRectangleInvalidations++;
        _dirtyAreaTotal += (long)dirty.Width * dirty.Height;
        _dirtyRegionCount++;
        _diagnosticLastInvalidateRect=dirty;
        Invalidate(dirty);
    }

    private static Rectangle InflateForPainting(Rectangle value)
    {
        if (value.IsEmpty) return value;
        value.Inflate(8, 28);
        return value;
    }

    internal static Rectangle[] GetHoverDirtyRectangles(Rectangle previous, Rectangle current,
        Rectangle clientRectangle)
    {
        var rectangles = new List<Rectangle>(2);
        Add(previous);
        Add(current);
        return rectangles.Distinct().ToArray();

        void Add(Rectangle value)
        {
            if (value.IsEmpty) return;
            var inflated = Rectangle.Intersect(clientRectangle, InflateForPainting(value));
            if (!inflated.IsEmpty) rectangles.Add(inflated);
        }
    }

    private void RequestHoverPaint(Rectangle previous, Rectangle current)
    {
        var rectangles = GetHoverDirtyRectangles(previous, current, ClientRectangle);
        if (rectangles.Length == 0) return;
        _hoverInvalidationCount++;
        _hoverDirtyAreaTotal += rectangles.Sum(x => (long)x.Width * x.Height);
        _diagnosticLastInvalidateRect=rectangles.Aggregate(Rectangle.Empty,(a,b)=>a.IsEmpty?b:Rectangle.Union(a,b));
        InvalidateSeparateRectangles(rectangles);
    }

    private void RequestVisualTransitionPaint(Rectangle oldVisual, Rectangle newVisual,
        Rectangle oldToolbar, Rectangle newToolbar, bool immediate = false)
    {
        var now = _interactionClock.ElapsedMilliseconds;
        if (!immediate && now - _lastPaintRequestMs < RepaintIntervalMs) return;
        _diagnosticThrottleDecision=immediate?"IMMEDIATE":"PAINT_8MS";
        _lastPaintRequestMs = now;
        var rectangles = new List<Rectangle>(4);
        rectangles.AddRange(GetHoverDirtyRectangles(oldVisual, newVisual, ClientRectangle));
        AddToolbar(oldToolbar);
        AddToolbar(newToolbar);
        InvalidateSeparateRectangles(rectangles.Distinct().ToArray());

        void AddToolbar(Rectangle toolbar)
        {
            if (toolbar.IsEmpty) return;
            toolbar.Inflate(4, 4);
            toolbar = Rectangle.Intersect(ClientRectangle, toolbar);
            if (!toolbar.IsEmpty) rectangles.Add(toolbar);
        }
    }

    private void InvalidateSeparateRectangles(IReadOnlyCollection<Rectangle> rectangles)
    {
        if (rectangles.Count == 0) return;
        using var dirty = new Region();
        dirty.MakeEmpty();
        foreach (var rectangle in rectangles) dirty.Union(rectangle);
        Invalidate(dirty);
    }

    private void RequestInteractionPaint(Rectangle area, bool immediate = false)
    {
        var now = _interactionClock.ElapsedMilliseconds;
        if (!immediate && now - _lastPaintRequestMs < RepaintIntervalMs) return;
        _lastPaintRequestMs = now;
        if (area.IsEmpty)
        {
            _fullClientInteractionInvalidations++;_fullOverlayInvalidateCount++;
            Invalidate();
        }
        else
        {
            if (area == ClientRectangle){_fullClientInteractionInvalidations++;_fullOverlayInvalidateCount++;}
            Invalidate(Rectangle.Intersect(ClientRectangle, area));
        }
    }

    internal void CancelFromBinding()=>CancelCapture();
    private void CancelCapture()
    {
        if (_cancelled || IsDisposed || Disposing) return;
        _cancelled = true;
        Capture = false;
        _dragging = false;
        _pointerOperation = PointerOperation.None;
        _hasSelection = false;
        _selection = Rectangle.Empty;
        _hoveredWindow = Rectangle.Empty;
        _pendingCreate = false;
        _toolbar.Visible = false;
        Result = null;
        CaptureFlashDiagnosticLog.Write("CancelCapture; Overlay end begins");
        DialogResult = DialogResult.Cancel;
        if (_reusableLifecycle) Hide(); else Close();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & Keys.KeyCode) == Keys.Escape)
        {
            CaptureFlashDiagnosticLog.Write("ProcessCmdKey ESC received");
            CancelCapture();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if ((keyData & Keys.KeyCode) == Keys.Escape)
        {
            CaptureFlashDiagnosticLog.Write("ProcessDialogKey ESC received");
            CancelCapture();
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }

    protected override void WndProc(ref Message m)
    {
        const int wmKeyDown = 0x0100;
        const int wmSysKeyDown = 0x0104;
        if ((m.Msg == wmKeyDown || m.Msg == wmSysKeyDown) && (Keys)(int)m.WParam == Keys.Escape)
        {
            CaptureFlashDiagnosticLog.Write("WndProc ESC received");
            CancelCapture();
            return;
        }
        base.WndProc(ref m);
    }

    internal (int CopyFromScreenCount, int FullBitmapCopiesDuringMouseMove, int MouseMoveCount,
        int PaintCount, double FreezeMs, bool FrozenBitmapDisposed, int OldInvalidations,
        int NewInvalidations, double AverageDirtyArea) GetPerformanceSnapshot() =>
        (_copyFromScreenCount, _fullBitmapCopiesDuringMouseMove, _mouseMoveCount, _paintCount,
         Stopwatch.GetElapsedTime(_captureStartedAt, _freezeCompletedAt).TotalMilliseconds, _resourcesDisposed,
         _oldRectangleInvalidations, _newRectangleInvalidations,
         _dirtyRegionCount == 0 ? 0 : _dirtyAreaTotal / (double)_dirtyRegionCount);

    internal (int HoverInvalidations, long HoverDirtyArea, int FullClientInteractionInvalidations,
        bool PendingCreate) GetHoverSelectionSnapshot() =>
        (_hoverInvalidationCount, _hoverDirtyAreaTotal, _fullClientInteractionInvalidations, _pendingCreate);

    internal (double TriggerToFreezeMs, double FreezeToVisibleMs, double TriggerToFirstPaintMs,
        int ShowCount, int HideCount, int OpacityChanges, int BoundsChanges) GetStartupSnapshot()
    {
        static double Elapsed(long start, long end) => start == 0 || end == 0
            ? 0 : Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;
        return (Elapsed(_captureRequestedAt, _freezeCompletedAt),
            Elapsed(_freezeCompletedAt, _firstVisibleAt), Elapsed(_captureRequestedAt, _firstPaintAt),
            _showCount, _hideCount, 0, 1);
    }

    internal (int OverlayMouseMoveCount,double P50Ms,double P95Ms,double MaxMs,double OverlayPaintP95Ms,
        int FullOverlayInvalidateCount,int FullFreezeBitmapRedrawCount,int BitmapCloneDuringMouseMoveCount,
        int DXGIFrameAcquireAfterFreezeCount,double MaxScreenshotOverlayUiBlockMs) GetPointerPerformanceSnapshot()
    {
        static double Percentile(List<double> values,double percentile)
        {if(values.Count==0)return 0;var sorted=values.OrderBy(x=>x).ToArray();return sorted[(int)Math.Clamp(Math.Ceiling(sorted.Length*percentile)-1,0,sorted.Length-1)];}
        return (_mouseMoveCount,Percentile(_mouseMoveHandlerMs,.50),Percentile(_mouseMoveHandlerMs,.95),
            _mouseMoveHandlerMs.DefaultIfEmpty(0).Max(),Percentile(_overlayPaintMs,.95),_fullOverlayInvalidateCount,
            _fullFreezeBitmapRedrawCount,0,0,
            _maxScreenshotOverlayUiBlockMs);
    }
    internal (double StaticComposeMs,double FirstPaintMs,double MaxOverlayUiBlockMs) GetFirstPaintSnapshotForSmoke()=>
        (_staticComposeMs,_firstPaintMs,_maxScreenshotOverlayUiBlockMs);
    internal (bool InitialHoverReadyBeforeVisible,int StartupDeferredHoverInvalidateCount,Rectangle InitialHover,
        double StaticComposeMs,double FirstPaintMs) GetInitialFrameSnapshotForSmoke()=>
        (_initialHoverReadyBeforeVisible,_startupDeferredHoverInvalidateCount,_hoveredWindow,_staticComposeMs,_firstPaintMs);
    internal RectangleF GetPromptBoundsForSmoke()=>PromptBounds();

    internal void ResetPointerPerformanceCountersForSmoke()
    {
        _mouseMoveCount=0;_mouseMoveHandlerMs.Clear();_overlayPaintMs.Clear();_fullOverlayInvalidateCount=0;
        _fullFreezeBitmapRedrawCount=0;_maxScreenshotOverlayUiBlockMs=0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_resourcesDisposed)
        {
            ReleasePointerInteraction();
            _resourcesDisposed = true;
            _frozenScreen.Dispose();
            _staticFreezeLayer?.Dispose();
            _shadeBrush.Dispose();
            _promptBackgroundBrush.Dispose();
            _sizeBackgroundBrush.Dispose();
            _selectionBorderPen.Dispose();
            _promptFont.Dispose();
            _sizeFont.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF rectangle, float radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
