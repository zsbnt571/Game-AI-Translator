namespace ScreenshotTranslationUiTester;

internal sealed class LibraryWorkspaceState
{
    internal string Search = "", Engine = "全部引擎", Folder = "全部文件夹";
    internal int Filter;
    internal string Heading = "游戏库", Count = "", Retention = "保留数量", UpperSection = "我的游戏", LowerSection = "我的游戏";
    internal string GameTitle = "", GamePath = "", ChineseName = "", Note = "";
    internal Image? Cover;
}

public sealed partial class MainForm
{
    internal LibraryWorkspaceState LibraryState { get; } = new();
    private void LibraryFilterChanged() { if (!_libraryFiltersLoading) ScheduleLibraryRefresh(); }
    internal string LibrarySearchText
    { get => WpfWorkspace.Enabled ? LibraryState.Search : _librarySearch.Text; set { if (!WpfWorkspace.Enabled) { _librarySearch.Text = value; return; } if (LibraryState.Search == value) return; LibraryState.Search = value; LibraryFilterChanged(); } }
    internal int LibraryFilterIndex
    { get => WpfWorkspace.Enabled ? LibraryState.Filter : _libraryFilter.SelectedIndex; set { if (!WpfWorkspace.Enabled) { _libraryFilter.SelectedIndex = value; return; } value = Math.Clamp(value, 0, 3); if (LibraryState.Filter == value) return; LibraryState.Filter = value; LibraryFilterChanged(); } }
    internal string LibraryEngineChoice
    { get => WpfWorkspace.Enabled ? LibraryState.Engine : _libraryEngine.Text; set { if (!WpfWorkspace.Enabled) { _libraryEngine.SelectedItem = value; return; } if (LibraryState.Engine == value) return; LibraryState.Engine = value; LibraryFilterChanged(); } }
    internal string LibraryFolderChoice
    { get => WpfWorkspace.Enabled ? LibraryState.Folder : _libraryFolder.Text; set { if (!WpfWorkspace.Enabled) { _libraryFolder.SelectedItem = value; return; } if (LibraryState.Folder == value) return; LibraryState.Folder = value; LibraryFilterChanged(); } }
    private int LibraryEngineIndex { get => WpfWorkspace.Enabled ? LibraryState.Engine == "全部引擎" ? 0 : 1 : _libraryEngine.SelectedIndex; set { if (WpfWorkspace.Enabled) { if (value == 0) LibraryEngineChoice = "全部引擎"; } else _libraryEngine.SelectedIndex = value; } }
    private int LibraryFolderIndex { get => WpfWorkspace.Enabled ? LibraryState.Folder == "全部文件夹" ? 0 : 1 : _libraryFolder.SelectedIndex; set { if (WpfWorkspace.Enabled) { if (value == 0) LibraryFolderChoice = "全部文件夹"; } else _libraryFolder.SelectedIndex = value; } }
    private string LibraryHeadingText { get => WpfWorkspace.Enabled ? LibraryState.Heading : _libraryHeading?.Text ?? "游戏库"; set { if (WpfWorkspace.Enabled) LibraryState.Heading = value; else if (_libraryHeading is not null) _libraryHeading.Text = value; } }
    private string LibraryCountText { get => WpfWorkspace.Enabled ? LibraryState.Count : _libraryCount.Text; set { if (WpfWorkspace.Enabled) LibraryState.Count = value; else _libraryCount.Text = value; } }
    private string LibraryRetentionText { get => WpfWorkspace.Enabled ? LibraryState.Retention : _libraryRetention.Text; set { if (WpfWorkspace.Enabled) LibraryState.Retention = value; else _libraryRetention.Text = value; } }
    private string LibraryUpperSectionText { get => WpfWorkspace.Enabled ? LibraryState.UpperSection : _recentSectionHeading.Text; set { if (WpfWorkspace.Enabled) LibraryState.UpperSection = value; else _recentSectionHeading.Text = value; } }
    private string LibraryLowerSectionText { get => WpfWorkspace.Enabled ? LibraryState.LowerSection : _mainSectionHeading.Text; set { if (WpfWorkspace.Enabled) LibraryState.LowerSection = value; else _mainSectionHeading.Text = value; } }
    internal string GameTitleText { get => WpfWorkspace.Enabled ? LibraryState.GameTitle : _gameTitle.Text; set { if (WpfWorkspace.Enabled) LibraryState.GameTitle = value; else _gameTitle.Text = value; } }
    internal string GamePathText { get => WpfWorkspace.Enabled ? LibraryState.GamePath : _gamePath.Text; set { if (WpfWorkspace.Enabled) LibraryState.GamePath = value; else _gamePath.Text = value; } }
    internal string GameChineseNameText { get => WpfWorkspace.Enabled ? LibraryState.ChineseName : _libraryChineseName.Text; set { if (WpfWorkspace.Enabled) LibraryState.ChineseName = value; else _libraryChineseName.Text = value; } }
    private string GameNoteText { get => WpfWorkspace.Enabled ? LibraryState.Note : _libraryNote.Text; set { if (WpfWorkspace.Enabled) LibraryState.Note = value; else _libraryNote.Text = value; } }
    internal Image? GameCoverImage { get => WpfWorkspace.Enabled ? LibraryState.Cover : _libraryCover.Image; set { if (WpfWorkspace.Enabled) LibraryState.Cover = value; else _libraryCover.Image = value; } }
}
