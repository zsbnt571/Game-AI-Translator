using System.Collections.Concurrent;
using System.Text.RegularExpressions;
namespace ScreenshotTranslationUiTester;

// A separate, bounded name queue; never enables the game's translation hooks.
internal sealed class RpgLabelTranslations:IDisposable
{
    private readonly ConcurrentDictionary<string,string> names=new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> pending=new();
    private readonly ConcurrentDictionary<string,byte> requested=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,int> provisional=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,byte> scheduled=new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime=new();
    private readonly SemaphoreSlim signal=new(0);
    private readonly Task worker;
    private readonly bool allLanguages;
    private readonly bool followGameTarget;
    private readonly TranslationWorkspaceStore? workspace;
    private readonly ApiSettings targetSettings;
    private readonly TranslationGlossaryCache receipts=new();
    private readonly object receiptsGate=new();
    private sealed record CheckedName(TranslationWorkspaceStore.Document Rules,string Text,int ReceiptVersion,string Display);
    private sealed record ChangedRules(string[] Terms);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TranslationWorkspaceStore.Document,
        System.Runtime.CompilerServices.ConditionalWeakTable<TranslationWorkspaceStore.Document,ChangedRules>> ruleChanges=new();
    private readonly ConcurrentDictionary<string,CheckedName> checkedNames=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,int> receiptVersions=new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> displayChecks=new();
    private readonly ConcurrentDictionary<string,byte> displayChecking=new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> displayRequests=new();
    private readonly ConcurrentDictionary<string,byte> displayRequesting=new(StringComparer.Ordinal);
    private int displayWorkerRunning,ruleCheckRunning;
    private long nextRuleCheck;
    private readonly object notifyGate=new();
    private string[]? changedSources;
    internal IReadOnlyList<string>? ChangedSources=>System.Threading.Volatile.Read(ref changedSources);
    internal ApplicationLanguage Language {get;}
    private static ApiSettings NameSettings(ApiSettings source,ApplicationLanguage language,bool followGameTarget=false)
    {
        var settings=followGameTarget?ApiSettingsSnapshot.Copy(source):InterfaceDisplayLanguage.Settings(source,language);
        settings.SourceLanguage=SourceLanguageMode.Auto;settings.SourceLanguages=[];
        if(!followGameTarget&&language==ApplicationLanguage.SimplifiedChinese)settings.TargetLanguage="zh-CN";
        // Preserve the selected target for game data names. Content-specific
        // gameplay instructions must not override this name translation request.
        settings.TranslationStyle=TranslationStyle.Natural;settings.CustomTranslationPrompt="";
        return settings;
    }
    internal static string CachePath(string exe,ApiSettings source,ApplicationLanguage language,bool followGameTarget=false)
    {var settings=NameSettings(source,language,followGameTarget);return Path.Combine(RpgMakerDataAdapter.Storage(exe),"labels",RpgTranslationCache.Id(settings,RpgTranslationCache.Endpoint(settings.ApiUrl))+".json");}
    internal event Action? Changed;
    internal string Status {get;private set;}="";
    internal bool HasPendingDisplayChecks=>System.Threading.Volatile.Read(ref displayWorkerRunning)!=0||!displayChecks.IsEmpty||!displayRequests.IsEmpty;
    internal void RequestForDisplay(string source)
    {
        if(lifetime.IsCancellationRequested||string.IsNullOrWhiteSpace(source)||source.Length>240||displayRequesting.Count>=512)return;
        if(!displayRequesting.TryAdd(source,0))return;
        displayRequests.Enqueue(source);StartDisplayWorker();
    }
    // UI lookups never read a file, wait on the workspace/receipt locks or run
    // whole-text quality checks. Only known cached names can enter this queue,
    // so repeated paints cannot grow it with arbitrary or duplicate strings.
    internal string LookupForDisplay(string source)
    {
        if(lifetime.IsCancellationRequested||(!followGameTarget&&Language!=UiLanguage.Current))return "";
        string value=names.GetValueOrDefault(source,"");
        if(workspace is null)return value;
        RefreshDisplayRules();
        var document=workspace.PublishedSnapshot;
        if(document is not null)
        {
            if(TranslationWorkspaceStore.Manual(document,source,EmbeddedTextSyntax.Generic) is {} manual)return manual;
            if(document.Terms.TryGetValue(source,out string? term))return term;
        }
        if(value.Length==0)return "";
        checkedNames.TryGetValue(source,out var known);
        if(known is not null&&ReferenceEquals(known.Rules,document)&&known.Text==value&&known.ReceiptVersion==receiptVersions.GetValueOrDefault(source))
            return known.Display;
        if(displayChecking.TryAdd(source,0)){displayChecks.Enqueue(source);StartDisplayWorker();}
        // A rule edit must not flash a whole displayed table back to source
        // text. Keep only this exact previously checked value while rechecking;
        // the worker replaces/rejects it without granting a new receipt.
        return known is not null&&known.Text==value?known.Display:"";
    }
    private void RefreshDisplayRules()
    {
        if(Environment.TickCount64<System.Threading.Volatile.Read(ref nextRuleCheck)||Interlocked.CompareExchange(ref ruleCheckRunning,1,0)!=0)return;
        System.Threading.Volatile.Write(ref nextRuleCheck,Environment.TickCount64+1000);
        _=Task.Run(()=>
        {
            try
            {
                if(lifetime.IsCancellationRequested)return;
                var previous=workspace!.PublishedSnapshot;
                var current=workspace.Snapshot();
                if(!ReferenceEquals(previous,current)&&!lifetime.IsCancellationRequested)NotifyChanged();
            }
            catch(Exception ex)when(ex is IOException or System.Text.Json.JsonException){Status="译名规则未能读取："+SafeDiagnosticOutput.ExceptionSummary(ex);}
            finally{Interlocked.Exchange(ref ruleCheckRunning,0);}
        });
    }
    private void StartDisplayWorker()
    {
        if(lifetime.IsCancellationRequested||Interlocked.CompareExchange(ref displayWorkerRunning,1,0)!=0)return;
        _=Task.Run(()=>
        {
            var completed=new List<string>(256);long notified=Environment.TickCount64;
            try
            {
                while(!lifetime.IsCancellationRequested)
                {
                    string? source;
                    // Visible rows retain priority over a background recheck
                    // of a large table after terminology edits.
                    if(displayRequests.TryDequeue(out source))
                    {
                        checkedNames.TryGetValue(source,out var previous);
                        try{Request(source);}finally{displayRequesting.TryRemove(source,out _);}
                        if(checkedNames.TryGetValue(source,out var current)&&!ReferenceEquals(previous,current))completed.Add(source);
                    }
                    else if(displayChecks.TryDequeue(out source))
                    {try{_=Lookup(source);}finally{displayChecking.TryRemove(source,out _);}completed.Add(source);}
                    else break;
                    if(completed.Count>=256&&Environment.TickCount64-notified>=100)
                    {NotifyChanged(completed);completed.Clear();notified=Environment.TickCount64;}
                }
                if(completed.Count>0&&!lifetime.IsCancellationRequested)NotifyChanged(completed);
            }
            finally
            {
                Interlocked.Exchange(ref displayWorkerRunning,0);
                if((!displayChecks.IsEmpty||!displayRequests.IsEmpty)&&!lifetime.IsCancellationRequested)StartDisplayWorker();
            }
        });
    }
    internal string Lookup(string source)
    {
        if(!followGameTarget&&Language!=UiLanguage.Current)return "";
        string value=names.GetValueOrDefault(source,"");
        if(workspace is null)return value;
        try
        {
            var document=workspace.Snapshot();
            if(TranslationWorkspaceStore.Manual(document,source,EmbeddedTextSyntax.Generic) is {} manual)return manual;
            if(document.Terms.TryGetValue(source,out var term))return term;
            if(value.Length==0)return "";
            int version=receiptVersions.GetValueOrDefault(source);
            if(checkedNames.TryGetValue(source,out var known)&&known.Text==value&&known.ReceiptVersion==version)
            {
                if(ReferenceEquals(known.Rules,document))return known.Display;
                // A document revision may add one unrelated name or a manual
                // correction. Diff each immutable snapshot pair once, then
                // recheck only names whose source/result could be affected.
                // Rule metadata is included; exact manual/term overrides were
                // already handled above. No dictionary fingerprint per row.
                var changes=ruleChanges.GetValue(known.Rules,_=>new()).GetValue(document,
                    next=>new(TranslationGlossary.Changed(known.Rules.Terms,next.Terms))).Terms;
                if(!TranslationGlossary.Affected(source,changes)&&!TranslationGlossary.Affected(value,changes))
                {
                    checkedNames[source]=known with{Rules=document};
                    return known.Display;
                }
            }
            bool accepted;
            lock(receiptsGate)accepted=receipts.Matches(source,value,document.Terms);
            string display=accepted&&AutomaticTranslationQuality.Displayable(source,value,targetSettings,document.Terms,EmbeddedTextSyntax.Generic)?value:"";
            // Workspace snapshots are immutable. Exact value + per-source
            // receipt revision prevents a repaired name from reusing an old
            // rejection, even when its spelling did not change.
            if(receiptVersions.GetValueOrDefault(source)==version&&names.GetValueOrDefault(source,"")==value)
                checkedNames[source]=new(document,value,version,display);
            return display;
        }
        catch(Exception ex) when(ex is IOException or System.Text.Json.JsonException){Status="译名规则未能读取："+SafeDiagnosticOutput.ExceptionSummary(ex);}
        return value;
    }
    private void AcceptReceipt(string source,string value,IReadOnlyDictionary<string,string> terms)
    {
        lock(receiptsGate)
        {
            receipts.Accept(source,value,terms);
            receiptVersions.AddOrUpdate(source,1,(_,version)=>unchecked(version+1));
            checkedNames.TryRemove(source,out _);
        }
    }
    private void RememberName(string source,string value)
    {
        names[source]=value;checkedNames.TryRemove(source,out _);
        // This method runs in the name worker. Prime validation there instead
        // of making the first paint of a large database do it on the UI thread.
        _=Lookup(source);
    }
    private void RememberValidatedName(string source,string value,TranslationWorkspaceStore.Document? document)
    {
        names[source]=value;
        // Startup just checked this exact pair and receipt under this snapshot.
        // Publishing that result avoids a second identical quality pass.
        if(document is not null)checkedNames[source]=new(document,value,receiptVersions.GetValueOrDefault(source),value);
    }
    private void NotifyChanged(IEnumerable<string>? sources=null)
    {
        lock(notifyGate)
        {
            if(lifetime.IsCancellationRequested)return;
            System.Threading.Volatile.Write(ref changedSources,sources?.Distinct(StringComparer.Ordinal).ToArray());
            Changed?.Invoke();
        }
    }
    private bool NeedsTranslation(string source)
    {
        if(workspace?.Snapshot() is {} document&&
            (TranslationWorkspaceStore.Manual(document,source,EmbeddedTextSyntax.Generic) is not null||document.Terms.ContainsKey(source)))return false;
        return Lookup(source).Length==0||provisional.ContainsKey(source);
    }
    internal bool NeedsRequest(string source)=>source.Length<=240&&provisional.GetValueOrDefault(source)<4&&NeedsTranslation(source)&&!requested.ContainsKey(source);
    internal void Request(string source)
    {
        if(string.IsNullOrWhiteSpace(source)||source.Length>240||!Regex.IsMatch(source,(allLanguages||Language!=ApplicationLanguage.SimplifiedChinese)?@"\p{L}":@"[A-Za-z\u3040-\u30ff]"))return;
        if(Language==ApplicationLanguage.SimplifiedChinese&&!allLanguages&&Regex.IsMatch(source,@"[\u4e00-\u9fff]")&&!Regex.IsMatch(source,@"[\u3040-\u30ff]"))return;
        if(pending.Count>=512)return;
        if(!NeedsTranslation(source)||provisional.GetValueOrDefault(source)>=4)return;
        if(!provisional.ContainsKey(source)&&Lookup(source).Length==0&&names.TryRemove(source,out _))requested.TryRemove(source,out _);
        if(!requested.TryAdd(source,0))return;
        pending.Enqueue(source);signal.Release();
    }
    private void ScheduleRepair(string source,int attempt)
    {
        if(attempt>=4||!scheduled.TryAdd(source,0))return;
        _=Task.Run(async()=>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt==0?0:2*Math.Pow(3,attempt-1)),lifetime.Token);
                scheduled.TryRemove(source,out _);
                requested.TryRemove(source,out _);Request(source);
            }
            catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
            catch(Exception ex)when(ex is IOException or System.Text.Json.JsonException)
            {Status="译名规则未能读取："+SafeDiagnosticOutput.ExceptionSummary(ex);}
        });
    }
    internal RpgLabelTranslations(string exe,ApiSettings source,Func<string,ApiSettings,CancellationToken,Task<string>> translate,bool allLanguages=false,bool followGameTarget=false)
    {
        this.allLanguages=allLanguages||followGameTarget;this.followGameTarget=followGameTarget;Language=UiLanguage.Current;
        var settings=NameSettings(source,Language,followGameTarget);
        targetSettings=settings;workspace=followGameTarget?TranslationWorkspaceStore.For(exe,settings):null;
        var single=ApiSettingsSnapshot.Copy(settings);var batch=ApiSettingsSnapshot.Copy(settings);RpgTranslationPrompts.Configure(settings,single,batch);
        worker=Task.Run(async()=>
        {
            string path=CachePath(exe,source,Language,followGameTarget);
            bool dirty=false;
            try
            {
                var previousIssues=workspace?.Issues().GroupBy(i=>i.Source,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.Last(),StringComparer.Ordinal);
                void RestoreSoft(string key,string value,IReadOnlyDictionary<string,string>? terms)
                {
                    string reason=AutomaticTranslationQuality.Reason(key,value,settings,terms,EmbeddedTextSyntax.Generic);
                    if(!AutomaticTranslationQuality.Soft(reason)){provisional.TryRemove(key,out _);return;}
                    var old=previousIssues?.GetValueOrDefault(key);
                    bool same=old is not null&&old.Soft&&old.TextHash==TranslationAttemptStatus.TextHash(value)&&old.TermStamp==TranslationGlossary.Stamp(terms??new Dictionary<string,string>(),key);
                    provisional[key]=same?Math.Clamp(old!.Attempts,0,4):0;
                }
                // Read only game caches with the same target; never overwrite them.
                if(followGameTarget||InterfaceDisplayLanguage.Matches(source.TargetLanguage,Language))
                {
                    var loaded=RpgTranslationCache.Load(exe,source);
                    if(followGameTarget)loaded=RpgTranslationCache.Normalize(loaded,EmbeddedTextSyntax.Generic);
                    var document=workspace?.Snapshot();
                    var originalReceipts=TranslationGlossaryCache.Load(loaded.Path,loaded.Receipts);
                    foreach(var p in loaded.Entries)
                    {
                        if(document is not null)
                        {
                            // Effective() overlays manual edits. Keep those dynamic in
                            // Lookup instead of promoting them into automatic cache data.
                            // Validate original receipts before accepting current rules.
                            if(!originalReceipts.Matches(p.Key,p.Value,document.Terms)||
                                !TranslationWorkspaceStore.Valid(p.Key,p.Value,EmbeddedTextSyntax.Generic)||
                                !AutomaticTranslationQuality.Displayable(p.Key,p.Value,source,document.Terms,EmbeddedTextSyntax.Generic)||
                                !TranslationLanguagePolicy.ShouldTranslate(p.Key,source))continue;
                            AcceptReceipt(p.Key,p.Value,document.Terms);
                        }
                        RememberValidatedName(p.Key,p.Value,document);
                        RestoreSoft(p.Key,p.Value,document?.Terms);
                    }
                }
                if(Language==ApplicationLanguage.SimplifiedChinese&&!followGameTarget)
                {
                    var legacy=ApiSettingsSnapshot.Copy(source);legacy.TargetLanguage="zh-CN";
                    var oldPath=Path.Combine(RpgMakerDataAdapter.Storage(exe),"labels",RpgTranslationCache.Id(legacy,RpgTranslationCache.Endpoint(legacy.ApiUrl))+".json");
                    var oldNames=RpgTranslationCache.Read(oldPath)??RpgTranslationCache.Read(oldPath+".previous");
                    if(oldNames is not null)foreach(var p in oldNames)RememberName(p.Key,p.Value);
                }
                var saved=RpgTranslationCache.Read(path)??RpgTranslationCache.Read(path+".previous");
                var savedReceipts=TranslationGlossaryCache.Load(path);
                var savedDocument=workspace?.Snapshot();
                if(saved is not null)foreach(var p in saved)
                {
                    if(savedDocument is not null)
                    {
                        var rules=savedDocument.Terms;
                        if(!savedReceipts.Matches(p.Key,p.Value,rules)||
                            !TranslationWorkspaceStore.Valid(p.Key,p.Value,EmbeddedTextSyntax.Generic)||
                            !AutomaticTranslationQuality.Displayable(p.Key,p.Value,settings,rules,EmbeddedTextSyntax.Generic))continue;
                        if(names.TryGetValue(p.Key,out var currentName)&&AutomaticTranslationQuality.Valid(p.Key,currentName,settings,rules,EmbeddedTextSyntax.Generic)&&
                            !AutomaticTranslationQuality.Valid(p.Key,p.Value,settings,rules,EmbeddedTextSyntax.Generic))continue;
                        AcceptReceipt(p.Key,p.Value,rules);
                    }
                    RememberValidatedName(p.Key,p.Value,savedDocument);
                    RestoreSoft(p.Key,p.Value,savedDocument?.Terms);
                }
                NotifyChanged();
                bool configured=!string.IsNullOrWhiteSpace(settings.ApiUrl)&&!string.IsNullOrWhiteSpace(settings.Model)&&(settings.AllowEmptyApiKey||!string.IsNullOrWhiteSpace(settings.ApiKey));
                while(!lifetime.IsCancellationRequested)
                {
                    await signal.WaitAsync(lifetime.Token);
                    var items=new List<string>();
                    while(items.Count<24&&pending.TryDequeue(out var text))if(NeedsTranslation(text)&&provisional.GetValueOrDefault(text)<4)items.Add(text);
                    if(items.Count==0)continue;
                    if(!configured){Status="设置翻译方案后可补齐修改页译名";continue;}
                    Status="正在补齐修改页译名…";
                    try
                    {
                        var document=workspace?.Snapshot();
                        var prompts=document is null?(Single:single,Batch:batch):TranslationWorkspaceStore.WithTerms(document,items.ToArray(),single,batch);
                        var result=await RpgTranslationBatch.Translate(items.ToArray(),prompts.Single,prompts.Batch,translate,lifetime.Token,syntax:EmbeddedTextSyntax.Generic,terms:document?.Terms);
                        var current=workspace?.Snapshot();
                        var quality=result as RpgTranslationBatchResult;
                        var issues=new List<TranslationWorkspaceStore.Issue>();var resolved=new List<string>();
                        foreach(string text in items)
                        {
                            // Manual/term choices remain dynamic. A request sent
                            // under older rules cannot become a new automatic name.
                            if(current is not null&&(TranslationWorkspaceStore.Manual(current,text,EmbeddedTextSyntax.Generic) is not null||current.Terms.ContainsKey(text)))
                            {provisional.TryRemove(text,out _);continue;}
                            if(current is not null&&document is not null&&
                                (TranslationGlossary.Stamp(document.Terms,text)!=TranslationGlossary.Stamp(current.Terms,text)||
                                 current.Requests.GetValueOrDefault(text,"")!=document.Requests.GetValueOrDefault(text,"")))
                            {provisional.TryRemove(text,out _);ScheduleRepair(text,0);continue;}
                            if(!result.TryGetValue(text,out var value))
                            {
                                if(quality?.Failures.TryGetValue(text,out var reason)==true)
                                {
                                    if(provisional.TryGetValue(text,out var previousAttempt))
                                    {
                                        int attempt=Math.Min(4,previousAttempt+1);provisional[text]=attempt;
                                        string previousValue=Lookup(text);
                                        if(document is not null)issues.Add(TranslationAttemptStatus.Evidence(text,reason,attempt,quality.Replies.GetValueOrDefault(text,""),document.Terms,true,previousValue));
                                        ScheduleRepair(text,attempt);
                                    }
                                    else if(document is not null)issues.Add(TranslationAttemptStatus.Evidence(text,reason,1,quality.Replies.GetValueOrDefault(text,""),document.Terms));
                                }
                                continue;
                            }
                            string soft=quality?.SoftFailures.GetValueOrDefault(text,"")??"";
                            if(soft.Length>0)
                            {
                                // An unsuccessful repair never relabels an older
                                // value as a response to the newest glossary.
                                string retained=Lookup(text);bool preserve=retained.Length>0;
                                if(!preserve)
                                {if(document is not null)AcceptReceipt(text,value,document.Terms);RememberName(text,value);dirty=true;}
                                int attempt=provisional.AddOrUpdate(text,1,(_,old)=>Math.Min(4,old+1));
                                if(document is not null)issues.Add(TranslationAttemptStatus.Evidence(text,soft,attempt,quality!.Replies.GetValueOrDefault(text,value),document.Terms,true,preserve?retained:value));
                                ScheduleRepair(text,attempt);
                            }
                            else
                            {
                                if(document is not null)AcceptReceipt(text,value,document.Terms);
                                RememberName(text,value);provisional.TryRemove(text,out _);resolved.Add(text);dirty=true;
                            }
                        }
                        if(workspace is not null&&(issues.Count>0||resolved.Count>0))workspace.RecordIssues(issues,resolved);
                        Status=!provisional.IsEmpty
                            ?provisional.Values.Any(v=>v>=4)?"部分译名仍待改善，自动补译已停止，可点击刷新重试":"部分译名已显示，正在自动补齐"
                            :result.Count<items.Count?"部分译名未能翻译，可点击刷新重试":"";
                        if(dirty)
                        {
                            var values=names.ToDictionary(p=>p.Key,p=>p.Value);
                            if(workspace is not null)
                            {
                                Dictionary<string,TranslationGlossaryCache.Receipt> accepted;
                                lock(receiptsGate)accepted=receipts.ForSources(values);
                                PortableDataStorage.WriteJson(path+".glossary.json",accepted);
                            }
                            PortableDataStorage.WriteJson(path,values);dirty=false;
                        }
                    }
                    catch(OperationCanceledException)when(lifetime.IsCancellationRequested){throw;}
                    catch(Exception ex){Status="部分译名未能读取，可点击刷新重试："+SafeDiagnosticOutput.ExceptionSummary(ex);}
                    // A failed request must not terminate this game's name queue.
                    NotifyChanged(items);
                }
            }
            catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
            catch(Exception ex){Status="译名读取失败："+SafeDiagnosticOutput.ExceptionSummary(ex);}
        });
    }
    internal void RetryMissing()
    {
        foreach(var key in requested.Keys.Concat(provisional.Keys).Distinct(StringComparer.Ordinal))
            if(Lookup(key).Length==0||provisional.ContainsKey(key))
            {if(provisional.ContainsKey(key))provisional[key]=0;requested.TryRemove(key,out _);}
    }
    public void Dispose()
    {
        lifetime.Cancel();
        displayChecks.Clear();displayRequests.Clear();displayChecking.Clear();displayRequesting.Clear();
    }
}
