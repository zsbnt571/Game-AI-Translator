using W = System.Windows;
using WC = System.Windows.Controls;

namespace ScreenshotTranslationUiTester;

internal sealed class WpfSettingsWorkspace : WC.Grid, IDisposable
{
    private readonly ISettingsWorkspaceHost host;
    private readonly WC.StackPanel navigation = new();
    private readonly WC.ContentControl content = new() { Focusable = false, IsTabStop = false, FocusVisualStyle = null };
    private readonly WC.Grid footer = new() { Margin = new(24, 10, 24, 10), MaxWidth = 1004, HorizontalAlignment = W.HorizontalAlignment.Stretch };
    private readonly WC.ContentControl profileFooter = new() { MaxWidth = 1004, Margin = new(24, 10, 24, 0), HorizontalContentAlignment = W.HorizontalAlignment.Stretch };
    private readonly WC.TextBlock status = new() { TextWrapping = W.TextWrapping.Wrap, VerticalAlignment = W.VerticalAlignment.Center };
    private readonly WC.TextBlock saveHint = new() { TextWrapping = W.TextWrapping.Wrap, FontSize = 12, Margin = new(0, 3, 12, 0) };
    private readonly WC.Button save;
    private readonly Dictionary<int, WC.ScrollViewer> pages = [];
    private readonly Dictionary<int, WC.Button> links = [];
    private readonly List<(WC.TextBlock View, string Text)> groupHeadings = [];
    private readonly Dictionary<int, WpfDesktopSettingsPage> nativePages = [];
    private readonly List<WC.Button> profileTabs = [];
    private readonly List<W.FrameworkElement> profileViews = [];
    private readonly WC.TextBlock profileHint = new() { TextWrapping = W.TextWrapping.Wrap, Margin = new(0, 0, 0, 16) };
    private readonly double[] profileScrollOffsets = new double[3];
    private WpfProfileEditor? profileEditor;
    private WpfTranslationContentEditor? contentEditor;
    private WpfAiSettingsPage? aiSettings;
    private int profileSection = 1, previousCategory = -1;
    private bool refreshing;
    private static readonly string[] ProfileSections = ["默认方案", "连接", "翻译内容"];

    internal WpfSettingsWorkspace(ISettingsWorkspaceHost host)
    {
        this.host = host;
        ColumnDefinitions.Add(new() { Width = new(192) }); ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) });
        RowDefinitions.Add(new() { Height = new(1, W.GridUnitType.Star) }); RowDefinitions.Add(new() { Height = W.GridLength.Auto });
        navigation.Margin = new(12, 12, 0, 12);
        foreach (var group in WpfSettingsLayout.Groups)
        {
            var heading = new WC.TextBlock { FontSize = 12, FontWeight = W.FontWeights.SemiBold, Margin = new(12, groupHeadings.Count == 0 ? 0 : 16, 8, 6) };
            heading.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Muted");
            W.Automation.AutomationProperties.SetAutomationId(heading, "SettingsGroup_" + group.Pages[0]);
            groupHeadings.Add((heading, group.Name)); navigation.Children.Add(heading);
            foreach (int category in group.Pages)
            {
                var link = WpfThemeResources.Button(host.WpfSettingsNames[category], null, () => host.WpfSelectSettings(category));
                link.SetResourceReference(WC.Control.StyleProperty, "Fusion.NavButton");
                link.MinHeight = 36; link.HorizontalContentAlignment = W.HorizontalAlignment.Left;
                link.Padding = new(10, 5, 8, 5); link.Margin = new(0, 0, 0, 2); link.BorderThickness = new(3, 0, 0, 0);
                W.Automation.AutomationProperties.SetAutomationId(link, "SettingsCategory_" + category);
                links.Add(category, link); navigation.Children.Add(link);
            }
        }
        var navScroll = new WC.ScrollViewer { Content = navigation, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = WC.ScrollBarVisibility.Disabled };
        WC.Grid.SetRowSpan(navScroll, 2); Children.Add(navScroll); WC.Grid.SetColumn(content, 1); Children.Add(content);
        footer.ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto });
        status.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Muted");
        saveHint.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Muted");
        W.Automation.AutomationProperties.SetAutomationId(status, "SettingsSaveState");
        W.Automation.AutomationProperties.SetAutomationId(saveHint, "SettingsSaveHint");
        var messages = new WC.StackPanel { VerticalAlignment = W.VerticalAlignment.Center }; messages.Children.Add(status); messages.Children.Add(saveHint); footer.Children.Add(messages);
        save = WpfThemeResources.Button("保存设置", null, host.NativeSaveDesktopSettings, primary: true);
        save.MinHeight = 36; save.MinWidth = 112; save.VerticalAlignment = W.VerticalAlignment.Center;
        W.Automation.AutomationProperties.SetAutomationId(save, "SaveDesktopSettings"); WC.Grid.SetColumn(save, 1); footer.Children.Add(save);
        WC.Grid.SetColumn(footer, 1); WC.Grid.SetRow(footer, 1); Children.Add(footer);
        WC.Grid.SetColumn(profileFooter, 1); WC.Grid.SetRow(profileFooter, 1); Children.Add(profileFooter);
        host.NativeSettingsStatusChanged += RefreshFooter;
        host.ProfileEditor.ProfileNameFocusRequested += ShowConnection;
    }

    internal void Refresh()
    {
        if (refreshing || host.WpfSettingsPreparing) return; refreshing = true;
        try
        {
            int index = host.WpfSettingsCategory;
            foreach (var (view, text) in groupHeadings) view.Text = WpfSettingsLayout.Text(text);
            foreach (var (category, link) in links)
            {
                var p = UiTheme.Current; WpfThemeResources.SetButton(link, category == 10 ? AiToolsText.Text("AiTools") : host.WpfSettingsNames[category], null);
                link.BorderBrush = WpfDrawing.Brush(category == index ? p.Accent : p.Main); link.Background = WpfDrawing.Brush(category == index ? p.Control : p.Main);
                link.Foreground = WpfDrawing.Brush(category == index ? p.Accent : p.Text);
            }
            if (!pages.TryGetValue(index, out var page))
            {
                var body = new WC.StackPanel { MaxWidth = 1004, Margin = new(24, 20, 24, 24), HorizontalAlignment = W.HorizontalAlignment.Stretch };
                if (index == 3) BuildProfilePage(body);
                else if (index == 10)
                {
                    aiSettings ??= new(host.CreateAiToolsController(), ownsController: false);
                    body.Children.Add(aiSettings);
                }
                else
                {
                    var native = new WpfDesktopSettingsPage(host.DesktopSettings, index); nativePages.Add(index, native); body.Children.Add(native.Content);
                }
                page = new() { Content = body, FocusVisualStyle = null, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = WC.ScrollBarVisibility.Disabled };
                W.Automation.AutomationProperties.SetAutomationId(page, "SettingsPage_" + index);
                page.ScrollChanged += (_, _) => AlignFooter(page); pages.Add(index, page);
            }
            content.Content = page;
            if (nativePages.TryGetValue(index, out var nativePage)) nativePage.Refresh();
            if (index == 10) aiSettings?.Refresh();
            if (index == 3)
            {
                profileEditor?.Refresh(); contentEditor?.Refresh();
                // Editing a plan from a game must land on its connection, even if
                // the user last visited the translation-content subpage.
                if (previousCategory != 3 && host.ProfileEditor.ProfileDraft.Returning) SelectProfileSection(1);
                RefreshProfileSections();
                foreach (var text in ((WC.StackPanel)page.Content).Children.OfType<WC.TextBlock>().Where(x => x.Tag is string))
                    text.Text = UiLanguage.Text(Equals(text.Tag, "SettingsHeading") ? host.WpfSettingsNames[index] : host.NativeSettingsDescription(index));
            }
            previousCategory = index; AlignFooter(page); RefreshFooter();
        }
        finally { refreshing = false; }
    }

    private void BuildProfilePage(WC.StackPanel body)
    {
        host.NativePrepareProfilePage(); profileEditor ??= new(host.ProfileEditor); contentEditor ??= new(host.TranslationContent);
        var heading = new WC.TextBlock { FontSize = 26, FontWeight = W.FontWeights.Bold, Margin = new(0, 0, 0, 8), Tag = "SettingsHeading" };
        heading.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Text"); body.Children.Add(heading);
        var description = new WC.TextBlock { TextWrapping = W.TextWrapping.Wrap, Margin = new(0, 0, 0, 16), Tag = "SettingsDescription" };
        description.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Muted"); body.Children.Add(description);
        var tabs = new WC.WrapPanel { Margin = new(0, 0, 0, 12) };
        for (int i = 0; i < ProfileSections.Length; i++)
        {
            int section = i;
            var tab = WpfThemeResources.Button(ProfileSections[i], null, () => SelectProfileSection(section));
            tab.MinHeight = 36; tab.Padding = new(14, 6, 14, 6); tab.Margin = new(0, 0, 8, 4);
            W.Automation.AutomationProperties.SetAutomationId(tab, "SettingsProfileTab_" + i);
            profileTabs.Add(tab); tabs.Children.Add(tab);
        }
        body.Children.Add(tabs); profileHint.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Muted");
        W.Automation.AutomationProperties.SetAutomationId(profileHint, "SettingsProfileHint"); body.Children.Add(profileHint);
        ((WC.Panel)profileEditor.Footer.Parent).Children.Remove(profileEditor.Footer);
        ((WC.Panel)contentEditor.Footer.Parent).Children.Remove(contentEditor.Footer);
        foreach (var view in new W.FrameworkElement[] { profileEditor.Defaults, profileEditor.Connection, contentEditor.Content })
        { profileViews.Add(view); body.Children.Add(view); }
    }
    private void ShowConnection() => SelectProfileSection(1);
    private void SelectProfileSection(int index)
    {
        if (profileSection != index)
        {
            if (pages.TryGetValue(3, out var page)) profileScrollOffsets[profileSection] = page.VerticalOffset;
            profileSection = index; RefreshProfileSections();
            if (pages.TryGetValue(3, out page)) page.ScrollToVerticalOffset(profileScrollOffsets[index]);
            RefreshFooter();
        }
    }
    private void RefreshProfileSections()
    {
        for (int i = 0; i < profileTabs.Count; i++)
        {
            WpfThemeResources.SetButton(profileTabs[i], ProfileSections[i], null, primary: i == profileSection);
            profileViews[i].Visibility = i == profileSection ? W.Visibility.Visible : W.Visibility.Collapsed;
        }
        profileHint.Text = WpfSettingsLayout.Text(profileSection switch
        {
            0 => "选择后立即保存；单个游戏固定使用的方案保持不变。",
            1 => "修改后点击“保存 API 方案”。测试连接不会自动保存。",
            _ => "语言、风格和提示单独保存，切换 API 方案不会改变这些内容。"
        });
    }
    private void RefreshFooter()
    {
        int category = host.WpfSettingsCategory;
        profileFooter.Visibility = category == 3 && profileSection != 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
        profileFooter.Content = profileSection == 1 ? profileEditor?.Footer : profileSection == 2 ? contentEditor?.Footer : null;
        // Read-only pages and pages with their own explicit save actions do not
        // advertise an unrelated global save button.
        footer.Visibility = category is 0 or 1 or 2 or 4 or 5 or 6 ? W.Visibility.Visible : W.Visibility.Collapsed;
        status.Text = UiLanguage.Text(host.NativeSettingsStatus); save.IsEnabled = host.NativeSettingsReady;
        saveHint.Text = WpfSettingsLayout.Text("保存待保存的通用、外观和截图选项；API 方案与翻译内容分别保存。");
        WpfThemeResources.SetButton(save, "保存设置", null, primary: true);
    }
    private void AlignFooter(WC.ScrollViewer scroll)
    {
        if (!ReferenceEquals(content.Content, scroll) || scroll.ViewportWidth <= 0) return;
        footer.Margin = new(24, 10, 24 + Math.Max(0, scroll.ActualWidth - scroll.ViewportWidth), 10);
        profileFooter.Margin = new(24, 10, footer.Margin.Right, 0);
    }
    public void Dispose()
    {
        host.NativeSettingsStatusChanged -= RefreshFooter; host.ProfileEditor.ProfileNameFocusRequested -= ShowConnection;
        profileEditor?.Dispose(); contentEditor?.Dispose(); aiSettings?.Dispose();
        foreach (var native in nativePages.Values) native.Dispose(); nativePages.Clear(); pages.Clear();
    }
}

public sealed partial class MainForm
{
    internal string[] WpfSettingsNames => NativeSettingsNames;
    internal bool WpfSettingsPreparing => _preparingSettings;
    internal int WpfSettingsCategory => WpfWorkspace.Enabled ? _lastSettingsCategory : Math.Max(0, _settingsNavigation.SelectedIndex);
    internal void WpfSelectSettings(int index) { if (WpfWorkspace.Enabled) NativeSelectSettings(index); else _settingsNavigation.SelectedIndex = index; }
    internal void NativePrepareProfilePage() => EnsureSettingsCategory(3);
}
