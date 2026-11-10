using System.Text.Json;
using W = System.Windows;
using WC = System.Windows.Controls;
using WA = System.Windows.Automation;

namespace ScreenshotTranslationUiTester;

internal sealed record LibraryTagUiState(bool Browser, LibraryTagDocument Document, LibraryTagFacet[] Facets, LibraryTagSelection[] Selected, string Notice);
internal interface ILibraryTagWorkspaceHost
{
    LibraryTagUiState TagUi { get; }
    void ToggleLibraryTag(LibraryTagSelection tag);
    void ClearLibraryTagFilters();
    void ClearLibraryTagGroup(string group);
    void ConfigureLibraryTags();
    void ManageLibraryTags();
    void ReturnFromTagBrowser();
    bool LibraryTagBatchAvailable => false;
    bool LibraryTagBatchMode => false;
    int LibraryTagBatchCount => 0;
    event Action? LibraryTagBatchChanged { add { } remove { } }
    void SetLibraryTagBatch(bool enabled) { }
    void SelectAllLibraryTagBatch() { }
    void ClearLibraryTagBatch() { }
    void EditLibraryTagBatch() { }
}

internal sealed class NativeLibraryTagSettings : NativeDialogWindow
{
    private sealed record GroupChoice(string Id, string Name) { public override string ToString() => Name; }
    private readonly WC.CheckBox engines = new() { Content = "显示引擎分类", Margin = new(0, 10, 0, 16) };
    private readonly WC.ComboBox primary = new() { DisplayMemberPath = "Name", MinHeight = 36, Margin = new(0, 0, 0, 15) };
    private readonly WC.ComboBox secondary = new() { DisplayMemberPath = "Name", MinHeight = 36, Margin = new(0, 0, 0, 15) };
    private readonly WC.TextBlock feedback = NativeDialogControls.Text("");
    private readonly LibraryTagStore store;
    internal NativeLibraryTagSettings(LibraryTagStore store) : base("分类显示设置", 540, 440)
    {
        this.store = store; engines.IsChecked = store.Snapshot.Sidebar.Engines;
        WA.AutomationProperties.SetAutomationId(engines, "LibraryEngineGrouping"); WA.AutomationProperties.SetAutomationId(primary, "LibraryPrimaryGrouping"); WA.AutomationProperties.SetAutomationId(secondary, "LibrarySecondaryGrouping");
        Choices(store.Snapshot.Sidebar.Primary, store.Snapshot.Sidebar.Secondary);
        var manager = NativeDialogControls.Button("管理标签组", () => { var p = Chosen(primary); var s = Chosen(secondary); using var dialog = new NativeLibraryTagManager(store); dialog.ShowDialog(this); store.Reload(); Choices(p, s); });
        var save = NativeDialogControls.Button("保存", () =>
        {
            try { store.SetSidebar(new(engines.IsChecked == true, Chosen(primary), Chosen(secondary))); Finish(System.Windows.Forms.DialogResult.OK); }
            catch (Exception ex) { feedback.Text = UiOperationText.Error(ex); }
        }, true);
        WA.AutomationProperties.SetAutomationId(save, "LibrarySaveGrouping");
        Content = NativeDialogControls.Layout(NativeDialogControls.Column(engines, NativeDialogControls.Text("主分类"), primary, NativeDialogControls.Text("副分类"), secondary,
            NativeDialogControls.Text("选择你自己的标签组，如评级、内容或风格。未开启时保留原游戏库。"), feedback),
            NativeDialogControls.Row(manager, save, ResultButton("取消", System.Windows.Forms.DialogResult.Cancel)));
        void LanguageChanged(object? sender,EventArgs e)=>Choices(Chosen(primary),Chosen(secondary));
        UiLanguage.LanguageChanged+=LanguageChanged;Closed+=(_,_)=>UiLanguage.LanguageChanged-=LanguageChanged;
    }
    private static string Chosen(WC.ComboBox choice) => (choice.SelectedItem as GroupChoice)?.Id ?? "";
    private void Choices(string p, string s)
    {
        var choices = new[] { new GroupChoice("", UiLanguage.Text("不开启")) }.Concat(store.Snapshot.Groups.Select(g => new GroupChoice(g.Id, LibraryTagStore.DisplayName(g)))).ToArray();
        foreach (var (box, selected) in new[] { (primary, p), (secondary, s) })
        { box.ItemsSource = choices; box.SelectedItem = choices.FirstOrDefault(c => c.Id == selected) ?? choices[0]; }
    }
}

internal sealed class NativeLibraryTagManager : NativeDialogWindow
{
    private readonly LibraryTagStore store;
    private readonly WC.ComboBox groups = new() { MinHeight = 36, Margin = new(0, 0, 0, 12) };
    private readonly WC.ListBox values = new() { MinHeight = 180, MaxHeight = 300, Margin = new(0, 0, 0, 12) };
    private readonly WC.TextBlock kind = NativeDialogControls.Text("");
    private readonly WC.TextBlock feedback = NativeDialogControls.Text("");
    private readonly WC.Button renameGroup, deleteGroup, addValue, renameValue, deleteValue;
    internal NativeLibraryTagManager(LibraryTagStore store) : base("管理标签组", 650, 620)
    {
        this.store = store;
        WA.AutomationProperties.SetAutomationId(groups, "LibraryTagGroups"); WA.AutomationProperties.SetAutomationId(values, "LibraryTagValues");
        var create = NativeDialogControls.Button("新建标签组", CreateGroup); WA.AutomationProperties.SetAutomationId(create, "LibraryCreateTagGroup");
        renameGroup = NativeDialogControls.Button("重命名组", () => Rename(true));
        deleteGroup = NativeDialogControls.Button("删除组", () => Delete(true));
        addValue = NativeDialogControls.Button("添加标签", AddValue); WA.AutomationProperties.SetAutomationId(addValue, "LibraryCreateTagValue");
        renameValue = NativeDialogControls.Button("重命名标签", () => Rename(false));
        deleteValue = NativeDialogControls.Button("删除标签", () => Delete(false));
        groups.SelectionChanged += (_, _) => RefreshValues(); values.SelectionChanged += (_, _) => RefreshActions();
        Content = NativeDialogControls.Layout(NativeDialogControls.Column(NativeDialogControls.Text("标签组"), groups,
            NativeDialogControls.Row(create, renameGroup, deleteGroup), kind, values, NativeDialogControls.Row(addValue, renameValue, deleteValue), feedback),
            NativeDialogControls.Row(ResultButton("完成", System.Windows.Forms.DialogResult.OK, true)));
        RefreshGroups("rating");
        void LanguageChanged(object? sender,EventArgs e)=>RefreshGroups(Group?.Id);
        UiLanguage.LanguageChanged+=LanguageChanged;Closed+=(_,_)=>UiLanguage.LanguageChanged-=LanguageChanged;
    }
    private LibraryTagGroup? Group => groups.SelectedItem as LibraryTagGroup;
    private LibraryTagValue? Value => values.SelectedItem as LibraryTagValue;
    private void RefreshGroups(string? selected)
    {
        store.Reload(); groups.ItemsSource = store.Snapshot.Groups;
        groups.SelectedItem = store.Snapshot.Groups.FirstOrDefault(g => g.Id == selected) ?? store.Snapshot.Groups.FirstOrDefault();
        if (store.Notice.Length > 0) feedback.Text = store.Notice;
        RefreshValues();
    }
    private void RefreshValues()
    {
        values.ItemsSource = Group?.Values ?? [];
        kind.Text = Group is { } g ? (g.Multiple ? "多选：一款游戏可以选择多个标签。" : "单选：一款游戏只选择一个标签，适合评级、状态。") + (g.Source == "user" ? "" : " 此组保留来源定义。") : "先新建一个标签组。";
        RefreshActions();
    }
    private void RefreshActions()
    {
        bool editable = Group?.Source == "user";
        renameGroup.IsEnabled = deleteGroup.IsEnabled = addValue.IsEnabled = editable;
        renameValue.IsEnabled = deleteValue.IsEnabled = editable && Value is not null;
    }
    private void Apply(Action change, string? group)
    {
        try { change(); feedback.Text = "已保存。"; RefreshGroups(group); }
        catch (Exception ex) { feedback.Text = UiOperationText.Error(ex); }
    }
    private string? Ask(string title, string initial)
    {
        using var dialog = new NativeDialogWindow(title, 440, 225);
        var input = NativeDialogControls.Input(initial); input.MaxLength = 40;
        WA.AutomationProperties.SetAutomationId(input, "LibraryTagName");
        dialog.Content = NativeDialogControls.Layout(NativeDialogControls.Column(NativeDialogControls.Text("名称"), input),
            NativeDialogControls.Row(dialog.ResultButton("保存", System.Windows.Forms.DialogResult.OK, true), dialog.ResultButton("取消", System.Windows.Forms.DialogResult.Cancel)));
        return dialog.ShowDialog(this) == System.Windows.Forms.DialogResult.OK ? input.Text : null;
    }
    private void CreateGroup()
    {
        using var dialog = new NativeDialogWindow("新建标签组", 480, 330);
        var name = NativeDialogControls.Input(); name.MaxLength = 40;
        var mode = NativeDialogControls.Choice(["单选（例如评级）", "多选（例如内容与风格）"]);
        WA.AutomationProperties.SetAutomationId(name, "LibraryNewTagGroupName"); WA.AutomationProperties.SetAutomationId(mode, "LibraryNewTagGroupMode");
        dialog.Content = NativeDialogControls.Layout(NativeDialogControls.Column(NativeDialogControls.Text("标签组名称"), name, NativeDialogControls.Text("每款游戏的选择方式"), mode),
            NativeDialogControls.Row(dialog.ResultButton("创建", System.Windows.Forms.DialogResult.OK, true), dialog.ResultButton("取消", System.Windows.Forms.DialogResult.Cancel)));
        if (dialog.ShowDialog(this) != System.Windows.Forms.DialogResult.OK) return;
        try { var id = store.CreateGroup(name.Text, mode.SelectedIndex == 1); feedback.Text = "标签组已创建，可以添加自己的标签。"; RefreshGroups(id); }
        catch (Exception ex) { feedback.Text = UiOperationText.Error(ex); }
    }
    private void AddValue()
    {
        if (Group is not { } group) return;
        var name = Ask("添加标签", ""); if (name is null) return;
        Apply(() => store.CreateValue(group.Id, name), group.Id);
    }
    private void Rename(bool isGroup)
    {
        if (Group is not { } group || !isGroup && Value is null) return;
        var value = Value; var name = Ask(isGroup ? "重命名标签组" : "重命名标签", isGroup ? group.Name : value!.Name); if (name is null) return;
        Apply(() => { if (isGroup) store.RenameGroup(group.Id, name); else store.RenameValue(group.Id, value!.Id, name); }, group.Id);
    }
    private void Delete(bool isGroup)
    {
        if (Group is not { } group || !isGroup && Value is null) return;
        var value = Value;
        if (AppDialog.Show(this, "删除标签", UiLanguage.Format($"删除“{(isGroup ? LibraryTagStore.DisplayName(group) : LibraryTagStore.DisplayName(value!))}”及其游戏标签关联？游戏、笔记、文件夹和聊天记录会保留。"), AppDialogKind.Confirmation) != System.Windows.Forms.DialogResult.Yes) return;
        Apply(() => { if (isGroup) store.DeleteGroup(group.Id); else store.DeleteValue(group.Id, value!.Id); }, isGroup ? null : group.Id);
    }
}

internal sealed class NativeGameTagEditor : NativeDialogWindow
{
    private readonly Dictionary<string, Func<string[]>> readers = new(StringComparer.Ordinal);
    private readonly HashSet<string> dirty = new(StringComparer.Ordinal);
    private readonly List<Action> languageRefresh=[];
    private bool refreshingLanguage;
    internal NativeGameTagEditor(LibraryTagStore store, RecentGame[] games) : base("编辑分类标签", 580, 560)
    {
        var body = new WC.StackPanel();
        body.Children.Add(new WC.TextBlock { Text = games.Length == 1 ? games[0].Name : UiLanguage.Format($"为 {games.Length} 款游戏设置标签"), FontSize = 20, TextWrapping = W.TextWrapping.Wrap, Margin = new(0, 0, 0, 16) });
        if (games.Length > 1) body.Children.Add(NativeDialogControls.Text("只修改本次操作的标签组，其余标签保持各游戏原来的设置。"));
        var more = new WC.StackPanel();
        foreach (var group in store.Snapshot.Groups.Where(g => g.Source != "external"))
        {
            var target = group.Id is "rating" or LibraryTagStore.Development ? body : more;
            var first = LibraryTagStore.Assigned(store.Snapshot, games[0].Id, group.Id);
            bool mixed = games.Skip(1).Any(g => !LibraryTagStore.Assigned(store.Snapshot, g.Id, group.Id).Order().SequenceEqual(first.Order()));
            var heading=new WC.TextBlock { TextWrapping = W.TextWrapping.Wrap, Margin = new(0, 12, 0, 8), FontWeight = W.FontWeights.SemiBold };
            void RefreshHeading()=>heading.Text=LibraryTagStore.DisplayName(group)+(mixed?UiLanguage.Text(" · 当前选择不一致"):"");
            RefreshHeading();languageRefresh.Add(RefreshHeading);target.Children.Add(heading);
            if (!group.Multiple)
            {
                var options = new[] { new LibraryTagValue("", mixed ? "保持各游戏原值" : "未标注") }.Concat(group.Values).ToArray();
                if (mixed) options = options.Append(new LibraryTagValue(LibraryTagStore.Unset, "清除本组标签")).ToArray();
                var choice = new WC.ComboBox { ItemsSource = options, MinHeight = 36, Margin = new(0, 0, 0, 8) };
                choice.SelectedItem = !mixed && first.Length > 0 ? options.FirstOrDefault(v => v.Id == first[0]) ?? options[0] : options[0];
                WA.AutomationProperties.SetAutomationId(choice, "GameTagGroup:" + group.Id);
                readers[group.Id] = () => choice.SelectedItem is LibraryTagValue { Id.Length: > 0 } v && v.Id != LibraryTagStore.Unset ? [v.Id] : [];
                choice.SelectionChanged += (_, _) => { if(refreshingLanguage)return; if (mixed && (choice.SelectedItem as LibraryTagValue)?.Id == "") dirty.Remove(group.Id); else dirty.Add(group.Id); };
                languageRefresh.Add(()=>{var selected=choice.SelectedItem;choice.ItemsSource=null;choice.ItemsSource=options;choice.SelectedItem=selected;});
                choice.PreviewMouseWheel += (_, e) => { if (!choice.IsDropDownOpen) { WpfScrollRouting.RouteToParent(choice, e); e.Handled = true; } };
                target.Children.Add(choice);
            }
            else
            {
                var wrap = new WC.WrapPanel(); var checks = new List<(string Id, WC.CheckBox Box)>();
                foreach (var value in group.Values)
                {
                    var check = new WC.CheckBox { Content = new WC.TextBlock { Text = LibraryTagStore.DisplayName(value) }, IsChecked = !mixed && first.Contains(value.Id), Margin = new(0, 6, 18, 8) };
                    languageRefresh.Add(()=>((WC.TextBlock)check.Content).Text=LibraryTagStore.DisplayName(value));
                    WA.AutomationProperties.SetAutomationId(check, "GameTag:" + group.Id + ":" + value.Id);
                    check.Click += (_, _) => dirty.Add(group.Id); checks.Add((value.Id, check)); wrap.Children.Add(check);
                }
                readers[group.Id] = () => checks.Where(x => x.Box.IsChecked == true).Select(x => x.Id).ToArray(); target.Children.Add(wrap);
                if (group.Values.Length == 0) target.Children.Add(NativeDialogControls.Text("此组暂无标签，请先在“管理标签组”中添加。"));
            }
        }
        if (more.Children.Count > 0)
        {
            var expander = new WC.Expander { Header = "更多标签", Content = more, Margin = new(0, 12, 0, 8) };
            expander.SetResourceReference(WC.Control.ForegroundProperty, "Fusion.Text");
            WA.AutomationProperties.SetAutomationId(expander, "GameTagEditorMore"); body.Children.Add(expander);
        }
        void LanguageChanged(object? sender,EventArgs e){refreshingLanguage=true;try{foreach(var update in languageRefresh)update();}finally{refreshingLanguage=false;}}
        UiLanguage.LanguageChanged+=LanguageChanged;Closed+=(_,_)=>UiLanguage.LanguageChanged-=LanguageChanged;
        var feedback = NativeDialogControls.Text(""); body.Children.Add(feedback);
        var save = NativeDialogControls.Button("保存标签", () =>
        {
            try
            {
                var changed = readers.Where(pair => dirty.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value(), StringComparer.Ordinal);
                if (changed.Count > 0) store.SetGameGroups(games.Select(g => g.Id), changed);
                Finish(System.Windows.Forms.DialogResult.OK);
            }
            catch (Exception ex) { feedback.Text = UiOperationText.Error(ex); }
        }, true);
        WA.AutomationProperties.SetAutomationId(save, "SaveGameClassificationTags");
        Content = NativeDialogControls.Layout(body, NativeDialogControls.Row(save, ResultButton("取消", System.Windows.Forms.DialogResult.Cancel)));
    }
}
