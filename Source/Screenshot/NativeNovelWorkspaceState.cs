using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal sealed class NativeNovelWorkspaceState
{
    internal Func<string,string>? NameLookup;
    internal int NamesRevision;
    internal string Path="",Search="",Scope="common",Value="",Status="",Feedback="",SnapshotFeedback="刷新列表后可查看此游戏的快照。";
    internal readonly Dictionary<string,string> Searches=new(StringComparer.Ordinal);
    internal NovelVariableRow[] Rows=[],CommonRows=[];
    internal ModificationCommonCandidates.Candidate[] CommonCandidates=[];
    internal NovelVariableRow? Selected;
    internal NovelStateRecord[] Snapshots=[];
    internal NovelStateRecord? Snapshot;
    internal readonly DesktopActionState Start=new("修改并启动"),Refresh=new("刷新数据"),Apply=new("应用修改"),Lock=new("锁定此值"),Unlock=new("解除锁定"),Undo=new("撤销上次"),SnapshotRefresh=new("刷新快照"),SnapshotCapture=new("保存快照"),SnapshotLoad=new("载入快照");
    internal bool SnapshotVisible,SnapshotEnabled,ValueEnabled;
    internal bool Wolf,SpeedEnabled,SwitchesAvailable,DatabaseAvailable;
    internal HashSet<string> InventoryScopes=[];
    internal HashSet<string> CandidateScopes=[];
    internal double Speed=1;
    internal ModificationSpeedState SpeedControl=ModificationSpeedState.Unavailable;
}

public sealed partial class MainForm
{
    private HashSet<string>? _wolfEventVariables;
    private int _wolfMapId=-1;
    internal NativeNovelWorkspaceState NativeNovel {get;}=new();
    internal event Action? NativeNovelChanged;
    // Keep the previous list visible during navigation, but never let its
    // selection be written under a different category's heading.
    private bool NovelRowsReady=>_novelRowsKey.Length>0&&(_selectedGame?.AdapterId!="wolf"||_novelRowsKey.StartsWith(NativeNovel.Scope+"|",StringComparison.Ordinal));
    private JsonElement? NovelSelectedRow=>WpfWorkspace.Enabled?NativeNovel.Selected?.Source:_novelDataGrid.CurrentRow?.Tag as JsonElement?;
    private string NovelValueInput {get=>WpfWorkspace.Enabled?NativeNovel.Value:_novelDataValue.Text;set{if(WpfWorkspace.Enabled){NativeNovel.Value=value;NativeNovelChanged?.Invoke();}else _novelDataValue.Text=value;}}
    private string NovelDataFeedback {get=>WpfWorkspace.Enabled?NativeNovel.Feedback:_novelDataFeedback.Text;set{if(WpfWorkspace.Enabled){NativeNovel.Feedback=value;NativeNovelChanged?.Invoke();}else _novelDataFeedback.Text=value;}}
    private string NovelSnapshotFeedback {get=>WpfWorkspace.Enabled?NativeNovel.SnapshotFeedback:_novelSnapshotFeedback.Text;set{if(WpfWorkspace.Enabled){NativeNovel.SnapshotFeedback=value;NativeNovelChanged?.Invoke();}else _novelSnapshotFeedback.Text=value;}}
    private NovelStateRecord? NovelSelectedSnapshot=>WpfWorkspace.Enabled?NativeNovel.Snapshot:_novelSnapshots.SelectedItem as NovelStateRecord;
    private void ClearNovelGrid(){if(WpfWorkspace.Enabled){NativeNovel.Rows=[];NativeNovel.CommonRows=[];NativeNovel.CommonCandidates=[];NativeNovel.Selected=null;NativeNovel.Value="";NativeNovelChanged?.Invoke();}else _novelDataGrid.Rows.Clear();}
    private void SetNovelSnapshots(IEnumerable<NovelStateRecord> records)
    {
        if(WpfWorkspace.Enabled){NativeNovel.Snapshots=records.ToArray();NativeNovel.Snapshot=NativeNovel.Snapshots.FirstOrDefault();NativeNovelChanged?.Invoke();return;}
        _novelSnapshots.Items.Clear();foreach(var record in records)_novelSnapshots.Items.Add(record);if(_novelSnapshots.Items.Count>0)_novelSnapshots.SelectedIndex=0;
    }
    internal void NativeNovelSearch(string text)
    {
        if(NativeNovel.Scope=="common")return;
        if(NativeNovel.Search==text&&_wolfEventVariables is null)return;
        RememberNovelSelection();_wolfEventVariables=null;NativeNovel.Search=text;NativeNovel.Searches[NativeNovel.Scope]=text;FilterNativeNovelRows();
    }
    internal void NativeNovelSelect(NovelVariableRow? row){if(ReferenceEquals(NativeNovel.Selected,row))return;NativeNovel.Selected=row;NativeNovel.Value=row?.Value??"";RenderNativeNovelState();}
    internal void NativeNovelValue(string text)=>NativeNovel.Value=text;
    internal void NativeNovelSnapshot(NovelStateRecord? snapshot){NativeNovel.Snapshot=snapshot;RenderNativeNovelState();}
    internal Task NativeNovelCommand(string command)
    {
        if(command.StartsWith("scope:",StringComparison.Ordinal))
        {
            if(NativeNovel.Scope==command[6..])return Task.CompletedTask;
            RememberNovelSelection();if(NativeNovel.Scope!="common")NativeNovel.Searches[NativeNovel.Scope]=NativeNovel.Search;
            // WOLF categories request different snapshots. Other Novel engines
            // filter the current snapshot locally, so retain its validity and
            // return-state identity until a real read replaces it.
            if(NativeNovel.Wolf)_novelRowsKey="";
            NativeNovel.Scope=command[6..];
            NativeNovel.Search=NativeNovel.Scope=="common"?"":NativeNovel.Searches.GetValueOrDefault(NativeNovel.Scope,"");
            _wolfEventVariables=null;
            // A WOLF category can have a different snapshot. Do not rebuild
            // the previous category (and translate its entire list) before
            // immediately replacing it with the requested snapshot.
            if(NativeNovel.Wolf&&NovelSession()?.Enabled==true)return NovelCommandAsync("navigate");
            FilterNativeNovelRows();return Task.CompletedTask;
        }
        if(command=="refresh"){EnsureNativeNovelNames();NovelSession()?.Labels?.RetryMissing();}
        return NovelCommandAsync(command);
    }
    internal Task NativeNovelSnapshotCommand(string command)=>NovelSnapshotAsync(command);
    private void FilterNativeNovelRows(bool preserveDraft=false)
    {
        string? previous=NativeNovel.Selected?.Source.GetProperty("id").GetString();
        string draft=NativeNovel.Value;
        string query=NativeNovel.Scope=="common"?"":NativeNovel.Search.Trim();
        var projection=NovelProjection();var candidates=projection.Candidates;
        if(query.Length>0&&(NativeNovel.Scope is "characters" or "relationships"))
            projection.ById??=projection.Rows.GroupBy(row=>row.Source.GetProperty("id").GetString()??"").ToDictionary(group=>group.Key,group=>group.First(),StringComparer.Ordinal);
        NativeNovel.CandidateScopes.UnionWith(candidates.Where(item=>item.SectionKey is "characters" or "relationships").Select(item=>item.SectionKey));
        NativeNovel.CommonCandidates=candidates.Where(item=>NativeNovel.Scope switch{
                "characters" or "relationships"=>item.SectionKey==NativeNovel.Scope,
                "common"=>item.SectionKey is "" or "currency" or "recollection",_=>false})
            .Where(item=>{if(query.Length==0)return true;var row=projection.ById![item.Source.GetProperty("id").GetString()!];return row.RawName.Contains(query,StringComparison.OrdinalIgnoreCase)||NativeNovel.NameLookup?.Invoke(row.RawName).Contains(query,StringComparison.OrdinalIgnoreCase)==true||row.Value==query||row.Source.GetProperty("id").GetString()!.Contains(query,StringComparison.OrdinalIgnoreCase);}).ToArray();
        var commonIds=NativeNovel.CommonCandidates.Select(item=>item.Source.GetProperty("id").GetString()).ToHashSet();
        NativeNovel.CommonRows=projection.Rows.Where(row=>commonIds.Contains(row.Source.GetProperty("id").GetString())).ToArray();
        NativeNovel.SwitchesAvailable|=projection.Switches;
        NativeNovel.InventoryScopes.UnionWith(projection.Inventory);
        NativeNovel.Rows=projection.Rows.Where(row=>_wolfEventVariables is null||_wolfEventVariables.Contains(row.Source.GetProperty("id").GetString()??""))
            .Where(row=>NativeNovel.Scope switch{
                "common" or "characters" or "relationships"=>commonIds.Contains(row.Source.GetProperty("id").GetString()),
                "items" or "weapons" or "armors"=>row.Source.TryGetProperty("inventoryScope",out var kind)&&kind.GetString()==NativeNovel.Scope,
                "switches"=>row.Source.GetProperty("type").GetString()=="boolean",
                "db"=>row.Source.GetProperty("id").GetString()!.StartsWith("db:",StringComparison.Ordinal),
                "variables"=>!row.Source.GetProperty("id").GetString()!.Contains("db:")&&!row.Source.GetProperty("id").GetString()!.Contains("map:"),
                "locked"=>row.Source.GetProperty("locked").GetBoolean(),_=>true})
            // Cached row objects may have last been painted in a different
            // target language. Search the active lookup, not that old display.
            .Where(row=>query.Length==0||row.RawName.Contains(query,StringComparison.OrdinalIgnoreCase)||NativeNovel.NameLookup?.Invoke(row.RawName).Contains(query,StringComparison.OrdinalIgnoreCase)==true||row.Value==query||row.Source.GetProperty("id").GetString()!.Contains(query,StringComparison.OrdinalIgnoreCase)).ToArray();
        NativeNovel.Selected=NativeNovel.Rows.FirstOrDefault(row=>row.Source.GetProperty("id").GetString()==previous)??NativeNovel.Rows.FirstOrDefault();
        NativeNovel.Value=preserveDraft&&previous is not null&&NativeNovel.Selected?.Source.GetProperty("id").GetString()==previous?draft:NativeNovel.Selected?.Value??"";
        RenderNativeNovelState();QueueNativeNovelNames();
    }
    private void RenderNativeNovelState()
    {
        var game=_selectedGame;EnsureNovelSessionView(game);EnsureNativeNovelNames();var state=NativeNovel;
        state.Path=game?.ExePath??"";
        var session=NovelSession();bool connected=session?.Connection.Connected==true,active=connected&&session!.Enabled;
        state.Status=connected?active?"修改已开启 · 翻译可独立开关":"已连接 · 修改关闭":"从本软件启动并连接游戏后，可以读取变量。";
        state.Start.Text=connected?active?"关闭修改":"开启修改":"修改并启动";state.Start.Enabled=NovelModification(game)&&!_novelBusy&&!_gameActionRunning;
        state.Refresh.Enabled=state.Undo.Enabled=active&&!_novelBusy;
        state.Wolf=game?.AdapterId=="wolf";
        state.Speed=active?NovelSpeed(session?.Connection):1;
        state.SpeedControl=state.Wolf?ModificationSpeedState.ForWolf(state.Speed,active,_novelBusy||_gameActionRunning,session?.Connection):ModificationSpeedState.Unavailable;
        state.SpeedEnabled=state.SpeedControl.Enabled;
        bool selected=active&&!_novelBusy&&NovelRowsReady&&state.Selected is not null;
        state.Apply.Enabled=state.Lock.Enabled=state.Unlock.Enabled=state.ValueEnabled=selected;
        state.SnapshotVisible=NovelSnapshots(game);bool ready=state.SnapshotVisible&&!_novelBusy&&!_gameActionRunning;
        state.SnapshotRefresh.Enabled=state.SnapshotEnabled=ready;state.SnapshotCapture.Enabled=ready&&connected;state.SnapshotLoad.Enabled=ready&&connected&&state.Snapshot is not null;
        NativeNovelChanged?.Invoke();
    }
}
