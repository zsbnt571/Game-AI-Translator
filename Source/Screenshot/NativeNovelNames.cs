using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private static readonly Regex NovelCdbNameSuffix=new(@"\s*\[CDB [^\]]+\]\s*$",RegexOptions.Compiled);
    private static string NovelNameText(string source)=>source.Contains("[CDB ",StringComparison.Ordinal)?NovelCdbNameSuffix.Replace(source,"").Trim():source.Trim();
    private NovelVariableRow[]? _novelNameRows,_novelCommonNameRows;
    private readonly Dictionary<string,List<NovelVariableRow>> _novelNameIndex=new(StringComparer.Ordinal);
    private System.Windows.Forms.Timer? _novelNamesFilterTimer;
    private Action? _novelNamesFilterAction;

    private void EnsureNativeNovelNames()
    {
        if(!WpfWorkspace.Enabled||_selectedGame is not {} game||!NovelModification(game)||NovelSession() is not {} session)return;
        ApiSettings settings;
        try{settings=_fusion!.GameSettings(_appliedDesktopSettings,game.ExePath);}catch(InvalidOperationException){return;}
        string identity="game-target|"+FusionConfiguration.TranslationIdentity(settings);
        if(session.Labels is not null&&session.LabelIdentity!=identity){session.Labels.Dispose();session.Labels=null;}
        if(session.Labels is null)
        {
            var labels=new RpgLabelTranslations(game.ExePath,settings,async(text,profile,token)=>(await _translationService.TranslatePlainAsync(text,profile,token)).Text,followGameTarget:true);
            session.Labels=labels;session.LabelIdentity=identity;
            var changeGate=new object();var changedNames=new HashSet<string>(StringComparer.Ordinal);
            bool allNames=false,queued=false;
            labels.Changed+=()=>
            {
                if(IsDisposed||Disposing)return;
                // Several completed batches can arrive before the dispatcher
                // is free. Post one callback and combine their exact keys.
                lock(changeGate)
                {
                    if(labels.ChangedSources is {} sources)changedNames.UnionWith(sources);else allNames=true;
                    if(queued)return;queued=true;
                }
                try{BeginInvoke((Action)(()=>
                {
                    string[]? changed;
                    lock(changeGate){changed=allNames?null:changedNames.ToArray();changedNames.Clear();allNames=false;queued=false;}
                    if(IsDisposed||Disposing||!SameGame(_selectedGame?.ExePath,game.ExePath)||!ReferenceEquals(NovelSession(),session)||!ReferenceEquals(session.Labels,labels))return;
                    UpdateNativeNovelNames(labels,changed);
                    NativeNovel.NamesRevision++;
                    NativeNovelChanged?.Invoke();
                    ScheduleNativeNovelNameFilter(game,session,labels);
                }));}catch(InvalidOperationException){lock(changeGate)queued=false;}
            };
            UpdateNativeNovelNames(labels);
            NativeNovelRequestNames(NativeNovel.Rows.Take(48).Concat(NativeNovel.CommonRows.Take(16)));
            NativeNovel.NamesRevision++;
        }
        var current=session.Labels;
        NativeNovel.NameLookup=source=>current.LookupForDisplay(NovelNameText(source)) is {Length:>0} translated?translated:source;
    }

    private void ScheduleNativeNovelNameFilter(GameInfo game,GameDataSession session,RpgLabelTranslations labels)
    {
        // A translated query can initially match no rows while their cached
        // names are checked off-thread. Revisit the full snapshot after those
        // results arrive, even when the currently filtered list is empty.
        string query=NativeNovel.Search,scope=NativeNovel.Scope;
        if(string.IsNullOrWhiteSpace(query)||scope is "common" or "maps" or "events")return;
        if(_novelNamesFilterTimer is null)
        {
            _novelNamesFilterTimer=new(){Interval=250};
            _novelNamesFilterTimer.Tick+=(_,_) =>
            {
                _novelNamesFilterTimer?.Stop();
                var action=_novelNamesFilterAction;_novelNamesFilterAction=null;
                action?.Invoke();
            };
            Disposed+=(_,_)=>{_novelNamesFilterTimer?.Dispose();_novelNamesFilterTimer=null;_novelNamesFilterAction=null;};
        }
        _novelNamesFilterAction=()=>
        {
            if(IsDisposed||Disposing||_novelBusy||!NovelRowsReady||NativeNovel.Search!=query||NativeNovel.Scope!=scope||
                !SameGame(_selectedGame?.ExePath,game.ExePath)||!ReferenceEquals(NovelSession(),session)||!ReferenceEquals(session.Labels,labels))return;
            FilterNativeNovelRows(preserveDraft:true);
        };
        // Coalesce many batches into at most one re-filter per 250 ms. An
        // empty query never starts this timer, and stale contexts do no work.
        if(!_novelNamesFilterTimer.Enabled)_novelNamesFilterTimer.Start();
    }

    private void UpdateNativeNovelNames(RpgLabelTranslations labels,IReadOnlyCollection<string>? changed=null)
    {
        if(!ReferenceEquals(_novelNameRows,NativeNovel.Rows)||!ReferenceEquals(_novelCommonNameRows,NativeNovel.CommonRows))
        {
            _novelNameRows=NativeNovel.Rows;_novelCommonNameRows=NativeNovel.CommonRows;_novelNameIndex.Clear();
            foreach(var row in NativeNovel.Rows.Concat(NativeNovel.CommonRows).Distinct())
            {
                string name=NovelNameText(row.RawName);
                if(!_novelNameIndex.TryGetValue(name,out var matches))_novelNameIndex[name]=matches=[];
                matches.Add(row);
            }
            changed=null;
        }
        // Every new row still gets its cached translation. Once indexed,
        // a 24-name provider reply no longer revisits a 20,000-row database.
        foreach(string name in changed??_novelNameIndex.Keys)
            if(_novelNameIndex.TryGetValue(name,out var rows))
            {
                string value=labels.LookupForDisplay(name);
                foreach(var row in rows)row.SetTranslation(value);
            }
    }

    private void QueueNativeNovelNames()
    {
        EnsureNativeNovelNames();
        if(NovelSession()?.Labels is not {} labels)return;
        UpdateNativeNovelNames(labels);
        // Current entries first. LoadingRow requests later visible rows; a large
        // database never fills the translation queue merely by being opened.
        NativeNovelRequestNames(NativeNovel.Rows.Take(48).Concat(NativeNovel.CommonRows.Take(16)));
    }

    internal void NativeNovelRequestNames(IEnumerable<NovelVariableRow> rows)
    {
        if(NovelSession()?.Labels is not {} labels)return;
        foreach(var row in rows)
        {
            string name=NovelNameText(row.RawName);
            row.SetTranslation(labels.LookupForDisplay(name));
            if(name.Length>0)labels.RequestForDisplay(name);
            // Object captions also occur on grouped category cards.
            var parts=name.Split(" / ",StringSplitOptions.TrimEntries);
            if(parts.Length>=3&&!string.IsNullOrWhiteSpace(parts[^2]))
            {
                labels.RequestForDisplay(parts[^2]);
                labels.RequestForDisplay(string.Join(" / ",parts[..^1]));
            }
        }
    }
}
