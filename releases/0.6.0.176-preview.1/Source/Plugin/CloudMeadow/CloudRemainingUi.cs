using System;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Fusion.CloudMeadow
{
    // Serialized field ownership, not translated strings or nearest backgrounds.
    internal static class CloudRemainingUi
    {
        static readonly Type gameplay=Ui("GameplaySettingsManager"),options=Ui("OptionsWindowManager"),pause=Ui("PauseMenuWindow"),album=Ui("AlbumWindowManager"),
            ingredient=Ui("Farm.Cooking.IngredientInRecipeEntry"),recipe=Ui("Farm.Cooking.RecipeEntry"),
            option=AccessTools.TypeByName("TeamNimbus.CloudMeadow.Dialogue.DialogWindowOptionClickable");
        static Type Ui(string name){return AccessTools.TypeByName("TeamNimbus.CloudMeadow.UI."+name);}
        static Component Parent(Component c,Type type){return type==null?null:c.GetComponentInParent(type);}
        static bool Field(Component owner,string name,Component c){return CloudUiRoles.Get(owner,name) as UnityEngine.Object==c;}
        static Component ChildField(Component c,Component owner,string[] fields)
        {
            if(owner==null)return null;
            foreach(string field in fields)
            {
                var control=CloudUiRoles.Get(owner,field) as Component;
                if(control!=null&&c.transform.parent==control.transform)return control;
            }
            return null;
        }
        static bool Assign(CloudTextProfile p,CloudTextRole role,Component owner,string context,Component control=null)
        {
            p.Role=role;p.RoleOwner=owner;p.Context=context;p.DisplayLabel=true;
            p.Coordinated=role!=CloudTextRole.DialogueOption;
            p.Interaction=control==null?null:control.transform as RectTransform;
            p.SingleLine=false;return true;
        }
        internal static bool Resolve(Component c,CloudTextProfile p)
        {
            var owner=Parent(c,option);
            if(c is TMP_Text&&owner!=null&&Field(owner,"_optionText",c))
            {
                Assign(p,CloudTextRole.DialogueOption,owner,"option-text",owner);
                p.Boundary=c.transform as RectTransform;
                var group=c.transform.parent==null?null:c.transform.parent.GetComponent<VerticalLayoutGroup>();
                var fixedSize=c.GetComponent<LayoutElement>();
                p.Grow=group!=null&&group.childControlHeight&&(fixedSize==null||fixedSize.preferredHeight<0);
                return true;
            }
            if(!(c is Text))return false;
            owner=Parent(c,options);
            var button=ChildField(c,owner,new[]{"graphicsAndAudioClickable","keybindingsClickable","contentSettingsClickable","cheatsClickable","accessibilityClickable"});
            if(button!=null)return Assign(p,CloudTextRole.SettingsCategory,owner,"settings-category",button);
            owner=Parent(c,gameplay);
            if(owner!=null)return Assign(p,CloudTextRole.Gameplay,owner,"gameplay-field");
            owner=Parent(c,pause);
            button=ChildField(c,owner,new[]{"optonsClickable","saveClickable","loadClickable","returnToMainMenuClickable","quitGameClickable"});
            if(button!=null)return Assign(p,CloudTextRole.PauseButton,owner,"pause-button",button);
            owner=Parent(c,ingredient);
            if(owner!=null&&Field(owner,"nameText",c))return Assign(p,CloudTextRole.RecipeIngredient,owner,"ingredient-name");
            owner=Parent(c,recipe);
            if(owner!=null&&Field(owner,"recipeName",c))return Assign(p,CloudTextRole.RecipeName,owner,"recipe-title");
            owner=Parent(c,album);
            if(owner!=null)
            {
                button=ChildField(c,owner,new[]{"_closeClickable","_resetAlbumClickable"});
                if(button!=null)return Assign(p,CloudTextRole.AlbumButton,owner,"album-action",button);
                if(owner.transform.Find("BottomUI/Notice")==c.transform)return Assign(p,CloudTextRole.AlbumNotice,owner,"album-notice");
                // The filter button has its own label and excludes the portrait grid.
                if(owner.transform.Find("TopUI/NPC/Text")==c.transform)
                    return Assign(p,CloudTextRole.AlbumButton,owner,"album-filter",c.transform.parent);
            }
            return false;
        }
        internal static bool Scoped(CloudTextProfile p)
        {
            return p.Role==CloudTextRole.SettingsCategory||p.Role==CloudTextRole.Gameplay||p.Role==CloudTextRole.PauseButton||
                p.Role==CloudTextRole.AlbumButton||p.Role==CloudTextRole.AlbumNotice||p.Role==CloudTextRole.RecipeIngredient||p.Role==CloudTextRole.RecipeName;
        }
        internal static bool Recipe(CloudTextProfile p){return p.Role==CloudTextRole.RecipeIngredient||p.Role==CloudTextRole.RecipeName;}
        internal static Rect FieldBounds(Text t,CloudTextProfile p)
        {
            var rt=t.rectTransform;var b=rt.rect;
            if(p.Role==CloudTextRole.SettingsCategory)
            {
                // The native grid settles its individual clickable cell. Convert that
                // exact cell into Text-local units, including .5 / 2 local scales.
                return p.Interaction==null?new Rect():CloudTextPolicy.LocalRect(rt,p.Interaction);
            }
            if(p.Role==CloudTextRole.RecipeName&&p.RoleOwner!=null)
            {
                // This serialized title is a top-row field with an X preferred fitter.
                // Its vertical band is its own; only extend to the same card's right
                // edge with the existing left inset mirrored (never into ingredients).
                var card=CloudTextPolicy.LocalRect(rt,(RectTransform)p.RoleOwner.transform);
                float inset=Mathf.Max(0,b.xMin-card.xMin);
                b.xMax=Mathf.Max(b.xMin,card.xMax-inset);
            }
            return b;
        }
        internal static bool ScrollClipOnly(Transform ancestor,Component text)
        {
            // A viewport can be nested in another presentation wrapper. Follow its
            // actual ScrollRect.content relationship rather than just one parent.
            var scroll=ancestor.GetComponentInParent<ScrollRect>();
            if(scroll==null||scroll.content==null||!text.transform.IsChildOf(scroll.content))return false;
            if(scroll.viewport!=null)return ancestor==scroll.viewport;
            return (ancestor==scroll.transform||scroll.content.IsChildOf(ancestor))&&ancestor!=scroll.content;
        }
        internal static string RegionDetail(Text t,CloudTextProfile p)
        {
            if(p.Role!=CloudTextRole.RecipeName||p.RoleOwner==null)return "";
            var card=CloudTextPolicy.LocalRect(t.rectTransform,p.RoleOwner.transform as RectTransform);
            var field=FieldBounds(t,p);var final=CloudTextPolicy.Constrain(t,p,field);
            return " ownWidth="+CloudDiagnostics.Number(t.rectTransform.rect.width)+" cardWidth="+CloudDiagnostics.Number(card.width)+
                " fieldWidth="+CloudDiagnostics.Number(field.width)+" constrainedWidth="+CloudDiagnostics.Number(final.width)+" units=label-local";
        }
    }
}
