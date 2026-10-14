using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;

namespace ScreenshotTranslationUiTester;

// Native desktop layout and actions; the established image/text viewers remain
// single-level leaf hosts, retaining selection, zoom, editor and export behavior.
internal sealed class WpfScreenshotWorkspace : WC.Grid, IDisposable
{
    private readonly MainForm main;
    private readonly WpfSettingsFields fields = new();
    private readonly WC.Canvas toolbar = new(), historyHeader = new(), historyFooter = new();
    private readonly WC.Grid history = new(), panes = new();
    private readonly WC.Border historyBorder = new(), toolbarBorder = new();
    private readonly WC.StackPanel rail = new();
    private readonly WC.Button expand;
    private readonly WC.TextBlock records = new();
    private readonly WC.GridSplitter splitter;
    private readonly System.Windows.Forms.Integration.WindowsFormsHost left, right, historyList;
    private readonly List<(Control Model, W.FrameworkElement View, WC.Canvas Parent)> positioned = [];
    private bool refreshing;

    internal WpfScreenshotWorkspace(MainForm main)
    {
        this.main = main; main.WpfScreenshotView = this; ClipToBounds = true;
        RowDefinitions.Add(new() { Height = new(46) }); RowDefinitions.Add(new() { Height = new(1, W.GridUnitType.Star) }); RowDefinitions.Add(new() { Height = new(22) });
        toolbarBorder.Child = toolbar; toolbarBorder.BorderThickness = new(0, 0, 0, 1); Children.Add(toolbarBorder);
        var body = new WC.Grid(); body.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto }); body.ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) }); WC.Grid.SetRow(body, 1); Children.Add(body);
        historyBorder.BorderThickness = new(0, 0, 1, 0); body.Children.Add(historyBorder);
        var historyContainer = new WC.Grid(); historyContainer.Children.Add(history); historyContainer.Children.Add(rail); historyBorder.Child = historyContainer;
        history.RowDefinitions.Add(new() { Height = new(132) }); history.RowDefinitions.Add(new() { Height = new(1, W.GridUnitType.Star) }); history.RowDefinitions.Add(new() { Height = new(64) });
        history.Children.Add(historyHeader); WC.Grid.SetRow(historyFooter, 2); history.Children.Add(historyFooter);
        historyList = Host(main.WpfShotHistory); historyList.Margin = new(12, 0, 12, 0); WC.Grid.SetRow(historyList, 1); history.Children.Add(historyList);
        expand = WpfThemeResources.Button("展开历史", null, () => main.WpfSetShotHistory(true)); expand.FontSize = 11; expand.Width = 48; expand.Padding = new(4, 9, 4, 9); expand.Margin = new(7, 14, 7, 0);
        rail.Children.Add(expand); records.HorizontalAlignment = W.HorizontalAlignment.Center; records.Margin = new(0, 28, 0, 0); rail.Children.Add(records);
        panes.Margin = new(12, 12, 16, 18); panes.ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) }); panes.ColumnDefinitions.Add(new() { Width = new(14) }); panes.ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) }); WC.Grid.SetColumn(panes, 1); body.Children.Add(panes);
        left = Host(main.WpfShotLeft); right = Host(main.WpfShotRight); panes.Children.Add(left); WC.Grid.SetColumn(right, 2); panes.Children.Add(right);
        splitter = new() { Width = 14, HorizontalAlignment = W.HorizontalAlignment.Stretch, VerticalAlignment = W.VerticalAlignment.Stretch, ResizeDirection = WC.GridResizeDirection.Columns, ResizeBehavior = WC.GridResizeBehavior.PreviousAndNext, Background = WM.Brushes.Transparent, ShowsPreview = true };
        W.Automation.AutomationProperties.SetName(splitter, UiLanguage.Text("调整左右分栏"));
        WC.Grid.SetColumn(splitter, 1); panes.Children.Add(splitter);
        splitter.DragCompleted += (_, _) => { main.WpfShotSplit = panes.ColumnDefinitions[0].ActualWidth * 100 / Math.Max(1, panes.ActualWidth - 14); Refresh(); };
        splitter.MouseDoubleClick += (_, _) => { main.WpfShotSplit = 50; Refresh(); };
        splitter.PreviewKeyDown += (_, e) => { if (e.Key == WI.Key.Home) main.WpfShotSplit = 50; else if (e.Key is WI.Key.Left or WI.Key.Right) main.WpfShotSplit += e.Key == WI.Key.Left ? -3 : 3; else return; e.Handled = true; Refresh(); };
        Add(main.WpfShotToolbar, toolbar); Add(main.WpfShotHistoryHeader, historyHeader); Add(main.WpfShotHistoryFooter, historyFooter);
        var status = fields.Create(main.WpfShotStatus); status.Margin = new(22, 0, 0, 0); WC.Grid.SetRow(status, 2); Children.Add(status);
        SizeChanged += (_, _) => Refresh();
        main.WpfShotSplitChanged += Refresh;
    }
    private static System.Windows.Forms.Integration.WindowsFormsHost Host(Control control)
    {
        control.Parent?.Controls.Remove(control); control.Dock = DockStyle.Fill;
        return new() { Child = control, Focusable = true };
    }
    private void Add(IEnumerable<Control> models, WC.Canvas parent)
    {
        foreach (var model in models)
        {
            var view = fields.Create(model); view.MinWidth = 0; view.MaxWidth = double.PositiveInfinity; view.MinHeight = 0; view.MaxHeight = double.PositiveInfinity;
            if (view is WC.Control control) control.Padding = new(6, 2, 6, 2);
            if (view is WC.TextBlock label) { label.TextWrapping = W.TextWrapping.NoWrap; label.TextTrimming = W.TextTrimming.CharacterEllipsis; }
            view.Width = double.NaN; view.VerticalAlignment = W.VerticalAlignment.Center; view.HorizontalAlignment = W.HorizontalAlignment.Stretch;
            var frame = new WC.Border { Child = view }; fields.Bind(model, frame);
            parent.Children.Add(frame); positioned.Add((model, frame, parent));
        }
    }
    internal void Refresh()
    {
        if (refreshing || main.IsDisposed || !IsVisible) return; refreshing = true;
        try
        {
            main.WpfArrangeShot(ActualWidth, Math.Max(0, ActualHeight - 22)); fields.Refresh();
            double scale = 96d / main.DeviceDpi;
            foreach (var (model, view, _) in positioned)
            { WC.Canvas.SetLeft(view, model.Left * scale); WC.Canvas.SetTop(view, model.Top * scale); view.Width = Math.Max(0, model.Width * scale); view.Height = Math.Max(0, model.Height * scale); }
            bool expanded = main.WpfShotHistoryVisible;
            historyBorder.Width = expanded ? 262 : 62; history.Visibility = expanded ? W.Visibility.Visible : W.Visibility.Collapsed; rail.Visibility = expanded ? W.Visibility.Collapsed : W.Visibility.Visible;
            var p = ScreenshotRebuildStyle.Palette; Background = WpfDrawing.Brush(p.Main); toolbarBorder.Background = WpfDrawing.Brush(p.Secondary); toolbarBorder.BorderBrush = historyBorder.BorderBrush = WpfDrawing.Brush(p.Border);
            historyBorder.Background = WpfDrawing.Brush(p.Main.GetBrightness() > .5f ? Color.FromArgb(237,243,251) : p.Secondary);
            expand.Content = new WC.TextBlock { Text = "›\n\n" + UiLanguage.Choose("展开\n历史", "Expand\nhistory"), FontSize = 10.5, TextAlignment = W.TextAlignment.Center, TextWrapping = W.TextWrapping.NoWrap };
            W.Automation.AutomationProperties.SetName(expand, UiLanguage.Text("展开历史")); records.Text = main.WpfShotRecordCount.ToString(); records.Foreground = WpfDrawing.Brush(p.SecondaryText);
            int maximized = main.WpfShotMaximized;
            left.Visibility = maximized == 1 ? W.Visibility.Collapsed : W.Visibility.Visible; right.Visibility = maximized == 0 ? W.Visibility.Collapsed : W.Visibility.Visible; splitter.Visibility = maximized >= 0 ? W.Visibility.Collapsed : W.Visibility.Visible;
            panes.ColumnDefinitions[0].Width = maximized == 1 ? new(0) : new(main.WpfShotSplit, W.GridUnitType.Star);
            panes.ColumnDefinitions[1].Width = new(maximized >= 0 ? 0 : 14);
            panes.ColumnDefinitions[2].Width = maximized == 0 ? new(0) : new(100 - main.WpfShotSplit, W.GridUnitType.Star);
        }
        finally { refreshing = false; }
    }
    public void Dispose() { main.WpfShotSplitChanged -= Refresh; fields.Dispose(); left.Dispose(); right.Dispose(); historyList.Dispose(); }
}

public sealed partial class MainForm
{
    internal WpfScreenshotWorkspace? WpfScreenshotView { get; set; }
    internal ScreenshotRebuildPane WpfShotLeft => _shotLeft;
    internal ScreenshotRebuildPane WpfShotRight => _shotRight;
    internal ScreenshotRebuildHistory WpfShotHistory => _shotHistory;
    internal Label WpfShotStatus => _statusLabel;
    internal IEnumerable<Control> WpfShotToolbar => _shotToolbar!.Controls.Cast<Control>().ToArray();
    internal IEnumerable<Control> WpfShotHistoryHeader => [_shotHistoryHeading!, _shotCount, _shotCollapse!, _shotSearchBorder!, _shotDate];
    internal IEnumerable<Control> WpfShotHistoryFooter => _shotHistoryFooter!.Controls.Cast<Control>().ToArray();
    internal bool WpfShotHistoryVisible => _shotHistoryVisible;
    internal int WpfShotRecordCount => _shotRail.RecordCount;
    internal int WpfShotMaximized => _shotSplit.MaximizedPane;
    internal double WpfShotSplit { get => _shotSplit.SplitPercent; set => _shotSplit.SplitPercent = value; }
    internal event Action? WpfShotSplitChanged;
    internal void WpfSetShotHistory(bool visible) => SetShotHistoryVisible(visible);
    internal void WpfArrangeShot(double width, double height)
    {
        if (_shotPage is null) return; _shotPage.Dock = DockStyle.None;
        _shotPage.Size = new(Math.Max(1, (int)Math.Round(width * DeviceDpi / 96)), Math.Max(1, (int)Math.Round(height * DeviceDpi / 96)));
        ArrangeShotWorkspace();
    }
    internal bool WpfShotEscape()
    {
        if (_currentMainPage != "主页") return false;
        if (_shotSplit.MaximizedPane >= 0) { MaximizeShotPane(_shotSplit.MaximizedPane); return true; }
        if (_shotHistoryVisible) { SetShotHistoryVisible(false); return true; }
        return false;
    }
}
