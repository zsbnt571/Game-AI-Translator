namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private readonly DesktopSettingsDraft _desktopDraft = new();
    internal DesktopSettingsDraft DesktopDraft => _desktopDraft;
    internal ApiSettings NativeDraftSnapshot() => ReadNativeUiSettings();
    internal static string NativeSettingsDescription(int page) => page == 10 ? AiUiText.Text("SettingsDescription") : SettingsDescriptions[page];
    internal LibraryPreferences NativeLibraryPreferences => _libraryPreferences;
    internal void NativeSaveLibraryPreferences(LibraryPreferences preferences) => SaveLibraryPreferences(preferences);
    internal void NativeSaveDesktopSettings() => SaveSettings(true);
    private string _nativeSettingsStatus = "设置已保存";
    private string _nativeOcrInfo = "正在读取已安装语言…";
    private string _nativeCredentialStatus = "";
    internal event Action? NativeSettingsStatusChanged;
    internal string NativeSettingsStatus => _nativeSettingsStatus;
    internal bool NativeSettingsReady => _settingsLoadSucceeded;
    internal string NativeOcrLanguages => _nativeOcrInfo;
    internal string NativeSettingsPath => _settingsPath;
    internal string NativeCredentialStatus => _nativeCredentialStatus;
    internal void NativeSelectSettings(int index)
    {
        if (index < 0 || index >= NativeSettingsNames.Length || _loadingFusion) return;
        if (_lastSettingsCategory == 3 && index != 3 && !ConfirmSettingsLeave()) { _wpfWindow?.RefreshWorkspace(); return; }
        EnsureSettingsCategory(index); CancelBindingCapture(); _lastSettingsCategory = index;
        UpdateSettingsSaveState(); RecordPageLocation(); _wpfWindow?.RefreshWorkspace();
    }
    internal static readonly string[] NativeSettingsNames = ["外观", "软件通用", "按键", "翻译方案", "截图处理", "预览窗口", "历史与缓存", "游戏库与图片", "存储位置", "帮助与诊断", "AI 工具"];
    internal bool NativeDefaultLaunch
    {
        get => _fusion?.State.DefaultGameLaunchWithTranslation == true;
        set { if (_fusion is null) return; _fusion.Change(s => s.DefaultGameLaunchWithTranslation = value); RenderGameState(); UpdateSettingsSaveState("启动方式已保存"); }
    }
    private ApiSettings ReadNativeUiSettings()
    {
        var settings = ApiSettingsSnapshot.Copy(_translationOptions);
        _desktopDraft.ApplyTo(settings); FusionConfiguration.ApplyConnection(settings, _profileDraft.Snapshot());
        _contentDraft.Snapshot().ApplyTo(settings);
        settings.TypographyConfigVersion = FontManager.CurrentConfigVersion;
        settings.TranslationPromptVersion = TranslationPromptBuilder.Version;
        settings.OcrEngine = OcrEngineKind.Rapid; settings.VisualModel = VisualModelKind.Off;
        return settings;
    }
    private void ApplyNativeAppearance(ApiSettings settings)
    {
        string[] names = [nameof(ApiSettings.PreviewWindowSizingMode), nameof(ApiSettings.FixedPreviewWidth), nameof(ApiSettings.FixedPreviewHeight),
            nameof(ApiSettings.UiFontMode), nameof(ApiSettings.UiFontFamily), nameof(ApiSettings.UiFontSize),
            nameof(ApiSettings.PreviewTextFontMode), nameof(ApiSettings.PreviewTextFontFamily), nameof(ApiSettings.PreviewTextFontSize),
            nameof(ApiSettings.OverlayFontMode), nameof(ApiSettings.OverlayFontFamily), nameof(ApiSettings.OverlayFontSize),
            nameof(ApiSettings.OverlayBackgroundStyle), nameof(ApiSettings.OverlayBackgroundOpacity)];
        foreach (var name in names) _desktopDraft.Set(name, _desktopDraft.Field(name).Property.GetValue(settings));
        UpdateTypographyStatus(settings);
    }
    private void DesktopDraftEdited(string property)
    {
        if (_applyingSettings || _loadingFusion || !_settingsLoadSucceeded) return;
        _settingsDirty = true; UpdateSettingsSaveState();
        if (property == nameof(ApiSettings.PreviewTextPanelVisible))
        {
            bool visible = _desktopDraft.Get<bool>(property); _translationOptions.PreviewTextPanelVisible = visible;
            foreach (var preview in _previews.Where(x => !x.IsDisposed)) preview.ApplyTextPanelVisibility(visible);
            try { ConfigurationManager.Save(_settingsPath, CurrentSettings); }
            catch (Exception ex) { AppLog.Write("preview-text", "Unable to persist visibility", ex); }
        }
    }
    internal void NativeSetTheme(ApplicationThemeMode theme)
    { if (_desktopDraft.Get<ApplicationThemeMode>(nameof(ApiSettings.ThemeMode)) == theme) return; _desktopDraft.Set(nameof(ApiSettings.ThemeMode), theme); SaveSettings(false); }
    internal void NativeSetIconColorMode(UiIconColorMode mode)
        =>NativeSetIconAppearance(mode,_desktopDraft.Get<int>(nameof(ApiSettings.IconSize)));
    internal void NativeSetIconAppearance(UiIconColorMode mode,int size)
    {
        if(!Enum.IsDefined(mode))return;size=Math.Clamp(size,16,28);
        if(_desktopDraft.Get<UiIconColorMode>(nameof(ApiSettings.IconColorMode))==mode&&_desktopDraft.Get<int>(nameof(ApiSettings.IconSize))==size)return;
        _desktopDraft.Set(nameof(ApiSettings.IconColorMode),mode);_desktopDraft.Set(nameof(ApiSettings.IconSize),size);SaveSettings(false);
    }
    internal void NativePreviewIcons()
    {
        using var dialog=new NativeIconAppearanceDialog(_desktopDraft.Get<UiIconColorMode>(nameof(ApiSettings.IconColorMode)),
            _desktopDraft.Get<int>(nameof(ApiSettings.IconSize)),NativeSetIconAppearance);
        dialog.ShowDialog(this);
    }
    internal void NativeResetLibraryAppearance()
    {
        _desktopDraft.Set(nameof(ApiSettings.ThemeMode), ApplicationThemeMode.Day);
        _desktopDraft.Set(nameof(ApiSettings.IconColorMode), UiIconColorMode.Monochrome);
        _desktopDraft.Set(nameof(ApiSettings.IconSize),17);
        _desktopDraft.Set(nameof(ApiSettings.UiFontMode), UiFontMode.CurrentDefault);
        _desktopDraft.Set(nameof(ApiSettings.UiFontSize), 10f);
        _settingsDirty = true; SaveSettings(false); SaveLibraryPreferences(_libraryPreferences with { Portrait = true, CoverWidth = 158 });
    }
    internal void NativeChooseThemeColor(string property)
    {
        var current = _desktopDraft.Get<string>(property);
        using var picker = new ColorDialog { FullOpen = true, Color = UiTheme.Parse(current, Color.Black) };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        _desktopDraft.Set(property, ColorTranslator.ToHtml(picker.Color));
        _desktopDraft.Set(nameof(ApiSettings.ThemeMode), ApplicationThemeMode.Custom);
        ApplyDesktopAppearance(CurrentSettings);
    }
    internal void NativeOpenFolder(string path) => OpenFolder(path);
    internal void NativeOpenGameDiagnostics() => OpenGameDiagnosticsFolder(true);
    internal async Task NativeFillLibraryArtwork()
    { _artworkAttempted.Clear(); var store = await RecentStore(); await FillDefaultArtworkAsync(store.SnapshotAll().Select(x => x.ExePath)); if (!IsDisposed) ScheduleLibraryRefresh(); }
    internal async Task NativeRetryLibraryNames()
    { _libraryNameAttempts.Clear(); var store = await RecentStore(); await FillLibraryChineseNamesAsync(store.SnapshotAll(), true); }
    internal Task NativeEditLibraryRetention() => EditLibraryRetentionAsync();
    internal string? NativeChooseMigrationSource()
    {
        using var picker = new FolderBrowserDialog
        {
            Description = UiLanguage.Text("选择旧版软件文件夹或其中的 data 文件夹"),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        return picker.ShowDialog(this) == DialogResult.OK ? picker.SelectedPath : null;
    }

    internal string? NativeChooseStorageLocation()
    {
        using var picker = new FolderBrowserDialog { Description = UiLanguage.Text("选择独立的空文件夹。下次启动复制数据，原目录保留。") };
        if (picker.ShowDialog(this) != DialogResult.OK) return null;
        PortableDataStorage.RequestLocation(picker.SelectedPath);
        return "已设置：" + picker.SelectedPath + "\n下次启动时复制并校验数据；当前仍使用原目录，原文件不会删除。";
    }
    internal string? NativeChooseAiStorageLocation147()
    {
        using var picker = new FolderBrowserDialog { Description = AiStorageText147.Text(AiStoragePaths147.GetCurrent().ErrorCode=="ai_storage_unavailable"?"LocateHelp":"ChooseRoot"), UseDescriptionForTitle = true };
        return picker.ShowDialog(this) == DialogResult.OK ? picker.SelectedPath : null;
    }
    internal void NativeSettingsError(Exception ex) => ShowFusionError(ex);
}
