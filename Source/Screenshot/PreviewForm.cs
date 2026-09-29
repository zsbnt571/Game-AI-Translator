using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ScreenshotTranslationUiTester.CorePipelineV2;

namespace ScreenshotTranslationUiTester;

public sealed partial class PreviewForm : Form
{
    internal int RecognitionRegionCountForSmoke => _recognitionV2?.Regions.Count ?? 0;
    internal int VisionWorkerPidForSmoke => _visionRuntimeManager.ActiveWorkerPid ?? 0;
    internal VisionRuntimeManager VisionRuntimeForSmoke => _visionRuntimeManager;
    internal int OcrWorkerPidForSmoke => _ocrRuntimeManager.ActiveWorkerPid ?? 0;
    internal ImageHashDiagnostics ImageHashDiagnosticsForSmoke=>_imageIdentity.Diagnostics;
    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed record TranslationRequestSnapshot(
        string RequestId, long Generation, string ImageSessionId, string OcrRequestId,
        long OcrGeneration, OcrEngineKind OcrEngine, TranslationTextSource Source,
        IReadOnlyList<TranslationItem> Items, IReadOnlyList<TranslationSemanticGroup> SemanticGroups,
        IReadOnlyList<StructuredTextGroup> StructuredGroups,
        string TextSha256, int CharacterCount,
        int Utf8ByteCount, int LineCount, string CacheKey, ApiSettings Settings,
        string ProviderId, string BaseUrlIdentity, string Model, string TargetLanguage,
        TranslationStyle Style, string PromptHash, string PreserveRules, ProcessingTaskTrace Trace);

    private Bitmap _original;
    // UI and background GDI+ consumers must never share one Bitmap instance.
    // _original is owned exclusively by PictureBox/UI painting; _ocrSource is
    // the immutable session snapshot used by hashing and recognition workers.
    private Bitmap? _ocrSource;
    private SessionImageIdentity? _imageIdentity;
    private Task<Bitmap>? _ocrSnapshotTask;
    private bool _allowUiImageDisplay;
    private Bitmap? _originalDisplay;
    private Bitmap? _translatedDisplay;
    private PreviewMode _startupMode;
    private readonly ApiSettings _settings;
    private readonly OcrService _ocrService;
    private readonly TranslationService _translationService;
    private readonly OcrRuntimeManager _ocrRuntimeManager;
    private readonly VisionRuntimeManager _visionRuntimeManager;
    private readonly RecognitionPipelineV2 _recognitionPipelineV2;
    private readonly SessionServices _session;
    private bool _ownsServices;
    private bool _ownsVisionRuntime;
    private readonly RenderSettings _renderSettings = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _ocrOperation;
    private Task? _ocrTask;
    private long _ocrGeneration;
    private string _currentOcrRequestId = "";
    private string _imageSessionId = Guid.NewGuid().ToString("N");
    private OcrEngineKind _displayedOcrEngine;
    private Task? _translationTask;
    private long _translationGeneration;
    private string _currentTranslationRequestId = "";
    private Func<IReadOnlyList<TranslationItem>, ApiSettings, IProgress<TranslationProgress>?, CancellationToken,
        Task<TranslationBatchResult>> _translateBatchAsync;
    private readonly System.Windows.Forms.Timer _apiTimer = new() { Interval = 250 };
    private DateTimeOffset _apiRequestStarted;
    private long _apiElapsedBeforeRequest;
    private bool _apiTiming;
    private string _translationStageText = "";
    private readonly Panel _imagePanel = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(15, 17, 22) };
    private readonly PictureBox _picture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(15, 17, 22) };
    private float _viewZoom=1f;private bool _fitView=true;private const float MinViewZoom=.1f,MaxViewZoom=5f;private bool _panning;private Point _panStart,_panScrollStart;private bool _updatingImageLayout,_syncingZoomSelector;
    private readonly RichTextBox _text = new() { ReadOnly = true, ScrollBars = RichTextBoxScrollBars.Both, Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 10F), BorderStyle = BorderStyle.None, DetectUrls = true };
    private readonly ComboBox _imageMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly ComboBox _textMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 135 };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.LightSkyBlue, Margin = new Padding(12, 10, 4, 4) };
    private Button? _retryButton;
    private Button? _errorDetailsButton;
    private string? _lastTranslationError;
    private readonly ComboBox _zoom = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill };
    private OcrDocument? _document;
    private CorePipelineDocument? _corePipelineV2;
    private RecognitionDocumentV2? _recognitionV2;
    private VisualAnalysisResult? _visualAnalysisV2;
    private readonly HashSet<RegionOverlayLayer> _visibleV2Layers = [];
    private int _recognitionRunCount;
    private WallClockEndToEndTimer? _wallClock;
    private ProcessingTaskTrace? _processingTask;
    internal Func<BackgroundComputeDevice>? NextBackgroundDevice { get; set; }
    internal Func<BackgroundTreatment>? NextBackgroundTreatment { get; set; }
    internal Func<OcrLoad>? NextOcrLoad { get; set; }
    private void BeginProcessingOperation(string trigger)
    {
        _processingTask=new ProcessingTaskTrace(_imageSessionId,
            NextBackgroundDevice?.Invoke() ?? _settings.BackgroundComputeDevice,trigger,NextBackgroundTreatment?.Invoke() ?? _settings.BackgroundTreatment,FontManager.ResolveTranslationImageProfile(_settings),NextOcrLoad?.Invoke() ?? _settings.OcrLoad);
        _wallClock=_processingTask.Timer;_finalPreviewReady=false;
        var trace=_processingTask;
        trace.PhaseChanged=phase=>
        {
            if(IsDisposed || !IsHandleCreated)return;
            try{BeginInvoke(new Action(()=>{if(CanUse() && ReferenceEquals(trace,_processingTask) && trace.Timer.FinalResultReadyMs is null && !(_operation?.IsCancellationRequested??false))SetStatus(phase);}));}
            catch(InvalidOperationException){}
        };
    }
    private string? _lastTimingPath;
    private bool _finalPreviewReady;
    private RegionRenderResult? _lastRegionRender;
    private CorePipelineProductTiming? _lastCorePipelineTiming;
    private OcrEngineResult? _lastOcrResult;
    internal Action<OcrEngineResult>? DiagnosticOcrCompleted { get; set; }
    internal Action<Exception>? DiagnosticOcrFailure { get; set; }
    private long _translationParseMs;
    private bool _missingRecoveryTriggered;
    private IReadOnlyList<TranslationSemanticGroup> _translationSemanticGroups = [];
    private IReadOnlyList<StructuredTextGroup> _structuredTextGroups = [];
    private readonly Dictionary<string, (int Start, int Length)> _textRanges = new(StringComparer.Ordinal);
    private readonly Dictionary<string,RightTextRangeIdentity> _rightTextRangeIdentities=new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string,string> _translationByUnitId=new Dictionary<string,string>(StringComparer.Ordinal);
    private readonly Dictionary<TextViewMode, int> _textScrollLines = new();
    private TextViewMode _selectedTextMode;
    private string? _hoverSegmentId;
    private string? _lockedSegmentId;
    private RightTextRangeIdentity? _hoverRightIdentity;
    private RightTextRangeIdentity? _lockedRightIdentity;
    private readonly HashSet<string> _appliedHighlightRangeIds=new(StringComparer.Ordinal);
    private int _highlightRangeFormatCount;
    private int _rightTextFullAssignmentDuringInteraction;
    private bool _highlightInteractionActive;
    private bool _busy;
    private ImageViewMode _preferredImageMode=ImageViewMode.Original;
    private ImageViewMode _displayedImageMode=ImageViewMode.Original;
    private bool _suppressImageModeEvent;
    private bool _closing;
    private bool _adjusting;
    private bool _atomicCommitInProgress;
    private int _rightTextSetCount,_rightTextAppendCount,_imageAssignCount,_previewLayoutCount,_invalidateCount,_refreshCount=0,_paintCount,_scrollExtentChangeCount,_displayScaleChangeCount;
    private int _rightTranslationFinalSetCount,_rightPanelWidthChangeAfterFinalText,_rightFontChangeAfterFinalText,_rightLayoutEventAfterFinalText,_rightScrollExtentChangeAfterFinalText;
    private bool _finalTranslationTextCommitted;private int _rightPanelWidthAtFinal;
    private long _layoutVersion;
    private float _lastDisplayScale=-1;
    private Size _lastScrollExtent=Size.Empty;
    private bool _awaitingFirstFinalPaint;
    private readonly System.Windows.Forms.Timer _stableFrameTimer=new(){Interval=650};
    private RealExeE2ESnapshot? _lastE2ESnapshot;
    private bool _suppressAutoOcrForE2E;
    private bool _showGroupBounds = false;
    private bool _initialViewApplied;
    private bool _historyRequiresReOcr;
    private bool _historyViewOnly;
    private HistoryCoreSnapshot? _historySnapshot;
    private CoreProcessingProvenance? _processingProvenance;
    private string? _rightTextContextUrl;
    private readonly Action<PreviewWindowPlacement>? _saveWindowPlacement;
    private bool _isUserResize;
    private bool _isProgrammaticResize;
    private readonly ComboBox _ocrEngineSelector = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 118 };
    private Button? _moreButton;
    private Button? _textVisibilityButton;
    private readonly List<Control> _fullModeControls=[];
    public event EventHandler? ReSelectRequested;
    public event Action<PreviewForm, Bitmap>? SelfCaptureRequested;
    public event Action<ApiSettings>? AppearanceSettingsChanged;
    private const int EmGetFirstVisibleLine = 0x00CE;
    private const int EmGetScrollPos = 0x04DD;
    private const int EmSetScrollPos = 0x04DE;
    private const int EmLineScroll = 0x00B6;
    private const int WmVScroll = 0x0115;
    private const int SbBottom = 7;
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref NativePoint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    public PreviewForm(Bitmap image, PreviewMode startupMode, ApiSettings settings,
        OcrService ocrService, TranslationService translationService)
        : this(image, startupMode, settings, ocrService, translationService,
            new OcrRuntimeManager(AppContext.BaseDirectory, ocrService), new SessionServices())
    {
        _ownsServices = true;
    }

    public PreviewForm(Bitmap image, PreviewMode startupMode, ApiSettings settings,
        OcrService ocrService, TranslationService translationService, OcrRuntimeManager ocrRuntimeManager,
        SessionServices session, Action<PreviewWindowPlacement>? saveWindowPlacement = null)
        : this(image, startupMode, settings, ocrService, translationService, ocrRuntimeManager,
            new VisionRuntimeManager(AppContext.BaseDirectory), session, saveWindowPlacement)
    {
        _ownsVisionRuntime = true;
    }

    public PreviewForm(Bitmap image, PreviewMode startupMode, ApiSettings settings,
        OcrService ocrService, TranslationService translationService, OcrRuntimeManager ocrRuntimeManager,
        VisionRuntimeManager visionRuntimeManager, SessionServices session,
        Action<PreviewWindowPlacement>? saveWindowPlacement = null, bool takeImageOwnership = false)
    {
        _original = takeImageOwnership ? image : new Bitmap(image); // UI-owned image.
        _ocrSource = null;
        _imageIdentity = null;
        _allowUiImageDisplay = false;
        _startupMode = startupMode;
        _settings = ApiSettingsSnapshot.Copy(settings);
        if(_settings.PreviewDefaultText==PreviewDefaultText.Hidden)_settings.PreviewTextPanelVisible=false;
        _saveWindowPlacement = saveWindowPlacement;
        // Frozen V1 regression exercises non-recognition behavior with its
        // original synchronous OCR-cache fixtures. V2 vision has separate
        // real-image tests and is disabled only in that explicit test host.
        if (Environment.GetEnvironmentVariable("SCREENSHOT_TRANSLATOR_V2_FROZEN_TEST") == "1")
            _settings.VisualModel = VisualModelKind.Off;
        _ocrService = ocrService;
        _translationService = translationService;
        _translateBatchAsync = (items, requestSettings, progress, token) =>
            TranslationProviderRegistry.Get(requestSettings.TranslationProviderKind)
                .Factory(translationService).TranslateAsync(items, requestSettings, progress, token);
        _ocrRuntimeManager = ocrRuntimeManager;
        _visionRuntimeManager = visionRuntimeManager;
        _recognitionPipelineV2 = new RecognitionPipelineV2(_ocrRuntimeManager, _visionRuntimeManager);
        _session = session;
        Text = $"游戏 AI 截图翻译器 {BuildIdentity.ProductVersion} · 预览";
        TopMost=_settings.PreviewAlwaysOnTop;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1220, 800);
        MinimumSize = new Size(820, 560);
        FontSettingsPolicy.ApplyPreviewText(_text,_settings);FontSettingsPolicy.ApplyOverlay(_renderSettings,_settings);
        ConfigureRightTextUrlInteraction();
        if (_settings.PreviewWindowSizingMode==PreviewWindowSizingMode.FollowPrevious && (_settings.HasLastUserPreviewBounds || _settings.HasPreviewWindowPlacement))
        {
            StartPosition = FormStartPosition.Manual;
            var x=_settings.HasLastUserPreviewBounds?_settings.LastUserPreviewX:_settings.PreviewWindowX;
            var y=_settings.HasLastUserPreviewBounds?_settings.LastUserPreviewY:_settings.PreviewWindowY;
            var width=_settings.HasLastUserPreviewBounds?_settings.LastUserPreviewWidth:_settings.PreviewWindowWidth;
            var height=_settings.HasLastUserPreviewBounds?_settings.LastUserPreviewHeight:_settings.PreviewWindowHeight;
            Bounds = PreviewWindowPlacementPolicy.Normalize(
                new(x, y, width, height),
                Screen.AllScreens.Select(x => x.WorkingArea).ToArray(), MinimumSize);
            if (_settings.HasLastUserPreviewBounds?_settings.LastUserPreviewMaximized:_settings.PreviewWindowMaximized) WindowState = FormWindowState.Maximized;
        }
        else
        {
            var area=Screen.FromPoint(Cursor.Position).WorkingArea;var resolved=PreviewSizingPolicy.Resolve(_settings.PreviewWindowSizingMode,image.Size,area.Size,new(_settings.FixedPreviewWidth,_settings.FixedPreviewHeight),new(_settings.LastPreviewWidth,_settings.LastPreviewHeight),MinimumSize);
            Size=resolved;StartPosition=FormStartPosition.Manual;Location=new(area.Left+Math.Max(0,(area.Width-Width)/2),area.Top+Math.Max(0,(area.Height-Height)/2));
        }
        KeyPreview = true;
        BuildUi();
        FontManager.ApplyUi(this,_settings);
        FontManager.ApplyPreviewText(_text,_settings);
        UiTheme.Apply(this,_settings);
        Shown += (_, _) => { UpdateSplitter();RealExeE2ETrace.Mark("T_SOURCE_PREVIEW_COMMIT");BeginInvoke(new Action(async ()=>
            {
                RealExeE2ETrace.Mark("T_PREVIEW_RESPONSIVE");PostConfirmPerformanceTrace.Write("UIIdle",new{ElapsedMs=_wallClock?.ElapsedMs??0});
                if(CursorCaptureStageDump.Enabled)
                {
                    try{await EnsureOcrSnapshotAsync();}
                    finally{if(!IsDisposed)BeginInvoke(new Action(Close));}
                }
                else if(!_suppressAutoOcrForE2E)StartOcr(_startupMode != PreviewMode.OcrOnly);
            })); };
        Resize += (_, _) => { if(_isUserResize||!_finalPreviewReady)UpdateSplitter(); };
        ResizeBegin += (_, _) => { _isUserResize=true; SaveTextScroll(SelectedTextMode); };
        ResizeEnd += (_, _) => { BeginInvoke(new Action(() => RestoreTextScroll(SelectedTextMode))); if(_isUserResize&&!_isProgrammaticResize)SavePlacementNow(); _isUserResize=false; };
        KeyDown += HandleKeyDown;
        _apiTimer.Tick += (_, _) => UpdateRunningApiTime();
        _picture.Paint += Picture_Paint;
        _picture.MouseMove += Picture_MouseMove;
        _picture.MouseLeave += (_, _) => SetHover(null);
        _picture.Click += Picture_Click;
        _picture.MouseWheel+=Picture_MouseWheel;_picture.MouseDown+=Picture_MouseDown;_picture.MouseUp+=Picture_MouseUp;
        _text.MouseMove += Text_MouseMove;
        _text.MouseLeave += (_, _) => SetHover(null);
        _text.MouseClick += Text_Click;
        _split.Panel2.SizeChanged+=(_,_)=>{if(_finalTranslationTextCommitted&&_split.Panel2.ClientSize.Width!=_rightPanelWidthAtFinal){_rightPanelWidthChangeAfterFinalText++;_rightPanelWidthAtFinal=_split.Panel2.ClientSize.Width;}};
        _text.FontChanged+=(_,_)=>{if(_finalTranslationTextCommitted)_rightFontChangeAfterFinalText++;};
        _text.Layout+=(_,_)=>{if(_finalTranslationTextCommitted&&!_atomicCommitInProgress)_rightLayoutEventAfterFinalText++;};
    }

    private void SavePlacementNow(){if(_settings.PreviewWindowSizingMode!=PreviewWindowSizingMode.FollowPrevious||_saveWindowPlacement is null||IsDisposed)return;var b=WindowState==FormWindowState.Normal?Bounds:RestoreBounds;_saveWindowPlacement(new(b.X,b.Y,b.Width,b.Height,WindowState==FormWindowState.Maximized));}

    internal void AdoptCapturedImage(Bitmap image,PreviewMode mode)
    {
        if(Visible||IsDisposed)throw new InvalidOperationException("Only a hidden prewarmed preview can adopt a capture.");
        _operation?.Cancel();_ocrOperation?.Cancel();_picture.Image=null;_originalDisplay?.Dispose();_originalDisplay=null;_translatedDisplay?.Dispose();_translatedDisplay=null;
        DisposeBitmapWhenSafe(_original,_translationTask);
        if(_ocrSource is not null)DisposeBitmapWhenSafe(_ocrSource,_ocrTask,_imageIdentity?.ComputationTask);
        _wallClock=null;_processingTask=null;_corePipelineV2=null;_historyViewOnly=false;_historyRequiresReOcr=false;_historySnapshot=null;_processingProvenance=null;
        _original=image;_ocrSource=null;_imageIdentity=null;_ocrSnapshotTask=null;_allowUiImageDisplay=false;_startupMode=mode;_imageSessionId=Guid.NewGuid().ToString("N");_document=null;_recognitionV2=null;_lastOcrResult=null;_translationSemanticGroups=[];_structuredTextGroups=[];_translationByUnitId=new Dictionary<string,string>(StringComparer.Ordinal);
        _preferredImageMode=ResolveInitialPreviewSelection().Image;_displayedImageMode=ImageViewMode.Original;_picture.Image=null;UpdateImageLayout();_suppressAutoOcrForE2E=false;
    }

    public void ApplyAppearanceSettings(ApiSettings settings)
    {
        var beforeUi=(_settings.UiFontMode,_settings.UiFontFamily,_settings.UiFontSize);
        var beforeResolvedPreview=FontManager.ResolvePreviewProfile(_settings);
        var beforePreview=(_settings.PreviewTextFontMode,_settings.PreviewTextFontFamily,_settings.PreviewTextFontSize);
        var beforeOverlay=(_settings.OverlayFontMode,_settings.OverlayFontFamily,_settings.OverlayFontSize,_settings.OverlayBackgroundStyle,_settings.OverlayBackgroundOpacity);
        var timing=Stopwatch.StartNew();long themeMs=0,uiFontMs=0,previewFontMs=0,renderMs=0,refreshMs=0;
        _settings.PreviewZoomInBinding=settings.PreviewZoomInBinding;_settings.PreviewZoomOutBinding=settings.PreviewZoomOutBinding;_settings.PreviewResetFitBinding=settings.PreviewResetFitBinding;
        _settings.PreviewTextPanelVisible=settings.PreviewTextPanelVisible;if(_textVisibilityButton is not null)_textVisibilityButton.Text=_settings.PreviewTextPanelVisible?"隐藏文本":"显示文本";
        _settings.TypographyConfigVersion=settings.TypographyConfigVersion;_settings.ThemeMode=settings.ThemeMode;_settings.CustomThemeMainBackground=settings.CustomThemeMainBackground;_settings.CustomThemeSecondaryBackground=settings.CustomThemeSecondaryBackground;_settings.CustomThemeText=settings.CustomThemeText;_settings.CustomThemeSecondaryText=settings.CustomThemeSecondaryText;_settings.CustomThemeAccent=settings.CustomThemeAccent;_settings.CustomThemeBorder=settings.CustomThemeBorder;_settings.UiFontMode=settings.UiFontMode;_settings.UiFontFamily=settings.UiFontFamily;_settings.UiFontSize=settings.UiFontSize;_settings.PreviewTextFontMode=settings.PreviewTextFontMode;_settings.PreviewTextFontFamily=settings.PreviewTextFontFamily;_settings.PreviewTextFontSize=settings.PreviewTextFontSize;_settings.OverlayFontMode=settings.OverlayFontMode;_settings.OverlayFontFamily=settings.OverlayFontFamily;_settings.OverlayFontSize=settings.OverlayFontSize;_settings.OverlayBackgroundStyle=settings.OverlayBackgroundStyle;_settings.OverlayBackgroundOpacity=settings.OverlayBackgroundOpacity;
        var stage=Stopwatch.StartNew();UiTheme.Apply(this,_settings);stage.Stop();themeMs=stage.ElapsedMilliseconds;
        var uiChanged=beforeUi!=(_settings.UiFontMode,_settings.UiFontFamily,_settings.UiFontSize);
        var previewChanged=beforePreview!=(_settings.PreviewTextFontMode,_settings.PreviewTextFontFamily,_settings.PreviewTextFontSize);
        var overlayChanged=beforeOverlay!=(_settings.OverlayFontMode,_settings.OverlayFontFamily,_settings.OverlayFontSize,_settings.OverlayBackgroundStyle,_settings.OverlayBackgroundOpacity);
        if(uiChanged){stage.Restart();FontManager.ApplyUi(this,_settings);stage.Stop();uiFontMs=stage.ElapsedMilliseconds;}
        var resolvedPreview=FontManager.ResolvePreviewProfile(_settings);
        if(previewChanged||beforeResolvedPreview.PrimaryFamily!=resolvedPreview.PrimaryFamily||beforeResolvedPreview.Size!=resolvedPreview.Size){stage.Restart();FontSettingsPolicy.ApplyPreviewText(_text,_settings);stage.Stop();previewFontMs=stage.ElapsedMilliseconds;}
        FontSettingsPolicy.ApplyOverlay(_renderSettings,_settings);
        if(_document is not null&&overlayChanged&&!_busy){stage.Restart();BuildTranslatedDisplay();stage.Stop();renderMs=stage.ElapsedMilliseconds;}
        if(_document is not null&&(overlayChanged||previewChanged)){stage.Restart();UpdateIndependentViews();stage.Stop();refreshMs=stage.ElapsedMilliseconds;SetStatus("外观已更新：仅应用对应字体范围，未重新识别或调用翻译 API。");}
        timing.Stop();AppLog.Write("preview-performance",$"operation=ApplyAppearance total_ms={timing.ElapsedMilliseconds} theme_ms={themeMs} ui_font_ms={uiFontMs} preview_font_ms={previewFontMs} renderer_ms={renderMs} refresh_ms={refreshMs} ui_changed={uiChanged} preview_changed={previewChanged} overlay_changed={overlayChanged}");
    }
    public void ApplyPreviewWindowSettings(ApiSettings settings,bool applyGeometry)
    {
        var previous=_settings.PreviewWindowSizingMode;_settings.PreviewWindowSizingMode=settings.PreviewWindowSizingMode;_settings.FixedPreviewWidth=settings.FixedPreviewWidth;_settings.FixedPreviewHeight=settings.FixedPreviewHeight;_settings.HasPreviewWindowPlacement=settings.HasPreviewWindowPlacement;_settings.PreviewWindowX=settings.PreviewWindowX;_settings.PreviewWindowY=settings.PreviewWindowY;_settings.PreviewWindowWidth=settings.PreviewWindowWidth;_settings.PreviewWindowHeight=settings.PreviewWindowHeight;_settings.PreviewWindowMaximized=settings.PreviewWindowMaximized;_settings.LastPreviewWidth=settings.LastPreviewWidth;_settings.LastPreviewHeight=settings.LastPreviewHeight;
        if(!applyGeometry||previous==_settings.PreviewWindowSizingMode)return;
        _isProgrammaticResize=true;
        try{
        var area=Screen.FromControl(this).WorkingArea;
        if(_settings.PreviewWindowSizingMode==PreviewWindowSizingMode.FollowPrevious&&_settings.HasPreviewWindowPlacement){WindowState=FormWindowState.Normal;Bounds=PreviewWindowPlacementPolicy.Normalize(new(_settings.PreviewWindowX,_settings.PreviewWindowY,_settings.PreviewWindowWidth,_settings.PreviewWindowHeight),Screen.AllScreens.Select(x=>x.WorkingArea).ToArray(),MinimumSize);if(_settings.PreviewWindowMaximized)WindowState=FormWindowState.Maximized;}
        else if(_settings.PreviewWindowSizingMode!=PreviewWindowSizingMode.FollowPrevious){WindowState=FormWindowState.Normal;Size=PreviewSizingPolicy.Resolve(_settings.PreviewWindowSizingMode,_original.Size,area.Size,new(_settings.FixedPreviewWidth,_settings.FixedPreviewHeight),new(_settings.LastPreviewWidth,_settings.LastPreviewHeight),MinimumSize);Location=new(area.Left+Math.Max(0,(area.Width-Width)/2),area.Top+Math.Max(0,(area.Height-Height)/2));}
        }finally{_isProgrammaticResize=false;}
    }
    private DialogResult ShowPreviewDialog(Form dialog){FontManager.ApplyUi(dialog,_settings);return ModalSafety.Show(this,dialog);}
    private DialogResult ShowPreviewDialog(CommonDialog dialog)=>ModalSafety.Run(this,()=>dialog.ShowDialog(this));
    private void ShowAppearanceQuickDialog(){using var dialog=new AppearanceSettingsDialog(_settings);if(ShowPreviewDialog(dialog)!=DialogResult.OK)return;ApplyPreviewWindowSettings(dialog.Value,true);ApplyAppearanceSettings(dialog.Value);AppearanceSettingsChanged?.Invoke(ApiSettingsSnapshot.Copy(dialog.Value));}

    private void BuildUi()
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 94, WrapContents = true, Padding = new Padding(6),
            BackColor = Color.FromArgb(29, 34, 44)
        };
        Button Add(string label, EventHandler action)
        {
            var button = new Button
            {
                Text = label, AutoSize = true, MinimumSize = new Size(82, 34),
                FlatStyle = FlatStyle.Flat, ForeColor = Color.White,
                BackColor = Color.FromArgb(48, 57, 72), Margin = new Padding(3)
            };
            button.Click += action;
            bar.Controls.Add(button);
            return button;
        }
        bar.Controls.Add(new Label { Text = "图片", ForeColor = Color.White, AutoSize = true, Margin = new Padding(5, 10, 2, 2) });
        var initialSelection = ResolveInitialPreviewSelection();
        _preferredImageMode=initialSelection.Image;_imageMode.Items.AddRange(["原图", "译图（实验·生成中）"]);_suppressImageModeEvent=true;_imageMode.SelectedIndex=(int)_preferredImageMode;_suppressImageModeEvent=false;
        _imageMode.SelectedIndexChanged += (_, _) => {if(!_suppressImageModeEvent)SetPreferredImageMode((ImageViewMode)Math.Max(0,_imageMode.SelectedIndex),"UserSelection",true);};bar.Controls.Add(_imageMode);
        bar.Controls.Add(new Label { Text = "文本", ForeColor = Color.White, AutoSize = true, Margin = new Padding(8, 10, 2, 2) });
        RebuildTextModeItems(initialSelection.Text);
        _textMode.SelectedIndexChanged += (_, _) => {_selectedTextMode=TextModeFromVisibleIndex();if(!_atomicCommitInProgress)UpdateIndependentViews();}; bar.Controls.Add(_textMode);
        _textMode.SelectionChangeCommitted += (_, _) => RememberCurrentPreviewSelection();
        _ocrEngineSelector.Items.AddRange(["RapidOCR（快速）", "PaddleOCR（高质量）", "Windows OCR（实验 / 回退）"]);
        _settings.OcrEngine = initialSelection.Engine;
        _ocrEngineSelector.SelectedIndex = OcrEngineUiMapping.ToSelectedIndex(initialSelection.Engine);
        _ocrEngineSelector.SelectionChangeCommitted += (_, _) =>
            HandleOcrEngineSelectionCommitted();
        bar.Controls.Add(_ocrEngineSelector);_fullModeControls.Add(_ocrEngineSelector);
        Add("重新识别 OCR", (_, _) => StartOcr(false));
        _retryButton = Add("重新翻译", (_, _) => StartTranslation());
        _textVisibilityButton=Add(_settings.PreviewTextPanelVisible?"隐藏文本":"显示文本",(_,_)=>SetTextPanelVisibility(!_settings.PreviewTextPanelVisible,true));
        Button pin=null!;pin=Add(TopMost?"📌 已置顶":"📌 置顶",(_,_)=>{TopMost=!TopMost;_settings.PreviewAlwaysOnTop=TopMost;pin.Text=TopMost?"📌 已置顶":"📌 置顶";AppearanceSettingsChanged?.Invoke(ApiSettingsSnapshot.Copy(_settings));});
        var moreMenu=new ContextMenuStrip();
        void Menu(string text,EventHandler action){var item=moreMenu.Items.Add(text);item.Click+=action;}
        Menu("按当前模式重绘",async(_,_)=>await RedrawUsingCurrentModeAsync());
        Menu("编辑 OCR 区域",(_,_)=>ShowOcrBlockEditor());Menu("区域与调试图层",(_,_)=>ShowV2LayerDialog());Menu("取消翻译",(_,_)=>CancelTranslation());
        Menu("查看错误详情",(_,_)=>ShowTranslationErrorDetails());Menu("OCR 输入",(_,_)=>ShowOcrInput());Menu("译图与字体设置",(_,_)=>ShowAppearanceQuickDialog());Menu("翻译设置",(_,_)=>ShowTranslationSettings());
        Menu("复制图片",(_,_)=>CopyImage());Menu("复制当前文本",(_,_)=>CopyText());Menu("保存图片",(_,_)=>SaveImage());Menu("截取当前预览",(_,_)=>RequestSelfCapture());Menu("翻译输入追踪",(_,_)=>ShowTranslationInputTrace());Menu("重新选择",(_,_)=>{ReSelectRequested?.Invoke(this,EventArgs.Empty);Close();});
        Menu(_settings.PreviewChromeMode==PreviewChromeMode.GameCompact?"切换到完整模式":"切换到游戏简洁模式",(_,_)=>ToggleChromeMode());
        _moreButton=Add("⋯ 更多",(_,_)=>moreMenu.Show(Cursor.Position));
        var imageMenu=new ContextMenuStrip();var copySource=imageMenu.Items.Add("复制原图");copySource.Click+=(_,_)=>CopySpecificImage(_original,"已复制原图");var copyTranslated=imageMenu.Items.Add("复制译图");copyTranslated.Click+=(_,_)=>{if(_translatedDisplay is not null)CopySpecificImage(_translatedDisplay,"已复制译图");};imageMenu.Opening+=(_,_)=>copyTranslated.Enabled=_translatedDisplay is not null;imageMenu.Items.Add(new ToolStripSeparator());var saveCurrent=imageMenu.Items.Add("保存图片");saveCurrent.Click+=(_,_)=>SaveImage();_picture.ContextMenuStrip=imageMenu;_imagePanel.ContextMenuStrip=imageMenu;
        var textMenu=new ContextMenuStrip();var copyCurrentText=textMenu.Items.Add("复制文字");copyCurrentText.Click+=(_,_)=>CopyText();_text.ContextMenuStrip=textMenu;
        Button FullAdd(string text,EventHandler action){var control=Add(text,action);_fullModeControls.Add(control);return control;}
        FullAdd("编辑 OCR 区域",(_,_)=>ShowOcrBlockEditor());FullAdd("区域与调试图层",(_,_)=>ShowV2LayerDialog());FullAdd("取消翻译",(_,_)=>CancelTranslation());
        _errorDetailsButton=FullAdd("查看错误详情",(_,_)=>ShowTranslationErrorDetails());_errorDetailsButton.Enabled=false;
        FullAdd("OCR 输入",(_,_)=>ShowOcrInput());FullAdd("译图与字体设置",(_,_)=>ShowAppearanceQuickDialog());FullAdd("翻译设置",(_,_)=>ShowTranslationSettings());
        FullAdd("复制图片",(_,_)=>CopyImage());FullAdd("复制当前文本",(_,_)=>CopyText());FullAdd("保存图片",(_,_)=>SaveImage());
        FullAdd("截取当前预览",(_,_)=>RequestSelfCapture());FullAdd("翻译输入追踪",(_,_)=>ShowTranslationInputTrace());
        FullAdd("重新选择",(_,_)=>{ReSelectRequested?.Invoke(this,EventArgs.Empty);Close();});
        FullAdd("切换到游戏简洁模式",(_,_)=>ToggleChromeMode());
        _zoom.Items.AddRange(["适应窗口", "100%"]);
        _zoom.SelectedIndex = 0;
        _zoom.Margin = new Padding(9, 6, 3, 3);
        _zoom.SelectedIndexChanged += (_, _) => HandleZoomSelectionChanged();
        bar.Controls.Add(_zoom);
        bar.Controls.Add(_status);
        _imagePanel.Controls.Add(_picture);
        _imagePanel.Resize += (_, _) => UpdateImageLayout();_imagePanel.MouseWheel+=Picture_MouseWheel;_imagePanel.MouseDown+=Picture_MouseDown;_imagePanel.MouseMove+=PicturePan_MouseMove;_imagePanel.MouseUp+=Picture_MouseUp;
        _split.Panel1.Controls.Add(_imagePanel);
        _split.Panel2.Controls.Add(_text);
        Controls.Add(_split);
        Controls.Add(bar);
        ApplyChromeMode();
        UpdateIndependentViews();
    }

    private void ToggleChromeMode()
    {
        _settings.PreviewChromeMode=_settings.PreviewChromeMode==PreviewChromeMode.GameCompact?PreviewChromeMode.Full:PreviewChromeMode.GameCompact;
        ApplyChromeMode();
        AppearanceSettingsChanged?.Invoke(ApiSettingsSnapshot.Copy(_settings));
    }

    private void ApplyChromeMode()
    {
        var full=_settings.PreviewChromeMode==PreviewChromeMode.Full;
        var selected=SelectedTextMode;RebuildTextModeItems(selected);
        if(_moreButton is not null)_moreButton.Visible=!full;
        foreach(var control in _fullModeControls)control.Visible=full;
        PerformLayout();
    }

    private TextViewMode TextModeFromVisibleIndex()=>_textMode.SelectedIndex switch{1=>TextViewMode.Organized,2=>TextViewMode.Translation,_=>TextViewMode.RawOcr};
    private TextViewMode SelectedTextMode=>_selectedTextMode;
    private void RebuildTextModeItems(TextViewMode requested)
    {
        _selectedTextMode=requested;
        _textMode.BeginUpdate();_textMode.Items.Clear();
        _textMode.Items.AddRange(["OCR 原文","整理后原文","译文"]);_textMode.SelectedIndex=requested switch{TextViewMode.Organized=>1,TextViewMode.Translation=>2,_=>0};
        _textMode.EndUpdate();_selectedTextMode=requested;
    }
    private void SelectTextMode(TextViewMode mode){if(mode==TextViewMode.Hidden){SetTextPanelVisibility(false,false);return;}_selectedTextMode=mode;_textMode.SelectedIndex=mode switch{TextViewMode.Organized=>1,TextViewMode.Translation=>2,_=>0};_selectedTextMode=mode;}
    private void SetTextPanelVisibility(bool visible,bool notify){_settings.PreviewTextPanelVisible=visible;if(_textVisibilityButton is not null)_textVisibilityButton.Text=visible?"隐藏文本":"显示文本";UpdateIndependentViews();if(notify)AppearanceSettingsChanged?.Invoke(ApiSettingsSnapshot.Copy(_settings));}
    internal void ApplyTextPanelVisibility(bool visible)=>SetTextPanelVisibility(visible,false);

    private void ShowV2LayerDialog()
    {
        using var dialog = new Form { Text = UiStrings.RegionLayers, StartPosition = FormStartPosition.CenterParent,
            Size = new Size(390, 430), MinimizeBox = false, MaximizeBox = false };
        var list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true };
        foreach (var layer in Enum.GetValues<RegionOverlayLayer>()) list.Items.Add(new RegionLayerOption(layer), _visibleV2Layers.Contains(layer));
        var apply = new Button { Text = UiStrings.Apply, DialogResult = DialogResult.OK, Dock = DockStyle.Bottom, Height = 38 };
        dialog.Controls.Add(list); dialog.Controls.Add(apply); dialog.AcceptButton = apply;
        if (ShowPreviewDialog(dialog) != DialogResult.OK) return;
        _visibleV2Layers.Clear(); foreach (RegionLayerOption option in list.CheckedItems) _visibleV2Layers.Add(option.Layer);
        _picture.Invalidate();
    }

    private void StartOcr(bool translateAfter, bool continueOperation=false) => _ = StartOcrWhenSnapshotReadyAsync(translateAfter,continueOperation);

    private async Task StartOcrWhenSnapshotReadyAsync(bool translateAfter, bool continueOperation=false)
    {
        if(!CanUse())return;
        if(!continueOperation || _processingTask is null)BeginProcessingOperation("OCR");
        var trace=_processingTask!;
        try
        {
            using(trace.Timer.Stage("OCR Snapshot Preparation"))
                if (!await EnsureOcrSnapshotAsync()) return;
            if(!ReferenceEquals(trace,_processingTask))return;
            StartOcrCore(translateAfter);
        }
        catch (Exception ex)
        {
            if (CanUse()) SetStatus($"OCR快照创建失败：{ex.Message}");
            AppLog.Write("post-confirm", "Unable to prepare isolated OCR snapshot", ex);
        }
    }

    private async Task<bool> EnsureOcrSnapshotAsync()
    {
        if (_ocrSource is not null && _imageIdentity is not null) return true;
        if (!CanUse()) return false;
        var source = _original;
        if (_ocrSnapshotTask is null)
        {
            PostConfirmPerformanceTrace.Write("OcrSnapshotStart",new{Resolution=$"{source.Width}x{source.Height}",Bytes=(long)source.Width*source.Height*4,ThreadId=Environment.CurrentManagedThreadId});
            _ocrSnapshotTask = Task.Run(() =>
            {
                var copy=Stopwatch.StartNew();
                var snapshot=new Bitmap(source);
                copy.Stop();
                PostConfirmPerformanceTrace.Write("FullImageCopy",new{Operation="OcrIsolationSnapshot",Resolution=$"{source.Width}x{source.Height}",Bytes=(long)source.Width*source.Height*4,ThreadId=Environment.CurrentManagedThreadId,DurationMs=copy.Elapsed.TotalMilliseconds});
                return snapshot;
            });
        }
        var completed = await _ocrSnapshotTask;
        if (!CanUse()) { completed.Dispose(); return false; }
        if (_ocrSource is null)
        {
            _ocrSource=completed;
            _imageIdentity=new SessionImageIdentity(completed);
            if(CursorCaptureStageDump.Enabled)CursorCaptureStageDump.RecordOcrSnapshot(completed);
            _allowUiImageDisplay=true;
            PostConfirmPerformanceTrace.Write("OcrSnapshotEnd",new{Resolution=$"{completed.Width}x{completed.Height}",ThreadId=Environment.CurrentManagedThreadId});
            UpdateIndependentViews(rebuildText:false);
        }
        return true;
    }

    private void StartOcrCore(bool translateAfter)
    {
        if (!CanUse()) return;
        InvalidateTranslationRequest("ocr-start");
        var previousOperation = _ocrOperation;
        var previousTask = _ocrTask;
        previousOperation?.Cancel();
        if (previousOperation is not null)
        {
            if (previousTask is null || previousTask.IsCompleted) previousOperation.Dispose();
            else _ = previousTask.ContinueWith(_ => previousOperation.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        _ocrOperation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var generation = ++_ocrGeneration;
        var requestId = Guid.NewGuid().ToString("N");
        if (_wallClock is null || _wallClock.EndedAt is not null) _wallClock = new WallClockEndToEndTimer(requestId);
        _wallClock.Metadata["RecognitionRequestId"] = requestId; _finalPreviewReady = false;
        _currentOcrRequestId = requestId;
        var engine = _settings.OcrEngine;
        var image=_ocrSource!;var identity=_imageIdentity!;
        PostConfirmPerformanceTrace.Write("RunOcrAsyncStart",new{ThreadId=Environment.CurrentManagedThreadId,Resolution=$"{image.Width}x{image.Height}"});
        var runOcrPrefix=Stopwatch.StartNew();
        _ocrTask = RunOcrAsync(engine, requestId, generation, translateAfter, _ocrOperation.Token, _wallClock,image,identity);
        runOcrPrefix.Stop();PostConfirmPerformanceTrace.Write("RunOcrSyncPrefix",new{RunOcrSyncPrefixMs=runOcrPrefix.Elapsed.TotalMilliseconds});
    }

    private void HandleOcrEngineSelectionCommitted()
    {
        _settings.OcrEngine = OcrEngineUiMapping.FromSelectedIndex(_ocrEngineSelector.SelectedIndex);
        RememberCurrentPreviewSelection();
        StartOcr(false);
    }

    private async Task RunOcrAsync(OcrEngineKind engine, string requestId, long generation,
        bool translateAfter, CancellationToken token, WallClockEndToEndTimer? timing,Bitmap image,SessionImageIdentity identity)
    {
        using var ocrAndCoreStage=timing?.Stage("OCR and Core Build");
        var ocrSucceeded=false;
        var syncPrefix=Stopwatch.StartNew();
        await Task.Yield();
        syncPrefix.Stop();PostConfirmPerformanceTrace.FirstAsyncYield(syncPrefix.Elapsed.TotalMilliseconds);
        SetStatus($"正在识别 {engine} {image.Width}×{image.Height} 原始截图…");RealExeE2ETrace.Mark("T7 OCRQueued");
        try
        {
            var modelHint = engine switch { OcrEngineKind.Paddle => "PP-OCRv6_medium",
                OcrEngineKind.Rapid => "RapidOCR-PP-OCRv6", _ => "Windows.Media.Ocr" };
            var imageHash=await identity.GetHashAsync(token);
            PostConfirmPerformanceTrace.ImageHash(identity.Diagnostics);
            var cacheKey = SessionServices.CreateOcrKey(imageHash,engine,modelHint);
            OcrEngineResult engineResult;
            // Recognition/visual diagnostics belong to one OCR generation only.
            // An OFF run must never display or expose a previous diagnostic run.
            _recognitionV2 = null;
            _visualAnalysisV2 = null;
            if (_settings.VisualModel != VisualModelKind.Off)
            {
                _recognitionRunCount++;
                using var recognitionStage = timing?.Stage("Recognition Pipeline");
                var v2 = await Task.Run(()=>_recognitionPipelineV2.RunAsync(image,_settings,generation,token),token);
                engineResult = v2.Ocr; engineResult.RequestId = requestId; engineResult.ImageSessionId = _imageSessionId;
                _lastOcrResult=engineResult;
                timing?.Metadata.TryAdd("OCR", new { engineResult.EngineActual, engineResult.TotalMilliseconds, engineResult.ModelLoadMilliseconds, engineResult.WorkerPid, engineResult.SendMilliseconds, engineResult.ReceiveMilliseconds, engineResult.PreprocessRetryCount });
                _recognitionV2 = v2.Document; _visualAnalysisV2 = v2.Visual;
            }
            else if (_settings.OcrCacheEnabled && _session.TryGetOcr(cacheKey, out var cached))
            {
                engineResult = cached;
                engineResult.RequestId = requestId;
                engineResult.ImageSessionId = _imageSessionId;
                engineResult.EngineRequested = engine;
            }
            else
            {
                using var ocrStage=timing?.Stage("OCR Worker Roundtrip");
                engineResult = await Task.Run(()=>_ocrRuntimeManager.RecognizeWithFallbackAsync(
                    engine,image,_settings.OcrLanguage,token,requestId,imageHash,_imageSessionId,ocrLoad:_processingTask?.OcrLoad ?? _settings.OcrLoad),token);
                if (_settings.OcrCacheEnabled) _session.PutOcr(cacheKey, engineResult);
            }
            if (token.IsCancellationRequested || !IsCurrentOcrRequest(engine, requestId, generation) || !CanUse()) return;
            RealExeE2ETrace.Mark("T8 OCRCompleted");
            timing?.Metadata.TryAdd("OCR",new{engineResult.EngineActual,engineResult.WorkerPid,engineResult.TotalMilliseconds,engineResult.ModelLoadMilliseconds});
            var previousTranslation = _document?.FullTranslation ?? "";
            var previousSource = _document?.Text ?? "";
            _document?.DebugImage?.Dispose();
            using(timing?.Stage("Core Grouping"))_corePipelineV2 = BuildCorePipelineV2(engineResult, image);
            _processingProvenance=HistoryRegenerationPolicy.FromOcr(imageHash);
            _historyRequiresReOcr=false;_historyViewOnly=false;_historySnapshot=null;
            _recognitionV2 = null;
            _document = BuildCoreProductDocument(_corePipelineV2, engineResult);
            _translationSemanticGroups = [];
            _structuredTextGroups = [];
            if (!string.IsNullOrWhiteSpace(previousTranslation))
            {
                _document.FullTranslation = previousTranslation;
                _document.TranslationStale = !string.Equals(previousSource, _document.Text, StringComparison.Ordinal);
            }
            DiagnosticOcrCompleted?.Invoke(engineResult);
            _displayedOcrEngine = engineResult.EngineActual;
            if (!string.IsNullOrWhiteSpace(engineResult.FallbackReason)) SetStatus(engineResult.FallbackReason);
            BuildOriginalDisplay();
            if (!_initialViewApplied) { ApplyDefaultViews(); _initialViewApplied = true; }
            else UpdateIndependentViews();
            var visionText = _visualAnalysisV2 is null ? "Vision=Off" : $"Vision={_visualAnalysisV2.ModelDiagnostics.Model}/{_visualAnalysisV2.ModelDiagnostics.ElapsedMs}ms";
            if (_recognitionV2?.Diagnostics is { } diagnostics)
            {
                visionText += $" OCR={diagnostics.OcrMs}ms Fusion={diagnostics.FusionMs}ms Grouping={diagnostics.GroupingMs}ms Role={diagnostics.RoleMs}ms Recognition={diagnostics.TotalMs}ms Blocks={diagnostics.OcrBlockCount} Units={diagnostics.TranslationUnitCount}";
                timing?.Metadata.TryAdd("Recognition", diagnostics);
                timing?.Metadata.TryAdd("Vision", _visualAnalysisV2?.ModelDiagnostics);
            }
            SetStatus($"识别完成 {visionText} OCR={engineResult.EngineActual} Request={requestId[..8]} | {_document.Groups.Count} Region | {_document.Metrics}");
            ocrSucceeded=true;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (!IsCurrentOcrRequest(engine, requestId, generation) || !CanUse()) return;
            SetStatus("识别失败");
            if(DiagnosticOcrFailure is { } diagnosticFailure){diagnosticFailure(ex);return;}
            if (Environment.GetEnvironmentVariable("SCREENSHOT_TRANSLATOR_V2_FROZEN_TEST") == "1")
                AppLog.Write("frozen-test", "OCR recognition failed", ex);
            else AppDialog.Show(this, "OCR 识别失败", AppDialog.UserFacingError(ex), AppDialogKind.Warning, ex.Message);
            return;
        }
        finally
        {
            if(!ocrSucceeded && timing is not null)
            {
                ocrAndCoreStage?.Dispose();
                timing.Metadata["Outcome"]=token.IsCancellationRequested?"OCR canceled":"OCR failed or superseded";
                timing.Complete(Path.Combine(AppDataPaths.ProductLogsRoot,$"wallclock-{timing.OperationId}.json"));
            }
            if (IsCurrentOcrRequest(engine, requestId, generation))
            {
                _ocrOperation?.Dispose();
                _ocrOperation = null;
            }
        }
        ocrAndCoreStage?.Dispose();
        if (translateAfter && IsCurrentOcrRequest(engine, requestId, generation) && CanUse()) StartTranslation(continueOcrOperation:true);
        else if (!translateAfter && ReferenceEquals(timing, _wallClock)) CompleteWallClock();
    }

    private bool IsCurrentOcrRequest(OcrEngineKind engine, string requestId, long generation) =>
        generation == _ocrGeneration && requestId == _currentOcrRequestId && engine == _settings.OcrEngine;

    private static OcrDocument BuildDocument(OcrEngineResult result, Size sourceSize, CleanupStrength cleanup)
    {
        var rawLines = result.Blocks.OrderBy(x => x.ReadingOrder).Select(x => new OcrRegion
        {
            Id = x.Id,
            Text = string.IsNullOrWhiteSpace(x.CorrectedText) ? x.RawText : x.CorrectedText,
            RawText = x.RawText,
            Bounds = x.BoundingBox,
            Confidence = x.Confidence ?? 0,
            ReadingOrder = x.ReadingOrder,
            Language = result.EngineActual.ToString(),
            RawLines = []
        }).ToList();
        foreach (var line in rawLines) line.RawLines = [new OcrRegion
        {
            Id = line.Id, Text = line.Text, RawText = line.RawText, Bounds = line.Bounds,
            Confidence = line.Confidence, ReadingOrder = line.ReadingOrder, Language = line.Language
        }];
        var organized = OcrService.BuildOrganizedRegions(rawLines, sourceSize, cleanup);
        return new OcrDocument
        {
            Text = string.Join(Environment.NewLine + Environment.NewLine, organized.Select(x => x.Text)),
            RawText = result.RawText,
            RawLines = rawLines,
            Regions = organized,
            Groups = OcrService.BuildSegmentGroups(organized),
            OriginalSnapshot = OcrService.CreateSnapshot(rawLines, result.EngineActual.ToString()),
            SegmentMappingReliable = true,
            SourceWidth = sourceSize.Width,
            SourceHeight = sourceSize.Height,
            SelectedCandidate = result.ModelName,
            Metrics = new TimingMetrics { OcrMs = result.TotalMilliseconds, TotalMs = result.TotalMilliseconds }
        };
    }

    private static CorePipelineDocument BuildCorePipelineV2(OcrEngineResult result, Bitmap source)
    {
        var raw=BuildCorePipelineV2(result,source.Size).RawLines;
        return CorePipelineEngine.Analyze(source.Size,raw,sourceImage:source);
    }
    private static CorePipelineDocument BuildCorePipelineV2(IEnumerable<OcrRegion> regions, Bitmap source)
    {
        var raw=BuildCorePipelineV2(regions,source.Size).RawLines;
        return CorePipelineEngine.Analyze(source.Size,raw,sourceImage:source);
    }
    private static CorePipelineDocument BuildCorePipelineV2(OcrEngineResult result, Size sourceSize)
    {
        var raw = result.Blocks.Where(x => x.Enabled &&
                (!string.IsNullOrWhiteSpace(x.CorrectedText) || !string.IsNullOrWhiteSpace(x.RawText)))
            .OrderBy(x => x.ReadingOrder)
            .Select(x => new RawOcrLine(
                x.Id, x.RawText, string.IsNullOrWhiteSpace(x.CorrectedText) ? x.RawText : x.CorrectedText,
                x.Polygon.Length >= 3 ? x.Polygon.ToArray() :
                    [new(x.BoundingBox.Left,x.BoundingBox.Top),new(x.BoundingBox.Right,x.BoundingBox.Top),
                     new(x.BoundingBox.Right,x.BoundingBox.Bottom),new(x.BoundingBox.Left,x.BoundingBox.Bottom)],
                x.BoundingBox, x.Confidence ?? 0, x.ReadingOrder)
            { OcrRequestImageSha256 = x.OcrRequestImageSha256,
                OcrAlternatives = x.OcrAlternatives.Select(c => c.Copy()).ToArray() }).ToArray();
        return CorePipelineEngine.Analyze(sourceSize, raw);
    }

    private static CorePipelineDocument BuildCorePipelineV2(IEnumerable<OcrRegion> regions, Size sourceSize)
    {
        var raw = regions.Where(region => !string.IsNullOrWhiteSpace(region.Text))
            .OrderBy(region => region.ReadingOrder)
            .Select(region =>
            {
                var bounds = region.Bounds;
                PointF[] polygon = [new(bounds.Left,bounds.Top),new(bounds.Right,bounds.Top),
                    new(bounds.Right,bounds.Bottom),new(bounds.Left,bounds.Bottom)];
                return new RawOcrLine(region.Id, region.RawText, region.Text, polygon, bounds,
                    region.Confidence, region.ReadingOrder);
            }).ToArray();
        return CorePipelineEngine.Analyze(sourceSize, raw);
    }

    private static OcrDocument BuildCoreProductDocument(CorePipelineDocument core, OcrEngineResult result)
    {
        var raw = core.NormalizedLines.Select(x => new OcrRegion
        {
            Id=x.SourceId, Text=x.SourceText, RawText=x.SourceText, Bounds=x.Bounds,
            Confidence=x.Confidence, ReadingOrder=x.ReadingOrder, Language=result.EngineActual.ToString()
        }).ToList();
        var groups = core.VisualBlocks.Select((block,index) => new SegmentGroup
        {
            GroupId=block.BlockId, GroupType=SegmentType.Body,
            SourceSegmentIds=block.Lines.Select(x=>x.SourceId).ToArray(),
            OriginalText=block.SourceText, OrganizedText=block.SourceText,
            Bounds=block.Bounds, RenderRectangle=block.Bounds,
            ReadingOrder=index, CanOverlay=block.TextSelection==TextSelectionAction.Translate
        }).ToList();
        return new OcrDocument
        {
            Text=string.Join(Environment.NewLine+Environment.NewLine,groups.Select(x=>x.OrganizedText)),
            RawText=result.RawText, RawLines=raw, Regions=raw, Groups=groups,
            SourceWidth=core.Canvas.Width, SourceHeight=core.Canvas.Height,
            SelectedCandidate=$"{result.ModelName} + Core Pipeline V2", SegmentMappingReliable=true,
            Metrics=new TimingMetrics{OcrMs=result.TotalMilliseconds,TotalMs=result.TotalMilliseconds}
        };
    }

    private static OcrDocument BuildDocumentV2(RecognitionDocumentV2 v2, OcrEngineResult result, Size sourceSize)
    {
        var raw = v2.Regions.Where(r => !string.IsNullOrWhiteSpace(r.OcrText)).Select(r => new OcrRegion { Id = r.RegionId,
            Text = r.StructuredText, RawText = r.OcrText, Bounds = r.BoundingBox, Confidence = r.RecognitionConfidence,
            ReadingOrder = r.ReadingOrder, Language = result.EngineActual.ToString() }).ToList();
        var groups = v2.TranslationUnits.Select(unit =>
        {
            var members = v2.Regions.Where(r => unit.RegionIds.Contains(r.RegionId)).ToArray();
            var bounds = members.Select(r => r.BoundingBox).DefaultIfEmpty(RectangleF.Empty).Aggregate(RectangleF.Union);
            return new SegmentGroup { GroupId = unit.Id, GroupType = ToSegmentType(unit.RoleType),
                SourceSegmentIds = unit.RegionIds, OriginalText = unit.Text, OrganizedText = unit.Text,
                Bounds = bounds, RenderRectangle = bounds, ReadingOrder = unit.ReadingOrder,
                CanOverlay = !unit.PreserveOriginal };
        }).ToList();
        return new OcrDocument { Text = string.Join(Environment.NewLine + Environment.NewLine, groups.Select(x => x.OrganizedText)),
            RawText = result.RawText, RawLines = raw, Regions = raw, Groups = groups, SourceWidth = sourceSize.Width,
            SourceHeight = sourceSize.Height, SelectedCandidate = $"{result.ModelName} + Region V2", SegmentMappingReliable = true,
            Metrics = new TimingMetrics { OcrMs = result.TotalMilliseconds, MergeMs = 0,
                LayoutMs = 0, TotalMs = result.TotalMilliseconds } };
    }

    private static SegmentType ToSegmentType(RegionRoleType role) => role switch
    {
        RegionRoleType.Title => SegmentType.Title, RegionRoleType.CharacterName => SegmentType.CharacterInfo,
        RegionRoleType.Dialogue or RegionRoleType.Narration => SegmentType.Dialogue,
        RegionRoleType.Metadata or RegionRoleType.Header => SegmentType.Time,
        RegionRoleType.Button or RegionRoleType.Choice => SegmentType.Button,
        RegionRoleType.Caption => SegmentType.ImageCaption, RegionRoleType.UILabel => SegmentType.Label,
        _ => SegmentType.Body
    };

    private void ShowOcrBlockEditor()
    {
        if (_recognitionV2 is not null)
        {
            using var editor = new RecognitionRegionEditorForm(_original, _recognitionV2.Regions, RunRegionOnlyOcrAsync, RunRegionOnlyVisualAsync);
            if (ShowPreviewDialog(editor) != DialogResult.OK) return;
            InvalidateTranslationRequest("region-editor-core-rebuild");
            _recognitionV2.Regions.Clear(); _recognitionV2.Regions.AddRange(editor.ResultRegions.OrderBy(x => x.ReadingOrder));
            var editedRegionCount=_recognitionV2.Regions.Count;
            _recognitionV2.TranslationUnits.Clear(); _recognitionV2.TranslationUnits.AddRange(TranslationUnitBuilderV2.Build(_recognitionV2.Regions));
            var editedRegions = _recognitionV2.Regions.OrderBy(x => x.ReadingOrder).Select(region => new OcrRegion
            {
                Id=region.RegionId,Text=region.StructuredText,RawText=region.OcrText,Bounds=region.BoundingBox,
                Confidence=region.RecognitionConfidence,ReadingOrder=region.ReadingOrder,Language=_displayedOcrEngine.ToString()
            }).ToArray();
            _corePipelineV2=BuildCorePipelineV2(editedRegions,_original);
            var result=new OcrEngineResult{EngineActual=_displayedOcrEngine,ModelName="Core V2 Region Editor",
                RawText=string.Join(Environment.NewLine,editedRegions.Select(x=>x.Text))};
            _document?.DebugImage?.Dispose(); _document=BuildCoreProductDocument(_corePipelineV2,result);
            _recognitionV2=null;_historyRequiresReOcr=false;_historyViewOnly=false;_historySnapshot=null;
            _processingProvenance=HistoryRegenerationPolicy.FromOcr(SessionServices.ComputeImageHash(_original),true);
        foreach(var block in _corePipelineV2.VisualBlocks)block.PreserveExplicitLineBreaks=true;
            DocumentState.ClearTranslations(_document);
            _translationSemanticGroups = []; _structuredTextGroups = []; _translatedDisplay?.Dispose(); _translatedDisplay = null;
            BuildOriginalDisplay(); UpdateIndependentViews(); _picture.Invalidate(); SetStatus($"Region Editor V2 已应用：{editedRegionCount} Region；Core 已重建");
            return;
        }
        if (_document is null || _document.RawLines.Count == 0) { SetStatus("尚无可编辑的OCR区域"); return; }
        using var dialog = new Form { Text = "编辑 OCR 区域", StartPosition = FormStartPosition.CenterParent,
            Size = new Size(920, 560), MinimumSize = new Size(720, 440), Font = Font };
        var grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect };
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Enabled", HeaderText = "启用", Width = 58 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Order", HeaderText = "顺序", Width = 64 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Text", HeaderText = "文字", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Confidence", HeaderText = "置信度", Width = 82, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Bounds", HeaderText = "坐标", Width = 190, ReadOnly = true });
        foreach (var line in _document.RawLines.OrderBy(x => x.ReadingOrder))
        {
            var index = grid.Rows.Add(true, line.ReadingOrder, line.Text, line.Confidence.ToString("0.000"),
                $"{line.Bounds.X:0},{line.Bounds.Y:0} {line.Bounds.Width:0}×{line.Bounds.Height:0}");
            grid.Rows[index].Tag = line;
            if (line.Confidence > 0 && line.Confidence < .65f) grid.Rows[index].DefaultCellStyle.BackColor = Color.MistyRose;
        }
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft };
        var apply = new Button { Text = "应用", DialogResult = DialogResult.OK, Width = 100, Height = 34 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 100, Height = 34 };
        buttons.Controls.AddRange([apply, cancel]); dialog.Controls.Add(grid); dialog.Controls.Add(buttons);
        dialog.AcceptButton = apply; dialog.CancelButton = cancel;
        if (ShowPreviewDialog(dialog) != DialogResult.OK) return;
        var edited = new List<OcrRegion>();
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.Tag is not OcrRegion source || row.Cells["Enabled"].Value is not true) continue;
            var text = Convert.ToString(row.Cells["Text"].Value)?.Trim() ?? ""; if (text.Length == 0) continue;
            var order = int.TryParse(Convert.ToString(row.Cells["Order"].Value), out var parsed) ? parsed : source.ReadingOrder;
            edited.Add(new OcrRegion { Id = source.Id, Text = text, RawText = text, Bounds = source.Bounds,
                Confidence = source.Confidence, Language = source.Language, ReadingOrder = order, RawLines = [] });
        }
        edited = edited.OrderBy(x => x.ReadingOrder).ToList();
        foreach (var line in edited) line.RawLines = [new OcrRegion { Id = line.Id, Text = line.Text, RawText = line.Text,
            Bounds = line.Bounds, Confidence = line.Confidence, Language = line.Language, ReadingOrder = line.ReadingOrder }];
        var priorIds=_corePipelineV2?.VisualBlocks.Select(x=>x.BlockId).ToArray()??[];
        _corePipelineV2=BuildCorePipelineV2(edited,_original);
        var editedResult=new OcrEngineResult{EngineActual=_displayedOcrEngine,ModelName="Core V2 OCR Editor",
            RawText=string.Join(Environment.NewLine,edited.Select(x=>x.Text))};
        _document.DebugImage?.Dispose();_document=BuildCoreProductDocument(_corePipelineV2,editedResult);
        DocumentState.ClearTranslations(_document); _translationSemanticGroups = []; _structuredTextGroups = [];
        _historyRequiresReOcr=false;_historyViewOnly=false;_historySnapshot=null;
        _processingProvenance=HistoryRegenerationPolicy.FromOcr(SessionServices.ComputeImageHash(_original),true);
        foreach(var block in _corePipelineV2.VisualBlocks)block.PreserveExplicitLineBreaks=true;
        InvalidateTranslationRequest("ocr-editor-core-rebuild");
        _translatedDisplay?.Dispose(); _translatedDisplay = null;
        BuildOriginalDisplay(); UpdateIndependentViews(); SetStatus($"OCR编辑已应用：{edited.Count}个区域；Core 已重建；Block {priorIds.Length}→{_corePipelineV2.VisualBlocks.Count}");
    }

    private async Task<IReadOnlyList<OcrEngineBlock>> RunRegionOnlyOcrAsync(RecognitionRegion region, CancellationToken token)
    {
        var box = Rectangle.Round(RectangleF.Intersect(region.BoundingBox, new RectangleF(PointF.Empty, _original.Size)));
        if (box.Width < 2 || box.Height < 2) return [];
        using var crop = _original.Clone(box, _original.PixelFormat);
        var result = await _ocrRuntimeManager.RecognizeWithFallbackAsync(_settings.OcrEngine, crop, _settings.OcrLanguage, token, allowAutomaticWindowsFallback: false, ocrLoad:_processingTask?.OcrLoad ?? _settings.OcrLoad);
        foreach (var block in result.Blocks) { block.Id = $"ROI-{region.RegionId}-{block.Id}"; block.BoundingBox = new(block.BoundingBox.X + box.X, block.BoundingBox.Y + box.Y, block.BoundingBox.Width, block.BoundingBox.Height); block.Polygon = block.Polygon.Select(p => new PointF(p.X + box.X, p.Y + box.Y)).ToArray(); }
        return result.Blocks;
    }

    private async Task<VisualRegion?> RunRegionOnlyVisualAsync(RecognitionRegion region, CancellationToken token)
    {
        var box = Rectangle.Round(RectangleF.Intersect(region.BoundingBox, new RectangleF(PointF.Empty, _original.Size)));
        if (box.Width < 2 || box.Height < 2) return null;
        using var crop = _original.Clone(box, _original.PixelFormat);
        var result = await _visionRuntimeManager.AnalyzeAsync(crop, _settings.VisualModel, _ocrGeneration, token);
        var best = result.VisualRegions.OrderByDescending(x => x.Confidence).FirstOrDefault();
        if (best is null) return null;
        return new VisualRegion { VisualRegionId = best.VisualRegionId, Polygon = best.Polygon.Select(p => new PointF(p.X + box.X, p.Y + box.Y)).ToArray(),
            VisualRoleHint = best.VisualRoleHint, Confidence = best.Confidence, ReadingOrderHint = best.ReadingOrderHint,
            ParentContainerHint = best.ParentContainerHint, SourceModel = best.SourceModel, OptionalTextHint = best.OptionalTextHint };
    }

    internal void TranslateFromWorkspace()=>StartTranslation();

    private void StartTranslation(bool continueOcrOperation=false)
    {
        if (!CanUse()) return;
        if(!continueOcrOperation || _processingTask is null)BeginProcessingOperation("Translation");
        if(_historyViewOnly)
        {
            var hash=SessionServices.ComputeImageHash(_original);
            var decision=HistoryRegenerationPolicy.Evaluate(_historySnapshot,_original.Size,hash);
            AppLog.Write("history",$"CURRENT_REGENERATION reason={decision.Reason} reocr={decision.ReOcr} blocked={decision.Blocked}");
            if(decision.Blocked){SetStatus("历史原图与手工校正记录不一致，请检查原图或使用 OCR 编辑器处理。");return;}
            if(decision.ReOcr){_historyRequiresReOcr=true;}
            else if(_historySnapshot is { } saved)
            {
                _corePipelineV2=HistoryRegenerationPolicy.RebuildForRegeneration(saved,_original.Size,_settings,Guid.NewGuid().ToString("N"),_original);
                _document?.DebugImage?.Dispose();
                _document=BuildCoreProductDocument(_corePipelineV2,new OcrEngineResult
                    {EngineActual=_settings.OcrEngine,ModelName="Current History Regeneration"});
                _processingProvenance=HistoryRegenerationPolicy.FromOcr(hash,HistoryRegenerationPolicy.HasProtectedSourceCorrection(saved));
                _historyRequiresReOcr=false;_historyViewOnly=false;
            }
        }
        if (_historyRequiresReOcr || _corePipelineV2 is null)
        {
            _historyRequiresReOcr=false;
            SetStatus("正在为当前截图重建 Core OCR 状态，完成后自动翻译…");
            StartOcr(true,continueOperation:true);
            return;
        }
        ProductControlPlaneAuthority.RequireCore(_corePipelineV2,"StartTranslation");
        if (_document is null || _document.Groups.Count == 0)
        {
            AppDialog.Show(this, "没有 OCR 原文", "请先识别文字。", AppDialogKind.Warning);
            return;
        }
        _finalTranslationTextCommitted=false;_rightTranslationFinalSetCount=0;_rightPanelWidthChangeAfterFinalText=0;
        _rightFontChangeAfterFinalText=0;_rightLayoutEventAfterFinalText=0;_rightScrollExtentChangeAfterFinalText=0;
        TranslationRequestSnapshot snapshot; using (_wallClock?.Stage("Translation Request Build")) snapshot = CreateTranslationRequestSnapshot();
        var previous = _translationTask;
        if (_busy && (previous is null || previous.IsCompleted)) return;
        _operation?.Cancel();
        _translationTask = StartTranslationAfterAsync(previous, snapshot);
    }

    private async Task StartTranslationAfterAsync(Task? previous, TranslationRequestSnapshot snapshot)
    {
        if (previous is not null && !previous.IsCompleted)
        {
            try { await previous; }
            catch { /* RunTranslationAsync reports its own result. */ }
        }
        if (!CanUse() || snapshot.RequestId != _currentTranslationRequestId) return;
        ReplaceOperation();
        TranslationService.DiagnosticLog($"translation cts request={snapshot.RequestId} state=fresh canceled={_operation!.IsCancellationRequested}");
        await RunTranslationAsync(snapshot, _operation.Token);
    }

    private async Task RunTranslationAsync(TranslationRequestSnapshot snapshot, CancellationToken token)
    {
        if (_document is null) return;
        using var operationScope=snapshot.Trace.Enter();
        var timing=snapshot.Trace.Timer;
        var requestDocument = _document;
        requestDocument.Metrics.ApiRequests=0;requestDocument.Metrics.ApiWaitMs=0;
        requestDocument.Metrics.BackgroundMs=0;requestDocument.Metrics.LayoutMs=0;
        requestDocument.Metrics.DrawMs=0;requestDocument.Metrics.EncodeMs=0;
        _translationParseMs=0;_lastCorePipelineTiming=null;_lastRenderFailedCount=0;
        timing.Metadata["ReusesPriorOcr"]=snapshot.Trace.Trigger=="Translation";
        using var productAuthority=ProductControlPlaneAuthority.Begin("Translation+Layout+Style+Cleanup+Render");
        var core=ProductControlPlaneAuthority.RequireCore(_corePipelineV2,"RunTranslationAsync");
        SetTranslationBusy(true);RealExeE2ETrace.Mark("T9 TranslationStarted");
        ClearTranslationError();
        SetStatus("正在整理段落");
        // Model loading belongs to this active request, never to idle residency.
        using var backgroundLifetime=CancellationTokenSource.CreateLinkedTokenSource(token);
        var backgroundPreparation=snapshot.Settings.ImageTranslationEnabled && snapshot.Settings.BackgroundTreatment==BackgroundTreatment.FineRepair
            ?Task.Run(()=>GeneralBackgroundRecovery.PrewarmAsync(backgroundLifetime.Token,snapshot.Settings.BackgroundComputeDevice))
            :Task.CompletedTask;
        var progress = new InlineProgress<TranslationProgress>(p=>{if(IsCurrentTranslationRequest(snapshot))HandleTranslationProgress(p);});
        string? errorStatus = null;
        string? errorDetails = null;
        try
        {
            _document.TranslationStale = true;
            UpdateIndependentViews();
            var items = snapshot.Items;
            var translationKey = snapshot.CacheKey;
            TranslationBatchResult result;
            if (snapshot.Settings.TranslationCacheEnabled && _session.TryGetTranslation(translationKey, out var cached))
            {
                LogTranslationRequest(snapshot, "cache-hit", cacheHit: true);
                result = CoreTranslationContentValidator.Revalidate(items,cached,snapshot.RequestId,snapshot.Settings.TargetLanguage);
                if(result.MissingIds.Count>0)
                {
                    var missingItems=items.Where(x=>result.MissingIds.Contains(x.Id)).ToArray();
                    SetStatus("正在更新不兼容的缓存译文");
                    var fresh=await _translateBatchAsync(missingItems,snapshot.Settings,progress,token);
                    var merged=new Dictionary<string,string>(result.Translations,StringComparer.Ordinal);
                    foreach(var entry in fresh.Translations)merged[entry.Key]=entry.Value;
                    result=CoreTranslationContentValidator.Revalidate(items,fresh with{Translations=merged},snapshot.RequestId,snapshot.Settings.TargetLanguage);
                }
                else SetStatus("翻译缓存命中，内容检查通过");
            }
            else
            {
                LogTranslationRequest(snapshot, "send", cacheHit: false);
                using var apiStage = timing.Stage("Translation API");
                result = await _translateBatchAsync(items, snapshot.Settings, progress, token);
            }
            result=CoreTranslationContentValidator.Revalidate(items,result,snapshot.RequestId,snapshot.Settings.TargetLanguage);
            if(snapshot.Settings.TranslationCacheEnabled && result.MissingIds.Count==0)
                _session.PutTranslation(translationKey,result);
            var parseWatch=Stopwatch.StartNew(); token.ThrowIfCancellationRequested();
            RealExeE2ETrace.Mark("T10 TranslationCompleted / CacheResolved");
            if(result.RecoveryStats is{} recoveryStats)
                TranslationService.DiagnosticLog($"recovery completed initial={recoveryStats.InitialRequestCount} retry={recoveryStats.RetryRequestCount} missingOnly={recoveryStats.MissingOnlyRequestCount} split={recoveryStats.SplitRequestCount} single={recoveryStats.SingleGroupRequestCount} firstByteTimeout={recoveryStats.FirstByteTimeoutCount} partial={recoveryStats.PartialResponseCount} recovered={recoveryStats.RecoveredGroupCount} finalMissing={recoveryStats.FinalMissingGroupCount} total={recoveryStats.TotalRequestCount}");
            if (!CanUse() || !IsCurrentTranslationRequest(snapshot) ||
                !ReferenceEquals(core, _corePipelineV2) || !ReferenceEquals(requestDocument, _document))
            {
                LogTranslationRequest(snapshot, "stale-result-discarded", cacheHit: false);
                return;
            }
            if (ReferenceEquals(core,_corePipelineV2))
            {
                foreach(var block in _corePipelineV2.VisualBlocks)
                {
                    var state=_corePipelineV2.Translations[block.BlockId];
                    if(block.TextSelection==TextSelectionAction.Preserve)
                    { state.TranslatedText=block.SourceText;state.State=BlockTranslationState.Preserved;state.FailureReason=block.TextSelectionReason; }
                    else if(result.Translations.TryGetValue(block.BlockId,out var translated)&&
                            !string.IsNullOrWhiteSpace(translated)&&
                            CorePipelineEngine.NumericTokensMatch(block.SourceText,translated))
                    { state.TranslatedText=translated.Trim();state.State=BlockTranslationState.Accepted;state.FailureReason=""; }
                    else
                    {
                        state.TranslatedText="";state.State=BlockTranslationState.Failed;
                        state.FailureReason=string.IsNullOrWhiteSpace(translated)
                            ?"MISSING_BLOCK_TRANSLATION":"NUMERIC_FIDELITY_MISMATCH";
                    }
                    if(state.State==BlockTranslationState.Accepted)
                        TranslationBoundaryDiagnostics.Record("AcceptedMapping",snapshot.RequestId,block.BlockId,state.TranslatedText);
                }
                foreach(var group in _document.Groups)
                { group.Translation=result.Translations.GetValueOrDefault(group.GroupId,"");group.HasReliableTranslation=!string.IsNullOrWhiteSpace(group.Translation); }
                _document.FullTranslation=result.FullTranslation;_document.SegmentMappingReliable=result.SegmentMappingReliable;_document.TranslationStale=false;
            }
            _translationSemanticGroups = snapshot.SemanticGroups;
            _structuredTextGroups = snapshot.StructuredGroups;
            _translationByUnitId=new Dictionary<string,string>(result.Translations,StringComparer.Ordinal);
            _document.Metrics.ApiRequests = Math.Max(_document.Metrics.ApiRequests, result.RequestCount);
            _document.Metrics.ApiWaitMs = Math.Max(_document.Metrics.ApiWaitMs, result.WaitMs);
            RecalculateTotalTime();
            RealExeE2ETrace.Mark("T11 StructureCompleted");
            parseWatch.Stop(); _translationParseMs=parseWatch.ElapsedMilliseconds; _missingRecoveryTriggered=result.RequestCount>1;
            var missing = result.MissingIds.Count;
            PublishTranslationTextBeforeRenderer(snapshot);
            SetStatus("正在恢复背景");
            bool renderCommitted;
            using (timing.Stage("Renderer")) renderCommitted = await BuildTranslatedDisplayResponsiveAsync(token);
            if (!renderCommitted || !CanUse() || !IsCurrentTranslationRequest(snapshot) ||
                !ReferenceEquals(core, _corePipelineV2) || !ReferenceEquals(requestDocument, _document))
            {
                LogTranslationRequest(snapshot, "stale-render-discarded", cacheHit: false);
                return;
            }
            RealExeE2ETrace.Mark("T15 FinalBitmapReady");
            RealExeE2ETrace.Mark("T16 FinalUiCommitBegin");
            using (timing.Stage("Final UI Commit")) CommitFinalPreviewAtomically();
            RealExeE2ETrace.Mark("T17 FinalUiCommitEnd");
            timing.MarkFinalResultReady();
            var missingText = result.MissingIds.Count == 0
                ? ""
                : $"，缺少ID：{string.Join("、", result.MissingIds)}";
            SetStatus($"{CompletionStatus(missing)}｜{_document.Metrics}");
            using(timing.Stage("History Save"))
            _session.AddHistory(_original, snapshot.Settings.OcrEngine, _document.Text, _document.FullTranslation,
                snapshot.Settings.TargetLanguage, snapshot.Settings, _translatedDisplay,HistoryCoreSnapshot.Capture(core,
                    (_processingProvenance ?? new CoreProcessingProvenance()) with {
                        BackgroundExecution=snapshot.Trace.FinalIdentity, BackgroundTreatment=snapshot.Trace.Treatment,
                        RenderStrategyVersion=snapshot.Trace.Treatment==BackgroundTreatment.Lightweight?LightweightBackgroundRecovery.StrategyVersion:"fix3-fine",
                        ContentContract=CoreTranslationContentValidator.ContractVersion,
                        TranslationContext=HistoryRegenerationPolicy.TranslationContext(snapshot.Settings)}));
            LogTranslationRequest(snapshot, "completed", cacheHit: false);

        }
        catch(TranslationRecoveryFailedException ex)
        {
            LogTranslationRequest(snapshot,"recovery-exhausted",cacheHit:false,ex);
            errorStatus=$"翻译响应不完整，自动恢复仍未成功。缺失 {ex.RemainingMissingIds.Count} 个翻译组。你可以点击“重新翻译”再次尝试。";
            errorDetails=ex.ToString();
        }
        catch (TranslationFirstByteTimeoutException ex)
        {
            LogTranslationRequest(snapshot, "first-byte-timeout", cacheHit: false, ex);
            errorStatus = "等待首字节超时";
            errorDetails = ex.ToString();
        }
        catch (TranslationConnectionTimeoutException ex)
        {
            LogTranslationRequest(snapshot, "connection-timeout", cacheHit: false, ex);
            errorStatus = "连接服务器超时";
            errorDetails = ex.ToString();
        }
        catch (TranslationTimeoutException ex)
        {
            LogTranslationRequest(snapshot, "request-timeout", cacheHit: false, ex);
            errorStatus = "完整翻译流程超时";
            errorDetails = ex.ToString();
        }
        catch (OperationCanceledException)
        {
            LogTranslationRequest(snapshot, "canceled", cacheHit: false);
            TranslationService.DiagnosticLog($"RunTranslationAsync catch cancellation disposed={IsDisposed} disposing={Disposing} tokenCanceled={token.IsCancellationRequested}");
            errorStatus = "用户取消";
        }
        catch (HttpRequestException ex) when (IsRemoteDisconnect(ex))
        {
            errorStatus = "翻译连接被远端中断，请点击重新翻译。";
            errorDetails = ex.ToString();
        }
        catch (IOException ex) when (IsRemoteDisconnect(ex))
        {
            errorStatus = "翻译连接被远端中断，请点击重新翻译。";
            errorDetails = ex.ToString();
        }
        catch (Exception ex)
        {
            LogTranslationRequest(snapshot, "exception", cacheHit: false, ex);
            errorStatus = ex is BackgroundAccelerationException ? ex.Message : ex.Message.StartsWith("API 返回", StringComparison.OrdinalIgnoreCase)
                ? "HTTP错误" : ex is BatchJsonException ? "翻译结果解析失败" : "正文读取或翻译失败";
            errorDetails = ex.ToString();
        }
        finally
        {
            TranslationService.DiagnosticLog($"RunTranslationAsync finally enter disposed={IsDisposed} disposing={Disposing} tokenCanceled={token.IsCancellationRequested}");
            backgroundLifetime.Cancel();
            await backgroundPreparation;
            using(timing.Stage("Background Lifetime Release"))await GeneralBackgroundRecovery.ReleaseAsync();
            snapshot.Trace.WriteMetadata();
            if(timing.FinalResultReadyMs is not null && ReferenceEquals(timing,_wallClock))CompleteWallClock();
            else { timing.Metadata["Outcome"]=errorStatus ?? "Superseded";timing.Complete(Path.Combine(AppDataPaths.ProductLogsRoot,$"wallclock-{timing.OperationId}.json")); }
            StopApiTimer();
            RecalculateTotalTime();
            SetTranslationBusy(false);
            if (errorStatus is not null && CanUse())
                SetTranslationError(errorStatus, errorDetails);
            TranslationService.DiagnosticLog($"RunTranslationAsync finally exit disposed={IsDisposed} disposing={Disposing}");
        }
    }

    private TranslationRequestSnapshot CreateTranslationRequestSnapshot()
    {
        RequireRenderOwnerThread();
        var core=ProductControlPlaneAuthority.RequireCore(_corePipelineV2,"CreateTranslationRequestSnapshot");
        var source = _settings.TranslationTextSource;
        var semanticGroups = core.VisualBlocks.Where(x=>x.TextSelection==TextSelectionAction.Translate)
            .Select((x,index)=>new TranslationSemanticGroup(x.BlockId,x.SourceText,x.Lines.Select(l=>l.SourceId).ToArray(),SegmentType.Body,x.Bounds,index)).ToArray();
        var structuredGroups = core.VisualBlocks.Where(x=>x.TextSelection==TextSelectionAction.Translate)
            .Select((x,index)=>new StructuredTextGroup(x.BlockId,StructuredTextRole.Unknown,x.Lines.Select(l=>l.SourceId).ToArray(),x.SourceText,x.Bounds,index,x.SourceText)).ToArray();
        var items = core.VisualBlocks.Where(x => x.TextSelection == TextSelectionAction.Translate)
            .Select(x => CoreTranslationItemFactory.Create(core, x, StructuredTextRole.Unknown)).ToArray();
        var canonical = string.Join("\n", items.OrderBy(x => x.Id).Select(x => $"{x.Id}:{x.Text}"));
        var requestId = Guid.NewGuid().ToString("N");
        var generation = ++_translationGeneration;
        _currentTranslationRequestId = requestId;
        var requestSettings = ApiSettingsSnapshot.Copy(_settings);
        requestSettings.BackgroundComputeDevice=_processingTask!.Device;
        requestSettings.BackgroundTreatment=_processingTask.Treatment;
        var cacheKey = SessionServices.TranslationKey(items, requestSettings);
        var preserve = $"ids={requestSettings.PreserveIdentifiers},numbers={requestSettings.PreserveNumbers},variables={requestSettings.PreserveVariables}";
        return new TranslationRequestSnapshot(requestId, generation, _imageSessionId,
            _currentOcrRequestId, _ocrGeneration, _settings.OcrEngine, source, items, semanticGroups, structuredGroups,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))),
            canonical.Length, Encoding.UTF8.GetByteCount(canonical),
            canonical.Count(x => x == '\n') + 1, cacheKey, requestSettings,
            TranslationProviderRegistry.Get(requestSettings.TranslationProviderKind).Id,
            TranslationCacheKeyBuilder.NormalizeBaseUrl(requestSettings.ApiUrl), requestSettings.Model,
            requestSettings.TargetLanguage, requestSettings.TranslationStyle,
            TranslationPromptBuilder.PromptHash(requestSettings), preserve, _processingTask!);
    }

    private static StructuredTextRole ToStructuredRole(RegionRoleType role) => role switch
    {
        RegionRoleType.Title => StructuredTextRole.Title, RegionRoleType.CharacterName => StructuredTextRole.CharacterName,
        RegionRoleType.Species => StructuredTextRole.Species,
        RegionRoleType.Header => StructuredTextRole.Header, RegionRoleType.Dialogue => StructuredTextRole.Dialogue,
        RegionRoleType.Narration => StructuredTextRole.Narration, RegionRoleType.Metadata => StructuredTextRole.Metadata,
        RegionRoleType.Button => StructuredTextRole.Button, RegionRoleType.Choice => StructuredTextRole.Choice,
        RegionRoleType.Caption => StructuredTextRole.Caption, RegionRoleType.UILabel => StructuredTextRole.UILabel,
        RegionRoleType.BodyParagraph => StructuredTextRole.BodyParagraph, _ => StructuredTextRole.Unknown
    };

    private static bool IsApiTranslationRequired(TranslationUnitV2 unit) =>
        !unit.PreserveOriginal && !string.IsNullOrWhiteSpace(unit.Text);

    private bool IsCurrentTranslationRequest(TranslationRequestSnapshot snapshot) =>
        snapshot.RequestId == _currentTranslationRequestId &&
        snapshot.Generation == _translationGeneration &&
        snapshot.ImageSessionId == _imageSessionId &&
        snapshot.OcrRequestId == _currentOcrRequestId &&
        snapshot.OcrGeneration == _ocrGeneration &&
        snapshot.OcrEngine == _settings.OcrEngine;

    private void InvalidateTranslationRequest(string reason)
    {
        RequireRenderOwnerThread();
        if (_currentTranslationRequestId.Length > 0)
            TranslationService.DiagnosticLog($"translation invalidate request={_currentTranslationRequestId} reason={reason}");
        _currentTranslationRequestId = "";
        _translationGeneration++;
        _operation?.Cancel();
    }

    private static void LogTranslationRequest(TranslationRequestSnapshot snapshot, string outcome,
        bool cacheHit, Exception? error = null) => TranslationService.DiagnosticLog(
        $"translation request={snapshot.RequestId} generation={snapshot.Generation} imageSession={snapshot.ImageSessionId} " +
        $"ocrEngine={snapshot.OcrEngine} ocrRequest={snapshot.OcrRequestId} ocrGeneration={snapshot.OcrGeneration} " +
        $"source={snapshot.Source} chars={snapshot.CharacterCount} utf8Bytes={snapshot.Utf8ByteCount} lines={snapshot.LineCount} " +
        $"textSha256={snapshot.TextSha256} provider={snapshot.ProviderId} baseUrl={snapshot.BaseUrlIdentity} model={snapshot.Model} " +
        $"target={snapshot.TargetLanguage} style={snapshot.Style} promptHash={snapshot.PromptHash} preserve={snapshot.PreserveRules} " +
        $"cacheKeyHash={snapshot.CacheKey} cacheHit={cacheHit} inFlightDedupe=false " +
        $"outcome={outcome} exception={error?.GetType().Name ?? "none"}");

    private static bool IsRemoteDisconnect(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket && socket.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
                return true;
            if (current.Message.Contains("10054", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("远程主机强迫关闭", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return exception is HttpRequestException or IOException;
    }

    private void ShowTranslationSettings()
    {
        using var dialog = new TranslationSettingsDialog(_settings);
        if (ShowPreviewDialog(dialog) != DialogResult.OK) return;
        ApplyTranslationSettings(dialog.ResultSettings);
    }

    internal void ApplyTranslationSettings(ApiSettings updated)
    {
        var backgroundOnly=HistoryRegenerationPolicy.TranslationContext(_settings)==HistoryRegenerationPolicy.TranslationContext(updated) && _settings.TranslationTextSource==updated.TranslationTextSource;
        _settings.BackgroundTreatment=updated.BackgroundTreatment;
        _settings.OcrLoad=updated.OcrLoad;
        _settings.BackgroundComputeDevice=updated.BackgroundComputeDevice;
        if(backgroundOnly){CopyTranslationSettings(updated,_settings);return;} // Current task already owns its settings snapshot.
        var targetChanged = !string.Equals(_settings.TargetLanguage, updated.TargetLanguage,
            StringComparison.OrdinalIgnoreCase);
        InvalidateTranslationRequest("runtime translation settings changed");
        CopyTranslationSettings(updated, _settings);
        _settings.BackgroundComputeDevice=updated.BackgroundComputeDevice;
        SetStatus($"翻译设置已应用到当前 Preview：{_settings.TargetLanguage} / {_settings.TranslationStyle} / {_settings.Model}");
        TranslationService.DiagnosticLog(
            $"preview runtime settings applied target={_settings.TargetLanguage} style={_settings.TranslationStyle} " +
            $"provider={TranslationProviderRegistry.Get(_settings.TranslationProviderKind).Id} model={_settings.Model} " +
            $"promptHash={TranslationPromptBuilder.PromptHash(_settings)}");
        if (targetChanged && HasTranslationInput()) StartTranslation();
    }

    private bool HasTranslationInput() => CanUse() && _document is { Groups.Count: > 0 } &&
        DocumentState.CreateTranslationItems(_document, _settings.TranslationTextSource)
            .Any(x => !string.IsNullOrWhiteSpace(x.Text));

    private static void CopyTranslationSettings(ApiSettings source, ApiSettings target)
    {
        target.ApiUrl = source.ApiUrl; target.ApiKey = source.ApiKey; target.Model = source.Model;
        target.TargetLanguage = source.TargetLanguage; target.SourceLanguage = source.SourceLanguage;
        target.TranslationProvider = source.TranslationProvider; target.TranslationProviderKind = source.TranslationProviderKind;
        target.ProviderDisplayName = source.ProviderDisplayName; target.TranslationStyle = source.TranslationStyle;
        target.CustomTranslationPrompt = source.CustomTranslationPrompt; target.PreserveIdentifiers = source.PreserveIdentifiers;
        target.PreserveNumbers = source.PreserveNumbers; target.PreserveVariables = source.PreserveVariables;
        target.TranslationPromptVersion = source.TranslationPromptVersion;
    }

    private void HandleTranslationProgress(TranslationProgress progress)
    {
        if (_document is null || !CanUse()) return;
        _document.Metrics.ApiRequests = Math.Max(_document.Metrics.ApiRequests, progress.RequestCount);
        _document.Metrics.ApiWaitMs = Math.Max(_document.Metrics.ApiWaitMs, progress.TotalWaitMs);
        RecalculateTotalTime();
        _translationStageText = progress.Message;
        if (progress.Stage == TranslationStage.SendingRequest)
        {
            _apiElapsedBeforeRequest = progress.TotalWaitMs;
            _apiRequestStarted = DateTimeOffset.Now;
            _apiTiming = true;
            _apiTimer.Start();
        }
        else if (progress.Stage == TranslationStage.RequestFinished)
        {
            _apiTiming = false;
            _apiTimer.Stop();
            _document.Metrics.ApiWaitMs = progress.TotalWaitMs;
            RecalculateTotalTime();
        }
        SetStatus($"{progress.Message}｜{_document.Metrics}");
    }

    private void UpdateRunningApiTime()
    {
        if (!_apiTiming || _document is null || !CanUse()) return;
        _document.Metrics.ApiWaitMs = _apiElapsedBeforeRequest +
            (long)(DateTimeOffset.Now - _apiRequestStarted).TotalMilliseconds;
        RecalculateTotalTime();
        SetStatus($"{_translationStageText}｜{_document.Metrics}");
    }

    private void StopApiTimer()
    {
        _apiTiming = false;
        _apiTimer.Stop();
    }

    private void RecalculateTotalTime()
    {
        if (_document is null) return;
        _document.Metrics.TotalMs = _document.Metrics.CaptureMs + _document.Metrics.PreprocessMs + _document.Metrics.OcrMs +
                                    _document.Metrics.MergeMs + _document.Metrics.ApiWaitMs +
                                    _document.Metrics.BackgroundMs + _document.Metrics.LayoutMs +
                                    _document.Metrics.DrawMs + _document.Metrics.EncodeMs;
    }

    private void CompleteWallClock()
    {
        if (_wallClock is null || _wallClock.EndedAt is not null) return;
        if (_recognitionV2?.Diagnostics is { } d)
        {
            var visionPid=_visionRuntimeManager.ActiveWorkerPid; var ocrPid=_ocrRuntimeManager.ActiveWorkerPid;
            _wallClock.AddCompletedStage("Traditional CV",d.TraditionalCvMs);
            _wallClock.AddCompletedStage("Visual End-to-End",d.VisionMs,visionPid);
            var detail=_visualAnalysisV2?.ModelDiagnostics.Detail??"";
            long DetailMs(string key){var m=Regex.Match(detail,$@"\b{Regex.Escape(key)}=(\d+)ms");return m.Success?long.Parse(m.Groups[1].Value):0;}
            var workerMs=DetailMs("worker"); var queueMs=DetailMs("queue"); var startupMs=DetailMs("startup"); var loadMs=DetailMs("load");
            var preprocessMs=DetailMs("preprocess"); var inferenceMs=DetailMs("inference"); var postprocessMs=DetailMs("postprocess");
            _wallClock.AddCompletedStage("Image Prep",Math.Max(0,d.VisionMs-workerMs-queueMs-startupMs),visionPid);
            _wallClock.AddCompletedStage("Visual Worker Queue Wait",queueMs,visionPid);
            _wallClock.AddCompletedStage("Visual Worker Startup",startupMs,visionPid);
            _wallClock.AddCompletedStage("Visual Model Load",loadMs,visionPid);
            _wallClock.AddCompletedStage("Visual Preprocess",preprocessMs,visionPid);
            _wallClock.AddCompletedStage("Visual Inference",inferenceMs>0?inferenceMs:Math.Max(0,workerMs-loadMs),visionPid);
            _wallClock.AddCompletedStage("Visual Postprocess",postprocessMs,visionPid);
            _wallClock.AddCompletedStage("OCR Recognition",d.OcrMs,ocrPid);
            var ocrInit=_lastOcrResult?.ModelLoadMilliseconds??0;
            _wallClock.AddCompletedStage("OCR Engine Init",ocrInit,ocrPid);
            _wallClock.AddCompletedStage("OCR Detection / Recognition",Math.Max(0,d.OcrMs-ocrInit),ocrPid);
            _wallClock.AddCompletedStage("Crop OCR Total",d.CropOcrMs,ocrPid);
            _wallClock.AddCompletedStage("Region Fusion",d.FusionMs);
            _wallClock.AddCompletedStage("Region Proposal",d.TraditionalCvMs);
            _wallClock.AddCompletedStage("Semantic Grouping",d.GroupingMs);
            _wallClock.AddCompletedStage("Role Classification",d.RoleMs);
            _wallClock.AddCompletedStage("Translation Unit Build",d.TranslationUnitBuildMs);
            _wallClock.Metadata["CropOcrCount"]=d.CropOcrCount; _wallClock.Metadata["RoleClassificationCount"]=d.RoleClassificationCount;
        }
        if(_lastRegionRender is { } render)
        {
            _wallClock.AddCompletedStage("Renderer Mask",render.MaskMs);
            _wallClock.AddCompletedStage("Renderer Layout",render.LayoutMs);
            _wallClock.AddCompletedStage("Renderer Draw",render.DrawMs);
        }
        if(_lastCorePipelineTiming is { } coreTiming)
        {
            _wallClock.AddCompletedStage("Renderer Style",(long)Math.Round(coreTiming.StyleMs));
            _wallClock.AddCompletedStage("Renderer Background",(long)Math.Round(coreTiming.BackgroundMs));
            _wallClock.AddCompletedStage("Renderer Layout",(long)Math.Round(coreTiming.LayoutMs));
            _wallClock.AddCompletedStage("Renderer Draw",(long)Math.Round(coreTiming.DrawMs));
            _wallClock.AddCompletedStage("Renderer Encode",(long)Math.Round(coreTiming.EncodeMs));
            _wallClock.Metadata["RendererDiagnosticsEnabled"]=coreTiming.DiagnosticsEnabled;
            _wallClock.Metadata["RendererFullFrameDiagnosticPngWrites"]=coreTiming.FullFrameDiagnosticPngWrites;
            _wallClock.Metadata["RendererDiagnosticJsonWrites"]=coreTiming.DiagnosticJsonWrites;
            _wallClock.Metadata["RendererMeasuredTotalMs"]=coreTiming.TotalMs;
        }
        if(RealExeE2ETrace.CurrentCaptureMetrics is { } capture)
        {
            _document!.Metrics.CaptureMs=Math.Max(0,capture.ActualFrameAcquireMs);
            _wallClock.AddCompletedStage("Capture",Math.Max(0,capture.ActualFrameAcquireMs),capture.CaptureThreadId);
            _wallClock.AddCompletedStage("Capture GPU-to-CPU",Math.Max(0,capture.GpuToCpuCopyMs),capture.CaptureThreadId);
            _wallClock.AddCompletedStage("Capture Virtual Desktop Compose",Math.Max(0,capture.VirtualDesktopComposeMs),capture.CaptureThreadId);
            _wallClock.Metadata["CaptureBackend"]=capture.CaptureBackend;
            _wallClock.Metadata["CaptureUsedFallback"]=capture.UsedFallback;
            RecalculateTotalTime();
        }
        _wallClock.AddCompletedStage("Translation Parse",_translationParseMs);
        _wallClock.AddCompletedStage("Missing Group Recovery",_missingRecoveryTriggered?Math.Max(0,_document?.Metrics.ApiWaitMs??0):0);
        _wallClock.AddCompletedStage("UI Dispatch Wait",_wallClock.Stages.LastOrDefault(x=>x.Name=="Preview Update")?.DurationMs??0);
        _lastTimingPath = Path.Combine(AppDataPaths.ProductLogsRoot, $"wallclock-{_wallClock.OperationId}.json");
        _wallClock.Metadata["VisionInferenceCount"] = _recognitionRunCount;
        _wallClock.Metadata["OcrEngine"] = _settings.OcrEngine.ToString();
        _wallClock.Metadata["VisualModel"] = _settings.VisualModel.ToString();
        _wallClock.MarkFinalResultReady();_processingTask?.WriteMetadata();
        _wallClock.Complete(_lastTimingPath); _finalPreviewReady = true;
        if(_document is not null)_document.Metrics.TotalWallClockMs=_wallClock.ElapsedMs;
        var finalDiagnostics = _recognitionV2?.Diagnostics;
        var recognition = finalDiagnostics?.TotalMs ?? _document?.Metrics.OcrMs ?? 0;
        var vision = finalDiagnostics?.VisionMs ?? 0; var ocr = finalDiagnostics?.OcrMs ?? _document?.Metrics.OcrMs ?? 0;
        var metrics=_document?.Metrics??new TimingMetrics();
        var missing=_corePipelineV2?.VisualBlocks.Count(block=>block.TextSelection==TextSelectionAction.Translate &&
            _corePipelineV2.Translations.TryGetValue(block.BlockId,out var translation) && translation.State==BlockTranslationState.Failed)??0;
        var completion=CompletionStatus(missing);
        SetStatus($"{completion}｜截图 {metrics.CaptureMs}ms｜识别 {recognition}ms（视觉 {vision}ms / OCR {ocr}ms）｜翻译 API {metrics.ApiWaitMs}ms｜背景 {metrics.BackgroundMs}ms｜排版 {metrics.LayoutMs}ms｜绘制 {metrics.DrawMs}ms｜编码 {metrics.EncodeMs}ms｜实际总耗时 {_wallClock.ElapsedMs}ms");
    }

    private void BuildOriginalDisplay()
    {
        _originalDisplay?.Dispose();
        _originalDisplay = new Bitmap(_original);
    }

    private void BuildTranslatedDisplay() => RenderSynchronously();

    private Task<bool> BuildTranslatedDisplayResponsiveAsync(CancellationToken token) =>
        RenderResponsiveAsync(token);

    private sealed record TypographyProfile(float MinFont, float MaxFont, float LineSpacing,
        float HorizontalPadding, float VerticalPadding, int MaxLines,
        StringAlignment HorizontalAlignment, StringAlignment VerticalAlignment,
        bool AllowExpansion, float ExpansionFactor);

    private sealed record WrappedText(IReadOnlyList<string> Lines, float LineHeight, RectangleF Inner, bool Fits);
    private sealed record TextLayout(SegmentGroup Group, RectangleF Bounds, float FontSize, bool Fits, TypographyProfile Profile);

    private static TypographyProfile GetTypographyProfile(SegmentType type) => type switch
    {
        SegmentType.Title => new(9, 24, 1.05f, 6, 4, 2, StringAlignment.Near, StringAlignment.Center, true, 1.15f),
        SegmentType.Body => new(7, 14, 1.08f, 8, 6, int.MaxValue, StringAlignment.Near, StringAlignment.Near, true, 1.12f),
        SegmentType.Dialogue => new(7, 13, 1.07f, 6, 4, 6, StringAlignment.Near, StringAlignment.Near, true, 1.12f),
        SegmentType.ImageCaption => new(7, 10, 1.03f, 4, 3, 4, StringAlignment.Near, StringAlignment.Near, false, 1),
        SegmentType.CharacterInfo => new(7, 12, 1.04f, 5, 3, 3, StringAlignment.Near, StringAlignment.Center, false, 1),
        SegmentType.Time or SegmentType.MessageCount => new(7, 11, 1.0f, 4, 2, 2, StringAlignment.Near, StringAlignment.Center, false, 1),
        SegmentType.Button => new(8, 13, 1.0f, 5, 3, 2, StringAlignment.Center, StringAlignment.Center, false, 1),
        _ => new(7, 12, 1.04f, 5, 3, 3, StringAlignment.Near, StringAlignment.Center, false, 1)
    };

    private List<TextLayout> CreateLayouts(IReadOnlyList<SegmentGroup> regions)
    {
        ProductControlPlaneAuthority.LegacyReached("CreateLayouts");
        var result = new List<TextLayout>();
        var ordered = regions.OrderBy(x => x.Bounds.Top).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var region = ordered[i];
            var profile = GetTypographyProfile(region.GroupType);
            var rect = RectangleF.Inflate(region.Bounds, 3, 3);
            var nextTop = ordered.Skip(i + 1)
                .Where(x => HorizontalOverlap(rect, x.Bounds) > .15f)
                .Select(x => x.Bounds.Top).DefaultIfEmpty(_original.Height).Min();
            var expansionFactor = profile.AllowExpansion ? profile.ExpansionFactor : 1f;
            var allowedHeight = Math.Min(_original.Height - rect.Top,
                Math.Min(Math.Max(rect.Height, nextTop - rect.Top - 3), rect.Height * expansionFactor));
            var expanded = new RectangleF(rect.X, rect.Y, rect.Width, allowedHeight);
            if (!ordered.Where(x => !ReferenceEquals(x, region)).Any(x => OverlapArea(expanded, x.Bounds) > 1f)) rect = expanded;
            rect.Intersect(new RectangleF(0, 0, _original.Width, _original.Height));
            region.RenderRectangle = rect;
            var maximum = Math.Clamp(Math.Min(profile.MaxFont, region.Bounds.Height * .68f) * _renderSettings.FontScale, 7, 28);
            var min = Math.Clamp(profile.MinFont * _renderSettings.FontScale, 6, 12);
            var low = min; var high = maximum; var chosen = min; var fits = false;
            using var probe = Graphics.FromImage(_original);
            for (var pass = 0; pass < 12; pass++)
            {
                var size = (low + high) / 2f;
                if (TextFitsExactly(probe, region.Translation, rect, size, profile))
                {
                    chosen = size; fits = true; low = size;
                }
                else high = size;
            }
            if (!TextFitsExactly(probe, region.Translation, rect, chosen, profile)) fits = false;
            result.Add(new(region, rect, chosen, fits, profile));
        }
        return result;
    }

    private (Color Text, Color Outline, bool Complex) ResolveStyle(RectangleF rect)
    {
        ProductControlPlaneAuthority.LegacyReached("ResolveStyle");
        long sum = 0, sum2 = 0, count = 0;
        var bounded = Rectangle.Intersect(Rectangle.Round(rect), new Rectangle(Point.Empty, _original.Size));
        var stepX = Math.Max(1, bounded.Width / 24);
        var stepY = Math.Max(1, bounded.Height / 16);
        for (var y = bounded.Top; y < bounded.Bottom; y += stepY)
        for (var x = bounded.Left; x < bounded.Right; x += stepX)
        {
            var c = _original.GetPixel(x, y);
            var l = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
            sum += l; sum2 += l * l; count++;
        }
        var mean = count == 0 ? 128 : sum / (double)count;
        var variance = count == 0 ? 0 : sum2 / (double)count - mean * mean;
        var text = _renderSettings.TextColorMode switch
        {
            TextColorMode.White => Color.White,
            TextColorMode.Black => Color.Black,
            TextColorMode.Custom => _renderSettings.CustomTextColor,
            _ => mean < 145 ? Color.White : Color.Black
        };
        var outline = text.GetBrightness() > .5f ? Color.Black : Color.White;
        if (!_renderSettings.AutomaticOutlineColor) outline = _renderSettings.OutlineColor;
        return (text, outline, variance > 1350);
    }

    private void DrawOutlinedText(Graphics g, string text, RectangleF rect, float fontSize, TypographyProfile profile, Color fill, Color outline)
    {
        using var path = CreateTextPath(g, text, rect, fontSize, profile);
        if (_renderSettings.Outline)
        {
            using var pen = new Pen(outline, Math.Clamp(fontSize * .12f, 1.2f, 3.5f)) { LineJoin = LineJoin.Round };
            g.DrawPath(pen, path);
        }
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
    }

    private static bool TextFitsExactly(Graphics graphics, string text, RectangleF rect, float fontSize)
        => TextFitsExactly(graphics, text, rect, fontSize, GetTypographyProfile(SegmentType.Body));

    private static bool TextFitsExactly(Graphics graphics, string text, RectangleF rect, float fontSize, TypographyProfile profile)
    {
        var wrapped = WrapText(graphics, text, rect, fontSize, profile);
        if (!wrapped.Fits) return false;
        using var path = CreateTextPath(graphics, text, rect, fontSize, profile);
        var bounds = path.GetBounds();
        return !bounds.IsEmpty && bounds.Left >= wrapped.Inner.Left - .5f && bounds.Top >= wrapped.Inner.Top - .5f &&
               bounds.Right <= wrapped.Inner.Right + .5f && bounds.Bottom <= wrapped.Inner.Bottom + .5f;
    }

    private static GraphicsPath CreateTextPath(Graphics graphics, string text, RectangleF rect, float fontSize)
        => CreateTextPath(graphics, text, rect, fontSize, GetTypographyProfile(SegmentType.Body));

    private static GraphicsPath CreateTextPath(Graphics graphics, string text, RectangleF rect, float fontSize, TypographyProfile profile)
    {
        var path = new GraphicsPath();
        var wrapped = WrapText(graphics, text, rect, fontSize, profile);
        if (!wrapped.Fits) return path;
        using var family = new FontFamily("Microsoft YaHei UI");
        using var measureFont = new Font(family, fontSize * graphics.DpiY / 96f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var format = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.None };
        var totalHeight = wrapped.Lines.Count * wrapped.LineHeight;
        var y = profile.VerticalAlignment switch
        {
            StringAlignment.Center => wrapped.Inner.Top + (wrapped.Inner.Height - totalHeight) / 2f,
            StringAlignment.Far => wrapped.Inner.Bottom - totalHeight,
            _ => wrapped.Inner.Top
        };
        var em = fontSize * graphics.DpiY / 96f;
        foreach (var line in wrapped.Lines)
        {
            var width = graphics.MeasureString(line, measureFont, int.MaxValue, format).Width;
            var x = profile.HorizontalAlignment switch
            {
                StringAlignment.Center => wrapped.Inner.Left + (wrapped.Inner.Width - width) / 2f,
                StringAlignment.Far => wrapped.Inner.Right - width,
                _ => wrapped.Inner.Left
            };
            path.AddString(line, family, (int)FontStyle.Regular, em, new PointF(x, y), format);
            y += wrapped.LineHeight;
        }
        return path;
    }

    private static WrappedText WrapText(Graphics graphics, string text, RectangleF rect, float fontSize, TypographyProfile profile)
    {
        var inner = new RectangleF(rect.X + profile.HorizontalPadding, rect.Y + profile.VerticalPadding,
            Math.Max(0, rect.Width - profile.HorizontalPadding * 2), Math.Max(0, rect.Height - profile.VerticalPadding * 2));
        if (string.IsNullOrWhiteSpace(text) || inner.Width <= 2 || inner.Height <= 2)
            return new([], 0, inner, false);
        using var font = new Font("Microsoft YaHei UI", fontSize * graphics.DpiY / 96f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var format = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.None };
        float Width(string value) => graphics.MeasureString(value, font, int.MaxValue, format).Width;
        var lines = new List<string>();
        var current = "";
        var tokens = Regex.Matches(text.Replace("\r", "").Replace("\n", " "), @"[A-Za-z0-9]+(?:['’.-][A-Za-z0-9]+)*|\s+|.")
            .Select(x => x.Value);
        foreach (var raw in tokens)
        {
            var token = string.IsNullOrWhiteSpace(raw) ? " " : raw;
            var candidate = current.Length == 0 ? token.TrimStart() : current + token;
            if (Width(candidate) <= inner.Width + .5f) { current = candidate; continue; }
            if (!string.IsNullOrWhiteSpace(current)) lines.Add(current.TrimEnd());
            current = token.TrimStart();
            if (Width(current) <= inner.Width + .5f) continue;
            var oversized = current; current = "";
            foreach (var character in oversized)
            {
                candidate = current + character;
                if (current.Length > 0 && Width(candidate) > inner.Width + .5f) { lines.Add(current); current = character.ToString(); }
                else current = candidate;
            }
        }
        if (!string.IsNullOrWhiteSpace(current)) lines.Add(current.TrimEnd());
        var lineHeight = font.GetHeight(graphics) * profile.LineSpacing;
        var fits = lines.Count > 0 && lines.Count <= profile.MaxLines && lines.Count * lineHeight <= inner.Height + .5f && lines.All(x => Width(x) <= inner.Width + .5f);
        return new(lines, lineHeight, inner, fits);
    }

    private Color EstimateBackgroundColor(RectangleF source)
    {
        var r = Rectangle.Intersect(Rectangle.Round(source), new Rectangle(Point.Empty, _original.Size));
        if (r.Width <= 0 || r.Height <= 0) return Color.Black;
        long red = 0, green = 0, blue = 0, count = 0;
        var step = Math.Max(1, Math.Min(r.Width, r.Height) / 20);
        void Add(int x, int y) { if (x < 0 || y < 0 || x >= _original.Width || y >= _original.Height) return; var c = _original.GetPixel(x, y); red += c.R; green += c.G; blue += c.B; count++; }
        for (var x = r.Left; x < r.Right; x += step) { Add(x, r.Top - 2); Add(x, r.Bottom + 1); }
        for (var y = r.Top; y < r.Bottom; y += step) { Add(r.Left - 2, y); Add(r.Right + 1, y); }
        return count == 0 ? Color.Black : Color.FromArgb((int)(red / count), (int)(green / count), (int)(blue / count));
    }

    private static float OverlapArea(RectangleF a, RectangleF b)
    {
        var intersection = RectangleF.Intersect(a, b);
        return intersection.IsEmpty ? 0 : intersection.Width * intersection.Height;
    }

    private void ShowOcrInput()
    {
        if (_document?.DebugImage is null)
        {
            AppDialog.Show(this, "没有 OCR 调试图片", "请先执行 OCR。", AppDialogKind.Warning);
            return;
        }
        var form = new Form
        {
            Text = $"OCR实际输入：{_document.SelectedCandidate}｜{_document.DebugImage.Width}×{_document.DebugImage.Height}｜原始行 {_document.RawLines.Count}",
            Size = new Size(1000, 700), StartPosition = FormStartPosition.CenterParent
        };
        form.Controls.Add(new PictureBox { Dock = DockStyle.Fill, Image = new Bitmap(_document.DebugImage), SizeMode = PictureBoxSizeMode.Zoom });
        form.FormClosed += (_, _) => ((PictureBox)form.Controls[0]).Image?.Dispose();
        form.Show(this);
    }

    private void ShowTranslationInputTrace()
    {
        if (_document is null) { SetStatus("请先执行OCR"); return; }
        var raw = _document.OriginalSnapshot?.RawText ?? _document.RawText;
        var normalized = string.Join(Environment.NewLine, _document.RawLines.OrderBy(x => x.ReadingOrder).Select(x => x.Text));
        var edited = string.Join(Environment.NewLine + Environment.NewLine,
            _document.Regions.OrderBy(x => x.ReadingOrder).Select(x => x.Text));
        var finalSource = string.Join(Environment.NewLine + Environment.NewLine,
            _document.Groups.OrderBy(x => x.ReadingOrder).Select(x => x.OrganizedText));
        var translationInput = string.Join(Environment.NewLine,
            DocumentState.CreateTranslationItems(_document, _settings.TranslationTextSource).Select(x => $"[{x.Id}] {x.Text}"));
        var form = new Form { Text = "OCR → 翻译输入链追踪（诊断）", Size = new Size(1000, 720), StartPosition = FormStartPosition.CenterParent };
        var tabs = new TabControl { Dock = DockStyle.Fill };
        AddTraceTab(tabs, "Raw OCR", raw); AddTraceTab(tabs, "Normalized OCR", normalized);
        AddTraceTab(tabs, "Edited OCR", edited); AddTraceTab(tabs, "Final Source Text", finalSource);
        AddTraceTab(tabs, "Translation Input", translationInput);
        form.Controls.Add(tabs); form.Show(this);
    }

    private static void AddTraceTab(TabControl tabs, string title, string value)
    {
        var page = new TabPage(title);
        page.Controls.Add(new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, Text = value,
            Font = new Font("Microsoft YaHei UI", 10F), BackColor = Color.FromArgb(29, 34, 44), ForeColor = Color.White });
        tabs.TabPages.Add(page);
    }

    private void ShowStyleDialog()
    {
        using var dialog = new Form
        {
            Text = "译文样式（修改后只重新绘图）", ClientSize = new Size(470, 375),
            StartPosition = FormStartPosition.CenterParent, Font = Font
        };
        var mode = new ComboBox { Left = 150, Top = 22, Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
        mode.Items.AddRange(["自动文字颜色", "白色", "黑色", "自定义颜色"]);
        mode.SelectedIndex = (int)_renderSettings.TextColorMode;
        var outline = new CheckBox { Left = 150, Top = 68, Width = 180, Text = "启用描边", Checked = _renderSettings.Outline };
        var background = new CheckBox { Left = 150, Top = 106, Width = 180, Text = "启用半透明底板", Checked = _renderSettings.Background };
        var opacity = new TrackBar { Left = 145, Top = 142, Width = 230, Minimum = 0, Maximum = 230, Value = _renderSettings.BackgroundOpacity };
        var scale = new NumericUpDown { Left = 150, Top = 205, Width = 100, DecimalPlaces = 2, Minimum = .5M, Maximum = 2M, Increment = .05M, Value = (decimal)_renderSettings.FontScale };
        var custom = new Button { Left = 150, Top = 247, Width = 88, Text = "自定义文字" };
        custom.Click += (_, _) => { using var c = new ColorDialog { Color = _renderSettings.CustomTextColor }; if (c.ShowDialog(dialog) == DialogResult.OK) _renderSettings.CustomTextColor = c.Color; };
        var outlineColor = new Button { Left = 244, Top = 247, Width = 88, Text = "描边颜色" };
        outlineColor.Click += (_, _) => { using var c = new ColorDialog { Color = _renderSettings.OutlineColor }; if (c.ShowDialog(dialog) == DialogResult.OK) { _renderSettings.OutlineColor = c.Color; _renderSettings.AutomaticOutlineColor = false; } };
        var backgroundColor = new Button { Left = 338, Top = 247, Width = 88, Text = "底板颜色" };
        backgroundColor.Click += (_, _) => { using var c = new ColorDialog { Color = _renderSettings.BackgroundColor }; if (c.ShowDialog(dialog) == DialogResult.OK) _renderSettings.BackgroundColor = c.Color; };
        var ok = new Button { Left = 185, Top = 315, Width = 100, Text = "应用", DialogResult = DialogResult.OK };
        dialog.Controls.AddRange([
            new Label { Left = 28, Top = 26, AutoSize = true, Text = "文字颜色" }, mode,
            outline, background, new Label { Left = 28, Top = 154, AutoSize = true, Text = "底板透明度" }, opacity,
            new Label { Left = 28, Top = 208, AutoSize = true, Text = "字号比例" }, scale,
            custom, outlineColor, backgroundColor, ok
        ]);
        dialog.AcceptButton = ok;
        if (ShowPreviewDialog(dialog) != DialogResult.OK) return;
        _renderSettings.TextColorMode = (TextColorMode)mode.SelectedIndex;
        _renderSettings.Outline = outline.Checked;
        _renderSettings.Background = background.Checked;
        _renderSettings.BackgroundOpacity = opacity.Value;
        _renderSettings.FontScale = (float)scale.Value;
        if (_document is not null)
        {
            BuildTranslatedDisplay();
            SetView(true);
            SetStatus("译文样式已更新（未重新OCR、未调用API）");
        }
    }

    private void SetView(bool translation)
    {
        SetPreferredImageMode(translation?ImageViewMode.Translated:ImageViewMode.Original,"SetView",true);
        SelectTextMode(translation?TextViewMode.Translation:TextViewMode.Organized);
        UpdateIndependentViews();
    }

    private PreviewSelectionState ResolveInitialPreviewSelection()
    {
        if (_settings.RememberLastPreviewSelection && _session.LastPreviewSelection is { } last)
            return last;
        return new PreviewSelectionState(_settings.OcrEngine,
            (ImageViewMode)_settings.PreviewDefaultImage, (TextViewMode)_settings.PreviewDefaultText);
    }

    private void RememberCurrentPreviewSelection()
    {
        if (!_settings.RememberLastPreviewSelection || _textMode.SelectedIndex < 0) return;
        _session.RememberPreviewSelection(_settings.OcrEngine,
            _preferredImageMode, SelectedTextMode);
    }

    private void ApplyDefaultViews()
    {
        UpdateIndependentViews();
    }

    private void SetPreferredImageMode(ImageViewMode mode,string reason,bool remember)
    {
        _preferredImageMode=mode;_suppressImageModeEvent=true;try{if(_imageMode.SelectedIndex!=(int)mode)_imageMode.SelectedIndex=(int)mode;}finally{_suppressImageModeEvent=false;}
        UpdateIndependentViews(rebuildText:false);if(remember)RememberCurrentPreviewSelection();LogPreviewMode(reason);
    }

    private void LogPreviewMode(string reason)=>AppLog.Write("preview-mode",$"default={_settings.PreviewDefaultImage} remember={_settings.RememberLastPreviewSelection} last={_session.LastPreviewSelection?.Image.ToString()??"none"} preferred={_preferredImageMode} displayed={_displayedImageMode} translatedAvailable={_translatedDisplay is not null} reason={reason}");

    private void UpdateIndependentViews(bool rebuildText=true)
    {
        var total=Stopwatch.StartNew();long chromeMs,imageMs,textMs,layoutMs;
        var stage=Stopwatch.StartNew();
        _split.Panel2Collapsed=!_settings.PreviewTextPanelVisible;
        var translatedAvailable=_translatedDisplay is not null;_displayedImageMode=_preferredImageMode==ImageViewMode.Translated&&translatedAvailable?ImageViewMode.Translated:ImageViewMode.Original;
        var translatedLabel=translatedAvailable?"译图（实验）":"译图（实验·生成中）";if(_imageMode.Items.Count>1&&!Equals(_imageMode.Items[1],translatedLabel)){_suppressImageModeEvent=true;try{_imageMode.Items[1]=translatedLabel;}finally{_suppressImageModeEvent=false;}}
        stage.Stop();chromeMs=stage.ElapsedMilliseconds;stage.Restart();
        Image? nextImage=_allowUiImageDisplay?(_displayedImageMode==ImageViewMode.Translated?_translatedDisplay!:_originalDisplay??_original):null;
        if(!ReferenceEquals(_picture.Image,nextImage)){_picture.Image=nextImage;_imageAssignCount++;}
        stage.Stop();imageMs=stage.ElapsedMilliseconds;stage.Restart();
        if(rebuildText)BuildTextView();
        stage.Stop();textMs=stage.ElapsedMilliseconds;stage.Restart();
        UpdateImageLayout();
        _picture.Invalidate();_invalidateCount++;
        stage.Stop();layoutMs=stage.ElapsedMilliseconds;total.Stop();
        AppLog.Write("preview-performance",$"operation=view-switch mode={_displayedImageMode} total_ms={total.ElapsedMilliseconds} chrome_ms={chromeMs} image_assign_ms={imageMs} text_rebuild_ms={textMs} layout_refresh_ms={layoutMs} rebuilt_text={rebuildText}");
    }

    private void CommitFinalPreviewAtomically()
    {
        if(_translatedDisplay is null)return;
        _suppressImageModeEvent=true;
        try { if(_imageMode.Items.Count>1) _imageMode.Items[1]="译图（实验）"; }
        finally { _suppressImageModeEvent=false; }
        var commit=Stopwatch.StartNew();
        _atomicCommitInProgress=true;
        _split.SuspendLayout();_imagePanel.SuspendLayout();_picture.SuspendLayout();_text.SuspendLayout();
        try
        {
            _selectedTextMode=TextViewMode.Translation;
            if(_textMode.SelectedIndex!=2)_textMode.SelectedIndex=2;
            _split.Panel2Collapsed=!_settings.PreviewTextPanelVisible;
            _displayedImageMode=_preferredImageMode==ImageViewMode.Translated?ImageViewMode.Translated:ImageViewMode.Original;
            var finalImage=_displayedImageMode==ImageViewMode.Translated?_translatedDisplay:_originalDisplay??_original;
            // Prepare the complete right-side document before either visible control changes.
            UpdateSplitter();
            var document=PrepareTextDocument(TextViewMode.Translation);
            ApplyPreparedTextDocument(document,true);
            if(!ReferenceEquals(_picture.Image,finalImage)){_picture.Image=finalImage;_imageAssignCount++;}
            ApplyImageLayout(finalImage.Size);
            _layoutVersion++;
            _awaitingFirstFinalPaint=_displayedImageMode==ImageViewMode.Translated;
        }
        finally
        {
            _text.ResumeLayout(false);_picture.ResumeLayout(false);_imagePanel.ResumeLayout(false);_split.ResumeLayout(false);
            _atomicCommitInProgress=false;
        }
        _picture.Invalidate();_text.Invalidate();_invalidateCount+=2;
        commit.Stop();
        AppLog.Write("real-exe-e2e",$"event=AtomicFinalCommit ms={commit.ElapsedMilliseconds} image_assign={_imageAssignCount} right_text_set={_rightTextSetCount} right_text_append={_rightTextAppendCount} layout={_previewLayoutCount} invalidate={_invalidateCount} refresh={_refreshCount} scroll_changes={_scrollExtentChangeCount} scale_changes={_displayScaleChangeCount} layout_version={_layoutVersion}");
    }

    private void PublishTranslationTextBeforeRenderer(TranslationRequestSnapshot snapshot)
    {
        if(!CanUse()||!IsCurrentTranslationRequest(snapshot)||_document is null)return;
        PublishTranslationTextCore();
        RealExeE2ETrace.Mark("T11.5 TranslationTextPublished");
        AppLog.Write("translation-publish",$"request={snapshot.RequestId} generation={snapshot.Generation} before_renderer=true text_length={_text.TextLength}");
    }

    private void PublishTranslationTextCore()
    {
        if(_document is null)return;
        var publish=Stopwatch.StartNew();
        _atomicCommitInProgress=true;
        _split.SuspendLayout();_text.SuspendLayout();
        try
        {
            _selectedTextMode=TextViewMode.Translation;
            if(_textMode.SelectedIndex!=2)_textMode.SelectedIndex=2;
            _split.Panel2Collapsed=!_settings.PreviewTextPanelVisible;
            UpdateSplitter();
            ApplyPreparedTextDocument(PrepareTextDocument(TextViewMode.Translation));
        }
        finally
        {
            _text.ResumeLayout(false);_split.ResumeLayout(false);
            _atomicCommitInProgress=false;
        }
        _text.Invalidate();_invalidateCount++;
        publish.Stop();
        AppLog.Write("translation-publish",$"event=TextPublishedBeforeRenderer ms={publish.ElapsedMilliseconds} text_length={_text.TextLength}");
    }

    private void CaptureVisualPipelinePreviewScreenshots()
    {
        if (_translatedDisplay is null || !TranslationImageVisualPipelineDiagnostics.BeginPreviewCapture() ||
            TranslationImageVisualPipelineDiagnostics.LatestOutput is not { } output) return;
        var oldFit = _fitView; var oldZoom = _viewZoom; var oldImage = _picture.Image;
        try
        {
            _picture.Image = _translatedDisplay;
            _fitView = false; _viewZoom = 1F; UpdateImageLayout(); _imagePanel.PerformLayout(); _picture.Refresh();
            using (var shot = new Bitmap(ClientSize.Width, ClientSize.Height))
            { DrawToBitmap(shot, ClientRectangle); shot.Save(Path.Combine(output, "Preview-100%-screenshot.png")); }
            var oneToOneScale = _picture.Width / (double)_translatedDisplay.Width;
            var oneToOnePictureSize = _picture.Size;

            _fitView = true; UpdateImageLayout(); _imagePanel.PerformLayout(); _picture.Refresh();
            using (var shot = new Bitmap(ClientSize.Width, ClientSize.Height))
            { DrawToBitmap(shot, ClientRectangle); shot.Save(Path.Combine(output, "Preview-Fit-screenshot.png")); }
            var fitScale = Math.Min(_picture.ClientSize.Width / (double)_translatedDisplay.Width,
                _picture.ClientSize.Height / (double)_translatedDisplay.Height);
            TranslationImageVisualPipelineDiagnostics.SavePreviewMetadata(new
            {
                bitmap = new { _translatedDisplay.Width, _translatedDisplay.Height },
                oneToOne = new { requestedZoom = 1.0, actualImageScale = oneToOneScale, pictureSizeMode = PictureBoxSizeMode.Normal.ToString(), pictureSize = oneToOnePictureSize },
                fit = new { actualImageScale = fitScale, shrinkOnly = true, neverEnlargesSmallImage = fitScale <= 1.0, pictureSizeMode = _picture.SizeMode.ToString(), pictureClientSize = _picture.ClientSize },
                paint = new { interpolationMode = fitScale == 1.0 ? "No resize / 1:1" : "WinForms StretchImage shrink", pixelOffsetMode = "Integer PictureBox bounds", compositingQuality = "Display only; saved bitmap unchanged", smoothingMode = "No product bitmap rewrite", cachedScaledBitmap = false },
                sameImageObjectForBothCaptures = true
            });
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(output, "PREVIEW-CAPTURE-ERROR.txt"), ex.ToString()); }
        finally { _fitView = oldFit; _viewZoom = oldZoom; _picture.Image = oldImage; UpdateImageLayout(); _picture.Invalidate(); }
    }

    private sealed record PreparedTextDocument(string Text,IReadOnlyDictionary<string,(int Start,int Length)> Ranges,
        IReadOnlyDictionary<string,RightTextRangeIdentity> Identities,NativePoint Scroll);

    private PreparedTextDocument PrepareTextDocument(TextViewMode mode)
    {
        var scroll=GetTextScrollPosition();var ranges=new Dictionary<string,(int,int)>(StringComparer.Ordinal);
        var identities=new Dictionary<string,RightTextRangeIdentity>(StringComparer.Ordinal);var builder=new StringBuilder();
        void Add(string id,string value,IReadOnlyList<string>? sourceIds=null,StructuredTextRole role=StructuredTextRole.Unknown)
        {
            // RichEdit mouse hit-test indexes normalized LF characters. Build the ownership
            // ranges in that same coordinate system or every block after the first drifts.
            if(string.IsNullOrWhiteSpace(value))return;LogTextIntegrity(id,value);if(builder.Length>0)builder.Append("\n\n");var start=builder.Length;builder.Append(value);ranges[id]=(start,value.Length);
            var expected=(sourceIds??[]).Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
            TranslationUnitV2? unit=null;var reason="UNRESOLVED";
            if(_recognitionV2 is not null)
            {
                var byId=_recognitionV2.TranslationUnits.Where(x=>x.Id.Equals(id,StringComparison.Ordinal)).ToArray();
                if(byId.Length==1){unit=byId[0];reason="EXACT_DISPLAY_UNIT_ID";}
                else if(expected.Length>0)
                {
                    var exact=_recognitionV2.TranslationUnits.Where(x=>x.StableSourceIds.Distinct(StringComparer.Ordinal).OrderBy(s=>s,StringComparer.Ordinal).SequenceEqual(expected,StringComparer.Ordinal)).ToArray();
                    if(exact.Length==1){unit=exact[0];reason="EXACT_SOURCE_ID_SET";}
                }
            }
            var resolvedSources=unit?.StableSourceIds?.ToArray()??expected;
            var resolvedRegions=unit?.RegionIds?.ToArray()??Array.Empty<string>();
            identities[id]=new(start,value.Length,id,resolvedSources,resolvedRegions,unit is null?role:(StructuredTextRole)(int)unit.RoleType);
            RealPathDiagnosticTrace.Highlight(new{Timestamp=DateTimeOffset.Now,RightText=value,DisplayUnitId=id,ExpectedSourceIds=expected,ResolvedTranslationUnitId=unit?.Id??"UNRESOLVED",ResolvedRenderUnitIds=resolvedRegions,ResolutionReason=reason});
        }
        if(mode==TextViewMode.Hidden||_document is null)return new("",ranges,identities,scroll);
        if(_historyViewOnly&&mode==TextViewMode.Translation)
        {
            // Viewing history displays the saved text verbatim. Current Core
            // grouping may have changed, so surviving per-block matches must
            // not hide the old entries whose identities no longer exist.
            Add("SavedHistoryTranslation",_document.FullTranslation??"");
            return new(builder.ToString(),ranges,identities,scroll);
        }
        if(mode==TextViewMode.Translation&&!_document.Groups.Any(x=>!string.IsNullOrWhiteSpace(x.Translation)))
        {var value=!string.IsNullOrWhiteSpace(_document.FullTranslation)?(_document.TranslationStale?"[需要重新翻译]\r\n\r\n":"")+_document.FullTranslation:"尚未翻译";Add("FullTranslation",value);return new(builder.ToString(),ranges,identities,scroll);}
        if(mode==TextViewMode.Translation&&_translationSemanticGroups.Count>0)
        {foreach(var semantic in _translationSemanticGroups.OrderBy(x=>x.ReadingOrder)){var value=_translationByUnitId.GetValueOrDefault(semantic.Id,"");if(string.IsNullOrWhiteSpace(value))value=DocumentState.TranslationForSemanticGroup(_document,semantic);Add(semantic.Id,value,semantic.SourceBlockIds,(StructuredTextRole)(int)semantic.GroupType);}RealPathDiagnosticTrace.Stage("POST_DISPLAY_UNIT",identities.Select(x=>(object)new{Text=builder.ToString().Substring(x.Value.Start,x.Value.Length),SourceIds=x.Value.SourceIds,RegionIds=x.Value.StableRenderUnitIds,Role=x.Value.Role.ToString(),OwnerId=x.Key}));return new(builder.ToString(),ranges,identities,scroll);}
        foreach(var segment in _document.Groups.OrderBy(x=>x.ReadingOrder))Add(segment.GroupId,mode switch{TextViewMode.RawOcr=>RawTextForGroup(segment),TextViewMode.Translation=>segment.Translation,_=>segment.OrganizedText});
        return new(builder.ToString(),ranges,identities,scroll);
    }

    private void ApplyPreparedTextDocument(PreparedTextDocument document,bool finalTranslation=false)
    {
        _textRanges.Clear();foreach(var pair in document.Ranges)_textRanges[pair.Key]=pair.Value;
        _rightTextRangeIdentities.Clear();foreach(var pair in document.Identities)_rightTextRangeIdentities[pair.Key]=pair.Value;
        if(_text.Text!=document.Text){if(_highlightInteractionActive)_rightTextFullAssignmentDuringInteraction++;_appliedHighlightRangeIds.Clear();_text.Text=document.Text;_rightTextSetCount++;}
        if(finalTranslation&&!_finalTranslationTextCommitted){_finalTranslationTextCommitted=true;_rightTranslationFinalSetCount++;_rightPanelWidthAtFinal=_split.Panel2.ClientSize.Width;}
        ApplyTextHighlight();RestoreTextScrollPosition(document.Scroll);
    }

    private void ConfigureRightTextUrlInteraction()
    {
        _text.LinkClicked+=(_,e)=>OpenSafeHttpUrl(e.LinkText);
        _text.MouseDown+=(_,e)=>
        {
            if(e.Button!=MouseButtons.Right)return;
            _rightTextContextUrl=SafeHttpUrlAt(_text.Text,_text.GetCharIndexFromPosition(e.Location));
        };
        var menu=new ContextMenuStrip();
        var open=menu.Items.Add("用默认浏览器打开链接");
        var copy=menu.Items.Add("复制链接地址");
        menu.Opening+=(_,_)=>{var enabled=_rightTextContextUrl is not null;open.Enabled=enabled;copy.Enabled=enabled;};
        open.Click+=(_,_)=>{if(_rightTextContextUrl is not null)OpenSafeHttpUrl(_rightTextContextUrl);};
        copy.Click+=(_,_)=>{if(_rightTextContextUrl is not null)try{Clipboard.SetText(_rightTextContextUrl);}catch(ExternalException ex){AppLog.Write("url","Clipboard unavailable",ex);}};
        _text.ContextMenuStrip=menu;
    }

    internal static bool TryNormalizeSafeHttpUrl(string? value,out string normalized)
    {
        normalized="";
        var candidate=(value??"").Trim().TrimEnd('.',',',';',':','!','?',')',']','}','，','。','；','：','！','？');
        if(!Uri.TryCreate(candidate,UriKind.Absolute,out var uri))return false;
        if(uri.Scheme!=Uri.UriSchemeHttp&&uri.Scheme!=Uri.UriSchemeHttps)return false;
        if(string.IsNullOrWhiteSpace(uri.Host))return false;
        normalized=uri.AbsoluteUri;return true;
    }

    private static string? SafeHttpUrlAt(string text,int characterIndex)
    {
        foreach(Match match in Regex.Matches(text,"https?://[^\\s<>\\\"']+",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant))
            if(characterIndex>=match.Index&&characterIndex<=match.Index+match.Length&&TryNormalizeSafeHttpUrl(match.Value,out var url))return url;
        return null;
    }

    private static void OpenSafeHttpUrl(string value)
    {
        if(!TryNormalizeSafeHttpUrl(value,out var url))return;
        try{Process.Start(new ProcessStartInfo(url){UseShellExecute=true});}
        catch(Exception ex){AppLog.Write("url",$"Default browser open failed url={url}",ex);}
    }

    private void BuildTextView()
    {
        var prepared=PrepareTextDocument(SelectedTextMode);
        ApplyPreparedTextDocument(prepared);
        return;
#pragma warning disable CS0162
        var preservedScroll = GetTextScrollPosition();
        _textRanges.Clear();
        _text.Clear();
        if (SelectedTextMode == TextViewMode.Hidden) return;
        if (_document is null) { RestoreTextScrollPosition(preservedScroll); return; }
        if (SelectedTextMode == TextViewMode.Translation)
        {
            var mapped = _document.Groups.Any(x => !string.IsNullOrWhiteSpace(x.Translation));
            if (!mapped)
            {
                LogTextIntegrity("FullTranslation",_document.FullTranslation??"");
                _text.Text = !string.IsNullOrWhiteSpace(_document.FullTranslation)
                    ? (_document.TranslationStale ? "[需要重新翻译]\r\n\r\n" : "") + _document.FullTranslation
                    : "尚未翻译";
                RestoreTextScrollPosition(preservedScroll); return;
            }
        }
        if (SelectedTextMode == TextViewMode.Translation &&
            _translationSemanticGroups.Count > 0)
        {
            foreach (var semantic in _translationSemanticGroups.OrderBy(x => x.ReadingOrder))
            {
                var firstSource = semantic.SourceBlockIds.FirstOrDefault();
                var value = _document.Groups.FirstOrDefault(x => x.GroupId == firstSource)?.Translation ?? "";
                AppendTextRange(semantic.Id, value);
            }
            ApplyTextHighlight();
            RestoreTextScrollPosition(preservedScroll);
            return;
        }
        foreach (var segment in _document.Groups.OrderBy(x => x.ReadingOrder))
        {
            var value = SelectedTextMode switch
            {
                TextViewMode.RawOcr => RawTextForGroup(segment),
                TextViewMode.Translation => segment.Translation,
                _ => segment.OrganizedText
            };
            AppendTextRange(segment.GroupId, value);
        }
        ApplyTextHighlight();
        RestoreTextScrollPosition(preservedScroll);
#pragma warning restore CS0162
    }

    private void AppendTextRange(string id, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        LogTextIntegrity(id,value);
        if (_text.TextLength > 0) _text.AppendText(Environment.NewLine + Environment.NewLine);
        var start = _text.TextLength;
        _text.AppendText(value);_rightTextAppendCount++;
        _textRanges[id] = (start, value.Length);
    }

    private static void LogTextIntegrity(string source,string value)
    {
        foreach(var issue in TextIntegrityGuard.Inspect(value))
            AppLog.Write("text-integrity",$"source={source} code={issue.Code} index={issue.Index} message={issue.Message}");
    }

    private int GetFirstVisibleTextLine() => !_text.IsHandleCreated ? 0 :
        SendMessage(_text.Handle, EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero).ToInt32();

    private void SaveTextScroll(TextViewMode mode)
    {
        if (_text.IsHandleCreated) _textScrollLines[mode] = Math.Max(0, GetFirstVisibleTextLine());
    }

    private void RestoreTextScroll(TextViewMode mode)
    {
        if (!_text.IsHandleCreated) return;
        RestoreFirstVisibleLine(_textScrollLines.GetValueOrDefault(mode));
    }

    private void RestoreFirstVisibleLine(int wanted)
    {
        if (!_text.IsHandleCreated) return;
        SendMessage(_text.Handle, WmVScroll, new IntPtr(SbBottom), IntPtr.Zero);
        var maximum = Math.Max(0, GetFirstVisibleTextLine());
        var target = Math.Clamp(wanted, 0, maximum);
        var current = GetFirstVisibleTextLine();
        if (target != current) SendMessage(_text.Handle, EmLineScroll, IntPtr.Zero, new IntPtr(target - current));
        _text.Invalidate();
    }

    private string RawTextForGroup(SegmentGroup group)
    {
        if (_document?.OriginalSnapshot is null) return group.OriginalText;
        var ids = group.SourceSegmentIds.ToHashSet(StringComparer.Ordinal);
        return string.Join(Environment.NewLine, _document.OriginalSnapshot.Lines
            .Where(x => ids.Contains(x.SegmentId)).OrderBy(x => x.ReadingOrder).Select(x => x.Text));
    }

    private void UpdateImageLayout()
    {
        if (_picture.Image is null) return;
        ApplyImageLayout(_picture.Image.Size);
    }

    private void ApplyImageLayout(Size imageSize)
    {
        if(_updatingImageLayout)return;
        _updatingImageLayout=true;
        try
        {
            _previewLayoutCount++;
            var preservedScroll=_fitView?Point.Empty:CurrentImageScrollOffset();
            _imagePanel.AutoScrollPosition=Point.Empty;
            _imagePanel.AutoScrollMinSize=Size.Empty;
            _imagePanel.PerformLayout();
            var scale=_fitView?CalculateShrinkOnlyFitScale(imageSize,_imagePanel.ClientSize):_viewZoom;
            _picture.Dock=DockStyle.None;
            _picture.SizeMode=Math.Abs(scale-1f)<.0001f?PictureBoxSizeMode.Normal:PictureBoxSizeMode.StretchImage;
            _picture.Size=new(Math.Max(1,(int)Math.Round(imageSize.Width*scale)),Math.Max(1,(int)Math.Round(imageSize.Height*scale)));
            var extent=_fitView?Size.Empty:_picture.Size;
            _imagePanel.AutoScrollMinSize=extent;
            _imagePanel.PerformLayout();
            _picture.Location=new(Math.Max(0,(_imagePanel.ClientSize.Width-_picture.Width)/2),Math.Max(0,(_imagePanel.ClientSize.Height-_picture.Height)/2));
            if(!_fitView&&preservedScroll!=Point.Empty)_imagePanel.AutoScrollPosition=preservedScroll;
            if(extent!=_lastScrollExtent){_scrollExtentChangeCount++;_lastScrollExtent=extent;}
            if(Math.Abs(scale-_lastDisplayScale)>.0001f){_displayScaleChangeCount++;_lastDisplayScale=scale;}
            SyncZoomSelector();
        }
        finally{_updatingImageLayout=false;}
    }
    private Point CurrentImageScrollOffset()=>new(-_imagePanel.AutoScrollPosition.X,-_imagePanel.AutoScrollPosition.Y);
    private void HandleZoomSelectionChanged()
    {
        if(_syncingZoomSelector)return;
        if(_zoom.SelectedIndex==0)ResetFit();
        else if(_zoom.SelectedIndex==1)SetZoomScaleCentered(1f,null);
    }
    private void SyncZoomSelector()
    {
        _syncingZoomSelector=true;
        try
        {
            while(_zoom.Items.Count>2)_zoom.Items.RemoveAt(2);
            if(_fitView)_zoom.SelectedIndex=0;
            else if(Math.Abs(_viewZoom-1f)<.0001f)_zoom.SelectedIndex=1;
            else{_zoom.Items.Add($"{_viewZoom:P0}");_zoom.SelectedIndex=2;}
        }
        finally{_syncingZoomSelector=false;}
    }
    private void Picture_MouseWheel(object? sender,MouseEventArgs e)
    {var mods=ModifierKeys;var anchor=sender is Control control?_imagePanel.PointToClient(control.PointToScreen(e.Location)):(Point?)null;if(InputBindingMatcher.Wheel(_settings.PreviewZoomInBinding??InputBindingDefaults.ZoomIn,e.Delta,mods)){ZoomAround(1.12f,anchor);return;}if(InputBindingMatcher.Wheel(_settings.PreviewZoomOutBinding??InputBindingDefaults.ZoomOut,e.Delta,mods)){ZoomAround(1/1.12f,anchor);return;}_imagePanel.AutoScrollPosition=new Point(-_imagePanel.AutoScrollPosition.X,Math.Max(0,-_imagePanel.AutoScrollPosition.Y-e.Delta));}
    private void Picture_MouseDown(object? sender,MouseEventArgs e){if(InputBindingMatcher.Mouse(_settings.PreviewResetFitBinding??InputBindingDefaults.ResetFit,e.Button,ModifierKeys)){ResetFit();return;}if(e.Button==MouseButtons.Left&&!_fitView){_panning=true;_panStart=_imagePanel.PointToClient(((Control)sender!).PointToScreen(e.Location));_panScrollStart=new(-_imagePanel.AutoScrollPosition.X,-_imagePanel.AutoScrollPosition.Y);_picture.Cursor=Cursors.Hand;}}
    private void PicturePan_MouseMove(object? sender,MouseEventArgs e){if(!_panning)return;var now=_imagePanel.PointToClient(((Control)sender!).PointToScreen(e.Location));_imagePanel.AutoScrollPosition=new Point(Math.Max(0,_panScrollStart.X- (now.X-_panStart.X)),Math.Max(0,_panScrollStart.Y-(now.Y-_panStart.Y)));}
    private void Picture_MouseUp(object? sender,MouseEventArgs e){if(e.Button==MouseButtons.Left){_panning=false;_picture.Cursor=Cursors.Default;}}
    private void ZoomCentered(float factor)=>ZoomAround(factor,null);
    private void ZoomAround(float factor,Point? viewportAnchor)
    {
        var imageSize=_picture.Image?.Size??_original.Size;
        var oldScale=_fitView?CalculateShrinkOnlyFitScale(imageSize,_imagePanel.ClientSize):_viewZoom;
        SetZoomScaleCentered(Math.Clamp(oldScale*factor,MinViewZoom,MaxViewZoom),viewportAnchor);
    }
    private void SetZoomScaleCentered(float targetScale,Point? viewportAnchor)
    {
        var imageSize=_picture.Image?.Size??_original.Size;
        if(_picture.Image is null){_fitView=false;_viewZoom=Math.Clamp(targetScale,MinViewZoom,MaxViewZoom);SyncZoomSelector();return;}
        var vw=Math.Max(1,_imagePanel.ClientSize.Width);var vh=Math.Max(1,_imagePanel.ClientSize.Height);
        var anchor=viewportAnchor??new Point(vw/2,vh/2);
        var oldScale=_fitView?CalculateShrinkOnlyFitScale(imageSize,new(vw,vh)):_viewZoom;
        var oldLeft=_picture.Image is null?Math.Max(0,(vw-(int)Math.Round(imageSize.Width*oldScale))/2):_picture.Left;
        var oldTop=_picture.Image is null?Math.Max(0,(vh-(int)Math.Round(imageSize.Height*oldScale))/2):_picture.Top;
        var anchorImageX=(anchor.X-oldLeft)/Math.Max(.001f,oldScale);var anchorImageY=(anchor.Y-oldTop)/Math.Max(.001f,oldScale);
        _fitView=false;_viewZoom=Math.Clamp(targetScale,MinViewZoom,MaxViewZoom);
        _imagePanel.AutoScrollPosition=Point.Empty;UpdateImageLayout();_imagePanel.PerformLayout();
        var desiredX=_picture.Left+anchorImageX*_viewZoom-anchor.X;var desiredY=_picture.Top+anchorImageY*_viewZoom-anchor.Y;
        var shiftX=Math.Max(0,(int)Math.Ceiling(-desiredX));var shiftY=Math.Max(0,(int)Math.Ceiling(-desiredY));
        if(shiftX>0||shiftY>0)
        {
            _picture.Location=new(_picture.Left+shiftX,_picture.Top+shiftY);desiredX+=shiftX;desiredY+=shiftY;
            var extent=new Size(Math.Max(_picture.Width,_picture.Right),Math.Max(_picture.Height,_picture.Bottom));
            _imagePanel.AutoScrollMinSize=extent;_imagePanel.PerformLayout();
            if(extent!=_lastScrollExtent){_scrollExtentChangeCount++;_lastScrollExtent=extent;}
        }
        _imagePanel.AutoScrollPosition=new(Math.Max(0,(int)Math.Round(desiredX)),Math.Max(0,(int)Math.Round(desiredY)));
    }
    private void ResetFit(){_fitView=true;_viewZoom=1;_imagePanel.AutoScrollPosition=Point.Empty;_imagePanel.AutoScrollMinSize=Size.Empty;UpdateImageLayout();SyncZoomSelector();}
    internal static float CalculateShrinkOnlyFitScale(Size image,Size viewport)=>image.Width<=0||image.Height<=0?1f:Math.Min(1f,Math.Min(Math.Max(1,viewport.Width)/(float)image.Width,Math.Max(1,viewport.Height)/(float)image.Height));

    private void UpdateSplitter()
    {
        if (_adjusting || !_split.IsHandleCreated || _split.Panel2Collapsed || _split.ClientSize.Width < 680) return;
        _adjusting = true;
        try
        {
            _split.Panel1MinSize = 0; _split.Panel2MinSize = 0;
            _split.SplitterDistance = Math.Clamp((int)(_split.ClientSize.Width * .72), 0, _split.ClientSize.Width - _split.SplitterWidth);
            _split.Panel1MinSize = 440; _split.Panel2MinSize = 220;
        }
        finally { _adjusting = false; }
    }

    private void SetStatus(string value)
    {
        if (!CanUse()) return;
        var primary = value.Split('｜', 2)[0].Trim();
        _status.Text = _document is null || _historyViewOnly
            ? primary
            : $"{primary}｜OCR {_document.Metrics.OcrMs}ms｜API {_document.Metrics.ApiWaitMs}ms｜总耗时 {(_wallClock?.EndedAt is not null?_wallClock.ElapsedMs:_document.Metrics.TotalMs)}ms";
        var execution=_historyViewOnly ? _processingProvenance?.BackgroundExecution : _processingTask?.FinalIdentity;
        var treatment=_historyViewOnly ? _processingProvenance?.BackgroundTreatment : _processingTask?.Treatment;
        var device=execution?.ActualDevice switch {
            "Lightweight"=>"均衡清理", "cuda"=>"GPU", "cpu"=>"CPU", "CpuSurfaceOnly"=>"CPU（表面恢复）",
            "NotExecuted"=>_wallClock?.EndedAt is null ? $"本次 {(execution.RequestedDevice=="Gpu"?"GPU":"CPU")}" : "未调用背景模型",
            _=>_historyViewOnly ? "历史设备未记录" : null };
        if(treatment==BackgroundTreatment.Lightweight)device="均衡清理";
        if(device is not null)_status.Text+=$"｜{device}";
    }
    private void SetTranslationBusy(bool busy)
    {
        TranslationService.DiagnosticLog($"SetTranslationBusy before busy={busy} disposed={IsDisposed} disposing={Disposing} invokeRequired={(IsHandleCreated ? InvokeRequired : false)}");
        _busy = busy;
        if (CanUse() && _retryButton is not null) _retryButton.Enabled = !busy;
        if (!busy)
        {
            _operation?.Dispose();
            _operation = null;
        }
        TranslationService.DiagnosticLog($"SetTranslationBusy after busy={busy} disposed={IsDisposed} disposing={Disposing}");
    }
    private void ClearTranslationError()
    {
        _lastTranslationError = null;
        if (_errorDetailsButton is not null) _errorDetailsButton.Enabled = false;
    }
    private void SetTranslationError(string status, string? details)
    {
        _lastTranslationError = details;
        if (_errorDetailsButton is not null) _errorDetailsButton.Enabled = !string.IsNullOrWhiteSpace(details);
        SetStatus($"{status}｜{_document?.Metrics}");
    }
    private void ShowTranslationErrorDetails()
    {
        if (string.IsNullOrWhiteSpace(_lastTranslationError)) return;
        AppDialog.Show(this, "翻译错误详情", "翻译失败，请查看下方详细信息。", AppDialogKind.Error, _lastTranslationError);
    }
    private bool CanUse() => !_closing && !IsDisposed && !Disposing && IsHandleCreated;
    private void ReplaceOperation()
    {
        _operation?.Cancel(); _operation?.Dispose();
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
    }
    private static float HorizontalOverlap(RectangleF a, RectangleF b)
    {
        var width = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        return width / Math.Max(1, Math.Min(a.Width, b.Width));
    }
    private void CancelTranslation()
    {
        if (_translationTask is not { IsCompleted: false }) return;
        _operation?.Cancel();
        SetStatus("用户取消");
    }

    private void SetHover(string? segmentId,RightTextRangeIdentity? rightIdentity=null)
    {
        if (_lockedSegmentId is not null) return;
        if (_hoverSegmentId == segmentId&&ReferenceEquals(_hoverRightIdentity,rightIdentity)) return;
        _hoverSegmentId = segmentId;_hoverRightIdentity=rightIdentity;
        ApplyTextHighlight(); _picture.Invalidate();
    }

    private string? ActiveSegmentId => _lockedSegmentId ?? _hoverSegmentId;
    private RightTextRangeIdentity? ActiveRightIdentity=>_lockedRightIdentity??_hoverRightIdentity;

    private void Text_MouseMove(object? sender, MouseEventArgs e)
    {
        var index = _text.GetCharIndexFromPosition(e.Location);
        var range=_rightTextRangeIdentities.Values.FirstOrDefault(x=>index>=x.Start&&index<x.Start+x.Length);
        _highlightInteractionActive=true;try{SetHover(range?.TranslationUnitId,range);}finally{_highlightInteractionActive=false;}
    }
    private void Text_Click(object? sender, MouseEventArgs e)
    {
        // Resolve ownership from the pixel the user actually clicked. SelectionStart can
        // still point at the previous native selection (the real 42kg -> Shuri failure).
        var index = _text.GetCharIndexFromPosition(e.Location);
        var identity=_rightTextRangeIdentities.Values.FirstOrDefault(x=>index>=x.Start&&index<x.Start+x.Length);var id=identity?.TranslationUnitId;
        if(identity is null){RealPathDiagnosticTrace.Highlight(new{Timestamp=DateTimeOffset.Now,Event="RIGHT_CLICK",Click=e.Location,CharIndex=index,Resolution="UNRESOLVED"});return;}
        _highlightInteractionActive=true;try{
        _lockedSegmentId = _lockedSegmentId == id ? null : id;
        _lockedRightIdentity=_lockedSegmentId is null?null:identity;_hoverSegmentId=id;_hoverRightIdentity=identity;
        var selectedSources=identity.SourceIds.ToHashSet(StringComparer.Ordinal);var selectedRender=identity.StableRenderUnitIds.ToHashSet(StringComparer.Ordinal);
        var finalGeometry=_recognitionV2?.Regions.Where(x=>x.SourceBlockIds.Any(selectedSources.Contains)||selectedRender.Contains(x.RegionId)).Select(x=>new{x.RegionId,x.TranslationUnitId,x.SourceBlockIds,x.SourceLinePolygons,x.TranslationRenderRect}).ToArray()??[];
        RealPathDiagnosticTrace.Highlight(new{Timestamp=DateTimeOffset.Now,Event="RIGHT_CLICK",Click=e.Location,CharIndex=index,RightText=_text.Text.Substring(identity.Start,identity.Length),identity.TranslationUnitId,identity.SourceIds,identity.StableRenderUnitIds,FinalHighlightGeometry=finalGeometry});
        ApplyTextHighlight(); _picture.Invalidate();
        }finally{_highlightInteractionActive=false;}
    }
    private void Picture_MouseMove(object? sender, MouseEventArgs e){PicturePan_MouseMove(sender,e);if(!_panning)SetHover(SegmentAtPicturePoint(e.Location));}
    private void Picture_Click(object? sender, EventArgs e)
    {
        var id = SegmentAtPicturePoint(_picture.PointToClient(Cursor.Position));
        _lockedSegmentId = _lockedSegmentId == id ? null : id;
        _lockedRightIdentity=null;_hoverRightIdentity=null;_hoverSegmentId = id; ApplyTextHighlight(); _picture.Invalidate();
    }
    private string? SegmentAtPicturePoint(Point point)
    {
        if (_document is null || _picture.Image is null) return null;
        var scale = Math.Min(_picture.ClientSize.Width / (float)_picture.Image.Width, _picture.ClientSize.Height / (float)_picture.Image.Height);
        var ox = (_picture.ClientSize.Width - _picture.Image.Width * scale) / 2f;
        var oy = (_picture.ClientSize.Height - _picture.Image.Height * scale) / 2f;
        var source = new PointF((point.X - ox) / scale, (point.Y - oy) / scale);
        if (_recognitionV2 is not null)
        {
            var mode=_displayedImageMode;
            var region=_recognitionV2.Regions.Where(x=>RegionDisplayMapping.Resolve(x,mode)?.Contains(source)==true).OrderBy(x=>{var b=RegionDisplayMapping.Resolve(x,mode)!.Value;return b.Width*b.Height;}).FirstOrDefault();
            return region?.TranslationUnitId ?? region?.RegionId;
        }
        return _document.Groups.FirstOrDefault(x => x.Bounds.Contains(source))?.GroupId;
    }
    private void Picture_Paint(object? sender, PaintEventArgs e)
    {
        _paintCount++;
        if(_awaitingFirstFinalPaint)
        {
            _awaitingFirstFinalPaint=false;RealExeE2ETrace.Mark("T18 FirstFinalPaint");
            _stableFrameTimer.Stop();_stableFrameTimer.Tick-=CompleteStableFrameTrace;_stableFrameTimer.Tick+=CompleteStableFrameTrace;_stableFrameTimer.Start();
        }
        if (_document is null || _picture.Image is null) return;
        var scale = Math.Min(_picture.ClientSize.Width / (float)_picture.Image.Width, _picture.ClientSize.Height / (float)_picture.Image.Height);
        var ox = (_picture.ClientSize.Width - _picture.Image.Width * scale) / 2f;
        var oy = (_picture.ClientSize.Height - _picture.Image.Height * scale) / 2f;
        RectangleF Map(RectangleF value) => new(ox + value.X * scale, oy + value.Y * scale, value.Width * scale, value.Height * scale);
        PointF[] MapPolygon(PointF[] value) => value.Select(p => new PointF(ox + p.X * scale, oy + p.Y * scale)).ToArray();
        if (_visualAnalysisV2 is not null && _visibleV2Layers.Contains(RegionOverlayLayer.VisualRegions))
        {
            using var visualPen = new Pen(Color.Magenta, 2) { DashStyle = DashStyle.Dash };
            foreach (var visual in _visualAnalysisV2.VisualRegions) { var polygon = MapPolygon(visual.Polygon); if (polygon.Length >= 3) e.Graphics.DrawPolygon(visualPen, polygon); }
        }
        if (_recognitionV2 is not null && _visibleV2Layers.Contains(RegionOverlayLayer.OcrBlocks))
        {
            using var ocrPen = new Pen(Color.DeepSkyBlue, 1);
            foreach (var block in _recognitionV2.Regions.SelectMany(x => x.DetectedBlocks).DistinctBy(x => x.Id))
            { var polygon = MapPolygon(block.Polygon.Length >= 3 ? block.Polygon : GeometryV2.RectanglePolygon(block.BoundingBox)); if (polygon.Length >= 3) e.Graphics.DrawPolygon(ocrPen, polygon); }
        }
        if (_recognitionV2 is not null && _visibleV2Layers.Contains(RegionOverlayLayer.RecognitionRegions))
        {
            using var regionPen = new Pen(Color.FromArgb(210, 80, 220, 130), 2);
            using var manualPen = new Pen(Color.Gold, 2);
            using var roleFont = new Font("Microsoft YaHei UI", 8f, FontStyle.Bold);
            foreach (var region in _recognitionV2.Regions)
            {
                var polygon = MapPolygon(region.Polygon); if (polygon.Length < 3) continue;
                e.Graphics.DrawPolygon(region.ManualOverrideFlags == RegionManualOverrideFlags.None ? regionPen : manualPen, polygon);
                if (_visibleV2Layers.Contains(RegionOverlayLayer.RoleLabels))
                {
                    var order = _visibleV2Layers.Contains(RegionOverlayLayer.ReadingOrder) ? $"#{region.ReadingOrder} " : "";
                    var label = $"{order}{region.RoleType} {region.RegionId[4..10]}";
                    var at = polygon.OrderBy(p => p.Y).ThenBy(p => p.X).First(); var size = e.Graphics.MeasureString(label, roleFont);
                    e.Graphics.FillRectangle(Brushes.Black, at.X, Math.Max(0, at.Y - size.Height), size.Width, size.Height);
                    e.Graphics.DrawString(label, roleFont, Brushes.Lime, at.X, Math.Max(0, at.Y - size.Height));
                }
            }
        }
        if (_recognitionV2 is not null && _visibleV2Layers.Contains(RegionOverlayLayer.TranslationMapping))
        {
            using var mappingPen = new Pen(Color.Cyan, 3) { DashStyle = DashStyle.Dot };
            foreach (var region in _recognitionV2.Regions.Where(x => !string.IsNullOrWhiteSpace(x.TranslationUnitId)))
            {if(_displayedImageMode==ImageViewMode.Translated){if(region.TranslationRenderRect is{} rr)e.Graphics.DrawRectangle(mappingPen,Rectangle.Round(Map(rr)));}else{var polygon=MapPolygon(region.Polygon);if(polygon.Length>=3)e.Graphics.DrawPolygon(mappingPen,polygon);}}
        }
        if (_recognitionV2 is not null && _visibleV2Layers.Contains(RegionOverlayLayer.Coverage))
        {
            foreach (var line in _recognitionV2.SourceLineCoverage.Where(x => x.Polygon.Length >= 3))
            {
                var color = line.Disposition switch { SourceLineDisposition.AssignedToTranslationUnit => Color.Lime,
                    SourceLineDisposition.PreserveOriginal => Color.DeepSkyBlue, SourceLineDisposition.IgnoredWithReason => Color.Gold, _ => Color.Red };
                using var coveragePen = new Pen(color, line.Disposition == SourceLineDisposition.Unaccounted ? 5 : 3);
                var polygon = MapPolygon(line.Polygon); if (polygon.Length >= 3) e.Graphics.DrawPolygon(coveragePen, polygon);
            }
        }
        if (_recognitionV2 is not null && _visibleV2Layers.Contains(RegionOverlayLayer.SemanticGroups))
        {
            using var groupPen = new Pen(Color.Gold, 2) { DashStyle = DashStyle.DashDot };
            foreach (var unit in _recognitionV2.TranslationUnits)
            {
                var members = _recognitionV2.Regions.Where(x => unit.RegionIds.Contains(x.RegionId)).Select(x => x.BoundingBox).ToArray();
                if (members.Length > 0) e.Graphics.DrawRectangle(groupPen, Rectangle.Round(Map(members.Aggregate(RectangleF.Union))));
            }
        }
        if (_showGroupBounds)
        {
            using var sourcePen = new Pen(Color.Gold, 2); using var renderPen = new Pen(Color.Lime, 2) { DashStyle = DashStyle.Dash };
            using var labelFont = new Font("Microsoft YaHei UI", 8f); using var labelBg = new SolidBrush(Color.FromArgb(210, 0, 0, 0));
            foreach (var group in _document.Groups)
            {
                var source = Map(group.SourceRectangle); var render = Map(group.RenderRectangle.IsEmpty ? group.SourceRectangle : group.RenderRectangle);
                e.Graphics.DrawRectangle(sourcePen, Rectangle.Round(source)); e.Graphics.DrawRectangle(renderPen, Rectangle.Round(render));
                var label = $"{group.GroupId} {group.GroupType} S:{group.SourceRectangle.X:0},{group.SourceRectangle.Y:0},{group.SourceRectangle.Width:0},{group.SourceRectangle.Height:0} R:{group.RenderRectangle.X:0},{group.RenderRectangle.Y:0},{group.RenderRectangle.Width:0},{group.RenderRectangle.Height:0}";
                var size = e.Graphics.MeasureString(label, labelFont); e.Graphics.FillRectangle(labelBg, source.X, Math.Max(0, source.Y - size.Height), size.Width, size.Height); e.Graphics.DrawString(label, labelFont, Brushes.White, source.X, Math.Max(0, source.Y - size.Height));
            }
        }
        var ids = ActiveHighlightSourceBlockIds();
        if (ids.Count == 0) return;
        using var pen = new Pen(Color.Cyan, 3);
        if (_recognitionV2 is not null)
        {
            var selectedSourceIds=ids.ToHashSet(StringComparer.Ordinal);
            var selectedRenderIds=(ActiveRightIdentity?.StableRenderUnitIds??[]).ToHashSet(StringComparer.Ordinal);
            foreach (var region in _recognitionV2.Regions.Where(x =>
                         x.SourceBlockIds.Any(selectedSourceIds.Contains)||selectedRenderIds.Contains(x.RegionId)))
            {
                if(_displayedImageMode==ImageViewMode.Translated){if(region.TranslationRenderRect is{} rr)e.Graphics.DrawRectangle(pen,Rectangle.Round(Map(rr)));continue;}
                for(var i=0;i<region.SourceBlockIds.Count;i++)
                {
                    if(!selectedSourceIds.Contains(region.SourceBlockIds[i]))continue;
                    var line=i<region.SourceLinePolygons.Count?region.SourceLinePolygons[i]:region.Polygon;
                    var polygon=MapPolygon(line);if(polygon.Length>=3)e.Graphics.DrawPolygon(pen,polygon);
                }
            }
        }
        else foreach (var segment in _document.Groups.Where(x => ids.Contains(x.GroupId, StringComparer.Ordinal)))
        {var rect=_displayedImageMode==ImageViewMode.Translated?segment.RenderRectangle:segment.SourceRectangle;if(!rect.IsEmpty)e.Graphics.DrawRectangle(pen,Rectangle.Round(Map(rect)));}
    }

    private void CompleteStableFrameTrace(object? sender,EventArgs e)
    {
        _stableFrameTimer.Stop();_lastE2ESnapshot=RealExeE2ETrace.Complete(CurrentUiCounters());
    }

    private PreviewUiCounters CurrentUiCounters()=>new(_rightTextSetCount,_rightTextAppendCount,_imageAssignCount,
        _previewLayoutCount,_invalidateCount,_refreshCount,_paintCount,_scrollExtentChangeCount,
        _displayScaleChangeCount,_layoutVersion,0,_rightTranslationFinalSetCount,_rightPanelWidthChangeAfterFinalText,
        _rightFontChangeAfterFinalText,_rightLayoutEventAfterFinalText,_rightScrollExtentChangeAfterFinalText);
    private void ApplyTextHighlight()
    {
        var selected = ActiveSegmentId;
        var scrollPosition = GetTextScrollPosition();
        var oldStart = _text.SelectionStart; var oldLength = _text.SelectionLength;
        IEnumerable<string> rangeIds = [];
        if (selected is not null)
        {
        if (SelectedTextMode == TextViewMode.Translation)
            {
                var semanticId=ActiveRightIdentity?.TranslationUnitId??_translationSemanticGroups.FirstOrDefault(x => x.Id == selected ||
                    x.SourceBlockIds.Contains(selected, StringComparer.Ordinal))?.Id??selected;
                rangeIds = [semanticId];
            }
            else rangeIds = ActiveHighlightSourceBlockIds();
        }
        var next=rangeIds.Where(_textRanges.ContainsKey).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        // Same identity is a strict no-op; it is not a formatting update.
        if(_appliedHighlightRangeIds.SetEquals(next))return;
        foreach(var rangeId in _appliedHighlightRangeIds.Except(next).ToArray())
        {
            if(!_textRanges.TryGetValue(rangeId,out var oldRange))continue;
            _text.Select(oldRange.Start,oldRange.Length);_text.SelectionBackColor=_text.BackColor;_highlightRangeFormatCount++;
        }
        foreach (var rangeId in next.Except(_appliedHighlightRangeIds))
        {
            if (!_textRanges.TryGetValue(rangeId, out var range)) continue;
            _text.Select(range.Start, range.Length);
            _text.SelectionBackColor = Color.FromArgb(120, 210, 235);_highlightRangeFormatCount++;
        }
        _appliedHighlightRangeIds.Clear();_appliedHighlightRangeIds.UnionWith(next);
        _text.Select(Math.Min(oldStart, _text.TextLength), Math.Min(oldLength, Math.Max(0, _text.TextLength - oldStart)));
        RestoreTextScrollPosition(scrollPosition);
    }

    private IReadOnlyList<string> ActiveHighlightSourceBlockIds()
    {
        var selected=ActiveSegmentId;if(string.IsNullOrWhiteSpace(selected))return [];
        if(ActiveRightIdentity is { } right)return right.SourceIds;
        if(_recognitionV2 is not null)
        {
            var unit=_recognitionV2.TranslationUnits.FirstOrDefault(x=>x.Id==selected||x.RegionIds.Contains(selected,StringComparer.Ordinal)||x.StableSourceIds.Contains(selected,StringComparer.Ordinal));
            return unit is null?[selected]:unit.StableSourceIds.Count>0?unit.StableSourceIds:[unit.Id];
        }
        return DocumentState.ResolveStructuredHighlightSourceBlockIds(selected,SelectedTextMode,_structuredTextGroups);
    }

    private NativePoint GetTextScrollPosition()
    {
        var position = new NativePoint();
        if (_text.IsHandleCreated) SendMessage(_text.Handle, EmGetScrollPos, IntPtr.Zero, ref position);
        return position;
    }

    private void RestoreTextScrollPosition(NativePoint position)
    {
        if (_text.IsHandleCreated) SendMessage(_text.Handle, EmSetScrollPos, IntPtr.Zero, ref position);
    }

    private Bitmap CurrentImage => _displayedImageMode==ImageViewMode.Translated&&_translatedDisplay is not null?_translatedDisplay:_original;
    private void RequestSelfCapture()
    {
        if (!Visible || IsDisposed || !IsHandleCreated) { SetStatus("当前Preview窗口不可用"); return; }
        Bitmap? clone = null;
        try
        {
            clone = CaptureSelfWindow(rectangle =>
            {
                var bitmap = new Bitmap(rectangle.Width, rectangle.Height, PixelFormat.Format32bppArgb);
                using var graphics = Graphics.FromImage(bitmap);
                graphics.CopyFromScreen(rectangle.Location, Point.Empty, rectangle.Size, CopyPixelOperation.SourceCopy);
                return bitmap;
            });
            SelfCaptureRequested?.Invoke(this, clone);
            clone = null;
        }
        catch (Exception ex)
        {
            SetStatus("截取当前Preview失败");
            TranslationService.DiagnosticLog($"self-capture failed type={ex.GetType().FullName} message={ex.Message}");
        }
        finally { clone?.Dispose(); }
    }

    internal Bitmap CaptureSelfWindow(Func<Rectangle, Bitmap> capture)
    {
        if (!TemporaryWindowCaptureAllowance.TryBegin(this, out var allowance, out var error))
            throw new InvalidOperationException(error);
        Bitmap? bitmap = null;
        try
        {
            var rectangle = Bounds;
            if (rectangle.Width <= 0 || rectangle.Height <= 0)
                throw new InvalidDataException("Preview window bounds are empty.");
            bitmap = capture(rectangle);
            if (bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                bitmap.Dispose();
                bitmap = null;
                throw new InvalidDataException("Self-capture returned an empty bitmap.");
            }
        }
        finally
        {
            allowance!.Restore();
            if (!allowance.RestoreSucceeded)
            {
                bitmap?.Dispose();
                bitmap = null;
                TranslationService.DiagnosticLog($"self-capture WDA restore failed: {allowance.RestoreError}");
            }
        }
        if (bitmap is null)
            throw new InvalidOperationException($"Self-capture WDA restore failed: {allowance.RestoreError}");
        return bitmap;
    }
    private void OpenOcrComparison()
    {
        if (_original.Width <= 0 || _original.Height <= 0)
        {
            SetStatus("当前原始图片不可用");
            return;
        }
        var compare = new OcrComparisonForm(_ocrRuntimeManager, _original,
            OcrComparisonMode.Standard, autoRunPrimary: true);
        compare.Show();
        compare.BringToFront();
    }
    internal Bitmap CloneVisibleImageForTest() => new(CurrentImage);
    internal Bitmap CloneSourceImageForTest()=>new(_original);
    internal Bitmap CapturePictureSurfaceForSmoke()
    {
        if(_picture.Image is null)throw new InvalidOperationException("Preview image is not visible.");
        var surface=new Bitmap(Math.Max(1,_picture.ClientSize.Width),Math.Max(1,_picture.ClientSize.Height),PixelFormat.Format32bppArgb);
        _picture.DrawToBitmap(surface,new Rectangle(Point.Empty,surface.Size));
        return surface;
    }
    internal bool OcrBitmapIsIndependentForSmoke=>_ocrSource is not null&&!ReferenceEquals(_original,_ocrSource);
    internal (Size Ui,Size Ocr) BitmapOwnershipSizesForSmoke=>(_original.Size,_ocrSource?.Size??Size.Empty);
    internal Task<bool> PrepareOcrSnapshotForSmoke()=>EnsureOcrSnapshotAsync();
    internal Bitmap? CloneTranslatedImageForTest()=>_translatedDisplay is null?null:new(_translatedDisplay);
    internal string CopyTextValueForTest()=>_text.Focused&&_text.SelectionLength>0?_text.SelectedText:CurrentText;
    private string CurrentText => _text.Text;
    private void CopySpecificImage(Image image,string success){try{ClipboardHelper.SetImage(image);SetStatus(success);}catch(Exception ex){AppDialog.Show(this,"复制失败","无法复制图片。",AppDialogKind.Error,ex.Message);}}
    private void CopyImage() { try { ClipboardHelper.SetImage(CurrentImage); SetStatus("已复制图片"); } catch (Exception ex) { AppDialog.Show(this, "复制失败", "无法复制当前图片。", AppDialogKind.Error, ex.Message); } }
    private void CopyText()
    {
        var value = _text.Focused && _text.SelectionLength > 0 ? _text.SelectedText : CurrentText;
        if (!string.IsNullOrWhiteSpace(value)) ClipboardHelper.SetText(value);
    }
    internal void SaveDisplayedImageForDataRootVerification(string path)
    {
        if(!AppDataPaths.IsDataRootVerification || !AppDataPaths.HasExplicitRoot ||
            !Path.GetFullPath(path).StartsWith(AppDataPaths.OutputRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This output probe requires the dedicated verification directory.");
        SaveDisplayedImage(path);
    }
    private void SaveDisplayedImage(string path) => CurrentImage.Save(path,
        Path.GetExtension(path).Equals(".jpg",StringComparison.OrdinalIgnoreCase)?ImageFormat.Jpeg:ImageFormat.Png);
    private void SaveImage()
    {
        using var dialog = new SaveFileDialog { Filter = "PNG 图片|*.png|JPEG 图片|*.jpg", FileName = $"译文截图_{DateTime.Now:yyyyMMdd_HHmmss}.png" };
        if(AppDataPaths.HasExplicitRoot)dialog.InitialDirectory=AppDataPaths.OutputRoot;
        if (ShowPreviewDialog(dialog) != DialogResult.OK) return;
        SaveDisplayedImage(dialog.FileName);
    }
    private void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        if(InputBindingMatcher.Keyboard(_settings.PreviewZoomInBinding??InputBindingDefaults.ZoomIn,e.KeyCode,e.Modifiers)){ZoomCentered(1.12f);e.SuppressKeyPress=true;return;}
        if(InputBindingMatcher.Keyboard(_settings.PreviewZoomOutBinding??InputBindingDefaults.ZoomOut,e.KeyCode,e.Modifiers)){ZoomCentered(1/1.12f);e.SuppressKeyPress=true;return;}
        if(InputBindingMatcher.Keyboard(_settings.PreviewResetFitBinding??InputBindingDefaults.ResetFit,e.KeyCode,e.Modifiers)){ResetFit();e.SuppressKeyPress=true;return;}
        if (e.KeyCode == Keys.Escape)
        {
            _lockedSegmentId = _hoverSegmentId = null;
            ApplyTextHighlight(); _picture.Invalidate();
            e.Handled = true; return;
        }
        if (!e.Control || e.KeyCode != Keys.C) return;
        if (_text.Focused) CopyText(); else CopyImage();
        e.Handled = e.SuppressKeyPress = true;
    }
    internal void SetOcrSelectorProgrammaticallyForSmoke(OcrEngineKind engine) =>
        _ocrEngineSelector.SelectedIndex = OcrEngineUiMapping.ToSelectedIndex(engine);
    internal void CommitOcrSelectorForSmoke(OcrEngineKind engine)
    {
        _ocrEngineSelector.SelectedIndex = OcrEngineUiMapping.ToSelectedIndex(engine);
        HandleOcrEngineSelectionCommitted();
    }
    internal void StartOcrForSmoke() => StartOcr(false);
    internal Task PrepareAndStartOcrForSmokeAsync() => StartOcrWhenSnapshotReadyAsync(false);
    internal Task? OcrTaskForSmoke => Environment.GetEnvironmentVariable("SCREENSHOT_TRANSLATOR_V2_FROZEN_TEST") == "1"
        ? Task.CompletedTask : _ocrTask;
    internal int ActiveOcrOperationsForSmoke => _ocrTask is { IsCompleted: false } ? 1 : 0;
    internal string CurrentOcrRequestIdForSmoke => _currentOcrRequestId;
    internal long OcrGenerationForSmoke => _ocrGeneration;
    internal bool IsOcrResultCurrentForSmoke(OcrEngineKind engine, string requestId, long generation) =>
        IsCurrentOcrRequest(engine, requestId, generation);
    internal string ImageSessionIdForSmoke => _imageSessionId;
    internal OcrEngineKind DisplayedOcrEngineForSmoke => _displayedOcrEngine;
    internal OcrEngineKind SelectedOcrEngineForSmoke => _settings.OcrEngine;
    internal void SetDocumentForSmoke(OcrDocument document)
    {
        _ocrOperation?.Cancel();_document=document;
        _corePipelineV2=document.RawLines.Count>0?BuildCorePipelineV2(document.RawLines,_original.Size):null;
        _translationSemanticGroups=[];_structuredTextGroups=[];UpdateIndependentViews();
    }
    internal void RestoreHistoryState(string sourceText,string translationText,Bitmap? translatedImage,
        HistoryCoreSnapshot? coreSnapshot=null)
    {
        _suppressAutoOcrForE2E=true;
        _historyViewOnly=true;_historySnapshot=coreSnapshot;_processingProvenance=coreSnapshot?.Provenance;
        _displayedOcrEngine=_settings.OcrEngine;
        if(coreSnapshot is not null&&coreSnapshot.Lines.Count>0)
        {
            _corePipelineV2=coreSnapshot.Rebuild(_original.Size);
            var result=new OcrEngineResult{EngineActual=_settings.OcrEngine,ModelName="History Core Snapshot",RawText=sourceText};
            _document=BuildCoreProductDocument(_corePipelineV2,result);
            foreach(var group in _document.Groups)
            {
                var state=_corePipelineV2.Translations[group.GroupId];
                group.Translation=state.TranslatedText;
                group.HasReliableTranslation=state.State==BlockTranslationState.Accepted;
            }
            _document.FullTranslation=translationText;_document.TranslationStale=false;
            _historyRequiresReOcr=false;
        }
        else
        {
            // Older history entries did not persist OCR geometry. Keep their bitmap/text
            // viewable, but never pretend that a full-image synthetic box is safe to render.
            // A re-translate action first runs RapidOCR and builds a real Core state.
            var bounds=new RectangleF(1,1,Math.Max(1,_original.Width-2),Math.Max(1,_original.Height-2));
            _corePipelineV2=null; // Saved text is a view, not invented current OCR geometry.
            var group=new SegmentGroup{GroupId="history-restored",GroupType=SegmentType.Body,SourceSegmentIds=["history-restored"],
                OriginalText=sourceText,OrganizedText=sourceText,Translation=translationText,Bounds=bounds,RenderRectangle=bounds,
                ReadingOrder=0,HasReliableTranslation=!string.IsNullOrWhiteSpace(translationText),CanOverlay=false};
            _document=new OcrDocument{Text=sourceText,RawText=sourceText,Groups=[group],SourceWidth=_original.Width,
                SourceHeight=_original.Height,FullTranslation=translationText,SegmentMappingReliable=true,SelectedCandidate="History legacy migration"};
            _historyRequiresReOcr=true;
        }
        _translatedDisplay?.Dispose();_translatedDisplay=translatedImage is null?null:new Bitmap(translatedImage);
        _allowUiImageDisplay=true;_preferredImageMode=_translatedDisplay is null?ImageViewMode.Original:ImageViewMode.Translated;
        _selectedTextMode=string.IsNullOrWhiteSpace(translationText)?TextViewMode.Organized:TextViewMode.Translation;
        SelectTextMode(_selectedTextMode);UpdateIndependentViews();
        SetStatus("正在查看历史结果");
        AppLog.Write("history",$"PREVIEW_IMAGE_ASSIGNED source={_original.Width}x{_original.Height} translated={_translatedDisplay is not null} CORE_SNAPSHOT_RESTORED={coreSnapshot is not null} LEGACY_REOCR_REQUIRED={_historyRequiresReOcr}");
    }
    internal (bool ImageVisible,bool TextVisible,bool HasTranslatedImage,bool SnapshotDeferred) HistoryRestoreStateForSmoke =>
        (_picture.Image is not null,!string.IsNullOrWhiteSpace(_text.Text),_translatedDisplay is not null,_ocrSource is null&&_ocrSnapshotTask is null);
    internal bool CoreStatePresentForSmoke=>_corePipelineV2 is not null;
    internal bool HistoryRequiresReOcrForSmoke=>_historyRequiresReOcr;
    internal string[] CoreBlockIdsForSmoke=>_corePipelineV2?.VisualBlocks.Select(x=>x.BlockId).ToArray()??[];
    internal IReadOnlyList<string> ActiveHighlightSourceBlockIdsForSmoke => ActiveHighlightSourceBlockIds();
    internal string RuntimeTargetLanguageForSmoke => _settings.TargetLanguage;
    internal int ActiveTranslationOperationsForSmoke => _translationTask is { IsCompleted: false } ? 1 : 0;
    internal void ApplyDefaultViewsForSmoke() => ApplyDefaultViews();
    internal void SetTextViewForSmoke(TextViewMode mode) { SaveTextScroll(SelectedTextMode); SelectTextMode(mode); UpdateIndependentViews(); RestoreTextScroll(mode); }
    internal string DisplayedTextForSmoke => _text.Text;
    internal TextViewMode TextViewForSmoke => SelectedTextMode;
    internal string[] TextModeItemsForSmoke=>_textMode.Items.Cast<object>().Select(x=>x?.ToString()??"").ToArray();
    internal bool TextPanelVisibleSettingForSmoke=>_settings.PreviewTextPanelVisible;
    internal void ToggleTextPanelForSmoke()=>SetTextPanelVisibility(!_settings.PreviewTextPanelVisible,true);
    internal bool TextPanelCollapsedForSmoke=>_split.Panel2Collapsed;
    internal int ImagePanelWidthForSmoke=>_split.Panel1.Width;
    internal bool MoreButtonVisibleForSmoke=>_moreButton?.Visible==true;
    internal int VisibleFullModeControlCountForSmoke=>_fullModeControls.Count(x=>x.Visible);
    internal void SetChromeModeForSmoke(PreviewChromeMode mode){_settings.PreviewChromeMode=mode;ApplyChromeMode();}
    internal bool ModalSafetyRestoresTopMostForSmoke(bool initial,bool throwInside)
    {
        TopMost=initial;try{ModalSafety.Run(this,()=>{if(throwInside)throw new InvalidOperationException("smoke");return DialogResult.OK;});}catch(InvalidOperationException){}return TopMost==initial;
    }
    internal bool PlacementTimerExistsForSmoke=>false;
    internal ImageViewMode ImageViewForSmoke => _preferredImageMode;
    internal ImageViewMode DisplayedImageViewForSmoke=>_displayedImageMode;
    internal bool TranslatedAvailableForSmoke=>_translatedDisplay is not null;
    internal string ImageModeTextForSmoke=>_imageMode.SelectedItem?.ToString()??"";
    internal void SetPreviewSelectionForSmoke(OcrEngineKind engine, ImageViewMode image, TextViewMode text)
    {
        _settings.OcrEngine = engine; _ocrEngineSelector.SelectedIndex = OcrEngineUiMapping.ToSelectedIndex(engine);
        SetPreferredImageMode(image,"SmokeSelection",true);SelectTextMode(text);RememberCurrentPreviewSelection();
    }
    internal void SetTranslatedDisplayForSmoke(Bitmap? bitmap,string reason="RendererCompleted"){_translatedDisplay?.Dispose();_translatedDisplay=bitmap is null?null:new Bitmap(bitmap);UpdateIndependentViews();LogPreviewMode(reason);}
    internal void PublishTranslationTextForSmoke()=>PublishTranslationTextCore();
    internal void CommitTranslatedDisplayAtomicallyForSmoke(Bitmap bitmap,OcrDocument document)
    {
        _document=document;_translatedDisplay?.Dispose();_translatedDisplay=new Bitmap(bitmap);_preferredImageMode=ImageViewMode.Translated;CommitFinalPreviewAtomically();
    }
    internal void SuppressAutoOcrForE2E()=>_suppressAutoOcrForE2E=true;
    internal PreviewUiCounters UiCountersForSmoke=>CurrentUiCounters();
    internal RealExeE2ESnapshot? E2ESnapshotForSmoke=>_lastE2ESnapshot;
    internal Rectangle VisibleImageBoundsForSmoke=>_picture.Bounds;
    internal Size ScrollExtentForSmoke=>_imagePanel.AutoScrollMinSize;
    internal float DisplayScaleForSmoke=>_lastDisplayScale;
    internal long LayoutVersionForSmoke=>_layoutVersion;
    internal float ViewZoomForSmoke=>_viewZoom;internal bool FitViewForSmoke=>_fitView;internal Point PanOffsetForSmoke=>CurrentImageScrollOffset();internal void ZoomForSmoke(float factor,Point anchor)=>ZoomAround(factor,anchor);internal void ResetFitForSmoke()=>ResetFit();
    internal void SetZoomScaleForSmoke(float scale)=>SetZoomScaleCentered(scale,null);
    internal void SelectZoomPresetForSmoke(int index)=>_zoom.SelectedIndex=index;
    internal string ZoomSelectionForSmoke=>_zoom.SelectedItem?.ToString()??"";
    internal Size ImageViewportForSmoke=>_imagePanel.ClientSize;
    internal PictureBoxSizeMode ImageSizeModeForSmoke=>_picture.SizeMode;
    internal PointF ImagePointAtViewportForSmoke(Point point)=>new((point.X-_picture.Left)/Math.Max(.001f,_lastDisplayScale),(point.Y-_picture.Top)/Math.Max(.001f,_lastDisplayScale));
    internal int TextFirstVisibleLineForSmoke => GetFirstVisibleTextLine();
    internal Point TextScrollPositionForSmoke { get { var value = GetTextScrollPosition(); return new Point(value.X, value.Y); } }
    internal void SetTextScrollPositionForSmoke(Point value) { var native = new NativePoint { X = value.X, Y = value.Y }; RestoreTextScrollPosition(native); }
    internal void SetHoverForSmoke(string? id) => SetHover(id);
    internal IReadOnlyDictionary<string,RightTextRangeIdentity> RightTextRangeIdentitiesForSmoke=>_rightTextRangeIdentities;
    internal (int WholeDocumentHighlightResetCount,int HighlightRangeFormatCount,int RedundantHighlightUpdateCount,int RightTextFullAssignmentDuringInteraction) HighlightMetricsForSmoke=>
        (0,_highlightRangeFormatCount,0,_rightTextFullAssignmentDuringInteraction);
    internal void ClickRightTextAtForSmoke(int index){var identity=_rightTextRangeIdentities.Values.First(x=>index>=x.Start&&index<x.Start+x.Length);_lockedSegmentId=identity.TranslationUnitId;_lockedRightIdentity=identity;_hoverSegmentId=identity.TranslationUnitId;_hoverRightIdentity=identity;ApplyTextHighlight();}
    internal (int ClickedCharIndex,RightTextRangeIdentity? Identity) ClickRightTextAtByMouseForSmoke(int index)
    {var point=_text.GetPositionFromCharIndex(index);point.Offset(1,1);var actual=_text.GetCharIndexFromPosition(point);Text_Click(_text,new MouseEventArgs(MouseButtons.Left,1,point.X,point.Y,0));return(actual,ActiveRightIdentity);}
    internal object[] FinalHighlightGeometryForSmoke(){var identity=ActiveRightIdentity;if(identity is null||_recognitionV2 is null)return[];var sources=identity.SourceIds.ToHashSet(StringComparer.Ordinal);var render=identity.StableRenderUnitIds.ToHashSet(StringComparer.Ordinal);return _recognitionV2.Regions.Where(x=>x.SourceBlockIds.Any(sources.Contains)||render.Contains(x.RegionId)).Select(x=>(object)new{x.RegionId,x.TranslationUnitId,x.SourceBlockIds,x.TranslationRenderRect,x.SourceLinePolygons}).ToArray();}
    internal void SelectSourceForSmoke(string sourceId){var identity=_rightTextRangeIdentities.Values.First(x=>x.SourceIds.Contains(sourceId,StringComparer.Ordinal));_lockedSegmentId=null;_lockedRightIdentity=null;SetHover(identity.TranslationUnitId,identity);}
    internal void SetRecognitionMappingForSmoke(RecognitionDocumentV2 recognition,IReadOnlyList<TranslationSemanticGroup> semantic,IReadOnlyDictionary<string,string> translations){_recognitionV2=recognition;_translationSemanticGroups=semantic;_translationByUnitId=translations;}
    internal void ScrollTextLinesForSmoke(int lines) { if (_text.IsHandleCreated) SendMessage(_text.Handle, EmLineScroll, IntPtr.Zero, new IntPtr(lines)); }
    internal void ResizeTextViewportForSmoke(Size size) { SaveTextScroll(SelectedTextMode); _text.Size = size; RestoreTextScroll(SelectedTextMode); }
    internal void ScrollTextToBottomForSmoke() { if (_text.IsHandleCreated) SendMessage(_text.Handle, WmVScroll, new IntPtr(SbBottom), IntPtr.Zero); }
    internal void RefreshTextForSmoke() => BuildTextView();
    internal bool RegionLabelsVisibleForSmoke => _visibleV2Layers.Contains(RegionOverlayLayer.RoleLabels);
    internal int VisibleV2LayerCountForSmoke => _visibleV2Layers.Count;
    internal int RecognitionRunCountForSmoke => _recognitionRunCount;
    internal bool VisualAnalysisPresentForSmoke => _visualAnalysisV2 is not null;
    internal VisualModelKind VisualModelForSmoke => _settings.VisualModel;
    internal void SetVisualModelForSmoke(VisualModelKind model) => _settings.VisualModel = model;
    internal void SavePlacementForSmoke() => SavePlacementNow();
    internal PreviewWindowSizingMode PreviewSizingModeForSmoke=>_settings.PreviewWindowSizingMode;
    internal bool FinalPreviewReadyForSmoke => _finalPreviewReady;
    internal long EndToEndWallClockMsForSmoke => _wallClock?.ElapsedMs ?? 0;
    internal string? LastTimingPathForSmoke => _lastTimingPath;
    internal void SetV2LayerForSmoke(RegionOverlayLayer layer, bool visible) { if (visible) _visibleV2Layers.Add(layer); else _visibleV2Layers.Remove(layer); _picture.Invalidate(); }
    internal bool LastTextLineFullyVisibleForSmoke => _text.GetPositionFromCharIndex(Math.Max(0, _text.TextLength - 1)).Y + _text.Font.Height <= _text.ClientSize.Height;
    internal void StartTranslationForSmoke() => StartTranslation();
    internal Task? TranslationTaskForSmoke => _translationTask;
    internal string CurrentTranslationRequestIdForSmoke => _currentTranslationRequestId;
    internal long TranslationGenerationForSmoke => _translationGeneration;
    internal CancellationTokenSource? TranslationOperationForSmoke => _operation;
    internal void SetTranslationExecutorForSmoke(Func<IReadOnlyList<TranslationItem>, ApiSettings,
        IProgress<TranslationProgress>?, CancellationToken, Task<TranslationBatchResult>> executor) => _translateBatchAsync = executor;
    internal void SetTranslationSourceForSmoke(TranslationTextSource source) => _settings.TranslationTextSource = source;
    internal void CancelTranslationForSmoke() => CancelTranslation();
    internal void InvalidateTranslationForSmoke(string reason) => InvalidateTranslationRequest(reason);
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        TranslationService.DiagnosticLog($"FormClosing enter uiThread={!InvokeRequired} disposed={IsDisposed} disposing={Disposing} task={_translationTask?.Status} operationCanceled={_operation?.IsCancellationRequested}");
        _closing = true;
        if (e.CloseReason != CloseReason.WindowsShutDown && _saveWindowPlacement is not null)
        {
            var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            _saveWindowPlacement(new(bounds.X, bounds.Y, bounds.Width, bounds.Height, WindowState == FormWindowState.Maximized));
        }
        StopApiTimer();
        try { _ocrOperation?.Cancel(); } catch (ObjectDisposedException) { }
        var operation = _operation;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                TranslationService.DiagnosticLog($"Cancel before operationCanceled={operation?.IsCancellationRequested} lifetimeCanceled={_lifetime.IsCancellationRequested}");
                operation?.Cancel();
                _lifetime.Cancel();
                TranslationService.DiagnosticLog($"Cancel after operationCanceled={operation?.IsCancellationRequested} lifetimeCanceled={_lifetime.IsCancellationRequested}");
            }
            catch (ObjectDisposedException) { TranslationService.DiagnosticLog("Cancel skipped: CTS already disposed"); }
        });
        base.OnFormClosing(e);
        TranslationService.DiagnosticLog($"FormClosing exit cancel={e.Cancel} disposed={IsDisposed} disposing={Disposing}");
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        TranslationService.DiagnosticLog($"FormClosed enter uiThread={!InvokeRequired} disposed={IsDisposed} task={_translationTask?.Status}");
        base.OnFormClosed(e);
        TranslationService.DiagnosticLog($"FormClosed exit disposed={IsDisposed}");
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closing = true;
            TranslationService.DiagnosticLog($"Dispose begin task={_translationTask?.Status} operationCanceled={_operation?.IsCancellationRequested}");
            _picture.Image = null;
            _apiTimer.Dispose();
            var operation = _operation;
            var task = _translationTask;
            var ocrOperation = _ocrOperation;
            var ocrTask = _ocrTask;
            if (task is null || task.IsCompleted)
            {
                operation?.Dispose();
                if (ocrTask is null || ocrTask.IsCompleted) ocrOperation?.Dispose();
                else _ = ocrTask.ContinueWith(_ => ocrOperation?.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                _lifetime.Dispose();
            }
            else
            {
                _ = task.ContinueWith(_ =>
                {
                    operation?.Dispose();
                    if (ocrTask is null || ocrTask.IsCompleted) ocrOperation?.Dispose();
                    else _ = ocrTask.ContinueWith(_ => ocrOperation?.Dispose(), CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    _lifetime.Dispose();
                    TranslationService.DiagnosticLog("Deferred CTS disposal completed");
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            _document?.DebugImage?.Dispose();
            DisposeBitmapWhenSafe(_original,task,_ocrSnapshotTask);
            if(_ocrSource is not null)DisposeBitmapWhenSafe(_ocrSource,ocrTask,_imageIdentity?.ComputationTask);
            _originalDisplay?.Dispose(); _translatedDisplay?.Dispose();
            if (_ownsVisionRuntime) _ = _visionRuntimeManager.DisposeAsync();
            if (_ownsServices)
            {
                var cleanup=_ocrRuntimeManager.DisposeAsync().AsTask();
                _=cleanup.ContinueWith(_=>_session.Dispose(),CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            }
            TranslationService.DiagnosticLog("Dispose resources completed");
        }
        base.Dispose(disposing);
    }

    private static void DisposeBitmapWhenSafe(Bitmap bitmap,params Task?[] tasks)
    {
        var pending=tasks.Where(x=>x is not null&&!x.IsCompleted).Cast<Task>().Distinct().ToArray();
        if(pending.Length==0){bitmap.Dispose();return;}
        _=Task.WhenAll(pending).ContinueWith(_=>bitmap.Dispose(),CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
    }
}
