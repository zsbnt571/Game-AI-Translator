using W = System.Windows;
using WC = System.Windows.Controls;

namespace ScreenshotTranslationUiTester;

internal sealed class WpfSettingsWorkspace : WC.Grid, IDisposable
{
    private readonly ISettingsWorkspaceHost host;
    private readonly WC.StackPanel navigation = new();
    private readonly WC.ContentControl content = new() { Focusable = false, IsTabStop = false, FocusVisualStyle = null };
    private readonly WC.Grid footer = new() { Margin = new(28, 12, 28, 8), MaxWidth = 1004, HorizontalAlignment = W.HorizontalAlignment.Stretch };
    private readonly WC.TextBlock status = new() { TextWrapping = W.TextWrapping.Wrap, VerticalAlignment = W.VerticalAlignment.Center };
    private readonly WC.Button save;
    private readonly Dictionary<int, WC.ScrollViewer> pages = [];
    private readonly List<WC.Button> links = [];
    private readonly Dictionary<int, WpfDesktopSettingsPage> nativePages = [];
    private WpfProfileEditor? profileEditor;
    private WpfTranslationContentEditor? contentEditor;
    private bool refreshing;

    internal WpfSettingsWorkspace(ISettingsWorkspaceHost host)
    {
        this.host = host;
        ColumnDefinitions.Add(new() { Width = new(212) }); ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) });
        RowDefinitions.Add(new() { Height = new(1, W.GridUnitType.Star) }); RowDefinitions.Add(new() { Height = new(68) });
        navigation.Margin = new(20, 20, 0, 0);
        for (int i = 0; i < host.WpfSettingsNames.Length; i++)
        {
            int category = i;
            var link = WpfThemeResources.Button(host.WpfSettingsNames[i], null, () => host.WpfSelectSettings(category));
            link.SetResourceReference(WC.Control.StyleProperty, "Fusion.NavButton");
            link.MinHeight = 42; link.HorizontalContentAlignment = W.HorizontalAlignment.Left;
            link.Padding = new(12, 0, 12, 0); link.Margin = new(0); link.BorderThickness = new(3, 0, 0, 0);
            links.Add(link); navigation.Children.Add(link);
        }
        var navScroll = new WC.ScrollViewer { Content = navigation, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = WC.ScrollBarVisibility.Disabled };
        WC.Grid.SetRowSpan(navScroll, 2); Children.Add(navScroll); WC.Grid.SetColumn(content, 1); Children.Add(content);
        footer.ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto });
        status.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Muted"); W.Automation.AutomationProperties.SetAutomationId(status, "SettingsSaveState"); footer.Children.Add(status);
        save = WpfThemeResources.Button("保存设置", null, host.NativeSaveDesktopSettings); save.MinHeight = 36; save.MinWidth = 112; save.VerticalAlignment = W.VerticalAlignment.Center;
        W.Automation.AutomationProperties.SetAutomationId(save, "SaveDesktopSettings"); WC.Grid.SetColumn(save, 1); footer.Children.Add(save);
        WC.Grid.SetColumn(footer, 1); WC.Grid.SetRow(footer, 1); Children.Add(footer);
        host.NativeSettingsStatusChanged += RefreshFooter;
    }
    internal void Refresh()
    {
        if (refreshing || host.WpfSettingsPreparing) return; refreshing = true;
        try
        {
            int index = host.WpfSettingsCategory;
            for (int i = 0; i < links.Count; i++)
            {
                var link = links[i]; var p = UiTheme.Current; WpfThemeResources.SetButton(link, host.WpfSettingsNames[i], null);
                link.BorderBrush = WpfDrawing.Brush(i == index ? p.Accent : p.Main); link.Background = WpfDrawing.Brush(i == index ? p.Control : p.Main);
                link.Foreground = WpfDrawing.Brush(i == index ? p.Accent : p.Text);
            }
            if (!pages.TryGetValue(index, out var page))
            {
                var body = new WC.StackPanel { MaxWidth = 1004, Margin = new(28, 24, 28, 28), HorizontalAlignment = W.HorizontalAlignment.Stretch };
                if (index == 3)
                {
                    host.NativePrepareProfilePage(); profileEditor ??= new(host.ProfileEditor); contentEditor ??= new(host.TranslationContent);
                    var heading = new WC.TextBlock { FontSize = 28, FontWeight = W.FontWeights.Bold, Margin = new(0, 0, 0, 8), Tag = "SettingsHeading" };
                    heading.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Text"); body.Children.Add(heading);
                    var description = new WC.TextBlock { TextWrapping = W.TextWrapping.Wrap, Margin = new(0, 0, 0, 24), Tag = "SettingsDescription" };
                    description.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Muted"); body.Children.Add(description);
                    foreach (var view in new W.FrameworkElement[] { profileEditor.Defaults, profileEditor.Connection, contentEditor.Content }) { view.Margin = new(0, 0, 0, 16); body.Children.Add(view); }
                }
                else
                {
                    var native = new WpfDesktopSettingsPage(host.DesktopSettings, index); nativePages.Add(index, native); body.Children.Add(native.Content);
                }
                page = new() { Content = body, FocusVisualStyle = null, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = WC.ScrollBarVisibility.Disabled };
                page.ScrollChanged += (_, _) => AlignFooter(page); pages.Add(index, page);
            }
            content.Content = page;
            if (nativePages.TryGetValue(index, out var nativePage)) nativePage.Refresh();
            if (index == 3)
            {
                profileEditor?.Refresh(); contentEditor?.Refresh();
                foreach (var text in ((WC.StackPanel)page.Content).Children.OfType<WC.TextBlock>()) text.Text = UiLanguage.Text(Equals(text.Tag, "SettingsHeading") ? host.WpfSettingsNames[index] : host.NativeSettingsDescription(index));
            }
            AlignFooter(page); RefreshFooter();
        }
        finally { refreshing = false; }
    }
    private void RefreshFooter()
    {
        status.Text = UiLanguage.Text(host.NativeSettingsStatus); save.IsEnabled = host.NativeSettingsReady;
        WpfThemeResources.SetButton(save, "保存设置", null); save.Visibility = host.WpfSettingsCategory == 3 ? W.Visibility.Collapsed : W.Visibility.Visible;
    }
    private void AlignFooter(WC.ScrollViewer scroll)
    {
        if (!ReferenceEquals(content.Content, scroll) || scroll.ViewportWidth <= 0) return;
        footer.Margin = new(28, 12, 28 + Math.Max(0, scroll.ActualWidth - scroll.ViewportWidth), 8);
    }
    public void Dispose()
    { host.NativeSettingsStatusChanged -= RefreshFooter; profileEditor?.Dispose(); contentEditor?.Dispose(); foreach (var native in nativePages.Values) native.Dispose(); nativePages.Clear(); pages.Clear(); }
}

public sealed partial class MainForm
{
    internal string[] WpfSettingsNames => NativeSettingsNames;
    internal bool WpfSettingsPreparing => _preparingSettings;
    internal int WpfSettingsCategory => WpfWorkspace.Enabled ? _lastSettingsCategory : Math.Max(0, _settingsNavigation.SelectedIndex);
    internal void WpfSelectSettings(int index) { if (WpfWorkspace.Enabled) NativeSelectSettings(index); else _settingsNavigation.SelectedIndex = index; }
    internal void NativePrepareProfilePage() => EnsureSettingsCategory(3);
}
