namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private readonly List<GameActionButton> _dataTabs=[];
    private readonly Label _dataSelectedName=WorkspaceLabel("选择一项数据"),_dataOriginalName=WorkspaceLabel(""),_dataCurrentLabel=WorkspaceLabel(""),_dataKeyLabel=WorkspaceLabel("");
    private readonly Label _dataDescription=WorkspaceLabel("在左侧选择项目，查看详情并修改。"),_dataSectionHint=WorkspaceLabel("常用操作");
    private readonly TextBox _dataTextValue=new(){Width=190,MaxLength=2000};
    private readonly GamePlanBox _dataMapChoice=new(){Width=260,DropDownStyle=ComboBoxStyle.DropDownList};
    private readonly GamePlanBox _dataEventKind=new(){Width=260,DropDownStyle=ComboBoxStyle.DropDownList};
    private GameActionButton? _dataLockButton;
    private bool _dataRefreshPending,_dataSizing,_dataMapChoosing,_dataSizePending;
    private TableLayoutPanel? _dataLayout,_dataColumns,_dataScalarEditor,_dataObjectEditor;
    private Control? _dataListSearch,_dataListSurface,_dataDetailSurface;
    private GameCommonBoard? _dataCommonBoard;
    private GameActionButton? _dataApplyButton;
    private bool _dataScalarUpdating;
    private readonly GameDataIcon _dataSelectedIcon=new(){Size=new(64,64),Margin=new(0,0,12,0),Visible=false};
    private Control? _dataSteps,_dataMapToolbar,_dataContextActions,_dataLockActions,_dataBattleActions;
    private Control? _dataMapLabel,_dataReturnMap;
    private WorkspaceScrollView? _dataDetailScroll;
    private WorkspaceNumberField? _dataNumberField;
    private readonly Dictionary<string,DataViewState> _dataViewStates=new();
    private int _dataTabPrevious,_dataRevision,_dataLimit=160,_dataTotal,_dataSelectedMapId,_dataDetailRevision;
    private Task _dataDetailLoad=Task.CompletedTask;
    private readonly Dictionary<string,int> _dataDetailSelections=new();
    private sealed record DataViewState(string Query,string? SelectedKey,int FirstRow,int Limit,int Map,bool FollowMap);
    private sealed record MapChoice(int Id,string Name,string Chinese,bool Current)
    {public override string ToString()=>$"{Id:0000}  {(Chinese.Length>0?Chinese+" · ":"")}{Name}{(Current?" · 当前":"")}";}
    private string ActiveDataScope=>_dataScope.SelectedIndex==8?(_dataEventKind.SelectedIndex switch{1=>"commonEvents",2=>"interpreters",_=>"mapEvents"}):DataScopes[Math.Max(0,_dataScope.SelectedIndex)];
    private string ViewStateKey(int tab)=>_dataViewPath+"|"+tab;
    private void QueueModificationSize()
    {
        if(_dataSizePending||!IsHandleCreated||IsDisposed)return;_dataSizePending=true;
        BeginInvoke((Action)(()=>{_dataSizePending=false;if(!IsDisposed)SizeModificationColumns();}));
    }
    private void SizeModificationColumns()
    {
        if(_dataSizing||_dataColumns is null||_dataLayout is null||_gameSessionPage is null||!_dataLayout.Visible||!_dataColumns.IsHandleCreated)return;
        int index=_dataLayout.GetRow(_dataColumns);if(index<0||index>=_dataLayout.RowStyles.Count)return;
        int top=_gameSessionPage.PointToClient(_dataColumns.PointToScreen(Point.Empty)).Y+_gameSessionPage.ScrollOffset;
        int height=Math.Max(365*DeviceDpi/96,_gameSessionPage.ClientSize.Height-top-98*DeviceDpi/96);
        if(_dataScope.SelectedIndex==0&&_dataCommonBoard is not null)height=Math.Max(40*DeviceDpi/96,Math.Min(height,_dataCommonBoard.ContentHeight));
        var row=_dataLayout.RowStyles[index];if(Math.Abs(row.Height-height)<2)return;
        _dataSizing=true;try{row.Height=height;}finally{_dataSizing=false;}
    }
    private Control BuildGameModificationWorkspace()
    {
        var page=ModificationColumn();page.Name="ModificationWorkspace";page.AutoSize=true;page.Dock=DockStyle.Top;_dataLayout=page;
        _dataMapTimer.Tick+=async(_,_)=>await SyncGameMapAsync();_dataMapTimer.Start();FormClosed+=(_,_)=>_dataMapTimer.Dispose();
        page.Layout+=(_,_)=>QueueModificationSize();page.VisibleChanged+=(_,_)=>QueueModificationSize();_gameSessionPage!.SizeChanged+=(_,_)=>QueueModificationSize();
        _dataLaunch=IconButton("修改并启动","play",async(_,_)=>await LaunchGameDataAsync(false),true,116);
        _dataToggle=WorkspaceButton("开启修改",async(_,_)=>await ToggleGameDataAsync());
        _dataRestore=WorkspaceButton("移除连接组件",async(_,_)=>await LaunchGameDataAsync(true));
        _dataRefresh=IconButton("刷新","refresh",async(_,_)=>{SelectedDataSession()?.Labels?.RetryMissing();await RefreshGameDataAsync();},false,92);
        ((GameActionButton)_dataToggle).MinimumLogicalWidth=92;
        var head=new WorkspaceAlignedRow(_dataSectionHint,_dataLaunch,_dataToggle,_dataRefresh){Margin=new(0,0,0,4)};head.FlexibleColumn(0,500);
        var tabs=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Top,WrapContents=true,Margin=new(0,0,0,8)};
        string[] names=["常用","角色","道具","武器","防具","变量","开关","地图","事件","已锁定","通用数据"];
        _dataScope.Items.AddRange(names);_dataScope.SelectedIndex=0;_dataScope.Visible=false;
        for(int i=0;i<names.Length;i++)
        {
            int index=i;var tab=IconButton(names[i],"",(_,_)=>_dataScope.SelectedIndex=index,false,i==10?84:i==9?70:56);
            tab.TabStyle=true;tab.Primary=i==0;tab.Margin=new(0,0,3,0);_dataTabs.Add(tab);tabs.Controls.Add(tab);
        }
        var tabbar=new TableLayoutPanel{ColumnCount=2,RowCount=1,Dock=DockStyle.Top,AutoSize=false,Height=44,Margin=new(0,0,0,8)};tabs.AutoSize=false;tabs.Dock=DockStyle.Fill;
        tabbar.ColumnStyles.Add(new(SizeType.Percent,100));tabbar.ColumnStyles.Add(new(SizeType.AutoSize));tabbar.RowStyles.Add(new(SizeType.Percent,100));
        var tabActions=new WorkspaceControlRow{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=false,Anchor=AnchorStyles.Top|AnchorStyles.Right,Margin=Padding.Empty};
        foreach(var action in new[]{_dataLaunch,_dataToggle,_dataRefresh}){action.Margin=new(0,0,8,0);tabActions.Controls.Add(action);}
        tabbar.Controls.Add(tabs,0,0);tabbar.Controls.Add(tabActions,1,0);WorkspaceAdd(page,tabbar,SizeType.Absolute,44);
        tabs.SizeChanged+=(_,_)=>{int lines=1,x=0;foreach(Control c in tabs.Controls){if(x>0&&x+c.Width+c.Margin.Horizontal>tabs.Width){lines++;x=0;}x+=c.Width+c.Margin.Horizontal;}int row=page.GetRow(tabbar);if(row>=0){float h=(lines*40+4)*DeviceDpi/96f+tabbar.Margin.Vertical;if(Math.Abs(page.RowStyles[row].Height-h)>1)page.RowStyles[row].Height=h;}};
        _dataEventKind.Items.AddRange(["地图事件","公共事件","运行中事件"]);_dataEventKind.SelectedIndex=0;
        _dataMapLabel=WorkspaceLabel("浏览地图");
        _dataReturnMap=IconButton("跟随当前地图","",async(_,_)=>{_dataMapFollowCurrent=true;_dataSelectedMapId=0;_dataMapSyncRevision=0;_dataRevision++;ResetDataDetails();await RefreshGameDataAsync();},false,116);
        _dataMapToolbar=ModificationRow(_dataMapLabel,_dataMapChoice,_dataReturnMap,_dataEventKind);
        _dataMapToolbar.Visible=false;WorkspaceAdd(page,_dataMapToolbar);
        _dataMapChoice.SelectedIndexChanged+=async(_,_)=>{if(_dataMapChoosing)return;var choice=_dataMapChoice.SelectedItem as MapChoice;_dataSelectedMapId=choice?.Id??0;_dataMapFollowCurrent=choice?.Current??true;_dataMapSyncRevision=0;_dataRevision++;ResetDataDetails();await RefreshGameDataAsync();};
        _dataEventKind.SelectedIndexChanged+=async(_,_)=>{if(_dataScope.SelectedIndex!=8)return;UpdateModificationPresentation();_dataRevision++;_dataMap.ClearMap();_dataPage.Text="";ResetDataDetails();await RefreshGameDataAsync();};
        _dataScope.SelectedIndexChanged+=async(_,_)=>
        {
            _dataViewStates[ViewStateKey(_dataTabPrevious)]=new(_dataSearch.Text,_dataSelected?.GetProperty("key").GetString(),Math.Max(0,_dataGrid.FirstDisplayedScrollingRowIndex),_dataLimit,_dataSelectedMapId,_dataMapFollowCurrent);
            _dataTabPrevious=_dataScope.SelectedIndex;_dataRevision++;_dataLimit=160;_dataSearch.Clear();_dataSelectedMapId=0;_dataMapFollowCurrent=true;_dataMapSyncRevision=0;
            if(_dataViewStates.TryGetValue(ViewStateKey(_dataTabPrevious),out var saved)){_dataSearch.Text=saved.Query;_dataLimit=saved.Limit;_dataSelectedMapId=saved.Map;_dataMapFollowCurrent=saved.FollowMap;}
            foreach(var tab in _dataTabs){bool active=_dataTabs.IndexOf(tab)==_dataScope.SelectedIndex;if(tab.Primary!=active){tab.Primary=active;tab.Invalidate();}}
            _dataSectionHint.Text=names[_dataScope.SelectedIndex]+(_dataScope.SelectedIndex==10?" · 搜索字段名称或当前值":"");
            _dataMapToolbar.Visible=_dataScope.SelectedIndex is 7 or 8;_dataEventKind.Visible=_dataScope.SelectedIndex==8;
            _dataListSearch!.Visible=_dataScope.SelectedIndex!=7;
            _dataMapPanel!.Visible=_dataScope.SelectedIndex==7;_dataGrid.Visible=_dataScope.SelectedIndex!=7;
            ResetDataDetails();UpdateModificationPresentation();await RefreshGameDataAsync();
        };
        var columns=new TableLayoutPanel{ColumnCount=2,RowCount=1,Height=400,Dock=DockStyle.Fill,Margin=Padding.Empty};_dataColumns=columns;
        columns.ColumnStyles.Add(new(SizeType.Percent,57));columns.ColumnStyles.Add(new(SizeType.Percent,43));columns.RowStyles.Add(new(SizeType.Percent,100));
        var list=ModificationColumn();list.AutoSize=false;list.Padding=new(10);list.Dock=DockStyle.Fill;
        var search=SearchField(_dataSearch);_dataListSearch=search;search.Height=36;WorkspaceAdd(list,search,SizeType.Absolute,36);
        search.VisibleChanged+=(_,_)=>{list.RowStyles[0].Height=search.Visible?36*DeviceDpi/96:0;};
        _dataSearch.KeyDown+=async(_,e)=>{if(e.KeyCode==Keys.Enter){e.SuppressKeyPress=true;_dataRevision++;_dataLimit=160;ResetDataDetails();await RefreshGameDataAsync();}};
        _dataGrid.Columns.Add(new DataGridViewTextBoxColumn{Name="Original",HeaderText="名称 / Name",FillWeight=70});
        _dataGrid.Columns.Add(new DataGridViewTextBoxColumn{Name="Chinese",HeaderText="中文名",Visible=false});
        _dataGrid.Columns.Add(new DataGridViewTextBoxColumn{Name="Value",HeaderText="当前值",FillWeight=18});
        _dataGrid.Columns.Add(new DataGridViewTextBoxColumn{Name="Lock",HeaderText="锁定",FillWeight=12});
        _dataGrid.SelectionChanged+=(_,_)=>SelectGameDataRow();
        _dataGrid.CellClick+=async(_,e)=>
        {
            if(e.RowIndex<0||e.ColumnIndex!=3||_dataGrid.Rows[e.RowIndex].Tag is not System.Text.Json.JsonElement row||!row.GetProperty("canLock").GetBoolean())return;
            await ExecuteGameDataAsync(new(){["op"]=row.GetProperty("locked").GetBoolean()?"unlock":"lock",["key"]=row.GetProperty("key").GetString(),["value"]=System.Text.Json.Nodes.JsonNode.Parse(row.GetProperty("value").GetRawText())});
        };
        _dataGrid.Scroll+=async(_,_)=>{if(!_dataLoading&&_dataLimit<_dataTotal&&_dataGrid.FirstDisplayedScrollingRowIndex+_dataGrid.DisplayedRowCount(false)>=_dataGrid.Rows.Count-5){_dataLimit+=160;await RefreshGameDataAsync();}};
        var viewport=new Panel{Dock=DockStyle.Fill,Margin=new(0,10,0,6)};viewport.Controls.Add(_dataGrid);WorkspaceAdd(list,viewport,SizeType.Percent,100);
        var map=ModificationColumn();map.AutoSize=false;map.Dock=DockStyle.Fill;map.Visible=false;_dataMapPanel=map;
        WorkspaceAdd(map,_dataMap,SizeType.Percent,100);WorkspaceAdd(map,_dataMapSelection);
        WorkspaceAdd(map,ModificationRow(IconButton("−","",(_,_)=>_dataMap.Zoom(.8f),false,36),IconButton("适合窗口","",(_,_)=>_dataMap.Fit(),false,88),IconButton("+","",(_,_)=>_dataMap.Zoom(1.25f),false,36)));
        var legend=WorkspaceLabel("绿：通行  灰：阻挡  蓝：水域  紫：地形标记\n橙：事件  亮蓝：玩家 · 滚轮缩放 / 右键拖动");legend.MaximumSize=new(460,0);WorkspaceAdd(map,legend);
        viewport.Controls.Add(map);_dataMap.TileSelected+=async(_,_)=>await ShowSelectedMapTileAsync();
        _dataPrevious=WorkspaceButton("上一页",(_,_)=>{});_dataNext=WorkspaceButton("下一页",(_,_)=>{}); // Kept out of the visual tree: continuous scrolling owns list loading.
        WorkspaceAdd(list,_dataPage);
        var left=new GameDataSurface{Dock=DockStyle.Fill,Padding=new(3),Margin=new(0,0,7,0)};left.Controls.Add(list);_dataListSurface=left;columns.Controls.Add(left,0,0);
        var detail=ModificationColumn();detail.AutoSize=true;detail.Padding=new(12);_dataEditor=detail;
        _dataSelectedName.Font=new Font("Microsoft YaHei UI",15,FontStyle.Bold);FontManager.MarkPreviewTextRoot(_dataSelectedName);
        var titles=ModificationColumn();WorkspaceAdd(titles,_dataSelectedName);WorkspaceAdd(titles,_dataOriginalName);
        var nameHeader=new TableLayoutPanel{ColumnCount=2,RowCount=1,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Margin=Padding.Empty};nameHeader.ColumnStyles.Add(new(SizeType.AutoSize));nameHeader.ColumnStyles.Add(new(SizeType.Percent,100));nameHeader.RowStyles.Add(new(SizeType.AutoSize));
        nameHeader.Controls.Add(_dataSelectedIcon,0,0);nameHeader.Controls.Add(titles,1,0);WorkspaceAdd(detail,nameHeader);WorkspaceAdd(detail,_dataDescription);WorkspaceAdd(detail,_dataKeyLabel);WorkspaceAdd(detail,_dataCurrentLabel);
        _dataScalarEditor=ModificationColumn();_dataScalarEditor.AutoSize=true;
        _dataLockButton=IconButton("锁定","",async(_,_)=>await ChangeGameDataAsync(_dataSelected?.GetProperty("locked").GetBoolean()==true?"unlock":"lock"),false,92);
        _dataNumberField=new WorkspaceNumberField(_dataValue);
        _dataApplyButton=IconButton("应用","check",async(_,_)=>await ChangeGameDataAsync("set"),true,76);
        WorkspaceAdd(_dataScalarEditor,ModificationRow(WorkspaceLabel("目标值"),_dataNumberField,_dataBoolean,_dataTextValue,_dataApplyButton));
        _dataValue.KeyDown+=async(_,e)=>{if(e.KeyCode==Keys.Enter){e.SuppressKeyPress=true;await ChangeGameDataAsync("set");}};
        _dataBoolean.CheckedChanged+=async(_,_)=>{if(!_dataScalarUpdating&&_dataSelected?.GetProperty("type").GetString()=="boolean")await ChangeGameDataAsync("set");};
        Button Step(string text,int delta)=>IconButton(text,"",async(_,_)=>await AdjustGameDataValueAsync(delta),false,60);
        _dataSteps=ModificationRow(Step("− 10",-10),Step("− 1",-1),Step("+ 1",1),Step("+ 10",10));WorkspaceAdd(_dataScalarEditor,_dataSteps);WorkspaceAdd(_dataScalarEditor,ModificationRow(_dataLockButton));
        _dataChinese.Width=150;var aliases=ModificationColumn();aliases.AutoSize=true;aliases.Visible=false;
        WorkspaceAdd(aliases,SearchField(_dataChinese,"edit"));WorkspaceAdd(aliases,WorkspaceButton("保存中文名",(_,_)=>SaveGameDataAlias()));
        WorkspaceAdd(_dataScalarEditor,ModificationRow(IconButton("恢复输入","",(_,_)=>ShowGameDataScalar(_dataSelected),false,86),IconButton("编辑中文名","edit",(_,_)=>aliases.Visible=!aliases.Visible,false,110)));
        WorkspaceAdd(_dataScalarEditor,aliases);WorkspaceAdd(detail,_dataScalarEditor);
        _dataObjectEditor=ModificationColumn();_dataObjectEditor.AutoSize=true;WorkspaceAdd(detail,_dataObjectEditor);
        _dataDetailScroll=new WorkspaceScrollView{Dock=DockStyle.Fill};_dataDetailScroll.Controls.Add(detail);
        var right=new GameDataSurface{Dock=DockStyle.Fill,Padding=new(3),Margin=new(7,0,0,0)};right.Controls.Add(_dataDetailScroll);_dataDetailSurface=right;columns.Controls.Add(right,1,0);
        _dataCommonBoard=new GameCommonBoard{Dock=DockStyle.Fill,Margin=Padding.Empty};
        _dataCommonBoard.ContentHeightChanged+=QueueModificationSize;
        _dataCommonBoard.Execute=ExecuteGameDataAsync;
        columns.Controls.Add(_dataCommonBoard,0,0);columns.SetColumnSpan(_dataCommonBoard,2);UpdateModificationPresentation();
        foreach(var label in new[]{_dataOriginalName,_dataKeyLabel,_dataCurrentLabel,_dataDescription}){label.TextChanged+=(_,_)=>label.Visible=label.Text.Length>0;label.Visible=label.Text.Length>0;}
        detail.SizeChanged+=(_,_)=>{foreach(var label in new[]{_dataKeyLabel,_dataCurrentLabel,_dataDescription})label.MaximumSize=new(Math.Max(80,detail.ClientSize.Width-detail.Padding.Horizontal),0);};
        _dataBrowser=columns;WorkspaceAdd(page,columns,SizeType.Absolute,400);
        _dataContextActions=ModificationRow(IconButton("恢复全队","",async(_,_)=>await ExecuteGameDataAsync(new(){["op"]="recoverParty"}),false,84),IconButton("备份存档","folder",(_,_)=>BackupRpgSaves(),false,94));
        WorkspaceAdd(page,_dataContextActions);
        var battle=ModificationRow();_dataBattleActions=battle;
        battle.Controls.Add(WorkspaceLabel("战斗操作"));
        foreach(var (title,action) in new[]{("胜利","victory"),("逃跑","escape"),("敌人 HP: 1","enemyOne"),("敌人 HP: 最大","enemyMax"),("队伍 HP: 1","partyOne"),("失败","defeat"),("队伍 HP: 0","partyZero")})
            battle.Controls.Add(IconButton(title,"",async(_,_)=>await ExecuteGameDataAsync(new(){["op"]="battleAction",["action"]=action}),false,68));
        WorkspaceAdd(page,battle);
        _dataLockActions=ModificationRow(IconButton("保存锁定","",async(_,_)=>await SaveRpgLocksAsync(),false,92),IconButton("读取锁定","",async(_,_)=>await LoadRpgLocksAsync(),false,92));
        WorkspaceAdd(page,_dataLockActions);UpdateModificationPresentation();
        var footer=new WorkspaceAlignedRow(_dataFeedback,IconButton("撤销上次修改","",async(_,_)=>await ExecuteGameDataAsync(new(){["op"]="undo"}),false,116),IconButton("解除全部锁定","",async(_,_)=>await ExecuteGameDataAsync(new(){["op"]="unlockAll"}),false,116)){Margin=new(0,5,0,0)};footer.FlexibleColumn(0,520);footer.AutoSize=false;footer.Height=40;WorkspaceAdd(page,footer,SizeType.Absolute,40);
        CompactModificationControls(page);
        foreach(var actions in new[]{_dataContextActions,_dataBattleActions}.OfType<Control>())
        {
            actions.Margin=new(0,4,0,14);
            foreach(var button in actions.Controls.OfType<GameActionButton>()){button.LogicalHeight=34;button.RefreshMetrics();button.Margin=new(0,2,10,2);}
        }
        FormClosed+=(_,_)=>{foreach(var s in _dataSessions.Values)s.Dispose();};return page;
    }
    private static void CompactModificationControls(Control root)
    {
        if(root is GameActionButton button){button.LogicalHeight=28;button.RefreshMetrics();}
        if(root is WorkspaceNumberField or WorkspaceSwitch)root.Height=GameActionButton.RowHeight(root,28);
        if(root is GamePlanBox plan){plan.LogicalHeight=28;plan.MinimumSize=new(plan.MinimumSize.Width,0);plan.RefreshMetrics();}
        foreach(Control child in root.Controls)CompactModificationControls(child);
    }
    private static TableLayoutPanel ModificationColumn()=>new WorkspaceContentColumn();
    private static FlowLayoutPanel ModificationRow(params Control[] controls){var p=WorkspaceRow(controls);p.AutoSizeMode=AutoSizeMode.GrowAndShrink;return p;}
    private async Task AdjustGameDataValueAsync(int delta)
    {
        if(_dataLoading||SelectedDataSession() is not {Enabled:true,Ready:true}||_dataSelected is not {} row)return;
        decimal current=row.GetProperty("value").GetDecimal();
        _dataValue.Value=Math.Clamp(current+delta,_dataValue.Minimum,_dataValue.Maximum);await ChangeGameDataAsync("set");
    }
    private void UpdateModificationPresentation()
    {
        bool common=_dataScope.SelectedIndex==0;
        if(_dataCommonBoard is not null){_dataCommonBoard.Visible=common;if(common)_dataCommonBoard.BringToFront();}
        if(_dataListSurface is not null)_dataListSurface.Visible=!common;
        if(_dataDetailSurface is not null)_dataDetailSurface.Visible=!common;
        if(_dataContextActions is not null)_dataContextActions.Visible=common;
        if(_dataBattleActions is not null)_dataBattleActions.Visible=common;
        if(_dataLockActions is not null)_dataLockActions.Visible=_dataScope.SelectedIndex==9;
        bool browse=_dataScope.SelectedIndex==7||(_dataScope.SelectedIndex==8&&_dataEventKind.SelectedIndex==0);
        if(_dataMapLabel is not null)_dataMapLabel.Visible=browse;
        _dataMapChoice.Visible=browse;
        if(_dataReturnMap is not null)_dataReturnMap.Visible=browse;
        QueueModificationSize();
    }
}
internal sealed class GameDataSurface:Panel
{
    internal GameDataSurface(){DoubleBuffered=true;}
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;using var pen=new Pen(UiTheme.Current.Border);using var path=WorkspaceDrawing.Round(Rectangle.Inflate(ClientRectangle,-1,-1),8*DeviceDpi/96);e.Graphics.DrawPath(pen,path);}
}
