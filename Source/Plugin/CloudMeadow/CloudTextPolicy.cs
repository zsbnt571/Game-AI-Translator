using System;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Fusion.CloudMeadow
{
    internal enum CloudTextRole { General,Button,TooltipBody,TooltipTitle,TooltipValue,Dialogue,Bubble,MainMenu,QuestTitle,DialogueName,SaveField,Settings,Tutorial,QuestCounter,StatField,SettingsCategory,Gameplay,PauseButton,AlbumButton,AlbumNotice,RecipeName,RecipeIngredient,DialogueOption,HistoryBody,HistoryName,HistoryHeader,QuestHudTitle,QuestHudStep,InventoryHeader,InventoryName,InventoryNumber,GuideTitle,GuideName,GuideBody,GuideSkillName,GuideSkillBody,VersionLabel,ModalTitle,ModalBody,ModalButton,InventorySearch,ManualBody,ManualLabel,ManualTitle,ManualNumber,DocumentBody,DocumentLabel,DocumentTitle,DocumentNumber,IntrinsicLabel }
    internal enum CloudLayoutResult { Ready,Deferred,Capacity }
    internal sealed class CloudTextProfile
    {
        internal CloudTextRole Role;
        internal RectTransform Boundary;
        internal RectTransform Interaction;
        internal Component TooltipOwner;
        internal Component RoleOwner;
        internal Transform Parent;
        internal bool Grow,SingleLine,DisplayLabel,Coordinated;
        internal string Context;internal SpriteRenderer Keycap;
        internal bool Same(CloudTextProfile p){return p!=null&&Role==p.Role&&Boundary==p.Boundary&&Interaction==p.Interaction&&TooltipOwner==p.TooltipOwner&&RoleOwner==p.RoleOwner&&Parent==p.Parent&&Grow==p.Grow&&SingleLine==p.SingleLine&&Coordinated==p.Coordinated&&Context==p.Context&&Keycap==p.Keycap;}
    }
    // Only this game's known field identities/ancestor roles select a background.
    // No text literals, sample hashes, screen coordinates or nearest-Image guess.
    internal static class CloudTextPolicy
    {
        static readonly Type ability=AccessTools.TypeByName("TeamNimbus.CloudMeadow.UI.AbilityTooltipManager");
        static readonly Type standard=AccessTools.TypeByName("TeamNimbus.CloudMeadow.UI.StandardTooltipManager");
        static readonly Type clickable=AccessTools.TypeByName("TeamNimbus.CloudMeadow.UI.BaseHighlightedClickable");
        static readonly Type menu=AccessTools.TypeByName("TeamNimbus.CloudMeadow.UI.MainMenuManager"),quest=AccessTools.TypeByName("TeamNimbus.CloudMeadow.UI.QuestLogWindow"),questList=AccessTools.TypeByName("TeamNimbus.CloudMeadow.UI.QuestListContainer"),dialog=AccessTools.TypeByName("TeamNimbus.CloudMeadow.Dialogue.DialogWindow");
        static readonly FieldInfo nameText=Field(dialog,"_nameplateText"),nameRect=Field(dialog,"_nameplateRectTransform");
        static readonly string[] menuFields={"_newGameClickable","_loadGameClickable","_optionsClickable","_updatesClickable","_roadmapClickable","_albumClickable","_creditsClickable","_quitClickable"};
        static readonly FieldInfo abilityBody=Field(ability,"abilityDescriptionText"),abilityTitle=Field(ability,"abilityNameText"),abilityDamage=Field(ability,"abilityDamageText"),abilityValue=Field(ability,"cooldownValueText"),standardText=Field(standard,"text");
        static FieldInfo Field(Type t,string n){return t==null?null:AccessTools.Field(t,n);}
        static bool Ref(FieldInfo f,Component owner,Component label){return f!=null&&owner!=null&&(f.GetValue(owner) as UnityEngine.Object)==label;}
        static RectTransform Ancestor(Component label,Component owner,string name)
        {
            for(Transform p=label.transform.parent;p!=null;p=p.parent)
            {if(p.name==name&&p.GetComponent<Image>()!=null)return p as RectTransform;if(p==owner.transform)break;}
            return null;
        }
        internal static CloudTextProfile Resolve(Component label,string kind)
        {
            var p=new CloudTextProfile{Parent=label.transform.parent};
            if(kind=="reveal")
            {
                p.Role=CloudTextRole.Dialogue;p.DisplayLabel=true;
                // Verified Text Content / Text Area hierarchy; fall back to the text's own rect.
                for(Transform t=label.transform.parent;t!=null;t=t.parent)
                {if(t.name=="Text Area"){p.Boundary=t as RectTransform;break;}if(t.GetComponent<Canvas>()!=null)break;}
                return p;
            }
            if(kind=="bubble"){p.Role=CloudTextRole.Bubble;p.DisplayLabel=true;return p;}
            // Opt-in identities from the actual Game.dll fields / asset hierarchy.
            // In particular, a generic clickable or wrapping label is not enough.
            if(ResolveShortLabel(label,p))return p;
            if(CloudFixedUi.Resolve(label,p))return p;
            if(CloudManualLayout.Resolve(label,p))return p;
            if(CloudDisplayRoles.Resolve(label,p))return p;
            if(CloudRemainingUi.Resolve(label,p))return p;
            if(CloudUiRoles.Resolve(label,p))return p;
            Component owner=ability==null?null:label.GetComponentInParent(ability);
            if(owner!=null)
            {
                p.TooltipOwner=owner;
                if(Ref(abilityBody,owner,label)){p.Role=CloudTextRole.TooltipBody;p.Boundary=Ancestor(label,owner,"DescriptionBackground");p.Grow=true;}
                else if(Ref(abilityTitle,owner,label)||Ref(abilityDamage,owner,label)){p.Role=CloudTextRole.TooltipTitle;p.Boundary=Ancestor(label,owner,"TitleBar");p.Grow=true;}
                else if(Ref(abilityValue,owner,label)){p.Role=CloudTextRole.TooltipValue;p.Boundary=Ancestor(label,owner,"CooldownValueBackground");p.SingleLine=true;}
                p.Coordinated=p.DisplayLabel=p.Role!=CloudTextRole.General;
                return p;
            }
            owner=standard==null?null:label.GetComponentInParent(standard);
            if(Ref(standardText,owner,label)){p.Role=CloudTextRole.TooltipBody;p.Boundary=owner.transform as RectTransform;p.TooltipOwner=owner;p.Coordinated=true;p.Grow=true;p.DisplayLabel=true;return p;}
            if(CloudDocumentStructure.Resolve(label as Text,p))return p;
            var selected=label.GetComponentInParent<Selectable>();
            var click=clickable==null?null:label.GetComponentInParent(clickable);
            if(selected!=null||click!=null)
            {p.Role=CloudTextRole.Button;p.DisplayLabel=true;p.Interaction=(selected!=null?selected.transform:click.transform) as RectTransform;}
            var ui=label as Text;var tmp=label as TMP_Text;
            p.SingleLine=ui!=null?ui.horizontalOverflow==HorizontalWrapMode.Overflow:tmp!=null&&!tmp.enableWordWrapping;
            p.Grow=p.Role!=CloudTextRole.Button&&label.GetComponent<ContentSizeFitter>()!=null;
            return p;
        }
        static bool ResolveShortLabel(Component label,CloudTextProfile p)
        {
            var owner=dialog==null?null:label.GetComponentInParent(dialog);
            if(label is TMP_Text&&Ref(nameText,owner,label))
            {
                var boundary=nameRect==null?null:nameRect.GetValue(owner) as RectTransform;
                if(boundary==null||!label.transform.IsChildOf(boundary))return false;
                // _nameplateRectTransform is a point-sized positioning root. Its
                // verified direct background child owns the horizontal layout.
                var background=label.transform.parent;
                if(background==null||background.parent!=boundary||background.GetComponent<Image>()==null||background.GetComponent<HorizontalLayoutGroup>()==null)return false;
                p.Role=CloudTextRole.DialogueName;p.RoleOwner=owner;p.Boundary=background as RectTransform;
                p.DisplayLabel=p.SingleLine=true;return true;
            }
            owner=menu==null?null:label.GetComponentInParent(menu);
            if(label is Text&&owner!=null&&label.transform.parent!=null)
            {
                foreach(string field in menuFields)
                {
                    var f=Field(menu,field);var button=f==null?null:f.GetValue(owner) as Component;
                    if(button==null||label.transform.parent!=button.transform||button.GetComponent<Image>()==null)continue;
                    var parent=button.transform.parent;
                    if(parent==null||parent.GetComponent<GridLayoutGroup>()==null)continue;
                    p.Role=CloudTextRole.MainMenu;p.RoleOwner=owner;p.Interaction=button.transform as RectTransform;
                    p.Boundary=p.Interaction;p.DisplayLabel=p.SingleLine=true;return true;
                }
            }
            owner=quest==null?null:label.GetComponentInParent(quest);
            var area=label.transform.parent;var bar=area==null?null:area.parent;var panel=bar==null?null:bar.parent;
            if(label is TMP_Text&&owner!=null&&questList!=null&&area!=null&&bar!=null&&panel!=null&&
                area.name=="Title Text Area"&&area.GetComponent<Image>()!=null&&panel.GetComponent(questList)!=null&&panel.IsChildOf(owner.transform))
            {
                p.Role=CloudTextRole.QuestTitle;p.RoleOwner=panel.GetComponent(questList);p.Boundary=area as RectTransform;
                p.DisplayLabel=p.SingleLine=true;return true;
            }
            return false;
        }
        internal static bool ShortTmp(CloudTextProfile p){return p.Role==CloudTextRole.QuestTitle||p.Role==CloudTextRole.DialogueName||p.Role==CloudTextRole.QuestCounter||p.Role==CloudTextRole.HistoryHeader;}
        // Geometry identity is read-only and excludes all plugin-owned style values.
        internal struct Geometry
        {
            Transform parent;Vector2 min,max,pivot,position,size;Vector3 scale;Quaternion rotation;
            internal static Geometry Read(RectTransform t)
            {return new Geometry{parent=t.parent,min=t.anchorMin,max=t.anchorMax,pivot=t.pivot,position=t.anchoredPosition,size=t.rect.size,scale=t.lossyScale,rotation=t.rotation};}
            internal bool Same(Geometry g){return parent==g.parent&&min==g.min&&max==g.max&&pivot==g.pivot&&position==g.position&&size==g.size&&scale==g.scale&&rotation==g.rotation;}
        }
        static readonly Vector3[] corners=new Vector3[4];
        internal static bool Usable(Rect b){return Finite(b.width)&&Finite(b.height)&&b.width>0.01f&&b.height>0.01f;}
        static bool Finite(float f){return !float.IsNaN(f)&&!float.IsInfinity(f);}
        internal static Rect LocalRect(RectTransform label,RectTransform other)
        {
            other.GetWorldCorners(corners);float l=float.PositiveInfinity,d=l,r=float.NegativeInfinity,u=r;
            foreach(var w in corners){var v=label.InverseTransformPoint(w);l=Mathf.Min(l,v.x);r=Mathf.Max(r,v.x);d=Mathf.Min(d,v.y);u=Mathf.Max(u,v.y);}
            return Rect.MinMaxRect(l,d,r,u);
        }
        static Rect Intersect(Rect a,Rect b)
        {float l=Mathf.Max(a.xMin,b.xMin),d=Mathf.Max(a.yMin,b.yMin);return new Rect(l,d,Mathf.Max(0,Mathf.Min(a.xMax,b.xMax)-l),Mathf.Max(0,Mathf.Min(a.yMax,b.yMax)-d));}
        internal static Rect Constrain(Component label,CloudTextProfile profile,Rect b)
        {
            var rt=label.transform as RectTransform;if(rt==null)return new Rect();
            if(profile.Boundary!=null)b=Intersect(b,LocalRect(rt,profile.Boundary));
            var graphic=label as MaskableGraphic;
            if(graphic!=null&&graphic.maskable)
                for(Transform p=rt.parent;p!=null;p=p.parent)
                {
                    var rect=p as RectTransform;if(rect==null)continue;
                    // A scroll viewport clips the rendered row. It does not resize
                    // that row's layout to the currently visible slice. Unity's Mask /
                    // RectMask2D remains enabled and still performs the actual clipping.
                    if(CloudRemainingUi.ScrollClipOnly(p,label))continue;
                    var clip=p.GetComponent<RectMask2D>();var stencil=p.GetComponent<Mask>();
                    if((clip!=null&&clip.isActiveAndEnabled)||(stencil!=null&&stencil.isActiveAndEnabled&&stencil.graphic!=null&&stencil.graphic.isActiveAndEnabled))
                        b=Intersect(b,LocalRect(rt,rect));
                    var canvas=p.GetComponent<Canvas>();if(canvas!=null&&canvas.overrideSorting)break;
                }
            return b;
        }
        // Overflow labels may intentionally have a point-sized RectTransform (tutorials),
        // or a stretched label under a point-sized clickable. Use ORIGINAL rendered ink
        // at its original anchor, never the translated mesh, screen or nearest Image.
        internal static Rect UiBounds(Text label,CloudTextProfile profile,Rect nativeInk,float nativeWidth,out string basis)
        {
            var rt=label.rectTransform;var b=rt.rect;basis="label-rect";
            if(CloudFixedUi.Scoped(profile))
            {basis="verified-fixed-field";return CloudFixedUi.Bounds(label,profile);}
            if(CloudManualLayout.Scoped(profile))
            {basis="manual-owned-field";return Constrain(label,profile,CloudManualLayout.Bounds(label,profile));}
            if(CloudDisplayRoles.Ui(profile))
            {basis="identified-owned-field";return Constrain(label,profile,CloudDisplayRoles.FieldBounds(label,profile));}
            if(CloudRemainingUi.Scoped(profile))
            {basis=CloudRemainingUi.Recipe(profile)?"recipe-owned-field-native-scroll-clip":"scoped-owned-field";return Constrain(label,profile,CloudRemainingUi.FieldBounds(label,profile));}
            if(profile.Role==CloudTextRole.MainMenu)
            {
                // The verified menu GridLayoutGroup allocates an entire button cell.
                // Native ink is a visual size reference, not that cell's capacity.
                // Wait for the real cell; never invent a canvas/screen rectangle.
                b=MenuCell(label,profile);
                basis="main-menu-field-cell";return Constrain(label,profile,b);
            }
            if(profile.Role==CloudTextRole.Settings||profile.Role==CloudTextRole.StatField)
            {basis="settings-own-field";return Constrain(label,profile,b);}
            if(profile.Role==CloudTextRole.SaveField)
            {
                // The native ink belongs to this field, never to the clickable slot.
                // ContentSizeFitter receives original demand (not translated feedback).
                var fitter=label.GetComponent<ContentSizeFitter>();
                if(Usable(nativeInk))
                {
                    bool fitX=fitter!=null&&fitter.horizontalFit!=ContentSizeFitter.FitMode.Unconstrained;
                    float width=fitX?Mathf.Max(nativeInk.width,nativeWidth):b.width;
                    if(width<=0.01f)width=nativeInk.width;
                    if(profile.Context=="timePlayed"||profile.Context=="timePlayedHeaderText")
                    {
                        var name=CloudUiRoles.Get(profile.RoleOwner,"playerName") as Text;
                        var nf=name==null?null:name.GetComponent<ContentSizeFitter>();
                        // Only this serialized, fixed-width name column can constrain
                        // the stacked time fields; never borrow a translated fitter width.
                        if(name!=null&&(nf==null||nf.horizontalFit==ContentSizeFitter.FitMode.Unconstrained))
                            width=Mathf.Max(width,LocalRect(rt,name.rectTransform).width);
                    }
                    // Keep the native baseline/ink origin, including the font's leading.
                    var pivot=Text.GetTextAnchorPivot(label.alignment);
                    b=Rect.MinMaxRect(nativeInk.xMin-(width-nativeInk.width)*pivot.x,nativeInk.yMin,
                        nativeInk.xMax+(width-nativeInk.width)*(1-pivot.x),nativeInk.yMax);
                }
                basis="save-field-native-ink";return Constrain(label,profile,b);
            }
            bool empty=!Usable(b);
            if(empty&&profile.Interaction!=null)
            {var control=LocalRect(rt,profile.Interaction);if(Usable(control)){b=control;basis="interaction-rect";}}
            if(profile.Boundary==null&&Usable(nativeInk))
            {
                bool x=b.width<=0.01f||label.horizontalOverflow==HorizontalWrapMode.Overflow;
                bool y=b.height<=0.01f||label.verticalOverflow==VerticalWrapMode.Overflow;
                if(x||y)
                {
                    // Finite native extent supports deliberate overflow without inventing
                    // empty canvas space or changing the control's clickable rectangle.
                    b=Rect.MinMaxRect(x?nativeInk.xMin:b.xMin,y?nativeInk.yMin:b.yMin,x?nativeInk.xMax:b.xMax,y?nativeInk.yMax:b.yMax);
                    basis="native-overflow-ink";
                }
            }
            return Constrain(label,profile,b);
        }
        internal static Rect MenuCell(Text label,CloudTextProfile profile)
        {return profile.Interaction==null?new Rect():LocalRect(label.rectTransform,profile.Interaction);}
        internal static Rect Bounds(Component label,CloudTextProfile profile)
        {
            var rt=label.transform as RectTransform;return rt==null?new Rect():Constrain(label,profile,rt.rect);
        }
        internal static void RepositionTooltip(CloudTextProfile p)
        {
            if(p.TooltipOwner==null)return;
            // Reuse the game's own offscreen correction after translated preferred
            // sizes settle. Never assign a root position, pivot, or interaction size.
            var field=Field(p.TooltipOwner.GetType(),"_testIfOffscreen");if(field!=null)field.SetValue(p.TooltipOwner,true);
        }
    }
}
