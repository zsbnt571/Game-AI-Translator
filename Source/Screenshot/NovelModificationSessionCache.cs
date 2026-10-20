using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private readonly ModificationSnapshotCache _novelSnapshotsCache = new();
    private readonly ConditionalWeakTable<GameDataSession, NovelReturnView> _novelReturnViews = new();
    private GameDataSession? _novelViewSession;
    private IGameDataConnection? _novelSnapshotConnection;
    private int _novelOperation;
    private bool _novelRefreshPending;
    private string _novelRequestKey = "", _novelRowsKey = "";
    private readonly ConditionalWeakTable<JsonElement[], NovelRowsProjection> _novelProjections = new();
    private readonly Dictionary<JsonElement, NovelRowsProjection> _novelPreparedSnapshots = new();
    private int _novelPreparedBytes;
    private long _novelProjectionSequence;
    private const int NovelPreparedByteLimit=64*1024*1024;
    private sealed class NovelRowsProjection(JsonElement[] raw,string? adapter,Dictionary<string,string>? names,int estimatedBytes=0)
    {
        internal readonly JsonElement[] Raw=raw;
        internal readonly string? Adapter=adapter;
        internal readonly Dictionary<string,string>? Names=names;
        internal readonly int EstimatedBytes=estimatedBytes;
        internal long LastUsed;
        internal readonly NovelVariableRow[] Rows=raw.Select(row=>new NovelVariableRow(row,adapter=="yukar-bakin")).ToArray();
        internal Dictionary<string,NovelVariableRow>? ById;
        internal readonly ModificationCommonCandidates.Candidate[] Candidates=raw.Length==0?[]:ModificationCommonCandidates.Build(raw,adapter,names);
        internal readonly bool Switches=raw.Any(row=>row.GetProperty("type").GetString()=="boolean");
        internal readonly HashSet<string> Inventory=raw.Select(row=>row.TryGetProperty("inventoryScope",out var kind)?kind.GetString()??"":"").Where(value=>value.Length>0).ToHashSet(StringComparer.Ordinal);
    }
    private NovelRowsProjection NovelProjection()
    {
        string? adapter=_selectedGame?.AdapterId;
        var names=_selectedGame is {} game?_wolfNumericNames.GetValueOrDefault(game.ExePath):null;
        if(_novelProjections.TryGetValue(_novelRows,out var projection)&&projection.Adapter==adapter&&ReferenceEquals(projection.Names,names))return projection;
        projection=new(_novelRows,adapter,names);
        _novelProjections.Remove(_novelRows);_novelProjections.Add(_novelRows,projection);
        return projection;
    }
    private async Task<NovelRowsProjection> PrepareNovelSnapshotAsync(GameInfo game,JsonElement result)
    {
        var names=_wolfNumericNames.GetValueOrDefault(game.ExePath);
        if(_novelPreparedSnapshots.TryGetValue(result,out var saved)&&saved.Adapter==game.AdapterId&&ReferenceEquals(saved.Names,names))
        {saved.LastUsed=++_novelProjectionSequence;return saved;}
        // The cache owns an immutable JSON document. Its child elements can be
        // retained directly; cloning every cell on each category visit both
        // discarded row identity and repeated the complete schema scan.
        NovelRowsProjection Build()=>new(result.GetProperty("rows").EnumerateArray().ToArray(),game.AdapterId,names,
            checked(result.GetRawText().Length*2+result.GetProperty("rows").GetArrayLength()*160));
        var prepared=result.GetProperty("rows").GetArrayLength()>256?await Task.Run(Build):Build();
        if(prepared.EstimatedBytes<=NovelPreparedByteLimit)
        {
            if(_novelPreparedSnapshots.Remove(result,out var previous))_novelPreparedBytes-=previous.EstimatedBytes;
            while(_novelPreparedSnapshots.Count>=12||_novelPreparedBytes+prepared.EstimatedBytes>NovelPreparedByteLimit)
            {
                // Dictionary slots are reused after removal; enumeration
                // order is not insertion age. Evict the least recently used
                // model instead of repeatedly evicting the newest large one.
                var oldest=_novelPreparedSnapshots.MinBy(pair=>pair.Value.LastUsed);_novelPreparedBytes-=oldest.Value.EstimatedBytes;
                _novelPreparedSnapshots.Remove(oldest.Key);
            }
            prepared.LastUsed=++_novelProjectionSequence;
            _novelPreparedSnapshots[result]=prepared;_novelPreparedBytes+=prepared.EstimatedBytes;
        }
        _novelProjections.Remove(prepared.Raw);_novelProjections.Add(prepared.Raw,prepared);
        return prepared;
    }
    private sealed class NovelReturnView
    {
        internal string Path="",Scope="common",Search="",Value="",Feedback="",RowsKey="";
        internal int Map=-1,Generation;
        internal JsonElement[] Raw=[];
        internal NovelVariableRow[] Rows=[],CommonRows=[];
        internal ModificationCommonCandidates.Candidate[] Candidates=[];
        internal NovelVariableRow? Selected;
        internal HashSet<string> Inventory=[], CandidateScopes=[];
        internal HashSet<string>? EventVariables;
        internal bool Switches,Database;
        internal readonly Dictionary<string,string> Selections=new(StringComparer.Ordinal);
        internal Dictionary<string,string> Searches=new(StringComparer.Ordinal);
    }
    private void RememberNovelSelection()
    {
        if(_novelViewSession is null||_novelRowsKey.Length==0)return;
        if(NativeNovel.Selected?.Source.GetProperty("id").GetString() is {} key)
            _novelReturnViews.GetValue(_novelViewSession,_=>new()).Selections[NovelSelectionKey()]=key;
    }
    private string NovelSelectionKey()=>NativeNovel.Scope+"\0"+(NativeNovel.Scope=="common"?"":NativeNovel.Search)+"\0"+_wolfMapId;
    private void EnsureNovelSessionView(GameInfo? game)
    {
        var session=NovelSession();var state=NativeNovel;
        if(SameGame(_novelPath,game?.ExePath)&&ReferenceEquals(_novelViewSession,session))return;
        RememberNovelSelection();
        if(_novelViewSession is {} old&&_novelRowsKey.Length>0&&ReferenceEquals(_novelSnapshotConnection,old.Connection))
        {
            var saved=_novelReturnViews.GetValue(old,_=>new());
            saved.Path=_novelPath??"";saved.Scope=state.Scope;saved.Search=state.Search;saved.Value=state.Value;saved.Feedback=state.Feedback;
            saved.Searches=new(state.Searches,StringComparer.Ordinal);
            saved.Raw=_novelRows;saved.Rows=state.Rows;saved.CommonRows=state.CommonRows;saved.Candidates=state.CommonCandidates;saved.Selected=state.Selected;
            saved.Map=_wolfMapId;saved.Generation=_novelGeneration;saved.RowsKey=_novelRowsKey;saved.EventVariables=_wolfEventVariables;
            saved.Inventory=new(state.InventoryScopes);saved.CandidateScopes=new(state.CandidateScopes);saved.Switches=state.SwitchesAvailable;saved.Database=state.DatabaseAvailable;
        }
        _novelViewSession=session;_novelSnapshotConnection=null;_novelPath=game?.ExePath;
        state.NameLookup=null;state.NamesRevision++;
        _novelOperation++;_novelBusy=false;_novelRefreshPending=false;_novelRequestKey=_novelRowsKey="";
        _wolfEventVariables=null;_wolfMapId=-1;_novelRows=[];_novelGeneration=0;
        state.Rows=[];state.CommonRows=[];state.CommonCandidates=[];state.InventoryScopes=[];state.CandidateScopes=[];
        state.SwitchesAvailable=state.DatabaseAvailable=false;state.Scope="common";state.Search="";state.Selected=null;state.Value="";state.Feedback="";
        state.Searches.Clear();
        state.Snapshots=[];state.Snapshot=null;state.SnapshotFeedback="刷新列表后可查看此游戏的快照。";
        if(session?.Connection.Connected==true&&session.Enabled&&_novelReturnViews.TryGetValue(session,out var restore)&&SameGame(restore.Path,game?.ExePath)&&_novelSnapshotsCache.IsGenerationCurrent(session.Connection,restore.Generation))
        {
            _novelSnapshotConnection=session.Connection;_novelRows=restore.Raw;_novelGeneration=restore.Generation;_wolfMapId=restore.Map;_wolfEventVariables=restore.EventVariables;_novelRowsKey=restore.RowsKey;
            state.Scope=restore.Scope;state.Search=restore.Search;state.Value=restore.Value;state.Feedback=restore.Feedback;
            foreach(var pair in restore.Searches)state.Searches[pair.Key]=pair.Value;
            state.Rows=restore.Rows;state.CommonRows=restore.CommonRows;state.CommonCandidates=restore.Candidates;state.Selected=restore.Selected;
            state.InventoryScopes=new(restore.Inventory);state.CandidateScopes=new(restore.CandidateScopes);state.SwitchesAvailable=restore.Switches;state.DatabaseAvailable=restore.Database;
        }
    }
    private JsonObject NovelSnapshotRequest(GameInfo game)
    {
        var request=new JsonObject{["op"]="dataSnapshot"};
        if(game.AdapterId=="wolf")
        {
            request["map"]=_wolfMapId;
            request["scope"]=NativeNovel.Scope is "common" or "characters" or "relationships" or "switches"?"all":NativeNovel.Scope;
            request["query"]=NativeNovel.Scope=="common"?"":NativeNovel.Search;
        }
        return request;
    }
    private async Task<JsonElement> AnnotateNovelSnapshotAsync(GameInfo game,JsonElement result)
    {
        Dictionary<string,string>? names=null;
        if(game.AdapterId=="wolf"&&!_wolfNumericNames.TryGetValue(game.ExePath,out names))
        {
            try{names=await Task.Run(()=>WolfModificationNames.Read(game.ExePath));}
            catch(Exception error)when(error is IOException or InvalidDataException or NotSupportedException or InvalidOperationException){names=[];}
            // Switching away and back can leave an older read in flight.
            // Publish one immutable naming context per game; a later identical
            // read must not invalidate already-prepared projections by identity.
            if(_wolfNumericNames.TryGetValue(game.ExePath,out var published))names=published;
            else _wolfNumericNames[game.ExePath]=names;
        }
        var annotated=await Task.Run(()=>AnnotateNovelRows(result,names));
        // Freshness begins when the complete snapshot is ready for display.
        // A large first projection can outlive the cache's freshness interval;
        // stamping before this work would immediately re-read it on return.
        if(WpfWorkspace.Enabled)await PrepareNovelSnapshotAsync(game,annotated);
        return annotated;
    }
}
