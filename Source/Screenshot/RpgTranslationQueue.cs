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
    private string cachePath="",displayCachePath="";
    private readonly string exe;
    private readonly ApiSettings cacheSettings;
    private readonly TranslationWorkspaceStore workspace;
    private TranslationWorkspaceStore.Document workspaceDocument;
    private readonly TranslationTemplates templates=new();
    private TranslationGlossaryCache nativeGlossary=new(),displayGlossary=new();
    private readonly HashSet<string> staleGlossary=new(StringComparer.Ordinal);
    private System.Collections.Concurrent.ConcurrentDictionary<string,string> boundaryAliases=new(StringComparer.Ordinal);
    private readonly DisplayLineEndingAliases lineAliases=new();
    private sealed record ManualDisplayAliases(long Revision,Dictionary<string,string> Boundary,DisplayLineEndingAliases Lines);
    private ManualDisplayAliases manualAliases=new(-1,new(StringComparer.Ordinal),new());
    private ManualDisplayAliases IndexManual(TranslationWorkspaceStore.Document document)
    {
        var lines=new DisplayLineEndingAliases();
        if(options.Syntax==EmbeddedTextSyntax.Generic)
            foreach(string source in document.Manual.Keys)lines.Add(source);
        return new(document.Revision,options.DisplayBoundaryWhitespace?DisplayBoundaryAliases.Index(document.Manual.Keys):new(StringComparer.Ordinal),lines);
    }
    private void RefreshBoundaryAliases()
    {
        System.Threading.Volatile.Write(ref manualAliases,IndexManual(workspaceDocument));
        if(!options.DisplayBoundaryWhitespace)return;
        System.Threading.Volatile.Write(ref boundaryAliases,new(DisplayBoundaryAliases.Index(cache.Keys),StringComparer.Ordinal));
    }
    private void RememberNative(string source,string text)
    {
        cache[source]=text;
        if(options.Syntax==EmbeddedTextSyntax.Generic)lineAliases.Add(source);
        if(options.DisplayBoundaryWhitespace)
        {
            string key=DisplayBoundaryAliases.Key(source);
            if(key.Length>0)System.Threading.Volatile.Read(ref boundaryAliases).AddOrUpdate(key,source,(_,old)=>
                source.Length<old.Length||source.Length==old.Length&&string.CompareOrdinal(source,old)<0?source:old);
        }
    }
    private bool TryManual(string source,TranslationWorkspaceStore.Document document,out string text)
    {
        text=TranslationWorkspaceStore.Manual(document,source,options.Syntax,options.CatalogBoundOnly)??"";
        if(text.Length>0)return true;
        var aliases=System.Threading.Volatile.Read(ref manualAliases);
        // A completed provider result can race a newly saved correction before
        // the periodic refresh. Match aliases to that document's revision too.
        if(aliases.Revision!=document.Revision)aliases=IndexManual(document);
        string key=options.DisplayBoundaryWhitespace?DisplayBoundaryAliases.Key(source):source;
        if(options.DisplayBoundaryWhitespace&&aliases.Boundary.TryGetValue(key,out var manual))
            text=TranslationWorkspaceStore.Manual(document,manual,options.Syntax,options.CatalogBoundOnly)??"";
        if(text.Length>0){text=DisplayBoundaryAliases.Key(text);return text.Length>0;}
        if(options.Syntax==EmbeddedTextSyntax.Generic&&aliases.Lines.TryGet(source,
            original=>TranslationWorkspaceStore.Manual(document,original,options.Syntax,options.CatalogBoundOnly),out var manualLines)
            &&TranslationWorkspaceStore.Valid(source,manualLines,options.Syntax,options.CatalogBoundOnly))
        {text=manualLines;return true;}
        return false;
    }
    private bool TryNative(string source,out string text,TranslationWorkspaceStore.Document? latest=null)
    {
        var document=latest??System.Threading.Volatile.Read(ref workspaceDocument);
        if(TryManual(source,document,out text))return true;
        string key=options.DisplayBoundaryWhitespace?DisplayBoundaryAliases.Key(source):source;
        text=cache.GetValueOrDefault(source,"");
        if(text.Length==0&&options.DisplayBoundaryWhitespace&&System.Threading.Volatile.Read(ref boundaryAliases).TryGetValue(key,out var original)
            &&cache.TryGetValue(original,out var reused)&&nativeGlossary.Matches(original,reused,document.Terms))text=DisplayBoundaryAliases.Key(reused);
        if(text.Length==0&&options.Syntax==EmbeddedTextSyntax.Generic&&lineAliases.TryGet(source,
            original=>cache.TryGetValue(original,out var value)&&nativeGlossary.Matches(original,value,document.Terms)?value:null,out var lines)
            &&TranslationWorkspaceStore.Valid(source,lines,options.Syntax,options.CatalogBoundOnly))text=lines;
        return text.Length>0;
    }
    private readonly EmbeddedTranslationOptions options;
    private readonly EmbeddedSourceLanguagePolicy languagePolicy;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,string> cache=new(StringComparer.Ordinal);
    // Display-only results have no writeback permission. Keep them out of the
    // embedded cache, including next launch's startup prime. Persist them in a
    // separate file so opening the same screen again needs no provider request.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,string> displayCache=new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,byte> selectedCatalogueSources=new(StringComparer.Ordinal);
    // Writeback can already contain a newly saved manual correction while the
    // periodic queue refresh is still pending. Readback must see that revision.
    internal string Lookup(string source)=>!languagePolicy.AllowsSelectedText(source)||!selectedCatalogueSources.ContainsKey(source)&&!languagePolicy.AllowsRuntime(source)?"":TryNative(source,out var text,workspace.Snapshot())?text:displayCache.GetValueOrDefault(source,"");
    private readonly Dictionary<string,(int Attempts,DateTime Due,string Code)> failures=new(StringComparer.Ordinal);
    private bool dirty;
    private string cacheSaveError="";
    private int cacheSaveFailures;
    private DateTime nextCacheSaveAttempt=DateTime.MinValue;
    private volatile bool retryRequested;
    private volatile bool readOnlyWindowVisible;
    private string[] readOnlyTexts=[];
    internal string[] ReadOnlyTexts=>System.Threading.Volatile.Read(ref readOnlyTexts);
    internal void SetReadOnlyWindowVisible(bool visible)=>readOnlyWindowVisible=visible;
    private int failedCount;
    private Task? worker;
    private readonly TranslationLatency latency=new();
    private readonly CatalogCoverage coverage=new();
    private readonly string latencySession=Guid.NewGuid().ToString("N");
    private readonly string latencyGameId;
    private DateTime nextLatencyLog;
    private bool bridgePrepared,bridgeEnabled;
    private Task<JsonElement> ScheduledRequest(JsonObject request)
    {
        request["deferIfPaused"]=true;
        string op=request["op"]?.GetValue<string>()??"";
        if(op is "translationPoll" or "translationApply")return latency.MeasureAsync(op=="translationPoll"?TranslationLatency.Stage.BridgePoll:TranslationLatency.Stage.BridgeApply,()=>connection.RequestAsync(request,lifetime.Token));
        return connection.RequestAsync(request,lifetime.Token);
    }
    private async Task PrepareBridgeAsync()
    {
        if(!bridgePrepared)
        {
            // Mixed visible text must not ask Ren'Py or another catalogue bridge
            // to enumerate every alternative translation shipped with the game.
            var prepare=new JsonObject{["op"]="translationPrepare",["sourceLanguage"]=(cacheSettings.SourceLanguage==SourceLanguageMode.Mixed?SourceLanguageMode.Auto:cacheSettings.SourceLanguage).ToString()};
            if(options.StartupCache)prepare["preserveStartup"]=true;
            await ScheduledRequest(prepare);
            bridgePrepared=true;
        }
        if(!bridgeEnabled)
        {
            await ScheduledRequest(new(){["op"]="translationEnable",["prepared"]=true});
            bridgeEnabled=true;
        }
    }
    private async Task<JsonElement> PollBridgeAsync()
    {
        await PrepareBridgeAsync();
        return await ScheduledRequest(new(){["op"]="translationPoll"});
    }
    internal bool Enabled {get;private set;}
    // Unity can stop accepting new work while individual text/font restorations
    // remain unconfirmed. Keep that ownership distinct from scheduling Enabled.
    internal bool RestorePending {get;private set;}
    internal string Status {get;private set;}="翻译关闭";
    internal string CacheWarning=>dirty&&cacheSaveError.Length>0?"缓存保存失败，本次新增译文尚未持久保存："+cacheSaveError:"";
    internal int FailedCount=>System.Threading.Volatile.Read(ref failedCount);
    private EmbeddedTranslationQueueSnapshot snapshot=new();
    internal EmbeddedTranslationQueueSnapshot Snapshot=>System.Threading.Volatile.Read(ref snapshot);
    private int? writebacksConfirmed,writebacksFailed;
    private int? nativePreprocessorHits;
    private int? activeRenderedBindings,activeCachedWaitingBindings;
    private int? nativeCapturedTexts,nativeBindings,lastVerifiedBindings,nativeWritebackPending,fontBlockedBindings,revealingBindings,fontsOwned;
    private string nativeCaptureSemantics="unknown",fontStatus="unknown";
    internal void RetryFailures()=>retryRequested=true;
    private sealed record Job(string[] Sources,Task<Dictionary<string,string>> Work,bool Foreground,CancellationTokenSource Cancellation,Dictionary<string,string> RequestTokens,IReadOnlyDictionary<string,string> Terms);
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
        private int rejectedAttempts;
        private DateTime retryAfter;
        internal int Count=>values.Count;
        internal int AcceptedCount=>accepted.Count;
        internal void Set(string source,string text)
        {
            if(accepted.TryGetValue(source,out var previous)&&previous==text)
            {values.Remove(source);order.Remove(source);age.Remove(source);return;}
            values[source]=text;order.Enqueue(source);age.Enqueue(source);
        }
        internal void Promote(IReadOnlyList<string> current)=>order.Promote(current);
        internal void ForgetAcceptance()=>accepted.Clear();
        internal void Clear(){values.Clear();order.Clear();age.Clear();accepted.Clear();pages=0;rejectedAttempts=0;retryAfter=default;}
        internal void KeepOnly(Func<string,bool> allowed)
        {foreach(var source in values.Keys.Where(source=>!allowed(source)).ToArray()){values.Remove(source);order.Remove(source);age.Remove(source);}}
        internal void Accepted(JsonArray entries)
        {foreach(var entry in entries)accepted[entry!["source"]!.GetValue<string>()]=entry["text"]!.GetValue<string>();rejectedAttempts=0;retryAfter=default;}
        internal void Retry(JsonArray entries)
        {
            // Polls may be incremental. A missing/partial cache receipt must not
            // silently lose the only event for a translated visible label.
            foreach(var entry in entries)
            {
                string source=entry!["source"]!.GetValue<string>();
                if(!values.ContainsKey(source))Set(source,entry["text"]!.GetValue<string>());
            }
            rejectedAttempts=Math.Min(rejectedAttempts+1,4);
            retryAfter=DateTime.UtcNow.AddMilliseconds(Math.Min(2000,125*(1<<rejectedAttempts)));
        }
        internal JsonArray TakePage()
        {
            var entries=new JsonArray();int bytes=0,examined=0;
            if(order.Count==0||DateTime.UtcNow<retryAfter)return entries;
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
        latencyGameId=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(exe).ToUpperInvariant())));
        this.options=options??EmbeddedTranslationOptions.ForLegacy(exe);
        languagePolicy=new(settings,exe);
        singleSettings=ApiSettingsSnapshot.Copy(settings);batchSettings=ApiSettingsSnapshot.Copy(settings);
        cacheSettings=ApiSettingsSnapshot.Copy(settings);
        workspace=TranslationWorkspaceStore.For(exe,settings);workspaceDocument=workspace.Snapshot();
        RpgTranslationPrompts.Configure(settings,singleSettings,batchSettings);
    }
    internal Task StartAsync()=>Task.Run(async()=>
    {
        try{await StartCoreAsync();}
        catch(Exception startupError)
        {
            lifetime.Cancel();
            // Startup cache may already be visible. A failed prepare/apply must
            // restore it too, even before the polling worker has been created.
            RestorePending=options.StartupCache;
            if(connection.Connected)
                try{await connection.RequestAsync(new(){["op"]="translationDisable"});RestorePending=false;}
                catch(Exception restoreError)
                {
                    Enabled=false;Status=WithCacheWarning("翻译启动失败，原文恢复未确认："+SafeDiagnosticOutput.ExceptionSummary(restoreError));
                    if(restoreError is not (UnityRestoreUnconfirmedException or UnityMainThreadPausedTimeoutException or UnrealCulturePendingException))connection.Dispose();
                    throw new IOException(Status,new AggregateException(startupError,restoreError));
                }
            Enabled=false;
            Status=WithCacheWarning("翻译启动失败："+SafeDiagnosticOutput.ExceptionSummary(startupError));
            if(options.StartupCache&&!connection.Connected)Status+=" · 连接已断开，原文恢复未确认";
            throw;
        }
    });
    private Task StartCoreAsync()
    {
        if(RestorePending)throw new UnityRestoreUnconfirmedException();
        Status="正在准备已有译文…";
        var loaded=RpgTranslationCache.Normalize(RpgTranslationCache.Load(exe,cacheSettings,options.CacheScope),options.Syntax);cachePath=loaded.Path;
        dirty=loaded.NeedsSave;
        nativeGlossary=TranslationGlossaryCache.Load(cachePath);
        var invalidCache=new List<TranslationWorkspaceStore.Issue>();
        foreach(var pair in loaded.Entries)
            if(TranslationWorkspaceStore.Valid(pair.Key,pair.Value,options.Syntax,options.CatalogBoundOnly)&&(TranslationWorkspaceStore.Manual(workspaceDocument,pair.Key,options.Syntax,options.CatalogBoundOnly) is not null||AutomaticTranslationQuality.Valid(pair.Key,pair.Value,cacheSettings,workspaceDocument.Terms,options.Syntax)))RememberNative(pair.Key,pair.Value);
            else{dirty=true;invalidCache.Add(new(pair.Key,"已有自动译文存在格式残留、控制符问题或结巴词未译，暂未加载；原句再次读取后补译。",DateTime.UtcNow));}
        if(invalidCache.Count>0)workspace.RecordIssues(invalidCache);
        RefreshBoundaryAliases();
        var displayLoaded=RpgTranslationCache.Normalize(RpgTranslationCache.LoadDisplay(exe,cacheSettings,options.CacheScope),options.Syntax);displayCachePath=displayLoaded.Path;
        displayGlossary=TranslationGlossaryCache.Load(displayCachePath);
        dirty|=displayLoaded.NeedsSave;
        foreach(var pair in displayLoaded.Entries)
            if(RpgTranslationText.ValidCachedPair(pair.Key,pair.Value,options.Syntax)&&AutomaticTranslationQuality.Valid(pair.Key,pair.Value,cacheSettings,workspaceDocument.Terms,options.Syntax))displayCache[pair.Key]=pair.Value;
            else dirty=true;
        if(options.CacheScope==TranslationCacheScope.Screen)
        {
            // Read-only reuse is safe for an explicitly opened screen overlay.
            // Never save this sidecar over the native queue's files or promote
            // OCR text into native startup/writeback permissions.
            foreach(var pair in RpgTranslationCache.Load(exe,cacheSettings).Entries
                .Concat(RpgTranslationCache.LoadDisplay(exe,cacheSettings).Entries))
                if(RpgTranslationText.ValidCachedPair(pair.Key,pair.Value,options.Syntax)&&AutomaticTranslationQuality.Valid(pair.Key,pair.Value,cacheSettings,workspaceDocument.Terms,options.Syntax)&&displayCache.TryAdd(pair.Key,pair.Value))dirty=true;
        }
        TranslationMemory.Publish(exe,cacheSettings,options.CacheScope,false,cache);
        TranslationMemory.Publish(exe,cacheSettings,options.CacheScope,true,displayCache);
        foreach(var p in cache)if(!nativeGlossary.Matches(p.Key,p.Value,workspaceDocument.Terms))staleGlossary.Add(p.Key);
        foreach(var p in displayCache)if(!displayGlossary.Matches(p.Key,p.Value,workspaceDocument.Terms))staleGlossary.Add(p.Key);
        // Preparation is a game-thread operation too. Switching away after its
        // publication can leave the receipt pending for as long as the game is
        // paused. Let the worker own it, just like polling and writeback, so
        // catalogue extraction, provider jobs and cache saves can start now.
        // PollBridgeAsync still enforces prepare -> enable -> epoch before any
        // writeback, and the worker restores originals if preparation fails.
        Status="正在后台读取文本并准备译文…";
        Enabled=true;worker=Task.Run(RunAsync);
        return Task.CompletedTask;
    }
    private void AcceptPending(JsonElement result,JsonArray entries,int epoch,PendingWritebacks pending)
    {
        // Godot/Ren'Py count accepted cache rows as "applied", RPG uses
        // "accepted", and Unity explicitly returns "stored". This confirms
        // bridge cache acceptance only, never successful control rendering.
        // Legacy Unreal replies count identities. A newer explicit "stored"
        // receipt counts source pairs independently of native control writes.
        if(!options.CatalogBoundOnly||EmbeddedWritebackReceipt.Count(result,"stored") is not null)
        {
            if(EmbeddedWritebackReceipt.SourcePairsStored(result,epoch,entries.Count))pending.Accepted(entries);
            else pending.Retry(entries);
        }
        ReadWritebackCounts(result);
    }
    private void ReadWritebackCounts(JsonElement result)
    {
        latency.ReadRuntime(result);
        if(EmbeddedWritebackReceipt.Count(result,"writebacksConfirmed") is {} confirmed)writebacksConfirmed=confirmed;
        if(EmbeddedWritebackReceipt.Count(result,"writebacksFailed") is {} failed)writebacksFailed=failed;
        if(EmbeddedWritebackReceipt.Count(result,"nativePreprocessorHits") is {} projected)nativePreprocessorHits=projected+(EmbeddedWritebackReceipt.Count(result,"legacyProjectionHits")??0);
        if(EmbeddedWritebackReceipt.Count(result,"activeRenderedBindings") is {} activeRendered)activeRenderedBindings=activeRendered;
        if(EmbeddedWritebackReceipt.Count(result,"activeCachedWaitingBindings") is {} activeWaiting)activeCachedWaitingBindings=activeWaiting;
        if(EmbeddedWritebackReceipt.Count(result,"rawCaptureCount") is {} captured)nativeCapturedTexts=captured;
        if(EmbeddedWritebackReceipt.Count(result,"bindings") is {} bindings)nativeBindings=bindings;
        if(EmbeddedWritebackReceipt.Count(result,"lastVerifiedBindings") is {} verified)lastVerifiedBindings=verified;
        if(EmbeddedWritebackReceipt.Count(result,"writebackPending") is {} pending)nativeWritebackPending=pending;
        if(EmbeddedWritebackReceipt.Count(result,"fontBlockedBindings") is {} blocked)fontBlockedBindings=blocked;
        if(EmbeddedWritebackReceipt.Count(result,"revealingBindings") is {} revealing)revealingBindings=revealing;
        if(EmbeddedWritebackReceipt.Count(result,"fontsOwned") is {} fonts)fontsOwned=fonts;
        if(result.ValueKind==JsonValueKind.Object&&result.TryGetProperty("rawCaptureCountSemantics",out var semantics)&&semantics.ValueKind==JsonValueKind.String)
            nativeCaptureSemantics=SafeDiagnosticOutput.Summary(semantics.GetString()??"unknown");
        if(result.ValueKind==JsonValueKind.Object&&result.TryGetProperty("fontStatus",out var font)&&font.ValueKind==JsonValueKind.String)
            fontStatus=SafeDiagnosticOutput.Summary(font.GetString()??"unknown");
    }
    private Task<Exception?>? cacheSaveWork;
    private bool BeginCacheSave(bool finalAttempt=false)
    {
        if(cacheSaveWork is not null||!dirty||!finalAttempt&&DateTime.UtcNow<nextCacheSaveAttempt)return false;
        var nativeSnapshot=new Dictionary<string,string>(cache,StringComparer.Ordinal);
        var displaySnapshot=new Dictionary<string,string>(displayCache,StringComparer.Ordinal);
        var nativeReceipts=nativeGlossary.Snapshot(nativeSnapshot);var displayReceipts=displayGlossary.Snapshot(displaySnapshot);
        dirty=false; // Later arrivals mark dirty again; finishing this snapshot cannot erase them.
        cacheSaveWork=Persist();return true;
        async Task<Exception?> Persist()
        {
            try
            {
                await latency.MeasureAsync(TranslationLatency.Stage.CacheSave,()=>Task.Run(()=>
                {
                    PortableDataStorage.WriteJson(cachePath,nativeSnapshot);
                    PortableDataStorage.WriteJson(cachePath+".glossary.json",nativeReceipts);
                    PortableDataStorage.WriteJson(displayCachePath+".glossary.json",displayReceipts);
                    if(displayCachePath.Length>0)PortableDataStorage.WriteJson(displayCachePath,displaySnapshot);
                    TranslationMemory.Publish(exe,cacheSettings,options.CacheScope,false,nativeSnapshot);
                    TranslationMemory.Publish(exe,cacheSettings,options.CacheScope,true,displaySnapshot);
                    return true;
                })).ConfigureAwait(false);
                return null;
            }
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.Security.SecurityException){return ex;}
        }
    }
    private void CompleteCacheSave(bool finalAttempt=false)
    {
        if(cacheSaveWork?.IsCompleted!=true)return;
        var error=cacheSaveWork.GetAwaiter().GetResult();cacheSaveWork=null;
        string relativePath=Path.GetRelativePath(AppDataPaths.Root,cachePath);
        if(error is null)
        {
            cacheSaveError="";nextCacheSaveAttempt=DateTime.MinValue;
            if(cacheSaveFailures>0)AppLog.Write("translation-cache",$"stage=cache-save recovered=true path={relativePath} previousFailures={cacheSaveFailures}");
            cacheSaveFailures=0;return;
        }
        dirty=true;cacheSaveFailures=Math.Min(cacheSaveFailures+1,30);
        int retrySeconds=Math.Min(15,1<<Math.Min(cacheSaveFailures,4));
        nextCacheSaveAttempt=DateTime.UtcNow.AddSeconds(retrySeconds);
        cacheSaveError=SafeDiagnosticOutput.Summary(SafeDiagnosticOutput.ExceptionSummary(error));
        AppLog.Write("translation-cache",$"stage=cache-save final={finalAttempt} path={relativePath} hresult=0x{error.HResult:X8} retrySeconds={retrySeconds}",error);
    }
    private async Task FlushCacheAsync()
    {
        if(cacheSaveWork is not null){await cacheSaveWork;CompleteCacheSave(true);}
        if(dirty&&BeginCacheSave(true)){await cacheSaveWork!;CompleteCacheSave(true);}
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
        Task<JsonElement>? pollWork=null,applyWork=null,catalogWork=null;
        bool restoreAfterFailure=false;
        try
        {
            bool renpy=options.Syntax==EmbeddedTextSyntax.Renpy;
            bool remote=options.RemoteCatalog;bool remoteFailed=false;
            var extraction=new RpgTextCatalogResult([],0,[]);
            string extractionSummary="";
            // Catalogue I/O must never block polling or current-screen translation.
            bool hasDesktopCatalog=options.ConfiguredDesktopCatalog is not null||options.DesktopCatalog is not null;
            var language=languagePolicy;
            string extractionHint=language.CatalogHint;
            Task<RpgTextCatalogResult> ReadCatalog(string hint)
            {
                var snapshot=ApiSettingsSnapshot.Copy(cacheSettings);snapshot.CatalogLanguageHint=hint;
                return options.ConfiguredDesktopCatalog is {} configured?Task.Run(()=>configured(snapshot,lifetime.Token),lifetime.Token):
                    options.DesktopCatalog is {} reader?Task.Run(()=>reader(lifetime.Token),lifetime.Token):Task.FromResult(new RpgTextCatalogResult([],0,[]));
            }
            var extractionTask=ReadCatalog(extractionHint);
            var waitingCatalog=new HashSet<string>(StringComparer.Ordinal);
            var languageExcluded=new HashSet<string>(StringComparer.Ordinal);
            int declaredLanguageSkipped=0;
            var cachedCatalogUpdates=new Dictionary<string,string>(StringComparer.Ordinal);
            var pendingWritebacks=new PendingWritebacks();string pendingLanguage="";
            var manualWritebackSources=new HashSet<string>(StringComparer.Ordinal);
            _=extractionTask.ContinueWith(t=>{_ = t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
            bool extractionLoaded=false;
            var catalog=new HashSet<string>(StringComparer.Ordinal);
            var backlog=new Queue<string>();
            var visible=new ForegroundQueue();var active=new HashSet<string>(StringComparer.Ordinal);
            var observed=new HashSet<string>(StringComparer.Ordinal);
            var rawObserved=new HashSet<string>(StringComparer.Ordinal);
            var runtimeExcluded=new HashSet<string>(StringComparer.Ordinal);
            var runtimeNonText=new HashSet<string>(StringComparer.Ordinal);
            // Count transitions once, instead of walking a 150,000-entry
            // catalogue every 100 ms. These are cache counts, not render receipts.
            int completed=0,observedDone=0,additionalDone=0;
            var additional=new HashSet<string>(StringComparer.Ordinal);
            var readOnlySources=new HashSet<string>(StringComparer.Ordinal);
            var writableSources=new HashSet<string>(StringComparer.Ordinal);
            var aheadObserved=new HashSet<string>(StringComparer.Ordinal);
            bool RuntimeAllowed(string source)=>RpgTranslationText.HasTranslatableText(source,options.Syntax)&&language.AllowsRuntime(source);
            bool MayTranslate(string source)=>catalog.Contains(source)||(observed.Contains(source)||aheadObserved.Contains(source)||readOnlySources.Contains(source))&&RuntimeAllowed(source);
            bool HasTranslation(string source)=>TryNative(source,out _)||!writableSources.Contains(source)&&displayCache.ContainsKey(source);
            var forced=new HashSet<string>(staleGlossary,StringComparer.Ordinal);
            bool NeedsTranslation(string source)=>!HasTranslation(source)||forced.Contains(source)&&!TryManual(source,workspaceDocument,out _);
            void ReuseDisplayTranslation(string source)
            {
                if(!cache.ContainsKey(source)&&displayCache.TryGetValue(source,out var reusable)&&displayGlossary.Matches(source,reusable,workspaceDocument.Terms)
                    &&(!options.CatalogBoundOnly||UnrealTranslationConnection.ValidPair(source,reusable)))
                {
                    RememberNative(source,reusable);dirty=true;
                    nativeGlossary.Accept(source,reusable,workspaceDocument.Terms);
                    if(!TryManual(source,workspaceDocument,out _))
                    {if(catalog.Contains(source))completed++;if(observed.Contains(source))observedDone++;if(additional.Contains(source))additionalDone++;}
                }
            }
            void AddCatalog(string text,bool trusted=false)
            {
                if(!language.AllowsSelectedText(text)){languageExcluded.Add(text);return;}
                // A deferred, unproven source is not a confirmed excluded language.
                if(!language.AllowsActiveCatalog(text,trusted))return;
                if(!catalog.Contains(text))ReuseDisplayTranslation(text);
                if(!catalog.Add(text))return;
                coverage.AddCatalog(text);
                language.ObserveCatalogSource(text);
                selectedCatalogueSources.TryAdd(text,0);
                writableSources.Add(text);
                bool translated=TryNative(text,out var effective);
                if(translated){completed++;cachedCatalogUpdates[text]=effective;}
                if(NeedsTranslation(text))backlog.Enqueue(text);
                if(additional.Remove(text)&&translated)additionalDone--;
            }
            int catalogCursor=0,catalogTotal=0,catalogSkipped=0,catalogFailures=0;bool catalogDone=!remote;
            var nextCatalogPoll=DateTime.MinValue;
            string runtimeWarning="";
            var upcoming=new Queue<string>();var aheadQueued=new HashSet<string>(StringComparer.Ordinal);
            int epoch=0,applyEpoch=0,writebackPagesSinceCatalog=0;bool hasPoll=false,bridgePaused=!bridgeEnabled;
            JsonArray? applyEntries=null;
            var nextBridgeAttempt=DateTime.MinValue;var nextPollAt=DateTime.MinValue;var saved=DateTime.UtcNow;
            var nextWorkspaceRefresh=DateTime.MinValue;
            var handledRequests=new Dictionary<string,string>(StringComparer.Ordinal);
            string catalogIdentity=language.CatalogHint+"|"+language.Tracker.HasDeclaredLocale;
            string catalogLocale=language.CatalogHint;
            void RefreshActiveCatalog()
            {
                string identity=language.CatalogHint+"|"+language.Tracker.HasDeclaredLocale;
                if(identity==catalogIdentity)return;
                catalogIdentity=identity;
                if(catalogLocale!=language.CatalogHint)
                {
                    // Unseen future lines belong to the old language/session view.
                    // The current poll's ahead events are admitted after this reset.
                    aheadObserved.Clear();upcoming.Clear();aheadQueued.Clear();
                    catalogLocale=language.CatalogHint;
                }
                // Apply this in the same poll that reports a new locale, before
                // any old catalogue backlog can dispatch another provider call.
                foreach(var job in jobs.Where(j=>!j.Foreground&&!j.Sources.Any(s=>observed.Contains(s)||aheadObserved.Contains(s))))job.Cancellation.Cancel();
                catalog.Clear();selectedCatalogueSources.Clear();backlog.Clear();cachedCatalogUpdates.Clear();waitingCatalog.Clear();
                completed=0;declaredLanguageSkipped=0;languageExcluded.Clear();pendingWritebacks.Clear();
                language.ResetCatalogEvidence();coverage.ResetCatalog();
                if(hasDesktopCatalog)extractionLoaded=false;
                if(remote){catalogCursor=0;catalogDone=false;nextCatalogPoll=DateTime.MinValue;}
            }
            while(!lifetime.IsCancellationRequested&&connection.Connected)
            {
                var loopStarted=System.Diagnostics.Stopwatch.StartNew();
                var updates=new Dictionary<string,string>(StringComparer.Ordinal);
                if(DateTime.UtcNow>=nextWorkspaceRefresh)
                {
                    nextWorkspaceRefresh=DateTime.UtcNow.AddSeconds(1);
                    var previous=workspaceDocument;var latest=workspace.Snapshot();
                    System.Threading.Volatile.Write(ref workspaceDocument,latest);
                    if(previous.Revision!=latest.Revision)
                    {
                        RefreshBoundaryAliases();
                        var changedTerms=TranslationGlossary.Changed(previous.Terms,latest.Terms);
                        if(changedTerms.Length>0)
                        {
                            templates.Clear();
                            foreach(var source in catalog.Concat(observed).Concat(aheadObserved).Concat(readOnlySources).Concat(cache.Keys).Concat(displayCache.Keys).Distinct(StringComparer.Ordinal))
                            {
                                if(TryManual(source,latest,out _)||!MayTranslate(source)||!TranslationGlossary.Affected(source,changedTerms))continue;
                                if(cache.TryGetValue(source,out var currentNative)&&nativeGlossary.Matches(source,currentNative,latest.Terms)
                                    ||!cache.ContainsKey(source)&&displayCache.TryGetValue(source,out var currentDisplay)&&displayGlossary.Matches(source,currentDisplay,latest.Terms))continue;
                                forced.Add(source);failures.Remove(source);
                                // Keep the readable old result until its replacement is
                                // ready. Visible work still takes priority over the catalogue.
                                if(observed.Contains(source)||readOnlySources.Contains(source))visible.Enqueue(source);else backlog.Enqueue(source);
                            }
                        }
                        // A correction can be shown and undone between two
                        // periodic snapshots. Remember the actual writeback
                        // sources even when neither snapshot contains that edit.
                        var manualChanges=previous.Manual.Keys.Concat(latest.Manual.Keys).Concat(manualWritebackSources);
                        if(options.DisplayBoundaryWhitespace)manualChanges=manualChanges.Concat(manualChanges.Select(DisplayBoundaryAliases.Key));
                        if(options.Syntax==EmbeddedTextSyntax.Generic)
                        {
                            var keys=manualChanges.Select(DisplayLineEndingAliases.Key).ToHashSet(StringComparer.Ordinal);
                            manualChanges=manualChanges.Concat(catalog.Concat(observed).Concat(aheadObserved).Where(source=>keys.Contains(DisplayLineEndingAliases.Key(source))));
                        }
                        foreach(var source in manualChanges.Distinct(StringComparer.Ordinal))
                        {
                            if(!(catalog.Contains(source)||observed.Contains(source)||aheadObserved.Contains(source))||!MayTranslate(source))continue;
                            updates[source]=TryNative(source,out var value)?value:source;
                            if(TryManual(source,latest,out _)){failures.Remove(source);forced.Remove(source);}
                            else if(!HasTranslation(source))visible.Enqueue(source);
                        }
                        completed=catalog.Count(s=>TryNative(s,out _));observedDone=observed.Count(s=>TryNative(s,out _));additionalDone=additional.Count(s=>TryNative(s,out _));
                    }
                    foreach(var request in latest.Requests)
                    {
                        if(handledRequests.GetValueOrDefault(request.Key)==request.Value||!(catalog.Contains(request.Key)||observed.Contains(request.Key)||aheadObserved.Contains(request.Key)||readOnlySources.Contains(request.Key))
                            ||!MayTranslate(request.Key)||TryManual(request.Key,latest,out _))continue;
                        handledRequests[request.Key]=request.Value;forced.Add(request.Key);failures.Remove(request.Key);visible.Enqueue(request.Key);
                    }
                }
                if(applyWork?.IsCompleted==true)
                {
                    try{AcceptPending(await applyWork,applyEntries!,applyEpoch,pendingWritebacks);}
                    catch(Exception ex)when(ex is UnityMainThreadPausedException or UnrealCulturePendingException)
                    {pendingWritebacks.Retry(applyEntries!);bridgePaused=true;nextBridgeAttempt=DateTime.UtcNow.AddMilliseconds(500);}
                    finally{applyWork=null;applyEntries=null;}
                }
                if(options.ConfiguredDesktopCatalog is not null&&extractionHint!=language.CatalogHint&&extractionTask.IsCompleted)
                {
                    _=extractionTask.Exception;
                    extractionHint=language.CatalogHint;extractionLoaded=false;
                    extractionTask=ReadCatalog(extractionHint);
                    _=extractionTask.ContinueWith(t=>{_ = t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
                }
                if(!extractionLoaded&&extractionTask.IsCompleted&&hasPoll)
                {
                    try{extraction=await extractionTask;}
                    catch(OperationCanceledException)when(lifetime.IsCancellationRequested){throw;}
                    catch(Exception ex){extraction=new([],0,[SafeDiagnosticOutput.ExceptionSummary(ex)]){Report=new(false,[new(CatalogIssueKind.Failed,"文本目录","读取发生错误，当前结果不代表目录为空")])};}
                    extractionLoaded=true;
                    if(hasDesktopCatalog)
                    {
                        if(extraction.Report is {} report)
                        {
                            extractionSummary=report.Summary(extraction.Files,extraction.Texts.Length);
                            try
                            {
                                string identity=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(exe).ToUpperInvariant()))).ToLowerInvariant();
                                string reportPath=Path.Combine(AppDataPaths.Root,"diagnostics","catalogs",identity+".json");
                                PortableDataStorage.WriteJson(reportPath,new{version=1,scannedAtUtc=DateTime.UtcNow,files=extraction.Files,texts=extraction.Texts.Length,skippedByLanguage=extraction.SkippedByLanguage,summary=extractionSummary,report});
                                AppLog.Write("translation-catalog",extractionSummary+"；详细记录：diagnostics/catalogs/"+identity+".json");
                            }
                            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                            {AppLog.Write("translation-catalog","提取报告未能保存；当前读取和翻译继续",ex);}
                        }
                        language.SelectCatalog(extraction.Texts,extraction.SelectedLanguage);
                        declaredLanguageSkipped+=extraction.SkippedByLanguage;
                        bool trusted=extraction.SelectedLanguage.Length>0&&language.CatalogHint==GameSourceLanguageTracker.Canonical(extraction.SelectedLanguage);
                        foreach(var text in extraction.Texts)AddCatalog(text,trusted&&(extraction.TrustedSourceTexts is null||extraction.TrustedSourceTexts.Contains(text)));
                    }
                }
                // A stalled game request must not stall provider completions,
                // cache persistence or background catalogue translation. Exactly
                // one bridge operation is in flight; all queue mutations remain
                // on this worker and all replies are consumed before another send.
                if(pollWork is null&&applyWork is null&&catalogWork is null&&DateTime.UtcNow>=nextBridgeAttempt&&DateTime.UtcNow>=nextPollAt
                    &&(!hasPoll||!jobs.Any(j=>j.Work.IsCompleted)&&(pendingWritebacks.Count==0||DateTime.UtcNow>=nextPollAt.AddMilliseconds(150))))
                {pollWork=PollBridgeAsync();nextPollAt=DateTime.UtcNow.AddMilliseconds(100);}
                JsonElement poll=default;
                if(pollWork?.IsCompleted==true)
                {
                    try{poll=await pollWork;hasPoll=true;bridgePaused=false;}
                    catch(Exception ex)when(ex is UnityMainThreadPausedException or UnrealCulturePendingException)
                    {bridgePaused=true;nextBridgeAttempt=DateTime.UtcNow.AddMilliseconds(500);}
                    finally{pollWork=null;}
                }
                var currentPriority=new List<string>();
                if(poll.ValueKind==JsonValueKind.Object)
                {
                int polledEpoch=poll.GetProperty("epoch").GetInt32();
                if(epoch!=polledEpoch)
                {
                    pendingWritebacks.Clear();
                    if(remote&&epoch>=0)
                    {
                        foreach(var job in jobs.Where(j=>!j.Foreground&&!j.Sources.Any(s=>observed.Contains(s)||aheadObserved.Contains(s))))job.Cancellation.Cancel();
                        catalog.Clear();selectedCatalogueSources.Clear();backlog.Clear();waitingCatalog.Clear();cachedCatalogUpdates.Clear();completed=0;
                        declaredLanguageSkipped=0;languageExcluded.Clear();language.ResetCatalogEvidence();coverage.ResetCatalog();
                    }
                    // Readback belongs to one bridge generation. An older
                    // control/font receipt cannot describe the rebuilt scene.
                    writebacksConfirmed=writebacksFailed=nativePreprocessorHits=null;
                    activeRenderedBindings=activeCachedWaitingBindings=null;
                    nativeCapturedTexts=nativeBindings=lastVerifiedBindings=nativeWritebackPending=null;
                    fontBlockedBindings=revealingBindings=fontsOwned=null;
                    nativeCaptureSemantics=fontStatus="unknown";runtimeWarning="";
                    // A cursor belongs to one bridge generation. A rebuilt or
                    // smaller catalogue must start at zero; cached translations
                    // stay reusable and AddCatalog deduplicates earlier pages.
                    catalogCursor=0;catalogDone=!remote;catalogFailures=0;remoteFailed=false;nextCatalogPoll=DateTime.MinValue;
                    // A new bridge epoch invalidates its cache acceptance. An
                    // incremental poll may not repeat old source events.
                    foreach(string source in catalog.Concat(observed).Concat(aheadObserved))
                        if(TryNative(source,out var cached)&&MayTranslate(source))cachedCatalogUpdates[source]=cached;
                }
                epoch=polledEpoch;
                ReadWritebackCounts(poll);
                if(poll.TryGetProperty("runtimeWarning",out var runtimeNote)&&runtimeNote.ValueKind==JsonValueKind.String)
                    runtimeWarning=SafeDiagnosticOutput.Summary(runtimeNote.GetString()??"");
                var currentTexts=poll.GetProperty("texts").EnumerateArray().Where(item=>item.ValueKind==JsonValueKind.String)
                    .Select(item=>item.GetString()!).Where(text=>!string.IsNullOrWhiteSpace(text)&&text.Length<=6000&&!text.Contains('\0')).Distinct(StringComparer.Ordinal).ToArray();
                currentPriority.AddRange(currentTexts);
                foreach(string text in currentTexts)coverage.Observe(text);
                if(poll.TryGetProperty("activeTextLanguage",out var activeLocale)&&activeLocale.ValueKind==JsonValueKind.String)
                    language.ObserveGameLocale(activeLocale.GetString());
                language.ObserveRuntime(currentTexts.Where(text=>RpgTranslationText.HasTranslatableText(text,options.Syntax)));
                RefreshActiveCatalog();
                if(poll.TryGetProperty("ahead",out var ahead)&&ahead.ValueKind==JsonValueKind.Array)
                    foreach(var item in ahead.EnumerateArray())
                    {
                        if(item.ValueKind!=JsonValueKind.String)continue;
                        var text=item.GetString()!;
                        if(string.IsNullOrWhiteSpace(text)||text.Length>6000||text.Contains('\0')||!RuntimeAllowed(text))continue;
                        // Ahead events identify real writable sources, even before
                        // their controls become visible. Send existing translations
                        // through the same epoch/receipt path as newly translated ones.
                        // Keep this separate from visible-text statistics.
                        aheadObserved.Add(text);writableSources.Add(text);
                        ReuseDisplayTranslation(text);
                        if(TryNative(text,out var cachedAhead))updates[text]=cachedAhead;
                        if(NeedsTranslation(text)&&!active.Contains(text)&&!failures.ContainsKey(text)&&aheadQueued.Add(text))upcoming.Enqueue(text);
                    }
                if(extractionLoaded&&catalogDone&&waitingCatalog.Count>0)
                {
                    language.SelectCatalog(waitingCatalog);
                    foreach(var text in waitingCatalog)AddCatalog(text);
                    waitingCatalog.Clear();
                }
                foreach(var pair in cachedCatalogUpdates)updates[pair.Key]=pair.Value;
                cachedCatalogUpdates.Clear();
                foreach(var text in currentTexts)
                {
                    rawObserved.Add(text);
                    if(!RpgTranslationText.HasTranslatableText(text,options.Syntax)){runtimeNonText.Add(text);continue;}
                    if(!language.AllowsSelectedText(text)||!catalog.Contains(text)&&!language.AllowsRuntime(text)){runtimeExcluded.Add(text);continue;}
                    writableSources.Add(text);
                    // The current bridge now recognizes this exact source. Reuse
                    // its display translation through the normal validated apply
                    // path; cache text itself never grants writeback authority.
                    ReuseDisplayTranslation(text);
                    if(observed.Add(text))
                    {
                        bool translated=TryNative(text,out _);
                        if(translated)observedDone++;
                        if(!catalog.Contains(text)&&additional.Add(text)&&translated)additionalDone++;
                    }
                    latency.ObserveVisible(text,HasTranslation(text)||active.Contains(text));
                    if(TryNative(text,out var cached))updates[text]=cached;
                    if(NeedsTranslation(text)&&!active.Contains(text)&&!failures.ContainsKey(text))visible.Enqueue(text);
                }
                // Read-only text translates while the session is enabled, even
                // if its window is closed. It remains separate from writeback.
                if(poll.TryGetProperty("readOnlyTexts",out var readOnly)&&readOnly.ValueKind==JsonValueKind.Array)
                {
                    var current=readOnly.EnumerateArray().Where(v=>v.ValueKind==JsonValueKind.String)
                        .Select(v=>v.GetString()!).Where(s=>!string.IsNullOrWhiteSpace(s)&&s.Length<=6000&&!s.Contains('\0'))
                        .Distinct(StringComparer.Ordinal).Take(128).ToArray();
                    System.Threading.Volatile.Write(ref readOnlyTexts,current);
                    currentPriority.AddRange(current);
                    language.ObserveRuntime(current.Where(text=>RpgTranslationText.HasTranslatableText(text,options.Syntax)));
                    RefreshActiveCatalog();
                    foreach(var text in current)
                    {
                        readOnlySources.Add(text);
                        if(RuntimeAllowed(text))latency.ObserveVisible(text,HasTranslation(text)||active.Contains(text));
                        if(RuntimeAllowed(text)&&NeedsTranslation(text)&&!active.Contains(text)&&!failures.ContainsKey(text))visible.Enqueue(text);
                    }
                }
                else System.Threading.Volatile.Write(ref readOnlyTexts,Array.Empty<string>());
                }
                // Cached directory entries can become ready even when the game
                // is paused. Keep them pending until a real poll confirms epoch.
                foreach(var pair in cachedCatalogUpdates)updates[pair.Key]=pair.Value;
                cachedCatalogUpdates.Clear();
                var resolvedIssues=new List<string>();var newIssues=new List<TranslationWorkspaceStore.Issue>();
                var completedRequests=new Dictionary<string,string>(StringComparer.Ordinal);
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
                        // A newer retry must not be satisfied by an older request.
                        var currentDocument=workspace.Snapshot();
                        bool hasManual=TryManual(source,currentDocument,out var corrected);
                        if(hasManual)
                        {forced.Remove(source);failures.Remove(source);updates[source]=corrected;resolvedIssues.Add(source);}
                        string requestToken=currentDocument.Requests.GetValueOrDefault(source,"");
                        if(TranslationGlossary.Stamp(job.Terms,source)!=TranslationGlossary.Stamp(currentDocument.Terms,source))
                        {if(!hasManual){forced.Add(source);failures.Remove(source);visible.Enqueue(source);}continue;}
                        if(requestToken!=job.RequestTokens.GetValueOrDefault(source,"")&&requestToken.Length>0)
                        {if(!hasManual){forced.Add(source);visible.Enqueue(source);}continue;}
                        if(results.TryGetValue(source,out var text))
                        {
                            // A catalogue choice can arrive while a visible
                            // request is in flight. Do not apply another language.
                            if(!MayTranslate(source)){failures.Remove(source);continue;}
                            forced.Remove(source);resolvedIssues.Add(source);
                            if(requestToken.Length>0)completedRequests[source]=requestToken;
                            if(readOnlySources.Contains(source)&&!writableSources.Contains(source))
                            {displayCache[source]=text;displayGlossary.Accept(source,text,job.Terms);dirty=true;failures.Remove(source);continue;}
                            if(!TryNative(source,out _))
                            {if(catalog.Contains(source))completed++;if(observed.Contains(source))observedDone++;if(additional.Contains(source))additionalDone++;}
                            RememberNative(source,text);nativeGlossary.Accept(source,text,job.Terms);dirty=true;failures.Remove(source);updates[source]=TryManual(source,workspace.Snapshot(),out var manualValue)?manualValue:text;
                        }
                        else if(!hasManual&&MayTranslate(source))
                        {int count=failures.GetValueOrDefault(source).Attempts+1;
                            string reason=results is RpgTranslationBatchResult detail?detail.Failures.GetValueOrDefault(source,"unknown"):"unknown";
                            failures[source]=(count,count<4?DateTime.UtcNow.AddSeconds(Math.Min(60,2*Math.Pow(3,count-1))):DateTime.MaxValue,reason);
                            newIssues.Add(new(source,TranslationAttemptStatus.Issue(reason,count),DateTime.UtcNow));}
                    }
                }
                if(newIssues.Count>0||resolvedIssues.Count>0)workspace.RecordIssues(newIssues,resolvedIssues);
                if(completedRequests.Count>0)workspace.CompleteRequests(completedRequests);
                lifetime.Token.ThrowIfCancellationRequested();
                foreach(var pair in updates)
                    if(!readOnlySources.Contains(pair.Key)||writableSources.Contains(pair.Key))
                    {
                        bool manual=TryManual(pair.Key,workspace.Snapshot(),out var manualValue);
                        if(manual)manualWritebackSources.Add(pair.Key);else manualWritebackSources.Remove(pair.Key);
                        pendingWritebacks.Set(pair.Key,manual?manualValue:pair.Value);
                    }
                string effectiveLanguage=language.RuntimePolicyIdentity;
                if(effectiveLanguage!=pendingLanguage)
                {
                    // Scan only on a language transition, never on every poll of
                    // a large pending cache. Stored translations remain intact.
                    pendingWritebacks.ForgetAcceptance();pendingWritebacks.KeepOnly(MayTranslate);pendingLanguage=effectiveLanguage;
                }
                pendingWritebacks.Promote(currentPriority);
                bool catalogTurn=remote&&(!catalogDone||!renpy)&&DateTime.UtcNow>=nextCatalogPoll&&writebackPagesSinceCatalog>=3;
                if(hasPoll&&!bridgePaused&&!catalogTurn&&pollWork is null&&applyWork is null&&catalogWork is null)
                {
                    var entries=pendingWritebacks.TakePage();
                    if(entries.Count>0)
                    {
                        applyEntries=entries;applyEpoch=epoch;
                        writebackPagesSinceCatalog++;
                        applyWork=ScheduledRequest(new(){["op"]="translationApply",["epoch"]=epoch,["entries"]=entries});
                    }
                }
                if(retryRequested){retryRequested=false;foreach(var key in failures.Keys.ToArray())failures[key]=(0,DateTime.UtcNow,failures[key].Code);}
                foreach(var pair in failures.ToArray())
                    if(pair.Value.Due<=DateTime.UtcNow&&!active.Contains(pair.Key)&&MayTranslate(pair.Key))
                    {
                        // A failed catalogue batch must not consume the slot
                        // reserved for text actually observed in the game.
                        if(observed.Contains(pair.Key)||readOnlySources.Contains(pair.Key))visible.Enqueue(pair.Key);
                        else if(aheadQueued.Add(pair.Key))upcoming.Enqueue(pair.Key);
                    }
                // Keep retry backoff above; promoting an existing entry cannot
                // bypass it, cancel an active batch, or create a duplicate job.
                visible.Promote(currentPriority);
                // Two bounded background requests leave two slots for visible text. No duplicate or cancelled batch replay.
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
                        if(MayTranslate(source)&&NeedsTranslation(source)&&(!failures.TryGetValue(source,out var failure)||failure.Due<=DateTime.UtcNow)&&active.Add(source)){work.Add(source);foreground=true;if(failures.ContainsKey(source))break;}
                    }
                    if(work.Count==0)
                    {
                        if(jobs.Count(j=>!j.Foreground)>=2)break;
                        int chars=0;
                        while((upcoming.Count>0||backlog.Count>0)&&work.Count<8&&chars<2400)
                        {
                            var next=upcoming.Count>0?upcoming.Peek():backlog.Peek();
                            bool singleRetry=failures.TryGetValue(next,out var retry)&&TranslationAttemptStatus.RetryIndividually(retry.Code);
                            if(singleRetry&&work.Count>0)break;
                            var source=upcoming.Count>0?upcoming.Dequeue():backlog.Dequeue();aheadQueued.Remove(source);
                            if(!MayTranslate(source)||!NeedsTranslation(source)||failures.TryGetValue(source,out var failure)&&failure.Due>DateTime.UtcNow||!active.Add(source))continue;
                            work.Add(source);chars+=source.Length;if(singleRetry)break;
                        }
                    }
                    if(work.Count==0)break;
                    var sources=work.ToArray();latency.Dispatched(sources);var jobCancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    var doc=workspace.Snapshot();var prompts=TranslationWorkspaceStore.WithTerms(doc,sources,singleSettings,batchSettings);
                    jobs.Add(new(sources,RpgTranslationBatch.Translate(sources,prompts.Single,prompts.Batch,(prompt,settings,token)=>latency.MeasureAsync(TranslationLatency.Stage.Provider,()=>TranslationFlightPool.Shared.Request(translate,prompt,settings,token)),jobCancellation.Token,renpy,options.Syntax,doc.Terms,templates),foreground,jobCancellation,sources.ToDictionary(s=>s,s=>doc.Requests.GetValueOrDefault(s,""),StringComparer.Ordinal),doc.Terms));
                }
                // Dispatch current-screen work and apply ready results before
                // asking for another directory page. Pace pages without starving
                // them while the foreground is active; an in-flight IPC request
                // itself is still serialized by the connection.
                if(remote&&hasPoll&&!bridgePaused&&(!catalogDone||!renpy)&&DateTime.UtcNow>=nextCatalogPoll&&pollWork is null&&applyWork is null&&catalogWork is null)
                {
                    writebackPagesSinceCatalog=0;
                    catalogWork=ScheduledRequest(new(){["op"]="translationCatalog",["cursor"]=catalogCursor});
                }
                if(catalogWork?.IsCompleted==true)
                {
                    try
                    {
                    var page=await catalogWork;
                    int total=page.GetProperty("total").GetInt32(),skipped=page.GetProperty("skipped").GetInt32();
                    int next=page.GetProperty("next").GetInt32();
                    bool scanning=page.TryGetProperty("scanning",out var scanningValue)&&scanningValue.GetBoolean()
                        ||page.TryGetProperty("done",out var doneValue)&&!doneValue.GetBoolean();
                    var rows=page.GetProperty("texts");
                    if(total<0||skipped<0||next<0||next>total||next<catalogCursor||next==catalogCursor&&next<total&&!scanning
                        ||rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength()>2048
                        ||rows.EnumerateArray().Any(item=>item.ValueKind!=JsonValueKind.String))
                        throw new IOException("游戏文本目录没有正确继续返回数据。");
                    // Validate a whole page before letting it affect language,
                    // translation scheduling or native writeback eligibility.
                    catalogTotal=total;catalogSkipped=skipped;
                    string selected=page.TryGetProperty("selectedLanguage",out var selectedValue)&&selectedValue.ValueKind==JsonValueKind.String?selectedValue.GetString()??"":"";
                    if(selected.Length>0)
                    {
                        language.SelectCatalog([],selected);
                    }
                    // A mixed-origin catalogue can filter identified locale
                    // branches without assigning one locale to every source.
                    if(page.TryGetProperty("skippedByLanguage",out var filteredValue)&&filteredValue.TryGetInt32(out int filtered))declaredLanguageSkipped=Math.Max(declaredLanguageSkipped,filtered);
                    foreach(var item in rows.EnumerateArray())
                    {
                        var text=item.GetString()!;
                        if(!language.CatalogResolved)waitingCatalog.Add(text);
                        AddCatalog(text,selected.Length>0&&GameSourceLanguageTracker.Canonical(selected)==language.CatalogHint);
                    }
                    catalogCursor=next;catalogDone=next>=catalogTotal&&!scanning;
                    if(remoteFailed)AppLog.Write("translation-catalog","session="+latencySession+" game="+latencyGameId+" remote-read-resumed cursor="+catalogCursor);
                    catalogFailures=0;remoteFailed=false;
                    // Drain pages already produced by the game promptly. Empty
                    // pages while its bounded scan runs retain a slower cadence.
                    // Polls and ready screen writebacks still get their turn above.
                    nextCatalogPoll=DateTime.UtcNow.AddMilliseconds(catalogDone?2000:next<total?25:scanning?100:250);
                    }
                    catch(OperationCanceledException)when(lifetime.IsCancellationRequested){throw;}
                    catch(Exception ex)when(ex is UnityMainThreadPausedException or UnrealCulturePendingException)
                    {bridgePaused=true;nextBridgeAttempt=DateTime.UtcNow.AddMilliseconds(500);}
                    catch(Exception ex)when(connection.Connected)
                    {
                        catalogFailures=Math.Min(catalogFailures+1,6);
                        int delay=Math.Min(30,1<<(catalogFailures-1));
                        if(!remoteFailed)AppLog.Write("translation-catalog","session="+latencySession+" game="+latencyGameId+" remote-read-pending cursor="+catalogCursor+" error="+ex.GetType().Name);
                        remoteFailed=true;catalogDone=false;nextCatalogPoll=DateTime.UtcNow.AddSeconds(delay);
                    }
                    finally{catalogWork=null;}
                }
                int failed=failures.Count;System.Threading.Volatile.Write(ref failedCount,failed);
                System.Threading.Volatile.Write(ref snapshot,new(language.Label,language.EvidenceSource,catalog.Count,completed,
                    rawObserved.Count,observed.Count,runtimeExcluded.Count,observedDone,additional.Count,additionalDone,
                    ReadOnlyTexts.Length,active.Count,pendingWritebacks.AcceptedCount,pendingWritebacks.Count+(applyEntries?.Count??0),writebacksConfirmed,writebacksFailed,
                    runtimeNonText.Count,nativeCapturedTexts,nativeBindings,lastVerifiedBindings,nativeWritebackPending,fontBlockedBindings,revealingBindings,
                    nativeCaptureSemantics,fontStatus,fontsOwned,latency.Snapshot(),
                    CatalogReading:!catalogDone||!extractionLoaded,
                    CatalogIncomplete:hasDesktopCatalog&&extractionLoaded&&(!extraction.ReadCompleted||extraction.Unreadable.Length>0||extraction.Report?.Completed==false),
                    CatalogIssueCount:extraction.Report?.Issues.Length??extraction.Unreadable.Length,
                    CatalogExcludedTexts:languageExcluded.Count+declaredLanguageSkipped,
                    RetryScheduledTexts:failures.Count(p=>p.Value.Due!=DateTime.MaxValue),RetryStoppedTexts:failures.Count(p=>p.Value.Due==DateTime.MaxValue),
                    BridgePaused:bridgePaused,CatalogRetryPending:remoteFailed,ActiveRenderedBindings:activeRenderedBindings,ActiveCachedWaitingBindings:activeCachedWaitingBindings,
                    FailureCodes:failures.Values.Select(f=>f.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),RuntimeNote:runtimeWarning));
                Status=$"原文：{language.Label} · 目录文本 {catalog.Count} 条（已有译文 {completed} 条）";
                if(languageExcluded.Count+declaredLanguageSkipped>0)Status+=$" · 目录排除其他语言 {languageExcluded.Count+declaredLanguageSkipped} 条";
                if(rawObserved.Count>0)Status+=$" · 运行时收到 {rawObserved.Count} 条（可译 {observed.Count} 条，已有译文 {observedDone} 条）";
                else Status+=ReadOnlyTexts.Length>0?" · 运行时已读到文字，尚不能内嵌替换":" · 运行时尚未收到文本";
                if(runtimeExcluded.Count>0)Status+=$" · 运行时按源语言筛选 {runtimeExcluded.Count} 条";
                if(runtimeNonText.Count>0)Status+=$" · 仅变量或控制符 {runtimeNonText.Count} 条";
                if(additional.Count>0)Status+=$" · 目录外文本已有译文 {additionalDone} / {additional.Count} 条";
                if(ReadOnlyTexts.Length>0)Status+=options.CacheScope==TranslationCacheScope.Screen?" · 屏幕译文准备中":
                    readOnlyWindowVisible?" · 实时补译中，译文窗已开启":" · 已读取文字，游戏内替换尚未确认；可手动开启屏幕翻译";
                Status+=$" · 处理中 {active.Count} 条";
                int awaitingCache=pendingWritebacks.Count+(applyEntries?.Count??0);
                if(awaitingCache>0)Status+=$" · 待桥接缓存确认 {awaitingCache} 条";
                if(bridgePaused)Status+=" · 游戏内文字刷新暂未应答，后台翻译继续；恢复后自动补写";
                if(activeRenderedBindings is {} rendered)Status+=$" · 活跃文字控件已取用译文 {rendered} 个";
                else if(nativePreprocessorHits is >0)Status+=" · 本次运行曾取用译文（不代表当前页面全部显示）";
                if(activeCachedWaitingBindings is >0)Status+=$" · 有缓存但尚未确认显示 {activeCachedWaitingBindings} 个控件";
                if(fontBlockedBindings is >0)Status+=$" · {fontBlockedBindings} 处文字的字体暂不能显示译文";
                if(writebacksConfirmed is {} nativeConfirmed&&(nativeConfirmed>0||nativePreprocessorHits is not >0))Status+=$" · 累计文字更新 {nativeConfirmed} 次（含重复刷新，非翻译请求数）";
                else if(nativePreprocessorHits is not >0&&pendingWritebacks.AcceptedCount>0)Status+=$" · 桥接缓存已接收 {pendingWritebacks.AcceptedCount} 条（控件写回未确认）";
                if(writebacksFailed is >0)Status+=$" · 原生写回失败 {writebacksFailed} 次";
                if(!catalogDone||!extractionLoaded)Status+=" · 正在读取文本目录";
                if(catalogSkipped>0)Status+=options.CatalogBoundOnly?$" · {catalogSkipped} 项保留原有译文或暂未提取":options.Syntax==EmbeddedTextSyntax.Generic?$" · 提取时跳过 {catalogSkipped} 项（资源或过长文本），运行时继续补译":$" · {catalogSkipped} 条过长文本未提取";
                if(failed>0)Status+=TranslationAttemptStatus.Summary(failures.Count(p=>p.Value.Due!=DateTime.MaxValue),failures.Count(p=>p.Value.Due==DateTime.MaxValue));
                else if(!bridgePaused&&runtimeWarning.Length==0&&catalogDone&&extractionLoaded&&completed==catalog.Count&&additionalDone==additional.Count&&backlog.Count==0&&visible.Count==0&&jobs.Count==0&&awaitingCache==0)
                    Status+=catalog.Count==0&&rawObserved.Count==0&&ReadOnlyTexts.Length==0?" · 等待可读取的游戏文字":
                        writebacksConfirmed==0&&nativePreprocessorHits is not >0&&rawObserved.Count>0?" · 译文已准备，游戏内写回尚未确认":
                        " · 当前已捕获文字的译文缓存已准备";
                if(runtimeWarning.Length>0)Status+=" · "+runtimeWarning;
                if(extractionSummary.Length>0)Status+=" · "+extractionSummary;
                else if(extraction.Unreadable.Length>0)Status+=$" · {extraction.Unreadable.Length} 处未能完整读取（不能据此判断没有文字）";
                if(remoteFailed)Status+=" · 游戏文本目录读取暂未完成，将自动重试；当前文字继续处理";
                CompleteCacheSave();
                if(dirty&&((DateTime.UtcNow-saved).TotalSeconds>=5||jobs.Count==0))
                {if(BeginCacheSave())saved=DateTime.UtcNow;}
                if(DateTime.UtcNow>=nextLatencyLog)
                {
                    nextLatencyLog=DateTime.UtcNow.AddSeconds(10);
                    AppLog.Write("translation-latency","session="+latencySession+" game="+latencyGameId+" timings="+JsonSerializer.Serialize(latency.Snapshot()));
                    AppLog.Write("translation-coverage","session="+latencySession+" game="+latencyGameId+" coverage="+JsonSerializer.Serialize(coverage.Snapshot(extractionLoaded&&catalogDone&&!remoteFailed&&(!hasDesktopCatalog||extraction.ReadCompleted&&extraction.Unreadable.Length==0&&extraction.Report?.Completed!=false))));
                }
                Status=WithCacheWarning(Status);
                // Wake when a provider or bridge reply is ready, while retaining
                // the 100 ms polling cadence and a small anti-spin loop floor.
                var signals=new List<Task>();
                if(pollWork is not null)signals.Add(pollWork);if(applyWork is not null)signals.Add(applyWork);if(catalogWork is not null)signals.Add(catalogWork);
                signals.AddRange(jobs.Select(j=>(Task)j.Work));if(!extractionLoaded)signals.Add(extractionTask);if(cacheSaveWork is not null)signals.Add(cacheSaveWork);
                int wakeAfter=100;
                if(remote&&!catalogDone&&!bridgePaused&&pollWork is null&&applyWork is null&&catalogWork is null)
                    wakeAfter=Math.Clamp((int)Math.Ceiling((nextCatalogPoll-DateTime.UtcNow).TotalMilliseconds),10,100);
                signals.Add(Task.Delay(wakeAfter,lifetime.Token));
                await Task.WhenAny(signals).WaitAsync(lifetime.Token);
                int pause=10-(int)loopStarted.ElapsedMilliseconds;if(pause>0)await Task.Delay(pause,lifetime.Token);
            }
            if(!lifetime.IsCancellationRequested)Status="翻译连接已断开，已有译文缓存保留。";
        }
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
        catch(Exception ex){restoreAfterFailure=true;Status="翻译连接已停止："+SafeDiagnosticOutput.ExceptionSummary(ex);}
        finally
        {
            lifetime.Cancel();
            foreach(var request in new[]{pollWork,applyWork,catalogWork})
                if(request is not null)_=request.ContinueWith(t=>{_ = t.Exception;},CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            foreach(var job in jobs)_=job.Work.ContinueWith(t=>{_ = t.Exception;job.Cancellation.Dispose();},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            // Keep Enabled until restoration finishes so a new UI session cannot
            // overtake this session's final Disable on a still-connected bridge.
            if(restoreAfterFailure&&connection.Connected)
                try{RestorePending=options.StartupCache;await connection.RequestAsync(new(){["op"]="translationDisable"});RestorePending=false;}
                catch(Exception restoreError)
                {
                    Status+=" · 原文恢复未确认："+SafeDiagnosticOutput.ExceptionSummary(restoreError);
                    if(restoreError is not (UnityRestoreUnconfirmedException or UnityMainThreadPausedTimeoutException or UnrealCulturePendingException))connection.Dispose();
                }
            else if(restoreAfterFailure&&options.StartupCache){RestorePending=true;Status+=" · 连接已断开，原文恢复未确认";}
            await FlushCacheAsync();
            Status=WithCacheWarning(Status);
            Enabled=false;
        }
    }
    internal async Task StopAsync()
    {
        lifetime.Cancel();
        string? restoreFailure=null;
        RestorePending=options.StartupCache;
        // Restoring originals must not wait for a provider which ignores cancellation.
        try
        {
            if(connection.Connected)await connection.RequestAsync(new(){["op"]="translationDisable"});
            else if(options.StartupCache)throw new UnityRestoreUnconfirmedException();
            RestorePending=false;
        }
        catch(Exception restoreError)
        {
            restoreFailure=SafeDiagnosticOutput.ExceptionSummary(restoreError);
            if(restoreError is not (UnityRestoreUnconfirmedException or UnityMainThreadPausedTimeoutException or UnrealCulturePendingException))connection.Dispose();
            throw;
        }
        finally
        {
            if(worker is not null)await worker;else await FlushCacheAsync();
            Enabled=false;Status=WithCacheWarning(restoreFailure is null?"翻译关闭":"翻译任务已停止，原文恢复未确认："+restoreFailure);
        }
    }
    public void Dispose(){lifetime.Cancel();Enabled=false;}
}
