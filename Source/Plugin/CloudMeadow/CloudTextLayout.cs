using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Fusion.Embedded.Layout;

namespace Fusion.CloudMeadow
{
    // UI.Text layout stays in mesh space. We never assign a fitted fontSize,
    // transform, pivot or control rectangle, so those cannot become the next baseline.
    internal sealed class CloudTextLayout : IDisposable
    {
        readonly Dictionary<int,State> states=new Dictionary<int,State>();
        readonly TextGenerator measure=new TextGenerator(),generator=new TextGenerator();
        readonly UIVertex[] quad=new UIVertex[4];
        readonly FieldInfo callback=AccessTools.Field(typeof(Text),"m_DisableFontTextureRebuiltCallback");
        readonly CloudDiagnostics diagnostics;
        bool populating;
        static readonly Regex Tags=new Regex(@"<[^<>]+>");
        sealed class State
        {
            internal Text Label;internal Font OriginalFont,OwnedFont;
            internal string Source,Translation;
            internal CloudTextProfile Profile;internal CloudTrace Trace;
            internal TextGenerationSettings Original,LastOriginal;
            internal Rect LastBounds;internal float LastUnits;
            internal Rect NativeMenuInk;
            internal float Width,SourceWidth,SourceHeight,Nominal=1,Fitted;
            internal int PendingFrame,Revision,RegionAttempts;internal float NextRegionAttempt;
            internal string RegionBasis;internal bool LocalSettled,SprintMesh;
            internal float NativeCap=float.NaN,DisplayCap=float.NaN;
            internal Rect LastKeycap;internal float LastSpacing;
            internal bool Active,Pending,Measured,Capacity,NeedsRollback;
            internal bool Blocked,BlockCaptured;internal int BlockFrame;
            internal Rect BlockBounds;internal TextGenerationSettings BlockStyle;
            internal CloudTextReader Reader;internal int ReaderGeneration;internal bool DisplayFallback;
            internal CloudDocumentStructure.Field Document;
            internal LayoutPlan CommonPlan;
        }
        internal CloudTextLayout(CloudDiagnostics d){diagnostics=d;}
        static bool Valid(float n){return n>0&&!float.IsNaN(n)&&!float.IsInfinity(n);}
        static string Plain(string s){return Tags.Replace(s??"","");}
        static bool Cjk(string s){return Regex.IsMatch(Plain(s),@"[\u2e80-\u9fff\uac00-\ud7af\uf900-\ufaff]");}
        static void Dirty(Text t){if(t!=null){t.SetVerticesDirty();LayoutRebuilder.MarkLayoutForRebuild(t.rectTransform);}}
        // Matches this game's UnityEngine.UI.Text.pixelsPerUnit implementation, using
        // the native font even while a dynamic Chinese fallback is assigned.
        static float Units(Text t,Font font)
        {return t.canvas==null?1:font==null||font.dynamic?t.canvas.scaleFactor:t.fontSize>0&&font.fontSize>0?(float)font.fontSize/t.fontSize:1;}
        TextGenerationSettings Native(State s)
        {
            var t=s.Label;
            if(t.font!=s.OwnedFont||s.OwnedFont==null)s.OriginalFont=t.font;
            var g=t.GetGenerationSettings(Vector2.zero);g.font=s.OriginalFont;g.scaleFactor=Units(t,s.OriginalFont);
            g.fontSize=s.OriginalFont!=null&&s.OriginalFont.dynamic?t.fontSize:0;
            g.resizeTextMinSize=t.resizeTextMinSize;g.resizeTextMaxSize=t.resizeTextMaxSize;
            g.color=Color.white;return g;
        }
        internal void Begin(Component c,string source,CloudTextProfile profile,CloudTrace trace)
        {
            var t=c as Text;if(t==null)return;State s;
            if(states.TryGetValue(t.GetInstanceID(),out s))
            {
                Release(s);
                if(s.Label==t&&s.Source==source&&s.Profile.Same(profile)){s.Trace=trace;s.Blocked=false;return;}
            }
            states[t.GetInstanceID()]=new State{Label=t,Source=source,OriginalFont=t.font,Profile=profile,Trace=trace,Document=CloudDocumentStructure.Capture(t,profile)};
        }
        void Release(State s)
        {
            s.Active=false;s.Pending=false;s.LocalSettled=false;s.SprintMesh=false;s.NeedsRollback=false;
            s.ReaderGeneration++;s.DisplayFallback=false;if(s.Reader!=null){s.Reader.Close();s.Reader=null;}
            if(s.Label!=null&&s.OwnedFont!=null&&s.Label.font==s.OwnedFont)s.Label.font=s.OriginalFont;
            s.OwnedFont=null;Dirty(s.Label);
        }
        internal void Abort(Component c){State s;if(c!=null&&states.TryGetValue(c.GetInstanceID(),out s))Release(s);}
        internal void Forget(Component c){if(c==null)return;State s;if(states.TryGetValue(c.GetInstanceID(),out s)){Release(s);states.Remove(c.GetInstanceID());}}
        internal void SourceChanged(Component c)
        {var t=c as Text;State s;if(t!=null&&states.TryGetValue(t.GetInstanceID(),out s)&&s.Active&&!s.Pending&&t.text!=s.Translation)Release(s);}
        internal void LayoutSettled(Component c){State s;if(c!=null&&states.TryGetValue(c.GetInstanceID(),out s)){s.Pending=false;s.LocalSettled=true;}}
        internal void RenewTrace(Component c,CloudTrace trace){State s;if(c!=null&&states.TryGetValue(c.GetInstanceID(),out s))s.Trace=trace;}
        internal void Reactivated(Component c)
        {State s;if(c!=null&&states.TryGetValue(c.GetInstanceID(),out s)&&CloudRemainingUi.Scoped(s.Profile)){s.Measured=false;s.Blocked=false;s.BlockCaptured=false;s.RegionAttempts=0;s.NextRegionAttempt=0;}}
        internal void NativeBeforePrepare(Component c)
        {var t=c as Text;State s;if(t!=null&&states.TryGetValue(t.GetInstanceID(),out s)&&t.font!=s.OwnedFont)s.OriginalFont=t.font;}
        internal void BlockCurrent(Component c)
        {State s;if(c is Text&&states.TryGetValue(c.GetInstanceID(),out s)){s.Blocked=true;s.BlockCaptured=s.RegionAttempts>=6;s.BlockFrame=Time.frameCount;if(s.BlockCaptured){s.BlockBounds=Bounds(s);s.BlockStyle=Native(s);}}}
        static Rect Ink(IList<UIVertex> vertices,float factor)
        {
            float l=float.PositiveInfinity,d=l,r=float.NegativeInfinity,u=r;
            for(int i=0;i+3<vertices.Count-4;i+=4)
            {
                var a=vertices[i].position;var b=vertices[i+2].position;
                if(Mathf.Abs(a.x-b.x)<0.001f||Mathf.Abs(a.y-b.y)<0.001f)continue;
                for(int j=0;j<4;j++){var v=vertices[i+j].position*factor;l=Mathf.Min(l,v.x);r=Mathf.Max(r,v.x);d=Mathf.Min(d,v.y);u=Mathf.Max(u,v.y);}
            }
            return r>l&&u>d?Rect.MinMaxRect(l,d,r,u):new Rect();
        }
        Rect Bounds(State s)
        {
            if(CloudDocumentStructure.Scoped(s.Profile))
            {
                s.RegionBasis=s.Profile.Role==CloudTextRole.IntrinsicLabel?"intrinsic-owned-rect":"fixed-document-allocation";
                return CloudTextPolicy.Constrain(s.Label,s.Profile,CloudDocumentStructure.Bounds(s.Label,s.Profile,s.Document));
            }
            // Reconstruct the game's source mesh, including bitmap-font units and
            // deliberate Overflow settings. This never measures our translated result.
            var g=Native(s);g.generationExtents=s.Label.rectTransform.rect.size;
            if(s.Profile.Role==CloudTextRole.SaveField)
            {
                // Recreate the field's native fitter allocation independently of any
                // translated font or preferred-size interception (including zero rects).
                var f=s.Label.GetComponent<ContentSizeFitter>();var size=g.generationExtents;
                if(f!=null&&f.horizontalFit!=ContentSizeFitter.FitMode.Unconstrained)size.x=s.SourceWidth;
                if(f!=null&&f.verticalFit!=ContentSizeFitter.FitMode.Unconstrained)size.y=s.SourceHeight;
                g.generationExtents=size;g.verticalOverflow=VerticalWrapMode.Overflow;
            }
            measure.Invalidate();Rect ink=new Rect();
            if(measure.Populate(s.Source,g))
            {
                float unit=Mathf.Max(0.01f,g.scaleFactor);ink=Ink(measure.verts,1/unit);
                if(measure.verts.Count>4){Vector2 first=measure.verts[0].position/unit;ink.position+=s.Label.PixelAdjustPoint(first)-first;}
            }
            var b=CloudTextPolicy.UiBounds(s.Label,s.Profile,ink,s.SourceWidth,out s.RegionBasis);
            if(s.Profile.Role==CloudTextRole.Tutorial&&s.Profile.Keycap!=null)
            {
                var key=KeycapRect(s);
                if(CloudTextPolicy.Usable(key))
                {b=CloudTextPolicy.Constrain(s.Label,s.Profile,Rect.MinMaxRect(b.xMin,Mathf.Min(b.yMin,key.yMin),b.xMax,Mathf.Max(b.yMax,key.yMax)));s.RegionBasis="tutorial-native-line-and-keycap";}
            }
            if(s.Profile.Role==CloudTextRole.MainMenu||s.Profile.Role==CloudTextRole.SaveField||s.Profile.Coordinated)return b;
            float x=Mathf.Min(2,Mathf.Max(0,b.width)*0.02f),y=Mathf.Min(2,Mathf.Max(0,b.height)*0.04f);
            return Rect.MinMaxRect(b.xMin+x,b.yMin+y,b.xMax-x,b.yMax-y);
        }
        bool Changed(State s)
        {return !s.Measured||!Native(s).Equals(s.LastOriginal)||Bounds(s)!=s.LastBounds||Mathf.Abs(Units(s.Label,s.OriginalFont)-s.LastUnits)>0.001f||
            s.Profile.Keycap!=null&&(KeycapRect(s)!=s.LastKeycap||Spacing(s)!=s.LastSpacing);}
        TextGenerationSettings Settings(State s,float width,float scale)
        {
            var t=s.Label;var g=t.GetGenerationSettings(Vector2.zero);g.resizeTextForBestFit=false;
            bool single=s.Profile.SingleLine;
            // A version label is intrinsically one line. Buttons retain the
            // game's own wrapping capability, including translated long actions.
            if(s.Profile.Role==CloudTextRole.ModalButton)single=s.Original.horizontalOverflow==HorizontalWrapMode.Overflow;
            g.verticalOverflow=VerticalWrapMode.Overflow;g.horizontalOverflow=single&&(!s.DisplayFallback||CloudFixedUi.Direct(s.Profile)||CloudManualLayout.Direct(s.Profile)||CloudDocumentStructure.Direct(s.Profile))?HorizontalWrapMode.Overflow:HorizontalWrapMode.Wrap;
            g.lineSpacing=Cjk(s.Translation)?Mathf.Max(1,s.Original.lineSpacing):s.Original.lineSpacing;
            g.alignByGeometry=true;g.generationExtents=new Vector2(width/Mathf.Max(0.01f,scale),0);return g;
        }
        void OriginalDemand(State s)
        {
            s.Original=Native(s);var g=s.Original;g.resizeTextForBestFit=false;
            float units=Mathf.Max(0.01f,g.scaleFactor);
            s.SourceWidth=measure.GetPreferredWidth(s.Source,g)/units;
            s.Width=s.Label.rectTransform.rect.width;
            if(s.Profile.Role==CloudTextRole.SaveField)
            {var f=s.Label.GetComponent<ContentSizeFitter>();if(f!=null&&f.horizontalFit!=ContentSizeFitter.FitMode.Unconstrained)s.Width=s.SourceWidth;}
            if(!Valid(s.Width))s.Width=s.Profile.Boundary!=null?s.Profile.Boundary.rect.width:s.SourceWidth;
            s.Width=Mathf.Max(4,s.Width);g.generationExtents=new Vector2(s.Width,0);g.verticalOverflow=VerticalWrapMode.Overflow;
            s.SourceHeight=measure.GetPreferredHeight(s.Source,g)/units;
            if(s.Profile.Role==CloudTextRole.SaveField)
            {
                measure.Invalidate();if(measure.Populate(s.Source,g))s.SourceHeight=Mathf.Max(s.SourceHeight,Ink(measure.verts,1/units).height);
            }
        }
        float Nominal(State s)
        {
            if(s.Profile.Role==CloudTextRole.MainMenu){float n=MenuNominal(s);return Valid(n)?n:1;}
            if(s.Profile.Coordinated)return CoordinatedNominal(s);
            if(!Cjk(s.Translation))return 1;
            var g=s.Original;float nativeScale=1;
            if(g.resizeTextForBestFit&&g.font!=null&&g.font.dynamic)
            {
                g.generationExtents=s.Label.rectTransform.rect.size;measure.Populate(s.Source,g);
                if(measure.fontSizeUsedForBestFit>0&&g.fontSize>0)nativeScale=Mathf.Min(1,(float)measure.fontSizeUsedForBestFit/g.fontSize);
            }
            g.resizeTextForBestFit=false;g.generationExtents=Vector2.zero;g.horizontalOverflow=HorizontalWrapMode.Overflow;
            if(s.OriginalFont==s.Label.font)return nativeScale;
            measure.Populate("H",g);float original=Ink(measure.verts,1/Mathf.Max(0.01f,g.scaleFactor)).height;
            string sample="";foreach(char c in Plain(s.Translation))if(c>=0x2e80&&c<=0x9fff){sample+=c;if(sample.Length>=8)break;}
            if(sample.Length==0)return nativeScale;
            var current=Settings(s,10000,1);current.horizontalOverflow=HorizontalWrapMode.Overflow;
            measure.Populate(sample,current);float translated=Ink(measure.verts,1/Mathf.Max(0.01f,s.Label.pixelsPerUnit)).height;
            return nativeScale*(Valid(original)&&Valid(translated)?Mathf.Clamp(original/translated,0.5f,1):1);
        }
        float CoordinatedNominal(State s)
        {
            s.NativeCap=s.DisplayCap=float.NaN;
            var g=s.Original;g.resizeTextForBestFit=false;g.generationExtents=Vector2.zero;
            g.horizontalOverflow=HorizontalWrapMode.Overflow;g.verticalOverflow=VerticalWrapMode.Overflow;
            // A stable cap-height reference avoids content-length-dependent baselines.
            measure.Invalidate();if(!measure.Populate("H",g))return 1;
            float native=Ink(measure.verts,1/Mathf.Max(0.01f,g.scaleFactor)).height;
            var current=Settings(s,10000,1);current.horizontalOverflow=HorizontalWrapMode.Overflow;
            measure.Invalidate();if(!measure.Populate("国H012",current))return 1;
            float coordinated=Ink(measure.verts,1/Mathf.Max(0.01f,s.Label.pixelsPerUnit)).height;
            s.NativeCap=native;s.DisplayCap=coordinated;
            return Valid(native)&&Valid(coordinated)?native/coordinated:1;
        }
        Rect Generate(State s,Rect b,float scale)
        {return GenerateWith(s,b,scale,generator);}
        Rect GenerateWith(State s,Rect b,float scale,TextGenerator target)
        {
            float width=b.width;
            for(int attempt=0;attempt<5;attempt++)
            {
                target.Invalidate();if(!target.Populate(s.Translation,Settings(s,width,scale)))return new Rect();
                // The album notice has no explicit line break. Keep its final
                // punctuation with preceding text by reflowing the complete string
                // at the SAME size, within its own field. No inserted/deleted text.
                if((s.Profile.Role!=CloudTextRole.AlbumNotice&&s.Profile.Role!=CloudTextRole.ModalBody&&s.Profile.Role!=CloudTextRole.ManualBody)||!Cjk(s.Translation)||s.Translation.IndexOf('<')>=0||target.lines.Count<2)break;
                bool orphan=false;
                int first=s.Profile.Role==CloudTextRole.AlbumNotice?target.lines.Count-1:1;
                for(int line=first;line<target.lines.Count;line++)
                {
                    int start=target.lines[line].startCharIdx;
                    int end=line+1<target.lines.Count?target.lines[line+1].startCharIdx:s.Translation.Length;
                    if(start>=0&&end>start&&end<=s.Translation.Length&&Regex.IsMatch(s.Translation.Substring(start,end-start).Trim(),@"^[。，！？…；：、）】》”’.,!?;:]+$")){orphan=true;break;}
                }
                if(!orphan)break;
                width=b.width*(1-0.03f*(attempt+1));
            }
            return Ink(target.verts,scale/Mathf.Max(0.01f,s.Label.pixelsPerUnit));
        }
        static bool Fits(Rect ink,Rect b){return Valid(ink.width)&&Valid(ink.height)&&ink.width<=b.width+0.1f&&ink.height<=b.height+0.1f;}
        float MenuNominal(State s)
        {
            // Match the actual full source mesh height in label-local units.
            // A bitmap font's pixel size and a dynamic font's nominal size are
            // different units; a fixed minimum ratio would defeat this conversion.
            var g=s.Original;g.generationExtents=s.Label.rectTransform.rect.size;
            measure.Invalidate();if(!measure.Populate(s.Source,g))return float.NaN;
            s.NativeMenuInk=Ink(measure.verts,1/Mathf.Max(0.01f,g.scaleFactor));float native=s.NativeMenuInk.height;
            var settings=Settings(s,1,1);settings.horizontalOverflow=HorizontalWrapMode.Overflow;
            measure.Invalidate();if(!measure.Populate(s.Translation,settings))return float.NaN;
            float translated=Ink(measure.verts,1/Mathf.Max(0.01f,s.Label.pixelsPerUnit)).height;
            return Valid(native)&&Valid(translated)?native/translated:float.NaN;
        }
        static string RectData(Rect b)
        {return CloudDiagnostics.Number(b.xMin)+","+CloudDiagnostics.Number(b.yMin)+","+CloudDiagnostics.Number(b.xMax)+","+CloudDiagnostics.Number(b.yMax);}
        void FitMenu(State s)
        {
            var b=Bounds(s);float nominal=MenuNominal(s),chosen=nominal;Rect ink=new Rect();
            bool available=Valid(nominal)&&CloudTextPolicy.Usable(b);
            if(available)
            {
                ink=Generate(s,b,chosen);
                if(!Fits(ink,b))
                {
                    // At most 15% smaller VISUAL height than the native menu.
                    float lo=nominal*0.85f,hi=nominal;
                    if(Fits(Generate(s,b,lo),b))
                    {for(int i=0;i<9;i++){float mid=(lo+hi)*0.5f;if(Fits(Generate(s,b,mid),b))lo=mid;else hi=mid;}chosen=lo;}
                    else chosen=lo;
                    ink=Generate(s,b,chosen);
                }
            }
            s.Nominal=available?nominal:1;s.Fitted=available?chosen:1;
            s.Capacity=!available||!Fits(ink,b);s.Measured=true;s.LastOriginal=Native(s);s.LastBounds=b;s.LastUnits=Units(s.Label,s.OriginalFont);s.Revision++;
            var placed=ink;var anchor=Text.GetTextAnchorPivot(s.Label.alignment);
            placed.position=new Vector2(b.xMin+(b.width-ink.width)*anchor.x,b.yMin+(b.height-ink.height)*anchor.y);
            diagnostics.Event(s.Trace,s.Capacity?"ui-layout-capacity":"ui-layout-ready",s.Trace.Job,-1,
                "revision="+s.Revision+" mode=menu-native-ink units=label-local basis="+s.RegionBasis+
                " width="+CloudDiagnostics.Number(b.width)+" height="+CloudDiagnostics.Number(b.height)+
                " cell="+RectData(CloudTextPolicy.MenuCell(s.Label,s.Profile))+" constrained="+RectData(b)+
                " nativeInk="+(available?RectData(s.NativeMenuInk):"unavailable")+" placedInk="+(available?RectData(placed):"unavailable")+
                " nativePixelsPerUnit="+CloudDiagnostics.Number(s.LastUnits)+" fallbackPixelsPerUnit="+CloudDiagnostics.Number(s.Label.pixelsPerUnit)+
                " baseScale="+CloudDiagnostics.Number(nominal)+" finalScale="+CloudDiagnostics.Number(chosen)+
                " glyphWidth="+(available?CloudDiagnostics.Number(ink.width):"unavailable")+" glyphHeight="+(available?CloudDiagnostics.Number(ink.height):"unavailable")+
                " reason="+(!available?"glyph-or-cell-unavailable":s.Capacity?"readable-glyph-exceeds-cell":"glyph-fit-positioned-by-native-anchor"));
        }
        void Fit(State s)
        {
            if(s.Profile.Role==CloudTextRole.MainMenu){FitMenu(s);return;}
            if(Common(s.Profile)){FitCommon(s);return;}
            var b=Bounds(s);s.Nominal=Nominal(s);float chosen=s.Nominal;
            bool sprint=s.Profile.Role==CloudTextRole.Tutorial&&s.Profile.Keycap!=null;
            bool stable=CloudDisplayRoles.Guide(s.Profile)||s.Profile.Role==CloudTextRole.InventoryName;
            if(!sprint&&!stable&&!CloudFixedUi.NativeScroll(s.Profile)&&!Fits(Generate(s,b,chosen),b))
            {
                float lo=chosen*(s.Profile.Coordinated?0.85f:0.5f),hi=chosen;
                if(Fits(Generate(s,b,lo),b))for(int i=0;i<9;i++){float mid=(lo+hi)*0.5f;if(Fits(Generate(s,b,mid),b))lo=mid;else hi=mid;}
                chosen=lo;
            }
            var finalInk=sprint?new Rect():Generate(s,b,chosen);
            s.Fitted=chosen;s.Capacity=sprint?!PopulateSprint(s,b,null):!Fits(finalInk,b);s.Measured=true;
            s.LastOriginal=Native(s);s.LastBounds=b;s.LastUnits=Units(s.Label,s.OriginalFont);s.Revision++;
            if(sprint){s.LastKeycap=KeycapRect(s);s.LastSpacing=Spacing(s);}
            diagnostics.Event(s.Trace,s.Capacity?"ui-layout-capacity":"ui-layout-ready",s.Trace.Job,-1,"revision="+s.Revision+" basis="+s.RegionBasis+" width="+CloudDiagnostics.Number(b.width)+" height="+CloudDiagnostics.Number(b.height)+" scale1000="+(int)(s.Fitted*1000)+
                (!s.Profile.Coordinated?"":" units=label-local context="+s.Profile.Context+" wrap="+!s.Profile.SingleLine+
                " nativeCap="+CloudDiagnostics.Number(s.NativeCap)+" displayCap="+CloudDiagnostics.Number(s.DisplayCap)+
                " baseScale="+CloudDiagnostics.Number(s.Nominal)+" finalScale="+CloudDiagnostics.Number(s.Fitted)+
                " glyphWidth="+(sprint?"split-runs":CloudDiagnostics.Number(finalInk.width))+" glyphHeight="+(sprint?"split-runs":CloudDiagnostics.Number(finalInk.height))+
                " reason="+(s.Capacity?"readable-field-capacity":sprint?"keycap-separated-tracked-runs":"coordinated-field-fit")));
        }
        static bool Common(CloudTextProfile p)
        {return CloudDocumentStructure.Scoped(p)||CloudFixedUi.Direct(p);}
        void FitCommon(State s)
        {
            var b=Bounds(s);s.Nominal=CoordinatedNominal(s);
            bool body=s.Profile.Role==CloudTextRole.DocumentBody;
            Generate(s,b,s.Nominal);
            float line=s.NativeCap;
            if(generator.lines.Count>0)line=Mathf.Max(line,generator.lines[0].height*s.Nominal/Mathf.Max(0.01f,s.Label.pixelsPerUnit));
            var use=body?TextUse.Body:s.Profile.Role==CloudTextRole.DocumentNumber?TextUse.Number:
                s.Profile.Role==CloudTextRole.ModalButton?TextUse.Button:TextUse.Label;
            var input=new LayoutInput{Use=use,Current=s.Active,
                RegionReady=CloudTextPolicy.Usable(b),MetricsReady=Valid(s.NativeCap)&&Valid(s.DisplayCap),
                Available=new Extent(b.width,b.height),NativeCap=s.NativeCap,DisplayCap=s.DisplayCap,
                ReadableLineHeight=line,MinimumScaleRatio=0.85f,KeepGroupSize=body,FieldScroll=body,NativeFlow=false};
            s.CommonPlan=EmbeddedLayoutRules.Decide(input,delegate(float scale){var r=Generate(s,b,scale);return new Extent(r.width,r.height);});
            s.Nominal=s.CommonPlan.Nominal;s.Fitted=s.CommonPlan.Scale;
            s.Capacity=s.CommonPlan.State!=LayoutState.Ready;s.Measured=true;
            s.LastOriginal=Native(s);s.LastBounds=b;s.LastUnits=Units(s.Label,s.OriginalFont);s.Revision++;
            diagnostics.Event(s.Trace,"embedded-layout-decision",s.Trace.Job,-1,"state="+s.CommonPlan.State+" reading="+s.CommonPlan.Reading+
                " basis="+s.RegionBasis+" width="+CloudDiagnostics.Number(b.width)+" height="+CloudDiagnostics.Number(b.height)+
                " nativeCap="+CloudDiagnostics.Number(s.NativeCap)+" displayCap="+CloudDiagnostics.Number(s.DisplayCap)+
                " scale="+CloudDiagnostics.Number(s.Fitted)+" revision="+s.Revision);
        }
        internal CloudLayoutResult Prepare(Component c,string value)
        {
            var t=c as Text;State s;if(t==null)return CloudLayoutResult.Ready;
            if(!states.TryGetValue(t.GetInstanceID(),out s)||t.font==null)return CloudLayoutResult.Capacity;
            s.Blocked=false;s.DisplayFallback=false;
            // Font fallback was prepared by the caller after native capture.
            if(s.OwnedFont==null){s.OwnedFont=t.font;}else if(t.font!=s.OwnedFont){s.OriginalFont=t.font;s.OwnedFont=t.font;}
            bool fresh=!s.Active;bool newText=s.Translation!=value;s.Translation=value;s.Active=true;
            if(newText)s.Measured=false;
            if(fresh||Changed(s)){OriginalDemand(s);s.Nominal=Nominal(s);}
            var region=Bounds(s);
            if(!CloudTextPolicy.Usable(region))
            {
                // Layout creation is not a capacity verdict. Six paced attempts over
                // roughly three seconds, then dormant until native region/style changes.
                s.Measured=false;
                if(s.Profile.TooltipOwner!=null&&s.Profile.Grow){s.Pending=true;s.PendingFrame=Time.frameCount;}
                if(Time.unscaledTime<s.NextRegionAttempt)return CloudLayoutResult.Deferred;
                s.RegionAttempts++;s.NextRegionAttempt=Time.unscaledTime+Mathf.Min(0.8f,0.1f*(1<<Math.Min(3,s.RegionAttempts-1)));
                if(s.RegionAttempts<6){Dirty(t);diagnostics.Event(s.Trace,"ui-region-pending",0,-1,"attempt="+s.RegionAttempts+CloudRemainingUi.RegionDetail(t,s.Profile));return CloudLayoutResult.Deferred;}
                diagnostics.Event(s.Trace,"ui-region-unresolved",0,-1,"basis="+s.RegionBasis+" attempts=6 measurements=unavailable"+CloudRemainingUi.RegionDetail(t,s.Profile));
                s.Measured=true;s.LastBounds=region;s.LastOriginal=Native(s);s.LastUnits=Units(t,s.OriginalFont);
                return CloudLayoutResult.Capacity;
            }
            s.RegionAttempts=0;s.NextRegionAttempt=0;
            if(fresh&&!s.LocalSettled&&s.Profile.Grow&&(newText||Changed(s)||s.Capacity))
            {s.Pending=true;s.PendingFrame=Time.frameCount;Dirty(t);return CloudLayoutResult.Deferred;}
            if(s.Pending&&Time.frameCount<=s.PendingFrame)return CloudLayoutResult.Deferred;
            s.Pending=false;
            if(Changed(s))Fit(s);
            return s.Capacity?CloudLayoutResult.Capacity:CloudLayoutResult.Ready;
        }
        // Layout readiness is separate from content validity. No source-language
        // restoration is performed while a valid translated field is settling.
        internal void DisplayFallback(Component c,string value)
        {
            State s;if(!(c is Text)||!states.TryGetValue(c.GetInstanceID(),out s))return;
            if(!s.Active){s.Translation=value;s.Active=true;s.OwnedFont=s.Label.font;OriginalDemand(s);s.Nominal=Nominal(s);}
            s.Pending=false;s.DisplayFallback=true;s.NeedsRollback=false;s.Blocked=false;
            Dirty(s.Label);
        }
        internal void Displayed(Component c)
        {State s;if(c!=null&&states.TryGetValue(c.GetInstanceID(),out s)){s.Pending=false;s.NeedsRollback=false;Dirty(s.Label);CloudTextPolicy.RepositionTooltip(s.Profile);}}
        internal bool NeedsReflow(Component c)
        {
            State s;if(!(c is Text)||!states.TryGetValue(c.GetInstanceID(),out s))return false;
            if(s.Blocked)
            {
                // A failed growing tooltip first settles back to its native preferred
                // size. That undo is not a new reason to retry and grow it again.
                if(Time.frameCount<=s.BlockFrame)return false;
                if(!s.BlockCaptured){s.BlockBounds=Bounds(s);s.BlockStyle=Native(s);s.BlockCaptured=true;return false;}
                return Bounds(s)!=s.BlockBounds||!Native(s).Equals(s.BlockStyle);
            }
            return s.Pending||Changed(s);
        }
        internal bool TakeRollback(Component c){State s;return c is Text&&states.TryGetValue(c.GetInstanceID(),out s)&&s.NeedsRollback;}
        internal void MeshFailed(Component c){State s;if(c is Text&&states.TryGetValue(c.GetInstanceID(),out s)&&s.Active)s.NeedsRollback=true;}
        internal void Preferred(Text t,int axis,ref float value)
        {
            State s;if(t==null||!states.TryGetValue(t.GetInstanceID(),out s)||!s.Active||(!s.Pending&&t.text!=s.Translation))return;
            if(s.Profile.Role==CloudTextRole.IntrinsicLabel)
            {
                // OriginalDemand remains the native baseline. Independently measure
                // translated demand at native cap height, never previous fitted ink.
                var intrinsic=Settings(s,1,s.Nominal);intrinsic.horizontalOverflow=HorizontalWrapMode.Overflow;intrinsic.generationExtents=Vector2.zero;
                float unit=Mathf.Max(0.01f,t.pixelsPerUnit);measure.Invalidate();measure.Populate(s.Translation,intrinsic);
                var ink=Ink(measure.verts,s.Nominal/unit);
                float demand=(axis==0?measure.GetPreferredWidth(s.Translation,intrinsic):measure.GetPreferredHeight(s.Translation,intrinsic))*s.Nominal/unit;
                value=Mathf.Max(axis==0?ink.width:ink.height,demand)+2;return;
            }
            if(s.Profile.Role==CloudTextRole.RecipeName&&axis==0)
            {
                // The fitter must allocate this title's band, not zero-width ink.
                float titleWidth=CloudRemainingUi.FieldBounds(t,s.Profile).width;
                value=Valid(titleWidth)?titleWidth:s.SourceWidth;return;
            }
            if(CloudFixedUi.Scoped(s.Profile))
            {
                if(s.Profile.Role==CloudTextRole.ModalBody||s.Profile.Role==CloudTextRole.ModalButton||s.Profile.Role==CloudTextRole.ModalTitle)
                {
                    // Only the verified modal layout groups consume translated
                    // demand. MaxScalingViewport owns the BODY's native scroll cap.
                    float modalWidth=Mathf.Max(4,t.rectTransform.rect.width);
                    var modalSettings=Settings(s,modalWidth,s.Nominal);float unit=Mathf.Max(0.01f,t.pixelsPerUnit);
                    if(axis==0)
                    {
                        if(s.Profile.Role==CloudTextRole.ModalBody||s.Profile.Role==CloudTextRole.ModalTitle){value=s.SourceWidth;return;}
                        modalSettings.generationExtents=Vector2.zero;modalSettings.horizontalOverflow=HorizontalWrapMode.Overflow;
                        value=Mathf.Max(s.SourceWidth,measure.GetPreferredWidth(s.Translation,modalSettings)*s.Nominal/unit+2);return;
                    }
                    float demand=measure.GetPreferredHeight(s.Translation,modalSettings)*s.Nominal/unit;
                    demand=Mathf.Max(demand,GenerateWith(s,new Rect(0,0,modalWidth,1),s.Nominal,measure).height);
                    value=Mathf.Max(4,demand+4);return;
                }
                value=axis==0?s.SourceWidth:s.SourceHeight;return;
            }
            if(CloudDisplayRoles.Guide(s.Profile)||CloudManualLayout.Scoped(s.Profile)||CloudDocumentStructure.Document(s.Profile))
            {
                // Fixed pages cannot grow neighbouring people. Retain ORIGINAL
                // demand, including the parent height that positions a child name.
                value=axis==0?s.SourceWidth:s.SourceHeight;return;
            }
            if(s.Profile.Role==CloudTextRole.SaveField||CloudRemainingUi.Recipe(s.Profile))
            {value=axis==0?s.SourceWidth:s.SourceHeight;return;}
            float width=t.rectTransform.rect.width;if(!Valid(width))width=s.Width;
            var g=Settings(s,Mathf.Max(4,width-4),s.Nominal);float units=Mathf.Max(0.01f,t.pixelsPerUnit);
            if(axis==0)
            {
                g.generationExtents=Vector2.zero;g.horizontalOverflow=HorizontalWrapMode.Overflow;
                float demand=measure.GetPreferredWidth(s.Translation,g)*s.Nominal/units;
                // Native layout groups/fitter choose the final size; measured demand is
                // independent of any fitted vertices or current translated height.
                value=s.Profile.Grow?Mathf.Max(s.SourceWidth,demand+4):s.SourceWidth;
            }
            else if(s.Profile.Grow)
            {
                float height=measure.GetPreferredHeight(s.Translation,g)*s.Nominal/units;
                measure.Populate(s.Translation,g);height=Mathf.Max(height,Ink(measure.verts,s.Nominal/units).height);
                value=Mathf.Max(s.SourceHeight,height+4);
            }
            else value=s.SourceHeight;
        }
        internal bool Populate(Text t,VertexHelper output)
        {
            State s;if(populating||!states.TryGetValue(t.GetInstanceID(),out s))return false;
            s.SprintMesh=false;if(!s.Active||s.Pending&&!s.DisplayFallback||t.text!=s.Translation)return false;
            populating=true;bool old=callback!=null&&(bool)callback.GetValue(t);if(callback!=null)callback.SetValue(t,true);
            try
            {
                if(Changed(s)){OriginalDemand(s);Fit(s);}
                var b=Bounds(s);
                if(!CloudTextPolicy.Usable(b)){if(s.Reader!=null)s.Reader.Hide();s.NeedsRollback=true;return false;}
                if(s.Profile.Role==CloudTextRole.Tutorial&&s.Profile.Keycap!=null)
                {if(PopulateSprint(s,b,output)){if(s.Reader!=null)s.Reader.Hide();s.SprintMesh=true;return true;}s.NeedsRollback=true;}
                var ink=Generate(s,b,s.Fitted);
                if((s.Capacity||!Fits(ink,b))&&!CloudFixedUi.Direct(s.Profile)&&!CloudManualLayout.Direct(s.Profile)&&!CloudDocumentStructure.Direct(s.Profile)&&!CloudFixedUi.NativeScroll(s.Profile))
                {
                    if(Common(s.Profile)&&s.CommonPlan.Reading!=ReadingMode.FieldScroll)
                    {
                        diagnostics.Event(s.Trace,"embedded-region-not-readable",s.Trace.Job,-1,"state="+s.CommonPlan.State+" basis="+s.RegionBasis);
                        s.NeedsRollback=true;return false;
                    }
                    return PopulateReading(s,b,output);
                }
                if(s.Reader!=null)s.Reader.Hide();
                Vector2 anchor=Text.GetTextAnchorPivot(t.alignment);
                Vector2 offset=new Vector2(b.xMin+(b.width-ink.width)*anchor.x-ink.xMin,b.yMin+(b.height-ink.height)*anchor.y-ink.yMin);
                var verts=generator.verts;float factor=s.Fitted/Mathf.Max(0.01f,t.pixelsPerUnit);output.Clear();
                for(int i=0;i<verts.Count-4;i++)
                {int j=i&3;quad[j]=verts[i];var v=quad[j].position*factor;v.x+=offset.x;v.y+=offset.y;quad[j].position=v;if(j==3)output.AddUIVertexQuad(quad);}
                return true;
            }
            finally{populating=false;if(callback!=null)callback.SetValue(t,old);}
        }
        bool PopulateReading(State s,Rect b,VertexHelper output)
        {
            // Capacity remains recorded; render the complete string at a stable
            // cap height through a scrollable viewport within the actual field.
            s.DisplayFallback=true;float scale=Valid(s.Nominal)?s.Nominal:1;
            if(CloudManualLayout.Scoped(s.Profile)&&Valid(s.DisplayCap))
            {
                // The small instruction bands are bounded by adjacent icons.
                // Keep at least one COMPLETE glyph line, with at most the same
                // 15% local fit allowance used above, before requesting a rail.
                float lineScale=b.height*0.98f/s.DisplayCap;
                if(lineScale<scale*0.85f)
                {
                    if(s.Reader!=null)s.Reader.Hide();s.NeedsRollback=true;
                    diagnostics.Event(s.Trace,"manual-region-not-readable",s.Trace.Job,-1,"context="+s.Profile.Context+" basis="+s.RegionBasis);
                    return false;
                }
                scale=Mathf.Min(scale,lineScale);
            }
            var content=b;content.xMax-=Mathf.Min(8,b.width*0.08f);
            var ink=Generate(s,content,scale);if(!CloudTextPolicy.Usable(ink)){if(s.Reader!=null)s.Reader.Hide();return false;}
            // This allocates managed state only, not a Unity object. Chinese is
            // already rendered at the field's top while the rail awaits commit.
            if(s.Reader==null)
            {
                int generation=s.ReaderGeneration;var scene=s.Label.gameObject.scene;
                s.Reader=new CloudTextReader(s.Label,delegate
                {
                    State live;return s.Label!=null&&s.Active&&s.ReaderGeneration==generation&&
                        states.TryGetValue(s.Label.GetInstanceID(),out live)&&ReferenceEquals(live,s)&&
                        s.Label.gameObject.scene.Equals(scene)&&s.Label.text==s.Translation;
                },delegate
                {
                    string kind=s.Profile.Role==CloudTextRole.Bubble?"bubble":s.Profile.Role==CloudTextRole.Dialogue?"reveal":"generic";
                    return s.Profile.Same(CloudTextPolicy.Resolve(s.Label,kind))?Bounds(s):new Rect();
                });
            }
            float scroll=s.Reader.Request(b,ink.height,s.Source+"\n"+s.Translation);var verts=generator.verts;float factor=scale/Mathf.Max(0.01f,s.Label.pixelsPerUnit);output.Clear();
            Vector2 offset=new Vector2(content.xMin-ink.xMin,content.yMax-ink.yMax+scroll);
            for(int i=0;i<verts.Count-4;i++)
            {
                int j=i&3;quad[j]=verts[i];var v=quad[j].position*factor;v.x+=offset.x;v.y+=offset.y;quad[j].position=v;
                if(j==3&&CloudTextReader.Clip(quad,content))output.AddUIVertexQuad(quad);
            }
            diagnostics.Event(s.Trace,"chinese-field-reading",s.Trace.Job,-1,"basis="+s.RegionBasis+" viewportHeight="+CloudDiagnostics.Number(b.height)+" fullHeight="+CloudDiagnostics.Number(ink.height)+" complete-text=True");
            return true;
        }
        internal bool OwnsSpacing(Text t)
        {State s;return t!=null&&states.TryGetValue(t.GetInstanceID(),out s)&&(s.SprintMesh||CloudRemainingUi.Scoped(s.Profile)||CloudDisplayRoles.Ui(s.Profile)||CloudFixedUi.Scoped(s.Profile)||CloudManualLayout.Scoped(s.Profile)||CloudDocumentStructure.Scoped(s.Profile))&&s.Active&&!s.Pending&&t.text==s.Translation;}
        static float Spacing(State s)
        {
            var type=AccessTools.TypeByName("UnityEngine.UI.LetterSpacing");var component=type==null?null:s.Label.GetComponent(type) as Behaviour;
            var value=component!=null&&component.isActiveAndEnabled?CloudUiRoles.Get(component,"m_spacing"):null;
            // The shipped modifier adds spacing*fontSize/100 directly in label-local units.
            return value is float?(float)value*s.Label.fontSize/100f:0;
        }
        static Rect KeycapRect(State s)
        {
            var cap=s.Profile.Keycap;if(cap==null||cap.sprite==null)return new Rect();var sb=cap.sprite.bounds;
            Vector3 a=s.Label.transform.InverseTransformPoint(cap.transform.TransformPoint(sb.min));
            Vector3 z=s.Label.transform.InverseTransformPoint(cap.transform.TransformPoint(sb.max));
            return Rect.MinMaxRect(Mathf.Min(a.x,z.x),Mathf.Min(a.y,z.y),Mathf.Max(a.x,z.x),Mathf.Max(a.y,z.y));
        }
        UIVertex[] SprintRun(State s,string text,float width,float scale,out Rect ink)
        {
            generator.Invalidate();ink=new Rect();
            if(!generator.Populate(text,Settings(s,width,scale)))return null;
            var vertices=generator.verts;var copy=new UIVertex[vertices.Count];
            float factor=scale/Mathf.Max(0.01f,s.Label.pixelsPerUnit),tracking=Spacing(s);
            // These are single-line runs. Apply the verified native tracking once to
            // generated glyph quads, rather than indexing raw rich-text tag characters.
            for(int i=0;i<copy.Length;i++){copy[i]=vertices[i];var v=copy[i].position*factor;v.x+=(i/4)*tracking;copy[i].position=v;}
            ink=Ink(copy,1);return copy;
        }
        bool PopulateSprint(State s,Rect b,VertexHelper output)
        {
            // The protected key token remains in the complete translated string.
            // Each run keeps its markup; no shortening, padding spaces or icon moves.
            var match=Regex.Match(s.Translation,@"<color=black>Shift</color>");
            if(!match.Success||s.Profile.Keycap.sprite==null)return false;
            Rect key=KeycapRect(s);
            if(!CloudTextPolicy.Usable(key)||key.xMin<=b.xMin||key.xMax>=b.xMax)return false;
            string[] runs={s.Translation.Substring(0,match.Index),match.Value,s.Translation.Substring(match.Index+match.Length)};
            float gap=Mathf.Min(key.height*0.12f,(b.xMax-key.xMax)*0.05f);
            Rect[] areas={Rect.MinMaxRect(b.xMin,b.yMin,key.xMin-gap,b.yMax),key,Rect.MinMaxRect(key.xMax+gap,b.yMin,b.xMax,b.yMax)};
            var streams=new List<UIVertex[]>();float minimum=s.Nominal;
            for(int n=0;n<3;n++)
            {
                if(Plain(runs[n]).Trim().Length==0){streams.Add(new UIVertex[0]);continue;}
                float scale=s.Nominal;Rect ink=new Rect();bool fits=false;UIVertex[] vertices=null;
                for(int attempt=0;attempt<2;attempt++)
                {
                    vertices=SprintRun(s,runs[n],areas[n].width,scale,out ink);if(vertices==null)return false;
                    if(Fits(ink,areas[n])){fits=true;break;}scale=s.Nominal*0.85f;
                }
                if(!fits)return false;
                minimum=Mathf.Min(minimum,scale);
                // Narration is kept outside the keycap; the key name is centred on it.
                float x=n==0?areas[n].xMax-ink.width:n==1?areas[n].center.x-ink.width/2:areas[n].xMin;
                float y=key.center.y-ink.height/2;
                if(y<b.yMin-0.1f||y+ink.height>b.yMax+0.1f)return false;
                var copy=new UIVertex[Math.Max(0,vertices.Length-4)];
                for(int i=0;i<copy.Length;i++){copy[i]=vertices[i];var v=copy[i].position;v.x+=x-ink.xMin;v.y+=y-ink.yMin;copy[i].position=v;}
                streams.Add(copy);
            }
            s.Fitted=minimum;
            if(output!=null){output.Clear();foreach(var vertices in streams)for(int i=0;i<vertices.Length;i++){int j=i&3;quad[j]=vertices[i];if(j==3)output.AddUIVertexQuad(quad);}}
            return true;
        }
        internal void Prune(){var dead=new List<int>();foreach(var p in states)if(p.Value.Label==null)dead.Add(p.Key);foreach(int id in dead){Release(states[id]);states.Remove(id);}}
        public void Dispose(){foreach(var s in states.Values)Release(s);states.Clear();((IDisposable)measure).Dispose();((IDisposable)generator).Dispose();}
    }
}
