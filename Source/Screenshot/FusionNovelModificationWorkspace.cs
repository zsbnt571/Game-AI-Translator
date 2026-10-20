using System.Text.Json;
using System.Text.Json.Nodes;
namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private Control? _novelModificationPanel;
    private readonly Label _novelDataStatus=WpfWorkspace.Enabled ? null! : WorkspaceLabel(""),_novelDataFeedback=WpfWorkspace.Enabled ? null! : WorkspaceLabel("");
    private readonly TextBox _novelDataSearch=WpfWorkspace.Enabled ? null! : new(){Width=240,PlaceholderText=UiLanguage.Text("搜索变量名称")},_novelDataValue=WpfWorkspace.Enabled ? null! : new(){Width=220,AccessibleName="修改后的值"};
    private readonly DataGridView _novelDataGrid=WpfWorkspace.Enabled ? null! : new WorkspaceDataGrid(){Height=300,Dock=DockStyle.Top,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,MultiSelect=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill};
    private Button? _novelStart,_novelRefresh,_novelApply,_novelLock,_novelUnlock,_novelUndo;
    private bool _novelBusy;private string? _novelPath;private int _novelGeneration;private JsonElement[] _novelRows=[];
    private readonly Dictionary<string,Dictionary<string,string>> _wolfNumericNames=new(StringComparer.OrdinalIgnoreCase);
    private static bool NovelModification(GameInfo? game)=>game?.AdapterId is "web-tyrano" or "web-vnmaker" or "web-choicescript" or "yukar-bakin" or "wolf";
    private GameDataSession? NovelSession()=>_selectedGame is {} g?_embeddedTranslationSessions.GetValueOrDefault(g.ExePath):null;
    private Control BuildNovelModificationWorkspace()
    {
        var panel=WorkspaceColumn();panel.AutoSize=true;panel.Dock=DockStyle.Top;
        _novelStart=IconButton("修改并启动","play",async(_,_)=>await NovelCommandAsync("start"),true,136);
        _novelRefresh=IconButton("刷新数据","refresh",async(_,_)=>await NovelCommandAsync("refresh"),false,120);
        WorkspaceAdd(panel,WorkspaceRow(_novelStart,_novelRefresh,SearchField(_novelDataSearch,"search")));
        WorkspaceAdd(panel,_novelDataStatus);WorkspaceAdd(panel,_novelDataFeedback);
        _novelDataGrid.Columns.Add("name","变量名称");_novelDataGrid.Columns.Add("value","当前值");_novelDataGrid.Columns.Add("locked","锁定");
        _novelDataGrid.Columns[0].FillWeight=55;_novelDataGrid.Columns[1].FillWeight=35;_novelDataGrid.Columns[2].FillWeight=10;
        foreach(DataGridViewColumn column in _novelDataGrid.Columns)column.SortMode=DataGridViewColumnSortMode.NotSortable;
        ((WorkspaceDataGrid)_novelDataGrid).ApplyPalette();WorkspaceAdd(panel,_novelDataGrid);
        _novelApply=IconButton("应用修改","check",async(_,_)=>await NovelCommandAsync("dataSet"),true,116);
        _novelLock=IconButton("锁定此值","lock",async(_,_)=>await NovelCommandAsync("dataLock"),false,116);
        _novelUnlock=IconButton("解除锁定","unlock",async(_,_)=>await NovelCommandAsync("dataUnlock"),false,116);
        _novelUndo=IconButton("撤销上次","back",async(_,_)=>await NovelCommandAsync("dataUndo"),false,116);
        WorkspaceAdd(panel,WorkspaceRow(WorkspaceLabel("修改后的值"),SearchField(_novelDataValue,"edit"),_novelApply,_novelLock,_novelUnlock,_novelUndo));
        WorkspaceAdd(panel,WorkspaceLabel("修改当前运行中的变量。开关填写 true 或 false；进度请按游戏原有方式保存。读档或切换数据后请重新刷新。"));
        BuildNovelStateWorkspace(panel);
        _novelDataSearch.TextChanged+=(_,_)=>FilterNovelRows();
        _novelDataGrid.SelectionChanged+=(_,_)=>{if(_novelDataGrid.CurrentRow?.Tag is JsonElement row)NovelValueInput=NovelValue(row.GetProperty("value"));RenderNovelModificationState();};
        return panel;
    }
    private static string NovelValue(JsonElement value)=>value.ValueKind==JsonValueKind.String?value.GetString()??"":value.GetRawText();
    private void FilterNovelRows()
    {
        if(WpfWorkspace.Enabled){FilterNativeNovelRows();return;}
        string? previous=(_novelDataGrid.CurrentRow?.Tag as JsonElement?)?.GetProperty("name").GetString();
        _novelDataGrid.Rows.Clear();string search=_novelDataSearch.Text.Trim();
        foreach(var row in _novelRows)
        {
            string name=row.GetProperty("name").GetString()??"";if(search.Length>0&&!name.Contains(search,StringComparison.OrdinalIgnoreCase))continue;
            int n=_novelDataGrid.Rows.Add(name,NovelValue(row.GetProperty("value")),row.GetProperty("locked").GetBoolean()?"已锁定":"");_novelDataGrid.Rows[n].Tag=row;
            if(name==previous)_novelDataGrid.CurrentCell=_novelDataGrid.Rows[n].Cells[0];
        }
        if(_novelDataGrid.CurrentRow is null&&_novelDataGrid.Rows.Count>0)_novelDataGrid.CurrentCell=_novelDataGrid.Rows[0].Cells[0];
        if(_novelDataGrid.CurrentRow?.Tag is JsonElement current)NovelValueInput=NovelValue(current.GetProperty("value"));
        RenderNovelModificationState();
    }
    private void RenderNovelModificationState()
    {
        if(WpfWorkspace.Enabled){RenderNativeNovelState();return;}
        if(_novelStart is null)return;
        var game=_selectedGame;
        bool changed=!SameGame(_novelPath,game?.ExePath);
        if(changed){_novelPath=game?.ExePath;_novelRows=[];_novelDataGrid.Rows.Clear();NovelDataFeedback="";}
        var session=NovelSession();bool connected=session?.Connection.Connected==true,active=connected&&session!.Enabled;
        _novelDataStatus.Text=connected?active?"修改已开启 · 翻译可独立开关":"已连接 · 修改关闭":"从本软件启动并连接游戏后，可以读取变量。";
        _novelStart.Text=connected?active?"关闭修改":"开启修改":"修改并启动";
        _novelStart.Enabled=NovelModification(game)&&!_novelBusy&&!_gameActionRunning;
        _novelRefresh!.Enabled=active&&!_novelBusy;_novelUndo!.Enabled=active&&!_novelBusy;
        bool selected=active&&!_novelBusy&&_novelDataGrid.CurrentRow?.Tag is JsonElement;
        _novelApply!.Enabled=selected;_novelLock!.Enabled=selected;_novelUnlock!.Enabled=selected;_novelDataValue.Enabled=selected;
        RenderNovelState(connected,changed);
    }
    private static JsonElement AnnotateNovelRows(JsonElement source,Dictionary<string,string>? names)
    {
        var annotated=JsonNode.Parse(source.GetRawText())!;
        foreach(var row in annotated["rows"]!.AsArray())
        {
            if(row is null)continue;
            if(names is not null&&names.TryGetValue(row["id"]!.GetValue<string>(),out var label))row["name"]=label;
            string name=row["name"]?.GetValue<string>()??"";
            row["commonLabel"]=ModificationCommonCandidates.Label(name);row["inventoryScope"]=ModificationCommonCandidates.InventoryScope(name);
        }
        return JsonSerializer.SerializeToElement(annotated);
    }
    private async Task SelectNativeWolfMapVariables(int? map,int eventId,string[] ids)
    {
        if(_selectedGame?.AdapterId!="wolf")return;
        _wolfMapId=map??-1;NativeNovel.Scope="all";_wolfEventVariables=ids.ToHashSet(StringComparer.Ordinal);
        if(map is {} id)for(int i=0;i<10;i++)_wolfEventVariables.Add($"map:{id}:{eventId}:{i}");
        NativeNovel.Search="";FilterNativeNovelRows();
        NovelDataFeedback="已筛选事件相关变量；开启修改并刷新后可编辑。输入搜索文字可恢复全部变量。";
        if(NovelSession()?.Enabled==true)await NovelCommandAsync("refresh");
    }
    private async Task NovelCommandAsync(string operation)
    {
        if(_selectedGame is not {} game||!NovelModification(game))return;
        if(WpfWorkspace.Enabled)EnsureNovelSessionView(game);
        if((operation is "maps" or "events")&&game.AdapterId=="wolf")
        {
            if(WpfWorkspace.Enabled){RememberNovelSelection();NativeNovel.Scope=operation;_novelRefreshPending=false;RenderNativeNovelState();return;}
            var dialog=new NativeWolfMapDialog(game.ExePath,SelectNativeWolfMapVariables,operation=="events");dialog.Show(this);return;
        }
        var snapshotRequest=NovelSnapshotRequest(game);string requestKey=snapshotRequest.ToJsonString();string signature=NativeNovel.Scope+"|"+requestKey;
        if(_novelBusy){if(operation is "navigate" or "refresh"&&signature!=_novelRequestKey){_novelRefreshPending=true;RenderNovelModificationState();}return;}
        if(operation=="navigate"&&_novelRowsKey==signature&&NovelSession() is {} existing&&ReferenceEquals(_novelSnapshotConnection,existing.Connection)&&_novelSnapshotsCache.TryGet(game.ExePath,existing.Connection,snapshotRequest,out _,out bool recent)&&recent)return;
        var session=NovelSession();var selection=NovelSelectedRow;int operationId=++_novelOperation;
        string viewScope=NativeNovel.Scope;bool revalidate=false;
        _novelRequestKey=signature;
        _novelBusy=true;RenderNovelModificationState();
        bool Current()=>!IsDisposed&&!Disposing&&SameGame(_selectedGame?.ExePath,game.ExePath)&&ReferenceEquals(session,_embeddedTranslationSessions.GetValueOrDefault(game.ExePath))&&operationId==_novelOperation;
        try
        {
            if(operation=="start"&&(session is null||!session.Connection.Connected))
            {await RunGameActionForAsync(game,"modify");session=_embeddedTranslationSessions.GetValueOrDefault(game.ExePath);if(session?.Connection.Connected!=true)return;if(WpfWorkspace.Enabled)EnsureNovelSessionView(game);operationId=++_novelOperation;_novelBusy=true;_novelRequestKey=signature;}
            else if(operation=="start"&&session is not null)
            {bool next=!session.Enabled;_novelSnapshotsCache.Invalidate(session.Connection);await session.Connection.RequestAsync(new(){["op"]=next?"dataEnable":"dataDisable"});session.Enabled=next;if(!next)ResetNovelSpeed(session.Connection);}
            if(session?.Connection.Connected!=true)throw new IOException("游戏未连接。");
            if(!session.Enabled){if(Current()){_novelRows=[];ClearNovelGrid();}return;}
            if(operation.StartsWith("speed:",StringComparison.Ordinal))
            {
                var capability=game.AdapterId=="wolf"?ModificationSpeedState.ForWolf(NovelSpeed(session.Connection),true,false,session.Connection):ModificationSpeedState.Unavailable;
                if(!capability.Supported)throw new IOException(capability.Reason);
                if(!double.TryParse(operation[6..],System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out double rate)||!capability.Accepts(rate))throw new IOException("变速范围为 0.25 到 100 倍，请填写有效数字。");
                if(!Current())return;
                var speed=await session.Connection.RequestAsync(new(){["op"]="dataSpeed",["rate"]=rate});
                double actual=speed.GetProperty("speed").GetDouble();
                if(!capability.Accepts(actual))throw new IOException("游戏未返回有效的当前速度。");
                // A late reply may update its own still-connected session, but
                // never the speed of a replacement process at the same path.
                if(session.Connection.Connected&&ReferenceEquals(session,_embeddedTranslationSessions.GetValueOrDefault(game.ExePath)))
                    _novelSpeeds.GetValue(session.Connection,_=>new()).Value=actual;
                return;
            }
            if(operation.StartsWith("data",StringComparison.Ordinal))
            {
                if(!ReferenceEquals(_novelSnapshotConnection,session.Connection)||!Current())throw new IOException("游戏连接已变化，请刷新数据后再修改。");
                if(WpfWorkspace.Enabled&&operation!="dataUndo"&&!NovelRowsReady)throw new IOException("正在切换分类，请等当前分类的数据读取完成后再修改。");
                var request=new JsonObject{["op"]=operation,["generation"]=_novelGeneration};
                if(operation!="dataUndo")
                {
                    if(selection is not {} selected)throw new IOException("请先选择一项变量。");
                    request["entry"]=selected.GetProperty("id").GetString();request["expected"]=JsonNode.Parse(selected.GetProperty("value").GetRawText());
                    if(operation!="dataUnlock")
                    {
                        string type=selected.GetProperty("type").GetString()!;string value=NovelValueInput;
                        request["value"]=type switch{"string"=>JsonValue.Create(value),"boolean"=>bool.TryParse(value,out bool flag)?JsonValue.Create(flag):throw new IOException("开关请填写 true 或 false。"),"number"=>double.TryParse(value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out double number)&&double.IsFinite(number)?JsonValue.Create(number):throw new IOException("请填写有效数字。"),_=>throw new IOException("此数据类型不可修改。")};
                    }
                }
                _novelSnapshotsCache.Invalidate(session.Connection);
                await session.Connection.RequestAsync(request);
                if(Current())NovelDataFeedback=operation=="dataUndo"?"已撤销上次修改。":"操作完成，已重新读取当前值。";
            }
            JsonElement result;
            if(operation=="navigate"&&_novelSnapshotsCache.TryGet(game.ExePath,session.Connection,snapshotRequest,out result,out bool fresh))revalidate=!fresh;
            else result=await _novelSnapshotsCache.ReadAsync(game.ExePath,session.Connection,snapshotRequest,value=>AnnotateNovelSnapshotAsync(game,value));
            if(!Current()||viewScope!=NativeNovel.Scope||requestKey!=NovelSnapshotRequest(game).ToJsonString())return;
            var projection=await PrepareNovelSnapshotAsync(game,result);
            if(!Current()||viewScope!=NativeNovel.Scope||requestKey!=NovelSnapshotRequest(game).ToJsonString())return;
            RememberNovelSelection();_novelRowsKey=viewScope+"|"+requestKey;_novelSnapshotConnection=session.Connection;
            NativeNovel.DatabaseAvailable=result.TryGetProperty("databaseAvailable",out var supportedDatabase)&&supportedDatabase.GetBoolean();
            _novelGeneration=result.GetProperty("generation").GetInt32();_novelRows=projection.Raw;
            if(_novelReturnViews.GetValue(session,_=>new()).Selections.TryGetValue(NovelSelectionKey(),out var selectedKey))
                NativeNovel.Selected=projection.Rows.FirstOrDefault(row=>row.Source.GetProperty("id").GetString()==selectedKey);
            FilterNovelRows();
            if(operation is "start" or "refresh" or "navigate")NovelDataFeedback=result.GetProperty("truncated").GetBoolean()?"本次列出部分变量。可切换分类，或输入当前数值后刷新以查找更多。":$"已读取 {_novelRows.Length} 项变量。";
            if(game.AdapterId=="wolf"&&NativeNovel.Scope=="db"&&result.TryGetProperty("databaseAvailable",out var database)&&!database.GetBoolean())
                NovelDataFeedback="当前版本的可变数据库尚未识别；仍可查看已读取的普通变量。";
            if(game.AdapterId=="wolf"&&_wolfMapId>=0&&result.TryGetProperty("mapAvailable",out var available)&&!available.GetBoolean())
                NovelDataFeedback="游戏尚未提供所选地图的 Self 变量；可继续查看事件和已读取的普通变量。";
        }
        catch(Exception ex){if(Current())NovelDataFeedback=SafeDiagnosticOutput.ExceptionSummary(ex);}
        finally
        {
            if(operationId==_novelOperation)
            {
                _novelBusy=false;if(!IsDisposed)RenderNovelModificationState();
                if(Current()&&NativeNovel.Scope is not ("maps" or "events")&&(_novelRefreshPending||revalidate))
                {
                    bool pending=_novelRefreshPending;_novelRefreshPending=false;
                    BeginInvoke(()=>{if(Current())_=NovelCommandAsync(pending?"navigate":"refresh");});
                }
            }
        }
    }
}
