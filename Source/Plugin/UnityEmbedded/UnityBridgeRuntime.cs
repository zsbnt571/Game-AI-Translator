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
            internal int Id; internal object Target; internal PropertyInfo Text; internal string Source,Applied;
            internal object OriginalFont,OwnedFont; internal PropertyInfo Font;
            internal bool LastActive;internal long LastObserved;
        }
        sealed class Request
        {
            internal Dictionary<string,object> Body; internal string Response; internal readonly ManualResetEvent Done=new ManualResetEvent(false);
        }
        static UnityBridgeRuntime current;
        readonly Dictionary<int,Binding> bindings=new Dictionary<int,Binding>();
        readonly Dictionary<string,string> translations=new Dictionary<string,string>(StringComparer.Ordinal);
        readonly TextLookup lookup=new TextLookup(); bool lookupDirty=true;
        readonly Dictionary<string,string> staged=new Dictionary<string,string>(StringComparer.Ordinal);bool preparing;
        readonly Queue<string> visible=new Queue<string>();readonly HashSet<string> queued=new HashSet<string>(StringComparer.Ordinal);
        readonly Queue<string> ahead=new Queue<string>();readonly HashSet<string> aheadQueued=new HashSet<string>(StringComparer.Ordinal);
        readonly Queue<Request> requests=new Queue<Request>();readonly List<Type> textTypes=new List<Type>();
        readonly HashSet<MethodBase> patched=new HashSet<MethodBase>();readonly Harmony harmony=new Harmony("local.fusion.unity.embedded.73");
        int prefixCacheHits,enableCacheHits,renderCacheHits,lateCacheHits;string fontStatus="not requested";
        readonly bool il2Cpp;readonly Queue<Binding> lateTargets=new Queue<Binding>();
        readonly Queue<Binding> pendingRefresh=new Queue<Binding>();readonly HashSet<Binding> refreshQueued=new HashSet<Binding>(),frameVisited=new HashSet<Binding>();
        double lastLookupMilliseconds,maxLookupMilliseconds,lastRestoreMilliseconds,maxRestoreMilliseconds,lastLateMilliseconds,maxLateMilliseconds;long lastMetricsAt;
        readonly Action<string> log;readonly Stopwatch timer=Stopwatch.StartNew();
        Stream pipe;Thread thread;volatile bool stopped,disconnected;bool enabled,writing;int epoch;long scanAt;int scanCursor;
        int tickCount;long lastTickMilliseconds;string closeReason="dispose";
        readonly Queue<object> discovered=new Queue<object>();object dynamicFont; readonly Dictionary<Type,object> tmpFonts=new Dictionary<Type,object>();
        public UnityBridgeRuntime(Action<string> log):this(log,false){}
        public UnityBridgeRuntime(Action<string> log,bool il2Cpp){this.log=log;this.il2Cpp=il2Cpp;}
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
                foreach(var value in entries){var entry=value as Dictionary<string,object>;if(entry==null)continue;string source=String(entry,"source"),text=String(entry,"text");if(Eligible(source)&&!string.IsNullOrEmpty(text)&&text.Length<=24000)translations[source]=text;}
                enabled=Environment.GetEnvironmentVariable("FUSION_UNITY_TRANSLATION_START")=="1";
            }
            catch(Exception ex){translations.Clear();enabled=false;log("Startup translations unavailable: "+ex.GetType().Name);}
        }
        public void Tick()
        {
            if(stopped)return;if(disconnected){Disable();Dispose();return;}
            Interlocked.Exchange(ref lastTickMilliseconds,timer.ElapsedMilliseconds);
            if(Interlocked.Increment(ref tickCount)==1)log("Unity bridge event=main-thread-ready");
            for(int count=0;count<8;count++)
            {
                Request request;lock(requests){if(requests.Count==0)break;request=requests.Dequeue();}
                object id;request.Body.TryGetValue("id",out id);
                try{request.Response=BridgeJson.Write(Map("id",id,"ok",true,"result",Handle(request.Body)));}
                catch(Exception ex){request.Response=BridgeJson.Write(Map("id",id,"ok",false,"error",ex is InvalidOperationException?ex.Message:"Unity bridge operation failed."));log("Request failed: "+ex.GetType().Name);}
                request.Done.Set();
            }
            if(timer.ElapsedMilliseconds>=scanAt)
            {
                scanAt=timer.ElapsedMilliseconds+(il2Cpp?200:400);InstallHooks();Discover();
            }
            // The fallback scanner processes a bounded set of already discovered UI components.
            long until=timer.ElapsedTicks+Stopwatch.Frequency/500;
            for(int count=0;count<48&&discovered.Count>0&&timer.ElapsedTicks<until;count++)
            {
                var target=discovered.Dequeue();try{var property=target.GetType().GetProperty("text");if(property!=null&&property.PropertyType==typeof(string))CaptureExisting(target,property);}catch{}
            }
        }
        public void LateTick()
        {
            if(stopped||disconnected)return;
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
                if(!Alive(binding.Target)){bindings.Remove(binding.Id);continue;}
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
                "Unity bridge metrics pending={0} bindings={1} maxLookupMs={2:F3} maxLateFrameMs={3:F3} maxRestoreMs={4:F3}",
                pendingRefresh.Count,bindings.Count,maxLookupMilliseconds,maxLateMilliseconds,maxRestoreMilliseconds));}catch{}
        }
        void ObserveBinding(Binding binding)
        {
            Binding registered;if(!frameVisited.Add(binding)||!bindings.TryGetValue(binding.Id,out registered)||!ReferenceEquals(binding,registered))return;
            if(!Alive(binding.Target)){bindings.Remove(binding.Id);return;}
            try{if(CaptureExisting(binding.Target,binding.Text))lateCacheHits++;}catch{}
        }
        void ScheduleRefresh(HashSet<string> changed)
        {
            foreach(var binding in bindings.Values.Where(b=>changed==null||b.Source!=null&&changed.Contains(b.Source)).OrderByDescending(b=>b.LastActive).ThenByDescending(b=>b.LastObserved))
                if(refreshQueued.Count<12000&&refreshQueued.Add(binding))pendingRefresh.Enqueue(binding);
        }
        void EnsureLookup()
        {
            if(!lookupDirty)return;long started=timer.ElapsedTicks;lookup.Rebuild(translations);lookupDirty=false;
            lastLookupMilliseconds=(timer.ElapsedTicks-started)*1000.0/Stopwatch.Frequency;maxLookupMilliseconds=Math.Max(maxLookupMilliseconds,lastLookupMilliseconds);
        }
        void InstallHooks()
        {
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
                var onEnable=type.GetMethod("OnEnable",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly);
                if(onEnable!=null&&!patched.Contains(onEnable))try{harmony.Patch(onEnable,new HarmonyMethod(typeof(UnityBridgeRuntime).GetMethod("EnablePrefix",BindingFlags.NonPublic|BindingFlags.Static)));patched.Add(onEnable);}catch(Exception ex){log("Enable hook unavailable: "+type.FullName+" ("+ex.GetType().Name+")");}
                foreach(string renderName in new[]{"OnPopulateMesh","OnPreRenderCanvas","OnPreRenderObject","GenerateTextMesh"})
                {
                    foreach(var render in type.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly).Where(m=>m.Name==renderName&&!m.IsAbstract))
                        if(!patched.Contains(render))try{harmony.Patch(render,new HarmonyMethod(typeof(UnityBridgeRuntime).GetMethod("RenderPrefix",BindingFlags.NonPublic|BindingFlags.Static)));patched.Add(render);}catch(Exception ex){log("Render hook unavailable: "+type.FullName+" ("+ex.GetType().Name+")");}
                }
                if(name=="TMPro.TMP_Text")foreach(var method in type.GetMethods(BindingFlags.Public|BindingFlags.Instance))
                    if(method.Name=="SetText"&&method.GetParameters().Length>0&&method.GetParameters()[0].ParameterType==typeof(string))Patch(method);
            }
        }
        void Patch(MethodBase method)
        {
            if(method!=null&&method.ReflectedType!=method.DeclaringType)method=method.DeclaringType.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly).FirstOrDefault(m=>m.MetadataToken==method.MetadataToken);
            if(method==null||patched.Contains(method))return;
            try{harmony.Patch(method,new HarmonyMethod(typeof(UnityBridgeRuntime).GetMethod("TextPrefix",BindingFlags.NonPublic|BindingFlags.Static)));patched.Add(method);}
            catch(Exception ex){log("Text hook unavailable: "+method.DeclaringType.FullName+"."+method.Name+" ("+ex.GetType().Name+")");}
        }
        static void TextPrefix(object __instance,ref string __0)
        {
            var runtime=current;if(runtime==null||runtime.stopped||runtime.writing||__instance==null)return;
            try{string original=__0;__0=runtime.Capture(__instance,__0);if(original!=__0)runtime.prefixCacheHits++;}catch{}
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
            if(source==null||!Alive(target)||IsInput(target))return source;
            var property=target.GetType().GetProperty("text");if(property==null||!property.CanRead||!property.CanWrite)return source;
            int id=Id(target);if(id==0)return source;Binding binding;
            if(bindings.TryGetValue(id,out binding)&&!ReferenceEquals(binding.Target,target))
            {
                // IL2CPP can produce another managed wrapper for the same native object.
                if(Id(binding.Target)!=id)bindings.Remove(id);else binding.Target=target;
            }
            if(!bindings.TryGetValue(id,out binding))
            {
                if(bindings.Count>12000)Prune();if(bindings.Count>12000)return source;
                binding=new Binding{Id=id,Target=target,Text=property};bindings[id]=binding;
                lateTargets.Enqueue(binding);
                if(lateTargets.Count>16000)CompactLateTargets();
            }
            binding.LastActive=Active(target);binding.LastObserved=timer.ElapsedTicks;
            // A game may echo the already displayed value. Keep our ownership in that case.
            if(binding.Applied!=null&&source==binding.Applied)
            {
                if(enabled)
                {
                    if(!binding.LastActive||IsRevealing(target))return source;
                    // Cache revisions and a newly enabled profile must resolve from
                    // the owned original, not repeatedly echo the old displayed value.
                    EnsureLookup();string revised;
                    if(lookup.TryGet(translations,binding.Source??"",out revised)&&PrepareFont(binding,target,revised))
                    {binding.Applied=revised;return revised;}
                }
                RestoreFont(binding,target);binding.Applied=null;
                if(enabled&&Eligible(binding.Source))QueueText(target,binding.Source);
                return binding.Source;
            }
            RestoreFont(binding,target);binding.Source=source;binding.Applied=null;
            if(!Eligible(source))return source;
            if(enabled)
            {
                if(!binding.LastActive||IsRevealing(target)){QueueText(target,source);return source;}
                EnsureLookup();
                string translated;if(lookup.TryGet(translations,source,out translated)){if(PrepareFont(binding,target,translated)){binding.Applied=translated;return translated;}return source;}
                QueueText(target,source);
            }
            return source;
        }
        bool CaptureExisting(object target,PropertyInfo property)
        {
            if(!Alive(target))return false;
            string source=ReadText(target,property);if(source==null)return false;
            string displayed=Capture(target,source);if(displayed==source)return false;
            Write(target,property,displayed);return true;
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
        void Discover()
        {
            if(discovered.Count>0||textTypes.Count==0)return;
            // Resource-wide discovery includes inactive prefabs. Prefer scene objects
            // so a large preloaded menu does not postpone a newly visible dialogue.
            // Rotate component families rather than issuing several full scans in one frame.
            var type=textTypes[scanCursor++%textTypes.Count];
            try{var items=FindSceneObjects(type);if(items!=null)foreach(var item in items){if(discovered.Count>=16000)break;if(item!=null)discovered.Enqueue(item);}}catch(Exception ex){log("UI discovery unavailable: "+ex.GetType().Name);}
            Prune();
        }
        static IEnumerable FindSceneObjects(Type type)
        {
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
        void Prune(){foreach(var pair in bindings.ToArray())if(!Alive(pair.Value.Target))bindings.Remove(pair.Key);CompactLateTargets();}
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
        {
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
        static bool Eligible(string value){return value!=null&&value.Length>=2&&value.Length<=6000&&Regex.IsMatch(value,@"\p{L}")&&!Regex.IsMatch(value,@"^(?:\w+://|[\w./\\-]+\.(?:png|jpg|wav|ogg|json|asset|prefab))$",RegexOptions.IgnoreCase);}
        static Dictionary<string,object> Map(params object[] values){var result=new Dictionary<string,object>(StringComparer.Ordinal);for(int i=0;i<values.Length;i+=2)result.Add((string)values[i],values[i+1]);return result;}
        static string String(Dictionary<string,object> data,string name){object value;return data.TryGetValue(name,out value)?value as string:null;}
        static int Number(Dictionary<string,object> data,string name,int fallback){object value;return data.TryGetValue(name,out value)?Convert.ToInt32(value):fallback;}
        object Handle(Dictionary<string,object> request)
        {
            switch(String(request,"op"))
            {
                case "translationPrepare": epoch++;preparing=true;staged.Clear();visible.Clear();queued.Clear();ahead.Clear();aheadQueued.Clear();return Map("epoch",epoch);
                case "translationEnable":lookupDirty=true;if(preparing){translations.Clear();foreach(var pair in staged)translations[pair.Key]=pair.Value;staged.Clear();preparing=false;}enabled=true;ScheduleRefresh(null);scanAt=0;return Map("epoch",epoch,"enabled",true,"writebackPending",pendingRefresh.Count);
                case "translationDisable":Disable();return Map("epoch",epoch,"enabled",false);
                case "translationPoll":var result=new List<string>();while(visible.Count>0&&result.Count<128){string value=visible.Dequeue();queued.Remove(value);result.Add(value);}var later=new List<string>();while(ahead.Count>0&&later.Count<128){string value=ahead.Dequeue();aheadQueued.Remove(value);if(!result.Contains(value)&&!queued.Contains(value))later.Add(value);}return Map("epoch",epoch,"texts",result,"ahead",later);
                case "translationCatalog":return Map("total",0,"next",0,"skipped",0,"texts",new string[0]);
                case "translationApply":
                    lookupDirty=true;
                    if(Number(request,"epoch",-1)!=epoch)throw new InvalidOperationException("Translation session changed; stale result rejected.");
                    object values;var entries=request.TryGetValue("entries",out values)?values as IList:null;if(entries==null||entries.Count>128)throw new InvalidOperationException("Invalid translation batch.");
                    foreach(var entry in entries){var item=entry as Dictionary<string,object>;if(item==null)throw new InvalidOperationException("Invalid translation entry.");string source=String(item,"source"),text=String(item,"text");if(!Eligible(source)||string.IsNullOrEmpty(text)||text.Length>24000)throw new InvalidOperationException("Invalid translated text.");}
                    bool prime=request.TryGetValue("prime",out values)&&values is bool&&(bool)values;
                    if(prime&&!preparing)throw new InvalidOperationException("No prepared translation session.");
                    var destination=prime?staged:translations;if(destination.Count+entries.Count>150000)throw new InvalidOperationException("Translation cache limit exceeded.");
                    var changed=new HashSet<string>(StringComparer.Ordinal);foreach(Dictionary<string,object> entry in entries){string source=String(entry,"source");destination[source]=String(entry,"text");changed.Add(source);}if(enabled&&!prime)ScheduleRefresh(changed);
                    return Map("epoch",epoch,"applied",entries.Count,"stored",entries.Count,"writebackPending",pendingRefresh.Count,"writebackConfirmed",false);
                case "snapshot":return Map("generation",epoch,"rows",new object[0],"state","translationOnly");
                case "translationDiagnostics":
                    return Map("epoch",epoch,"enabled",enabled,"hooks",patched.Count,"bindings",bindings.Count,"cache",translations.Count,"setterCacheHits",prefixCacheHits,"enableCacheHits",enableCacheHits,"renderCacheHits",renderCacheHits,"lateCacheHits",lateCacheHits,"hookMode",il2Cpp?"frame-scan":"harmony",
                        "companyName",ApplicationString("companyName"),"productName",ApplicationString("productName"),"persistentDataPath",ApplicationString("persistentDataPath"),"unityVersion",ApplicationString("unityVersion"),
                        "fontStatus",fontStatus,"fontsOwned",bindings.Values.Count(b=>b.OwnedFont!=null),"framePolling",true,"discoveryIntervalMs",il2Cpp?200:400,"captureFamilies",textTypes.Select(t=>t.FullName).ToArray(),
                        "writebackPending",pendingRefresh.Count,"lastLookupMs",lastLookupMilliseconds,"maxLookupMs",maxLookupMilliseconds,"lastRestoreMs",lastRestoreMilliseconds,"maxRestoreMs",maxRestoreMilliseconds,"lastLateFrameMs",lastLateMilliseconds,"maxLateFrameMs",maxLateMilliseconds,
                        "observed",bindings.Values.OrderByDescending(b=>Active(b.Target)).Take(32).Select(b=>Map("source",Preview(b.Source),"current",Preview(BoundText(b)),"active",Active(b.Target),"owned",b.Applied!=null)).ToArray(),
                        "displayed",bindings.Values.Where(b=>b.Applied!=null&&BoundText(b)==b.Applied).OrderByDescending(b=>Active(b.Target)).Take(32).Select(b=>Map("source",Preview(b.Source),"text",Preview(BoundText(b)),"active",Active(b.Target),"component",b.Target==null?"":b.Target.GetType().FullName)).ToArray(),
                        "requested",DiagnoseSources(request));
                default:throw new InvalidOperationException("This Unity bridge provides translation only.");
            }
        }
        static string BoundText(Binding binding){if(!Alive(binding.Target))return null;try{return ReadText(binding.Target,binding.Text);}catch{return null;}}
        static string Preview(string text){return text==null||text.Length<=512?text:text.Substring(0,512)+"…";}
        object[] DiagnoseSources(Dictionary<string,object> request)
        {
            object raw;var sources=request.TryGetValue("sources",out raw)?raw as IList:null;
            if(sources==null)return new object[0];if(sources.Count>32)throw new InvalidOperationException("Too many diagnostic sources.");
            var rows=new List<object>();EnsureLookup();
            foreach(var item in sources)
            {
                string source=item as string;if(!Eligible(source))throw new InvalidOperationException("Invalid diagnostic source.");
                string translated;bool cached=lookup.TryGet(translations,source,out translated);
                var matches=bindings.Values.Where(b=>b.Source==source&&Alive(b.Target)).ToArray();
                rows.Add(Map("source",source,"captured",matches.Length>0,"cached",cached,"bindingCount",matches.Length,"bindings",matches.OrderByDescending(b=>Active(b.Target)).Take(8).Select(b=>Map("active",Active(b.Target),"current",Preview(BoundText(b)),"writebackConfirmed",cached&&b.Applied==translated&&BoundText(b)==translated,"component",b.Target.GetType().FullName)).ToArray()));
            }
            return rows.ToArray();
        }
        void RefreshBindings()
        {
            long started=timer.ElapsedTicks;
            foreach(var binding in bindings.Values.ToArray())
            {
                object target=binding.Target;if(!Alive(target))continue;
                try{string present=ReadText(target,binding.Text);if(present!=binding.Source&&present!=binding.Applied){RestoreFont(binding,target);continue;}
                    if(enabled&&(!Active(target)||IsRevealing(target))){if(Eligible(binding.Source))QueueText(target,binding.Source);continue;}
                    if(enabled)EnsureLookup();
                    string display=binding.Source;string translated;if(enabled&&lookup.TryGet(translations,binding.Source??"",out translated)){if(PrepareFont(binding,target,translated))display=translated;}else RestoreFont(binding,target);
                    binding.Applied=enabled&&display!=binding.Source?display:null;if(display!=present)Write(target,binding.Text,display);
                    if(enabled&&Eligible(binding.Source)&&binding.Applied==null)QueueText(target,binding.Source);
                }catch{}
            }
            lastRestoreMilliseconds=(timer.ElapsedTicks-started)*1000.0/Stopwatch.Frequency;maxRestoreMilliseconds=Math.Max(maxRestoreMilliseconds,lastRestoreMilliseconds);
        }
        void Disable(){enabled=false;epoch++;preparing=false;staged.Clear();visible.Clear();queued.Clear();ahead.Clear();aheadQueued.Clear();pendingRefresh.Clear();refreshQueued.Clear();RefreshBindings();}
        void Write(object target,PropertyInfo property,string text){if(!Alive(target))return;writing=true;try{property.SetValue(target,text,null);}finally{writing=false;}}
        bool PrepareFont(Binding binding,object target,string text)
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
                if(Alive(binding.OwnedFont)&&SameUnityObject(existing,binding.OwnedFont))return true;
                object replacement=null;var fontType=FindType("UnityEngine.Font");
                if(fontType!=null&&fontProperty.PropertyType==fontType)
                {
                    var hasChar=fontType.GetMethod("HasCharacter",new[]{typeof(char)});if(hasChar!=null&&text.Where(c=>c>=0x2e80).All(c=>Convert.ToBoolean(hasChar.Invoke(existing,new object[]{c}))))return true;
                    if(!Alive(dynamicFont))dynamicFont=CreateSystemFont(fontType);replacement=dynamicFont;
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
                        if(factory!=null)replacement=factory.Invoke(null,new object[]{"Microsoft YaHei","Regular",48});
                        if(!Alive(replacement)&&fontType!=null)
                        {
                            if(!Alive(dynamicFont))dynamicFont=CreateSystemFont(fontType);
                            var legacy=assetType.GetMethods(BindingFlags.Public|BindingFlags.Static).FirstOrDefault(m=>m.Name=="CreateFontAsset"&&m.GetParameters().Length==1&&m.GetParameters()[0].ParameterType==fontType);
                            if(legacy!=null&&Alive(dynamicFont))replacement=legacy.Invoke(null,new[]{dynamicFont});
                            if(!Alive(replacement))fontStatus="TMP font creation returned no live asset; factory="+(legacy!=null)+", systemFont="+Alive(dynamicFont);
                        }
                        if(Alive(replacement))tmpFonts[assetType]=replacement;
                        else if(fontType==null)fontStatus="Unity Font type unavailable";
                    }
                }
                if(!Alive(replacement))return false;if(SameUnityObject(existing,replacement))return true;
                binding.Font=fontProperty;binding.OriginalFont=existing;binding.OwnedFont=replacement;fontProperty.SetValue(target,replacement,null);fontStatus="fallback assigned";return true;
            }catch(Exception ex){log("Font fallback unavailable: "+ex.GetType().Name);return false;}finally{writing=wasWriting;}
        }
        static bool SameUnityObject(object a,object b){if(ReferenceEquals(a,b))return true;if(a==null||b==null)return false;int id=Id(a);return id!=0&&id==Id(b);}
        void RestoreFont(Binding binding,object target){bool wasWriting=writing;writing=true;try{if(Alive(target)&&binding.Font!=null&&SameUnityObject(binding.Font.GetValue(target,null),binding.OwnedFont)&&Alive(binding.OriginalFont))binding.Font.SetValue(target,binding.OriginalFont,null);}catch{}finally{writing=wasWriting;}binding.Font=null;binding.OriginalFont=null;binding.OwnedFont=null;}
        static object CreateSystemFont(Type fontType)
        {
            // TMP needs font-face data; an OS-name-only dynamic Font may not expose it.
            // Unity's public Font(string) constructor loads a file when given a path.
            var constructor=fontType.GetConstructor(new[]{typeof(string)});
            if(constructor!=null)
            {
                string directory=Path.Combine(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.System)),"Fonts");
                foreach(string filename in new[]{"msyh.ttc","msyh.ttf","simhei.ttf","Deng.ttf"})
                {
                    string path=Path.Combine(directory,filename);
                    if(File.Exists(path))try{var font=constructor.Invoke(new object[]{path});if(Alive(font))return font;}catch{}
                }
            }
            var create=fontType.GetMethod("CreateDynamicFontFromOSFont",new[]{typeof(string[]),typeof(int)});if(create!=null){var font=create.Invoke(null,new object[]{new[]{"Microsoft YaHei","DengXian","SimHei"},24});if(Alive(font))return font;}
            create=fontType.GetMethod("CreateDynamicFontFromOSFont",new[]{typeof(string),typeof(int)});var fallback=create==null?null:create.Invoke(null,new object[]{"Microsoft YaHei",24});return Alive(fallback)?fallback:null;
        }
        static string ApplicationString(string name){try{var type=FindType("UnityEngine.Application");var property=type==null?null:type.GetProperty(name,BindingFlags.Public|BindingFlags.Static);return property==null?"":property.GetValue(null,null) as string??"";}catch{return "";}}
        static readonly HashSet<string> attemptedAssemblies=new HashSet<string>(StringComparer.Ordinal);
        public static Type FindType(string name)
        {
            lock(typeLock)
            {
                Type found;if(knownTypes.TryGetValue(name,out found))return found;
                foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies()){Type type=assembly.GetType(name,false);if(type!=null){knownTypes[name]=type;return type;}}
                string[] candidates=name.StartsWith("TMPro.",StringComparison.Ordinal)?new[]{"Unity.TextMeshPro","TextMeshPro"}:name.StartsWith("UnityEngine.UI.",StringComparison.Ordinal)?new[]{"UnityEngine.UI"}:new[]{"UnityEngine.CoreModule","UnityEngine.TextRenderingModule","UnityEngine"};
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
                        if(!request.Done.WaitOne(15000))throw new IOException("Game main thread did not respond.");
                        if(stopped)break;stage="write";writer.WriteLine(request.Response);request.Done.Close();
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
        static string ReadLine(TextReader reader){var b=new StringBuilder();while(b.Length<1024*1024){int c=reader.Read();if(c<0)return b.Length==0?null:b.ToString();if(c=='\n')return b.ToString();if(c!='\r')b.Append((char)c);}throw new IOException("Request limit exceeded.");}
        internal bool IsStopped{get{return stopped;}}
        public void Dispose(){if(stopped)return;stopped=true;LogMetrics(true);log("Unity bridge event=closed reason="+closeReason+" ticks="+Interlocked.CompareExchange(ref tickCount,0,0));if(current==this)current=null;try{if(pipe!=null)pipe.Dispose();}catch{}try{if(!il2Cpp)harmony.UnpatchSelf();}catch{}bindings.Clear();discovered.Clear();lateTargets.Clear();pendingRefresh.Clear();refreshQueued.Clear();frameVisited.Clear();tmpFonts.Clear();dynamicFont=null;lock(requests){while(requests.Count>0)requests.Dequeue().Done.Set();}}
        public void RestoreAndDispose(){if(stopped)return;closeReason="plugin-destroyed";Disable();Dispose();}
    }
}




