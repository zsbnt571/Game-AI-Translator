using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[assembly:AssemblyVersion("0.6.0.21")]
[assembly:AssemblyFileVersion("0.6.0.21")]
namespace Fusion.CloudMeadow
{
    [BepInPlugin("local.codex.cloudmeadow.translator","Fusion Cloud Meadow Adapter Candidate","0.6.0.21")]
    public sealed partial class CloudMeadowPlugin : BaseUnityPlugin
    {
        internal static CloudMeadowPlugin Instance;
        CloudTranslation translation;CloudFonts fonts;CloudTextLayout textLayout;CloudTmpLayout tmpLayout;Harmony harmony;
        Type revealType,bubbleType,optionType,tooltipType,modalType;
        float nextScan;int writeDepth,parseDepth,generation;bool ready;
        readonly Dictionary<int,Binding> bindings=new Dictionary<int,Binding>();
        readonly HashSet<int> ownLabels=new HashSet<int>();
        readonly Dictionary<int,Rejected> rejected=new Dictionary<int,Rejected>();
        readonly HashSet<int> localSettles=new HashSet<int>();
        sealed class Rejected {internal Component Label;internal string Source;}
        sealed class Binding
        {
            internal Component Owner,Label;internal UnityEngine.SceneManagement.Scene Scene;
            internal int Generation,Priority,LayoutWaits,SettleAttempts;internal string Source,Expected,Applied,Kind,LocalValue;
            internal float NextApply,NextLayout,NextEnqueue,ReadyAt;
            internal bool AllowApply,WasVisible,Blocked,WaitingLayout;
            internal CloudTextProfile Profile;internal CloudTrace Trace;internal BubbleCall Bubble;
        }
        static object Field(object o,string name){return AccessTools.Field(o.GetType(),name).GetValue(o);}
        static string Read(Component c){var tmp=c as TMP_Text;return tmp!=null?tmp.text:((Text)c).text;}
        static bool Narrative(Binding b){return b.Profile.Role==CloudTextRole.Dialogue||b.Profile.Role==CloudTextRole.DialogueOption||CloudDisplayRoles.Narrative(b.Profile);}
        bool TryValue(Binding b,out string value)
        {
            value=b.LocalValue;if(value==null&&!translation.TryGet(b.Source,out value,Narrative(b),b.Profile.Role.ToString()))return false;
            value=CloudManualLayout.Display(b.Profile,value);return true;
        }
        static bool Live(Component c){return c!=null&&c.gameObject.activeInHierarchy&&c.gameObject.scene.IsValid();}
        void Awake()
        {
            try
            {
                if(Instance!=null){enabled=false;return;}Instance=this;
                revealType=AccessTools.TypeByName("Game.UI.TMPDialogueReveal");bubbleType=AccessTools.TypeByName("TeamNimbus.CloudMeadow.UI.DialogueBubbleManager");optionType=AccessTools.TypeByName("TeamNimbus.CloudMeadow.Dialogue.DialogWindowOptionClickable");
                if(revealType==null||bubbleType==null||optionType==null)throw new InvalidOperationException("Target text types are unavailable.");
                tooltipType=AccessTools.TypeByName("TeamNimbus.CloudMeadow.UI.StandardTooltipManager");modalType=AccessTools.TypeByName("TeamNimbus.CloudMeadow.UI.ModalDialogWindow");
                translation=new CloudTranslation(Config,Logger);fonts=new CloudFonts(System.IO.Path.GetDirectoryName(Info.Location));textLayout=new CloudTextLayout(translation.Diagnostics);tmpLayout=new CloudTmpLayout(translation.Diagnostics);
                harmony=new Harmony("local.codex.cloudmeadow.translator");
                Patch(AccessTools.Method(revealType,"StartRevealingNewString"),"RevealPrefix","RevealPostfix");
                Patch(AccessTools.Method(revealType,"ShowRevealedString"),"RevealPrefix","RevealPostfix");
                Patch(AccessTools.Method(revealType,"OnDisable"),"OwnerDisabled",null);
                InitializeBubbleLifecycle();
                Patch(AccessTools.Method(bubbleType,"OnDisable"),"OwnerDisabled",null);
                // Do not replace IDs, delegates, database values, or dialogue instructions.
                Patch(AccessTools.Method(optionType,"HandleClicked"),"OptionPrefix",null,"OptionFinalizer");
                var parser=AccessTools.TypeByName("Game.UI.Dialogue.TMPCustomTagsTextPreprocessor");
                Patch(AccessTools.Method(parser,"ParseText"),"ParsePrefix",null,"ParseFinalizer");
                Patch(AccessTools.Method(tooltipType,"ShowToolTip"),null,"TooltipShown");
                Patch(AccessTools.Method(tooltipType,"UpdateText"),null,"TooltipUpdated");
                Patch(AccessTools.Method(AccessTools.TypeByName("TeamNimbus.CloudMeadow.Inventory.InventoryWindowScrollviewEntry"),"Initialize"),null,"InventoryInitialized");
                Patch(AccessTools.Method(AccessTools.TypeByName("TeamNimbus.CloudMeadow.Dialogue.DialogHistoryEntryManager"),"SharedDialogTextSetup"),null,"HistoryInitialized");
                Patch(AccessTools.PropertySetter(typeof(Text),"text"),null,"TextChanged");
                Patch(AccessTools.Method(typeof(Text),"OnEnable"),null,"TextActivated");
                Patch(AccessTools.Method(typeof(TextMeshProUGUI),"OnEnable"),null,"TextActivated");
                Patch(AccessTools.Method(typeof(Text),"OnPopulateMesh",new[]{typeof(VertexHelper)}),"TextMesh",null);
                Patch(AccessTools.PropertyGetter(typeof(Text),"preferredWidth"),null,"PreferredWidth");
                Patch(AccessTools.PropertyGetter(typeof(Text),"preferredHeight"),null,"PreferredHeight");
                Patch(AccessTools.PropertyGetter(typeof(TMP_Text),"preferredWidth"),null,"TmpPreferredWidth");
                Patch(AccessTools.PropertyGetter(typeof(TMP_Text),"preferredHeight"),null,"TmpPreferredHeight");
                Patch(AccessTools.PropertySetter(typeof(TMP_Text),"text"),null,"TextChanged");
                foreach(var method in typeof(TMP_Text).GetMethods(BindingFlags.Public|BindingFlags.Instance))
                    if(method.Name=="SetText")Patch(method,null,"TextChanged");
                var spacing=AccessTools.TypeByName("UnityEngine.UI.LetterSpacing");
                if(spacing!=null)Patch(AccessTools.Method(spacing,"ModifyMesh",new[]{typeof(VertexHelper)}),"TutorialSpacing",null);
                ready=true;Logger.LogInfo("Cloud Meadow hooks installed (candidate, not a translation-success signal). Toggle="+translation.ToggleKey);
            }
            catch
            {
                if(harmony!=null)harmony.UnpatchSelf();Logger.LogError("Cloud Meadow adapter could not initialize. Check installed candidate files and required game version. Game text remains original.");enabled=false;
            }
        }
        void Patch(MethodBase target,string prefix,string postfix,string finalizer=null)
        {
            if(target==null)throw new MissingMethodException("Required game text hook is missing.");
            harmony.Patch(target,prefix==null?null:new HarmonyMethod(typeof(CloudMeadowPlugin),prefix),postfix==null?null:new HarmonyMethod(typeof(CloudMeadowPlugin),postfix),null,finalizer==null?null:new HarmonyMethod(typeof(CloudMeadowPlugin),finalizer),null);
        }
        void OnDestroy()
        {
            ready=false;if(harmony!=null)harmony.UnpatchSelf();
            writeDepth++;try{foreach(var b in bindings.Values)if(Current(b)&&b.Applied!=null)RestoreDisplay(b);}catch{}finally{writeDepth--;}
            if(textLayout!=null)textLayout.Dispose();if(tmpLayout!=null)tmpLayout.Dispose();if(fonts!=null)fonts.Dispose();CloudReaderUpdates.Shutdown();if(Instance==this)Instance=null;
        }
        void Event(Binding b,string stage,string detail=null)
        {translation.Diagnostics.Event(b.Trace,stage,b.Trace.Job,-1,detail);}
        void Renew(Binding b,string reason)
        {
            // Same component/content/layout identity keeps its event history. Repeated
            // native redraws still get the cached translation, but not a new log identity.
            // Capture/RecaptureContext creates a fresh trace for real identity changes.
            // Bubble call timing starts at the pre-Show hit; first visibility
            // must not discard the activation/layout/animation interval.
            if(b.Kind!="bubble")b.ReadyAt=0;
            b.SettleAttempts=0;
            if(CloudRemainingUi.Scoped(b.Profile)||b.Profile.Role==CloudTextRole.DialogueOption)
            {
                b.Blocked=false;b.NextApply=b.NextLayout=0;
                textLayout.Reactivated(b.Label);tmpLayout.Reactivated(b.Label);
            }
            Event(b,reason,"generation="+b.Generation);
        }
        void Update()
        {
            if(!ready)return;
            translation.Diagnostics.FlushSummary();
            if(!Typing()&&!Input.GetKey(KeyCode.LeftControl)&&!Input.GetKey(KeyCode.RightControl)&&!Input.GetKey(KeyCode.LeftAlt)&&!Input.GetKey(KeyCode.RightAlt)&&!Input.GetKey(KeyCode.LeftShift)&&!Input.GetKey(KeyCode.RightShift)&&Input.GetKeyDown(translation.ToggleKey))
            {translation.Capturing=!translation.Capturing;translation.Diagnostics.Global(translation.Capturing?"capture-resumed":"capture-paused","queued="+translation.Queued+" active="+translation.Active);}
            if(Time.unscaledTime>=nextScan)
            {
                nextScan=Time.unscaledTime+0.5f;
                var dead=new List<int>();foreach(var pair in bindings)if(pair.Value.Owner==null||pair.Value.Label==null)dead.Add(pair.Key);
                foreach(int id in dead)Forget(id);dead.Clear();foreach(var pair in rejected)if(pair.Value.Label==null)dead.Add(pair.Key);foreach(int id in dead)rejected.Remove(id);
                textLayout.Prune();tmpLayout.Prune();
                if(translation.Capturing)
                {
                    foreach(var label in Resources.FindObjectsOfTypeAll<Text>())ObserveSafely(label,false);
                    foreach(var label in Resources.FindObjectsOfTypeAll<TMP_Text>())ObserveSafely(label,false);
                }
            }
            foreach(var b in new List<Binding>(bindings.Values))
            {
                if(!Current(b)){if(b.WasVisible)Event(b,"consumer-inactive-or-stale");b.WasVisible=false;continue;}
                bool visible=Visible(b.Label);
                if(!visible){b.WasVisible=false;continue;}
                if(!b.WasVisible){Renew(b,"visible-again");b.WasVisible=true;}
                if(translation.Capturing){b.AllowApply=true;if(b.LocalValue==null&&b.Applied==null&&Time.unscaledTime>=b.NextEnqueue){b.NextEnqueue=Time.unscaledTime+(b.Kind=="generic"?0.25f:0.1f);translation.Enqueue(b.Source,b.Priority,b.Trace,Narrative(b),b.Profile.Role.ToString());}}
            }
            translation.Pump(this);
            foreach(var b in new List<Binding>(bindings.Values))
            {
                if(b.Kind=="bubble"){try{PumpBubble(b);}catch{EndBubble(b,"pump-exception");}continue;}
                if(!Current(b)||!Visible(b.Label)||!b.AllowApply||Time.unscaledTime<b.NextApply)continue;
                if(textLayout.TakeRollback(b.Label))
                {
                    // A changed mesh/layout is not a changed source. Keep Chinese
                    // and re-enter the bounded display path; do not restore English.
                    string current;if(TryValue(b,out current)){writeDepth++;try{PreparationStopped(b,CloudLayoutResult.Capacity,current);Event(b,"layout-change-chinese-retained");}finally{writeDepth--;}}
                }
                if(b.Applied!=null||b.Blocked)
                {
                    if(Time.unscaledTime<b.NextLayout)continue;b.NextLayout=Time.unscaledTime+0.25f;
                    if(!ContextChanged(b)&&!textLayout.NeedsReflow(b.Label)&&!tmpLayout.NeedsReflow(b.Label))continue;
                    if(b.Kind=="reveal"&&!(bool)AccessTools.Property(revealType,"HasCompletedShowing").GetValue(b.Owner,null))continue;
                }
                if(ContextChanged(b)){RecaptureContext(b);continue;}
                string value;if(!TryValue(b,out value))continue;
                if(b.LocalValue==null){translation.CacheObserved(b.Source,b.Trace);if(b.ReadyAt==0)b.ReadyAt=Time.realtimeSinceStartup;}
                Apply(b,value);
            }
        }
        void LateUpdate()
        {
            if(!ready||!CloudReaderUpdates.Safe)return;writeDepth++;
            try
            {
                fonts.RefreshPendingMeshes();
                var pending=new List<int>(localSettles);localSettles.Clear();var rebuilt=new HashSet<int>();
                foreach(int id in pending)
                {
                    Binding b;string value;if(!bindings.TryGetValue(id,out b)||!Current(b)||!b.AllowApply||!Visible(b.Label)||!TryValue(b,out value))continue;
                    var root=SettleRoot(b);if(root!=null&&rebuilt.Add(root.GetInstanceID()))
                        try{LayoutRebuilder.ForceRebuildLayoutImmediate(root);}catch{Event(b,"local-layout-exception");}
                    textLayout.LayoutSettled(b.Label);Apply(b,value);
                }
                CloudReaderUpdates.Flush();
            }
            finally{writeDepth--;}
        }
        static RectTransform SettleRoot(Binding b)
        {
            if(b.Profile.Role==CloudTextRole.DialogueName)return b.Profile.Boundary;
            if(b.Profile.Role==CloudTextRole.ModalBody||b.Profile.Role==CloudTextRole.ModalButton||b.Profile.Role==CloudTextRole.ModalTitle)
                return b.Profile.RoleOwner==null?null:b.Profile.RoleOwner.transform.Find("DialogBackground") as RectTransform;
            if(CloudDisplayRoles.Flow(b.Profile))return b.Profile.RoleOwner==null?b.Label.transform.parent as RectTransform:b.Profile.RoleOwner.transform.parent as RectTransform;
            return b.Label.transform.parent as RectTransform;
        }
        void ScheduleSettle(Binding b)
        {
            // Animated dialogue/bubble already received Chinese at their parser
            // entry. Never restart a running reveal from an extra layout pass.
            if(b.Kind!="generic")return;
            // At most three local end-of-update passes for this occurrence. Later
            // real geometry/style changes are handled by the existing reflow checks.
            if(b.SettleAttempts<3){b.SettleAttempts++;localSettles.Add(b.Owner.GetInstanceID());}
        }
        static bool Typing()
        {var es=UnityEngine.EventSystems.EventSystem.current;var selected=es==null?null:es.currentSelectedGameObject;return selected!=null&&(selected.GetComponent<InputField>()!=null||selected.GetComponent<TMP_InputField>()!=null);}
        bool Mapped(Binding b)
        {Binding current;return b.Owner!=null&&bindings.TryGetValue(b.Owner.GetInstanceID(),out current)&&ReferenceEquals(b,current)&&current.Generation==b.Generation;}
        bool Current(Binding b)
        {
            if(b.Kind=="bubble")return BubbleCurrent(b);
            if(!Mapped(b)||!Live(b.Owner)||!Live(b.Label)||!b.Owner.gameObject.scene.Equals(b.Scene)||!b.Label.gameObject.scene.Equals(b.Scene))return false;
            // Bubble lifecycle is checked separately above; TMP/generic checks stay equivalent.
            string text=Read(b.Label);return text==b.Expected||b.Applied!=null&&text==b.Applied;
        }
        bool ContextChanged(Binding b){return !b.Profile.Same(CloudTextPolicy.Resolve(b.Label,b.Kind));}
        void RecaptureContext(Binding b)
        {if(!Current(b))return;string source=b.Source;var next=Capture(b.Owner,b.Label,source,b.Kind);if(next!=null){next.AllowApply=b.AllowApply;Event(next,"context-changed");string value;if(next.AllowApply&&TryValue(next,out value))Apply(next,value);}}
        void Forget(int id)
        {
            Binding b;if(!bindings.TryGetValue(id,out b))return;
            EndBubble(b,"forgotten-or-destroyed");
            if(b.Kind!="generic"&&b.Label!=null)ownLabels.Remove(b.Label.GetInstanceID());
            writeDepth++;try{textLayout.Forget(b.Label);tmpLayout.Forget(b.Label);}finally{writeDepth--;}
            bindings.Remove(id);
            localSettles.Remove(id);
        }
        static bool Visible(Component label)
        {
            if(!Live(label))return false;
            var behaviour=label as Behaviour;if(behaviour!=null&&!behaviour.isActiveAndEnabled)return false;
            var graphic=label as Graphic;if(graphic!=null&&(graphic.color.a<=0.001f||graphic.canvasRenderer.cull))return false;
            bool ignoreGroups=false;float alpha=1;
            for(Transform parent=label.transform;parent!=null;parent=parent.parent)
            {
                var canvas=parent.GetComponent<Canvas>();if(canvas!=null&&!canvas.isActiveAndEnabled)return false;
                if(ignoreGroups)continue;
                foreach(var group in parent.GetComponents<CanvasGroup>()){alpha*=group.alpha;if(group.ignoreParentGroups)ignoreGroups=true;}
                if(alpha<=0.001f)return false;
            }
            return true;
        }
        internal bool HasCurrentConsumer(string source)
        {foreach(var b in bindings.Values)if(b.Source==source&&Current(b)&&Visible(b.Label))return true;return false;}
        Binding Capture(Component owner,Component label,string source,string kind)
        {
            if(owner==null||label==null)return null;int id=owner.GetInstanceID();Binding previous;bindings.TryGetValue(id,out previous);
            var profile=CloudTextPolicy.Resolve(label,kind);string reason;string local,dateReason;
            bool localDate=EmbeddedDateDisplay.TryFormat(profile,source,translation.ChineseDisplay,out local,out dateReason);
            if(!localDate&&!CloudDisplayRoles.Local(profile,source,translation.ChineseDisplay,out local))CloudUiRoles.Local(profile,source,translation.ChineseDisplay,out local);
            if(local==null&&!translation.Eligible(source,profile.DisplayLabel,out reason))
            {
                Rejected last;if(!rejected.TryGetValue(label.GetInstanceID(),out last)||last.Label!=label||last.Source!=source)
                {var trace=translation.Diagnostics.Begin(source,profile.Role.ToString());translation.Diagnostics.Event(trace,"filtered-"+reason);if(rejected.Count>=2048)rejected.Clear();rejected[label.GetInstanceID()]=new Rejected{Label=label,Source=source};}
                if(previous!=null)Forget(id);return null;
            }
            rejected.Remove(label.GetInstanceID());
            if(previous!=null){EndBubble(previous,"binding-replaced");Event(previous,"binding-superseded");if(previous.Kind!="generic"&&previous.Label!=null)ownLabels.Remove(previous.Label.GetInstanceID());if(previous.Label!=label)Forget(id);}
            bindings.Remove(id); // A failed new capture must not leave an old consumer current.
            var b=new Binding{Owner=owner,Label=label,Scene=owner.gameObject.scene,Generation=++generation,Source=source,Expected=Read(label),Kind=kind,Profile=profile,LocalValue=local,AllowApply=translation.Capturing,WasVisible=Visible(label)};
            b.Priority=kind=="reveal"?12:kind!="generic"?6:profile.TooltipOwner!=null?4:modalType!=null&&label.GetComponentInParent(modalType)!=null?3:1;
            b.Trace=translation.Diagnostics.Begin(source,profile.Role.ToString());
            writeDepth++;try{textLayout.Begin(label,source,profile,b.Trace);tmpLayout.Begin(label,source,profile,b.Trace);}finally{writeDepth--;}
            bindings[id]=b;if(kind!="generic")ownLabels.Add(label.GetInstanceID());
            Event(b,kind=="generic"?"captured":"complete-text-ready","generation="+b.Generation);
            if(dateReason!=null)Event(b,localDate?"date-localized":"date-native-fallback","field="+profile.Context+" reason="+dateReason);
            if(CloudFixedUi.Scoped(profile)||CloudManualLayout.Scoped(profile)||CloudDocumentStructure.Scoped(profile))
                Event(b,"scoped-ui-identity","component=UGUI.Text context="+profile.Context+" units=label-local");
            if(local!=null)Event(b,"context-display-ready");else if(kind!="bubble"&&translation.Capturing&&Visible(label))translation.Enqueue(source,b.Priority,b.Trace,Narrative(b),b.Profile.Role.ToString());else Event(b,translation.Capturing?"waiting-visible":"capture-paused");
            return b;
        }
        void ObserveSafely(Component label,bool activated)
        {try{Observe(label,activated);}catch{translation.Diagnostics.Global("capture-exception");}}
        void Observe(Component label,bool activated)
        {
            if(!ready||writeDepth>0||parseDepth>0||label==null||tmpLayout.IsProbe(label)||!label.gameObject.scene.IsValid()||ownLabels.Contains(label.GetInstanceID()))return;
            if(label.GetComponentInParent<TMP_InputField>()!=null)return;
            if(label.GetComponentInParent<InputField>()!=null&&!CloudFixedUi.SearchPlaceholder(label))return;
            if(label.GetComponentInParent(revealType)!=null||label.GetComponentInParent(bubbleType)!=null)return;
            writeDepth++;try{textLayout.SourceChanged(label);tmpLayout.SourceChanged(label);}finally{writeDepth--;}
            if(!Live(label))return;
            string text=Read(label);Binding b;
            if(bindings.TryGetValue(label.GetInstanceID(),out b))
            {
                bool sourceReturned=text==b.Source&&b.Applied!=null;
                if(sourceReturned)
                {b.Applied=null;b.Expected=text;b.Blocked=false;b.NextApply=0;b.AllowApply=translation.Capturing;b.WasVisible=Visible(label);Renew(b,"native-source-returned");if(!b.AllowApply)Event(b,"capture-paused");}
                if(Current(b))
                {
                    if(ContextChanged(b)){RecaptureContext(b);return;}
                    if((activated||!b.WasVisible)&&!sourceReturned){Renew(b,"activated");b.WasVisible=Visible(label);}
                    if(translation.Capturing)b.AllowApply=true;
                    string value;if(b.AllowApply&&b.Applied==null&&!b.Blocked&&Time.unscaledTime>=b.NextApply&&TryValue(b,out value))Apply(b,value);
                    return;
                }
                Event(b,"binding-stale");Forget(label.GetInstanceID());
            }
            if(!translation.Capturing||!Visible(label))return;
            if(activated)rejected.Remove(label.GetInstanceID());
            b=Capture(label,label,text,"generic");string cached;if(b!=null&&TryValue(b,out cached))Apply(b,cached);
        }
        void Set(Component label,string value){var tmp=label as TMP_Text;if(tmp!=null)tmp.SetText(value);else ((Text)label).text=value;}
        CloudLayoutResult PrepareLayout(Binding b,string value)
        {
            tmpLayout.NativeBeforePrepare(b.Label);textLayout.NativeBeforePrepare(b.Label);
            var tmp=b.Label as TMP_Text;
            if(!(tmp!=null?fonts.Prepare(tmp,b.Profile.Role==CloudTextRole.DialogueOption?value+"国H012":value,CloudDisplayRoles.Flow(b.Profile)):fonts.Prepare((Text)b.Label,value,b.Profile.Coordinated)))
            {Event(b,"font-unavailable");return CloudLayoutResult.Capacity;}
            Event(b,"font-ready");string display=value;
            if(b.Kind=="reveal")display=(string)AccessTools.Method(revealType,"GetTextWithoutCustomTags").Invoke(b.Owner,new object[]{value});
            var result=tmpLayout.Prepare(b.Label,display);
            return result==CloudLayoutResult.Ready?textLayout.Prepare(b.Label,display):result;
        }
        void RestoreDisplay(Binding b)
        {
            textLayout.Abort(b.Label);tmpLayout.Abort(b.Label);
            if(b.Kind=="reveal")AccessTools.Method(revealType,"ShowRevealedString").Invoke(b.Owner,new object[]{b.Source});
            else {if(b.Kind=="bubble")AccessTools.Field(bubbleType,"message").SetValue(b.Owner,b.Source);Set(b.Label,b.Source);}
            b.Applied=null;b.Expected=b.Kind=="bubble"?b.Source:Read(b.Label);b.WaitingLayout=false;
        }
        void PreparationStopped(Binding b,CloudLayoutResult result,string value)
        {
            b.WaitingLayout=result==CloudLayoutResult.Deferred;
            if(b.WaitingLayout)b.LayoutWaits++;
            // Failure of an optional reading/layout aid must not cancel a valid
            // translation. The original component still receives the full value.
            try{textLayout.DisplayFallback(b.Label,value);}catch{Event(b,"ui-display-aid-exception");}
            try{tmpLayout.DisplayFallback(b.Label,value);}catch{Event(b,"tmp-display-aid-exception");}
            b.Blocked=false;ScheduleSettle(b);
            Event(b,"chinese-display-layout-pending","layout="+result+" localPass="+b.SettleAttempts);
        }
        void Written(Binding b,string stage)
        {
            b.Applied=Read(b.Label);
            b.Expected=b.Kind=="bubble"?(string)Field(b.Owner,"message"):b.Applied;b.Blocked=false;
            try{textLayout.Displayed(b.Label);}catch{Event(b,"ui-written-layout-exception");}
            try{tmpLayout.Displayed(b.Label);}catch{Event(b,"tmp-written-layout-exception");}
            Event(b,stage);translation.Diagnostics.Event(b.Trace,"occurrence-to-write",b.Trace.Job,(Time.realtimeSinceStartup-b.Trace.Started)*1000);
            translation.ReadyToWrite(b.Source,b.Trace);
            if(b.ReadyAt>0)translation.Diagnostics.Event(b.Trace,"cache-observed-to-write",b.Trace.Job,(Time.realtimeSinceStartup-b.ReadyAt)*1000);
        }
        void Apply(Binding b,string value)
        {
            if(!Current(b)||ContextChanged(b)){Event(b,"not-written-stale");return;}
            b.NextApply=Time.unscaledTime+0.25f;b.WaitingLayout=false;
            if(b.LocalValue==null){translation.CacheObserved(b.Source,b.Trace);if(b.ReadyAt==0)b.ReadyAt=Time.realtimeSinceStartup;}writeDepth++;
            try
            {
                CloudLayoutResult result;
                try{result=PrepareLayout(b,value);}
                catch{Event(b,"layout-preparation-exception");result=CloudLayoutResult.Capacity;}
                if(!Current(b)){Event(b,"not-written-stale-after-layout");return;}
                if(result!=CloudLayoutResult.Ready)PreparationStopped(b,result,value);
                if(b.Kind=="reveal")
                {AccessTools.Method(revealType,"ClearAndReset").Invoke(b.Owner,null);AccessTools.Method(revealType,"ShowRevealedString").Invoke(b.Owner,new object[]{value});}
                else {if(b.Kind=="bubble")AccessTools.Field(bubbleType,"message").SetValue(b.Owner,value);Set(b.Label,value);}
                Written(b,"display-written");
            }
            catch
            {Event(b,"write-exception");try{if(Current(b)){RestoreDisplay(b);b.Blocked=true;textLayout.BlockCurrent(b.Label);Event(b,"write-failed-original-retained");}}catch{Event(b,"restore-exception");}}
            finally{writeDepth--;}
        }
        // Entry substitution happens before the game's parser/character animation.
        // It is synchronous only for a validated cache hit; no network wait here.
        bool EntryCached(Binding b,out string translated)
        {
            translated=null;if(b==null||!translation.Capturing||!TryValue(b,out translated))return false;
            translation.CacheObserved(b.Source,b.Trace);if(b.ReadyAt==0)b.ReadyAt=Time.realtimeSinceStartup;writeDepth++;
            try
            {
                CloudLayoutResult result;
                try{result=PrepareLayout(b,translated);}
                catch{Event(b,"entry-layout-exception");result=CloudLayoutResult.Capacity;}
                // The incoming parser argument is NEW content; the bubble manager
                // has not assigned its new message yet. Check this captured label
                // and generation, not the still-old manager message.
                if(!Mapped(b)||!Live(b.Label)||Read(b.Label)!=b.Expected){Event(b,"entry-not-written-stale");return false;}
                if(result!=CloudLayoutResult.Ready)PreparationStopped(b,result,translated);
                b.Applied=translated;return true;
            }
            catch{Event(b,"entry-layout-exception");return false;}
            finally{writeDepth--;}
        }
        static void RevealPrefix(object __instance,ref string __0)
        {
            var p=Instance;if(p==null||!p.ready||p.writeDepth>0)return;
            try{var owner=(Component)__instance;var label=(TMP_Text)Field(owner,"_text");var b=p.Capture(owner,label,__0,"reveal");string value;if(p.EntryCached(b,out value))__0=value;}
            catch{p.translation.Diagnostics.Global("complete-dialogue-capture-exception");}
        }
        static void RevealPostfix(object __instance){CompleteCapture(__instance);}
        static void CompleteCapture(object instance)
        {
            var p=Instance;if(p==null||!p.ready||p.writeDepth>0)return;Binding b;
            if(!p.bindings.TryGetValue(((Component)instance).GetInstanceID(),out b))return;
            b.Expected=b.Kind=="bubble"?(string)Field(instance,"message"):Read(b.Label);
            if(b.Applied!=null){p.writeDepth++;try{p.Written(b,"cached-entry-written");}finally{p.writeDepth--;}}
        }
        static void OwnerDisabled(object __instance)
        {var p=Instance;Binding b;if(p!=null&&p.bindings.TryGetValue(((Component)__instance).GetInstanceID(),out b)){if(b.Kind=="bubble")p.EndBubble(b,"hidden");b.WasVisible=false;p.Event(b,"owner-disabled");}}
        static void ParsePrefix(){if(Instance!=null)Instance.parseDepth++;}
        static Exception ParseFinalizer(Exception __exception){if(Instance!=null)Instance.parseDepth=Math.Max(0,Instance.parseDepth-1);return __exception;}
        // The manager has now set fitter width, pivot and position. Rebuild only its
        // own small layout subtree, with translated preferred demand, before rendering.
        static void TooltipShown(object __instance){FinishTooltip(__instance);}
        static void TooltipUpdated(object __instance){FinishTooltip(__instance);}
        static void FinishTooltip(object instance)
        {
            var p=Instance;if(p==null||!p.ready||p.writeDepth>0||!p.translation.Capturing)return;
            try
            {
                var owner=(Component)instance;var label=Field(owner,"text") as Text;
                if(!Live(owner)||label==null)return;
                p.ObserveSafely(label,false);Binding b;string value;
                if(!p.bindings.TryGetValue(label.GetInstanceID(),out b)||!p.Current(b)||!p.TryValue(b,out value)||b.Blocked)return;
                p.writeDepth++;
                try
                {
                    var root=owner.transform as RectTransform;if(root==null)return;
                    for(int i=0;i<2;i++)
                    {
                        LayoutRebuilder.ForceRebuildLayoutImmediate(root);
                        p.textLayout.LayoutSettled(label);p.Apply(b,value);
                        // One further local settle includes demand recalculated for
                        // the manager's final width; never a frame/scene polling loop.
                        if(b.Blocked||!p.Current(b))break;
                    }
                }
                finally{p.writeDepth--;}
            }
            catch{p.translation.Diagnostics.Global("tooltip-display-phase-unavailable");}
        }
        static void TextChanged(object __instance){var p=Instance;if(p!=null&&p.ready)p.ObserveSafely(__instance as Component,false);}
        static void InventoryInitialized(object __instance)
        {
            var p=Instance;if(p==null||!p.ready||p.writeDepth>0)return;
            foreach(string name in new[]{"nameText","quantityText","valueText"})
            {var text=CloudUiRoles.Get(__instance as Component,name) as Component;if(text==null)continue;p.ObserveSafely(text,true);Binding b;if(p.bindings.TryGetValue(text.GetInstanceID(),out b)&&p.Current(b))p.ScheduleSettle(b);}
        }
        static void HistoryInitialized(object __instance)
        {
            var p=Instance;if(p==null||!p.ready||p.writeDepth>0)return;
            foreach(string name in new[]{"_speakerText","_dialogText"})
            {var text=CloudUiRoles.Get(__instance as Component,name) as Component;if(text==null)continue;p.ObserveSafely(text,true);Binding b;if(p.bindings.TryGetValue(text.GetInstanceID(),out b)&&p.Current(b))p.ScheduleSettle(b);}
        }
        static void TextActivated(object __instance){var p=Instance;if(p!=null&&p.ready)p.ObserveSafely(__instance as Component,true);}
        static bool TutorialSpacing(Component __instance)
        {var p=Instance;return p==null||!p.ready||!p.textLayout.OwnsSpacing(__instance.GetComponent<Text>());}
        static bool TextMesh(Text __instance,VertexHelper toFill)
        {var p=Instance;if(p==null||!p.ready)return true;CloudReaderUpdates.EnterRead();try{return !p.textLayout.Populate(__instance,toFill);}catch{p.textLayout.MeshFailed(__instance);p.translation.Diagnostics.Global("layout-mesh-exception");return true;}finally{CloudReaderUpdates.ExitRead();}}
        static void PreferredWidth(Text __instance,ref float __result){var p=Instance;if(p!=null&&p.ready){CloudReaderUpdates.EnterRead();try{p.textLayout.Preferred(__instance,0,ref __result);}finally{CloudReaderUpdates.ExitRead();}}}
        static void PreferredHeight(Text __instance,ref float __result){var p=Instance;if(p!=null&&p.ready){CloudReaderUpdates.EnterRead();try{p.textLayout.Preferred(__instance,1,ref __result);}finally{CloudReaderUpdates.ExitRead();}}}
        static void TmpPreferredWidth(TMP_Text __instance,ref float __result){var p=Instance;if(p!=null&&p.ready){CloudReaderUpdates.EnterRead();try{p.tmpLayout.Preferred(__instance,0,ref __result);}finally{CloudReaderUpdates.ExitRead();}}}
        static void TmpPreferredHeight(TMP_Text __instance,ref float __result){var p=Instance;if(p!=null&&p.ready){CloudReaderUpdates.EnterRead();try{p.tmpLayout.Preferred(__instance,1,ref __result);}finally{CloudReaderUpdates.ExitRead();}}}
        sealed class OptionState {internal Binding Binding;internal int Generation;internal string Original,Translated;}
        static void OptionPrefix(object __instance,out OptionState __state)
        {
            __state=null;var p=Instance;if(p==null||!p.ready)return;
            try{var label=(TMP_Text)Field(__instance,"_optionText");Binding b;if(p.bindings.TryGetValue(label.GetInstanceID(),out b)&&b.Applied!=null&&p.Current(b))
            {__state=new OptionState{Binding=b,Generation=b.Generation,Original=b.Source,Translated=label.text};p.writeDepth++;try{label.text=b.Source;}finally{p.writeDepth--;}}}catch{}
        }
        static Exception OptionFinalizer(OptionState __state,Exception __exception)
        {
            var p=Instance;if(p==null||__state==null)return __exception;var b=__state.Binding;
            try{if(p.Mapped(b)&&b.Generation==__state.Generation&&Live(b.Label)&&b.Label.gameObject.scene.Equals(b.Scene)&&Read(b.Label)==__state.Original)
            {p.writeDepth++;try{p.Set(b.Label,__state.Translated);}finally{p.writeDepth--;}}}catch{}
            return __exception;
        }
    }
}
