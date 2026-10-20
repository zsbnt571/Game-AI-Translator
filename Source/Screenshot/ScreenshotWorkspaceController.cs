using System.Windows.Threading;

namespace ScreenshotTranslationUiTester;

// The workspace owns selection, decoded images, paging and persisted view state.
// Windows and translation services are supplied at the composition boundary.
internal sealed record ScreenshotWorkspaceCommands(
    Action Capture, Action Import, Action EditProfile,
    Action<SessionHistoryItem> EditRecord, Action<SessionHistoryItem> RetranslateRecord,
    Action<IScreenshotPaneSession> Copy,
    Action<IScreenshotPaneSession, SessionHistoryItem> Export,
    Func<WorkspaceProfileChoice[]> Profiles, Func<string> ProfileId, Action<string> SelectProfile);

internal sealed class ScreenshotWorkspaceController : IDisposable
{
    private readonly Func<IReadOnlyList<SessionHistoryItem>> snapshotHistory;
    private readonly Func<bool> isPageVisible;
    private readonly Action<Exception> reportError;
    private readonly Action<string> reportStatus;
    private readonly string layoutPath;
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DesktopUiTimer searchTimer = new(true) { Interval = 200 };
    private readonly DesktopUiTimer layoutTimer = new(true) { Interval = 600 };
    private readonly ScreenshotWorkspaceCommands commands;
    private readonly Func<byte[], Bitmap> decode;
    private IReadOnlyList<SessionHistoryItem>? snapshot;
    private ScreenshotWorkspaceLayout saved;
    private ScreenshotPaneViewState? pendingLeft, pendingRight;
    private Bitmap? original, translated;
    private Task selectionTask = Task.CompletedTask;
    private Task refreshTask = Task.CompletedTask;
    private int epoch, historyVersion, loadedVersion = -1, selectionDecodes, historyNotificationPending;
    private bool refreshing, refreshPending, applying, restore = true, imagesLoading;
    private string lastSelected;
    private double splitPercent;
    private ThemePalette? palette;

    internal ScreenshotPaneSession Left { get; } = new() { PaneTitle = "左侧" };
    internal ScreenshotPaneSession Right { get; } = new() { PaneTitle = "右侧" };
    internal ScreenshotHistorySession History { get; } = new();
    internal IScreenshotPaneSession Active { get; private set; }
    internal SessionHistoryItem? Selected { get; private set; }
    internal bool IsDisposed { get; private set; }
    internal bool IsLoading => refreshing || imagesLoading;
    internal bool HistoryVisible { get; private set; }
    internal int MaximizedPane { get; private set; } = -1;
    internal int RecordCount { get; private set; }
    internal int Pages { get; private set; } = 1;
    internal int PageIndex { get; private set; }
    internal string Query { get; private set; } = "";
    internal int DateFilter { get; private set; }
    internal string Status { get; private set; } = "就绪";
    internal int LayoutWrites { get; private set; }
    internal int SelectionDecodes => Volatile.Read(ref selectionDecodes);
    internal int ThemeApplies { get; private set; }
    internal bool CanEdit => Selected is not null;
    internal bool CanRetranslate => Selected is { OriginalPng.Length: > 0 };
    internal event Action? Changed;
    internal event Action? StatusChanged;
    internal double SplitPercent
    {
        get => splitPercent;
        set
        {
            if (!double.IsFinite(value) || IsDisposed) return;
            var next = Math.Clamp(value, 15, 85);
            if (splitPercent == next) return;
            splitPercent = next; ScheduleLayout(); NotifyChanged();
        }
    }

    internal ScreenshotWorkspaceController(string layoutPath,
        Func<IReadOnlyList<SessionHistoryItem>> snapshotHistory, Func<bool> isPageVisible,
        ScreenshotWorkspaceCommands commands, Action<Exception> reportError,
        Action<string> reportStatus, Func<byte[], Bitmap>? decode = null)
    {
        this.layoutPath = layoutPath;
        this.snapshotHistory = snapshotHistory;
        this.isPageVisible = isPageVisible;
        this.commands = commands;
        this.reportError = reportError;
        this.reportStatus = reportStatus;
        this.decode = decode ?? DecodeImage;
        saved = ScreenshotWorkspaceLayoutStore.Load(layoutPath);
        lastSelected = saved.SelectedRecord;
        splitPercent = saved.SplitPercent;
        HistoryVisible = saved.HistoryExpanded;
        Left.ApplyViewState(saved.Left); Right.ApplyViewState(saved.Right); Active = Left;
        foreach (var pane in new[] { Left, Right })
        {
            pane.Activated += (_, _) => Active = pane;
            pane.MaximizeRequested += (_, _) => Maximize(ReferenceEquals(pane, Left) ? 0 : 1);
            pane.CopyRequested += (_, _) => commands.Copy(pane);
            pane.ExportRequested += (_, _) => Export(pane);
            pane.EditRequested += (_, _) => Edit();
            pane.StateChanged += (_, _) => ScheduleLayout();
            pane.ViewChanged += (_, _) => NotifyChanged();
        }
        History.SelectionChanged += (_, _) => selectionTask = LoadSelectionAsync();
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); _ = RefreshAsync(); };
        layoutTimer.Tick += (_, _) => { layoutTimer.Stop(); SaveLayout(); };
    }

    internal void SetStatus(string value)
    {
        if (IsDisposed || Status == value) return;
        Status = value; StatusChanged?.Invoke();
    }
    private void NotifyChanged() { if (!IsDisposed) Changed?.Invoke(); }
    internal void RefreshPresentation()
    {
        if (IsDisposed) return;
        if (palette != ScreenshotRebuildStyle.Palette) { palette = ScreenshotRebuildStyle.Palette; ThemeApplies++; }
        NotifyChanged();
    }
    internal void SetHistoryVisible(bool visible)
    {
        if (IsDisposed || HistoryVisible == visible) return;
        HistoryVisible = visible; ScheduleLayout(); NotifyChanged();
    }
    internal void Swap()
    {
        if (IsDisposed) return;
        var left = Left.CaptureViewState(); var right = Right.CaptureViewState();
        Left.ApplyViewState(right); Right.ApplyViewState(left);
        ScheduleLayout(); NotifyChanged();
    }
    internal void Maximize(int index)
    {
        if (IsDisposed || index is < 0 or > 1) return;
        MaximizedPane = MaximizedPane == index ? -1 : index;
        Left.Maximized = MaximizedPane == 0; Right.Maximized = MaximizedPane == 1;
        if (MaximizedPane >= 0) Active = index == 0 ? Left : Right;
        NotifyChanged();
    }
    internal bool Escape()
    {
        if (MaximizedPane >= 0) { Maximize(MaximizedPane); return true; }
        if (HistoryVisible) { SetHistoryVisible(false); return true; }
        return false;
    }
    internal void Search(string query)
    {
        if (IsDisposed || Query == query) return;
        Query = query; PageIndex = 0; searchTimer.Stop(); searchTimer.Start();
    }
    internal void SetDate(int index)
    {
        if (IsDisposed || index is < 0 or > 2 || DateFilter == index) return;
        DateFilter = index; PageIndex = 0; _ = RefreshAsync();
    }
    internal void MovePage(int direction)
    {
        if (IsDisposed) return;
        int next = Math.Clamp(PageIndex + direction, 0, Pages - 1);
        if (next == PageIndex) return;
        PageIndex = next; _ = RefreshAsync();
    }
    internal void SetFilterWithoutDelay(string query, int date)
    { Query = query; DateFilter = date; PageIndex = 0; searchTimer.Stop(); }
    internal void Edit() { if (!IsDisposed && Selected is { } item) commands.EditRecord(item); }
    internal void Retranslate()
    {
        if (IsDisposed) return;
        if (Selected is { OriginalPng.Length: > 0 } item) commands.RetranslateRecord(item);
        else reportStatus("此记录缺少原图，无法重新翻译。");
    }
    internal void Export(IScreenshotPaneSession pane)
    { if (!IsDisposed && Selected is { } item) commands.Export(pane, item); }
    internal void CopyActive() { if (!IsDisposed) commands.Copy(Active); }

    // May be raised by the history store on a worker thread. Never touches a view there.
    internal void HistoryChanged()
    {
        Interlocked.Increment(ref historyVersion);
        if (IsDisposed || dispatcher.HasShutdownStarted) return;
        if (Interlocked.Exchange(ref historyNotificationPending, 1) != 0) return;
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref historyNotificationPending, 0);
            if (IsDisposed || !isPageVisible()) return;
            PageIndex = 0; _ = RefreshAsync();
        }));
    }
    internal Task RefreshAsync()
    {
        if (IsDisposed) return Task.CompletedTask;
        if (refreshing) { refreshPending = true; return refreshTask; }
        return refreshTask = RefreshCoreAsync();
    }
    private async Task RefreshCoreAsync()
    {
        refreshing = true;
        try
        {
            do
            {
                refreshPending = false;
                if (snapshot is null || loadedVersion != Volatile.Read(ref historyVersion))
                {
                    int version = Volatile.Read(ref historyVersion);
                    var next = await Task.Run(snapshotHistory);
                    if (IsDisposed) return;
                    snapshot = next; loadedVersion = version;
                    if (version != Volatile.Read(ref historyVersion)) refreshPending = true;
                }
                IEnumerable<SessionHistoryItem> found = snapshot;
                string query = Query.Trim();
                if (query.Length > 0) found = found.Where(x => (x.SourceText + " " + x.SourceSummary + " " + x.TranslationText + " " + x.TranslationSummary).Contains(query, StringComparison.OrdinalIgnoreCase));
                if (DateFilter == 1) found = found.Where(x => x.Timestamp.LocalDateTime.Date == DateTime.Today);
                if (DateFilter == 2) found = found.Where(x => x.Timestamp >= DateTimeOffset.Now.AddDays(-7));
                var records = found.OrderByDescending(x => x.Timestamp).ToArray();
                RecordCount = records.Length; Pages = Math.Max(1, (records.Length + 29) / 30);
                int restored = restore ? Array.FindIndex(records, x => ScreenshotHistoryIdentity.Key(x) == saved.SelectedRecord) : -1;
                if (restored >= 0) PageIndex = restored / 30;
                PageIndex = Math.Clamp(PageIndex, 0, Pages - 1);
                History.EmptyMessage = query.Length > 0 || DateFilter > 0 ? "没有匹配的记录\n试试其他关键词或日期" : "还没有截图记录\n开始截图或导入图片";
                History.SetRecords(records.Skip(PageIndex * 30).Take(30).ToArray());
                int selected = restored >= 0 ? restored % 30 : History.SelectedIndex < 0 && History.Count > 0 ? 0 : History.SelectedIndex;
                if (selected != History.SelectedIndex) History.SelectedIndex = selected;
                else selectionTask = LoadSelectionAsync();
                await selectionTask;
                restore = false; NotifyChanged();
            } while (refreshPending && !IsDisposed);
        }
        catch (Exception ex) { if (!IsDisposed) reportError(ex); }
        finally { refreshing = false; }
    }
    internal async Task SelectAsync(int index)
    {
        if (IsDisposed) return;
        if (History.SelectedIndex != index) History.SelectedIndex = index;
        else selectionTask = LoadSelectionAsync();
        await selectionTask;
    }
    private void ReleaseImages()
    {
        Left.ClearContent(); Right.ClearContent();
        original?.Dispose(); translated?.Dispose(); original = translated = null;
    }
    private async Task LoadSelectionAsync()
    {
        if (IsDisposed) return;
        var item = History.SelectedRecord;
        if (!restore && item is not null && ReferenceEquals(item, Selected) && !imagesLoading) return;
        bool same = item is not null && Selected is not null && ScreenshotHistoryIdentity.Key(item) == ScreenshotHistoryIdentity.Key(Selected);
        var left = restore ? saved.Left : Left.CaptureViewState();
        var right = restore ? saved.Right : Right.CaptureViewState();
        bool preserve = same || restore;
        pendingLeft = preserve ? left : new(left.View, left.FontSize);
        pendingRight = preserve ? right : new(right.View, right.FontSize);
        int request = ++epoch; Selected = item;
        if (item is not null) lastSelected = ScreenshotHistoryIdentity.Key(item);
        applying = true; ReleaseImages(); applying = false;
        imagesLoading = item is not null; NotifyChanged();
        if (item is null) { pendingLeft = pendingRight = null; ScheduleLayout(); return; }
        static string Full(string? text, string? summary) => string.IsNullOrWhiteSpace(text) ? summary ?? "" : text;
        string sourceText = Full(item.SourceText, item.SourceSummary), translatedText = Full(item.TranslationText, item.TranslationSummary);
        foreach (var pane in new[] { Left, Right })
        {
            pane.SetMetadata("原文", UiTargetLanguageDisplay.DisplayFromCode(item.TargetLanguage));
            pane.SetContent(null, null, sourceText, translatedText, false); pane.SetLoading(true);
        }
        Bitmap? source = null, result = null;
        var errors = new List<string>();
        try
        {
            await Task.Run(() =>
            {
                try { if (item.OriginalPng.Length > 0) { Interlocked.Increment(ref selectionDecodes); source = decode(item.OriginalPng); } }
                catch (Exception ex) { errors.Add("原图：" + SafeDiagnosticOutput.ExceptionSummary(ex)); }
                try { if (item.TranslatedImage is { Length: > 0 }) { Interlocked.Increment(ref selectionDecodes); result = decode(item.TranslatedImage); } }
                catch (Exception ex) { errors.Add("译图：" + SafeDiagnosticOutput.ExceptionSummary(ex)); }
            });
            if (IsDisposed || request != epoch) return;
            left = left with { View = Left.View, FontSize = Left.FontSize };
            right = right with { View = Right.View, FontSize = Right.FontSize };
            original = source; source = null; translated = result; result = null;
            applying = true;
            foreach (var pane in new[] { Left, Right }) pane.SetContent(original, translated, sourceText, translatedText, !preserve);
            if (preserve) { Left.ApplyViewState(left); Right.ApplyViewState(right); }
            applying = false;
            if (errors.Count > 0) reportStatus("部分图片不可用，文字保留");
            foreach (var error in errors) AppLog.Write("screenshot-history", error);
        }
        finally
        {
            source?.Dispose(); result?.Dispose();
            if (request == epoch)
            {
                imagesLoading = false; pendingLeft = pendingRight = null;
                if (!IsDisposed) { NotifyChanged(); ScheduleLayout(); }
            }
        }
    }
    private static Bitmap DecodeImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using var decoded = Image.FromStream(stream, true, true);
        return new Bitmap(decoded);
    }
    internal void ScheduleLayout()
    {
        if (applying || IsDisposed) return;
        layoutTimer.Stop(); layoutTimer.Start();
    }
    internal void SaveLayout(bool closing = false)
    {
        if (applying || (imagesLoading && !closing)) return;
        try
        {
            var left = imagesLoading && pendingLeft is { } savedLeft ? savedLeft with { View = Left.View, FontSize = Left.FontSize } : Left.CaptureViewState();
            var right = imagesLoading && pendingRight is { } savedRight ? savedRight with { View = Right.View, FontSize = Right.FontSize } : Right.CaptureViewState();
            var layout = new ScreenshotWorkspaceLayout { HistoryExpanded = HistoryVisible, SplitPercent = SplitPercent, SelectedRecord = lastSelected, Left = left, Right = right }.Normalize();
            if (layout == saved && File.Exists(layoutPath)) return;
            ScreenshotWorkspaceLayoutStore.Save(layoutPath, layout); saved = layout; LayoutWrites++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Write("screenshot-layout", SafeDiagnosticOutput.ExceptionSummary(ex)); }
    }
    public void Dispose()
    {
        if (IsDisposed) return;
        layoutTimer.Stop(); SaveLayout(closing: true); IsDisposed = true; epoch++;
        searchTimer.Dispose(); layoutTimer.Dispose(); Changed = null; StatusChanged = null;
        ReleaseImages(); History.Dispose(); snapshot = null; Selected = null;
        pendingLeft = pendingRight = null; imagesLoading = false;
    }
}

internal static class ScreenshotHistoryIdentity
{
    internal static string NormalizeKey(string? value) => (value ?? "").Replace('\\', '/').Split('/').Last();
    internal static string Key(SessionHistoryItem item) => NormalizeKey(item.SourceImagePath ?? item.Timestamp.ToString("O"));
}
