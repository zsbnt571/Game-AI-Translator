using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private Control? _renpyModificationPanel, _unsupportedModificationPanel;
    private readonly ComboBox _renpyDataScope=new GamePlanBox(){DropDownStyle=ComboBoxStyle.DropDownList,Width=140};
    private readonly TextBox _renpyDataSearch=new(){Width=240,PlaceholderText="搜索中文、原文或当前数值"};
    private readonly TextBox _renpyDataNote=new(){Width=230,MaxLength=120,PlaceholderText="可填写自己看得懂的中文名"};
    private readonly CheckBox _renpyDataChangedOnly=new WorkspaceSwitch(){AccessibleName="只看变化",Width=88};
    private readonly Label _renpyDataNamesStatus=WorkspaceLabel("");
    private Button? _renpyDataFavorite,_renpyDataSaveNote;
    private RenpyDataPresentation _renpyDataPresentation=new();
    private readonly HashSet<string> _renpyChangedNames=new(StringComparer.Ordinal);
    private int? _renpyDataGeneration;
    private readonly TextBox _renpyDataValue=new(){Width=180,AccessibleName="修改后的值"};
    private readonly CheckBox _renpyDataBoolean=new WorkspaceSwitch(){Text="开启 / 已拥有",Width=160};
    private readonly Label _renpyDataStatus=WorkspaceLabel(""),_renpyDataHint=WorkspaceLabel(""),_renpyDataSelection=WorkspaceLabel("请选择一项");
    private readonly DataGridView _renpyDataGrid=new WorkspaceDataGrid(){Name="RenpyDataGrid",Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,MultiSelect=true,AllowUserToResizeRows=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,BorderStyle=BorderStyle.None};
    private Control? _renpyDataValueField;
    private readonly Label _renpyDataSortStatus=WorkspaceLabel("名称 · 自然顺序");
    private TableLayoutPanel? _renpyDataNotesPanel;
    private Button? _renpyDataLaunch,_renpyDataRefresh,_renpyDataApply,_renpyDataUndo,_renpyDataBackup;
    private readonly Dictionary<IGameDataConnection,string> _renpyDataBackups=new();
    private List<JsonElement> _renpyDataRows=new();
    private string? _renpyDataPath,_renpyDataSaveDirectory;
    private bool _renpyDataBusy;
    private string _renpyDataState="ready",_renpyCgState="notRecognized";
    private bool _renpyDataTruncated,_renpySnapshotFailure,_renpyCgIncomplete;
    private int _renpyCgRows;
    private int _renpyUndoCount;
    private static readonly string[] RenpyScopes=["money","items","cg","variables","favorites"];

    private Control BuildRenpyModificationWorkspace()
    {
        var panel=new WorkspaceAdaptiveGridColumn{List=_renpyDataGrid,Padding=new(0,8,0,4),Name="RenpyAdaptiveWorkspace"};
        _renpyDataScope.Items.AddRange(["金钱","物品","CG 解锁记录","全部变量","我的常用"]);_renpyDataScope.SelectedIndex=0;
        _renpyDataLaunch=IconButton("修改并启动","play",async(_,_)=>await LaunchScriptTranslationSessionAsync("modify"),true,130);
        _renpyDataRefresh=IconButton("刷新数据","refresh",async(_,_)=>await RefreshRenpyDataAsync(),false,108);
        var search=SearchField(_renpyDataSearch);search.Width=260;
        WorkspaceAdd(panel,WorkspaceRow(_renpyDataScope,search,WorkspaceLabel("只看变化"),_renpyDataChangedOnly,_renpyDataRefresh,_renpyDataLaunch));
        BuildRenpyNumericFinder(panel);
        _renpyDataGrid.Columns.Add("label","中文名 / 备注");_renpyDataGrid.Columns.Add("name","原文 / 变量路径");_renpyDataGrid.Columns.Add("value","当前值");_renpyDataGrid.Columns.Add("hint","生效范围");
        _renpyDataGrid.Columns[0].FillWeight=25;_renpyDataGrid.Columns[1].FillWeight=30;_renpyDataGrid.Columns[2].FillWeight=16;_renpyDataGrid.Columns[3].FillWeight=29;
        foreach(DataGridViewColumn column in _renpyDataGrid.Columns)column.SortMode=DataGridViewColumnSortMode.Programmatic;
        _renpyDataGrid.AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.None;
        ((WorkspaceDataGrid)_renpyDataGrid).ApplyPalette();
        WorkspaceAdd(panel,BuildRenpyBatchActions());
        WorkspaceAdd(panel,_renpyDataGrid,SizeType.Percent,100);
        _renpyDataSelection.AutoSize=false;_renpyDataSelection.AutoEllipsis=true;_renpyDataSelection.Height=26;
        WorkspaceAdd(panel,_renpyDataSelection,SizeType.Absolute,26);
        _renpyDataApply=IconButton("应用修改","check",async(_,_)=>await WriteRenpyDataAsync(false),true,108);
        _renpyDataUndo=IconButton("撤销上次修改","back",async(_,_)=>await WriteRenpyDataAsync(true),false,132);
        _renpyDataBackup=IconButton("打开存档备份","folder",(_,_)=>{if(SelectedScriptTranslationSession()?.Connection is {} connection&&_renpyDataBackups.TryGetValue(connection,out var backup))System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(backup){UseShellExecute=true});},false,132);
        _renpyDataValueField=SearchField(_renpyDataValue,"edit");_renpyDataValueField.Width=180;
        _renpyDataNotesPanel=WorkspaceColumn();_renpyDataNotesPanel.AutoSize=true;_renpyDataNotesPanel.Visible=false;
        var details=IconButton("备注与帮助","edit",(_,_)=>{_renpyDataNotesPanel.Visible=!_renpyDataNotesPanel.Visible;panel.PerformLayout();panel.Parent?.PerformLayout();},false,116);details.Name="RenpyNotesToggle";
        WorkspaceAdd(panel,WorkspaceRow(_renpyDataValueField,_renpyDataBoolean,_renpyDataUndo,_renpyDataBackup,details));
        WorkspaceAdd(panel,_renpyDataHint);
        _renpyDataFavorite=IconButton("加入常用","tag",(_,_)=>SaveRenpyDataView(true),false,118);
        _renpyDataSaveNote=IconButton("保存备注","check",(_,_)=>SaveRenpyDataView(false),false,108);
        var note=SearchField(_renpyDataNote,"edit");note.Width=250;
        WorkspaceAdd(_renpyDataNotesPanel,WorkspaceRow(WorkspaceLabel("我的中文名"),note,_renpyDataSaveNote,_renpyDataFavorite,IconButton("补齐中文名","translate",(_,_)=>RequestRenpyDataNames(),false,120)));
        WorkspaceAdd(_renpyDataNotesPanel,_renpyDataNamesStatus);
        WorkspaceAdd(_renpyDataNotesPanel,WorkspaceLabel("名称与分类为通用词义提示，请结合当前值核对。原文始终保留；备注和收藏只保存在本工具。"));
        WorkspaceAdd(_renpyDataNotesPanel,WorkspaceLabel("找不到数值？使用“数值查找”逐次缩小范围。读档或重新连接后重新查找，中文备注和常用仍会保留。"));
        WorkspaceAdd(panel,_renpyDataNotesPanel);
        _renpyDataScope.SelectedIndexChanged+=(_,_)=>FilterRenpyData();_renpyDataSearch.TextChanged+=(_,_)=>FilterRenpyData();
        _renpyDataChangedOnly.CheckedChanged+=(_,_)=>{FilterRenpyData();if(!_renpyDataChangedOnly.Checked&&_renpyDataHint.Text.StartsWith("只列出与上次刷新"))_renpyDataHint.Text="";};
        ConfigureRenpyEditing();
        _renpyDataGrid.SelectionChanged+=(_,_)=>{if(!_renpyLoading)SelectRenpyData();};
        // Only blank container surfaces clear the selection. Editor controls,
        // action buttons and labels retain it so their actions have a target.
        void BlankClick(object? sender,MouseEventArgs e)
        {if(e.Button==MouseButtons.Left){_renpyDataGrid.ClearSelection();_renpyDataGrid.CurrentCell=null;}}
        void HookBlank(Control c)
        {if(c is Panel or TableLayoutPanel)c.MouseDown+=BlankClick;foreach(Control child in c.Controls)HookBlank(child);}
        HookBlank(panel);
        _unsupportedModificationPanel=WorkspaceColumn();_unsupportedModificationPanel.AutoSize=true;
        WorkspaceAdd((TableLayoutPanel)_unsupportedModificationPanel,WorkspaceLabel("此引擎暂未接入游戏修改。当前支持 RPG Maker MV/MZ，以及 Ren’Py 数值和解锁记录。"));
        return panel;
    }

    private JsonElement? SelectedRenpyRow()=>_renpyDataGrid.SelectedRows.Count==1&&_renpyDataGrid.SelectedRows[0].Tag is JsonElement row?row:null;
    private static string RenpyValue(JsonElement value)=>value.ValueKind switch{JsonValueKind.True=>"开启",JsonValueKind.False=>"关闭",JsonValueKind.Null=>"暂无记录",_=>value.ToString()};
    private void SelectRenpyData()
    {
        if(_renpyLoading)return;
        _renpyLoading=true;
        var row=SelectedRenpyRow();_renpyEditingRow=row;_renpyEditingConnection=_renpyDataSnapshotConnection;bool boolean=row?.GetProperty("kind").GetString()=="bool";
        _renpyDataValue.Visible=row is not null&&!boolean;if(_renpyDataValueField is not null)_renpyDataValueField.Visible=row is not null&&!boolean;_renpyDataBoolean.Visible=row is not null&&boolean;
        _renpyDataSelection.Text=row?.GetProperty("name").GetString()??(_renpyDataGrid.SelectedRows.Count>1?$"已选择 {_renpyDataGrid.SelectedRows.Count} 项 · 右键批量操作":"请选择一项");
        string name=row?.GetProperty("name").GetString()??"";
        _renpyDataNote.Text=_renpyDataPresentation.Notes.GetValueOrDefault(name,"");
        if(_renpyDataFavorite is not null){_renpyDataFavorite.Text=_renpyDataPresentation.Favorites.Contains(name)?"移出常用":"加入常用";_renpyDataFavorite.Enabled=row is not null;}
        if(_renpyDataSaveNote is not null)_renpyDataSaveNote.Enabled=row is not null;
        _renpyDataValue.Text=row?.GetProperty("value").ToString()??"";
        _renpyDataBoolean.Checked=row?.GetProperty("value").ValueKind==JsonValueKind.True;
        _renpyDataValue.Enabled=row is not null&&!_renpyDataBusy;_renpyDataBoolean.Enabled=row is not null&&!_renpyDataBusy;
        _renpyLoading=false;
        if(_renpyDataApply is not null)_renpyDataApply.Enabled=row is not null&&!_renpyDataBusy&&SelectedScriptTranslationSession()?.Connection is {Connected:true} selected&&ReferenceEquals(_renpyDataSnapshotConnection,selected);
    }
    private void FilterRenpyData()
    {
        if(_renpyLoading)return;
        var selected=_renpyDataGrid.SelectedRows.Cast<DataGridViewRow>().Where(r=>r.Tag is JsonElement).Select(r=>((JsonElement)r.Tag!).GetProperty("name").GetString()!).ToHashSet(StringComparer.Ordinal);
        string? current=(_renpyDataGrid.CurrentRow?.Tag as JsonElement?)?.GetProperty("name").GetString();
        int first=_renpyDataGrid.FirstDisplayedScrollingRowIndex;
        _renpyLoading=true;_renpyDataGrid.SuspendLayout();
        try
        {
            _renpyDataGrid.Rows.Clear();
            string group=RenpyScopes[Math.Max(0,_renpyDataScope.SelectedIndex)],query=_renpyDataSearch.Text.Trim();
            var visible=new List<JsonElement>();
            foreach(var row in _renpyDataRows)
            {
                string name=row.GetProperty("name").GetString()!,label=RenpyDataLabel(name);
                if(_renpyFinderPanel?.Visible==true&&!_renpyNumericFinder.Includes(row))continue;
                if(group=="favorites"?!_renpyDataPresentation.Favorites.Contains(name):group!="variables"&&row.GetProperty("group").GetString()!=group)continue;
                if(_renpyDataChangedOnly.Checked&&!_renpyChangedNames.Contains(name))continue;
                if(query.Length>0&&!name.Contains(query,StringComparison.OrdinalIgnoreCase)&&!label.Contains(query,StringComparison.OrdinalIgnoreCase)&&!RenpyValue(row.GetProperty("value")).Equals(query,StringComparison.OrdinalIgnoreCase)&&!row.GetProperty("value").ToString().Equals(query,StringComparison.OrdinalIgnoreCase))continue;
                visible.Add(row);
            }
            visible.Sort(CompareRenpyRows);
            DataGridViewRow? currentRow=null;
            foreach(var row in visible)
            {
                string name=row.GetProperty("name").GetString()!;
                int i=_renpyDataGrid.Rows.Add(RenpyDataLabel(name),name,RenpyValue(row.GetProperty("value")),row.GetProperty("hint").GetString());
                var item=_renpyDataGrid.Rows[i];item.Tag=row;if(name==current)currentRow=item;
            }
            if(currentRow is not null)_renpyDataGrid.CurrentCell=currentRow.Cells[0];
            _renpyDataGrid.ClearSelection();
            foreach(DataGridViewRow item in _renpyDataGrid.Rows)if(selected.Contains(((JsonElement)item.Tag!).GetProperty("name").GetString()!))item.Selected=true;
            if(_renpyDataGrid.SelectedRows.Count==0&&_renpyDataGrid.Rows.Count>0)_renpyDataGrid.Rows[0].Selected=true;
            if(first>=0&&_renpyDataGrid.Rows.Count>0)_renpyDataGrid.FirstDisplayedScrollingRowIndex=Math.Min(first,_renpyDataGrid.Rows.Count-1);
            UpdateRenpyDataStatus();
        }
        finally{_renpyDataGrid.ResumeLayout();_renpyLoading=false;}
        SelectRenpyData();QueueRenpyDataNames();
    }
    private void UpdateRenpyDataStatus()
    {
        string group=RenpyScopes[Math.Max(0,_renpyDataScope.SelectedIndex)];
        bool filtered=_renpyDataSearch.Text.Trim().Length>0||_renpyDataChangedOnly.Checked||(_renpyNumericFinder.Active&&_renpyFinderPanel?.Visible==true);
        int shown=_renpyDataGrid.Rows.Count;
        if(_renpySnapshotFailure){_renpyDataStatus.Text="数据读取失败，请重试刷新。"+(_renpyDataRows.Count>0?" 当前仍显示上次读取的数据。":"");return;}
        if(_renpyDataGeneration is null){_renpyDataStatus.Text="尚未读取游戏数据，请点击“刷新数据”。";return;}
        if(shown==0&&filtered)_renpyDataStatus.Text="当前筛选没有匹配项。可清空搜索、关闭“只看变化”，或返回全部变量。";
        else if(shown==0&&group=="favorites")_renpyDataStatus.Text="尚无常用项。选中变量后，在“备注与帮助”中加入常用。";
        else if(shown==0&&_renpyDataState=="saveNotLoaded"&&group is "money" or "items")_renpyDataStatus.Text="尚未加载存档进度。开始游戏或读档后，再刷新金钱和物品。";
        else if(shown==0&&(group=="cg"||(_renpyDataState=="saveNotLoaded"&&group=="variables")))
        {
            _renpyDataStatus.Text=_renpyCgState switch
            {
                "notLoaded"=>"画廊数据尚未加载。打开游戏画廊后刷新；进度变量需开始游戏或读档。",
                "unsupported"=>"已识别画廊，但部分解锁记录的数据结构暂不支持读取。",
                "failed"=>"画廊解锁记录读取失败，请重试刷新。",
                "ready"=>"当前没有可列出的画廊解锁记录。",
                _=>"尚未识别到可修改的画廊解锁记录；这不代表游戏没有画廊。"
            };
            if(group=="variables"&&_renpyDataState=="saveNotLoaded"&&_renpyCgState!="notLoaded")_renpyDataStatus.Text+=" 存档进度尚未加载。";
        }
        else if(shown==0)_renpyDataStatus.Text="当前分类没有可显示的数据。可切换“全部变量”查看，或在游戏状态改变后刷新。";
        else
        {
            _renpyDataStatus.Text=$"已读取 {_renpyDataRows.Count} 项 · 当前显示 {shown} 项";
            if(_renpyDataState=="saveNotLoaded")_renpyDataStatus.Text+=$" · 可查看 {_renpyCgRows} 项画廊记录，存档进度尚未加载";
            if(_renpyNumericFinder.Active&&_renpyFinderPanel?.Visible==true)_renpyDataStatus.Text+=" · 数值筛选中";
        }
        if(_renpyDataTruncated)_renpyDataStatus.Text+=" · 数据较多，部分深层数据未展开";
        if(shown>0&&_renpyCgIncomplete&&group is "cg" or "variables")_renpyDataStatus.Text+=" · 部分画廊数据未能读取";
    }
    private void RenderRenpyModificationState()
    {
        if(IsDisposed||Disposing||_renpyDataLaunch is null)return;
        string? path=_selectedGame?.ExePath;
        if(!SameGame(path,_renpyDataPath))
        {
            _renpyDataPath=path;_renpyDataRows.Clear();_renpyDataGrid.Rows.Clear();_renpyDataSaveDirectory=null;_renpyUndoCount=0;_renpyDataHint.Text="";_renpyDataStatus.Text="";_renpyDataNamesStatus.Text="";_renpySnapshotFailure=false;_renpyDataTruncated=false;_renpyCgIncomplete=false;_renpyDataState="ready";_renpyCgState="notRecognized";_renpyCgRows=0;
            _renpyDataPresentation=path is null?new():RenpyDataPresentation.Load(path);_renpyDataGeneration=null;_renpyChangedNames.Clear();_renpyDataChangedOnly.Checked=false;SelectRenpyData();
            _renpyDataConnection=null;_renpyDataSnapshotConnection=null;ClearRenpyNumericSession("已切换游戏，请重新开始数值查找。");
        }
        var selectedConnection=SelectedScriptTranslationSession()?.Connection;
        bool connected=selectedConnection?.Connected==true;
        if(!ReferenceEquals(selectedConnection,_renpyDataConnection))
        {
            _renpyDataConnection=selectedConnection;_renpyDataSnapshotConnection=null;_renpyDataGeneration=null;_renpyChangedNames.Clear();_renpyDataRows.Clear();_renpyDataGrid.Rows.Clear();_renpyDataSaveDirectory=null;_renpyUndoCount=0;
            _renpySnapshotFailure=false;_renpyDataTruncated=false;_renpyCgIncomplete=false;_renpyDataState="ready";_renpyCgState="notRecognized";_renpyCgRows=0;_renpyDataStatus.Text="";
            ClearRenpyNumericSession("连接已变化，请进入游戏后重新开始数值查找。");
        }
        bool stopped=SameGame(_desktopGameState?.Game.ExePath,path)&&_desktopGameState?.Process==FusionGameProcessState.Stopped;
        _renpyDataLaunch.Visible=!connected;_renpyDataLaunch.Enabled=stopped&&!_renpyDataBusy&&_selectedGame is {} game&&!_gameSessions.Get(game).Busy;
        _renpyDataRefresh!.Enabled=connected&&!_renpyDataBusy;_renpyDataUndo!.Enabled=connected&&!_renpyDataBusy&&_renpyUndoCount>0;
        _renpyDataBackup!.Visible=SelectedScriptTranslationSession()?.Connection is {} current&&_renpyDataBackups.ContainsKey(current);
        if(!connected){_renpyDataSnapshotConnection=null;if(_renpyDataGeneration is not null||_renpyNumericFinder.Active){_renpyDataGeneration=null;_renpyChangedNames.Clear();ClearRenpyNumericSession("游戏已断开连接，请重新连接后开始查找。");}_renpyDataRows.Clear();_renpyDataGrid.Rows.Clear();_renpyDataStatus.Text=stopped?"点击“修改并启动”，进入游戏或读档后刷新。无需开启翻译。":"当前游戏未连接。保存并退出游戏后，使用“修改并启动”连接。";}
        else if(_renpyDataStatus.Text.Length==0&&!_renpyDataBusy)_renpyDataStatus.Text="已连接。进入游戏或读档后点击“刷新数据”。";
        ((WorkspaceDataGrid)_renpyDataGrid).ApplyPalette();
        _renpyDataValue.Enabled=_renpyDataBoolean.Enabled=connected&&!_renpyDataBusy&&SelectedRenpyRow() is not null;
        _renpyDataApply!.Enabled=connected&&ReferenceEquals(_renpyDataSnapshotConnection,selectedConnection)&&!_renpyDataBusy&&SelectedRenpyRow() is not null;
        RenderRenpyNumericFinder();
    }
    private async Task RefreshRenpyDataAsync()
    {
        if(_renpyDataBusy||_selectedGame is not {} game||SelectedScriptTranslationSession()?.Connection is not {Connected:true} connection)return;
        _renpyDataBusy=true;RenderRenpyModificationState();
        try{await LoadRenpySnapshotAsync(game.ExePath,connection);}
        catch(Exception ex){if(IsCurrentRenpyConnection(game.ExePath,connection)){_renpySnapshotFailure=true;UpdateRenpyDataStatus();_renpyDataHint.Text=SafeDiagnosticOutput.ExceptionSummary(ex);}SelectRenpyData();}
        finally{_renpyDataBusy=false;RenderRenpyModificationState();}
    }
    private async Task LoadRenpySnapshotAsync(string path,IGameDataConnection connection)
    {
        var result=await connection.RequestAsync(new(){["op"]="dataSnapshot"});
        if(!IsCurrentRenpyConnection(path,connection))return;
        var rows=result.GetProperty("rows").EnumerateArray().Select(r=>r.Clone()).ToList();int generation=result.GetProperty("generation").GetInt32();
        _renpyChangedNames.Clear();
        if(_renpyDataGeneration==generation)
        {
            var previous=_renpyDataRows.ToDictionary(r=>r.GetProperty("name").GetString()!,r=>r.GetProperty("value").GetRawText(),StringComparer.Ordinal);
            foreach(var row in rows){string name=row.GetProperty("name").GetString()!;if(previous.TryGetValue(name,out string? old)&&old!=row.GetProperty("value").GetRawText())_renpyChangedNames.Add(name);}
        }
        _renpyDataGeneration=generation;_renpyDataRows=rows;_renpyDataSnapshotConnection=connection;
        _renpyDataMainMenu=result.GetProperty("mainMenu").GetBoolean();
        _renpySnapshotFailure=false;_renpyDataTruncated=result.GetProperty("truncated").GetBoolean();
        _renpyDataState=result.TryGetProperty("dataState",out var dataState)?dataState.GetString()??"ready":_renpyDataMainMenu?"saveNotLoaded":"ready";
        _renpyCgRows=result.TryGetProperty("cgRows",out var cgRows)?cgRows.GetInt32():rows.Count(r=>r.GetProperty("group").GetString()=="cg");
        _renpyCgState=result.TryGetProperty("cgState",out var cgState)?cgState.GetString()??"notRecognized":_renpyCgRows>0?"ready":"notRecognized";
        _renpyCgIncomplete=result.TryGetProperty("cgDiagnostics",out var cgDiagnostics)&&cgDiagnostics.ValueKind==JsonValueKind.Array&&cgDiagnostics.GetArrayLength()>0;
        string finderReset=_renpyNumericFinder.Observe(connection,generation,_renpyDataMainMenu,result.GetProperty("truncated").GetBoolean(),rows);
        if(finderReset.Length>0)_renpyFinderStatus.Text=finderReset;
        _renpyDataSaveDirectory=result.GetProperty("savedir").GetString();_renpyUndoCount=result.GetProperty("undo").GetInt32();
        try{if(SelectedScriptTranslationSession() is {} session)EnsureRenpyDataNames(path,session,_fusion!.GameSettings(_appliedDesktopSettings,path));}catch(InvalidOperationException){}
        FilterRenpyData();
        if(_renpyDataChangedOnly.Checked)_renpyDataHint.Text="只列出与上次刷新相比已改变的值；首次读取或读档后的第一次刷新用于建立比较基准。";
        UpdateRenpyDataStatus();
    }
    private Task WriteRenpyDataAsync(bool undo)=>WriteRenpyCapturedAsync(undo,SelectedRenpyRow(),_renpyDataBoolean.Checked,_renpyDataValue.Text,_renpyDataSnapshotConnection);
    private async Task WriteRenpyCapturedAsync(bool undo,JsonElement? row,bool boolean,string input,IGameDataConnection? expected)
    {
        if(_renpyDataBusy||_selectedGame is not {} game||SelectedScriptTranslationSession()?.Connection is not {Connected:true} connection)return;
        if(!SameGame(_renpyDataPath,game.ExePath)||!ReferenceEquals(_renpyDataSnapshotConnection,connection)){RenderRenpyModificationState();_renpyDataHint.Text="游戏连接已变化，请刷新数据后再修改。";return;}
        if(!ReferenceEquals(connection,expected)||!undo&&row is null)return;
        if(!undo&&((row!.Value.GetProperty("kind").GetString()=="bool"&&row.Value.GetProperty("value").ValueKind==(boolean?JsonValueKind.True:JsonValueKind.False))||(row.Value.GetProperty("kind").GetString()!="bool"&&row.Value.GetProperty("value").ToString()==input.Trim())))return;
        JsonObject request=new(){["op"]=undo?"dataUndo":"dataSet"};
        try
        {
            if(!undo)
            {
                request["entry"]=row!.Value.GetProperty("id").GetString();string kind=row.Value.GetProperty("kind").GetString()!;
                request["value"]=kind=="bool"?JsonValue.Create(boolean):kind=="int"?JsonValue.Create(long.Parse(input,CultureInfo.InvariantCulture)):JsonValue.Create(double.Parse(input,CultureInfo.InvariantCulture));
            }
            _renpyDataBusy=true;RenderRenpyModificationState();
            if(!_renpyDataBackups.ContainsKey(connection))
            {
                if(string.IsNullOrWhiteSpace(_renpyDataSaveDirectory))throw new IOException("请先刷新数据。");
                string savedir=_renpyDataSaveDirectory;
                string backup=Path.Combine(RenpyGameAdapter.Storage(game.ExePath),"save-backups",DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff"));
                await Task.Run(()=>BackupRenpySaveLocations(game.ExePath,savedir,backup));_renpyDataBackups[connection]=backup;
            }
            if(!IsCurrentRenpyConnection(game.ExePath,connection)||!ReferenceEquals(_renpyDataSnapshotConnection,connection))return;
            await connection.RequestAsync(request);
            if(!IsCurrentRenpyConnection(game.ExePath,connection))return;
            _renpyDataHint.Text=undo?"已撤销上次修改。":row!.Value.GetProperty("group").GetString()=="cg"?"解锁记录已更新；重新打开画廊查看。游戏若另有解锁条件，以画廊实际状态为准。":"已修改当前进度，请在游戏中存档。";
            await LoadRenpySnapshotAsync(game.ExePath,connection);
        }
        catch(Exception ex){if(IsCurrentRenpyConnection(game.ExePath,connection))_renpyDataHint.Text=ex is FormatException or OverflowException?"请输入有效数字；整数项不能填写小数。":SafeDiagnosticOutput.ExceptionSummary(ex);SelectRenpyData();}
        finally{_renpyDataBusy=false;RenderRenpyModificationState();}
    }
    internal static void BackupRenpySaves(string savedir,string target)
    {
        savedir=Path.GetFullPath(savedir);if(Path.GetPathRoot(savedir)==savedir)throw new IOException("存档路径无效。");
        for(string? p=savedir;p is not null;p=Path.GetDirectoryName(p))if(Directory.Exists(p)&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("存档目录包含链接，未修改。");
        Directory.CreateDirectory(target);
        if(Directory.Exists(savedir))
        {
            var pending=new Stack<string>();pending.Push(savedir);
            while(pending.Count>0)foreach(string item in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var attributes=File.GetAttributes(item);if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("存档文件包含链接，未修改。");
                if((attributes&FileAttributes.Directory)!=0){pending.Push(item);continue;}
                string destination=Path.Combine(target,Path.GetRelativePath(savedir,item));Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(item,destination,false);
            }
        }
        File.WriteAllText(Path.Combine(target,"fusion-backup-info.json"),JsonSerializer.Serialize(new{source=savedir,createdUtc=DateTime.UtcNow}));
    }
}


