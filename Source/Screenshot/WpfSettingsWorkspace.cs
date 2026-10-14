using W = System.Windows;
using WC = System.Windows.Controls;

namespace ScreenshotTranslationUiTester;

internal sealed class WpfSettingsWorkspace : WC.Grid, IDisposable
{
    private readonly MainForm main;
    private readonly WC.StackPanel navigation = new();
    private readonly WC.ContentControl content = new();
    private readonly WpfSettingsFields footerFields = new();
    private readonly W.FrameworkElement save;
    private readonly Dictionary<int, (WC.ScrollViewer View, WpfSettingsFields Fields)> pages = [];
    private readonly List<WC.Button> links = [];
    private bool refreshing;
    internal WpfSettingsWorkspace(MainForm main)
    {
        this.main = main;
        ColumnDefinitions.Add(new() { Width = new(212) }); ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) });
        RowDefinitions.Add(new() { Height = new(1, W.GridUnitType.Star) }); RowDefinitions.Add(new() { Height = new(68) });
        navigation.Margin = new(20, 20, 0, 0);
        for (int i = 0; i < main.WpfSettingsNames.Length; i++)
        {
            int category = i;
            var link = WpfThemeResources.Button(main.WpfSettingsNames[i], null, () => { main.WpfSelectSettings(category); Refresh(); });
            link.MinHeight = 42; link.HorizontalContentAlignment = W.HorizontalAlignment.Left;
            link.Padding = new(12, 0, 12, 0); link.Margin = new(0, 0, 0, 0); link.BorderThickness = new(3, 0, 0, 0);
            links.Add(link); navigation.Children.Add(link);
        }
        var navScroll = new WC.ScrollViewer { Content = navigation, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = WC.ScrollBarVisibility.Disabled };
        WC.Grid.SetRowSpan(navScroll, 2); Children.Add(navScroll);
        WC.Grid.SetColumn(content, 1); Children.Add(content);
        var footer = new WC.Grid { Margin = new(28, 12, 28, 8) };
        footer.ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto });
        footer.Children.Add(footerFields.Create(main.WpfSettingsSaveState));
        save = footerFields.Create(main.WpfSettingsSaveButton); save.VerticalAlignment = W.VerticalAlignment.Center;
        WC.Grid.SetColumn(save, 1); footer.Children.Add(save); WC.Grid.SetColumn(footer, 1); WC.Grid.SetRow(footer, 1); Children.Add(footer);
    }
    internal void Refresh()
    {
        if (refreshing) return; refreshing = true;
        try
        {
            int index = main.WpfSettingsCategory;
            for (int i = 0; i < links.Count; i++)
            {
                var link = links[i]; var p = UiTheme.Current;
                WpfThemeResources.SetButton(link, main.WpfSettingsNames[i], null);
                link.BorderBrush = WpfDrawing.Brush(i == index ? p.Accent : p.Main);
                link.Background = WpfDrawing.Brush(i == index ? p.Control : p.Main);
                link.Foreground = WpfDrawing.Brush(i == index ? p.Accent : p.Text);
            }
            if (!pages.TryGetValue(index, out var page))
            {
                var model = main.WpfSettingsContent(index); var fields = new WpfSettingsFields();
                var body = new WC.StackPanel { MaxWidth = 1060, Margin = new(28, 24, 28, 28), HorizontalAlignment = W.HorizontalAlignment.Left };
                foreach (Control block in model.Controls.Cast<Control>().ToArray())
                {
                    W.FrameworkElement view = block is SettingsSection section ? Section(section, fields) : fields.Create(block);
                    view.Margin = new(0, 0, 0, 16); body.Children.Add(view);
                }
                var scroll = new WC.ScrollViewer { Content = body, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = WC.ScrollBarVisibility.Disabled };
                scroll.SizeChanged += (_, _) => body.Width = Math.Max(200, Math.Min(1004, scroll.ActualWidth - 74));
                pages[index] = page = (scroll, fields);
            }
            content.Content = page.View; page.Fields.Refresh(); footerFields.Refresh();
            save.Visibility = index == 3 ? W.Visibility.Collapsed : W.Visibility.Visible;
        }
        finally { refreshing = false; }
    }
    private static W.FrameworkElement Section(SettingsSection section, WpfSettingsFields fields)
    {
        var column = new WC.StackPanel();
        var heading = fields.Create(section.Heading); heading.Margin = new(0, 0, 0, 20); column.Children.Add(heading);
        foreach (var (label, model) in section.Fields)
        {
            var row = new WC.Grid { Margin = new(0, 0, 0, label.Text.Length == 0 ? 12 : 16) };
            row.ColumnDefinitions.Add(new() { Width = new(206) }); row.ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) });
            var title = fields.Create(label); title.Margin = new(0, 6, 16, 0); title.VerticalAlignment = W.VerticalAlignment.Top;
            var value = fields.Create(model); WC.Grid.SetColumn(value, 1); row.Children.Add(title); row.Children.Add(value);
            // Hiding a field hides its label and spacing, while its retained model
            // still owns the previous value and all save/validation handlers.
            fields.Bind(model, row);
            row.SizeChanged += (_, _) => row.ColumnDefinitions[0].Width = new(Math.Clamp(row.ActualWidth / 4, 164, 206));
            column.Children.Add(row);
        }
        var card = new WC.Border { Child = column, CornerRadius = new(8), Padding = new(20, 20, 20, 8), BorderThickness = new(1) };
        card.SetResourceReference(WC.Border.BackgroundProperty, "Fusion.Secondary"); card.SetResourceReference(WC.Border.BorderBrushProperty, "Fusion.Border");
        return card;
    }
    public void Dispose() { footerFields.Dispose(); foreach (var page in pages.Values) page.Fields.Dispose(); pages.Clear(); }
}

public sealed partial class MainForm
{
    internal string[] WpfSettingsNames => _settingsNavigation.Items.Cast<object>().Select(x => x.ToString() ?? "").ToArray();
    internal int WpfSettingsCategory => Math.Max(0, _settingsNavigation.SelectedIndex);
    internal void WpfSelectSettings(int index) => _settingsNavigation.SelectedIndex = index;
    internal Control WpfSettingsContent(int index) { EnsureSettingsCategory(index); return (Control)_settingsTabs!.TabPages[index].Tag!; }
    internal Label WpfSettingsSaveState => _settingsSaveState;
    internal Button WpfSettingsSaveButton => _settingsSaveButton;
}
