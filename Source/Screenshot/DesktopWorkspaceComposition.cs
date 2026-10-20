namespace ScreenshotTranslationUiTester;

// The application composes existing business services behind feature contracts.
// This boundary contains no copied state and no alternate UI implementation.
public sealed partial class MainForm : ILibraryWorkspaceHost, IGameDetailsHost, INovelWorkspaceHost, IRenpyWorkspaceHost, ISettingsWorkspaceHost, IDesktopSettingsHost, IProfileEditorHost, ITranslationContentHost
{
    System.Int32 ILibraryWorkspaceHost.DeviceDpi { get => DeviceDpi; }
    System.Drawing.Font ILibraryWorkspaceHost.Font { get => Font; }
    void ILibraryWorkspaceHost.NativeLibraryPresentation() => NativeLibraryPresentation();
    void ILibraryWorkspaceHost.WpfEditRetention() => WpfEditRetention();
    System.Int32 ILibraryWorkspaceHost.WpfFilterIndex { get => WpfFilterIndex; set => WpfFilterIndex = value; }
    System.String[] ILibraryWorkspaceHost.WpfFilterItems { get => WpfFilterItems; }
    void ILibraryWorkspaceHost.WpfImport() => WpfImport();
    void ILibraryWorkspaceHost.WpfImportMenu() => WpfImportMenu();
    System.Threading.Tasks.Task ILibraryWorkspaceHost.WpfImportPaths(System.String[] files) => WpfImportPaths(files);
    System.String ILibraryWorkspaceHost.WpfLibraryCount { get => WpfLibraryCount; }
    System.String ILibraryWorkspaceHost.WpfLibraryHeading { get => WpfLibraryHeading; }
    System.Boolean ILibraryWorkspaceHost.WpfLibraryHome { get => WpfLibraryHome; }
    System.Windows.FrameworkElement ILibraryWorkspaceHost.WpfLibraryTree { get => WpfLibraryTree; }
    System.Boolean ILibraryWorkspaceHost.WpfListMode { get => WpfListMode; }
    System.Windows.FrameworkElement? ILibraryWorkspaceHost.WpfLowerHeader { get => WpfLowerHeader; set => WpfLowerHeader = value; }
    System.String ILibraryWorkspaceHost.WpfLowerSection { get => WpfLowerSection; }
    System.Windows.FrameworkElement ILibraryWorkspaceHost.WpfMainGrid { get => WpfMainGrid; }
    System.Windows.FrameworkElement ILibraryWorkspaceHost.WpfRecentGrid { get => WpfRecentGrid; }
    System.String ILibraryWorkspaceHost.WpfRetentionText { get => WpfRetentionText; }
    void ILibraryWorkspaceHost.WpfSaveSidebar(System.Int32 width) => WpfSaveSidebar(width);
    System.String ILibraryWorkspaceHost.WpfSearchText { get => WpfSearchText; set => WpfSearchText = value; }
    void ILibraryWorkspaceHost.WpfSectionMenu(System.Boolean upper) => WpfSectionMenu(upper);
    void ILibraryWorkspaceHost.WpfSetLibraryList(System.Boolean list) => WpfSetLibraryList(list);
    System.Boolean ILibraryWorkspaceHost.WpfShowShelf { get => WpfShowShelf; }
    System.Int32 ILibraryWorkspaceHost.WpfSidebarWidth { get => WpfSidebarWidth; }
    event System.Action? ILibraryWorkspaceHost.WpfStatusChanged { add => WpfStatusChanged += value; remove => WpfStatusChanged -= value; }
    System.String ILibraryWorkspaceHost.WpfStatusSource { get => WpfStatusSource; }
    System.String ILibraryWorkspaceHost.WpfStatusText { get => WpfStatusText; }
    System.Windows.FrameworkElement? ILibraryWorkspaceHost.WpfUpperHeader { get => WpfUpperHeader; set => WpfUpperHeader = value; }
    System.String ILibraryWorkspaceHost.WpfUpperSection { get => WpfUpperSection; }
    IGameDetailsHost ILibraryWorkspaceHost.GameDetails => this;
    ScreenshotTranslationUiTester.GameDetailPresentation IGameDetailsHost.GameDetail { get => GameDetail; }
    System.String IGameDetailsHost.GamePathText { get => GamePathText; }
    System.Boolean IGameDetailsHost.IsDisposed { get => IsDisposed; }
    void IGameDetailsHost.NativeGameAction(ScreenshotTranslationUiTester.GameDetailAction action) => NativeGameAction(action);
    void IGameDetailsHost.NativeGameMenu(System.String menu, System.Windows.FrameworkElement anchor) => NativeGameMenu(menu, anchor);
    System.String IGameDetailsHost.NativeGameProfileSummary { get => NativeGameProfileSummary; }
    System.Windows.FrameworkElement IGameDetailsHost.NativeRpgView { get => NativeRpgView; }
    void IGameDetailsHost.NativeSelectWorkspaceProfile(System.Boolean embedded, System.String id) => NativeSelectWorkspaceProfile(embedded, id);
    System.String IGameDetailsHost.NativeWorkspaceProfileId(System.Boolean embedded) => NativeWorkspaceProfileId(embedded);
    ScreenshotTranslationUiTester.WorkspaceProfileChoice[] IGameDetailsHost.NativeWorkspaceProfiles(System.Boolean embedded) => NativeWorkspaceProfiles(embedded);
    event System.Action? IGameDetailsHost.GameDetailChanged { add => GameDetailChanged += value; remove => GameDetailChanged -= value; }
    System.Drawing.Image? IGameDetailsHost.WpfSessionArtwork { get => WpfSessionArtwork; }
    System.Windows.Media.Imaging.BitmapSource? IGameDetailsHost.WpfSessionArtworkSource => WpfSessionArtworkSource;
    CoverFraming IGameDetailsHost.WpfSessionCoverFrame => WpfSessionCoverFrame;
    Task<bool> IGameDetailsHost.NativeSaveCoverFrame(CoverFraming frame) => NativeSaveCoverFrame(frame);
    System.String IGameDetailsHost.WpfSessionSubtitle { get => WpfSessionSubtitle; }
    System.String IGameDetailsHost.WpfSessionTitle { get => WpfSessionTitle; }
    INovelWorkspaceHost IGameDetailsHost.Novel => this;
    IRenpyWorkspaceHost IGameDetailsHost.Renpy => this;
    ScreenshotTranslationUiTester.NativeNovelWorkspaceState INovelWorkspaceHost.NativeNovel { get => NativeNovel; }
    event System.Action? INovelWorkspaceHost.NativeNovelChanged { add => NativeNovelChanged += value; remove => NativeNovelChanged -= value; }
    System.Threading.Tasks.Task INovelWorkspaceHost.NativeNovelCommand(System.String command) => NativeNovelCommand(command);
    System.Threading.Tasks.Task INovelWorkspaceHost.NativeNovelMapVariables(int? map,int eventId,string[] ids) => SelectNativeWolfMapVariables(map,eventId,ids);
    void INovelWorkspaceHost.NativeNovelSearch(System.String text) => NativeNovelSearch(text);
    void INovelWorkspaceHost.NativeNovelSelect(ScreenshotTranslationUiTester.NovelVariableRow? row) => NativeNovelSelect(row);
    void INovelWorkspaceHost.NativeNovelRequestNames(System.Collections.Generic.IEnumerable<NovelVariableRow> rows) => NativeNovelRequestNames(rows);
    void INovelWorkspaceHost.NativeNovelSnapshot(ScreenshotTranslationUiTester.NovelStateRecord? snapshot) => NativeNovelSnapshot(snapshot);
    System.Threading.Tasks.Task INovelWorkspaceHost.NativeNovelSnapshotCommand(System.String command) => NativeNovelSnapshotCommand(command);
    void INovelWorkspaceHost.NativeNovelValue(System.String text) => NativeNovelValue(text);
    ScreenshotTranslationUiTester.NativeRenpyWorkspaceState IRenpyWorkspaceHost.NativeRenpy { get => NativeRenpy; }
    System.Threading.Tasks.Task IRenpyWorkspaceHost.NativeRenpyAction(System.String action) => NativeRenpyAction(action);
    System.Threading.Tasks.Task IRenpyWorkspaceHost.NativeRenpyBoolean(System.Boolean value) => NativeRenpyBoolean(value);
    ScreenshotTranslationUiTester.RenpyEditSession IRenpyWorkspaceHost.NativeRenpyCaptureEdit() => NativeRenpyCaptureEdit();
    event System.Action? IRenpyWorkspaceHost.NativeRenpyChanged { add => NativeRenpyChanged += value; remove => NativeRenpyChanged -= value; }
    void IRenpyWorkspaceHost.NativeRenpyChangedOnly(System.Boolean changed) => NativeRenpyChangedOnly(changed);
    System.Threading.Tasks.Task IRenpyWorkspaceHost.NativeRenpyCommit(ScreenshotTranslationUiTester.RenpyEditSession edit, System.String input) => NativeRenpyCommit(edit, input);
    void IRenpyWorkspaceHost.NativeRenpyScope(System.Int32 scope) => NativeRenpyScope(scope);
    void IRenpyWorkspaceHost.NativeRenpySearch(System.String query) => NativeRenpySearch(query);
    void IRenpyWorkspaceHost.NativeRenpySelect(System.Collections.Generic.IEnumerable<ScreenshotTranslationUiTester.RenpyVariableRow> rows) => NativeRenpySelect(rows);
    void IRenpyWorkspaceHost.NativeRenpySort(System.Int32 column) => NativeRenpySort(column);
    void ISettingsWorkspaceHost.NativePrepareProfilePage() => NativePrepareProfilePage();
    void ISettingsWorkspaceHost.NativeSaveDesktopSettings() => NativeSaveDesktopSettings();
    System.String ISettingsWorkspaceHost.NativeSettingsDescription(System.Int32 page) => NativeSettingsDescription(page);
    System.Boolean ISettingsWorkspaceHost.NativeSettingsReady { get => NativeSettingsReady; }
    System.String ISettingsWorkspaceHost.NativeSettingsStatus { get => NativeSettingsStatus; }
    event System.Action? ISettingsWorkspaceHost.NativeSettingsStatusChanged { add => NativeSettingsStatusChanged += value; remove => NativeSettingsStatusChanged -= value; }
    void ISettingsWorkspaceHost.WpfSelectSettings(System.Int32 index) => WpfSelectSettings(index);
    System.Int32 ISettingsWorkspaceHost.WpfSettingsCategory { get => WpfSettingsCategory; }
    System.String[] ISettingsWorkspaceHost.WpfSettingsNames { get => WpfSettingsNames; }
    System.Boolean ISettingsWorkspaceHost.WpfSettingsPreparing { get => WpfSettingsPreparing; }
    IDesktopSettingsHost ISettingsWorkspaceHost.DesktopSettings => this;
    IProfileEditorHost ISettingsWorkspaceHost.ProfileEditor => this;
    ITranslationContentHost ISettingsWorkspaceHost.TranslationContent => this;
    ScreenshotTranslationUiTester.DesktopSettingsDraft IDesktopSettingsHost.DesktopDraft { get => DesktopDraft; }
    void IDesktopSettingsHost.NativeBeginBinding(ScreenshotTranslationUiTester.InputActionId action, System.Func<System.Drawing.Rectangle> bounds) => NativeBeginBinding(action, bounds);
    void IDesktopSettingsHost.NativeBeginGameShortcut() => NativeBeginGameShortcut();
    System.String IDesktopSettingsHost.NativeBindingText(ScreenshotTranslationUiTester.InputActionId action) => NativeBindingText(action);
    System.String? IDesktopSettingsHost.NativeChooseStorageLocation() => NativeChooseStorageLocation();
    void IDesktopSettingsHost.NativeChooseThemeColor(System.String property) => NativeChooseThemeColor(property);
    System.String IDesktopSettingsHost.NativeCredentialStatus { get => NativeCredentialStatus; }
    ScreenshotTranslationUiTester.InputBinding IDesktopSettingsHost.NativeDefaultBinding(ScreenshotTranslationUiTester.InputActionId action) => NativeDefaultBinding(action);
    System.Boolean IDesktopSettingsHost.NativeDefaultLaunch { get => NativeDefaultLaunch; set => NativeDefaultLaunch = value; }
    ScreenshotTranslationUiTester.ApiSettings IDesktopSettingsHost.NativeDraftSnapshot() => NativeDraftSnapshot();
    System.Threading.Tasks.Task IDesktopSettingsHost.NativeEditLibraryRetention() => NativeEditLibraryRetention();
    System.Threading.Tasks.Task IDesktopSettingsHost.NativeFillLibraryArtwork() => NativeFillLibraryArtwork();
    System.String IDesktopSettingsHost.NativeGameShortcutText { get => NativeGameShortcutText; }
    ScreenshotTranslationUiTester.LibraryPreferences IDesktopSettingsHost.NativeLibraryPreferences { get => NativeLibraryPreferences; }
    System.String IDesktopSettingsHost.NativeOcrLanguages { get => NativeOcrLanguages; }
    void IDesktopSettingsHost.NativeOpenFolder(System.String path) => NativeOpenFolder(path);
    void IDesktopSettingsHost.NativeOpenGameDiagnostics() => NativeOpenGameDiagnostics();
    void IDesktopSettingsHost.NativeOpenModelManager() => NativeOpenModelManager();
    void IDesktopSettingsHost.NativeOpenOcrComparison(System.Boolean experimental) => NativeOpenOcrComparison(experimental);
    void IDesktopSettingsHost.NativeResetLibraryAppearance() => NativeResetLibraryAppearance();
    System.Threading.Tasks.Task IDesktopSettingsHost.NativeRetryLibraryNames() => NativeRetryLibraryNames();
    void IDesktopSettingsHost.NativeSaveLibraryPreferences(ScreenshotTranslationUiTester.LibraryPreferences preferences) => NativeSaveLibraryPreferences(preferences);
    void IDesktopSettingsHost.NativeSetBinding(ScreenshotTranslationUiTester.InputActionId action, ScreenshotTranslationUiTester.InputBinding binding) => NativeSetBinding(action, binding);
    void IDesktopSettingsHost.NativeSetGameShortcut(System.String value) => NativeSetGameShortcut(value);
    void IDesktopSettingsHost.NativeSetTheme(ScreenshotTranslationUiTester.ApplicationThemeMode theme) => NativeSetTheme(theme);
    System.String IDesktopSettingsHost.NativeSettingsDescription(System.Int32 page) => NativeSettingsDescription(page);
    void IDesktopSettingsHost.NativeSettingsError(System.Exception ex) => NativeSettingsError(ex);
    System.String IDesktopSettingsHost.NativeSettingsPath { get => NativeSettingsPath; }
    event System.Action? IDesktopSettingsHost.NativeShortcutsChanged { add => NativeShortcutsChanged += value; remove => NativeShortcutsChanged -= value; }
    System.String[] IDesktopSettingsHost.WpfSettingsNames { get => WpfSettingsNames; }
    System.Drawing.Font IProfileEditorHost.Font { get => Font; }
    void IProfileEditorHost.NativeApplyParameterExample(System.String json) => NativeApplyParameterExample(json);
    void IProfileEditorHost.NativeCopyProfile() => NativeCopyProfile();
    void IProfileEditorHost.NativeDeleteProfile() => NativeDeleteProfile();
    void IProfileEditorHost.NativeNewProfile() => NativeNewProfile();
    event System.Action? IProfileEditorHost.ProfileNameFocusRequested { add => ProfileNameFocusRequested += value; remove => ProfileNameFocusRequested -= value; }
    void IProfileEditorHost.NativeRenameProfile() => NativeRenameProfile();
    void IProfileEditorHost.NativeReturnFromProfile(System.Boolean save) => NativeReturnFromProfile(save);
    System.Boolean IProfileEditorHost.NativeSaveProfile() => NativeSaveProfile();
    void IProfileEditorHost.NativeSelectCommonProfile(System.String id) => NativeSelectCommonProfile(id);
    void IProfileEditorHost.NativeSelectProfile(System.String id) => NativeSelectProfile(id);
    System.Threading.Tasks.Task IProfileEditorHost.NativeTestProfileAsync() => NativeTestProfileAsync();
    ScreenshotTranslationUiTester.ApiProfileEditorState IProfileEditorHost.ProfileDraft { get => ProfileDraft; }
    ScreenshotTranslationUiTester.TranslationContentEditorState ITranslationContentHost.ContentDraft { get => ContentDraft; }
    System.Drawing.Font ITranslationContentHost.Font { get => Font; }
    System.Boolean ITranslationContentHost.NativeSaveTranslationContent() => NativeSaveTranslationContent();
}
