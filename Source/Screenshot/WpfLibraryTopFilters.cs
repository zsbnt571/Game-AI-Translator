using System.Text.Json;
using W = System.Windows;
using WC = System.Windows.Controls;
using WP = System.Windows.Controls.Primitives;
using WA = System.Windows.Automation;

namespace ScreenshotTranslationUiTester;

// Filters live in an overlay: opening a long group never moves the game grid.
internal sealed class WpfLibraryTagPanel : WC.StackPanel
{
    private readonly ILibraryTagWorkspaceHost host;
    private readonly WC.WrapPanel filters = new(), chosen = new(), batch = new();
    private readonly WC.TextBlock notice = Label(""), batchCount = Label("");
    private readonly Dictionary<string, WC.Button> buttons = [];
    private readonly List<(WC.CheckBox Box, string Group, string? Value)> choices = [];
    private readonly WP.Popup popup = new() { StaysOpen = false, AllowsTransparency = true, Placement = WP.PlacementMode.Bottom, VerticalOffset = 5 };
    private readonly WC.Button apply;
    private string active = "", facetsStamp = "", chosenStamp = "";
    private static readonly string[] Quick = [LibraryTagStore.Engine, LibraryTagStore.Development, "rating"];
    internal WpfLibraryTagPanel(ILibraryTagWorkspaceHost host)
    {
        this.host = host; Margin = new(0, 0, 0, 4);
        SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Fusion.Text");
        WA.AutomationProperties.SetAutomationId(this, "LibraryTagBrowser");
        foreach (string id in Quick.Append("more"))
        {
            var button = WpfThemeResources.Button("", "down", () => OpenFilter(id), trailingIcon: true);
            button.Margin = new(0, 0, 8, 6); button.Padding = new(10, 6, 10, 6);
            button.MaxWidth = 180; WA.AutomationProperties.SetAutomationId(button, "LibraryFilter:" + id);
            buttons[id] = button; filters.Children.Add(button);
        }
        Children.Add(filters); Children.Add(chosen); Children.Add(notice);
        batchCount.VerticalAlignment = W.VerticalAlignment.Center; batchCount.Margin = new(0, 0, 12, 6);
        batch.Children.Add(batchCount);
        apply = WpfThemeResources.Button("设置标签", "tag", host.EditLibraryTagBatch, true);
        AddBatch(apply, "LibraryApplyBatchTags");
        AddBatch(WpfThemeResources.Button("全选结果", null, host.SelectAllLibraryTagBatch), "LibraryBatchSelectAll");
        AddBatch(WpfThemeResources.Button("取消选择", null, host.ClearLibraryTagBatch), "LibraryBatchClearSelection");
        batch.Margin = new(0, 4, 0, 0); Children.Add(batch);
        Unloaded += (_, _) => popup.IsOpen = false;
        SizeChanged += (_, _) => { if (popup.IsOpen) SizePopup(); };
    }
    private void AddBatch(WC.Button button, string id)
    {
        button.Margin = new(0, 0, 8, 6); button.Padding = new(10, 5, 10, 5);
        WA.AutomationProperties.SetAutomationId(button, id); batch.Children.Add(button);
    }
    private static WC.TextBlock Label(string text, bool bold = false)
    {
        var label = new WC.TextBlock { Text = text, TextWrapping = W.TextWrapping.Wrap, FontWeight = bold ? W.FontWeights.SemiBold : W.FontWeights.Normal };
        label.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Text"); return label;
    }
    private void OpenFilter(string id)
    {
        if (popup.IsOpen && active == id) { popup.IsOpen = false; return; }
        popup.IsOpen = false; active = id; popup.PlacementTarget = buttons[id];
        BuildPopup(); popup.IsOpen = true;
        popup.Dispatcher.BeginInvoke(new Action(() => { if (popup.IsOpen) choices.FirstOrDefault().Box?.Focus(); }));
    }
    private void SizePopup()
    {
        if (popup.Child is not WC.Border border || border.Child is not WC.ScrollViewer scroll) return;
        border.Width = Math.Clamp(ActualWidth - 12, 220, active == "more" ? 480 : 380);
        scroll.MaxHeight = Math.Clamp((W.Window.GetWindow(this)?.ActualHeight ?? 720) * .45, 180, 360);
    }
    private void BuildPopup()
    {
        choices.Clear(); var state = host.TagUi; var body = new WC.StackPanel();
        var groups = state.Facets.Where(f => active == "more" ? !Quick.Contains(f.Id) : f.Id == active);
        foreach (var facet in groups)
        {
            var title = Label(facet.Id is LibraryTagStore.Engine or LibraryTagStore.Legacy?UiLanguage.Text(facet.Name):facet.Name, true); title.Margin = new(0, 2, 0, 10); body.Children.Add(title);
            var wrap = new WC.WrapPanel { Margin = new(0, 0, 0, 12) };
            void Add(string text, string? value)
            {
                var choice = new WC.CheckBox { Content = Label(text), Margin = new(0, 0, 16, 10), Padding = new(2), MinHeight = 28, MaxWidth = 300, VerticalContentAlignment = W.VerticalAlignment.Center };
                string id = value is null ? "LibraryTagAll:" + facet.Id : "LibraryTag:" + new LibraryTagSelection(facet.Id, value).Key;
                WA.AutomationProperties.SetAutomationId(choice, id); WA.AutomationProperties.SetName(choice, facet.Name + "：" + text);
                choice.Click += (_, _) => { if (value is null) host.ClearLibraryTagGroup(facet.Id); else host.ToggleLibraryTag(new(facet.Id, value)); };
                choices.Add((choice, facet.Id, value)); wrap.Children.Add(choice);
            }
            Add(UiLanguage.Text("全部"), null);
            foreach (var value in facet.Values.Where(v => v.Id != LibraryTagStore.Unset)) Add(value.Name, value.Id);
            body.Children.Add(wrap);
        }
        if (active == "more")
        {
            var manage = WpfThemeResources.Button("管理标签", "edit", () => { popup.IsOpen = false; host.ManageLibraryTags(); });
            WA.AutomationProperties.SetAutomationId(manage, "LibraryManageTags"); manage.HorizontalAlignment = W.HorizontalAlignment.Left; body.Children.Add(manage);
        }
        var scroll = new WC.ScrollViewer { Content = body, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = WC.ScrollBarVisibility.Disabled };
        var border = new WC.Border { Child = scroll, Padding = new(16, 12, 10, 4), BorderThickness = new(1), CornerRadius = new(8) };
        border.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) { popup.IsOpen = false; buttons[active].Focus(); e.Handled = true; }
        };
        border.Resources.MergedDictionaries.Add(WpfThemeResources.Create()); WpfThemeResources.Apply(border.Resources);
        border.SetResourceReference(WC.Border.BackgroundProperty, "Fusion.Secondary"); border.SetResourceReference(WC.Border.BorderBrushProperty, "Fusion.Border");
        WA.AutomationProperties.SetAutomationId(border, "LibraryFilterPopup"); popup.Child = border; SizePopup(); UpdateChoices(state);
    }
    private void UpdateChoices(LibraryTagUiState state)
    {
        foreach (var choice in choices)
            choice.Box.IsChecked = choice.Value is null ? !state.Selected.Any(s => s.Group == choice.Group) : state.Selected.Contains(new(choice.Group, choice.Value));
    }
    internal void Refresh()
    {
        var state = host.TagUi;
        Visibility = state.Browser || state.Selected.Length > 0 || state.Notice.Length > 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
        filters.Visibility = state.Browser ? W.Visibility.Visible : W.Visibility.Collapsed;
        if (!state.Browser) popup.IsOpen = false;
        foreach (var (id, button) in buttons)
        {
            var facet = state.Facets.FirstOrDefault(f => f.Id == id);
            button.Visibility = id == "more" || facet is not null ? W.Visibility.Visible : W.Visibility.Collapsed;
            int n = state.Selected.Count(s => id == "more" ? !Quick.Contains(s.Group) : s.Group == id);
            string name = id == "more" ? UiLanguage.Text("更多标签") : id==LibraryTagStore.Engine?UiLanguage.Text(facet?.Name??""):facet?.Name ?? "";
            WpfThemeResources.SetButton(button, name + (n > 0 ? $" · {n}" : ""), "down", trailingIcon: true);
            WA.AutomationProperties.SetName(button, n>0?UiLanguage.Format($"{name}，已选 {n} 项"):UiLanguage.Format($"{name}，全部"));
        }
        string nextFacets = JsonSerializer.Serialize(new { state.Facets, Language = UiLanguage.Current, Theme = UiTheme.Current });
        if (facetsStamp != nextFacets) { facetsStamp = nextFacets; if (popup.IsOpen) BuildPopup(); }
        UpdateChoices(state);
        notice.Text = UiLanguage.Text(state.Notice); notice.Visibility = state.Notice.Length == 0 ? W.Visibility.Collapsed : W.Visibility.Visible;
        batch.Visibility = state.Browser && host.LibraryTagBatchMode ? W.Visibility.Visible : W.Visibility.Collapsed;
        batchCount.Text = UiLanguage.Format($"已选 {host.LibraryTagBatchCount} 款"); apply.IsEnabled = host.LibraryTagBatchCount > 0;
        string nextChosen = JsonSerializer.Serialize(new { state.Selected, state.Facets });
        if (chosenStamp == nextChosen) return;
        chosenStamp = nextChosen; chosen.Children.Clear();
        foreach (var selection in state.Selected)
        {
            var facet = state.Facets.FirstOrDefault(g => g.Id == selection.Group); var value = facet?.Values.FirstOrDefault(v => v.Id == selection.Value);
            if (value is null) continue;
            var remove = new WC.Button { Content = new WC.TextBlock { Text = facet!.Name + "：" + value.Name + "  ×", TextWrapping = W.TextWrapping.Wrap }, Margin = new(0, 2, 7, 5), Padding = new(9, 4, 9, 4), MaxWidth = 300 };
            WA.AutomationProperties.SetAutomationId(remove, "LibraryRemoveTag:" + selection.Key);
            WA.AutomationProperties.SetName(remove, UiLanguage.Format($"移除筛选：{facet.Name}：{value.Name}")); remove.Click += (_, _) => host.ToggleLibraryTag(selection); chosen.Children.Add(remove);
        }
        if (state.Selected.Length > 0)
        {
            var clear = WpfThemeResources.Button("清空", null, host.ClearLibraryTagFilters); clear.BorderThickness = new(0); clear.Padding = new(8, 4, 8, 4);
            WA.AutomationProperties.SetAutomationId(clear, "LibraryClearTags"); chosen.Children.Add(clear);
        }
    }
}
