using System;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Fusion.CloudMeadow
{
    // Identities are game-owned references, not translated strings or prefab root names.
    internal static class CloudFixedUi
    {
        static readonly Type version=AccessTools.TypeByName("TeamNimbus.CloudMeadow.UI.VersionBarManager"),
            modal=AccessTools.TypeByName("TeamNimbus.CloudMeadow.UI.ModalDialogWindow"),
            inventory=AccessTools.TypeByName("TeamNimbus.CloudMeadow.Inventory.InventoryWindowManager");
        static Component Owner(Component c,Type t){return t==null?null:c.GetComponentInParent(t);}
        static bool Field(Component owner,string name,Component value){return owner!=null&&(CloudUiRoles.Get(owner,name) as UnityEngine.Object)==value;}
        static bool Set(CloudTextProfile p,CloudTextRole role,Component owner,string context,bool grow=false)
        {p.Role=role;p.RoleOwner=owner;p.Context=context;p.Coordinated=p.DisplayLabel=true;p.SingleLine=role!=CloudTextRole.ModalBody;p.Grow=grow;return true;}
        internal static bool SearchPlaceholder(Component c)
        {
            if(!(c is Text))return false;var owner=Owner(c,inventory);
            var input=owner==null?null:CloudUiRoles.Get(owner,"searchInput") as InputField;
            return input!=null&&input.placeholder==c&&input.textComponent!=c&&c.transform.IsChildOf(input.transform);
        }
        internal static bool InventoryHeader(Component c,CloudTextProfile p)
        {
            if(!(c is Text))return false;var owner=Owner(c,inventory);var column=c.transform.parent;
            if(owner==null||c.name!="Text"||column==null||column.parent==null||column.parent.name!="EntryColumnGroup"||
                column.parent.GetComponent<HorizontalLayoutGroup>()==null||!column.IsChildOf(owner.transform))return false;
            if(column.name!="Name"&&column.name!="Value"&&column.name!="Type"&&column.name!="Quality"&&column.name!="Quantity"&&column.name!="Total")return false;
            // Ordinary and multi-select transaction groups share the same column components.
            return Set(p,CloudTextRole.InventoryHeader,owner,column.name);
        }
        internal static bool Resolve(Component c,CloudTextProfile p)
        {
            if(!(c is Text))return false;
            if(InventoryHeader(c,p))return true;
            if(SearchPlaceholder(c))return Set(p,CloudTextRole.InventorySearch,Owner(c,inventory),"search-placeholder");
            var owner=Owner(c,version);
            if(Field(owner,"_text",c))return Set(p,CloudTextRole.VersionLabel,owner,"version-label");
            owner=Owner(c,modal);if(owner==null)return false;
            if(Field(owner,"titleText",c))return Set(p,CloudTextRole.ModalTitle,owner,"modal-title",true);
            if(Field(owner,"messageText",c))return Set(p,CloudTextRole.ModalBody,owner,"modal-body",true);
            foreach(string name in new[]{"acceptButton","cancelButton","thirdButton"})
                if(Field(owner,name+"Label",c))
                {
                    var button=CloudUiRoles.Get(owner,name) as Component;
                    if(button==null||!c.transform.IsChildOf(button.transform))return false;
                    Set(p,CloudTextRole.ModalButton,owner,name,true);p.Interaction=button.transform as RectTransform;return true;
                }
            return false;
        }
        internal static bool Scoped(CloudTextProfile p)
        {return p.Role==CloudTextRole.VersionLabel||p.Role==CloudTextRole.ModalTitle||p.Role==CloudTextRole.ModalBody||p.Role==CloudTextRole.ModalButton||p.Role==CloudTextRole.InventoryHeader||p.Role==CloudTextRole.InventorySearch;}
        internal static bool Direct(CloudTextProfile p){return Scoped(p)&&p.Role!=CloudTextRole.ModalBody;}
        internal static bool NativeScroll(CloudTextProfile p){return p.Role==CloudTextRole.ModalBody;}
        internal static Rect Inset(Rect b,float x,float y)
        {return Rect.MinMaxRect(b.xMin+x,b.yMin+y,b.xMax-x,b.yMax-y);}
        internal static Rect Bounds(Text t,CloudTextProfile p)
        {
            var rt=t.rectTransform;var b=rt.rect;
            if(p.Role==CloudTextRole.VersionLabel)
            {
                // The serialized label stretches across its OWN bar; side stripes
                // are excluded by a relative inset, not by the old English ink.
                return Inset(b,b.width*0.10f,b.height*0.08f);
            }
            if(p.Role==CloudTextRole.InventoryHeader)
            {
                var column=rt.parent as RectTransform;var cell=CloudTextPolicy.LocalRect(rt,column);
                // Keep native horizontal origin and label width; use the actual
                // column band vertically, independent of English word length.
                b.yMin=cell.yMin;b.yMax=cell.yMax;
                b.xMin=Mathf.Max(b.xMin,cell.xMin);b.xMax=Mathf.Min(b.xMax,cell.xMax);
                return Inset(b,0,Mathf.Min(1,b.height*0.05f));
            }
            if(p.Role==CloudTextRole.ModalButton&&(!CloudTextPolicy.Usable(b))&&p.Interaction!=null)
            {
                b=CloudTextPolicy.LocalRect(rt,p.Interaction);var group=p.Interaction.GetComponent<HorizontalLayoutGroup>();
                if(group!=null)
                {
                    // Padding belongs to the button transform; convert points to label space.
                    var cell=p.Interaction.rect;var a=rt.InverseTransformPoint(p.Interaction.TransformPoint(new Vector3(cell.xMin+group.padding.left,cell.yMin+group.padding.bottom)));
                    var z=rt.InverseTransformPoint(p.Interaction.TransformPoint(new Vector3(cell.xMax-group.padding.right,cell.yMax-group.padding.top)));
                    b=Rect.MinMaxRect(a.x,a.y,z.x,z.y);
                }
            }
            // Modal body uses its full native ScrollRect content. Viewport clipping
            // and MaxScalingViewport still own scrolling, never a second reader.
            return b;
        }
    }
}
