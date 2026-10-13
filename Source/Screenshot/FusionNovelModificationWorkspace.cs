using System.Text.Json;
using System.Text.Json.Nodes;
namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private Control? _novelModificationPanel;
    private readonly Label _novelDataStatus=WorkspaceLabel(""),_novelDataFeedback=WorkspaceLabel("");
    private readonly TextBox _novelDataSearch=new(){Width=240,PlaceholderText=UiLanguage.Text("搜索变量名称")},_novelDataValue=new(){Width=220,AccessibleName="修改后的值"};
    private readonly DataGridView _novelDataGrid=new WorkspaceDataGrid(){Height=300,Dock=DockStyle.Top,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,MultiSelect=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill};
    private Button? _novelStart,_novelRefresh,_novelApply,_novelLock,_novelUnlock,_novelUndo;
    private bool _novelBusy;private string? _novelPath;private int _novelGeneration;private JsonElement[] _novelRows=[];
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
        _novelDataGrid.SelectionChanged+=(_,_)=>{if(_novelDataGrid.CurrentRow?.Tag is JsonElement row)_novelDataValue.Text=NovelValue(row.GetProperty("value"));RenderNovelModificationState();};
        return panel;
    }
    private static string NovelValue(JsonElement value)=>value.ValueKind==JsonValueKind.String?value.GetString()??"":value.GetRawText();
    private void FilterNovelRows()
    {
        string? previous=(_novelDataGrid.CurrentRow?.Tag as JsonElement?)?.GetProperty("name").GetString();
        _novelDataGrid.Rows.Clear();string search=_novelDataSearch.Text.Trim();
        foreach(var row in _novelRows)
        {
            string name=row.GetProperty("name").GetString()??"";if(search.Length>0&&!name.Contains(search,StringComparison.OrdinalIgnoreCase))continue;
            int n=_novelDataGrid.Rows.Add(name,NovelValue(row.GetProperty("value")),row.GetProperty("locked").GetBoolean()?"已锁定":"");_novelDataGrid.Rows[n].Tag=row;
            if(name==previous)_novelDataGrid.CurrentCell=_novelDataGrid.Rows[n].Cells[0];
        }
        if(_novelDataGrid.CurrentRow is null&&_novelDataGrid.Rows.Count>0)_novelDataGrid.CurrentCell=_novelDataGrid.Rows[0].Cells[0];
        if(_novelDataGrid.CurrentRow?.Tag is JsonElement current)_novelDataValue.Text=NovelValue(current.GetProperty("value"));
        RenderNovelModificationState();
    }
    private void RenderNovelModificationState()
    {
        if(_novelStart is null)return;
        var game=_selectedGame;
        bool changed=!SameGame(_novelPath,game?.ExePath);
        if(changed){_novelPath=game?.ExePath;_novelRows=[];_novelDataGrid.Rows.Clear();_novelDataFeedback.Text="";}
        var session=NovelSession();bool connected=session?.Connection.Connected==true,active=connected&&session!.Enabled;
        _novelDataStatus.Text=connected?active?"修改已开启 · 翻译可独立开关":"已连接 · 修改关闭":"从本软件启动并连接游戏后，可以读取变量。";
        _novelStart.Text=connected?active?"关闭修改":"开启修改":"修改并启动";
        _novelStart.Enabled=NovelModification(game)&&!_novelBusy&&!_gameActionRunning;
        _novelRefresh!.Enabled=active&&!_novelBusy;_novelUndo!.Enabled=active&&!_novelBusy;
        bool selected=active&&!_novelBusy&&_novelDataGrid.CurrentRow?.Tag is JsonElement;
        _novelApply!.Enabled=selected;_novelLock!.Enabled=selected;_novelUnlock!.Enabled=selected;_novelDataValue.Enabled=selected;
        RenderNovelState(connected,changed);
    }
    private async Task NovelCommandAsync(string operation)
    {
        if(_novelBusy||_selectedGame is not {} game||!NovelModification(game))return;
        var session=NovelSession();var selection=_novelDataGrid.CurrentRow?.Tag is JsonElement r?r:(JsonElement?)null;
        _novelBusy=true;RenderNovelModificationState();
        bool Current()=>!IsDisposed&&!Disposing&&SameGame(_selectedGame?.ExePath,game.ExePath)&&ReferenceEquals(session,_embeddedTranslationSessions.GetValueOrDefault(game.ExePath));
        try
        {
            if(operation=="start"&&(session is null||!session.Connection.Connected))
            {await RunGameActionForAsync(game,"modify");session=_embeddedTranslationSessions.GetValueOrDefault(game.ExePath);if(session?.Connection.Connected!=true)return;}
            else if(operation=="start"&&session is not null)
            {bool next=!session.Enabled;await session.Connection.RequestAsync(new(){["op"]=next?"dataEnable":"dataDisable"});session.Enabled=next;}
            if(session?.Connection.Connected!=true)throw new IOException("游戏未连接。");
            if(!session.Enabled){if(Current()){_novelRows=[];_novelDataGrid.Rows.Clear();}return;}
            if(operation.StartsWith("data",StringComparison.Ordinal))
            {
                var request=new JsonObject{["op"]=operation,["generation"]=_novelGeneration};
                if(operation!="dataUndo")
                {
                    if(selection is not {} selected)throw new IOException("请先选择一项变量。");
                    request["entry"]=selected.GetProperty("id").GetString();request["expected"]=JsonNode.Parse(selected.GetProperty("value").GetRawText());
                    if(operation!="dataUnlock")
                    {
                        string type=selected.GetProperty("type").GetString()!;string value=_novelDataValue.Text;
                        request["value"]=type switch{"string"=>JsonValue.Create(value),"boolean"=>bool.TryParse(value,out bool flag)?JsonValue.Create(flag):throw new IOException("开关请填写 true 或 false。"),"number"=>double.TryParse(value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out double number)&&double.IsFinite(number)?JsonValue.Create(number):throw new IOException("请填写有效数字。"),_=>throw new IOException("此数据类型不可修改。")};
                    }
                }
                await session.Connection.RequestAsync(request);
                if(Current())_novelDataFeedback.Text=operation=="dataUndo"?"已撤销上次修改。":"操作完成，已重新读取当前值。";
            }
            var result=await session.Connection.RequestAsync(new(){["op"]="dataSnapshot"});
            if(!Current())return;
            _novelGeneration=result.GetProperty("generation").GetInt32();_novelRows=result.GetProperty("rows").EnumerateArray().Select(row=>row.Clone()).ToArray();FilterNovelRows();
            if(operation is "start" or "refresh")_novelDataFeedback.Text=result.GetProperty("truncated").GetBoolean()?"本次列出部分变量，较深的数据尚未展开。":$"已读取 {_novelRows.Length} 项变量。";
        }
        catch(Exception ex){if(!IsDisposed&&SameGame(_selectedGame?.ExePath,game.ExePath))_novelDataFeedback.Text=SafeDiagnosticOutput.ExceptionSummary(ex);}
        finally{_novelBusy=false;if(!IsDisposed)RenderNovelModificationState();}
    }
}
