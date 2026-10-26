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
    private readonly WolfDisplayTemplates wolfTemplates=new();
    private int wolfTemplateRevision;
    private readonly object wolfManualGate=new();
    private long wolfManualRevision=-1;
    private void RegisterWolfManualTemplates(TranslationWorkspaceStore.Document document)
    {
        if(options.Syntax!=EmbeddedTextSyntax.Wolf||System.Threading.Volatile.Read(ref wolfManualRevision)==document.Revision)return;
        lock(wolfManualGate)
        {
            if(wolfManualRevision==document.Revision)return;
            foreach(var pair in document.Manual)
                if(TranslationWorkspaceStore.Valid(pair.Key,pair.Value,EmbeddedTextSyntax.Wolf))wolfTemplates.Register(pair.Key);
            System.Threading.Volatile.Write(ref wolfManualRevision,document.Revision);
        }
    }
    private readonly GameDialogueContexts dialogueContexts=new();
    private GameDialogueContextCache dialogueCache=new();
    private GameDialogueRoleCache dialogueRoleCache=new();
    // Existing unresolved sources retain their legacy retry/evidence path for
    // this session. Later role failures must not downgrade accepted attribution.
    private HashSet<string> roleBlockedSources=new(StringComparer.Ordinal);
    private TranslationGlossaryCache nativeGlossary=new(),displayGlossary=new();
    private readonly HashSet<string> staleGlossary=new(StringComparer.Ordinal);
    private readonly HashSet<string> provisional=new(StringComparer.Ordinal);
    private System.Collections.Concurrent.ConcurrentDictionary<string,string> boundaryAliases=new(StringComparer.Ordinal);
    private readonly DisplayLineEndingAliases lineAliases=new();
    private readonly DisplayTemplateAliases displayTemplates=new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,byte> derivedDisplaySources=new(StringComparer.Ordinal);
    private int displayTemplateRevision;
    private bool UsesDisplayTemplates=>options.Syntax==EmbeddedTextSyntax.Generic&&!options.CatalogBoundOnly;
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
        RegisterWolfManualTemplates(workspaceDocument);
        if(UsesDisplayTemplates)
            foreach(string source in workspaceDocument.Manual.Keys)displayTemplates.Add(source);
        if(!options.DisplayBoundaryWhitespace)return;
        System.Threading.Volatile.Write(ref boundaryAliases,new(DisplayBoundaryAliases.Index(cache.Keys),StringComparer.Ordinal));
    }
    private void RememberNative(string source,string text)
    {
        cache[source]=text;
        if(options.Syntax==EmbeddedTextSyntax.Wolf&&(source.Contains('\\')||WolfLanguageBranches.IsTemplate(source)||ClockDisplayTemplate.TrySource(source.Trim(),out _)))
        {wolfTemplates.Register(source);System.Threading.Interlocked.Increment(ref wolfTemplateRevision);}
        if(options.Syntax==EmbeddedTextSyntax.Generic)lineAliases.Add(source);
        if(UsesDisplayTemplates&&displayTemplates.Add(source))
            System.Threading.Interlocked.Increment(ref displayTemplateRevision);
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
        bool found=TryNativeUnwrapped(source,out text,latest);
        if(found&&options.Syntax==EmbeddedTextSyntax.Wolf)text=WolfDisplayWrapping.Fit(source,text);
        return found;
    }
    private bool TryNativeUnwrapped(string source,out string text,TranslationWorkspaceStore.Document? latest=null)
    {
        var document=latest??System.Threading.Volatile.Read(ref workspaceDocument);
        if(TryManual(source,document,out text))return true;
        if(dialogueContexts.TryGet(source,out var context))
        {
            if(context.Roles is null)return dialogueCache.TryGet(source,context,cacheSettings,document.Terms,out text,options.Syntax);
            if(dialogueRoleCache.TryGet(source,context,cacheSettings,document.Terms,out text,options.Syntax))return true;
            if(GameDialogueContext.Create(context.Previous) is {} legacyContext
                &&dialogueCache.TryGet(source,legacyContext,cacheSettings,document.Terms,out text,options.Syntax))return true;
            // Role agreement is an enhancement. Until a validated variant is
            // available, retain the usable ordinary translation and its receipt.
            // Never relabel that fallback as a successful role translation.
        }
        string key=options.DisplayBoundaryWhitespace?DisplayBoundaryAliases.Key(source):source;
        text=cache.GetValueOrDefault(source,"");
        if(text.Length==0&&options.DisplayBoundaryWhitespace&&System.Threading.Volatile.Read(ref boundaryAliases).TryGetValue(key,out var original)
            &&cache.TryGetValue(original,out var reused)&&nativeGlossary.Matches(original,reused,document.Terms))text=DisplayBoundaryAliases.Key(reused);
        if(text.Length==0&&options.Syntax==EmbeddedTextSyntax.Generic&&lineAliases.TryGet(source,
            original=>cache.TryGetValue(original,out var value)&&nativeGlossary.Matches(original,value,document.Terms)?value:null,out var lines)
            &&TranslationWorkspaceStore.Valid(source,lines,options.Syntax,options.CatalogBoundOnly))text=lines;
        if(text.Length==0&&UsesDisplayTemplates&&!document.Requests.ContainsKey(source))
        {
            // Resolve against today's correction, glossary and retry state.
            // Do not persist an expansion: its owner may be corrected later.
            string? ReadTemplate(string owner)
            {
                if(TranslationWorkspaceStore.Manual(document,owner,options.Syntax) is {} manual)return manual;
                if(document.Requests.ContainsKey(owner)||!cache.TryGetValue(owner,out var automatic)
                    ||!nativeGlossary.Matches(owner,automatic,document.Terms)
                    ||!AutomaticTranslationQuality.Valid(owner,automatic,cacheSettings,document.Terms,options.Syntax))return null;
                return automatic;
            }
            if(displayTemplates.TryGet(source,ReadTemplate,out var expanded)
                &&TranslationWorkspaceStore.Valid(source,expanded,options.Syntax))
            {text=expanded;derivedDisplaySources[source]=0;}
        }
        if(text.Length==0&&options.Syntax==EmbeddedTextSyntax.Wolf)
        {
            RegisterWolfManualTemplates(document);
            WolfDisplayTemplates.Value? ResolvePart(string owner)
            {
                if(TryManual(owner,document,out var manual))return new WolfDisplayTemplates.Value(manual,true);
                // A pending retry or changed glossary cannot lend authority to
                // a newly expanded value. The original template stays intact.
                return !document.Requests.ContainsKey(owner)&&cache.TryGetValue(owner,out var automatic)
                    &&nativeGlossary.Matches(owner,automatic,document.Terms)?new WolfDisplayTemplates.Value(automatic):null;
            }
            text=wolfTemplates.Find(source,ResolvePart);
            if(text.Length==0)text=WolfDisplayComposition.Find(source,part=>ResolvePart(part)?.Text,
                part=>ResolvePart(part)?.Text??wolfTemplates.Find(part,ResolvePart));
            if(text.Length>0&&!RpgTranslationText.ValidCachedPair(source,text,EmbeddedTextSyntax.Wolf))text="";
        }
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
    private bool HasRoleContext(string source)=>dialogueContexts.TryGet(source,out var context)&&context.Roles is not null;
    internal string Lookup(string source)=>!languagePolicy.AllowsSelectedText(source)||!selectedCatalogueSources.ContainsKey(source)&&!languagePolicy.AllowsRuntime(source)?"":TryNative(source,out var text,workspace.Snapshot())?text:displayCache.GetValueOrDefault(source,"");
    private readonly Dictionary<string,(int Attempts,DateTime Due,string Code)> failures=new(StringComparer.Ordinal);
    private bool dirty,roleDirty,savingNativeCache,savingRoleCache;
    private string cacheSaveError="";
    private int cacheSaveFailures;
    private DateTime nextCacheSaveAttempt=DateTime.MinValue;
    private volatile bool retryRequested;
    private readonly TranslationProviderPause providerPause=new();
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
    private bool bridgePrepared,bridgeEnabled,bridgePrimed,bridgeExactPrime;
    private int bridgePrepareEpoch;
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
            if(options.Syntax==EmbeddedTextSyntax.Renpy&&GameDialogueRoles.Applies(cacheSettings))prepare["speakerAttributionContract"]=GameDialogueRoles.Contract;
            if(options.StartupCache)prepare["preserveStartup"]=true;
            var receipt=await ScheduledRequest(prepare);
            bridgeExactPrime=options.CacheScope==TranslationCacheScope.Native&&!options.CatalogBoundOnly&&
                receipt.ValueKind==JsonValueKind.Object&&receipt.TryGetProperty("exactStartupPrime",out var capability)&&capability.ValueKind==JsonValueKind.True;
            if(bridgeExactPrime)
            {
                if(!receipt.TryGetProperty("epoch",out var generation)||generation.ValueKind!=JsonValueKind.Number||!generation.TryGetInt32(out bridgePrepareEpoch))
                    throw new IOException("游戏未接受翻译请求。");
            }
            bridgePrepared=true;
        }
        if(bridgeExactPrime&&!bridgePrimed)
        {
            // Only a bridge advertising a separate exact map may receive unseen
            // native rows. A bounded snapshot never grants template authority.
            var budget=System.Diagnostics.Stopwatch.StartNew();
            for(int revisionAttempt=0;revisionAttempt<3&&!bridgePrimed;revisionAttempt++)
            {
                var document=workspace.Snapshot(true);
                var exact=cache.Where(p=>nativeGlossary.Matches(p.Key,p.Value,document.Terms)).Take(100000)
                    .ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
                foreach(var pair in document.Manual)
                    if(TranslationWorkspaceStore.Valid(pair.Key,pair.Value,options.Syntax)&&
                        (exact.ContainsKey(pair.Key)||exact.Count<100000))exact[pair.Key]=pair.Value;
                exact=EmbeddedStartupCache.Select(exact.Where(p=>languagePolicy.AllowsSelectedText(p.Key))
                    .ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal),cacheSettings.SourceLanguage);
                JsonArray page=[];int bytes=0,pages=0;long totalBytes=0;bool replace=true;
                async Task SendPage()
                {
                    var receipt=await ScheduledRequest(new(){["op"]="translationApply",["epoch"]=bridgePrepareEpoch,
                        ["prime"]=true,["exactOnly"]=true,["replaceExact"]=replace,["entries"]=page});
                    if(EmbeddedWritebackReceipt.Count(receipt,"epoch")!=bridgePrepareEpoch||
                        !EmbeddedWritebackReceipt.SourcePairsStored(receipt,bridgePrepareEpoch,page,options.Syntax))
                        throw new IOException("游戏未接受翻译请求。");
                    page=[];bytes=0;replace=false;pages++;
                }
                foreach(var pair in exact)
                {
                    var row=new JsonObject{["source"]=pair.Key,["text"]=pair.Value};
                    int size=Encoding.UTF8.GetByteCount(row.ToJsonString());
                    if(size>=60000)continue; // Same single-entry bound as live writeback.
                    if(totalBytes+size>31L*1024*1024||pages>=128||pages>0&&budget.ElapsedMilliseconds>=5000)break;
                    if(page.Count>0&&(page.Count>=128||bytes+size>42000))await SendPage();
                    page.Add(row);bytes+=size;totalBytes+=size;
                }
                // Empty snapshots deliberately revoke a launch-time snapshot too.
                await SendPage();
                if(workspace.Snapshot(true).Revision==document.Revision)bridgePrimed=true;
                else if(budget.ElapsedMilliseconds>=5000)throw new IOException("游戏未接受翻译请求。");
                // A changed correction or glossary restarts from a replacement
                // page; no row from the old snapshot survives into enable.
            }
            if(!bridgePrimed)throw new IOException("游戏未接受翻译请求。");
        }
        if(!bridgeEnabled)
        {
            var enable=new JsonObject{["op"]="translationEnable",["prepared"]=true};
            if(bridgeExactPrime)enable["epoch"]=bridgePrepareEpoch;
            var receipt=await ScheduledRequest(enable);
            if(bridgeExactPrime&&(EmbeddedWritebackReceipt.Count(receipt,"epoch")!=bridgePrepareEpoch||
                receipt.TryGetProperty("stale",out var stale)&&stale.ValueKind!=JsonValueKind.False))
                throw new IOException("游戏未接受翻译请求。");
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
    internal string CacheWarning=>(dirty||roleDirty)&&cacheSaveError.Length>0?"缓存保存失败，本次新增译文尚未持久保存："+cacheSaveError:"";
    internal int FailedCount=>System.Threading.Volatile.Read(ref failedCount);
    private EmbeddedTranslationQueueSnapshot snapshot=new();
    internal EmbeddedTranslationQueueSnapshot Snapshot=>System.Threading.Volatile.Read(ref snapshot);
    private int? writebacksConfirmed,writebacksFailed;
    private int? nativePreprocessorHits;
    private int? activeRenderedBindings,activeCachedWaitingBindings;
    private int? nativeCapturedTexts,nativeBindings,lastVerifiedBindings,nativeWritebackPending,fontBlockedBindings,revealingBindings,fontsOwned;
    private string nativeCaptureSemantics="unknown",fontStatus="unknown";
    private GameFontRuntimeStatus fontRuntimeStatus=new();
    internal void RetryFailures()=>retryRequested=true;
    private sealed record Job(string[] Sources,Task<Dictionary<string,string>> Work,bool Foreground,CancellationTokenSource Cancellation,Dictionary<string,string> RequestTokens,IReadOnlyDictionary<string,string> Terms,RpgTranslationBatchProgress Progress,IReadOnlyDictionary<string,GameDialogueContext> Contexts)
    {
        // These fields belong exclusively to RunAsync, never to provider callbacks.
        internal readonly HashSet<string> Delivered=new(StringComparer.Ordinal);
        internal bool EarlyConsumed;
        internal IEnumerable<string> PendingSources=>Sources.Where(source=>!Delivered.Contains(source));
        internal bool HasResults=>Work.IsCompleted||!EarlyConsumed&&Progress.Accepted.IsCompletedSuccessfully;
    }
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
        dialogueContexts.Clear();dialogueCache=GameDialogueContextCache.Load(cachePath+".dialogue-context-v1.json");
        dialogueRoleCache=GameDialogueRoles.Applies(cacheSettings)?GameDialogueRoleCache.Load(cachePath+".dialogue-roles-v2.json"):new();
        dirty=loaded.NeedsSave;
        nativeGlossary=TranslationGlossaryCache.Load(cachePath,loaded.Receipts);
        var invalidCache=new List<TranslationWorkspaceStore.Issue>();
        var previousIssues=workspace.Issues().GroupBy(p=>p.Source,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.Last(),StringComparer.Ordinal);
        roleBlockedSources=new(previousIssues.Keys,StringComparer.Ordinal);
        var revalidatedIssues=new Dictionary<string,TranslationWorkspaceStore.Issue>(StringComparer.Ordinal);
        void RevalidatePreviousIssue(string source,string value,string reason,TranslationGlossaryCache receipts)
        {
            if(reason.Length!=0||workspaceDocument.Manual.ContainsKey(source)||workspaceDocument.Requests.ContainsKey(source)
                ||!receipts.Matches(source,value,workspaceDocument.Terms))return;
            if(previousIssues.TryGetValue(source,out var old)&&old.Soft&&old.TextHash==TranslationAttemptStatus.TextHash(value)
                &&old.TermStamp==TranslationGlossary.Stamp(workspaceDocument.Terms,source))revalidatedIssues[source]=old;
        }
        void RestoreProvisional(string source,string value,string reason)
        {
            provisional.Add(source);
            var old=previousIssues.GetValueOrDefault(source);
            bool same=old is not null&&old.Soft&&old.TextHash==TranslationAttemptStatus.TextHash(value)&&old.TermStamp==TranslationGlossary.Stamp(workspaceDocument.Terms,source);
            int attempts=same?Math.Clamp(old!.Attempts,0,4):0;
            if(attempts>0)failures[source]=(attempts,attempts>=4?DateTime.MaxValue:DateTime.UtcNow,reason);
            invalidCache.Add(same?old!:TranslationAttemptStatus.Evidence(source,reason,attempts,value,workspaceDocument.Terms,true));
        }
        foreach(var pair in loaded.Entries)
            if(TranslationWorkspaceStore.Valid(pair.Key,pair.Value,options.Syntax,options.CatalogBoundOnly)&&(TranslationWorkspaceStore.Manual(workspaceDocument,pair.Key,options.Syntax,options.CatalogBoundOnly) is not null||AutomaticTranslationQuality.Displayable(pair.Key,pair.Value,cacheSettings,workspaceDocument.Terms,options.Syntax)))
            {
                RememberNative(pair.Key,pair.Value);
                string reason=AutomaticTranslationQuality.Reason(pair.Key,pair.Value,cacheSettings,workspaceDocument.Terms,options.Syntax);
                if(AutomaticTranslationQuality.Soft(reason)&&TranslationWorkspaceStore.Manual(workspaceDocument,pair.Key,options.Syntax,options.CatalogBoundOnly) is null)
                    RestoreProvisional(pair.Key,pair.Value,reason);
                else RevalidatePreviousIssue(pair.Key,pair.Value,reason,nativeGlossary);
            }
            else{dirty=true;invalidCache.Add(new(pair.Key,"已有自动译文未通过检查："+TranslationAttemptStatus.Reason(AutomaticTranslationQuality.Reason(pair.Key,pair.Value,cacheSettings,workspaceDocument.Terms,options.Syntax))+"；读取原句后将自动补译。",DateTime.UtcNow));}
        RefreshBoundaryAliases();
        var displayLoaded=RpgTranslationCache.Normalize(RpgTranslationCache.LoadDisplay(exe,cacheSettings,options.CacheScope),options.Syntax);displayCachePath=displayLoaded.Path;
        displayGlossary=TranslationGlossaryCache.Load(displayCachePath,displayLoaded.Receipts);
        dirty|=displayLoaded.NeedsSave;
        foreach(var pair in displayLoaded.Entries)
            if(RpgTranslationText.ValidCachedPair(pair.Key,pair.Value,options.Syntax)&&AutomaticTranslationQuality.Displayable(pair.Key,pair.Value,cacheSettings,workspaceDocument.Terms,options.Syntax))
            {
                displayCache[pair.Key]=pair.Value;
                string reason=AutomaticTranslationQuality.Reason(pair.Key,pair.Value,cacheSettings,workspaceDocument.Terms,options.Syntax);
                if(AutomaticTranslationQuality.Soft(reason)&&
                    !(cache.TryGetValue(pair.Key,out var nativeValue)&&nativeGlossary.Matches(pair.Key,nativeValue,workspaceDocument.Terms)&&
                      AutomaticTranslationQuality.Valid(pair.Key,nativeValue,cacheSettings,workspaceDocument.Terms,options.Syntax)))
                    RestoreProvisional(pair.Key,pair.Value,reason);
                else if(!cache.TryGetValue(pair.Key,out var preferred)||!nativeGlossary.Matches(pair.Key,preferred,workspaceDocument.Terms)
                    ||!AutomaticTranslationQuality.Valid(pair.Key,preferred,cacheSettings,workspaceDocument.Terms,options.Syntax))
                    RevalidatePreviousIssue(pair.Key,pair.Value,reason,displayGlossary);
            }
            else dirty=true;
        if(invalidCache.Count>0||revalidatedIssues.Count>0)
            workspace.RecordRevalidatedIssues(invalidCache,revalidatedIssues.Values,workspaceDocument.Revision);
        if(options.CacheScope==TranslationCacheScope.Screen)
        {
            // Read-only reuse is safe for an explicitly opened screen overlay.
            // Never save this sidecar over the native queue's files or promote
            // OCR text into native startup/writeback permissions.
            foreach(var nativeLoaded in new[]{RpgTranslationCache.Load(exe,cacheSettings),RpgTranslationCache.LoadDisplay(exe,cacheSettings)})
            {
                var receipts=TranslationGlossaryCache.Load(nativeLoaded.Path,nativeLoaded.Receipts);
                foreach(var pair in nativeLoaded.Entries)
                    if(receipts.Matches(pair.Key,pair.Value,workspaceDocument.Terms)&&RpgTranslationText.ValidCachedPair(pair.Key,pair.Value,options.Syntax)
                        &&AutomaticTranslationQuality.Valid(pair.Key,pair.Value,cacheSettings,workspaceDocument.Terms,options.Syntax)&&displayCache.TryAdd(pair.Key,pair.Value))
                    {displayGlossary.Accept(pair.Key,pair.Value,workspaceDocument.Terms);dirty=true;}
            }
        }
        TranslationMemory.Publish(exe,cacheSettings,options.CacheScope,false,cache,nativeGlossary.ForSources(cache));
        TranslationMemory.Publish(exe,cacheSettings,options.CacheScope,true,displayCache,displayGlossary.ForSources(displayCache));
        foreach(var p in cache)if(!nativeGlossary.Matches(p.Key,p.Value,workspaceDocument.Terms))staleGlossary.Add(p.Key);
        foreach(var p in displayCache)if(!displayGlossary.Matches(p.Key,p.Value,workspaceDocument.Terms)&&
            !(cache.TryGetValue(p.Key,out var nativeValue)&&nativeGlossary.Matches(p.Key,nativeValue,workspaceDocument.Terms)&&
              AutomaticTranslationQuality.Valid(p.Key,nativeValue,cacheSettings,workspaceDocument.Terms,options.Syntax)))staleGlossary.Add(p.Key);
        foreach(var p in dialogueCache.ForEditor(cacheSettings,workspaceDocument.Terms,options.Syntax))
            if(!AutomaticTranslationQuality.Valid(p.Key,p.Value,cacheSettings,workspaceDocument.Terms,options.Syntax))provisional.Add(p.Key);
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
            if(EmbeddedWritebackReceipt.SourcePairsStored(result,epoch,entries,options.Syntax,options.CatalogBoundOnly))pending.Accepted(entries);
            else pending.Retry(entries);
        }
        ReadWritebackCounts(result);
    }
    private void ReadWritebackCounts(JsonElement result)
    {
        latency.ReadRuntime(result);
        fontRuntimeStatus=fontRuntimeStatus.Read(result);
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
    private Task originIndexWork=Task.CompletedTask;
    private bool remoteOriginsIndexed;
    private void QueueOriginIndex(RpgTextCatalogResult value)
    {
        var settings=ApiSettingsSnapshot.Copy(cacheSettings);
        originIndexWork=originIndexWork.ContinueWith(previous=>
        {
            _=previous.Exception;
            TranslationOriginIndex.TrySave(exe,settings,value);
        },CancellationToken.None,TaskContinuationOptions.None,TaskScheduler.Default);
    }
    private Task<Exception?>? cacheSaveWork;
    private bool BeginCacheSave(bool finalAttempt=false)
    {
        if(cacheSaveWork is not null||!(dirty||roleDirty)||!finalAttempt&&DateTime.UtcNow<nextCacheSaveAttempt)return false;
        bool saveNative=dirty,saveRoles=roleDirty;
        savingNativeCache=saveNative;savingRoleCache=saveRoles;
        var nativeSnapshot=new Dictionary<string,string>(cache,StringComparer.Ordinal);
        var dialogueSnapshot=dialogueCache.Snapshot();
        var roleSnapshot=dialogueRoleCache.Snapshot();
        var displaySnapshot=new Dictionary<string,string>(displayCache,StringComparer.Ordinal);
        var nativeReceipts=nativeGlossary.ForSources(nativeSnapshot);var displayReceipts=displayGlossary.ForSources(displaySnapshot);
        dirty=false;roleDirty=false; // Later arrivals remain independently dirty.
        cacheSaveWork=Persist();return true;
        async Task<Exception?> Persist()
        {
            try
            {
                await latency.MeasureAsync(TranslationLatency.Stage.CacheSave,()=>Task.Run(()=>
                {
                    if(saveNative)
                    {
                        TranslationStorage.WriteJson(cachePath,nativeSnapshot);
                        if(options.Syntax==EmbeddedTextSyntax.Renpy)TranslationStorage.WriteJson(cachePath+".dialogue-context-v1.json",dialogueSnapshot);
                        TranslationStorage.WriteJson(cachePath+".glossary.json",nativeReceipts);
                        TranslationStorage.WriteJson(displayCachePath+".glossary.json",displayReceipts);
                        if(displayCachePath.Length>0)TranslationStorage.WriteJson(displayCachePath,displaySnapshot);
                        TranslationMemory.Publish(exe,cacheSettings,options.CacheScope,false,nativeSnapshot,nativeReceipts);
                        TranslationMemory.Publish(exe,cacheSettings,options.CacheScope,true,displaySnapshot,displayReceipts);
                    }
                    if(saveRoles&&options.Syntax==EmbeddedTextSyntax.Renpy&&roleSnapshot.Count>0)
                        TranslationStorage.WriteJson(cachePath+".dialogue-roles-v2.json",roleSnapshot,32*1024*1024);
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
        dirty|=savingNativeCache;roleDirty|=savingRoleCache;cacheSaveFailures=Math.Min(cacheSaveFailures+1,30);
        int retrySeconds=Math.Min(15,1<<Math.Min(cacheSaveFailures,4));
        nextCacheSaveAttempt=DateTime.UtcNow.AddSeconds(retrySeconds);
        cacheSaveError=SafeDiagnosticOutput.Summary(SafeDiagnosticOutput.ExceptionSummary(error));
        AppLog.Write("translation-cache",$"stage=cache-save final={finalAttempt} path={relativePath} hresult=0x{error.HResult:X8} retrySeconds={retrySeconds}",error);
    }
    private async Task FlushCacheAsync()
    {
        if(cacheSaveWork is not null){await cacheSaveWork;CompleteCacheSave(true);}
        if((dirty||roleDirty)&&BeginCacheSave(true)){await cacheSaveWork!;CompleteCacheSave(true);}
        try{await originIndexWork;}catch(Exception ex){AppLog.Write("translation-origins","来源索引未能保存",ex);}
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
        Task? terminologyWork=null;
        GameTerminology.SourceOwnership? terminologyOwnership=null;
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
            bool supplementStarted=false;
            var waitingCatalog=new HashSet<string>(StringComparer.Ordinal);
            var languageExcluded=new HashSet<string>(StringComparer.Ordinal);
            int declaredLanguageSkipped=0;
            var cachedCatalogUpdates=new Dictionary<string,string>(StringComparer.Ordinal);
            var pendingWritebacks=new PendingWritebacks();string pendingLanguage="";
            var manualWritebackSources=new HashSet<string>(StringComparer.Ordinal);
            _=extractionTask.ContinueWith(t=>{_ = t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
            bool extractionLoaded=false;
            var catalog=new HashSet<string>(StringComparer.Ordinal);
            var discoveredCatalog=new HashSet<string>(StringComparer.Ordinal);
            var deferredCatalog=new HashSet<string>(StringComparer.Ordinal);
            var remoteActiveSources=new HashSet<string>(StringComparer.Ordinal);
            var remoteSelectedResourceSources=new HashSet<string>(StringComparer.Ordinal);
            var remoteOrigins=new RemoteCatalogOrigins();
            var backlog=new ForegroundQueue();
            var visible=new ForegroundQueue();var active=new HashSet<string>(StringComparer.Ordinal);
            var observed=new HashSet<string>(StringComparer.Ordinal);
            int deliveredWolfTemplateRevision=0;
            int deliveredDisplayTemplateRevision=0;
            var suppressedWolfFragments=new HashSet<string>(StringComparer.Ordinal);
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
            bool RuntimeFragmentAllowed(string source)
            {
                if(options.Syntax!=EmbeddedTextSyntax.Wolf||source.Trim(' ','\r','\n','\t').EnumerateRunes().Take(2).Count()!=1||catalog.Contains(source))return true;
                var document=workspace.Snapshot();
                if(TryManual(source,document,out _))return true;
                bool Current(string text,TranslationGlossaryCache receipts)=>RpgTranslationText.ValidCachedPair(source,text,EmbeddedTextSyntax.Wolf)
                    &&receipts.Matches(source,text,document.Terms)&&AutomaticTranslationQuality.Valid(source,text,cacheSettings,document.Terms,EmbeddedTextSyntax.Wolf);
                if(cache.TryGetValue(source,out var exact)&&Current(exact,nativeGlossary)
                    ||displayCache.TryGetValue(source,out var displayExact)&&Current(displayExact,displayGlossary))return true;
                // Some WOLF games draw a sentence as individual glyph calls.
                // Guessing a translation for each glyph wastes provider requests
                // and can corrupt the sentence. Structured catalogue labels and
                // explicitly saved exact translations keep their own authority.
                if(suppressedWolfFragments.Count<256&&suppressedWolfFragments.Add(source))
                    AppLog.Write("translation-fragment","session="+latencySession+" game="+latencyGameId+" engine=wolf action=keep-original reason=unproven-single-scalar provider=skipped");
                return false;
            }
            bool RuntimeAllowed(string source)=>RpgTranslationText.HasTranslatableText(source,options.Syntax)&&language.AllowsRuntime(source)&&RuntimeFragmentAllowed(source);
            bool MayTranslate(string source)=>catalog.Contains(source)||(observed.Contains(source)||aheadObserved.Contains(source)||readOnlySources.Contains(source))&&RuntimeAllowed(source);
            bool HasTranslation(string source)=>TryNative(source,out _)||(!writableSources.Contains(source)||HasRoleContext(source))&&displayCache.ContainsKey(source);
            bool Complete(string source)=>TryManual(source,workspaceDocument,out _)||!provisional.Contains(source)&&TryNative(source,out _);
            var forced=new HashSet<string>(staleGlossary.Concat(provisional),StringComparer.Ordinal);
            void TrackReusedQuality(string source,string text)
            {
                if(TryManual(source,workspaceDocument,out _)||provisional.Contains(source))return;
                if(AutomaticTranslationQuality.Soft(AutomaticTranslationQuality.Reason(source,text,cacheSettings,workspaceDocument.Terms,options.Syntax)))
                {provisional.Add(source);forced.Add(source);}
            }
            bool NeedsTranslation(string source)=>RuntimeFragmentAllowed(source)&&(!HasTranslation(source)||forced.Contains(source)&&!TryManual(source,workspaceDocument,out _));
            void ReuseDisplayTranslation(string source)
            {
                if(dialogueContexts.TryGet(source,out _))return;
                if(TryManual(source,workspaceDocument,out _))return;
                if(displayCache.TryGetValue(source,out var reusable)&&displayGlossary.Matches(source,reusable,workspaceDocument.Terms)
                    &&(!cache.TryGetValue(source,out var current)||!AutomaticTranslationQuality.Valid(source,current,cacheSettings,workspaceDocument.Terms,options.Syntax)&&
                       AutomaticTranslationQuality.Valid(source,reusable,cacheSettings,workspaceDocument.Terms,options.Syntax))
                    &&(!options.CatalogBoundOnly||UnrealTranslationConnection.ValidPair(source,reusable)))
                {
                    bool wasComplete=Complete(source);
                    RememberNative(source,reusable);dirty=true;
                    nativeGlossary.Accept(source,reusable,workspaceDocument.Terms);
                    if(AutomaticTranslationQuality.Valid(source,reusable,cacheSettings,workspaceDocument.Terms,options.Syntax))
                    {provisional.Remove(source);forced.Remove(source);failures.Remove(source);}
                    if(!wasComplete&&Complete(source))
                    {if(catalog.Contains(source))completed++;if(observed.Contains(source))observedDone++;if(additional.Contains(source))additionalDone++;}
                }
            }
            void ReadDialogueContexts(JsonElement receipt)
            {
                if(!renpy)return;
                // Metadata readiness never blocks the ordinary translation
                // queue. No readiness state is retained across bridge epochs.
                // A failure already recorded for an ordinary request keeps its
                // original retry/evidence path even if attribution arrives later.
                foreach(string source in failures.Keys)if(!HasRoleContext(source))roleBlockedSources.Add(source);
                foreach(string source in dialogueContexts.Read(receipt,cacheSettings,roleBlockedSources))
                {
                    if(TryNative(source,out var ready))cachedCatalogUpdates[source]=ready;
                    else if(HasRoleContext(source)&&displayCache.TryGetValue(source,out var display))cachedCatalogUpdates[source]=display;
                    // Preserve the displayed ordinary result while optional
                    // enhancement is pending; never replace it with source text.
                    if(!NeedsTranslation(source))continue;
                    if(!MayTranslate(source)||active.Contains(source))continue;
                    if(observed.Contains(source))visible.Enqueue(source);
                    else backlog.Enqueue(source);
                }
            }
            void AddCatalog(string text,bool trusted=false,bool unlocalizedSource=false,bool selectedResource=false)
            {
                unlocalizedSource |= remoteActiveSources.Contains(text);
                selectedResource |= remoteSelectedResourceSources.Contains(text);
                discoveredCatalog.Add(text);
                if(extraction.SkippedSourceTexts?.Contains(text)==true)return;
                if(!RpgTranslationText.HasTranslatableText(text,options.Syntax))return;
                bool scriptAlreadyTarget=language.ScriptCatalogConfirmed(extraction.ScriptSourceLanguage)&&
                    TranslationLanguagePolicy.Canonical(cacheSettings.TargetLanguage)==extraction.ScriptSourceLanguage;
                if(!language.AllowsSelectedText(text)||scriptAlreadyTarget){languageExcluded.Add(text);return;}
                // A deferred, unproven source is not a confirmed excluded language.
                if(!unlocalizedSource&&!selectedResource&&!language.AllowsActiveCatalog(text,trusted)&&!language.AllowsScriptCatalog(text,extraction.ScriptSourceLanguage))
                {if(!observed.Contains(text)&&!aheadObserved.Contains(text))deferredCatalog.Add(text);return;}
                deferredCatalog.Remove(text);
                if(!catalog.Contains(text))ReuseDisplayTranslation(text);
                if(!catalog.Add(text))return;
                coverage.AddCatalog(text);
                language.ObserveCatalogSource(text);
                selectedCatalogueSources.TryAdd(text,0);
                writableSources.Add(text);
                bool translated=TryNative(text,out var effective);
                if(translated)TrackReusedQuality(text,effective);
                if(translated){if(!provisional.Contains(text))completed++;cachedCatalogUpdates[text]=effective;}
                if(NeedsTranslation(text))backlog.Enqueue(text);
                if(additional.Remove(text)&&translated&&!provisional.Contains(text))additionalDone--;
            }
            int catalogCursor=0,catalogTotal=0,catalogSkipped=0,catalogFailures=0;bool catalogDone=!remote;
            RemoteCatalogScanState remoteScan=new(!remote,0);
            var nextCatalogPoll=DateTime.MinValue;
            string runtimeWarning="";
            var upcoming=new Queue<string>();var aheadQueued=new HashSet<string>(StringComparer.Ordinal);
            int epoch=0,applyEpoch=0,writebackPagesSinceCatalog=0;bool hasPoll=false,bridgePaused=!bridgeEnabled;
            JsonArray? applyEntries=null;
            var nextBridgeAttempt=DateTime.MinValue;var nextPollAt=DateTime.MinValue;var saved=DateTime.UtcNow;
            var nextWorkspaceRefresh=DateTime.MinValue;
            var fastPollUntil=DateTime.MinValue;
            var handledRequests=new Dictionary<string,string>(StringComparer.Ordinal);
            string CatalogIdentity()=>language.CatalogHint+"|"+language.Tracker.HasDeclaredLocale+"|"+language.ScriptCatalogConfirmed(extraction.ScriptSourceLanguage);
            string catalogIdentity=CatalogIdentity();
            string catalogLocale=language.CatalogHint;
            var terminology=GameTerminology.For(workspace);bool terminologyDirty=true;
            terminologyOwnership=terminology.OwnSources(cacheSettings);
            int pendingNames=0;DateTime nextNameCount=DateTime.MinValue;
            var terminologyObserved=new HashSet<string>(StringComparer.Ordinal);
            void ObserveNames(IEnumerable<string> values,IEnumerable<CatalogTextOrigin>? origins=null)
            {
                var fresh=values.Where(terminologyObserved.Add).ToArray();
                if(fresh.Length==0&&origins is null)return;
                terminologyOwnership.Add(fresh);
                terminology.Observe(fresh,origins);terminologyDirty=true;
            }
            void ReuseVerifiedNames()
            {
                var document=workspace.Snapshot();var verified=new Dictionary<string,string>(StringComparer.Ordinal);
                foreach(string source in terminology.PendingSources(cacheSettings))
                {
                    if(active.Contains(source)||forced.Contains(source)||provisional.Contains(source)||failures.ContainsKey(source)
                        ||document.Manual.ContainsKey(source)||document.Requests.ContainsKey(source)||dialogueContexts.TryGet(source,out _))continue;
                    if(cache.TryGetValue(source,out var text)&&nativeGlossary.Matches(source,text,document.Terms)
                        &&AutomaticTranslationQuality.Valid(source,text,cacheSettings,document.Terms,options.Syntax))verified[source]=text;
                    else if(!cache.ContainsKey(source)&&displayCache.TryGetValue(source,out text)&&displayGlossary.Matches(source,text,document.Terms)
                        &&AutomaticTranslationQuality.Valid(source,text,cacheSettings,document.Terms,options.Syntax))verified[source]=text;
                }
                terminology.ReuseVerifiedTranslations(cacheSettings,verified,document,options.Syntax,lifetime.Token);
                // A name-only job can start before a later poll exposes that name
                // as a standalone source. Reuse its current-profile result, not
                // arbitrary global glossary mappings or contextual dialogue.
                document=workspace.Snapshot();
                foreach(var pair in terminology.PreparedTranslations(cacheSettings,MayTranslate,document))
                {
                    string source=pair.Key;
                    if(!MayTranslate(source)||cache.ContainsKey(source)||displayCache.ContainsKey(source)||active.Contains(source)||forced.Contains(source)
                        ||provisional.Contains(source)||failures.ContainsKey(source)||dialogueContexts.TryGet(source,out _)
                        ||!AutomaticTranslationQuality.Valid(source,pair.Value,cacheSettings,document.Terms,options.Syntax))continue;
                    if(readOnlySources.Contains(source)&&!writableSources.Contains(source))
                    {displayCache[source]=pair.Value;displayGlossary.Accept(source,pair.Value,document.Terms);dirty=true;continue;}
                    RememberNative(source,pair.Value);nativeGlossary.Accept(source,pair.Value,document.Terms);dirty=true;
                    if(catalog.Contains(source))completed++;if(observed.Contains(source))observedDone++;if(additional.Contains(source))additionalDone++;
                    cachedCatalogUpdates[source]=pair.Value;
                }
            }
            void RefreshActiveCatalog()
            {
                string identity=CatalogIdentity();
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
                foreach(var job in jobs.Where(j=>!j.Foreground&&!j.PendingSources.Any(s=>observed.Contains(s)||aheadObserved.Contains(s))))job.Cancellation.Cancel();
                catalog.Clear();selectedCatalogueSources.Clear();backlog.Clear();cachedCatalogUpdates.Clear();waitingCatalog.Clear();
                discoveredCatalog.Clear();deferredCatalog.Clear();remoteActiveSources.Clear();remoteSelectedResourceSources.Clear();
                remoteOrigins=new();
                completed=0;declaredLanguageSkipped=0;languageExcluded.Clear();pendingWritebacks.Clear();
                language.ResetCatalogEvidence();coverage.ResetCatalog();
                if(hasDesktopCatalog)extractionLoaded=false;
                if(remote){catalogCursor=0;remoteOriginsIndexed=false;catalogDone=false;remoteScan=new(false,0);nextCatalogPoll=DateTime.MinValue;}
            }
            while(!lifetime.IsCancellationRequested&&connection.Connected)
            {
                ReuseVerifiedNames();
                if(DateTime.UtcNow>=nextNameCount){pendingNames=terminology.PendingCount(cacheSettings);nextNameCount=DateTime.UtcNow.AddSeconds(1);}
                if(terminologyWork?.IsCompleted==true)
                {
                    try{await terminologyWork;}catch(OperationCanceledException)when(lifetime.IsCancellationRequested){throw;}
                    catch(Exception ex){AppLog.Write("game-terminology","统一译名准备暂未完成；当前对白继续处理",ex);}
                    terminologyWork=null;
                }
                if(terminologyWork is null&&hasPoll&&extractionLoaded&&catalogDone&&(terminologyDirty||DateTime.UtcNow>=nextWorkspaceRefresh&&terminology.HasPending(cacheSettings)))
                {
                    terminologyDirty=false;
                    terminologyWork=Task.Run(()=>terminology.PrepareAsync(cacheSettings,(prompt,profile,ct)=>TranslationFlightPool.Shared.Request(translate,prompt,profile,ct),lifetime.Token),lifetime.Token);
                }
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
                            foreach(string source in dialogueContexts.ChangedTerms(previous.Terms,latest.Terms))
                            {
                                if(TryManual(source,latest,out _)||!MayTranslate(source))continue;
                                forced.Add(source);failures.Remove(source);
                                provisional.Add(source);
                                if(observed.Contains(source))visible.Enqueue(source);else backlog.Enqueue(source);
                            }
                            foreach(var source in catalog.Concat(observed).Concat(aheadObserved).Concat(readOnlySources).Concat(cache.Keys).Concat(displayCache.Keys).Distinct(StringComparer.Ordinal))
                            {
                                if(TryManual(source,latest,out _)||!MayTranslate(source)||!TranslationGlossary.Affected(source,changedTerms))continue;
                                // Adopting an exact verified name does not change
                                // that same cached result. Only this automatic
                                // addition may advance its existing receipt.
                                if(!active.Contains(source)&&!forced.Contains(source)&&!provisional.Contains(source)&&!failures.ContainsKey(source)
                                    &&!dialogueContexts.TryGet(source,out _))
                                {
                                    if(cache.TryGetValue(source,out var exact)&&nativeGlossary.Matches(source,exact,previous.Terms)
                                        &&terminology.CanRebaseExact(source,exact,cacheSettings,previous,latest,options.Syntax))
                                    {nativeGlossary.Accept(source,exact,latest.Terms);dirty=true;continue;}
                                    if(!cache.ContainsKey(source)&&displayCache.TryGetValue(source,out exact)&&displayGlossary.Matches(source,exact,previous.Terms)
                                        &&terminology.CanRebaseExact(source,exact,cacheSettings,previous,latest,options.Syntax))
                                    {displayGlossary.Accept(source,exact,latest.Terms);dirty=true;continue;}
                                }
                                if(cache.TryGetValue(source,out var currentNative)&&nativeGlossary.Matches(source,currentNative,latest.Terms)
                                    ||!cache.ContainsKey(source)&&displayCache.TryGetValue(source,out var currentDisplay)&&displayGlossary.Matches(source,currentDisplay,latest.Terms))continue;
                                forced.Add(source);failures.Remove(source);
                                provisional.Add(source);
                                // Keep the readable old result until its replacement is
                                // ready. Visible work still takes priority over the catalogue.
                                if(observed.Contains(source)||readOnlySources.Contains(source))visible.Enqueue(source);else backlog.Enqueue(source);
                            }
                        }
                        // A correction can be shown and undone between two
                        // periodic snapshots. Remember the actual writeback
                        // sources even when neither snapshot contains that edit.
                        var manualChanges=previous.Manual.Keys.Concat(latest.Manual.Keys).Concat(manualWritebackSources);
                        if(options.Syntax==EmbeddedTextSyntax.Wolf&&previous.Manual.Keys.Concat(latest.Manual.Keys).Any(s=>s.Contains('\\')||WolfLanguageBranches.IsTemplate(s)||ClockDisplayTemplate.TrySource(s.Trim(),out _)))
                            manualChanges=manualChanges.Concat(observed).Concat(aheadObserved);
                        if(options.DisplayBoundaryWhitespace)manualChanges=manualChanges.Concat(manualChanges.Select(DisplayBoundaryAliases.Key));
                        if(options.Syntax==EmbeddedTextSyntax.Generic)
                        {
                            var keys=manualChanges.Select(DisplayLineEndingAliases.Key).ToHashSet(StringComparer.Ordinal);
                            manualChanges=manualChanges.Concat(catalog.Concat(observed).Concat(aheadObserved).Where(source=>keys.Contains(DisplayLineEndingAliases.Key(source))));
                            if(UsesDisplayTemplates)manualChanges=manualChanges.Concat(observed).Concat(aheadObserved);
                        }
                        foreach(var source in manualChanges.Distinct(StringComparer.Ordinal))
                        {
                            if(!(catalog.Contains(source)||observed.Contains(source)||aheadObserved.Contains(source))||!MayTranslate(source))continue;
                            updates[source]=TryNative(source,out var value)?value:source;
                            if(TryManual(source,latest,out _)){failures.Remove(source);forced.Remove(source);provisional.Remove(source);}
                            else if(!HasTranslation(source))visible.Enqueue(source);
                        }
                        completed=catalog.Count(Complete);observedDone=observed.Count(Complete);additionalDone=additional.Count(Complete);
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
                    catch(Exception ex)when(ex is UnityMainThreadPausedException or UnrealCulturePendingException or EmbeddedTransportBusyException)
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
                    // Admission below uses this extraction's evidence already;
                    // do not cancel/requeue its first batches on the next poll.
                    catalogIdentity=CatalogIdentity();
                    if(hasDesktopCatalog)
                    {
                        QueueOriginIndex(extraction);
                        if(extraction.Report is {} report)
                        {
                            extractionSummary=report.Summary(extraction.Files,extraction.Texts.Length);
                            try
                            {
                                string identity=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(exe).ToUpperInvariant()))).ToLowerInvariant();
                                string reportPath=Path.Combine(AppDataPaths.Root,"diagnostics","catalogs",identity+".json");
                                TranslationStorage.WriteJson(reportPath,new{version=1,scannedAtUtc=DateTime.UtcNow,files=extraction.Files,texts=extraction.Texts.Length,skippedByLanguage=extraction.SkippedByLanguage,skippedPlaceholders=extraction.SkippedSourceTexts?.Count??0,summary=extractionSummary,report});
                                AppLog.Write("translation-catalog",extractionSummary+"；详细记录：diagnostics/catalogs/"+identity+".json");
                            }
                            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                            {AppLog.Write("translation-catalog","提取报告未能保存；当前读取和翻译继续",ex);}
                        }
                        language.SelectCatalog(extraction.Texts,extraction.SelectedLanguage);
                        declaredLanguageSkipped+=extraction.SkippedByLanguage;
                        bool trusted=extraction.SelectedLanguage.Length>0&&language.CatalogHint==GameSourceLanguageTracker.CanonicalLocale(extraction.SelectedLanguage);
                        foreach(var text in extraction.Texts.OrderBy(t=>extraction.PrioritySourceTexts?.Contains(t)==true?0:1))AddCatalog(text,trusted&&(extraction.TrustedSourceTexts is null||extraction.TrustedSourceTexts.Contains(text)),extraction.UnlocalizedSourceTexts?.Contains(text)==true,language.AllowsSelectedResource(text,extraction.SelectedLanguage,extraction.TrustedSourceTexts?.Contains(text)==true));
                        ObserveNames(catalog,extraction.Origins.Where(o=>catalog.Contains(o.Text)));
                        if(extraction.PrioritySourceTexts is {} uiSources)backlog.Promote(uiSources.ToArray());
                    }
                    if(!supplementStarted&&options.SupplementalDesktopCatalog is {} supplement)
                    {
                        supplementStarted=true;extractionTask=Task.Run(()=>supplement(lifetime.Token),lifetime.Token);extractionLoaded=false;
                        _=extractionTask.ContinueWith(t=>{_ = t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
                    }
                }
                // A stalled game request must not stall provider completions,
                // cache persistence or background catalogue translation. Exactly
                // one bridge operation is in flight; all queue mutations remain
                // on this worker and all replies are consumed before another send.
                if(pollWork is null&&applyWork is null&&catalogWork is null&&DateTime.UtcNow>=nextBridgeAttempt&&DateTime.UtcNow>=nextPollAt
                    &&(!hasPoll||!jobs.Any(j=>j.HasResults)&&(pendingWritebacks.Count==0||DateTime.UtcNow>=nextPollAt.AddMilliseconds(150))))
                {pollWork=PollBridgeAsync();nextPollAt=DateTime.UtcNow.AddMilliseconds(!hasPoll||DateTime.UtcNow<fastPollUntil||jobs.Any(j=>j.Foreground)?50:100);}
                JsonElement poll=default;
                if(pollWork?.IsCompleted==true)
                {
                    try{poll=await pollWork;hasPoll=true;bridgePaused=false;}
                    catch(Exception ex)when(ex is UnityMainThreadPausedException or UnrealCulturePendingException or EmbeddedTransportBusyException)
                    {bridgePaused=true;nextBridgeAttempt=DateTime.UtcNow.AddMilliseconds(500);}
                    finally{pollWork=null;}
                }
                var currentPriority=new List<string>();
                if(poll.ValueKind==JsonValueKind.Object)
                {
                int polledEpoch=poll.GetProperty("epoch").GetInt32();
                if(epoch!=polledEpoch)
                {
                    dialogueContexts.Clear();
                    pendingWritebacks.Clear();
                    if(remote&&epoch>=0)
                    {
                        foreach(var job in jobs.Where(j=>!j.Foreground&&!j.PendingSources.Any(s=>observed.Contains(s)||aheadObserved.Contains(s))))job.Cancellation.Cancel();
                        catalog.Clear();selectedCatalogueSources.Clear();backlog.Clear();waitingCatalog.Clear();cachedCatalogUpdates.Clear();completed=0;
                        discoveredCatalog.Clear();deferredCatalog.Clear();remoteActiveSources.Clear();remoteSelectedResourceSources.Clear();
                        remoteOrigins=new();
                        declaredLanguageSkipped=0;languageExcluded.Clear();language.ResetCatalogEvidence();coverage.ResetCatalog();
                    }
                    // Readback belongs to one bridge generation. An older
                    // control/font receipt cannot describe the rebuilt scene.
                    writebacksConfirmed=writebacksFailed=nativePreprocessorHits=null;
                    activeRenderedBindings=activeCachedWaitingBindings=null;
                    nativeCapturedTexts=nativeBindings=lastVerifiedBindings=nativeWritebackPending=null;
                    fontBlockedBindings=revealingBindings=fontsOwned=null;
                    fontRuntimeStatus=new();
                    nativeCaptureSemantics=fontStatus="unknown";runtimeWarning="";
                    // A cursor belongs to one bridge generation. A rebuilt or
                    // smaller catalogue must start at zero; cached translations
                    // stay reusable and AddCatalog deduplicates earlier pages.
                    catalogCursor=0;remoteOriginsIndexed=false;catalogDone=!remote;remoteScan=new(!remote,0);catalogFailures=0;remoteFailed=false;nextCatalogPoll=DateTime.MinValue;
                    // A new bridge epoch invalidates its cache acceptance. An
                    // incremental poll may not repeat old source events.
                    foreach(string source in catalog.Concat(observed).Concat(aheadObserved))
                        if(TryNative(source,out var cached)&&MayTranslate(source))cachedCatalogUpdates[source]=cached;
                }
                epoch=polledEpoch;
                ReadDialogueContexts(poll);
                ReadWritebackCounts(poll);
                if(poll.TryGetProperty("runtimeWarning",out var runtimeNote)&&runtimeNote.ValueKind==JsonValueKind.String)
                    runtimeWarning=SafeDiagnosticOutput.Summary(runtimeNote.GetString()??"");
                var currentTexts=poll.GetProperty("texts").EnumerateArray().Where(item=>item.ValueKind==JsonValueKind.String)
                    .Select(item=>item.GetString()!).Where(text=>!string.IsNullOrWhiteSpace(text)&&text.Length<=6000&&!text.Contains('\0')).Distinct(StringComparer.Ordinal).ToArray();
                if(currentTexts.Length>0)fastPollUntil=DateTime.UtcNow.AddMilliseconds(750);
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
                        if(aheadObserved.Add(text))ObserveNames([text]);writableSources.Add(text);deferredCatalog.Remove(text);
                        ReuseDisplayTranslation(text);
                        if(TryNative(text,out var cachedAhead))updates[text]=cachedAhead;
                        if(NeedsTranslation(text)&&!active.Contains(text)&&!failures.ContainsKey(text)&&aheadQueued.Add(text))upcoming.Enqueue(text);
                    }
                if(extractionLoaded&&catalogDone&&waitingCatalog.Count>0)
                {
                    language.SelectCatalog(waitingCatalog);
                    foreach(var text in waitingCatalog)AddCatalog(text);
                    ObserveNames(waitingCatalog.Where(catalog.Contains));
                    waitingCatalog.Clear();
                }
                foreach(var pair in cachedCatalogUpdates)updates[pair.Key]=pair.Value;
                cachedCatalogUpdates.Clear();
                foreach(var text in currentTexts)
                {
                    rawObserved.Add(text);
                    if(!RpgTranslationText.HasTranslatableText(text,options.Syntax)){runtimeNonText.Add(text);continue;}
                    if(!language.AllowsSelectedText(text)||!catalog.Contains(text)&&!RuntimeAllowed(text)){runtimeExcluded.Add(text);continue;}
                    writableSources.Add(text);
                    deferredCatalog.Remove(text);
                    // The current bridge now recognizes this exact source. Reuse
                    // its display translation through the normal validated apply
                    // path; cache text itself never grants writeback authority.
                    ReuseDisplayTranslation(text);
                    if(observed.Add(text))
                    {
                        ObserveNames([text]);
                        if(TryNative(text,out var reused))TrackReusedQuality(text,reused);
                        bool translated=Complete(text);
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
                        if(readOnlySources.Add(text)&&RuntimeAllowed(text))ObserveNames([text]);
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
                foreach(var job in jobs.Where(j=>j.HasResults).ToArray())
                {
                    Dictionary<string,string> results;
                    string[] deliveredSources;
                    if(job.Work.IsCompleted)
                    {
                        try{results=await job.Work;}
                        catch(OperationCanceledException)when(job.Cancellation.IsCancellationRequested&&!lifetime.IsCancellationRequested)
                        {results=new(StringComparer.Ordinal);}
                        finally{jobs.Remove(job);job.Cancellation.Dispose();}
                        deliveredSources=job.PendingSources.ToArray();
                    }
                    else
                    {
                        job.EarlyConsumed=true;
                        if(job.Cancellation.IsCancellationRequested)continue;
                        results=RpgTranslationBatchResult.Copy(await job.Progress.Accepted);
                        deliveredSources=job.PendingSources.Where(results.ContainsKey).ToArray();
                        // Final completion cannot remove or overwrite a newer job for these sources.
                        job.Delivered.UnionWith(deliveredSources);
                    }
                    foreach(var source in deliveredSources)
                    {
                        active.Remove(source);
                        // A newer retry must not be satisfied by an older request.
                        var currentDocument=workspace.Snapshot();
                        bool hasManual=TryManual(source,currentDocument,out var corrected);
                        if(hasManual)
                        {forced.Remove(source);failures.Remove(source);provisional.Remove(source);updates[source]=corrected;resolvedIssues.Add(source);continue;}
                        string requestToken=currentDocument.Requests.GetValueOrDefault(source,"");
                        bool hadContext=job.Contexts.TryGetValue(source,out var previousContext);
                        // Late role metadata must not discard a usable ordinary
                        // request already in flight. It keeps its original cache
                        // identity; it is never accepted as a role-cache result.
                        bool ordinaryRoleUpgrade=(!hadContext||previousContext!.Roles is null)
                            &&dialogueContexts.TryGet(source,out var currentContext)&&currentContext.Roles is not null
                            &&(!hadContext||previousContext!.Previous.SequenceEqual(currentContext.Previous,StringComparer.Ordinal));
                        if(dialogueContexts.Stamp(source)!=(hadContext?previousContext!.Identity:"")&&!ordinaryRoleUpgrade)
                        {
                            if(!hasManual)
                            {
                                forced.Add(source);
                                if(!HasRoleContext(source)&&previousContext?.Roles is null)failures.Remove(source);
                                visible.Enqueue(source);
                            }
                            continue;
                        }
                        if((job.Contexts.TryGetValue(source,out var termContext)?termContext.TermsStamp(job.Terms,source):TranslationGlossary.Stamp(job.Terms,source))
                            !=(job.Contexts.TryGetValue(source,out termContext)?termContext.TermsStamp(currentDocument.Terms,source):TranslationGlossary.Stamp(currentDocument.Terms,source)))
                        {if(!hasManual){forced.Add(source);failures.Remove(source);visible.Enqueue(source);}continue;}
                        if(requestToken!=job.RequestTokens.GetValueOrDefault(source,"")&&requestToken.Length>0)
                        {if(!hasManual){forced.Add(source);visible.Enqueue(source);}continue;}
                        if(results.TryGetValue(source,out var text))
                        {
                            // A catalogue choice can arrive while a visible
                            // request is in flight. Do not apply another language.
                            if(!MayTranslate(source)){failures.Remove(source);continue;}
                            bool wasComplete=Complete(source);
                            var quality=results as RpgTranslationBatchResult;
                            string soft=quality?.SoftFailures.GetValueOrDefault(source,"")??"";
                            bool retainedPrevious=false;
                            if(soft.Length>0)
                            {
                                // Keep an earlier readable result when a repair
                                // comes back worse; it remains visibly pending.
                                if(TryNativeUnwrapped(source,out var previousValue)&&previousValue.Length>0)
                                {text=previousValue;retainedPrevious=true;}
                                else if(readOnlySources.Contains(source)&&displayCache.TryGetValue(source,out previousValue)&&previousValue.Length>0)
                                {text=previousValue;retainedPrevious=true;}
                                provisional.Add(source);forced.Add(source);
                                int count=failures.GetValueOrDefault(source).Attempts+1;
                                failures[source]=(count,count<4?DateTime.UtcNow.AddSeconds(Math.Min(60,2*Math.Pow(3,count-1))):DateTime.MaxValue,soft);
                                newIssues.Add(TranslationAttemptStatus.Evidence(source,soft,count,quality!.Replies.GetValueOrDefault(source,text),job.Terms,true,text));
                            }
                            else{forced.Remove(source);provisional.Remove(source);failures.Remove(source);resolvedIssues.Add(source);}
                            latency.Accepted(source);
                            if(requestToken.Length>0&&soft.Length==0)completedRequests[source]=requestToken;
                            if(readOnlySources.Contains(source)&&!writableSources.Contains(source)&&!(job.Contexts.TryGetValue(source,out var roleOnly)&&roleOnly.Roles is not null))
                            {if(!retainedPrevious){displayCache[source]=text;displayGlossary.Accept(source,text,job.Terms);dirty=true;}continue;}
                            bool nowComplete=soft.Length==0;
                            int delta=(nowComplete?1:0)-(wasComplete?1:0);
                            if(catalog.Contains(source))completed+=delta;if(observed.Contains(source))observedDone+=delta;if(additional.Contains(source))additionalDone+=delta;
                            // A retained result keeps the receipt from the request
                            // that produced it; this repair did not translate it.
                            if(!retainedPrevious)
                            {
                                if(job.Contexts.TryGetValue(source,out var dialogue))
                                {
                                    if(dialogue.Roles is null){dialogueCache.Accept(source,text,dialogue,job.Terms);dirty=true;}
                                    else{dialogueRoleCache.Accept(source,text,dialogue,job.Terms);roleDirty=true;}
                                }
                                else{RememberNative(source,text);nativeGlossary.Accept(source,text,job.Terms);dirty=true;}
                            }
                            updates[source]=TryManual(source,workspace.Snapshot(),out var manualValue)?manualValue:
                                retainedPrevious&&options.Syntax==EmbeddedTextSyntax.Wolf?WolfDisplayWrapping.Fit(source,text):text;
                        }
                        else if(!hasManual&&MayTranslate(source))
                        {int count=failures.GetValueOrDefault(source).Attempts+1;
                            string reason=results is RpgTranslationBatchResult detail?detail.Failures.GetValueOrDefault(source,"unknown"):"unknown";
                            providerPause.Observe(reason,DateTime.UtcNow);
                            failures[source]=(count,count<4?DateTime.UtcNow.AddSeconds(Math.Min(60,2*Math.Pow(3,count-1))):DateTime.MaxValue,reason);
                            string reply=results is RpgTranslationBatchResult evidence?evidence.Replies.GetValueOrDefault(source,""):"";
                            bool retainRoleFallback=job.Contexts.TryGetValue(source,out var failedContext)&&failedContext.Roles is not null;
                            string? retained=provisional.Contains(source)||retainRoleFallback
                                ?TryNativeUnwrapped(source,out var oldValue)?oldValue:displayCache.GetValueOrDefault(source):null;
                            newIssues.Add(TranslationAttemptStatus.Evidence(source,reason,count,reply,job.Terms,provisional.Contains(source),retained));}
                    }
                }
                if(newIssues.Count>0||resolvedIssues.Count>0)workspace.RecordIssues(newIssues,resolvedIssues);
                if(completedRequests.Count>0)workspace.CompleteRequests(completedRequests);
                lifetime.Token.ThrowIfCancellationRequested();
                int currentWolfTemplateRevision=System.Threading.Volatile.Read(ref wolfTemplateRevision);
                int currentDisplayTemplateRevision=System.Threading.Volatile.Read(ref displayTemplateRevision);
                if(UsesDisplayTemplates&&currentDisplayTemplateRevision!=deliveredDisplayTemplateRevision)
                {
                    var templateDocument=workspace.Snapshot();
                    foreach(string source in observed.Concat(aheadObserved).Distinct(StringComparer.Ordinal))
                        if(MayTranslate(source))
                        {
                            if(TryNative(source,out var updated,templateDocument))updates[source]=updated;
                            else if(derivedDisplaySources.TryRemove(source,out _))
                            {updates[source]=source;visible.Enqueue(source);}
                        }
                    deliveredDisplayTemplateRevision=currentDisplayTemplateRevision;
                }
                if(options.Syntax==EmbeddedTextSyntax.Wolf&&currentWolfTemplateRevision!=deliveredWolfTemplateRevision)
                {
                    // A previously sent expanded result is an exact key inside
                    // the native display cache. Refresh those observed values
                    // when its automatic owner changes, without persisting them.
                    // CompleteRequests just published a new workspace revision;
                    // do not consult the periodic snapshot's old retry token.
                    var templateDocument=workspace.Snapshot();
                    foreach(string source in observed.Concat(aheadObserved).Distinct(StringComparer.Ordinal))
                        if(MayTranslate(source)&&TryNative(source,out var updated,templateDocument))updates[source]=updated;
                    deliveredWolfTemplateRevision=currentWolfTemplateRevision;
                }
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
                if(retryRequested){retryRequested=false;providerPause.Retry();foreach(var key in failures.Keys.ToArray())failures[key]=(0,DateTime.UtcNow,failures[key].Code);}
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
                var awaitingNames=new List<(string Source,bool Foreground)>();
                while(jobs.Count<4&&!providerPause.Blocked(DateTime.UtcNow))
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
                        if(terminology.IsPreparing(source,cacheSettings)){awaitingNames.Add((source,true));continue;}
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
                            if(terminology.IsPreparing(source,cacheSettings)){awaitingNames.Add((source,false));continue;}
                            if(!MayTranslate(source)||!NeedsTranslation(source)||failures.TryGetValue(source,out var failure)&&failure.Due>DateTime.UtcNow||!active.Add(source))continue;
                            work.Add(source);chars+=source.Length;if(singleRetry)break;
                        }
                    }
                    if(work.Count==0)break;
                    var sources=work.ToArray();latency.Dispatched(sources);var jobCancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);var progress=new RpgTranslationBatchProgress();
                    var doc=workspace.Snapshot();var contexts=dialogueContexts.ForSources(sources);
                    var prompts=TranslationWorkspaceStore.WithTerms(doc,sources.Concat(contexts.Values.SelectMany(c=>c.Previous)).ToArray(),singleSettings,batchSettings);
                    var bypassTemplates=sources.Where(forced.Contains).ToHashSet(StringComparer.Ordinal);
                    Task<string> Provider(string prompt,ApiSettings settings,CancellationToken token)
                    {
                        Task<string> Call()=>latency.MeasureAsync(TranslationLatency.Stage.Provider,()=>TranslationFlightPool.Shared.Request(translate,prompt,settings,token));
                        return foreground?latency.MeasureAsync(TranslationLatency.Stage.ForegroundProvider,Call):Call();
                    }
                    jobs.Add(new(sources,RpgTranslationBatch.Translate(sources,prompts.Single,prompts.Batch,Provider,jobCancellation.Token,renpy,options.Syntax,doc.Terms,templates,progress:progress,bypassTemplates:bypassTemplates,dialogueContexts:contexts),foreground,jobCancellation,sources.ToDictionary(s=>s,s=>doc.Requests.GetValueOrDefault(s,""),StringComparer.Ordinal),doc.Terms,progress,contexts));
                }
                foreach(var waiting in awaitingNames)if(waiting.Foreground)visible.Enqueue(waiting.Source);else backlog.Enqueue(waiting.Source);
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
                    ReadDialogueContexts(page);
                    int total=page.GetProperty("total").GetInt32(),skipped=page.GetProperty("skipped").GetInt32();
                    int next=page.GetProperty("next").GetInt32();
                    bool scanning=page.TryGetProperty("scanning",out var scanningValue)&&scanningValue.GetBoolean()
                        ||page.TryGetProperty("done",out var doneValue)&&!doneValue.GetBoolean();
                    var rows=page.GetProperty("texts");
                    if(total<0||skipped<0||next<0||next>total||next<catalogCursor||next==catalogCursor&&next<total&&!scanning
                        ||rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength()>2048
                        ||rows.EnumerateArray().Any(item=>item.ValueKind!=JsonValueKind.String))
                        throw new IOException("游戏文本目录没有正确继续返回数据。");
                    // Only opted-in bridges publish selected-source provenance.
                    // Require the matching generation and an exact subset of
                    // this page; neither language inference nor a source label
                    // alone authorizes translating unused language resources.
                    var activeSources=renpy||options.RemoteActiveSources?RemoteSourceCatalog.ActiveSources(page,rows,epoch):[];
                    var selectedResources=options.RemoteActiveSources?RemoteSourceCatalog.SelectedResources(page,rows,epoch):[];
                    var originPage=RemoteCatalogOrigins.Read(page,rows,renpy?"renpy":"");
                    var scanState=RemoteCatalogScanState.Read(page,next>=total&&!scanning);
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
                    remoteActiveSources.UnionWith(activeSources);
                    remoteSelectedResourceSources.UnionWith(selectedResources.Where(source=>language.AllowsSelectedResource(source.Text,source.Locale,true)).Select(source=>source.Text));
                    remoteOrigins.Add(originPage);
                    foreach(var item in rows.EnumerateArray())
                    {
                        var text=item.GetString()!;
                        if(!language.CatalogResolved)waitingCatalog.Add(text);
                        AddCatalog(text,selected.Length>0&&GameSourceLanguageTracker.CanonicalLocale(selected)==language.CatalogHint);
                    }
                    ObserveNames(rows.EnumerateArray().Select(item=>item.GetString()!).Where(catalog.Contains),originPage.Origins.Where(o=>catalog.Contains(o.Text)));
                    catalogCursor=next;catalogDone=next>=catalogTotal&&!scanning;
                    if(scanState.IssueCount>remoteScan.IssueCount)
                        AppLog.Write("translation-catalog","session="+latencySession+" game="+latencyGameId+" remote-read-incomplete issues="+scanState.IssueCount);
                    remoteScan=scanState;
                    if(catalogDone&&!remoteOriginsIndexed)
                    {
                        remoteOriginsIndexed=true;
                        QueueOriginIndex(remoteOrigins.Complete(discoveredCatalog));
                    }
                    if(remoteFailed)AppLog.Write("translation-catalog","session="+latencySession+" game="+latencyGameId+" remote-read-resumed cursor="+catalogCursor);
                    catalogFailures=0;remoteFailed=false;
                    // Drain pages already produced by the game promptly. Empty
                    // pages while its bounded scan runs retain a slower cadence.
                    // Polls and ready screen writebacks still get their turn above.
                    nextCatalogPoll=DateTime.UtcNow.AddMilliseconds(catalogDone?2000:next<total?25:scanning?100:250);
                    }
                    catch(OperationCanceledException)when(lifetime.IsCancellationRequested){throw;}
                    catch(Exception ex)when(ex is UnityMainThreadPausedException or UnrealCulturePendingException or EmbeddedTransportBusyException)
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
                    CatalogIncomplete:hasDesktopCatalog&&extractionLoaded&&(!extraction.ReadCompleted||extraction.Unreadable.Length>0||extraction.Report?.Completed==false)||remoteOrigins.IssueCount>0||remoteScan.IssueCount>0,
                    CatalogIssueCount:(extraction.Report?.Issues.Count(i=>i.Kind!=CatalogIssueKind.Skipped)??extraction.Unreadable.Length)+remoteOrigins.IssueCount+remoteScan.IssueCount,
                    CatalogExcludedTexts:languageExcluded.Count+declaredLanguageSkipped,
                    RetryScheduledTexts:failures.Count(p=>p.Value.Due!=DateTime.MaxValue),RetryStoppedTexts:failures.Count(p=>p.Value.Due==DateTime.MaxValue),
                    BridgePaused:bridgePaused,CatalogRetryPending:remoteFailed,ActiveRenderedBindings:activeRenderedBindings,ActiveCachedWaitingBindings:activeCachedWaitingBindings,
                    FailureCodes:failures.Values.Select(f=>f.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),RuntimeNote:runtimeWarning,
                    CatalogDiscoveredTexts:discoveredCatalog.Count,CatalogDeferredTexts:deferredCatalog.Count,
                    CatalogLocaleUnconfirmed:deferredCatalog.Count>0&&!language.Tracker.HasDeclaredLocale&&!language.ScriptCatalogConfirmed(extraction.ScriptSourceLanguage),
                    TerminologyPendingNames:pendingNames,TerminologyPreparing:terminologyWork is {IsCompleted:false},
                    FontDeferredComponents:fontRuntimeStatus.DeferredComponents,FontWarning:fontRuntimeStatus.Warning));
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
                if(remoteScan.IssueCount>0)Status+=$" · {remoteScan.IssueCount} 处目录未能完整读取，运行时继续补译";
                if(catalogSkipped>0)Status+=options.CatalogBoundOnly?$" · {catalogSkipped} 项保留原有译文或暂未提取":options.Syntax==EmbeddedTextSyntax.Generic?$" · 提取时跳过 {catalogSkipped} 项（资源或过长文本），运行时继续补译":$" · {catalogSkipped} 条过长文本未提取";
                if(failed>0)Status+=TranslationAttemptStatus.Summary(failures.Count(p=>p.Value.Due!=DateTime.MaxValue),failures.Count(p=>p.Value.Due==DateTime.MaxValue));
                else if(!bridgePaused&&runtimeWarning.Length==0&&catalogDone&&extractionLoaded&&completed==catalog.Count&&additionalDone==additional.Count&&backlog.Count==0&&visible.Count==0&&jobs.Count==0&&awaitingCache==0)
                    Status+=catalog.Count==0&&rawObserved.Count==0&&ReadOnlyTexts.Length==0?" · 等待可读取的游戏文字":
                        writebacksConfirmed==0&&nativePreprocessorHits is not >0&&rawObserved.Count>0?" · 译文已准备，游戏内写回尚未确认":
                        " · 当前已捕获文字的译文缓存已准备";
                if(providerPause.Blocked(DateTime.UtcNow))Status+=" · "+providerPause.Status(DateTime.UtcNow);
                if(runtimeWarning.Length>0)Status+=" · "+runtimeWarning;
                if(extractionSummary.Length>0)Status+=" · "+extractionSummary;
                else if(extraction.Unreadable.Length>0)Status+=$" · {extraction.Unreadable.Length} 处未能完整读取（不能据此判断没有文字）";
                if(remoteFailed)Status+=" · 游戏文本目录读取暂未完成，将自动重试；当前文字继续处理";
                CompleteCacheSave();
                if((dirty||roleDirty)&&((DateTime.UtcNow-saved).TotalSeconds>=5||jobs.Count==0))
                {if(BeginCacheSave())saved=DateTime.UtcNow;}
                if(DateTime.UtcNow>=nextLatencyLog)
                {
                    nextLatencyLog=DateTime.UtcNow.AddSeconds(10);
                    AppLog.Write("translation-latency","session="+latencySession+" game="+latencyGameId+" timings="+JsonSerializer.Serialize(latency.Snapshot()));
                    AppLog.Write("translation-coverage","session="+latencySession+" game="+latencyGameId+" coverage="+JsonSerializer.Serialize(coverage.Snapshot(extractionLoaded&&catalogDone&&remoteScan.Complete&&!remoteFailed&&remoteOrigins.IssueCount==0&&(!hasDesktopCatalog||extraction.ReadCompleted&&extraction.Unreadable.Length==0&&extraction.Report?.Completed!=false))));
                }
                Status=WithCacheWarning(Status);
                // Wake when a provider or bridge reply is ready, while retaining
                // the 100 ms polling cadence and a small anti-spin loop floor.
                var signals=new List<Task>();
                if(pollWork is not null)signals.Add(pollWork);if(applyWork is not null)signals.Add(applyWork);if(catalogWork is not null)signals.Add(catalogWork);
                signals.AddRange(jobs.Select(j=>(Task)j.Work));if(!extractionLoaded)signals.Add(extractionTask);if(cacheSaveWork is not null)signals.Add(cacheSaveWork);
                signals.AddRange(jobs.Where(j=>!j.EarlyConsumed).Select(j=>(Task)j.Progress.Accepted));
                int wakeAfter=100;
                if(pollWork is null&&applyWork is null&&catalogWork is null&&nextPollAt>DateTime.UtcNow)
                    wakeAfter=Math.Clamp((int)Math.Ceiling((nextPollAt-DateTime.UtcNow).TotalMilliseconds),10,100);
                if(remote&&!catalogDone&&!bridgePaused&&pollWork is null&&applyWork is null&&catalogWork is null)
                    wakeAfter=Math.Min(wakeAfter,Math.Clamp((int)Math.Ceiling((nextCatalogPoll-DateTime.UtcNow).TotalMilliseconds),10,100));
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
            terminologyOwnership?.Dispose();
            if(terminologyWork is not null)_=terminologyWork.ContinueWith(t=>{_ = t.Exception;},CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
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

