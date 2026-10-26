using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

// Shared single/batch write contract. Every expected value comes from a live
// snapshot; a later write is never made against an optimistic local mutation.
internal static class NovelModificationWrites
{
    internal sealed record Result(int Completed, int Total, Exception? Error, bool Refreshed=false);

    internal static JsonNode Value(JsonElement row, string text)
    {
        string type=row.GetProperty("type").GetString()??"";
        if(type=="string")return JsonValue.Create(text)!;
        if(type=="boolean"&&row.GetProperty("value").ValueKind is JsonValueKind.True or JsonValueKind.False)
            return bool.TryParse(text,out bool flag)?JsonValue.Create(flag)!:throw new IOException("开关请填写 true 或 false。");
        if(type=="number"&&row.GetProperty("value").ValueKind==JsonValueKind.Number)
        {
            try
            {
                // Parse once as JSON to retain integer/decimal precision. The
                // finite check is validation only, not a conversion of payload.
                var value=JsonNode.Parse(text.Trim());
                if(value is JsonValue scalar&&scalar.GetValueKind()==JsonValueKind.Number
                    &&double.TryParse(text,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out double number)&&double.IsFinite(number))return value;
            }
            catch(JsonException) { }
            throw new IOException("请填写有效数字。");
        }
        throw new IOException("此数据类型不可修改。");
    }

    internal static async Task<Result> Execute(string operation,IReadOnlyList<JsonElement> selected,string text,int generation,
        Func<bool> current,Func<string,JsonElement?> find,Func<JsonObject,Task<JsonElement>> send,Func<Task> reread)
    {
        int completed=0;
        bool sent=false,refreshAttempted=false,refreshed=false;
        try
        {
            if(operation is not ("dataSet" or "dataLock" or "dataUnlock"))throw new IOException("此操作不支持批量修改。");
            if(selected.Count==0)throw new IOException("请先选择一项变量。");
            var ids=selected.Select(row=>row.GetProperty("id").GetString()??"").ToArray();
            if(ids.Any(string.IsNullOrEmpty)||ids.Distinct(StringComparer.Ordinal).Count()!=ids.Length)throw new IOException("选择中包含重复或无效的变量。");
            if(operation!="dataUnlock"&&selected.Select(row=>row.GetProperty("type").GetString()).Distinct().Count()!=1)throw new IOException("请选择相同类型的变量。");
            // Validate the complete selection before making its first write.
            var values=selected.Select(row=>operation=="dataUnlock"?null:Value(row,text)).ToArray();
            for(int i=0;i<selected.Count;i++)
            {
                if(!current())throw new IOException("游戏连接或当前分类已变化，已停止修改。");
                var row=find(ids[i])??throw new IOException("所选变量已不在当前列表，请重新选择。");
                if(row.GetProperty("type").GetString()!=selected[i].GetProperty("type").GetString()
                    ||row.GetProperty("value").GetRawText()!=selected[i].GetProperty("value").GetRawText())
                    throw new IOException("数值已变化，请刷新后重试。");
                if(row.TryGetProperty("readOnly",out var readOnly)&&readOnly.ValueKind==JsonValueKind.True
                    ||row.TryGetProperty("canWrite",out var canWrite)&&canWrite.ValueKind==JsonValueKind.False)
                    throw new IOException("此变量只读。");
                if(operation is "dataLock" or "dataUnlock"&&row.TryGetProperty("canLock",out var canLock)&&canLock.ValueKind==JsonValueKind.False)
                    throw new IOException("此变量不支持锁定。");
                var request=new JsonObject{["op"]=operation,["generation"]=generation,["entry"]=ids[i],
                    ["expected"]=JsonNode.Parse(row.GetProperty("value").GetRawText())};
                if(values[i] is {} value)request["value"]=value;
                sent=true;var response=await send(request);
                if(response.TryGetProperty("ok",out var ok)&&ok.ValueKind==JsonValueKind.False)
                    throw new IOException("游戏未确认修改成功。");
                completed++;
            }
            // Every write carries the original expected value; the bridge
            // atomically compares it. Read once after the batch, not once per
            // item (which would multiply a full database scan by selection size).
            refreshAttempted=true;await reread();refreshed=true;
            return new(completed,selected.Count,null,true);
        }
        catch(Exception error)
        {
            if(sent&&!refreshAttempted)
                try{await reread();refreshed=true;}
                catch(Exception readError){return new(completed,selected.Count,new IOException(error.Message+" "+readError.Message,readError));}
            return new(completed,selected.Count,error,refreshed);
        }
    }
}

public sealed partial class MainForm
{
    internal async Task NativeNovelBatch(string operation,IReadOnlyList<NovelVariableRow> selected,string value)
    {
        if(_novelBusy||_selectedGame is not {} game||!NovelModification(game))return;
        EnsureNovelSessionView(game);
        var session=NovelSession();
        if(session?.Connection.Connected!=true||!session.Enabled||!ReferenceEquals(_novelSnapshotConnection,session.Connection)||!NovelRowsReady)
        {NovelDataFeedback="游戏连接已变化，请刷新数据后再修改。";return;}
        // UI objects from an older projection are not authority to write a new
        // process or snapshot, even when their IDs happen to match.
        var currentRows=NativeNovel.Rows.ToHashSet();
        if(selected.Count==0||selected.Any(row=>!currentRows.Contains(row)))
        {NovelDataFeedback="所选变量已不在当前列表，请重新选择。";return;}
        var snapshotRequest=NovelSnapshotRequest(game);string requestKey=snapshotRequest.ToJsonString(),scope=NativeNovel.Scope;
        string search=NativeNovel.Search;int map=_wolfMapId;
        var eventSelection=_wolfEventVariables;var categoryIndex=_wolfCategoryEntries.GetValueOrDefault(game.ExePath);
        int generation=_novelGeneration,operationId=++_novelOperation;
        var byId=NativeNovel.Rows.ToDictionary(row=>row.Source.GetProperty("id").GetString()!,row=>row.Source,StringComparer.Ordinal);
        _novelBusy=true;_novelRequestKey=scope+"|"+requestKey;RenderNovelModificationState();
        bool Current()=>!IsDisposed&&!Disposing&&operationId==_novelOperation&&SameGame(_selectedGame?.ExePath,game.ExePath)
            &&ReferenceEquals(session,NovelSession())&&ReferenceEquals(session.Connection,_novelSnapshotConnection)
            &&session.Connection.Connected&&session.Enabled&&NativeNovel.Scope==scope&&generation==_novelGeneration
            &&NativeNovel.Search==search&&_wolfMapId==map&&ReferenceEquals(_wolfEventVariables,eventSelection)
            &&ReferenceEquals(_wolfCategoryEntries.GetValueOrDefault(game.ExePath),categoryIndex);
        try
        {
            var result=await NovelModificationWrites.Execute(operation,selected.Select(row=>row.Source).ToArray(),value,generation,Current,
                id=>byId.TryGetValue(id,out var row)?row:null,
                async request=>{_novelSnapshotsCache.Invalidate(session.Connection);return await session.Connection.RequestAsync(request);},
                async ()=>
                {
                    var fresh=await _novelSnapshotsCache.ReadAsync(game.ExePath,session.Connection,snapshotRequest,item=>AnnotateNovelSnapshotAsync(game,item));
                    if(!Current())throw new IOException("游戏连接或当前分类已变化，已停止修改。");
                    int freshGeneration=fresh.GetProperty("generation").GetInt32();
                    var projection=await PrepareNovelSnapshotAsync(game,fresh);
                    if(!Current())throw new IOException("游戏连接或当前分类已变化，已停止修改。");
                    if(!_novelSnapshotsCache.IsLatestGeneration(session.Connection,freshGeneration))
                        throw new IOException("游戏数据已再次变化，请刷新后重试。");
                    // A read is allowed to observe a newer game-data layout.
                    // Keep all session/view guards, publish its generation with
                    // its rows, and never replay the rejected write automatically.
                    generation=_novelGeneration=freshGeneration;
                    _novelRows=projection.Raw;_novelRowsKey=scope+"|"+requestKey;
                    FilterNativeNovelRows();
                    byId=NativeNovel.Rows.ToDictionary(row=>row.Source.GetProperty("id").GetString()!,row=>row.Source,StringComparer.Ordinal);
                });
            if(operationId==_novelOperation&&ReferenceEquals(session,NovelSession()))
            {
                if(result.Error is null)NovelDataFeedback=selected.Count==1?"操作完成，已重新读取当前值。":UiLanguage.Format($"已完成 {result.Completed} 项修改。")+" "+UiLanguage.Text("撤销上次修改仅撤销最近一项。");
                else
                {
                    // A successful write followed by a failed read must not
                    // expose stale expected values as editable current data.
                    if(!result.Refreshed)_novelRowsKey="";
                    NovelDataFeedback=UiLanguage.Format($"已完成 {result.Completed} 项，未完成 {result.Total-result.Completed} 项。")+" "+SafeDiagnosticOutput.ExceptionSummary(result.Error);
                    if(result.Refreshed)NovelDataFeedback+=" "+UiLanguage.Text("已重新读取当前值，请核对后再次应用。");
                }
            }
        }
        finally
        {
            if(operationId==_novelOperation)
            {
                _novelBusy=false;RenderNovelModificationState();
                if(_novelRefreshPending&&NativeNovel.Scope is not ("maps" or "events"))
                {_novelRefreshPending=false;_=NovelCommandAsync("navigate");}
            }
        }
    }
}
