namespace ScreenshotTranslationUiTester;

// Session state is independent of either window framework. Image ownership
// remains with the workspace; both panes only borrow these bitmaps.
internal interface IScreenshotPaneSession
{
    ScreenshotPaneView View { get; set; }
    string PaneTitle { get; set; }
    bool Maximized { get; set; }
    int FontSize { get; set; }
    bool HasContent { get; }
    bool IsImageView { get; }
    string CurrentText { get; }
    string SelectedTextOrAll { get; }
    Bitmap? CurrentBitmap { get; }
    string NativeEmptyText { get; }
    string NativeSelectedText { get; set; }
    Func<ScreenshotPaneViewState>? NativeStateRequested { get; set; }
    Action<ScreenshotPaneViewState>? NativeStateApplied { get; set; }
    Action<string>? NativeImageCommand { get; set; }
    event EventHandler? Activated;
    event EventHandler? MaximizeRequested;
    event EventHandler? CopyRequested;
    event EventHandler? ExportRequested;
    event EventHandler? EditRequested;
    event EventHandler? StateChanged;
    event EventHandler? ViewChanged;
    event Action? NativeViewChanged;
    void SetMetadata(string? sourceLanguage, string? targetLanguage);
    void SetContent(Bitmap? original, Bitmap? translated, string source, string translation, bool resetView = true);
    void ClearContent();
    void SetLoading(bool value);
    ScreenshotPaneViewState CaptureViewState();
    void ApplyViewState(ScreenshotPaneViewState? state);
    void NativeStateChanged();
    void NativeActivate();
    void NativeMaximize();
    void NativeCopy();
    void NativeExport();
    void NativeEdit();
}

internal sealed class ScreenshotPaneSession : IScreenshotPaneSession
{
    private Bitmap? original, translated;
    private string source = "", translation = "";
    private bool loading, hasContent, maximized;
    private ScreenshotPaneViewState state = new();
    public ScreenshotPaneView View
    {
        get => state.View;
        set { if (!Enum.IsDefined(value) || value == state.View) return; state = state with { View = value }; NativeSelectedText = ""; NativeViewChanged?.Invoke(); ViewChanged?.Invoke(this, EventArgs.Empty); StateChanged?.Invoke(this, EventArgs.Empty); }
    }
    public string PaneTitle { get; set; } = "左侧";
    public bool Maximized { get => maximized; set { if (value == maximized) return; maximized = value; NativeViewChanged?.Invoke(); } }
    public int FontSize
    { get => state.FontSize; set { int next = Math.Clamp(value, 12, 28); if (next == state.FontSize) return; state = state with { FontSize = next }; NativeViewChanged?.Invoke(); StateChanged?.Invoke(this, EventArgs.Empty); } }
    public bool HasContent => hasContent;
    public bool IsImageView => View is ScreenshotPaneView.OriginalImage or ScreenshotPaneView.TranslatedImage;
    public string CurrentText => View == ScreenshotPaneView.SourceText ? source : View == ScreenshotPaneView.TranslatedText ? translation : "";
    public Bitmap? CurrentBitmap => View == ScreenshotPaneView.OriginalImage ? original : View == ScreenshotPaneView.TranslatedImage ? translated : null;
    public string NativeEmptyText => loading ? IsImageView ? "正在读取图片…" : "正在读取文字…" : !hasContent ? "选择历史记录，或开始截图、导入图片" : View switch
    { ScreenshotPaneView.OriginalImage => "原图不可用", ScreenshotPaneView.TranslatedImage => "暂无译图", ScreenshotPaneView.SourceText => "暂无原文", _ => "暂无译文" };
    public string NativeSelectedText { get; set; } = "";
    public string SelectedTextOrAll => NativeSelectedText.Length > 0 ? NativeSelectedText : CurrentText;
    public Func<ScreenshotPaneViewState>? NativeStateRequested { get; set; }
    public Action<ScreenshotPaneViewState>? NativeStateApplied { get; set; }
    public Action<string>? NativeImageCommand { get; set; }
    public event EventHandler? Activated, MaximizeRequested, CopyRequested, ExportRequested, EditRequested, StateChanged, ViewChanged;
    public event Action? NativeViewChanged;
    internal string SourceLanguage { get; private set; } = "";
    internal string TargetLanguage { get; private set; } = "";
    public void SetMetadata(string? sourceLanguage, string? targetLanguage) { SourceLanguage = sourceLanguage ?? ""; TargetLanguage = targetLanguage ?? ""; NativeViewChanged?.Invoke(); }
    public void SetContent(Bitmap? original, Bitmap? translated, string source, string translation, bool resetView = true)
    {
        this.original = original; this.translated = translated; this.source = source ?? ""; this.translation = translation ?? "";
        NativeSelectedText = ""; hasContent = true; loading = false;
        if (resetView) { state = new(state.View, state.FontSize); NativeStateApplied?.Invoke(state); }
        NativeViewChanged?.Invoke();
    }
    public void ClearContent()
    { original = translated = null; source = translation = NativeSelectedText = ""; hasContent = loading = false; state = new(state.View, state.FontSize); NativeStateApplied?.Invoke(state); NativeViewChanged?.Invoke(); }
    public void SetLoading(bool value) { loading = value; NativeViewChanged?.Invoke(); }
    public ScreenshotPaneViewState CaptureViewState() => (NativeStateRequested?.Invoke() ?? state) with { View = state.View, FontSize = state.FontSize };
    public void ApplyViewState(ScreenshotPaneViewState? value)
    {
        if (value is null) return;
        state = value with { View = Enum.IsDefined(value.View) ? value.View : ScreenshotPaneView.OriginalImage, FontSize = Math.Clamp(value.FontSize, 12, 28),
            FitMode = Enum.IsDefined(value.FitMode) ? value.FitMode : ScreenshotPaneFitMode.Window, Zoom = float.IsFinite(value.Zoom) ? Math.Clamp(value.Zoom, .05f, 20f) : 1 };
        NativeSelectedText = ""; NativeViewChanged?.Invoke(); NativeStateApplied?.Invoke(state);
    }
    public void NativeStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
    public void NativeActivate() => Activated?.Invoke(this, EventArgs.Empty);
    public void NativeMaximize() => MaximizeRequested?.Invoke(this, EventArgs.Empty);
    public void NativeCopy() => CopyRequested?.Invoke(this, EventArgs.Empty);
    public void NativeExport() => ExportRequested?.Invoke(this, EventArgs.Empty);
    public void NativeEdit() => EditRequested?.Invoke(this, EventArgs.Empty);
}

internal interface IScreenshotHistorySession
{
    string EmptyMessage { get; set; }
    int DecodeCount { get; }
    int Count { get; }
    int SelectedIndex { get; set; }
    SessionHistoryItem? SelectedRecord { get; }
    (SessionHistoryItem Item, Bitmap Thumbnail)[] NativeCards { get; }
    event EventHandler? SelectionChanged;
    event Action? NativeRecordsChanged;
    void SetRecords(IReadOnlyList<SessionHistoryItem> records);
}

internal sealed class ScreenshotHistorySession : IScreenshotHistorySession, IDisposable
{
    private List<(SessionHistoryItem Item, Bitmap Thumbnail)> cards = [];
    private int selected = -1;
    public string EmptyMessage { get; set; } = "还没有截图记录\n开始截图或导入图片";
    public int DecodeCount { get; private set; }
    public int Count => cards.Count;
    public int SelectedIndex { get => selected; set { int next = value >= 0 && value < Count ? value : -1; if (next == selected) return; selected = next; SelectionChanged?.Invoke(this, EventArgs.Empty); } }
    public SessionHistoryItem? SelectedRecord => selected >= 0 && selected < cards.Count ? cards[selected].Item : null;
    public (SessionHistoryItem Item, Bitmap Thumbnail)[] NativeCards => cards.ToArray();
    public event EventHandler? SelectionChanged;
    public event Action? NativeRecordsChanged;
    public void SetRecords(IReadOnlyList<SessionHistoryItem> records)
    {
        var key = SelectedRecord is { } current ? ScreenshotHistoryIdentity.Key(current) : "";
        if (!cards.Select(x => x.Item).SequenceEqual(records))
        {
            var remaining = cards.GroupBy(c => ScreenshotHistoryIdentity.Key(c.Item)).ToDictionary(g => g.Key, g => new Queue<(SessionHistoryItem Item, Bitmap Thumbnail)>(g));
            var next = new List<(SessionHistoryItem Item, Bitmap Thumbnail)>();
            foreach (var item in records)
            {
                var previous = remaining.TryGetValue(ScreenshotHistoryIdentity.Key(item), out var queue) && queue.Count > 0 ? queue.Dequeue() : default;
                if (previous.Item is not null && previous.Item.ThumbnailJpeg.AsSpan().SequenceEqual(item.ThumbnailJpeg)) { next.Add((item, previous.Thumbnail)); continue; }
                previous.Thumbnail?.Dispose(); Bitmap thumbnail;
                try { using var stream = new MemoryStream(item.ThumbnailJpeg); using var image = Image.FromStream(stream); thumbnail = new(image); }
                catch { thumbnail = new(86, 53); using var graphics = Graphics.FromImage(thumbnail); graphics.Clear(ScreenshotRebuildStyle.Palette.Control); }
                next.Add((item, thumbnail)); DecodeCount++;
            }
            foreach (var queue in remaining.Values) foreach (var card in queue) card.Thumbnail.Dispose();
            cards = next; selected = cards.FindIndex(c => ScreenshotHistoryIdentity.Key(c.Item) == key);
        }
        NativeRecordsChanged?.Invoke();
    }
    public void Dispose() { foreach (var card in cards) card.Thumbnail.Dispose(); cards.Clear(); selected = -1; }
}
