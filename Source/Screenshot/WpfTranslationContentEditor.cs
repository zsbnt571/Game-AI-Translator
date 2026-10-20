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

    internal WpfTranslationContentEditor(ITranslationContentHost host)
    {
        this.host = host; state = host.ContentDraft;
        var body = new WC.StackPanel(); var title = Text("翻译内容");
        title.FontWeight = W.FontWeights.Bold; title.Margin = new(0, 0, 0, 20);
        title.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Text");
        chrome.Add(() => title.FontSize = host.Font.SizeInPoints * 96d / 72 * 1.1); body.Children.Add(title);
        Row(body, "目标语言", target);
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
        Row(body, "翻译风格", style); Identify(description, "TranslationStyleDescription"); Row(body, "", description);
        Row(body, "", Text(TranslationStylePolicy.FidelityHint));
        var prompt = new WC.TextBox { MinHeight = 112, Height = 112, AcceptsReturn = true, TextWrapping = W.TextWrapping.Wrap,
            VerticalContentAlignment = W.VerticalAlignment.Top, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto };
        Identify(prompt, "CustomTranslationPrompt"); prompt.SetBinding(WC.TextBox.TextProperty, Binding(nameof(state.Prompt)));
        prompt.PreviewMouseWheel += (_, e) => { WpfSettingsFields.ScrollEditor(prompt, e.Delta, prompt.IsKeyboardFocusWithin); e.Handled = true; };
        Row(body, "用户提示", prompt); Row(body, "", Text(TranslationStylePolicy.ProfileHint));
        var protections = new WC.WrapPanel();
        protections.Children.Add(Check("保留 ID / Code", nameof(state.PreserveIdentifiers)));
        protections.Children.Add(Check("保留数字", nameof(state.PreserveNumbers)));
        protections.Children.Add(Check("保留变量 / 占位符", nameof(state.PreserveVariables)));
        Row(body, "文字保护", protections);
        var save = WpfThemeResources.Button("保存翻译内容", null, () => host.NativeSaveTranslationContent(), primary: true);
        save.HorizontalAlignment = W.HorizontalAlignment.Left; save.MinHeight = 36; save.Padding = new(12, 5, 12, 5);
        Identify(save, "SaveTranslationContent");
        chrome.Add(() => { WpfThemeResources.SetButton(save, UiLanguage.Text("保存翻译内容"), null, true); save.Background = WpfDrawing.Brush(SettingsColors.PrimaryFill); });
        var actions = new WC.WrapPanel(); save.Margin = new(0, 0, 12, 4); actions.Children.Add(save); Row(body, "", actions);
        Identify(status, "TranslationContentStatus"); Row(body, "", status);
        Content = new WC.Border { Child = body, CornerRadius = new(8), Padding = new(20, 20, 20, 8), BorderThickness = new(1) };
        Content.SetResourceReference(WC.Border.BackgroundProperty, "Fusion.Secondary"); Content.SetResourceReference(WC.Border.BorderBrushProperty, "Fusion.Border");
        target.SelectionChanged += (_, _) => { if (!syncing && target.SelectedValue is string code) state.TargetLanguage = code; };
        source.SelectionChanged += (_, _) => { if (!syncing && source.SelectedValue is string value && Enum.TryParse<SourceLanguageMode>(value, out var mode)) state.SourceMode = mode; };
        style.SelectionChanged += (_, _) => { if (!syncing && style.SelectedValue is string value && Enum.TryParse<TranslationStyle>(value, out var mode)) state.Style = mode; };
        state.PropertyChanged += StateChanged; Refresh();
    }
    private void ReadLanguages()
    { if (!syncing) state.SetSourceLanguages(languageChecks.Where(p => p.Value.IsChecked == true).Select(p => p.Key)); }
    private void StateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null) { RefreshChoices(); RefreshStatus(); return; }
        if (e.PropertyName is nameof(state.TargetLanguage) or nameof(state.SourceMode) or nameof(state.SourceLanguages) or nameof(state.Style)) RefreshChoices();
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
            var modes = TranslationStylePolicy.Modes.AsEnumerable();
            if (state.Style == TranslationStyle.Literal) modes = modes.Append(TranslationStyle.Literal);
            Fill(style, modes.Select(value => new Item(value.ToString(), UiLanguage.Text(UiStrings.TranslationStyleName(value)))), state.Style.ToString());
            languages.Visibility = state.SourceMode == SourceLanguageMode.Mixed ? W.Visibility.Visible : W.Visibility.Collapsed;
            var selected = state.SourceLanguages;
            foreach (var pair in languageChecks) pair.Value.IsChecked = selected.Contains(pair.Key);
            description.Text = UiLanguage.Text(TranslationStylePolicy.Description(state.Style));
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
    private WC.TextBlock Text(string text) { var block = Note(); chrome.Add(() => block.Text = UiLanguage.Text(text)); return block; }
    private void Row(WC.StackPanel body, string label, W.FrameworkElement value)
    {
        var row = new FieldRow { Margin = new(0, 0, 0, label.Length == 0 ? 12 : 16) };
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
    public void Dispose() => state.PropertyChanged -= StateChanged;
    private sealed record Item(string Id, string Text) { public override string ToString() => Text; }
    private sealed class FieldRow : WC.Grid
    {
        protected override W.Size MeasureOverride(W.Size availableSize)
        {
            if (double.IsFinite(availableSize.Width)) ColumnDefinitions[0].Width = new(Math.Clamp(availableSize.Width / 4, 164, 206));
            return base.MeasureOverride(availableSize);
        }
    }
}
