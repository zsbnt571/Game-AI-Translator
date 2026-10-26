using System.ComponentModel;
using System.Globalization;
using W = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Data;
using WI = System.Windows.Input;

namespace ScreenshotTranslationUiTester;

// Native fields bind to a plain draft. Existing profile persistence and test
// services remain behind explicit commands; no hidden input controls relay edits.
internal sealed class WpfProfileEditor : IDisposable
{
    private readonly IProfileEditorHost host;
    private readonly ApiProfileEditorState state;
    private readonly List<Action> chrome = [];
    private readonly List<Action> numberSync = [];
    private readonly WC.ComboBox common = Choice("CommonProfile"), editing = Choice("ProfileEditor");
    private readonly WC.PasswordBox key = new() { MinHeight = 36, Padding = new(10, 6, 10, 6) };
    private readonly WC.TextBox name;
    private readonly WC.TextBlock origin = Note(), usage = Note(), testStatus = Note(), saveStatus = Note();
    private readonly WC.StackPanel sourceDetails = new() { Visibility = W.Visibility.Collapsed };
    private readonly List<W.FrameworkElement> advancedRows = [];
    private readonly WC.Button sourceButton, testButton, returnButton, backButton;
    private bool syncing;
    internal WC.Border Defaults { get; }
    internal WC.Border Connection { get; }

    internal WpfProfileEditor(IProfileEditorHost host)
    {
        this.host = host; state = host.ProfileDraft; host.ProfileNameFocusRequested += FocusName;
        var defaults = Column("默认方案"); Defaults = Card(defaults);
        Row(defaults, "公共默认方案", common);
        Row(defaults, "", Text("公共默认及模式选择立即保存；跟随公共者随之切换，固定选择保持不变。"));
        var body = Column("方案与连接"); Connection = Card(body);
        Row(body, "正在编辑的方案", editing);
        var more = Button("更多 ▾", "ProfileMore", () => { });
        more.Click += (_, _) => Menu(more, [
            ("重命名", host.NativeRenameProfile), ("复制为独立方案", host.NativeCopyProfile), ("删除方案", host.NativeDeleteProfile)]);
        Row(body, "", Actions(Button("新建方案", "NewProfile", host.NativeNewProfile), more));
        name = Edit(nameof(state.Name), "ProfileName"); Row(body, "方案名称", name);
        sourceButton = Button("查看来源与使用范围 ▾", "ProfileUsage", () =>
        {
            sourceDetails.Visibility = sourceDetails.Visibility == W.Visibility.Visible ? W.Visibility.Collapsed : W.Visibility.Visible;
            RefreshSourceButton();
        });
        sourceDetails.Children.Add(origin); sourceDetails.Children.Add(usage);
        origin.Margin = new(0, 8, 0, 8);
        var source = new WC.StackPanel(); source.Children.Add(sourceButton); source.Children.Add(sourceDetails);
        Row(body, "来源与使用范围", source);
        Row(body, "接口地址", Edit(nameof(state.ApiUrl), "ApiUrl"));
        Identify(key, "ApiKey"); Row(body, "API 密钥", key);
        key.PasswordChanged += (_, _) => { if (!syncing) state.ApiKey = key.Password; };
        Row(body, "", Check("无需密钥（本地／免鉴权）", nameof(state.AllowEmptyKey), "NoCredential"));
        var noCredentialHelp = Text(ApiConnectionPolicy.NoCredentialHelp); Identify(noCredentialHelp, "NoCredentialHelp");
        Row(body, "", noCredentialHelp);
        Row(body, "模型", Edit(nameof(state.Model), "ProfileModel"));
        Row(body, "首响应 / 总超时（秒）", Actions(
            Number("FirstByteTimeout", () => state.FirstTimeout, v => state.FirstTimeout = v, 10, 90),
            Number("RequestTimeout", () => state.RequestTimeout, v => state.RequestTimeout = v, 30, 180)));
        Row(body, "", Check("展开高级 API 参数", nameof(state.AdvancedExpanded), "AdvancedApiToggle"));
        var json = Edit(nameof(state.AdvancedParameters), "AdvancedApiParameters");
        json.AcceptsReturn = true; json.TextWrapping = W.TextWrapping.Wrap; json.Height = 132; json.MaxLength = 32768;
        json.VerticalContentAlignment = W.VerticalAlignment.Top; json.VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto;
        json.PreviewMouseWheel += (_, e) => { WpfSettingsFields.ScrollEditor(json, e.Delta, json.IsKeyboardFocusWithin); e.Handled = true; };
        var jsonRow = Row(body, "请求参数（JSON）", json); Identify(jsonRow, "AdvancedApiRow"); advancedRows.Add(jsonRow);
        var examples = Button("常用参数示例 ▾", "ApiParametersExamples", () => { });
        examples.Click += (_, _) => Menu(examples, [
            ("DeepSeek：关闭思考", () => host.NativeApplyParameterExample("{\"thinking\":{\"type\":\"disabled\"},\"reasoning_effort\":null}")),
            ("DeepSeek：开启思考", () => host.NativeApplyParameterExample("{\"thinking\":{\"type\":\"enabled\"},\"reasoning_effort\":null}")),
            ("OpenAI：关闭思考（模型需支持 none）", () => host.NativeApplyParameterExample("{\"reasoning_effort\":\"none\",\"thinking\":null}")),
            ("OpenAI：低思考", () => host.NativeApplyParameterExample("{\"reasoning_effort\":\"low\",\"temperature\":null,\"thinking\":null}")),
            ("恢复自动参数", () => state.AdvancedParameters = "")]);
        advancedRows.Add(Row(body, "", examples));
        var hint = Text("留空或 {} 自动适配。JSON 字段覆盖默认参数，null 移除字段；model、messages、stream 由软件管理。仅影响当前 API 方案，保存后用于新请求。");
        Identify(hint, "AdvancedApiHint"); advancedRows.Add(Row(body, "", hint));
        testButton = Button("测试连接", "TestProfile", async () => await host.NativeTestProfileAsync());
        returnButton = Button(state.ReturnLabel, "ReturnProfile", () => host.NativeReturnFromProfile(true));
        backButton = Button("返回", "BackProfile", () => host.NativeReturnFromProfile(false));
        Row(body, "", Actions(testButton, Button("保存 API 方案", "SaveProfile", () => host.NativeSaveProfile(), true), returnButton, backButton));
        Identify(testStatus, "ProfileTestStatus"); Identify(saveStatus, "ProfileSaveStatus");
        Row(body, "", testStatus); Row(body, "", saveStatus);
        Row(body, "", Text("API 方案保存接口、密钥、模型、连接超时和高级参数。下方翻译内容独立保存，切换 API 方案不会改变它。"));
        common.SelectionChanged += (_, _) => Select(common, host.NativeSelectCommonProfile);
        editing.SelectionChanged += (_, _) => Select(editing, host.NativeSelectProfile);
        state.PropertyChanged += StateChanged;
        Refresh();
    }
    private void Select(WC.ComboBox box, Action<string> command)
    {
        if (syncing || box.SelectedItem is not ChoiceItem choice) return;
        command(choice.Id);
        // A cancelled unsaved-changes prompt leaves the draft ID unchanged.
        // Restore the selection even when no PropertyChanged was needed.
        RefreshChoices();
    }
    private void StateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case null: SyncState(); break;
            case nameof(state.ApiKey): SyncKey(); break;
            case nameof(state.FirstTimeout): case nameof(state.RequestTimeout): foreach (var sync in numberSync) sync(); break;
            case nameof(state.CommonChoices): case nameof(state.EditorChoices): case nameof(state.CommonId): case nameof(state.EditingId): RefreshChoices(); break;
            case nameof(state.AdvancedExpanded): RefreshDisclosure(); break;
            case nameof(state.Testing): testButton.IsEnabled = !state.Testing; break;
            case nameof(state.Returning): case nameof(state.ReturnLabel): RefreshReturn(); break;
            case nameof(state.SaveStatus): case nameof(state.TestStatus): case nameof(state.Origin): case nameof(state.Usage): RefreshStatus(); break;
        }
    }
    private void SyncState()
    {
        SyncKey(); RefreshChoices(); RefreshDisclosure(); RefreshStatus(); RefreshReturn();
        testButton.IsEnabled = !state.Testing; foreach (var sync in numberSync) sync();
    }
    private void SyncKey()
    {
        bool previous = syncing; syncing = true;
        try { if (key.Password != state.ApiKey) key.Password = state.ApiKey; }
        finally { syncing = previous; }
    }
    private void RefreshChoices()
    {
        bool previous = syncing; syncing = true;
        try
        {
            Fill(common, state.CommonChoices, state.CommonId); Fill(editing, state.EditorChoices, state.EditingId);
        }
        finally { syncing = previous; }
        static void Fill(WC.ComboBox box, ApiProfileChoice[] choices, string? id)
        {
            var items = choices.Select(p => new ChoiceItem(p.Id, p.Name +
                (p.Unsaved ? UiLanguage.Choose("（未保存）", " (unsaved)", " (не сохранено)") :
                 p.Incomplete ? UiLanguage.Choose("（未配置完成）", " (incomplete)", " (не настроен)") : ""))).ToArray();
            if (!box.Items.Cast<ChoiceItem>().SequenceEqual(items)) box.ItemsSource = items;
            box.SelectedValue = id;
        }
    }
    private void RefreshDisclosure() { foreach (var row in advancedRows) row.Visibility = state.AdvancedExpanded ? W.Visibility.Visible : W.Visibility.Collapsed; }
    private void RefreshReturn()
    {
        returnButton.Visibility = backButton.Visibility = state.Returning ? W.Visibility.Visible : W.Visibility.Collapsed;
        WpfThemeResources.SetButton(returnButton, UiLanguage.Text(state.ReturnLabel), null);
    }
    private void RefreshStatus()
    {
        origin.Text = UiLanguage.Text(state.Origin); usage.Text = UiLanguage.Text(state.Usage);
        testStatus.Text = UiLanguage.Text(state.TestStatus); saveStatus.Text = UiLanguage.Text(state.SaveStatus);
    }
    private void RefreshSourceButton() => WpfThemeResources.SetButton(sourceButton,
        UiLanguage.Text("查看来源与使用范围") + (sourceDetails.Visibility == W.Visibility.Visible ? " ▴" : " ▾"), null);
    internal void Refresh() { foreach (var apply in chrome) apply(); SyncState(); RefreshSourceButton(); }
    internal void FocusName() { name.Focus(); name.SelectAll(); name.BringIntoView(); }
    private WC.TextBox Edit(string property, string id)
    {
        var edit = new WC.TextBox { MinHeight = 36 }; Identify(edit, id);
        edit.SetBinding(WC.TextBox.TextProperty, Binding(property)); return edit;
    }
    private WD.Binding Binding(string property) => new(property) { Source = state, Mode = WD.BindingMode.TwoWay, UpdateSourceTrigger = WD.UpdateSourceTrigger.PropertyChanged };
    private WC.CheckBox Check(string text, string property, string id)
    {
        var box = new WC.CheckBox { VerticalContentAlignment = W.VerticalAlignment.Center, MinHeight = 30 };
        Identify(box, id); box.SetBinding(WC.Primitives.ToggleButton.IsCheckedProperty, Binding(property));
        chrome.Add(() => box.Content = new WC.TextBlock { Text = UiLanguage.Text(text), TextWrapping = W.TextWrapping.Wrap }); return box;
    }
    private WC.Button Button(string text, string id, Action action, bool primary = false)
    {
        var button = WpfThemeResources.Button(text, null, action, primary);
        button.HorizontalAlignment = W.HorizontalAlignment.Left; button.MinHeight = 36; button.Padding = new(12, 5, 12, 5); Identify(button, id);
        chrome.Add(() => { WpfThemeResources.SetButton(button, UiLanguage.Text(text), null, primary); if (primary) button.Background = WpfDrawing.Brush(SettingsColors.PrimaryFill); }); return button;
    }
    private WC.TextBlock Text(string text)
    {
        var block = Note(); chrome.Add(() => block.Text = UiLanguage.Text(text)); return block;
    }
    private WC.StackPanel Column(string heading)
    {
        var body = new WC.StackPanel(); var title = Text(heading); title.FontWeight = W.FontWeights.Bold; title.Margin = new(0, 0, 0, 20);
        title.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Text");
        chrome.Add(() => title.FontSize = host.Font.SizeInPoints * 96d / 72 * 1.1); body.Children.Add(title); return body;
    }
    private WC.Grid Row(WC.StackPanel body, string label, W.FrameworkElement value)
    {
        var row = new FieldRow { Margin = new(0, 0, 0, label.Length == 0 ? 12 : 16) };
        row.ColumnDefinitions.Add(new() { Width = new(206) }); row.ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) });
        var title = Text(label); title.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Text"); title.Margin = new(0, 6, 16, 0);
        row.Children.Add(title); WC.Grid.SetColumn(value, 1); row.Children.Add(value); body.Children.Add(row);
        if (label.Length > 0) chrome.Add(() => W.Automation.AutomationProperties.SetName(value, UiLanguage.Text(label)));
        return row;
    }
    private W.FrameworkElement Number(string id, Func<int> read, Action<int> write, int min, int max)
    {
        bool updating = false;
        var row = new WC.Grid { Width = 144, Height = 36, HorizontalAlignment = W.HorizontalAlignment.Left };
        row.ColumnDefinitions.Add(new() { Width = new(30) }); row.ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = new(30) });
        var edit = new WC.TextBox { TextAlignment = W.TextAlignment.Center, Padding = new(0, 6, 0, 6), BorderThickness = new(0), Background = System.Windows.Media.Brushes.Transparent };
        var minus = new WC.Button { Content = "−" }; var plus = new WC.Button { Content = "+" };
        foreach (var step in new[] { minus, plus }) step.SetResourceReference(WC.Control.StyleProperty, "Fusion.StepperButton");
        chrome.Add(() => { W.Automation.AutomationProperties.SetName(minus, UiLanguage.Choose("减小", "Decrease", "Уменьшить")); W.Automation.AutomationProperties.SetName(plus, UiLanguage.Choose("增大", "Increase", "Увеличить")); });
        row.Children.Add(minus); WC.Grid.SetColumn(edit, 1); row.Children.Add(edit); WC.Grid.SetColumn(plus, 2); row.Children.Add(plus);
        minus.Click += (_, _) => write(Math.Max(min, read() - 1)); plus.Click += (_, _) => write(Math.Min(max, read() + 1));
        void Sync() { updating = true; try { var text = read().ToString(CultureInfo.CurrentCulture); if (edit.Text != text) edit.Text = text; minus.IsEnabled = read() > min; plus.IsEnabled = read() < max; } finally { updating = false; } }
        edit.TextChanged += (_, _) => { if (!updating && int.TryParse(edit.Text, out int v) && v >= min && v <= max) write(v); };
        edit.LostKeyboardFocus += (_, _) => Sync();
        edit.PreviewKeyDown += (_, e) => { if (e.Key is WI.Key.Up or WI.Key.Down) { write(Math.Clamp(read() + (e.Key == WI.Key.Up ? 1 : -1), min, max)); e.Handled = true; } };
        numberSync.Add(Sync);
        var outline = new WC.Border { Child = row, CornerRadius = new(5), BorderThickness = new(1), HorizontalAlignment = W.HorizontalAlignment.Left };
        outline.SetResourceReference(WC.Border.BorderBrushProperty, "Fusion.Border"); outline.SetResourceReference(WC.Border.BackgroundProperty, "Fusion.Main");
        Identify(outline, id); Identify(edit, id + "Input"); return outline;
    }
    private static WC.ComboBox Choice(string id)
    {
        var box = new WC.ComboBox { Width = 320, MinHeight = 36, HorizontalAlignment = W.HorizontalAlignment.Left, SelectedValuePath = nameof(ChoiceItem.Id) };
        Identify(box, id);
        box.PreviewMouseWheel += (_, e) =>
        {
            if (box.IsDropDownOpen) return;
            for (var p = System.Windows.Media.VisualTreeHelper.GetParent(box); p is not null; p = System.Windows.Media.VisualTreeHelper.GetParent(p))
                if (p is WC.ScrollViewer scroll) { scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta); break; }
            e.Handled = true;
        };
        return box;
    }
    private static WC.TextBlock Note()
    {
        var note = new WC.TextBlock { TextWrapping = W.TextWrapping.Wrap }; note.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Muted"); return note;
    }
    private static WC.Border Card(WC.StackPanel body)
    {
        var card = new WC.Border { Child = body, CornerRadius = new(8), Padding = new(20, 20, 20, 8), BorderThickness = new(1) };
        card.SetResourceReference(WC.Border.BackgroundProperty, "Fusion.Secondary"); card.SetResourceReference(WC.Border.BorderBrushProperty, "Fusion.Border"); return card;
    }
    private static WC.WrapPanel Actions(params W.FrameworkElement[] items)
    {
        var row = new WC.WrapPanel(); foreach (var item in items) { item.Margin = new(0, 0, 12, 4); row.Children.Add(item); } return row;
    }
    private static void Menu(WC.Button button, (string Text, Action Run)[] actions)
    {
        var menu = new WC.ContextMenu { PlacementTarget = button, Placement = WC.Primitives.PlacementMode.Bottom };
        // Popups have their own presentation source; give them the same theme
        // explicitly, as the other native menus do.
        menu.Resources.MergedDictionaries.Add(WpfThemeResources.Create()); WpfThemeResources.Apply(menu.Resources);
        menu.FontFamily = button.FontFamily; menu.FontSize = button.FontSize;
        foreach (var (text, run) in actions) { var item = new WC.MenuItem { Header = UiLanguage.Text(text) }; item.Click += (_, _) => run(); menu.Items.Add(item); }
        button.ContextMenu = menu; menu.IsOpen = true;
    }
    private static void Identify(W.DependencyObject element, string id) => W.Automation.AutomationProperties.SetAutomationId(element, id);
    public void Dispose() { state.PropertyChanged -= StateChanged; host.ProfileNameFocusRequested -= FocusName; }
    private sealed record ChoiceItem(string Id, string Text) { public override string ToString() => Text; }
    private sealed class FieldRow : WC.Grid
    {
        protected override W.Size MeasureOverride(W.Size availableSize)
        {
            if (double.IsFinite(availableSize.Width)) ColumnDefinitions[0].Width = new(Math.Clamp(availableSize.Width / 4, 164, 206));
            return base.MeasureOverride(availableSize);
        }
    }
}
