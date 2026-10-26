namespace ScreenshotTranslationUiTester;

// Each view receives only its own feature's state and commands. No view can
// reach the application's legacy controls or another feature through this API.
internal interface ILibraryWorkspaceHost
{
    System.Int32 DeviceDpi { get; }
    System.Drawing.Font Font { get; }
    void NativeLibraryPresentation();
    void WpfEditRetention();
    System.Int32 WpfFilterIndex { get; set; }
    System.String[] WpfFilterItems { get; }
    void WpfImport();
    void WpfImportMenu();
    System.Threading.Tasks.Task WpfImportPaths(System.String[] files);
    System.String WpfLibraryCount { get; }
    System.String WpfLibraryHeading { get; }
    System.Boolean WpfLibraryHome { get; }
    System.Windows.FrameworkElement WpfLibraryTree { get; }
    System.Boolean WpfListMode { get; }
    System.Windows.FrameworkElement? WpfLowerHeader { get; set; }
    System.String WpfLowerSection { get; }
    System.Windows.FrameworkElement WpfMainGrid { get; }
    System.Windows.FrameworkElement WpfRecentGrid { get; }
    System.String WpfRetentionText { get; }
    void WpfSaveSidebar(System.Int32 width);
    System.String WpfSearchText { get; set; }
    void WpfSectionMenu(System.Boolean upper);
    void WpfSetLibraryList(System.Boolean list);
    System.Boolean WpfShowShelf { get; }
    System.Int32 WpfSidebarWidth { get; }
    event System.Action? WpfStatusChanged;
    System.String WpfStatusSource { get; }
    System.String WpfStatusText { get; }
    System.Windows.FrameworkElement? WpfUpperHeader { get; set; }
    System.String WpfUpperSection { get; }
    IGameDetailsHost GameDetails { get; }
}

internal interface IGameDetailsHost
{
    ModificationSpeedState NativeGameSpeedState => ModificationSpeedState.Unavailable;
    Task NativeSetGameSpeed(double rate) => Task.CompletedTask;
    bool NativeStorySkipVisible => false;
    bool NativeStorySkipEnabled => false;
    Task NativeStorySkip(bool stop) => Task.CompletedTask;
    DesktopActionState NativeCommonModificationAction => new("开启修改"){Text="开启修改",Enabled=false};
    string NativeCommonModificationStatus => "修改未开启。开启后才能调整游戏速度。";
    Task NativeToggleCommonModification() => Task.CompletedTask;
    ScreenshotTranslationUiTester.GameDetailPresentation GameDetail { get; }
    System.String GamePathText { get; }
    System.Boolean IsDisposed { get; }
    void NativeGameAction(ScreenshotTranslationUiTester.GameDetailAction action);
    void NativeGameMenu(System.String menu, System.Windows.FrameworkElement anchor);
    System.String NativeGameProfileSummary { get; }
    System.Windows.FrameworkElement NativeRpgView { get; }
    System.Windows.Controls.ContentControl? NativeRpgCommonSpeedSlot => null;
    void NativeSelectWorkspaceProfile(System.Boolean embedded, System.String id);
    System.String NativeWorkspaceProfileId(System.Boolean embedded);
    ScreenshotTranslationUiTester.WorkspaceProfileChoice[] NativeWorkspaceProfiles(System.Boolean embedded);
    event System.Action? GameDetailChanged;
    System.Drawing.Image? WpfSessionArtwork { get; }
    System.Windows.Media.Imaging.BitmapSource? WpfSessionArtworkSource => WpfSessionArtwork is Bitmap bitmap?WpfDrawing.Image(bitmap):null;
    CoverFraming WpfSessionCoverFrame => CoverFraming.Centered;
    Task<bool> NativeSaveCoverFrame(CoverFraming frame) => Task.FromResult(false);
    System.String WpfSessionSubtitle { get; }
    System.String WpfSessionTitle { get; }
    INovelWorkspaceHost Novel { get; }
    IRenpyWorkspaceHost Renpy { get; }
}

internal interface INovelWorkspaceHost
{
    ScreenshotTranslationUiTester.NativeNovelWorkspaceState NativeNovel { get; }
    event System.Action? NativeNovelChanged;
    System.Threading.Tasks.Task NativeNovelCommand(System.String command);
    System.Threading.Tasks.Task NativeNovelBatch(System.String operation,System.Collections.Generic.IReadOnlyList<NovelVariableRow> selected,System.String value);
    System.Threading.Tasks.Task NativeNovelMapVariables(int? map,int eventId,string[] ids);
    void NativeNovelSearch(System.String text);
    void NativeNovelSelect(ScreenshotTranslationUiTester.NovelVariableRow? row);
    void NativeNovelSnapshot(ScreenshotTranslationUiTester.NovelStateRecord? snapshot);
    System.Threading.Tasks.Task NativeNovelSnapshotCommand(System.String command);
    void NativeNovelValue(System.String text);
    void NativeNovelRequestNames(System.Collections.Generic.IEnumerable<NovelVariableRow> rows) { }
}

internal interface IRenpyWorkspaceHost
{
    ScreenshotTranslationUiTester.NativeRenpyWorkspaceState NativeRenpy { get; }
    System.Threading.Tasks.Task NativeRenpyAction(System.String action);
    System.Threading.Tasks.Task NativeRenpyBoolean(System.Boolean value);
    ScreenshotTranslationUiTester.RenpyEditSession NativeRenpyCaptureEdit();
    event System.Action? NativeRenpyChanged;
    void NativeRenpyChangedOnly(System.Boolean changed);
    System.Threading.Tasks.Task NativeRenpyCommit(ScreenshotTranslationUiTester.RenpyEditSession edit, System.String input);
    void NativeRenpyScope(System.Int32 scope);
    void NativeRenpySearch(System.String query);
    void NativeRenpySelect(System.Collections.Generic.IEnumerable<ScreenshotTranslationUiTester.RenpyVariableRow> rows);
    void NativeRenpySort(System.Int32 column);
}

internal interface ISettingsWorkspaceHost
{
    void NativePrepareProfilePage();
    void NativeSaveDesktopSettings();
    System.String NativeSettingsDescription(System.Int32 page);
    System.Boolean NativeSettingsReady { get; }
    System.String NativeSettingsStatus { get; }
    event System.Action? NativeSettingsStatusChanged;
    void WpfSelectSettings(System.Int32 index);
    System.Int32 WpfSettingsCategory { get; }
    System.String[] WpfSettingsNames { get; }
    System.Boolean WpfSettingsPreparing { get; }
    IDesktopSettingsHost DesktopSettings { get; }
    IProfileEditorHost ProfileEditor { get; }
    ITranslationContentHost TranslationContent { get; }
}

internal interface IDesktopSettingsHost
{
    ScreenshotTranslationUiTester.DesktopSettingsDraft DesktopDraft { get; }
    void NativeBeginBinding(ScreenshotTranslationUiTester.InputActionId action, System.Func<System.Drawing.Rectangle> bounds);
    void NativeBeginGameShortcut();
    System.String NativeBindingText(ScreenshotTranslationUiTester.InputActionId action);
    System.String? NativeChooseStorageLocation();
    System.String? NativeChooseMigrationSource();
    void NativeChooseThemeColor(System.String property);
    System.String NativeCredentialStatus { get; }
    ScreenshotTranslationUiTester.InputBinding NativeDefaultBinding(ScreenshotTranslationUiTester.InputActionId action);
    System.Boolean NativeDefaultLaunch { get; set; }
    ScreenshotTranslationUiTester.ApiSettings NativeDraftSnapshot();
    System.Threading.Tasks.Task NativeEditLibraryRetention();
    System.Threading.Tasks.Task NativeFillLibraryArtwork();
    System.String NativeGameShortcutText { get; }
    ScreenshotTranslationUiTester.LibraryPreferences NativeLibraryPreferences { get; }
    System.String NativeOcrLanguages { get; }
    void NativeOpenFolder(System.String path);
    void NativeOpenGameDiagnostics();
    void NativeOpenModelManager();
    void NativeOpenOcrComparison(System.Boolean experimental);
    void NativeResetLibraryAppearance();
    System.Threading.Tasks.Task NativeRetryLibraryNames();
    void NativeSaveLibraryPreferences(ScreenshotTranslationUiTester.LibraryPreferences preferences);
    void NativeSetBinding(ScreenshotTranslationUiTester.InputActionId action, ScreenshotTranslationUiTester.InputBinding binding);
    void NativeSetGameShortcut(System.String value);
    void NativeSetTheme(ScreenshotTranslationUiTester.ApplicationThemeMode theme);
    System.String NativeSettingsDescription(System.Int32 page);
    void NativeSettingsError(System.Exception ex);
    System.String NativeSettingsPath { get; }
    event System.Action? NativeShortcutsChanged;
    System.String[] WpfSettingsNames { get; }
}

internal interface IProfileEditorHost
{
    System.Drawing.Font Font { get; }
    void NativeApplyParameterExample(System.String json);
    void NativeCopyProfile();
    void NativeDeleteProfile();
    void NativeNewProfile();
    event System.Action? ProfileNameFocusRequested;
    void NativeRenameProfile();
    void NativeReturnFromProfile(System.Boolean save);
    System.Boolean NativeSaveProfile();
    void NativeSelectCommonProfile(System.String id);
    void NativeSelectProfile(System.String id);
    System.Threading.Tasks.Task NativeTestProfileAsync();
    ScreenshotTranslationUiTester.ApiProfileEditorState ProfileDraft { get; }
}

internal interface ITranslationContentHost
{
    ScreenshotTranslationUiTester.TranslationContentEditorState ContentDraft { get; }
    System.Drawing.Font Font { get; }
    System.Boolean NativeSaveTranslationContent();
}

