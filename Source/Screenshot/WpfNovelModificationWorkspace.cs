using W = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Data;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

internal sealed class WpfNovelModificationWorkspace : WC.StackPanel, IDisposable
{
    private readonly INovelWorkspaceHost host;
    private readonly List<Action> bindings = [];
    private readonly WC.TextBox search = new() { Width = 240, Height = 36 };
    private readonly WC.TextBox value = new() { Width = 220, Height = 36 };
    private readonly WC.DataGrid grid = new()
    {
        Height = 300, AutoGenerateColumns = false, IsReadOnly = true,
        CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false,
        SelectionMode = WC.DataGridSelectionMode.Single, SelectionUnit = WC.DataGridSelectionUnit.FullRow,
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

    internal WpfNovelModificationWorkspace(INovelWorkspaceHost host)
    {
        this.host = host;
        var state = host.NativeNovel;
        Id(search, "NovelSearch"); Id(value, "NovelValue"); Id(grid, "NovelVariables"); Id(snapshots, "NovelSnapshots");
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
        Add(categories);
        Add(Row(Button(state.Start, "play", "start", true), Button(state.Refresh, "refresh", "refresh"), Button(state.Undo,"back","dataUndo")));
        Add(Label(() => state.Status)); Add(Label(() => state.Feedback));
        var columns = new WpfModificationColumns(); Id(columns,"NovelDataBrowser"); Add(columns);
        var list = new WC.StackPanel(); columns.Children.Add(list);
        var searchField = WpfModificationLayout.Search(search,"搜索名称、编号或当前数值"); searchField.Margin=new(0,4,0,8); list.Children.Add(searchField);
        bindings.Add(()=>Show(searchField,state.Scope is not ("common" or "maps" or "events")));
        var speedControl=new WpfGameSpeedControl{Commit=rate=>host.NativeNovelCommand("speed:"+rate.ToString(System.Globalization.CultureInfo.InvariantCulture)),Margin=new(0,4,0,12)};
        list.Children.Add(speedControl);bindings.Add(()=>{speedControl.SetState(state.SpeedControl);Show(speedControl,state.Scope=="common");});
        var commonHint=Text("角色与关系按对象分类；待确认候选请先核对原始名称和当前值。未识别的项目仍可在变量或通用数据中查找。");list.Children.Add(commonHint);
        bindings.Add(()=>Show(commonHint,IsCandidateScope(state.Scope)));
        common.Execute=async request=>
        {
            string? key=request["key"]?.GetValue<string>();
            var row=host.NativeNovel.CommonRows.FirstOrDefault(item=>item.Source.GetProperty("id").GetString()==key);
            if(row is null||!host.NativeNovel.Refresh.Enabled)return;
            host.NativeNovelSelect(row);host.NativeNovelValue(request["value"]?.ToJsonString()??row.Value);
            await host.NativeNovelCommand(request["op"]?.GetValue<string>() switch{"lock"=>"dataLock","unlock"=>"dataUnlock",_=>"dataSet"});
        };
        common.RequestNames=keys=>{var wanted=keys.ToHashSet(StringComparer.Ordinal);host.NativeNovelRequestNames(host.NativeNovel.CommonRows.Where(row=>wanted.Contains(row.Source.GetProperty("id").GetString()??"")));};
        list.Children.Add(common.View);bindings.Add(()=>Show(common.View,IsCandidateScope(state.Scope)));
        var emptyCommon=Label(()=>state.Scope=="common"?"尚未识别到可直接修改的常用数值，可在变量和通用数据中查找。":"当前分类没有匹配的项目。可调整搜索，或在变量和通用数据中查找。");list.Children.Add(emptyCommon);
        bindings.Add(()=>Show(emptyCommon,IsCandidateScope(state.Scope)&&state.CommonRows.Length==0));
        foreach (var column in new[] { ("变量名称", "Name", 55), ("当前值", "Value", 35), ("锁定", "Locked", 10) })
        {
            WC.DataGridColumn view=column.Item2=="Name"
                ?new WC.DataGridTemplateColumn{Header=UiLanguage.Text("名称 / Name"),CellTemplate=WpfModificationLayout.Names("DisplayName","Original","RawName"),Width=new(column.Item3,WC.DataGridLengthUnitType.Star)}
                :new WC.DataGridTextColumn { Header = UiLanguage.Text(column.Item1), Binding = new WD.Binding(column.Item2), Width = new(column.Item3, WC.DataGridLengthUnitType.Star) };
            grid.Columns.Add(view);bindings.Add(()=>view.Header=UiLanguage.Text(column.Item2=="Name"?"名称 / Name":column.Item1));
        }
        WpfModificationLayout.Table(grid);
        list.Children.Add(mapHost);bindings.Add(()=>Show(mapHost,state.Scope is "maps" or "events"));
        list.Children.Add(grid);bindings.Add(()=>Show(grid,!IsCandidateScope(state.Scope)&&state.Scope is not ("maps" or "events")));
        var count=Label(()=>$"{state.Rows.Length} 项");list.Children.Add(count);bindings.Add(()=>Show(count,!IsCandidateScope(state.Scope)&&state.Scope is not ("maps" or "events")));
        var detail=new WC.StackPanel(); var details=WpfModificationLayout.Details(detail);Id(details,"NovelDetails");columns.Children.Add(details);
        bindings.Add(()=>Show(details,!IsCandidateScope(state.Scope)&&state.Scope is not ("maps" or "events")));
        var selectedName=Label(()=>state.Selected?.DisplayName??UiLanguage.Text("选择一项数据"),localize:false);selectedName.FontSize=20;selectedName.FontWeight=W.FontWeights.SemiBold;detail.Children.Add(selectedName);
        detail.Children.Add(Label(()=>state.Selected?.Original??"",localize:false));
        detail.Children.Add(Label(()=>state.Selected is {} row?UiLanguage.Text("编号")+"  "+ReadableModificationKey.Format(row.Source.GetProperty("id").GetString()??""):UiLanguage.Text("在左侧选择项目，查看详情并修改。"),localize:false));
        detail.Children.Add(Label(()=>state.Selected is {} row?UiLanguage.Format($"当前值  {row.Value}"):"",localize:false));
        detail.Children.Add(Text("目标值"));
        var lockValue=Button(state.Lock,"lock","dataLock");var unlockValue=Button(state.Unlock,"unlock","dataUnlock");
        bindings.Add(()=>{bool locked=state.Selected?.Source.GetProperty("locked").GetBoolean()==true;Show(lockValue,!locked);Show(unlockValue,locked);});
        detail.Children.Add(Row(lockValue,unlockValue,value,Button(state.Apply,"check","dataSet",true)));
        detail.Children.Add(Text("开关填写 true 或 false。修改应用到当前选择的项目。"));
        Add(Text("修改当前运行中的变量。开关填写 true 或 false；进度请按游戏原有方式保存。读档或切换数据后请重新刷新。"));
        var snapshotPanel = new WC.StackPanel();
        snapshotPanel.Children.Add(Text("状态快照（测试）"));
        snapshotPanel.Children.Add(Row(snapshots, Button(state.SnapshotRefresh, "refresh", "list", snapshot: true), Button(state.SnapshotCapture, "copy", "capture", snapshot: true), Button(state.SnapshotLoad, "back", "load", snapshot: true)));
        snapshotPanel.Children.Add(Label(() => state.SnapshotFeedback));
        snapshotPanel.Children.Add(Text("保存当前状态的独立副本；载入前会自动备份当前状态。此功能仍需实机验证，重要进度请同时使用游戏自己的存档。"));
        Add(snapshotPanel); bindings.Add(() => snapshotPanel.Visibility = state.SnapshotVisible ? W.Visibility.Visible : W.Visibility.Collapsed);
        search.TextChanged += (_, _) => { if (!syncing) host.NativeNovelSearch(search.Text); };
        value.TextChanged += (_, _) => { if (!syncing) host.NativeNovelValue(value.Text); };
        grid.SelectionChanged += (_, _) => { if (!syncing) host.NativeNovelSelect(grid.SelectedItem as NovelVariableRow); };
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
            foreach (var binding in bindings) binding();
            bool candidatesVisible=IsCandidateScope(state.Scope),tableVisible=!candidatesVisible&&state.Scope is not ("maps" or "events");
            if(candidatesVisible&&(!ReferenceEquals(commonSource,state.CommonRows)||commonNamesRevision!=state.NamesRevision))
            {
                commonSource=state.CommonRows;commonNamesRevision=state.NamesRevision;
                common.SetRows(ModificationCommonCandidates.BoardRows(state.CommonCandidates,state.NameLookup));
            }
            if(candidatesVisible)common.SetAvailability(state.Refresh.Enabled,state.Status);
            if(tableVisible)
            {
            string nextKey=state.Path+"\0"+state.Scope+"\0"+state.Search;
            bool rebind=!ReferenceEquals(grid.ItemsSource,state.Rows),changedView=nextKey!=tableViewKey;
            if(rebind||changedView)
            {
                if(tableViewKey.Length>0&&grid.Items.Count>0&&WpfModificationLayout.TableScroll(grid) is {} oldScroll)
                {if(tablePositions.Count>=64&&!tablePositions.ContainsKey(tableViewKey))tablePositions.Remove(tablePositions.Keys.First());tablePositions[tableViewKey]=oldScroll.VerticalOffset;}
                tableViewKey=nextKey;grid.ItemsSource=state.Rows;
                double offset=tablePositions.GetValueOrDefault(nextKey);int revision=++tableRestoreRevision;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,(Action)(()=>
                {if(!disposed&&grid.Visibility==W.Visibility.Visible&&revision==tableRestoreRevision&&tableViewKey==nextKey)WpfModificationLayout.TableScroll(grid)?.ScrollToVerticalOffset(offset);}));
            }
            if(!ReferenceEquals(grid.SelectedItem,state.Selected))grid.SelectedItem=state.Selected;
            }
            if (search.Text != state.Search) search.Text = state.Search;
            if (value.Text != state.Value) value.Text = state.Value;
            value.IsEnabled = state.ValueEnabled;
            if (!ReferenceEquals(snapshots.ItemsSource, state.Snapshots)) snapshots.ItemsSource = state.Snapshots;
            snapshots.SelectedItem = state.Snapshot; snapshots.IsEnabled = state.SnapshotEnabled;
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
