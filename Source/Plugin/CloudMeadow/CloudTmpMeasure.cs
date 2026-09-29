using System;
using TMPro;
using UnityEngine;

namespace Fusion.CloudMeadow
{
    // The game's own legacy GetTextInfo performs wrapping and exposes completed
    // character/line geometry in DontRender mode. It runs on a private disabled
    // label, never on the game's reveal/parser component or its animation mesh.
    internal sealed class CloudTmpMeasure : IDisposable
    {
        GameObject root;TextMeshProUGUI label;
        internal bool Owns(Component c){return c!=null&&c==label;}
        internal sealed class Result
        {
            internal bool Available,Fits;internal string Reason;
            internal int Lines,Characters;internal float MaxLineWidth,Height;
            internal Rect Ink,LineBox;internal float PointSize,PreferredWidth,PreferredHeight;
        }
        void Create()
        {
            if(label!=null)return;
            root=new GameObject("Fusion text measurement",typeof(RectTransform),typeof(Canvas),typeof(CanvasGroup));
            root.hideFlags=HideFlags.HideAndDontSave;UnityEngine.Object.DontDestroyOnLoad(root);
            root.GetComponent<Canvas>().renderMode=RenderMode.WorldSpace;
            root.GetComponent<CanvasGroup>().alpha=0;
            root.GetComponent<CanvasGroup>().blocksRaycasts=false;
            label=root.AddComponent<TextMeshProUGUI>();label.enabled=false;label.raycastTarget=false;
            label.rectTransform.anchorMin=label.rectTransform.anchorMax=new Vector2(0.5f,0.5f);
            label.rectTransform.pivot=new Vector2(0,1);
        }
        static bool Finite(float f){return !float.IsNaN(f)&&!float.IsInfinity(f);}
        internal Result Read(TMP_Text source,string text,float size,float width,float height)
        {
            var result=new Result{Reason="measurement-unavailable"};
            if(width<=0||height<=0||!Finite(width)||!Finite(height))return result;
            Create();
            label.font=source.font;label.fontSharedMaterial=source.fontSharedMaterial;
            label.fontStyle=source.fontStyle;label.spriteAsset=source.spriteAsset;
            label.richText=source.richText;label.parseCtrlCharacters=source.parseCtrlCharacters;
            label.isRightToLeftText=source.isRightToLeftText;label.isOrthographic=source.isOrthographic;
            label.enableKerning=source.enableKerning;label.extraPadding=source.extraPadding;
            label.characterSpacing=source.characterSpacing;label.wordSpacing=source.wordSpacing;
            label.lineSpacing=source.lineSpacing;label.paragraphSpacing=source.paragraphSpacing;
            label.enableAutoSizing=false;label.enableWordWrapping=source.enableWordWrapping;
            label.overflowMode=TextOverflowModes.Overflow;label.alignment=source.alignment;
            label.fontSize=size;label.margin=Vector4.zero;label.rectTransform.sizeDelta=new Vector2(width,height);
            label.maxVisibleCharacters=int.MaxValue;label.maxVisibleWords=int.MaxValue;label.maxVisibleLines=int.MaxValue;
            // GetTextInfo, unlike GetPreferredValues, supplies individual wrapped lines.
            // Its input buffer belongs exclusively to this hidden, noninteractive label.
            var info=label.GetTextInfo(text);
            if(info==null||info.characterCount<=0||info.lineCount<=0||label.isTextTruncated)return result;
            var left=new float[info.lineCount];var right=new float[info.lineCount];
            for(int i=0;i<left.Length;i++){left[i]=float.PositiveInfinity;right[i]=float.NegativeInfinity;}
            float bottom=float.PositiveInfinity,top=float.NegativeInfinity;bool any=false,inside=true;
            var area=label.rectTransform.rect;
            for(int i=0;i<info.characterCount;i++)
            {
                var ch=info.characterInfo[i];if(!ch.isVisible)continue;
                if(ch.lineNumber>=left.Length)return result;
                // Character quads include transparent SDF shader padding. Treating
                // that halo as a left-edge overflow rejects every TopLeft line.
                // Use the generated advance/ascender range, expanded by glyph ink.
                float l=Mathf.Min(ch.origin,ch.xAdvance),r=Mathf.Max(ch.origin,ch.xAdvance);
                float b=ch.descender,t=ch.ascender;
                TMP_Glyph glyph;
                if(ch.elementType==TMP_TextElementType.Character&&ch.fontAsset!=null&&ch.fontAsset.characterDictionary.TryGetValue(ch.character,out glyph))
                {
                    float gl=ch.origin+glyph.xOffset*ch.scale,gr=gl+glyph.width*ch.scale;
                    if((ch.style&FontStyles.Italic)!=0)
                    {float slant=ch.fontAsset.italicStyle*0.01f;gl+=Mathf.Min(slant*glyph.yOffset,slant*(glyph.yOffset-glyph.height))*ch.scale;gr+=Mathf.Max(slant*glyph.yOffset,slant*(glyph.yOffset-glyph.height))*ch.scale;}
                    l=Mathf.Min(l,gl);r=Mathf.Max(r,gr);
                    float gt=ch.baseLine+(ch.fontAsset.fontInfo.Baseline+glyph.yOffset)*ch.scale;
                    b=Mathf.Min(b,gt-glyph.height*ch.scale);t=Mathf.Max(t,gt);
                }
                else if(ch.elementType==TMP_TextElementType.Sprite)
                {l=Mathf.Min(l,ch.bottomLeft.x);r=Mathf.Max(r,ch.topRight.x);b=Mathf.Min(b,ch.bottomLeft.y);t=Mathf.Max(t,ch.topRight.y);}
                if(!Finite(l)||!Finite(r)||!Finite(b)||!Finite(t))return result;
                left[ch.lineNumber]=Mathf.Min(left[ch.lineNumber],l);right[ch.lineNumber]=Mathf.Max(right[ch.lineNumber],r);
                bottom=Mathf.Min(bottom,b);top=Mathf.Max(top,t);any=true;
                if(l<area.xMin-0.5f||r>area.xMax+0.5f||b<area.yMin-0.5f||t>area.yMax+0.5f)inside=false;
            }
            if(!any)return result;
            for(int i=0;i<info.lineCount;i++)
            {
                if(Finite(left[i])&&Finite(right[i]))result.MaxLineWidth=Mathf.Max(result.MaxLineWidth,right[i]-left[i]);
                // Include line ascenders/descenders, not just ink of currently revealed glyphs.
                var line=info.lineInfo[i];
                if(line.characterCount>0&&Finite(line.ascender)&&Finite(line.descender))
                {top=Mathf.Max(top,line.ascender);bottom=Mathf.Min(bottom,line.descender);}
            }
            result.Available=true;result.Characters=info.characterCount;result.Lines=info.lineCount;result.Height=top-bottom;
            result.Fits=inside&&top<=area.yMax+0.5f&&bottom>=area.yMin-0.5f&&result.MaxLineWidth<=width+0.5f&&result.Height<=height+0.5f;
            result.Reason=result.Fits?"wrapped-lines-fit":"wrapped-lines-exceed-region";
            return result;
        }
        // Identified Quest titles/nameplates, plus the .15 independent quest counter
        // label. Read above remains unchanged for dialogue and other labels.
        internal Result ReadShort(TMP_Text source,string text,float size,float width,float height,TMP_FontAsset font,Material material,bool native,bool multi=false)
        {
            var result=new Result{Reason="resolved-glyph-unavailable"};
            if(width<=0||height<=0||!Finite(width)||!Finite(height)||!Finite(size)||size<=0)return result;
            // These matrix effects invalidate padding inferred from the generated
            // axis-aligned quad. Never pretend that absent metrics mean zero ink.
            if(text.IndexOf("<rotate",StringComparison.OrdinalIgnoreCase)>=0||text.IndexOf("<scale",StringComparison.OrdinalIgnoreCase)>=0)
            {result.Reason="glyph-matrix-unavailable";return result;}
            Create();label.font=font;label.fontSharedMaterial=material;
            label.fontStyle=source.fontStyle;label.spriteAsset=source.spriteAsset;
            label.richText=source.richText;label.parseCtrlCharacters=source.parseCtrlCharacters;
            label.isRightToLeftText=source.isRightToLeftText;label.isOrthographic=source.isOrthographic;
            label.enableKerning=source.enableKerning;label.extraPadding=source.extraPadding;
            label.characterSpacing=source.characterSpacing;label.wordSpacing=source.wordSpacing;
            label.lineSpacing=source.lineSpacing;label.paragraphSpacing=source.paragraphSpacing;
            label.enableAutoSizing=native&&source.enableAutoSizing;
            label.fontSizeMin=source.fontSizeMin;label.fontSizeMax=source.fontSizeMax;
            label.enableWordWrapping=multi;label.overflowMode=TextOverflowModes.Overflow;
            label.alignment=native?source.alignment:TextAlignmentOptions.MidlineGeoAligned;
            label.fontSize=size;label.margin=Vector4.zero;label.rectTransform.sizeDelta=new Vector2(width,height);
            label.maxVisibleCharacters=int.MaxValue;label.maxVisibleWords=int.MaxValue;label.maxVisibleLines=int.MaxValue;
            var info=label.GetTextInfo(text);
            if(info==null||info.characterCount<=0||(!multi&&info.lineCount!=1)||label.isTextTruncated)
            {result.Reason="short-label-not-one-complete-line";return result;}
            float left=float.PositiveInfinity,bottom=left,right=float.NegativeInfinity,top=right;
            bool any=false,overLeft=false,overRight=false,overBottom=false,overTop=false;var area=label.rectTransform.rect;
            for(int i=0;i<info.characterCount;i++)
            {
                var ch=info.characterInfo[i];if(!ch.isVisible)continue;
                float l,r,b,t;
                if(ch.elementType==TMP_TextElementType.Character)
                {
                    // SetArraySizes in the shipped legacy TMP has already resolved
                    // case, weight typeface, local/global fallbacks and substitutes.
                    // textElement is that exact selected glyph, not a dictionary guess.
                    var glyph=ch.textElement;
                    if(glyph==null||ch.fontAsset==null||glyph.id!=ch.character||ch.scale<=0||glyph.height<=0||glyph.width<=0)
                        return result;
                    if((ch.style&(FontStyles.Underline|FontStyles.Strikethrough))!=0)
                    {result.Reason="decoration-metrics-unavailable";return result;}
                    if(Mathf.Abs(ch.topLeft.y-ch.topRight.y)>0.01f||Mathf.Abs(ch.bottomLeft.y-ch.bottomRight.y)>0.01f)return result;
                    // Legacy GenerateTextMesh: quad height=(glyph.height+2*padding)*scale.
                    // Remove only this symmetric atlas padding; retain horizontal
                    // font-weight expansion and any italic shear conservatively.
                    float padding=(ch.topLeft.y-ch.bottomLeft.y-glyph.height*ch.scale)*0.5f;
                    if(!Finite(padding)||padding< -0.01f)return result;padding=Mathf.Max(0,padding);
                    l=Mathf.Min(ch.topLeft.x,ch.bottomLeft.x)+padding;
                    r=Mathf.Max(ch.topRight.x,ch.bottomRight.x)-padding;
                    b=ch.bottomLeft.y+padding;t=ch.topLeft.y-padding;
                }
                else if(ch.elementType==TMP_TextElementType.Sprite)
                {l=Mathf.Min(ch.topLeft.x,ch.bottomLeft.x);r=Mathf.Max(ch.topRight.x,ch.bottomRight.x);b=Mathf.Min(ch.bottomLeft.y,ch.bottomRight.y);t=Mathf.Max(ch.topLeft.y,ch.topRight.y);}
                else return result;
                if(!Finite(l)||!Finite(r)||!Finite(b)||!Finite(t)||r<=l||t<=b)return result;
                left=Mathf.Min(left,l);right=Mathf.Max(right,r);bottom=Mathf.Min(bottom,b);top=Mathf.Max(top,t);any=true;
                overLeft|=l<area.xMin-0.5f;overRight|=r>area.xMax+0.5f;
                overBottom|=b<area.yMin-0.5f;overTop|=t>area.yMax+0.5f;
            }
            if(!any)return result;
            var line=info.lineInfo[0];
            if(!Finite(line.ascender)||!Finite(line.descender))return result;
            result.Ink=Rect.MinMaxRect(left,bottom,right,top);
            result.LineBox=Rect.MinMaxRect(line.lineExtents.min.x,line.descender,line.lineExtents.max.x,line.ascender);
            if(multi)for(int i=1;i<info.lineCount;i++)
            {
                line=info.lineInfo[i];if(!Finite(line.ascender)||!Finite(line.descender))return result;
                result.LineBox=Rect.MinMaxRect(Mathf.Min(result.LineBox.xMin,line.lineExtents.min.x),Mathf.Min(result.LineBox.yMin,line.descender),
                    Mathf.Max(result.LineBox.xMax,line.lineExtents.max.x),Mathf.Max(result.LineBox.yMax,line.ascender));
            }
            result.Available=true;result.Lines=info.lineCount;result.Characters=info.characterCount;
            result.MaxLineWidth=right-left;result.Height=top-bottom;result.PointSize=label.fontSize;
            string axes=(overLeft?"left+":"")+(overRight?"right+":"")+(overBottom?"bottom+":"")+(overTop?"top":"");
            result.Fits=axes.Length==0;result.Reason=result.Fits?"resolved-ink-inside-region":"resolved-ink-exceeds-"+axes.TrimEnd('+');
            // Demand at a fixed visual baseline, not the fitted size or live text.
            // Read after GetTextInfo: preferred getters may replace probe scratch data.
            var demand=label.GetPreferredValues(text);result.PreferredWidth=demand.x;result.PreferredHeight=demand.y;
            return result;
        }
        public void Dispose(){if(root!=null)UnityEngine.Object.Destroy(root);root=null;label=null;}
    }
}
