using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Fusion.Embedded.Layout;

namespace Fusion.CloudMeadow
{
    // Game structure adapter. Existing verified special roles resolve first.
    // Every BaseGuidebookPages registered page can supply the same fixed-document
    // contract; no chapter allowlist, per-page font size or screenshot geometry.
    internal static class CloudDocumentStructure
    {
        static readonly Type pages=AccessTools.TypeByName("TeamNimbus.CloudMeadow.UI.BaseGuidebookPages");
        internal static bool Document(CloudTextProfile p)
        {return p.Role==CloudTextRole.DocumentBody||p.Role==CloudTextRole.DocumentLabel||p.Role==CloudTextRole.DocumentTitle||p.Role==CloudTextRole.DocumentNumber;}
        internal static bool Scoped(CloudTextProfile p){return Document(p)||p.Role==CloudTextRole.IntrinsicLabel;}
        internal static bool Direct(CloudTextProfile p){return Scoped(p)&&p.Role!=CloudTextRole.DocumentBody;}
        internal static bool Resolve(Text text,CloudTextProfile p)
        {
            if(text==null)return false;
            var owner=pages==null?null:text.GetComponentInParent(pages);
            if(owner!=null)
            {
                var registered=CloudUiRoles.Get(owner,"pages") as GameObject[];Transform page=null;
                if(registered!=null)foreach(var obj in registered)
                    if(obj!=null&&text.transform.IsChildOf(obj.transform)){page=obj.transform;break;}
                if(page!=null)
                {
                    // Page roots are point-sized. These structural resources prove
                    // a fixed illustrated page or a full document sheet, not a list.
                    bool spread=page.Find("LeftPage/PageHeaderLeft")!=null||page.Find("RightPage/PageHeaderRight")!=null;
                    bool sheet=page.Find("DocumentBackground")!=null;
                    if(spread||sheet)
                    {
                        var fit=text.GetComponent<ContentSizeFitter>();
                        bool namedLabel=text.name=="Name"||text.name=="Subtitle"||text.name=="Title"||text.name=="PageNumber";
                        bool paragraphFlow=!namedLabel&&text.horizontalOverflow==HorizontalWrapMode.Wrap&&fit!=null&&
                            fit.verticalFit==ContentSizeFitter.FitMode.PreferredSize&&fit.horizontalFit==ContentSizeFitter.FitMode.Unconstrained;
                        bool body=paragraphFlow||text.name.StartsWith("Section",StringComparison.Ordinal)||text.name=="Description"||
                            text.name=="TypeText"||text.name=="IconText"||text.name=="UnlockText"||text.name=="PhotoText";
                        p.Role=text.name=="PageNumber"?CloudTextRole.DocumentNumber:body?CloudTextRole.DocumentBody:
                            text.name=="Title"?CloudTextRole.DocumentTitle:CloudTextRole.DocumentLabel;
                        p.RoleOwner=page;p.Context=sheet?"fixed-document-sheet":"fixed-illustrated-page";
                        p.Coordinated=p.DisplayLabel=true;p.Grow=false;p.SingleLine=!body;return true;
                    }
                }
            }
            var fitter=text.GetComponent<ContentSizeFitter>();var rt=text.rectTransform;
            // Intrinsic labels own translated preferred width/height. Only pinned
            // labels with no layout group/input/interaction qualify. Preferred
            // width removes soft wrapping; explicit line breaks remain untouched.
            if(fitter!=null&&fitter.horizontalFit==ContentSizeFitter.FitMode.PreferredSize&&fitter.verticalFit==ContentSizeFitter.FitMode.PreferredSize&&
                rt.anchorMin==rt.anchorMax&&text.canvas!=null&&
                text.GetComponentInParent<InputField>()==null&&text.GetComponentInParent<Selectable>()==null&&
                (rt.parent==null||rt.parent.GetComponent<LayoutGroup>()==null))
            {
                p.Role=CloudTextRole.IntrinsicLabel;p.RoleOwner=text.canvas;p.Context="intrinsic-single-line";
                p.Coordinated=p.DisplayLabel=p.SingleLine=p.Grow=true;return true;
            }
            return false;
        }
        internal sealed class Field
        {
            internal RectTransform Page,Paper,Header;
            internal readonly List<RectTransform> Obstacles=new List<RectTransform>();
            internal readonly List<Text> Peers=new List<Text>();
        }
        static bool Decoration(Image image)
        {
            string n=image.name.ToLowerInvariant();string s=image.sprite==null?"":image.sprite.name.ToLowerInvariant();
            // Only the game's structural image categories; backdrops and document
            // decoration never consume paragraph capacity. Skill ICON children do.
            return n.Contains("background")||n.EndsWith("paper")||n.StartsWith("pageheader")||n.Contains("tape")||n=="stamp"||n=="signature"||
                n=="markings"||n=="corner"||s.Contains("_tape")||s.Contains("_documentseal");
        }
        internal static Field Capture(Text text,CloudTextProfile p)
        {
            if(!Document(p)||p.RoleOwner==null)return null;
            var page=p.RoleOwner.transform;Transform side=null;
            for(var a=text.transform.parent;a!=null&&a!=page;a=a.parent)
                if(a.name=="LeftPage"||a.name=="RightPage"){side=a;break;}
            var field=new Field{Page=page as RectTransform};
            field.Header=side==null?null:side.Find(side.name=="LeftPage"?"PageHeaderLeft":"PageHeaderRight") as RectTransform;
            field.Paper=page.Find("DocumentBackground") as RectTransform;
            if(field.Paper==null)
                for(var a=page.parent;a!=null;a=a.parent)
                {var paper=a.Find("Background") as RectTransform;if(paper!=null&&paper.GetComponent<Image>()!=null){field.Paper=paper;break;}}
            var scope=side??page;
            // Enumerated once per binding, never a new scene scan per frame.
            foreach(var image in scope.GetComponentsInChildren<Image>(true))
                if(!Decoration(image))field.Obstacles.Add(image.rectTransform);
            foreach(var peer in scope.GetComponentsInChildren<Text>(true))
                if(peer!=text)field.Peers.Add(peer);
            return field;
        }
        static Area A(Rect r){return new Area(r.xMin,r.yMin,r.xMax,r.yMax);}
        static Rect R(Area r){return new Rect(r.Left,r.Bottom,r.Width,r.Height);}
        internal static Rect Bounds(Text text,CloudTextProfile p,Field f)
        {
            var own=text.rectTransform;var b=own.rect;
            if(p.Role==CloudTextRole.IntrinsicLabel)
            {
                // The fitter's full rect is capacity, not old English ink. Limit
                // it by the owning canvas in LABEL units; retain its own origin.
                var canvas=text.canvas==null?null:text.canvas.transform as RectTransform;
                if(canvas!=null)b=R(EmbeddedLayoutRules.Intersect(A(b),A(CloudTextPolicy.LocalRect(own,canvas))));
                return b;
            }
            if(f==null||f.Paper==null)return new Rect();
            var paper=CloudTextPolicy.LocalRect(own,f.Paper);var allocated=A(b);
            if(f.Header!=null)
            {
                var head=CloudTextPolicy.LocalRect(own,f.Header);
                float edge=head.height*0.16f;
                var column=new Area(head.xMin-edge,paper.yMin+edge,head.xMax+edge,p.Role==CloudTextRole.DocumentBody?head.yMin:paper.yMax-edge);
                allocated=EmbeddedLayoutRules.Intersect(allocated,column);
            }
            else
            {
                // A top-level sheet title owns the header band above its subtitle.
                if(p.Role==CloudTextRole.DocumentTitle&&text.transform.parent==f.Page)
                    allocated.Top=paper.yMax-Math.Min(2,paper.height*0.01f);
                allocated=EmbeddedLayoutRules.Intersect(allocated,A(paper));
            }
            float gap=Math.Min(2,Math.Max(0,b.width)*0.005f);
            foreach(var obstacle in f.Obstacles)
            {
                if(obstacle==null||!obstacle.gameObject.activeSelf)continue;
                // Native captions/numerical chart annotations can deliberately
                // belong to an image. This does not grant body prose that overlap.
                if(p.Role!=CloudTextRole.DocumentBody&&text.transform.IsChildOf(obstacle))continue;
                // Decorative paper ancestor is excluded above, illustrations are
                // actual obstacles even when they are children of a Text fitter.
                allocated=EmbeddedLayoutRules.Avoid(allocated,A(CloudTextPolicy.LocalRect(own,obstacle)),gap);
            }
            foreach(var peer in f.Peers)
            {
                if(peer==null||!peer.gameObject.activeSelf||text.transform.IsChildOf(peer.transform))continue;
                // Full peer allocation, not translated ink/preferred size. Fixed
                // document fitters are fed original demand by the Unity adapter.
                var other=CloudTextPolicy.LocalRect(own,peer.rectTransform);
                // Child subtitles are separate fields. Generic nesting does not
                // grant a parent's paragraph ownership of a child's label.
                allocated=EmbeddedLayoutRules.Avoid(allocated,A(other),gap);
            }
            return R(allocated);
        }
    }
}
