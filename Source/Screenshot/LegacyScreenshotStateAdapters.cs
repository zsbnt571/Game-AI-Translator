namespace ScreenshotTranslationUiTester;

// Retained only for the legacy diagnostic harness. Native workspaces use plain session objects.
internal sealed partial class ScreenshotRebuildPane : IScreenshotPaneSession
{
    ScreenshotPaneView IScreenshotPaneSession.View { get => View; set => View = value; }
    string IScreenshotPaneSession.PaneTitle { get => PaneTitle; set => PaneTitle = value; }
    bool IScreenshotPaneSession.Maximized { get => Maximized; set => Maximized = value; }
    int IScreenshotPaneSession.FontSize { get => FontSize; set => FontSize = value; }
    bool IScreenshotPaneSession.HasContent { get => HasContent; }
    bool IScreenshotPaneSession.IsImageView { get => IsImageView; }
    string IScreenshotPaneSession.CurrentText { get => CurrentText; }
    string IScreenshotPaneSession.SelectedTextOrAll { get => SelectedTextOrAll; }
    Bitmap? IScreenshotPaneSession.CurrentBitmap { get => CurrentBitmap; }
    string IScreenshotPaneSession.NativeEmptyText { get => NativeEmptyText; }
    string IScreenshotPaneSession.NativeSelectedText { get => NativeSelectedText; set => NativeSelectedText = value; }
    Func<ScreenshotPaneViewState>? IScreenshotPaneSession.NativeStateRequested { get => NativeStateRequested; set => NativeStateRequested = value; }
    Action<ScreenshotPaneViewState>? IScreenshotPaneSession.NativeStateApplied { get => NativeStateApplied; set => NativeStateApplied = value; }
    Action<string>? IScreenshotPaneSession.NativeImageCommand { get => NativeImageCommand; set => NativeImageCommand = value; }
    event EventHandler? IScreenshotPaneSession.Activated { add => Activated += value; remove => Activated -= value; }
    event EventHandler? IScreenshotPaneSession.MaximizeRequested { add => MaximizeRequested += value; remove => MaximizeRequested -= value; }
    event EventHandler? IScreenshotPaneSession.CopyRequested { add => CopyRequested += value; remove => CopyRequested -= value; }
    event EventHandler? IScreenshotPaneSession.ExportRequested { add => ExportRequested += value; remove => ExportRequested -= value; }
    event EventHandler? IScreenshotPaneSession.EditRequested { add => EditRequested += value; remove => EditRequested -= value; }
    event EventHandler? IScreenshotPaneSession.StateChanged { add => StateChanged += value; remove => StateChanged -= value; }
    event EventHandler? IScreenshotPaneSession.ViewChanged { add => ViewChanged += value; remove => ViewChanged -= value; }
    event Action? IScreenshotPaneSession.NativeViewChanged { add => NativeViewChanged += value; remove => NativeViewChanged -= value; }
    void IScreenshotPaneSession.SetMetadata(string? sourceLanguage, string? targetLanguage) => SetMetadata(sourceLanguage,targetLanguage);
    void IScreenshotPaneSession.SetContent(Bitmap? original, Bitmap? translated, string source, string translation, bool resetView) => SetContent(original,translated,source,translation,resetView);
    void IScreenshotPaneSession.ClearContent() => ClearContent();
    void IScreenshotPaneSession.SetLoading(bool value) => SetLoading(value);
    ScreenshotPaneViewState IScreenshotPaneSession.CaptureViewState() => CaptureViewState();
    void IScreenshotPaneSession.ApplyViewState(ScreenshotPaneViewState? state) => ApplyViewState(state);
    void IScreenshotPaneSession.NativeStateChanged() => NativeStateChanged();
    void IScreenshotPaneSession.NativeActivate() => NativeActivate();
    void IScreenshotPaneSession.NativeMaximize() => NativeMaximize();
    void IScreenshotPaneSession.NativeCopy() => NativeCopy();
    void IScreenshotPaneSession.NativeExport() => NativeExport();
    void IScreenshotPaneSession.NativeEdit() => NativeEdit();
}

internal sealed partial class ScreenshotRebuildHistory : IScreenshotHistorySession
{
    string IScreenshotHistorySession.EmptyMessage { get => EmptyMessage; set => EmptyMessage = value; }
    int IScreenshotHistorySession.DecodeCount { get => DecodeCount; }
    int IScreenshotHistorySession.Count { get => Count; }
    int IScreenshotHistorySession.SelectedIndex { get => SelectedIndex; set => SelectedIndex = value; }
    SessionHistoryItem? IScreenshotHistorySession.SelectedRecord { get => SelectedRecord; }
    (SessionHistoryItem Item, Bitmap Thumbnail)[] IScreenshotHistorySession.NativeCards { get => NativeCards; }
    event EventHandler? IScreenshotHistorySession.SelectionChanged { add => SelectionChanged += value; remove => SelectionChanged -= value; }
    event Action? IScreenshotHistorySession.NativeRecordsChanged { add => NativeRecordsChanged += value; remove => NativeRecordsChanged -= value; }
    void IScreenshotHistorySession.SetRecords(IReadOnlyList<SessionHistoryItem> records) => SetRecords(records);
}
