using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;

namespace ScreenshotTranslationUiTester;

internal sealed class WpfScreenshotHistory : WC.ListBox, IDisposable
{
    private readonly IScreenshotHistorySession model;
    private bool updating;
    private SessionHistoryItem[] records = [];
    internal WpfScreenshotHistory(IScreenshotHistorySession model, Action edit, Action retranslate, Func<bool> canRetranslate)
    {
        this.model = model; BorderThickness = new(0); Background = WM.Brushes.Transparent;
        HorizontalContentAlignment = W.HorizontalAlignment.Stretch;
        WC.ScrollViewer.SetHorizontalScrollBarVisibility(this, WC.ScrollBarVisibility.Disabled);
        WC.VirtualizingPanel.SetIsVirtualizing(this, true); WC.VirtualizingPanel.SetVirtualizationMode(this, WC.VirtualizationMode.Recycling);
        SelectionChanged += (_, _) => { if (!updating) model.SelectedIndex = SelectedIndex; };
        MouseDoubleClick += (_, _) => edit();
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { edit(); e.Handled = true; } };
        ContextMenu = new WC.ContextMenu();
        ContextMenuOpening += (_, e) =>
        {
            if (model.SelectedRecord is null) { e.Handled = true; return; }
            ContextMenu.Items.Clear();
            var retry = new WC.MenuItem { Header = UiLanguage.Text("重新翻译"), IsEnabled = canRetranslate() };
            retry.Click += (_, _) => retranslate(); ContextMenu.Items.Add(retry);
            var open = new WC.MenuItem { Header = UiLanguage.Text("详细编辑") };
            open.Click += (_, _) => edit(); ContextMenu.Items.Add(open);
        };
        model.NativeRecordsChanged += Refresh; model.SelectionChanged += Selected;
        W.Automation.AutomationProperties.SetName(this, UiLanguage.Text("截图历史记录")); Refresh();
    }
    private void Selected(object? sender, EventArgs e) { if (updating) return; updating = true; SelectedIndex = model.SelectedIndex; updating = false; }
    internal void Refresh()
    {
        updating = true;
        try
        {
            var next = model.NativeCards;
            if (!records.SequenceEqual(next.Select(x => x.Item)))
            {
                records = next.Select(x => x.Item).ToArray(); Items.Clear(); DateTime? day = null;
                foreach (var (item, thumbnail) in next)
                {
                    var row = new WC.StackPanel { Margin = new(2, 3, 2, 6) };
                    var date = item.Timestamp.ToLocalTime().Date;
                    if (date != day) { var label = new WC.TextBlock { Text = date.ToString("yyyy-MM-dd"), Margin = new(0, 5, 0, 7) }; label.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Muted"); row.Children.Add(label); day = date; }
                    var content = new WC.Grid(); content.ColumnDefinitions.Add(new() { Width = new(83) }); content.ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) });
                    content.Children.Add(new WC.Image { Source = WpfDrawing.Image(thumbnail), Width = 77, Height = 53, Stretch = WM.Stretch.UniformToFill });
                    var summary = new WC.StackPanel { Margin = new(6, 0, 0, 0) }; summary.Children.Add(new WC.TextBlock { Text = item.Timestamp.ToLocalTime().ToString("HH:mm"), FontWeight = W.FontWeights.SemiBold }); summary.Children.Add(new WC.TextBlock { Text = item.TranslationSummary, TextWrapping = W.TextWrapping.Wrap, MaxHeight = 40, TextTrimming = W.TextTrimming.CharacterEllipsis }); WC.Grid.SetColumn(summary, 1); content.Children.Add(summary); row.Children.Add(content);
                    Items.Add(new WC.ListBoxItem { Content = row, HorizontalContentAlignment = W.HorizontalAlignment.Stretch, ToolTip = item.TranslationSummary });
                }
            }
            Foreground = WpfDrawing.Brush(UiTheme.Current.Text); SelectedIndex = model.SelectedIndex;
        }
        finally { updating = false; }
    }
    public void Dispose() { model.NativeRecordsChanged -= Refresh; model.SelectionChanged -= Selected; Items.Clear(); }
}

internal sealed partial class ScreenshotRebuildHistory
{
    internal event Action? NativeRecordsChanged;
    internal (SessionHistoryItem Item, Bitmap Thumbnail)[] NativeCards => cards.Select(x => (x.Item, x.Thumbnail)).ToArray();
}
