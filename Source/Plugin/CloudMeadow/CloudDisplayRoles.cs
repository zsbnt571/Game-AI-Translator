using System;
using System.Text.RegularExpressions;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Fusion.CloudMeadow
{
    // .17 roles are selected by serialized owner fields or the inspected fixed
    // handbook hierarchy. They do not depend on an English sample or its hash.
    internal static class CloudDisplayRoles
    {
        static readonly Type history=AccessTools.TypeByName("TeamNimbus.CloudMeadow.Dialogue.DialogHistoryEntryManager"),
            hud=AccessTools.TypeByName("TeamNimbus.CloudMeadow.UI.QuestSystemActiveQuest"),
            step=AccessTools.TypeByName("TeamNimbus.CloudMeadow.UI.QuestSystemActiveQuestStep"),
            inventory=AccessTools.TypeByName("TeamNimbus.CloudMeadow.Inventory.InventoryWindowScrollviewEntry"),
            book=AccessTools.TypeByName("TeamNimbus.CloudMeadow.UI.GuidebookWindowManager");
        static Component Owner(Component c,Type t){return t==null?null:c.GetComponentInParent(t);}
        static bool Field(Component o,string f,Component c){return CloudUiRoles.Get(o,f) as UnityEngine.Object==c;}
        static bool Set(CloudTextProfile p,CloudTextRole r,Component o,string context,bool grow=false)
        {p.Role=r;p.RoleOwner=o;p.Context=context;p.DisplayLabel=p.Coordinated=true;p.Grow=grow;p.SingleLine=false;return true;}
        internal static bool Resolve(Component c,CloudTextProfile p)
        {
            Component o;
            if(c is TMP_Text)
            {
                o=Owner(c,history);
                if(o!=null)
                {
                    if(Field(o,"_dialogText",c))return Set(p,CloudTextRole.HistoryBody,o,"history-body",true);
                    if(Field(o,"_speakerText",c))return Set(p,CloudTextRole.HistoryName,o,"history-speaker",true);
                }
                o=Owner(c,hud);if(o!=null&&Field(o,"_questNameText",c))return Set(p,CloudTextRole.QuestHudTitle,o,"quest-hud-title",true);
                o=Owner(c,step);if(o!=null&&Field(o,"_questStepDescriptionText",c))return Set(p,CloudTextRole.QuestHudStep,o,"quest-hud-step",true);
            }
            if(c.name=="Header Text"&&c.transform.parent!=null&&c.transform.parent.name=="HeaderBackground")
                for(Transform a=c.transform.parent;a!=null;a=a.parent)
                    if(a.name=="DialogHistoryManager_Off")return Set(p,CloudTextRole.HistoryHeader,a,"history-title");
            if(!(c is Text))return false;
            o=Owner(c,inventory);
            if(o!=null)
            {
                if(Field(o,"nameText",c))return Set(p,CloudTextRole.InventoryName,o,"inventory-name");
                if(Field(o,"quantityText",c)||Field(o,"valueText",c))return Set(p,CloudTextRole.InventoryNumber,o,"inventory-number");
            }
            var parent=c.transform.parent;
            o=Owner(c,book);if(o==null)return false;
            bool scoped=false;for(Transform a=parent;a!=null&&a!=o.transform;a=a.parent)
                if(a.name=="Party_Off"||a.name=="Merchants_Off"||a.name=="Townspeople_Off"){scoped=true;break;}
            if(!scoped)return false;
            if(c.name=="AbilityDescription"&&parent!=null&&parent.Find("Background/AbilityName")!=null)
                return Set(p,CloudTextRole.GuideSkillBody,parent,"guide-skill-description");
            if(c.name=="AbilityName"&&parent!=null&&parent.parent!=null&&parent.parent.Find("AbilityDescription")!=null)
                return Set(p,CloudTextRole.GuideSkillName,parent.parent,"guide-skill-name");
            if(c.name=="Description"||c.name=="History")return Set(p,CloudTextRole.GuideBody,parent,"guide-body");
            if(c.name=="Name")return Set(p,CloudTextRole.GuideName,parent,"guide-entity");
            if(c.name=="Title")return Set(p,CloudTextRole.GuideTitle,parent,"guide-title");
            return false;
        }
        internal static bool Flow(CloudTextProfile p)
        {return p.Role==CloudTextRole.HistoryBody||p.Role==CloudTextRole.HistoryName||p.Role==CloudTextRole.QuestHudTitle||p.Role==CloudTextRole.QuestHudStep;}
        internal static bool History(CloudTextProfile p){return p.Role==CloudTextRole.HistoryBody||p.Role==CloudTextRole.HistoryName||p.Role==CloudTextRole.HistoryHeader;}
        internal static bool Guide(CloudTextProfile p)
        {return p.Role==CloudTextRole.GuideBody||p.Role==CloudTextRole.GuideName||p.Role==CloudTextRole.GuideTitle||p.Role==CloudTextRole.GuideSkillBody||p.Role==CloudTextRole.GuideSkillName;}
        internal static bool Ui(CloudTextProfile p)
        {return Guide(p)||p.Role==CloudTextRole.InventoryHeader||p.Role==CloudTextRole.InventoryName||p.Role==CloudTextRole.InventoryNumber||p.Role==CloudTextRole.HistoryHeader;}
        internal static bool Narrative(CloudTextProfile p)
        {return p.Role==CloudTextRole.HistoryBody||p.Role==CloudTextRole.GuideBody;}
        internal static bool Local(CloudTextProfile p,string source,bool chinese,out string value)
        {
            value=null;if(!chinese||string.IsNullOrEmpty(source))return false;
            // These headers describe actual inventory data: KoronaValueOfOne is
            // value, not a promised selling price. No shared cache entry is replaced.
            if(p.Role==CloudTextRole.InventoryHeader)
            {if(p.Context=="Name"&&source.Trim()=="Name")value="名称";if(p.Context=="Value"&&source.Trim()=="Value")value="价值";}
            if((p.Role==CloudTextRole.InventoryNumber||p.Role==CloudTextRole.ManualNumber||CloudDocumentStructure.Scoped(p))&&Regex.IsMatch(source,@"^[\s+\-\d.,%/x×:]+$"))value=source;
            return value!=null;
        }
        static Rect CropPortrait(RectTransform label,Rect b,RectTransform portrait)
        {
            if(portrait==null||portrait.GetComponent<Image>()==null)return b;
            var image=CloudTextPolicy.LocalRect(label,portrait);
            if(image.yMax<=b.yMin||image.yMin>=b.yMax||image.xMax<=b.xMin||image.xMin>=b.xMax)return b;
            // A fixed portrait is an obstacle, never capacity. Keep a continuous
            // paragraph column on the side that actually contains its left edge.
            if(image.center.x>b.center.x)b.xMax=Mathf.Min(b.xMax,image.xMin);
            else b.xMin=Mathf.Max(b.xMin,image.xMax);
            return b;
        }
        internal static Rect FieldBounds(Text t,CloudTextProfile p)
        {
            var rt=t.rectTransform;var b=rt.rect;
            if(p.Role==CloudTextRole.GuideBody)
            {
                b=CropPortrait(rt,b,t.transform.parent as RectTransform);
                // The player's card has a separate gear image inside its history
                // band. Only intersecting, smaller direct images can be obstacles.
                if(t.name=="History"&&t.transform.parent!=null)
                    foreach(Transform child in t.transform.parent)
                    {var r=child as RectTransform;if(r!=null&&r.rect.width<b.width&&child.name!="Abilities")b=CropPortrait(rt,b,r);}
            }
            else if(p.Role==CloudTextRole.GuideName&&t.transform.parent!=null&&t.transform.parent.name=="Description")
            {
                var body=t.transform.parent.GetComponent<Text>();
                if(body!=null)
                {
                    var field=FieldBounds(body,new CloudTextProfile{Role=CloudTextRole.GuideBody});
                    var l=rt.InverseTransformPoint(body.transform.TransformPoint(new Vector3(field.xMin,0,0)));
                    var r=rt.InverseTransformPoint(body.transform.TransformPoint(new Vector3(field.xMax,0,0)));
                    b.xMin=l.x;b.xMax=r.x;
                }
            }
            else if(p.Role==CloudTextRole.GuideSkillBody)
            {
                var title=p.RoleOwner.transform.Find("Background") as RectTransform;
                if(title!=null)
                {
                    var cap=CloudTextPolicy.LocalRect(rt,title);var group=title.GetComponent<HorizontalLayoutGroup>();
                    float gap=group==null?0:group.padding.bottom;
                    b.yMax=cap.yMin-gap;
                    float bottom=float.NegativeInfinity;
                    foreach(Transform sibling in p.RoleOwner.transform.parent)
                    {
                        var next=sibling.Find("Background") as RectTransform;if(next==null||next==title)continue;
                        var n=CloudTextPolicy.LocalRect(rt,next);if(n.xMax>b.xMin&&n.xMin<b.xMax&&n.yMax<b.yMax)bottom=Mathf.Max(bottom,n.yMax+gap);
                    }
                    foreach(Transform child in p.RoleOwner.transform)
                    {
                        var icon=child as RectTransform;if(icon==null||!child.name.StartsWith("AbilityIcon",StringComparison.Ordinal))continue;
                        var r=CloudTextPolicy.LocalRect(rt,icon);b.xMax=Mathf.Min(b.xMax,r.xMin);
                        if(float.IsNegativeInfinity(bottom))bottom=r.yMin;
                    }
                    if(!float.IsNegativeInfinity(bottom))b.yMin=bottom;
                }
            }
            return b;
        }
    }
}
