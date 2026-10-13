using System.Drawing.Imaging;
using System.Text;

namespace ScreenshotTranslationUiTester;

internal sealed record ScreenshotWorkspaceDiagnostics(
    int LeftMode, int RightMode, bool HistoryVisible, int MaximizedPane, string SelectedId,
    int RecordCount, Rectangle LeftBounds, Rectangle RightBounds, bool LeftVisible, bool RightVisible,
    double SplitPercent, bool Loading, ScreenshotPaneViewState LeftState, ScreenshotPaneViewState RightState,
    Rectangle HistoryBounds, int ToolbarHeight, int DeviceDpi);

internal sealed record ScreenshotWorkspacePerformanceDiagnostics(int LayoutWrites, int SelectionDecodes, int FullThemeApplies);

public sealed partial class MainForm
{
    private readonly ScreenshotRebuildPane _shotLeft = new() { Dock = DockStyle.Fill, PaneTitle = "左侧" };
    private readonly ScreenshotRebuildPane _shotRight = new() { Dock = DockStyle.Fill, PaneTitle = "右侧" };
    private readonly ScreenshotRebuildSplit _shotSplit = new() { Dock = DockStyle.Fill };
    private readonly ScreenshotRebuildHistory _shotHistory = new() { Dock = DockStyle.Fill };
    private readonly ScreenshotHistoryRail _shotRail = new() { Dock = DockStyle.Fill };
    private readonly TextBox _shotSearch = new() { Name = "ScreenshotHistorySearch", BorderStyle = BorderStyle.None, PlaceholderText = UiLanguage.Text("搜索原文或译文") };
    private readonly ComboBox _shotDate = new GamePlanBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 96, LogicalHeight = 26 };
    private readonly Label _shotCount = new() { TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _shotPageNumber = new() { TextAlign = ContentAlignment.MiddleCenter };
    private readonly List<Font> _shotFonts = [];
    private readonly System.Windows.Forms.Timer _shotSearchTimer = new() { Interval = 200 };
    private readonly System.Windows.Forms.Timer _shotLayoutTimer = new() { Interval = 600 };
    private Panel? _shotPage, _shotToolbar, _shotBody, _shotHistoryHost, _shotExpanded, _shotContent, _shotSearchBorder, _shotHistoryFooter;
    private Label? _shotHeading, _shotProfileLabel, _shotHistoryHeading, _shotHistoryNote;
    private ScreenshotRebuildButton? _shotCapture, _shotImport, _shotExport, _shotSwap, _shotEditProfile, _shotRetranslate, _shotEditRecord, _shotResetSplit, _shotOverflow, _shotCollapse, _shotPrevious, _shotNext;
    private LocalizedContextMenuStrip? _shotOverflowMenu, _shotExportMenu, _shotRecordMenu;
    private ScreenshotRebuildPane? _shotActive;
    private SessionHistoryItem? _shotSelected;
    private Bitmap? _shotOriginal, _shotTranslated;
    private IReadOnlyList<SessionHistoryItem>? _shotSnapshot;
    private int _shotEpoch, _shotPageIndex, _shotHistoryVersion, _shotLoadedVersion = -1;
    private int _shotLayoutWrites, _shotSelectionDecodes, _shotFullThemeApplies;
    private bool _shotLoading, _shotImagesLoading, _shotRefreshPending, _shotApplying, _shotClosed, _shotHistoryVisible, _shotRestore = true, _shotArranging;
    private string _shotLastSelected = "";
    private ScreenshotWorkspaceLayout _shotSaved = new();
    private ScreenshotPaneViewState? _shotPendingLeft, _shotPendingRight;
    private ThemePalette? _shotAppliedPalette;
    private (int Width, int Dpi, Font ProfileFont, Font ActionFont, ApplicationLanguage Language)? _shotToolbarLayout;
    private (int Width, int Dpi, Font SearchFont, ApplicationLanguage Language)? _shotHistoryFixedLayout;
    private Task _shotSelectionTask = Task.CompletedTask;
    private string ShotLayoutPath => Path.Combine(Path.GetDirectoryName(_settingsPath)!, "screenshot-workspace.json");
    private int ShotScale(int n) => (int)Math.Round(n * DeviceDpi / 96d);

    private Font ShotFont(float points = 8.25f, FontStyle style = FontStyle.Regular)
    { var font = ScreenshotRebuildStyle.Font(points, style); _shotFonts.Add(font); return font; }
    private Label ShotLabel(string text, float points = 8.25f, bool bold = false)
        => new() { Text = text, Font = ShotFont(points, bold ? FontStyle.Bold : FontStyle.Regular), AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private static ScreenshotRebuildButton ShotButton(string text, string icon, int width, Action click)
    { var button = new ScreenshotRebuildButton(text, icon, width); button.Click += (_, _) => click(); return button; }

    private void BuildScreenshotWorkspace(Panel page)
    {
        page.AutoScroll = false; page.Padding = Padding.Empty;
        _shotPage = new BufferedPage { Dock = DockStyle.Fill, AutoScroll = false, Font = ShotFont() };
        // Compact typography is local to this page; library/navigation keep .85 fonts.
        FontManager.MarkPreviewTextRoot(_shotPage);
        _shotToolbar = new Panel(); _shotBody = new Panel();
        _shotHistoryHost = new Panel(); _shotExpanded = new Panel { Dock = DockStyle.Fill };
        _shotContent = new Panel();
        _shotHeading = ShotLabel("截图翻译", 11.25f, true);
        _shotHeading.Name = "ScreenshotToolbarHeading";
        _shotProfileLabel = new ScreenshotRebuildSingleLineLabel { Text = "翻译方案", Font = ShotFont(), TextAlign = ContentAlignment.MiddleLeft };
        _shotProfileLabel.Name = "ScreenshotToolbarProfileLabel";
        _shotProfile.Name = "ScreenshotToolbarProfile"; _shotProfile.AccessibleName = "截图翻译方案";
        _shotProfile.Font = ShotFont();
        if (_shotProfile is GamePlanBox plan) plan.LogicalHeight = 26;
        // The selector is now part of the initial shell. ConfigureFusionInputs(this)
        // will attach its owner drawing and wheel guard once, with no menu host.
        _shotEditProfile = ShotButton("编辑方案", "", 64, () => EditModeProfile(false));
        _shotRetranslate = ShotButton("重新翻译", "", 64, RetranslateShot);
        _shotEditRecord = ShotButton("详细编辑", "", 64, OpenShotEditor);
        _shotResetSplit = ShotButton("左右等分", "", 64, () => _shotSplit.SplitPercent = 50);
        _shotEditProfile.Name = "ScreenshotToolbarEditProfile"; _shotEditProfile.AccessibleName = "编辑当前截图翻译方案";
        _shotRetranslate.Name = "ScreenshotToolbarRetranslate";
        _shotEditRecord.Name = "ScreenshotToolbarEditRecord";
        _shotResetSplit.Name = "ScreenshotToolbarResetSplit"; _shotResetSplit.AccessibleName = "恢复左右等分";
        _shotOverflow = ShotButton("···", "", 26, ShowShotOverflowMenu);
        _shotOverflow.Name = "ScreenshotToolbarOverflow"; _shotOverflow.AccessibleName = "窗口收窄后收起的操作";
        _shotCapture = ShotButton("开始截图", "camera", 84, BeginCapture); _shotCapture.Primary = true;
        _shotImport = ShotButton("导入图片", "image", 74, ImportShotImage);
        _shotExport = ShotButton("导出", "export", 90, ShowShotExportMenu);
        _shotSwap = ShotButton("交换左右", "", 92, SwapShotPanes);
        _shotCapture.Name = "ScreenshotToolbarCapture"; _shotImport.Name = "ScreenshotToolbarImport";
        _shotExport.Name = "ScreenshotToolbarExport"; _shotSwap.Name = "ScreenshotToolbarSwap";
        _shotToolbar.Controls.AddRange([_shotHeading, _shotProfileLabel, _shotProfile, _shotEditProfile, _shotRetranslate, _shotEditRecord, _shotResetSplit, _shotOverflow, _shotSwap, _shotExport, _shotImport, _shotCapture]);
        _fusionTips.SetToolTip(_shotProfile, UiLanguage.Text("选择截图使用的翻译方案"));
        _fusionTips.SetToolTip(_shotEditProfile, UiLanguage.Text("编辑当前截图翻译方案"));
        _fusionTips.SetToolTip(_shotResetSplit, UiLanguage.Text("将左右两栏恢复为相同宽度"));
        _shotToolbar.Paint += (_, e) => { using var pen = new Pen(ScreenshotRebuildStyle.Palette.Border); e.Graphics.DrawLine(pen, 0, _shotToolbar.Height - 1, _shotToolbar.Width, _shotToolbar.Height - 1); };
        _homeOcrStatus = new Label(); _homeApiStatus = new Label();

        _shotHistoryHeading = ShotLabel("翻译记录", 9f, true);
        _shotCount.Font = ShotFont(7.5f);
        _shotCollapse = ShotButton("", "back", 26, () => SetShotHistoryVisible(false));
        _shotCollapse.AccessibleName = "收起历史";
        _shotSearch.Font = ShotFont();
        _shotSearchBorder = new Panel();
        _shotSearchBorder.Controls.Add(_shotSearch);
        _shotSearchBorder.Paint += (_, e) =>
        {
            var p = ScreenshotRebuildStyle.Palette;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = WorkspaceDrawing.Round(Rectangle.Inflate(_shotSearchBorder.ClientRectangle, -1, -1), ShotScale(5));
            using var line = new Pen(p.Border); e.Graphics.DrawPath(line, path);
        };
        _shotDate.Font = ShotFont();
        _shotDate.Items.AddRange(["全部日期", "今天", "近七天"]); _shotDate.SelectedIndex = 0;
        _shotHistoryFooter = new Panel();
        _shotHistoryNote = ShotLabel("当前记录同时应用于左右两栏", 7.5f);
        _shotHistoryNote.TextAlign = ContentAlignment.MiddleCenter;
        _shotPrevious = ShotButton("", "back", 24, () => { if (_shotPageIndex > 0) { _shotPageIndex--; RefreshHistory(); } });
        _shotNext = ShotButton("", "next", 24, () => { _shotPageIndex++; RefreshHistory(); });
        _shotPrevious.AccessibleName = "上一页"; _shotNext.AccessibleName = "下一页";
        _shotPageNumber.Font = ShotFont(7.5f);
        _shotHistoryFooter.Controls.AddRange([_shotPrevious, _shotNext, _shotPageNumber, _shotHistoryNote]);
        _shotExpanded.Controls.AddRange([_shotHistoryHeading, _shotCount, _shotCollapse, _shotSearchBorder, _shotDate, _shotHistory, _shotHistoryFooter]);
        _shotHistory.Dock = DockStyle.None;
        _shotHistoryHost.Controls.Add(_shotExpanded); _shotHistoryHost.Controls.Add(_shotRail);
        _shotRail.Font = ShotFont(7.5f); _shotRail.ExpandRequested += (_, _) => SetShotHistoryVisible(true);
        _shotHistoryHost.Paint += (_, e) => { using var line = new Pen(ScreenshotRebuildStyle.Palette.Border); e.Graphics.DrawLine(line, _shotHistoryHost.Width - 1, 0, _shotHistoryHost.Width - 1, _shotHistoryHost.Height); };
        _shotSplit.Panel1.Controls.Add(_shotLeft); _shotSplit.Panel2.Controls.Add(_shotRight);
        _shotContent.Controls.Add(_shotSplit);
        _shotBody.Controls.AddRange([_shotHistoryHost, _shotContent]);
        _shotPage.Controls.AddRange([_shotBody, _shotToolbar]); page.Controls.Add(_shotPage);

        foreach (var pane in new[] { _shotLeft, _shotRight })
        {
            pane.Activated += (_, _) => _shotActive = pane;
            pane.MaximizeRequested += (_, _) => MaximizeShotPane(pane == _shotLeft ? 0 : 1);
            pane.CopyRequested += (_, _) => CopyShotPane(pane);
            pane.ExportRequested += (_, _) => ExportShotPane(pane);
            pane.EditRequested += (_, _) => OpenShotEditor();
            pane.StateChanged += (_, _) => ScheduleShotLayout();
            pane.ViewChanged += (_, _) => UpdateGalleryChrome();
        }
        _shotHistory.SelectionChanged += (_, _) => _shotSelectionTask = LoadShotSelectionAsync();
        _shotHistory.DoubleClick += (_, _) => OpenShotEditor();
        _shotRecordMenu = new LocalizedContextMenuStrip();
        _shotRecordMenu.Items.Add("重新翻译", null, (_, _) => RetranslateShot());
        _shotRecordMenu.Items.Add("详细编辑", null, (_, _) => OpenShotEditor());
        _shotHistory.ContextMenuStrip = _shotRecordMenu;
        _shotSearch.TextChanged += (_, _) => { _shotPageIndex = 0; _shotSearchTimer.Stop(); _shotSearchTimer.Start(); };
        _shotDate.SelectedIndexChanged += (_, _) => { _shotPageIndex = 0; RefreshHistory(); };
        _shotSearchTimer.Tick += (_, _) => { _shotSearchTimer.Stop(); RefreshHistory(); };
        _shotLayoutTimer.Tick += (_, _) => { _shotLayoutTimer.Stop(); SaveShotLayout(); };
        _shotSplit.SplitChanged += (_, _) => ScheduleShotLayout();
        _session.HistoryChanged += ShotHistoryChanged;
        _shotPage.SizeChanged += (_, _) => ArrangeShotWorkspace();
        DpiChanged += (_, _) => ArrangeShotWorkspace();
        FormClosed += (_, _) =>
        {
            _shotLayoutTimer.Stop(); SaveShotLayout(closing: true); _shotClosed = true; _shotEpoch++;
            _session.HistoryChanged -= ShotHistoryChanged;
            _shotSearchTimer.Dispose(); _shotLayoutTimer.Dispose(); ReleaseShotImages();
            _shotOverflowMenu?.Dispose(); _shotExportMenu?.Dispose(); _shotRecordMenu?.Dispose();
        };
        Disposed += (_, _) => { foreach (var font in _shotFonts) font.Dispose(); _shotFonts.Clear(); };
        _shotApplying = true; _shotSaved = ScreenshotWorkspaceLayoutStore.Load(ShotLayoutPath);
        _shotLastSelected = _shotSaved.SelectedRecord;
        _shotLeft.ApplyViewState(_shotSaved.Left); _shotRight.ApplyViewState(_shotSaved.Right);
        _shotSplit.SplitPercent = _shotSaved.SplitPercent;
        SetShotHistoryVisible(_shotSaved.HistoryExpanded); _shotActive = _shotLeft;
        _shotApplying = false;
        ArrangeShotWorkspace(); UpdateGalleryChrome();
    }

    private void ArrangeShotWorkspace()
    {
        if (_shotArranging || _shotPage is null || _shotToolbar is null || _shotBody is null || _shotHistoryHost is null || _shotContent is null) return;
        _shotArranging = true;
        try
        {
            int S(int n) => ShotScale(n);
            int width = _shotPage.ClientSize.Width, height = _shotPage.ClientSize.Height, toolbarHeight = S(46);
            _shotToolbar.SetBounds(0, 0, width, toolbarHeight);
            _shotBody.SetBounds(0, toolbarHeight, width, Math.Max(0, height - toolbarHeight));
            int historyWidth = S(_shotHistoryVisible ? 262 : 62);
            _shotHistoryHost.SetBounds(0, 0, historyWidth, _shotBody.Height);
            _shotContent.SetBounds(historyWidth, 0, Math.Max(0, width - historyWidth), _shotBody.Height);
            _shotContent.Padding = new(S(12), S(12), S(16), S(18));
            var toolbarLayout = (width, DeviceDpi, _shotProfile.Font, _shotCapture!.Font, UiLanguage.Current);
            if (_shotToolbarLayout != toolbarLayout)
            {
                int right = width - S(17);
                foreach (var button in new[] { _shotCapture!, _shotImport!, _shotExport!, _shotSwap! })
                {
                    button.RefreshMetrics(); button.Location = new(right - button.Width, (toolbarHeight - button.Height) / 2); right = button.Left - S(8);
                }
                int headingWidth=Math.Max(S(82),TextRenderer.MeasureText(_shotHeading!.Text,_shotHeading.Font).Width+S(8));
                _shotHeading.SetBounds(S(18), 0, headingWidth, toolbarHeight);
                ArrangeShotToolbarActions(_shotHeading.Right+S(12), right, toolbarHeight);
                _shotToolbarLayout = toolbarLayout;
            }
            // The expanded history remains alive while collapsed, but its
            // controls do not need resizing behind the narrow history rail.
            if (!_shotHistoryVisible) return;
            int w = historyWidth;
            var historyLayout = (w, DeviceDpi, _shotSearch.Font, UiLanguage.Current);
            if (_shotHistoryFixedLayout != historyLayout)
            {
                _shotHistoryHeading!.SetBounds(S(14), S(13), S(91), S(25));
                _shotCount.SetBounds(S(108), S(13), S(72), S(25));
                _shotCollapse!.RefreshMetrics(); _shotCollapse.Location = new(w - S(40), S(13));
                _shotSearchBorder!.SetBounds(S(14), S(51), w - S(28), S(33));
                _shotSearch.SetBounds(S(9), Math.Max(S(5), (_shotSearchBorder.Height - _shotSearch.PreferredHeight) / 2), _shotSearchBorder.Width - S(18), _shotSearch.PreferredHeight);
                _shotDate.SetBounds(S(14), S(92), S(97), S(27));
                _shotPrevious!.SetBounds(S(70), 0, S(24), S(25)); _shotPageNumber.SetBounds(S(96), 0, S(64), S(25)); _shotNext!.SetBounds(S(162), 0, S(24), S(25));
                _shotHistoryFixedLayout = historyLayout;
            }
            int footerHeight = _shotPrevious!.Visible ? S(64) : S(35);
            _shotHistory.SetBounds(S(12), S(132), w - S(24), Math.Max(0, _shotHistoryHost.Height - S(132) - footerHeight));
            _shotHistoryFooter!.SetBounds(0, _shotHistoryHost.Height - footerHeight, w, footerHeight);
            _shotHistoryNote!.SetBounds(S(8), footerHeight - S(30), w - S(16), S(25));
        }
        finally { _shotArranging = false; }
    }

    private void ArrangeShotToolbarActions(int left, int right, int height)
    {
        int S(int n) => ShotScale(n);
        var actions = new[] { _shotEditProfile!, _shotRetranslate!, _shotEditRecord!, _shotResetSplit! };
        foreach (var action in actions) action.RefreshMetrics();
        _shotOverflow!.RefreshMetrics();
        var visibleActions = actions.ToList();
        bool showProfileLabel = true, showOverflow = false;
        _shotProfileLabel!.Width = Math.Max(S(48),TextRenderer.MeasureText(_shotProfileLabel.Text,_shotProfileLabel.Font).Width);
        if (_shotProfile is GamePlanBox plan) plan.RefreshMetrics();
        int room = Math.Max(0, right - left), gap = S(8), minimumProfileWidth = S(100);
        int Required(int profileWidth)
        {
            var widths = new List<int> { profileWidth };
            if (showProfileLabel) widths.Add(_shotProfileLabel.Width);
            widths.AddRange(visibleActions.Select(x => x.Width));
            if (showOverflow) widths.Add(_shotOverflow.Width);
            return widths.Sum() + gap * (widths.Count - 1);
        }
        // Shrink the selector first. Only narrow windows use overflow, keeping
        // retranslation visible longer than the less frequent editing actions.
        foreach (var action in new[] { _shotResetSplit!, _shotEditRecord!, _shotEditProfile!, _shotRetranslate! })
        {
            if (Required(minimumProfileWidth) <= room) break;
            visibleActions.Remove(action); showOverflow = true;
        }
        if (Required(minimumProfileWidth) > room) showProfileLabel = false;
        int profileWidth = Math.Min(S(180), Math.Max(S(24), room - Required(0)));
        _shotProfileLabel.Visible = showProfileLabel; _shotOverflow.Visible = showOverflow;
        foreach (var action in actions) action.Visible = visibleActions.Contains(action);
        var controls = new List<Control>();
        if (showProfileLabel) controls.Add(_shotProfileLabel);
        controls.Add(_shotProfile); controls.AddRange(visibleActions);
        if (showOverflow) controls.Add(_shotOverflow);
        _shotProfile.Width = profileWidth;
        foreach (var control in controls)
        {
            int controlHeight = control == _shotProfileLabel ? height : control.Height;
            control.SetBounds(left, (height - controlHeight) / 2, control.Width, controlHeight);
            left = control.Right + gap;
        }
    }

    private void SetShotHistoryVisible(bool visible)
    {
        _shotHistoryVisible = visible;
        if (_shotExpanded is not null) _shotExpanded.Visible = visible;
        _shotRail.Visible = !visible;
        ArrangeShotWorkspace(); ScheduleShotLayout();
    }
    private void SwapShotPanes()
    {
        var left = _shotLeft.CaptureViewState(); var right = _shotRight.CaptureViewState();
        _shotLeft.ApplyViewState(right); _shotRight.ApplyViewState(left);
        ScheduleShotLayout(); UpdateGalleryChrome();
    }
    private void MaximizeShotPane(int index)
    {
        _shotSplit.MaximizedPane = _shotSplit.MaximizedPane == index ? -1 : index;
        _shotLeft.Maximized = _shotSplit.MaximizedPane == 0; _shotRight.Maximized = _shotSplit.MaximizedPane == 1;
        if (_shotSplit.MaximizedPane >= 0) _shotActive = index == 0 ? _shotLeft : _shotRight;
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_currentMainPage == "主页" && keyData == Keys.Escape)
        {
            if (_shotSplit.MaximizedPane >= 0) { MaximizeShotPane(_shotSplit.MaximizedPane); return true; }
            if (_shotHistoryVisible) { SetShotHistoryVisible(false); return true; }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    private void ShowShotOverflowMenu()
    {
        _shotOverflowMenu?.Dispose(); _shotOverflowMenu = new LocalizedContextMenuStrip();
        void Add(ScreenshotRebuildButton button, string title, Action action)
        {
            if (button.Visible) return;
            _shotOverflowMenu.Items.Add(title, null, (_, _) => action()).Enabled = button.Enabled;
        }
        Add(_shotEditProfile!, "编辑当前方案", () => EditModeProfile(false));
        Add(_shotRetranslate!, "重新翻译", RetranslateShot);
        Add(_shotEditRecord!, "详细编辑", OpenShotEditor);
        Add(_shotResetSplit!, "恢复左右等分", () => _shotSplit.SplitPercent = 50);
        if (_shotOverflowMenu.Items.Count > 0) _shotOverflowMenu.Show(_shotOverflow!, new Point(0, _shotOverflow!.Height));
    }
    private void ShowShotExportMenu()
    {
        _shotExportMenu?.Dispose(); _shotExportMenu = new LocalizedContextMenuStrip();
        foreach (var pair in new[] { (_shotLeft, "左侧"), (_shotRight, "右侧") })
        {
            var pane = pair.Item1;
            var entry = _shotExportMenu.Items.Add("导出" + pair.Item2 + " · " + ScreenshotRebuildPane.ViewTitle(pane.View), null, (_, _) => ExportShotPane(pane));
            entry.Enabled = pane.IsImageView ? pane.CurrentBitmap is not null : pane.CurrentText.Length > 0;
        }
        _shotExportMenu.Items.Add(new ToolStripSeparator());
        var active = _shotActive ?? _shotLeft;
        var copy = _shotExportMenu.Items.Add("复制" + (active == _shotLeft ? "左侧" : "右侧") + " · " + ScreenshotRebuildPane.ViewTitle(active.View), null, (_, _) => CopyShotPane(active));
        copy.Enabled = active.IsImageView ? active.CurrentBitmap is not null : active.CurrentText.Length > 0;
        _shotExportMenu.Show(_shotExport!, new Point(0, _shotExport!.Height));
    }
    private void ImportShotImage()
    {
        using var dialog = new OpenFileDialog { Filter = UiLanguage.Text("图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp"), Title = UiLanguage.Text("打开需要翻译的图片") };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { using var image = new Bitmap(dialog.FileName); CreateAndShowPreview(new CaptureResult(new Bitmap(image), PreviewMode.OcrAndTranslate)); }
        catch (Exception ex) { ShowFusionError(ex); }
    }
    private void OpenShotEditor()
    {
        try
        {
            if (_shotSelected is not { } item) return;
            if (item.OriginalPng.Length == 0) new SavedHistoryResultForm(item).Show(this);
            else OpenHistoryItem(item);
        }
        catch (Exception ex) { ShowFusionError(ex); }
    }
    private void RetranslateShot()
    {
        try
        {
            if (_shotSelected is { OriginalPng.Length: > 0 } item) OpenHistoryItem(item).TranslateFromWorkspace();
            else _statusLabel.Text = "此记录缺少原图，无法重新翻译。";
        }
        catch (Exception ex) { ShowFusionError(ex); }
    }
    private void CopyShotPane(ScreenshotRebuildPane pane)
    {
        try
        {
            if (pane.IsImageView) { if (pane.CurrentBitmap is not { } image) return; ClipboardHelper.SetImage(image); }
            else { var text = pane.SelectedTextOrAll; if (text.Length == 0) return; ClipboardHelper.SetText(text); }
            _statusLabel.Text = "已复制。";
        }
        catch (Exception ex) { ShowFusionError(ex); }
    }
    private void ExportShotPane(ScreenshotRebuildPane pane)
    {
        if (_shotSelected is not { } item || (pane.IsImageView ? pane.CurrentBitmap is null : pane.CurrentText.Length == 0)) return;
        try
        {
            bool image = pane.IsImageView;
            using var snapshot = image ? new Bitmap(pane.CurrentBitmap!) : null;
            var text = pane.CurrentText;
            using var dialog = new SaveFileDialog { Filter = UiLanguage.Text(image ? "PNG 图片|*.png" : "文字文件|*.txt"), FileName = ScreenshotRebuildPane.ViewTitle(pane.View) + "-" + item.Timestamp.ToString("yyyyMMdd-HHmmss") + (image ? ".png" : ".txt") };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            if (image) snapshot!.Save(dialog.FileName, ImageFormat.Png); else File.WriteAllText(dialog.FileName, text, new UTF8Encoding(false));
            _statusLabel.Text = "已导出。";
        }
        catch (Exception ex) { ShowFusionError(ex); }
    }

    private void ShotHistoryChanged()
    {
        Interlocked.Increment(ref _shotHistoryVersion);
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke((Action)(() => { if (_currentMainPage == "主页") { _shotPageIndex = 0; RefreshHistory(); } })); } catch (InvalidOperationException) { }
    }
    private async Task RefreshGalleryAsync()
    {
        if (IsDisposed || _shotClosed) return;
        if (_shotLoading) { _shotRefreshPending = true; return; }
        _shotLoading = true;
        try
        {
            do
            {
                _shotRefreshPending = false;
                if (_shotSnapshot is null || _shotLoadedVersion != Volatile.Read(ref _shotHistoryVersion))
                {
                    int version = Volatile.Read(ref _shotHistoryVersion);
                    var snapshot = await Task.Run(_session.SnapshotHistory);
                    if (IsDisposed || _shotClosed) return;
                    _shotSnapshot = snapshot; _shotLoadedVersion = version;
                    if (version != Volatile.Read(ref _shotHistoryVersion)) _shotRefreshPending = true;
                }
                IEnumerable<SessionHistoryItem> found = _shotSnapshot;
                string query = _shotSearch.Text.Trim();
                if (query.Length > 0) found = found.Where(x => (x.SourceText + " " + x.SourceSummary + " " + x.TranslationText + " " + x.TranslationSummary).Contains(query, StringComparison.OrdinalIgnoreCase));
                if (_shotDate.SelectedIndex == 1) found = found.Where(x => x.Timestamp.LocalDateTime.Date == DateTime.Today);
                if (_shotDate.SelectedIndex == 2) found = found.Where(x => x.Timestamp >= DateTimeOffset.Now.AddDays(-7));
                var records = found.OrderByDescending(x => x.Timestamp).ToArray();
                int pages = Math.Max(1, (records.Length + 29) / 30);
                int restored = _shotRestore ? Array.FindIndex(records, x => ScreenshotRebuildHistory.Key(x) == _shotSaved.SelectedRecord) : -1;
                if (restored >= 0) _shotPageIndex = restored / 30;
                _shotPageIndex = Math.Clamp(_shotPageIndex, 0, pages - 1);
                _shotHistory.EmptyMessage = query.Length > 0 || _shotDate.SelectedIndex > 0 ? "没有匹配的记录\n试试其他关键词或日期" : "还没有截图记录\n开始截图或导入图片";
                _shotHistory.SetRecords(records.Skip(_shotPageIndex * 30).Take(30).ToArray());
                _historyThumbnailSide = ResolveHistoryThumbnailSide();
                _historyThumbnailDecodeCount = _shotHistory.DecodeCount;
                _historyUsage.Text = $"{records.Length} 条记录";
                _shotCount.Text = $"{records.Length} 条";
                _shotRail.RecordCount = records.Length;
                _shotPageNumber.Text = $"{_shotPageIndex + 1} / {pages}";
                _shotPrevious!.Visible = _shotNext!.Visible = _shotPageNumber.Visible = pages > 1;
                _shotPrevious.Enabled = _shotPageIndex > 0; _shotNext.Enabled = _shotPageIndex + 1 < pages;
                ArrangeShotWorkspace();
                int selected = restored >= 0 ? restored % 30 : _shotHistory.SelectedIndex < 0 && _shotHistory.Count > 0 ? 0 : _shotHistory.SelectedIndex;
                if (selected != _shotHistory.SelectedIndex) _shotHistory.SelectedIndex = selected;
                else _shotSelectionTask = LoadShotSelectionAsync();
                await _shotSelectionTask; _shotRestore = false;
            } while (_shotRefreshPending && !IsDisposed && !_shotClosed);
        }
        catch (Exception ex) { if (!IsDisposed && !_shotClosed) ShowFusionError(ex); }
        finally { _shotLoading = false; }
    }
    private void ReleaseShotImages()
    {
        _shotLeft.ClearContent(); _shotRight.ClearContent();
        _shotOriginal?.Dispose(); _shotTranslated?.Dispose(); _shotOriginal = _shotTranslated = null;
    }
    private async Task LoadShotSelectionAsync()
    {
        var item = _shotHistory.SelectedRecord;
        // Filtering, resizing, or revisiting this page does not change an
        // immutable history item. Retain its decoded images and current view.
        if (!_shotRestore && item is not null && ReferenceEquals(item, _shotSelected)
            && !_shotImagesLoading) return;
        bool same = item is not null && _shotSelected is not null && ScreenshotRebuildHistory.Key(item) == ScreenshotRebuildHistory.Key(_shotSelected);
        var left = _shotRestore ? _shotSaved.Left : _shotLeft.CaptureViewState();
        var right = _shotRestore ? _shotSaved.Right : _shotRight.CaptureViewState();
        bool preserve = same || _shotRestore;
        _shotPendingLeft = preserve ? left : new(left.View, left.FontSize);
        _shotPendingRight = preserve ? right : new(right.View, right.FontSize);
        int epoch = ++_shotEpoch; _shotSelected = item;
        if (item is not null) _shotLastSelected = ScreenshotRebuildHistory.Key(item);
        _shotApplying = true; ReleaseShotImages(); _shotApplying = false;
        _shotImagesLoading = item is not null;
        _shotRail.Invalidate();
        UpdateGalleryChrome();
        if (item is null) { _shotPendingLeft = _shotPendingRight = null; ScheduleShotLayout(); return; }
        static string Full(string? text, string? summary) => string.IsNullOrWhiteSpace(text) ? summary ?? "" : text;
        string sourceText = Full(item.SourceText, item.SourceSummary), translatedText = Full(item.TranslationText, item.TranslationSummary);
        foreach (var pane in new[] { _shotLeft, _shotRight })
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
                try { if (item.OriginalPng.Length > 0) { Interlocked.Increment(ref _shotSelectionDecodes); source = DecodeOwnedBitmap(item.OriginalPng, out _); } }
                catch (Exception ex) { errors.Add("原图：" + SafeDiagnosticOutput.ExceptionSummary(ex)); }
                try { if (item.TranslatedImage is { Length: > 0 }) { Interlocked.Increment(ref _shotSelectionDecodes); result = DecodeOwnedBitmap(item.TranslatedImage, out _); } }
                catch (Exception ex) { errors.Add("译图：" + SafeDiagnosticOutput.ExceptionSummary(ex)); }
            });
            if (IsDisposed || _shotClosed || epoch != _shotEpoch) return;
            left = left with { View = _shotLeft.View, FontSize = _shotLeft.FontSize };
            right = right with { View = _shotRight.View, FontSize = _shotRight.FontSize };
            _shotOriginal = source; source = null; _shotTranslated = result; result = null;
            _shotApplying = true;
            foreach (var pane in new[] { _shotLeft, _shotRight }) pane.SetContent(_shotOriginal, _shotTranslated, sourceText, translatedText, !preserve);
            if (preserve) { _shotLeft.ApplyViewState(left); _shotRight.ApplyViewState(right); }
            _shotApplying = false;
            if (errors.Count > 0) _statusLabel.Text = "部分图片不可用，文字保留";
            foreach (var error in errors) AppLog.Write("screenshot-history", error);
        }
        finally
        {
            source?.Dispose(); result?.Dispose();
            if (epoch == _shotEpoch)
            {
                _shotImagesLoading = false;
                _shotPendingLeft = _shotPendingRight = null;
                if (!IsDisposed && !_shotClosed) { UpdateGalleryChrome(); ScheduleShotLayout(); }
            }
        }
    }
    private void ScheduleShotLayout()
    {
        if (_shotApplying || _shotClosed || IsDisposed || Disposing) return;
        // Persist after the interaction settles, not during every split drag.
        _shotLayoutTimer.Stop(); _shotLayoutTimer.Start();
    }
    private void SaveShotLayout(bool closing = false)
    {
        if (_shotApplying || (_shotImagesLoading && !closing)) return;
        try
        {
            // Closing during an image load still saves the newest layout. The
            // pending view avoids persisting the temporary blank canvas state.
            var left = _shotImagesLoading && _shotPendingLeft is { } pendingLeft
                ? pendingLeft with { View = _shotLeft.View, FontSize = _shotLeft.FontSize } : _shotLeft.CaptureViewState();
            var right = _shotImagesLoading && _shotPendingRight is { } pendingRight
                ? pendingRight with { View = _shotRight.View, FontSize = _shotRight.FontSize } : _shotRight.CaptureViewState();
            var layout = new ScreenshotWorkspaceLayout { HistoryExpanded = _shotHistoryVisible, SplitPercent = _shotSplit.SplitPercent, SelectedRecord = _shotLastSelected, Left = left, Right = right }.Normalize();
            if (layout == _shotSaved && File.Exists(ShotLayoutPath)) return;
            ScreenshotWorkspaceLayoutStore.Save(ShotLayoutPath, layout); _shotSaved = layout; _shotLayoutWrites++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Write("screenshot-layout", SafeDiagnosticOutput.ExceptionSummary(ex)); }
    }
    private void UpdateGalleryChrome()
    {
        var p = ScreenshotRebuildStyle.Palette;
        if (_shotPage is null) return;
        _shotRetranslate!.Enabled = _shotSelected is { OriginalPng.Length: > 0 };
        _shotEditRecord!.Enabled = _shotSelected is not null;
        // Global theme application can replace local screenshot colors even
        // when the palette itself is unchanged. Detect that before skipping.
        if (_shotAppliedPalette == p && _shotPage.BackColor == p.Main
            && _shotToolbar!.BackColor == p.Secondary && _shotCount.ForeColor == p.SecondaryText) return;
        _shotPage.BackColor = _shotBody!.BackColor = _shotContent!.BackColor = _shotSplit.BackColor = p.Main;
        _shotToolbar!.BackColor = p.Secondary;
        var history = p.Main.GetBrightness() > .5f ? Color.FromArgb(237,243,251) : p.Secondary;
        _shotHistoryHost!.BackColor = _shotExpanded!.BackColor = _shotHistory.BackColor = _shotRail.BackColor = _shotHistoryFooter!.BackColor = history;
        _shotSearchBorder!.BackColor = _shotSearch.BackColor = p.Secondary; _shotSearch.ForeColor = p.Text;
        _shotHeading!.ForeColor = _shotProfileLabel!.ForeColor = _shotHistoryHeading!.ForeColor = p.Text;
        _shotCount.ForeColor = _shotPageNumber.ForeColor = _shotHistoryNote!.ForeColor = p.SecondaryText;
        _shotDate.BackColor = p.Secondary; _shotDate.ForeColor = p.Text;
        _shotProfile.BackColor = p.Secondary; _shotProfile.ForeColor = p.Text;
        ScreenshotNativeScrollbarTheme.Apply(_shotHistory);
        _shotLeft.ApplyTheme(); _shotRight.ApplyTheme();
        _shotAppliedPalette = p; _shotFullThemeApplies++;
        _shotPage.Invalidate(true);
    }
    internal static string GalleryItemTitle(SessionHistoryItem item)
    {
        // Keep a record's heading separate from the paragraphs shown in its text pane.
        var content = new[] { item.TranslationText, item.TranslationSummary, item.SourceText, item.SourceSummary }
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
        var text = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return text.Length == 0 ? "截图翻译" : text.Length > 26 ? text[..26] + "…" : text;
    }
    internal async Task RefreshScreenshotWorkspaceForSmoke() { while (_shotLoading) await Task.Delay(10); await RefreshGalleryAsync(); await _shotSelectionTask; }
    internal async Task SelectScreenshotRecordForSmoke(int index) { if (_shotHistory.SelectedIndex != index) _shotHistory.SelectedIndex = index; else _shotSelectionTask = LoadShotSelectionAsync(); await _shotSelectionTask; }
    internal void SetScreenshotModesForSmoke(int left, int right) { _shotLeft.View = (ScreenshotPaneView)left; _shotRight.View = (ScreenshotPaneView)right; }
    internal void SetScreenshotSearchForSmoke(string query, int dateIndex = 0) { _shotSearch.Text = query; _shotSearchTimer.Stop(); _shotPageIndex = 0; _shotDate.SelectedIndex = dateIndex; }
    internal void ToggleScreenshotHistoryForSmoke() => SetShotHistoryVisible(!_shotHistoryVisible);
    internal void SwapScreenshotPanesForSmoke() => SwapShotPanes();
    internal void MaximizeScreenshotPaneForSmoke(int index) => MaximizeShotPane(index);
    internal void SetScreenshotSplitForSmoke(double ratio) => _shotSplit.SplitPercent = ratio;
    internal ScreenshotRebuildPane ScreenshotPaneForSmoke(int index) => index == 0 ? _shotLeft : _shotRight;
    internal void PrepareScreenshotMoreMenuForSmoke() => ArrangeShotWorkspace();
    internal IReadOnlyDictionary<string, Rectangle> ScreenshotToolbarBoundsForSmoke() =>
        _shotToolbar!.Controls.Cast<Control>().Where(control => control.Visible)
            .ToDictionary(control => control.Name, control => control.Bounds);
    internal ScreenshotWorkspacePerformanceDiagnostics ScreenshotWorkspacePerformanceForSmoke() =>
        new(_shotLayoutWrites, Volatile.Read(ref _shotSelectionDecodes), _shotFullThemeApplies);
    internal ScreenshotWorkspaceDiagnostics ScreenshotWorkspaceStateForSmoke() => new(
        (int)_shotLeft.View, (int)_shotRight.View, _shotHistoryVisible, _shotSplit.MaximizedPane,
        _shotSelected is null ? "" : ScreenshotRebuildHistory.Key(_shotSelected), _shotHistory.Count,
        _shotSplit.Panel1.Bounds, _shotSplit.Panel2.Bounds, _shotLeft.Visible, _shotRight.Visible, _shotSplit.SplitPercent,
        _shotLoading || _shotImagesLoading, _shotLeft.CaptureViewState(), _shotRight.CaptureViewState(),
        _shotHistoryHost?.Bounds ?? Rectangle.Empty, _shotToolbar?.Height ?? 0, DeviceDpi);
}

