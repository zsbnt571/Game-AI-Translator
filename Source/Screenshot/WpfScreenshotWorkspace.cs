using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;

namespace ScreenshotTranslationUiTester;

// Layout and commands are owned by WPF. The session owns selection, images and
// persistence, without constructing a second, hidden screenshot control tree.
internal sealed class WpfScreenshotWorkspace : WC.Grid, IDisposable
{
    private readonly ScreenshotWorkspaceController model;
    private readonly ScreenshotWorkspaceCommands commands;
    private readonly WpfViewRefresh refreshQueue;
    private readonly WC.Canvas toolbar = new() { ClipToBounds = true };
    private readonly WC.Grid historyHeader = new() { Margin = new(14, 13, 14, 0), ClipToBounds = true };
    private readonly WC.Grid history = new(), panes = new();
    private readonly WC.Border historyBorder = new() { ClipToBounds = true }, toolbarBorder = new();
    private readonly WC.StackPanel rail = new();
    private readonly WC.Button expand, collapse, previous, next;
    private readonly WC.TextBlock records = new(), count = new(), pageNumber = new(), status = new(), heading = new(), profileLabel = new(), historyTitle = new(), historyNote = new(), emptyHistory = new();
    private readonly WC.ComboBox profile = new() { MinWidth = 24, MinHeight = 36, DisplayMemberPath = nameof(WorkspaceProfileChoice.Label), SelectedValuePath = nameof(WorkspaceProfileChoice.Id) };
    private readonly WC.ComboBox date = new() { Width = 97, Height = 27, MinHeight = 0, HorizontalAlignment = W.HorizontalAlignment.Left, Padding = new(6, 2, 6, 2) };
    private readonly WC.TextBox search = new() { Height = 33, Margin = new(0, 13, 0, 8), Padding = new(9, 4, 9, 4), MinWidth = 0 };
    private readonly WC.GridSplitter splitter;
    private readonly WpfScreenshotPane left, right;
    private readonly WpfScreenshotHistory historyList;
    private readonly List<(WC.Button Button, string Text, string? Icon, bool Primary)> actions = [];
    private readonly Dictionary<W.FrameworkElement, double> widths = [];
    private readonly WC.Button capture, import, export, swap, editProfile, retranslate, editRecord, reset, overflow;
    private WorkspaceProfileChoice[] profileChoices = [];
    private ApplicationLanguage? language;
    private bool refreshing, splitDragging;
    private double arrangedWidth = -1;

    internal WpfScreenshotWorkspace(ScreenshotWorkspaceController model, ScreenshotWorkspaceCommands commands)
    {
        this.model = model; this.commands = commands; refreshQueue = new(Dispatcher, Refresh); ClipToBounds = true;
        RowDefinitions.Add(new() { Height = new(56) }); RowDefinitions.Add(new() { Height = new(1, W.GridUnitType.Star) }); RowDefinitions.Add(new() { Height = new(22) });
        toolbarBorder.Child = toolbar; toolbarBorder.BorderThickness = new(0, 0, 0, 1); Children.Add(toolbarBorder);
        var body = new WC.Grid(); body.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto }); body.ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) }); WC.Grid.SetRow(body, 1); Children.Add(body);
        historyBorder.BorderThickness = new(0, 0, 1, 0); body.Children.Add(historyBorder);
        var historyContainer = new WC.Grid(); historyContainer.Children.Add(history); historyContainer.Children.Add(rail); historyBorder.Child = historyContainer;
        history.RowDefinitions.Add(new() { Height = new(132) }); history.RowDefinitions.Add(new() { Height = new(1, W.GridUnitType.Star) }); history.RowDefinitions.Add(new() { Height = W.GridLength.Auto });
        history.Children.Add(historyHeader);
        historyList = new(model.History, model.Edit, model.Retranslate, () => model.CanRetranslate) { Margin = new(12, 0, 12, 0) };
        var historyItems = new WC.Grid(); historyItems.Children.Add(historyList); emptyHistory.Margin = new(20); emptyHistory.TextAlignment = W.TextAlignment.Center; emptyHistory.TextWrapping = W.TextWrapping.Wrap; emptyHistory.VerticalAlignment = W.VerticalAlignment.Center; emptyHistory.IsHitTestVisible = false; historyItems.Children.Add(emptyHistory);
        WC.Grid.SetRow(historyItems, 1); history.Children.Add(historyItems);
        expand = WpfThemeResources.Button("", "sidebar-show", () => model.SetHistoryVisible(true)); expand.MinWidth = expand.MinHeight = 0; expand.Width = expand.Height = 44; expand.Padding = new(8); expand.Margin = new(13, 14, 13, 0);
        rail.Children.Add(expand); records.HorizontalAlignment = W.HorizontalAlignment.Center; records.Margin = new(0, 28, 0, 0); rail.Children.Add(records);
        panes.Margin = new(12, 12, 16, 18); panes.ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) }); panes.ColumnDefinitions.Add(new() { Width = new(14) }); panes.ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) }); WC.Grid.SetColumn(panes, 1); body.Children.Add(panes);
        left = new(model.Left); right = new(model.Right); panes.Children.Add(left); WC.Grid.SetColumn(right, 2); panes.Children.Add(right);
        splitter = new() { Width = 14, HorizontalAlignment = W.HorizontalAlignment.Stretch, VerticalAlignment = W.VerticalAlignment.Stretch, ResizeDirection = WC.GridResizeDirection.Columns, ResizeBehavior = WC.GridResizeBehavior.PreviousAndNext, Background = WM.Brushes.Transparent, ShowsPreview = false };
        W.Automation.AutomationProperties.SetName(splitter, UiLanguage.Text("调整左右分栏"));
        WpfUiLanguage.Property(splitter,W.Automation.AutomationProperties.NameProperty,"调整左右分栏");
        WC.Grid.SetColumn(splitter, 1); panes.Children.Add(splitter);
        splitter.DragStarted += (_, _) => splitDragging = true;
        splitter.DragCompleted += (_, e) =>
        {
            splitDragging = false;
            // GridSplitter updates the column definitions before the next layout.
            // ActualWidth still describes the previous frame here.
            if (!e.Canceled)
            {
                var a = panes.ColumnDefinitions[0].Width; var b = panes.ColumnDefinitions[2].Width;
                if (a.IsStar && b.IsStar) model.SplitPercent = a.Value * 100 / Math.Max(1, a.Value + b.Value);
            }
            Refresh();
        };
        splitter.MouseDoubleClick += (_, _) => { model.SplitPercent = 50; Refresh(); };
        splitter.PreviewKeyDown += (_, e) => { if (e.Key == WI.Key.Home) model.SplitPercent = 50; else if (e.Key is WI.Key.Left or WI.Key.Right) model.SplitPercent += e.Key == WI.Key.Left ? -3 : 3; else return; e.Handled = true; Refresh(); };
        Add(heading, "Heading"); heading.FontWeight = W.FontWeights.Bold; heading.FontSize = 15;
        Add(profileLabel, "ProfileLabel"); Add(profile, "Profile"); profile.FontSize = profileLabel.FontSize = 13.333;
        profile.SelectionChanged += (_, _) => { if (!refreshing && profile.SelectedItem is WorkspaceProfileChoice choice) commands.SelectProfile(choice.Id); };
        profile.PreviewMouseWheel += (_, e) => { if (!profile.IsDropDownOpen) e.Handled = true; };
        editProfile = Action("EditProfile", "编辑方案", null, commands.EditProfile);
        retranslate = Action("Retranslate", "重新翻译", null, model.Retranslate);
        editRecord = Action("EditRecord", "详细编辑", null, model.Edit);
        reset = Action("ResetSplit", "左右等分", null, () => model.SplitPercent = 50);
        overflow = Action("Overflow", "···", null, ShowOverflow);
        swap = Action("Swap", "交换左右", null, model.Swap);
        export = Action("Export", "导出", "export", ShowExport);
        import = Action("Import", "导入图片", "image", commands.Import);
        capture = Action("Capture", "开始截图", "camera", commands.Capture, true);
        collapse = WpfThemeResources.Button("", "sidebar-hide", () => model.SetHistoryVisible(false)); collapse.MinHeight = collapse.MinWidth = 0; collapse.Width = collapse.Height = 36; collapse.Padding = new(6); collapse.ToolTip = UiLanguage.Text("收起历史"); W.Automation.AutomationProperties.SetName(collapse, UiLanguage.Text("收起历史")); WpfUiLanguage.Property(collapse,W.Automation.AutomationProperties.NameProperty,"收起历史"); WpfUiLanguage.Property(collapse,W.FrameworkElement.ToolTipProperty,"收起历史");
        BuildHistoryHeader();
        var footer = new WC.StackPanel();
        var paging = new WC.StackPanel { Orientation = WC.Orientation.Horizontal, HorizontalAlignment = W.HorizontalAlignment.Center };
        previous = WpfThemeResources.Button("", "back", () => model.MovePage(-1)); next = WpfThemeResources.Button("", "next", () => model.MovePage(1));
        foreach (var b in new[] { previous, next }) { b.MinWidth = 30; b.MinHeight = 28; b.Padding = new(4); }
        W.Automation.AutomationProperties.SetName(previous, UiLanguage.Text("上一页")); W.Automation.AutomationProperties.SetName(next, UiLanguage.Text("下一页"));
        WpfUiLanguage.Property(previous,W.Automation.AutomationProperties.NameProperty,"上一页");WpfUiLanguage.Property(next,W.Automation.AutomationProperties.NameProperty,"下一页");
        pageNumber.Width = 64; pageNumber.TextAlignment = W.TextAlignment.Center; pageNumber.VerticalAlignment = W.VerticalAlignment.Center; paging.Children.Add(previous); paging.Children.Add(pageNumber); paging.Children.Add(next); footer.Children.Add(paging);
        historyNote.Margin = new(8, 8, 8, 10); historyNote.FontSize = 11; historyNote.TextAlignment = W.TextAlignment.Center; historyNote.TextWrapping = W.TextWrapping.Wrap; footer.Children.Add(historyNote); WC.Grid.SetRow(footer, 2); history.Children.Add(footer);
        status.Margin = new(22, 0, 12, 0); status.FontSize = 11; status.TextTrimming = W.TextTrimming.CharacterEllipsis; WC.Grid.SetRow(status, 2); Children.Add(status);
        model.Changed += refreshQueue.Request; model.StatusChanged += RefreshStatus;
        IsVisibleChanged += (_, _) =>
        {
            refreshQueue.Suspended = !IsVisible;
            if (IsVisible) { refreshQueue.Request(); refreshQueue.Flush(); }
        };
        refreshQueue.Suspended = !IsVisible;
        Refresh();
    }
    private void Add(W.FrameworkElement view, string suffix)
    {
        W.Automation.AutomationProperties.SetAutomationId(view, "ScreenshotToolbar" + suffix); toolbar.Children.Add(view);
        if (view is WC.TextBlock label) { label.TextWrapping = W.TextWrapping.NoWrap; label.TextTrimming = W.TextTrimming.CharacterEllipsis; label.VerticalAlignment = W.VerticalAlignment.Center; }
    }
    private WC.Button Action(string id, string label, string? icon, Action command, bool primary = false)
    {
        var button = WpfThemeResources.Button(label, icon, command, primary); button.MinHeight = 36; button.FontSize = 13.333; button.Padding = new(10, 5, 10, 5);
        Add(button, id); actions.Add((button, label, icon, primary)); return button;
    }
    private void BuildHistoryHeader()
    {
        historyHeader.ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) }); historyHeader.ColumnDefinitions.Add(new() { Width = new(72) }); historyHeader.ColumnDefinitions.Add(new() { Width = new(36) });
        foreach (double height in new[] { 36d, 54d, 27d }) historyHeader.RowDefinitions.Add(new() { Height = new(height) });
        historyTitle.FontWeight = W.FontWeights.SemiBold; historyTitle.VerticalAlignment = count.VerticalAlignment = W.VerticalAlignment.Center; count.FontSize = 11;
        historyHeader.Children.Add(historyTitle); WC.Grid.SetColumn(count, 1); historyHeader.Children.Add(count); WC.Grid.SetColumn(collapse, 2); historyHeader.Children.Add(collapse);
        W.Automation.AutomationProperties.SetAutomationId(search, "ScreenshotHistorySearch"); W.Automation.AutomationProperties.SetName(search, UiLanguage.Text("搜索原文或译文")); search.ToolTip = UiLanguage.Text("搜索原文或译文"); WpfUiLanguage.Property(search,W.Automation.AutomationProperties.NameProperty,"搜索原文或译文"); WpfUiLanguage.Property(search,W.FrameworkElement.ToolTipProperty,"搜索原文或译文");
        WC.Grid.SetRow(search, 1); WC.Grid.SetColumnSpan(search, 3); historyHeader.Children.Add(search);
        search.TextChanged += (_, _) => { if (!refreshing) model.Search(search.Text); };
        WC.Grid.SetRow(date, 2); WC.Grid.SetColumnSpan(date, 3); historyHeader.Children.Add(date);
        date.SelectionChanged += (_, _) => { if (!refreshing) model.SetDate(date.SelectedIndex); };
        date.PreviewMouseWheel += (_, e) => { if (!date.IsDropDownOpen) e.Handled = true; };
    }
    private void ShowOverflow()
    {
        var menu = new WC.ContextMenu();
        foreach (var b in new[] { editProfile, retranslate, editRecord, reset })
        {
            if (b.Visibility == W.Visibility.Visible) continue;
            var label = actions.First(x => ReferenceEquals(x.Button, b)).Text;
            var item = new WC.MenuItem { Header = UiLanguage.Text(label), IsEnabled = b.IsEnabled };
            item.Click += (_, _) => b.RaiseEvent(new W.RoutedEventArgs(WC.Primitives.ButtonBase.ClickEvent)); menu.Items.Add(item);
        }
        Open(menu, overflow);
    }
    private void ShowExport()
    {
        var menu = new WC.ContextMenu();
        foreach (var pane in new[] { model.Left, model.Right })
        {
            var item = new WC.MenuItem { Header = UiLanguage.Text("导出" + pane.PaneTitle + " · " + ScreenshotRebuildPane.ViewTitle(pane.View)), IsEnabled = pane.IsImageView ? pane.CurrentBitmap is not null : pane.CurrentText.Length > 0 };
            item.Click += (_, _) => model.Export(pane); menu.Items.Add(item);
        }
        menu.Items.Add(new WC.Separator()); var active = model.Active;
        var copy = new WC.MenuItem { Header = UiLanguage.Text("复制" + active.PaneTitle + " · " + ScreenshotRebuildPane.ViewTitle(active.View)), IsEnabled = active.IsImageView ? active.CurrentBitmap is not null : active.CurrentText.Length > 0 };
        copy.Click += (_, _) => model.CopyActive(); menu.Items.Add(copy); Open(menu, export);
    }
    private static void Open(WC.ContextMenu menu, WC.Button target)
    { menu.PlacementTarget = target; menu.Placement = WC.Primitives.PlacementMode.Bottom; menu.IsOpen = true; }
    protected override W.Size MeasureOverride(W.Size availableSize)
    {
        if (double.IsFinite(availableSize.Width)) ArrangeToolbar(availableSize.Width);
        return base.MeasureOverride(availableSize);
    }
    private void MeasureLabels()
    {
        foreach (var view in toolbar.Children.OfType<W.FrameworkElement>())
        {
            if (ReferenceEquals(view, profile)) continue;
            view.Visibility = W.Visibility.Visible; view.Width = view.Height = double.NaN; view.Measure(new(double.PositiveInfinity, 56)); widths[view] = Math.Ceiling(view.DesiredSize.Width);
        }
        widths[heading] = Math.Max(82, widths[heading] + 8); arrangedWidth = -1;
    }
    private void ArrangeToolbar(double width)
    {
        if (width == arrangedWidth || widths.Count == 0) return; arrangedWidth = width;
        double rightEdge = width - 17;
        void Place(W.FrameworkElement view, double x, double w, double h = 36)
        { if (view is WC.TextBlock) h = view.DesiredSize.Height; view.Visibility = W.Visibility.Visible; WC.Canvas.SetLeft(view, x); WC.Canvas.SetTop(view, (56 - h) / 2); view.Width = Math.Max(0, w); view.Height = h; }
        foreach (var b in new[] { capture, import, export, swap }) { double w = widths[b]; Place(b, rightEdge - w, w); rightEdge -= w + 8; }
        Place(heading, 18, widths[heading], 56);
        double x = 18 + widths[heading] + 12, room = rightEdge - x;
        var visible = new List<WC.Button> { editProfile, retranslate, editRecord, reset };
        bool label = true, more = false;
        double Required(double selectorWidth) => selectorWidth + (label ? widths[profileLabel] + 8 : 0) + visible.Sum(b => widths[b] + 8) + (more ? widths[overflow] + 8 : 0);
        foreach (var b in new[] { reset, editRecord, editProfile, retranslate })
        { if (Required(100) <= room) break; visible.Remove(b); more = true; }
        if (Required(100) > room) label = false;
        double profileWidth = Math.Clamp(room - Required(0), 24, 180);
        profileLabel.Visibility = label ? W.Visibility.Visible : W.Visibility.Collapsed; overflow.Visibility = more ? W.Visibility.Visible : W.Visibility.Collapsed;
        foreach (var b in new[] { editProfile, retranslate, editRecord, reset }) b.Visibility = visible.Contains(b) ? W.Visibility.Visible : W.Visibility.Collapsed;
        if (label) { Place(profileLabel, x, widths[profileLabel], 56); x += widths[profileLabel] + 8; }
        Place(profile, x, profileWidth); x += profileWidth + 8;
        foreach (var b in visible) { Place(b, x, widths[b]); x += widths[b] + 8; }
        if (more) Place(overflow, x, widths[overflow]);
    }
    internal void Refresh()
    {
        if (refreshing || model.IsDisposed) return; refreshing = true;
        try
        {
            if (language != UiLanguage.Current)
            {
                language = UiLanguage.Current; heading.Text = UiLanguage.Text("截图翻译"); profileLabel.Text = UiLanguage.Text("翻译方案");
                historyTitle.Text = UiLanguage.Text("翻译记录"); historyNote.Text = UiLanguage.Text("当前记录同时应用于左右两栏");
                date.ItemsSource = new[] { "全部日期", "今天", "近七天" }.Select(UiLanguage.Text).ToArray();
                WpfThemeResources.SetButton(expand, "", "sidebar-show");
                expand.ToolTip = UiLanguage.Text("展开历史"); W.Automation.AutomationProperties.SetName(expand, UiLanguage.Text("展开历史"));
                foreach (var action in actions) WpfThemeResources.SetButton(action.Button, action.Text, action.Icon, action.Primary);
                MeasureLabels();
            }
            var choices = commands.Profiles();
            if (!profileChoices.SequenceEqual(choices)) { profileChoices = choices; profile.ItemsSource = choices; }
            profile.SelectedValue = commands.ProfileId();
            if (search.Text != model.Query) search.Text = model.Query;
            date.SelectedIndex = model.DateFilter;
            retranslate.IsEnabled = model.CanRetranslate; editRecord.IsEnabled = model.CanEdit;
            count.Text = UiLanguage.Choose($"{model.RecordCount} 条", $"{model.RecordCount}", $"{model.RecordCount}"); records.Text = model.RecordCount.ToString();
            pageNumber.Text = $"{model.PageIndex + 1} / {model.Pages}";
            ((W.FrameworkElement)previous.Parent).Visibility = model.Pages > 1 ? W.Visibility.Visible : W.Visibility.Collapsed;
            previous.IsEnabled = model.PageIndex > 0; next.IsEnabled = model.PageIndex + 1 < model.Pages;
            emptyHistory.Text = UiLanguage.Text(model.History.EmptyMessage); emptyHistory.Visibility = model.History.Count == 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
            left.Refresh(); right.Refresh(); historyList.Refresh(); RefreshStatus();
            bool expanded = model.HistoryVisible;
            historyBorder.Width = expanded ? 262 : 72; history.Visibility = expanded ? W.Visibility.Visible : W.Visibility.Collapsed; rail.Visibility = expanded ? W.Visibility.Collapsed : W.Visibility.Visible;
            var p = ScreenshotRebuildStyle.Palette; Background = WpfDrawing.Brush(p.Main); toolbarBorder.Background = WpfDrawing.Brush(p.Secondary); toolbarBorder.BorderBrush = historyBorder.BorderBrush = WpfDrawing.Brush(p.Border);
            historyBorder.Background = WpfDrawing.Brush(p.Main.GetBrightness() > .5f ? Color.FromArgb(237, 243, 251) : p.Secondary);
            foreach (var label in new[] { heading, profileLabel, historyTitle }) label.Foreground = WpfDrawing.Brush(p.Text);
            foreach (var label in new[] { count, records, pageNumber, historyNote, emptyHistory, status }) label.Foreground = WpfDrawing.Brush(p.SecondaryText);
            int maximized = model.MaximizedPane;
            left.Visibility = maximized == 1 ? W.Visibility.Collapsed : W.Visibility.Visible; right.Visibility = maximized == 0 ? W.Visibility.Collapsed : W.Visibility.Visible; splitter.Visibility = maximized >= 0 ? W.Visibility.Collapsed : W.Visibility.Visible;
            if (!splitDragging)
            {
                panes.ColumnDefinitions[0].Width = maximized == 1 ? new(0) : new(model.SplitPercent, W.GridUnitType.Star);
                panes.ColumnDefinitions[1].Width = new(maximized >= 0 ? 0 : 14);
                panes.ColumnDefinitions[2].Width = maximized == 0 ? new(0) : new(100 - model.SplitPercent, W.GridUnitType.Star);
            }
            ArrangeToolbar(ActualWidth);
        }
        finally { refreshing = false; }
    }
    internal IReadOnlyDictionary<string, Rectangle> ToolbarBounds() => toolbar.Children.OfType<W.FrameworkElement>().Where(x => x.Visibility == W.Visibility.Visible)
        .ToDictionary(x => W.Automation.AutomationProperties.GetAutomationId(x), x => new Rectangle((int)Math.Round(WC.Canvas.GetLeft(x)), (int)Math.Round(WC.Canvas.GetTop(x)), (int)Math.Round(x.ActualWidth), (int)Math.Round(x.ActualHeight)));
    internal Rectangle PaneBounds(int index)
    { var pane = index == 0 ? left : right; var at = pane.TranslatePoint(new(), panes); return new((int)at.X, (int)at.Y, (int)pane.ActualWidth, (int)pane.ActualHeight); }
    internal Rectangle HistoryBounds => new(0, 56, (int)historyBorder.ActualWidth, (int)historyBorder.ActualHeight);
    private void RefreshStatus() => status.Text = UiLanguage.Text(model.Status);
    public void Dispose() { refreshQueue.Dispose(); model.Changed -= refreshQueue.Request; model.StatusChanged -= RefreshStatus; left.Dispose(); right.Dispose(); historyList.Dispose(); }
}

public sealed partial class MainForm
{
    internal WpfScreenshotWorkspace? WpfScreenshotView { get; set; }
    internal IScreenshotPaneSession WpfShotLeft => _shotLeft;
    internal IScreenshotPaneSession WpfShotRight => _shotRight;
    internal IScreenshotHistorySession WpfShotHistory => _shotHistory;
    internal bool WpfShotHistoryVisible => WpfWorkspace.Enabled ? ScreenshotWorkspace.HistoryVisible : _shotHistoryVisible;
    internal int WpfShotRecordCount => WpfWorkspace.Enabled ? ScreenshotWorkspace.RecordCount : _shotRail.RecordCount;
    internal int WpfShotMaximized => ShotMaximizedPane;
    internal double WpfShotSplit { get => ShotSplitPercent; set => ShotSplitPercent = value; }
    internal event Action? WpfShotSplitChanged;
    internal void WpfSetShotHistory(bool visible) => SetShotHistoryVisible(visible);
    internal bool WpfShotEscape()
    {
        if (_currentMainPage != "主页") return false;
        if (WpfWorkspace.Enabled) return ScreenshotWorkspace.Escape();
        if (ShotMaximizedPane >= 0) { MaximizeShotPane(ShotMaximizedPane); return true; }
        if (_shotHistoryVisible) { SetShotHistoryVisible(false); return true; }
        return false;
    }
    private ScreenshotWorkspaceDiagnostics NativeScreenshotDiagnostics() => new(
        (int)_shotLeft.View, (int)_shotRight.View, ScreenshotWorkspace.HistoryVisible, ShotMaximizedPane,
        ScreenshotWorkspace.Selected is null ? "" : ScreenshotHistoryIdentity.Key(ScreenshotWorkspace.Selected), _shotHistory.Count,
        WpfScreenshotView?.PaneBounds(0) ?? Rectangle.Empty, WpfScreenshotView?.PaneBounds(1) ?? Rectangle.Empty,
        ShotMaximizedPane != 1, ShotMaximizedPane != 0, ShotSplitPercent, ScreenshotWorkspace.IsLoading,
        _shotLeft.CaptureViewState(), _shotRight.CaptureViewState(), WpfScreenshotView?.HistoryBounds ?? Rectangle.Empty, 56, 96);
}
