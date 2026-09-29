using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Fusion.CloudMeadow
{
    // The native style is distinct from our last assignments. Conditional undo
    // keeps later game writes; a fitted result is never used as a source style.
    internal sealed class CloudTmpLayout : IDisposable
    {
        readonly Dictionary<int,State> states=new Dictionary<int,State>();
        readonly CloudDiagnostics diagnostics;
        readonly CloudTmpMeasure measure=new CloudTmpMeasure();
        internal bool IsProbe(Component c){return measure.Owns(c);}
        internal CloudTmpLayout(CloudDiagnostics d){diagnostics=d;}
        sealed class Style
        {
            internal TMP_FontAsset Font;internal Material Material;
            internal float Size,Min,Max,Line,Paragraph,Character,Word;
            internal FontStyles FontStyle;
            internal bool Auto,Wrap;internal TextAlignmentOptions Align;internal TextOverflowModes Overflow;internal Vector4 Margin;
            internal static Style Read(TMP_Text t)
            {return new Style{Font=t.font,Material=t.fontSharedMaterial,Size=t.fontSize,Min=t.fontSizeMin,Max=t.fontSizeMax,Line=t.lineSpacing,Paragraph=t.paragraphSpacing,Character=t.characterSpacing,Word=t.wordSpacing,FontStyle=t.fontStyle,Auto=t.enableAutoSizing,Wrap=t.enableWordWrapping,Align=t.alignment,Overflow=t.overflowMode,Margin=t.margin};}
            internal bool Same(Style b)
            {return b!=null&&Font==b.Font&&Material==b.Material&&Size==b.Size&&Min==b.Min&&Max==b.Max&&Line==b.Line&&Paragraph==b.Paragraph&&Character==b.Character&&Word==b.Word&&FontStyle==b.FontStyle&&Auto==b.Auto&&Wrap==b.Wrap&&Align==b.Align&&Overflow==b.Overflow&&Margin==b.Margin;}
            internal void Release(TMP_Text t,Style owned)
            {
                // Capture the material comparison before assigning font (TMP may also
                // reset material). Preserve an independently assigned game material.
                var liveMaterial=t.fontSharedMaterial;bool restoreMaterial=liveMaterial==owned.Material;
                if(t.font==owned.Font)t.font=Font;
                t.fontSharedMaterial=restoreMaterial?Material:liveMaterial;
                if(t.fontSize==owned.Size)t.fontSize=Size;
                if(t.fontSizeMin==owned.Min)t.fontSizeMin=Min;if(t.fontSizeMax==owned.Max)t.fontSizeMax=Max;
                if(t.lineSpacing==owned.Line)t.lineSpacing=Line;if(t.paragraphSpacing==owned.Paragraph)t.paragraphSpacing=Paragraph;
                if(t.characterSpacing==owned.Character)t.characterSpacing=Character;if(t.wordSpacing==owned.Word)t.wordSpacing=Word;
                if(t.fontStyle==owned.FontStyle)t.fontStyle=FontStyle;
                if(t.enableWordWrapping==owned.Wrap)t.enableWordWrapping=Wrap;
                if(t.alignment==owned.Align)t.alignment=Align;if(t.overflowMode==owned.Overflow)t.overflowMode=Overflow;
                if(t.margin==owned.Margin)t.margin=Margin;if(t.enableAutoSizing==owned.Auto)t.enableAutoSizing=Auto;
            }
        }
        sealed class State
        {
            internal TMP_Text Label;internal Style Original,Owned,LastOriginal;
            internal string Source,Translation;internal CloudTextProfile Profile;internal CloudTrace Trace;
            internal Rect LastBounds;internal bool Measured,Capacity;internal float Chosen;internal int Revision,RegionAttempts;
            internal CloudTextPolicy.Geometry Geometry;
            internal float DemandWidth,DemandHeight;internal bool DemandReady,PendingShort;
            internal int DemandSettles;internal Vector4 ShortMargin;
            internal CloudTmpReader Reader;internal int OriginalPage,ReaderGeneration;
        }
        void Release(State s)
        {
            int retiredGeneration=s.ReaderGeneration;
            if(s.Reader!=null)
            {
                retiredGeneration=++s.ReaderGeneration;
                s.OriginalPage=s.Reader.NativePage;
                s.Reader.Close(delegate
                {
                    State live;return s.Label!=null&&s.ReaderGeneration==retiredGeneration&&s.Reader==null&&
                        (!states.TryGetValue(s.Label.GetInstanceID(),out live)||ReferenceEquals(live,s));
                });s.Reader=null;
            }
            else if(s.Label!=null)s.OriginalPage=CloudTmpReader.NativePageOf(s.Label);
            if(s.Label!=null&&s.Owned!=null){s.Original.Release(s.Label,s.Owned);s.Original=Style.Read(s.Label);}
            s.Owned=null;
        }
        internal void Abort(Component c){State s;if(c!=null&&states.TryGetValue(c.GetInstanceID(),out s))Release(s);}
        internal void Forget(Component c){if(c==null)return;State s;if(states.TryGetValue(c.GetInstanceID(),out s)){Release(s);states.Remove(c.GetInstanceID());}}
        internal void Begin(Component c,string source,CloudTextProfile profile,CloudTrace trace)
        {
            var t=c as TMP_Text;if(t==null)return;State old;int originalPage=t.pageToDisplay;
            if(states.TryGetValue(t.GetInstanceID(),out old))
            {
                Release(old);originalPage=old.OriginalPage;
                if(old.Label==t&&old.Source==source&&old.Profile.Same(profile)){old.Trace=trace;old.Original=Style.Read(t);return;}
            }
            states[t.GetInstanceID()]=new State{Label=t,Original=Style.Read(t),Source=source,Profile=profile,Trace=trace,OriginalPage=originalPage};
        }
        // Called BEFORE the caller prepares a fallback. Restores our old assignments,
        // while preserving external size/material/style changes as the new baseline.
        internal void NativeBeforePrepare(Component c)
        {var t=c as TMP_Text;State s;if(t!=null&&states.TryGetValue(t.GetInstanceID(),out s)){Release(s);s.Original=Style.Read(t);}}
        internal void SourceChanged(Component c)
        {var t=c as TMP_Text;State s;if(t!=null&&states.TryGetValue(t.GetInstanceID(),out s)&&s.Owned!=null&&t.text!=s.Translation)Release(s);}
        internal void RenewTrace(Component c,CloudTrace trace){State s;if(c!=null&&states.TryGetValue(c.GetInstanceID(),out s))s.Trace=trace;}
        internal void Reactivated(Component c)
        {State s;if(c!=null&&states.TryGetValue(c.GetInstanceID(),out s)&&s.Profile.Role==CloudTextRole.DialogueOption){s.Measured=false;s.RegionAttempts=0;s.DemandSettles=0;}}
        internal void Displayed(Component c){var t=c as TMP_Text;State s;if(t!=null&&states.TryGetValue(t.GetInstanceID(),out s)&&s.Owned!=null){s.Translation=t.text;if(s.Reader!=null)s.Reader.Written(s.Translation);CloudTextPolicy.RepositionTooltip(s.Profile);}}
        static float Height(TMP_FontAsset font,int c,int depth)
        {
            if(font==null||depth>5||font.fontInfo==null)return 0;TMP_Glyph glyph;
            if(font.characterDictionary!=null&&font.characterDictionary.TryGetValue(c,out glyph))return glyph.height*glyph.scale*font.fontInfo.Scale/Mathf.Max(1,font.fontInfo.PointSize);
            if(font.fallbackFontAssets!=null)foreach(var f in font.fallbackFontAssets){float h=Height(f,c,depth+1);if(h>0)return h;}
            return 0;
        }
        float Nominal(State s,string value)
        {
            float original=Height(s.Original.Font,'H',0),translated=0;
            bool tag=false;foreach(char c in value){if(c=='<'){tag=true;continue;}if(c=='>'){tag=false;continue;}if(!tag&&c>=0x2e80&&c<=0x9fff)translated=Mathf.Max(translated,Height(s.Label.font,c,0));}
            return original>0&&translated>0?s.Original.Size*Mathf.Clamp(original/translated,0.75f,1):s.Original.Size;
        }
        internal CloudLayoutResult Prepare(Component c,string display)
        {
            var t=c as TMP_Text;State s;if(t==null)return CloudLayoutResult.Ready;
            if(!states.TryGetValue(t.GetInstanceID(),out s)||t.font==null)return CloudLayoutResult.Capacity;
            if(s.Profile.Role==CloudTextRole.DialogueOption)return PrepareOption(s,display);
            if(CloudDisplayRoles.Flow(s.Profile))return PrepareFlow(s,display);
            if(CloudTextPolicy.ShortTmp(s.Profile))return PrepareShort(s,display);
            var b=CloudTextPolicy.Bounds(t,s.Profile);var rect=t.rectTransform.rect;
            bool changed=!s.Measured||!s.Original.Same(s.LastOriginal)||s.LastBounds!=b||s.Translation!=display;
            s.Owned=Style.Read(t); // Include a just-assigned fallback even on failure.
            if(b.width<4||b.height<4)
            {
                s.RegionAttempts++;s.LastBounds=b;s.LastOriginal=s.Original;
                if(s.RegionAttempts<6){s.Measured=false;return CloudLayoutResult.Deferred;}
                s.Measured=true;s.Capacity=true;
                diagnostics.Event(s.Trace,"tmp-region-unresolved",0,-1,"attempts=6 measurements=unavailable");
                return CloudLayoutResult.Capacity;
            }
            s.RegionAttempts=0;
            try
            {
            var margin=s.Original.Margin;
            margin.x=Mathf.Max(margin.x,b.xMin-rect.xMin+2);margin.z=Mathf.Max(margin.z,rect.xMax-b.xMax+2);
            margin.y=Mathf.Max(margin.y,rect.yMax-b.yMax+2);margin.w=Mathf.Max(margin.w,b.yMin-rect.yMin+3);
            t.enableAutoSizing=false;t.enableWordWrapping=!s.Profile.SingleLine;t.overflowMode=TextOverflowModes.Overflow;
            t.lineSpacing=Mathf.Max(0,s.Original.Line);t.paragraphSpacing=Mathf.Max(0,s.Original.Paragraph);t.margin=margin;
            if(s.Profile.Role==CloudTextRole.Dialogue)t.alignment=TextAlignmentOptions.TopLeft;
            s.Owned=Style.Read(t);
            if(changed)
            {
                float width=rect.width-margin.x-margin.z,height=rect.height-margin.y-margin.w;
                float ceiling=Nominal(s,display),chosen=ceiling;
                var actual=measure.Read(t,display,chosen,width,height);
                if(actual.Available&&!actual.Fits)
                {
                    // Glyph correction and capacity reduction have separate, limited
                    // ranges. Dialogue cannot collapse to a quarter of its native size.
                    float lo=ceiling*0.8f,hi=ceiling;
                    var small=measure.Read(t,display,lo,width,height);
                    if(small.Available&&small.Fits)
                    {
                        for(int i=0;i<8;i++){float mid=(lo+hi)*0.5f;var fit=measure.Read(t,display,mid,width,height);if(fit.Available&&fit.Fits)lo=mid;else hi=mid;}
                        chosen=lo;actual=measure.Read(t,display,chosen,width,height);
                    }
                    else {chosen=lo;actual=small;}
                }
                s.Capacity=!actual.Available||!actual.Fits;s.Chosen=chosen;s.Measured=true;
                s.LastBounds=b;s.LastOriginal=s.Original;s.Revision++;
                diagnostics.Event(s.Trace,s.Capacity?"tmp-layout-capacity":"tmp-layout-ready",s.Trace.Job,-1,
                    "component=TMP_UGUI revision="+s.Revision+" wrap="+t.enableWordWrapping+" singleLine="+s.Profile.SingleLine+
                    " width="+CloudDiagnostics.Number(width)+" height="+CloudDiagnostics.Number(height)+
                    " margin="+CloudDiagnostics.Number(margin.x)+","+CloudDiagnostics.Number(margin.y)+","+CloudDiagnostics.Number(margin.z)+","+CloudDiagnostics.Number(margin.w)+
                    " nativeSize="+CloudDiagnostics.Number(s.Original.Size)+" baseSize="+CloudDiagnostics.Number(ceiling)+" finalSize="+CloudDiagnostics.Number(chosen)+
                    " lines="+(actual.Available?actual.Lines.ToString():"unavailable")+" maxLineWidth="+(actual.Available?CloudDiagnostics.Number(actual.MaxLineWidth):"unavailable")+
                    " blockHeight="+(actual.Available?CloudDiagnostics.Number(actual.Height):"unavailable")+" reason="+actual.Reason);
            }
            t.fontSize=s.Chosen;s.Owned=Style.Read(t);s.Translation=display;
            return s.Capacity?CloudLayoutResult.Capacity:CloudLayoutResult.Ready;
            }
            finally{s.Owned=Style.Read(t);}
        }
        internal bool NeedsReflow(Component c)
        {
            var t=c as TMP_Text;State s;if(t==null||!states.TryGetValue(t.GetInstanceID(),out s))return false;
            // Compare live styles against the last owned result without mutating the
            // component from a polling check. Undo/merge occurs once, before preparation.
            var expected=s.Owned??s.Original;
            if((CloudTextPolicy.ShortTmp(s.Profile)||s.Profile.Role==CloudTextRole.DialogueOption)&&!s.Geometry.Same(CloudTextPolicy.Geometry.Read(t.rectTransform)))return true;
            return !s.Measured||CloudTextPolicy.Bounds(t,s.Profile)!=s.LastBounds||!Style.Read(t).Same(expected);
        }
        internal bool KeepPending(Component c)
        {State s;return c!=null&&states.TryGetValue(c.GetInstanceID(),out s)&&(s.Profile.Role==CloudTextRole.DialogueName||s.Profile.Role==CloudTextRole.DialogueOption)&&s.PendingShort;}
        internal void Preferred(TMP_Text t,int axis,ref float value)
        {
            State flow;
            if(t!=null&&states.TryGetValue(t.GetInstanceID(),out flow)&&CloudDisplayRoles.Flow(flow.Profile))
            {
                if(flow.Owned!=null&&flow.DemandReady&&(t.text==flow.Source||t.text==flow.Translation))
                    value=axis==0?flow.DemandWidth:flow.DemandHeight;
                return;
            }
            State optionState;
            if(t!=null&&states.TryGetValue(t.GetInstanceID(),out optionState)&&optionState.Profile.Role==CloudTextRole.DialogueOption)
            {
                if(axis==1&&optionState.Profile.Grow&&optionState.Owned!=null&&optionState.DemandReady&&(t.text==optionState.Source||t.text==optionState.Translation))value=optionState.DemandHeight;
                return;
            }
            State s;if(t==null||!states.TryGetValue(t.GetInstanceID(),out s)||s.Profile.Role!=CloudTextRole.DialogueName||s.Owned==null||!s.DemandReady)return;
            if(t.text!=s.Source&&t.text!=s.Translation)return;
            // Feed the existing HorizontalLayoutGroup/ContentSizeFitter only for
            // this field. A fitted output must never become next frame's demand.
            value=axis==0?s.DemandWidth:s.DemandHeight;
        }
        static string RectData(Rect b)
        {return CloudDiagnostics.Number(b.xMin)+","+CloudDiagnostics.Number(b.yMin)+","+CloudDiagnostics.Number(b.xMax)+","+CloudDiagnostics.Number(b.yMax);}
        CloudLayoutResult PrepareFlow(State s,string display)
        {
            var t=s.Label;var b=CloudTextPolicy.Bounds(t,s.Profile);var rect=t.rectTransform.rect;
            s.Owned=Style.Read(t);s.Translation=display;
            var margin=s.Original.Margin;
            float width=b.width-margin.x-margin.z;
            // Only a verified layout parent can supply an unsettled row's width.
            if(width<=0&&t.transform.parent!=null)
            {
                var group=t.transform.parent.GetComponent<HorizontalOrVerticalLayoutGroup>();
                var parent=t.transform.parent as RectTransform;
                if(group!=null&&parent!=null&&group.childControlWidth)
                    width=parent.rect.width-group.padding.horizontal-margin.x-margin.z;
            }
            t.enableAutoSizing=false;t.enableWordWrapping=true;t.margin=margin;
            t.lineSpacing=Mathf.Max(0,s.Original.Line);t.paragraphSpacing=Mathf.Max(0,s.Original.Paragraph);
            t.overflowMode=TextOverflowModes.Overflow;
            t.alignment=CloudDisplayRoles.History(s.Profile)?TextAlignmentOptions.TopLeft:TextAlignmentOptions.MidlineLeft;
            if(width<=0)
            {
                s.Capacity=false;s.Measured=true;s.LastBounds=b;s.LastOriginal=s.Original;
                s.Chosen=Nominal(s,display);t.fontSize=s.Chosen;s.Owned=Style.Read(t);
                LayoutRebuilder.MarkLayoutForRebuild(t.rectTransform);return CloudLayoutResult.Deferred;
            }
            // A large MEASUREMENT height obtains intrinsic demand. It is never used
            // as a drawing rectangle. The existing layout groups allocate that demand.
            var cap=measure.ReadShort(t,"H",s.Original.Size,width,10000,s.Original.Font,s.Original.Material,true);
            var face=measure.ReadShort(t,"国H012",s.Original.Size,width,10000,t.font,t.fontSharedMaterial,false);
            float chosen=cap.Available&&face.Available&&face.Height>0?s.Original.Size*cap.Height/face.Height:Nominal(s,display);
            var actual=measure.ReadShort(t,display,chosen,width,10000,t.font,t.fontSharedMaterial,false,true);
            s.Chosen=chosen;t.fontSize=chosen;s.DemandReady=actual.Available;
            if(actual.Available)
            {
                // PreferredHeight contains the font's line advance, so subsequent
                // rows never collide even when visible glyph ink is shorter.
                s.DemandWidth=width+margin.x+margin.z;
                s.DemandHeight=Mathf.Max(actual.Height,actual.PreferredHeight)+margin.y+margin.w;
            }
            s.Capacity=!actual.Available;s.Measured=true;s.LastBounds=b;s.LastOriginal=s.Original;
            s.Geometry=CloudTextPolicy.Geometry.Read(t.rectTransform);s.Owned=Style.Read(t);
            bool waiting=actual.Available&&s.DemandHeight>rect.height+0.01f;
            if(waiting)LayoutRebuilder.MarkLayoutForRebuild(t.rectTransform);
            diagnostics.Event(s.Trace,"tmp-flow-demand",0,-1,"role="+s.Profile.Role+" width="+CloudDiagnostics.Number(width)+" nativeSize="+CloudDiagnostics.Number(s.Original.Size)+" chosenSize="+CloudDiagnostics.Number(chosen)+" demandHeight="+CloudDiagnostics.Number(s.DemandHeight)+" allocatedHeight="+CloudDiagnostics.Number(rect.height)+" viewport-is-capacity=False");
            return waiting?CloudLayoutResult.Deferred:s.Capacity?CloudLayoutResult.Capacity:CloudLayoutResult.Ready;
        }
        internal void DisplayFallback(Component c,string value)
        {
            State s;var t=c as TMP_Text;if(t==null||!states.TryGetValue(t.GetInstanceID(),out s))return;
            s.Translation=value;
            if(s.Owned==null){t.enableAutoSizing=false;t.fontSize=Nominal(s,value);t.enableWordWrapping=!s.Profile.SingleLine;}
            // Flow/nameplate/option demand uses the native parent layout first.
            // A genuinely overfull FIXED field uses TMP pages within its own rect,
            // instead of restoring English or permitting unlimited overflow.
            if(s.Capacity&&CloudTextPolicy.Usable(CloudTextPolicy.Bounds(t,s.Profile))&&!CloudDisplayRoles.Flow(s.Profile))
            {
                if(s.Reader==null)
                {
                    int generation=s.ReaderGeneration;var scene=t.gameObject.scene;
                    var area=CloudTextPolicy.Bounds(t,s.Profile);
                    float size=s.Chosen>0?s.Chosen:Nominal(s,value);
                    var margin=s.Original.Margin;margin.z+=8;
                    // Do not assign Page mode or create UI in a parser/prepare hook.
                    // The callback records its owned style only when actually applied.
                    s.Reader=new CloudTmpReader(t,s.Source,s.OriginalPage,delegate
                    {
                        State live;return t!=null&&s.ReaderGeneration==generation&&s.Owned!=null&&
                            states.TryGetValue(t.GetInstanceID(),out live)&&ReferenceEquals(live,s)&&
                            t.gameObject.scene.Equals(scene)&&t.text==s.Translation;
                    },delegate
                    {
                        string kind=s.Profile.Role==CloudTextRole.Dialogue?"reveal":s.Profile.Role==CloudTextRole.Bubble?"bubble":"generic";
                        return s.Profile.Same(CloudTextPolicy.Resolve(t,kind))&&CloudTextPolicy.Bounds(t,s.Profile)==area&&Style.Read(t).Same(s.Owned);
                    },delegate
                    {
                        if(t.enableAutoSizing)t.enableAutoSizing=false;
                        if(!t.enableWordWrapping)t.enableWordWrapping=true;
                        if(t.overflowMode!=TextOverflowModes.Page)t.overflowMode=TextOverflowModes.Page;
                        if(t.fontSize!=size)t.fontSize=size;
                        if(t.alignment!=TextAlignmentOptions.TopLeft)t.alignment=TextAlignmentOptions.TopLeft;
                        if(t.margin!=margin)t.margin=margin;
                        s.Owned=Style.Read(t);
                    });
                }
            }
            s.Owned=Style.Read(t);LayoutRebuilder.MarkLayoutForRebuild(t.rectTransform);
        }
        CloudLayoutResult PrepareOption(State s,string display)
        {
            var t=s.Label;var rect=t.rectTransform.rect;var b=CloudTextPolicy.Bounds(t,s.Profile);
            s.Owned=Style.Read(t);s.PendingShort=false;
            try
            {
                if(!CloudTextPolicy.Usable(b))
                {
                    s.LastBounds=b;s.LastOriginal=s.Original;s.Geometry=CloudTextPolicy.Geometry.Read(t.rectTransform);s.RegionAttempts++;s.Measured=s.RegionAttempts>=6;
                    if(!s.Measured)return CloudLayoutResult.Deferred;
                    s.Capacity=true;diagnostics.Event(s.Trace,"tmp-region-unresolved",0,-1,"mode=dialogue-option attempts=6 measurements=unavailable");
                    return CloudLayoutResult.Capacity;
                }
                var margin=s.Original.Margin;
                margin.x=Mathf.Max(margin.x,b.xMin-rect.xMin);margin.z=Mathf.Max(margin.z,rect.xMax-b.xMax);
                margin.y=Mathf.Max(margin.y,rect.yMax-b.yMax);margin.w=Mathf.Max(margin.w,b.yMin-rect.yMin);
                float width=rect.width-margin.x-margin.z,height=rect.height-margin.y-margin.w;
                var cap=measure.ReadShort(t,"H",s.Original.Size,width,height,s.Original.Font,s.Original.Material,true);
                var native=measure.ReadShort(t,s.Source,s.Original.Size,width,height,s.Original.Font,s.Original.Material,true,true);
                t.enableAutoSizing=false;t.enableWordWrapping=true;t.overflowMode=TextOverflowModes.Overflow;
                t.alignment=TextAlignmentOptions.MidlineGeoAligned;t.margin=margin;
                var face=measure.ReadShort(t,"国H012",s.Original.Size,width,height,t.font,t.fontSharedMaterial,false);
                bool reliable=cap.Available&&face.Available&&native.Available&&cap.Height>0&&face.Height>0;
                if(!reliable)
                {
                    s.LastBounds=b;s.LastOriginal=s.Original;s.Geometry=CloudTextPolicy.Geometry.Read(t.rectTransform);
                    s.RegionAttempts++;s.Measured=s.RegionAttempts>=6;s.Capacity=s.Measured;
                    diagnostics.Event(s.Trace,s.Measured?"tmp-layout-capacity":"waiting-option-metrics",0,-1,"mode=dialogue-option reason=cap-reference-unavailable measurements=unavailable attempts="+s.RegionAttempts);
                    return s.Measured?CloudLayoutResult.Capacity:CloudLayoutResult.Deferred;
                }
                s.RegionAttempts=0;
                float ceiling=s.Original.Size*cap.Height/face.Height,chosen=ceiling;
                var actual=measure.ReadShort(t,display,chosen,width,height,t.font,t.fontSharedMaterial,false,true);
                s.DemandReady=actual.Available;
                if(actual.Available)
                {
                    s.DemandHeight=Mathf.Max(native.PreferredHeight,actual.Height+Mathf.Max(0,native.PreferredHeight-native.Height))+s.Original.Margin.y+s.Original.Margin.w;
                    if(s.Profile.Grow&&s.DemandSettles<3&&s.DemandHeight>rect.height+0.5f)
                    {
                        s.DemandSettles++;s.PendingShort=true;s.Measured=false;s.Translation=display;t.fontSize=chosen;s.Owned=Style.Read(t);
                        LayoutRebuilder.MarkLayoutForRebuild(t.rectTransform);
                        diagnostics.Event(s.Trace,"waiting-option-layout",0,-1,"attempt="+s.DemandSettles+" demandHeight="+CloudDiagnostics.Number(s.DemandHeight));
                        return CloudLayoutResult.Deferred;
                    }
                    if(!actual.Fits)
                    {
                        float lo=ceiling*0.85f,hi=ceiling;
                        var small=measure.ReadShort(t,display,lo,width,height,t.font,t.fontSharedMaterial,false,true);
                        if(small.Available&&small.Fits)
                        {for(int i=0;i<8;i++){float mid=(lo+hi)*0.5f;var fit=measure.ReadShort(t,display,mid,width,height,t.font,t.fontSharedMaterial,false,true);if(fit.Available&&fit.Fits)lo=mid;else hi=mid;}}
                        chosen=lo;actual=measure.ReadShort(t,display,chosen,width,height,t.font,t.fontSharedMaterial,false,true);
                    }
                }
                s.Capacity=!actual.Available||!actual.Fits;s.Chosen=chosen;s.Measured=true;s.Revision++;
                s.LastBounds=b;s.LastOriginal=s.Original;s.Geometry=CloudTextPolicy.Geometry.Read(t.rectTransform);
                t.fontSize=chosen;s.Translation=display;
                diagnostics.Event(s.Trace,s.Capacity?"tmp-layout-capacity":"tmp-layout-ready",s.Trace.Job,-1,
                    "mode=dialogue-option units=label-local basis=option-own-field wrap=True width="+CloudDiagnostics.Number(width)+" height="+CloudDiagnostics.Number(height)+
                    " margin="+margin+" baseSize="+CloudDiagnostics.Number(ceiling)+" finalSize="+CloudDiagnostics.Number(chosen)+
                    " lines="+(actual.Available?actual.Lines.ToString():"unavailable")+" ink="+(actual.Available?RectData(actual.Ink):"unavailable")+
                    " lineBox="+(actual.Available?RectData(actual.LineBox):"unavailable")+" reason="+actual.Reason);
                return s.Capacity?CloudLayoutResult.Capacity:CloudLayoutResult.Ready;
            }
            finally{s.Owned=Style.Read(t);}
        }
        CloudLayoutResult PrepareShort(State s,string display)
        {
            var t=s.Label;var rect=t.rectTransform.rect;var b=CloudTextPolicy.Bounds(t,s.Profile);
            var geometry=CloudTextPolicy.Geometry.Read(t.rectTransform);
            bool changed=!s.Measured||!s.Original.Same(s.LastOriginal)||s.LastBounds!=b||s.Translation!=display||!s.Geometry.Same(geometry);
            s.Owned=Style.Read(t);s.PendingShort=false;
            try
            {
                if(!CloudTextPolicy.Usable(b))
                {
                    s.RegionAttempts++;s.LastBounds=b;s.LastOriginal=s.Original;s.Geometry=geometry;
                    if(s.RegionAttempts<6){s.Measured=false;return CloudLayoutResult.Deferred;}
                    s.Capacity=s.Measured=true;
                    diagnostics.Event(s.Trace,"tmp-region-unresolved",0,-1,"attempts=6 mode=short-field measurements=unavailable");
                    return CloudLayoutResult.Capacity;
                }
                s.RegionAttempts=0;
                // Original serialized margins and real clipping only. No invented
                // per-font inset: the label/title area already excludes its icons
                // and the nameplate's layout group already supplies its padding.
                var margin=s.Original.Margin;
                margin.x=Mathf.Max(margin.x,b.xMin-rect.xMin);margin.z=Mathf.Max(margin.z,rect.xMax-b.xMax);
                margin.y=Mathf.Max(margin.y,rect.yMax-b.yMax);margin.w=Mathf.Max(margin.w,b.yMin-rect.yMin);
                float width=rect.width-margin.x-margin.z,height=rect.height-margin.y-margin.w;
                CloudTmpMeasure.Result native=null,actual=null;float ceiling=float.NaN;
                if(changed)
                {
                    // The caller restored Original before preparing the fallback.
                    // Native font/material are explicit; the game label is not used
                    // as a scratch measurement surface.
                    native=measure.ReadShort(t,s.Source,s.Original.Size,width,height,s.Original.Font,s.Original.Material,true,s.Profile.Role==CloudTextRole.QuestCounter);
                }
                t.enableAutoSizing=false;t.enableWordWrapping=s.Profile.Role==CloudTextRole.QuestCounter;t.overflowMode=TextOverflowModes.Overflow;
                t.alignment=TextAlignmentOptions.MidlineGeoAligned;t.margin=margin;
                if(changed)
                {
                    var reference=measure.ReadShort(t,display,s.Original.Size,width,height,t.font,t.fontSharedMaterial,false,s.Profile.Role==CloudTextRole.QuestCounter);
                    bool reliable=native.Available&&reference.Available&&native.Height>0&&reference.Height>0;
                    float chosen=s.Original.Size;
                    if(reliable)
                    {
                        // Compare physical glyph heights, not nominal font sizes.
                        // No .75 floor is applied before another capacity reduction.
                        ceiling=s.Original.Size*native.Height/reference.Height;chosen=ceiling;
                        if(s.Profile.Role==CloudTextRole.QuestCounter)
                        {
                            // Multiline counter captions use a per-glyph baseline;
                            // a different wrapped line count must not shrink the baseline.
                            string sample="H";bool tag=false;
                            foreach(char c in display){if(c=='<'){tag=true;continue;}if(c=='>'){tag=false;continue;}if(!tag&&c>=0x2e80&&c<=0x9fff){sample=c.ToString();break;}}
                            var cap=measure.ReadShort(t,"H",s.Original.Size,width,height,s.Original.Font,s.Original.Material,true);
                            var face=measure.ReadShort(t,sample,s.Original.Size,width,height,t.font,t.fontSharedMaterial,false);
                            reliable=cap.Available&&face.Available&&cap.Height>0&&face.Height>0;
                            if(reliable){ceiling=s.Original.Size*cap.Height/face.Height;chosen=ceiling;}
                        }
                        actual=measure.ReadShort(t,display,chosen,width,height,t.font,t.fontSharedMaterial,false,s.Profile.Role==CloudTextRole.QuestCounter);
                        s.DemandReady=actual.Available;
                        if(s.DemandReady)
                        {
                            s.DemandWidth=Mathf.Max(native.PreferredWidth,actual.PreferredWidth)+s.Original.Margin.x+s.Original.Margin.z;
                            // Keep the native line's leading around the coordinated
                            // ink; the fallback face's taller line box is not extra UI.
                            s.DemandHeight=Mathf.Max(native.PreferredHeight,actual.Height+Mathf.Max(0,native.PreferredHeight-native.Height))+s.Original.Margin.y+s.Original.Margin.w;
                        }
                        // Give the native nameplate fitter a bounded opportunity to
                        // use demand at the unfitted baseline. No rect/anchor writes.
                        if(s.Profile.Role==CloudTextRole.DialogueName&&s.DemandReady&&s.DemandSettles<3&&
                            (s.DemandSettles==0||!actual.Fits)&&(s.DemandWidth>rect.width+0.5f||s.DemandHeight>rect.height+0.5f))
                        {
                            s.DemandSettles++;s.PendingShort=true;s.Measured=false;s.Translation=display;
                            t.fontSize=chosen;s.Owned=Style.Read(t);LayoutRebuilder.MarkLayoutForRebuild(t.rectTransform);
                            diagnostics.Event(s.Trace,"waiting-nameplate-layout",0,-1,"attempt="+s.DemandSettles+" demandWidth="+CloudDiagnostics.Number(s.DemandWidth)+" demandHeight="+CloudDiagnostics.Number(s.DemandHeight));
                            return CloudLayoutResult.Deferred;
                        }
                        if(actual.Available&&!actual.Fits)
                        {
                            float lo=ceiling*0.85f,hi=ceiling;
                            var small=measure.ReadShort(t,display,lo,width,height,t.font,t.fontSharedMaterial,false,s.Profile.Role==CloudTextRole.QuestCounter);
                            if(small.Available&&small.Fits)
                            {for(int i=0;i<8;i++){float mid=(lo+hi)*0.5f;var fit=measure.ReadShort(t,display,mid,width,height,t.font,t.fontSharedMaterial,false,s.Profile.Role==CloudTextRole.QuestCounter);if(fit.Available&&fit.Fits)lo=mid;else hi=mid;}}
                            chosen=lo;actual=measure.ReadShort(t,display,chosen,width,height,t.font,t.fontSharedMaterial,false,s.Profile.Role==CloudTextRole.QuestCounter);
                        }
                        if(!reliable){actual.Fits=false;actual.Reason="counter-cap-reference-unavailable";}
                    }
                    else {s.DemandReady=false;actual=reference;actual.Fits=false;actual.Reason=!native.Available?"native-"+native.Reason:reference.Reason;}
                    s.Capacity=!reliable||!actual.Available||!actual.Fits;s.Chosen=chosen;s.Measured=true;
                    s.LastBounds=b;s.LastOriginal=s.Original;s.Geometry=geometry;s.ShortMargin=margin;s.Revision++;
                    diagnostics.Event(s.Trace,s.Capacity?"tmp-layout-capacity":"tmp-layout-ready",s.Trace.Job,-1,
                        "component=TMP_UGUI mode=resolved-short-ink revision="+s.Revision+" units=label-local basis="+(s.Profile.Role==CloudTextRole.QuestTitle?"quest-title-area":s.Profile.Role==CloudTextRole.QuestCounter?"quest-counter-own-field":"nameplate-field-background")+
                        " rect="+RectData(rect)+" constrained="+RectData(b)+" width="+CloudDiagnostics.Number(width)+" height="+CloudDiagnostics.Number(height)+
                        " margin="+CloudDiagnostics.Number(margin.x)+","+CloudDiagnostics.Number(margin.y)+","+CloudDiagnostics.Number(margin.z)+","+CloudDiagnostics.Number(margin.w)+
                        " nativeSize="+CloudDiagnostics.Number(s.Original.Size)+" nativeInk="+(native.Available?RectData(native.Ink):"unavailable")+
                        " baseSize="+CloudDiagnostics.Number(ceiling)+" finalSize="+CloudDiagnostics.Number(chosen)+
                        " glyphs="+(reliable&&actual.Available?"resolved":"unavailable")+" glyphBounds="+(actual.Available?RectData(actual.Ink):"unavailable")+
                        " lineBox="+(actual.Available?RectData(actual.LineBox):"unavailable")+" reason="+actual.Reason);
                }
                t.fontSize=s.Chosen;t.margin=s.ShortMargin;s.Translation=display;
                return s.Capacity?CloudLayoutResult.Capacity:CloudLayoutResult.Ready;
            }
            finally{s.Owned=Style.Read(t);}
        }
        internal void Prune(){var dead=new List<int>();foreach(var p in states)if(p.Value.Label==null)dead.Add(p.Key);foreach(int id in dead){Release(states[id]);states.Remove(id);}}
        public void Dispose(){foreach(var s in states.Values)Release(s);states.Clear();measure.Dispose();}
    }
}
