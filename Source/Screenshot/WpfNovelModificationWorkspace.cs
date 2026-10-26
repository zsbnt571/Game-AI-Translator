using W = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Data;
using WI = System.Windows.Input;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

internal sealed class WpfNovelModificationWorkspace : WC.StackPanel, IDisposable
{
    private readonly INovelWorkspaceHost host;
    private readonly List<Action> bindings = [];
    private readonly WC.TextBox search = new() { Width = 240, Height = 36 };
    private readonly WC.TextBox value = new() { Width = 220, Height = 36 };
    private readonly WC.ComboBox boolean = new() { Width = 150, Height = 36 };
    private readonly HashSet<string> selectedKeys = new(StringComparer.Ordinal);
    private NovelVariableRow[]? selectionSource;
    private HashSet<NovelVariableRow> selectableRows=[];
    private bool editingBusy;
    private readonly WC.DataGrid grid = new()
    {
        Height = 300, AutoGenerateColumns = false, IsReadOnly = true,
        CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false,
        SelectionMode = WC.DataGridSelectionMode.Extended, SelectionUnit = WC.DataGridSelectionUnit.FullRow,
        HeadersVisibility = WC.DataGridHeadersVisibility.Column, GridLinesVisibility = WC.DataGridGridLinesVisibility.Horizontal,
        EnableRowVirtualization = true, EnableColumnVirtualization = true, RowHeaderWidth = 0
    };
    private readonly WC.ComboBox snapshots = new() { Width = 330, Height = 36 };
    private bool syncing;
    private readonly NativeRpgCommonBoard common = new();
    private NovelVariableRow[]? commonSource;
    private int commonNamesRevision=-1;
    private readonly WC.ContentControl mapHost=new(){Height=500};
    private NativeWolfMapWorkspace? mapWorkspace;
    private string mapPath="";
    private readonly Dictionary<string,double> tablePositions=new(StringComparer.OrdinalIgnoreCase);
    private string tableViewKey="";
    private int tableRestoreRevision;
    private bool disposed;

    internal WpfNovelModificationWorkspace(INovelWorkspaceHost host, System.Windows.FrameworkElement? gameSpeed = null)
    {
        this.host = host;
        var state = host.NativeNovel;
        Id(search, "NovelSearch"); Id(value, "NovelValue"); Id(boolean, "NovelBoolean"); Id(grid, "NovelVariables"); Id(snapshots, "NovelSnapshots");
        boolean.Items.Add(new WC.ComboBoxItem { Tag = false }); boolean.Items.Add(new WC.ComboBoxItem { Tag = true });
        bindings.Add(()=>{((WC.ComboBoxItem)boolean.Items[0]).Content=UiLanguage.SwitchText(false);((WC.ComboBoxItem)boolean.Items[1]).Content=UiLanguage.SwitchText(true);});
        bindings.Add(()=>search.ToolTip = UiLanguage.Text("搜索名称、编号或当前数值"));
        var categories=Row();Id(categories,"NovelCategories");
        foreach(var item in new[]{("common","常用"),("characters","角色"),("relationships","关系"),("items","物品"),("weapons","武器"),("armors","防具"),("variables","变量"),("switches","开关"),("db","可变数据库"),("locked","已锁定"),("all","通用数据")})
        {
            var tab=WpfThemeResources.Button(item.Item2,null,async()=>await host.NativeNovelCommand("scope:"+item.Item1));tab.Height=36;tab.Margin=new(0,3,8,3);Id(tab,"NovelScope"+item.Item1);categories.Children.Add(tab);
            bindings.Add(()=>{WpfThemeResources.SetButton(tab,item.Item2,null,state.Scope==item.Item1);tab.Visibility=(item.Item1 is "items" or "weapons" or "armors")&&!state.InventoryScopes.Contains(item.Item1)||(item.Item1 is "characters" or "relationships")&&!state.CandidateScopes.Contains(item.Item1)||item.Item1=="db"&&!state.DatabaseAvailable||item.Item1=="switches"&&!state.SwitchesAvailable?W.Visibility.Collapsed:W.Visibility.Visible;});
        }
        foreach(var item in new[]{("maps","地图"),("events","事件")})
        {
            var button=WpfThemeResources.Button(item.Item2,"grid",async()=>await host.NativeNovelCommand(item.Item1));button.Height=36;button.Margin=new(0,3,8,3);Id(button,"Novel"+item.Item1);categories.Children.Insert(categories.Children.Count-2,button);
            bindings.Add(()=>{WpfThemeResources.SetButton(button,item.Item2,"grid",state.Scope==item.Item1);button.Visibility=state.Wolf?W.Visibility.Visible:W.Visibility.Collapsed;});
        }
        Add(Row(Button(state.Start, "play", "start", true), Button(state.Refresh, "refresh", "refresh"), Button(state.Undo,"back","dataUndo")));
        Add(categories);
        Add(Label(() => state.Status)); Add(Label(() => state.Feedback));
        if(gameSpeed is not null){Add(gameSpeed);bindings.Add(()=>Show(gameSpeed,state.Scope=="common"));}
        var columns = new WpfModificationColumns(); Id(columns,"NovelDataBrowser"); Add(columns);
        var list = new WC.StackPanel(); columns.Children.Add(list);
        var searchField = WpfModificationLayout.Search(search,"搜索名称、编号或当前数值"); searchField.Margin=new(0,4,0,8); list.Children.Add(searchField);
        bindings.Add(()=>Show(searchField,state.Scope is not ("common" or "maps" or "events")));
        var commonHint=Text("角色与关系按对象分类；待确认候选请先核对原始名称和当前值。未识别的项目仍可在变量或通用数据中查找。");list.Children.Add(commonHint);
        bindings.Add(()=>Show(commonHint,IsCandidateScope(state.Scope)));
        common.Execute=async request=>
        {
            string? key=request["key"]?.GetValue<string>();
            var row=host.NativeNovel.CommonRows.FirstOrDefault(item=>item.Source.GetProperty("id").GetString()==key);
            if(row is null||!host.NativeNovel.Apply.Enabled)return;
            host.NativeNovelSelect(row);host.NativeNovelValue(request["value"]?.ToJsonString()??row.Value);
            await host.NativeNovelCommand(request["op"]?.GetValue<string>() switch{"lock"=>"dataLock","unlock"=>"dataUnlock",_=>"dataSet"});
        };
        common.RequestNames=keys=>{var wanted=keys.ToHashSet(StringComparer.Ordinal);host.NativeNovelRequestNames(host.NativeNovel.CommonRows.Where(row=>wanted.Contains(row.Source.GetProperty("id").GetString()??"")));};
        list.Children.Add(common.View);bindings.Add(()=>Show(common.View,IsCandidateScope(state.Scope)));
        var emptyCommon=Label(()=>state.Scope=="common"?"尚未识别到可直接修改的常用数值，可在变量和通用数据中查找。":"当前分类没有匹配的项目。可调整搜索，或在变量和通用数据中查找。");list.Children.Add(emptyCommon);
        bindings.Add(()=>Show(emptyCommon,IsCandidateScope(state.Scope)&&state.CommonRows.Length==0));
        var selectionActions=Row(SelectionButton("全选当前结果","check","selectAll"),SelectionButton("取消选择","close","clearSelection"),SelectionButton("开启所选","check","enableMany"),SelectionButton("关闭所选","close","disableMany"));
        Id(selectionActions,"NovelSelectionActions");list.Children.Add(selectionActions);
        bindings.Add(()=>Show(selectionActions,!IsCandidateScope(state.Scope)&&state.Scope is not ("maps" or "events")));
        foreach (var column in new[] { ("变量名称", "Name", 55), ("当前值", "Value", 35), ("锁定", "Locked", 10) })
        {
            WC.DataGridColumn view=column.Item2=="Name"
                ?new WC.DataGridTemplateColumn{Header=UiLanguage.Text("名称 / Name"),CellTemplate=WpfModificationLayout.Names("DisplayName","Original","RawName"),Width=new(column.Item3,WC.DataGridLengthUnitType.Star)}
                :new WC.DataGridTextColumn { Header = UiLanguage.Text(column.Item1), Binding = column.Item2=="Value"?new WD.Binding("."){Converter=NovelValueDisplay.Instance}:new WD.Binding(column.Item2), Width = new(column.Item3, WC.DataGridLengthUnitType.Star) };
            grid.Columns.Add(view);bindings.Add(()=>view.Header=UiLanguage.Text(column.Item2=="Name"?"名称 / Name":column.Item1));
        }
        WpfModificationLayout.Table(grid);
        list.Children.Add(mapHost);bindings.Add(()=>Show(mapHost,state.Scope is "maps" or "events"));
        list.Children.Add(grid);bindings.Add(()=>Show(grid,!IsCandidateScope(state.Scope)&&state.Scope is not ("maps" or "events")));
        var count=Label(()=>UiLanguage.Format($"{state.Rows.Length} 项 · 已选择 {SelectedRows().Length} 项"),localize:false);list.Children.Add(count);bindings.Add(()=>Show(count,!IsCandidateScope(state.Scope)&&state.Scope is not ("maps" or "events")));
        var detail=new WC.StackPanel(); var details=WpfModificationLayout.Details(detail);Id(details,"NovelDetails");columns.Children.Add(details);
        bindings.Add(()=>Show(details,!IsCandidateScope(state.Scope)&&state.Scope is not ("maps" or "events")));
        var selectedName=Label(()=>SelectedRows() is {Length:>1} rows?UiLanguage.Format($"已选择 {rows.Length} 项 · 右键批量操作"):SelectedRows().FirstOrDefault()?.DisplayName??UiLanguage.Text("选择一项数据"),localize:false);selectedName.FontSize=20;selectedName.FontWeight=W.FontWeights.SemiBold;detail.Children.Add(selectedName);
        detail.Children.Add(Label(()=>SelectedRows() is [var row]?row.Original:"",localize:false));
        detail.Children.Add(Label(()=>SelectedRows() is [var row]?UiLanguage.Text("编号")+"  "+ReadableModificationKey.Format(RowKey(row)):UiLanguage.Text("在左侧选择项目，查看详情并修改。"),localize:false));
        detail.Children.Add(Label(()=>SelectedRows() is [var row]?UiLanguage.Format($"当前值  {NovelValueDisplay.Format(row)}"):"",localize:false));
        detail.Children.Add(Text("目标值"));
        var lockValue=SelectionButton("锁定","lock","dataLock");var unlockValue=SelectionButton("解除锁定","unlock","dataUnlock");
        bindings.Add(()=>{var rows=SelectedRows();Show(lockValue,rows.Any(row=>!IsLocked(row)));Show(unlockValue,rows.Any(IsLocked));});
        detail.Children.Add(Row(lockValue,unlockValue,value,boolean,SelectionButton("应用","check","dataSet",true)));
        detail.Children.Add(Text("修改仅应用到所选项目。数值保持数值，开关可选择开启或关闭。"));
        var mixedHint=Text("选择相同类型的项目后再批量修改。");detail.Children.Add(mixedHint);bindings.Add(()=>Show(mixedHint,SelectedRows().Length>1&&SelectionType().Length==0));
        Add(Text("修改当前运行中的变量；进度请按游戏原有方式保存。读档或切换数据后请重新刷新。"));
        var snapshotPanel = new WC.StackPanel();
        snapshotPanel.Children.Add(Text("状态快照（测试）"));
        snapshotPanel.Children.Add(Row(snapshots, Button(state.SnapshotRefresh, "refresh", "list", snapshot: true), Button(state.SnapshotCapture, "copy", "capture", snapshot: true), Button(state.SnapshotLoad, "back", "load", snapshot: true)));
        snapshotPanel.Children.Add(Label(() => state.SnapshotFeedback));
        snapshotPanel.Children.Add(Text("保存当前状态的独立副本；载入前会自动备份当前状态。此功能仍需实机验证，重要进度请同时使用游戏自己的存档。"));
        Add(snapshotPanel); bindings.Add(() => snapshotPanel.Visibility = state.SnapshotVisible ? W.Visibility.Visible : W.Visibility.Collapsed);
        search.TextChanged += (_, _) => { if (!syncing) host.NativeNovelSearch(search.Text); };
        value.TextChanged += (_, _) => { if (!syncing) host.NativeNovelValue(value.Text); };
        boolean.SelectionChanged += (_, _) => { if(!syncing&&boolean.SelectedItem is WC.ComboBoxItem {Tag:bool flag})host.NativeNovelValue(flag?"true":"false"); };
        boolean.PreviewMouseWheel += (_, e) => { if (!boolean.IsDropDownOpen) WpfScrollRouting.RouteToParent(boolean, e); };
        grid.SelectionChanged += (_, _) => { if (!syncing) SelectionChanged(); };
        grid.PreviewKeyDown += (_,e)=>{if(e.Key==WI.Key.A&&(WI.Keyboard.Modifiers&WI.ModifierKeys.Control)!=0){e.Handled=true;grid.SelectAll();}};
        var menu=new WC.ContextMenu();
        foreach(var (caption,action) in new[]{("全选当前结果","selectAll"),("取消选择","clearSelection"),("开启所选项","enableMany"),("关闭所选项","disableMany"),("应用到所选项","dataSet"),("锁定所选项","dataLock"),("解除所选锁定","dataUnlock"),("复制原文 / 变量路径","copy")})
        {
            var item=new WC.MenuItem();Id(item,"NovelMenu"+action);bindings.Add(()=>{item.Header=UiLanguage.Text(caption);item.IsEnabled=CanSelectAction(action);});item.Click+=async(_,_)=>await SelectionAction(action);menu.Items.Add(item);
        }
        menu.Opened+=(_,_)=>Refresh();grid.ContextMenu=menu;
        grid.PreviewMouseRightButtonDown+=(_,e)=>{var row=WC.ItemsControl.ContainerFromElement(grid,e.OriginalSource as W.DependencyObject) as WC.DataGridRow;if(row is not null&&!row.IsSelected){grid.SelectedItems.Clear();row.IsSelected=true;}};
        grid.LoadingRow += (_, e) => { if(e.Row.Item is NovelVariableRow row)host.NativeNovelRequestNames([row]); };
        snapshots.SelectionChanged += (_, _) => { if (!syncing) host.NativeNovelSnapshot(snapshots.SelectedItem as NovelStateRecord); };
        snapshots.PreviewMouseWheel += (_, e) => { if (!snapshots.IsDropDownOpen) WpfScrollRouting.RouteToParent(snapshots, e); };
        host.NativeNovelChanged += Refresh;
        UiLanguage.LanguageChanged+=RefreshLanguage;
        Loaded += (_, _) => Refresh();
    }
    private static void Show(W.FrameworkElement view,bool visible)=>view.Visibility=visible?W.Visibility.Visible:W.Visibility.Collapsed;
    private static bool IsCandidateScope(string scope)=>scope is "common" or "characters" or "relationships";
    private static void Id(W.DependencyObject view, string id) => System.Windows.Automation.AutomationProperties.SetAutomationId(view, id);
    private void Add(W.FrameworkElement child) { child.Margin = new(0, 0, 0, 12); Children.Add(child); }
    private static WC.TextBlock TextView(string text) => new() { Text=UiLanguage.Text(text),TextWrapping=W.TextWrapping.Wrap,VerticalAlignment=W.VerticalAlignment.Center,Margin=new(0,4,0,4) };
    private WC.TextBlock Text(string text)
    {
        var view=TextView(text);
        bindings.Add(()=>view.Text=UiLanguage.Text(text));return view;
    }
    private WC.TextBlock Label(Func<string> text,bool localize=true) { var label = TextView(""); bindings.Add(() => label.Text = localize?UiLanguage.Text(text()):text()); return label; }
    private static WC.WrapPanel Row(params W.FrameworkElement[] children) => WpfModificationLayout.Toolbar(children);
    private static string RowKey(NovelVariableRow row)=>row.Source.GetProperty("id").GetString()??"";
    private static bool IsLocked(NovelVariableRow row)=>row.Source.TryGetProperty("locked",out var locked)&&locked.ValueKind==JsonValueKind.True;
    private NovelVariableRow[] SelectedRows()
    {
        if(!ReferenceEquals(selectionSource,host.NativeNovel.Rows)){selectionSource=host.NativeNovel.Rows;selectableRows=selectionSource.ToHashSet();}
        return grid.SelectedItems.Cast<NovelVariableRow>().Where(selectableRows.Contains).ToArray();
    }
    private static string EditableType(NovelVariableRow row)
    {
        if(!row.Source.TryGetProperty("type",out var type)||!row.Source.TryGetProperty("value",out var current))return "";
        return type.GetString() switch
        {
            "boolean" when current.ValueKind is JsonValueKind.True or JsonValueKind.False=>"boolean",
            "number" when current.ValueKind==JsonValueKind.Number=>"number",
            "string" when current.ValueKind==JsonValueKind.String=>"string",
            _=>""
        };
    }
    private string SelectionType()
    {
        var rows=SelectedRows();if(rows.Length==0)return "";
        string type=EditableType(rows[0]);return rows.All(row=>EditableType(row)==type)?type:"";
    }
    private void SelectionChanged()
    {
        var rows=SelectedRows();selectedKeys.Clear();foreach(var row in rows)selectedKeys.Add(RowKey(row));
        host.NativeNovelSelect(grid.SelectedItem as NovelVariableRow);
        // A shared target is only prefilled when every selected value agrees.
        // Choosing a boolean option still requires the explicit Apply action.
        if(rows.Length>1)host.NativeNovelValue(rows.All(row=>row.Value==rows[0].Value)?rows[0].Value:"");
        Refresh();
    }
    private bool CanSelectAction(string action)
    {
        var rows=SelectedRows();if(editingBusy||disposed)return false;
        if(action=="selectAll")return grid.Items.Count>0;
        if(action is "clearSelection" or "copy")return rows.Length>0;
        if(!host.NativeNovel.Apply.Enabled||rows.Length==0)return false;
        return action switch
        {
            "enableMany"=>rows.Any(row=>EditableType(row)=="boolean"&&!row.Source.GetProperty("value").GetBoolean()),
            "disableMany"=>rows.Any(row=>EditableType(row)=="boolean"&&row.Source.GetProperty("value").GetBoolean()),
            "dataUnlock"=>rows.Any(IsLocked),
            "dataSet" or "dataLock"=>SelectionType().Length>0,
            _=>false
        };
    }
    private WC.Button SelectionButton(string caption,string icon,string action,bool primary=false)
    {
        var button=WpfThemeResources.Button(caption,icon,async()=>await SelectionAction(action),primary);button.Height=36;button.MinWidth=96;Id(button,"NovelAction"+action);
        bindings.Add(()=>{WpfThemeResources.SetButton(button,caption,icon,primary);button.IsEnabled=CanSelectAction(action);});return button;
    }
    private async Task SelectionAction(string action)
    {
        if(!CanSelectAction(action))return;
        if(action=="selectAll"){grid.SelectAll();return;}
        if(action=="clearSelection"){grid.UnselectAll();return;}
        var rows=SelectedRows();
        if(action=="copy")
        {
            try{W.Clipboard.SetText(string.Join(Environment.NewLine,rows.Select(row=>row.RawName+"\t"+RowKey(row))));}
            catch(Exception error){host.NativeNovel.Feedback=SafeDiagnosticOutput.ExceptionSummary(error);Refresh();}
            return;
        }
        string input=host.NativeNovel.Value,operation=action;
        if(action is "enableMany" or "disableMany")
        {
            bool next=action=="enableMany";rows=rows.Where(row=>EditableType(row)=="boolean"&&row.Source.GetProperty("value").GetBoolean()!=next).ToArray();input=next?"true":"false";operation="dataSet";
        }
        else if(action=="dataUnlock")rows=rows.Where(IsLocked).ToArray();
        else if(SelectionType()=="number")
        {
            if(!TryNormalizeNumberInput(input,out input))
            {host.NativeNovel.Feedback="请填写有效数字。";Refresh();return;}
        }
        else if(SelectionType()=="boolean"&&!bool.TryParse(input,out _))
        {host.NativeNovel.Feedback="请选择开启或关闭。";Refresh();return;}
        if(rows.Length==0)return;
        editingBusy=true;Refresh();
        try{await host.NativeNovelBatch(operation,rows,input);}
        catch(Exception error){if(!disposed)host.NativeNovel.Feedback=SafeDiagnosticOutput.ExceptionSummary(error);}
        finally{editingBusy=false;if(!disposed)Refresh();}
    }
    internal static bool TryNormalizeNumberInput(string input,out string normalized)
    {
        normalized="";var culture=UiLanguage.NumericInputCulture;
        const NumberStyles styles=NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint|NumberStyles.AllowExponent;
        if((!double.TryParse(input.Trim(),styles,culture,out double number)&&!double.TryParse(input.Trim(),styles,CultureInfo.InvariantCulture,out number))||!double.IsFinite(number))return false;
        // Do not round-trip precise digits through double. This only normalizes
        // the validated UI separator before the existing invariant protocol.
        normalized=JsonNumberText(input.Trim().Replace(culture.NumberFormat.NumberDecimalSeparator,".",StringComparison.Ordinal));return true;
    }
    private static string JsonNumberText(string input)
    {
        bool negative=input.StartsWith('-');if(negative||input.StartsWith('+'))input=input[1..];
        int exponent=input.IndexOfAny(['e','E']);string suffix=exponent<0?"":input[exponent..];if(exponent>=0)input=input[..exponent];
        int point=input.IndexOf('.');string whole=(point<0?input:input[..point]).TrimStart('0');if(whole.Length==0)whole="0";
        string fraction=point<0?"":"."+(point==input.Length-1?"0":input[(point+1)..]);
        return (negative?"-":"")+whole+fraction+suffix;
    }
    private WC.Button Button(DesktopActionState state, string icon, string command, bool primary = false, bool snapshot = false)
    {
        var button = WpfThemeResources.Button(state.InitialText, icon, async () => { if (snapshot) await host.NativeNovelSnapshotCommand(command); else await host.NativeNovelCommand(command); }, primary);
        button.Height = 36; button.MinWidth = 100; Id(button, "Novel" + (snapshot ? "Snapshot" : "Command") + command);
        bindings.Add(() => { button.IsEnabled = state.Enabled; string caption=command switch{"refresh"=>"刷新","dataUndo"=>"撤销上次修改","dataSet"=>"应用","dataLock"=>"锁定","dataUnlock"=>"解除锁定",_=>string.IsNullOrEmpty(state.Text)?state.InitialText:state.Text};WpfThemeResources.SetButton(button,caption,icon,primary); });
        return button;
    }
    internal void Refresh()
    {
        if (syncing) return; syncing = true;
        try
        {
            var state = host.NativeNovel;
            if(mapPath!=state.Path){mapWorkspace?.Dispose();mapWorkspace=null;mapHost.Content=null;mapPath=state.Path;common.ClearRows();commonSource=null;commonNamesRevision=-1;}
            if(state.Wolf&&(state.Scope is "maps" or "events"))
            {
                if(mapWorkspace is null){mapWorkspace=new NativeWolfMapWorkspace(state.Path,host.NativeNovelMapVariables);mapHost.Content=mapWorkspace;}
                mapWorkspace.SetMode(state.Scope=="events");
            }
            if(displayLanguage!=UiLanguage.Current)
            {displayLanguage=UiLanguage.Current;foreach(var row in state.Rows)row.RefreshLanguage();}
            bool candidatesVisible=IsCandidateScope(state.Scope),tableVisible=!candidatesVisible&&state.Scope is not ("maps" or "events");
            if(candidatesVisible&&(!ReferenceEquals(commonSource,state.CommonRows)||commonNamesRevision!=state.NamesRevision))
            {
                commonSource=state.CommonRows;commonNamesRevision=state.NamesRevision;
                common.SetRows(ModificationCommonCandidates.BoardRows(state.CommonCandidates,state.NameLookup));
            }
            if(candidatesVisible)common.SetAvailability(state.Apply.Enabled,state.Status);
            if(tableVisible)
            {
            string nextKey=state.Path+"\0"+state.Scope+"\0"+state.Search;
            bool rebind=!ReferenceEquals(grid.ItemsSource,state.Rows),changedView=nextKey!=tableViewKey;
            if(rebind||changedView)
            {
                if(tableViewKey.Length>0&&grid.Items.Count>0&&WpfModificationLayout.TableScroll(grid) is {} oldScroll)
                {if(tablePositions.Count>=64&&!tablePositions.ContainsKey(tableViewKey))tablePositions.Remove(tablePositions.Keys.First());tablePositions[tableViewKey]=oldScroll.VerticalOffset;}
                if(changedView)selectedKeys.Clear();
                tableViewKey=nextKey;grid.ItemsSource=state.Rows;
                double offset=tablePositions.GetValueOrDefault(nextKey);int revision=++tableRestoreRevision;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,(Action)(()=>
                {if(!disposed&&grid.Visibility==W.Visibility.Visible&&revision==tableRestoreRevision&&tableViewKey==nextKey)WpfModificationLayout.TableScroll(grid)?.ScrollToVerticalOffset(offset);}));
            }
            var selected=state.Rows.Where(row=>selectedKeys.Contains(RowKey(row))).ToArray();
            if(selected.Length==0&&state.Selected is {} current&&state.Rows.Contains(current))selected=[current];
            if(!grid.SelectedItems.Cast<NovelVariableRow>().SequenceEqual(selected))
            {grid.SelectedItems.Clear();foreach(var row in selected)grid.SelectedItems.Add(row);}
            selectedKeys.Clear();foreach(var row in selected)selectedKeys.Add(RowKey(row));
            }
            if (search.Text != state.Search) search.Text = state.Search;
            if (value.Text != state.Value) value.Text = state.Value;
            string selectionType=SelectionType();Show(boolean,selectionType=="boolean");Show(value,selectionType!="boolean");
            boolean.SelectedIndex=bool.TryParse(state.Value,out bool flag)?flag?1:0:-1;
            value.IsEnabled=boolean.IsEnabled=!editingBusy&&state.ValueEnabled&&selectionType.Length>0;
            if (!ReferenceEquals(snapshots.ItemsSource, state.Snapshots)) snapshots.ItemsSource = state.Snapshots;
            snapshots.SelectedItem = state.Snapshot; snapshots.IsEnabled = state.SnapshotEnabled;
            foreach (var binding in bindings) binding();
        }
        finally { syncing = false; }
    }
    private void RefreshLanguage(object? sender,EventArgs e)
    {
        if(disposed)return;
        if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke((Action)(()=>RefreshLanguage(sender,e)));return;}
        Refresh();
    }
    public void Dispose() { disposed=true;tableRestoreRevision++;host.NativeNovelChanged -= Refresh;UiLanguage.LanguageChanged-=RefreshLanguage;common.Dispose();mapWorkspace?.Dispose(); }
    private ApplicationLanguage displayLanguage=UiLanguage.Current;
}

internal sealed class NovelValueDisplay : WD.IValueConverter
{
    internal static readonly NovelValueDisplay Instance=new();
    internal static string Format(NovelVariableRow row)
    {
        if(row.Source.TryGetProperty("type",out var type)&&type.GetString()=="boolean"&&row.Source.TryGetProperty("value",out var current)&&current.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return UiLanguage.Text(current.GetBoolean()?"开启":"关闭");
        return row.Value;
    }
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>value is NovelVariableRow row?Format(row):"";
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>throw new NotSupportedException();
}
