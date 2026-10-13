using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Fusion.CloudMeadow
{
    // Managed page state; Written and input only queue data. All reader UI and
    // page mutations, including cleanup, belong to the guarded commit phase.
    internal sealed class CloudTmpReader : ICloudReaderUpdate
    {
        static readonly Dictionary<int,CloudTmpReader> pageOwners=new Dictionary<int,CloudTmpReader>();
        readonly TMP_Text label;readonly Func<bool> current,layoutCurrent;readonly Action prepare;
        readonly UnityEngine.SceneManagement.Scene scene;readonly int id,originalPage,capturedPage;
        Scrollbar bar;int pages=1,ownedPage,desiredPage=1,revision;
        bool arranging,closed,refresh,lastVisible;string identity;readonly string source;Func<bool> restoreCurrent;
        CloudTextPolicy.Geometry geometry;Rect bounds;TMP_FontAsset font;Material material;
        internal CloudTmpReader(TMP_Text text,string original,int nativePage,Func<bool> valid,Func<bool> region,Action setup)
        {label=text;source=original;originalPage=nativePage;capturedPage=text.pageToDisplay;current=valid;layoutCurrent=region;prepare=setup;scene=text.gameObject.scene;id=text.GetInstanceID();}
        bool Current {get{return !closed&&label!=null&&label.gameObject.scene.Equals(scene)&&current();}}
        bool Visible {get{return Current&&label.isActiveAndEnabled;}}
        internal int NativePage {get{return label==null||label.pageToDisplay==(ownedPage!=0?ownedPage:capturedPage)?originalPage:label.pageToDisplay;}}
        internal static int NativePageOf(TMP_Text text)
        {CloudTmpReader owner;return pageOwners.TryGetValue(text.GetInstanceID(),out owner)&&owner.label==text&&text.gameObject.scene.Equals(owner.scene)?owner.NativePage:text.pageToDisplay;}
        internal void Written(string content)
        {
            if(identity!=content)desiredPage=1;
            identity=content;geometry=CloudTextPolicy.Geometry.Read(label.rectTransform);bounds=label.rectTransform.rect;font=label.font;material=label.fontSharedMaterial;
            refresh=true;revision++;CloudReaderUpdates.Request(this);
        }
        internal void Close(Func<bool> restore=null)
        {if(restore!=null)restoreCurrent=restore;if(!closed||restore!=null){closed=true;revision++;CloudReaderUpdates.Request(this);}}
        internal void Pulse()
        {
            bool visible=Visible;
            if(visible!=lastVisible){lastVisible=visible;CloudReaderUpdates.Request(this);}
            if(!Current&&!closed)Close();
        }
        void Create()
        {
            var root=new GameObject("Fusion text pages",typeof(RectTransform),typeof(CanvasRenderer),typeof(Image),typeof(LayoutElement),typeof(Scrollbar));
            root.hideFlags=HideFlags.DontSave;root.transform.SetParent(label.transform,false);root.GetComponent<LayoutElement>().ignoreLayout=true;
            var rect=(RectTransform)root.transform;rect.anchorMin=new Vector2(1,0);rect.anchorMax=new Vector2(1,1);rect.pivot=new Vector2(1,0.5f);rect.sizeDelta=new Vector2(5,0);rect.anchoredPosition=Vector2.zero;
            root.GetComponent<Image>().color=new Color(0.3f,0.3f,0.3f,0.2f);
            var thumb=new GameObject("Page",typeof(RectTransform),typeof(CanvasRenderer),typeof(Image));thumb.transform.SetParent(root.transform,false);
            thumb.GetComponent<Image>().color=new Color(0.3f,0.3f,0.3f,0.75f);
            root.AddComponent<CloudTmpReaderInput>().Owner=this;bar=root.GetComponent<Scrollbar>();
            bar.handleRect=(RectTransform)thumb.transform;bar.handleRect.sizeDelta=Vector2.zero;bar.targetGraphic=thumb.GetComponent<Image>();
            bar.direction=Scrollbar.Direction.BottomToTop;bar.navigation=new Navigation{mode=Navigation.Mode.None};bar.onValueChanged.AddListener(Change);
        }
        void Active(bool value){if(bar!=null&&bar.gameObject.activeSelf!=value)bar.gameObject.SetActive(value);}
        public void CommitReading()
        {
            if(!CloudReaderUpdates.Safe){CloudReaderUpdates.Request(this);return;}
            if(closed||!Current)
            {
                closed=true;
                CloudTmpReader owner;
                if(pageOwners.TryGetValue(id,out owner)&&ReferenceEquals(owner,this))
                {
                    pageOwners.Remove(id);
                    if(label!=null&&restoreCurrent!=null&&restoreCurrent()&&label.gameObject.scene.Equals(scene)&&
                        (label.text==identity||label.text==source)&&label.pageToDisplay==ownedPage&&label.pageToDisplay!=originalPage)
                        label.pageToDisplay=originalPage;
                }
                Active(false);if(bar!=null)UnityEngine.Object.Destroy(bar.gameObject);bar=null;return;
            }
            lastVisible=Visible;if(!lastVisible){Active(false);return;}
            if(identity!=label.text||font!=label.font||material!=label.fontSharedMaterial||bounds!=label.rectTransform.rect||
                !geometry.Same(CloudTextPolicy.Geometry.Read(label.rectTransform))||!layoutCurrent())
            {Active(false);return;} // normal layout reflow/Displayed supplies the new request
            int applied=revision;
            if(refresh)
            {
                refresh=false;prepare();
                if(!Current||applied!=revision){CloudReaderUpdates.Request(this);return;}
                pageOwners[id]=this;
                if(label.pageToDisplay!=desiredPage)label.pageToDisplay=desiredPage;ownedPage=desiredPage;
                // Explicit safe update, never Written/parser/measurement.
                label.ForceMeshUpdate();
                if(!Current||applied!=revision){CloudReaderUpdates.Request(this);return;}
                if(bounds!=label.rectTransform.rect||!geometry.Same(CloudTextPolicy.Geometry.Read(label.rectTransform))||!layoutCurrent())
                {Active(false);return;}
                pages=Mathf.Max(1,label.textInfo.pageCount);desiredPage=Mathf.Clamp(desiredPage,1,pages);
            }
            pageOwners[id]=this;
            if(label.pageToDisplay!=desiredPage)label.pageToDisplay=desiredPage;ownedPage=desiredPage;
            if(pages>1&&bar==null)Create();
            if(bar!=null)
            {
                arranging=true;
                try
                {
                    if(bar.numberOfSteps!=pages)bar.numberOfSteps=pages;
                    float size=1f/pages,value=pages>1?1f-(float)(desiredPage-1)/(pages-1):1;
                    if(bar.size!=size)bar.size=size;if(bar.value!=value)bar.value=value;
                }
                finally{arranging=false;}
                Active(pages>1);
            }
        }
        void Move(int page)
        {if(!Visible||identity!=label.text)return;page=Mathf.Clamp(page,1,pages);if(page==desiredPage)return;desiredPage=page;revision++;CloudReaderUpdates.Request(this);}
        void Change(float value){if(!arranging)Move(1+Mathf.RoundToInt((1-value)*(pages-1)));}
        internal void Scroll(PointerEventData e){if(Visible&&pages>1&&e.scrollDelta.y!=0){Move(desiredPage-(int)Mathf.Sign(e.scrollDelta.y));e.Use();}}
    }
    internal sealed class CloudTmpReaderInput : MonoBehaviour,IScrollHandler
    {
        internal CloudTmpReader Owner;
        void LateUpdate(){if(Owner!=null)Owner.Pulse();}
        public void OnScroll(PointerEventData e){if(Owner!=null)Owner.Scroll(e);}
    }
}
