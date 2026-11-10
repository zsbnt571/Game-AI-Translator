using System.ComponentModel;
using W = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Data;

namespace ScreenshotTranslationUiTester;

internal sealed class WpfTranslationContentEditor : IDisposable
{
    private readonly ITranslationContentHost host;
    private readonly TranslationContentEditorState state;
    private readonly List<Action> chrome = [];
    private readonly WC.ComboBox target = Choice("TargetLanguage"), source = Choice("SourceLanguageMode"), style = Choice("TranslationStyle");
    private readonly WC.WrapPanel languages = new();
    private readonly Dictionary<string, WC.CheckBox> languageChecks = [];
    private readonly WC.TextBlock description = Note(), status = Note();
    private bool syncing;
    internal WC.Border Content { get; }
    internal WC.StackPanel Footer { get; } = new();

    internal WpfTranslationContentEditor(ITranslationContentHost host)
    {
        this.host = host; state = host.ContentDraft;
        var body = new WC.StackPanel(); var title = Text("翻译内容");
        title.FontWeight = W.FontWeights.Bold; title.Margin = new(0, 0, 0, 20);
        title.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Text");
        chrome.Add(() => title.FontSize = host.Font.SizeInPoints * 96d / 72 * 1.1); body.Children.Add(title);
        var targetColumn = new WC.StackPanel(); Identify(targetColumn, "TargetLanguageGroup");
        targetColumn.Children.Add(target);
        chrome.Add(() => W.Automation.AutomationProperties.SetName(target, UiLanguage.Text("目标语言")));
        var characterEnhancement = Check("角色与性别增强", nameof(state.CharacterGenderEnhancementEnabled));
        characterEnhancement.Margin = new(0, 10, 0, 4);
        targetColumn.Children.Add(characterEnhancement);
        var characterHint = Text("根据可靠角色信息调整译文。关闭后使用普通翻译，已有译文和角色设置会保留。");
        Identify(characterHint, "CharacterGenderEnhancementHint");
        chrome.Add(() => characterHint.FontSize = Math.Max(12, host.Font.SizeInPoints * 96d / 72 * .92));
        targetColumn.Children.Add(characterHint);
        Row(body, "目标语言", targetColumn);
        var sourceColumn = new WC.StackPanel(); sourceColumn.Children.Add(source);
        languages.Margin = new(0, 8, 0, 0); Identify(languages, "SourceLanguageCombination");
        foreach (var (code, label) in TranslationLanguagePolicy.Choices)
        {
            var check = new WC.CheckBox { MinHeight = 30, Margin = new(0, 0, 16, 4), VerticalContentAlignment = W.VerticalAlignment.Center };
            Identify(check, "SourceLanguage_" + code); languageChecks.Add(code, check); languages.Children.Add(check);
            chrome.Add(() => { check.Content = UiLanguage.Text(label); W.Automation.AutomationProperties.SetName(check, UiLanguage.Text(label)); });
            check.Checked += (_, _) => ReadLanguages(); check.Unchecked += (_, _) => ReadLanguages();
        }
        sourceColumn.Children.Add(languages);
        var sourceHint = Text("选择“自选组合”可勾选多种原文语言，至少选择一种。备用语言包不一起翻译；语言选项名称和已是目标语言的文字默认保留。");
        sourceHint.Margin = new(0, 8, 0, 0); sourceColumn.Children.Add(sourceHint); Row(body, "原文语言", sourceColumn);
        Row(body, "提示词预设", style); Identify(description, "TranslationStyleDescription"); Row(body, "", description);
        var library= WpfThemeResources.Button("管理预设与词库",null,host.NativeOpenTranslationLibrary);
        Identify(library,"ManageTranslationLibrary");library.HorizontalAlignment=W.HorizontalAlignment.Left;
        Row(body,"",library);
        var packs=Note();Identify(packs,"SelectedGlossaryPacks");
        void UpdatePacks()
        {
            var names=TranslationLibrary.Packs().ToDictionary(p=>p.Id,p=>TranslationLibrary.DisplayName(p));
            packs.Text=state.PackIds.Length==0?UiLanguage.Format($"已有 {names.Count} 个词库可选，当前未启用。点“管理预设与词库”，在“词库”页勾选后保存。单游戏术语仍保留。"):UiLanguage.Format($"已选用：{string.Join(", ",state.PackIds.Select(id=>names.GetValueOrDefault(id,UiLanguage.Text("不可用词库"))))}");
        }
        chrome.Add(UpdatePacks);state.PropertyChanged+=PacksChanged;
        void PacksChanged(object? sender,PropertyChangedEventArgs e){if(e.PropertyName is null or nameof(state.PackIds))UpdatePacks();}
        extraDetach=()=>state.PropertyChanged-=PacksChanged;
        Row(body,"词库",packs);
        var prompt = new WC.TextBox { MinHeight = 112, Height = 112, AcceptsReturn = true, TextWrapping = W.TextWrapping.Wrap,
            VerticalContentAlignment = W.VerticalAlignment.Top, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto };
        Identify(prompt, "CustomTranslationPrompt"); prompt.SetBinding(WC.TextBox.TextProperty, Binding(nameof(state.Prompt)));
        prompt.PreviewMouseWheel += (_, e) => { WpfSettingsFields.ScrollEditor(prompt, e.Delta, prompt.IsKeyboardFocusWithin); e.Handled = true; };
        Row(body, "附加要求", prompt); Row(body, "", Text("原有自定义提示保留在这里，与所选预设一起使用。"));
        Row(body, "", Text(TranslationStylePolicy.ProfileHint));
        var protections = new WC.WrapPanel();
        protections.Children.Add(Check("保留 ID / Code", nameof(state.PreserveIdentifiers)));
        protections.Children.Add(Check("保留数字", nameof(state.PreserveNumbers)));
        protections.Children.Add(Check("保留变量 / 占位符", nameof(state.PreserveVariables)));
        Row(body, "文字保护", protections);
        var save = WpfThemeResources.Button("保存翻译内容", null, () => host.NativeSaveTranslationContent(), primary: true);
        save.HorizontalAlignment = W.HorizontalAlignment.Left; save.MinHeight = 36; save.Padding = new(12, 5, 12, 5);
        Identify(save, "SaveTranslationContent");
        chrome.Add(() => { WpfThemeResources.SetButton(save, UiLanguage.Text("保存翻译内容"), null, true); save.Background = WpfDrawing.Brush(SettingsColors.PrimaryFill); });
        var actions = new WC.WrapPanel(); save.Margin = new(0, 0, 12, 4); actions.Children.Add(save); Row(Footer, "", actions);
        Identify(status, "TranslationContentStatus"); Row(Footer, "", status); body.Children.Add(Footer);
        Content = new WC.Border { Child = body, CornerRadius = new(8), Padding = new(20, 20, 20, 8), BorderThickness = new(1) };
        Content.SetResourceReference(WC.Border.BackgroundProperty, "Fusion.Secondary"); Content.SetResourceReference(WC.Border.BorderBrushProperty, "Fusion.Border");
        target.SelectionChanged += (_, _) => { if (!syncing && target.SelectedValue is string code) state.TargetLanguage = code; };
        source.SelectionChanged += (_, _) => { if (!syncing && source.SelectedValue is string value && Enum.TryParse<SourceLanguageMode>(value, out var mode)) state.SourceMode = mode; };
        style.SelectionChanged += (_, _) => { if (!syncing && style.SelectedValue is string value && TranslationLibrary.Presets().FirstOrDefault(p=>p.Id==value) is {} chosen) state.Choose(chosen); };
        state.PropertyChanged += StateChanged; Refresh();
    }
    private void ReadLanguages()
    { if (!syncing) state.SetSourceLanguages(languageChecks.Where(p => p.Value.IsChecked == true).Select(p => p.Key)); }
    private void StateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null) { RefreshChoices(); RefreshStatus(); return; }
        if (e.PropertyName is nameof(state.TargetLanguage) or nameof(state.SourceMode) or nameof(state.SourceLanguages) or nameof(state.Style) or nameof(state.PresetId) or nameof(state.PresetText)) RefreshChoices();
        else if (e.PropertyName == nameof(state.Status)) RefreshStatus();
    }
    internal void Refresh() { foreach (var apply in chrome) apply(); RefreshChoices(); RefreshStatus(); }
    private void RefreshStatus() => status.Text = UiLanguage.Text(state.Status);
    private void RefreshChoices()
    {
        bool previous = syncing; syncing = true;
        try
        {
            Fill(target, UiTargetLanguageDisplay.DisplayNames.Select(label => new Item(UiTargetLanguageDisplay.CodeFromDisplay(label), UiLanguage.Text(label))), state.TargetLanguage);
            Fill(source, Enum.GetValues<SourceLanguageMode>().Select(value => new Item(value.ToString(), UiLanguage.Text(UiStrings.SourceLanguageName(value)))), state.SourceMode.ToString());
            var selectedId=TranslationLibrary.PresetId(state.Snapshot());
            var presets=TranslationLibrary.Presets().Where(p=>p.LegacyStyle!=TranslationStyle.Literal||p.Id==selectedId).ToList();
            if(!presets.Any(p=>p.Id==selectedId))presets.Add(new(selectedId,"已保存的自定义预设","已保留这份预设的内容。",state.PresetText));
            Fill(style, presets.Select(p=>new Item(p.Id,TranslationLibrary.DisplayName(p))),selectedId);
            languages.Visibility = state.SourceMode == SourceLanguageMode.Mixed ? W.Visibility.Visible : W.Visibility.Collapsed;
            var selected = state.SourceLanguages;
            foreach (var pair in languageChecks) pair.Value.IsChecked = selected.Contains(pair.Key);
            description.Text = TranslationLibrary.DisplayDescription(presets.First(p=>p.Id==selectedId));
        }
        finally { syncing = previous; }
        static void Fill(WC.ComboBox box, IEnumerable<Item> choices, string selected)
        {
            var items = choices.ToArray();
            if (!box.Items.Cast<Item>().SequenceEqual(items)) box.ItemsSource = items;
            box.SelectedValue = selected;
        }
    }
    private WD.Binding Binding(string property) => new(property) { Source = state, Mode = WD.BindingMode.TwoWay, UpdateSourceTrigger = WD.UpdateSourceTrigger.PropertyChanged };
    private WC.CheckBox Check(string text, string property)
    {
        var box = new WC.CheckBox { MinHeight = 30, Margin = new(0, 0, 12, 4), VerticalContentAlignment = W.VerticalAlignment.Center };
        Identify(box, property); box.SetBinding(WC.Primitives.ToggleButton.IsCheckedProperty, Binding(property));
        chrome.Add(() => { box.Content = new WC.TextBlock { Text = UiLanguage.Text(text), TextWrapping = W.TextWrapping.Wrap }; W.Automation.AutomationProperties.SetName(box, UiLanguage.Text(text)); }); return box;
    }
    private WC.TextBlock Text(string text) { var block = Note(); chrome.Add(() => block.Text = WpfSettingsLayout.Text(text)); return block; }
    private void Row(WC.StackPanel body, string label, W.FrameworkElement value)
    {
        var row = new WpfSettingsFieldRow { Margin = new(0, 0, 0, label.Length == 0 ? 12 : 16) };
        row.ColumnDefinitions.Add(new() { Width = new(206) }); row.ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) });
        var title = Text(label); title.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Text"); title.Margin = new(0, 6, 16, 0);
        row.Children.Add(title); WC.Grid.SetColumn(value, 1); row.Children.Add(value); body.Children.Add(row);
        if (label.Length > 0) chrome.Add(() => W.Automation.AutomationProperties.SetName(value, UiLanguage.Text(label)));
    }
    private static WC.ComboBox Choice(string id)
    {
        var box = new WC.ComboBox { Width = 320, MinHeight = 36, HorizontalAlignment = W.HorizontalAlignment.Left, SelectedValuePath = nameof(Item.Id) };
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
    { var note = new WC.TextBlock { TextWrapping = W.TextWrapping.Wrap }; note.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Muted"); return note; }
    private static void Identify(W.DependencyObject element, string id) => W.Automation.AutomationProperties.SetAutomationId(element, id);
    private Action? extraDetach;
    public void Dispose() { state.PropertyChanged -= StateChanged;extraDetach?.Invoke(); }
    private sealed record Item(string Id, string Text) { public override string ToString() => Text; }
}
