using System.ComponentModel;
using W = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Data;
using WA = System.Windows.Automation;

namespace ScreenshotTranslationUiTester;

// The selected value is always an enum, never a translated caption or an
// automatically detected gender. Detection can change without making a manual choice.
internal sealed class CharacterSelectionOption(CharacterGenderSelection value) : INotifyPropertyChanged
{
    internal CharacterGenderSelection Value { get; } = value;
    private string label = "";
    public string Label => label;
    internal void Refresh(string automatic)
    {
        label = Value == CharacterGenderSelection.Auto ? NativeCharacterPanel.Format("自动（识别：{0}）", automatic)
            : UiLanguage.Text(Value switch { CharacterGenderSelection.Male => "男性", CharacterGenderSelection.Female => "女性", _ => "不指定（不使用性别提示）" });
        PropertyChanged?.Invoke(this, new(nameof(Label)));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class NativeCharacterPanel : WC.Grid
{
    private readonly GameCharacterProfiles profiles;
    private readonly bool selectionOnly;
    private readonly NativeRecordGrid records = new(("角色", 32), ("识别结果", 24), ("当前设置", 44));
    private readonly WC.TextBox search = NativeDialogControls.Input();
    private readonly WC.ComboBox selection = new() { MinHeight = 40, MinWidth = 260, MaxWidth = 640, HorizontalAlignment = W.HorizontalAlignment.Left };
    private readonly CharacterSelectionOption[] options = Enum.GetValues<CharacterGenderSelection>().Select(v => new CharacterSelectionOption(v)).ToArray();
    private readonly WC.TextBlock selectedName = new() { TextWrapping = W.TextWrapping.Wrap, FontWeight = W.FontWeights.SemiBold };
    private readonly WC.TextBlock identity = new() { TextWrapping = W.TextWrapping.Wrap };
    private readonly WC.TextBlock result = new() { TextWrapping = W.TextWrapping.Wrap };
    private readonly WC.TextBlock count = new() { TextWrapping = W.TextWrapping.Wrap };
    private readonly WC.TextBlock status = new() { TextWrapping = W.TextWrapping.Wrap };
    private readonly WC.TextBlock empty = NativeDialogControls.Text("尚未找到可可靠区分的角色。开启翻译或准备游戏文本后会自动补充；这不影响正常翻译。");
    private readonly WC.Button restore;
    private GameCharacterProfileView[] snapshots = [];
    private string selectedId = "", statusKey = "正在读取角色…";
    private string savingId = "";
    private CharacterGenderSelection pendingSelection;
    private bool refreshing, saving;
    private volatile bool listening;
    private int queued;

    internal NativeCharacterPanel(string executable,bool selectionOnly=false)
    {
        this.selectionOnly=selectionOnly;
        profiles = GameCharacterProfiles.For(executable);
        restore = NativeDialogControls.Button("恢复自动识别", () => _ = SaveAsync(CharacterGenderSelection.Auto));
        restore.MinHeight = 40; restore.Margin = new(0, 10, 0, 0); restore.HorizontalAlignment = W.HorizontalAlignment.Left;
        selection.ItemsSource = options;
        var optionText = new W.FrameworkElementFactory(typeof(WC.TextBlock));
        optionText.SetBinding(WC.TextBlock.TextProperty, new WD.Binding(nameof(CharacterSelectionOption.Label)));
        optionText.SetValue(WC.TextBlock.TextWrappingProperty, W.TextWrapping.Wrap);
        optionText.SetValue(WC.TextBlock.MaxWidthProperty, 580d);
        selection.ItemTemplate = new W.DataTemplate { VisualTree = optionText };
        selection.PreviewMouseWheel += (_, e) => { if (!selection.IsDropDownOpen) { WpfScrollRouting.RouteToParent(selection, e); e.Handled = true; } };
        Identify(search, "CharacterSearch", "搜索角色"); Identify(records.View, "CharacterList", "角色");
        Identify(selection, "CharacterGenderSelection", "角色性别"); Identify(restore, "CharacterRestoreAutomatic", "恢复自动识别");
        Identify(result, "CharacterDetectedGender", "识别结果"); Identify(status, "CharacterSaveStatus", "");
        WA.AutomationProperties.SetLiveSetting(status, WA.AutomationLiveSetting.Polite);
        search.MinHeight = 38; search.Margin = new(0, 0, 0, 10);
        records.View.MinHeight = 130;
        records.View.SetResourceReference(WC.DataGrid.HorizontalGridLinesBrushProperty, "Fusion.Border");
        foreach (var column in records.View.Columns)
        {
            var header = new W.FrameworkElementFactory(typeof(WC.TextBlock));
            header.SetBinding(WC.TextBlock.TextProperty, new WD.Binding());
            header.SetValue(WC.TextBlock.TextWrappingProperty, W.TextWrapping.Wrap);
            column.HeaderTemplate = new W.DataTemplate { VisualTree = header };
        }
        var searchCaption = NativeDialogControls.Text("搜索角色"); searchCaption.Margin = new(0, 0, 0, 6);
        WA.AutomationProperties.SetLabeledBy(search, searchCaption);
        var choiceCaption = NativeDialogControls.Text("角色性别"); choiceCaption.Margin = new(0, 10, 0, 6);
        WA.AutomationProperties.SetLabeledBy(selection, choiceCaption);
        foreach (var block in new[] { selectedName, identity, result, count, status }) block.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Text");
        identity.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Muted");
        count.Margin = new(0, 0, 0, 8); selectedName.Margin = new(0, 12, 0, 6); result.Margin = new(0, 8, 0, 0); status.Margin = new(0, 10, 0, 0);
        empty.Margin = new(14); empty.VerticalAlignment = W.VerticalAlignment.Center;
        var list = new WC.Grid { MinHeight = 130 }; list.Children.Add(records.View); list.Children.Add(empty);
        var fields = new WC.StackPanel(); fields.Children.Add(selectedName); fields.Children.Add(identity); fields.Children.Add(choiceCaption); fields.Children.Add(selection); fields.Children.Add(result); fields.Children.Add(restore);
        if(!selectionOnly)
        {
            Add(NativeDialogControls.Text("增强开启后默认自动识别；无法确定时照常翻译。手动选择后可恢复自动。"));
            Add(NativeDialogControls.Text("不清空已有译文，也不强制重译。中文翻译保持现有流程。"));
            Add(searchCaption); Add(search); Add(count); Add(list, true);
        }
        Add(NativeDialogControls.Text("角色设置会保存。需在“目标语言”下方开启“角色与性别增强”后，才会用于翻译。"));
        Add(fields); Add(status);
        search.TextChanged += (_, _) => RefreshRows();
        records.SelectionChanged += (_, _) => { if (!refreshing) { selectedId = (records.CurrentRow?.Tag as GameCharacterProfileView)?.CharacterId ?? ""; RefreshSelection(); } };
        selection.SelectionChanged += async (_, _) => { if (!refreshing && !saving && selection.SelectedItem is CharacterSelectionOption option && selectedId.Length > 0) await SaveAsync(option.Value); };
        Loaded += (_, _) => { if (!listening) { listening = true; profiles.Changed += ProfilesChanged; UiLanguage.LanguageChanged += LanguageChanged; } RefreshProfiles(); };
        Unloaded += (_, _) => { if (listening) { listening = false; profiles.Changed -= ProfilesChanged; UiLanguage.LanguageChanged -= LanguageChanged; } };
        RefreshProfiles();
    }

    // Selection comes from a row carrying an existing native ID, never from a
    // name match or edited glossary text. The standalone view uses the same store.
    internal void SelectCharacter(string id)
    {
        selectedId=id;RefreshProfiles();
    }

    internal static string Format(string key, params object[] values) => string.Format(System.Globalization.CultureInfo.CurrentCulture, UiLanguage.Text(key), values);
    private static string Gender(GameCharacterProfileView? view) => UiLanguage.Text(view?.Conflict == true ? "信息有冲突" : view?.AutomaticGender switch { "male" => "男性", "female" => "女性", _ => "尚未确定" });
    private static string Name(GameCharacterProfileView view) => string.IsNullOrWhiteSpace(view.DisplayName) ? view.CharacterId : view.DisplayName;
    private static string Current(GameCharacterProfileView view) => view.Selection == CharacterGenderSelection.Auto ? Format("自动（识别：{0}）", Gender(view))
        : Format("手动：{0}", UiLanguage.Text(view.Selection switch { CharacterGenderSelection.Male => "男性", CharacterGenderSelection.Female => "女性", _ => "不指定（不使用性别提示）" }));
    private void Add(W.UIElement child, bool fill = false) { int row = RowDefinitions.Count; RowDefinitions.Add(new() { Height = fill ? new(1, W.GridUnitType.Star) : W.GridLength.Auto }); WC.Grid.SetRow(child, row); Children.Add(child); }
    private static void Identify(W.FrameworkElement view, string id, string label) { WA.AutomationProperties.SetAutomationId(view, id); if (label.Length > 0) WpfUiLanguage.Property(view, WA.AutomationProperties.NameProperty, label); }
    private void ProfilesChanged()
    {
        if (!listening || Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref queued, 1) != 0) return;
        Dispatcher.BeginInvoke(() => { Interlocked.Exchange(ref queued, 0); if (listening) RefreshProfiles(); });
    }
    private void LanguageChanged(object? sender, EventArgs e) { if (Dispatcher.CheckAccess()) RefreshRows(); else Dispatcher.BeginInvoke(RefreshRows); }
    private void RefreshProfiles()
    {
        try
        {
            snapshots = profiles.Snapshot();
            if (profiles.LastError == "unreadable") statusKey = "读取角色失败，正常翻译不受影响。";
            else if (statusKey == "正在读取角色…" || statusKey == "读取角色失败，正常翻译不受影响。") statusKey = "";
            RefreshRows();
        }
        catch { statusKey = "读取角色失败，正常翻译不受影响。"; status.Text = UiLanguage.Text(statusKey); }
    }
    private void RefreshRows()
    {
        if (refreshing) return;
        if(selectionOnly){RefreshSelection();return;}
        refreshing = true;
        try
        {
            string query = search.Text.Trim();
            var visible = snapshots.Where(x => query.Length == 0 || Name(x).Contains(query, StringComparison.OrdinalIgnoreCase) || x.CharacterId.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.CharacterId, StringComparer.Ordinal).ToArray();
            var existing = records.Rows.ToDictionary(x => ((GameCharacterProfileView)x.Tag!).CharacterId, StringComparer.Ordinal);
            var wanted = new HashSet<string>(visible.Select(x => x.CharacterId), StringComparer.Ordinal);
            foreach (var row in records.Rows.Where(x => !wanted.Contains(((GameCharacterProfileView)x.Tag!).CharacterId)).ToArray()) records.Rows.Remove(row);
            for (int i = 0; i < visible.Length; i++)
            {
                var profile = visible[i];
                if (!existing.TryGetValue(profile.CharacterId, out var row)) { row = new NativeRecordRow(3); records.Rows.Insert(i, row); }
                else if (records.Rows.IndexOf(row) != i) records.Rows.Move(records.Rows.IndexOf(row), i);
                row.Tag = profile; row.Cells[0].Value = Name(profile); row.Cells[1].Value = Gender(profile); row.Cells[2].Value = Current(profile);
            }
            var selected = records.Rows.FirstOrDefault(x => ((GameCharacterProfileView)x.Tag!).CharacterId == selectedId);
            if (selected is null) selected = records.Rows.FirstOrDefault();
            if (!ReferenceEquals(records.View.SelectedItem, selected)) records.View.SelectedItem = selected;
            selectedId = (selected?.Tag as GameCharacterProfileView)?.CharacterId ?? "";
            empty.Visibility = visible.Length == 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
            empty.Text = snapshots.Length == 0 ? "尚未找到可可靠区分的角色。开启翻译或准备游戏文本后会自动补充；这不影响正常翻译。" : "没有符合搜索的角色。";
            count.Text = Format("已读取 {0} 个角色", snapshots.Length);
            foreach (var column in records.View.Columns) WpfUiLanguage.Property(column, WC.DataGridColumn.HeaderProperty);
        }
        finally { refreshing = false; }
        RefreshSelection();
    }
    private void RefreshSelection()
    {
        bool previousRefreshing = refreshing; refreshing = true;
        try
        {
            var view = snapshots.FirstOrDefault(x => x.CharacterId == selectedId);
            selectedName.Text = view is null ? UiLanguage.Text("选择角色") : Name(view);
            identity.Text = view is null ? "" : Format("角色标识：{0}", view.CharacterId);
            result.Text = view is null ? "" : UiLanguage.Text("识别结果") + ": " + Gender(view);
            if(view?.RosterOnly==true)result.Text+="\n"+UiLanguage.Text("已读取角色名册，尚未确认其台词。设置会保留，身份明确后才用于翻译。");
            else if(view?.CatalogOnly==true)result.Text+="\n"+UiLanguage.Text("该角色目前用于资源预翻译；实时说话者尚未确认时，继续使用普通翻译。");
            foreach (var option in options) option.Refresh(Gender(view));
            var selected = options.First(x => x.Value == (saving && selectedId == savingId ? pendingSelection : view?.Selection ?? CharacterGenderSelection.Auto));
            if (!ReferenceEquals(selection.SelectedItem, selected)) selection.SelectedItem = selected;
            selection.IsEnabled = view is not null && !saving;
            restore.IsEnabled = view is not null && view.Selection != CharacterGenderSelection.Auto && !saving;
            status.Text = UiLanguage.Text(statusKey);
        }
        finally { refreshing = previousRefreshing; }
    }
    private async Task SaveAsync(CharacterGenderSelection value)
    {
        string id = selectedId; if (saving || id.Length == 0) return;
        saving = true; savingId = id; pendingSelection = value; statusKey = "正在保存角色设置…"; RefreshSelection();
        bool saved = false;
        try { saved = await Task.Run(() => profiles.SetSelection(id, value)); }
        catch { }
        finally
        {
            // The same panel can be hosted by WPF or the legacy WinForms entry.
            // Do not depend on a captured synchronization context for UI writes.
            if (!Dispatcher.HasShutdownStarted)
                await Dispatcher.InvokeAsync(() =>
                {
                    saving = false; savingId = "";
                    statusKey = saved ? value == CharacterGenderSelection.Auto ? "已恢复自动识别。" : "角色设置已保存。" : "角色设置未保存，请重试。原有设置和译文保持不变。";
                    RefreshProfiles();
                });
        }
    }
}

internal sealed class NativeCharacterWindow : NativeDialogWindow
{
    internal NativeCharacterWindow(GameInfo game) : base("角色与性别", 960, 760)
    {
        MinWidth = 620; MinHeight = 540;
        var panel = new NativeCharacterPanel(game.ExePath);
        Content = new TranslationPage(panel) { Margin = new(20) };
    }
}
