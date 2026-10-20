using W = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Data;
using WI = System.Windows.Input;

namespace ScreenshotTranslationUiTester;

internal sealed class WpfRenpyModificationWorkspace : WC.StackPanel, IDisposable
{
    private readonly IRenpyWorkspaceHost host;
    private readonly List<Action> bindings = [];
    private readonly WC.TextBox search = Input("RenpySearch", 240), value = Input("RenpyValue", 180), note = Input("RenpyNote", 230), finderValue = Input("RenpyFinderValue", 140);
    private readonly WC.ComboBox scope, comparison;
    private readonly WC.CheckBox changedOnly = new() { Content = UiLanguage.Text("只看变化"), VerticalAlignment = W.VerticalAlignment.Center }, boolean = new() { Content = UiLanguage.Text("开启 / 已拥有"), VerticalAlignment = W.VerticalAlignment.Center };
    private readonly WC.DataGrid grid = new()
    {
        Height = 320, AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = true,
        SelectionMode = WC.DataGridSelectionMode.Extended, SelectionUnit = WC.DataGridSelectionUnit.FullRow,
        HeadersVisibility = WC.DataGridHeadersVisibility.Column, RowHeaderWidth = 0, GridLinesVisibility = WC.DataGridGridLinesVisibility.Horizontal,
        EnableColumnVirtualization = true, EnableRowVirtualization = true
    };
    private bool syncing;
    private bool disposed;
    private RenpyEditSession? edit;
    internal WpfRenpyModificationWorkspace(IRenpyWorkspaceHost host)
    {
        this.host = host; var state = host.NativeRenpy;
        bindings.Add(() => { changedOnly.Content=UiLanguage.Text("只看变化"); boolean.Content=UiLanguage.Text("开启 / 已拥有"); });
        scope = Choice("RenpyScope", 140, ["金钱", "物品", "CG 解锁记录", "全部变量", "我的常用", "常用"]);
        comparison = Choice("RenpyFinderComparison", 156, ["等于填写的数值", "比上次增大", "比上次减小", "与上次相同", "与上次不同"]);
        var categories=Row();Id(categories,"RenpyCategories");
        foreach(var item in new[]{(5,"常用"),(0,"金钱"),(1,"物品"),(2,"CG 解锁记录"),(3,"变量"),(4,"我的常用")})
        {
            var tab=WpfThemeResources.Button(item.Item2,null,()=>host.NativeRenpyScope(item.Item1));tab.Height=36;tab.Margin=new(0,3,8,3);Id(tab,"RenpyScope"+item.Item1);categories.Children.Add(tab);
            bindings.Add(()=>WpfThemeResources.SetButton(tab,item.Item2,null,state.Scope==item.Item1));
        }
        Add(categories);
        Add(Row(Button(state.Launch,"play","launch",true),Button(state.Refresh,"refresh","refresh"),Button(state.Undo,"back","undo")));
        var speedControl=new WpfGameSpeedControl();Add(speedControl);bindings.Add(()=>{speedControl.SetState(ModificationSpeedState.Unavailable);Show(speedControl,state.Scope==5);});
        var commonHint=Text("常用显示金钱、已收藏的变量及名称明确的属性候选。请核对原始变量路径和当前值；未识别的项目仍可在变量中查找。");Add(commonHint);bindings.Add(()=>Show(commonHint,state.Scope==5));
        Add(Row(Button(state.FinderToggle, "search", "finder"), Label(() => state.Status)));
        var finder = new WC.StackPanel(); finder.Children.Add(Row(finderValue, Button(state.FinderBegin, "search", "begin"), comparison, Button(state.FinderNarrow, "search", "narrow"), Button(state.FinderReset, "back", "reset"))); finder.Children.Add(Label(() => state.FinderStatus)); Add(finder);
        bindings.Add(() => Show(finder, state.FinderVisible));
        var columns=new WpfModificationColumns();Id(columns,"RenpyDataBrowser");Add(columns);
        var list=new WC.StackPanel();columns.Children.Add(list);
        var searchField=WpfModificationLayout.Search(search,"搜索原名、译名或当前数值");searchField.Margin=new(0,4,0,8);list.Children.Add(searchField);
        list.Children.Add(Row(changedOnly,Label(()=>state.SortStatus)));
        list.Children.Add(Row(Button("全选当前结果", "check", "selectAll"), Button("开启所选", "check", "enableMany"), Button("关闭所选", "close", "disableMany")));
        foreach (var column in new[] { ("译名 / 备注", "Label", 25), ("原文 / 变量路径", "Name", 30), ("当前值", "Value", 16), ("生效范围", "Hint", 29) })
        {
            var view=new WC.DataGridTextColumn { Header = UiLanguage.Text(column.Item1), Binding = new WD.Binding(column.Item2), SortMemberPath = column.Item2, Width = new(column.Item3, WC.DataGridLengthUnitType.Star) };
            grid.Columns.Add(view); bindings.Add(()=>view.Header=UiLanguage.Text(column.Item1));
        }
        Id(grid, "RenpyVariables");WpfModificationLayout.Table(grid);list.Children.Add(grid);
        list.Children.Add(Label(()=>$"{state.Rows.Length} 项"));
        var detail=new WC.StackPanel();var details=WpfModificationLayout.Details(detail);Id(details,"RenpyDetails");columns.Children.Add(details);
        var selection=Label(()=>state.Selected.Length==1?state.Selection:UiLanguage.Text(state.Selection),localize:false);selection.FontSize=20;selection.FontWeight=W.FontWeights.SemiBold;detail.Children.Add(selection);
        detail.Children.Add(Label(()=>state.Selected.Length==1?state.Selected[0].Label:UiLanguage.Text("在左侧选择项目，查看详情并修改。"),localize:false));
        detail.Children.Add(Label(()=>state.Selected.Length==1?UiLanguage.Format($"当前值  {state.Selected[0].Value}"):"",localize:false));
        detail.Children.Add(Text("目标值"));
        detail.Children.Add(Row(value,boolean,Button(state.Apply,"check","apply",true)));
        detail.Children.Add(Label(()=>state.Hint));
        detail.Children.Add(Row(Button(state.Backup,"folder","backup"),Button("备注与帮助","edit","notes")));
        var notes = new WC.StackPanel(); notes.Children.Add(Row(Text("我的译名"), note, Button(state.SaveNote, "check", "note"), Button(state.Favorite, "tag", "favorite"), Button("补齐译名", "translate", "names")));
        notes.Children.Add(Label(() => state.NamesStatus)); notes.Children.Add(Text("名称与分类为通用词义提示，请结合当前值核对。原文始终保留；备注和收藏只保存在本工具。")); notes.Children.Add(Text("找不到数值？使用“数值查找”逐次缩小范围。读档或重新连接后重新查找，备注和常用仍会保留。")); detail.Children.Add(notes); bindings.Add(() => Show(notes, state.NotesVisible));
        scope.SelectionChanged += (_, _) => { if (!syncing) host.NativeRenpyScope(scope.SelectedIndex); };
        comparison.SelectionChanged += (_, _) => { if (!syncing) state.Comparison = Math.Max(0, comparison.SelectedIndex); };
        search.TextChanged += (_, _) => { if (!syncing) host.NativeRenpySearch(search.Text); };
        note.TextChanged += (_, _) => { if (!syncing) state.Note = note.Text; };
        finderValue.TextChanged += (_, _) => { if (!syncing) state.FinderInput = finderValue.Text; };
        value.TextChanged += (_, _) => { if (!syncing) state.Value = value.Text; };
        value.GotKeyboardFocus += (_, _) => edit = host.NativeRenpyCaptureEdit();
        value.LostKeyboardFocus += async (_, _) => { if (!syncing && edit is { } captured) { edit = null; await host.NativeRenpyCommit(captured, value.Text); } };
        value.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == WI.Key.Escape) { e.Handled = true; edit = null; await host.NativeRenpyAction("cancelEdit"); }
            else if (e.Key == WI.Key.Enter) { e.Handled = true; var captured = edit ?? host.NativeRenpyCaptureEdit(); edit = null; await host.NativeRenpyCommit(captured, value.Text); }
        };
        finderValue.PreviewKeyDown += async (_, e) => { if (e.Key == WI.Key.Enter) { e.Handled = true; await host.NativeRenpyAction(state.FinderNarrow.Enabled ? "narrow" : "begin"); } };
        boolean.Click += async (_, _) => { if (!syncing) await host.NativeRenpyBoolean(boolean.IsChecked == true); };
        changedOnly.Click += (_, _) => { if (!syncing) host.NativeRenpyChangedOnly(changedOnly.IsChecked == true); };
        grid.SelectionChanged += (_, _) => { if (!syncing) host.NativeRenpySelect(grid.SelectedItems.Cast<RenpyVariableRow>()); };
        grid.Sorting += (_, e) => { e.Handled = true; host.NativeRenpySort(grid.Columns.IndexOf(e.Column)); };
        var menu = new WC.ContextMenu();
        foreach (var (caption, action) in new[] { ("全选当前结果", "selectAll"), ("取消选择", "clearSelection"), ("开启所选项", "enableMany"), ("关闭所选项", "disableMany"), ("复制原文 / 变量路径", "copy") })
        {
            var item = new WC.MenuItem { Header = UiLanguage.Text(caption) }; bindings.Add(()=>item.Header=UiLanguage.Text(caption)); item.Click += async (_, _) => await host.NativeRenpyAction(action); menu.Items.Add(item);
        }
        grid.ContextMenu = menu;
        grid.PreviewMouseRightButtonDown += (_, e) => { var row = WC.ItemsControl.ContainerFromElement(grid, e.OriginalSource as W.DependencyObject) as WC.DataGridRow; if (row is not null && !row.IsSelected) { grid.SelectedItems.Clear(); row.IsSelected = true; } };
        host.NativeRenpyChanged += Refresh;UiLanguage.LanguageChanged+=RefreshLanguage; Loaded += (_, _) => Refresh();
    }
    private static void Id(W.DependencyObject view, string id) => System.Windows.Automation.AutomationProperties.SetAutomationId(view, id);
    private static WC.TextBox Input(string id, double width) { var view = new WC.TextBox { Width = width, Height = 36 }; Id(view, id); return view; }
    private WC.ComboBox Choice(string id, double width, string[] items)
    {
        var view = new WC.ComboBox { Width = width, Height = 36 }; var language = (ApplicationLanguage)(-1);
        bindings.Add(() => { if(language==UiLanguage.Current)return; language=UiLanguage.Current; int selected=view.SelectedIndex; view.ItemsSource=items.Select(UiLanguage.Text).ToArray(); view.SelectedIndex=selected; });
        Id(view, id); view.PreviewMouseWheel += (_, e) => { if (!view.IsDropDownOpen) WpfScrollRouting.RouteToParent(view, e); }; return view;
    }
    private static void Show(W.FrameworkElement view, bool visible) => view.Visibility = visible ? W.Visibility.Visible : W.Visibility.Collapsed;
    private void Add(W.FrameworkElement child) { child.Margin = new(0, 0, 0, 10); Children.Add(child); }
    private static WC.TextBlock TextView(string text) => new() { Text=UiLanguage.Text(text),TextWrapping=W.TextWrapping.Wrap,VerticalAlignment=W.VerticalAlignment.Center,Margin=new(0,4,0,4) };
    private WC.TextBlock Text(string text)
    {
        var view=TextView(text);
        bindings.Add(()=>view.Text=UiLanguage.Text(text));return view;
    }
    private WC.TextBlock Label(Func<string> text,bool localize=true) { var label = TextView(""); bindings.Add(() => label.Text = localize?UiLanguage.Text(text()):text()); return label; }
    private static WC.WrapPanel Row(params W.FrameworkElement[] children) => WpfModificationLayout.Toolbar(children);
    private WC.Button Button(string text, string icon, string action, bool primary = false)
    {
        var button = WpfThemeResources.Button(text, icon, async () => await host.NativeRenpyAction(action), primary); button.Height = 36; button.MinWidth = 96; Id(button, "RenpyAction" + action); bindings.Add(() => WpfThemeResources.SetButton(button, text, icon, primary)); return button;
    }
    private WC.Button Button(DesktopActionState state, string icon, string action, bool primary = false)
    {
        var button = WpfThemeResources.Button(state.InitialText, icon, async () => await host.NativeRenpyAction(action), primary); button.Height = 36; button.MinWidth = 96; Id(button, "RenpyAction" + action);
        bindings.Add(() => { button.IsEnabled = state.Enabled; Show(button, state.Visible);WpfThemeResources.SetButton(button,action switch{"refresh"=>"刷新","apply"=>"应用",_=>state.Text.Length>0?state.Text:state.InitialText},icon,primary); }); return button;
    }
    internal void Refresh()
    {
        if (syncing) return; syncing = true;
        try
        {
            var state = host.NativeRenpy; foreach (var bind in bindings) bind();
            if (!ReferenceEquals(grid.ItemsSource, state.Rows)) grid.ItemsSource = state.Rows;
            if (!grid.SelectedItems.Cast<RenpyVariableRow>().SequenceEqual(state.Selected)) { grid.SelectedItems.Clear(); foreach (var row in state.Selected) grid.SelectedItems.Add(row); }
            Set(search, state.Search); Set(value, state.Value); Set(note, state.Note); Set(finderValue, state.FinderInput);
            scope.SelectedIndex = state.Scope; comparison.SelectedIndex = state.Comparison; changedOnly.IsChecked = state.ChangedOnly; boolean.IsChecked = state.Boolean;
            Show(value, state.ValueVisible); Show(boolean, state.BooleanVisible); value.IsEnabled = boolean.IsEnabled = state.ValueEnabled; comparison.IsEnabled = finderValue.IsEnabled = state.FinderEnabled;
        }
        finally { syncing = false; }
    }
    private static void Set(WC.TextBox editor, string text) { if (editor.Text != text) editor.Text = text; }
    private void RefreshLanguage(object? sender,EventArgs e)
    {
        if(disposed)return;
        if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke((Action)(()=>RefreshLanguage(sender,e)));return;}
        foreach(var row in host.NativeRenpy.Rows)row.RefreshLanguage();
        Refresh();
    }
    public void Dispose(){disposed=true;host.NativeRenpyChanged-=Refresh;UiLanguage.LanguageChanged-=RefreshLanguage;}
}
