using System;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Fusion.CloudMeadow
{
    // Three verified fixed-page chapters only. Party/skills/merchants/residents
    // continue through CloudDisplayRoles and never enter these rules.
    internal static class CloudManualLayout
    {
        static readonly Type book=AccessTools.TypeByName("TeamNimbus.CloudMeadow.UI.GuidebookWindowManager");
        internal static bool Resolve(Component c,CloudTextProfile p)
        {
            if(!(c is Text)||book==null)return false;var owner=c.GetComponentInParent(book);if(owner==null)return false;
            Component chapter=null;string chapterName=null;
            foreach(string field in new[]{"homestead","breeding","cooking"})
            {var found=CloudUiRoles.Get(owner,field) as Component;if(found!=null&&c.transform.IsChildOf(found.transform)){chapter=found;chapterName=field;break;}}
            if(chapter==null)return false;
            Transform side=null;for(var a=c.transform.parent;a!=null&&a!=chapter.transform;a=a.parent)
                if(a.name=="LeftPage"||a.name=="RightPage"){side=a;break;}
            if(side==null||side.parent==null)return false;
            string path=c.name;for(var a=c.transform.parent;a!=side;a=a.parent){if(a==null)return false;path=a.name+"/"+path;}
            bool body=c.name.StartsWith("Section",StringComparison.Ordinal)||c.name=="TypeText"||c.name=="IconText"||c.name=="UnlockText"||c.name=="PhotoText"||
                (c.transform.parent.name=="BreedingPairNotes"&&(c.name=="Note1"||c.name=="Note2"||c.name=="Note3"));
            p.Role=c.name=="PageNumber"?CloudTextRole.ManualNumber:body?CloudTextRole.ManualBody:
                c.name=="Title"&&c.transform.parent.name.StartsWith("PageHeader",StringComparison.Ordinal)?CloudTextRole.ManualTitle:CloudTextRole.ManualLabel;
            p.RoleOwner=side;p.Context=chapterName+":"+side.parent.name+":"+path;
            p.Coordinated=p.DisplayLabel=true;p.SingleLine=!body;p.Grow=false;return true;
        }
        internal static bool Scoped(CloudTextProfile p)
        {return p.Role==CloudTextRole.ManualBody||p.Role==CloudTextRole.ManualLabel||p.Role==CloudTextRole.ManualTitle||p.Role==CloudTextRole.ManualNumber;}
        internal static bool Direct(CloudTextProfile p){return Scoped(p)&&p.Role!=CloudTextRole.ManualBody;}
        internal static string Display(CloudTextProfile p,string value)
        {
            if(p.Role!=CloudTextRole.ManualBody||string.IsNullOrEmpty(value)||!Regex.IsMatch(value,@"[\u3400-\u9fff]"))return value;
            // Original English hard wraps/indentation are editorial, not paragraph
            // boundaries on these fixed explanatory fields. Only the display copy
            // changes. Rich tags, protected tokens, numbers and double breaks survive.
            var parts=Regex.Split(value,@"(<[^<>]*>)");
            for(int i=0;i<parts.Length;i+=2)
            {
                parts[i]=parts[i].Replace("\r\n","\n");
                parts[i]=Regex.Replace(parts[i],@"(?m)^[ \t]+","");
                parts[i]=Regex.Replace(parts[i],@"(?<!\n)\n(?!\n)"," ");
                parts[i]=Regex.Replace(parts[i],@"([\u3400-\u9fff。，；：！？]) +(?=[\u3400-\u9fff。，；：！？])","$1");
            }
            return string.Concat(parts);
        }
        static Rect Local(Text t,Transform node)
        {return node is RectTransform?CloudTextPolicy.LocalRect(t.rectTransform,(RectTransform)node):new Rect();}
        static Rect Intersection(Rect a,Rect b)
        {return Rect.MinMaxRect(Mathf.Max(a.xMin,b.xMin),Mathf.Max(a.yMin,b.yMin),Mathf.Min(a.xMax,b.xMax),Mathf.Min(a.yMax,b.yMax));}
        static float Gap(Rect b){return Mathf.Min(2,Mathf.Max(0,b.width)*0.005f);}
        static void Below(Text t,ref Rect b,Transform obstacle,float gap)
        {if(obstacle==null)return;var r=Local(t,obstacle);if(r.width>0&&r.xMin<b.xMax&&r.xMax>b.xMin)b.yMax=Mathf.Min(b.yMax,r.yMin-gap);}
        static void Above(Text t,ref Rect b,Transform obstacle,float gap)
        {if(obstacle==null)return;var r=Local(t,obstacle);if(r.width>0&&r.xMin<b.xMax&&r.xMax>b.xMin)b.yMin=Mathf.Max(b.yMin,r.yMax+gap);}
        static void Right(Text t,ref Rect b,Transform obstacle,float gap)
        {if(obstacle==null)return;var r=Local(t,obstacle);if(r.height>0&&r.yMin<b.yMax&&r.yMax>b.yMin)b.xMin=Mathf.Max(b.xMin,r.xMax+gap);}
        internal static Rect Bounds(Text t,CloudTextProfile p)
        {
            var b=t.rectTransform.rect;var side=p.RoleOwner==null?null:p.RoleOwner.transform;if(side==null)return b;
            var header=side.Find(side.name=="LeftPage"?"PageHeaderLeft":"PageHeaderRight");
            float gap=Gap(b);
            if(p.Role==CloudTextRole.ManualTitle&&header!=null)
            {
                var band=Local(t,header);var number=header.Find("PageNumber");
                // The upper band is shared with the page number, not the full header image.
                b.xMin=band.xMin+gap;b.xMax=band.xMax-gap;
                if(number!=null){var n=Local(t,number);if(n.center.x<b.center.x)b.xMin=n.xMax+gap;else b.xMax=n.xMin-gap;}
                return b;
            }
            if(p.Role!=CloudTextRole.ManualBody)
            {
                if(p.Context.StartsWith("cooking:Page1:",StringComparison.Ordinal)&&t.name=="StatName")
                {
                    // StatIcons is the verified 330-wide two-row group. The HP
                    // label's native width is only 14; its free row space belongs
                    // to the label up to the next icon or this group's right edge.
                    var row=side.Find("CookingExampleBackground/Section1_2/StatIcons");
                    if(row!=null)
                    {
                        var band=Local(t,row);b.xMax=Mathf.Max(b.xMax,band.xMax-gap);
                        foreach(Transform icon in t.transform)
                        {
                            if(icon.GetComponent<Image>()==null)continue;var r=Local(t,icon);
                            if(r.xMin>b.xMin&&r.yMin<b.yMax&&r.yMax>b.yMin)b.xMax=Mathf.Min(b.xMax,r.xMin-gap);
                        }
                    }
                    return b;
                }
                var parent=t.transform.parent;
                if(parent!=null&&parent.GetComponent<Image>()!=null&&
                    (parent.name=="Header"||parent.name=="PhotoTag"||parent.name.StartsWith("Tag",StringComparison.Ordinal)||parent.name.EndsWith("Tab",StringComparison.Ordinal)))
                {
                    var band=Local(t,parent);
                    // Caption/trait bands own the label. Do not borrow the illustration.
                    b=CloudFixedUi.Inset(band,Mathf.Min(4,band.width*0.04f),Mathf.Min(2,band.height*0.08f));
                    foreach(Transform child in parent)
                        if(child.name=="BuildIcon")Right(t,ref b,child,gap);
                }
                return b;
            }
            // These page roots are point-sized. The verified book Background and
            // side's page header define the outer paper limit only, never the
            // capacity of every paragraph. Start with the paragraph's own rect.
            var center=side.parent==null||side.parent.parent==null?null:side.parent.parent.parent;
            var paper=center==null?null:center.Find("Background");
            if(paper!=null&&header!=null)
            {
                var page=Local(t,paper);var cap=Local(t,header);float edge=cap.height*0.16f;
                var inside=Rect.MinMaxRect(cap.xMin-edge,page.yMin+edge,cap.xMax+edge,cap.yMin);
                b=Intersection(b,inside);
            }
            string path=p.Context.Substring(p.Context.IndexOf(':',p.Context.IndexOf(':')+1)+1);
            if(p.Context.StartsWith("homestead:",StringComparison.Ordinal)&&t.name.StartsWith("Section",StringComparison.Ordinal)&&
                t.transform.parent.name.EndsWith("Photo",StringComparison.Ordinal))
            {
                Below(t,ref b,t.transform.parent,gap);
                Below(t,ref b,t.transform.parent.Find("PhotoTag"),gap);
            }
            // Specific image/label relationships verified in the resource tree.
            // Positions are always transformed at use time; no screenshot pixels.
            if(p.Context.StartsWith("homestead:Page1:",StringComparison.Ordinal))
            {
                if(t.name=="TypeText")
                {
                    Right(t,ref b,t.transform.Find("Header"),gap);
                    var icons=t.transform.Find("SpecialIcons");
                    if(icons!=null)foreach(Transform icon in icons)Above(t,ref b,icon,gap);
                    Above(t,ref b,t.transform.Find("IconText"),gap);
                }
                if(t.name=="IconText")
                {var icons=t.transform.Find("SpecialIcons");if(icons!=null)foreach(Transform icon in icons)Above(t,ref b,icon,gap);}
                if(path=="Section1_1")Above(t,ref b,side.Find("Section1_1/BuildingTypes/TypeHeader"),gap);
                if(path=="Section1_2")
                {
                    var photo=t.transform.Find("CrystalNotes");var r=Local(t,photo);
                    if(photo!=null&&r.yMin<b.yMax&&r.yMax>b.yMin&&r.xMin<b.xMax&&r.xMax>b.xMin)b.xMax=Mathf.Min(b.xMax,r.xMin-gap);
                }
                if(path=="FieldPhoto/Section2_1")Below(t,ref b,side.Find("FieldPhoto/PhotoTag"),gap);
            }
            if(p.Context.StartsWith("breeding:Page2:",StringComparison.Ordinal))
            {
                if(t.name=="Note1"||t.name=="Note2"||t.name=="Note3")
                {var icons=side.Find("BreedingPairNotes/"+t.name+"Images");if(icons!=null)foreach(Transform icon in icons)Above(t,ref b,icon,gap);}
                if(path=="BreedingPairNotes/Section3_1")Below(t,ref b,t.transform.Find("PastureIcon"),gap);
            }
            if(p.Context.StartsWith("breeding:Page1:",StringComparison.Ordinal))
            {
                if(path=="DomesticationBar/Section1_1")Above(t,ref b,side.Find("DomesticationBar"),gap);
                if(path=="DomesticationBar/Section1_2")
                {Below(t,ref b,side.Find("DomesticationBar"),gap);Above(t,ref b,side.Find("WildEggPhoto"),gap);}
                if(path=="WildEggPhoto/Section1_3")Below(t,ref b,side.Find("WildEggPhoto"),gap);
                if(path=="Traits/TraitSources/Section2_1")
                    foreach(string tab in new[]{"SpeciesTab","BloodlineTab","UniversalTab"})Above(t,ref b,side.Find("Traits/TraitSources/"+tab),gap);
                if(path=="Traits/Section2_2")Above(t,ref b,side.Find("IncestWarning/WarningImage/Section2_3"),gap);
                if(path=="IncestWarning/WarningImage/Section2_3")Above(t,ref b,side.Find("IncestWarning/WarningImage"),gap);
            }
            if(p.Context.StartsWith("cooking:Page1:",StringComparison.Ordinal))
            {
                if(path=="CookingExampleBackground/Section1_1")Above(t,ref b,side.Find("CookingExampleBackground"),gap);
                if(path=="CookingExampleBackground/Section1_2")Below(t,ref b,side.Find("CookingExampleBackground"),gap);
                if(path=="CookingExampleBackground/Section1_2/StatIcons/Section1_3")
                {
                    var icons=t.transform.parent;
                    foreach(string label in new[]{"PhysiqueIcon/StatName","PhysiqueIcon/StatName/StaminaIcon/StatName","PhysiqueIcon/StatName/StaminaIcon/StatName/IntuitionIcon/StatName","ExperienceIcon/StatName","ExperienceIcon/StatName/HPIcon/StatName"})
                        Below(t,ref b,icons.Find(label),gap);
                }
                if(path=="QualityExampleBackground/Section2_1")
                {Below(t,ref b,side.Find("Photos/KitchenPhoto1"),gap);Below(t,ref b,side.Find("Photos/KitchenPhoto2/Tag2"),gap);Above(t,ref b,side.Find("QualityExampleBackground"),gap);}
                if(path=="QualityExampleBackground/Section2_2")Below(t,ref b,side.Find("QualityExampleBackground"),gap);
            }
            return b;
        }
    }
}
