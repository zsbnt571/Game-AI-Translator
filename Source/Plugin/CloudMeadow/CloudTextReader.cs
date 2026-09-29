using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Fusion.CloudMeadow
{
    // Managed reading state exists before its UI. Drawing and input only change
    // this state; CommitReading alone owns UI creation, sync, visibility and disposal.
    internal sealed class CloudTextReader : ICloudReaderUpdate
    {
        readonly Text label;readonly Func<bool> current;readonly Func<Rect> field;
        readonly UnityEngine.SceneManagement.Scene scene;
        RectTransform rect;Scrollbar bar;
        Rect bounds,ownRect;TextGenerationSettings style;Matrix4x4 matrix;
        float height,range,view,position=1;bool wanted,closed,arranging,redraw,lastVisible;
        int revision;string identity,renderedText;
        internal CloudTextReader(Text text,Func<bool> valid,Func<Rect> region)
        {label=text;current=valid;field=region;scene=text.gameObject.scene;}
        bool Current {get{return !closed&&label!=null&&label.gameObject.scene.Equals(scene)&&current();}}
        bool Visible {get{return Current&&label.isActiveAndEnabled;}}
        // Data-only: never sample the scrollbar during a rebuild or use a previous
        // content's offset. A real region/font change invalidates queued geometry.
        internal float Request(Rect area,float fullHeight,string content)
        {
            Pulse();
            var native=label.GetGenerationSettings(Vector2.zero);var local=label.rectTransform.rect;
            var transform=label.rectTransform.localToWorldMatrix;
            bool reset=identity!=content||renderedText!=label.text;
            if(reset||!wanted||bounds!=area||height!=fullHeight||ownRect!=local||!style.Equals(native)||matrix!=transform)
            {
                if(reset)position=1;
                identity=content;renderedText=label.text;bounds=area;height=fullHeight;view=area.height;range=Mathf.Max(0,height-view);
                ownRect=local;style=native;matrix=transform;wanted=true;revision++;
                CloudReaderUpdates.Request(this);
            }
            return range*(1-position);
        }
        internal void Hide(){if(wanted){wanted=false;revision++;CloudReaderUpdates.Request(this);}}
        internal void Close(){if(!closed){closed=true;revision++;CloudReaderUpdates.Request(this);}}
        internal void Pulse()
        {
            bool visible=Visible;
            if(visible!=lastVisible){lastVisible=visible;CloudReaderUpdates.Request(this);}
            if(!Current&&!closed)Close();
        }
        void Create()
        {
            var root=new GameObject("Fusion field reading",typeof(RectTransform),typeof(CanvasRenderer),typeof(Image),typeof(LayoutElement));
            root.hideFlags=HideFlags.DontSave;root.transform.SetParent(label.transform,false);
            root.GetComponent<LayoutElement>().ignoreLayout=true;root.GetComponent<Image>().color=Color.clear;
            rect=(RectTransform)root.transform;root.AddComponent<CloudTextReaderInput>().Owner=this;
            var rail=new GameObject("Scroll",typeof(RectTransform),typeof(CanvasRenderer),typeof(Image),typeof(Scrollbar));
            rail.transform.SetParent(root.transform,false);var rr=(RectTransform)rail.transform;
            rr.anchorMin=new Vector2(1,0);rr.anchorMax=new Vector2(1,1);rr.pivot=new Vector2(1,0.5f);rr.sizeDelta=new Vector2(5,0);rr.anchoredPosition=Vector2.zero;
            rail.GetComponent<Image>().color=new Color(0.3f,0.3f,0.3f,0.18f);
            var knob=new GameObject("Handle",typeof(RectTransform),typeof(CanvasRenderer),typeof(Image));knob.transform.SetParent(rail.transform,false);
            var handle=(RectTransform)knob.transform;handle.sizeDelta=Vector2.zero;
            var image=knob.GetComponent<Image>();image.color=new Color(0.3f,0.3f,0.3f,0.75f);
            bar=rail.GetComponent<Scrollbar>();bar.handleRect=handle;bar.targetGraphic=image;
            bar.direction=Scrollbar.Direction.BottomToTop;bar.navigation=new Navigation{mode=Navigation.Mode.None};
            bar.onValueChanged.AddListener(Changed);
        }
        void Active(bool value){if(rect!=null&&rect.gameObject.activeSelf!=value)rect.gameObject.SetActive(value);}
        public void CommitReading()
        {
            if(!CloudReaderUpdates.Safe){CloudReaderUpdates.Request(this);return;}
            if(closed||!Current)
            {closed=true;Active(false);if(rect!=null)UnityEngine.Object.Destroy(rect.gameObject);rect=null;bar=null;return;}
            bool visible=Visible;lastVisible=visible;
            if(!wanted||!visible){Active(false);return;}
            if(renderedText!=label.text){Active(false);return;} // a newer translation has not drawn yet
            if(bounds!=field()||ownRect!=label.rectTransform.rect||!style.Equals(label.GetGenerationSettings(Vector2.zero))||matrix!=label.rectTransform.localToWorldMatrix)
            {Active(false);label.SetVerticesDirty();return;} // next native mesh computes fresh geometry
            int applied=revision;bool created=rect==null;if(created)Create();
            // Setters are guarded by equality, including the hidden -> visible path.
            if(rect.anchorMin!=Vector2.zero)rect.anchorMin=Vector2.zero;
            if(rect.anchorMax!=Vector2.zero)rect.anchorMax=Vector2.zero;
            if(rect.pivot!=Vector2.zero)rect.pivot=Vector2.zero;
            var origin=new Vector2(bounds.xMin-ownRect.xMin,bounds.yMin-ownRect.yMin);
            if(rect.anchoredPosition!=origin)rect.anchoredPosition=origin;
            if(rect.sizeDelta!=bounds.size)rect.sizeDelta=bounds.size;
            arranging=true;
            try{float size=Mathf.Clamp01(view/Mathf.Max(view,height));if(bar.size!=size)bar.size=size;if(bar.value!=position)bar.value=position;}
            finally{arranging=false;}
            if(!Current||revision!=applied){CloudReaderUpdates.Request(this);return;}
            Active(true);
            if(created||redraw){redraw=false;label.SetVerticesDirty();}
        }
        void Changed(float value){if(!arranging)Move(value);}
        void Move(float value)
        {if(!Visible||!wanted||renderedText!=label.text)return;value=Mathf.Clamp01(value);if(position==value)return;position=value;redraw=true;revision++;CloudReaderUpdates.Request(this);}
        internal void Scroll(PointerEventData e)
        {if(Visible&&wanted&&range>0){Move(position+e.scrollDelta.y*view*0.2f/range);e.Use();}}
        internal void Drag(PointerEventData e)
        {
            Vector2 a,b;
            if(Visible&&wanted&&range>0&&rect!=null&&RectTransformUtility.ScreenPointToLocalPointInRectangle(rect,e.position,e.pressEventCamera,out a)&&
                RectTransformUtility.ScreenPointToLocalPointInRectangle(rect,e.position-e.delta,e.pressEventCamera,out b))
            {Move(position-(a.y-b.y)/range);e.Use();}
        }
        // UGUI Text quads are axis aligned before native mesh modifiers. Clamp the
        // visible quad and interpolate its UV; no character is removed from the
        // full generated string. Scrolling brings the remaining quads into view.
        internal static bool Clip(UIVertex[] q,Rect b)
        {
            float l=q[0].position.x,r=l,d=q[0].position.y,u=d;
            for(int i=1;i<4;i++){l=Mathf.Min(l,q[i].position.x);r=Mathf.Max(r,q[i].position.x);d=Mathf.Min(d,q[i].position.y);u=Mathf.Max(u,q[i].position.y);}
            float nl=Mathf.Max(l,b.xMin),nr=Mathf.Min(r,b.xMax),nd=Mathf.Max(d,b.yMin),nu=Mathf.Min(u,b.yMax);
            if(nr<=nl||nu<=nd||r<=l||u<=d)return false;
            var bl=q[0];var br=q[0];var tl=q[0];var tr=q[0];
            for(int i=0;i<4;i++){bool left=q[i].position.x<(l+r)*0.5f,top=q[i].position.y>(d+u)*0.5f;if(left){if(top)tl=q[i];else bl=q[i];}else{if(top)tr=q[i];else br=q[i];}}
            for(int i=0;i<4;i++)
            {
                var v=q[i].position;v.x=Mathf.Clamp(v.x,nl,nr);v.y=Mathf.Clamp(v.y,nd,nu);
                float x=(v.x-l)/(r-l),y=(v.y-d)/(u-d);
                q[i].uv0=Vector2.Lerp(Vector2.Lerp(bl.uv0,br.uv0,x),Vector2.Lerp(tl.uv0,tr.uv0,x),y);q[i].position=v;
            }
            return true;
        }
    }
}

namespace Fusion.CloudMeadow
{
    internal sealed class CloudTextReaderInput : MonoBehaviour,IScrollHandler,IBeginDragHandler,IDragHandler
    {
        internal CloudTextReader Owner;
        void LateUpdate(){if(Owner!=null)Owner.Pulse();}
        public void OnScroll(PointerEventData e){if(Owner!=null)Owner.Scroll(e);}
        public void OnBeginDrag(PointerEventData e){e.Use();}
        public void OnDrag(PointerEventData e){if(Owner!=null)Owner.Drag(e);}
    }
}
