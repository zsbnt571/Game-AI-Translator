using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

internal sealed class RpgTranslationSession : IDisposable
{
    private readonly IGameDataConnection connection;
    private readonly CancellationTokenSource lifetime=new();
    private readonly Func<string,ApiSettings,CancellationToken,Task<string>> translate;
    private readonly ApiSettings singleSettings,batchSettings;
    private string cachePath="";
    private readonly string exe;
    private readonly ApiSettings cacheSettings;
    private readonly EmbeddedTranslationOptions options;
    private readonly EmbeddedSourceLanguagePolicy languagePolicy;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,string> cache=new(StringComparer.Ordinal);
    // Display-only results have no writeback permission. Keep them out of the
    // persistent embedded cache, including next launch's startup prime.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,string> displayCache=new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,byte> selectedCatalogueSources=new(StringComparer.Ordinal);
    internal string Lookup(string source)=>!selectedCatalogueSources.ContainsKey(source)&&languagePolicy.Resolved&&!languagePolicy.Allows(source)?"":cache.TryGetValue(source,out var text)?text:displayCache.GetValueOrDefault(source,"");
    private readonly Dictionary<string,(int Attempts,DateTime Due)> failures=new(StringComparer.Ordinal);
    private bool dirty;
    private string cacheSaveError="";
    private int cacheSaveFailures;
    private DateTime nextCacheSaveAttempt=DateTime.MinValue;
    private volatile bool retryRequested;
    private volatile bool readOnlyTranslationEnabled;
    private string[] readOnlyTexts=[];
    internal string[] ReadOnlyTexts=>System.Threading.Volatile.Read(ref readOnlyTexts);
    internal void EnableReadOnlyTranslation(bool enabled)=>readOnlyTranslationEnabled=enabled;
    private int failedCount;
    private Task? worker;
    internal bool Enabled {get;private set;}
    internal string Status {get;private set;}="翻译关闭";
    internal string CacheWarning=>dirty&&cacheSaveError.Length>0?"缓存保存失败，本次新增译文尚未持久保存："+cacheSaveError:"";
    internal int FailedCount=>System.Threading.Volatile.Read(ref failedCount);
    internal void RetryFailures()=>retryRequested=true;
    private sealed record Job(string[] Sources,Task<Dictionary<string,string>> Work,bool Foreground,CancellationTokenSource Cancellation);
    // Observation is incremental, so older screen text must remain queued.
    // Move this poll's text ahead of it without another entry or provider call.
    private sealed class ForegroundQueue
    {
        private readonly LinkedList<string> order=new();
        private readonly Dictionary<string,LinkedListNode<string>> nodes=new(StringComparer.Ordinal);
        internal int Count=>nodes.Count;
        internal void Enqueue(string source)
        {if(!nodes.ContainsKey(source))nodes[source]=order.AddLast(source);}
        internal void Promote(IReadOnlyList<string> current)
        {
            for(int i=current.Count-1;i>=0;i--)
                if(nodes.TryGetValue(current[i],out var node)){order.Remove(node);order.AddFirst(node);}
        }
        internal string Peek()=>order.First!.Value;
        internal string Dequeue()
        {var node=order.First!;order.RemoveFirst();nodes.Remove(node.Value);return node.Value;}
        internal void Remove(string source)
        {if(nodes.Remove(source,out var node))order.Remove(node);}
        internal void Clear(){nodes.Clear();order.Clear();}
    }
    private sealed class PendingWritebacks
    {
        private readonly ForegroundQueue order=new();
        private readonly ForegroundQueue age=new();
        private readonly Dictionary<string,string> values=new(StringComparer.Ordinal);
        private readonly Dictionary<string,string> accepted=new(StringComparer.Ordinal);
        private int pages;
        internal int Count=>values.Count;
        internal void Set(string source,string text)
        {
            if(accepted.TryGetValue(source,out var previous)&&previous==text)
            {values.Remove(source);order.Remove(source);age.Remove(source);return;}
            values[source]=text;order.Enqueue(source);age.Enqueue(source);
        }
        internal void Promote(IReadOnlyList<string> current)=>order.Promote(current);
        internal void ForgetAcceptance()=>accepted.Clear();
        internal void Clear(){values.Clear();order.Clear();age.Clear();accepted.Clear();pages=0;}
        internal void KeepOnly(Func<string,bool> allowed)
        {foreach(var source in values.Keys.Where(source=>!allowed(source)).ToArray()){values.Remove(source);order.Remove(source);age.Remove(source);}}
        internal void Accepted(JsonArray entries)
        {foreach(var entry in entries)accepted[entry!["source"]!.GetValue<string>()]=entry["text"]!.GetValue<string>();}
        internal JsonArray TakePage()
        {
            var entries=new JsonArray();int bytes=0,examined=0;
            if(order.Count==0)return entries;
            // Some bridges cannot confirm source-cache acceptance. Keep their
            // current retry path, but reserve every fourth page for the oldest
            // unsent values so a repeated full-screen snapshot cannot starve it.
            var selection=++pages%4==0?age:order;
            while(order.Count>0&&entries.Count<128&&examined<128)
            {
                string source=selection.Peek();var node=new JsonObject{["source"]=source,["text"]=values[source]};
                int size=Encoding.UTF8.GetByteCount(node.ToJsonString());
                if(entries.Count>0&&bytes+size>42000)break;
                order.Remove(source);age.Remove(source);values.Remove(source);examined++;
                // Preserve the bridge's existing single-entry safety limit.
                if(size<60000){entries.Add(node);bytes+=size;}
            }
            return entries;
        }
    }
    internal RpgTranslationSession(IGameDataConnection connection,string exe,ApiSettings settings,Func<string,ApiSettings,CancellationToken,Task<string>> translate,EmbeddedTranslationOptions? options=null)
    {
        this.connection=connection;this.exe=exe;this.translate=translate;
        this.options=options??EmbeddedTranslationOptions.ForLegacy(exe);
        languagePolicy=new(settings.SourceLanguage);
        singleSettings=ApiSettingsSnapshot.Copy(settings);batchSettings=ApiSettingsSnapshot.Copy(settings);
        cacheSettings=ApiSettingsSnapshot.Copy(settings);
        RpgTranslationPrompts.Configure(settings,singleSettings,batchSettings);
    }
    internal Task StartAsync()=>Task.Run(async()=>
    {
        try{await StartCoreAsync();}
        catch
        {
            lifetime.Cancel();
            // Startup cache may already be visible. A failed prepare/apply must
            // restore it too, even before the polling worker has been created.
            if(connection.Connected)
                try{await connection.RequestAsync(new(){["op"]="translationDisable"});}
                catch{connection.Dispose();}
            Enabled=false;
            throw;
        }
    });
    private async Task StartCoreAsync()
    {
        Status="正在准备已有译文…";
        var loaded=RpgTranslationCache.Load(exe,cacheSettings);cachePath=loaded.Path;
        dirty=loaded.NeedsSave;
        foreach(var pair in loaded.Entries)
            if(!string.IsNullOrWhiteSpace(pair.Value)&&(!options.CatalogBoundOnly||UnrealTranslationConnection.ValidPair(pair.Key,pair.Value)))cache[pair.Key]=pair.Value;
            else dirty=true;
        var prepared=await connection.RequestAsync(new(){["op"]="translationPrepare",["sourceLanguage"]=cacheSettings.SourceLanguage.ToString()},lifetime.Token);
        int epoch=prepared.GetProperty("epoch").GetInt32();
        // Preserve old caches, but apply entries only after selecting their
        // source language or observing matching text on the current screen.
        await connection.RequestAsync(new(){["op"]="translationEnable",["prepared"]=true},lifetime.Token);
        Enabled=true;Status="正在提取游戏文本…";worker=Task.Run(RunAsync);
    }
    private Task<JsonElement> Prime(JsonArray entries,int epoch)=>connection.RequestAsync(new(){["op"]="translationApply",["epoch"]=epoch,["prime"]=true,["entries"]=entries},lifetime.Token);
    private async Task<bool> ApplyPending(int epoch,PendingWritebacks pending)
    {
        lifetime.Token.ThrowIfCancellationRequested();
        var entries=pending.TakePage();if(entries.Count==0)return false;
        // One bounded page per poll. A large cache must not monopolize the
        // connection while a new scene is waiting to be observed.
        var result=await connection.RequestAsync(new(){["op"]="translationApply",["epoch"]=epoch,["entries"]=entries},lifetime.Token);
        // Godot/Ren'Py count accepted cache rows as "applied", RPG uses
        // "accepted", and Unity explicitly returns "stored". This confirms
        // bridge cache acceptance only, never successful control rendering.
        // Unreal counts identities, not sources, and may reject candidates; its
        // replies must never suppress later retries for a source.
        if(!options.CatalogBoundOnly&&result.ValueKind==JsonValueKind.Object&&
            !(result.TryGetProperty("stale",out var stale)&&stale.ValueKind==JsonValueKind.True)&&
            (!result.TryGetProperty("epoch",out var replyEpoch)||replyEpoch.ValueKind==JsonValueKind.Number&&replyEpoch.TryGetInt32(out int value)&&value==epoch))
        {
            foreach(string field in new[]{"stored","accepted","applied"})
                if(result.TryGetProperty(field,out var count))
                {if(count.ValueKind==JsonValueKind.Number&&count.TryGetInt32(out int total)&&total==entries.Count)pending.Accepted(entries);break;}
        }
        return true;
    }
    private bool SaveCache(bool finalAttempt=false)
    {
        if(!dirty)return true;
        if(!finalAttempt&&DateTime.UtcNow<nextCacheSaveAttempt)return false;
        string relativePath=Path.GetRelativePath(AppDataPaths.Root,cachePath);
        try
        {
            PortableDataStorage.WriteJson(cachePath,cache);
            dirty=false;cacheSaveError="";nextCacheSaveAttempt=DateTime.MinValue;
            if(cacheSaveFailures>0)AppLog.Write("translation-cache",$"stage=cache-save recovered=true path={relativePath} previousFailures={cacheSaveFailures}");
            cacheSaveFailures=0;return true;
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Persistence is independent of the active display bridge. Retain
            // both dirty state and in-memory translations until a later write
            // succeeds; a permanent permission failure remains visible.
            cacheSaveFailures=Math.Min(cacheSaveFailures+1,30);
            int retrySeconds=Math.Min(15,1<<Math.Min(cacheSaveFailures,4));
            nextCacheSaveAttempt=DateTime.UtcNow.AddSeconds(retrySeconds);
            cacheSaveError=SafeDiagnosticOutput.Summary(SafeDiagnosticOutput.ExceptionSummary(ex));
            string connectionState;try{connectionState=connection.Connected.ToString();}catch{connectionState="unavailable";}
            AppLog.Write("translation-cache",$"stage=cache-save final={finalAttempt} path={relativePath} hresult=0x{ex.HResult:X8} connected={connectionState} retrySeconds={retrySeconds}",ex);
            return false;
        }
    }
    private string WithCacheWarning(string status)
    {
        const string marker=" · 缓存保存失败";
        int previous=status.IndexOf(marker,StringComparison.Ordinal);
        if(previous>=0)status=status[..previous];
        string warning=CacheWarning;
        return warning.Length==0?status:status+" · "+warning+(lifetime.IsCancellationRequested?"":"（将自动重试；当前翻译继续）");
    }
    private async Task RunAsync()
    {
        var jobs=new List<Job>();
        bool restoreAfterFailure=false;
        try
        {
            bool renpy=options.Syntax==EmbeddedTextSyntax.Renpy;
            bool remote=options.RemoteCatalog;bool remoteFailed=false;
            var extraction=new RpgTextCatalogResult([],0,[]);
            // Catalogue I/O must never block polling or current-screen translation.
            bool hasDesktopCatalog=options.ConfiguredDesktopCatalog is not null||options.DesktopCatalog is not null;
            var extractionTask=options.ConfiguredDesktopCatalog is {} configured?Task.Run(()=>configured(cacheSettings,lifetime.Token),lifetime.Token):
                options.DesktopCatalog is {} readCatalog?Task.Run(()=>readCatalog(lifetime.Token),lifetime.Token):Task.FromResult(extraction);
            var language=languagePolicy;
            bool languageCommitted=false;
            var provisionalAppliedLanguages=new HashSet<string>(StringComparer.Ordinal);
            var waitingCatalog=new HashSet<string>(StringComparer.Ordinal);
            var languageExcluded=new HashSet<string>(StringComparer.Ordinal);
            string provisionalLanguage="";int declaredLanguageSkipped=0;
            var cachedCatalogUpdates=new Dictionary<string,string>(StringComparer.Ordinal);
            var pendingWritebacks=new PendingWritebacks();string pendingLanguage="";
            _=extractionTask.ContinueWith(t=>{_ = t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
            bool extractionLoaded=false;
            var catalog=new HashSet<string>(StringComparer.Ordinal);
            var backlog=new Queue<string>();
            var visible=new ForegroundQueue();var active=new HashSet<string>(StringComparer.Ordinal);
            var observed=new HashSet<string>(StringComparer.Ordinal);
            // Count transitions once, instead of walking a 150,000-entry
            // catalogue every 100 ms. These are cache counts, not render receipts.
            int completed=0,observedDone=0,additionalDone=0;
            var additional=new HashSet<string>(StringComparer.Ordinal);
            var readOnlySources=new HashSet<string>(StringComparer.Ordinal);
            var writableSources=new HashSet<string>(StringComparer.Ordinal);
            bool MayTranslate(string source)=>(catalog.Contains(source)||language.Allows(source,provisionalLanguage))&&(!readOnlySources.Contains(source)||writableSources.Contains(source)||readOnlyTranslationEnabled);
            bool HasTranslation(string source)=>cache.ContainsKey(source)||!writableSources.Contains(source)&&displayCache.ContainsKey(source);
            void AddCatalog(string text,bool trusted=false)
            {
                if(!trusted&&!language.Allows(text)){languageExcluded.Add(text);return;}
                if(!catalog.Add(text))return;
                selectedCatalogueSources.TryAdd(text,0);
                writableSources.Add(text);
                bool translated=cache.ContainsKey(text);
                if(translated){completed++;cachedCatalogUpdates[text]=cache[text];}else backlog.Enqueue(text);
                if(additional.Remove(text)&&translated)additionalDone--;
            }
            int catalogCursor=0,catalogTotal=0,catalogSkipped=0;bool catalogDone=!remote;
            var nextCatalogPoll=DateTime.MinValue;
            string runtimeWarning="";
            var upcoming=new Queue<string>();var aheadQueued=new HashSet<string>(StringComparer.Ordinal);
            int epoch=0;var saved=DateTime.UtcNow;
            while(!lifetime.IsCancellationRequested&&connection.Connected)
            {
                var updates=new Dictionary<string,string>(StringComparer.Ordinal);
                if(!extractionLoaded&&extractionTask.IsCompleted)
                {
                    try{extraction=await extractionTask;}
                    catch(OperationCanceledException)when(lifetime.IsCancellationRequested){throw;}
                    catch(Exception ex){extraction=new([],0,[SafeDiagnosticOutput.ExceptionSummary(ex)]);}
                    extractionLoaded=true;
                    if(hasDesktopCatalog)
                    {
                        language.SelectCatalog(extraction.Texts,extraction.SelectedLanguage);
                        declaredLanguageSkipped+=extraction.SkippedByLanguage;
                        bool trusted=extraction.SelectedLanguage.Length>0&&language.SelectedLanguage==extraction.SelectedLanguage;
                        foreach(var text in extraction.Texts)AddCatalog(text,trusted&&(extraction.TrustedSourceTexts is null||extraction.TrustedSourceTexts.Contains(text)));
                    }
                }
                var poll=await connection.RequestAsync(new(){["op"]="translationPoll"},lifetime.Token);
                int polledEpoch=poll.GetProperty("epoch").GetInt32();
                if(epoch!=polledEpoch)pendingWritebacks.Clear();
                epoch=polledEpoch;
                if(poll.TryGetProperty("runtimeWarning",out var runtimeNote)&&runtimeNote.ValueKind==JsonValueKind.String)
                    runtimeWarning=SafeDiagnosticOutput.Summary(runtimeNote.GetString()??"");
                var currentTexts=poll.GetProperty("texts").EnumerateArray().Select(item=>item.GetString()!).ToArray();
                var currentPriority=new List<string>(currentTexts);
                if(!language.Resolved&&currentTexts.Length>0)provisionalLanguage=EmbeddedSourceLanguagePolicy.Choose(currentTexts);
                if(poll.TryGetProperty("ahead",out var ahead))
                    foreach(var item in ahead.EnumerateArray())
                    {var text=item.GetString()!;if(language.Resolved&&language.Allows(text)&&!cache.ContainsKey(text)&&!active.Contains(text)&&!failures.ContainsKey(text)&&aheadQueued.Add(text))upcoming.Enqueue(text);}
                if(extractionLoaded&&catalogDone&&waitingCatalog.Count>0)
                {
                    language.SelectCatalog(waitingCatalog);
                    foreach(var text in waitingCatalog)AddCatalog(text);
                    waitingCatalog.Clear();
                }
                if(language.Resolved&&provisionalAppliedLanguages.Any(value=>value!=language.SelectedLanguage))
                {
                    // Visible text can precede directory I/O. If the complete
                    // catalogue chooses a different language, restore the early
                    // writebacks and rebuild only the selected language's map.
                    pendingWritebacks.Clear();
                    await connection.RequestAsync(new(){["op"]="translationDisable"},lifetime.Token);
                    var prepared=await connection.RequestAsync(new(){["op"]="translationPrepare",["sourceLanguage"]=cacheSettings.SourceLanguage.ToString()},lifetime.Token);
                    epoch=prepared.GetProperty("epoch").GetInt32();
                    await connection.RequestAsync(new(){["op"]="translationEnable",["prepared"]=true},lifetime.Token);
                    foreach(var text in catalog)if(cache.TryGetValue(text,out var cached))cachedCatalogUpdates[text]=cached;
                }
                if(language.Resolved&&!languageCommitted)
                {
                    languageCommitted=true;
                    // Unneeded provisional requests must not occupy all four
                    // slots until their provider timeout. Keep each cancelled
                    // job in the list until it finishes to preserve concurrency.
                    foreach(var job in jobs.Where(job=>job.Sources.All(source=>!MayTranslate(source))))job.Cancellation.Cancel();
                    // Also discard failed provisional requests that never wrote
                    // anything, otherwise their retry count can remain stuck.
                    foreach(var text in observed.Where(text=>!catalog.Contains(text)&&!language.Allows(text)).ToArray())
                    {observed.Remove(text);if(cache.ContainsKey(text))observedDone--;if(additional.Remove(text)&&cache.ContainsKey(text))additionalDone--;}
                    foreach(var text in failures.Keys.Where(text=>!catalog.Contains(text)&&!language.Allows(text)).ToArray())failures.Remove(text);
                    foreach(var text in displayCache.Keys.Where(text=>!language.Allows(text)))displayCache.TryRemove(text,out _);
                }
                if(language.Resolved)provisionalAppliedLanguages.Clear();
                foreach(var pair in cachedCatalogUpdates)updates[pair.Key]=pair.Value;
                cachedCatalogUpdates.Clear();
                foreach(var text in currentTexts)
                {
                    if(!catalog.Contains(text)&&!language.Allows(text,provisionalLanguage))continue;
                    writableSources.Add(text);
                    if(observed.Add(text))
                    {
                        bool translated=cache.ContainsKey(text);
                        if(translated)observedDone++;
                        if(!catalog.Contains(text)&&additional.Add(text)&&translated)additionalDone++;
                    }
                    if(cache.TryGetValue(text,out var cached))updates[text]=cached;
                    else if(!active.Contains(text)&&!failures.ContainsKey(text))visible.Enqueue(text);
                }
                // Read-only display text is an explicit fallback channel. Never
                // confuse it with text the bridge can safely replace in-game.
                if(poll.TryGetProperty("readOnlyTexts",out var readOnly)&&readOnly.ValueKind==JsonValueKind.Array)
                {
                    var current=readOnly.EnumerateArray().Where(v=>v.ValueKind==JsonValueKind.String)
                        .Select(v=>v.GetString()!).Where(s=>!string.IsNullOrWhiteSpace(s)&&s.Length<=6000&&!s.Contains('\0'))
                        .Distinct(StringComparer.Ordinal).Take(128).ToArray();
                    System.Threading.Volatile.Write(ref readOnlyTexts,current);
                    if(readOnlyTranslationEnabled)currentPriority.AddRange(current);
                    if(!language.Resolved&&currentTexts.Length==0&&current.Length>0)provisionalLanguage=EmbeddedSourceLanguagePolicy.Choose(current);
                    foreach(var text in current)
                    {
                        readOnlySources.Add(text);
                        if(readOnlyTranslationEnabled&&language.Allows(text,provisionalLanguage)&&!HasTranslation(text)&&!active.Contains(text)&&!failures.ContainsKey(text))visible.Enqueue(text);
                    }
                }
                foreach(var job in jobs.Where(j=>j.Work.IsCompleted).ToArray())
                {
                    Dictionary<string,string> results;
                    try{results=await job.Work;}
                    catch(OperationCanceledException)when(job.Cancellation.IsCancellationRequested&&!lifetime.IsCancellationRequested)
                    {results=new(StringComparer.Ordinal);}
                    finally{jobs.Remove(job);job.Cancellation.Dispose();}
                    foreach(var source in job.Sources)
                    {
                        active.Remove(source);
                        if(results.TryGetValue(source,out var text))
                        {
                            // A catalogue choice can arrive while a visible
                            // request is in flight. Do not apply another language.
                            if(!MayTranslate(source)){failures.Remove(source);continue;}
                            if(readOnlySources.Contains(source)&&!writableSources.Contains(source))
                            {displayCache[source]=text;failures.Remove(source);continue;}
                            if(!cache.ContainsKey(source))
                            {if(catalog.Contains(source))completed++;if(observed.Contains(source))observedDone++;if(additional.Contains(source))additionalDone++;}
                            cache[source]=text;dirty=true;failures.Remove(source);updates[source]=text;
                        }
                        else if(MayTranslate(source))
                        {int count=failures.GetValueOrDefault(source).Attempts+1;failures[source]=(count,count<4?DateTime.UtcNow.AddSeconds(Math.Min(60,2*Math.Pow(3,count-1))):DateTime.MaxValue);}
                    }
                }
                lifetime.Token.ThrowIfCancellationRequested();
                foreach(var pair in updates)
                    if(!readOnlySources.Contains(pair.Key)||writableSources.Contains(pair.Key))pendingWritebacks.Set(pair.Key,pair.Value);
                string effectiveLanguage=language.Resolved?language.SelectedLanguage:provisionalLanguage;
                if(effectiveLanguage!=pendingLanguage)
                {
                    // Scan only on a language transition, never on every poll of
                    // a large pending cache. Stored translations remain intact.
                    pendingWritebacks.ForgetAcceptance();pendingWritebacks.KeepOnly(MayTranslate);pendingLanguage=effectiveLanguage;
                }
                pendingWritebacks.Promote(currentPriority);
                if(await ApplyPending(epoch,pendingWritebacks)&&!language.Resolved)provisionalAppliedLanguages.Add(provisionalLanguage);
                if(retryRequested){retryRequested=false;foreach(var key in failures.Keys.ToArray())failures[key]=(0,DateTime.UtcNow);}
                foreach(var pair in failures.ToArray())
                    if(pair.Value.Due<=DateTime.UtcNow&&!active.Contains(pair.Key)&&MayTranslate(pair.Key))
                    {
                        // A failed catalogue batch must not consume the slot
                        // reserved for text actually observed in the game.
                        if(observed.Contains(pair.Key)||readOnlyTranslationEnabled&&readOnlySources.Contains(pair.Key))visible.Enqueue(pair.Key);
                        else if(aheadQueued.Add(pair.Key))upcoming.Enqueue(pair.Key);
                    }
                // Keep retry backoff above; promoting an existing entry cannot
                // bypass it, cancel an active batch, or create a duplicate job.
                visible.Promote(currentPriority);
                // Three batch slots plus a reserved foreground slot. Polls never wait for a slow batch.
                while(jobs.Count<4)
                {
                    var work=new List<string>();bool foreground=false;
                    // A Ren'Py menu can expose many short labels at once. Group the
                    // already-queued labels without delaying the first request.
                    while(visible.Count>0&&work.Count<Math.Clamp(options.ForegroundBatchSize,1,8)&&work.Sum(s=>s.Length)<2400)
                    {
                        // Retry a failed visible item alone so one malformed
                        // batch response cannot repeatedly hold the whole menu.
                        if(work.Count>0&&failures.ContainsKey(visible.Peek()))break;
                        var source=visible.Dequeue();
                        if(MayTranslate(source)&&!HasTranslation(source)&&active.Add(source)){work.Add(source);foreground=true;if(failures.ContainsKey(source))break;}
                    }
                    if(work.Count==0)
                    {
                        if(jobs.Count(j=>!j.Foreground)>=3)break;
                        int chars=0;
                        while((upcoming.Count>0||backlog.Count>0)&&work.Count<24&&chars<7000)
                        {var source=upcoming.Count>0?upcoming.Dequeue():backlog.Dequeue();aheadQueued.Remove(source);if(!MayTranslate(source)||HasTranslation(source)||failures.TryGetValue(source,out var failure)&&failure.Due>DateTime.UtcNow||!active.Add(source))continue;work.Add(source);chars+=source.Length;}
                    }
                    if(work.Count==0)break;
                    var sources=work.ToArray();var jobCancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    jobs.Add(new(sources,RpgTranslationBatch.Translate(sources,singleSettings,batchSettings,translate,jobCancellation.Token,renpy,options.Syntax),foreground,jobCancellation));
                }
                // Dispatch current-screen work and apply ready results before
                // asking for another directory page. Pace pages without starving
                // them while the foreground is active; an in-flight IPC request
                // itself is still serialized by the connection.
                if(remote&&(!catalogDone||!renpy)&&DateTime.UtcNow>=nextCatalogPoll)
                {
                    try
                    {
                    var page=await connection.RequestAsync(new(){["op"]="translationCatalog",["cursor"]=catalogCursor},lifetime.Token);
                    catalogTotal=page.GetProperty("total").GetInt32();catalogSkipped=page.GetProperty("skipped").GetInt32();
                    string selected=page.TryGetProperty("selectedLanguage",out var selectedValue)&&selectedValue.ValueKind==JsonValueKind.String?selectedValue.GetString()??"":"";
                    if(selected.Length>0)
                    {
                        language.SelectCatalog([],selected);
                    }
                    // A mixed-origin catalogue can filter identified locale
                    // branches without assigning one locale to every source.
                    if(page.TryGetProperty("skippedByLanguage",out var filteredValue)&&filteredValue.TryGetInt32(out int filtered))declaredLanguageSkipped=Math.Max(declaredLanguageSkipped,filtered);
                    foreach(var item in page.GetProperty("texts").EnumerateArray())
                    {
                        var text=item.GetString()!;
                        if(language.Resolved)AddCatalog(text,selected.Length>0&&selected==language.SelectedLanguage);
                        else waitingCatalog.Add(text);
                    }
                    int next=page.GetProperty("next").GetInt32();
                    bool scanning=page.TryGetProperty("scanning",out var scanningValue)&&scanningValue.GetBoolean()
                        ||page.TryGetProperty("done",out var doneValue)&&!doneValue.GetBoolean();
                    if(next<0||next>catalogTotal||next<catalogCursor||next==catalogCursor&&next<catalogTotal&&!scanning)throw new IOException("游戏文本目录没有正确继续返回数据。");
                    catalogCursor=next;catalogDone=next>=catalogTotal&&!scanning;
                    nextCatalogPoll=DateTime.UtcNow.AddMilliseconds(catalogDone?2000:250);
                    }
                    catch(OperationCanceledException)when(lifetime.IsCancellationRequested){throw;}
                    catch(Exception)when(connection.Connected)
                    {remote=false;remoteFailed=true;catalogDone=true;}
                }
                int failed=failures.Count;System.Threading.Volatile.Write(ref failedCount,failed);
                Status=$"原文：{language.Label} · 已提取 {catalog.Count} 条 · 已翻译 {completed} 条";
                if(languageExcluded.Count+declaredLanguageSkipped>0)Status+=$" · 已排除其他语言 {languageExcluded.Count+declaredLanguageSkipped} 条";
                if(!renpy)Status+=observed.Count>0?$" · 运行时累计捕获 {observed.Count} 条（已有译文 {observedDone} 条）":ReadOnlyTexts.Length>0?" · 运行时已读到文字，尚不能内嵌替换":" · 运行时尚未捕获文本";
                if(additional.Count>0)Status+=renpy?$" · 新发现文本 {additionalDone} / {additional.Count} 条":$" · 目录外补充 {additionalDone} / {additional.Count} 条";
                if(ReadOnlyTexts.Length>0)Status+=readOnlyTranslationEnabled?" · 备用译文窗已开启":" · 有文字暂不能替换，可打开备用译文窗";
                Status+=$" · 处理中 {active.Count} 条";
                if(pendingWritebacks.Count>0)Status+=$" · 等待显示 {pendingWritebacks.Count} 条";
                if(!catalogDone||!extractionLoaded)Status+=" · 正在读取文本目录";
                if(catalogSkipped>0)Status+=options.CatalogBoundOnly?$" · {catalogSkipped} 项保留原有译文或暂未提取":options.Syntax==EmbeddedTextSyntax.Generic?$" · 提取时跳过 {catalogSkipped} 项（资源或过长文本），运行时继续补译":$" · {catalogSkipped} 条过长文本未提取";
                if(failed>0)Status+=$" · 失败待重试 {failed} 条";
                else if(runtimeWarning.Length==0&&catalogDone&&extractionLoaded&&completed==catalog.Count&&additionalDone==additional.Count&&backlog.Count==0&&visible.Count==0&&jobs.Count==0&&pendingWritebacks.Count==0)Status+=catalog.Count==0&&observed.Count==0?" · 等待可读取的游戏文字":renpy||options.Syntax==EmbeddedTextSyntax.Generic?" · 当前提取文本已译完":" · 预翻译完成";
                if(runtimeWarning.Length>0)Status+=" · "+runtimeWarning;
                if(extraction.Unreadable.Length>0)Status+=options.Syntax==EmbeddedTextSyntax.Generic?$" · {extraction.Unreadable.Length} 个资源存在未提取内容":$" · {extraction.Unreadable.Length} 个文件未能读取";
                if(remoteFailed)Status+=options.CatalogBoundOnly?" · 游戏文本目录读取失败，暂无法获取其余文本":" · 游戏文本目录读取失败，继续实时补译";
                if(dirty&&((DateTime.UtcNow-saved).TotalSeconds>=5||jobs.Count==0))
                {if(SaveCache())saved=DateTime.UtcNow;}
                Status=WithCacheWarning(Status);
                await Task.Delay(100,lifetime.Token);
            }
            if(!lifetime.IsCancellationRequested)Status="翻译连接已断开，已有译文缓存保留。";
        }
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
        catch(Exception ex){restoreAfterFailure=true;Status="翻译连接已停止："+SafeDiagnosticOutput.ExceptionSummary(ex);}
        finally
        {
            lifetime.Cancel();
            foreach(var job in jobs)_=job.Work.ContinueWith(t=>{_ = t.Exception;job.Cancellation.Dispose();},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            // Keep Enabled until restoration finishes so a new UI session cannot
            // overtake this session's final Disable on a still-connected bridge.
            if(restoreAfterFailure&&connection.Connected)
                try{await connection.RequestAsync(new(){["op"]="translationDisable"});}catch{connection.Dispose();}
            if(dirty)SaveCache(finalAttempt:true);
            Status=WithCacheWarning(Status);
            Enabled=false;
        }
    }
    internal async Task StopAsync()
    {
        lifetime.Cancel();
        // Restoring originals must not wait for a provider which ignores cancellation.
        try
        {
            if(connection.Connected)await connection.RequestAsync(new(){["op"]="translationDisable"});
        }
        catch{connection.Dispose();throw;}
        finally
        {
            if(worker is not null)await worker;else if(dirty)SaveCache(finalAttempt:true);
            Enabled=false;Status=WithCacheWarning("翻译关闭");
        }
    }
    public void Dispose(){lifetime.Cancel();Enabled=false;}
}
