using System.Drawing.Drawing2D;

namespace ScreenshotTranslationUiTester;

internal enum ScreenshotPaneView { OriginalImage, TranslatedImage, SourceText, TranslatedText }
internal enum ScreenshotPaneFitMode { Window, Width, Manual }

// FontSize is in 96-DPI logical pixels; Zoom is relative to fit-to-window.
// Pan is an additional image translation when native scrolling reaches an edge.
internal sealed record ScreenshotPaneViewState(
    ScreenshotPaneView View = ScreenshotPaneView.OriginalImage,
    int FontSize = 16,
    ScreenshotPaneFitMode FitMode = ScreenshotPaneFitMode.Window,
    float Zoom = 1,
    int ScrollX = 0,
    int ScrollY = 0,
    int PanX = 0,
    int PanY = 0);

// Both panes borrow the same bitmaps. Their owning workspace disposes images only
// after clearing the references in both panes.
internal sealed partial class ScreenshotRebuildPane : Panel
{
    private readonly Font localFont;
    private readonly Panel header = new(), tools = new(), viewport = new();
    private readonly ScreenshotRebuildSingleLineLabel side = new() { TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label caption = new() { AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label dimensions = new() { AutoEllipsis = true, TextAlign = ContentAlignment.MiddleRight };
    private readonly ScreenshotRebuildSingleLineLabel zoomLabel = new() { TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand, AccessibleName = "缩放比例，点击恢复适应窗口" };
    private readonly Label fontLabel = new() { TextAlign = ContentAlignment.MiddleCenter };
    private readonly ScreenshotRebuildSegments segments = new();
    private readonly ScreenshotRebuildCanvas canvas = new() { Dock = DockStyle.Fill };
    private readonly ScreenshotRebuildTextBox text = new()
    {
        Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, ReadOnly = true,
        DetectUrls = false, WordWrap = true, ScrollBars = RichTextBoxScrollBars.Vertical,
        HideSelection = false, ShortcutsEnabled = true, TabStop = true,
        AccessibleName = "截图文字内容"
    };
    private readonly Label empty = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
    private readonly List<Control> imageTools = [], textTools = [];
    private readonly ScreenshotRebuildButton maximize, copy, export, edit, fontMinus, fontPlus;
    private readonly ToolTip hints = new() { InitialDelay = 500, ReshowDelay = 150 };
    private readonly LocalizedContextMenuStrip imageMenu = new();
    private readonly LocalizedContextMenuStrip textMenu = new();
    private readonly ToolStripMenuItem copyImage, exportImage;
    private bool copyAllText;
    private Bitmap? original, translated;
    private string sourceText = "", translationText = "", paneTitle = "左侧", sourceLanguage = "", targetLanguage = "";
    private ScreenshotPaneView view;
    private bool maximized, loading, hasContent, applyingState, arranging;
    private (int Width, int Dpi, ScreenshotPaneView View, bool Maximized, Font Font, ApplicationLanguage Language)? horizontalLayout;
    private int fontSize = 16;
    private Font? textFont;

    internal event EventHandler? ViewChanged;
    internal event EventHandler? Activated;
    internal event EventHandler? MaximizeRequested;
    internal event EventHandler? CopyRequested;
    internal event EventHandler? ExportRequested;
    internal event EventHandler? EditRequested;
    internal event EventHandler? StateChanged;

    internal ScreenshotRebuildPane()
    {
        DoubleBuffered = true; Margin = Padding.Empty;
        AccessibleName = paneTitle + "独立查看栏";
        localFont = ScreenshotRebuildStyle.Font(); Font = localFont;
        FontManager.MarkPreviewTextRoot(this);
        header.Margin = tools.Margin = viewport.Margin = Padding.Empty;
        side.Text = paneTitle; header.Controls.AddRange([side, segments]);
        segments.SelectionChanged += (_, _) => View = segments.Selected;
        maximize = MakeButton("最大化", "expand", 66, () => MaximizeRequested?.Invoke(this, EventArgs.Empty));
        maximize.AccessibleName = "最大化当前栏";
        header.Controls.Add(maximize);
        imageTools.Add(MakeButton("适应窗口", "", 78, FitWindow));
        imageTools.Add(MakeButton("适应宽度", "", 68, FitWidth));
        imageTools.Add(MakeButton("", "minus", 26, ZoomOut));
        imageTools.Add(zoomLabel);
        imageTools.Add(MakeButton("", "plus", 26, ZoomIn));
        zoomLabel.Click += (_, _) => FitWindow();
        hints.SetToolTip(zoomLabel, UiLanguage.Text("相对适应窗口的比例，点击恢复 100%"));
        hints.SetToolTip(imageTools[2], UiLanguage.Text("缩小图片（滚轮向下）"));
        hints.SetToolTip(imageTools[4], UiLanguage.Text("放大图片（滚轮向上）"));
        hints.SetToolTip(canvas, UiLanguage.Text("滚轮缩放；按住鼠标左键拖动图片；适应窗口可恢复居中"));
        imageTools[2].AccessibleName = "缩小图片"; imageTools[4].AccessibleName = "放大图片";
        copy = MakeButton("复制", "copy", 54, () => CopyRequested?.Invoke(this, EventArgs.Empty));
        export = MakeButton("导出", "export", 54, () => ExportRequested?.Invoke(this, EventArgs.Empty));
        edit = MakeButton("详细编辑", "edit", 76, () => EditRequested?.Invoke(this, EventArgs.Empty));
        fontMinus = MakeButton("A−", "", 28, () => FontSize -= 2);
        fontPlus = MakeButton("A+", "", 28, () => FontSize += 2);
        fontMinus.AccessibleName = "减小文字字号"; fontPlus.AccessibleName = "增大文字字号";
        hints.SetToolTip(edit, UiLanguage.Text("打开现有详细编辑窗口"));
        textTools.AddRange([copy, export, edit, fontMinus, fontLabel, fontPlus]);
        foreach (var control in imageTools.Concat(textTools)) tools.Controls.Add(control);
        tools.Controls.Add(dimensions);
        viewport.Controls.AddRange([canvas, text, empty]);
        Controls.AddRange([header, tools, viewport, caption]);
        // These labels also change during canvas layout. Format them at their
        // source so a resize cannot write Chinese over a language refresh.
        UiLanguage.Preserve(caption);UiLanguage.Preserve(dimensions);
        UiLanguage.LanguageChanged+=InterfaceLanguageChanged;
        Disposed+=(_,_)=>UiLanguage.LanguageChanged-=InterfaceLanguageChanged;
        copyImage = new("复制图片", null, (_, _) => CopyRequested?.Invoke(this, EventArgs.Empty));
        exportImage = new("导出图片", null, (_, _) => ExportRequested?.Invoke(this, EventArgs.Empty));
        imageMenu.Items.AddRange([copyImage, exportImage]); canvas.ContextMenuStrip = imageMenu;
        var copySelection = new ToolStripMenuItem("复制所选", null, (_, _) => RequestTextCopy(all: false)) { ShortcutKeyDisplayString = "Ctrl+C" };
        var copyAll = new ToolStripMenuItem("复制全部", null, (_, _) => RequestTextCopy(all: true));
        var selectAll = new ToolStripMenuItem("全选", null, (_, _) => { text.Focus(); text.SelectAll(); }) { ShortcutKeyDisplayString = "Ctrl+A" };
        textMenu.Items.AddRange([copySelection, copyAll, new ToolStripSeparator(), selectAll]);
        textMenu.Opening += (_, _) =>
        {
            copySelection.Enabled = text.SelectionLength > 0;
            copyAll.Enabled = selectAll.Enabled = text.TextLength > 0;
        };
        text.ContextMenuStrip = textMenu;
        canvas.ViewStateChanged += (_, _) => { UpdateLabels(); NotifyStateChanged(); };
        canvas.Activated += (_, _) => Activated?.Invoke(this, EventArgs.Empty);
        foreach (Control control in header.Controls.Cast<Control>().Concat(tools.Controls.Cast<Control>()).Append(text))
        {
            control.MouseDown += (_, _) => Activated?.Invoke(this, EventArgs.Empty);
            control.Enter += (_, _) => Activated?.Invoke(this, EventArgs.Empty);
        }
        ApplyTextFont(); UpdateContent(); ApplyTheme();
    }

    internal ScreenshotPaneView View
    {
        get => view;
        set
        {
            if (!Enum.IsDefined(value) || view == value) return;
            view = value; UpdateContent();
            ViewChanged?.Invoke(this, EventArgs.Empty); NotifyStateChanged();
        }
    }
    internal string PaneTitle
    {
        get => paneTitle;
        set { var next = value ?? ""; if (paneTitle == next) return; paneTitle = next; side.Text = paneTitle; AccessibleName = paneTitle + "独立查看栏"; PerformLayout(); }
    }
    internal bool Maximized
    {
        get => maximized;
        set
        {
            if (maximized == value) return;
            maximized = value;
            maximize.IconKey = value ? "back" : "expand";
            maximize.AccessibleName = value ? "还原双栏" : "最大化当前栏";
            hints.SetToolTip(maximize, maximize.AccessibleName); PerformLayout();
        }
    }
    internal int FontSize
    {
        get => fontSize;
        set
        {
            int next = Math.Clamp(value, 12, 28); if (next == fontSize) return;
            fontSize = next; ApplyTextFont(); UpdateLabels(); NotifyStateChanged();
        }
    }
    internal bool HasContent => hasContent;
    internal bool IsImageView => view is ScreenshotPaneView.OriginalImage or ScreenshotPaneView.TranslatedImage;
    internal string CurrentText => view == ScreenshotPaneView.SourceText ? sourceText : view == ScreenshotPaneView.TranslatedText ? translationText : "";
    internal string SelectedTextOrAll => copyAllText ? CurrentText : NativeStateRequested is not null ? NativeSelectedText.Length > 0 ? NativeSelectedText : CurrentText : text.SelectionLength > 0 ? text.SelectedText : CurrentText;
    private void RequestTextCopy(bool all)
    {
        if (IsImageView || (all ? text.TextLength == 0 : text.SelectionLength == 0)) return;
        copyAllText = all;
        try { CopyRequested?.Invoke(this, EventArgs.Empty); }
        finally { copyAllText = false; }
    }
    internal Bitmap? CurrentBitmap => view == ScreenshotPaneView.OriginalImage ? original : view == ScreenshotPaneView.TranslatedImage ? translated : null;
    internal ScreenshotPaneFitMode FitMode => canvas.FitMode;
    internal float Zoom => canvas.Zoom;
    internal Rectangle ContentViewportBounds => viewport.Bounds;
    internal string SourceLanguage { get => sourceLanguage; set { sourceLanguage = value ?? ""; UpdateLabels(); } }
    internal string TargetLanguage { get => targetLanguage; set { targetLanguage = value ?? ""; UpdateLabels(); } }
    internal static string ViewTitle(ScreenshotPaneView value) => value switch
    {
        ScreenshotPaneView.OriginalImage => "原图", ScreenshotPaneView.TranslatedImage => "译图",
        ScreenshotPaneView.SourceText => "原文", _ => "译文"
    };
    internal void SetMetadata(string? sourceLanguage, string? targetLanguage)
    { this.sourceLanguage = sourceLanguage ?? ""; this.targetLanguage = targetLanguage ?? ""; UpdateLabels(); }
    internal void SetContent(Bitmap? original, Bitmap? translated, string source, string translation, bool resetView = true)
    {
        this.original = original; this.translated = translated;
        sourceText = source ?? ""; translationText = translation ?? "";
        hasContent = true; loading = false;
        if (resetView) canvas.ResetView(); UpdateContent();
    }
    internal void ClearContent()
    {
        original = translated = null; sourceText = translationText = "";
        hasContent = false; loading = false; canvas.ResetView(); UpdateContent();
    }
    internal void SetLoading(bool value) { loading = value; UpdateContent(); }
    internal void FitWindow() { if (NativeImageCommand is { } command) command("window"); else canvas.SetFit(ScreenshotPaneFitMode.Window); }
    internal void FitWidth() { if (NativeImageCommand is { } command) command("width"); else canvas.SetFit(ScreenshotPaneFitMode.Width); }
    internal void ActualSize() { if (NativeImageCommand is { } command) command("actual"); else canvas.SetZoom(1); }
    internal void ZoomIn() { if (NativeImageCommand is { } command) command("in"); else canvas.ZoomBy(1.2f); }
    internal void ZoomOut() { if (NativeImageCommand is { } command) command("out"); else canvas.ZoomBy(1 / 1.2f); }
    internal ScreenshotPaneViewState CaptureViewState() => NativeStateRequested?.Invoke() ?? new(view, fontSize, canvas.FitMode, canvas.Zoom, canvas.ScrollOffset.X, canvas.ScrollOffset.Y, canvas.PanOffset.X, canvas.PanOffset.Y);
    internal void ApplyViewState(ScreenshotPaneViewState? state)
    {
        if (state is null) return; applyingState = true;
        try
        {
            View = Enum.IsDefined(state.View) ? state.View : ScreenshotPaneView.OriginalImage;
            FontSize = state.FontSize; canvas.ApplyState(state.FitMode, state.Zoom, new(state.ScrollX, state.ScrollY), new(state.PanX, state.PanY)); NativeStateApplied?.Invoke(state); UpdateLabels();
        }
        finally { applyingState = false; }
    }
    internal void ApplyTheme()
    {
        var p = ScreenshotRebuildStyle.Palette;
        BackColor = header.BackColor = tools.BackColor = p.Secondary;
        ForeColor = text.ForeColor = p.Text;
        viewport.BackColor = canvas.BackColor = empty.BackColor = p.Control;
        text.BackColor = p.Secondary;
        side.ForeColor = caption.ForeColor = dimensions.ForeColor = zoomLabel.ForeColor = fontLabel.ForeColor = empty.ForeColor = p.SecondaryText;
        caption.BackColor = dimensions.BackColor = side.BackColor = zoomLabel.BackColor = fontLabel.BackColor = p.Secondary;
        ScreenshotNativeScrollbarTheme.Apply(canvas); ScreenshotNativeScrollbarTheme.Apply(text);
        canvas.Invalidate(); Invalidate(true);
    }
    private ScreenshotRebuildButton MakeButton(string label, string icon, int width, Action action)
    {
        var button = new ScreenshotRebuildButton(label, icon, width);
        button.AccessibleName = label;
        button.Click += (_, _) => action(); return button;
    }
    private void ApplyTextFont()
    {
        var replacement = ScreenshotRebuildStyle.Font(fontSize * 72f / 96f);
        if (text.Font.Equals(replacement)) { replacement.Dispose(); return; }
        var previous = textFont; text.Font = replacement;
        if (ReferenceEquals(text.Font, replacement)) textFont = replacement;
        else { replacement.Dispose(); textFont = ReferenceEquals(text.Font, previous) ? previous : null; }
        if (previous is not null && !ReferenceEquals(previous, text.Font) && !ReferenceEquals(previous, textFont)) previous.Dispose();
    }
    private void UpdateContent()
    {
        if (maximize is null) return;
        segments.Selected = view;
        bool images = IsImageView;
        foreach (var control in imageTools) control.Visible = images;
        foreach (var control in textTools) control.Visible = !images;
        canvas.SetImage(CurrentBitmap);
        canvas.EmptyText = loading ? "正在读取图片…" : !hasContent ? "选择历史记录，或开始截图、导入图片" : view == ScreenshotPaneView.TranslatedImage ? "暂无译图" : "原图不可用";
        canvas.Visible = images;
        string content = CurrentText;
        if (!StringComparer.Ordinal.Equals(text.Text, content)) text.Text = content;
        text.Visible = !images && content.Length > 0; empty.Visible = !images && content.Length == 0;
        empty.Text = loading ? "正在读取文字…" : hasContent ? view == ScreenshotPaneView.SourceText ? "暂无原文" : "暂无译文" : "选择历史记录，或开始截图、导入图片";
        copy.Enabled = export.Enabled = content.Length > 0; edit.Enabled = hasContent;
        copyImage.Enabled = exportImage.Enabled = CurrentBitmap is not null;
        foreach (var control in imageTools) control.Enabled = CurrentBitmap is not null;
        UpdateLabels(); PerformLayout(); canvas.Invalidate(); NativeViewChanged?.Invoke();
    }
    private void UpdateLabels()
    {
        zoomLabel.Text = $"{Math.Round(canvas.Zoom * 100):0}%"; fontLabel.Text = $"{fontSize}px";
        if (fontMinus is not null) { fontMinus.Enabled = fontSize > 12; fontPlus.Enabled = fontSize < 28; }
        dimensions.Text = IsImageView ? CurrentBitmap is { } image ? $"{image.Width} × {image.Height}" : "" : UiLanguage.Choose($"{CurrentText.Length:N0} 字符",$"{CurrentText.Length:N0} characters");
        string label = view switch { ScreenshotPaneView.OriginalImage => "原始截图", ScreenshotPaneView.TranslatedImage => "翻译图片", ScreenshotPaneView.SourceText => "原文", _ => "译文" };
        string language = view is ScreenshotPaneView.OriginalImage or ScreenshotPaneView.SourceText ? sourceLanguage : targetLanguage;
        caption.Text = UiLanguage.Text(label)+(language.Length>0?" · "+UiLanguage.Text(language):"");
    }
    private void InterfaceLanguageChanged(object? sender,EventArgs e){UpdateLabels();PerformLayout();Invalidate(true);}
    private void NotifyStateChanged() { if (!applyingState) StateChanged?.Invoke(this, EventArgs.Empty); NativeViewChanged?.Invoke(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); PerformLayout(); }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e); if (arranging || maximize is null) return; arranging = true;
        try
        {
            int S(int value) => ScreenshotRebuildStyle.S(this, value);
            int width = Math.Max(1, ClientSize.Width), headerHeight = S(42), toolsHeight = S(44), footerHeight = S(38), pad = S(10), gap = S(6);
            var nextHorizontal = (width, DeviceDpi, view, maximized, Font, UiLanguage.Current);
            if (horizontalLayout != nextHorizontal || e.AffectedProperty == "Font")
            {
                header.SetBounds(1, 1, Math.Max(1, width - 2), headerHeight - 1);
                bool narrow = width < S(340);
                maximize.SetLayout(narrow ? "" : maximized ? "还原" : "最大化", narrow ? 26 : null, header.Width - pad, S(7), alignRight: true);
                int sideWidth=Math.Max(S(26),TextRenderer.MeasureText(side.Text,side.Font,Size.Empty,TextFormatFlags.NoPadding).Width+S(3));
                side.Visible = !narrow; side.SetBounds(pad, S(7), sideWidth, S(28));
                int segmentLeft = narrow ? pad : side.Right+gap;
                segments.SetBounds(segmentLeft, S(6), Math.Max(S(120), Math.Min(S(UiLanguage.IsTranslated?290:214), maximize.Left - gap - segmentLeft)), S(29));
                tools.SetBounds(1, headerHeight, Math.Max(1, width - 2), toolsHeight);
                zoomLabel.Size = new(S(38), S(26)); fontLabel.Size = new(S(38), S(26));
                int x = pad;
                bool compactTools = width < S(390);
                int toolGap = compactTools ? S(4) : gap;
                foreach (var control in IsImageView ? imageTools : textTools)
                {
                    if (control is ScreenshotRebuildButton button)
                    {
                        string label = button.Text; int? fixedWidth = null;
                        if (ReferenceEquals(control, imageTools[0])) { label = compactTools ? "适窗" : "适应窗口"; fixedWidth = compactTools ? 50 : null; }
                        else if (ReferenceEquals(control, imageTools[1])) { label = compactTools ? "适宽" : "适应宽度"; fixedWidth = compactTools ? 50 : null; }
                        else if (ReferenceEquals(control, copy)) { label = compactTools ? "" : "复制"; fixedWidth = compactTools ? 26 : null; }
                        else if (ReferenceEquals(control, export)) { label = compactTools ? "" : "导出"; fixedWidth = compactTools ? 26 : null; }
                        else if (ReferenceEquals(control, edit)) { label = compactTools ? "" : "详细编辑"; fixedWidth = compactTools ? 26 : null; }
                        button.SetLayout(label, fixedWidth, x, S(9));
                    }
                    else control.Location = new(x, S(9));
                    x += control.Width + toolGap;
                }
                int dimensionWidth = Math.Max(0, tools.Width - x - pad);
                dimensions.Visible = dimensionWidth >= S(60); dimensions.SetBounds(x, S(9), dimensionWidth, S(26));
                viewport.Padding = IsImageView ? Padding.Empty : new Padding(S(12), S(10), S(12), S(10));
                horizontalLayout = nextHorizontal;
            }
            int top = headerHeight + toolsHeight, viewportHeight = Math.Max(0, ClientSize.Height - top - footerHeight);
            viewport.SetBounds(S(6), top, Math.Max(1, width - S(12)), viewportHeight);
            caption.SetBounds(S(12), top + viewportHeight, Math.Max(1, width - S(44)), footerHeight);
        }
        finally { arranging = false; }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
        int S(int value) => ScreenshotRebuildStyle.S(this, value);
        var p = ScreenshotRebuildStyle.Palette; e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var border = new Pen(p.Border);
        using var outline = WorkspaceDrawing.Round(Rectangle.Inflate(ClientRectangle, -1, -1), S(7));
        e.Graphics.DrawPath(border, outline); e.Graphics.DrawLine(border, 1, S(42), Width - 2, S(42));
        WorkspaceSkin.Icon(e.Graphics, IsImageView ? "image" : "list", new(Width - S(25), Height - S(25), S(13), S(13)), p.SecondaryText);
    }
    protected override void Dispose(bool disposing)
    {
        Font? ownedFont = null;
        if (disposing) { canvas.SetImage(null); hints.Dispose(); imageMenu.Dispose(); textMenu.Dispose(); ownedFont = textFont; textFont = null; }
        try { base.Dispose(disposing); }
        finally { if (disposing) { ownedFont?.Dispose(); localFont.Dispose(); } }
    }
}

// Keep a selected passage intact when the user opens its context menu, even
// when the right click lands outside that passage. Native Ctrl+C stays intact.
internal sealed class ScreenshotRebuildTextBox : RichTextBox
{
    private (int Start, int Length)? rightClickSelection;
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ScreenshotNativeScrollbarTheme.Apply(this, force: true); }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0204) rightClickSelection = (SelectionStart, SelectionLength); // WM_RBUTTONDOWN
        if (message.Msg == 0x007B && rightClickSelection is { } beforeMenu) // WM_CONTEXTMENU
            Select(Math.Min(beforeMenu.Start, TextLength), Math.Min(beforeMenu.Length, Math.Max(0, TextLength - beforeMenu.Start)));
        if (message.Msg is 0x0204 or 0x0205)
        {
            base.WndProc(ref message);
            if (rightClickSelection is { } selection)
                Select(Math.Min(selection.Start, TextLength), Math.Min(selection.Length, Math.Max(0, TextLength - selection.Start)));
            if (message.Msg == 0x0205) rightClickSelection = null;
            return;
        }
        base.WndProc(ref message);
    }
    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if ((keyData == (Keys.Shift | Keys.F10) || keyData == Keys.Apps) && ContextMenuStrip is { } menu)
        {
            rightClickSelection = null;
            var caret = GetPositionFromCharIndex(Math.Min(TextLength, SelectionStart + SelectionLength));
            menu.Show(this, new Point(Math.Clamp(caret.X, 0, Math.Max(0, ClientSize.Width - 1)), Math.Clamp(caret.Y + Font.Height, 0, Math.Max(0, ClientSize.Height - 1))));
            return true;
        }
        return base.ProcessCmdKey(ref message, keyData);
    }
}

internal sealed class ScreenshotRebuildCanvas : ScrollableControl
{
    internal static Action<ScreenshotRebuildCanvas, string, double>? PaintTimingForSmoke;
    private Bitmap? image;
    private Bitmap? displayCache;
    private InterpolationMode cacheInterpolation;
    private readonly System.Windows.Forms.Timer refinementTimer = new() { Interval = 140 };
    private bool interactionPending;
    private int cacheBuildCount, cacheHitCount;
    private const long MaximumCachePixels = 8L * 1024 * 1024;
    private ScreenshotPaneFitMode fitMode = ScreenshotPaneFitMode.Window;
    private float zoom = 1, renderScale = 1;
    private bool arranging, dragging, positioning;
    private Point dragStart, dragOrigin, panOffset;
    private const float MinimumZoom = .2f, MaximumZoom = 5f;
    internal string EmptyText { get; set; } = "选择一条历史记录";
    internal event EventHandler? ViewStateChanged;
    internal event EventHandler? Activated;
    internal ScreenshotPaneFitMode FitMode => fitMode;
    internal float Zoom => zoom;
    internal Point ScrollOffset => new(-AutoScrollPosition.X, -AutoScrollPosition.Y);
    internal Point PanOffset => panOffset;
    internal Rectangle ImageBounds => new(ImageOrigin(), ScaledSize);
    // Diagnostics expose counts and dimensions only, never the borrowed image.
    internal (int Builds, int Hits, Size Size, long Pixels, bool RefinementPending, bool SizeLimitExceeded) CacheDiagnostics
        => (cacheBuildCount, cacheHitCount, displayCache?.Size ?? Size.Empty,
            displayCache is null ? 0 : (long)displayCache.Width * displayCache.Height,
            interactionPending, (long)ScaledSize.Width * ScaledSize.Height > MaximumCachePixels);
    internal ScreenshotRebuildCanvas()
    {
        AutoScroll = true; TabStop = true;
        AccessibleName = "图片预览，滚轮缩放，按住鼠标左键拖动图片";
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        refinementTimer.Tick += (_, _) =>
        {
            refinementTimer.Stop(); interactionPending = false;
            if (!IsDisposed && image is not null) Invalidate();
        };
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ScreenshotNativeScrollbarTheme.Apply(this, force: true); }
    internal void SetImage(Bitmap? value)
    {
        if (ReferenceEquals(image, value)) return;
        if (dragging) EndDrag();
        refinementTimer.Stop(); interactionPending = false;
        ReleaseDisplayCache(); image = value; Reflow(); Invalidate();
    }
    internal void ResetView() { if (dragging) EndDrag(); fitMode = ScreenshotPaneFitMode.Window; zoom = 1; panOffset = Point.Empty; AutoScrollPosition = Point.Empty; Reflow(); }
    internal void SetFit(ScreenshotPaneFitMode value)
    {
        if (dragging) EndDrag();
        fitMode = value == ScreenshotPaneFitMode.Width ? value : ScreenshotPaneFitMode.Window;
        panOffset = Point.Empty; AutoScrollPosition = Point.Empty; Reflow(); Changed();
    }
    internal void ApplyState(ScreenshotPaneFitMode mode, float scale, Point offset, Point pan = default)
    {
        fitMode = Enum.IsDefined(mode) ? mode : ScreenshotPaneFitMode.Window;
        zoom = float.IsFinite(scale) ? Math.Clamp(scale, MinimumZoom, MaximumZoom) : 1;
        panOffset = Point.Empty;
        Reflow(); AutoScrollPosition = new(Math.Max(0, offset.X), Math.Max(0, offset.Y));
        panOffset = new(Math.Clamp(pan.X, -1000000, 1000000), Math.Clamp(pan.Y, -1000000, 1000000));
        ClampPan(); Invalidate();
    }
    internal void ZoomBy(float factor) => SetZoom(zoom * factor);
    internal void SetZoom(float value, Point? at = null)
    {
        if (image is null) return;
        float next = float.IsFinite(value) ? Math.Clamp(value, MinimumZoom, MaximumZoom) : 1;
        if (fitMode == ScreenshotPaneFitMode.Manual && Math.Abs(next - zoom) < .00001f) return;
        Point anchor = at ?? new(ClientSize.Width / 2, ClientSize.Height / 2);
        var origin = ImageOrigin();
        float px = (anchor.X - origin.X) / Math.Max(.001f, renderScale), py = (anchor.Y - origin.Y) / Math.Max(.001f, renderScale);
        fitMode = ScreenshotPaneFitMode.Manual;
        zoom = next; QueueRefinement();
        panOffset = Point.Empty; AutoScrollPosition = Point.Empty; Reflow();
        SetImageOrigin(new((int)Math.Round(anchor.X - px * renderScale), (int)Math.Round(anchor.Y - py * renderScale)), notify: false);
        if (dragging) { dragStart = anchor; dragOrigin = ImageOrigin(); }
        Changed();
    }
    private Size ScaledSize => image is null ? Size.Empty : new(Math.Max(1, (int)Math.Round(image.Width * renderScale)), Math.Max(1, (int)Math.Round(image.Height * renderScale)));
    private Point CenteredOrigin()
    {
        var size = ScaledSize;
        return new(Math.Max(0, (ClientSize.Width - size.Width) / 2), Math.Max(0, (ClientSize.Height - size.Height) / 2));
    }
    private Point ImageOrigin()
    {
        var center = CenteredOrigin();
        return new(center.X + AutoScrollPosition.X + panOffset.X, center.Y + AutoScrollPosition.Y + panOffset.Y);
    }
    private Point ClampImageOrigin(Point desired)
    {
        if (image is null) return desired;
        var size = ScaledSize;
        int keepX = Math.Min(ScreenshotRebuildStyle.S(this, 48), Math.Min(size.Width, Math.Max(1, ClientSize.Width)));
        int keepY = Math.Min(ScreenshotRebuildStyle.S(this, 48), Math.Min(size.Height, Math.Max(1, ClientSize.Height)));
        return new(Math.Clamp(desired.X, keepX - size.Width, Math.Max(keepX - size.Width, ClientSize.Width - keepX)),
            Math.Clamp(desired.Y, keepY - size.Height, Math.Max(keepY - size.Height, ClientSize.Height - keepY)));
    }
    private void ClampPan()
    {
        if (image is null) return;
        var before = ImageOrigin(); var clamped = ClampImageOrigin(before);
        panOffset.Offset(clamped.X - before.X, clamped.Y - before.Y);
    }
    private void SetImageOrigin(Point desired, bool notify = true)
    {
        if (image is null) return;
        var before = ImageOrigin(); desired = ClampImageOrigin(desired);
        var center = CenteredOrigin();
        int maximumX = HorizontalScroll.Visible ? Math.Max(0, HorizontalScroll.Maximum - HorizontalScroll.LargeChange + 1) : 0;
        int maximumY = VerticalScroll.Visible ? Math.Max(0, VerticalScroll.Maximum - VerticalScroll.LargeChange + 1) : 0;
        var scroll = new Point(Math.Clamp(center.X - desired.X, 0, maximumX), Math.Clamp(center.Y - desired.Y, 0, maximumY));
        positioning = true;
        try
        {
            if (ScrollOffset != scroll) AutoScrollPosition = scroll;
            panOffset = new(desired.X - center.X + ScrollOffset.X, desired.Y - center.Y + ScrollOffset.Y);
        }
        finally { positioning = false; }
        if (notify && ImageOrigin() != before) Changed();
    }
    private void Reflow()
    {
        if (arranging) return; arranging = true;
        try
        {
            var previous = ScrollOffset;
            if (image is null) { if (AutoScrollMinSize != Size.Empty) AutoScrollMinSize = Size.Empty; return; }
            // Fit against the scrollbar-free client area without actually
            // removing and recreating native scrollbars on each resize step.
            // This canvas has no native border, so its outer size is precisely
            // the client area before scrollbars, including per-monitor DPI.
            int availableWidth = Math.Max(1, Width - 8 * DeviceDpi / 96);
            int availableHeight = Math.Max(1, Height - 8 * DeviceDpi / 96);
            Size extent = Size.Empty;
            float fit = Math.Min(availableWidth / (float)image.Width, availableHeight / (float)image.Height);
            if (fitMode == ScreenshotPaneFitMode.Window) { zoom = 1; renderScale = fit; }
            else if (fitMode == ScreenshotPaneFitMode.Width)
            {
                int scrollbar = image.Height * (availableWidth / (float)image.Width) > availableHeight ? SystemInformation.VerticalScrollBarWidth : 0;
                renderScale = Math.Max(1, availableWidth - scrollbar) / (float)image.Width;
                zoom = renderScale / Math.Max(.001f, fit);
                extent = new(0, ScaledSize.Height); previous.X = 0;
            }
            else { renderScale = fit * zoom; extent = ScaledSize; }
            if (AutoScrollMinSize != extent) AutoScrollMinSize = extent;
            int maximumX = HorizontalScroll.Visible ? Math.Max(0, HorizontalScroll.Maximum - HorizontalScroll.LargeChange + 1) : 0;
            int maximumY = VerticalScroll.Visible ? Math.Max(0, VerticalScroll.Maximum - VerticalScroll.LargeChange + 1) : 0;
            var desired = new Point(Math.Clamp(previous.X, 0, maximumX), Math.Clamp(previous.Y, 0, maximumY));
            if (ScrollOffset != desired) AutoScrollPosition = desired;
            ClampPan();
        }
        finally { arranging = false; }
        Invalidate();
    }
    private void Changed() { Invalidate(); ViewStateChanged?.Invoke(this, EventArgs.Empty); }
    private void QueueRefinement()
    {
        if (image is null || IsDisposed) return;
        interactionPending = true; refinementTimer.Stop(); refinementTimer.Start();
    }
    private void ReleaseDisplayCache() { displayCache?.Dispose(); displayCache = null; }
    private bool DrawCachedImage(Graphics graphics, Point origin, Size size, InterpolationMode interpolation)
    {
        if (displayCache is not null && displayCache.Size == size && cacheInterpolation == interpolation)
        {
            cacheHitCount++; graphics.DrawImageUnscaled(displayCache, origin); return true;
        }
        if (interactionPending) return false;
        ReleaseDisplayCache();
        // A large manual zoom must not allocate an image several screens wide.
        // In that case GDI+ still draws the original, clipped to this viewport.
        if ((long)size.Width * size.Height > MaximumCachePixels) return false;
        var scaled = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            using var surface = Graphics.FromImage(scaled);
            surface.CompositingMode = CompositingMode.SourceCopy;
            surface.InterpolationMode = interpolation; surface.PixelOffsetMode = PixelOffsetMode.HighQuality;
            surface.DrawImage(image!, new Rectangle(Point.Empty, size), new Rectangle(Point.Empty, image!.Size), GraphicsUnit.Pixel);
            displayCache = scaled; cacheInterpolation = interpolation; cacheBuildCount++;
        }
        catch { scaled.Dispose(); throw; }
        graphics.DrawImageUnscaled(displayCache, origin); return true;
    }
    protected override void OnResize(EventArgs e)
    {
        var previous = (zoom, ScrollOffset, panOffset);
        base.OnResize(e);
        if (arranging) return;
        QueueRefinement(); Reflow();
        if (previous != (zoom, ScrollOffset, panOffset)) ViewStateChanged?.Invoke(this, EventArgs.Empty);
    }
    protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); if (!arranging && !positioning) { ClampPan(); Changed(); } }
    private void PaintCheckpoint(Action<ScreenshotRebuildCanvas, string, double>? timing, ref long checkpoint, string stage)
    {
        if (timing is null) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        timing(this, stage, (now - checkpoint) * 1000d / System.Diagnostics.Stopwatch.Frequency);
        checkpoint = System.Diagnostics.Stopwatch.GetTimestamp();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var timing = ScreenshotRebuildSelfTests.IsActive ? PaintTimingForSmoke : null;
        long started = timing is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp(), checkpoint = started;
        try
        {
            var p = ScreenshotRebuildStyle.Palette;
            e.Graphics.Clear(p.Secondary);
            PaintCheckpoint(timing, ref checkpoint, "Clear");
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var backdrop = WorkspaceDrawing.Round(ClientRectangle, ScreenshotRebuildStyle.S(this, 6));
            using (var background = new SolidBrush(p.Control)) e.Graphics.FillPath(background, backdrop);
            PaintCheckpoint(timing, ref checkpoint, "Backdrop");
            var paintState = e.Graphics.Save();
            e.Graphics.SetClip(backdrop, CombineMode.Intersect);
            PaintCheckpoint(timing, ref checkpoint, "Clip");
            if (image is null)
            {
                WorkspaceDrawing.Text(e.Graphics, UiLanguage.Text(EmptyText), Font, Rectangle.Inflate(ClientRectangle, -8, -8), p.SecondaryText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                e.Graphics.Restore(paintState);
                PaintCheckpoint(timing, ref checkpoint, "EmptyTextAndRestore");
                return;
            }
            var origin = ImageOrigin(); var size = ScaledSize;
            var interpolation = renderScale >= 2 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            bool cached = DrawCachedImage(e.Graphics, origin, size, interpolation);
            PaintCheckpoint(timing, ref checkpoint, "CacheLookupBuildOrBlit");
            if (!cached)
            {
                // While sizes are changing, avoid rebuilding a high-quality scaled
                // surface for every intermediate input. Idle restores the exact
                // original interpolation policy before caching the final size.
                e.Graphics.InterpolationMode = interactionPending && interpolation != InterpolationMode.NearestNeighbor
                    ? InterpolationMode.Bilinear : interpolation;
                e.Graphics.DrawImage(image, new Rectangle(origin, size), new Rectangle(Point.Empty, image.Size), GraphicsUnit.Pixel);
            }
            PaintCheckpoint(timing, ref checkpoint, "RawDrawImage");
            e.Graphics.Restore(paintState);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3), p.Accent, p.Control);
            PaintCheckpoint(timing, ref checkpoint, "RestoreAndFocus");
        }
        finally
        {
            if (timing is not null) timing(this, "OnPaintTotal", (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d / System.Diagnostics.Stopwatch.Frequency);
        }
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); Focus(); Activated?.Invoke(this, EventArgs.Empty);
        if (e.Button != MouseButtons.Left || image is null) return;
        dragging = true; dragStart = e.Location; dragOrigin = ImageOrigin(); Capture = true; Cursor = Cursors.Hand;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); if (!dragging) return;
        int dx = e.X - dragStart.X, dy = e.Y - dragStart.Y;
        if (dx == 0 && dy == 0) return;
        if (fitMode != ScreenshotPaneFitMode.Manual) { fitMode = ScreenshotPaneFitMode.Manual; Reflow(); }
        SetImageOrigin(new(dragOrigin.X + dx, dragOrigin.Y + dy));
    }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left) EndDrag(); }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) EndDrag(); }
    private void EndDrag() { dragging = false; Capture = false; Cursor = Cursors.Default; }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
        SetZoom(zoom * (float)Math.Pow(1.2, e.Delta / 120d), e.Location);
    }
    private void SetScrollOffset(Point value)
    {
        var previous = ScrollOffset;
        int maximumX = HorizontalScroll.Visible ? Math.Max(0, HorizontalScroll.Maximum - HorizontalScroll.LargeChange + 1) : 0;
        int maximumY = VerticalScroll.Visible ? Math.Max(0, VerticalScroll.Maximum - VerticalScroll.LargeChange + 1) : 0;
        var desired = new Point(Math.Clamp(value.X, 0, maximumX), Math.Clamp(value.Y, 0, maximumY));
        if (desired == previous) return;
        AutoScrollPosition = desired;
        if (ScrollOffset != previous) Changed();
    }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); var scroll = ScrollOffset; int step = Math.Max(24, Font.Height * 3);
        switch (e.KeyCode)
        {
            case Keys.Add: case Keys.Oemplus: ZoomBy(1.2f); break;
            case Keys.Subtract: case Keys.OemMinus: ZoomBy(1 / 1.2f); break;
            case Keys.D0: case Keys.NumPad0: SetZoom(1); break;
            case Keys.F: SetFit(ScreenshotPaneFitMode.Window); break;
            case Keys.Left: SetScrollOffset(new(Math.Max(0, scroll.X - step), scroll.Y)); break;
            case Keys.Right: SetScrollOffset(new(scroll.X + step, scroll.Y)); break;
            case Keys.Up: SetScrollOffset(new(scroll.X, Math.Max(0, scroll.Y - step))); break;
            case Keys.Down: SetScrollOffset(new(scroll.X, scroll.Y + step)); break;
            case Keys.PageUp: SetScrollOffset(new(scroll.X, Math.Max(0, scroll.Y - ClientSize.Height))); break;
            case Keys.PageDown: SetScrollOffset(new(scroll.X, scroll.Y + ClientSize.Height)); break;
            case Keys.Home: SetScrollOffset(Point.Empty); break;
            case Keys.End: SetScrollOffset(new(scroll.X, Math.Max(0, ScaledSize.Height - ClientSize.Height))); break;
            default: return;
        }
        e.Handled = e.SuppressKeyPress = true;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { refinementTimer.Dispose(); ReleaseDisplayCache(); image = null; }
        base.Dispose(disposing);
    }
}
