using System.ComponentModel;
using System.Globalization;
using W = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Data;
using WI = System.Windows.Input;

namespace ScreenshotTranslationUiTester;

// Native preference pages bind to the draft, never to hidden Forms controls.
internal sealed class WpfDesktopSettingsPage : IDisposable
{
    private readonly IDesktopSettingsHost host;
    private readonly DesktopSettingsDraft state;
    private readonly List<Action> refresh = [];
    private readonly List<Action> detach = [];
    private WC.StackPanel section = null!;
    internal WC.StackPanel Content { get; } = new();
    internal static bool Supports(int page) => page is 0 or 1 or 2 or 4 or 5 or 6 or 7 or 8 or 9;

    internal WpfDesktopSettingsPage(IDesktopSettingsHost host, int page)
    {
        this.host = host; state = host.DesktopDraft;
        var heading = Text(host.WpfSettingsNames[page], false); heading.FontSize = 28; heading.FontWeight = W.FontWeights.Bold;
        heading.Margin = new(0, 0, 0, 8); Content.Children.Add(heading);
        var description = Text(host.NativeSettingsDescription(page)); description.Margin = new(0, 0, 0, 24); Content.Children.Add(description);
        switch (page)
        {
            case 0: Appearance(); break;
            case 1: General(); break;
            case 2: Shortcuts(); break;
            case 4: Capture(); break;
            case 5: Preview(); break;
            case 6: History(); break;
            case 7: Artwork(); break;
            case 8: Storage(); break;
            case 9: Help(); break;
        }
        Refresh();
    }
    internal void Refresh() { foreach (var action in refresh) action(); }
    private void Section(string title)
    {
        section = new(); var heading = Text(title, false); heading.FontWeight = W.FontWeights.Bold; heading.FontSize = 16;
        heading.Margin = new(0, 0, 0, 20); section.Children.Add(heading);
        var card = new WC.Border { Child = section, CornerRadius = new(8), Padding = new(20, 20, 20, 8), BorderThickness = new(1), Margin = new(0, 0, 0, 16) };
        card.SetResourceReference(WC.Border.BackgroundProperty, "Fusion.Secondary"); card.SetResourceReference(WC.Border.BorderBrushProperty, "Fusion.Border"); Content.Children.Add(card);
    }
    private void Row(string label, W.FrameworkElement value)
    {
        var row = new FieldRow { Margin = new(0, 0, 0, label.Length == 0 ? 12 : 16) };
        row.ColumnDefinitions.Add(new() { Width = new(206) }); row.ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) });
        var title = Text(label, false); title.Margin = new(0, 6, 16, 0);
        row.Children.Add(title); WC.Grid.SetColumn(value, 1); row.Children.Add(value); section.Children.Add(row);
        if (label.Length > 0) refresh.Add(() => W.Automation.AutomationProperties.SetName(value, UiLanguage.Text(label)));
    }
    private WC.TextBlock Text(string text, bool muted = true)
    {
        var view = new WC.TextBlock { TextWrapping = W.TextWrapping.Wrap };
        view.SetResourceReference(WC.TextBlock.ForegroundProperty, muted ? "Fusion.Muted" : "Fusion.Text");
        refresh.Add(() => view.Text = UiLanguage.Text(text)); return view;
    }
    private WC.TextBlock DynamicText(Func<string> text)
    {
        var view = new WC.TextBlock { TextWrapping = W.TextWrapping.Wrap }; view.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Muted");
        refresh.Add(() => view.Text = UiLanguage.Text(text())); return view;
    }
    private static void Identify(W.DependencyObject view, string id) => W.Automation.AutomationProperties.SetAutomationId(view, id);
    private void Watch(string property, Action update)
    {
        var field = state.Field(property); PropertyChangedEventHandler handler = (_, _) => update(); field.PropertyChanged += handler;
        detach.Add(() => field.PropertyChanged -= handler); update();
    }
    private T Get<T>(string property) => state.Get<T>(property);
    private WC.CheckBox Check(string text, string property)
    {
        var view = new WC.CheckBox { MinHeight = 30, Margin = new(0, 0, 12, 4), VerticalContentAlignment = W.VerticalAlignment.Center };
        Identify(view, property); view.SetBinding(WC.Primitives.ToggleButton.IsCheckedProperty, new WD.Binding(nameof(SettingValue.Value)) { Source = state.Field(property), Mode = WD.BindingMode.TwoWay, UpdateSourceTrigger = WD.UpdateSourceTrigger.PropertyChanged });
        refresh.Add(() => { view.Content = new WC.TextBlock { Text = UiLanguage.Text(text), TextWrapping = W.TextWrapping.Wrap }; W.Automation.AutomationProperties.SetName(view, UiLanguage.Text(text)); }); return view;
    }
    private WC.CheckBox CheckAction(string text, Func<bool> read, Action<bool> write)
    {
        var view = new WC.CheckBox { MinHeight = 30, VerticalContentAlignment = W.VerticalAlignment.Center }; bool syncing = false;
        refresh.Add(() => { syncing = true; view.Content = UiLanguage.Text(text); view.IsChecked = read(); syncing = false; });
        view.Checked += (_, _) => { if (!syncing) write(true); }; view.Unchecked += (_, _) => { if (!syncing) write(false); }; return view;
    }
    private WC.ComboBox Choice(string property, string[] labels)
    {
        var type = state.Field(property).Property.PropertyType;
        object[] values = type.IsEnum ? Enum.GetValues(type).Cast<object>().Take(labels.Length).ToArray() : labels.Cast<object>().ToArray();
        var view = Choice(property, () => Get<object>(property), value => state.Set(property, value), () => values.Select((value, i) => new ChoiceItem(value, UiLanguage.Text(labels[i]))).ToArray());
        Watch(property, () => view.SelectedValue = Get<object>(property)); return view;
    }
    private WC.ComboBox Choice(string id, Func<object> read, Action<object> write, Func<ChoiceItem[]> items)
    {
        var view = new WC.ComboBox { MinHeight = 36, Width = 320, HorizontalAlignment = W.HorizontalAlignment.Left, SelectedValuePath = nameof(ChoiceItem.Value) };
        Identify(view, id); bool syncing = false;
        refresh.Add(() => { syncing = true; try { var choices = items(); if (!view.Items.Cast<ChoiceItem>().SequenceEqual(choices)) view.ItemsSource = choices; view.SelectedValue = read(); } finally { syncing = false; } });
        view.SelectionChanged += (_, _) => { if (!syncing && view.SelectedValue is { } value) write(value); };
        view.PreviewMouseWheel += (_, e) => { if (!view.IsDropDownOpen) ScrollPage(view, e); }; return view;
    }
    private W.FrameworkElement Number(string property, decimal min, decimal max, decimal step = 1, int decimals = 0)
    {
        var panel = new WC.Grid { Width = 146, MinHeight = 38, HorizontalAlignment = W.HorizontalAlignment.Left };
        panel.ColumnDefinitions.Add(new() { Width = new(30) }); panel.ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) }); panel.ColumnDefinitions.Add(new() { Width = new(30) });
        var editor = new WC.TextBox { BorderThickness = new(0), Padding = new(2, 6, 2, 6), TextAlignment = W.TextAlignment.Center, VerticalContentAlignment = W.VerticalAlignment.Center };
        Identify(editor, property); bool syncing = false;
        decimal Current() => Convert.ToDecimal(state.Field(property).Value, CultureInfo.InvariantCulture);
        void Sync() { syncing = true; editor.Text = Current().ToString("F" + decimals, CultureInfo.CurrentCulture); syncing = false; }
        void Commit(decimal value) => state.Set(property, Math.Clamp(value, min, max));
        editor.TextChanged += (_, _) => { if (!syncing && decimal.TryParse(editor.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) && value >= min && value <= max) Commit(value); };
        editor.LostKeyboardFocus += (_, _) => Sync();
        editor.PreviewKeyDown += (_, e) => { if (e.Key is WI.Key.Up or WI.Key.Down) { Commit(Current() + (e.Key == WI.Key.Up ? step : -step)); e.Handled = true; } };
        editor.PreviewMouseWheel += (_, e) => ScrollPage(editor, e);
        var minus = Button("−", () => Commit(Current() - step)); var plus = Button("+", () => Commit(Current() + step));
        foreach (var button in new[] { minus, plus }) { button.MinWidth = 0; button.MinHeight = 36; button.Padding = new(0); button.BorderThickness = new(0); button.Margin = new(0); }
        W.Automation.AutomationProperties.SetName(minus, UiLanguage.Text("减少")); W.Automation.AutomationProperties.SetName(plus, UiLanguage.Text("增加"));
        panel.Children.Add(minus); WC.Grid.SetColumn(editor, 1); panel.Children.Add(editor); WC.Grid.SetColumn(plus, 2); panel.Children.Add(plus);
        Watch(property, Sync);
        var border = new WC.Border { Child = panel, CornerRadius = new(6), BorderThickness = new(1), HorizontalAlignment = W.HorizontalAlignment.Left };
        border.SetResourceReference(WC.Border.BorderBrushProperty, "Fusion.Border"); return border;
    }
    private WC.ComboBox Font(string property)
    {
        string[]? available = null; bool loading = false;
        var view = Choice(property, () => Get<string>(property), value => state.Set(property, value), () => (available ?? [Get<string>(property)]).Append(Get<string>(property)).Distinct().Select(x => new ChoiceItem(x, x)).ToArray());
        Watch(property, () => { if (!view.Items.Cast<ChoiceItem>().Any(x => Equals(x.Value, Get<string>(property)))) Refresh(); view.SelectedValue = Get<string>(property); });
        view.DropDownOpened += async (_, _) => { if (available is not null || loading) return; loading = true; try { available = await Task.Run(FontSettingsPolicy.Families); Refresh(); } catch (Exception ex) { host.NativeSettingsError(ex); } finally { loading = false; } }; return view;
    }
    private WC.Button Button(string text, Action action)
    {
        var view = WpfThemeResources.Button(text, null, () => { try { action(); } catch (Exception ex) { host.NativeSettingsError(ex); } });
        view.MinHeight = 36; view.Margin = new(0, 0, 12, 4); refresh.Add(() => WpfThemeResources.SetButton(view, text, null)); return view;
    }
    private WC.Button AsyncButton(string text, Func<Task> action)
    {
        var view = WpfThemeResources.Button(text, null, () => { }); view.MinHeight = 36; view.Margin = new(0, 0, 12, 4);
        view.Click += async (_, _) => { view.IsEnabled = false; try { await action(); } catch (Exception ex) { host.NativeSettingsError(ex); } finally { view.IsEnabled = true; } };
        refresh.Add(() => WpfThemeResources.SetButton(view, text, null)); return view;
    }
    private static WC.WrapPanel Flow(params W.UIElement[] children) { var panel = new WC.WrapPanel(); foreach (var child in children) { if (child is W.FrameworkElement element && element.Margin == new W.Thickness()) element.Margin = new(0, 0, 12, 4); panel.Children.Add(child); } return panel; }
    private T Enabled<T>(T view, string property, Func<bool> enabled) where T : W.FrameworkElement { Watch(property, () => view.IsEnabled = enabled()); return view; }
    private void Appearance()
    {
        Section("主题与文字"); var themes = new List<WC.Button>();
        foreach (var mode in new[] { ApplicationThemeMode.Day, ApplicationThemeMode.Night })
        {
            var button = Button(mode == ApplicationThemeMode.Day ? "日间" : "夜间", () => host.NativeSetTheme(mode)); button.MinWidth = 96; themes.Add(button);
            void Update() { bool selected = Get<ApplicationThemeMode>(nameof(ApiSettings.ThemeMode)) == mode; button.BorderBrush = WpfDrawing.Brush(selected ? UiTheme.Current.Accent : UiTheme.Current.Border); button.Foreground = WpfDrawing.Brush(selected ? UiTheme.Current.Accent : UiTheme.Current.Text); }
            Watch(nameof(ApiSettings.ThemeMode), Update); refresh.Add(Update);
        }
        Row("主题", Flow(themes.ToArray()));
        Row("字体模式", Choice(nameof(ApiSettings.UiFontMode), ["产品默认", "自定义字体"]));
        var fonts = Font(nameof(ApiSettings.UiFontFamily)); var actual = DynamicText(() => FontManager.ResolveUiProfile(host.NativeDraftSnapshot()).PrimaryFamily);
        Watch(nameof(ApiSettings.UiFontMode), () => { bool custom = Get<UiFontMode>(nameof(ApiSettings.UiFontMode)) == UiFontMode.Custom; fonts.Visibility = custom ? W.Visibility.Visible : W.Visibility.Collapsed; actual.Visibility = custom ? W.Visibility.Collapsed : W.Visibility.Visible; });
        Row("字体 / 字号", Flow(fonts, actual, Number(nameof(ApiSettings.UiFontSize), 7, 24, .5m, 1)));
        Section("游戏库外观"); Row("封面样式", Choice("LibraryCoverStyle", () => host.NativeLibraryPreferences.Portrait, x => host.NativeSaveLibraryPreferences(host.NativeLibraryPreferences with { Portrait = (bool)x }), () => [new(true, UiLanguage.Text("海报封面")), new(false, UiLanguage.Text("横向封面"))]));
        Row("封面大小", Choice("LibraryCoverSize", () => LibraryCoverSizes.IndexFor(host.NativeLibraryPreferences.CoverWidth), x => host.NativeSaveLibraryPreferences(host.NativeLibraryPreferences with { CoverWidth = LibraryCoverSizes.Widths[(int)x] }), () => LibraryCoverSizes.Labels.Select((x, i) => new ChoiceItem(i, UiLanguage.Text(x))).ToArray()));
        Row("", Text("封面视图按窗口宽度自动排满每行；列表缩略图、行高和首页最近使用也跟随大小设置，选择后立即保存。"));
        var advanced = new WC.StackPanel { Visibility = W.Visibility.Collapsed };
        advanced.Children.Add(Choice(nameof(ApiSettings.ThemeMode), ["日间", "夜间", "自定义"]));
        var colors = new[] { ("主背景", nameof(ApiSettings.CustomThemeMainBackground)), ("次级背景", nameof(ApiSettings.CustomThemeSecondaryBackground)), ("正文", nameof(ApiSettings.CustomThemeText)), ("次级文字", nameof(ApiSettings.CustomThemeSecondaryText)), ("强调色", nameof(ApiSettings.CustomThemeAccent)), ("边框", nameof(ApiSettings.CustomThemeBorder)) };
        advanced.Children.Add(Flow(colors.Select(pair => Button(pair.Item1, () => host.NativeChooseThemeColor(pair.Item2))).ToArray()));
        Row("", Button("自定义主题", () => advanced.Visibility = advanced.Visibility == W.Visibility.Visible ? W.Visibility.Collapsed : W.Visibility.Visible)); Row("", advanced);
        Row("", Button("恢复默认外观", () => { host.NativeResetLibraryAppearance(); Refresh(); }));
    }
    private void General()
    {
        Section("语言"); Row("界面语言", Choice(nameof(ApiSettings.InterfaceLanguage), ["简体中文", "English", "Русский"])); Row("", Text("保存后切换整个软件的界面语言。游戏译文语言在“翻译方案”中设置。"));
        Section("启动与关闭"); Row("游戏启动方式", CheckAction("默认翻译并启动游戏", () => host.NativeDefaultLaunch, value => host.NativeDefaultLaunch = value));
        Row("关闭主窗口时", Choice(nameof(ApiSettings.CloseMainWindowBehavior), ["退出程序", "最小化到托盘"])); Row("", Text("启动方式立即保存。切换游戏时，已经运行的游戏会继续运行。"));
    }
    private void Shortcuts()
    {
        Section("截图快捷键");
        foreach (var (label, action) in new[] { ("开始截图", InputActionId.StartCapture), ("取消当前截图", InputActionId.CancelCapture), ("显示 / 隐藏截图结果", InputActionId.ToggleResults), ("预览放大", InputActionId.PreviewZoomIn), ("预览缩小", InputActionId.PreviewZoomOut), ("预览恢复适应", InputActionId.PreviewResetFit) })
        {
            if (action == InputActionId.PreviewZoomIn) Section("预览操作");
            var display = new WC.TextBlock { MinWidth = 145, MinHeight = 36, VerticalAlignment = W.VerticalAlignment.Center, TextWrapping = W.TextWrapping.Wrap };
            display.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Text"); Identify(display, "Binding_" + action);
            var frame = new WC.Border { Child = display, Padding = new(10, 6, 10, 0), BorderThickness = new(1), CornerRadius = new(6), Margin = new(0, 0, 12, 4) };
            frame.SetResourceReference(WC.Border.BorderBrushProperty, "Fusion.Border");
            Rectangle Bounds() { var origin = frame.PointToScreen(new(0, 0)); var end = frame.PointToScreen(new(frame.ActualWidth, frame.ActualHeight)); return Rectangle.FromLTRB((int)origin.X, (int)origin.Y, (int)end.X, (int)end.Y); }
            void Update() => display.Text = UiLanguage.Text(host.NativeBindingText(action));
            host.NativeShortcutsChanged += Update; detach.Add(() => host.NativeShortcutsChanged -= Update); refresh.Add(Update);
            var edit = Button("修改", () => host.NativeBeginBinding(action, Bounds)); Identify(edit, "EditBinding_" + action);
            var clear = Button("清除", () => host.NativeSetBinding(action, new())); Identify(clear, "ClearBinding_" + action);
            var reset = Button("恢复默认", () => host.NativeSetBinding(action, host.NativeDefaultBinding(action))); Identify(reset, "ResetBinding_" + action);
            var menu = new WC.ContextMenu();
            foreach(var entry in new (string Text,Action Run)[]{("修改",()=>host.NativeBeginBinding(action,Bounds)),("清除",()=>host.NativeSetBinding(action,new())),("恢复默认",()=>host.NativeSetBinding(action,host.NativeDefaultBinding(action)))})
            {var item=new WC.MenuItem{Header=UiLanguage.Text(entry.Text)};item.Click+=(_,_)=>entry.Run();menu.Items.Add(item);}
            frame.ContextMenu=menu;
            Row(label, Flow(frame, edit, clear, reset));
        }
        Section("游戏内快捷键"); var game = Button("F8", host.NativeBeginGameShortcut); Identify(game, "EmbeddedToggleKey"); game.MinWidth = 200;
        void UpdateGame() => WpfThemeResources.SetButton(game, host.NativeGameShortcutText, null);
        host.NativeShortcutsChanged += UpdateGame; detach.Add(() => host.NativeShortcutsChanged -= UpdateGame); refresh.Add(UpdateGame);
        Row("游戏内扫描开关", Flow(game, Button("恢复默认", () => host.NativeSetGameShortcut("F8"))));
        Row("", Text("点击录入字母、数字、功能键或 Ctrl / Alt / Shift 组合键；Esc 取消。默认 F8。\n支持此快捷键的游戏在下次启动时生效；关闭扫描后，已排队的翻译仍会继续。"));
    }
    private void Capture()
    {
        Section("选区操作栏"); Row("确认按钮位置", Choice(nameof(ApiSettings.CaptureToolbarPlacement), ["选区边缘", "鼠标附近"]));
        Row("", Text("鼠标附近：选区完成后，操作栏显示在鼠标旁并保持固定。选区边缘：沿选区边缘显示。靠近屏幕边缘时自动避让。"));
        Section("截图与识别"); Row("截图时隐藏", Flow(Check("主窗口", nameof(ApiSettings.HideMainWindowDuringCapture)), Check("已有预览窗口", nameof(ApiSettings.HidePreviewWindowsDuringCapture))));
        Row("OCR 引擎", Text("RapidOCR（快速）", false)); Row("识别负载", Choice(nameof(ApiSettings.OcrLoad), ["标准", "低负载（实验）"]));
        Row("识别语言", Choice(nameof(ApiSettings.OcrLanguage), ["自动", "英语", "简体中文", "日语", "韩语", "混合语言"])); Row("已安装语言", DynamicText(() => host.NativeOcrLanguages));
        Row("文字整理", Choice(nameof(ApiSettings.CleanupStrength), ["关闭", "普通", "严格"])); Row("翻译输入", Choice(nameof(ApiSettings.TranslationTextSource), ["自动（推荐）", "整理后原文", "OCR 原文"]));
        Section("翻译图片"); Row("翻译图片", Check("启用译图（实验功能）", nameof(ApiSettings.ImageTranslationEnabled)));
        Row("背景处理", Choice(nameof(ApiSettings.BackgroundTreatment), ["精细修复", "均衡清理（实验）"]));
        Row("精细背景计算", Enabled(Choice(nameof(ApiSettings.BackgroundComputeDevice), ["CPU", "GPU（NVIDIA）"]), nameof(ApiSettings.BackgroundTreatment), () => Get<BackgroundTreatment>(nameof(ApiSettings.BackgroundTreatment)) == BackgroundTreatment.FineRepair));
        Row("", Text("均衡不加载重型背景模型；识别负载与背景计算分别控制。"));
        Section("译图文字与背景"); Row("译图字体模式", Choice(nameof(ApiSettings.OverlayFontMode), ["产品默认", "自定义字体"]));
        Row("译图字体", Enabled(Font(nameof(ApiSettings.OverlayFontFamily)), nameof(ApiSettings.OverlayFontMode), () => Get<OverlayFontMode>(nameof(ApiSettings.OverlayFontMode)) == OverlayFontMode.Custom));
        Row("译图字号", Flow(Enabled(Number(nameof(ApiSettings.OverlayFontSize), 0, 48), nameof(ApiSettings.OverlayFontMode), () => Get<OverlayFontMode>(nameof(ApiSettings.OverlayFontMode)) == OverlayFontMode.Custom), Text("0 表示自动适配")));
        Row("译图背景策略", Choice(nameof(ApiSettings.OverlayBackgroundStyle), Enum.GetValues<TranslationOverlayBackgroundStyle>().Select(UiStrings.BackgroundStyleName).ToArray()));
        Row("底板透明度", Enabled(Number(nameof(ApiSettings.OverlayBackgroundOpacity), 0, 255), nameof(ApiSettings.OverlayBackgroundStyle), () => Get<TranslationOverlayBackgroundStyle>(nameof(ApiSettings.OverlayBackgroundStyle)) == TranslationOverlayBackgroundStyle.LightOverlay));
    }
    private void Preview()
    {
        Section("默认内容"); Row("默认图片", Choice(nameof(ApiSettings.PreviewDefaultImage), ["原图", "译图（存在时）"])); Row("默认文本", Choice(nameof(ApiSettings.PreviewDefaultText), ["OCR 原文", "整理后原文", "译文（存在时）", "隐藏"]));
        Row("文本栏", Check("显示右侧文本栏", nameof(ApiSettings.PreviewTextPanelVisible))); Row("记住选择", Check("记住上次选择", nameof(ApiSettings.RememberLastPreviewSelection)));
        Section("窗口尺寸"); Row("窗口大小", Choice(nameof(ApiSettings.PreviewWindowSizingMode), Enum.GetValues<PreviewWindowSizingMode>().Select(UiStrings.PreviewSizeModeName).ToArray()));
        Row("固定宽度 / 高度", Enabled(Flow(Number(nameof(ApiSettings.FixedPreviewWidth), 820, 10000), Number(nameof(ApiSettings.FixedPreviewHeight), 560, 10000)), nameof(ApiSettings.PreviewWindowSizingMode), () => Get<PreviewWindowSizingMode>(nameof(ApiSettings.PreviewWindowSizingMode)) == PreviewWindowSizingMode.Fixed));
        Section("文本栏外观"); Row("文本栏字体模式", Choice(nameof(ApiSettings.PreviewTextFontMode), ["产品默认", "跟随软件界面字体", "自定义字体"]));
        Row("文本栏字体 / 大小", Flow(Enabled(Font(nameof(ApiSettings.PreviewTextFontFamily)), nameof(ApiSettings.PreviewTextFontMode), () => Get<PreviewTextFontMode>(nameof(ApiSettings.PreviewTextFontMode)) == PreviewTextFontMode.Custom), Enabled(Number(nameof(ApiSettings.PreviewTextFontSize), 7, 48, .5m, 1), nameof(ApiSettings.PreviewTextFontMode), () => Get<PreviewTextFontMode>(nameof(ApiSettings.PreviewTextFontMode)) == PreviewTextFontMode.Custom)));
        Row("", Text("这些选项只影响截图预览窗口。译图中的文字样式在“截图处理”中设置。"));
    }
    private void History()
    {
        Section("历史列表"); Row("截图历史缩略图", Choice(nameof(ApiSettings.HistoryThumbnailSize), ["小", "中", "大"])); Row("历史文字大小", Number(nameof(ApiSettings.HistoryTextSize), 8, 24, .5m, 1));
        Section("自动清理"); Row("数量清理", Flow(Check("启用", nameof(ApiSettings.HistoryCleanupByCount)), Enabled(Number(nameof(ApiSettings.HistoryLimit), 10, 200), nameof(ApiSettings.HistoryCleanupByCount), () => Get<bool>(nameof(ApiSettings.HistoryCleanupByCount)))));
        Row("保存天数", Flow(Check("启用", nameof(ApiSettings.HistoryCleanupByAge)), Enabled(Number(nameof(ApiSettings.HistoryRetentionDays), 1, 3650), nameof(ApiSettings.HistoryCleanupByAge), () => Get<bool>(nameof(ApiSettings.HistoryCleanupByAge)))));
        Row("空间上限（MB）", Flow(Check("启用", nameof(ApiSettings.HistoryCleanupBySpace)), Enabled(Number(nameof(ApiSettings.HistoryMaximumMegabytes), 32, 102400), nameof(ApiSettings.HistoryCleanupBySpace), () => Get<bool>(nameof(ApiSettings.HistoryCleanupBySpace)))));
        Row("", Text("只清理本地截图历史记录，不影响游戏翻译缓存。"));
        Section("缓存"); Row("截图会话缓存", Flow(Check("OCR 缓存", nameof(ApiSettings.OcrCacheEnabled)), Check("翻译缓存", nameof(ApiSettings.TranslationCacheEnabled))));
    }
    private void Artwork()
    {
        Section("游戏图片"); ChoiceItem[] Sources() => [new(ArtworkSource.Automatic, UiLanguage.Text("本地自动")), new(ArtworkSource.Online, UiLanguage.Text("网上匹配，缺少时保留本地图片"))];
        Row("默认图标来源", Choice("LibraryIconSource", () => host.NativeLibraryPreferences.IconSource, value => host.NativeSaveLibraryPreferences(host.NativeLibraryPreferences with { IconSource = (ArtworkSource)value }), Sources));
        Row("默认封面来源", Choice("LibraryCoverSource", () => host.NativeLibraryPreferences.CoverSource, value => host.NativeSaveLibraryPreferences(host.NativeLibraryPreferences with { CoverSource = (ArtworkSource)value }), Sources));
        Row("", Text("网上匹配使用游戏名称查找 Steam 图片；名称不唯一时请手动选择。\n本地模式提取程序图标，并在游戏启动后尝试截图。手动图片不会被自动覆盖。")); Row("已有游戏", AsyncButton("补齐默认图片", host.NativeFillLibraryArtwork));
        Section("记录与名称"); Row("最近记录", AsyncButton("设置保留数量", host.NativeEditLibraryRetention));
        Row("游戏名称", CheckAction("自动补齐本地化名称", () => host.NativeLibraryPreferences.AutomaticChineseNames, value => host.NativeSaveLibraryPreferences(host.NativeLibraryPreferences with { AutomaticChineseNames = value })));
        Row("", Text("使用各游戏选择的翻译方案，按当前界面语言在后台补齐译名，只发送游戏名称。不同语言的译名分别保存，手动名称会保留。\n未配置方案时保留原名。右键“标签与便签”可编辑名字或重新生成译名。")); Row("", AsyncButton("重试补齐译名", host.NativeRetryLibraryNames));
    }
    private void Storage()
    {
        Section("软件数据"); var path = new WC.TextBox { Text = AppDataPaths.Root, IsReadOnly = true, MinHeight = 36, VerticalContentAlignment = W.VerticalAlignment.Center };
        Identify(path, "DataDirectory"); Row("当前数据目录", path);
        var notice = Text("设置、游戏库、图片、截图历史、缓存及日志统一存放在这里。"); string? pending = null;
        Row("", Flow(Button("打开数据文件夹", () => host.NativeOpenFolder(AppDataPaths.Root)), Button("选择数据位置", () => { pending = host.NativeChooseStorageLocation() ?? pending; Refresh(); })));
        refresh.Add(() => { if (pending is not null) notice.Text = UiLanguage.Text(pending); }); Row("", notice);
        Section("存储说明"); Row("默认位置", Text("软件文件夹内的 data。目录不可写时会报告错误，不会转存到 C 盘。")); Row("游戏原存档", Text("游戏自己保存的存档仍在原位置，本软件不会移动。"));
    }
    private void Help()
    {
        Section("关于 Fusion"); Row("软件版本", Text(BuildIdentity.ProductVersion, false));
        Section("使用帮助"); Row("首次使用", Text("1. 将游戏启动文件加入游戏库，并打开游戏详情。\n2. 选择翻译方案；如提示未配置完成，点击旁边的“编辑”补齐接口、模型等设置。\n3. 点击“翻译并启动”。普通“启动游戏”默认不开启翻译。\n4. 进入菜单或对话页面，查看游戏内文字和本页翻译状态。"));
        Row("部分文字未译", Text("先看游戏详情中的“翻译状态”：区分未收到文字、等待翻译、接口失败、等待游戏应用和字体受阻。\n遇到失败可重试或更正单条译文；一般无需清空已有译文。反馈时提供当前画面和翻译状态报告。"));
        Row("翻译方式", Text("游戏内翻译通过对应引擎接入读取和替换文字；自动识别引擎不代表所有页面均已覆盖。\n屏幕翻译需要手动开启，在画面上显示译文；两种方式的状态分别显示。"));
        Section("诊断与数据"); Row("游戏退出诊断", Text("从本软件启动游戏后自动监测退出，最小化后继续。\n报告保存在本机，保留最近 50 次记录；可在游戏详情查看并导出。"));
        Row("截图识别诊断", Flow(Button("模型与运行环境管理",host.NativeOpenModelManager),Button("OCR 对比（主线）",()=>host.NativeOpenOcrComparison(false)),Button("OCR 对比（含 Windows 实验）",()=>host.NativeOpenOcrComparison(true))));
        Row("", Button("打开游戏诊断目录", host.NativeOpenGameDiagnostics));
        Row("数据配置", Text(host.NativeSettingsPath)); Row("密钥状态", DynamicText(() => host.NativeCredentialStatus));
        Row("当前运行环境", Text(FusionRuntime.Root + "\n缺少组件时会提示对应功能不可用；此测试版保持现有运行环境配置。"));
        Row("译文与设置", Text("游戏译文按游戏及翻译方案保存，可在“译文与术语”中更正、重译或导入导出。\n桌面接口密钥由当前 Windows 用户加密保存；部分旧接入方式会在游戏目录写入所需配置。"));
    }
    private static void ScrollPage(W.DependencyObject from, WI.MouseWheelEventArgs e)
    {
        for (var parent = System.Windows.Media.VisualTreeHelper.GetParent(from); parent is not null; parent = System.Windows.Media.VisualTreeHelper.GetParent(parent))
            if (parent is WC.ScrollViewer scroll) { scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta); break; }
        e.Handled = true;
    }
    public void Dispose() { foreach (var action in detach) action(); detach.Clear(); }
    private sealed record ChoiceItem(object Value, string Text) { public override string ToString() => Text; }
    private sealed class FieldRow : WC.Grid
    {
        protected override W.Size MeasureOverride(W.Size availableSize)
        { if (double.IsFinite(availableSize.Width)) ColumnDefinitions[0].Width = new(Math.Clamp(availableSize.Width / 4, 164, 206)); return base.MeasureOverride(availableSize); }
    }
}
