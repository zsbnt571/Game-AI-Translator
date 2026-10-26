using System;
using System.Collections.Generic;
using System.Reflection;

namespace Fusion.UnityEmbedded
{
    // Own scalar text-layout values only. Never change rectangles, game styles,
    // anchors, scroll views, or values subsequently replaced by another writer.
    internal sealed class CjkTextLayout
    {
        sealed class Lease {internal PropertyInfo Property;internal object Original,Owned;}
        readonly Dictionary<string,Lease> leases=new Dictionary<string,Lease>();
        readonly HashSet<string> relinquished=new HashSet<string>();
        static readonly Dictionary<string,Type> layoutTypes=new Dictionary<string,Type>();
        string source;bool fittedLabel;
        string fitText;double fitWidth,fitHeight;int fitRevision;
        WeakReference fitFont;double fitMeasuredSize;long fitReviewAt;
        internal readonly LegacyTextRegion Region=new LegacyTextRegion();
        internal string DisplayText{get;private set;}
        internal void Capture(object target,string original)
        {
            if(source!=original){Restore(target);source=original;relinquished.Clear();}
            Region.Capture(target,original);
        }
        double OriginalNumber(object target,string name)
        {Lease lease;if(leases.TryGetValue(name,out lease)&&Equals(lease.Property.GetValue(target,null),lease.Owned))return Convert.ToDouble(lease.Original);return Convert.ToDouble(target.GetType().GetProperty(name).GetValue(target,null));}
        void Set(object target,string name,object value)
        {
            if(relinquished.Contains(name))return;
            var p=target.GetType().GetProperty(name);if(p==null||!p.CanRead||!p.CanWrite)return;
            var old=p.GetValue(target,null);Lease lease;
            if(leases.TryGetValue(name,out lease)&&!Equals(old,lease.Owned)){leases.Remove(name);relinquished.Add(name);return;}
            if(p.PropertyType.IsEnum)value=Enum.Parse(p.PropertyType,(string)value);
            else value=Convert.ChangeType(value,p.PropertyType);
            if(Equals(old,value))return;
            if(lease==null)lease=new Lease{Property=p,Original=old};
            p.SetValue(target,value,null);lease.Owned=value;leases[name]=lease;
        }
        void ReleaseFit(object target)
        {
            Lease fit;
            if(leases.TryGetValue("fontSize",out fit))
            {
                if(Equals(fit.Property.GetValue(target,null),fit.Owned))
                {fit.Property.SetValue(target,fit.Original,null);if(!Equals(fit.Property.GetValue(target,null),fit.Original))return;}
                else relinquished.Add("fontSize");
                leases.Remove("fontSize");
            }
            fittedLabel=false;
        }
        internal void Prepare(object target,string original,string translated,bool paragraphHint=false)
        {
            if(translated==null||!ContainsCjk(translated)){Restore(target);return;}
            try
            {
                if(source!=original){Restore(target);source=original;relinquished.Clear();}
                var type=target.GetType();var rectTransform=type.GetProperty("rectTransform");if(rectTransform==null)return;
                var tr=rectTransform.GetValue(target,null);var rect=tr.GetType().GetProperty("rect").GetValue(tr,null);
                double height=Convert.ToDouble(rect.GetType().GetProperty("height").GetValue(rect,null));
                double width=Convert.ToDouble(rect.GetType().GetProperty("width").GetValue(rect,null));
                var sizeProperty=type.GetProperty("fontSize");if(sizeProperty==null)return;
                double size=OriginalNumber(target,"fontSize");if(size<=0)return;
                bool paragraph=!CompactLabel(original,paragraphHint);
                bool legacy=type.GetProperty("horizontalOverflow")!=null;
                if(legacy)
                {
                    // A preferred-size layout owns the rectangle, not the other
                    // way around. Fitting text back into its previous allocation
                    // creates feedback and shrinks bubbles/tabs on reuse.
                    int contentAxes=ContentAxes(target);
                    bool contentWidth=(contentAxes&1)!=0,contentHeight=(contentAxes&2)!=0;
                    // The render projection retains source preferred sizes.
                    // Fit inside that stable allocation, including a content
                    // fitter's original allocation, instead of moving its UI.
                    if(Region.Ready)
                    {
                        if(contentWidth)width=Region.PreferredSize(target,true);
                        if(contentHeight)height=Region.PreferredSize(target,false);
                        contentWidth=contentHeight=false;
                        if(fitText!=translated||fitWidth!=width||fitHeight!=height||fitRevision!=Region.Revision)
                        {
                            ReleaseFit(target);size=OriginalNumber(target,"fontSize");
                            fitText=translated;fitWidth=width;fitHeight=height;fitRevision=Region.Revision;
                        }
                    }
                    // A pooled control can be prepared before its layout group
                    // becomes active. Once that axis is content-driven, release
                    // any earlier fixed-rectangle fit instead of retaining the
                    // small size on every subsequent opening.
                    if(fittedLabel&&(contentWidth||(paragraph&&contentHeight)))
                    {
                        ReleaseFit(target);size=OriginalNumber(target,"fontSize");
                    }
                    // Keep the authored baseline/alignment. Geometry alignment
                    // changes native truncation decisions for tiny text anchors.
                    // Pixel-font line spacing can be far below a CJK line. Keep
                    // larger native spacing, and prevent translated lines colliding.
                    var spacing=type.GetProperty("lineSpacing");
                    if(spacing!=null&&Convert.ToDouble(spacing.GetValue(target,null))<1)Set(target,"lineSpacing",1f);
                    string unflowed=translated;
                    translated=Region.Flow(target,translated,Convert.ToInt32(sizeProperty.GetValue(target,null)),width);
                    // A zero-width text anchor is not a paragraph column. Latin
                    // words overflow it as a unit, but CJK wraps at every glyph.
                    // Keep explicit newlines while avoiding a vertical stack.
                    if(width<=0||contentWidth)Set(target,"horizontalOverflow","Overflow");
                    else if(paragraph)
                    {
                        Set(target,"horizontalOverflow","Wrap");
                        height=Region.AvailableHeight(target,width,height);
                        // Correct font metrics first, then fit a wrapped paragraph
                        // inside its own authored rectangle. Never resize the UI.
                        double unused,demand;
                        if(!contentHeight&&!fittedLabel&&width>0&&height>=size&&Measure(target,translated,size,out unused,out demand,width)&&demand>height)
                        {
                            double lo=Math.Max(1,Math.Floor(size*0.75)),hi=size;
                            for(int i=0;i<7;i++){double mid=Math.Floor((lo+hi)/2);if(mid<=lo)break;
                                if(Measure(target,Region.Flow(target,unflowed,(int)mid,width),mid,out unused,out demand,width)&&demand<=height)lo=mid;else hi=mid;}
                            Set(target,"fontSize",(int)lo);fittedLabel=true;
                        }
                    }
                    else if(height<size*2)
                    {
                        Set(target,"horizontalOverflow","Overflow");
                        // Short single-line labels must fit the existing button.
                        // Use Unity's own layout metrics when present. Never apply
                        // this shrinking rule to a dialogue paragraph.
                        double measuredWidth,measuredHeight;
                        if(!fittedLabel&&Measure(target,translated,size,out measuredWidth,out measuredHeight)&&width>0)
                        {
                            // A ContentSizeFitter can shrink its rectangle after
                            // our first adjustment. Never chase that derived size
                            // every frame, or a label would shrink recursively.
                            // Font cap-height was matched before this pass.
                            // A pixel-font label may have a tiny authored rect
                            // while its typographic line box is much taller.
                            // Fitting that line box again makes readable names
                            // and tabs miniature. Only excess width shrinks here.
                            double ratio=measuredWidth<=width+1?1:Math.Min(1,width/Math.Max(1,measuredWidth));
                            int fitted=Math.Max((int)Math.Ceiling(size*0.6),Math.Max(1,(int)Math.Floor(size*ratio)));
                            fittedLabel=true;
                            if(fitted<size)Set(target,"fontSize",fitted);
                        }
                    }
                    // A content-sized height must not reject glyphs using the
                    // preceding allocation. Likewise, a single-line label can
                    // have an authored rect shorter than its typographic line.
                    // Preserve paragraph clipping and all parent masks/rects.
                    if(contentHeight)Set(target,"verticalOverflow","Overflow");
                    else if(!paragraph&&original.IndexOf('\n')<0&&translated.IndexOf('\n')<0&&height>=0)
                    {
                        // Preferred height measures glyph bounds, not the native
                        // first-line admission test. A CJK line box can exceed a
                        // fixed pixel-font row even when those bounds fit, making
                        // the entire label disappear. Single-line labels already
                        // retain their authored rect and surrounding masks.
                        Set(target,"verticalOverflow","Overflow");
                    }
                    if(!paragraph&&original.IndexOf('\n')<0&&translated.IndexOf('\n')<0)
                    {
                        // Preferred height is based on the visible glyph bounds.
                        // Align those same bounds when the layout owns height;
                        // a fallback font's larger ascent otherwise places the
                        // bottom strokes outside a tightly content-sized row.
                        // Fixed top/bottom baselines remain authored. Rectangles,
                        // parent masks and the anchor direction stay unchanged.
                        var alignment=type.GetProperty("alignment");
                        string anchor=alignment==null?null:Convert.ToString(alignment.GetValue(target,null));
                        if(height>0&&anchor!=null&&((contentAxes&2)!=0||anchor.StartsWith("Middle",StringComparison.Ordinal)))Set(target,"alignByGeometry",true);
                    }
                    var best=type.GetProperty("resizeTextForBestFit");
                    if((contentHeight||height>0)&&best!=null&&(bool)best.GetValue(target,null))
                    {
                        // Do not let a replacement font enlarge a label past its
                        // original nominal size, even if a large max was authored.
                        int ceiling=Math.Max(1,(int)(contentHeight?size:Math.Min(size,height)));
                        var max=type.GetProperty("resizeTextMaxSize");
                        if(max!=null&&Convert.ToInt32(max.GetValue(target,null))>ceiling)Set(target,"resizeTextMaxSize",ceiling);
                        var min=type.GetProperty("resizeTextMinSize");
                        if(min!=null&&Convert.ToInt32(min.GetValue(target,null))>ceiling)Set(target,"resizeTextMinSize",ceiling);
                    }
                    DisplayText=Region.Flow(target,unflowed,Convert.ToInt32(sizeProperty.GetValue(target,null)),width);
                    Region.Align(target,DisplayText);
                }
                else
                {
                    // Page/Linked overflow owns pagination across containers.
                    // Fitting the complete translated buffer into one rectangle
                    // changes page boundaries and can expose later dialogue.
                    // Preserve that authored flow and restore any earlier fit
                    // when a pooled control switches into one of these modes.
                    var overflow=type.GetProperty("overflowMode");
                    string overflowMode=overflow==null?null:Convert.ToString(overflow.GetValue(target,null));
                    if(overflowMode=="Page"||overflowMode=="Linked")
                    {Restore(target);return;}
                    if(Flag(target,"enableAutoSizing",false)||Flag(target,"autoSizeTextContainer",false)||ContentAxes(target)!=0)
                    {
                        ReleaseFit(target);fitText=null;fitReviewAt=0;
                        if(leases.ContainsKey("fontSize"))return;
                    }
                    // A complete SetText buffer can still be a one-line label.
                    // Do not force every complete buffer into paragraph wrapping.
                    if((original??"").Length>=80||(original??"").IndexOf('\n')>=0)Set(target,"enableWordWrapping",true);
                    var auto=type.GetProperty("enableAutoSizing");
                    if(auto!=null&&(bool)auto.GetValue(target,null))
                    {
                        double ceiling=Math.Min(size,OriginalNumber(target,"fontSizeMax"));
                        Set(target,"fontSizeMax",(float)ceiling);
                        if(OriginalNumber(target,"fontSizeMin")>ceiling)Set(target,"fontSizeMin",(float)ceiling);
                    }
                    else if(width>1&&height>1&&IsTmpUi(type)&&!Flag(target,"autoSizeTextContainer",false)&&ContentAxes(target)==0)
                    {
                        // A replacement face can have a taller line box even
                        // when the translated sentence fits horizontally. Fit
                        // the glyph line into this fixed allocation; never grow
                        // its RectTransform or alter a neighbouring hit area.
                        var measure=type.GetMethod("GetPreferredValues",new[]{typeof(string),typeof(float),typeof(float)});
                        var fontProperty=type.GetProperty("font");object font=fontProperty==null?null:fontProperty.GetValue(target,null);
                        bool changed=fitText!=translated||fitWidth!=width||fitHeight!=height||!ReferenceEquals(fitFont==null?null:fitFont.Target,font);
                        long now=System.Diagnostics.Stopwatch.GetTimestamp();
                        double presentSize=Convert.ToDouble(sizeProperty.GetValue(target,null));
                        // Revalidate metrics on a paced review even if the text
                        // and rect stayed unchanged. A stable review must not
                        // restore then shrink the same label on every call.
                        if(measure!=null&&!relinquished.Contains("fontSize")&&(changed||presentSize!=fitMeasuredSize||now>=fitReviewAt))
                        {
                            if(changed)ReleaseFit(target);size=OriginalNumber(target,"fontSize");
                            fitText=translated;fitWidth=width;fitHeight=height;
                            fitFont=font==null?null:new WeakReference(font);fitReviewAt=now+System.Diagnostics.Stopwatch.Frequency/2;
                            for(int attempt=0;attempt<3;attempt++)
                            {
                                var bounds=measure.Invoke(target,new object[]{translated,(float)width,1000000f});
                                var x=bounds.GetType().GetField("x");var y=bounds.GetType().GetField("y");if(x==null||y==null)break;
                                double wide=Convert.ToDouble(x.GetValue(bounds)),tall=Convert.ToDouble(y.GetValue(bounds));
                                if(double.IsNaN(wide)||double.IsInfinity(wide)||wide<0||double.IsNaN(tall)||double.IsInfinity(tall)||tall<=0)break;
                                // Older TMP versions accumulate the advances of
                                // wrapped lines in preferredWidth. That is not
                                // the width of the widest rendered line. Only
                                // disregard it when a second, wider measurement
                                // proves this paragraph actually gained lines.
                                // Non-breaking runs and compact labels still
                                // have to fit horizontally.
                                double horizontalRatio=wide/width;
                                if(horizontalRatio>1&&paragraph&&Flag(target,"enableWordWrapping",false)&&width<1000000)
                                {
                                    var unwrapped=measure.Invoke(target,new object[]{translated,1000000f,1000000f});
                                    double naturalHeight=Convert.ToDouble(y.GetValue(unwrapped));
                                    if(!double.IsNaN(naturalHeight)&&!double.IsInfinity(naturalHeight)&&naturalHeight>0&&tall>naturalHeight+.01)
                                        horizontalRatio=1;
                                }
                                double ratio=Math.Max(horizontalRatio,tall*1.05/height);if(ratio<=1)break;
                                double currentSize=Convert.ToDouble(sizeProperty.GetValue(target,null));
                                double next=Math.Max(Math.Min(8,size),currentSize*Math.Min(.95,1/ratio));
                                if(next>=currentSize-.01)break;
                                Set(target,"fontSize",(float)next);if(Math.Abs(Convert.ToDouble(sizeProperty.GetValue(target,null))-next)>.01)break;fittedLabel=true;
                            }
                            fitMeasuredSize=Convert.ToDouble(sizeProperty.GetValue(target,null));
                        }
                    }
                }
            }catch{}
        }
        static bool IsTmpUi(Type type)
        {for(int depth=0;type!=null&&depth<32;type=type.BaseType,depth++)if(type.FullName=="TMPro.TextMeshProUGUI")return true;return false;}
        // Follow only Unity's documented layout components, at most eight
        // parents. Never inspect game controllers, object names or scene paths.
        // A fixed allocation or explicit LayoutElement stops that axis.
        internal static int ContentAxes(object target)
        {
            try
            {
                var p=target.GetType().GetProperty("rectTransform");
                object node=p==null?null:p.GetValue(target,null);
                int open=3,found=0;
                for(int depth=0;node!=null&&open!=0&&depth<8;depth++)
                {
                    object element=Component(node,"UnityEngine.UI.LayoutElement");
                    if(element!=null)
                    {
                        if(Flag(element,"ignoreLayout",false))break;
                        if(Number(element,"preferredWidth",-1)>=0)open&=~1;
                        if(Number(element,"preferredHeight",-1)>=0)open&=~2;
                    }
                    object fitter=Component(node,"UnityEngine.UI.ContentSizeFitter");
                    if(fitter!=null)
                    {
                        if(Number(fitter,"horizontalFit",0)>0)found|=open&1;
                        if(Number(fitter,"verticalFit",0)>0)found|=open&2;
                        open&=~found;
                    }
                    var parent=node.GetType().GetProperty("parent");
                    object next=parent==null?null:parent.GetValue(node,null);if(next==null||ReferenceEquals(next,node))break;
                    object group=Component(next,"UnityEngine.UI.HorizontalLayoutGroup")??Component(next,"UnityEngine.UI.VerticalLayoutGroup");
                    if(group==null)break;
                    if(!Flag(group,"childControlWidth",true))open&=~1;
                    if(!Flag(group,"childControlHeight",true))open&=~2;
                    node=next;
                }
                return found;
            }catch{return 0;}
        }
        static object Component(object node,string name)
        {
            Type type;if(!layoutTypes.TryGetValue(name,out type)){type=UnityBridgeRuntime.FindType(name);if(type==null)return null;layoutTypes[name]=type;}
            var method=node.GetType().GetMethod("GetComponent",new[]{typeof(Type)});
            if(method==null)return null;
            object component=method.Invoke(node,new object[]{type});if(component==null)return null;
            // Inactive pooled UI is prepared before OnEnable. Its enabled
            // layout still owns the next allocation; treating it as fixed here
            // shrinks text once, then expands it again on the first activation.
            // Explicitly disabled components never acquire that ownership.
            var enabled=type.GetProperty("enabled")??type.GetProperty("isActiveAndEnabled");
            if(enabled!=null&&enabled.PropertyType==typeof(bool)&&!(bool)enabled.GetValue(component,null))return null;
            return component;
        }
        static bool Flag(object value,string name,bool fallback)
        {var p=value.GetType().GetProperty(name);return p==null?fallback:Convert.ToBoolean(p.GetValue(value,null));}
        static double Number(object value,string name,double fallback)
        {var p=value.GetType().GetProperty(name);return p==null?fallback:Convert.ToDouble(p.GetValue(value,null));}
        static bool Measure(object target,string text,double size,out double width,out double height,double wrapWidth=0)
        {
            width=height=0;
            try
            {
                var type=target.GetType();var settingsMethod=type.GetMethod("GetGenerationSettings");
                var generatorProperty=type.GetProperty("cachedTextGeneratorForLayout");
                if(settingsMethod==null||generatorProperty==null)return false;
                object extent=Activator.CreateInstance(settingsMethod.GetParameters()[0].ParameterType);
                object settings=settingsMethod.Invoke(target,new[]{extent});var st=settings.GetType();
                st.GetField("fontSize").SetValue(settings,(int)size);st.GetField("resizeTextForBestFit").SetValue(settings,false);
                if(wrapWidth>0)
                {
                    var extentField=st.GetField("generationExtents");object value=extentField.GetValue(settings);
                    value.GetType().GetField("x").SetValue(value,(float)wrapWidth);extentField.SetValue(settings,value);
                    var flow=st.GetField("horizontalOverflow");flow.SetValue(settings,Enum.Parse(flow.FieldType,"Wrap"));
                }
                object generator=generatorProperty.GetValue(target,null);var gt=generator.GetType();
                double scale=Convert.ToDouble(type.GetProperty("pixelsPerUnit").GetValue(target,null));if(scale<=0)return false;
                width=Convert.ToDouble(gt.GetMethod("GetPreferredWidth").Invoke(generator,new[]{(object)text,settings}))/scale;
                height=Convert.ToDouble(gt.GetMethod("GetPreferredHeight").Invoke(generator,new[]{(object)text,settings}))/scale;
                return width>0&&height>0&&!double.IsInfinity(width)&&!double.IsInfinity(height);
            }catch{return false;}
        }
        static bool ContainsCjk(string value){foreach(char c in value)if(c>=0x2e80&&c<=0x9fff)return true;return false;}
        internal static string PreserveIndent(string original,string translated,double nativeSpace,double displaySpace)
        {
            if(string.IsNullOrEmpty(original)||string.IsNullOrEmpty(translated)||!ContainsCjk(translated)||nativeSpace<=0||displaySpace<=0)return translated;
            int count=0;while(count<original.Length&&original[count]==' ')count++;
            if(count<2||count>128)return translated;
            double wanted=Math.Ceiling(count*nativeSpace/displaySpace);
            if(double.IsNaN(wanted)||double.IsInfinity(wanted)||wanted<1||wanted>256)return translated;
            // Leading repeated spaces can reserve room for a neighbouring label.
            // Preserve their measured advance after font matching. The source,
            // cache and paragraph content stay unchanged; this is display only.
            return new string(' ',(int)wanted)+translated.TrimStart(' ');
        }
        internal static bool CompactLabel(string value,bool paragraphHint)
        {
            if(paragraphHint||string.IsNullOrEmpty(value)||value.Length>48)return false;
            int lines=1;foreach(char c in value)if(c=='\n'&&++lines>3)return false;
            return true;
        }
        internal bool RestorePending{get{return leases.Count>0;}}
        internal void Restore(object target)
        {
            foreach(var name in new List<string>(leases.Keys))
            {
                var lease=leases[name];
                try{if(Equals(lease.Property.GetValue(target,null),lease.Owned)){lease.Property.SetValue(target,lease.Original,null);if(!Equals(lease.Property.GetValue(target,null),lease.Original))continue;}leases.Remove(name);}catch{}
            }
            source=fitText=null;fittedLabel=false;fitRevision=0;
            fitFont=null;fitMeasuredSize=0;fitReviewAt=0;
            DisplayText=null;Region.Clear();
        }
    }
}
