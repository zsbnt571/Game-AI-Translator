using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Fusion.CloudMeadow
{
    public sealed partial class CloudMeadowPlugin
    {
        // Show's invocation state, never a lookup of whichever call finished last.
        sealed class BubbleCall
        {
            internal Binding Binding;
            internal Transform Target;
            internal UnityEngine.SceneManagement.Scene TargetScene;
            internal string Input,Delivered,Native;
            internal bool Returned,Ended,AnimationComplete,CompletionRecorded,LayoutPending;
            internal int LayoutAttempts;
        }
        MethodInfo bubbleWrap,bubbleTyping,bubbleDelay;
        void InitializeBubbleLifecycle()
        {
            bubbleWrap=AccessTools.Method(AccessTools.TypeByName("TeamNimbus.Common.Utility.StringExtensions"),"WordWrap",new[]{typeof(string),typeof(int)});
            bubbleTyping=AccessTools.Method(bubbleType,"TypeMessage");bubbleDelay=AccessTools.Method(bubbleType,"DelayHideDialogue");
            if(bubbleWrap==null||bubbleTyping==null||bubbleDelay==null)throw new InvalidOperationException("Bubble lifecycle unavailable.");
            Patch(AccessTools.Method(bubbleType,"Show"),"BubblePrefix","BubblePostfix","BubbleFinalizer");
            Patch(bubbleTyping,"BubbleTickPrefix","BubbleTickPostfix");
        }
        string WrappedBubble(string value){return (string)bubbleWrap.Invoke(null,new object[]{value,48});}
        string BubbleIdentity(BubbleCall call,bool showing)
        {
            var b=call.Binding;
            if(b.Owner==null||b.Label==null||call.Target==null)return "object-missing";
            if(!Mapped(b)||!ReferenceEquals(b.Bubble,call))return "binding-replaced";
            if(call.Ended)return "display-ended";
            if(!b.Owner.gameObject.scene.IsValid()||!b.Label.gameObject.scene.IsValid()||!call.Target.gameObject.scene.IsValid())return "scene-invalid";
            if(!call.Target.gameObject.scene.Equals(call.TargetScene))return "target-scene-changed";
            if(!b.Owner.gameObject.scene.Equals(b.Label.gameObject.scene))return "label-scene-mismatch";
            if(Field(b.Owner,"messageText") as Text!=b.Label||!b.Label.transform.IsChildOf(b.Owner.transform))return "label-replaced";
            if(b.Source!=call.Input)return "source-changed";
            if(showing)
            {
                if(!call.Returned)return "preparing-show";
                if(!b.Owner.gameObject.scene.Equals(b.Scene)||!b.Label.gameObject.scene.Equals(b.Scene))return "scene-changed";
                if(b.Owner.transform.parent!=call.Target)return "target-replaced";
                if(!Live(b.Owner)||!Live(b.Label))return "hidden";
                if((string)Field(b.Owner,"message")!=call.Native)return "content-changed";
            }
            return null;
        }
        void EndBubble(Binding b,string reason)
        {
            if(b.Bubble==null||b.Bubble.Ended)return;
            b.Bubble.Ended=true;b.AllowApply=false;
            Event(b,"bubble-ended","reason="+reason+" generation="+b.Generation);
        }
        bool BubbleCurrent(Binding b)
        {
            if(b.Bubble==null)return false;
            string reason=BubbleIdentity(b.Bubble,true);
            if(reason==null)return true;
            if(reason!="preparing-show")EndBubble(b,reason);
            return false;
        }
        static void BubblePrefix(object __instance,Transform __0,ref string __1,out BubbleCall __state)
        {
            __state=null;var p=Instance;if(p==null||!p.ready||p.writeDepth>0)return;
            try
            {
                var label=(Text)Field(__instance,"messageText");var b=p.Capture((Component)__instance,label,__1,"bubble");
                if(b==null)return;
                var call=new BubbleCall{Binding=b,Target=__0,TargetScene=__0==null?default(UnityEngine.SceneManagement.Scene):__0.gameObject.scene,Input=__1};
                b.Bubble=call;__state=call;
                p.Event(b,"bubble-show-enter","object="+b.Owner.GetInstanceID()+" generation="+b.Generation+" active="+label.gameObject.activeInHierarchy);
                string reason=p.BubbleIdentity(call,false);
                if(reason!=null){p.EndBubble(b,reason);return;}
                string value;
                if(!p.translation.Capturing){p.Event(b,"bubble-entry-skipped","reason=capture-paused");call.Native=p.WrappedBubble(__1);return;}
                if(!p.TryValue(b,out value)){p.Event(b,"bubble-cache-miss");call.Native=p.WrappedBubble(__1);return;}
                p.translation.CacheObserved(b.Source,b.Trace);b.ReadyAt=Time.realtimeSinceStartup;
                p.Event(b,"bubble-cache-hit");
                // No read of old label.text, visibility requirement, mesh mutation
                // or network wait. The game's Show consumes this complete value.
                call.Native=p.WrappedBubble(value);
                if((reason=p.BubbleIdentity(call,false))!=null){p.EndBubble(b,reason);return;}
                call.Delivered=value;__1=value;
                p.Event(b,"bubble-entry-adopted","hidden-entry="+!label.gameObject.activeInHierarchy);
            }
            catch{if(__state!=null)p.EndBubble(__state.Binding,"entry-exception");p.translation.Diagnostics.Global("complete-bubble-capture-exception");}
        }
        static void BubblePostfix(object __instance,BubbleCall __state)
        {
            var p=Instance;if(p==null||__state==null)return;var call=__state;var b=call.Binding;
            try
            {
                string reason=p.BubbleIdentity(call,false);
                if(reason!=null){p.EndBubble(b,reason);return;}
                if(!ReferenceEquals(b.Owner,__instance)||b.Owner.transform.parent!=call.Target){p.EndBubble(b,"show-target-mismatch");return;}
                if((string)Field(b.Owner,"message")!=call.Native){p.EndBubble(b,"show-content-mismatch");return;}
                // Show legitimately reparents the pool object. Bind the resulting
                // scene now, after verifying the exact target and native message.
                b.Scene=b.Owner.gameObject.scene;b.Expected=call.Native;call.Returned=true;
                if(!p.BubbleCurrent(b))return;
                p.Event(b,"bubble-show-returned");
                if(call.Delivered!=null)
                {
                    b.Applied=call.Delivered;call.LayoutPending=true;
                    p.Event(b,"bubble-parameter-delivered","generation="+b.Generation);
                    p.PrepareBubbleLayout(b);
                }
                else if(p.translation.Capturing&&Visible(b.Label))p.translation.Enqueue(b.Source,b.Priority,b.Trace,Narrative(b),b.Profile.Role.ToString());
            }
            catch{p.EndBubble(b,"show-confirm-exception");}
        }
        static Exception BubbleFinalizer(Exception __exception,BubbleCall __state)
        {if(__exception!=null&&Instance!=null&&__state!=null)Instance.EndBubble(__state.Binding,"native-show-exception");return __exception;}
        static void BubbleTickPrefix(object __instance,out Binding __state)
        {__state=null;var p=Instance;if(p!=null)p.bindings.TryGetValue(((Component)__instance).GetInstanceID(),out __state);}
        static void BubbleTickPostfix(Binding __state)
        {
            var p=Instance;if(p==null||!p.ready||__state==null||__state.Bubble==null)return;
            try{p.PumpBubble(__state);}catch{p.EndBubble(__state,"animation-observe-exception");}
        }
        bool BubbleAnimationReady(Binding b)
        {
            var action=Field(b.Owner,"updateAction") as Delegate;
            if(action==null||!ReferenceEquals(action.Target,b.Owner)){EndBubble(b,"animation-owner-changed");return false;}
            if(action.Method==bubbleTyping){Event(b,"bubble-wait-animation");return false;}
            if(action.Method!=bubbleDelay){EndBubble(b,"animation-state-changed");return false;}
            // Verified TypeMessage transition: all source characters have been
            // revealed AND the label contains the full native message.
            if(!b.Bubble.AnimationComplete)
            {
                if((int)Field(b.Owner,"alphaStart")!=b.Bubble.Native.Length||Read(b.Label)!=b.Bubble.Native)
                {Event(b,"bubble-wait-complete-text");return false;}
                b.Bubble.AnimationComplete=true;Event(b,"bubble-native-animation-complete");
            }
            return true;
        }
        void PrepareBubbleLayout(Binding b)
        {
            var call=b.Bubble;if(!call.LayoutPending||call.Delivered==null||!BubbleCurrent(b))return;
            if(!CloudReaderUpdates.Safe){Event(b,"bubble-layout-wait-safe-phase");return;}
            call.LayoutAttempts++;writeDepth++;
            try
            {
                CloudLayoutResult result;
                try{result=PrepareLayout(b,call.Native);}catch{Event(b,"bubble-layout-exception");result=CloudLayoutResult.Capacity;}
                if(!BubbleCurrent(b))return;
                if(result!=CloudLayoutResult.Ready)PreparationStopped(b,result,call.Native);
                // Optional preparation never cancels delivered Chinese. At most
                // three explicit passes per Show; native mesh reflow remains active.
                call.LayoutPending=result==CloudLayoutResult.Deferred&&call.LayoutAttempts<3;
                Event(b,"bubble-layout-result","result="+result+" pass="+call.LayoutAttempts);
                b.NextLayout=Time.unscaledTime+0.25f;
            }
            finally{writeDepth--;}
        }
        void RecordBubbleComplete(Binding b,string stage)
        {
            if(b.Bubble.CompletionRecorded)return;b.Bubble.CompletionRecorded=true;
            try{textLayout.Displayed(b.Label);}catch{Event(b,"bubble-layout-complete-exception");}
            Event(b,stage);translation.ReadyToWrite(b.Source,b.Trace);
            if(b.ReadyAt>0)translation.Diagnostics.Event(b.Trace,"bubble-cache-observed-to-complete-text",b.Trace.Job,(Time.realtimeSinceStartup-b.ReadyAt)*1000);
        }
        void PumpBubble(Binding b)
        {
            if(!BubbleCurrent(b))return;var call=b.Bubble;
            if(call.Delivered!=null)
            {
                if(Time.unscaledTime>=b.NextLayout)
                {
                    b.NextLayout=Time.unscaledTime+0.25f;
                    if(!call.LayoutPending&&call.LayoutAttempts<3&&textLayout.NeedsReflow(b.Label))call.LayoutPending=true;
                    PrepareBubbleLayout(b);
                }
                if(!call.CompletionRecorded&&BubbleAnimationReady(b))RecordBubbleComplete(b,"bubble-delivered-text-complete");
                return;
            }
            if(!b.AllowApply||!BubbleAnimationReady(b))return;
            string value;if(!TryValue(b,out value)){Event(b,"bubble-wait-translation");return;}
            translation.CacheObserved(b.Source,b.Trace);if(b.ReadyAt==0)b.ReadyAt=Time.realtimeSinceStartup;
            if(!CloudReaderUpdates.Safe){Event(b,"bubble-write-wait-safe-phase");return;}
            string display=WrappedBubble(value);
            if(!BubbleCurrent(b)||!BubbleAnimationReady(b)||Read(b.Label)!=call.Native){EndBubble(b,"complete-text-changed");return;}
            writeDepth++;
            try
            {
                // DelayHideDialogue owns duration/callback/closure. Do not restart
                // Show, reset alphaStart, reopen the bubble or extend its lifetime.
                AccessTools.Field(bubbleType,"message").SetValue(b.Owner,display);Set(b.Label,display);
                call.Native=display;call.Delivered=value;b.Expected=display;b.Applied=value;
                call.LayoutPending=true;PrepareBubbleLayout(b);
                if(BubbleCurrent(b))RecordBubbleComplete(b,"bubble-display-written");
            }
            catch{EndBubble(b,"write-exception");}
            finally{writeDepth--;}
        }
    }
}
