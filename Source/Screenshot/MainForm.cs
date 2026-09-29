using System.Runtime.InteropServices;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm : Form, IMessageFilter
{
    private enum BindingCaptureState { Idle, Arming, Capturing }
    private const int HotKeyId = 0x4741;
    private readonly ComboBox _modifierBox = new();
    private readonly ComboBox _keyBox = new();
    private InputBindingCaptureButton? _startBindingEditor,_cancelBindingEditor,_toggleBindingEditor,_zoomInBindingEditor,_zoomOutBindingEditor,_resetFitBindingEditor;
    private InputBindingCaptureButton? _activeBindingCapture;private InputActionId? _activeBindingAction;private BindingCaptureState _bindingCaptureState;private readonly List<string> _bindingCaptureDiagnostics=[];
    private readonly ComboBox _ocrLanguageBox = new();
    private readonly TextBox _apiUrlBox = new();
    private readonly TextBox _apiKeyBox = new();
    private readonly TextBox _modelBox = new();
    private readonly ComboBox _targetLanguageBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _firstByteTimeoutBox = new() { Minimum = 10, Maximum = 90, Value = 30 };
    private readonly NumericUpDown _requestTimeoutBox = new() { Minimum = 30, Maximum = 180, Value = 90 };
    private readonly ComboBox _cleanupStrengthBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _ocrEngineBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _visualModelBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _previewDefaultImageBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _previewDefaultTextBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _previewTextPanelVisibleBox=new(){Text="显示右侧文本栏",AutoSize=true,Checked=true};
    private readonly ComboBox _translationSourceBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _rememberLastPreviewBox = new() { Text = "记住上次 Preview 选择", AutoSize = true };
    private readonly CheckBox _ocrCacheBox = new() { Text = "启用OCR缓存", AutoSize = true };
    private readonly CheckBox _translationCacheBox = new() { Text = "启用翻译缓存", AutoSize = true };
    private readonly CheckBox _imageTranslationBox = new() { Text = "启用译图（实验功能）", AutoSize = true };
    private readonly NumericUpDown _historyLimitBox = new() { Minimum = 10, Maximum = 200, Value = 20 };
    private readonly CheckBox _hideMainDuringCaptureBox = new() { Text = "截图时隐藏主界面", AutoSize = true, Checked = true };
    private readonly CheckBox _hidePreviewsDuringCaptureBox = new() { Text = "截图时隐藏翻译预览窗口", AutoSize = true, Checked = true };
    private readonly ComboBox _sourceLanguageBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _translationStyleBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _providerDisplayNameBox = new();
    private readonly TextBox _customPromptBox = new() { Multiline = true, Height = 90, ScrollBars = ScrollBars.Vertical };
    private readonly CheckBox _preserveIdentifiersBox = new() { Text = "保留 ID / Code", AutoSize = true };
    private readonly CheckBox _preserveNumbersBox = new() { Text = "保留数字", AutoSize = true };
    private readonly CheckBox _preserveVariablesBox = new() { Text = "保留变量 / 占位符", AutoSize = true };
    private readonly ComboBox _previewSizingBox=new(){DropDownStyle=ComboBoxStyle.DropDownList};private readonly NumericUpDown _fixedPreviewWidthBox=new(){Minimum=820,Maximum=10000};private readonly NumericUpDown _fixedPreviewHeightBox=new(){Minimum=560,Maximum=10000};
    private readonly ComboBox _uiFontModeBox=new(){DropDownStyle=ComboBoxStyle.DropDownList};private readonly ComboBox _uiFontBox=new(){DropDownStyle=ComboBoxStyle.DropDown};private readonly NumericUpDown _uiFontSizeBox=new(){Minimum=7,Maximum=24};
    private readonly Label _typographyStatus=new(){AutoSize=true,MaximumSize=new Size(620,0)};
    private readonly ComboBox _previewFontModeBox=new(){DropDownStyle=ComboBoxStyle.DropDownList};private readonly ComboBox _previewFontBox=new(){DropDownStyle=ComboBoxStyle.DropDownList};private readonly NumericUpDown _previewFontSizeBox=new(){Minimum=7,Maximum=48};
    private readonly ComboBox _overlayFontModeBox=new(){DropDownStyle=ComboBoxStyle.DropDownList};private readonly ComboBox _overlayFontBox=new(){DropDownStyle=ComboBoxStyle.DropDownList};private readonly NumericUpDown _overlayFontSizeBox=new(){Minimum=0,Maximum=48};private readonly ComboBox _overlayBackgroundBox=new(){DropDownStyle=ComboBoxStyle.DropDownList};private readonly NumericUpDown _overlayOpacityBox=new(){Minimum=0,Maximum=255};
    private readonly Label _statusLabel = new();
    private readonly Label _configPathLabel = new();
    private readonly Label _keyStatusLabel = new();
    private ApiSettings _translationOptions = new();
    private readonly OcrService _ocrService = new();
    private readonly TranslationService _translationService = new();
    private readonly OcrRuntimeManager _ocrRuntimeManager;
    private readonly VisionRuntimeManager _visionRuntimeManager;
    private readonly SessionServices _session;
    private readonly ScreenCaptureCoordinator _screenCaptureCoordinator=new();
    private readonly List<PreviewForm> _previews = [];
    private readonly string _settingsPath;
    private readonly bool _allowLegacyMigration;
    private readonly CaptureFlashDiagnosticOptions _captureDiagnostics;
    private CaptureOverlay? _captureOverlay;
    private PreviewForm? _prewarmedPreview;
    private bool _hotKeyRegistered;
    private readonly GlobalHotKeyHost _hotKeyHost,_cancelHotKeyHost,_toggleHotKeyHost;
    private readonly DesktopInputRuntime _inputRuntime = new();

    private bool _captureOpen;
    private bool _settingsLoadCompleted;
    private bool _settingsLoadSucceeded;
    private bool _settingsDirty;
    private bool _applyingSettings;
    private bool _liveUiFontApplyPending;
    private readonly Dictionary<string, Panel> _pages = new(StringComparer.Ordinal);
    private readonly Panel _mainContentHost=new WorkspacePageHost(){Dock=DockStyle.Fill};
    private string _currentMainPage="";
    private string _lastNavigationEvent="startup",_lastThemeEvent="startup",_lastSettingsSaveEvent="startup";
    private FormWindowState _lastObservedWindowState=FormWindowState.Normal;
    private Label? _homeOcrStatus;
    private Label? _homeApiStatus;
    private SettingsPageHost? _settingsTabs;
    private readonly HistoryGrid _historyList = new() { Dock=DockStyle.Fill };
    private int _historyThumbnailSide;
    private int _historyThumbnailDecodeCount;
    private readonly Label _historyUsage=new(){AutoSize=true};
    private readonly ComboBox _historyThumbnailBox=new(){DropDownStyle=ComboBoxStyle.DropDownList};
    private readonly NumericUpDown _historyTextSizeBox=new(){Minimum=8,Maximum=24,DecimalPlaces=1,Increment=.5M};
    private readonly CheckBox _historyByCountBox=new(){Text="按数量清理",AutoSize=true};private readonly CheckBox _historyByAgeBox=new(){Text="按保存天数清理",AutoSize=true};private readonly CheckBox _historyBySpaceBox=new(){Text="按磁盘空间清理",AutoSize=true};
    private readonly NumericUpDown _historyDaysBox=new(){Minimum=1,Maximum=3650};private readonly NumericUpDown _historySpaceBox=new(){Minimum=32,Maximum=102400};
    private readonly Label _diagnosticsSummary = new() { AutoSize = true, MaximumSize = new Size(820, 0) };
    private readonly ComboBox _themeModeBox=new(){DropDownStyle=ComboBoxStyle.DropDownList};
    private readonly ComboBox _closeBehaviorBox=new(){DropDownStyle=ComboBoxStyle.DropDownList};
    private readonly NotifyIcon _trayIcon=new(){Icon=SystemIcons.Application,Text="截图翻译器",Visible=!AppDataPaths.IsDataRootVerification};
    private bool _trueExit;

    public MainForm(string? settingsPath = null) :
        this(settingsPath, new(CaptureFlashDiagnosticMode.Full, false, false))
    {
    }

    internal MainForm(string? settingsPath, CaptureFlashDiagnosticOptions captureDiagnostics)
    {
        // Build all logical-pixel controls before the initial DPI layout runs.
        // Otherwise early Font/size changes consume scaling while the form is empty.
        SuspendLayout();
        _captureDiagnostics = captureDiagnostics;
        _settingsPath = ConfigurationManager.ResolveSettingsPath(settingsPath);
        _session=new SessionServices(Path.Combine(Path.GetDirectoryName(_settingsPath)!,"history"));
        _allowLegacyMigration = !AppDataPaths.HasExplicitRoot && string.IsNullOrWhiteSpace(settingsPath)
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConfigurationManager.TestConfigEnvironmentVariable));
        _ocrRuntimeManager = new OcrRuntimeManager(AppContext.BaseDirectory, _ocrService);
        _visionRuntimeManager=new VisionRuntimeManager(AppContext.BaseDirectory);
        _hotKeyHost=new GlobalHotKeyHost(HotKeyId,()=>{if(!IsDisposed)BeginInvoke(BeginCapture);});
        _cancelHotKeyHost=new GlobalHotKeyHost(HotKeyId+1,()=>{if(!IsDisposed)BeginInvoke(DispatchCancelBindingHotKey);});
        _toggleHotKeyHost=new GlobalHotKeyHost(HotKeyId+2,()=>{if(!IsDisposed)BeginInvoke(ToggleResultsFromBinding);});


        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Text = $"FUSION R1 · {BuildIdentity.BuildVersion}";
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(5);
        ClientSize = new Size(1440, 880);
        MinimumSize = new Size(1120, 740);
        Load+=(_,_)=>{var area=Screen.FromControl(this).WorkingArea;MinimumSize=new Size(Math.Min(MinimumSize.Width,area.Width),Math.Min(MinimumSize.Height,area.Height));Size=new Size(Math.Min(Width,area.Width),Math.Min(Height,area.Height));};
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(18, 23, 34);
        ForeColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 10F);
        _fusion = new FusionConfiguration();
        BuildFeatureCompleteUi(); UiPerformanceTrace.Attach(this);
        Application.AddMessageFilter(this);
        BuildTrayMenu();
        // Bind saved settings exactly once, before the first visible frame.
        LoadSettings();DesktopStages.Mark("base-settings-bound");
        LoadFusionUi();
        ApplyDesktopAppearance(_translationOptions);
        Deactivate+=(_,_)=>CancelBindingCapture("WindowDeactivated");
        Resize+=(_,_)=>{var previous=_lastObservedWindowState;_lastObservedWindowState=WindowState;if(previous==FormWindowState.Minimized&&WindowState!=FormWindowState.Minimized)ValidateUiInvariants("MainWindowRestore");};
        AttachSettingsDirtyTracking();
        Shown += async (_, _) =>
        {
            // Let external smoke/automation Shown handlers arm their timers before
            // optional Windows OCR language discovery performs platform work.
            await Task.Yield();
            if(IsDisposed||Disposing)return;
            _statusLabel.Text="正在准备截图窗口…";
            if (_captureDiagnostics.Mode == CaptureFlashDiagnosticMode.Full)
            {
                _captureOverlay = new CaptureOverlay(reusableLifecycle: true);
                _captureOverlay.PrewarmHandle();
            }
            _desktopReady=true;DesktopStages.Mark("settings-preparation-on-demand");
            if(!string.IsNullOrWhiteSpace(_fusion!.State.LastGame))SelectFusionGame(_fusion.State.LastGame,null,false);
            _statusLabel.Text="正在准备截图功能…";
            await Task.Yield();
            if(IsDisposed||Disposing)return;
            if(_captureDiagnostics.Mode==CaptureFlashDiagnosticMode.Full&&_prewarmedPreview is null)
            {
                var placeholder=new Bitmap(1,1);placeholder.SetPixel(0,0,Color.Black);
                _prewarmedPreview=new PreviewForm(placeholder,PreviewMode.OcrOnly,CurrentSettings,_ocrService,_translationService,
                    _ocrRuntimeManager,_visionRuntimeManager,_session,SavePreviewPlacement,takeImageOwnership:true);
                _prewarmedPreview.SuppressAutoOcrForE2E();_=_prewarmedPreview.Handle;
            }
            ApplyHotKey(false);
            if(CursorCaptureStageDump.Enabled)
            {
                if(_homeOcrStatus is not null)_homeOcrStatus.Text="Cursor Stage Dump：Rapid = NOT REQUIRED · PP-S = NOT REQUIRED";
                if(_homeApiStatus is not null)_homeApiStatus.Text="本诊断不运行 OCR、翻译或 Renderer；快捷键保持产品真实路径。";
                _statusLabel.Text="Cursor Stage Dump R2 已就绪：按说明依次完成 OUTSIDE、A、B。";
                Text += " · CURSOR STAGE DUMP R2";
            }
            else
            {
                await Task.Yield();
                if(IsDisposed||Disposing)return;
                await LoadOcrLanguagesAsync();
                // Fusion: OCR health is checked on demand; navigation does not run inference.
            }
            DesktopStages.Mark("necessary-initialization-completed");
            if (!IsDisposed&&_captureDiagnostics.Enabled) BeginInvoke(BeginCapture);
        };
        ResumeLayout(true);DesktopStages.Mark("main-form-layout-prepared");
        AttachDesktopStages();
    }

    private void ApplyMainChromeTheme(){BackColor=UiDarkTheme.WindowBackground;ForeColor=UiDarkTheme.TextPrimary;foreach(Control child in Controls){if(child is Button or Label)UiDarkTheme.Apply(child);}if(_settingsTabs is not null)UiDarkTheme.Apply(_settingsTabs);}

    private void BuildTrayMenu()
    {
        var menu=new ContextMenuStrip();
        menu.Items.Add("打开主界面",null,(_,_)=>RestoreFromTray());
        menu.Items.Add("开始截图",null,(_,_)=>BeginCapture());
        menu.Items.Add("暂停 / 启用快捷键",null,(_,_)=>{if(_inputRuntime.HasConfiguredBindings){RemoveHotKey();_statusLabel.Text="快捷键已暂停。";}else ApplyHotKey(true);});
        menu.Items.Add("设置",null,(_,_)=>{RestoreFromTray();ShowPage("设置");});
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出",null,(_,_)=>ExitApplication());
        _trayIcon.ContextMenuStrip=menu;_trayIcon.DoubleClick+=(_,_)=>RestoreFromTray();
    }
    private void RestoreFromTray(){_hotKeyHost.Log("Tray Restore");ShowInTaskbar=true;Show();WindowState=FormWindowState.Normal;Activate();BringToFront();_trayIcon.Visible=true;}
    private void ExitApplication(){_trueExit=true;_trayIcon.Visible=false;Close();}

    private readonly ComboBox _backgroundTreatmentBox=new(){DropDownStyle=ComboBoxStyle.DropDownList};
    private readonly ComboBox _ocrLoadBox = new() { DropDownStyle=ComboBoxStyle.DropDownList, Width=200 };
    private readonly ComboBox _backgroundComputeBox=new(){DropDownStyle=ComboBoxStyle.DropDownList};

    private ApiSettings UiSettings => new()
    {
        TypographyConfigVersion = FontManager.CurrentConfigVersion,
        ApiUrl = _apiUrlBox.Text.Trim(), ApiKey = _apiKeyBox.Text,
        Model = _modelBox.Text.Trim(), TargetLanguage = UiTargetLanguageDisplay.CodeFromDisplay(_targetLanguageBox.SelectedItem?.ToString()),
        OcrLanguage = _ocrLanguageBox.SelectedItem?.ToString() ?? "自动",
        FirstByteTimeoutSeconds = (int)_firstByteTimeoutBox.Value,
        RequestTimeoutSeconds = (int)_requestTimeoutBox.Value,
        CleanupStrength = (CleanupStrength)Math.Max(0, _cleanupStrengthBox.SelectedIndex),
        OcrEngine = OcrEngineKind.Rapid,
        VisualModel = VisualModelKind.Off,
        HotKeyModifiers = _modifierBox.SelectedItem?.ToString() ?? "Ctrl + Alt",
        HotKeyKey = _keyBox.SelectedItem?.ToString() ?? "Z",
        StartCaptureBinding = _translationOptions.StartCaptureBinding,
        CancelCaptureBinding = _translationOptions.CancelCaptureBinding,
        ToggleResultsBinding = _translationOptions.ToggleResultsBinding,
        PreviewZoomInBinding = _translationOptions.PreviewZoomInBinding,
        PreviewZoomOutBinding = _translationOptions.PreviewZoomOutBinding,
        PreviewResetFitBinding = _translationOptions.PreviewResetFitBinding,
        OcrCacheEnabled = _ocrCacheBox.Checked,
        TranslationCacheEnabled = _translationCacheBox.Checked,
        ImageTranslationEnabled = _imageTranslationBox.Checked,
        BackgroundComputeDevice = _backgroundComputeBox.SelectedIndex==1 ? BackgroundComputeDevice.Gpu : BackgroundComputeDevice.Cpu,
        BackgroundTreatment = _backgroundTreatmentBox.SelectedIndex==1 ? BackgroundTreatment.Lightweight : BackgroundTreatment.FineRepair,
        OcrLoad = _ocrLoadBox.SelectedIndex==1 ? OcrLoad.Low : OcrLoad.Standard,
        HistoryLimit = (int)_historyLimitBox.Value,
        PreviewDefaultImage = (PreviewDefaultImage)Math.Max(0, _previewDefaultImageBox.SelectedIndex),
        PreviewDefaultText = (PreviewDefaultText)Math.Max(0, _previewDefaultTextBox.SelectedIndex),
        PreviewTextPanelVisible=_previewTextPanelVisibleBox.Checked,
        TranslationTextSource = (TranslationTextSource)Math.Max(0, _translationSourceBox.SelectedIndex),
        RememberLastPreviewSelection = _rememberLastPreviewBox.Checked,
        PreviewChromeMode = _translationOptions.PreviewChromeMode,
        PreviewAlwaysOnTop = _translationOptions.PreviewAlwaysOnTop,
        HideMainWindowDuringCapture = _hideMainDuringCaptureBox.Checked,
        HidePreviewWindowsDuringCapture = _hidePreviewsDuringCaptureBox.Checked,
        HasPreviewWindowPlacement = _translationOptions.HasPreviewWindowPlacement,
        PreviewWindowX = _translationOptions.PreviewWindowX,
        PreviewWindowY = _translationOptions.PreviewWindowY,
        PreviewWindowWidth = _translationOptions.PreviewWindowWidth,
        PreviewWindowHeight = _translationOptions.PreviewWindowHeight,
        PreviewWindowMaximized = _translationOptions.PreviewWindowMaximized,
        HasLastUserPreviewBounds=_translationOptions.HasLastUserPreviewBounds,LastUserPreviewX=_translationOptions.LastUserPreviewX,LastUserPreviewY=_translationOptions.LastUserPreviewY,LastUserPreviewWidth=_translationOptions.LastUserPreviewWidth,LastUserPreviewHeight=_translationOptions.LastUserPreviewHeight,LastUserPreviewMaximized=_translationOptions.LastUserPreviewMaximized,
        ThemeMode=_themeModeBox.SelectedIndex>=0?(ApplicationThemeMode)_themeModeBox.SelectedIndex:_translationOptions.ThemeMode,CustomThemeMainBackground=_translationOptions.CustomThemeMainBackground,CustomThemeSecondaryBackground=_translationOptions.CustomThemeSecondaryBackground,CustomThemeText=_translationOptions.CustomThemeText,CustomThemeSecondaryText=_translationOptions.CustomThemeSecondaryText,CustomThemeAccent=_translationOptions.CustomThemeAccent,CustomThemeBorder=_translationOptions.CustomThemeBorder,
        CloseMainWindowBehavior=_closeBehaviorBox.SelectedIndex>=0?(CloseMainWindowBehavior)_closeBehaviorBox.SelectedIndex:_translationOptions.CloseMainWindowBehavior,HistoryCleanupByCount=_historyByCountBox.Checked,HistoryCleanupByAge=_historyByAgeBox.Checked,HistoryRetentionDays=(int)_historyDaysBox.Value,HistoryCleanupBySpace=_historyBySpaceBox.Checked,HistoryMaximumMegabytes=(int)_historySpaceBox.Value,HistoryThumbnailSize=_historyThumbnailBox.SelectedIndex>=0?(HistoryThumbnailSize)_historyThumbnailBox.SelectedIndex:_translationOptions.HistoryThumbnailSize,HistoryTextSize=(float)_historyTextSizeBox.Value,
        PreviewWindowSizingMode=(PreviewWindowSizingMode)Math.Max(0,_previewSizingBox.SelectedIndex),FixedPreviewWidth=(int)_fixedPreviewWidthBox.Value,FixedPreviewHeight=(int)_fixedPreviewHeightBox.Value,LastPreviewWidth=_translationOptions.LastPreviewWidth,LastPreviewHeight=_translationOptions.LastPreviewHeight,
        UiFontMode=(UiFontMode)Math.Max(0,_uiFontModeBox.SelectedIndex),UiFontFamily=_uiFontBox.Text,UiFontSize=(float)_uiFontSizeBox.Value,PreviewTextFontMode=(PreviewTextFontMode)Math.Max(0,_previewFontModeBox.SelectedIndex),PreviewTextFontFamily=_previewFontBox.Text,PreviewTextFontSize=(float)_previewFontSizeBox.Value,
        OverlayFontMode=(OverlayFontMode)Math.Max(0,_overlayFontModeBox.SelectedIndex),OverlayFontFamily=_overlayFontBox.Text,OverlayFontSize=(float)_overlayFontSizeBox.Value,OverlayBackgroundStyle=(TranslationOverlayBackgroundStyle)Math.Max(0,_overlayBackgroundBox.SelectedIndex),OverlayBackgroundOpacity=(int)_overlayOpacityBox.Value,
        TranslationProvider = _translationOptions.TranslationProvider,
        TranslationProviderKind = _translationOptions.TranslationProviderKind,
        TranslationPromptVersion = TranslationPromptBuilder.Version,
        SourceLanguage = _sourceLanguageBox.SelectedIndex >= 0
            ? (SourceLanguageMode)_sourceLanguageBox.SelectedIndex
            : _translationOptions.SourceLanguage,
        TranslationStyle = _translationStyleBox.SelectedIndex >= 0
            ? (TranslationStyle)_translationStyleBox.SelectedIndex
            : _translationOptions.TranslationStyle,
        ProviderDisplayName = string.IsNullOrWhiteSpace(_providerDisplayNameBox.Text)
            ? _translationOptions.ProviderDisplayName : _providerDisplayNameBox.Text.Trim(),
        CustomTranslationPrompt = _customPromptBox.Text,
        PreserveIdentifiers = _preserveIdentifiersBox.Checked,
        PreserveNumbers = _preserveNumbersBox.Checked,
        PreserveVariables = _preserveVariablesBox.Checked
    };

    private void BuildFeatureCompleteUi() => BuildFusionShell();

    private void ShowPage(string name)
    {
        if(name=="历史")name="主页";
        using var uiTiming=UiPerformanceTrace.Measure("main-navigation" );
        if(name=="设置"){using var timing=DesktopStages.MeasureOnce("settings-first-entry-prepared");EnsureSettingsCategory(Math.Max(0,_settingsNavigation.SelectedIndex));}
        if(_currentMainPage=="设置"&&name!="设置"&&!ConfirmProfileLeave())return;
        CancelBindingCapture();
        _lastNavigationEvent=$"{DateTimeOffset.Now:O} NavigationClick:{name}";
        if(!_pages.ContainsKey(name))throw new ArgumentOutOfRangeException(nameof(name),name,"Unknown main page");
        var current=_pages[name];
        if(_currentMainPage==name&&!current.IsDisposed&&current.Visible&&current.Parent==_mainContentHost&&current.Controls.Count>0&&_mainContentHost.Controls.GetChildIndex(current)==0)return;
        if(_pages.TryGetValue(_currentMainPage,out var previousPage))RememberPosition(previousPage);
        WorkspacePages.Show(_mainContentHost,_pages[name]);_currentMainPage=name;
        RestorePosition(_pages[name]);
        ValidatePageHost(name,"NavigationClick");
        if (name == "主页") RefreshHistory();
        if (name == "内嵌翻译"){RefreshGameState();ScheduleLibraryRefresh();}
        RefreshFusionChrome();
    }

    private void ToggleTheme()
    {
        _themeModeBox.SelectedIndex=CurrentSettings.ThemeMode==ApplicationThemeMode.Night?(int)ApplicationThemeMode.Day:(int)ApplicationThemeMode.Night;
        _translationOptions.ThemeMode=(ApplicationThemeMode)_themeModeBox.SelectedIndex;_lastThemeEvent=$"{DateTimeOffset.Now:O} QuickThemeToggle";SaveSettings(false);
    }

    private bool ValidatePageHost(string expected,string trigger="Manual")
    {
        var ok=_pages.TryGetValue(expected,out var page)&&!page.IsDisposed&&page.Parent==_mainContentHost&&_mainContentHost.Controls.Contains(page)&&page.Controls.Count>0&&page.Visible&&_mainContentHost.Controls.GetChildIndex(page)==0;
        if(!ok){WriteHostDiagnostic("PageHostInvariantViolation",trigger,expected,page);RecoverMainPage(expected,trigger);page=_pages.GetValueOrDefault(expected);if(page is not null&&!page.IsDisposed&&page.Parent==_mainContentHost)page.BringToFront();ok=page is not null&&!page.IsDisposed&&page.Parent==_mainContentHost&&page.Controls.Count>0&&_mainContentHost.Controls.GetChildIndex(page)==0;}
        return ok;
    }

    private void ValidateUiInvariants(string trigger){if(IsDisposed||Disposing)return;if(!string.IsNullOrWhiteSpace(_currentMainPage))ValidatePageHost(_currentMainPage,trigger);ValidateSettingsHost(trigger);}
    private void RecoverMainPage(string name,string trigger)
    {
        if(!_pages.TryGetValue(name,out var page))return;
        if(page.IsDisposed)
        {
            WriteHostDiagnostic("PageHostDisposedRecovery",trigger,name,page);
            var replacement=new Panel{Name=$"Recovered{name}",Dock=DockStyle.Fill,AutoScroll=true,BackColor=UiDarkTheme.WindowBackground};
            replacement.Controls.Add(new Label{Dock=DockStyle.Top,Height=64,Text="界面已自动恢复。异常页面的诊断信息已经保存。",ForeColor=UiDarkTheme.TextPrimary,Font=UiDarkTheme.SafeUiFont,Padding=new Padding(12)});
            AttachHostDiagnostics(replacement,$"RecoveredMainPage:{name}");_pages[name]=replacement;_mainContentHost.Controls.Add(replacement);UiDarkTheme.Apply(replacement);_statusLabel.Text="界面已自动恢复。";return;
        }
        if(page.Parent!=_mainContentHost||!_mainContentHost.Controls.Contains(page)){WriteHostDiagnostic("PageHostDetachedRecovery",trigger,name,page);_mainContentHost.Controls.Add(page);page.Dock=DockStyle.Fill;_statusLabel.Text="界面已自动恢复。";}
    }
    private void ValidateSettingsHost(string trigger)
    {
        if(_settingsTabs?.SelectedTab is not SettingsPage tab)return;var root=tab.Tag as Control;
        var ok=!tab.IsDisposed&&root is not null&&!root.IsDisposed&&tab.Controls.Contains(root)&&root.Parent==tab;
        if(ok)return;WriteHostDiagnostic("SettingsTabInvariantViolation",trigger,tab.Text,tab);
        if(root is not null&&!root.IsDisposed){tab.Controls.Add(root);root.Dock=DockStyle.Top;UiDarkTheme.Apply(root);_statusLabel.Text="界面已自动恢复。";}
    }
    private void AttachHostDiagnostics(Control control,string role)
    {
        control.Disposed+=(_,_)=>{if(IsDisposed||Disposing)return;AppLog.Write("page-host",$"PageHostLifecycle UnexpectedDisposed role={role} id={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(control)} stack={Environment.StackTrace}");};
        control.ParentChanged+=(_,_)=>{if(!IsHandleCreated||IsDisposed||Disposing)return;AppLog.Write("page-host",$"PageHostLifecycle ParentChanged role={role} id={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(control)} parent={control.Parent?.Name??"null"} stack={Environment.StackTrace}");};
    }
    private void WriteHostDiagnostic(string kind,string trigger,string expected,Control? page)=>AppLog.Write("page-host",$"{kind} timestamp={DateTimeOffset.Now:O} trigger={trigger} currentNavigation={_currentMainPage} expectedPage={expected} hostCount={_mainContentHost.Controls.Count} type={page?.GetType().FullName??"null"} name={page?.Name??"null"} pageInstanceId={(page is null?0:System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(page))} isDisposed={page?.IsDisposed} disposing={page?.Disposing} parent={page?.Parent?.Name??"null"} visible={page?.Visible} handleCreated={page?.IsHandleCreated} tabIndex={page?.TabIndex??-1} selectedTab={_settingsTabs?.SelectedTab?.Text??"null"} lastNavigation={_lastNavigationEvent} lastTheme={_lastThemeEvent} lastSettingsSave={_lastSettingsSaveEvent} stack={Environment.StackTrace}");

    private static Label PageTitle(string text) => new() { Text = text, Dock = DockStyle.Top, Height = 58,
        Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Bold), ForeColor = Color.White };
    private static Label Info(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(760, 0),
        ForeColor = Color.FromArgb(190, 205, 225), Margin = new Padding(4, 8, 4, 8) };

    private void BuildHomePage(Panel page)
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        flow.Controls.Add(PageTitle("游戏 AI 截图翻译器"));
        flow.Controls.Add(Info("原图 + 右侧完整译文是推荐使用方式。OCR全部本地运行，翻译仅在用户主动操作时联网。"));
        var capture = MakeButton("立即截图翻译", Point.Empty, new Size(220, 48)); capture.BackColor = Color.FromArgb(43, 163, 116);
        capture.Click += (_, _) => BeginCapture(); flow.Controls.Add(capture);
        _homeOcrStatus = Info("OCR引擎：RapidOCR（快速）"); _homeApiStatus = Info("API：等待加载设置");
        flow.Controls.Add(_homeOcrStatus); flow.Controls.Add(_homeApiStatus);
        var repair = MakeButton("检查并修复运行环境", Point.Empty, new Size(210, 38));
        repair.Click += async (_, _) => { using var dialog=new ModelManagerForm(AppContext.BaseDirectory, _ocrRuntimeManager, CurrentSettings);dialog.ShowDialog(this);await RunStartupHealthCheckAsync(); };
        flow.Controls.Add(repair);
        flow.Controls.Add(Info("最近任务耗时会显示在预览窗口。历史记录独立持久保存，可从“历史”重新打开。"));
        page.Controls.Add(flow);
    }

    private void BuildCapturePage(Panel page)
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        flow.Controls.Add(PageTitle("截图翻译"));
        flow.Controls.Add(Info("自动窗口/区域选择支持 Create、Move 与八方向 Resize；右键或 Esc 取消。"));
        var capture = MakeButton("立即截图", Point.Empty, new Size(190, 46)); capture.Click += (_, _) => BeginCapture(); flow.Controls.Add(capture);
        flow.Controls.Add(Info("模式：AI翻译 / 仅OCR（可在截图工具栏选择）\n截图选择：自动窗口/区域 / 全屏"));
        var hotkey = MakeButton("修改快捷键", Point.Empty, new Size(160, 38)); hotkey.Click += (_, _) => ShowPage("设置"); flow.Controls.Add(hotkey);
        page.Controls.Add(flow);
    }

    private void BuildOcrPage(Panel page)
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        flow.Controls.Add(PageTitle("OCR"));
        flow.Controls.Add(Info("RapidOCR（快速）适合日常识别；PaddleOCR（高质量）适合低对比度和困难画面；Windows OCR仅用于实验、兼容和故障回退。"));
        _ocrEngineBox.Items.AddRange(["RapidOCR（快速）", "PaddleOCR（高质量）", "Windows OCR（实验/回退）"]);
        _ocrEngineBox.SelectedIndex = 0; flow.Controls.Add(_ocrEngineBox);
        var check = MakeButton("检查 OCR 环境", Point.Empty, new Size(180, 38));
        check.Click += async (_, _) => { check.Enabled = false; try { var s = await _ocrRuntimeManager.CheckAllAsync(CancellationToken.None); _statusLabel.Text = string.Join(" ｜ ", s.Select(x => $"{x.Engine}:{(x.Ready ? "Ready" : "Unavailable")}")); } finally { check.Enabled = true; } };
        flow.Controls.Add(check);
        var compare = MakeButton("OCR 对比（主线）", Point.Empty, new Size(210, 38));
        compare.Click += (_, _) => new OcrComparisonForm(_ocrRuntimeManager,
            OcrComparisonMode.Standard).Show(this); flow.Controls.Add(compare);
        var experimental = MakeButton("OCR 对比（含 Windows 实验）", Point.Empty, new Size(310, 38));
        experimental.Click += (_, _) => new OcrComparisonForm(_ocrRuntimeManager,
            OcrComparisonMode.ExperimentalWindows).Show(this); flow.Controls.Add(experimental);
        var open = MakeButton("打开 Runtime 目录", Point.Empty, new Size(200, 38)); open.Click += (_, _) => OpenFolder(_ocrRuntimeManager.RuntimeRoot); flow.Controls.Add(open);
        var install = MakeButton("安装/修复 Runtime", Point.Empty, new Size(205, 38));
        install.Click += (_, _) => StartRuntimeInstaller(); flow.Controls.Add(install);
        flow.Controls.Add(Info("普通主线只运行 Rapid + Paddle；Windows 仅在实验页面中手动测试。人工质量评分保持 UNRATED。"));
        page.Controls.Add(flow);
    }

    private void BuildImageTranslationPage(Panel page)
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        flow.Controls.Add(PageTitle("译图（实验）")); flow.Controls.Add(Info("译图默认关闭，只有在预览窗口主动选择时生成。失败不会影响右侧完整文字译文。"));
        flow.Controls.Add(_imageTranslationBox); page.Controls.Add(flow);
    }

    private void BuildHistoryPage(Panel page)
    {
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Padding=new Padding(0)};
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        var flow = new FlowLayoutPanel { Dock = DockStyle.Top,AutoSize=true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var historyHelp=Info("历史独立持久保存；双击任意缩略图可重新打开 Preview。它与 OCR / 翻译缓存互不影响。");
        flow.Controls.Add(PageTitle("历史")); flow.Controls.Add(historyHelp);
        flow.SizeChanged+=(_,_)=>{var width=Math.Max(1,flow.ClientSize.Width-flow.Padding.Horizontal-historyHelp.Margin.Horizontal);if(historyHelp.MaximumSize.Width!=width)historyHelp.MaximumSize=new Size(width,0);};
        var clear=MakeButton("立即清空历史",Point.Empty,new Size(150,36));clear.Click+=(_,_)=>{if(AppDialog.Show(this,"清空历史","确认删除全部历史图片与索引？",AppDialogKind.Confirmation)!=DialogResult.Yes)return;_session.ClearHistory();RefreshHistory();};
        flow.Controls.Add(Stack(_historyUsage,clear));_historyList.DoubleClick += (_, _) => OpenHistorySelection();
        _historyList.MouseDown+=(_,e)=>HandleHistoryMouseDown(e.Location);
        _historyList.Resize+=(_,_)=>UpdateHistoryLayout();
        root.Controls.Add(flow,0,0);root.Controls.Add(_historyList,0,1);page.Controls.Add(root);
    }

    private void RefreshHistory()=>_=RefreshGalleryAsync();
    private int ResolveHistoryColumns()=>_historyList.Columns;
    private void HandleHistoryMouseDown(Point location){if(_historyList.HitTest(location)<0)_historyList.ClearSelection();}
    private int ResolveHistoryThumbnailSide()=>CurrentSettings.HistoryThumbnailSize switch{HistoryThumbnailSize.Small=>96,HistoryThumbnailSize.Large=>220,_=>150};
    private void UpdateHistoryLayout(bool allowThumbnailRefresh=true)=>_historyList.Reflow();
    private void ApplyHistoryScrollTheme()=>_historyList.Invalidate();

    private void OpenHistorySelection()
    {
        if (_historyList.SelectedRecord is not SessionHistoryItem item) return;
        if(item.OriginalPng.Length==0)
        {
            // A saved result is never used as source for OCR or regeneration.
            new SavedHistoryResultForm(item).Show(this);
            return;
        }
        OpenHistoryItem(item);
    }

    private PreviewForm OpenHistoryItem(SessionHistoryItem item)
    {
        AppLog.Write("history",$"HISTORY_ENTRY_CLICK timestamp={item.Timestamp:O} HISTORY_METADATA_LOADED sourcePath={item.SourceImagePath??"BYTES"} SOURCE_IMAGE_EXISTS={(item.SourceImagePath is null||File.Exists(item.SourceImagePath))} SOURCE_IMAGE_FILE_LENGTH={item.OriginalPng.LongLength}");
        Bitmap? image=null;Bitmap? translated=null;
        try
        {
            if(item.OriginalPng.Length==0)
                throw new InvalidOperationException("这条历史记录的原图已缺失，无法重新识别或生成译图。历史译文仍保留；请重新打开原图。");
            AppLog.Write("history","IMAGE_LOAD_START");image=DecodeOwnedBitmap(item.OriginalPng,out var rawFormat);
            if(item.TranslatedImage is not null)translated=DecodeOwnedBitmap(item.TranslatedImage,out _);
            AppLog.Write("history",$"IMAGE_LOAD_COMPLETE IMAGE_WIDTH={image.Width} IMAGE_HEIGHT={image.Height} IMAGE_PIXEL_FORMAT={image.PixelFormat} IMAGE_RAW_FORMAT={rawFormat} IMAGE_OBJECT_HASH={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(image)}");
            var preview=CreateAndShowPreview(new CaptureResult(image,PreviewMode.OcrOnly),item,translated);image=null;return preview;
        }
        catch(Exception ex){AppLog.Write("history","FIRST_EXCEPTION FIRST_INVALID_STAGE=HISTORY_IMAGE_DECODE_OR_PREVIEW_ASSIGN",ex);throw;}
        finally{image?.Dispose();translated?.Dispose();}
    }

    private static Bitmap DecodeOwnedBitmap(byte[] bytes,out Guid rawFormat)
    {
        using var stream=new MemoryStream(bytes,false);using var decoded=Image.FromStream(stream,true,true);rawFormat=decoded.RawFormat.Guid;
        return new Bitmap(decoded);
    }

    private void BuildSettingsPage(Panel page)
    {
        _themeModeBox.Items.AddRange(["日间","夜间","自定义"]);_closeBehaviorBox.Items.AddRange(["退出程序","最小化到托盘"]);
        _historyThumbnailBox.Items.AddRange(["小","中","大"]);
        _backgroundComputeBox.Items.AddRange(["CPU","GPU（NVIDIA）"]);
        _backgroundTreatmentBox.Items.AddRange(["精细修复","均衡清理（实验）"]);
        _backgroundTreatmentBox.SelectedIndexChanged+=(_,_)=>_backgroundComputeBox.Enabled=_backgroundTreatmentBox.SelectedIndex!=1;
        _ocrLoadBox.Items.AddRange(["标准","低负载（实验）"]);
        _ocrEngineBox.Items.Add("RapidOCR（快速）");_visualModelBox.Items.Add("关闭（产品模式）");
        _ocrLanguageBox.DropDownStyle=ComboBoxStyle.DropDownList;_cleanupStrengthBox.Items.AddRange(["关闭","普通","严格"]);_targetLanguageBox.Items.AddRange(UiTargetLanguageDisplay.DisplayNames.ToArray());_sourceLanguageBox.Items.AddRange(Enum.GetValues<SourceLanguageMode>().Select(UiStrings.SourceLanguageName).ToArray());_translationStyleBox.Items.AddRange(Enum.GetValues<TranslationStyle>().Select(UiStrings.TranslationStyleName).ToArray());_apiKeyBox.UseSystemPasswordChar=true;_previewDefaultImageBox.Items.AddRange(["原图","译图（存在时）"]);_previewDefaultTextBox.Items.AddRange(["OCR 原文","整理后原文","译文（存在时）"]);_translationSourceBox.Items.AddRange(["自动（推荐）","整理后原文","OCR 原文"]);
        _previewSizingBox.Items.AddRange(Enum.GetValues<PreviewWindowSizingMode>().Select(UiStrings.PreviewSizeModeName).ToArray());_uiFontModeBox.Items.AddRange(["产品默认","自定义字体"]);_previewFontModeBox.Items.AddRange(["产品默认","跟随软件界面字体","自定义字体"]);_overlayFontModeBox.Items.AddRange(["产品默认","自定义字体"]);_overlayBackgroundBox.Items.AddRange(Enum.GetValues<TranslationOverlayBackgroundStyle>().Select(UiStrings.BackgroundStyleName).ToArray());PrepareFontChoices();
        foreach(var box in new[]{_previewSizingBox,_uiFontModeBox,_previewFontModeBox,_overlayFontModeBox,_overlayBackgroundBox})box.SelectedIndexChanged+=(_,_)=>UpdateAppearanceEnabled();
        // Values and handlers are initialized before reading settings; layout is prepared separately.
        _embeddedKey.Items.AddRange(Enumerable.Range(1,12).Select(x=>(object)("F"+x)).ToArray());
        _configPathLabel.AutoSize=true;_configPathLabel.MaximumSize=new(600,0);_keyStatusLabel.AutoSize=true;

    }

    private static TableLayoutPanel SettingsTable(){var t=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,Padding=new Padding(12),BackColor=Color.FromArgb(18,23,34),ForeColor=Color.White};t.ColumnStyles.Add(new(SizeType.Absolute,160));t.ColumnStyles.Add(new(SizeType.Percent,100));return t;}private SettingsPage SettingsTab(string name,Control content){var p=new SettingsPage(name){AutoScroll=true,BackColor=Color.FromArgb(18,23,34),ForeColor=Color.White,Padding=new Padding(8),Tag=content};p.Controls.Add(content);AttachHostDiagnostics(p,$"SettingsTab:{name}");AttachHostDiagnostics(content,$"SettingsRoot:{name}");EnsureSettingsVisibility(p);return p;}
    private Control BuildCustomThemeEditor()
    {
        var panel=new FlowLayoutPanel{AutoSize=true,WrapContents=true};
        Button Add(string name,Func<ApiSettings,string> get,Action<ApiSettings,string> set){var button=MakeButton(name,Point.Empty,new Size(105,34));button.Click+=(_,_)=>{using var dialog=new ColorDialog{FullOpen=true,Color=UiTheme.Parse(get(_translationOptions),UiTheme.Parse(get(new ApiSettings()),Color.Black))};if(dialog.ShowDialog(this)!=DialogResult.OK)return;set(_translationOptions,ColorTranslator.ToHtml(dialog.Color));_themeModeBox.SelectedIndex=(int)ApplicationThemeMode.Custom;ApplyDesktopAppearance(CurrentSettings);_settingsDirty=true;};panel.Controls.Add(button);return button;}
        Add("主背景",s=>s.CustomThemeMainBackground,(s,v)=>s.CustomThemeMainBackground=v);Add("次级背景",s=>s.CustomThemeSecondaryBackground,(s,v)=>s.CustomThemeSecondaryBackground=v);Add("正文",s=>s.CustomThemeText,(s,v)=>s.CustomThemeText=v);Add("次级文字",s=>s.CustomThemeSecondaryText,(s,v)=>s.CustomThemeSecondaryText=v);Add("强调色",s=>s.CustomThemeAccent,(s,v)=>s.CustomThemeAccent=v);Add("边框",s=>s.CustomThemeBorder,(s,v)=>s.CustomThemeBorder=v);return panel;
    }
    internal static readonly Color EnabledSettingLabelColor=Color.FromArgb(225,232,242);internal static readonly Color DisabledSettingLabelColor=Color.FromArgb(170,182,200);
    private static void EnsureSettingsVisibility(Control root)=>UiDarkTheme.Apply(root);

    private void BuildSettingsPageLegacy(Panel page)
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 1, Padding = new Padding(12) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var title = PageTitle("设置"); table.Controls.Add(title, 0, 0); table.SetColumnSpan(title, 2);
        AddSettingRow(table, "快捷键", BuildHotKeyEditor());
        AddSettingRow(table, "OCR引擎", _ocrEngineBox);
        _ocrLanguageBox.DropDownStyle = ComboBoxStyle.DropDownList; AddSettingRow(table, "OCR语言", _ocrLanguageBox);
        _cleanupStrengthBox.Items.AddRange(["关闭", "普通", "严格"]); AddSettingRow(table, "文字整理强度", _cleanupStrengthBox);
        AddSettingRow(table, "API地址", _apiUrlBox); _apiKeyBox.UseSystemPasswordChar = true; AddSettingRow(table, "API Key", _apiKeyBox);
        AddSettingRow(table, "模型", _modelBox); AddSettingRow(table, "目标语言", _targetLanguageBox);
        AddSettingRow(table, "首字节超时(秒)", _firstByteTimeoutBox); AddSettingRow(table, "完整请求超时(秒)", _requestTimeoutBox);
        AddSettingRow(table, "Thinking", Info("Disabled（固定）")); AddSettingRow(table, "缓存", Stack(_ocrCacheBox, _translationCacheBox));
        AddSettingRow(table, "历史上限", _historyLimitBox); AddSettingRow(table, "译图", _imageTranslationBox);
        _previewDefaultImageBox.Items.AddRange(["原图", "译图（存在时）"]);
        _previewDefaultTextBox.Items.AddRange(["OCR原文", "整理后原文", "译文（存在时）"]);
        _translationSourceBox.Items.AddRange(["自动（推荐）", "整理后原文", "OCR原文"]);
        AddSettingRow(table, "预览默认图片", _previewDefaultImageBox);
        AddSettingRow(table, "预览默认文本", _previewDefaultTextBox);
        AddSettingRow(table, "翻译文本来源", _translationSourceBox);
        AddSettingRow(table, "预览选择", _rememberLastPreviewBox);
        _configPathLabel.AutoSize = true; _configPathLabel.MaximumSize = new Size(720, 0); AddSettingRow(table, "配置文件", _configPathLabel);
        _keyStatusLabel.AutoSize = true; AddSettingRow(table, "密钥状态", _keyStatusLabel);
        var save = MakeButton("保存设置", Point.Empty, new Size(140, 40)); save.Click += (_, _) => SaveSettings(true);
        var test = MakeButton("测试连接", Point.Empty, new Size(140, 40)); test.Click += async (_, _) => await TestApiAsync(test);
        var translation = MakeButton("翻译设置…", Point.Empty, new Size(140, 40));
        translation.Click += (_, _) => ShowGlobalTranslationSettings();
        AddSettingRow(table, "", Stack(save, test, translation)); page.Controls.Add(table);
    }

    private Control BuildHotKeyEditor()
    {
        _modifierBox.DropDownStyle = ComboBoxStyle.DropDownList; _modifierBox.Items.AddRange(["Ctrl + Alt", "Ctrl + Shift", "Alt + Shift"]); _modifierBox.SelectedIndex = 0;
        _keyBox.DropDownStyle = ComboBoxStyle.DropDownList; _keyBox.Items.AddRange(Enum.GetNames<Keys>().Where(n => n.Length == 1 && char.IsLetterOrDigit(n[0])).ToArray()); _keyBox.SelectedItem = "Z";
        var apply = MakeButton("应用快捷键", Point.Empty, new Size(145, 34)); apply.Click += (_, _) => ApplyHotKey(true);
        return Stack(_modifierBox, _keyBox, apply);
    }
    private Control BuildBindingEditor(InputActionId action)
    {
        var current=action switch{InputActionId.StartCapture=>_translationOptions.StartCaptureBinding??InputBindingDefaults.StartCapture,InputActionId.CancelCapture=>_translationOptions.CancelCaptureBinding??InputBindingDefaults.CancelCapture,InputActionId.ToggleResults=>_translationOptions.ToggleResultsBinding??InputBindingDefaults.ToggleResults,InputActionId.PreviewZoomIn=>_translationOptions.PreviewZoomInBinding??InputBindingDefaults.ZoomIn,InputActionId.PreviewZoomOut=>_translationOptions.PreviewZoomOutBinding??InputBindingDefaults.ZoomOut,_=>_translationOptions.PreviewResetFitBinding??InputBindingDefaults.ResetFit};
        var editor=new InputBindingCaptureButton(current);void Set(InputBinding b){switch(action){case InputActionId.StartCapture:_translationOptions.StartCaptureBinding=b;_startBindingEditor=editor;break;case InputActionId.CancelCapture:_translationOptions.CancelCaptureBinding=b;_cancelBindingEditor=editor;break;case InputActionId.ToggleResults:_translationOptions.ToggleResultsBinding=b;_toggleBindingEditor=editor;break;case InputActionId.PreviewZoomIn:_translationOptions.PreviewZoomInBinding=b;_zoomInBindingEditor=editor;break;case InputActionId.PreviewZoomOut:_translationOptions.PreviewZoomOutBinding=b;_zoomOutBindingEditor=editor;break;default:_translationOptions.PreviewResetFitBinding=b;_resetFitBindingEditor=editor;break;}_settingsDirty=true;ShowBindingWarnings();}editor.BindingChanged+=Set;Set(current);
        editor.Tag=action;var change=MakeButton("修改",Point.Empty,new(70,32));change.Tag=action;BindBindingCommandButton(change,()=>{TraceBinding($"UiEvent=ModifyCommand Target={action} BEFORE={CaptureStateDiagnostic()}");BeginInvoke(()=>BeginBindingCapture(action,editor));});
        var clear=MakeButton("清除",Point.Empty,new(70,32));BindBindingCommandButton(clear,()=>{CancelBindingCapture("ClearCommand");ApplyBinding(action,editor,new(),"Clear");});
        var reset=MakeButton("恢复默认",Point.Empty,new(90,32));BindBindingCommandButton(reset,()=>{CancelBindingCapture("RestoreDefaultCommand");var value=action switch{InputActionId.StartCapture=>InputBindingDefaults.StartCapture,InputActionId.CancelCapture=>InputBindingDefaults.CancelCapture,InputActionId.ToggleResults=>InputBindingDefaults.ToggleResults,InputActionId.PreviewZoomIn=>InputBindingDefaults.ZoomIn,InputActionId.PreviewZoomOut=>InputBindingDefaults.ZoomOut,_=>InputBindingDefaults.ResetFit};ApplyBinding(action,editor,value,"RestoreDefault");});
        return Stack(editor,change,clear,reset);
    }
    private void BindBindingCommandButton(Button button,Action command){button.Click+=(_,_)=>command();button.MouseUp+=(_,e)=>{if(e.Button==MouseButtons.Right&&button.ClientRectangle.Contains(e.Location)){TraceBinding($"UiEvent=BindingCommandRightClick Text={button.Text} BEFORE={CaptureStateDiagnostic()}");command();}};}
    private void TraceBinding(string value){var line=$"{DateTimeOffset.Now:O} {value}";_bindingCaptureDiagnostics.Add(line);AppLog.Write("binding-capture",line);}
    private string CaptureStateDiagnostic()=> $"State={_bindingCaptureState};Active={_activeBindingAction?.ToString()??"NONE"};Committed={_activeBindingCapture?.Binding.DisplayName??"NONE"};Pending=NONE;UiText={_activeBindingCapture?.Text??"NONE"}";
    private void BeginBindingCapture(InputActionId action,InputBindingCaptureButton editor){TraceBinding($"BeginRequested Target={action} BEFORE={CaptureStateDiagnostic()}");CancelBindingCapture("NewCapture");_activeBindingAction=action;_activeBindingCapture=editor;editor.BeginCapture();_bindingCaptureState=BindingCaptureState.Arming;Application.Idle+=ArmBindingCaptureWhenReleased;BeginInvoke(TryCompleteBindingArming);TraceBinding($"ArmingStarted Target={action} AFTER={CaptureStateDiagnostic()} Control=0x{editor.Handle.ToInt64():X}");}
    private void ArmBindingCaptureWhenReleased(object? sender,EventArgs e)=>TryCompleteBindingArming();
    private void TryCompleteBindingArming(){if(_bindingCaptureState!=BindingCaptureState.Arming||_activeBindingCapture is null){Application.Idle-=ArmBindingCaptureWhenReleased;return;}if(!AllMouseButtonsReleased())return;Application.Idle-=ArmBindingCaptureWhenReleased;_bindingCaptureState=BindingCaptureState.Capturing;PublishBindingInputLease();TraceBinding($"ArmingCompleted AllMouseButtonsReleased=YES AFTER={CaptureStateDiagnostic()}");}
    private static bool AllMouseButtonsReleased()=>!MouseKeyDown(0x01)&&!MouseKeyDown(0x02)&&!MouseKeyDown(0x04)&&!MouseKeyDown(0x05)&&!MouseKeyDown(0x06);
    private static bool MouseKeyDown(int virtualKey)=>(GetAsyncKeyState(virtualKey)&0x8000)!=0;
    private void CancelBindingCapture(string reason="Cancel"){if(_activeBindingCapture is not null)_inputRuntime.EndLease();TraceBinding($"CancelCalled Reason={reason} BEFORE={CaptureStateDiagnostic()}");Application.Idle-=ArmBindingCaptureWhenReleased;_bindingCaptureState=BindingCaptureState.Idle;_activeBindingCapture?.CancelCapture();_activeBindingCapture=null;_activeBindingAction=null;TraceBinding($"CancelCompleted Reason={reason} AFTER={CaptureStateDiagnostic()}");}
    private void CommitBindingCapture(InputBinding binding){if(_bindingCaptureState!=BindingCaptureState.Capturing||_activeBindingCapture is null){TraceBinding($"CommitIgnored State={_bindingCaptureState} Pending={binding.DisplayName}");return;}var target=_activeBindingCapture;var action=_activeBindingAction;_inputRuntime.EndLease();TraceBinding($"Pending Target={action} Binding={binding.DisplayName}");if(IsHighRiskGlobalMouseBinding(action,binding)&&AppDialog.Show(this,"确认高风险全局快捷键","鼠标左键或右键是常用系统操作，设为全局快捷键可能造成频繁触发。确定保存？",AppDialogKind.Confirmation)!=DialogResult.Yes){TraceBinding($"HighRiskCommitRejected Target={action} Binding={binding.DisplayName}");CancelBindingCapture("HighRiskConfirmationRejected");return;}Application.Idle-=ArmBindingCaptureWhenReleased;_bindingCaptureState=BindingCaptureState.Idle;_activeBindingCapture=null;_activeBindingAction=null;ApplyBinding(action!.Value,target,binding,"ModifyCommit");}
    private void ApplyBinding(InputActionId action,InputBindingCaptureButton editor,InputBinding binding,string reason){TraceBinding($"RuntimeApply Begin Action={action} Reason={reason} OldConfigured={editor.Binding.DisplayName} New={binding.DisplayName} OldRuntime={RuntimeBindingFor(action)?.DisplayName??"NONE"}");editor.SetBinding(binding);ApplyHotKey(false);SaveSettings(false);TraceBinding($"RuntimeApply End Action={action} Configured={BindingForAction(action).DisplayName} Runtime={RuntimeBindingFor(action)?.DisplayName??"NONE"}");}
    private InputBinding BindingForAction(InputActionId action)=>action switch{InputActionId.StartCapture=>_translationOptions.StartCaptureBinding??new(),InputActionId.CancelCapture=>_translationOptions.CancelCaptureBinding??new(),InputActionId.ToggleResults=>_translationOptions.ToggleResultsBinding??new(),InputActionId.PreviewZoomIn=>_translationOptions.PreviewZoomInBinding??new(),InputActionId.PreviewZoomOut=>_translationOptions.PreviewZoomOutBinding??new(),_=>_translationOptions.PreviewResetFitBinding??new()};
    private InputBinding? RuntimeBindingFor(InputActionId action)
    {var value=_inputRuntime.RegisteredBinding(action);return value is null||value.Kind==InputBindingKind.None?null:value;}
    private static bool IsHighRiskGlobalMouseBinding(InputActionId? action,InputBinding binding)=>(action is InputActionId.StartCapture or InputActionId.CancelCapture or InputActionId.ToggleResults)&&binding.Kind==InputBindingKind.MouseButton&&(binding.MouseButton is InputMouseButton.Left or InputMouseButton.Right);
    private bool CaptureMouseFromHook(InputBinding binding){if(_activeBindingCapture is null||_bindingCaptureState==BindingCaptureState.Idle)return false;if(_bindingCaptureState==BindingCaptureState.Arming){TraceBinding($"ArmingInputIgnored Candidate={binding.DisplayName}");return true;}if(binding.Kind==InputBindingKind.MouseButton&&binding.MouseButton is InputMouseButton.Left or InputMouseButton.Right){var overEditor=_activeBindingCapture.RectangleToScreen(_activeBindingCapture.ClientRectangle).Contains(Cursor.Position);if(!overEditor){TraceBinding($"HookCancel Target={_activeBindingAction} Reason=InteractionOutsideCaptureEditor Candidate={binding.DisplayName}");CancelBindingCapture("InteractionOutsideCaptureEditor");return false;}}TraceBinding($"HookCapture Target={_activeBindingAction} Candidate={binding.DisplayName}");BeginInvoke(()=>CommitBindingCapture(binding));return true;}
    private bool CaptureKeyboardFromHook(InputBinding binding)
    {
        if(_activeBindingCapture is null||_bindingCaptureState==BindingCaptureState.Idle)return false;
        if(binding.Key==Keys.Escape){BeginInvoke(()=>CancelBindingCapture("EscapeKeyboardHook"));return true;}
        if(_bindingCaptureState!=BindingCaptureState.Capturing)return false;
        if(IsModifierTrigger(binding.Key))
        {
            TraceBinding($"ModifierHeld Target={_activeBindingAction} Key={binding.Key} CandidateDeferred=YES");
            return true;
        }
        BeginInvoke(()=>CommitBindingCapture(binding));return true;
    }
    private static bool IsModifierTrigger(Keys key)=>key is
        Keys.ControlKey or Keys.LControlKey or Keys.RControlKey or
        Keys.Menu or Keys.LMenu or Keys.RMenu or
        Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey or
        Keys.LWin or Keys.RWin;
    private InputContextKind CurrentInputContext()
    {
        if(_activeBindingCapture is not null&&_bindingCaptureState!=BindingCaptureState.Idle)return InputContextKind.BindingCapture;
        if(_captureOpen)return InputContextKind.ScreenshotSelection;
        if(OwnedForms.Any(x=>x.Visible&&x.Modal&&x.ContainsFocus))return InputContextKind.ModalProtected;
        if(_previews.Any(x=>!x.IsDisposed&&x.ContainsFocus))return InputContextKind.Preview;
        return ContainsFocus?InputContextKind.MainWindow:InputContextKind.Idle;
    }
    private bool IsProtectedMouseContext(InputBinding binding){if(_captureOpen||_previews.Any(x=>!x.IsDisposed&&x.ContainsFocus&&x.Bounds.Contains(Cursor.Position)))return true;if(binding.Kind==InputBindingKind.MouseButton&&binding.MouseButton is InputMouseButton.Left or InputMouseButton.Right&&Bounds.Contains(Cursor.Position))return true;return false;}
    public bool PreFilterMessage(ref Message m)
    {
        // UI-thread fallback only. Never capture messages belonging to another
        // foreground window or commit an outside click before its cancel is queued.
        if(_activeBindingCapture is not { } editor||_bindingCaptureState==BindingCaptureState.Idle
            ||DesktopInputRuntime.GetForegroundWindow()!=Handle)return false;
        var keyMessage=m.Msg is 0x100 or 0x104;
        if(keyMessage&&(Keys)m.WParam.ToInt32()==Keys.Escape){CancelBindingCapture("Escape");return true;}
        if(_bindingCaptureState!=BindingCaptureState.Capturing)return false;
        var mods=ModifierKeys;
        var win=MouseKeyDown((int)Keys.LWin)||MouseKeyDown((int)Keys.RWin);
        if(keyMessage)
        {
            var key=(Keys)m.WParam.ToInt32();if(IsModifierTrigger(key))return true; // Local recording editor must not commit a modifier alone.
            CommitBindingCapture(new(InputBindingKind.Keyboard,key,Ctrl:mods.HasFlag(Keys.Control),
                Alt:mods.HasFlag(Keys.Alt),Shift:mods.HasFlag(Keys.Shift),Win:win,
                Numpad:key==Keys.Enter&&((m.LParam.ToInt64()>>24)&1)!=0));return true;
        }
        if(m.Msg is not (0x201 or 0x204 or 0x207 or 0x20B or 0x20A))return false;
        if(m.Msg is 0x201 or 0x204 && !editor.RectangleToScreen(editor.ClientRectangle).Contains(Cursor.Position))
        {CancelBindingCapture("InteractionOutsideCaptureEditor");return false;}
        var binding=m.Msg==0x20A
            ?new InputBinding(InputBindingKind.MouseWheel,Wheel:(short)((m.WParam.ToInt64()>>16)&0xffff)>0?InputWheelDirection.Up:InputWheelDirection.Down)
            :new InputBinding(InputBindingKind.MouseButton,MouseButton:m.Msg switch {
                0x201=>InputMouseButton.Left,0x204=>InputMouseButton.Right,0x207=>InputMouseButton.Middle,
                _=>((m.WParam.ToInt64()>>16)&0xffff)==1?InputMouseButton.XButton1:InputMouseButton.XButton2});
        CommitBindingCapture(binding with {Ctrl=mods.HasFlag(Keys.Control),Alt=mods.HasFlag(Keys.Alt),Shift=mods.HasFlag(Keys.Shift),Win=win});
        return true;
    }
    private void ShowBindingWarnings(){var entries=new[]{new InputBindingEntry(InputActionId.StartCapture,InputBindingScope.Global,_translationOptions.StartCaptureBinding??new()),new(InputActionId.CancelCapture,InputBindingScope.Global,_translationOptions.CancelCaptureBinding??new()),new(InputActionId.ToggleResults,InputBindingScope.Global,_translationOptions.ToggleResultsBinding??new()),new(InputActionId.PreviewZoomIn,InputBindingScope.PreviewLocal,_translationOptions.PreviewZoomInBinding??new()),new(InputActionId.PreviewZoomOut,InputBindingScope.PreviewLocal,_translationOptions.PreviewZoomOutBinding??new()),new(InputActionId.PreviewResetFit,InputBindingScope.PreviewLocal,_translationOptions.PreviewResetFitBinding??new())};var conflicts=InputBindingConflictDetector.Find(entries);if(conflicts.Count>0){var first=conflicts[0];var binding=entries.First(x=>x.Action==first.First).Binding;_statusLabel.Text=InputBindingConflictPresenter.Describe(binding,InputActionDisplayName(first.First),InputActionDisplayName(first.Second));}else _statusLabel.Text=entries.Where(x=>x.Scope==InputBindingScope.Global).Any(x=>InputBindingFormatter.IsRiskyGlobal(x.Binding))?"警告：此绑定可能在日常操作中频繁触发。":"就绪";}
    private static string InputActionDisplayName(InputActionId action)=>action switch{InputActionId.StartCapture=>"截图翻译",InputActionId.CancelCapture=>"取消截图",InputActionId.ToggleResults=>"切换结果",InputActionId.PreviewZoomIn=>"Preview 放大",InputActionId.PreviewZoomOut=>"Preview 缩小",_=>"Preview 适应窗口"};

    private void BuildDiagnosticsPage(Panel page)
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        flow.Controls.Add(PageTitle("诊断")); flow.Controls.Add(_diagnosticsSummary);
        var refresh = MakeButton("刷新诊断", Point.Empty, new Size(140, 38)); refresh.Click += (_, _) => RefreshDiagnostics();
        var export = MakeButton("导出诊断报告", Point.Empty, new Size(160, 38)); export.Click += (_, _) => ExportDiagnostics();
        var logs = MakeButton("打开日志目录", Point.Empty, new Size(170, 38)); logs.Click += (_, _) => OpenFolder(_ocrRuntimeManager.LogsRoot);
        flow.Controls.Add(Stack(refresh, export, logs)); page.Controls.Add(flow);
    }

    private void RefreshDiagnostics()
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var windows = _ocrRuntimeManager.GetStatus(OcrEngineKind.Windows);
        var rapid = _ocrRuntimeManager.GetStatus(OcrEngineKind.Rapid);
        var paddle = _ocrRuntimeManager.GetStatus(OcrEngineKind.Paddle);
        _diagnosticsSummary.Text =
            $"Product: {BuildIdentity.ProductName}\nDisplay Version: {BuildIdentity.DisplayVersion}\nBuild Version: {BuildIdentity.BuildVersion}\nBuild Id: {BuildIdentity.BuildId}\nStable Application Id: {BuildIdentity.StableApplicationId}\nCapture Backend: DXGI Primary / GDI Fallback\n" +
            $"Build Timestamp: {BuildIdentity.BuildTimestamp}\nEXE SHA256: {BuildIdentity.ExeHashShort}\nMain DLL SHA256: {BuildIdentity.MainDllHashShort}\n" +
            $"RC2 Baseline Hash: 391B6D8ED50F462DC56691576F45DE1136832721403C1FF9BD1C94CD17EB4B87\n" +
            $"Settings Schema: 3\nWindows OCR: {(windows.Ready ? "Ready" : "Unavailable")}\n" +
            $"RapidOCR: {(rapid.Ready ? "Ready" : "Unavailable")}\nPaddleOCR: {(paddle.Ready ? "Ready" : "Unavailable")}\n" +
            $"Active Worker PID: {_ocrRuntimeManager.ActiveWorkerPid?.ToString() ?? "None"}\n" +
            $"OCR Cache: {_session.OcrCount} (Hit {_session.OcrHits} / Miss {_session.OcrMisses})\n" +
            $"Translation Cache: {_session.TranslationCount} (Hit {_session.TranslationHits} / Miss {_session.TranslationMisses})\n" +
            $"History: {_session.HistoryCount}\nGDI Objects: {NativeMethods.GetGuiResources(process.Handle, 0)}\n" +
            $"Working Set: {process.WorkingSet64 / 1024d / 1024d:0.0} MiB";
    }

    private void ExportDiagnostics()
    {
        try
        {
            RefreshDiagnostics();
            var path = Path.Combine(_ocrRuntimeManager.LogsRoot, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, _diagnosticsSummary.Text);
            _statusLabel.Text = $"诊断报告已导出：{path}";
        }
        catch (Exception ex) { _statusLabel.Text = $"诊断报告导出失败：{ex.Message}"; }
    }

    private static void AddSettingRow(TableLayoutPanel table, string label, Control control)
    {
        var row = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = label, AutoSize = true, MinimumSize = new Size(120, 30), Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.White }, 0, row);
        control.Margin = new Padding(8, 6, 10, 6); control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        if(control is Button)control.Anchor=AnchorStyles.Left;
        if(!string.IsNullOrWhiteSpace(label))control.AccessibleName=label;
        if (control is TextBox or ComboBox) control.MinimumSize = new Size(160, 28);
        if(control is ComboBox)control.MaximumSize=new(340,0);
        if(control is NumericUpDown){control.Anchor=AnchorStyles.Left;control.Width=Math.Min(control.Width,110);}
        if(control is TextBox)control.MaximumSize=new(600,0);
        table.Controls.Add(control, 1, row);
    }
    private static FlowLayoutPanel Stack(params Control[] controls) { var p = new FlowLayoutPanel { AutoSize = true, WrapContents = true }; p.Controls.AddRange(controls); return p; }
    private void OpenFolder(string path) { try { Directory.CreateDirectory(path); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); } catch (Exception ex) { _statusLabel.Text = ex.Message; } }
    private void StartRuntimeInstaller()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "SETUP-OCR-RUNTIME.ps1");
        if (!File.Exists(script)) { _statusLabel.Text = "Runtime安装脚本不存在，Windows OCR仍可正常使用。"; return; }
        if (AppDialog.Show(this, "安装 / 修复 OCR 运行环境", "即将下载隔离 Python、PaddleOCR、RapidOCR 和官方模型，预计约 1.2GB。是否继续？", AppDialogKind.Confirmation) != DialogResult.Yes) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"") { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory });
            _statusLabel.Text = "OCR Runtime安装程序已启动；安装失败不会影响Windows OCR。";
        }
        catch (Exception ex) { _statusLabel.Text = "无法启动Runtime安装程序：" + ex.Message; }
    }

    private void BuildHotKeySection()
    {
        Controls.Add(LabelAt("截图快捷键", 32, 119));
        _modifierBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _modifierBox.Items.AddRange(["Ctrl + Alt", "Ctrl + Shift", "Alt + Shift"]);
        _modifierBox.SelectedIndex = 0;
        _modifierBox.SetBounds(142, 114, 145, 31);
        _keyBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _keyBox.Items.AddRange(Enum.GetNames<Keys>()
            .Where(n => n.Length == 1 && char.IsLetterOrDigit(n[0])).ToArray());
        _keyBox.SelectedItem = "T";
        _keyBox.SetBounds(298, 114, 85, 31);
        var apply = MakeButton("应用快捷键", new Point(396, 112), new Size(150, 36));
        apply.Click += (_, _) => ApplyHotKey(true);
        Controls.AddRange([_modifierBox, _keyBox, apply]);
    }

    private void BuildApiSection()
    {
        var group = new GroupBox
        {
            Text = "截图测试器独立 OCR / API 设置", ForeColor = Color.White,
            Location = new Point(32, 175), Size = new Size(690, 455)
        };
        _apiUrlBox.SetBounds(155, 39, 500, 30);
        _apiKeyBox.SetBounds(155, 84, 500, 30);
        _apiKeyBox.UseSystemPasswordChar = true;
        _modelBox.SetBounds(155, 129, 500, 30);
        _targetLanguageBox.SetBounds(155, 174, 500, 30);
        _ocrLanguageBox.SetBounds(155, 219, 250, 31);
        _ocrLanguageBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _firstByteTimeoutBox.SetBounds(255, 255, 145, 30);
        _requestTimeoutBox.SetBounds(255, 295, 145, 30);
        _firstByteTimeoutBox.TextAlign = HorizontalAlignment.Right;
        _requestTimeoutBox.TextAlign = HorizontalAlignment.Right;
        _cleanupStrengthBox.SetBounds(155, 325, 160, 31);
        _cleanupStrengthBox.Items.AddRange(["关闭", "普通", "严格"]);
        group.Controls.AddRange([
            LabelAt("API 地址", 22, 42), _apiUrlBox,
            LabelAt("API Key", 22, 87), _apiKeyBox,
            LabelAt("模型", 22, 132), _modelBox,
            LabelAt("目标语言", 22, 177), _targetLanguageBox,
            LabelAt("OCR 语言", 22, 222), _ocrLanguageBox,
            TimeoutLabel("首字节超时(秒)", 255), _firstByteTimeoutBox,
            TimeoutLabel("完整请求超时(秒)", 295), _requestTimeoutBox,
            LabelAt("文字整理强度", 22, 328), _cleanupStrengthBox
        ]);
        _configPathLabel.SetBounds(22, 360, 635, 22);
        _configPathLabel.AutoEllipsis = true;
        _configPathLabel.ForeColor = Color.FromArgb(170, 190, 220);
        _keyStatusLabel.SetBounds(22, 383, 635, 22);
        _keyStatusLabel.ForeColor = Color.FromArgb(170, 190, 220);
        var save = MakeButton("保存独立设置", new Point(155, 408), new Size(160, 40));
        save.Click += (_, _) => SaveSettings(true);
        var test = MakeButton("测试连接", new Point(330, 408), new Size(130, 40));
        test.Click += async (_, _) => await TestApiAsync(test);
        group.Controls.AddRange([_configPathLabel, _keyStatusLabel, save, test]);
        Controls.Add(group);
    }

    private static Label LabelAt(string text, int x, int y, float size = 10F, bool bold = false) => new()
    {
        AutoSize = true, Text = text,
        Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
        Location = new Point(x, y), ForeColor = Color.White
    };

    private static Label TimeoutLabel(string text, int y) => new()
    {
        AutoSize = false,
        Text = text,
        Font = new Font("Microsoft YaHei UI", 10F),
        Bounds = new Rectangle(22, y, 220, 30),
        TextAlign = ContentAlignment.MiddleRight,
        ForeColor = Color.White
    };

    private static Button MakeButton(string text, Point location, Size size) => new()
    {
        Text = text, Location = location, Size = size, FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(55, 105, 210), ForeColor = Color.White, Cursor = Cursors.Hand
    };

    protected override void WndProc(ref Message message)
    {
        if (HandleWorkspaceFrame(ref message)) return;
        base.WndProc(ref message);
        if (message.Msg == 0x24) ApplyWorkspaceMaximizedBounds(ref message);
    }

    private async Task LoadOcrLanguagesAsync()
    {
        try
        {
            var languages = await _ocrService.GetAvailableLanguagesAsync();
            if(IsDisposed||Disposing)return;
            // Guard only the synchronous binding, so edits made while discovery
            // is pending retain dirty tracking and their current selection.
            var selected = _ocrLanguageBox.SelectedItem?.ToString()??CurrentSettings.OcrLanguage;
            var wasApplying = _applyingSettings;_applyingSettings=true;
            try
            {
                _ocrLanguageBox.Items.Clear();
                _ocrLanguageBox.Items.AddRange(["自动", "英语", "简体中文", "日语", "韩语", "混合语言"]);
                _ocrLanguageBox.SelectedItem = _ocrLanguageBox.Items.Contains(selected) ? selected : "自动";
                _fusionOcrInfo.Text = $"自动/混合模式会合并已安装语言：{string.Join("、", languages)}";
            }
            finally{_applyingSettings=wasApplying;}
        }
        catch (Exception ex) { if(!IsDisposed)_fusionOcrInfo.Text = "无法读取 Windows OCR 语言：" + ex.Message; }
    }

    private async Task RunStartupHealthCheckAsync()
    {
        var rapid = _ocrRuntimeManager.GetStatus(OcrEngineKind.Rapid);
        var rapidProbe = await _ocrRuntimeManager.CheckAsync(OcrEngineKind.Rapid, CancellationToken.None);
        var visionRoot = ResolveBundledVisionRoot();
        var ppModel = Path.Combine(visionRoot, "paddlex", "official_models", "PP-DocLayout-S");
        var visionPython = Path.Combine(visionRoot, "venv", "Scripts", "python.exe");
        var ppInstalled = Directory.Exists(ppModel) && File.Exists(visionPython);
        var providerReady = !string.IsNullOrWhiteSpace(CurrentSettings.ApiUrl)
            && !string.IsNullOrWhiteSpace(CurrentSettings.ApiKey)
            && !string.IsNullOrWhiteSpace(CurrentSettings.Model);
        var rapidReady = rapid.Ready && rapidProbe.Ready;
        if (_homeOcrStatus is not null)
            _homeOcrStatus.Text = $"本地环境：RapidOCR {(rapidReady ? "正常" : "需恢复")} · 快捷键 {(AppDataPaths.DisableGlobalInput ? "本次启动禁用" : _hotKeyRegistered ? "正常" : "未注册")}";
        if (_homeApiStatus is not null)
            _homeApiStatus.Text = providerReady ? "翻译 Provider：已配置" : "翻译 Provider：尚未配置，请在设置 → 翻译中填写";

        var riskyPath = AppContext.BaseDirectory.Any(ch => ch > 127);
        if (!ModelManagerOperations.RequiredStartupComponentsReady(rapidReady, _hotKeyRegistered || AppDataPaths.DisableGlobalInput))
        {
            _statusLabel.Text = "首次运行检查发现本地组件或快捷键需要处理；请点击“检查并修复运行环境”。";
            AppDialog.Show(this, "首次运行检查",
                "本地运行环境未完全就绪。请使用主页的“检查并修复运行环境”入口。",
                AppDialogKind.Warning,
                $"Rapid={rapidReady}; PP-S=OFF (OptionalInstalled={ppInstalled}); HotKey={_hotKeyRegistered}; Runtime={_ocrRuntimeManager.RuntimeRoot}; Vision={visionRoot}");
        }
        else if (riskyPath)
        {
            AppDialog.Show(this, "安装路径提示",
                "当前安装路径包含非 ASCII 字符。部分 Paddle 原生组件可能无法正常加载；建议将整个软件目录移动到纯英文短路径后重试。",
                AppDialogKind.Warning, AppContext.BaseDirectory);
        }
    }

    private static string ResolveBundledVisionRoot()
    {
        // Final portable builds never follow development-time location pointers.
        return Path.Combine(AppContext.BaseDirectory, "vision-runtime");
    }

    private async Task TestApiAsync(Button button)
    {
        button.Enabled = false;
        _statusLabel.Text = "正在测试翻译 API…";
        try
        {
            await _translationService.TestAsync(CurrentSettings);
            SaveSettings(false);
            _statusLabel.Text = "API 连接成功，测试翻译已返回。";
        }
        catch (Exception ex) { _statusLabel.Text = "API 测试失败：" + ex.Message; }
        finally { button.Enabled = true; }
    }

    private void LoadSettings()
    {
        _settingsLoadCompleted = false;
        _settingsLoadSucceeded = false;
        _settingsDirty = false;
        _applyingSettings = true;
        try
        {
            if (!ConfigurationManager.TryLoad(_settingsPath, _allowLegacyMigration,
                    out var settings, out var error))
            {
                _statusLabel.Text = "配置未加载，关闭窗口不会保存：" + error;
                return;
            }
            ApplySettingsToControls(settings);
            ApplyDesktopAppearance(settings);
            _settingsLoadSucceeded = true;
        }
        finally
        {
            _applyingSettings = false;
            _settingsLoadCompleted = true;
            _settingsDirty = false;
        }
    }

    private void ApplySettingsToControls(ApiSettings settings)
    {
        _translationOptions = ApiSettingsSnapshot.Copy(settings);
        RefreshBindingEditors();
        _apiUrlBox.Text = settings.ApiUrl;
        _apiKeyBox.Text = settings.ApiKey;
        _modelBox.Text = settings.Model;
        _targetLanguageBox.SelectedItem = UiTargetLanguageDisplay.DisplayFromCode(settings.TargetLanguage);
        if (!_ocrLanguageBox.Items.Contains(settings.OcrLanguage))
            _ocrLanguageBox.Items.Add(settings.OcrLanguage);
        _ocrLanguageBox.SelectedItem=settings.OcrLanguage;
        ConfigurationManager.NormalizeTimeouts(settings);
        _firstByteTimeoutBox.Value = settings.FirstByteTimeoutSeconds;
        _requestTimeoutBox.Value = settings.RequestTimeoutSeconds;
        SelectIndexIfAvailable(_cleanupStrengthBox, (int)settings.CleanupStrength);
        SelectIndexIfAvailable(_ocrEngineBox, 0);
        SelectIndexIfAvailable(_visualModelBox, 0);
        _ocrCacheBox.Checked = settings.OcrCacheEnabled;
        _ocrLoadBox.SelectedIndex=settings.OcrLoad==OcrLoad.Low?1:0;
        _translationCacheBox.Checked = settings.TranslationCacheEnabled;
        _imageTranslationBox.Checked = settings.ImageTranslationEnabled;
        _backgroundComputeBox.SelectedIndex=settings.BackgroundComputeDevice==BackgroundComputeDevice.Gpu?1:0;
        _backgroundTreatmentBox.SelectedIndex=settings.BackgroundTreatment==BackgroundTreatment.Lightweight?1:0;
        _historyLimitBox.Value = Math.Clamp(settings.HistoryLimit, (int)_historyLimitBox.Minimum, (int)_historyLimitBox.Maximum);
        SelectIndexIfAvailable(_historyThumbnailBox,(int)settings.HistoryThumbnailSize);_historyTextSizeBox.Value=(decimal)settings.HistoryTextSize;_historyByCountBox.Checked=settings.HistoryCleanupByCount;_historyByAgeBox.Checked=settings.HistoryCleanupByAge;_historyDaysBox.Value=settings.HistoryRetentionDays;_historyBySpaceBox.Checked=settings.HistoryCleanupBySpace;_historySpaceBox.Value=settings.HistoryMaximumMegabytes;
        SelectIndexIfAvailable(_previewDefaultImageBox, (int)settings.PreviewDefaultImage);
        SelectIndexIfAvailable(_previewDefaultTextBox, (int)settings.PreviewDefaultText);
        SelectIndexIfAvailable(_translationSourceBox, (int)settings.TranslationTextSource);
        _rememberLastPreviewBox.Checked = settings.RememberLastPreviewSelection;
        _previewTextPanelVisibleBox.Checked=settings.PreviewTextPanelVisible;
        SelectIndexIfAvailable(_themeModeBox,(int)settings.ThemeMode);
        SelectIndexIfAvailable(_closeBehaviorBox,(int)settings.CloseMainWindowBehavior);
        _hideMainDuringCaptureBox.Checked = settings.HideMainWindowDuringCapture;
        _hidePreviewsDuringCaptureBox.Checked = settings.HidePreviewWindowsDuringCapture;
        SelectIndexIfAvailable(_sourceLanguageBox, (int)settings.SourceLanguage);
        SelectIndexIfAvailable(_translationStyleBox, (int)settings.TranslationStyle);
        _providerDisplayNameBox.Text = settings.ProviderDisplayName;
        _customPromptBox.Text = settings.CustomTranslationPrompt;
        _preserveIdentifiersBox.Checked = settings.PreserveIdentifiers;
        _preserveNumbersBox.Checked = settings.PreserveNumbers;
        _preserveVariablesBox.Checked = settings.PreserveVariables;
        _modifierBox.SelectedItem = _modifierBox.Items.Contains(settings.HotKeyModifiers) ? settings.HotKeyModifiers : "Ctrl + Alt";
        _keyBox.SelectedItem = _keyBox.Items.Contains(settings.HotKeyKey) ? settings.HotKeyKey : "Z";
        if (_homeOcrStatus is not null) _homeOcrStatus.Text = $"OCR引擎：{settings.OcrEngine} · {(settings.OcrEngine == OcrEngineKind.Windows ? "Ready" : "按需检查")}";
        if (_homeApiStatus is not null) _homeApiStatus.Text = string.IsNullOrWhiteSpace(settings.ApiKey)
            ? "API：Not configured（仅OCR仍可用）" : $"API：Configured · {settings.Model}";
        UpdateConfigStatus(settings.ApiKey);
        _previewSizingBox.SelectedIndex=(int)settings.PreviewWindowSizingMode;_fixedPreviewWidthBox.Value=settings.FixedPreviewWidth;_fixedPreviewHeightBox.Value=settings.FixedPreviewHeight;_uiFontModeBox.SelectedIndex=(int)settings.UiFontMode;SelectFont(_uiFontBox,settings.UiFontFamily);_uiFontSizeBox.Value=(decimal)settings.UiFontSize;_previewFontModeBox.SelectedIndex=(int)settings.PreviewTextFontMode;SelectFont(_previewFontBox,settings.PreviewTextFontFamily);_previewFontSizeBox.Value=(decimal)settings.PreviewTextFontSize;_overlayFontModeBox.SelectedIndex=(int)settings.OverlayFontMode;SelectFont(_overlayFontBox,settings.OverlayFontFamily);_overlayFontSizeBox.Value=(decimal)settings.OverlayFontSize;_overlayBackgroundBox.SelectedIndex=(int)settings.OverlayBackgroundStyle;_overlayOpacityBox.Value=settings.OverlayBackgroundOpacity;UpdateAppearanceEnabled();
        // Apply appearance only after all saved values have been bound.
        ApplyDesktopAppearance(settings);
    }

    private void RefreshBindingEditors(){_startBindingEditor?.SetBinding(_translationOptions.StartCaptureBinding??InputBindingDefaults.StartCapture);_cancelBindingEditor?.SetBinding(_translationOptions.CancelCaptureBinding??InputBindingDefaults.CancelCapture);_toggleBindingEditor?.SetBinding(_translationOptions.ToggleResultsBinding??InputBindingDefaults.ToggleResults);_zoomInBindingEditor?.SetBinding(_translationOptions.PreviewZoomInBinding??InputBindingDefaults.ZoomIn);_zoomOutBindingEditor?.SetBinding(_translationOptions.PreviewZoomOutBinding??InputBindingDefaults.ZoomOut);_resetFitBindingEditor?.SetBinding(_translationOptions.PreviewResetFitBinding??InputBindingDefaults.ResetFit);}
    private static void SelectIndexIfAvailable(ComboBox box,int index)
    {
        if (box.Items.Count == 0) return;
        box.SelectedIndex = Math.Clamp(index, 0, box.Items.Count - 1);
    }

    private static void SelectFont(ComboBox box,string value){box.Text=value??"";}
    private void UpdateAppearanceEnabled(){_fixedPreviewWidthBox.Enabled=_fixedPreviewHeightBox.Enabled=_previewSizingBox.SelectedIndex==2;_uiFontBox.Enabled=_uiFontModeBox.SelectedIndex==1;_uiFontSizeBox.Enabled=true;UpdateUiFontExplanation();_previewFontBox.Enabled=_previewFontSizeBox.Enabled=_previewFontModeBox.SelectedIndex==2;_overlayFontBox.Enabled=_overlayFontSizeBox.Enabled=_overlayFontModeBox.SelectedIndex==1;_overlayOpacityBox.Enabled=_overlayBackgroundBox.SelectedIndex==1;}
    private void ResetAppearanceFromMain(){_previewSizingBox.SelectedIndex=0;_fixedPreviewWidthBox.Value=1220;_fixedPreviewHeightBox.Value=800;_uiFontModeBox.SelectedIndex=0;_uiFontBox.Text="";_uiFontSizeBox.Value=10;_previewFontModeBox.SelectedIndex=0;_previewFontBox.Text="";_previewFontSizeBox.Value=10;_overlayFontModeBox.SelectedIndex=0;_overlayFontBox.Text="";_overlayFontSizeBox.Value=0;_overlayBackgroundBox.SelectedIndex=0;_overlayOpacityBox.Value=105;UpdateTypographyStatus(CurrentSettings);UpdateAppearanceEnabled();FontSettingsPolicy.ApplyUi(this,CurrentSettings);_lastThemeEvent=$"{DateTimeOffset.Now:O} ResetAppearance";ValidateUiInvariants("AppearanceRefresh");}

    private void ShowAppearanceSettings()
    {
        using var dialog=new AppearanceSettingsDialog(CurrentSettings);if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        _translationOptions=ApiSettingsSnapshot.Copy(dialog.Value);ApplyAppearanceControls(dialog.Value);FontSettingsPolicy.ApplyUi(this,_translationOptions);SaveSettings(false);
        _statusLabel.Text="预览、字体与译图设置已保存；现有译图仅重新渲染，不会重新识别或调用翻译 API。";
    }
    private void ApplyAppearanceControls(ApiSettings s){_previewSizingBox.SelectedIndex=(int)s.PreviewWindowSizingMode;_fixedPreviewWidthBox.Value=s.FixedPreviewWidth;_fixedPreviewHeightBox.Value=s.FixedPreviewHeight;_uiFontModeBox.SelectedIndex=(int)s.UiFontMode;SelectFont(_uiFontBox,s.UiFontFamily);_uiFontSizeBox.Value=(decimal)s.UiFontSize;_previewFontModeBox.SelectedIndex=(int)s.PreviewTextFontMode;SelectFont(_previewFontBox,s.PreviewTextFontFamily);_previewFontSizeBox.Value=(decimal)s.PreviewTextFontSize;_overlayFontModeBox.SelectedIndex=(int)s.OverlayFontMode;SelectFont(_overlayFontBox,s.OverlayFontFamily);_overlayFontSizeBox.Value=(decimal)s.OverlayFontSize;_overlayBackgroundBox.SelectedIndex=(int)s.OverlayBackgroundStyle;_overlayOpacityBox.Value=s.OverlayBackgroundOpacity;UpdateTypographyStatus(s);UpdateAppearanceEnabled();}
    private void UpdateTypographyStatus(ApiSettings s){var ui=FontManager.ResolveUiProfile(s);var preview=FontManager.ResolvePreviewProfile(s);_typographyStatus.Text=$"UI: {ui.PrimaryFamily}  |  Preview: {preview.PrimaryFamily}  |  安全回退已启用";}

    private void ShowGlobalTranslationSettings()
    {
        using var dialog = new TranslationSettingsDialog(CurrentSettings);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var updated = dialog.ResultSettings;
        _translationOptions = ApiSettingsSnapshot.Copy(updated);
        _applyingSettings = true;
        try
        {
            _apiUrlBox.Text = updated.ApiUrl;
            _apiKeyBox.Text = updated.ApiKey;
            _modelBox.Text = updated.Model;
            _targetLanguageBox.SelectedItem = UiTargetLanguageDisplay.DisplayFromCode(updated.TargetLanguage);
            _sourceLanguageBox.SelectedIndex=(int)updated.SourceLanguage;_translationStyleBox.SelectedIndex=(int)updated.TranslationStyle;_providerDisplayNameBox.Text=updated.ProviderDisplayName;_customPromptBox.Text=updated.CustomTranslationPrompt;_preserveIdentifiersBox.Checked=updated.PreserveIdentifiers;_preserveNumbersBox.Checked=updated.PreserveNumbers;_preserveVariablesBox.Checked=updated.PreserveVariables;
        }
        finally { _applyingSettings = false; }
        foreach (var preview in _previews.Where(x => !x.IsDisposed && !x.Disposing))
            preview.ApplyTranslationSettings(updated);
        _settingsDirty = true;
        _statusLabel.Text = "翻译设置已更新；点击保存设置后写入用户配置。";
    }

    private void AttachSettingsDirtyTracking()
    {
        _apiUrlBox.TextChanged += SettingsControlChanged;
        _apiKeyBox.TextChanged += SettingsControlChanged;
        _modelBox.TextChanged += SettingsControlChanged;
        _targetLanguageBox.SelectedIndexChanged += SettingsControlChanged;
        _ocrLanguageBox.SelectedIndexChanged += SettingsControlChanged;
        _firstByteTimeoutBox.ValueChanged += SettingsControlChanged;
        _requestTimeoutBox.ValueChanged += SettingsControlChanged;
        _cleanupStrengthBox.SelectedIndexChanged += SettingsControlChanged;
        _ocrEngineBox.SelectedIndexChanged += SettingsControlChanged;
        _visualModelBox.SelectedIndexChanged += SettingsControlChanged;
        _ocrCacheBox.CheckedChanged += SettingsControlChanged;
        _translationCacheBox.CheckedChanged += SettingsControlChanged;
        _imageTranslationBox.CheckedChanged += SettingsControlChanged;
        _historyLimitBox.ValueChanged += SettingsControlChanged;
        _previewDefaultImageBox.SelectedIndexChanged += SettingsControlChanged;
        _previewDefaultTextBox.SelectedIndexChanged += SettingsControlChanged;
        _translationSourceBox.SelectedIndexChanged += SettingsControlChanged;
        _backgroundComputeBox.SelectedIndexChanged += SettingsControlChanged;
        _backgroundTreatmentBox.SelectedIndexChanged += SettingsControlChanged;
        _rememberLastPreviewBox.CheckedChanged += SettingsControlChanged;
        _hideMainDuringCaptureBox.CheckedChanged += SettingsControlChanged;
        _hidePreviewsDuringCaptureBox.CheckedChanged += SettingsControlChanged;
        _sourceLanguageBox.SelectedIndexChanged += SettingsControlChanged;
        _translationStyleBox.SelectedIndexChanged += SettingsControlChanged;
        _providerDisplayNameBox.TextChanged += SettingsControlChanged;
        _customPromptBox.TextChanged += SettingsControlChanged;
        _preserveIdentifiersBox.CheckedChanged += SettingsControlChanged;
        _preserveNumbersBox.CheckedChanged += SettingsControlChanged;
        _preserveVariablesBox.CheckedChanged += SettingsControlChanged;
        _modifierBox.SelectedIndexChanged += SettingsControlChanged;
        _keyBox.SelectedIndexChanged += SettingsControlChanged;
        foreach(var box in new[]{_previewSizingBox,_uiFontModeBox,_uiFontBox,_previewFontModeBox,_previewFontBox,_overlayFontModeBox,_overlayFontBox,_overlayBackgroundBox})box.SelectedIndexChanged+=SettingsControlChanged;
        foreach(var box in new[]{_uiFontBox,_previewFontBox,_overlayFontBox})box.TextChanged+=SettingsControlChanged;
        _themeModeBox.SelectedIndexChanged+=SettingsControlChanged;_closeBehaviorBox.SelectedIndexChanged+=SettingsControlChanged;
        _previewTextPanelVisibleBox.CheckedChanged+=SettingsControlChanged;_previewTextPanelVisibleBox.CheckedChanged+=(_,_)=>ApplyPreviewTextVisibilityFromSettings();
        _historyThumbnailBox.SelectedIndexChanged+=SettingsControlChanged;_historyTextSizeBox.ValueChanged+=SettingsControlChanged;_historyByCountBox.CheckedChanged+=SettingsControlChanged;_historyByAgeBox.CheckedChanged+=SettingsControlChanged;_historyDaysBox.ValueChanged+=SettingsControlChanged;_historyBySpaceBox.CheckedChanged+=SettingsControlChanged;_historySpaceBox.ValueChanged+=SettingsControlChanged;
        foreach(var number in new[]{_fixedPreviewWidthBox,_fixedPreviewHeightBox,_uiFontSizeBox,_previewFontSizeBox,_overlayFontSizeBox,_overlayOpacityBox})number.ValueChanged+=SettingsControlChanged;
    }

    private void ApplyPreviewTextVisibilityFromSettings(){if(_applyingSettings||!_settingsLoadSucceeded)return;_translationOptions.PreviewTextPanelVisible=_previewTextPanelVisibleBox.Checked;foreach(var preview in _previews.Where(x=>!x.IsDisposed))preview.ApplyTextPanelVisibility(_previewTextPanelVisibleBox.Checked);try{ConfigurationManager.Save(_settingsPath,CurrentSettings);}catch(Exception ex){AppLog.Write("preview-text","Unable to persist visibility",ex);}}
    private void SettingsControlChanged(object? sender, EventArgs e)
    {
        if (_applyingSettings || _loadingFusion || !_settingsLoadSucceeded) return;
        if(IsProfileControl(sender)){ProfileEdited();return;}
        _settingsDirty = true;
        // Applying a font recreates native control handles. Keep editing stable;
        // apply the complete font choice together when settings are saved.
    }

    private void ScheduleLiveUiFontApply()
    {
        if (_liveUiFontApplyPending || IsDisposed || Disposing) return;
        _liveUiFontApplyPending = true;
        BeginInvoke((Action)(() =>
        {
            _liveUiFontApplyPending = false;
            if (IsDisposed || Disposing || _applyingSettings) return;
            try
            {
                var preview = ApiSettingsSnapshot.Copy(_translationOptions);
                preview.UiFontMode = (UiFontMode)Math.Max(0, _uiFontModeBox.SelectedIndex);
                preview.UiFontFamily = _uiFontBox.Text;
                preview.UiFontSize = (float)_uiFontSizeBox.Value;
                ApplyDesktopAppearance(preview);
                UpdateTypographyStatus(preview);
            }
            catch (Exception ex)
            {
                AppLog.Write("typography", "Live Settings-page font preview used safe fallback", ex);
                ApplyDesktopAppearance(new ApiSettings());
            }
        }));
    }

    private void SaveSettings(bool show)
    {
        using var uiTiming=UiPerformanceTrace.Measure("settings-save" );
        RememberPosition(this);
        var selectedCategory=_settingsNavigation.SelectedIndex;
        _lastSettingsSaveEvent=$"{DateTimeOffset.Now:O} SaveSettings";
        try
        {
            if (!show && (!_settingsLoadCompleted || !_settingsLoadSucceeded || !_settingsDirty))
            {
                CaptureFlashDiagnosticLog.Write(
                    $"Settings save skipped loaded={_settingsLoadCompleted} succeeded={_settingsLoadSucceeded} dirty={_settingsDirty}");
                return;
            }
            SaveFusionState();
            var current = CurrentSettings;
            ConfigurationManager.Save(_settingsPath, current);
            ApplyHotKey(false);
            _translationOptions = ApiSettingsSnapshot.Copy(current);
            _session.ApplyHistoryCleanup(current);ApplyDesktopAppearance(current);RefreshFusionChrome();
            var livePreviews=_previews.Where(x=>!x.IsDisposed&&!x.Disposing).ToList();var active=livePreviews.LastOrDefault(x=>x.ContainsFocus)??livePreviews.LastOrDefault();
            foreach (var preview in livePreviews)
            {preview.ApplyPreviewWindowSettings(current,preview==active);preview.ApplyAppearanceSettings(current);}
            _settingsLoadCompleted = true;
            _settingsLoadSucceeded = true;
            _settingsDirty = false;
            UpdateConfigStatus(current.ApiKey);
            if(_currentMainPage=="主页")RefreshHistory();
            if (show) _statusLabel.Text = "设置已保存。截图设置用于新任务；游戏内设置需应用后在下次启动时生效。";
        }
        catch (Exception ex) { if (show) _statusLabel.Text = "设置保存失败：" + ex.Message; }
        finally{if(selectedCategory>=0&&selectedCategory<_settingsNavigation.Items.Count){_settingsNavigation.SelectedIndex=selectedCategory;_settingsTabs!.SelectedIndex=selectedCategory;}ValidateUiInvariants("SettingsSave");RestorePosition(this);_settingsNavigation.Commit(_settingsTabs?.SelectedIndex??-1);}
    }

    private void UpdateConfigStatus(string? key)
    {
        _configPathLabel.Text = "配置文件：" + _settingsPath;
        _configPathLabel.Tag = _settingsPath;
        _keyStatusLabel.Text = "API Key：" + ConfigurationManager.KeyStatus(key);
    }

    private void PublishBindingInputLease()
    {
        if(_activeBindingCapture is not { } editor||_bindingCaptureState!=BindingCaptureState.Capturing||!IsHandleCreated)return;
        _inputRuntime.BeginLease(Handle,true,editor.RectangleToScreen(editor.ClientRectangle),
            binding=>{if(ReferenceEquals(_activeBindingCapture,editor)&&_bindingCaptureState==BindingCaptureState.Capturing)CommitBindingCapture(binding);},
            ()=>{if(ReferenceEquals(_activeBindingCapture,editor))CancelBindingCapture("InputLeaseCancelled");});
    }
    private void ApplyHotKey(bool sound)
    {
        RemoveHotKey();
        if(_trueExit||IsDisposed||Disposing)return;
        if(AppDataPaths.DisableGlobalInput){_inputRuntime.Disable();_statusLabel.Text="本次启动已禁用全局快捷键与鼠标监听。";return;}
        var settings=CurrentSettings;
        var binding=settings.StartCaptureBinding??InputBindingDefaults.StartCapture;
        _statusLabel.Text="正在准备快捷键…";
        _inputRuntime.Configure([
            new(InputActionId.StartCapture,binding,BeginCapture),
            new(InputActionId.CancelCapture,settings.CancelCaptureBinding??InputBindingDefaults.CancelCapture,DispatchCancelBindingHotKey),
            new(InputActionId.ToggleResults,settings.ToggleResultsBinding??InputBindingDefaults.ToggleResults,ToggleResultsFromBinding)
        ],(ready,message)=>{
            if(IsDisposed||Disposing)return;
            _hotKeyRegistered=ready&&binding.Kind==InputBindingKind.Keyboard;
            _statusLabel.Text=ready?(binding.IsEmpty?"截图快捷键未绑定。":"快捷键已启用："+binding.DisplayName):message;
            if(ready)DesktopStages.Mark("input-listeners-ready");
            else if(sound)System.Media.SystemSounds.Exclamation.Play();
        });
        _modifierBox.Tag=_modifierBox.SelectedItem?.ToString();_keyBox.Tag=binding.Key.ToString();
    }
    private void RemoveHotKey()
    {
        _inputRuntime.Disable();
        _cancelHotKeyHost.Unregister();_toggleHotKeyHost.Unregister();
        _hotKeyHost.Unregister();
        _hotKeyRegistered = false;
    }

    private void DispatchCancelBindingHotKey(){if(_activeBindingCapture is not null&&_bindingCaptureState!=BindingCaptureState.Idle){TraceBinding($"EscapeHotKey Dispatch=CancelBindingCapture BEFORE={CaptureStateDiagnostic()}");CancelBindingCapture("EscapeGlobalHotKey");return;}TraceBinding("EscapeHotKey Dispatch=RuntimeCancelCapture");CancelCaptureFromBinding();}
    private void CancelCaptureFromBinding(){if(_captureOpen)_captureOverlay?.CancelFromBinding();}
    private void ToggleResultsFromBinding(){var live=_previews.Where(x=>!x.IsDisposed).ToArray();if(live.Length==0)return;var hide=live.Any(x=>x.Visible);foreach(var preview in live){if(hide)preview.Hide();else{preview.Show();preview.Activate();}}}

    private async void BeginCapture()
    {
        if (_captureOpen) return;
        if(CursorCaptureStageDump.Enabled&&!CursorCaptureStageDump.TryBeginRun(out var dumpError))
        {
            _statusLabel.Text=dumpError;
            return;
        }
        TemporaryCaptureVisibility? captureVisibility = null;
        RealExeE2ETrace.BeginHotkey();
        RealExeE2ETrace.Mark("T1 CaptureRequested");
        _captureOpen = true;
        var captureRequestedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        CaptureFlashDiagnosticLog.Write("BeginCapture enter");
        try
        {
            if (_captureDiagnostics.Mode == CaptureFlashDiagnosticMode.CaptureOnly)
            {
                CaptureFlashDiagnosticLog.Write("CopyFromScreen begin; MainForm remains visible");
                using var bitmap = CaptureOverlay.CaptureVirtualScreenForDiagnostic(SystemInformation.VirtualScreen);
                CaptureFlashDiagnosticLog.Write($"CopyFromScreen end size={bitmap.Width}x{bitmap.Height}");
                return;
            }
            if (_captureDiagnostics.Mode == CaptureFlashDiagnosticMode.AffinityCapture)
            {
                var applicationWindows = new List<Form> { this };
                applicationWindows.AddRange(_previews.Where(x => x.Visible && !x.IsDisposed));
                if (!TemporaryWindowCaptureExclusion.TryBegin(
                        applicationWindows, out var diagnosticExclusion, out var diagnosticError))
                {
                    _statusLabel.Text = $"AffinityCapture临时排除失败：{diagnosticError}";
                    return;
                }
                try
                {
                    NativeMethods.DwmFlush();
                    CaptureFlashDiagnosticLog.Write("Affinity CopyFromScreen begin; all application windows remain visible");
                    using var bitmap = CaptureOverlay.CaptureVirtualScreenForDiagnostic(SystemInformation.VirtualScreen);
                    var path = CaptureFlashDiagnosticLog.CreateArtifactPath("wda-exclude-capture.png");
                    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    CaptureFlashDiagnosticLog.Write($"Affinity CopyFromScreen end size={bitmap.Width}x{bitmap.Height} artifact={path}");
                }
                finally
                {
                    diagnosticExclusion!.Restore();
                }
                return;
            }
            if (_captureDiagnostics.Mode == CaptureFlashDiagnosticMode.HideCapture)
            {
                CaptureFlashDiagnosticLog.Write("MainForm.Hide begin");
                Hide();
                CaptureFlashDiagnosticLog.Write("MainForm.Hide end; DwmFlush begin");
                NativeMethods.DwmFlush();
                CaptureFlashDiagnosticLog.Write("DwmFlush end; CopyFromScreen begin");
                using var bitmap = CaptureOverlay.CaptureVirtualScreenForDiagnostic(SystemInformation.VirtualScreen);
                CaptureFlashDiagnosticLog.Write($"CopyFromScreen end size={bitmap.Width}x{bitmap.Height}");
                return;
            }

            CaptureOverlay overlay;
            if (_captureDiagnostics.Mode == CaptureFlashDiagnosticMode.OverlayOnly)
            {
                var bounds = SystemInformation.VirtualScreen;
                CaptureFlashDiagnosticLog.Write("Static bitmap preparation begin; no CopyFromScreen; MainForm remains visible");
                using var bitmap = CaptureFlashDiagnosticLog.CreateStaticDesktop(bounds);
                CaptureFlashDiagnosticLog.Write("Static bitmap preparation end; Overlay construction begin");
                overlay = new CaptureOverlay(bounds, bitmap,
                    _captureDiagnostics.NoActivate, _captureDiagnostics.NoShade);
            }
            else
            {
                overlay = _captureOverlay ??= new CaptureOverlay(reusableLifecycle: true);
                overlay.PrewarmHandle();
                var captureSettings = CurrentSettings;
                var visiblePreviews = _previews.Where(x => x.Visible && !x.IsDisposed).ToArray();
                if (!TemporaryCaptureVisibility.TryBegin(this, visiblePreviews,
                        captureSettings.HideMainWindowDuringCapture,
                        captureSettings.HidePreviewWindowsDuringCapture,
                        out captureVisibility, out var exclusionError))
                {
                    _statusLabel.Text = $"无法临时排除本程序窗口，本次截图已取消：{exclusionError}";
                    CaptureFlashDiagnosticLog.Write("Full capture aborted because temporary WDA exclusion failed");
                    return;
                }
                RealExeE2ETrace.Mark("T2 AppWindowsHidden");

                Exception? captureError = null;
                try
                {
                    CaptureFlashDiagnosticLog.Write("CopyFromScreen begin");
                    var cursor=SystemCursorCaptureAudit.Snapshot();
                    CaptureFlashDiagnosticLog.Write($"CAPTURE_START CURSOR_SCREEN_X={cursor.Position.X} CURSOR_SCREEN_Y={cursor.Position.Y} CURSOR_VISIBLE_STATE={cursor.Visible} CURSOR_HIDE_REQUESTED=NO CURSOR_HIDE_RESULT=NOT_APPLICABLE {cursor.Result}");
                    var captured=await _screenCaptureCoordinator.CaptureAsync(SystemInformation.VirtualScreen,captureRequestedAt);
                    CaptureFlashDiagnosticLog.Write($"RAW_CAPTURE_CREATED backend={captured.Metrics.CaptureBackend} RAW_CAPTURE_CURSOR_PRESENT=UNKNOWN(pixel gate required)");
                    if(CursorCaptureStageDump.Enabled)
                    {
                        CursorCaptureStageDump.RecordAutoCapture(captured,SystemInformation.VirtualScreen,cursor);
                        await CursorCaptureStageDump.CaptureForcedBackendsAsync(SystemInformation.VirtualScreen,captureRequestedAt);
                    }
                    try{overlay.PrepareReusableCapture(captured.Bitmap,captureRequestedAt);RealExeE2ETrace.SetCaptureMetrics(captured.Metrics);}
                    catch{captured.Bitmap.Dispose();throw;}
                    RealExeE2ETrace.Mark("T3 ScreenBitmapCaptured");
                    CaptureFlashDiagnosticLog.Write("FROZEN_BITMAP_CREATED FROZEN_CURSOR_PRESENT=UNKNOWN(pixel gate required) CopyFromScreen end; FrozenBitmap ready");
                }
                catch (Exception ex)
                {
                    captureError = ex;
                    CaptureFlashDiagnosticLog.Write($"CopyFromScreen failed type={ex.GetType().FullName} message={ex.Message}");
                }
                if (captureError is not null)
                {
                    _statusLabel.Text = $"屏幕冻结失败：{captureError.Message}";
                    overlay.ReleaseReusableCapture();
                    return;
                }

                CaptureFlashDiagnosticLog.Write("Capture visibility retained until CaptureOverlay lifecycle ends");
                CaptureFlashDiagnosticLog.Write("Reusable Overlay capture preparation end");
            }

            if(CursorCaptureStageDump.Enabled&&CursorCaptureStageDump.TryGetSurfaceTarget(out var targetRect))
                overlay.SetSelectionForRealE2ETest(targetRect);

            if (_captureDiagnostics.NoActivate)
            {
                CaptureFlashDiagnosticLog.Write("Overlay.Show without activation begin");
                overlay.FormClosed += (_, _) =>
                {
                    CaptureFlashDiagnosticLog.Write("Non-activating Overlay.FormClosed; dispose");
                    overlay.Dispose();
                };
                overlay.ShowNoActivateForDiagnostic();
                CaptureFlashDiagnosticLog.Write("Overlay.Show without activation end");
                return;
            }

            var reusableOverlay = ReferenceEquals(overlay, _captureOverlay);
            EventHandler overlayVisible=(_,_)=>
            {
                RealExeE2ETrace.Mark("T_OVERLAY_VISIBLE");
                var mainWindowVisibleWhileCaptureOverlayActive=Visible;
                TranslationService.DiagnosticLog($"MainWindowVisibleWhileCaptureOverlayActive={mainWindowVisibleWhileCaptureOverlayActive}");
                System.Diagnostics.Debug.Assert(!mainWindowVisibleWhileCaptureOverlayActive,
                    "MainWindow must remain hidden while the capture overlay is active.");
            };
            EventHandler inputLeaseVisibility=(_,_)=>{
                if(overlay.Visible)_inputRuntime.BeginLease(overlay.Handle,false,Rectangle.Empty,_=>{},()=>overlay.CancelFromBinding());
                else _inputRuntime.EndLease();
            };
            overlay.VisibleChanged+=inputLeaseVisibility;
            overlay.Shown+=overlayVisible;
            try
            {
                CaptureFlashDiagnosticLog.Write("Overlay.ShowDialog begin");
            if (overlay.ShowDialog() == DialogResult.OK && overlay.Result is not null)
            {
                var result = overlay.Result;
                if(CursorCaptureStageDump.Enabled)CursorCaptureStageDump.RecordPreviewSource(result.Image);
                if (result.Mode == PreviewMode.OcrCompare)
                    OpenOcrComparison(result.Image, autoRunPrimary: true);
                else
                {
                    // The Preview takes exclusive ownership of result.Image.  Make the
                    // deferred clipboard payload before that transfer so clipboard
                    // encoding can never race the Preview's OCR isolation clone.
                    Bitmap? clipboardImage=CloneCaptureForDeferredClipboard(result.Image);
                    try
                    {
                        CreateAndShowPreview(result);
                        var isolatedClipboardImage=clipboardImage;
                        clipboardImage=null;
                        BeginInvoke(new Action(()=>
                        {
                            using(isolatedClipboardImage)
                            {
                                try{ClipboardHelper.SetImage(isolatedClipboardImage);ToastNotifier.Show("已复制到剪贴板");}
                                catch(Exception ex){AppLog.Write("clipboard","Deferred screenshot copy failed",ex);}
                            }
                        }));
                    }
                    finally{clipboardImage?.Dispose();}
                }
                if(!result.ImageOwnershipTransferred)result.Image.Dispose();
            }
                CaptureFlashDiagnosticLog.Write("Overlay.ShowDialog end");
            }
            finally
            {
                _inputRuntime.EndLease();overlay.VisibleChanged-=inputLeaseVisibility;
                overlay.Shown-=overlayVisible;
                overlay.ResultPrepared = null;
                if (reusableOverlay) overlay.ReleaseReusableCapture();
                else overlay.Dispose();
            }
        }
        catch (Exception ex) when (_captureDiagnostics.Enabled)
        {
            CaptureFlashDiagnosticLog.Write($"ERROR type={ex.GetType().FullName} message={ex.Message}");
            _statusLabel.Text = $"截图闪屏诊断失败：{ex.GetType().Name}。请查看 logs。";
        }
        finally
        {
            if (captureVisibility is not null)
            {
                CaptureFlashDiagnosticLog.Write("CaptureOverlay lifecycle ended; capture visibility restore begin");
                captureVisibility.Restore();
                CaptureFlashDiagnosticLog.Write($"Capture visibility restore end success={captureVisibility.RestoreSucceeded}");
                if (!captureVisibility.RestoreSucceeded)
                    _statusLabel.Text = $"恢复窗口捕获状态失败：{captureVisibility.RestoreError}";
            }
            if (_captureDiagnostics.Mode == CaptureFlashDiagnosticMode.HideCapture && !Visible)
            {
                CaptureFlashDiagnosticLog.Write("MainForm.Show begin");
                Show();
                CaptureFlashDiagnosticLog.Write("MainForm.Show end");
            }
            _inputRuntime.EndLease();
            _captureOpen = false;
            CaptureFlashDiagnosticLog.Write("BeginCapture exit");
        }
    }

    private PreviewForm CreateAndShowPreview(CaptureResult result,SessionHistoryItem? historyItem=null,Bitmap? historyTranslatedImage=null)
    {
        if (result.Mode == PreviewMode.OcrCompare)
            throw new InvalidOperationException("OcrCompare capture must be dispatched to OcrComparisonForm.");
        RealExeE2ETrace.Mark("T_PREVIEW_CREATE_BEGIN");
        var ownership=System.Diagnostics.Stopwatch.StartNew();
        var preview=_prewarmedPreview;
        if(preview is not null)
        {
            _prewarmedPreview=null;preview.ApplyTranslationSettings(CurrentSettings);preview.ApplyAppearanceSettings(CurrentSettings);preview.AdoptCapturedImage(result.Image,result.Mode);
        }
        else preview = new PreviewForm(result.Image, result.Mode, CurrentSettings,
            new OcrService(), new TranslationService(), _ocrRuntimeManager, _visionRuntimeManager, _session, SavePreviewPlacement, takeImageOwnership:true);
        preview.NextBackgroundDevice=()=>CurrentSettings.BackgroundComputeDevice;
        preview.NextBackgroundTreatment=()=>CurrentSettings.BackgroundTreatment;
        preview.NextOcrLoad=()=>CurrentSettings.OcrLoad;
        result.ImageOwnershipTransferred=true;
        if(historyItem is not null)preview.RestoreHistoryState(historyItem.SourceText??historyItem.SourceSummary,
            historyItem.TranslationText??historyItem.TranslationSummary,historyTranslatedImage,historyItem.CoreSnapshot);
        ownership.Stop();PostConfirmPerformanceTrace.Write("PreviewOwnershipAssign",new{Resolution=$"{result.Image.Width}x{result.Image.Height}",ThreadId=Environment.CurrentManagedThreadId,DurationMs=ownership.Elapsed.TotalMilliseconds});
        RealExeE2ETrace.Mark("T_PREVIEW_CREATE_END");
        RealExeE2ETrace.Mark("T5 PreviewCreated");
        _previews.Add(preview);
        preview.HandleCreated += Preview_InputHandleCreated;
        preview.FormClosed += Preview_FormClosed;
        preview.ReSelectRequested += Preview_ReSelectRequested;
        preview.SelfCaptureRequested += Preview_SelfCaptureRequested;
        preview.AppearanceSettingsChanged += Preview_AppearanceSettingsChanged;
        try
        {
            _ = preview.Handle;
            PublishPreviewInputProtection();
            CaptureFlashDiagnosticLog.Write("PreviewForm.Show begin");
            var previewShowAudit=System.Diagnostics.Stopwatch.StartNew();
            WindowStateGuard.ShowPreviewWithoutRestoringMain(this, preview);
            previewShowAudit.Stop();PostConfirmPerformanceTrace.Write("PreviewShow",new{PreviewShowMs=previewShowAudit.Elapsed.TotalMilliseconds});
            RealExeE2ETrace.Mark("T6 PreviewFirstVisible");
            CaptureFlashDiagnosticLog.Write("PreviewForm.Show end");
            return preview;
        }
        catch
        {
            preview.HandleCreated -= Preview_InputHandleCreated;
            preview.FormClosed -= Preview_FormClosed;
            preview.ReSelectRequested -= Preview_ReSelectRequested;
            preview.SelfCaptureRequested -= Preview_SelfCaptureRequested;
            preview.AppearanceSettingsChanged -= Preview_AppearanceSettingsChanged;
            _previews.Remove(preview);
            PublishPreviewInputProtection();
            preview.Dispose();
            throw;
        }
    }

    internal static Bitmap CloneCaptureForDeferredClipboard(Bitmap capturedImage)=>new(capturedImage);

    private void SavePreviewPlacement(PreviewWindowPlacement placement)
    {
        if(_translationOptions.PreviewWindowSizingMode!=PreviewWindowSizingMode.FollowPrevious)return;
        _translationOptions.HasPreviewWindowPlacement = true;
        _translationOptions.PreviewWindowX = placement.X;
        _translationOptions.PreviewWindowY = placement.Y;
        _translationOptions.PreviewWindowWidth = placement.Width;
        _translationOptions.PreviewWindowHeight = placement.Height;
        _translationOptions.PreviewWindowMaximized = placement.Maximized;
        _translationOptions.LastPreviewWidth=placement.Width;_translationOptions.LastPreviewHeight=placement.Height;
        _translationOptions.HasLastUserPreviewBounds=true;
        _translationOptions.LastUserPreviewX=placement.X;_translationOptions.LastUserPreviewY=placement.Y;
        _translationOptions.LastUserPreviewWidth=placement.Width;_translationOptions.LastUserPreviewHeight=placement.Height;
        _translationOptions.LastUserPreviewMaximized=placement.Maximized;
        try { ConfigurationManager.Save(_settingsPath, CurrentSettings); }
        catch (Exception ex) { AppLog.Write("preview-window", "Unable to persist preview placement", ex); }
    }

    private void Preview_AppearanceSettingsChanged(ApiSettings settings)
    {
        _translationOptions=ApiSettingsSnapshot.Copy(settings);_applyingSettings=true;_previewTextPanelVisibleBox.Checked=settings.PreviewTextPanelVisible;_applyingSettings=false;ApplyAppearanceControls(settings);FontSettingsPolicy.ApplyUi(this,_translationOptions);
        _lastThemeEvent=$"{DateTimeOffset.Now:O} PreviewAppearanceChanged";
        try{ConfigurationManager.Save(_settingsPath,CurrentSettings);}catch(Exception ex){AppLog.Write("appearance","Unable to save appearance settings",ex);}
        var livePreviews=_previews.Where(x=>!x.IsDisposed).ToList();var active=livePreviews.LastOrDefault(x=>x.ContainsFocus)??livePreviews.LastOrDefault();foreach(var preview in livePreviews){preview.ApplyPreviewWindowSettings(_translationOptions,preview==active);preview.ApplyAppearanceSettings(_translationOptions);}ValidateUiInvariants("AppearanceRefresh");
    }
    internal void RestoreFromExternalLaunch(){if(IsDisposed)return;_hotKeyHost.Log("Single Instance Activate");ShowInTaskbar=true;Show();if(WindowState==FormWindowState.Minimized)WindowState=FormWindowState.Normal;_trayIcon.Visible=true;Activate();BringToFront();}

    internal int SettingsTabCountForSmoke=>_settingsTabs?.TabCount??0;
    internal string SelectSettingsTabForSmoke(int index){ShowPage("设置");if(_settingsTabs is null)return "";index=Math.Clamp(index,0,_settingsTabs.TabCount-1);_settingsNavigation.SelectedIndex=index;EnsureSettingsCategory(index);return _settingsTabs.SelectedTab?.Text??"";}
    internal void DrawCurrentPageForSmoke(Bitmap target)
    {
        // DrawToBitmap does not honor overlapping WinForms child z-order reliably.
        // Temporarily suppress sibling pages only while producing the automated artifact.
        var current=_pages.GetValueOrDefault(_currentMainPage);
        var siblings=_pages.Values.Where(x=>x!=current).ToArray();
        foreach(var sibling in siblings)sibling.Visible=false;
        try
        {
            DrawToBitmap(target,ClientRectangle);using var graphics=Graphics.FromImage(target);
            // Form.DrawToBitmap includes the non-client origin for a top-level window.
            // Use window-relative screen coordinates so the verification overlay lands
            // on the same pixels as the native TabControl frame.
            void PaintTabs(Control root){if(root is DarkTabControl tabs){var screen=tabs.PointToScreen(Point.Empty);var here=new Point(screen.X-Left,screen.Y-Top);tabs.PaintThemeFrame(graphics,here);}foreach(Control child in root.Controls)PaintTabs(child);}
            foreach(Control child in Controls)PaintTabs(child);
        }
        finally{foreach(var sibling in siblings)sibling.Visible=true;current?.BringToFront();}
    }
    internal IReadOnlyList<string> SettingsVisibleTextsForSmoke(){var values=new List<string>();void Scan(Control c){if(!string.IsNullOrWhiteSpace(c.Text))values.Add(c.Text);if(c is ComboBox box)values.AddRange(box.Items.Cast<object>().Select(x=>x?.ToString()??""));foreach(Control child in c.Controls)Scan(child);}if(_settingsTabs is not null)Scan(_settingsTabs);return values;}
    internal static readonly string[] MainPageNamesForSmoke=["主页","历史","设置"];
    internal bool NavigateForSmoke(string name){ShowPage(name);return ValidatePageHost(name);}
    internal bool NavigateWithoutEventsForSmoke(string name){ShowPage(name);return ValidatePageHost(name);}
    internal int MainHostControlCountForSmoke=>_mainContentHost.Controls.Count;
    internal string CurrentMainPageForSmoke=>_currentMainPage;
    internal bool TrayRestoreCyclesForSmoke(int cycles)
    {
        ApplyHotKey(false);
        if (!_hotKeyRegistered) return false;
        var identities=_pages.ToDictionary(x=>x.Key,x=>System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(x.Value));
        for(var i=0;i<cycles;i++)
        {
            ShowPage(MainPageNamesForSmoke[i%MainPageNamesForSmoke.Length]);
            Hide();ShowInTaskbar=false;_trayIcon.Visible=true;Application.DoEvents();
            RestoreFromTray();Application.DoEvents();
            if(!_hotKeyRegistered||_mainContentHost.Controls.Count!=3||
               identities.Any(x=>!_pages.TryGetValue(x.Key,out var page)||page.IsDisposed||System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(page)!=x.Value)||
               !ValidatePageHost(_currentMainPage,"TrayCycleSmoke")) return false;
        }
        return true;
    }
    internal bool ThemePreservesBoundsForSmoke()
    {
        var controls=AllControls(this).ToArray();var before=controls.ToDictionary(x=>x,x=>x.Bounds);
        foreach(var mode in Enum.GetValues<ApplicationThemeMode>()){var settings=CurrentSettings;settings.ThemeMode=mode;UiTheme.Apply(this,settings);if(controls.Any(x=>x.Bounds!=before[x]))return false;}return true;
    }
    private static IEnumerable<Control> AllControls(Control root){yield return root;foreach(Control child in root.Controls)foreach(var nested in AllControls(child))yield return nested;}
    internal bool SettingsTabAliveForSmoke(int index){if(_settingsTabs is null||index<0||index>=_settingsTabs.TabCount)return false;_settingsTabs.SelectedIndex=index;var tab=_settingsTabs.TabPages[index];var ok=!tab.IsDisposed&&tab.Parent==_settingsTabs&&tab.Controls.Count>0;if(!ok)AppLog.Write("page-host",$"PageHostInvariantViolation settingsTab={tab.Text} controls={tab.Controls.Count} disposed={tab.IsDisposed}");return ok;}
    internal void SaveSettingsForSmoke()=>SaveSettings(true);
    internal void RefreshAppearanceForSmoke(){FontSettingsPolicy.ApplyUi(this,CurrentSettings);_lastThemeEvent=$"{DateTimeOffset.Now:O} SmokeAppearanceRefresh";PerformLayout();ValidateUiInvariants("AppearanceRefresh");}
    internal PreviewForm OpenPreviewForSmoke(Bitmap image)=>CreateAndShowPreview(new CaptureResult(image,PreviewMode.OcrOnly));
    internal PreviewForm OpenHistoryItemForSmoke(SessionHistoryItem item)=>OpenHistoryItem(item);
    internal PreviewForm OpenFirstHistorySelectionForSmoke()
    {
        NavigateForSmoke("历史");Application.DoEvents();
        if(_historyList.Count==0)throw new InvalidOperationException("History is empty.");
        _historyList.SelectedIndex=0;return OpenHistoryItem(_historyList.Records[0]);
    }
    internal bool RecoverDetachedMainPageForSmoke(){if(!_pages.TryGetValue(_currentMainPage,out var page))return false;_mainContentHost.Controls.Remove(page);ValidateUiInvariants("ForcedDetachedMainPageSmoke");return page.Parent==_mainContentHost&&page.Controls.Count>0;}
    internal bool RecoverDetachedSettingsRootForSmoke(int index){if(_settingsTabs is null||index<0||index>=_settingsTabs.TabCount)return false;ShowPage("设置");_settingsTabs.SelectedIndex=index;var tab=_settingsTabs.TabPages[index];if(tab.Tag is not Control root)return false;tab.Controls.Remove(root);ValidateUiInvariants("ForcedDetachedSettingsRootSmoke");return root.Parent==tab&&tab.Controls.Contains(root);}
    internal string CurrentPageDiagnosticForSmoke(){var page=_pages.GetValueOrDefault(_currentMainPage);return $"page={_currentMainPage};id={(page is null?0:System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(page))};parent={page?.Parent?.Name??"null"};disposed={page?.IsDisposed};hostCount={_mainContentHost.Controls.Count}";}
    internal IReadOnlyList<string> ThemeContrastIssuesForSmoke()=>AuditContrast(this);
    internal IReadOnlyList<string> ButtonClipIssuesForSmoke()=>AuditButtons(this);
    internal bool HomeHasOcrOnlyForSmoke(){var values=new List<string>();void Scan(Control c){if(!string.IsNullOrWhiteSpace(c.Text))values.Add(c.Text);foreach(Control child in c.Controls)Scan(child);}Scan(_pages["主页"]);return values.Any(x=>x.Contains("仅 OCR",StringComparison.Ordinal));}
    internal void SeedHistoryForSmoke(int count)
    {
        for(var i=0;i<count;i++){using var image=new Bitmap(320+i,180);using var g=Graphics.FromImage(image);g.Clear(Color.FromArgb((i*31)%255,(i*53)%255,(i*79)%255));g.DrawString($"history-{i}",SystemFonts.DefaultFont,Brushes.White,8,8);_session.AddHistory(image,OcrEngineKind.Rapid,$"source-{i}",$"translation-{i}","zh",new ApiSettings{HistoryLimit=Math.Max(20,count),HistoryCleanupByCount=true});}RefreshHistory();
    }
    internal (int Items,int Images,int Files) HistoryStateForSmoke()=> (_historyList.Count,_historyList.Count,Directory.Exists(Path.Combine(Path.GetDirectoryName(_settingsPath)!,"history"))?Directory.GetFiles(Path.Combine(Path.GetDirectoryName(_settingsPath)!,"history"),"*",SearchOption.AllDirectories).Length:0);
    internal int HistoryBlankClicksForSmoke(int count)
    {
        var random=new Random(501);var clicked=0;for(var attempt=0;attempt<count*200&&clicked<count;attempt++){var point=new Point(random.Next(Math.Max(1,_historyList.ClientSize.Width)),random.Next(Math.Max(1,_historyList.ClientSize.Height)));if(_historyList.HitTest(point)>=0)continue;HandleHistoryMouseDown(point);clicked++;}return clicked;
    }
    internal (int Columns,Size Tile,int ThumbnailSide) ResizeHistoryForSmoke(int windowWidth){if(_currentMainPage!="历史")ShowPage("历史");Width=windowWidth;PerformLayout();_historyList.PerformLayout();UpdateHistoryLayout();return(ResolveHistoryColumns(),_historyList.TileSize,_historyThumbnailSide);}
    internal int HistoryThumbnailDecodeCountForSmoke=>_historyThumbnailDecodeCount;
    internal bool HotKeyLifetimeValidForSmoke=>_hotKeyRegistered&&_hotKeyHost.Registered&&_hotKeyHost.RegistrationHandle!=IntPtr.Zero;
    internal bool TrayIconPresentForSmoke=>_trayIcon.Visible;
    internal int ActiveBindingCaptureCountForSmoke=>_activeBindingCapture is not null&&_bindingCaptureState!=BindingCaptureState.Idle?1:0;
    internal bool ActiveBindingOwnsWinFormsMouseCaptureForSmoke=>_activeBindingCapture?.Capture??false;
    internal string ActiveBindingActionForSmoke=>_activeBindingAction?.ToString()??"NONE";
    internal string BindingCaptureStateForSmoke=>_bindingCaptureState.ToString();
    internal string RuntimeBindingForSmoke(InputActionId action)=>RuntimeBindingFor(action)?.DisplayName??"NONE";
    internal void ApplyBindingForSmoke(InputActionId action,InputBinding binding){var editor=action switch{InputActionId.StartCapture=>_startBindingEditor,InputActionId.CancelCapture=>_cancelBindingEditor,InputActionId.ToggleResults=>_toggleBindingEditor,InputActionId.PreviewZoomIn=>_zoomInBindingEditor,InputActionId.PreviewZoomOut=>_zoomOutBindingEditor,_=>_resetFitBindingEditor};if(editor is not null)ApplyBinding(action,editor,binding,"SmokeRuntimeApply");}
    internal void BeginBindingCaptureForSmoke(InputActionId action){var editor=action switch{InputActionId.StartCapture=>_startBindingEditor,InputActionId.CancelCapture=>_cancelBindingEditor,InputActionId.ToggleResults=>_toggleBindingEditor,InputActionId.PreviewZoomIn=>_zoomInBindingEditor,InputActionId.PreviewZoomOut=>_zoomOutBindingEditor,_=>_resetFitBindingEditor};if(editor is not null)BeginBindingCapture(action,editor);}
    internal bool CaptureKeyboardBindingForSmoke(InputBinding binding)=>CaptureKeyboardFromHook(binding);
    internal string BindingDisplayForSmoke(InputActionId action)=>(action switch{InputActionId.StartCapture=>_startBindingEditor,InputActionId.CancelCapture=>_cancelBindingEditor,InputActionId.ToggleResults=>_toggleBindingEditor,InputActionId.PreviewZoomIn=>_zoomInBindingEditor,InputActionId.PreviewZoomOut=>_zoomOutBindingEditor,_=>_resetFitBindingEditor})?.Text??"";
    internal void CancelBindingCaptureForSmoke()=>CancelBindingCapture();
    internal void DispatchCancelBindingHotKeyForSmoke()=>DispatchCancelBindingHotKey();
    internal IReadOnlyList<string> BindingDiagnosticsForSmoke=>_bindingCaptureDiagnostics;
    internal bool ClickModifyForDiagnostic(InputActionId action){var button=AllControls(this).OfType<Button>().FirstOrDefault(x=>x.Tag is InputActionId id&&id==action&&x.Text=="修改");if(button is null)return false;button.PerformClick();return true;}
    internal IntPtr BindingMessageTargetForDiagnostic=>_settingsTabs?.Handle??Handle;
    internal void TrayHideForSmoke(){_hotKeyHost.Log("Tray Hide Smoke");Hide();ShowInTaskbar=false;_trayIcon.Visible=true;}
    internal void TrayRestoreForSmoke()=>RestoreFromTray();
    internal bool HistoryScrollThemeForSmoke(ApplicationThemeMode mode){var settings=CurrentSettings;settings.ThemeMode=mode;UiTheme.Apply(this,settings);ApplyHistoryScrollTheme();return _historyList.IsHandleCreated;}
    internal bool SettingsUsesThemedBorderForSmoke()=>_settingsTabs is SettingsPageHost&&UiTheme.Current.Border!=Color.White;
    internal Rectangle SettingsTabBoundsForSmoke(){ShowPage("设置");return _settingsTabs?.Bounds??Rectangle.Empty;}
    internal void SetThemeForSmoke(ApplicationThemeMode mode){var settings=CurrentSettings;settings.ThemeMode=mode;UiTheme.Apply(this,settings);_settingsTabs?.Invalidate();}
    internal bool PreviewTextPanelVisibleForSmoke=>_previewTextPanelVisibleBox.Checked;
    internal VisionRuntimeManager SharedVisionForSmoke=>_visionRuntimeManager;
    internal void SetPreviewTextPanelVisibleForSmoke(bool visible)=>_previewTextPanelVisibleBox.Checked=visible;

    internal Dictionary<string, object?> VerifyDataRootServicesForSmoke(bool readback)
    {
        if(!AppDataPaths.IsDataRootVerification || !AppDataPaths.DisableGlobalInput)
            throw new InvalidOperationException("Hidden data-root verification was not selected.");
        LoadSettings();
        if(!_settingsLoadSucceeded)throw new InvalidOperationException("The GUI did not load isolated settings.");
        ApplyHotKey(false);
        if(_hotKeyRegistered || _inputRuntime.KeyboardInstalled || _inputRuntime.MouseInstalled || _trayIcon.Visible)
            throw new InvalidOperationException("Hidden verification unexpectedly installed global input or a tray icon.");
        if(CurrentSettings.ApiKey.Length != 0)throw new InvalidOperationException("Verification refuses credentials.");
        if(!readback)
        {
            ConfigurationManager.Save(_settingsPath,CurrentSettings);
            using var original=new Bitmap(256,96);
            using(var g=Graphics.FromImage(original)){g.Clear(Color.FromArgb(37,51,71));g.DrawString("Data root verification",SystemFonts.DefaultFont,Brushes.White,12,30);}
            using var translated=new Bitmap(original);
            using(var g=Graphics.FromImage(translated)){g.Clear(Color.FromArgb(37,51,71));g.DrawString("独立数据目录验证",SystemFonts.DefaultFont,Brushes.White,12,30);}
            _session.AddHistory(original,OcrEngineKind.Rapid,"Data root verification","独立数据目录验证","zh-CN",CurrentSettings,translated);
            using var preview=new PreviewForm(original,PreviewMode.OcrOnly,CurrentSettings,
                _ocrService,_translationService,_ocrRuntimeManager,_visionRuntimeManager,_session);
            preview.SuppressAutoOcrForE2E();
            _=preview.Handle;
            preview.RestoreHistoryState("Data root verification","独立数据目录验证",translated,null);
            preview.SaveDisplayedImageForDataRootVerification(Path.Combine(AppDataPaths.OutputRoot,"data-root-preview.png"));
        }
        var history=_session.SnapshotHistory();
        if(history.Count!=1 || history[0].SourceText!="Data root verification" || history[0].TranslationText!="独立数据目录验证")
            throw new InvalidOperationException("Isolated History round-trip mismatch.");
        if(history.Any(x=>x.SourceImagePath is null || !x.SourceImagePath.StartsWith(AppDataPaths.HistoryRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("History escaped the isolated root.");
        _session.PutOcr("data-root-contract",new OcrEngineResult{EngineRequested=OcrEngineKind.Rapid,EngineActual=OcrEngineKind.Rapid,RawText="data-root",Blocks=[new OcrEngineBlock{Id="DATA-ROOT-OCR",RawText="data-root",BoundingBox=new RectangleF(0,0,40,14)}]});
        if(!_session.TryGetOcr("data-root-contract",out var ocr) || ocr.RawText!="data-root")
            throw new InvalidOperationException("Memory OCR cache round-trip failed.");
        _session.PutTranslation("data-root-contract",new TranslationBatchResult(new(){{"DATA-ROOT","独立目录"}},0,0,[],false));
        if(!_session.TryGetTranslation("data-root-contract",out var translation) || translation.Translations["DATA-ROOT"]!="独立目录")
            throw new InvalidOperationException("Memory translation cache round-trip failed.");
        var ocrCount=_session.OcrCount;var translationCount=_session.TranslationCount;
        _session.ClearCaches();
        if(_session.OcrCount!=0 || _session.TranslationCount!=0)throw new InvalidOperationException("Memory caches failed to clear.");
        AppLog.Write("data-root","MainForm settings, history, preview output and memory cache round-trip completed.");
        TranslationService.DiagnosticLog("Data-root verification uses the production safe diagnostic sink.");
        return new()
        {
            ["SettingsPath"]=_settingsPath,["HistoryRoot"]=AppDataPaths.HistoryRoot,["HistoryCount"]=history.Count,
            ["OutputPath"]=Path.Combine(AppDataPaths.OutputRoot,"data-root-preview.png"),["HandleCreated"]=IsHandleCreated,
            ["Visible"]=Visible,["GlobalKeyboardHook"]=_inputRuntime.KeyboardInstalled,
            ["GlobalMouseHook"]=_inputRuntime.MouseInstalled,["TrayVisible"]=_trayIcon.Visible,
            ["LegacyMigrationAllowed"]=_allowLegacyMigration,["ApiConfigured"]=false,
            ["OcrMemoryCacheHits"]=_session.OcrHits,["TranslationMemoryCacheHits"]=_session.TranslationHits,
            ["OcrMemoryCacheCountBeforeClear"]=ocrCount,["TranslationMemoryCacheCountBeforeClear"]=translationCount,
            ["MemoryCachesCleared"]=_session.OcrCount==0&&_session.TranslationCount==0,
            ["ClipboardTouched"]=false,["PhysicalUiAcceptance"]="MANUAL PENDING"
        };
    }

    internal void ExitForSmoke(){_trueExit=true;Close();}
    internal static IReadOnlyList<string> AuditContrast(Control root){var issues=new List<string>();void Scan(Control c){if(c.Visible&&c.Enabled&&c is TextBoxBase or ComboBox or NumericUpDown or Button or Label or CheckBox){var back=c.BackColor==Color.Transparent?c.Parent?.BackColor??UiDarkTheme.WindowBackground:c.BackColor;var delta=Math.Abs(Luma(back)-Luma(c.ForeColor));if(delta<70)issues.Add($"{c.GetType().Name}:{c.Text}:{back}:{c.ForeColor}");}foreach(Control child in c.Controls)Scan(child);}Scan(root);return issues;}
    internal static IReadOnlyList<string> AuditButtons(Control root){var issues=new List<string>();void Scan(Control c){if(c is Button b&&b.Visible&&b.ClientSize.Width>0&&!string.IsNullOrWhiteSpace(b.Text)){var measured=TextRenderer.MeasureText(b.Text,b.Font,Size.Empty,TextFormatFlags.SingleLine);var available=Math.Max(0,b.ClientSize.Width-b.Padding.Horizontal-6);if(measured.Width>available)issues.Add($"{b.Text}:{measured.Width}>{available}");}foreach(Control child in c.Controls)Scan(child);}Scan(root);return issues;}
    private static double Luma(Color c)=>.2126*c.R+.7152*c.G+.0722*c.B;

    private OcrComparisonForm OpenOcrComparison(Bitmap image, bool autoRunPrimary)
    {
        var compare = new OcrComparisonForm(_ocrRuntimeManager, image,
            OcrComparisonMode.Standard, autoRunPrimary);
        compare.Show(this);
        compare.BringToFront();
        return compare;
    }

    private void Preview_InputHandleCreated(object? sender,EventArgs e)=>PublishPreviewInputProtection();
    private void PublishPreviewInputProtection()=>_inputRuntime.ProtectPreviewMouse(
        _previews.Where(x=>!x.IsDisposed&&x.IsHandleCreated).Select(x=>x.Handle).ToArray());
    private void Preview_FormClosed(object? sender, FormClosedEventArgs e)
    {
        if (sender is not PreviewForm preview) return;
        preview.HandleCreated -= Preview_InputHandleCreated;
        preview.FormClosed -= Preview_FormClosed;
        preview.ReSelectRequested -= Preview_ReSelectRequested;
        preview.SelfCaptureRequested -= Preview_SelfCaptureRequested;
        preview.AppearanceSettingsChanged -= Preview_AppearanceSettingsChanged;
        _previews.Remove(preview);
        PublishPreviewInputProtection();
        ValidateUiInvariants("PreviewClose");
    }

    private void Preview_ReSelectRequested(object? sender, EventArgs e)
    {
        if (IsDisposed || Disposing) return;
        BeginInvoke(BeginCapture);
    }

    private void Preview_SelfCaptureRequested(PreviewForm sender, Bitmap image)
    {
        try
        {
            if (image.Width <= 0 || image.Height <= 0)
                throw new InvalidDataException("Self Preview bitmap is empty.");
            var preview = CreateAndShowPreview(new CaptureResult(image, PreviewMode.OcrAndTranslate));
            preview.BringToFront();
        }
        finally { image.Dispose(); }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        CancelBindingCapture();
        if(!_trueExit&&e.CloseReason==CloseReason.UserClosing&&CurrentSettings.CloseMainWindowBehavior==CloseMainWindowBehavior.MinimizeToTray)
        {
            e.Cancel=true;_hotKeyHost.Log("Tray Hide");Hide();ShowInTaskbar=false;_trayIcon.Visible=true;
            _trayIcon.ShowBalloonTip(1500,"截图翻译器","程序仍在后台运行，截图快捷键继续可用。",ToolTipIcon.Info);
            return;
        }
        if(!e.Cancel&&!ConfirmProfileLeave()){e.Cancel=true;_trueExit=false;_trayIcon.Visible=true;return;}
        _profileTestCancellation?.Cancel();
        if(!e.Cancel)
        {
            _trueExit=true;_trayIcon.Visible=false;RemoveHotKey();_inputRuntime.Disable();
            foreach(var open in Application.OpenForms.Cast<Form>().Where(x=>x!=this&&!x.IsDisposed).ToArray())
                try{open.Close();}catch(Exception ex){AppLog.Write("shutdown","Unable to close child window",ex);}
        }
        base.OnFormClosing(e);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Application.RemoveMessageFilter(this);
        if (_settingsLoadCompleted && _settingsLoadSucceeded && _settingsDirty)
            SaveSettings(false);
        else
            CaptureFlashDiagnosticLog.Write(
                $"OnFormClosed settings save skipped loaded={_settingsLoadCompleted} succeeded={_settingsLoadSucceeded} dirty={_settingsDirty}");
        RemoveHotKey();
        _hotKeyHost.Dispose();_cancelHotKeyHost.Dispose();_toggleHotKeyHost.Dispose();
        _inputRuntime.Dispose();

        foreach (var preview in _previews.ToArray()) if(!preview.IsDisposed)preview.Close();
        _captureOverlay?.Dispose();
        _screenCaptureCoordinator.Dispose();
        _prewarmedPreview?.Dispose();
        _captureOverlay = null;
        var workerPid=_ocrRuntimeManager.ActiveWorkerPid;
        var visionWorkerPid=_visionRuntimeManager.ActiveWorkerPid;
        var visionCleanup=Task.Run(async()=>await _visionRuntimeManager.DisposeAsync());
        if(!visionCleanup.Wait(TimeSpan.FromSeconds(5))&&visionWorkerPid is int visionPid)
        {
            try{using var process=System.Diagnostics.Process.GetProcessById(visionPid);process.Kill(true);process.WaitForExit(1500);}
            catch(Exception ex){AppLog.Write("shutdown",$"Vision cleanup fallback pid={visionPid}",ex);}
        }
        var cleanup=Task.Run(async()=>await _ocrRuntimeManager.DisposeAsync());
        if(!cleanup.Wait(TimeSpan.FromSeconds(5))&&workerPid is int pid)
        {
            try{using var process=System.Diagnostics.Process.GetProcessById(pid);process.Kill(true);process.WaitForExit(1500);}
            catch(Exception ex){AppLog.Write("shutdown",$"OCR cleanup fallback pid={pid}",ex);}
        }
        _session.Dispose();
        _trayIcon.Dispose();
        base.OnFormClosed(e);
    }
}


