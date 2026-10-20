namespace ScreenshotTranslationUiTester;

internal sealed record WorkspaceProfileChoice(string Id, string Label) { public override string ToString() => Label; }

public sealed partial class MainForm
{
    internal ScreenshotWorkspaceController ScreenshotWorkspace { get; private set; } = null!;
    internal ScreenshotWorkspaceCommands ScreenshotCommands { get; private set; } = null!;
    private double ShotSplitPercent
    { get => WpfWorkspace.Enabled ? ScreenshotWorkspace.SplitPercent : _shotSplit.SplitPercent;
      set { if (WpfWorkspace.Enabled) ScreenshotWorkspace.SplitPercent = value; else _shotSplit.SplitPercent = value; } }
    private int ShotMaximizedPane
    { get => WpfWorkspace.Enabled ? ScreenshotWorkspace.MaximizedPane : _shotSplit.MaximizedPane;
      set { if (!WpfWorkspace.Enabled) _shotSplit.MaximizedPane = value; } }
    private string ShotQuery => _shotSearch.Text;
    private int ShotDateFilter => _shotDate.SelectedIndex;
    private void InitializeNativeScreenshotWorkspace()
    {
        ScreenshotCommands = new(BeginCapture, ImportShotImage, () => EditModeProfile(false),
            OpenShotEditor, RetranslateShot, CopyShotPane, ExportShotPane,
            () => NativeWorkspaceProfiles(false), () => NativeWorkspaceProfileId(false),
            id => NativeSelectWorkspaceProfile(false, id));
        ScreenshotWorkspace = new(ShotLayoutPath, _session.SnapshotHistory,
            () => _currentMainPage == "主页", ScreenshotCommands, ShowFusionError,
            status => DesktopStatus = status);
        _session.HistoryChanged += ScreenshotWorkspace.HistoryChanged;
        FormClosed += (_, _) =>
        {
            _session.HistoryChanged -= ScreenshotWorkspace.HistoryChanged;
            ScreenshotWorkspace.Dispose();
        };
        // Also release services if construction fails before a window is shown.
        Disposed += (_, _) =>
        {
            _session.HistoryChanged -= ScreenshotWorkspace.HistoryChanged;
            ScreenshotWorkspace.Dispose();
        };
    }
    internal WorkspaceProfileChoice[] NativeWorkspaceProfiles(bool embedded)
    {
        if (_fusion is null) return [];
        string? current = embedded && _selectedGame is { } game ? _fusion.GameProfileId(game.ExePath) : embedded ? _fusion.State.EmbeddedProfile : _fusion.State.ScreenshotProfile;
        var choices = new List<WorkspaceProfileChoice>();
        if (!embedded || _selectedGame is null) choices.Add(new("", UiLanguage.Text(FollowCommon)));
        foreach (var pair in _fusion.State.Profiles.OrderBy(x => FusionConfiguration.MissingConnection(x.Value).Count > 0))
        {
            bool incomplete = FusionConfiguration.MissingConnection(pair.Value).Count > 0;
            if (incomplete && pair.Key != current) continue;
            choices.Add(new(pair.Key, _fusion.Name(pair.Key) + (incomplete ? UiLanguage.Text("（未配置完成）") : "")));
        }
        return choices.ToArray();
    }
    internal string NativeWorkspaceProfileId(bool embedded) => _fusion is null ? "" : (embedded && _selectedGame is { } game ? _fusion.GameProfileId(game.ExePath) : embedded ? _fusion.State.EmbeddedProfile : _fusion.State.ScreenshotProfile) ?? "";
    internal void NativeSelectWorkspaceProfile(bool embedded, string id) { if (_loadingFusion) return; PersistProfileChoice(id.Length == 0 ? null : id, false, embedded); }

    // Existing command/diagnostic callers share the module's single state.
    internal void NativeShotSearch(string text) => ScreenshotWorkspace.Search(text);
    internal void NativeShotDate(int index) => ScreenshotWorkspace.SetDate(index);
    internal string NativeShotQuery => ScreenshotWorkspace.Query;
    internal int NativeShotDateIndex => ScreenshotWorkspace.DateFilter;
    internal int NativeShotPages => ScreenshotWorkspace.Pages;
    internal int NativeShotPageIndex => ScreenshotWorkspace.PageIndex;
    internal void NativeShotPage(int direction) => ScreenshotWorkspace.MovePage(direction);
    internal bool NativeShotCanRetranslate => ScreenshotWorkspace.CanRetranslate;
    internal bool NativeShotCanEdit => ScreenshotWorkspace.CanEdit;
    internal void NativeShotCapture() => ScreenshotCommands.Capture();
    internal void NativeShotImport() => ScreenshotCommands.Import();
    internal void NativeShotSwap() => ScreenshotWorkspace.Swap();
    internal void NativeShotRetranslate() => ScreenshotWorkspace.Retranslate();
    internal void NativeShotEdit() => ScreenshotWorkspace.Edit();
    internal void NativeShotEditProfile() => ScreenshotCommands.EditProfile();
    internal void NativeShotExport(IScreenshotPaneSession pane) => ScreenshotWorkspace.Export(pane);
    internal void NativeShotCopyActive() => ScreenshotWorkspace.CopyActive();
    internal IScreenshotPaneSession NativeShotActive => ScreenshotWorkspace.Active;
    internal string NativeShotStatus => ScreenshotWorkspace.Status;
}
