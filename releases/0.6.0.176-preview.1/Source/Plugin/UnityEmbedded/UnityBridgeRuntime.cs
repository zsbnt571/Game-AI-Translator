using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using HarmonyLib;

namespace Fusion.UnityEmbedded
{
    public sealed class UnityBridgeRuntime : IDisposable
    {
        sealed class Binding
        {
            internal readonly DisplayLatencyMark Latency=new DisplayLatencyMark();
            internal int Id; internal long EvidenceScope;internal object Target; internal PropertyInfo Text; internal string Source,Applied;
            internal object OriginalFont,OwnedFont; internal PropertyInfo Font;
            internal bool LastActive,FontBlocked,Revealing;internal long LastObserved,FontRetryAt;
            internal string ConfirmedSource,ConfirmedText,RestoreSource,DisplaySuffix;
            internal string ProvenLiteralSource;
            internal NativeTmpPreprocessor Processor;
            internal bool LegacyProjection,BufferedProjection,BufferedSetText;
            internal long BufferedRetryAt;
            internal readonly CjkTextLayout Layout=new CjkTextLayout();
            internal string PreparedDisplay,RenderedSource,RenderedText;
            internal string IndentSource,IndentText,IndentDisplay;internal int IndentSize,IndentFontId,IndentStyle;
            internal int FontMeshGeneration,AtlasCheckedGeneration,AtlasFontId,AtlasSize,AtlasStyle;
            internal double AtlasScale;
            internal float RasterDensity;internal long RasterCheckAt;
            internal string AtlasText;internal long AtlasCheckAt;
            internal PropertyInfo Size;internal object OriginalSize,OwnedSize;
            internal PropertyInfo FitSize,FitMax;internal int OriginalFitSize,OwnedFitSize,OriginalFitMax,OwnedFitMax;
            internal bool CompleteReveal,AtlasReady,EarlyTmpReveal,KnownCompleteBuffer;
            internal string FontReason,AtlasStatus;
            internal readonly IncrementalTextState Incremental=new IncrementalTextState();
        }
        sealed class DisplayLatencyMark
        {
            internal bool Started,Cached,Done,FontBlocked;internal long ObservedAt,CacheAt,FontAt;
            internal void Begin(long now){Started=true;Cached=Done=FontBlocked=false;ObservedAt=now;CacheAt=FontAt=0;}
        }
        sealed class DisplayLatencyMeasure
        {
            internal long Samples,LastMs,MaxMs;
            internal void Add(long value){Samples++;LastMs=Math.Max(0,value);MaxMs=Math.Max(MaxMs,LastMs);}
        }
        readonly DisplayLatencyMeasure observedToCache=new DisplayLatencyMeasure(),cacheToReadback=new DisplayLatencyMeasure(),glyphWait=new DisplayLatencyMeasure();
        void CacheReady(Binding binding)
        {
            var mark=binding.Latency;if(!mark.Started||mark.Cached)return;
            mark.Cached=true;mark.CacheAt=timer.ElapsedMilliseconds;observedToCache.Add(mark.CacheAt-mark.ObservedAt);
        }
        void FontReady(Binding binding,bool ready)
        {
            var mark=binding.Latency;if(!mark.Started)return;long now=timer.ElapsedMilliseconds;
            if(!ready&&!mark.FontBlocked){if(mark.Done){mark.Begin(now);CacheReady(binding);}mark.FontBlocked=true;mark.FontAt=now;}
            else if(ready&&mark.FontBlocked){glyphWait.Add(now-mark.FontAt);mark.FontBlocked=false;}
        }
        void ReadbackReady(Binding binding)
        {
            var mark=binding.Latency;if(!mark.Started||!mark.Cached||mark.Done)return;
            mark.Done=true;cacheToReadback.Add(timer.ElapsedMilliseconds-mark.CacheAt);
        }
        object LatencyProgress()
        {
            int cache=0,font=0,control=0;long oldest=0,now=timer.ElapsedMilliseconds;
            if(enabled)foreach(var binding in bindings.Values)
            {
                var mark=binding.Latency;if(!binding.LastActive||!mark.Started||mark.Done)continue;
                oldest=Math.Max(oldest,now-mark.ObservedAt);if(!mark.Cached)cache++;else if(mark.FontBlocked)font++;else control++;
            }
            return Map("observedToCacheSamples",observedToCache.Samples,"observedToCacheLastMs",observedToCache.LastMs,"observedToCacheMaxMs",observedToCache.MaxMs,
                "cacheToReadbackSamples",cacheToReadback.Samples,"cacheToReadbackLastMs",cacheToReadback.LastMs,"cacheToReadbackMaxMs",cacheToReadback.MaxMs,
                "glyphWaitSamples",glyphWait.Samples,"glyphWaitLastMs",glyphWait.LastMs,"glyphWaitMaxMs",glyphWait.MaxMs,
                "waitingForCache",cache,"waitingForFont",font,"waitingForReadback",control,"oldestObservedMs",oldest,
                "maxDiscoveryMs",maxDiscoveryMilliseconds,"discoverySnapshots",discoverySnapshots,"lastPumpAgeMs",Math.Max(0,now-Interlocked.Read(ref lastTickMilliseconds)));
        }
        sealed class Request
        {
            internal Dictionary<string,object> Body; internal string Response; internal readonly ManualResetEvent Done=new ManualResetEvent(false);
            internal bool Started,Cancelled,Completed,Abandoned;
        }
        static UnityBridgeRuntime current;
        readonly Dictionary<int,Binding> bindings=new Dictionary<int,Binding>();
        Dictionary<string,string> translations=new Dictionary<string,string>(StringComparer.Ordinal);
        readonly Dictionary<string,string> startupTranslations=new Dictionary<string,string>(StringComparer.Ordinal);
        readonly TextLookup startupLookup=new TextLookup();bool startupDisplayTemplates;
        readonly StableSourceHistory stableSources=new StableSourceHistory();
        long nextEvidenceScope;
        TextLookup lookup=new TextLookup(),stagedLookup=new TextLookup();
        Dictionary<string,string> staged=new Dictionary<string,string>(StringComparer.Ordinal);bool preparing;
        readonly Queue<string> visible=new Queue<string>();readonly HashSet<string> queued=new HashSet<string>(StringComparer.Ordinal);
        readonly Queue<string> ahead=new Queue<string>();readonly HashSet<string> aheadQueued=new HashSet<string>(StringComparer.Ordinal);
        readonly Queue<Request> requests=new Queue<Request>();readonly List<Type> textTypes=new List<Type>();
        readonly HashSet<MethodBase> patched=new HashSet<MethodBase>();readonly Harmony harmony=new Harmony("local.fusion.unity.embedded.73");
        readonly Dictionary<MethodBase,long> hookRetryAt=new Dictionary<MethodBase,long>();
        readonly DisplaySourceHooks displaySources;
        BufferedTmpProjection bufferedProjection;bool bufferedProjectionAttempted;
        LegacyTextProjection legacyProjection;bool legacyProjectionAttempted;MethodInfo rebuildingGraphics;
        int legacyProjectionHits;
        object completeCallTarget;string completeCallSource;bool insideTextSetter,insideTextPropertySetter;
        int completeCallCaptures,completeCallCacheHits;
        int nativeProcessorHits,nativeProcessorBindings;bool nativeProcessorUnavailable;
        int prefixCacheHits,enableCacheHits,renderCacheHits,lateCacheHits;string fontStatus="not requested";
        readonly bool il2Cpp;readonly Queue<Binding> lateTargets=new Queue<Binding>();
        readonly Queue<Binding> pendingRefresh=new Queue<Binding>();readonly HashSet<Binding> refreshQueued=new HashSet<Binding>(),frameVisited=new HashSet<Binding>();
        readonly Queue<Binding> restoreQueue=new Queue<Binding>();bool restoreSession;
        int lastRestoreCount;long restoreSlices;
        double startupIndexMilliseconds,lastLookupMilliseconds,maxLookupMilliseconds,lastRestoreMilliseconds,maxRestoreMilliseconds,lastLateMilliseconds,maxLateMilliseconds;long lastMetricsAt;
        readonly Action<string> log;readonly Stopwatch timer=Stopwatch.StartNew();
        Stream pipe;Thread thread;volatile bool stopped,disconnected;bool enabled,writing;int epoch;long scanAt;int scanCursor;
        long aheadScanAt;int aheadCursor;readonly Queue<object> aheadDiscovered=new Queue<object>();
        IEnumerator aheadEnumeration;Type aheadEnumerationType;int aheadInspected;long aheadExpires;bool preferAhead;
        double maxDiscoveryMilliseconds;long discoverySnapshots;
        int tickCount;long lastTickMilliseconds;string closeReason="dispose";
        readonly Queue<object> discovered=new Queue<object>();object dynamicFont; readonly Dictionary<Type,object> tmpFonts=new Dictionary<Type,object>();
        MethodInfo canvasRemove;object canvasCallback;Action canvasManagedCallback;bool inCanvas;
        long fontRetryAt;string lastFontError;int writebackConfirmed,writebackFailed,canvasCacheHits,rawCaptureCount;
        int atlasGeneration,atlasRefreshedGeneration,atlasRefreshes,atlasCapacityFailures,completeRevealCaptures,earlyTmpRevealSubstitutions;
        readonly HashSet<int> rebuiltAtlasFonts=new HashSet<int>();bool atlasFontOverflow;
        MethodInfo fontAtlasRemove;object fontAtlasCallback;Delegate fontAtlasManagedCallback;
        readonly Dictionary<int,object> uiFonts=new Dictionary<int,object>();
        sealed class LegacyAutoFont {internal object Font;internal Binding Owner;}
        readonly List<LegacyAutoFont> legacyAutoFonts=new List<LegacyAutoFont>();
        
        readonly StaticTmpFonts staticTmpFonts=new StaticTmpFonts(Alive,Id);
        readonly Queue<Binding> atlasRefresh=new Queue<Binding>();readonly HashSet<Binding> atlasQueued=new HashSet<Binding>();
        readonly Dictionary<Type,object> nativeTypes=new Dictionary<Type,object>();
        readonly HashSet<Type> unsupportedNativeTypes=new HashSet<Type>(),unsupportedNativeLogged=new HashSet<Type>();
        int nativeCapabilityGeneration,nativeCapabilityObserved;
        readonly Dictionary<MethodInfo,long> queryRetryAt=new Dictionary<MethodInfo,long>();
        sealed class DiscoveryFailure {internal string Family,Stage,Error;internal int Count;internal long LastLogged;}
        readonly Dictionary<string,DiscoveryFailure> discoveryFailures=new Dictionary<string,DiscoveryFailure>(StringComparer.Ordinal);
        public UnityBridgeRuntime(Action<string> log):this(log,false){}
        public UnityBridgeRuntime(Action<string> log,bool il2Cpp){this.log=log;this.il2Cpp=il2Cpp;if(il2Cpp)AppDomain.CurrentDomain.AssemblyLoad+=NativeAssemblyLoaded;else displaySources=new DisplaySourceHooks(harmony,log);}
        void NativeAssemblyLoaded(object sender,AssemblyLoadEventArgs args)
        {
            // Assembly-load callbacks may run off the game thread. Only signal a
            // generation change here; native capability probes remain on it.
            if(!stopped)Interlocked.Increment(ref nativeCapabilityGeneration);
        }
        void RefreshNativeCapabilities()
        {
            if(!il2Cpp)return;int generation=Interlocked.CompareExchange(ref nativeCapabilityGeneration,0,0);
            if(generation==nativeCapabilityObserved)return;nativeCapabilityObserved=generation;unsupportedNativeTypes.Clear();
        }
        public bool Start()
        {
            string name=Environment.GetEnvironmentVariable("FUSION_UNITY_PIPE"),secret=Environment.GetEnvironmentVariable("FUSION_UNITY_SECRET");
            if(string.IsNullOrEmpty(name)||string.IsNullOrEmpty(secret)||name.Length>200||secret.Length!=64)return false;
            string stage="startup-cache";
            try
            {
                log("Unity bridge event=initializing transport=win32-pipe");
                log("Unity bridge hook-mode="+(il2Cpp?"frame-scan runtime=il2cpp":"harmony runtime=mono"));
                LoadStartup();current=this;stage="text-hooks";InstallHooks();stage="worker";
                thread=new Thread(()=>Serve(name,secret));thread.IsBackground=true;thread.Name="Fusion translation pipe";thread.Start();return true;
            }
            catch(Exception ex){log("Unity bridge event=initialization-failed stage="+stage+" error="+ex.GetType().Name);closeReason="initialization-failed";Dispose();throw;}
        }
        void LoadStartup()
        {
            string path=Environment.GetEnvironmentVariable("FUSION_UNITY_STARTUP_FILE");if(string.IsNullOrEmpty(path))return;
            try
            {
                var file=new FileInfo(path);if(!file.Exists||file.Length>32*1024*1024)throw new IOException("Startup cache unavailable.");
                var document=BridgeJson.Parse(File.ReadAllText(path,Encoding.UTF8)) as Dictionary<string,object>;object raw;
                if(document==null||!document.TryGetValue("entries",out raw)||!(raw is IList))throw new IOException("Invalid startup cache.");
                var entries=(IList)raw;if(entries.Count>150000)throw new IOException("Startup cache limit exceeded.");
                object exact;bool exactOnly=document.TryGetValue("exactOnly",out exact)&&exact is bool&&(bool)exact;
                object aliases;startupDisplayTemplates=document.TryGetValue("allowDisplayTemplates",out aliases)&&aliases is bool&&(bool)aliases;
                var destination=exactOnly?startupTranslations:translations;var destinationIndex=exactOnly?startupLookup:lookup;long indexStarted=timer.ElapsedTicks;
                foreach(var value in entries){var entry=value as Dictionary<string,object>;if(entry==null)continue;string source=String(entry,"source"),text=String(entry,"text");if(Eligible(source)&&!string.IsNullOrEmpty(text)&&text.Length<=24000){destination[source]=text;destinationIndex.Update(source,text);}}
                startupIndexMilliseconds=(timer.ElapsedTicks-indexStarted)*1000.0/Stopwatch.Frequency;
                enabled=Environment.GetEnvironmentVariable("FUSION_UNITY_TRANSLATION_START")=="1";
            }
            catch(Exception ex){translations.Clear();startupTranslations.Clear();startupDisplayTemplates=false;lookup.Clear();startupLookup.Clear();enabled=false;log("Startup translations unavailable: "+ex.GetType().Name);}
        }
        public void Tick()
        {
            if(stopped)return;if(disconnected){Disable();if(restoreQueue.Count==0)Dispose();return;}
            Interlocked.Exchange(ref lastTickMilliseconds,timer.ElapsedMilliseconds);
            if(Interlocked.Increment(ref tickCount)==1)log("Unity bridge event=main-thread-ready");
            for(int count=0;count<8;count++)
            {
                Request request;lock(requests){if(requests.Count==0)break;request=requests.Dequeue();if(request.Cancelled)continue;request.Started=true;}
                object id;request.Body.TryGetValue("id",out id);
                try{request.Response=BridgeJson.Write(Map("id",id,"ok",true,"result",Handle(request.Body)));}
                catch(Exception ex){request.Response=BridgeJson.Write(Map("id",id,"ok",false,"error",ex is InvalidOperationException?ex.Message:"Unity bridge operation failed."));log("Request failed: "+ex.GetType().Name);}
                lock(requests){request.Completed=true;request.Done.Set();if(request.Abandoned)request.Done.Close();}
            }
            if(!enabled){PumpRestoreSlice();return;}
            if(timer.ElapsedMilliseconds>=scanAt)
            {
                scanAt=timer.ElapsedMilliseconds+(il2Cpp?200:400);InstallHooks();long scanStarted=timer.ElapsedTicks;Discover();discoverySnapshots++;maxDiscoveryMilliseconds=Math.Max(maxDiscoveryMilliseconds,(timer.ElapsedTicks-scanStarted)*1000.0/Stopwatch.Frequency);
            }
            if(enabled&&timer.ElapsedMilliseconds>=aheadScanAt){aheadScanAt=timer.ElapsedMilliseconds+2000;DiscoverAhead();}
            if(enabled)PumpAheadDiscovery();
            // The fallback scanner processes a bounded set of already discovered UI components.
            long until=timer.ElapsedTicks+Stopwatch.Frequency/500;
            for(int count=0;count<48&&(discovered.Count>0||aheadDiscovered.Count>0)&&timer.ElapsedTicks<until;count++)
            {
                bool useAhead=aheadDiscovered.Count>0&&(discovered.Count==0||preferAhead);preferAhead=!useAhead;
                var target=useAhead?aheadDiscovered.Dequeue():discovered.Dequeue();try{if(useAhead&&(!Alive(target)||!InLoadedScene(target)))continue;var property=target.GetType().GetProperty("text");if(property!=null&&property.PropertyType==typeof(string))CaptureExisting(target,property);}catch{}
            }
        }
        public void LateTick()
        {
            if(stopped||disconnected||!enabled)return;
            RefreshFontMeshes();
            // Both backends recheck known UI after ordinary Update callbacks. This
            // catches game-owned backing-field writes or unavailable Mono hooks;
            // IL2CPP continues to use native setters without Harmony trampolines.
            long started=timer.ElapsedTicks,until=started+Stopwatch.Frequency/500;int count=0;frameVisited.Clear();
            // Cache receipt does not synchronously rewrite every native UI object.
            // Service recently observed matching labels before the normal rotation,
            // sharing the same per-frame count/time budget with that rotation.
            while(pendingRefresh.Count>0&&count<64&&timer.ElapsedTicks<until)
            {
                var binding=pendingRefresh.Dequeue();refreshQueued.Remove(binding);count++;
                ObserveBinding(binding);
            }
            int available=lateTargets.Count;
            for(int inspected=0;inspected<available&&count<128&&timer.ElapsedTicks<until;inspected++,count++)
            {
                var binding=lateTargets.Dequeue();Binding registered;
                if(!bindings.TryGetValue(binding.Id,out registered)||!ReferenceEquals(binding,registered))continue;
                if(!Alive(binding.Target)){ReleaseProcessor(binding);bindings.Remove(binding.Id);continue;}
                lateTargets.Enqueue(binding);
                ObserveBinding(binding);
            }
            lastLateMilliseconds=(timer.ElapsedTicks-started)*1000.0/Stopwatch.Frequency;maxLateMilliseconds=Math.Max(maxLateMilliseconds,lastLateMilliseconds);
            LogMetrics(false);
        }
        void LogMetrics(bool force)
        {
            long now=timer.ElapsedMilliseconds;if(!force&&now-lastMetricsAt<10000)return;lastMetricsAt=now;
            try{log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "Unity bridge metrics pending={0} bindings={1} maxLookupMs={2:F3} maxLateFrameMs={3:F3} maxRestoreMs={4:F3} completeCalls={5} completeCacheHits={6} displaySites={7}",
                pendingRefresh.Count,bindings.Count,maxLookupMilliseconds,maxLateMilliseconds,maxRestoreMilliseconds,completeCallCaptures,completeCallCacheHits,displaySources==null?0:displaySources.CallSitesInstalled));}catch{}
        }
        void ObserveBinding(Binding binding)
        {
            Binding registered;if(!frameVisited.Add(binding)||!bindings.TryGetValue(binding.Id,out registered)||!ReferenceEquals(binding,registered))return;
            if(!Alive(binding.Target)){ReleaseProcessor(binding);bindings.Remove(binding.Id);return;}
            try{if(CaptureExisting(binding.Target,binding.Text))lateCacheHits++;}catch{}
        }
        void ScheduleRefresh(HashSet<string> changed)
        {
            foreach(var binding in bindings.Values.Where(b=>changed==null||b.Source!=null&&changed.Contains(b.Source)).OrderByDescending(b=>b.LastActive).ThenByDescending(b=>b.LastObserved))
            {
                string cached;if(TryTranslation(binding.Source??"",out cached))CacheReady(binding);
                if(refreshQueued.Count<12000&&refreshQueued.Add(binding))pendingRefresh.Enqueue(binding);
            }
        }
        void RecordLookupWork(long started)
        {
            lastLookupMilliseconds=(timer.ElapsedTicks-started)*1000.0/Stopwatch.Frequency;maxLookupMilliseconds=Math.Max(maxLookupMilliseconds,lastLookupMilliseconds);
        }
        bool TryTranslation(string source,out string translated)
        {
            
            // Exact pairs always win over inferred display aliases. Startup
            // aliases are opt-in and never prove that a writer has finished.
            return translations.TryGetValue(source,out translated)||startupTranslations.TryGetValue(source,out translated)
                ||lookup.TryGet(translations,source,out translated)
                ||startupDisplayTemplates&&startupLookup.TryGet(startupTranslations,source,out translated);
        }
        bool ObserveSource(Binding binding,string source)
        {
            bool settled=binding.Incremental.Observe(source,timer.ElapsedMilliseconds,binding.CompleteReveal||binding.KnownCompleteBuffer);
            // A formerly stable label may later prove to be a writer prefix.
            // Revoke only that exact observation, without suppressing other UI.
            stableSources.Forget(binding.EvidenceScope,binding.Incremental.PriorGrowthSource);
            return settled;
        }
        bool CanReuseObservedSource(Binding binding,string source)
        {
            // Readiness evidence and an exact translation are both required.
            // A template/cache hit alone is never evidence of a whole source.
            string ignored;
            return !binding.Incremental.IsStreaming&&!binding.Revealing&&stableSources.Contains(binding.EvidenceScope,source)
                &&(translations.TryGetValue(source,out ignored)||startupTranslations.TryGetValue(source,out ignored));
        }
        void InstallHooks()
        {
            InstallFontAtlasCallback();if(il2Cpp)InstallCanvasCallback();
            foreach(string name in new[]{"UnityEngine.UI.Text","TMPro.TMP_Text","TMPro.TextMeshProUGUI","TMPro.TextMeshPro","UnityEngine.TextMesh"})
            {
                var type=FindType(name);if(type==null)continue;
                // Scan each component family once. The base TMP_Text includes both
                // concrete renderers on Mono as well as IL2CPP. Hooks still cover each.
                if(!textTypes.Any(known=>known.IsAssignableFrom(type)))
                {textTypes.RemoveAll(known=>type.IsAssignableFrom(known));textTypes.Add(type);}
                // A managed prefix still needs a native detour on IL2CPP. In particular,
                // SetText(string,bool) can enter an unsafe native-to-managed trampoline.
                // Setter/enable/render hooks use that same backend, so none are installed.
                if(il2Cpp)continue;
                var property=type.GetProperty("text");Patch(property==null?null:property.GetSetMethod());
                if(name=="UnityEngine.UI.Text"&&!legacyProjectionAttempted)
                {
                    legacyProjectionAttempted=true;
                    try{legacyProjection=new LegacyTextProjection(harmony,type,LegacyDisplayText,LegacyDisplayDensity,LegacyPreferredSize,LegacyGenerationSettings);}
                    catch(Exception ex){log("Unity legacy projection unavailable: "+RootError(ex).GetType().Name);}
                    if(legacyProjection!=null)try{legacyProjection.InstallLetterSpacing(harmony,FindType("UnityEngine.UI.LetterSpacing"),OwnsLegacyCjk);}
                    catch(Exception ex){log("Unity letter spacing compatibility unavailable: "+RootError(ex).GetType().Name);}
                    var registry=FindType("UnityEngine.UI.CanvasUpdateRegistry");
                    if(registry!=null)rebuildingGraphics=registry.GetMethod("IsRebuildingGraphics",BindingFlags.Static|BindingFlags.Public);
                }
                var onEnable=type.GetMethod("OnEnable",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly);
                PatchCallback(onEnable,"EnablePrefix");
                foreach(string renderName in new[]{"OnPopulateMesh","OnPreRenderCanvas","OnPreRenderObject","GenerateTextMesh"})
                {
                    foreach(var render in type.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly).Where(m=>m.Name==renderName&&!m.IsAbstract))
                        if(name!="UnityEngine.UI.Text")PatchCallback(render,"RenderPrefix");
                }
                if(name=="TMPro.TMP_Text"&&!bufferedProjectionAttempted)
                {
                    bufferedProjectionAttempted=true;
                    if(type.GetProperty("textPreprocessor")==null)try{bufferedProjection=new BufferedTmpProjection(harmony,type,BufferedDisplayText);}catch(Exception ex){log("Unity TMP buffer projection unavailable: "+RootError(ex).GetType().Name);}
                }
                if(name=="TMPro.TMP_Text")foreach(var method in type.GetMethods(BindingFlags.Public|BindingFlags.Instance))
                    if(method.Name=="SetText"&&method.GetParameters().Length>0&&method.GetParameters()[0].ParameterType==typeof(string))Patch(method);
            }
            // Install only after the actual display setters are intercepted.
            // The evidence belongs to a proved invocation, never to a cache key.
            if(displaySources!=null)displaySources.Install();
        }
        void Patch(MethodBase method)
        {
            if(method!=null&&method.ReflectedType!=method.DeclaringType)method=method.DeclaringType.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly).FirstOrDefault(m=>m.MetadataToken==method.MetadataToken);
            if(!CanPatch(method))return;
            try{harmony.Patch(method,new HarmonyMethod(typeof(UnityBridgeRuntime).GetMethod("TextPrefix",BindingFlags.NonPublic|BindingFlags.Static)));patched.Add(method);}
            catch(Exception ex){HookFailure(method,ex);}
        }
        bool CanPatch(MethodBase method)
        {long retry;return method!=null&&!patched.Contains(method)&&(!hookRetryAt.TryGetValue(method,out retry)||timer.ElapsedMilliseconds>=retry);}
        void PatchCallback(MethodBase method,string prefix)
        {
            if(!CanPatch(method))return;
            try{harmony.Patch(method,new HarmonyMethod(typeof(UnityBridgeRuntime).GetMethod(prefix,BindingFlags.NonPublic|BindingFlags.Static)));patched.Add(method);}
            catch(Exception ex){HookFailure(method,ex);}
        }
        void HookFailure(MethodBase method,Exception exception)
        {
            // Some generated/stripped methods can never receive a managed hook.
            // Keep the scanner available while bounding repeated patch failures.
            bool first=!hookRetryAt.ContainsKey(method);hookRetryAt[method]=timer.ElapsedMilliseconds+30000;
            if(first)log("Unity hook unavailable: "+method.DeclaringType.FullName+"."+method.Name+" ("+RootError(exception).GetType().Name+"); scanner fallback active");
        }
        static void TextPrefix(object __instance,ref string __0,MethodBase __originalMethod)
        {
            var runtime=current;if(runtime==null||runtime.stopped||runtime.writing||__instance==null)return;
            object priorTarget=runtime.completeCallTarget;string priorSource=runtime.completeCallSource;bool priorSetter=runtime.insideTextSetter,priorPropertySetter=runtime.insideTextPropertySetter;
            try
            {
                bool complete=runtime.displaySources!=null&&runtime.displaySources.IsCompleteSource(__originalMethod,__0);
                runtime.completeCallTarget=complete?__instance:null;runtime.completeCallSource=complete?__0:null;
                runtime.insideTextSetter=true;
                runtime.insideTextPropertySetter=__originalMethod!=null&&__originalMethod.Name=="set_text";
                if(complete)runtime.completeCallCaptures++;
                string original=__0;__0=runtime.Capture(__instance,__0);
                if(original!=__0){runtime.prefixCacheHits++;if(complete)runtime.completeCallCacheHits++;}
            }
            catch{}
            finally{runtime.completeCallTarget=priorTarget;runtime.completeCallSource=priorSource;runtime.insideTextSetter=priorSetter;runtime.insideTextPropertySetter=priorPropertySetter;}
        }
        static void EnablePrefix(object __instance)
        {
            var runtime=current;if(runtime==null||runtime.stopped||runtime.writing||__instance==null)return;
            try{var property=__instance.GetType().GetProperty("text");if(property==null)return;if(runtime.CaptureExisting(__instance,property))runtime.enableCacheHits++;}catch{}
        }
        static void RenderPrefix(object __instance)
        {
            var runtime=current;if(runtime==null||runtime.stopped||runtime.writing||__instance==null)return;
            try{var property=__instance.GetType().GetProperty("text");if(property==null)return;if(runtime.CaptureExisting(__instance,property))runtime.renderCacheHits++;}catch{}
        }
        string Capture(object target,string source)
        {
            if(source==null||!Alive(target)||IsInput(target)||RebuildingGraphics())return source;
            var property=target.GetType().GetProperty("text");if(property==null||!property.CanRead||!property.CanWrite)return source;
            int id=Id(target);if(id==0)return source;Binding binding;
            if(bindings.TryGetValue(id,out binding)&&!ReferenceEquals(binding.Target,target))
            {
                // IL2CPP can produce another managed wrapper for the same native object.
                if(!Alive(binding.Target)||Id(binding.Target)!=id){ReleaseProcessor(binding);bindings.Remove(id);}else binding.Target=target;
            }
            if(!bindings.TryGetValue(id,out binding))
            {
                if(bindings.Count>12000)Prune();if(bindings.Count>12000)return source;
                binding=new Binding{Id=id,EvidenceScope=++nextEvidenceScope,Target=target,Text=property};bindings[id]=binding;
                lateTargets.Enqueue(binding);
                if(lateTargets.Count>16000)CompactLateTargets();
            }
            bool active=Active(target);if(active&&!binding.LastActive&&Eligible(binding.Source))binding.Latency.Begin(timer.ElapsedMilliseconds);binding.LastActive=active;binding.LastObserved=timer.ElapsedTicks;
            binding.Revealing=IsRevealing(target);binding.FontBlocked=false;
            if(BufferedDisplay(binding,source)||LegacyDisplay(binding,source)||NativeDisplay(binding,source))return source;
            // Preserve this particular assignment across activation/font readiness,
            // but an unproved game setter (even with the same word) ends it.
            // Thus an independent writer cannot inherit a menu literal's evidence.
            bool completeCall=insideTextSetter&&ReferenceEquals(completeCallTarget,target)&&completeCallSource==source;
            if(insideTextSetter)
            {
                if(!completeCall&&binding.ProvenLiteralSource!=null)
                {
                    // An earlier literal may have spent seconds on screen. Its
                    // elapsed stability must not authorize a later unknown writer.
                    stableSources.Forget(binding.EvidenceScope,binding.ProvenLiteralSource);
                    binding.Incremental.Reset();
                }
                binding.ProvenLiteralSource=completeCall?source:null;
            }
            else if(source!=binding.ProvenLiteralSource&&!OwnedEcho(binding,source))binding.ProvenLiteralSource=null;
            // A game may echo the already displayed value. Keep our ownership in that case.
            if(OwnedEcho(binding,source))
            {
                if(enabled)
                {
                    if(!binding.LastActive||binding.Revealing)return source;
                    // Cache revisions and a newly enabled profile must resolve from
                    // the owned original, not repeatedly echo the old displayed value.
                    string revised;
                    if(TryTranslation(binding.Source??"",out revised)&&PrepareFont(binding,target,revised))
                    {revised+=binding.DisplaySuffix??"";binding.Applied=revised;return revised;}
                }
                RestoreFont(binding,target);binding.Applied=null;
                if(enabled&&Eligible(binding.Source))QueueText(target,binding.Source);
                return binding.RestoreSource??binding.Source;
            }
            string rawSource=source,complete;
            bool reveal=CompleteRevealText.TryGet(source,out complete);
            if(reveal)source=complete;
            bool sameSource=binding.Source==source;string priorApplied=binding.Applied;
            binding.RestoreSource=rawSource;binding.CompleteReveal=reveal;
            if(reveal&&!sameSource)completeRevealCaptures++;
            // A HUD can reassign the same source every frame. Retain a verified
            // fallback for that source instead of restoring/reassigning its font
            // repeatedly; a different source still releases our exact ownership.
            if(!sameSource||!enabled||!binding.LastActive)RestoreFont(binding,target);
            if(!sameSource)
            {
                if(Eligible(source))binding.Latency.Begin(timer.ElapsedMilliseconds);
                if(Eligible(source)&&rawCaptureCount<int.MaxValue)rawCaptureCount++;
                binding.ConfirmedSource=null;binding.ConfirmedText=null;binding.EarlyTmpReveal=false;
            }
            binding.Source=source;binding.Applied=null;binding.DisplaySuffix=null;
            binding.KnownCompleteBuffer=binding.Revealing&&CanReplacePlainTmpReveal(target,source,source,null);
            bool settled=ObserveSource(binding,source);
            if(!Eligible(source))return rawSource;
            if(enabled)
            {
                bool provedLiteral=binding.ProvenLiteralSource==rawSource;
                if(!settled&&!provedLiteral&&!CanReuseObservedSource(binding,source))return rawSource;
                if(!binding.LastActive){QueueText(target,source,provedLiteral);return rawSource;}
                
                string translated;if(TryTranslation(source,out translated))
                {
                    // Equal plain-text shape can retain the game's reveal counters.
                    // Unknown/different-length writers keep the conservative wait.
                    if(binding.Revealing&&!CanReplacePlainTmpReveal(target,source,translated,sameSource&&priorApplied==translated?priorApplied:null))
                    {RestoreFont(binding,target);QueueText(target,source,provedLiteral);return rawSource;}
                    if(PrepareFont(binding,target,translated))
                    {
                        if(binding.Revealing&&!binding.EarlyTmpReveal){earlyTmpRevealSubstitutions++;binding.EarlyTmpReveal=true;}
                        binding.Applied=translated;return translated;
                    }
                    return rawSource;
                }
                RestoreFont(binding,target);QueueText(target,source,provedLiteral);
            }
            return rawSource;
        }
        static bool OwnedEcho(Binding binding,string value)
        {
            // Some writers read back text and append layout padding. Only accept
            // an exact value owned by this binding followed by horizontal spaces;
            // never trim arbitrary source/cache keys or replace the last original.
            if(binding.Applied==null||value==null)return false;
            if(value==binding.Applied)return true;
            if(value.Length>6000||value.Length<=binding.Applied.Length||!value.StartsWith(binding.Applied,StringComparison.Ordinal))return false;
            for(int i=binding.Applied.Length;i<value.Length;i++)if(value[i]!=' '&&value[i]!='\t')return false;
            binding.DisplaySuffix=(binding.DisplaySuffix??"")+value.Substring(binding.Applied.Length);
            binding.Applied=value;return true;
        }
        bool BufferedDisplay(Binding binding,string source)
        {
            if(bufferedProjection==null||!bufferedProjection.Supports(binding.Target))return false;
            binding.BufferedProjection=true;
            if(insideTextSetter)binding.BufferedSetText=!insideTextPropertySetter;
            string old=binding.PreparedDisplay,prior=binding.Source;
            ObserveNativeInput(binding,source);bool settled=ObserveSource(binding,source);
            string text=null;if(enabled&&Eligible(source)){TryTranslation(source,out text);}
            if(text!=null&&!PrepareFont(binding,binding.Target,text))text=null;
            if(text==null)RestoreFont(binding,binding.Target);binding.PreparedDisplay=text;
            if(old!=text||prior!=source)DirtyNativeDisplay(binding);
            if(enabled&&text==null&&settled&&Eligible(source))QueueText(binding.Target,source);
            return true;
        }
        string BufferedDisplayText(object target,string source)
        {
            if(stopped||source==null)return source;
            Binding binding;bindings.TryGetValue(Id(target),out binding);
            // A converter may run without the public setter. Capture its full
            // argument while retaining game-owned source and reveal counters.
            if((binding==null||binding.Source!=source)&&!RebuildingGraphics())
            {Capture(target,source);bindings.TryGetValue(Id(target),out binding);}
            if(binding==null||!binding.BufferedProjection||!enabled||binding.Source!=source||binding.PreparedDisplay==null)return source;
            if(binding.RenderedSource!=source||binding.RenderedText!=binding.PreparedDisplay)nativeProcessorHits++;
            binding.RenderedSource=source;binding.RenderedText=binding.PreparedDisplay;return binding.PreparedDisplay;
        }
        void RefreshBuffered(Binding binding)
        {
            if(RebuildingGraphics()||!Alive(binding.Target)||timer.ElapsedMilliseconds<binding.BufferedRetryAt)return;
            string source=binding.BufferedSetText?binding.Source:ReadText(binding.Target,binding.Text);
            string old=binding.PreparedDisplay;BufferedDisplay(binding,source);
            if(old==binding.PreparedDisplay)return;
            // Rebuild through the same public buffer entry point. Never assign
            // .text, which games can use as an identifier independent of SetText.
            var method=binding.Target.GetType().GetMethod("SetText",new[]{typeof(string),typeof(bool)});
            if(method==null)return;
            bool prior=writing;writing=true;
            try{method.Invoke(binding.Target,new object[]{source,true});}
            catch
            {
                // A live Unity wrapper can outlast TMP's internal buffers during
                // scene teardown. One failed control must not abort restoration
                // of all remaining controls or the bridge's disposal.
                binding.PreparedDisplay=old;binding.RenderedSource=null;binding.RenderedText=null;
                binding.BufferedRetryAt=timer.ElapsedMilliseconds+1000;writebackFailed++;
            }
            finally{writing=prior;}
        }
        bool RebuildingGraphics()
        {
            if(LegacyTextProjection.IsDrawing)return true;
            try{return rebuildingGraphics!=null&&(bool)rebuildingGraphics.Invoke(null,null);}catch{return false;}
        }
        bool LegacyDisplay(Binding binding,string source)
        {
            if(legacyProjection==null||!legacyProjection.Supports(binding.Target))return false;
            string rawSource=source,complete;
            bool reveal=CompleteRevealText.TryGet(source,out complete);
            if(reveal)source=complete;
            // A segmented writer reads .text before appending its next segment.
            // Even an explicit hidden suffix must therefore stay a render-only
            // projection: replacing .text contaminates the next source buffer.
            if(binding.Applied!=null){binding.LegacyProjection=false;return false;}
            binding.LegacyProjection=true;
            string oldDisplay=binding.PreparedDisplay,oldSource=binding.Source;
            ObserveNativeInput(binding,source);binding.RestoreSource=rawSource;binding.CompleteReveal=reveal;
            if(reveal&&oldSource!=source)completeRevealCaptures++;
            bool settled=ObserveSource(binding,source);
            string text=null;
            if(enabled&&Eligible(source)){TryTranslation(source,out text);}
            if(text!=null&&!PrepareFont(binding,binding.Target,text))text=null;
            if(text==null)RestoreFont(binding,binding.Target);
            else text=PrepareIndent(binding,binding.Target,binding.Layout.DisplayText??text);
            binding.PreparedDisplay=text;
            if(oldDisplay!=text||oldSource!=source)DirtyNativeDisplay(binding);
            if(enabled&&text==null&&settled&&Eligible(source))QueueText(binding.Target,source);
            return true;
        }
        string LegacyDisplayText(object target,string source)
        {
            if(stopped||!enabled)return source;
            Binding binding;
            // Fonts and layout were prepared by the setter/enable/late update.
            // A mesh pass can only read that prepared result, never dirty itself.
            if(!bindings.TryGetValue(Id(target),out binding)||!binding.LegacyProjection||binding.RestoreSource!=source||binding.PreparedDisplay==null)return source;
            if(binding.RenderedSource!=binding.Source||binding.RenderedText!=binding.PreparedDisplay)legacyProjectionHits++;
            binding.RenderedSource=binding.Source;binding.RenderedText=binding.PreparedDisplay;ReadbackReady(binding);
            return binding.PreparedDisplay;
        }
        float LegacyPreferredSize(object target,bool horizontal)
        {
            Binding binding;
            if(enabled&&!stopped&&legacyProjection!=null&&legacyProjection.Supports(target)&&Alive(target)&&!IsInput(target))
            {
                int id=Id(target);
                if(id!=0&&!bindings.TryGetValue(id,out binding)&&bindings.Count<12000)
                {
                    var property=target.GetType().GetProperty("text");
                    string source=property==null?null:ReadText(target,property);
                    if(Eligible(source))
                    {
                        binding=new Binding{Id=id,EvidenceScope=++nextEvidenceScope,Target=target,Text=property,Source=source,RestoreSource=source,LegacyProjection=true};
                        bindings[id]=binding;lateTargets.Enqueue(binding);
                        binding.Layout.Capture(target,source);
                    }
                }
            }
            return enabled&&bindings.TryGetValue(Id(target),out binding)&&binding.LegacyProjection&&binding.Layout.Region.Ready
                ?binding.Layout.Region.PreferredSize(target,horizontal):-1;
        }
        object LegacyGenerationSettings(object target,object settings)
        {
            Binding binding;
            return enabled&&bindings.TryGetValue(Id(target),out binding)&&binding.LegacyProjection&&binding.PreparedDisplay!=null
                ?binding.Layout.Region.Project(settings):settings;
        }
        bool OwnsLegacyCjk(object target)
        {
            Binding binding;
            return enabled&&!stopped&&Alive(target)&&bindings.TryGetValue(Id(target),out binding)
                &&binding.LegacyProjection&&binding.PreparedDisplay!=null
                &&binding.PreparedDisplay.Any(c=>c>=0x2e80&&c<=0x9fff);
        }
        float LegacyDisplayDensity(object target,float native)
        {
            Binding binding;
            return OwnsLegacyCjk(target)&&bindings.TryGetValue(Id(target),out binding)?Math.Max(native,binding.RasterDensity):native;
        }
        void PrepareLegacyDensity(Binding binding,object target,object font)
        {
            if(!binding.LastActive||legacyProjection==null||!legacyProjection.DensityAvailable||!legacyProjection.Supports(target)||font==null||font.GetType().FullName!="UnityEngine.Font")return;
            try
            {
                var dynamic=font.GetType().GetProperty("dynamic");if(dynamic==null||!(bool)dynamic.GetValue(font,null))return;
                if(timer.ElapsedMilliseconds<binding.RasterCheckAt)return;binding.RasterCheckAt=timer.ElapsedMilliseconds+200;
                float density=LegacyRasterDensity.Measure(target,LegacyRasterScale(target,font),LegacySize(target));
                if(density==binding.RasterDensity)return;binding.RasterDensity=density;binding.AtlasText=null;
                if(binding.PreparedDisplay!=null)DirtyNativeDisplay(binding);
            }catch{}
        }
        bool NativeDisplay(Binding binding,string source)
        {
            var reader=Reader(binding.Target);
            if(reader==null||reader.InputMode==null)return false;
            string mode;try{mode=Convert.ToString(ReadMember(reader.InputMode,binding.Target));}catch{return false;}
            // Before a managed setter runs, m_inputSource still describes the
            // previous assignment. Property setters will use the preprocessor;
            // SetText overloads bypass it and retain the buffered-text path.
            if(!il2Cpp&&insideTextSetter)mode=insideTextPropertySetter?"TextString":"SetText";
            if(mode!="TextInputBox"&&mode!="TextString")
            {
                // SetText/array inputs bypass TMP preprocessing. Release our
                // rendering ownership before the established buffered-text path.
                if(binding.Processor!=null){ReleaseProcessor(binding);RestoreFont(binding,binding.Target);}
                return false;
            }
            // A previous buffered writeback still owns this property. Keep its
            // original-source bookkeeping until the game assigns another value.
            if(binding.Applied!=null&&OwnedEcho(binding,source))return false;
            if(binding.Processor!=null&&!binding.Processor.IsInstalled){binding.Processor.Dispose();binding.Processor=null;}
            if(binding.Processor==null&&enabled&&!nativeProcessorUnavailable)
            {
                try
                {
                    binding.Processor=NativeTmpPreprocessor.Attach(binding.Target,s=>NativeDisplayText(binding,s));
                    if(binding.Processor!=null){nativeProcessorBindings++;DirtyNativeDisplay(binding);}
                }
                catch(Exception ex){nativeProcessorUnavailable=true;log("Unity TMP preprocessor unavailable: "+RootError(ex).GetType().Name);}
            }
            if(binding.Processor==null)return false;
            ObserveNativeInput(binding,source);
            bool settled=ObserveSource(binding,source);
            string text=null;
            if(enabled&&Eligible(source)&&(!il2Cpp||!NativeRevealing(binding,source))){TryTranslation(source,out text);}
            if(text!=null&&!PrepareFont(binding,binding.Target,text))text=null;
            if(text==null)RestoreFont(binding,binding.Target);
            if(text!=binding.PreparedDisplay){binding.PreparedDisplay=text;DirtyNativeDisplay(binding);}
            if(enabled&&text==null&&settled&&Eligible(source))QueueText(binding.Target,source);
            return true;
        }
        string NativeDisplayText(Binding binding,string source)
        {
            // TMP passes the rendering input before parsing and auto-size layout.
            // Return a projection only; m_text and game read/append operations
            // retain the original. A cache lookup needs no writer-stability delay
            // because it cannot replace the writer's source or append buffer.
            if(stopped||!Alive(binding.Target))return source;
            ObserveNativeInput(binding,source);
            string text;
            if(!enabled||!Eligible(source)||(il2Cpp&&NativeRevealing(binding,source))||!TryTranslation(source,out text)||!PrepareFont(binding,binding.Target,text))
            {RestoreFont(binding,binding.Target);binding.RenderedSource=null;binding.RenderedText=null;binding.PreparedDisplay=null;return source;}
            if(binding.RenderedSource!=source||binding.RenderedText!=text)nativeProcessorHits++;
            binding.RenderedSource=source;binding.RenderedText=text;binding.PreparedDisplay=text;ReadbackReady(binding);
            return text;
        }
        void ObserveNativeInput(Binding binding,string source)
        {
            if(binding.Source!=source)
            {
                if(Eligible(source))binding.Latency.Begin(timer.ElapsedMilliseconds);
                RestoreFont(binding,binding.Target);binding.RenderedSource=null;binding.RenderedText=null;binding.PreparedDisplay=null;
                binding.ConfirmedSource=null;binding.ConfirmedText=null;binding.ProvenLiteralSource=null;
                if(Eligible(source)&&rawCaptureCount<int.MaxValue)rawCaptureCount++;
            }
            binding.Source=source;binding.RestoreSource=source;binding.Applied=null;binding.DisplaySuffix=null;
            binding.CompleteReveal=false;binding.KnownCompleteBuffer=false;
        }
        static bool NativeRevealing(Binding binding,string source)
        {
            // On a brand-new mesh textInfo may still report zero/old characters.
            // Keep finite reveal limits authoritative before that first parse.
            var reader=Reader(binding.Target);bool revealing=IsRevealing(binding.Target);
            try{if(reader!=null&&reader.MaxVisible!=null&&source!=null)revealing|=Convert.ToInt32(reader.MaxVisible.GetValue(binding.Target,null))<source.Length;}catch{revealing=true;}
            binding.Revealing=revealing;return revealing;
        }
        static void ReleaseProcessor(Binding binding)
        {
            if(binding.Processor==null)return;binding.Processor.Dispose();binding.Processor=null;
            binding.PreparedDisplay=null;binding.RenderedSource=null;binding.RenderedText=null;
            if(Alive(binding.Target))DirtyNativeDisplay(binding);
        }
        static void DirtyNativeDisplay(Binding binding)
        {
            try
            {
                var target=binding.Target;var changed=target.GetType().GetProperty("havePropertiesChanged");
                if(changed!=null&&changed.CanWrite)changed.SetValue(target,true,null);
                var dirty=target.GetType().GetMethod("SetVerticesDirty",Type.EmptyTypes);if(dirty!=null)dirty.Invoke(target,null);
                var layout=target.GetType().GetMethod("SetLayoutDirty",Type.EmptyTypes);if(layout!=null)layout.Invoke(target,null);
            }catch{}
        }
        bool CaptureExisting(object target,PropertyInfo property)
        {
            if(!Alive(target))return false;
            Binding buffered;if(bindings.TryGetValue(Id(target),out buffered)&&buffered.BufferedProjection&&buffered.BufferedSetText){RefreshBuffered(buffered);return false;}
            string source;
            try{source=ReadText(target,property);}catch(Exception ex){RecordDiscoveryFailure(target.GetType(),"text-read",ex);return false;}
            if(source==null)return false;
            Binding previous;bindings.TryGetValue(Id(target),out previous);string priorApplied=previous==null?null:previous.Applied;
            string displayed=Capture(target,source);
            if(displayed==source){ConfirmBinding(target,source);return false;}
            bool written=false;
            try{written=Write(target,property,displayed);}catch(Exception ex){RecordDiscoveryFailure(target.GetType(),"text-writeback",ex);}
            if(written){ConfirmBinding(target,displayed);return true;}
            writebackFailed++;Binding binding;if(bindings.TryGetValue(Id(target),out binding))
            {binding.Applied=source==priorApplied?priorApplied:null;if(binding.Applied==null)RestoreFont(binding,target);}
            return false;
        }
        void ConfirmBinding(object target,string observed)
        {
            Binding binding;if(!bindings.TryGetValue(Id(target),out binding)||binding.Applied==null||observed!=binding.Applied)return;
            // Repeated scans of our owned display still advance observation time
            // from the original. They never treat translated echo as new source.
            ObserveSource(binding,binding.Source);
            if(Eligible(binding.Source)&&binding.Source==binding.Incremental.ExactObservedSource&&(binding.Incremental.IsAuthoritativeComplete||
                binding.Incremental.IsReady&&binding.Incremental.StableForMilliseconds>=IncrementalTextState.MaximumStableMilliseconds))
                stableSources.Remember(binding.EvidenceScope,binding.Source);
            if(binding.ConfirmedSource==binding.Source&&binding.ConfirmedText==observed)return;
            binding.ConfirmedSource=binding.Source;binding.ConfirmedText=observed;ReadbackReady(binding);
            if(writebackConfirmed<int.MaxValue)writebackConfirmed++;
        }
        // TMP's SetText(StringBuilder/char[]) keeps its authoritative input in a
        // backing array. In some TMP builds text's getter consumes its dirty flag
        // but does not update m_text, so a second read returns a previous line.
        // Read the known TMP input buffer only for those input modes. This keeps
        // markup intact and does not touch a game's dialogue/controller fields.
        sealed class TmpReader
        {
            internal MemberInfo InputMode,DirtyFlag,CharacterCount;
            internal MethodInfo BackingText;
            internal PropertyInfo MaxVisible,TextInfo;
        }
        static readonly Dictionary<Type,TmpReader> tmpReaders=new Dictionary<Type,TmpReader>();
        static readonly Dictionary<string,Type> knownTypes=new Dictionary<string,Type>(StringComparer.Ordinal);
        static readonly object typeLock=new object();
        static MemberInfo KnownMember(Type type,string name)
        {return (MemberInfo)type.GetField(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly)??type.GetProperty(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly);}
        static object ReadMember(MemberInfo member,object target)
        {var field=member as FieldInfo;return field!=null?field.GetValue(target):((PropertyInfo)member).GetValue(target,null);}
        static bool WritableBoolean(MemberInfo member)
        {var field=member as FieldInfo;var property=member as PropertyInfo;return field!=null?field.FieldType==typeof(bool)&&!field.IsInitOnly:property!=null&&property.PropertyType==typeof(bool)&&property.CanRead&&property.CanWrite;}
        static void WriteMember(MemberInfo member,object target,object value)
        {var field=member as FieldInfo;if(field!=null)field.SetValue(target,value);else ((PropertyInfo)member).SetValue(target,value,null);}
        static TmpReader Reader(object target)
        {
            var type=target.GetType();TmpReader reader;
            if(tmpReaders.TryGetValue(type,out reader))return reader;
            var tmp=FindType("TMPro.TMP_Text");
            if(tmp==null||!tmp.IsAssignableFrom(type)){tmpReaders[type]=null;return null;}
            reader=new TmpReader{InputMode=KnownMember(tmp,"m_inputSource"),DirtyFlag=KnownMember(tmp,"m_IsTextBackingStringDirty"),
                BackingText=tmp.GetMethod("InternalTextBackingArrayToString",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly,null,Type.EmptyTypes,null),
                MaxVisible=tmp.GetProperty("maxVisibleCharacters"),TextInfo=tmp.GetProperty("textInfo")};
            var info=FindType("TMPro.TMP_TextInfo");if(info!=null)reader.CharacterCount=KnownMember(info,"characterCount");
            if(reader.BackingText!=null&&reader.BackingText.ReturnType!=typeof(string))reader.BackingText=null;
            tmpReaders[type]=reader;return reader;
        }
        static string ReadText(object target,PropertyInfo property)
        {
            var reader=Reader(target);
            if(reader!=null&&WritableBoolean(reader.DirtyFlag))
            {
                try
                {
                    bool dirty=(bool)ReadMember(reader.DirtyFlag,target);
                    try
                    {
                        string mode=reader.InputMode==null?null:Convert.ToString(ReadMember(reader.InputMode,target));
                        if(mode=="SetText"||mode=="SetTextArray")
                        {
                            if(reader.BackingText!=null)
                                try{return reader.BackingText.Invoke(target,null) as string;}catch{}
                            // Once consumed, the ordinary getter may contain an older
                            // m_text rather than this buffer. Do not claim that as new text.
                            if(!dirty)return null;
                            WriteMember(reader.DirtyFlag,target,true);
                        }
                        return property.GetValue(target,null) as string;
                    }
                    // Both the buffer converter and fallback getter can consume the
                    // flag. Observation must retain the game's exact pre-read state.
                    finally{WriteMember(reader.DirtyFlag,target,dirty);}
                }catch{return null;}
            }
            // Exactly one getter read per observation, including hook diagnostics.
            return property.GetValue(target,null) as string;
        }
        static bool IsRevealing(object target)
        {
            var reader=Reader(target);if(reader==null||reader.MaxVisible==null||reader.TextInfo==null||reader.CharacterCount==null)return false;
            try
            {
                int shown=Convert.ToInt32(reader.MaxVisible.GetValue(target,null));if(shown==int.MaxValue)return false;
                var info=reader.TextInfo.GetValue(target,null);if(info==null)return false;
                int total=Convert.ToInt32(ReadMember(reader.CharacterCount,info));
                // Queue the complete buffer, but retain the original text and font
                // while the game's writer still owns its character-by-character reveal.
                return total>0&&shown<total;
            }catch{return false;}
        }
        static bool PlainRevealShape(string source,string translated)
        {
            if(string.IsNullOrEmpty(source)||source.Length>512||translated==null||source.Length!=translated.Length)return false;
            for(int i=0;i<source.Length;i++)
            {
                char a=source[i],b=translated[i];
                if((a=='\n'||a=='\r'||b=='\n'||b=='\r')&&a!=b)return false;
                foreach(char c in new[]{a,b})
                {
                    if(c=='<'||c=='>'||char.IsSurrogate(c)||(char.IsControl(c)&&c!='\n'&&c!='\r'))return false;
                    var kind=char.GetUnicodeCategory(c);
                    if(kind==System.Globalization.UnicodeCategory.NonSpacingMark||kind==System.Globalization.UnicodeCategory.SpacingCombiningMark||
                       kind==System.Globalization.UnicodeCategory.EnclosingMark||kind==System.Globalization.UnicodeCategory.Format)return false;
                }
            }
            return true;
        }
        static bool VisibleLimit(object target,Type tmp,string name,int minimum,bool exact=false)
        {
            var property=tmp.GetProperty(name);if(property==null||!property.CanRead||property.PropertyType!=typeof(int))return false;
            int value=(int)property.GetValue(target,null);return exact?value==minimum:value>=minimum;
        }
        bool CanReplacePlainTmpReveal(object target,string source,string translated,string ownedLayout)
        {
            if(!PlainRevealShape(source,translated))return false;
            try
            {
                var reader=Reader(target);var tmp=FindType("TMPro.TMP_Text");if(reader==null||tmp==null||reader.TextInfo==null||reader.MaxVisible==null)return false;
                int shown=Convert.ToInt32(reader.MaxVisible.GetValue(target,null));if(shown<0||shown>=source.Length)return false;
                if(!VisibleLimit(target,tmp,"firstVisibleCharacter",0,true)||!VisibleLimit(target,tmp,"maxVisibleWords",source.Length)||!VisibleLimit(target,tmp,"maxVisibleLines",source.Length))return false;
                var rtl=tmp.GetProperty("isRightToLeftText");var overflow=tmp.GetProperty("overflowMode");
                if(rtl==null||rtl.PropertyType!=typeof(bool)||(bool)rtl.GetValue(target,null)||overflow==null)return false;
                string mode=Convert.ToString(overflow.GetValue(target,null));if(mode!="Overflow"&&mode!="Truncate")return false;
                var info=reader.TextInfo.GetValue(target,null);if(info==null||reader.CharacterCount==null||Convert.ToInt32(ReadMember(reader.CharacterCount,info))!=source.Length)return false;
                var infoType=FindType("TMPro.TMP_TextInfo");var characterType=FindType("TMPro.TMP_CharacterInfo");
                if(infoType==null||characterType==null)return false;
                var arrayMember=KnownMember(infoType,"characterInfo");var character=KnownMember(characterType,"character");
                if(arrayMember==null||character==null)return false;
                var glyphs=ReadMember(arrayMember,info);if(glyphs==null)return false;
                var array=glyphs as Array;MethodInfo item=null;
                if(array!=null){if(array.Rank!=1||array.GetType().GetElementType()!=characterType||array.Length<source.Length)return false;}
                else
                {
                    var arrayType=glyphs.GetType();
                    if(!arrayType.IsGenericType||arrayType.GetGenericTypeDefinition().FullName!="Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray`1"||arrayType.GetGenericArguments()[0]!=characterType)return false;
                    var length=arrayType.GetProperty("Length");if(length==null||Convert.ToInt32(length.GetValue(glyphs,null))<source.Length)return false;
                    item=arrayType.GetMethod("get_Item",new[]{typeof(int)});if(item==null||item.ReturnType!=characterType)return false;
                }
                bool original=true,owned=ownedLayout!=null;long until=timer.ElapsedTicks+Stopwatch.Frequency/500;
                for(int i=0;i<source.Length;i++)
                {
                    if(timer.ElapsedTicks>=until)return false;
                    var glyph=array!=null?array.GetValue(i):item.Invoke(glyphs,new object[]{i});var value=ReadMember(character,glyph);
                    if(!(value is char))return false;char c=(char)value;
                    original&=c==source[i];owned&=ownedLayout!=null&&c==ownedLayout[i];if(!original&&!owned)return false;
                }
                // Never alter maxVisible*, firstVisibleCharacter, owner fields,
                // coroutine state or dialogue callbacks. The exact parsed source
                // (or our still-owned equal-shaped display) must identify the buffer.
                return original||owned;
            }catch{return false;}
        }
        void Discover()
        {
            RefreshNativeCapabilities();
            if(discovered.Count>0||textTypes.Count==0)return;
            // Resource-wide discovery includes inactive prefabs. Prefer scene objects
            // so a large preloaded menu does not postpone a newly visible dialogue.
            // Rotate component families rather than issuing several full scans in one frame.
            Type type=null;for(int count=0;count<textTypes.Count;count++){var candidate=textTypes[scanCursor++%textTypes.Count];if(!il2Cpp||!unsupportedNativeTypes.Contains(candidate)){type=candidate;break;}}
            if(type==null){Prune();return;}
            try{var items=FindSceneObjects(type);if(items!=null)foreach(var item in items){if(discovered.Count>=16000)break;if(item!=null)discovered.Enqueue(item);}}catch(Exception ex){RecordDiscoveryFailure(type,"scene-query",ex);}
            Prune();
        }

        void DiscoverAhead()
        {
            if(aheadEnumeration!=null||aheadDiscovered.Count>0||textTypes.Count==0)return;
            Type type=textTypes[aheadCursor++%textTypes.Count];if(il2Cpp&&unsupportedNativeTypes.Contains(type))return;
            try
            {
                var resources=FindType("UnityEngine.Resources");if(resources==null)return;IEnumerable items=null;
                if(il2Cpp)
                {
                    object nativeType;
                    if(!nativeTypes.TryGetValue(type,out nativeType)){FindNativeSceneObjects(type);if(!nativeTypes.TryGetValue(type,out nativeType))return;}
                    var method=resources.GetMethods(BindingFlags.Public|BindingFlags.Static).FirstOrDefault(m=>m.Name=="FindObjectsOfTypeAll"&&!m.IsGenericMethod&&m.GetParameters().Length==1&&m.GetParameters()[0].ParameterType.IsInstanceOfType(nativeType));
                    if(method!=null){items=NativeQuery(method,new[]{nativeType},type);if(items!=null)items=CastNativeObjects(items,type,false);}
                }
                else
                {
                    var method=resources.GetMethods(BindingFlags.Public|BindingFlags.Static).FirstOrDefault(m=>m.Name=="FindObjectsOfTypeAll"&&m.IsGenericMethodDefinition&&m.GetParameters().Length==0);
                    if(method!=null)items=method.MakeGenericMethod(type).Invoke(null,null) as IEnumerable;
                }
                if(items==null)return;
                // Keep our place in this bounded engine-owned snapshot. Restarting
                // from element zero after every small budget starved later menus.
                aheadEnumeration=items.GetEnumerator();aheadEnumerationType=type;
                aheadInspected=0;aheadExpires=timer.ElapsedMilliseconds+15000;
            }
            catch(Exception ex){RecordDiscoveryFailure(type,"ahead-scene-query",ex);}
        }
        void PumpAheadDiscovery()
        {
            if(aheadEnumeration==null)return;
            if(timer.ElapsedMilliseconds>=aheadExpires){ClearAheadDiscovery();return;}
            long until=timer.ElapsedTicks+Stopwatch.Frequency/1000;
            try
            {
                for(int count=0;count<64&&aheadDiscovered.Count<256&&timer.ElapsedTicks<until;count++)
                {
                    if(aheadInspected++>=4096||!aheadEnumeration.MoveNext()){ClearAheadDiscovery();return;}
                    var target=aheadEnumeration.Current;
                    if(Alive(target)&&!Active(target)&&InLoadedScene(target))aheadDiscovered.Enqueue(target);
                }
            }
            catch(Exception ex){RecordDiscoveryFailure(aheadEnumerationType,"ahead-enumeration",ex);ClearAheadDiscovery();}
        }
        void ClearAheadDiscovery()
        {
            var disposable=aheadEnumeration as IDisposable;try{if(disposable!=null)disposable.Dispose();}catch{}
            aheadEnumeration=null;aheadEnumerationType=null;aheadInspected=0;
        }
        static bool InLoadedScene(object target)
        {
            // Resources also contains prefabs/assets. Only known Unity component
            // scene identities may enter ahead capture; no arbitrary graph traversal.
            try
            {
                var component=FindType("UnityEngine.Component");if(component==null||!component.IsInstanceOfType(target))return false;
                var gameObject=component.GetProperty("gameObject");var go=gameObject==null?null:gameObject.GetValue(target,null);if(!Alive(go))return false;
                var sceneProperty=go.GetType().GetProperty("scene");var scene=sceneProperty==null?null:sceneProperty.GetValue(go,null);if(scene==null)return false;
                if(scene.GetType().FullName!="UnityEngine.SceneManagement.Scene")return false;
                var valid=scene.GetType().GetMethod("IsValid",Type.EmptyTypes);var loaded=scene.GetType().GetProperty("isLoaded");
                return valid!=null&&valid.ReturnType==typeof(bool)&&(bool)valid.Invoke(scene,null)&&loaded!=null&&loaded.PropertyType==typeof(bool)&&(bool)loaded.GetValue(scene,null);
            }catch{return false;}
        }

        IEnumerable FindSceneObjects(Type type)
        {
            if(il2Cpp)return FindNativeSceneObjects(type);
            var objects=FindType("UnityEngine.Object");
            if(objects!=null)
            {
                // New Unity exposes an unsorted scene query; use it when present,
                // without introducing a compile-time dependency on a new engine.
                var modern=objects.GetMethods(BindingFlags.Public|BindingFlags.Static).FirstOrDefault(m=>m.Name=="FindObjectsByType"&&m.IsGenericMethodDefinition&&m.GetParameters().Length==1&&m.GetParameters()[0].ParameterType.FullName=="UnityEngine.FindObjectsSortMode");
                if(modern!=null)try{var items=modern.MakeGenericMethod(type).Invoke(null,new[]{Enum.Parse(modern.GetParameters()[0].ParameterType,"None")}) as IEnumerable;if(items!=null)return items;}catch{}
                var legacy=objects.GetMethods(BindingFlags.Public|BindingFlags.Static).FirstOrDefault(m=>m.Name=="FindObjectsOfType"&&m.IsGenericMethodDefinition&&m.GetParameters().Length==0);
                if(legacy!=null)try{var items=legacy.MakeGenericMethod(type).Invoke(null,null) as IEnumerable;if(items!=null)return items;}catch{}
            }
            var resources=FindType("UnityEngine.Resources");var all=resources==null?null:resources.GetMethods(BindingFlags.Public|BindingFlags.Static).FirstOrDefault(m=>m.Name=="FindObjectsOfTypeAll"&&m.IsGenericMethodDefinition&&m.GetParameters().Length==0);
            var fallback=all==null?null:all.MakeGenericMethod(type).Invoke(null,null) as IEnumerable;
            // Older/resource-only queries may contain thousands of inactive prefabs.
            // They must not exhaust the discovery cap ahead of a live scene label.
            return fallback==null?null:fallback.Cast<object>().Where(Active);
        }
        IEnumerable FindNativeSceneObjects(Type type)
        {
            // Generated generic Unity helpers can contain unstripping-failed stubs
            // (notably Resources.ConvertObjects<T>). Use public native Type queries
            // and the loader's checked cast, never reconstruct native pointers.
            RefreshNativeCapabilities();if(unsupportedNativeTypes.Contains(type))return null;
            var bridge=FindType("Il2CppInterop.Runtime.Il2CppType");
            // The fixed loader's public false overload returns null for an
            // unavailable native class/type pointer. Unknown exceptions
            // must remain failures rather than permanently disabling a family.
            var optional=bridge==null?null:bridge.GetMethod("From",BindingFlags.Public|BindingFlags.Static,null,new[]{typeof(Type),typeof(bool)},null);
            if(optional!=null&&(optional.IsGenericMethod||optional.ReturnType.FullName!="Il2CppSystem.Type"))optional=null;
            var from=optional??(bridge==null?null:bridge.GetMethod("From",BindingFlags.Public|BindingFlags.Static,null,new[]{typeof(Type)},null));
            if(from==null)return null;object nativeType;if(!nativeTypes.TryGetValue(type,out nativeType))
            {
                try{nativeType=from.Invoke(null,optional==null?new object[]{type}:new object[]{type,false});}catch(Exception ex){RecordDiscoveryFailure(type,"native-type",ex);return null;}
                if(nativeType==null)
                {
                    if(optional!=null){unsupportedNativeTypes.Add(type);if(unsupportedNativeLogged.Add(type))log("Unity discovery unsupported family="+type.FullName+" reason=native-type-unavailable");}
                    return null;
                }
                nativeTypes[type]=nativeType;
            }
            var objects=FindType("UnityEngine.Object");IEnumerable items=null;
            if(objects!=null)
            {
                var modern=objects.GetMethods(BindingFlags.Public|BindingFlags.Static).FirstOrDefault(m=>m.Name=="FindObjectsByType"&&!m.IsGenericMethod&&m.GetParameters().Length==2&&m.GetParameters()[0].ParameterType.IsInstanceOfType(nativeType)&&m.GetParameters()[1].ParameterType.FullName=="UnityEngine.FindObjectsSortMode");
                if(modern!=null)items=NativeQuery(modern,new[]{nativeType,Enum.Parse(modern.GetParameters()[1].ParameterType,"None")},type);
                if(items==null)
                {
                    var legacy=objects.GetMethods(BindingFlags.Public|BindingFlags.Static).FirstOrDefault(m=>m.Name=="FindObjectsOfType"&&!m.IsGenericMethod&&m.GetParameters().Length==1&&m.GetParameters()[0].ParameterType.IsInstanceOfType(nativeType));
                    if(legacy!=null)items=NativeQuery(legacy,new[]{nativeType},type);
                }
            }
            bool all=false;
            if(items==null)
            {
                var resources=FindType("UnityEngine.Resources");var method=resources==null?null:resources.GetMethods(BindingFlags.Public|BindingFlags.Static).FirstOrDefault(m=>m.Name=="FindObjectsOfTypeAll"&&!m.IsGenericMethod&&m.GetParameters().Length==1&&m.GetParameters()[0].ParameterType.IsInstanceOfType(nativeType));
                if(method!=null){items=NativeQuery(method,new[]{nativeType},type);all=true;}
            }
            return items==null?null:CastNativeObjects(items,type,all);
        }
        IEnumerable NativeQuery(MethodInfo method,object[] args,Type family)
        {
            long retry;if(queryRetryAt.TryGetValue(method,out retry)&&timer.ElapsedMilliseconds<retry)return null;
            try{return method.Invoke(null,args) as IEnumerable;}
            catch(Exception ex){RecordDiscoveryFailure(family,"scene-query",ex);queryRetryAt[method]=timer.ElapsedMilliseconds+10000;return null;}
        }
        IEnumerable CastNativeObjects(IEnumerable items,Type type,bool activeOnly)
        {
            var wrapper=FindType("Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase");
            var method=wrapper==null?null:wrapper.GetMethods(BindingFlags.Public|BindingFlags.Instance).FirstOrDefault(m=>m.Name=="TryCast"&&m.IsGenericMethodDefinition&&m.GetGenericArguments().Length==1&&m.GetParameters().Length==0);
            MethodInfo cast=null;if(method!=null)try{cast=method.MakeGenericMethod(type);}catch{}
            IEnumerator enumerator;try{enumerator=items.GetEnumerator();}catch(Exception ex){RecordDiscoveryFailure(type,"query-enumeration",ex);yield break;}
            int inspected=0;
            try
            {
                while(inspected++<16000)
                {
                    object target;
                    try{if(!enumerator.MoveNext())yield break;target=enumerator.Current;}
                    catch(Exception ex){RecordDiscoveryFailure(type,"query-enumeration",ex);yield break;}
                    if(target!=null&&!type.IsInstanceOfType(target))try{target=cast!=null&&wrapper.IsInstanceOfType(target)?cast.Invoke(target,null):null;}catch(Exception ex){RecordDiscoveryFailure(type,"checked-cast",ex);target=null;}
                    if(target!=null&&type.IsInstanceOfType(target)&&(!activeOnly||Active(target)))yield return target;
                }
            }
            finally{var disposable=enumerator as IDisposable;if(disposable!=null)try{disposable.Dispose();}catch(Exception ex){RecordDiscoveryFailure(type,"query-enumeration",ex);}}
        }
        void RecordDiscoveryFailure(Type type,string stage,Exception exception)
        {
            string family=type==null?"unknown":type.FullName,error=RootError(exception).GetType().FullName,key=family+"|"+stage+"|"+error;
            DiscoveryFailure failure;if(!discoveryFailures.TryGetValue(key,out failure)){failure=new DiscoveryFailure{Family=family,Stage=stage,Error=error};discoveryFailures[key]=failure;}
            failure.Count++;long now=timer.ElapsedMilliseconds;
            if(failure.Count==1||now-failure.LastLogged>=10000){failure.LastLogged=now;log("Unity discovery unavailable family="+family+" stage="+stage+" error="+error+" count="+failure.Count);}
        }
        void InstallCanvasCallback()
        {
            if(canvasCallback!=null)return;
            try
            {
                var canvas=FindType("UnityEngine.Canvas");if(canvas==null)return;
                var add=canvas.GetMethod("add_preWillRenderCanvases",BindingFlags.Public|BindingFlags.Static);
                bool legacy=add==null;
                if(legacy)add=canvas.GetMethod("add_willRenderCanvases",BindingFlags.Public|BindingFlags.Static);
                if(add==null||add.GetParameters().Length!=1)return;var callbackType=add.GetParameters()[0].ParameterType;
                string eventName=legacy?"willRenderCanvases":"preWillRenderCanvases";
                var remove=canvas.GetMethod("remove_"+eventName,BindingFlags.Public|BindingFlags.Static,null,new[]{callbackType},null);if(remove==null)return;
                var support=FindType("Il2CppInterop.Runtime.DelegateSupport");
                var convert=support==null?null:support.GetMethods(BindingFlags.Public|BindingFlags.Static).FirstOrDefault(m=>m.Name=="ConvertDelegate"&&m.IsGenericMethodDefinition&&m.GetGenericArguments().Length==1&&m.GetParameters().Length==1&&m.GetParameters()[0].ParameterType==typeof(Delegate));
                if(convert==null)return;canvasManagedCallback=BeforeCanvasRender;
                var callback=convert.MakeGenericMethod(callbackType).Invoke(null,new object[]{canvasManagedCallback});if(callback==null)return;
                // Older Unity has only willRenderCanvases. Run before its UI
                // rebuild subscriber, retaining every existing callback/order.
                // The generated delegate field is available on IL2CPP builds;
                // never replace an existing chain unless it can be combined.
                if(!legacy||!PrependCanvasCallback(canvas,eventName,callbackType,callback))add.Invoke(null,new[]{callback});
                canvasCallback=callback;canvasRemove=remove;
                log("Unity bridge pre-render=canvas-public-event event="+eventName);
            }catch(Exception ex){canvasManagedCallback=null;log("Unity pre-render unavailable: "+RootError(ex).GetType().Name);}
        }
        static bool PrependCanvasCallback(Type canvas,string name,Type callbackType,object callback)
        {
            var property=canvas.GetProperty(name,BindingFlags.Public|BindingFlags.Static);
            if(property==null||!property.CanRead||!property.CanWrite||property.PropertyType!=callbackType)return false;
            object existing=property.GetValue(null,null),combined=callback;
            if(existing!=null)
            {
                var nativeDelegate=FindType("Il2CppSystem.Delegate");
                var combine=nativeDelegate==null?null:nativeDelegate.GetMethod("Combine",BindingFlags.Public|BindingFlags.Static,null,new[]{nativeDelegate,nativeDelegate},null);
                if(combine==null)return false;combined=combine.Invoke(null,new[]{callback,existing});
                if(combined==null)return false;
                if(!callbackType.IsInstanceOfType(combined))
                {
                    var cast=combined.GetType().GetMethods().FirstOrDefault(m=>m.Name=="TryCast"&&m.IsGenericMethodDefinition&&m.GetGenericArguments().Length==1&&m.GetParameters().Length==0);
                    combined=cast==null?null:cast.MakeGenericMethod(callbackType).Invoke(combined,null);
                    if(combined==null)return false;
                }
            }
            property.SetValue(null,combined,null);return true;
        }
        void BeforeCanvasRender()
        {
            if(stopped||disconnected||!enabled||translations.Count+startupTranslations.Count==0||writing||inCanvas)return;inCanvas=true;
            try
            {
                RefreshNativeCapabilities();
                // This callback precedes Canvas mesh generation. Query each public
                // scene family here so a small newly enabled UI need not wait for
                // the periodic discovery interval. Native queries themselves are
                // Unity-owned operations; time/count limits bound our processing.
                long until=timer.ElapsedTicks+Stopwatch.Frequency/250;int count=0;
                foreach(var type in textTypes)
                {
                    if(unsupportedNativeTypes.Contains(type))continue;
                    try
                    {
                        var targets=FindSceneObjects(type);if(targets==null)continue;
                        foreach(var target in targets)
                        {
                            if(count++>=2048)break;
                            // New controls must acquire a rendering preprocessor
                            // before this mesh pass. Font creation for an earlier
                            // label must not postpone all remaining new labels.
                            if(timer.ElapsedTicks>=until&&bindings.ContainsKey(Id(target)))continue;
                            var property=target.GetType().GetProperty("text");
                            if(property!=null&&property.PropertyType==typeof(string)&&CaptureExisting(target,property))canvasCacheHits++;
                        }
                    }catch{}
                }
            }finally{inCanvas=false;}
        }
        static Exception RootError(Exception ex){while(ex is TargetInvocationException&&ex.InnerException!=null)ex=ex.InnerException;return ex;}
        void Prune(){foreach(var pair in bindings.ToArray())if(!Alive(pair.Value.Target)){ReleaseProcessor(pair.Value);bindings.Remove(pair.Key);}CompactLateTargets();}
        void CompactLateTargets()
        {
            var retained=new HashSet<Binding>();int count=lateTargets.Count;
            while(count-->0)
            {
                var binding=lateTargets.Dequeue();Binding live;
                if(bindings.TryGetValue(binding.Id,out live)&&ReferenceEquals(live,binding)&&retained.Add(binding))lateTargets.Enqueue(binding);
            }
        }
        static bool Alive(object target)
        {
            if(target==null)return false;try{var type=FindType("UnityEngine.Object");if(type==null||!type.IsInstanceOfType(target))return false;var method=type.GetMethod("op_Implicit",BindingFlags.Public|BindingFlags.Static,null,new[]{type},null);return method!=null&&Convert.ToBoolean(method.Invoke(null,new[]{target}));}catch{return false;}
        }
        static int Id(object target){if(!Alive(target))return 0;try{return Convert.ToInt32(target.GetType().GetMethod("GetInstanceID").Invoke(target,null));}catch{return 0;}}
        static bool Active(object target)
        {
            if(!Alive(target))return false;
            try
            {
                // Restrict reflection to Unity's documented base properties.
                var component=FindType("UnityEngine.Component");
                if(component!=null&&component.IsInstanceOfType(target))
                {
                    var property=component.GetProperty("gameObject");var go=property==null?null:property.GetValue(target,null);
                    if(go!=null){if(!Alive(go))return false;var active=go.GetType().GetProperty("activeInHierarchy");if(active!=null&&!(bool)active.GetValue(go,null))return false;}
                }
                var behaviour=FindType("UnityEngine.Behaviour");
                if(behaviour!=null&&behaviour.IsInstanceOfType(target)){var active=behaviour.GetProperty("isActiveAndEnabled");if(active!=null&&!(bool)active.GetValue(target,null))return false;}
                return true;
            }catch{return false;}
        }
        void QueueText(object target,string source)
        {QueueText(target,source,false);}
        void QueueText(object target,string source,bool provedLiteral)
        {
            Binding binding;if(bindings.TryGetValue(Id(target),out binding)&&!ObserveSource(binding,source)&&!provedLiteral)return;
            if(Active(target)){if(queued.Count<4096&&queued.Add(source))visible.Enqueue(source);}
            else if(aheadQueued.Count<4096&&aheadQueued.Add(source))ahead.Enqueue(source);
        }
        static bool IsInput(object target)
        {
            // Only documented component APIs are used; arbitrary game property getters are not inspected.
            foreach(string name in new[]{"UnityEngine.UI.InputField","TMPro.TMP_InputField"})
            {
                var type=FindType(name);if(type==null)continue;
                var method=target.GetType().GetMethods().FirstOrDefault(m=>m.Name=="GetComponentInParent"&&m.IsGenericMethodDefinition&&m.GetParameters().Length==0);
                try{if(method!=null&&Alive(method.MakeGenericMethod(type).Invoke(target,null)))return true;}catch{}
            }return false;
        }
        static bool Eligible(string value){return value!=null&&value.Length>=1&&value.Length<=6000&&value.IndexOf('\0')<0&&Regex.IsMatch(value,@"\p{L}")&&!Regex.IsMatch(value,@"^(?:\w+://|[\w./\\-]+\.(?:png|jpg|wav|ogg|json|asset|prefab))$",RegexOptions.IgnoreCase);}
        static Dictionary<string,object> Map(params object[] values){var result=new Dictionary<string,object>(StringComparer.Ordinal);for(int i=0;i<values.Length;i+=2)result.Add((string)values[i],values[i+1]);return result;}
        static string String(Dictionary<string,object> data,string name){object value;return data.TryGetValue(name,out value)?value as string:null;}
        static int Number(Dictionary<string,object> data,string name,int fallback){object value;return data.TryGetValue(name,out value)?Convert.ToInt32(value):fallback;}
        object Handle(Dictionary<string,object> request)
        {
            switch(String(request,"op"))
            {
                case "translationPrepare": object preserve;bool keepStartup=request.TryGetValue("preserveStartup",out preserve)&&preserve is bool&&(bool)preserve;if(!keepStartup){startupTranslations.Clear();startupLookup.Clear();}epoch++;preparing=true;staged.Clear();stagedLookup.Clear();visible.Clear();queued.Clear();ahead.Clear();aheadQueued.Clear();return Map("epoch",epoch);
                case "translationEnable":if(preparing){var oldCache=translations;translations=staged;staged=oldCache;staged.Clear();var oldIndex=lookup;lookup=stagedLookup;stagedLookup=oldIndex;stagedLookup.Clear();preparing=false;}enabled=true;restoreSession=false;restoreQueue.Clear();ScheduleRefresh(null);scanAt=0;return Map("epoch",epoch,"enabled",true,"writebackPending",pendingRefresh.Count);
                case "translationDisable":Disable();int restorePending=restoreQueue.Count;return Map("epoch",epoch,"enabled",false,"restorePending",restorePending,"restored",restorePending==0);
                case "translationPoll":var result=new List<string>();while(visible.Count>0&&result.Count<128){string value=visible.Dequeue();queued.Remove(value);result.Add(value);}var later=new List<string>();while(ahead.Count>0&&later.Count<128){string value=ahead.Dequeue();aheadQueued.Remove(value);if(!result.Contains(value)&&!queued.Contains(value))later.Add(value);}var poll=Map("epoch",epoch,"texts",result,"ahead",later);AddProgress(poll);return poll;
                case "translationCatalog":return Map("total",0,"next",0,"skipped",0,"texts",new string[0]);
                case "translationApply":
                        if(Number(request,"epoch",-1)!=epoch)throw new InvalidOperationException("Translation session changed; stale result rejected.");
                    object values;var entries=request.TryGetValue("entries",out values)?values as IList:null;if(entries==null||entries.Count>128)throw new InvalidOperationException("Invalid translation batch.");
                    foreach(var entry in entries){var item=entry as Dictionary<string,object>;if(item==null)throw new InvalidOperationException("Invalid translation entry.");string source=String(item,"source"),text=String(item,"text");if(!Eligible(source)||string.IsNullOrEmpty(text)||text.Length>24000)throw new InvalidOperationException("Invalid translated text.");}
                    bool prime=request.TryGetValue("prime",out values)&&values is bool&&(bool)values;
                    if(prime&&!preparing)throw new InvalidOperationException("No prepared translation session.");
                    var destination=prime?staged:translations;if(destination.Count+entries.Count>150000)throw new InvalidOperationException("Translation cache limit exceeded.");
                    long indexStarted=timer.ElapsedTicks;var destinationIndex=prime?stagedLookup:lookup;
                    var changed=new HashSet<string>(StringComparer.Ordinal);foreach(Dictionary<string,object> entry in entries)
                    {
                        string source=String(entry,"source"),text=String(entry,"text"),previous;
                        if(destination.TryGetValue(source,out previous)&&previous==text)continue;
                        destination[source]=text;destinationIndex.Update(source,text);changed.Add(source);
                    }
                    RecordLookupWork(indexStarted);if(enabled&&!prime&&changed.Count>0)ScheduleRefresh(changed);
                    return Map("epoch",epoch,"applied",entries.Count,"stored",entries.Count,"writebackPending",pendingRefresh.Count,"writebackConfirmed",false);
                case "snapshot":return Map("generation",epoch,"rows",new object[0],"state","translationOnly");
                case "translationDiagnostics":
                    return Map("latency",LatencyProgress(),"latencySemantics","first-active-observation-to-cache-glyph-and-property-or-render-input-confirmation-not-pixels","epoch",epoch,"enabled",enabled,"hooks",patched.Count,"bindings",bindings.Count,"cache",translations.Count,"setterCacheHits",prefixCacheHits,"enableCacheHits",enableCacheHits,"renderCacheHits",renderCacheHits,"lateCacheHits",lateCacheHits,"hookMode",il2Cpp?"frame-scan":"harmony",
                        "nativePreprocessorBindings",nativeProcessorBindings,"nativePreprocessorHits",nativeProcessorHits,"nativePreprocessorSemantics","render-input-projection-not-property-writeback",
                        "completeCallCaptures",completeCallCaptures,"completeCallCacheHits",completeCallCacheHits,"completeDisplaySites",displaySources==null?0:displaySources.CallSitesInstalled,"completeDisplayFailures",displaySources==null?0:displaySources.Failures,"completeDisplayBudgetStops",displaySources==null?0:displaySources.BudgetStops,
                        "companyName",ApplicationString("companyName"),"productName",ApplicationString("productName"),"persistentDataPath",ApplicationString("persistentDataPath"),"unityVersion",ApplicationString("unityVersion"),
                        "fontStatus",fontStatus,"staticFontStatus",staticTmpFonts.Status,"staticFontPages",staticTmpFonts.PageCount,"staticFontClones",staticTmpFonts.CloneCount,"staticFontMaterialAdjustments",staticTmpFonts.MaterialAdjustments,"fontsOwned",bindings.Values.Count(b=>b.OwnedFont!=null),"fontBlockedBindings",bindings.Values.Count(b=>b.FontBlocked),"revealingBindings",bindings.Values.Count(b=>b.Revealing),"rawCaptureCount",rawCaptureCount,"rawCaptureCountSemantics","lifetime-component-source-transitions","framePolling",true,"discoveryIntervalMs",il2Cpp?200:400,"captureFamilies",textTypes.Select(t=>t.FullName).ToArray(),
                        "completeRevealCaptures",completeRevealCaptures,"earlyTmpRevealSubstitutions",earlyTmpRevealSubstitutions,"earlyTmpRevealBindings",bindings.Values.Count(b=>b.EarlyTmpReveal&&b.Applied!=null),"fontAtlasRefreshes",atlasRefreshes,"fontAtlasCapacityFailures",atlasCapacityFailures,"fontAtlasEventAvailable",fontAtlasCallback!=null,"legacyAutoFonts",legacyAutoFonts.Count,"startupExactCache",startupTranslations.Count,"preRenderAvailable",canvasCallback!=null,"canvasCacheHits",canvasCacheHits,"writebacksConfirmed",writebackConfirmed,"writebacksFailed",writebackFailed,
                        "discoveryFailures",discoveryFailures.Values.OrderBy(f=>f.Family).ThenBy(f=>f.Stage).Select(f=>Map("family",f.Family,"stage",f.Stage,"errorType",f.Error,"count",f.Count)).ToArray(),
                        "unsupportedFamilies",unsupportedNativeTypes.OrderBy(t=>t.FullName).Select(t=>Map("family",t.FullName,"reason","native-type-unavailable")).ToArray(),
                        "writebackPending",pendingRefresh.Count,"lookupTimingSemantics","incremental-index-update-per-batch","startupIndexMs",startupIndexMilliseconds,"lookupIndexUpdates",lookup.UpdateCount,"lastLookupMs",lastLookupMilliseconds,"maxLookupMs",maxLookupMilliseconds,"restorePending",restoreQueue.Count,"lastRestoreCount",lastRestoreCount,"restoreSlices",restoreSlices,"lastRestoreMs",lastRestoreMilliseconds,"maxRestoreMs",maxRestoreMilliseconds,"lastLateFrameMs",lastLateMilliseconds,"maxLateFrameMs",maxLateMilliseconds,
                        "observed",bindings.Values.OrderByDescending(b=>Active(b.Target)).Take(32).Select(b=>BindingDiagnostic(b)).ToArray(),
                        "displayed",bindings.Values.Where(b=>b.Applied!=null&&BoundText(b)==b.Applied).OrderByDescending(b=>Active(b.Target)).Take(32).Select(b=>Map("source",Preview(b.Source),"text",Preview(BoundText(b)),"active",Active(b.Target),"component",b.Target==null?"":b.Target.GetType().FullName)).ToArray(),
                        "requested",DiagnoseSources(request));
                default:throw new InvalidOperationException("This Unity bridge provides translation only.");
            }
        }
        void AddProgress(Dictionary<string,object> result)
        {
            result["latency"]=LatencyProgress();result["latencySemantics"]="first-active-observation-to-cache-glyph-and-property-or-render-input-confirmation-not-pixels";
            // These are component/property observations, not rendered-pixel proof.
            // rawCaptureCount counts eligible source changes per component across
            // this plugin lifetime; repeated controls may contain the same text.
            result["rawCaptureCount"]=rawCaptureCount;result["rawCaptureCountSemantics"]="lifetime-component-source-transitions";
            result["legacyProjectionHits"]=legacyProjectionHits;result["legacyProjectionBindings"]=bindings.Values.Count(b=>b.LegacyProjection);
            result["nativePreprocessorBindings"]=nativeProcessorBindings;result["nativePreprocessorHits"]=nativeProcessorHits;
            result["bindings"]=bindings.Count;result["writebacksConfirmed"]=writebackConfirmed;result["writebacksFailed"]=writebackFailed;
            result["restorePending"]=restoreQueue.Count;result["lastRestoreCount"]=lastRestoreCount;result["restoreSlices"]=restoreSlices;
            result["writebackPending"]=pendingRefresh.Count;result["fontStatus"]=fontStatus;result["completeRevealCaptures"]=completeRevealCaptures;result["fontAtlasRefreshes"]=atlasRefreshes;result["fontAtlasCapacityFailures"]=atlasCapacityFailures;
            int fonts=0,blocked=0,revealing=0,verified=0,activeRendered=0,activeWaiting=0;
            foreach(var binding in bindings.Values)
            {
                if(enabled&&Alive(binding.Target)&&Active(binding.Target)&&Eligible(binding.Source))
                {
                    if((binding.RenderedSource==binding.Source&&binding.RenderedText!=null)||(binding.Applied!=null&&binding.ConfirmedSource==binding.Source&&binding.ConfirmedText==binding.Applied))activeRendered++;
                    else {string known;if(TryTranslation(binding.Source,out known))activeWaiting++;}
                }
                if(binding.OwnedFont!=null)fonts++;if(binding.FontBlocked)blocked++;if(binding.Revealing)revealing++;
                if(binding.Applied!=null&&binding.ConfirmedSource==binding.Source&&binding.ConfirmedText==binding.Applied)verified++;
            }
            result["activeRenderedBindings"]=activeRendered;result["activeCachedWaitingBindings"]=activeWaiting;result["fontsOwned"]=fonts;result["fontBlockedBindings"]=blocked;result["revealingBindings"]=revealing;result["lastVerifiedBindings"]=verified;
        }
        static string BoundText(Binding binding){if(!Alive(binding.Target))return null;try{return ReadText(binding.Target,binding.Text);}catch{return null;}}
        static string Preview(string text){return text==null||text.Length<=512?text:text.Substring(0,512)+"…";}
        Dictionary<string,object> BindingDiagnostic(Binding binding)
        {
            object target=binding.Target;
            var row=Map("source",Preview(binding.Source),"current",Preview(BoundText(binding)),"active",Active(target),"owned",binding.Applied!=null,
                "component",target==null?"":target.GetType().FullName,"id",binding.Id,"fontBlocked",binding.FontBlocked,"fontReason",binding.FontReason,
                "atlasStatus",binding.AtlasStatus,"revealing",binding.Revealing,"preparedDisplay",Preview(binding.PreparedDisplay),
                "atlasGeneration",atlasGeneration,"meshGeneration",binding.FontMeshGeneration,
                "authoredFontSize",binding.Size==null?null:(object)binding.OriginalSize,"metricFontSize",binding.Size==null?null:(object)binding.OwnedSize,
                "bufferedProjection",binding.BufferedProjection,"legacyProjection",binding.LegacyProjection,"nativePreprocessor",binding.Processor!=null&&binding.Processor.IsInstalled,"renderInputSource",Preview(binding.RenderedSource),"renderInputText",Preview(binding.RenderedText));
            if(!Alive(target))return row;
            // Only known text/font scalar properties. Never traverse game controllers.
            foreach(string name in new[]{"fontSize","fontStyle","pixelsPerUnit","resizeTextForBestFit","resizeTextMinSize","resizeTextMaxSize","maxVisibleCharacters","horizontalOverflow","verticalOverflow","lineSpacing","enableWordWrapping","alignment"})
                row[name]=DiagnosticScalar(target,name);
            row["contentSizedAxes"]=CjkTextLayout.ContentAxes(target);row["alignByGeometry"]=DiagnosticScalar(target,"alignByGeometry");
            row["sourceLayoutRegion"]=binding.Layout.Region.Status;row["sourceLayoutOffsetY"]=binding.Layout.Region.OffsetY;
            row["sourcePreferredWidth"]=binding.Layout.Region.PreferredSize(target,true);row["sourcePreferredHeight"]=binding.Layout.Region.PreferredSize(target,false);
            row["projectedRasterDensity"]=binding.RasterDensity;
            try{var p=target.GetType().GetProperty("canvas");object canvas=p==null?null:p.GetValue(target,null);if(canvas!=null){row["canvasRenderMode"]=DiagnosticScalar(canvas,"renderMode");row["canvasScaleFactor"]=DiagnosticScalar(canvas,"scaleFactor");string status;row["screenRasterDensity"]=LegacyRasterDensity.ScreenDensity(target,out status);row["rasterProjectionStatus"]=status;}}catch{}
            try{var p=target.GetType().GetProperty("rectTransform");var tr=p==null?null:p.GetValue(target,null);var rect=tr==null?null:tr.GetType().GetProperty("rect").GetValue(tr,null);if(rect!=null){row["rectWidth"]=DiagnosticScalar(rect,"width");row["rectHeight"]=DiagnosticScalar(rect,"height");}}catch{}
            try{var p=target.GetType().GetProperty("font");object font=p==null?null:p.GetValue(target,null);row["fontName"]=font==null?null:DiagnosticScalar(font,"name");row["fontType"]=font==null?null:font.GetType().FullName;row["fontDynamic"]=font==null?null:DiagnosticScalar(font,"dynamic");if(font!=null)foreach(string name in new[]{"ascent","lineHeight","fontSize"})row["fontFace_"+name]=DiagnosticScalar(font,name);}catch{}
            try{var p=target.GetType().GetProperty("cachedTextGenerator");object generator=p==null?null:p.GetValue(target,null);
                if(generator!=null)foreach(string name in new[]{"vertexCount","characterCountVisible","lineCount","fontSizeUsedForBestFit"})row[name]=DiagnosticScalar(generator,name);}catch{}
            return row;
        }
        static object DiagnosticScalar(object target,string name)
        {
            try{var p=target.GetType().GetProperty(name);if(p==null||!p.CanRead)return null;
                var t=p.PropertyType;if(t!=typeof(string)&&t!=typeof(int)&&t!=typeof(float)&&t!=typeof(bool)&&!t.IsEnum)return null;
                object v=p.GetValue(target,null);return t.IsEnum?Convert.ToString(v):v;}catch{return null;}
        }
        object[] DiagnoseSources(Dictionary<string,object> request)
        {
            object raw;var sources=request.TryGetValue("sources",out raw)?raw as IList:null;
            if(sources==null)return new object[0];if(sources.Count>32)throw new InvalidOperationException("Too many diagnostic sources.");
            var rows=new List<object>();
            foreach(var item in sources)
            {
                string source=item as string;if(!Eligible(source))throw new InvalidOperationException("Invalid diagnostic source.");
                string translated;bool cached=TryTranslation(source,out translated);
                var matches=bindings.Values.Where(b=>b.Source==source&&Alive(b.Target)).ToArray();
                rows.Add(Map("source",source,"captured",matches.Length>0,"cached",cached,"bindingCount",matches.Length,"bindings",matches.OrderByDescending(b=>Active(b.Target)).Take(8).Select(b=>{var detail=BindingDiagnostic(b);detail["writebackConfirmed"]=cached&&b.Applied==translated+(b.DisplaySuffix??"")&&BoundText(b)==translated+(b.DisplaySuffix??"");return detail;}).ToArray()));
            }
            return rows.ToArray();
        }
        static bool HasRestoreOwnership(Binding binding)
        {return binding.Applied!=null||binding.OwnedFont!=null||binding.Size!=null||binding.FitSize!=null||binding.FitMax!=null||binding.Layout.RestorePending||binding.PreparedDisplay!=null;}
        void RestoreBinding(Binding binding)
        {
            object target=binding.Target;if(!Alive(target)){ReleaseProcessor(binding);return;}
            if(binding.BufferedProjection)RefreshBuffered(binding);
            else if(binding.LegacyProjection)LegacyDisplay(binding,ReadText(target,binding.Text));
            else if(binding.Processor!=null&&binding.Processor.IsInstalled){NativeDisplay(binding,ReadText(target,binding.Text));DirtyNativeDisplay(binding);}
            else
            {
                string present=ReadText(target,binding.Text);
                // A foreign writer owns a changed value. Only undo our exact
                // translated echo; a failed setter retains its restoration lease.
                if(!OwnedEcho(binding,present))binding.Applied=null;
                else if(Write(target,binding.Text,binding.RestoreSource??binding.Source))binding.Applied=null;
            }
            RestoreFont(binding,target);
            if(binding.PreparedDisplay==null){binding.RenderedSource=null;binding.RenderedText=null;}
            if(disconnected)ReleaseProcessor(binding);
            binding.Incremental.Reset();
        }
        void PumpRestoreSlice()
        {
            if(enabled||restoreQueue.Count==0)return;
            long started=timer.ElapsedTicks,until=started+Stopwatch.Frequency/500;int count=0,available=restoreQueue.Count;
            while(count<available&&count<64&&(count==0||timer.ElapsedTicks<until))
            {
                var binding=restoreQueue.Dequeue();count++;
                Binding registered;
                if(!bindings.TryGetValue(binding.Id,out registered)||!ReferenceEquals(binding,registered))continue;
                bool alive=Alive(binding.Target);
                try{RestoreBinding(binding);}catch{}
                if(alive&&HasRestoreOwnership(binding))restoreQueue.Enqueue(binding);
            }
            lastRestoreCount=count;restoreSlices++;
            lastRestoreMilliseconds=(timer.ElapsedTicks-started)*1000.0/Stopwatch.Frequency;maxRestoreMilliseconds=Math.Max(maxRestoreMilliseconds,lastRestoreMilliseconds);
        }
        void RefreshBindings()
        {
            // Last-chance plugin destruction has no guaranteed future frame.
            // Normal Disable and transport disconnect use PumpRestoreSlice.
            foreach(var binding in bindings.Values.ToArray())try{RestoreBinding(binding);}catch{}
        }
        void Disable()
        {
            if(!restoreSession||enabled||preparing)
            {
                enabled=false;restoreSession=true;ClearAheadDiscovery();aheadDiscovered.Clear();epoch++;preparing=false;
                staged.Clear();stagedLookup.Clear();startupTranslations.Clear();startupLookup.Clear();visible.Clear();queued.Clear();ahead.Clear();aheadQueued.Clear();
                pendingRefresh.Clear();refreshQueued.Clear();restoreQueue.Clear();
                // Snapshot references only, with no Unity getter/setter calls.
                foreach(var binding in bindings.Values)restoreQueue.Enqueue(binding);
            }
            PumpRestoreSlice();
        }
        bool Write(object target,PropertyInfo property,string text){if(!Alive(target))return false;bool wasWriting=writing;writing=true;try{property.SetValue(target,text,null);return ReadText(target,property)==text;}finally{writing=wasWriting;}}
        bool PrepareFont(Binding binding,object target,string text)
        {
            CacheReady(binding);
            if(binding.LegacyProjection&&!RebuildingGraphics())binding.Layout.Capture(target,binding.Source);
            bool ready=PrepareFontCore(binding,target,text);
            if(ready&&!staticTmpFonts.PrepareMaterials(target,text)){ready=false;FontFailure(binding,"fallback-material-unavailable");}
            if(ready&&!RebuildingGraphics())
            {
                // Unity's dynamic Font can supply CJK through its own fallback.
                // It still needs metric matching even when no font was replaced.
                var property=target.GetType().GetProperty("font");object font=property==null?null:property.GetValue(target,null);
                if(text!=null&&text.Any(c=>c>=0x2e80)&&Alive(font)&&font.GetType().FullName=="UnityEngine.Font")
                {
                    PrepareMeasuredSize(binding,target,Alive(binding.OriginalFont)?binding.OriginalFont:font,font);
                }
                if(ready)binding.Layout.Prepare(target,binding.Source,text,binding.CompleteReveal||binding.KnownCompleteBuffer||binding.Revealing);
                if(ready)PrepareLegacyDensity(binding,target,font);
                // Fitting can change the point size. Verify the final raster size,
                // including the canvas scale, before the display projection owns it.
                if(ready&&!PrepareLegacyAtlas(binding,target,font,text)){ready=false;FontFailure(binding,"measured-size-atlas-unavailable");}
            }
            FontReady(binding,ready);binding.FontBlocked=!ready;binding.FontReason=ready?null:fontStatus;return ready;
        }
        bool PrepareFontCore(Binding binding,object target,string text)
        {
            if(!Alive(target))return false;if(!text.Any(c=>c>=0x2e80))return true;
            bool wasWriting=writing;writing=true;
            try
            {
                var fontProperty=target.GetType().GetProperty("font");if(fontProperty==null||!fontProperty.CanRead||!fontProperty.CanWrite){fontStatus="component font property unavailable";return false;}
                object existing=fontProperty.GetValue(target,null);
                if(!Alive(existing))
                {
                    // A scene unload can invalidate a cached fallback without clearing its
                    // managed wrapper. Only replace that dead value when we still own it.
                    if(binding.OwnedFont!=null&&SameUnityObject(existing,binding.OwnedFont)&&Alive(binding.OriginalFont))
                    {fontProperty.SetValue(target,binding.OriginalFont,null);existing=binding.OriginalFont;}
                    else{fontStatus="component font unavailable; original text retained";return false;}
                }
                bool boundedBestFit=PrepareLegacyBestFit(binding,target,existing);
                // A stripped IL2CPP Font can retain HasCharacter but lose all
                // glyph-metrics access. HasCharacter alone accepted an atlas
                // that produced blank native UI at high canvas scale. Use our
                // bounded OS-font pool when the original cannot be verified.
                bool uncheckedLegacy=il2Cpp&&existing.GetType().FullName=="UnityEngine.Font"&&!LegacyMetricsAvailable(existing.GetType());
                bool ownedLegacy=uncheckedLegacy&&SameUnityObject(existing,binding.OwnedFont);
                if(!boundedBestFit&&(!uncheckedLegacy||ownedLegacy)&&FontContains(existing,text)&&PrepareLegacyAtlas(binding,target,existing,text))return true;
                if(binding.AtlasStatus=="legacy-atlas-budget"&&binding.AtlasFontId==Id(existing)&&binding.AtlasText==text&&timer.ElapsedMilliseconds<binding.AtlasCheckAt){FontFailure(binding,"glyph-warmup-pending");return false;}
                bool emptyHanPlaceholder=binding.AtlasStatus=="legacy-empty-han-placeholder";
                if(Alive(binding.OwnedFont)&&SameUnityObject(existing,binding.OwnedFont)&&PopulateFont(existing,text)&&FontContains(existing,text)&&PrepareLegacyAtlas(binding,target,existing,text))return true;
                if(timer.ElapsedMilliseconds<fontRetryAt||timer.ElapsedMilliseconds<binding.FontRetryAt)return false;
                if(fontProperty.PropertyType.FullName=="TMPro.TMP_FontAsset")
                {
                    object staticFont=staticTmpFonts.Prepare(existing,text);
                    if(Alive(staticFont)&&FontContains(staticFont,text))
                    {
                        if(SameUnityObject(existing,staticFont))return true;
                        var sharedMaterial=target.GetType().GetProperty("fontSharedMaterial");
                        object preset=sharedMaterial!=null&&sharedMaterial.CanRead&&sharedMaterial.CanWrite?sharedMaterial.GetValue(target,null):null;
                        fontProperty.SetValue(target,staticFont,null);
                        if(!SameUnityObject(fontProperty.GetValue(target,null),staticFont)){FontFailure(binding,"static-font-writeback-unconfirmed");return false;}
                        // This clone retains the original atlas, so preserve this
                        // control's outline/glow preset for its original glyphs.
                        if(binding.OwnedFont==null||!SameUnityObject(existing,binding.OwnedFont))binding.OriginalFont=existing;
                        binding.Font=fontProperty;binding.OwnedFont=staticFont;
                        if(Alive(preset))sharedMaterial.SetValue(target,preset,null);
                        binding.AtlasStatus="static-SDF-pages";fontStatus="static CJK fallback assigned; native glyph lookup confirmed";lastFontError=null;return true;
                    }
                }
                object replacement=null;var fontType=FindType("UnityEngine.Font");
                if(fontType!=null&&fontProperty.PropertyType==fontType)
                {
                    // Separate substantially different point sizes to keep a large title
                    // from exhausting the atlas used by many small body labels.
                    if(uncheckedLegacy&&LegacyAutoSize(target))replacement=LeaseLegacyAutoFont(binding,fontType,text);
                    else
                    {
                        int size=LegacySize(target),bucket=(size+15)/16;
                        if(!uiFonts.TryGetValue(bucket,out replacement)||!Alive(replacement))
                        {
                            if(uiFonts.Count>=32){FontFailure(binding,"legacy-font-pool-limit");return false;}
                            replacement=CreateSystemFont(fontType,true,text);if(Alive(replacement))uiFonts[bucket]=replacement;
                        }
                    }
                }
                else
                {
                    var assetType=fontProperty.PropertyType;
                    if(assetType.FullName!="TMPro.TMP_FontAsset"){fontStatus="unsupported font type; original text retained";return false;}
                    if(!tmpFonts.TryGetValue(assetType,out replacement)||!Alive(replacement))
                    {
                        tmpFonts.Remove(assetType);replacement=null;
                        // TMP versions differ: try only available public factory overloads.
                        var factory=assetType.GetMethods(BindingFlags.Public|BindingFlags.Static).FirstOrDefault(m=>m.Name=="CreateFontAsset"&&m.GetParameters().Length==3&&m.GetParameters()[0].ParameterType==typeof(string)&&m.GetParameters()[1].ParameterType==typeof(string));
                        if(factory!=null&&factory.GetParameters()[2].ParameterType==typeof(int))try{replacement=factory.Invoke(null,new object[]{"Microsoft YaHei","Regular",48});}catch{}
                        if(!Alive(replacement)&&fontType!=null)
                        {
                            if(!Alive(dynamicFont))dynamicFont=CreateSystemFont(fontType);
                            var legacy=assetType.GetMethods(BindingFlags.Public|BindingFlags.Static).FirstOrDefault(m=>m.Name=="CreateFontAsset"&&m.GetParameters().Length==1&&m.GetParameters()[0].ParameterType==fontType);
                            if(legacy!=null&&Alive(dynamicFont))try{replacement=legacy.Invoke(null,new[]{dynamicFont});}catch{}
                            // IL2CPP may expose the convenience overload as an
                            // unstripping stub. The public full factory remains a
                            // separate capability in TMP 2/3; never infer it failed.
                            if(!Alive(replacement)&&Alive(dynamicFont))replacement=CreateFullTmpFont(assetType,fontType,dynamicFont);
                            if(!Alive(replacement))fontStatus="TMP font creation returned no live asset; factory="+(legacy!=null)+", systemFont="+Alive(dynamicFont);
                        }
                        if(Alive(replacement)){EnableMultiAtlas(replacement);tmpFonts[assetType]=replacement;}
                        else if(fontType==null)fontStatus="Unity Font type unavailable";
                    }
                }
                if(!Alive(replacement)){FontFailure(binding,"no-live-fallback",true);return false;}
                if(!FontContains(replacement,text)){PopulateFont(replacement,text);if(!FontContains(replacement,text)){FontFailure(binding,"missing-glyphs");return false;}}
                if(!PrepareLegacyAtlas(binding,target,replacement,text)){FontFailure(binding,"glyph-atlas-capacity-or-unavailable");return false;}
                if(SameUnityObject(existing,replacement))return true;
                fontProperty.SetValue(target,replacement,null);
                if(!SameUnityObject(fontProperty.GetValue(target,null),replacement)){FontFailure(binding,"font-writeback-unconfirmed");return false;}
                if(binding.OwnedFont==null||!SameUnityObject(existing,binding.OwnedFont))binding.OriginalFont=existing;binding.Font=fontProperty;binding.OwnedFont=replacement;if(!emptyHanPlaceholder)PrepareMeasuredSize(binding,target,existing,replacement);if(!PrepareLegacyAtlas(binding,target,replacement,text)){RestoreFont(binding,target);FontFailure(binding,"measured-size-atlas-unavailable");return false;}fontStatus="fallback assigned; glyphs and property readback confirmed";lastFontError=null;return true;
            }catch(Exception ex){FontFailure(binding,RootError(ex).GetType().Name);return false;}finally{writing=wasWriting;}
        }

        void InstallFontAtlasCallback()
        {
            if(fontAtlasCallback!=null)return;
            try
            {
                var font=FindType("UnityEngine.Font");if(font==null)return;
                var add=font.GetMethod("add_textureRebuilt",BindingFlags.Public|BindingFlags.Static);
                if(add==null||add.GetParameters().Length!=1)return;
                var callbackType=add.GetParameters()[0].ParameterType;
                var remove=font.GetMethod("remove_textureRebuilt",BindingFlags.Public|BindingFlags.Static,null,new[]{callbackType},null);if(remove==null)return;
                var managedType=typeof(Action<>).MakeGenericType(font);
                var method=GetType().GetMethod("FontAtlasRebuilt",BindingFlags.NonPublic|BindingFlags.Instance);
                fontAtlasManagedCallback=Delegate.CreateDelegate(managedType,this,method);
                object callback=fontAtlasManagedCallback;
                if(!callbackType.IsInstanceOfType(callback))
                {
                    var support=FindType("Il2CppInterop.Runtime.DelegateSupport");
                    var convert=support==null?null:support.GetMethods(BindingFlags.Public|BindingFlags.Static).FirstOrDefault(m=>m.Name=="ConvertDelegate"&&m.IsGenericMethodDefinition&&m.GetGenericArguments().Length==1&&m.GetParameters().Length==1&&m.GetParameters()[0].ParameterType==typeof(Delegate));
                    if(convert==null)return;callback=convert.MakeGenericMethod(callbackType).Invoke(null,new[]{(object)fontAtlasManagedCallback});
                }
                if(callback==null)return;add.Invoke(null,new[]{callback});fontAtlasCallback=callback;fontAtlasRemove=remove;
            }
            catch{fontAtlasManagedCallback=null;}
        }
        static bool LegacyAutoSize(object target)
        {
            try{var p=target.GetType().GetProperty("resizeTextForBestFit");return p!=null&&p.PropertyType==typeof(bool)&&(bool)p.GetValue(target,null);}catch{return false;}
        }
        object LeaseLegacyAutoFont(Binding binding,Type fontType,string text)
        {
            // Auto-fit can rasterize dozens of trial sizes per label. Sharing
            // one atlas between independently fitted labels can evict a sibling's
            // final glyphs even when HasCharacter and mesh generation succeed.
            // Limit isolation to the stripped legacy API; retain the game's fit.
            foreach(var slot in legacyAutoFonts)
                if(ReferenceEquals(slot.Owner,binding)&&Alive(slot.Font))return slot.Font;
            LegacyAutoFont available=null;
            foreach(var slot in legacyAutoFonts)
            {
                var owner=slot.Owner;
                if(owner!=null&&Alive(owner.Target))
                {
                    bool stillAssigned=false;
                    try{stillAssigned=owner.Font!=null&&SameUnityObject(owner.Font.GetValue(owner.Target,null),slot.Font);}catch{continue;}
                    if(stillAssigned&&Active(owner.Target))continue;
                    if(stillAssigned)
                    {
                        RestoreFont(owner,owner.Target);
                        // A failed restore or missing original is not permission
                        // to recycle a font which a live game object still owns.
                        try{var p=owner.Target.GetType().GetProperty("font");if(p!=null&&SameUnityObject(p.GetValue(owner.Target,null),slot.Font))continue;}catch{continue;}
                    }
                }
                available=slot;break;
            }
            if(available==null)
            {
                if(legacyAutoFonts.Count>=32){FontFailure(binding,"legacy-auto-font-pool-limit");return null;}
                available=new LegacyAutoFont();legacyAutoFonts.Add(available);
            }
            available.Owner=binding;
            if(!Alive(available.Font))available.Font=CreateSystemFont(fontType,true,text);
            return available.Font;
        }
        void ReleaseLegacyAutoFonts()
        {
            foreach(var slot in legacyAutoFonts)
            {
                try
                {
                    if(!Alive(slot.Font))continue;
                    var owner=slot.Owner;
                    if(owner!=null&&Alive(owner.Target))
                    {
                        var p=owner.Target.GetType().GetProperty("font");
                        if(p!=null&&SameUnityObject(p.GetValue(owner.Target,null),slot.Font))continue;
                    }
                    var type=FindType("UnityEngine.Object");
                    var destroy=type==null?null:type.GetMethod("Destroy",BindingFlags.Public|BindingFlags.Static,null,new[]{type},null);
                    if(destroy!=null)destroy.Invoke(null,new[]{slot.Font});
                }catch{}
            }
            legacyAutoFonts.Clear();
        }
        void FontAtlasRebuilt(object font)
        {
            // A callback can occur while Unity is generating a mesh. Mark work,
            // never generate more glyphs, change fonts or mutate a mesh here.
            if(stopped)return;
            int id=Id(font);
            lock(rebuiltAtlasFonts){if(rebuiltAtlasFonts.Count<128)rebuiltAtlasFonts.Add(id);else atlasFontOverflow=true;Interlocked.Increment(ref atlasGeneration);}
        }
        void RefreshFontMeshes()
        {
            if(!enabled||RebuildingGraphics())return;
            int generation=Interlocked.CompareExchange(ref atlasGeneration,0,0);
            if(generation!=atlasRefreshedGeneration)
            {
                atlasRefreshedGeneration=generation;
                HashSet<int> changed;bool all;
                lock(rebuiltAtlasFonts){changed=new HashSet<int>(rebuiltAtlasFonts);rebuiltAtlasFonts.Clear();all=atlasFontOverflow;atlasFontOverflow=false;}
                foreach(var binding in bindings.Values)
                    if(HasAtlasDisplay(binding)&&Active(binding.Target)&&binding.FontMeshGeneration!=generation&&
                        (all||changed.Contains(LegacyFontId(binding.Target)))&&atlasQueued.Count<12000&&atlasQueued.Add(binding))atlasRefresh.Enqueue(binding);
            }
            long until=timer.ElapsedTicks+Stopwatch.Frequency/1000;
            for(int i=0;i<64&&atlasRefresh.Count>0&&timer.ElapsedTicks<until;i++)
            {
                var binding=atlasRefresh.Dequeue();atlasQueued.Remove(binding);Binding live;
                if(!bindings.TryGetValue(binding.Id,out live)||!ReferenceEquals(binding,live)||!HasAtlasDisplay(binding)||!Alive(binding.Target)||!Active(binding.Target))continue;
                try
                {
                    var type=FindType("UnityEngine.UI.Text");if(type==null||!type.IsInstanceOfType(binding.Target))continue;
                    var property=type.GetProperty("cachedTextGenerator");var generator=property==null?null:property.GetValue(binding.Target,null);
                    var invalidate=generator==null?null:generator.GetType().GetMethod("Invalidate",Type.EmptyTypes);
                    var dirty=type.GetMethod("SetVerticesDirty",Type.EmptyTypes);
                    if(dirty==null)continue;if(invalidate!=null)invalidate.Invoke(generator,null);dirty.Invoke(binding.Target,null);
                    binding.FontMeshGeneration=generation;atlasRefreshes++;
                }catch{}
            }
        }
        static bool HasAtlasDisplay(Binding binding)
        {
            // A rendering projection deliberately leaves Applied/Text.text alone.
            // Its vertices still reference the dynamic atlas and must be rebuilt
            // after another label causes the glyph texture to be repacked.
            string display=binding.Applied??(binding.LegacyProjection?binding.PreparedDisplay:null);
            return !string.IsNullOrEmpty(display)&&display.Any(c=>c>=0x2e80);
        }
        static int LegacyFontId(object target)
        {try{var p=target.GetType().GetProperty("font");var font=p==null?null:p.GetValue(target,null);return font!=null&&font.GetType().FullName=="UnityEngine.Font"?Id(font):0;}catch{return 0;}}
        static int LegacySize(object target)
        {try{var size=target.GetType().GetProperty("fontSize");int n=size==null?24:Convert.ToInt32(size.GetValue(target,null));return Math.Max(1,Math.Min(1024,n));}catch{return 24;}}
        static double LegacyRasterScale(object target,object font)
        {
            try
            {
                var dynamic=font.GetType().GetProperty("dynamic");var assigned=target.GetType().GetProperty("font");
                // pixelsPerUnit depends on the assigned font. A candidate font
                // is checked again after assignment, never using bitmap units.
                if(dynamic==null||!(bool)dynamic.GetValue(font,null)||assigned==null||!SameUnityObject(assigned.GetValue(target,null),font))return 1;
                var p=target.GetType().GetProperty("pixelsPerUnit");return p==null?1:Convert.ToDouble(p.GetValue(target,null));
            }catch{return 1;}
        }
        static bool PrepareLegacyBestFit(Binding binding,object target,object font)
        {
            // Legacy UI best-fit starts by rasterizing its nominal/maximum size.
            // At high canvas scale it can exhaust a 4096 atlas before shrinking
            // to the rectangle. A Han glyph cannot fit a line taller than that
            // rectangle; bound only auto-sized labels and retain the game's min.
            // Both properties are restored only while our exact values survive.
            if(font==null||font.GetType().FullName!="UnityEngine.Font")return false;
            try
            {
                var type=target.GetType();var best=type.GetProperty("resizeTextForBestFit");
                if(best==null||best.PropertyType!=typeof(bool)||!(bool)best.GetValue(target,null)){RestoreLegacyBestFit(binding,target);return false;}
                var overflow=type.GetProperty("verticalOverflow");
                // Overflow deliberately permits glyphs beyond the rectangle.
                // Do not reinterpret that layout as a size constraint.
                if(overflow==null||Convert.ToString(overflow.GetValue(target,null))!="Truncate"){RestoreLegacyBestFit(binding,target);return false;}
                if((CjkTextLayout.ContentAxes(target)&2)!=0){RestoreLegacyBestFit(binding,target);return false;}
                var size=type.GetProperty("fontSize");var max=type.GetProperty("resizeTextMaxSize");var min=type.GetProperty("resizeTextMinSize");
                var transform=type.GetProperty("rectTransform");if(size==null||max==null||min==null||transform==null||!size.CanWrite||!max.CanWrite)return false;
                object rectTransform=transform.GetValue(target,null);if(rectTransform==null)return false;
                var rectProperty=rectTransform.GetType().GetProperty("rect");if(rectProperty==null)return false;
                double height=Numeric(rectProperty.GetValue(rectTransform,null),"height");if(double.IsNaN(height)||height<1||height>4096)return false;
                int liveSize=Convert.ToInt32(size.GetValue(target,null)),liveMax=Convert.ToInt32(max.GetValue(target,null));
                int sourceSize=binding.FitSize!=null&&liveSize==binding.OwnedFitSize?binding.OriginalFitSize:liveSize;
                int sourceMax=binding.FitMax!=null&&liveMax==binding.OwnedFitMax?binding.OriginalFitMax:liveMax;
                int minimum=Convert.ToInt32(min.GetValue(target,null)),limit=(int)Math.Floor(height);
                if(limit<Math.Max(1,minimum)||sourceMax<minimum)return false;
                int chosenSize=Math.Min(sourceSize,limit),chosenMax=Math.Min(sourceMax,limit);
                if(chosenSize==sourceSize&&chosenMax==sourceMax){RestoreLegacyBestFit(binding,target);return false;}
                if(liveSize!=chosenSize)size.SetValue(target,chosenSize,null);
                binding.FitSize=size;binding.OriginalFitSize=sourceSize;binding.OwnedFitSize=chosenSize;
                if(liveMax!=chosenMax)max.SetValue(target,chosenMax,null);
                binding.FitMax=max;binding.OriginalFitMax=sourceMax;binding.OwnedFitMax=chosenMax;
                return Convert.ToInt32(size.GetValue(target,null))==chosenSize&&Convert.ToInt32(max.GetValue(target,null))==chosenMax;
            }catch{RestoreLegacyBestFit(binding,target);return false;}
        }
        static void RestoreLegacyBestFit(Binding binding,object target)
        {
            if(RestoreOwnedNumber(target,binding.FitSize,binding.OwnedFitSize,binding.OriginalFitSize))binding.FitSize=null;
            if(RestoreOwnedNumber(target,binding.FitMax,binding.OwnedFitMax,binding.OriginalFitMax))binding.FitMax=null;
        }
        static bool RestoreOwnedNumber(object target,PropertyInfo property,int owned,int original)
        {
            if(property==null||!Alive(target))return true;
            try{if(Convert.ToInt32(property.GetValue(target,null))!=owned)return true;property.SetValue(target,original,null);return Convert.ToInt32(property.GetValue(target,null))==original;}catch{return false;}
        }
        sealed class GlyphRun {internal int Size,Style;internal string Text;}
        static bool TryGlyphRuns(string text,int size,int style,bool rich,double scale,out List<GlyphRun> runs)
        {
            runs=new List<GlyphRun>();if(text==null||text.Length>6000||size<1||size>512||double.IsNaN(scale)||double.IsInfinity(scale)||scale<=0||scale>16)return false;
            var groups=new Dictionary<string,StringBuilder>();var sizes=new Stack<int>();int bold=0,italic=0,position=0;
            Func<string,bool> add=part=>
            {
                int runStyle=style|(bold>0?1:0)|(italic>0?2:0);string key=size+":"+runStyle;StringBuilder buffer;
                if(!groups.TryGetValue(key,out buffer)){if(groups.Count>=8)return false;groups[key]=buffer=new StringBuilder();}
                buffer.Append(part);return true;
            };
            if(rich)foreach(Match tag in Regex.Matches(text,@"</?(?<name>b|i|size|color|material|quad)\b[^<>]*>",RegexOptions.IgnoreCase))
            {
                if(!add(text.Substring(position,tag.Index-position)))return false;position=tag.Index+tag.Length;
                string name=tag.Groups["name"].Value.ToLowerInvariant();bool close=tag.Value.StartsWith("</",StringComparison.Ordinal);
                if(name=="b"){bold+=close?-1:1;if(bold<0)return false;}
                else if(name=="i"){italic+=close?-1:1;if(italic<0)return false;}
                else if(name=="size")
                {
                    if(close){if(sizes.Count==0)return false;size=sizes.Pop();}
                    else
                    {
                        var value=Regex.Match(tag.Value,@"^<size\s*=\s*[""']?(\d+)[""']?\s*>$",RegexOptions.IgnoreCase);int parsed;
                        if(!value.Success||!int.TryParse(value.Groups[1].Value,out parsed)||parsed<1||parsed>512)return false;
                        sizes.Push(size);size=parsed;
                    }
                }
            }
            if(!add(text.Substring(position))||bold!=0||italic!=0||sizes.Count!=0)return false;
            int total=0;long area=0;
            foreach(var pair in groups)
            {
                string glyphs=new string(pair.Value.ToString().Where(c=>!char.IsWhiteSpace(c)&&!char.IsControl(c)).Distinct().ToArray());
                if(glyphs.Any(char.IsSurrogate))return false;if(glyphs.Length==0)continue;
                var key=pair.Key.Split(':');int points=Math.Max(1,(int)Math.Round(int.Parse(key[0])*scale));if(points>4096)return false;
                total+=glyphs.Length;area+=(long)glyphs.Length*(points+4)*(points+4);
                if(total>512||area>8L*1024*1024)return false;
                runs.Add(new GlyphRun{Size=points,Style=int.Parse(key[1]),Text=glyphs});
            }
            return true;
        }
        bool PrepareLegacyAtlas(Binding binding,object target,object font,string text)
        {
            if(font==null||font.GetType().FullName!="UnityEngine.Font")return true;
            var type=font.GetType();var styleType=FindType("UnityEngine.FontStyle");
            var infoType=FindType("UnityEngine.CharacterInfo");if(styleType==null||infoType==null){binding.AtlasStatus="legacy-atlas-api-unavailable";return true;}
            var request=type.GetMethod("RequestCharactersInTexture",new[]{typeof(string),typeof(int),styleType});
            var info=type.GetMethod("GetCharacterInfo",new[]{typeof(char),infoType.MakeByRefType(),typeof(int),styleType});
            if(request==null||info==null||info.ReturnType!=typeof(bool)){binding.AtlasStatus="legacy-atlas-api-unavailable";return true;}
            try
            {
                int size=LegacySize(target);double scale=Math.Max(LegacyRasterScale(target,font),binding.RasterDensity);var styleProperty=target.GetType().GetProperty("fontStyle");
                object style=styleProperty!=null&&styleProperty.PropertyType==styleType?styleProperty.GetValue(target,null):Enum.ToObject(styleType,0);
                int styleId=Convert.ToInt32(style),id=Id(font),generation=Interlocked.CompareExchange(ref atlasGeneration,0,0);
                bool same=binding.AtlasFontId==id&&binding.AtlasSize==size&&binding.AtlasScale==scale&&binding.AtlasStyle==styleId&&binding.AtlasText==text;
                if(same&&timer.ElapsedMilliseconds<binding.AtlasCheckAt&&(!binding.AtlasReady||binding.AtlasCheckedGeneration==generation))return binding.AtlasReady;
                binding.AtlasFontId=id;binding.AtlasSize=size;binding.AtlasScale=scale;binding.AtlasStyle=styleId;binding.AtlasText=text;binding.AtlasReady=false;
                binding.AtlasCheckAt=timer.ElapsedMilliseconds+1000;binding.AtlasCheckedGeneration=generation;binding.AtlasStatus="legacy-atlas-incomplete";
                var richProperty=target.GetType().GetProperty("supportRichText")??target.GetType().GetProperty("richText");
                bool rich=richProperty==null||richProperty.PropertyType!=typeof(bool)||(bool)richProperty.GetValue(target,null);
                List<GlyphRun> runs;if(!TryGlyphRuns(text,size,styleId,rich,scale,out runs))return false;
                long until=timer.ElapsedTicks+Stopwatch.Frequency/250;
                // Re-check an existing atlas before requesting it again. Otherwise
                // several labels can repeatedly rebuild one another's shared atlas.
                bool emptyHan=false,lineMismatch=false;
                Func<GlyphRun,bool> ready=run=>run.Text.All(c=>
                {
                    if(timer.ElapsedTicks>=until)return false;
                    var arguments=new object[]{c,Activator.CreateInstance(infoType),run.Size,Enum.ToObject(styleType,run.Style)};
                    if(!(bool)info.Invoke(font,arguments))return false;
                    if(EmptyHanGlyph(c,run.Size,arguments[1])){emptyHan=true;return false;}
                    if(GlyphExceedsLineBox(c,run.Size,font,arguments[1])){lineMismatch=true;return false;}
                    return true;
                });
                foreach(var run in runs)if(!ready(run))
                {
                    if(timer.ElapsedTicks>=until)return DeferLegacyAtlas(binding);
                    request.Invoke(font,new object[]{run.Text,run.Size,Enum.ToObject(styleType,run.Style)});
                }
                // The final pass covers every size/style, because a later request
                // can repack or exhaust the same native texture.
                emptyHan=false;lineMismatch=false;
                if(!runs.All(ready))
                {
                    if(timer.ElapsedTicks>=until)return DeferLegacyAtlas(binding);
                    binding.AtlasStatus=emptyHan?"legacy-empty-han-placeholder":lineMismatch?"legacy-glyph-outside-line-box":"legacy-atlas-incomplete";
                    if(!lineMismatch)atlasCapacityFailures++;return false;
                }
                binding.AtlasReady=true;binding.AtlasCheckedGeneration=Interlocked.CompareExchange(ref atlasGeneration,0,0);
                binding.AtlasStatus="legacy-glyph-metrics-checked";
                return true;
            }
            catch{return false;}
        }
        bool DeferLegacyAtlas(Binding binding)
        {
            // A frame budget is not evidence of a missing font. Retry next
            // frame rather than negative-caching a known translation for 1s.
            binding.AtlasStatus="legacy-atlas-budget";binding.AtlasCheckAt=timer.ElapsedMilliseconds+16;return false;
        }
        static bool LegacyMetricsAvailable(Type font)
        {
            var style=FindType("UnityEngine.FontStyle");var info=FindType("UnityEngine.CharacterInfo");
            return style!=null&&info!=null&&font.GetMethod("GetCharacterInfo",new[]{typeof(char),info.MakeByRefType(),typeof(int),style})!=null;
        }
        static void EnableMultiAtlas(object font)
        {
            // Only called for our newly created TMP asset; never mutate a game's
            // shared font asset or its fallback list.
            try{var multi=font.GetType().GetProperty("isMultiAtlasTexturesEnabled");if(multi!=null&&multi.CanWrite&&multi.PropertyType==typeof(bool))multi.SetValue(font,true,null);}catch{}
        }
        static double Numeric(object value,string name)
        {
            if(value==null)return 0;var t=value.GetType();var f=t.GetField(name,BindingFlags.Public|BindingFlags.Instance);
            var p=t.GetProperty(name,BindingFlags.Public|BindingFlags.Instance);
            try{return Convert.ToDouble(f!=null?f.GetValue(value):p!=null?p.GetValue(value,null):0,System.Globalization.CultureInfo.InvariantCulture);}catch{return 0;}
        }
        static bool EmptyHanGlyph(char character,int size,object info)
        {
            // Some dynamic Japanese fonts report HasCharacter/GetCharacterInfo
            // true for a full-width empty placeholder (observed 2x2 at size 36).
            // Do not reject punctuation, thin strokes, rotated atlas UVs or tiny
            // UI sizes. This is a narrow glyph-metrics check, not pixel proof.
            if(size<12||!((character>=0x3400&&character<=0x4dbf)||(character>=0x4e00&&character<=0x9fff)||(character>=0xf900&&character<=0xfaff)))return false;
            var type=info==null?null:info.GetType();
            if(type==null||(type.GetProperty("glyphWidth")==null&&type.GetField("glyphWidth")==null)||
                (type.GetProperty("glyphHeight")==null&&type.GetField("glyphHeight")==null)||
                (type.GetProperty("advance")==null&&type.GetField("advance")==null))return false;
            double width,height,advance;
            if(!TryGlyphMetric(info,"glyphWidth",out width)||!TryGlyphMetric(info,"glyphHeight",out height)||!TryGlyphMetric(info,"advance",out advance))return false;
            return Math.Abs(width)<=2&&Math.Abs(height)<=2&&advance>=Math.Max(4,size/2);
        }
        static bool GlyphExceedsLineBox(char character,int size,object font,object info)
        {
            // OS fallback can return a valid Han glyph while a pixel font still
            // reports its Latin-only ascent/line height. Unity then clips that
            // glyph or drops the entire first line. Verify the font's geometry,
            // not its name or the game. Missing metrics remain unsupported proof.
            if(character<0x3400||character>0x9fff||size<1)return false;
            double ascent,line,points,top,bottom;
            if(!TryGlyphMetric(font,"ascent",out ascent)||!TryGlyphMetric(font,"lineHeight",out line)||!TryGlyphMetric(font,"fontSize",out points)||
                !TryGlyphMetric(info,"maxY",out top)||!TryGlyphMetric(info,"minY",out bottom)||points<=0||ascent<=0||line<ascent)return false;
            var dynamic=font.GetType().GetProperty("dynamic");
            double scale=dynamic!=null&&(bool)dynamic.GetValue(font,null)?size/points:1;
            double tolerance=Math.Max(1,size*0.04);
            return top>ascent*scale+tolerance||bottom<(ascent-line)*scale-tolerance;
        }
        static bool TryGlyphMetric(object value,string name,out double result)
        {
            result=0;
            try
            {
                var type=value.GetType();var field=type.GetField(name,BindingFlags.Public|BindingFlags.Instance);var property=type.GetProperty(name,BindingFlags.Public|BindingFlags.Instance);
                object raw=field!=null?field.GetValue(value):property!=null?property.GetValue(value,null):null;
                if(raw==null||raw is string||raw is bool)return false;
                result=Convert.ToDouble(raw,System.Globalization.CultureInfo.InvariantCulture);return !double.IsNaN(result)&&!double.IsInfinity(result);
            }catch{return false;}
        }
        static double GlyphHeight(object font,char glyph,int size,object style)
        {
            try
            {
                var infoType=FindType("UnityEngine.CharacterInfo");var styleType=FindType("UnityEngine.FontStyle");
                if(infoType==null||styleType==null||font==null)return 0;
                var info=font.GetType().GetMethod("GetCharacterInfo",new[]{typeof(char),infoType.MakeByRefType(),typeof(int),styleType});
                var request=font.GetType().GetMethod("RequestCharactersInTexture",new[]{typeof(string),typeof(int),styleType});
                if(info==null||request==null)return 0;request.Invoke(font,new object[]{glyph.ToString(),size,style});
                var args=new object[]{glyph,Activator.CreateInstance(infoType),size,style};if(!(bool)info.Invoke(font,args))return 0;
                return Math.Abs(Numeric(args[1],"maxY")-Numeric(args[1],"minY"));
            }catch{return 0;}
        }
        string PrepareIndent(Binding binding,object target,string text)
        {
            string source=binding.Source;
            if(source==null||!source.StartsWith("  ",StringComparison.Ordinal)||RebuildingGraphics())return text;
            try
            {
                var property=target.GetType().GetProperty("font");object font=property==null?null:property.GetValue(target,null);
                if(!Alive(font)||font.GetType().FullName!="UnityEngine.Font")return text;
                int size=LegacySize(target),id=Id(font);var style=target.GetType().GetProperty("fontStyle").GetValue(target,null);int styleId=Convert.ToInt32(style);
                if(binding.IndentSource==source&&binding.IndentText==text&&binding.IndentSize==size&&binding.IndentFontId==id&&binding.IndentStyle==styleId)return binding.IndentDisplay;
                object original=Alive(binding.OriginalFont)?binding.OriginalFont:font;
                int originalSize=binding.Size==null?size:Convert.ToInt32(binding.OriginalSize);
                string result=CjkTextLayout.PreserveIndent(source,text,SpaceAdvance(original,originalSize,style),SpaceAdvance(font,size,style));
                binding.IndentSource=source;binding.IndentText=text;binding.IndentDisplay=result;binding.IndentSize=size;binding.IndentFontId=id;binding.IndentStyle=styleId;
                return result;
            }catch{return text;}
        }
        static double SpaceAdvance(object font,int size,object style)
        {
            try
            {
                var infoType=FindType("UnityEngine.CharacterInfo");var styleType=FindType("UnityEngine.FontStyle");if(infoType==null||styleType==null)return 0;
                var get=font.GetType().GetMethod("GetCharacterInfo",new[]{typeof(char),infoType.MakeByRefType(),typeof(int),styleType});
                var request=font.GetType().GetMethod("RequestCharactersInTexture",new[]{typeof(string),typeof(int),styleType});if(get==null||request==null)return 0;
                request.Invoke(font,new object[]{" ",size,style});var args=new object[]{' ',Activator.CreateInstance(infoType),size,style};if(!(bool)get.Invoke(font,args))return 0;
                double advance=Numeric(args[1],"advance");var dynamic=font.GetType().GetProperty("dynamic");double points=Numeric(font,"fontSize");
                if(dynamic!=null&&!(bool)dynamic.GetValue(font,null)&&points>0)advance*=size/points;
                return advance;
            }catch{return 0;}
        }
        void PrepareMeasuredSize(Binding binding,object target,object original,object replacement)
        {
            // A family uses one stable H-to-CJK cap-height ratio, independent of
            // sentence length. Unknown metrics preserve the game's nominal size.
            // No inferred button roles or guessed game paths are used.
            if(binding.Size!=null||binding.FitSize!=null||original==null||replacement==null||original.GetType().FullName!="UnityEngine.Font"||replacement.GetType().FullName!="UnityEngine.Font")return;
            try
            {
                var property=target.GetType().GetProperty("fontSize");var styleType=FindType("UnityEngine.FontStyle");
                if(property==null||!property.CanRead||!property.CanWrite||property.PropertyType!=typeof(int)||styleType==null)return;
                int size=Convert.ToInt32(property.GetValue(target,null));if(size<1||size>512)return;
                var styleProperty=target.GetType().GetProperty("fontStyle");object style=styleProperty!=null&&styleProperty.PropertyType==styleType?styleProperty.GetValue(target,null):Enum.ToObject(styleType,0);
                double native=GlyphHeight(original,'H',size,style),cjk=GlyphHeight(replacement,'国',size,style);
                // Static bitmap metrics are atlas pixels, while a dynamic Font
                // reports the requested point size. Compare both in label units.
                var dynamicProperty=original.GetType().GetProperty("dynamic");
                if(dynamicProperty!=null&&!(bool)dynamicProperty.GetValue(original,null))
                {double atlasSize=Numeric(original,"fontSize");if(atlasSize>0)native*=size/atlasSize;}
                if(native<=0||cjk<=0)return;double ratio=native/cjk;
                if(double.IsNaN(ratio)||double.IsInfinity(ratio)||ratio<0.2||ratio>2)return;
                // Dense CJK strokes need the same optical allowance in labels
                // and sentences. Text length / reveal state must not change a
                // font family's apparent size. Fixed rectangles are fitted later.
                ratio*=1.15;
                ratio=Math.Min(1,ratio);
                int chosen=Math.Max(1,(int)Math.Round(size*ratio));if(chosen==size)return;
                property.SetValue(target,chosen,null);if(Convert.ToInt32(property.GetValue(target,null))!=chosen)return;
                binding.Size=property;binding.OriginalSize=size;binding.OwnedSize=chosen;binding.AtlasText=null;
            }catch{}
        }

        void FontFailure(Binding binding,string reason,bool shared=false)
        {bool pending=binding.AtlasStatus=="legacy-atlas-budget";binding.FontRetryAt=timer.ElapsedMilliseconds+(pending?16:1000);if(shared&&!pending)fontRetryAt=binding.FontRetryAt;fontStatus="original text retained; "+reason;if(!pending&&lastFontError!=reason){lastFontError=reason;log("Font fallback unavailable: "+reason+"; retry deferred");}}
        static bool FontContains(object font,string text)
        {
            if(!Alive(font))return false;string needed=new string(text.Where(c=>c>=0x2e80).ToArray());if(needed.Length==0)return true;
            var type=font.GetType();
            // Public convenience methods in generated IL2CPP assemblies can throw
            // while a public native primitive on the same type remains usable.
            // Try independent API signatures before declaring the glyph unavailable.
            var hasCharacters=type.GetMethod("HasCharacters",new[]{typeof(string)});
            if(hasCharacters!=null&&hasCharacters.ReturnType==typeof(bool))try{if((bool)hasCharacters.Invoke(font,new object[]{needed}))return true;}catch{}
            var hasWithFallbacks=type.GetMethod("HasCharacter",new[]{typeof(char),typeof(bool),typeof(bool)});
            if(hasWithFallbacks!=null&&hasWithFallbacks.ReturnType==typeof(bool))try
            {
                if(needed.All(c=>!char.IsSurrogate(c)&&(bool)hasWithFallbacks.Invoke(font,new object[]{c,true,false})))return true;
            }catch{}
            var legacyFallbacks=type.GetMethod("HasCharacter",new[]{typeof(char),typeof(bool)});
            if(legacyFallbacks!=null&&legacyFallbacks.ReturnType==typeof(bool))try
            {if(needed.All(c=>!char.IsSurrogate(c)&&(bool)legacyFallbacks.Invoke(font,new object[]{c,true})))return true;}catch{}
            foreach(var argument in new[]{typeof(char),typeof(int),typeof(uint)})
            {
                var hasCharacter=type.GetMethod("HasCharacter",new[]{argument});if(hasCharacter==null||hasCharacter.ReturnType!=typeof(bool))continue;
                try
                {
                    bool complete=true;
                    for(int i=0;i<needed.Length;i++)
                    {
                        int code=needed[i];
                        if(char.IsSurrogate(needed[i]))
                        {if(argument==typeof(char)||!char.IsHighSurrogate(needed[i])||i+1>=needed.Length||!char.IsLowSurrogate(needed[i+1])){complete=false;break;}code=char.ConvertToUtf32(needed[i],needed[++i]);}
                        object value=argument==typeof(char)?(object)(char)code:argument==typeof(uint)?(object)(uint)code:code;
                        if(!(bool)hasCharacter.Invoke(font,new[]{value})){complete=false;break;}
                    }
                    if(complete)return true;
                }catch{}
            }
            return false;
        }
        static bool PopulateFont(object font,string text)
        {
            var type=font.GetType();var add=type.GetMethod("TryAddCharacters",new[]{typeof(string),typeof(bool)});
            if(add!=null&&add.ReturnType==typeof(bool))try{if((bool)add.Invoke(font,new object[]{text,false}))return true;}catch{}
            add=type.GetMethod("TryAddCharacters",new[]{typeof(string),typeof(string).MakeByRefType(),typeof(bool)});
            if(add!=null&&add.ReturnType==typeof(bool))try{return (bool)add.Invoke(font,new object[]{text,null,false});}catch{}
            return false;
        }
        static object CreateFullTmpFont(Type assetType,Type fontType,object font)
        {
            foreach(var method in assetType.GetMethods(BindingFlags.Public|BindingFlags.Static).Where(m=>m.Name=="CreateFontAsset"))
            {
                var p=method.GetParameters();
                if((p.Length!=7&&p.Length!=8)||p[0].ParameterType!=fontType||p[1].ParameterType!=typeof(int)||p[2].ParameterType!=typeof(int)||
                    p[3].ParameterType.FullName!="UnityEngine.TextCore.LowLevel.GlyphRenderMode"||p[4].ParameterType!=typeof(int)||p[5].ParameterType!=typeof(int)||
                    p[6].ParameterType.FullName!="TMPro.AtlasPopulationMode"||(p.Length==8&&p[7].ParameterType!=typeof(bool)))continue;
                try
                {
                    var values=new List<object>{font,48,5,Enum.Parse(p[3].ParameterType,"SDFAA"),1024,1024,Enum.Parse(p[6].ParameterType,"Dynamic")};
                    if(p.Length==8)values.Add(true);var created=method.Invoke(null,values.ToArray());if(Alive(created))return created;
                }catch{}
            }
            return null;
        }
        void RestoreSize(Binding binding,object target)
        {
            if(binding.Size==null){RestoreLegacyBestFit(binding,target);return;}
            try{if(Alive(target)&&Equals(binding.Size.GetValue(target,null),binding.OwnedSize)){binding.Size.SetValue(target,binding.OriginalSize,null);if(!Equals(binding.Size.GetValue(target,null),binding.OriginalSize))return;}}
            catch{return;}
            binding.Size=null;binding.OriginalSize=null;binding.OwnedSize=null;RestoreLegacyBestFit(binding,target);
        }
        static bool SameUnityObject(object a,object b){if(ReferenceEquals(a,b))return true;if(a==null||b==null)return false;int id=Id(a);return id!=0&&id==Id(b);}
        void RestoreFont(Binding binding,object target){binding.RasterDensity=0;binding.RasterCheckAt=0;binding.Layout.Restore(target);RestoreSize(binding,target);binding.AtlasText=null;bool wasWriting=writing;writing=true;try{if(Alive(target)&&binding.Font!=null&&SameUnityObject(binding.Font.GetValue(target,null),binding.OwnedFont)&&!Alive(binding.OriginalFont))return;if(Alive(target)&&binding.Font!=null&&SameUnityObject(binding.Font.GetValue(target,null),binding.OwnedFont)&&Alive(binding.OriginalFont)){var material=staticTmpFonts.IsClone(binding.OwnedFont)?target.GetType().GetProperty("fontSharedMaterial"):null;object preset=material!=null&&material.CanRead&&material.CanWrite?material.GetValue(target,null):null;binding.Font.SetValue(target,binding.OriginalFont,null);if(!SameUnityObject(binding.Font.GetValue(target,null),binding.OriginalFont))return;if(Alive(preset))material.SetValue(target,preset,null);}}catch{return;}finally{writing=wasWriting;}binding.Font=null;binding.OriginalFont=null;binding.OwnedFont=null;}
        static bool AcceptCreatedFont(object font,bool legacyDynamic,string requiredText)
        {
            if(!Alive(font))return false;
            if(!legacyDynamic||FontContains(font,requiredText??"中"))return true;
            // This is only a freshly created, unassigned candidate. Never release
            // a shared game font or a fallback already owned by a live control.
            try{var type=FindType("UnityEngine.Object");var destroy=type==null?null:type.GetMethod("Destroy",BindingFlags.Public|BindingFlags.Static,null,new[]{type},null);if(destroy!=null)destroy.Invoke(null,new[]{font});}catch{}
            return false;
        }
        static object CreateSystemFont(Type fontType,bool legacyDynamic=false,string requiredText=null)
        {
            // TMP needs font-face data; an OS-name-only dynamic Font may not expose it.
            // Unity's public Font(string) constructor loads a file when given a path.
            var constructor=fontType.GetConstructor(new[]{typeof(string)});
            // Legacy UI needs the OS dynamic font factory. On older Mono,
            // Font(path) can return a live object without any usable face/glyphs.
            if(constructor!=null&&!legacyDynamic)
            {
                string directory=Path.Combine(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.System)),"Fonts");
                foreach(string filename in new[]{"msyh.ttc","msyh.ttf","simhei.ttf","Deng.ttf"})
                {
                    string path=Path.Combine(directory,filename);
                    if(File.Exists(path))try{var font=constructor.Invoke(new object[]{path});if(AcceptCreatedFont(font,legacyDynamic,requiredText))return font;}catch{}
                }
            }
            string[] names={"Microsoft YaHei","DengXian","SimHei"};
            var create=fontType.GetMethod("CreateDynamicFontFromOSFont",new[]{typeof(string[]),typeof(int)});
            if(create!=null)try{var font=create.Invoke(null,new object[]{names,24});if(AcceptCreatedFont(font,legacyDynamic,requiredText))return font;}catch{}
            // IL2CPP exposes native string[] as Il2CppStringArray. The generated
            // string-only helper can be an unstripping stub while this API works.
            create=fontType.GetMethods(BindingFlags.Public|BindingFlags.Static).FirstOrDefault(m=>m.Name=="CreateDynamicFontFromOSFont"&&m.GetParameters().Length==2&&m.GetParameters()[0].ParameterType.FullName=="Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray"&&m.GetParameters()[1].ParameterType==typeof(int));
            if(create!=null)try{var arrayConstructor=create.GetParameters()[0].ParameterType.GetConstructor(new[]{typeof(string[])});if(arrayConstructor!=null){var array=arrayConstructor.Invoke(new object[]{names});var font=create.Invoke(null,new object[]{array,24});if(AcceptCreatedFont(font,legacyDynamic,requiredText))return font;}}catch{}
            create=fontType.GetMethod("CreateDynamicFontFromOSFont",new[]{typeof(string),typeof(int)});
            if(create!=null)try{var fallback=create.Invoke(null,new object[]{"Microsoft YaHei",24});if(AcceptCreatedFont(fallback,legacyDynamic,requiredText))return fallback;}catch{}
#if FUSION_IL2CPP
            // A stripped wrapper can lose Font(string[], int) while retaining
            // Unity's generated native initializer. Recover that capability
            // before treating a usable native font as unsupported.
            if(legacyDynamic)try{var fallback=NativeLegacyFont.Create(fontType,names,24);if(AcceptCreatedFont(fallback,true,requiredText))return fallback;}catch{}
#endif
            return null;
        }
        static string ApplicationString(string name){try{var type=FindType("UnityEngine.Application");var property=type==null?null:type.GetProperty(name,BindingFlags.Public|BindingFlags.Static);return property==null?"":property.GetValue(null,null) as string??"";}catch{return "";}}
        static readonly HashSet<string> attemptedAssemblies=new HashSet<string>(StringComparer.Ordinal);
        public static Type FindType(string name)
        {
            lock(typeLock)
            {
                Type found;if(knownTypes.TryGetValue(name,out found))return found;
                foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies()){Type type=assembly.GetType(name,false);if(type!=null){knownTypes[name]=type;return type;}}
                string[] candidates=name.StartsWith("Il2CppInterop.",StringComparison.Ordinal)?new[]{"Il2CppInterop.Runtime"}:name.StartsWith("TMPro.",StringComparison.Ordinal)?new[]{"Unity.TextMeshPro","TextMeshPro"}:name.StartsWith("UnityEngine.UI.",StringComparison.Ordinal)?new[]{"UnityEngine.UI"}:new[]{"UnityEngine.CoreModule","UnityEngine.UIModule","UnityEngine.TextRenderingModule","UnityEngine"};
                foreach(string candidate in candidates)if(attemptedAssemblies.Add(candidate))try{var assembly=Assembly.Load(candidate);Type type=assembly.GetType(name,false);if(type!=null){knownTypes[name]=type;return type;}}catch{}
                // Do not cache misses: plugins may load their UI assembly later.
                return null;
            }
        }
        void Serve(string name,string secret)
        {
            string stage="connect",operation="none";long requestStarted=0;
            try
            {
                pipe=UnityPipeStream.Connect(name,30000,()=>stopped);
                using(var reader=new StreamReader(pipe,new UTF8Encoding(false,true)))using(var writer=new StreamWriter(pipe,new UTF8Encoding(false)){AutoFlush=true})
                {
                    // Awake can precede a long first-scene load. Do not acknowledge
                    // readiness until the callback which drains requests has run.
                    stage="main-thread-ready";long readyUntil=timer.ElapsedMilliseconds+175000;
                    while(Interlocked.CompareExchange(ref tickCount,0,0)==0&&!stopped)
                    {if(timer.ElapsedMilliseconds>=readyUntil)throw new TimeoutException("Unity main thread has not started.");Thread.Sleep(10);}
                    if(stopped)return;
                    stage="hello";
                    writer.WriteLine(BridgeJson.Write(Map("hello",1,"pid",Process.GetCurrentProcess().Id,"secret",secret,"engine","unity","protocol",1)));
                    log("Unity bridge event=connected ticks="+Interlocked.CompareExchange(ref tickCount,0,0));
                    while(!stopped)
                    {
                        stage="read";operation="none";requestStarted=0;
                        string line=ReadLine(reader);if(line==null){closeReason="server-eof";log("Unity bridge event=peer-closed");break;}
                        var body=BridgeJson.Parse(line) as Dictionary<string,object>;if(body==null)throw new IOException("Invalid request.");
                        operation=SafeOperation(String(body,"op"));requestStarted=timer.ElapsedMilliseconds;stage="main-thread-request";
                        var request=new Request{Body=body};lock(requests){if(requests.Count>=16)throw new IOException("Too many requests.");requests.Enqueue(request);}
                        bool done=request.Done.WaitOne(2000);
                        if(!done&&TryCancelUnstarted(request))
                        {
                            object id;body.TryGetValue("id",out id);
                            stage="write";writer.WriteLine(BridgeJson.Write(Map("id",id,"ok",false,"error","Unity main thread is paused; request was not started.","errorCode","unity-main-thread-paused","recoverable",true)));
                            request.Done.Close();continue;
                        }
                        if(!done&&!request.Done.WaitOne(13000)){lock(requests){request.Abandoned=true;if(request.Completed)request.Done.Close();}throw new IOException("Game main thread did not respond.");}
                        if(stopped){lock(requests)request.Done.Close();break;}stage="write";writer.WriteLine(request.Response);lock(requests)request.Done.Close();
                    }
                }
            }
            catch(Exception ex)
            {
                if(!stopped)
                {
                    closeReason="transport-"+stage;
                    var native=ex.InnerException as System.ComponentModel.Win32Exception;
                    log("Unity bridge event=connection-failed stage="+stage+" operation="+operation+" error="+ex.GetType().Name+" native="+(native==null?0:native.NativeErrorCode)+" request-ms="+(requestStarted==0?0:timer.ElapsedMilliseconds-requestStarted)+" tick-idle-ms="+(timer.ElapsedMilliseconds-Interlocked.Read(ref lastTickMilliseconds)));
                }
            }
            finally{disconnected=true;}
        }
        static string SafeOperation(string value)
        {switch(value){case "translationPrepare":case "translationEnable":case "translationDisable":case "translationPoll":case "translationCatalog":case "translationApply":case "translationDiagnostics":case "snapshot":return value;default:return "unknown";}}
        bool TryCancelUnstarted(Request request)
        {
            lock(requests)
            {
                if(request.Started||request.Completed)return false;request.Cancelled=true;
                // Remove this unstarted request now; a paused game must not fill
                // its bounded queue with retries. Tick shares this exact lock.
                int count=requests.Count;while(count-->0){var item=requests.Dequeue();if(!ReferenceEquals(item,request))requests.Enqueue(item);}
                return true;
            }
        }
        static string ReadLine(TextReader reader){var b=new StringBuilder();while(b.Length<1024*1024){int c=reader.Read();if(c<0)return b.Length==0?null:b.ToString();if(c=='\n')return b.ToString();if(c!='\r')b.Append((char)c);}throw new IOException("Request limit exceeded.");}
        internal bool IsStopped{get{return stopped;}}
        public void Dispose(){Dispose(false);}
        // Quitting the process has no surviving controls to restore. Rebuilding
        // every translated mesh here delays exit and touches tearing-down scenes.
        public void Shutdown(){if(stopped)return;closeReason="application-quit";Dispose(true);}
        void Dispose(bool applicationQuit){if(stopped)return;enabled=false;if(!applicationQuit){if(!restoreSession||restoreQueue.Count>0)RefreshBindings();foreach(var binding in bindings.Values)ReleaseProcessor(binding);}stopped=true;if(il2Cpp)AppDomain.CurrentDomain.AssemblyLoad-=NativeAssemblyLoaded;LogMetrics(true);log("Unity bridge event=closed reason="+closeReason+" ticks="+Interlocked.CompareExchange(ref tickCount,0,0));if(current==this)current=null;if(displaySources!=null)displaySources.Dispose();if(legacyProjection!=null)legacyProjection.Dispose();if(bufferedProjection!=null)bufferedProjection.Dispose();try{if(canvasRemove!=null&&canvasCallback!=null)canvasRemove.Invoke(null,new[]{canvasCallback});}catch{}canvasRemove=null;canvasCallback=null;canvasManagedCallback=null;try{if(fontAtlasRemove!=null&&fontAtlasCallback!=null)fontAtlasRemove.Invoke(null,new[]{fontAtlasCallback});}catch{}fontAtlasRemove=null;fontAtlasCallback=null;fontAtlasManagedCallback=null;atlasRefresh.Clear();atlasQueued.Clear();if(!applicationQuit){ReleaseLegacyAutoFonts();if(!bindings.Values.Any(b=>Alive(b.Target)&&b.OwnedFont!=null))staticTmpFonts.Dispose();}uiFonts.Clear();try{if(pipe!=null)pipe.Dispose();}catch{}try{if(!applicationQuit&&!il2Cpp)harmony.UnpatchSelf();}catch{}bindings.Clear();discovered.Clear();ClearAheadDiscovery();aheadDiscovered.Clear();lateTargets.Clear();restoreQueue.Clear();pendingRefresh.Clear();refreshQueued.Clear();frameVisited.Clear();tmpFonts.Clear();nativeTypes.Clear();unsupportedNativeTypes.Clear();unsupportedNativeLogged.Clear();queryRetryAt.Clear();dynamicFont=null;startupTranslations.Clear();stableSources.Clear();lock(requests){while(requests.Count>0)requests.Dequeue().Done.Set();}}
        public void RestoreAndDispose(){if(stopped)return;closeReason="plugin-destroyed";Dispose();}
    }
}





