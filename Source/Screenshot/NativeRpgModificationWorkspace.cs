using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;
namespace ScreenshotTranslationUiTester;
public sealed partial class MainForm
{
    private sealed partial class NativeRpgController
    {

    private Dictionary<string,GameDataSession> _dataSessions=>owner._dataSessions;
    private readonly Dictionary<string,bool> _dataCapabilities=new(StringComparer.OrdinalIgnoreCase);
    private readonly NativeRpgLabel _dataStatus=WorkspaceLabel("");
    private readonly NativeRpgLabel _dataFeedback=WorkspaceLabel("");
    private readonly NativeRpgLabel _dataPage=WorkspaceLabel("");
    private readonly NativeRpgChoice _dataScope=new(){Width=136};
    private readonly NativeRpgText _dataSearch=new(){Width=190,PlaceholderText=UiLanguage.Text("搜索原名、译名或编号")};
    private readonly NativeRpgGrid _dataGrid=new(){Name="NativeRpgGrid",Height=250};
    private readonly NativeRpgNumber _dataValue=new NativeRpgNumber(){Width=118,Minimum=int.MinValue,Maximum=int.MaxValue};
    private readonly NativeRpgToggle _dataBoolean=new NativeRpgToggle(){Text="开启",Height=36,Width=118,LocalizedAccessibleName="目标开关状态"};
    private readonly NativeRpgText _dataChinese=new(){Width=180,PlaceholderText=UiLanguage.Text("填写译名")};
    private readonly NativeRpgMap _dataMap=new(){Height=260};
    private readonly NativeRpgLabel _dataMapSelection=WorkspaceLabel("点击地图选择格子");
    private NativeRpgButton? _dataLaunch,_dataToggle,_dataRefresh,_dataRestore;
    private NativeRpgElement? _dataBrowser,_dataEditor,_dataMapPanel;
    private string? _dataViewPath;
    private int _dataEpoch;
    private bool _dataLoading,_dataSelecting,_dataWriting,_dataPolling;
    private readonly SemaphoreSlim _dataWriteGate=new(1,1);
    private bool _dataAliasWritable=true;
    private int _dataMapGeneration;
    private int _dataObjectGeneration=-1;
    private JsonElement? _dataSelected;
    private Dictionary<string,string> _dataAliases=new(StringComparer.Ordinal);
    private static readonly string[] DataScopes=["common","characters","items","weapons","armors","variables","switches","maps","mapEvents","locks","fields"];

    private bool IsDataView(string path,int epoch)=>!IsDisposed&&!Disposing&&SameGame(owner._selectedGame?.ExePath,path)&&_dataEpoch==epoch&&_dataViewSession?.Connection.Connected==true&&ReferenceEquals(_dataViewSession,SelectedDataSession());
    private GameDataSession? SelectedDataSession()=>owner._selectedGame is { } g&&_dataSessions.TryGetValue(g.ExePath,out var s)?s:null;
    private void RenderGameModificationState()
    {
        if(_dataLaunch is null)return;
        var game=owner._selectedGame;
        if(!SameGame(game?.ExePath,_dataViewPath)||!ReferenceEquals(_dataViewSession,SelectedDataSession()))
        {
            SaveDataReturnView();
            _dataViewPath=game?.ExePath;_dataEpoch++;_dataRevision++;_dataSelectedMapId=0;_dataMapFollowCurrent=true;_dataMapSyncRevision=0;ResetDataDetails();_dataLoading=false;_dataSelected=null;
            _dataSelecting=true;_dataGrid.Rows.Clear();_dataSelecting=false;_dataEditor!.Enabled=false;
            _dataMapPanel!.Visible=false;_dataSearch.Clear();_dataChinese.Clear();_dataPage.Text="";
            _dataCommonBoard?.ClearRows();
            _dataAliases=new(StringComparer.Ordinal);_dataAliasWritable=true;
            _dataGrid.IconAtlas?.Dispose();_dataGrid.IconAtlas=null;
            if(game is not null)try{_dataGrid.IconAtlas=RpgMakerDataAdapter.ReadIconAtlas(game.ExePath);}catch{}
            if(game is not null)try{var f=Path.Combine(RpgMakerDataAdapter.Storage(game.ExePath),"data-labels.json");if(File.Exists(f))_dataAliases=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(f))??new();}catch{_dataAliasWritable=false;}
            RestoreDataReturnView(SelectedDataSession());
        }
        bool supported=false;
        if(game is not null){if(!_dataCapabilities.TryGetValue(game.ExePath,out supported)){try{supported=RpgMakerDataAdapter.Detect(game.ExePath) is not null;}catch{} _dataCapabilities[game.ExePath]=supported;}}
        var state=SelectedDataSession();
        bool stopped=game is not null&&SameGame(owner._desktopGameState?.Game.ExePath,game.ExePath)&&owner._desktopGameState?.Process==FusionGameProcessState.Stopped&&!owner._gameSessions.Get(game).LaunchPending;
        if(stopped&&state?.Connection.Connected==true){state.Dispose();state.Enabled=false;state.Ready=false;state.Locks=0;state.Feedback="游戏已退出。下次可选择“修改并启动”。";}
        bool connected=state?.Connection.Connected==true;
        if((!connected||state?.Enabled!=true)&&(_dataGrid.Rows.Count>0||_dataMap.MapId!=0)){_dataSelecting=true;try{_dataGrid.Rows.Clear();}finally{_dataSelecting=false;}_dataMap.ClearMap();_dataPage.Text="";ResetDataDetails();_dataCommonBoard?.ClearRows();}
        bool busy=owner._gameActionRunning||_dataLoading;
        _dataStatus.Text=!supported?"此引擎的数据修改尚未接入。当前首版支持 RPG Maker MV/MZ 标准桌面结构。":connected?
            (state!.Enabled?"修改已开启 · 锁定 "+state.Locks+" 项":"已连接 · 修改关闭"):
            stopped?"首次使用会备份启动配置并安装数据连接组件。":"当前游戏未连接。请保存并退出，再选择“修改并启动”。";
        var feedback=state?.Feedback??owner._gameSessions.Snapshot().FirstOrDefault(s=>SameGame(s.Game.ExePath,game?.ExePath))?.Feedback??"";
        if(state?.Labels?.Status is {Length:>0} labelStatus)feedback+=(feedback.Length>0?" · ":"")+labelStatus;
        _dataFeedback.Text=feedback;
        _dataLaunch.Visible=supported&&!connected;_dataLaunch.Enabled=stopped&&!busy;
        _dataRestore!.Visible=supported;_dataRestore.Enabled=stopped&&!busy;
        _dataToggle!.Visible=connected;_dataToggle.Enabled=!_dataWriting;_dataToggle.Text=state?.Enabled==true?"关闭修改":"开启修改";
        _dataBrowser!.Visible=supported;_dataBrowser.Enabled=true;_dataRefresh!.Enabled=connected&&state!.Enabled&&!_dataWriting;
        _dataEditor!.Enabled=connected&&state!.Enabled&&state.Ready;
        if(_dataContextActions is not null)_dataContextActions.Enabled=connected&&state!.Enabled&&state.Ready&&!_dataWriting;
        if(_dataLockActions is not null)_dataLockActions.Enabled=connected&&state!.Enabled&&state.Ready&&!_dataWriting;
        if(_dataBattleActions is not null)_dataBattleActions.Enabled=connected&&state!.Enabled&&state.Ready&&state.InBattle&&!_dataWriting;
        _dataCommonBoard?.SetAvailability(connected&&state!.Enabled&&state.Ready,connected?state!.Ready?"开启修改后即可直接操作。":"请先进入游戏或读取存档。":"启动并连接游戏后可使用常用操作。");
        _dataCommonBoard?.SetSpeedContext(ModificationSpeedState.ContextFor(state?.Connection),busy||_dataWriting);

        if(state?.Enabled==true&&state.Translation?.Enabled==true&&_dataGrid.Visible)RefreshDataLabels();
    }

    private async Task RefreshGameDataStatusAsync()
    {
        if(_dataPolling||_dataLoading||_dataWriting||owner._selectedGame is not { } game||SelectedDataSession() is not {Enabled:true} state||!state.Connection.Connected)return;
        int epoch=_dataEpoch;_dataPolling=true;
        try
        {
            var result=await state.Connection.RequestAsync(new(){["op"]="status"});
            if(!state.Enabled||!IsDataView(game.ExePath,epoch)||_dataWriting||_dataLoading)return;
            state.InBattle=result.GetProperty("inBattle").GetBoolean();
            bool changedGeneration=result.GetProperty("generation").GetInt32()!=state.Generation;
            state.Ready=result.GetProperty("ready").GetBoolean();state.Locks=result.GetProperty("lockCount").GetInt32();
            if(changedGeneration){_dataSnapshots.Invalidate(state.Connection);await RefreshGameDataAsync();}else
            {
                RenderGameModificationState();
                if(!owner._workspaceResizing&&_dataObjectEditor?.Visible==true&&_dataEventConditionsRefresh is { } refreshConditions)await refreshConditions();
            }
        }
        catch(Exception ex){AppLog.Write("rpg-status",SafeDiagnosticOutput.ExceptionSummary(ex));}
        finally{_dataPolling=false;}
    }


    private Task LaunchGameDataAsync(bool restore)=>owner.LaunchRpgSessionAsync(restore?"restore":"modify");
    private async Task ToggleGameDataAsync()
    {
        if(SelectedDataSession() is not { } state)return;
        await ExecuteGameDataAsync(new(){["op"]=state.Enabled?"disable":"enable"});
    }

    private async Task RefreshGameDataAsync(bool preferCached=false)
    {
        RenderGameModificationState();
        if(owner._selectedGame is not { } game||SelectedDataSession() is not {Enabled:true} state||!state.Connection.Connected)return;
        string signature=$"{_dataEpoch}|{_dataRevision}|{ActiveDataScope}|{_dataSearch.Text}|{_dataLimit}|{_dataSelectedMapId}|{_dataMapFollowCurrent}";
        if(_dataLoading){if(_dataRequestSignature!=signature)_dataRefreshPending=true;return;}
        _dataRequestSignature=signature;
        int epoch=_dataEpoch,revision=_dataRevision;string scope=ActiveDataScope,query=ActiveDataScope is "common" or "maps"?"":_dataSearch.Text.Trim();
        bool StillCurrent()=>state.Enabled&&IsDataView(game.ExePath,epoch)&&revision==_dataRevision&&ReferenceEquals(state,SelectedDataSession());
        bool revalidate=false;
        _dataLoading=true;RenderGameModificationState();
        try
        {
            var aliasKeys=new JsonArray(_dataAliases.Where(x=>query.Length>0&&x.Value.Contains(query,StringComparison.OrdinalIgnoreCase)).Take(200).Select(x=>(JsonNode?)JsonValue.Create(x.Key)).ToArray());
            var request=new JsonObject(){["op"]="snapshot",["scope"]=scope=="maps"?"gold":scope,["query"]=scope=="maps"?"":query,["aliasKeys"]=aliasKeys,["offset"]=0,["limit"]=_dataLimit,["mapId"]=_dataMapFollowCurrent?0:_dataSelectedMapId};
            JsonElement result;
            if(preferCached&&scope is not ("maps" or "mapEvents" or "commonEvents" or "interpreters")&&_dataSnapshots.TryGet(game.ExePath,state.Connection,request,out result,out bool fresh))
            {
                revalidate=!fresh;
                if(signature==_dataAppliedSignature&&ReferenceEquals(_dataSnapshotConnection,state.Connection))return;
            }
            else result=await _dataSnapshots.ReadAsync(game.ExePath,state.Connection,request);
            if(!StillCurrent())return;
            _dataSnapshotConnection=state.Connection;
            state.InBattle=result.TryGetProperty("inBattle",out var inBattle)&&inBattle.GetBoolean();state.Enabled=result.GetProperty("enabled").GetBoolean();state.Ready=result.GetProperty("ready").GetBoolean();state.Generation=result.GetProperty("generation").GetInt32();state.Locks=result.GetProperty("lockCount").GetInt32();
            state.Feedback=!state.Ready?"请先进入游戏或读取存档。":result.GetProperty("lastError").GetString() is {Length:>0} error?error:"已读取当前数据。";
            if(!StillCurrent())return;
            if(!state.Ready){_dataGrid.Rows.Clear();_dataMap.ClearMap();ResetDataDetails();_dataCommonBoard?.ClearRows();return;}
            if(scope is "maps" or "mapEvents")
            {
                if(!state.Enabled){state.Feedback="请先开启修改，再浏览地图和事件。";return;}
                var catalog=await state.Connection.RequestAsync(new(){["op"]="maps",["generation"]=state.Generation});
                if(!StillCurrent())return;
                if(_dataSelectedMapId==0||_dataMapFollowCurrent)_dataSelectedMapId=catalog.GetProperty("currentMapId").GetInt32();
                _dataMapChoosing=true;
                try
                {
                    var choices=catalog.GetProperty("maps").EnumerateArray().Select(m=>new MapChoice(m.GetProperty("id").GetInt32(),m.GetProperty("name").GetString()??"",DataLabel(m.GetProperty("name").GetString()??"",m.TryGetProperty("chinese",out var ch)?ch.GetString()??"":""),m.GetProperty("current").GetBoolean())).ToArray();
                    if(!_dataMapChoice.Items.Cast<MapChoice>().SequenceEqual(choices)){_dataMapChoice.Items.Clear();_dataMapChoice.Items.AddRange(choices);}
                    _dataMapChoice.SelectedItem=_dataMapChoice.Items.Cast<MapChoice>().FirstOrDefault(m=>m.Id==_dataSelectedMapId);
                }
                finally{_dataMapChoosing=false;}
            }
            if(scope=="maps")
            {
                var update=await state.Connection.RequestAsync(new(){["op"]="mapLive",["generation"]=state.Generation,["mapId"]=_dataMapFollowCurrent?0:_dataSelectedMapId});
                if(!StillCurrent())return;
                if(update.GetProperty("pending").GetBoolean()){state.Feedback="正在切换地图，完成后自动同步。";return;}
                var map=update.GetProperty("map");_dataMapSyncRevision=update.GetProperty("revision").GetInt32();_dataSelectedMapId=map.GetProperty("id").GetInt32();UpdateMapChoices(update.GetProperty("currentMapId").GetInt32());
                _dataMapGeneration=state.Generation;_dataMap.SetMap(map);_dataMapPanel!.Visible=true;_dataGrid.Visible=false;
                _dataPage.Text=$"地图 {map.GetProperty("id").GetInt32():0000} · {map.GetProperty("width")} × {map.GetProperty("height")} 格";
                await ShowSelectedMapTileAsync();return;
            }
            _dataMapPanel!.Visible=false;_dataGrid.Visible=true;
            string? selectedKey=_dataSelected?.GetProperty("key").GetString();
            int first=Math.Max(0,_dataGrid.FirstDisplayedScrollingRowIndex);
            if(selectedKey is null&&_dataViewStates.TryGetValue(ViewStateKey(_dataScope.SelectedIndex),out var saved)){selectedKey=saved.SelectedKey;first=saved.FirstRow;}
            _dataSelecting=true;
            var incoming=result.GetProperty("rows").EnumerateArray().ToArray();
            if(scope=="common")_dataCommonBoard?.SetRows(incoming);
            while(_dataGrid.Rows.Count>incoming.Length)_dataGrid.Rows.RemoveAt(_dataGrid.Rows.Count-1);
            while(_dataGrid.Rows.Count<incoming.Length)_dataGrid.Rows.Add();
            int rowIndex=0;
            foreach(var row in incoming)
            {
                string key=row.GetProperty("key").GetString()!;var target=_dataGrid.Rows[rowIndex++];
                string chinese=DataChinese(row);
                string type=row.GetProperty("type").GetString()??"";
                object?[] values=[row.GetProperty("name").GetString(),chinese.Length>0?chinese:"—",type=="actor"?"等级 "+row.GetProperty("value"):type=="event"?"查看详情":DataValueText(row.GetProperty("value")),row.GetProperty("locked").GetBoolean()?"已锁定":"—"];
                for(int i=0;i<values.Length;i++)if(!Equals(target.Cells[i].Value,values[i]))target.Cells[i].Value=values[i];
                target.Tag=row.Clone();target.Cells[0].ToolTipText=key;
            }
            var selected=_dataGrid.Rows.Cast<NativeRpgGridRow>().FirstOrDefault(r=>r.Tag is JsonElement e&&e.GetProperty("key").GetString()==selectedKey);
            if(selected is null&&_dataGrid.Rows.Count>0)selected=_dataGrid.Rows[0];
            _dataGrid.ClearSelection();if(selected is not null){selected.Selected=true;_dataGrid.CurrentCell=selected.Cells[0];}
            if(_dataGrid.Rows.Count>0)_dataGrid.FirstDisplayedScrollingRowIndex=Math.Min(first,_dataGrid.Rows.Count-1);
            _dataSelecting=false;
            bool retainEvent=selected?.Tag is JsonElement selectedRow&&selectedRow.GetProperty("type").GetString()=="event"
                &&selectedKey==selectedRow.GetProperty("key").GetString()&&_dataObjectGeneration==state.Generation&&_dataEventConditionsRefresh is not null;
            if(retainEvent){_dataSelected=(JsonElement)selected!.Tag!;await _dataEventConditionsRefresh!();}
            else{SelectGameDataRow();await _dataDetailLoad;}
            _dataTotal=result.GetProperty("total").GetInt32();_dataPage.Text=$"{incoming.Length} / {_dataTotal} 项"+(incoming.Length<_dataTotal?" · 向下滚动继续加载":"");
            _dataAppliedSignature=signature;
        }
        catch(Exception ex){state.Feedback=SafeDiagnosticOutput.ExceptionSummary(ex);if(StillCurrent()&&scope=="maps"&&_dataMap.MapId==0){_dataDescription.Text=state.Feedback;_dataPage.Text="地图未能读取";}}
        finally
        {
            if(IsDataView(game.ExePath,epoch))
            {
                _dataLoading=false;_dataSelecting=false;RenderGameModificationState();
                if(_dataRefreshPending||revalidate)
                {
                    bool pending=_dataRefreshPending;_dataRefreshPending=false;
                    BeginInvoke(()=>{if(IsDataView(game.ExePath,epoch)&&ReferenceEquals(state,SelectedDataSession())&&(pending||revision==_dataRevision))_=RefreshGameDataAsync(preferCached:pending);});
                }
            }
        }
    }
    private static string DataValueText(JsonElement value)=>value.ValueKind is JsonValueKind.True or JsonValueKind.False?(value.GetBoolean()?"开启 / ON":"关闭 / OFF"):value.ToString();
    private static string DataDisplayValue(JsonElement value)=>value.ValueKind is JsonValueKind.True or JsonValueKind.False?UiLanguage.Text(DataValueText(value)):DataValueText(value);
    private string DataChinese(JsonElement item)
    {
        var alias=_dataAliases.GetValueOrDefault(item.GetProperty("key").GetString()!,"");
        return alias.Length>0?alias:DataLabel(item.GetProperty("name").GetString()??"",item.TryGetProperty("chinese",out var translated)?translated.GetString()??"":"");
    }
    private void ResetDataDetails()
    {
        _dataDetailRevision++;_dataSelected=null;_dataSelectedIcon.Visible=false;
        _dataSelectedName.Text=UiLanguage.Text("选择一项数据");_dataOriginalName.Text="";_dataKeyLabel.Text="";_dataCurrentLabel.Text="";_dataDescription.Text=UiLanguage.Text("在左侧选择项目，查看详情并修改。");
        if(_dataScalarEditor is not null)_dataScalarEditor.Visible=false;
        ClearDataObjectEditor();
    }
    private void ClearDataObjectEditor()
    {
        _dataEventConditionsRefresh=null;
        _dataObjectGeneration=-1;
        if(_dataObjectEditor is null)return;
        foreach(NativeRpgElement c in _dataObjectEditor.Controls.Cast<NativeRpgElement>().ToArray())c.Dispose();
        _dataObjectEditor.Controls.Clear();
    }
    private void SelectGameDataRow()
    {
        if(_dataSelecting)return;_dataDetailLoad=Task.CompletedTask;
        var row=_dataGrid.SelectedRows.Count==1&&_dataGrid.SelectedRows[0].Tag is JsonElement value?(JsonElement?)value:null;
        if(row is null){ResetDataDetails();return;}
        string type=row.Value.GetProperty("type").GetString()??"";
        if(type=="event"&&_dataSelected?.GetProperty("key").GetString()==row.Value.GetProperty("key").GetString()
            &&_dataObjectGeneration==SelectedDataSession()?.Generation&&_dataEventConditionsRefresh is not null)return;
        if(type is "actor" or "event"){_dataSelected=row;_dataDetailLoad=ShowGameDataObjectAsync(row.Value);return;}
        _dataDetailRevision++;ClearDataObjectEditor();ShowGameDataScalar(row);
    }
    private void ShowGameDataScalar(JsonElement? item)
    {
        if(item is not { } row){ResetDataDetails();return;}_dataSelected=row;
        _dataScalarEditor!.Visible=true;
        _dataSelectedIcon.Atlas=_dataGrid.IconAtlas;_dataSelectedIcon.Index=row.TryGetProperty("icon",out var icon)?icon.GetInt32():-1;_dataSelectedIcon.Visible=_dataSelectedIcon.Atlas is not null&&_dataSelectedIcon.Index>=0;_dataSelectedIcon.Invalidate();
        var alias=DataChinese(row);_dataSelectedName.Text=alias.Length>0?alias:row.GetProperty("name").GetString()??"";
        _dataOriginalName.Text=alias.Length>0?(row.GetProperty("name").GetString()??""):"";
        _dataKeyLabel.Text=ReadableDataKey(row.GetProperty("key").GetString()!);_dataCurrentLabel.Text=UiLanguage.Format($"当前值  {DataDisplayValue(row.GetProperty("value"))}");
        _dataDescription.Text=DataDescription(row);
        _dataLockButton!.Text=row.GetProperty("locked").GetBoolean()?"解除锁定":"锁定";_dataLockButton.Enabled=row.GetProperty("canLock").GetBoolean();
        string type=row.GetProperty("type").GetString()??"";
        _dataBoolean.Visible=type=="boolean";_dataValue.Visible=type=="number";_dataTextValue.Visible=type=="string";_dataSteps!.Visible=type=="number";
        if(_dataNumberField is not null)_dataNumberField.Visible=type=="number";
        _dataApplyButton!.Visible=type!="boolean";
        if(type=="boolean"){_dataScalarUpdating=true;try{_dataBoolean.Checked=row.GetProperty("value").GetBoolean();}finally{_dataScalarUpdating=false;}}
        else if(type=="string")_dataTextValue.Text=row.GetProperty("value").GetString()??"";
        else if(type=="number")
        {
            _dataValue.Minimum=decimal.MinValue;_dataValue.Maximum=decimal.MaxValue;_dataValue.DecimalPlaces=row.GetProperty("key").GetString()!.StartsWith("field:")?3:0;
            _dataValue.Minimum=row.TryGetProperty("min",out var min)?min.GetDecimal():int.MinValue;_dataValue.Maximum=row.TryGetProperty("max",out var max)?max.GetDecimal():int.MaxValue;
            _dataValue.Value=Math.Clamp(row.GetProperty("value").GetDecimal(),_dataValue.Minimum,_dataValue.Maximum);
        }
        _dataChinese.Text=_dataAliases.GetValueOrDefault(row.GetProperty("key").GetString()!,"");
    }
    private async Task ChangeGameDataAsync(string action)
    {
        if(SelectedDataSession() is not {Enabled:true,Ready:true}||_dataSelected is not { } row)return;
        if(action=="lock"&&!row.GetProperty("canLock").GetBoolean())return;
        string type=row.GetProperty("type").GetString()??"";
        JsonNode value=type=="boolean"?JsonValue.Create(_dataBoolean.Checked)!:type=="string"?JsonValue.Create(_dataTextValue.Text)!:JsonValue.Create(_dataValue.Value)!;
        await ExecuteGameDataAsync(new(){["op"]=action,["key"]=row.GetProperty("key").GetString(),["value"]=value});
    }
    private async Task ExecuteGameDataAsync(JsonObject request)
    {
        if(owner._selectedGame is not { } game||SelectedDataSession() is not { } state)return;
        int epoch=_dataEpoch,revision=_dataRevision;string feedback="";
        if(request["op"]?.ToString()=="disable"){state.Enabled=false;await owner.RevokeModificationSpeedAsync(game);}
        await _dataWriteGate.WaitAsync();
        try
        {
            while(_dataLoading&&IsDataView(game.ExePath,epoch))await Task.Delay(20);
            if(!IsDataView(game.ExePath,epoch)||revision!=_dataRevision||!state.Connection.Connected||!ReferenceEquals(state,SelectedDataSession()))return;
            if(request["op"]?.ToString() is not ("enable" or "disable")&&!ReferenceEquals(_dataSnapshotConnection,state.Connection)){state.Feedback="游戏连接已变化，请刷新数据后再修改。";return;}
            if(request["op"]?.ToString() is not ("enable" or "disable")&&!state.Enabled)return;
            int detailScroll=_dataDetailScroll?.ScrollOffset??0;
            if(!request.ContainsKey("generation"))request["generation"]=state.Generation;
            _dataWriting=true;_dataLoading=true;RenderGameModificationState();
            try
            {
                _dataSnapshots.Invalidate(state.Connection);
                var result=await state.Connection.RequestAsync(request);
                if(request["op"]?.ToString() is "enable" or "disable")
                {
                    state.Enabled=request["op"]?.ToString()=="enable"&&result.GetProperty("enabled").GetBoolean();
                    if(!state.Enabled){state.Ready=false;state.Locks=0;state.InBattle=false;}
                }
                feedback=request["op"]?.ToString() switch
                {
                    "teleport"=>result.TryGetProperty("moved",out var moved)&&moved.GetBoolean()?"已移动到目标格子。":"已请求切换地图，请在游戏内确认位置。",
                    "eventCommandSet"=>"指令修改已保存到本次游戏；需要触发事件时点击执行。",
                    "eventRun"=>"已开始执行事件，请查看游戏窗口。",
                    "interpreterGoto"=>"已调整正在运行的事件位置，请查看游戏窗口。",
                    "battleAction"=>"战斗操作已执行。",
                    _=>"操作完成。"
                };
            }
            catch(Exception ex){feedback=SafeDiagnosticOutput.ExceptionSummary(ex);}
            finally{if(IsDataView(game.ExePath,epoch))_dataLoading=false;}
            if(IsDataView(game.ExePath,epoch))
            {
                if(request["op"]?.ToString()=="eventCommandSet")_dataObjectGeneration=-1;
                if(state.Enabled)await RefreshGameDataAsync();else RenderGameModificationState();
                if(revision==_dataRevision&&_dataDetailScroll is not null)_dataDetailScroll.ScrollOffset=detailScroll;
                state.Feedback=feedback;
            }
            else state.Feedback=feedback;
        }
        finally{_dataWriting=false;_dataWriteGate.Release();if(!IsDisposed)RenderGameModificationState();}
    }

    private void SaveGameDataAlias()
    {
        if(owner._selectedGame is not { } game||_dataSelected is not { } row)return;
        if(!_dataAliasWritable){_dataFeedback.Text="译名文件无法读取，未覆盖原文件。";return;}
        var next=new Dictionary<string,string>(_dataAliases,StringComparer.Ordinal);next[row.GetProperty("key").GetString()!]=_dataChinese.Text.Trim();
        try{PortableDataStorage.WriteJson(Path.Combine(RpgMakerDataAdapter.Storage(game.ExePath),"data-labels.json"),next);_dataAliases=next;_=RefreshGameDataAsync();_dataFeedback.Text="译名已保存在软件的数据目录。";}
        catch(Exception ex){_dataFeedback.Text=SafeDiagnosticOutput.ExceptionSummary(ex);}
    }
    private Task ReadGameDataMapAsync()=>RefreshGameDataAsync();
    private async Task TeleportGameDataAsync()
    {
        if(_dataMap.SelectedTile is not { } tile){_dataFeedback.Text="请先选择地图格子。";return;}
        await ExecuteGameDataAsync(new(){["op"]="teleport",["generation"]=_dataMapGeneration,["mapId"]=_dataMap.MapId,["x"]=tile.X,["y"]=tile.Y});
    }

    }
}
