namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm : ILibraryTagWorkspaceHost
{
    private LibraryTagStore? _libraryTagStore;
    private LibraryTagStore LibraryTags => _libraryTagStore ??= new();
    private bool _libraryTagBrowser;
    private readonly List<LibraryTagSelection> _libraryTagSelections = [];
    private LibraryTagFacet[] _libraryTagFacets = [];
    private event Action? _libraryTagBatchChanged;
    bool ILibraryTagWorkspaceHost.LibraryTagBatchAvailable => _libraryGrid is NativeGameLibraryGrid;
    bool ILibraryTagWorkspaceHost.LibraryTagBatchMode => _libraryGrid is NativeGameLibraryGrid { BatchSelection: true };
    int ILibraryTagWorkspaceHost.LibraryTagBatchCount => _libraryGrid.SelectedGames.Length;
    event Action? ILibraryTagWorkspaceHost.LibraryTagBatchChanged { add => _libraryTagBatchChanged += value; remove => _libraryTagBatchChanged -= value; }
    void ILibraryTagWorkspaceHost.SetLibraryTagBatch(bool enabled) => SetLibraryTagBatch(enabled);
    private void SetLibraryTagBatch(bool enabled)
    {
        if (_libraryGrid is NativeGameLibraryGrid grid) grid.SetBatchSelection(enabled && _libraryTagBrowser);
    }
    void ILibraryTagWorkspaceHost.SelectAllLibraryTagBatch() { if (_libraryGrid is NativeGameLibraryGrid { BatchSelection: true } grid) grid.SelectAllVisible(); }
    void ILibraryTagWorkspaceHost.ClearLibraryTagBatch() { if (_libraryGrid is NativeGameLibraryGrid { BatchSelection: true } grid) grid.ClearSelection(); }
    void ILibraryTagWorkspaceHost.EditLibraryTagBatch()
    {
        if (_libraryGrid is NativeGameLibraryGrid { BatchSelection: true } grid && grid.SelectedGames.Length > 0) EditLibraryClassificationTags(grid.SelectedGames);
    }
    private System.Windows.Forms.Integration.ElementHost? _legacyTagHost;
    private WpfLibraryTagPanel? _legacyTagPanel;
    private Control CreateLegacyTagPanel()
    {
        _legacyTagPanel = new WpfLibraryTagPanel(this);
        var scroll = new System.Windows.Controls.ScrollViewer { Content = _legacyTagPanel, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled, Padding = new(8) };
        scroll.Resources.MergedDictionaries.Add(WpfThemeResources.Create()); WpfThemeResources.Apply(scroll.Resources);
        return _legacyTagHost = new() { Child = scroll, Dock = DockStyle.Fill, Visible = false, Margin = Padding.Empty };
    }
    private void RefreshLegacyTagPanel()
    {
        if (_legacyTagHost is null || _legacyTagPanel is null) return;
        _legacyTagPanel.Refresh(); bool visible = _libraryTagBrowser || _libraryTagSelections.Count > 0 || LibraryTags.Notice.Length > 0;
        _legacyTagHost.Visible = visible;
        if (_legacyTagHost.Parent is TableLayoutPanel parent)
        { parent.RowStyles[parent.GetRow(_legacyTagHost)].Height = visible ? (_libraryTagBrowser ? 250 : 80) * DeviceDpi / 96 : 0; parent.PerformLayout(); }
    }
    LibraryTagUiState ILibraryTagWorkspaceHost.TagUi => new(_libraryTagBrowser, LibraryTags.Snapshot, _libraryTagFacets, _libraryTagSelections.ToArray(), LibraryTags.Notice);
    void ILibraryTagWorkspaceHost.ToggleLibraryTag(LibraryTagSelection tag)
    {
        if (!_libraryTagSelections.Remove(tag)) _libraryTagSelections.Add(tag);
        RefreshLibraryImmediately();
    }
    void ILibraryTagWorkspaceHost.ClearLibraryTagFilters() { _libraryTagSelections.Clear(); RefreshLibraryImmediately(); }
    void ILibraryTagWorkspaceHost.ClearLibraryTagGroup(string group) { _libraryTagSelections.RemoveAll(t=>t.Group==group); RefreshLibraryImmediately(); }
    void ILibraryTagWorkspaceHost.ConfigureLibraryTags() => ConfigureLibraryTags();
    void ILibraryTagWorkspaceHost.ManageLibraryTags() => ManageLibraryTags();
    void ILibraryTagWorkspaceHost.ReturnFromTagBrowser() => NavigateLibrary(new("scope", "home"));
    private void RefreshLibraryTagState(RecentGame[] all)
    {
        LibraryTags.Reload();
        _libraryTagFacets = LibraryTagStore.Facets(LibraryTags.Snapshot, all, _libraryMetadata!, _libraryEngines, _libraryPreferences);
        _libraryTagSelections.RemoveAll(t => !_libraryTagFacets.Any(g => g.Id == t.Group && g.Values.Any(v => v.Id == t.Value)));
    }
    private bool ResetLibraryTagNavigation()
    {
        bool changed = _libraryTagBrowser || _libraryTagSelections.Count > 0;
        SetLibraryTagBatch(false);
        _libraryTagBrowser = false; _libraryTagSelections.Clear(); return changed;
    }
    private void ResetOldLibraryFilters()
    {
        _libraryFiltersLoading = true;
        try
        {
            _libraryCollection = ""; _libraryScope = "home"; _libraryTag = "";
            LibraryFilterIndex = 0; LibraryEngineChoice = "全部引擎"; LibraryFolderChoice = "全部文件夹"; LibrarySearchText = "";
        }
        finally { _libraryFiltersLoading = false; }
    }
    private bool HandleLibraryTagNavigation(LibraryNavigationTarget target)
    {
        if (target.Kind == "tag-settings") { ConfigureLibraryTags(); return true; }
        if (target.Kind == "tag-browser")
        {
            SetLibraryTagBatch(false);
            _libraryNavigationEpoch++; ResetOldLibraryFilters(); _libraryTagSelections.Clear(); _libraryTagBrowser = true;
            _libraryHomePending = WpfWorkspace.Enabled;
            if (!WpfWorkspace.Enabled) ShowLibraryHome();
            RefreshLibraryImmediately(); RecordPageLocation(); return true;
        }
        var selection = target.Kind == "group-tag" ? LibraryTagSelection.Parse(target.Value) :
            target.Kind == "engine" && !_libraryTagBrowser && _libraryTagSelections.Count > 0 ? new LibraryTagSelection(LibraryTagStore.Engine, target.Value) : null;
        if (selection is null) return false;
        SetLibraryTagBatch(false);
        _libraryNavigationEpoch++;
        if (_libraryTagBrowser) _libraryTagSelections.Clear();
        else if (LibraryEngineIndex > 0 && !_libraryTagSelections.Any(t => t.Group == LibraryTagStore.Engine)) _libraryTagSelections.Add(new(LibraryTagStore.Engine, LibraryEngineChoice));
        bool selected = _libraryTagSelections.Contains(selection);
        _libraryTagSelections.RemoveAll(t => t.Group == selection.Group);
        if (!selected) _libraryTagSelections.Add(selection);
        _libraryTagBrowser = false; ResetOldLibraryFilters(); _libraryHomePending = WpfWorkspace.Enabled;
        if (!WpfWorkspace.Enabled) ShowLibraryHome();
        RefreshLibraryImmediately(); RecordPageLocation(); return true;
    }
    private void ConfigureLibraryTags()
    {
        try
        {
            LibraryTags.Reload(); using var dialog = new NativeLibraryTagSettings(LibraryTags); dialog.ShowDialog(this);
            var side = LibraryTags.Snapshot.Sidebar;
            if (!_libraryTagBrowser) _libraryTagSelections.RemoveAll(t => t.Group == LibraryTagStore.Engine ? !side.Engines : t.Group != side.Primary && t.Group != side.Secondary);
            if (!side.Engines) LibraryEngineIndex = 0;
            RefreshLibraryImmediately();
        }
        catch (Exception ex) { ShowFusionError(ex); }
    }
    private void ManageLibraryTags()
    {
        try { LibraryTags.Reload(); using var dialog = new NativeLibraryTagManager(LibraryTags); dialog.ShowDialog(this); RefreshLibraryImmediately(); }
        catch (Exception ex) { ShowFusionError(ex); }
    }
    private void EditLibraryClassificationTags(RecentGame[] games)
    {
        try { LibraryTags.Reload(); using var dialog = new NativeGameTagEditor(LibraryTags, games); if (dialog.ShowDialog(this) == DialogResult.OK) RefreshLibraryImmediately(); }
        catch (Exception ex) { ShowFusionError(ex); }
    }
    private string LibraryTagNavigationKey => _libraryTagBrowser ? "tag-browser" : _libraryTagSelections.LastOrDefault() is { } tag ? (tag.Group == LibraryTagStore.Engine ? "engine:" + tag.Value : "group-tag:" + tag.Key) : "home";
    private string LibraryTagFilterTitle => string.Join(" · ", _libraryTagSelections.Select(t => _libraryTagFacets.FirstOrDefault(g => g.Id == t.Group)?.Values.FirstOrDefault(v => v.Id == t.Value)?.Name).Where(n => n is not null));
}
