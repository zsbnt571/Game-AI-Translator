using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Fusion.UnityEmbedded
{
    // Preserve the engine's source layout inputs while projecting a translation.
    // Only documented UI.Text generation data is read; no sibling/game traversal.
    internal sealed class LegacyTextRegion
    {
        object nativeSettings; double nativeScale,measuredWidth=-1,naturalWidth,naturalHeight;
        string source,flowInput,flowOutput; int flowSize; double flowWidth;
        double narrowWidth,narrowHeight,offsetX,offsetY; bool profileChecked;
        object alignedSettings;string alignedText;double alignedScale,alignedWidth,alignedHeight;
        Box originalBounds;bool boundsChecked;double boundsWidth,boundsHeight;
        internal bool Ready {get{return nativeSettings!=null;}}
        internal int Revision {get;private set;}
        internal string Status {get;private set;}
        internal double OffsetY {get{return offsetY;}}
        static object Copy(object value)
        {var type=value.GetType();var copy=Activator.CreateInstance(type);foreach(var f in type.GetFields(BindingFlags.Public|BindingFlags.Instance))f.SetValue(copy,f.GetValue(value));return copy;}
        static double Number(object value,string name)
        {var t=value.GetType();var f=t.GetField(name);var p=t.GetProperty(name);return Convert.ToDouble(f!=null?f.GetValue(value):p.GetValue(value,null));}
        static void Field(object value,string name,object data)
        {var f=value.GetType().GetField(name);if(f==null)return;f.SetValue(value,f.FieldType.IsEnum?Enum.Parse(f.FieldType,Convert.ToString(data)):Convert.ChangeType(data,f.FieldType));}
        static object Settings(object target,double width,double height)
        {var m=target.GetType().GetMethod("GetGenerationSettings");var ext=Activator.CreateInstance(m.GetParameters()[0].ParameterType);Field(ext,"x",width);Field(ext,"y",height);return m.Invoke(target,new[]{ext});}
        static object Extent(object settings,double width,double height)
        {var copy=Copy(settings);var field=copy.GetType().GetField("generationExtents");var ext=field.GetValue(copy);Field(ext,"x",width);Field(ext,"y",height);field.SetValue(copy,ext);return copy;}
        static object Generator(object target){return target.GetType().GetProperty("cachedTextGeneratorForLayout").GetValue(target,null);}
        static double Preferred(object generator,string value,object settings,bool width,double scale)
        {return Convert.ToDouble(generator.GetType().GetMethod(width?"GetPreferredWidth":"GetPreferredHeight").Invoke(generator,new[]{(object)value,settings}))/scale;}
        internal void Capture(object target,string value)
        {
            if(string.IsNullOrEmpty(value)||value.Length>6000)return;
            try
            {
                var rect=target.GetType().GetProperty("rectTransform").GetValue(target,null);rect=rect.GetType().GetProperty("rect").GetValue(rect,null);
                var settings=Settings(target,Number(rect,"width"),0);
                if(Ready)
                {
                    // Values changed by the game after our previous preparation
                    // become authoritative. Retain only fields we still own.
                    if(alignedSettings!=null)
                    {
                        bool changed=false;
                        foreach(string name in new[]{"font","fontSize","fontStyle","lineSpacing","textAnchor","richText","resizeTextForBestFit","resizeTextMinSize","resizeTextMaxSize","horizontalOverflow","verticalOverflow","alignByGeometry","pivot"})
                        {
                            var field=settings.GetType().GetField(name);if(field==null)continue;
                            var now=field.GetValue(settings);
                            if(Equals(now,field.GetValue(alignedSettings)))continue;
                            field.SetValue(nativeSettings,now);changed=true;
                        }
                        double scale=Number(target,"pixelsPerUnit");
                        if(scale>0&&alignedScale>0&&scale!=alignedScale)
                        {nativeScale*=scale/alignedScale;Field(nativeSettings,"scaleFactor",nativeScale);changed=true;}
                        if(changed){measuredWidth=-1;boundsChecked=false;alignedSettings=null;alignedText=null;}
                    }
                    UpdatePreferred(target,Number(rect,"width"));return;
                }
                // Stripped/older generators without the necessary fields keep
                // the previous path, never substitute guessed font metrics.
                if(settings.GetType().GetField("font")==null||settings.GetType().GetField("pivot")==null)return;
                nativeScale=Number(target,"pixelsPerUnit");if(nativeScale<=0)return;
                source=value;nativeSettings=Copy(settings);Status="source-layout";
                UpdatePreferred(target,Number(rect,"width"));
            }catch{Clear();}
        }
        void UpdatePreferred(object target,double width)
        {
            if(width==measuredWidth)return;
            var generator=Generator(target);var settings=Extent(nativeSettings,width,0);
            naturalWidth=Preferred(generator,source,settings,true,nativeScale);
            naturalHeight=Preferred(generator,source,settings,false,nativeScale);
            if(naturalWidth<=0||naturalHeight<=0||double.IsNaN(naturalWidth)||double.IsNaN(naturalHeight)||double.IsInfinity(naturalWidth)||double.IsInfinity(naturalHeight))throw new InvalidOperationException();
            measuredWidth=width;profileChecked=false;flowInput=null;boundsChecked=false;Status="source-layout";Revision++;
        }
        internal float PreferredSize(object target,bool horizontal)
        {
            if(!Ready)return -1;
            try{var tr=target.GetType().GetProperty("rectTransform").GetValue(target,null);var rect=tr.GetType().GetProperty("rect").GetValue(tr,null);UpdatePreferred(target,Number(rect,"width"));return (float)(horizontal?naturalWidth:naturalHeight);}catch{return -1;}
        }
        static double LineHeight(object settings)
        {
            var font=settings.GetType().GetField("font").GetValue(settings);
            double points=Number(font,"fontSize"),size=Number(settings,"fontSize"),height=Number(font,"lineHeight");
            return points>0?height*size/points*Number(settings,"lineSpacing"):0;
        }
        void Profile(object target,double width)
        {
            profileChecked=true;narrowWidth=narrowHeight=0;
            if(source.Length<160||width<=0||source.IndexOfAny(new[]{'<','[','\\','|','\t'})>=0)return;
            var lines=source.Replace("\r\n","\n").Replace('\r','\n').Split('\n');
            if(lines.Length<4||lines.Length>64)return;
            var generator=Generator(target);double largest=0;int prefix=0;
            for(int i=0;i<lines.Length;i++)
            {
                string line=lines[i];
                if(line.Trim().Length<16||line.StartsWith("  ")||Regex.IsMatch(line,@"^\s*(?:[-+*•]|\d+[.)])"))return;
                double used=Preferred(generator,line,nativeSettings,true,nativeScale);
                if(used>width*.85)
                {
                    // Repeated short authored rows followed by a full-width
                    // continuation encode a reserved side region (e.g. artwork).
                    if(prefix>=3&&largest<width*.8)
                    {narrowWidth=Math.Min(width,largest+2);narrowHeight=prefix*LineHeight(nativeSettings);Status="authored-line-region";}
                    return;
                }
                largest=Math.Max(largest,used);prefix++;
            }
        }
        static bool Cjk(char c){return c>=0x2e80&&c<=0x9fff;}
        internal static string JoinProse(string text)
        {return Regex.Replace(text,@"[ \t]*\r?\n[ \t]*|[ \t]*\r[ \t]*",m=>m.Index>0&&m.Index+m.Length<text.Length&&Cjk(text[m.Index-1])&&Cjk(text[m.Index+m.Length])?"":" ");}
        static bool Advances(object settings,string text,double scale,Dictionary<char,double> widths)
        {
            try
            {
                var font=settings.GetType().GetField("font").GetValue(settings);var ft=font.GetType();
                var dynamic=ft.GetProperty("dynamic");if(dynamic==null||!(bool)dynamic.GetValue(font,null))return false;
                var info=UnityBridgeRuntime.FindType("UnityEngine.CharacterInfo");var styleField=settings.GetType().GetField("fontStyle");
                if(info==null||styleField==null)return false;object style=styleField.GetValue(settings);
                var method=ft.GetMethod("GetCharacterInfo",new[]{typeof(char),info.MakeByRefType(),typeof(int),style.GetType()});
                var request=ft.GetMethod("RequestCharactersInTexture",new[]{typeof(string),typeof(int),style.GetType()});var advance=info.GetField("advance");var advanceProperty=info.GetProperty("advance");
                if(method==null||request==null||(advance==null&&advanceProperty==null))return false;
                int size=Math.Max(1,(int)Math.Round(Number(settings,"fontSize")*scale));
                request.Invoke(font,new object[]{text,size,style});
                foreach(char c in text)if(!widths.ContainsKey(c))
                {
                    var args=new object[]{c,Activator.CreateInstance(info),size,style};
                    if(!(bool)method.Invoke(font,args))return false;
                    widths[c]=Math.Max(0,Convert.ToDouble(advance!=null?advance.GetValue(args[1]):advanceProperty.GetValue(args[1],null))/scale);
                }
                return true;
            }catch{return false;}
        }
        internal string Flow(object target,string text,int size,double width)
        {
            if(!Ready||width<=0||text.Length>6000)return text;
            try
            {
                if(!profileChecked||measuredWidth!=width){UpdatePreferred(target,width);Profile(target,width);}
                if(narrowWidth<=0||narrowHeight<=0||text.IndexOfAny(new[]{'<','[','\\','|','\t'})>=0||text.Contains("\n\n"))return text;
                if(flowInput==text&&flowSize==size&&flowWidth==width)return flowOutput;
                var settings=Settings(target,width,0);Field(settings,"fontSize",size);Field(settings,"resizeTextForBestFit",false);
                double step=LineHeight(settings),scale=Number(target,"pixelsPerUnit");if(step<=0)return text;
                var advances=new Dictionary<char,double>();string flat=JoinProse(text);
                foreach(char c in flat)if(char.IsSurrogate(c))return text;
                // Query the already warmed dynamic font's advances in one pass;
                // do not repopulate a TextGenerator for every individual glyph.
                if(!Advances(settings,flat,scale,advances))
                {advances.Clear();var generator=Generator(target);foreach(char c in flat)if(!advances.ContainsKey(c))advances[c]=Math.Max(0,Preferred(generator,c.ToString(),settings,true,scale));}
                string result=FlowText(flat,width,narrowWidth,narrowHeight,step,advances);
                flowInput=text;flowSize=size;flowWidth=width;flowOutput=result;return result;
            }catch{return text;}
        }
        internal static string FlowText(string text,double width,double narrow,double reservedHeight,double step,Dictionary<char,double> advances)
        {
            var output=new StringBuilder();int start=0,line=0;
            while(start<text.Length&&line<256)
            {
                double limit=(line*step<reservedHeight?narrow:width)-1,used=0;int end=start,lastSpace=-1;
                while(end<text.Length){double advance; if(!advances.TryGetValue(text[end],out advance))return text;if(used+advance>limit&&end>start)break;used+=advance;if(text[end]==' ')lastSpace=end;end++;}
                if(end<text.Length&&end>start+1)
                {
                    if(char.IsLetterOrDigit(text[end])&&text[end]<256&&char.IsLetterOrDigit(text[end-1])&&text[end-1]<256&&lastSpace>start)end=lastSpace;
                    if("，。！？、；：)]}”’》」』".IndexOf(text[end])>=0)end--;
                }
                if(end<=start)return text;
                if(output.Length>0)output.Append('\n');output.Append(text.Substring(start,end-start).TrimEnd(' '));start=end;while(start<text.Length&&text[start]==' ')start++;line++;
            }
            return start==text.Length?output.ToString():text;
        }
        struct Box {internal double Left,Right,Bottom,Top;internal bool Valid;}
        Box OriginalBounds(object target,double width,double height)
        {
            if(boundsChecked&&boundsWidth==width&&boundsHeight==height)return originalBounds;
            boundsChecked=true;boundsWidth=width;boundsHeight=height;
            originalBounds=Bounds(Generator(target),source,Extent(nativeSettings,width,height),nativeScale);return originalBounds;
        }
        internal double AvailableHeight(object target,double width,double height)
        {
            if(!Ready||width<=0||height<=0)return height;
            try
            {
                var box=OriginalBounds(target,width,height);if(!box.Valid)return height;
                double pivot=Number(nativeSettings.GetType().GetField("pivot").GetValue(nativeSettings),"y");
                string anchor=Convert.ToString(nativeSettings.GetType().GetField("textAnchor").GetValue(nativeSettings));
                double inset=anchor.StartsWith("Upper")?(1-pivot)*height-box.Top:
                    anchor.StartsWith("Lower")?box.Bottom+pivot*height:2*Math.Abs((box.Top+box.Bottom)/2-(.5-pivot)*height);
                // Keep the native ink anchor's inset when fitting a paragraph;
                // otherwise aligning the first line can push its last line out.
                return inset>=0&&inset<height?height-inset:height;
            }catch{return height;}
        }
        static Box Bounds(object generator,string text,object settings,double scale)
        {
            var populate=generator.GetType().GetMethod("Populate",new[]{typeof(string),settings.GetType()});
            if(populate==null||!(bool)populate.Invoke(generator,new[]{(object)text,settings}))return new Box();
            var verts=generator.GetType().GetProperty("verts").GetValue(generator,null) as IList;
            var box=new Box();if(verts==null||verts.Count>24004)return box;
            if(verts.Count<4)return box;
            var position=verts[0].GetType().GetField("position");var vector=position.FieldType;var xf=vector.GetField("x");var yf=vector.GetField("y");
            for(int i=0;i+3<verts.Count;i+=4)
            {
                double l=double.MaxValue,r=double.MinValue,b=l,t=r;
                for(int j=0;j<4;j++){var p=position.GetValue(verts[i+j]);double x=Convert.ToDouble(xf.GetValue(p))/scale,y=Convert.ToDouble(yf.GetValue(p))/scale;l=Math.Min(l,x);r=Math.Max(r,x);b=Math.Min(b,y);t=Math.Max(t,y);}
                if(r-l<.01||t-b<.01)continue;
                if(!box.Valid){box=new Box{Left=l,Right=r,Bottom=b,Top=t,Valid=true};}
                else{box.Left=Math.Min(box.Left,l);box.Right=Math.Max(box.Right,r);box.Bottom=Math.Min(box.Bottom,b);box.Top=Math.Max(box.Top,t);}
            }
            return box;
        }
        internal void Align(object target,string text)
        {
            if(!Ready||text.Length>6000)return;
            try
            {
                var tr=target.GetType().GetProperty("rectTransform").GetValue(target,null);var rect=tr.GetType().GetProperty("rect").GetValue(tr,null);double width=Number(rect,"width"),height=Number(rect,"height");
                if(width<=0||height<=0){offsetX=offsetY=0;return;}
                var translated=Settings(target,width,height);double scale=Number(target,"pixelsPerUnit");
                // Mesh metrics are expensive. A refresh with unchanged text and
                // generation inputs must reuse the result, including failures.
                if(alignedText==text&&alignedWidth==width&&alignedHeight==height&&alignedScale==scale&&Equals(alignedSettings,translated))return;
                alignedText=text;alignedWidth=width;alignedHeight=height;alignedScale=scale;alignedSettings=Copy(translated);offsetX=offsetY=0;
                var generator=Generator(target);
                // Layout-only text generation does not change game properties or
                // display meshes. Compare actual ink anchors, not nominal sizes.
                var a=OriginalBounds(target,width,height);var b=Bounds(generator,text,translated,scale);
                if(!a.Valid||!b.Valid)return;
                string anchor=Convert.ToString(nativeSettings.GetType().GetField("textAnchor").GetValue(nativeSettings));
                offsetX=anchor.EndsWith("Left")?a.Left-b.Left:anchor.EndsWith("Right")?a.Right-b.Right:(a.Left+a.Right-b.Left-b.Right)/2;
                offsetY=anchor.StartsWith("Upper")?a.Top-b.Top:anchor.StartsWith("Lower")?a.Bottom-b.Bottom:(a.Top+a.Bottom-b.Top-b.Bottom)/2;
                if(Math.Abs(offsetX)>width||Math.Abs(offsetY)>height){offsetX=offsetY=0;}
            }catch{offsetX=offsetY=0;}
        }
        internal object Project(object settings)
        {
            if(!Ready||(offsetX==0&&offsetY==0))return settings;
            try{var ext=settings.GetType().GetField("generationExtents").GetValue(settings);double width=Number(ext,"x"),height=Number(ext,"y");if(width<=0||height<=0)return settings;var p=settings.GetType().GetField("pivot");var pivot=p.GetValue(settings);Field(pivot,"x",Number(pivot,"x")-offsetX/width);Field(pivot,"y",Number(pivot,"y")-offsetY/height);p.SetValue(settings,pivot);}catch{}
            return settings;
        }
        internal void Clear(){nativeSettings=alignedSettings=null;source=alignedText=null;measuredWidth=-1;profileChecked=boundsChecked=false;flowInput=flowOutput=null;offsetX=offsetY=0;Status=null;Revision=0;}
    }
}
