using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Fusion.CloudMeadow
{
    internal sealed class CloudFonts : IDisposable
    {
        readonly string directory;
        readonly Dictionary<int,JArray> glyphs=new Dictionary<int,JArray>();
        readonly Dictionary<int,List<JArray>> pageGlyphs=new Dictionary<int,List<JArray>>();
        readonly Dictionary<int,TMP_FontAsset> pages=new Dictionary<int,TMP_FontAsset>();
        readonly HashSet<int> failedPages=new HashSet<int>();
        readonly Dictionary<int,TMP_FontAsset> clones=new Dictionary<int,TMP_FontAsset>();
        readonly HashSet<int> cloneIds=new HashSet<int>();
        TMP_FontAsset coordinated;
        readonly List<UnityEngine.Object> owned=new List<UnityEngine.Object>();
        JObject index;
        Font uiFallback;
        readonly Dictionary<int,Text> fallbackLabels=new Dictionary<int,Text>();
        bool refreshMeshes;
        bool loaded,failed;
        internal CloudFonts(string path){directory=Path.Combine(path,"FontAssets");Font.textureRebuilt+=AtlasRebuilt;}
        void AtlasRebuilt(Font font){if(uiFallback!=null&&font==uiFallback)refreshMeshes=true;}
        internal void RefreshPendingMeshes()
        {
            if(!refreshMeshes)return;refreshMeshes=false;var dead=new List<int>();
            foreach(var pair in fallbackLabels)
            {
                var label=pair.Value;if(label==null){dead.Add(pair.Key);continue;}
                // Old UI.Text suppresses its font callback inside mesh generation. A
                // deferred vertex-only refresh catches atlas UV changes after that point,
                // without altering layout, sizes, text, or creating another font object.
                if(label.font==uiFallback){label.cachedTextGenerator.Invalidate();label.SetVerticesDirty();}
            }
            foreach(int id in dead)fallbackLabels.Remove(id);
        }
        void ReadIndex()
        {
            if(loaded||failed)return;
            try
            {
                index=JObject.Parse(File.ReadAllText(Path.Combine(directory,"font-index.json")));
                foreach(JArray row in (JArray)index["glyphs"])
                {int id=(int)row[0],page=(int)row[1];glyphs[id]=row;if(!pageGlyphs.ContainsKey(page))pageGlyphs[page]=new List<JArray>();pageGlyphs[page].Add(row);}
                loaded=true;
            }
            catch{failed=true;}
        }
        TMP_FontAsset Page(int number,TMP_FontAsset template)
        {
            TMP_FontAsset cached;if(pages.TryGetValue(number,out cached))return cached;
            if(failedPages.Contains(number))throw new InvalidDataException("Font page unavailable; original text retained.");
            failedPages.Add(number); // An incomplete page is not recreated every frame.
            int size=(int)index["size"];byte[] bytes=new byte[size*size];
            using(var stream=File.OpenRead(Path.Combine(directory,(string)index["pages"][number]["file"])))
            using(var gz=new GZipStream(stream,CompressionMode.Decompress))
            {int offset=0,n;while(offset<bytes.Length&&(n=gz.Read(bytes,offset,bytes.Length-offset))>0)offset+=n;if(offset!=bytes.Length||gz.ReadByte()!=-1)throw new InvalidDataException("Incomplete font atlas.");}
            var texture=new Texture2D(size,size,TextureFormat.Alpha8,false,true);owned.Add(texture);texture.name="FusionFallbackAtlas"+number;texture.LoadRawTextureData(bytes);texture.Apply(false,true);texture.filterMode=FilterMode.Bilinear;texture.wrapMode=TextureWrapMode.Clamp;
            var font=ScriptableObject.CreateInstance<TMP_FontAsset>();owned.Add(font);font.name="FusionCloudFallback"+number;font.fontAssetType=TMP_FontAsset.FontAssetTypes.SDF;font.atlas=texture;
            var shader=Shader.Find("TextMeshPro/Mobile/Distance Field")??Shader.Find("TextMeshPro/Distance Field")??template.material.shader;
            var material=new Material(shader);owned.Add(material);material.name=font.name+" Material";material.mainTexture=texture;
            material.SetFloat("_TextureWidth",size);material.SetFloat("_TextureHeight",size);material.SetFloat("_GradientScale",(float)index["spread"]+1);material.SetFloat("_WeightNormal",0);material.SetFloat("_WeightBold",0.5f);font.material=material;
            font.AddFaceInfo(new FaceInfo{Name=font.name,PointSize=(float)index["pointSize"],Scale=1,LineHeight=(float)index["lineHeight"],Ascender=(float)index["ascender"],Descender=(float)index["descender"],CapHeight=27,AtlasWidth=size,AtlasHeight=size,Padding=(float)index["spread"],SubSize=0.5f,Underline=-4,UnderlineThickness=2});
            var list=new List<TMP_Glyph>();foreach(var row in pageGlyphs[number])list.Add(new TMP_Glyph{id=(int)row[0],x=(float)row[2],y=(float)row[3],width=(float)row[4],height=(float)row[5],xOffset=(float)row[6],yOffset=(float)row[7],xAdvance=(float)row[8],scale=1});
            font.AddGlyphInfo(list.ToArray());font.AddKerningInfo(new KerningTable());font.fallbackFontAssets=new List<TMP_FontAsset>();font.ReadFontDefinition();pages[number]=font;failedPages.Remove(number);return font;
        }
        internal bool Prepare(TMP_Text label,string value,bool coordinate=false)
        {
            if(label==null||label.font==null)return false;
            try
            {
                var original=label.font;var missing=new HashSet<int>();bool inTag=false;
                if(coordinate)
                {
                    // Identified history/HUD only: use the EXISTING atlas family for
                    // Latin, numerals and CJK. No global font asset is modified.
                    ReadIndex();if(!loaded)return false;
                    var needed=new HashSet<int>();
                    foreach(char c in value+"国H012")
                    {if(c=='<'){inTag=true;continue;}if(c=='>'){inTag=false;continue;}if(!inTag&&!char.IsControl(c)){if(!glyphs.ContainsKey(c))return Prepare(label,value);needed.Add((int)glyphs[c][1]);}}
                    if(coordinated==null)
                    {
                        coordinated=UnityEngine.Object.Instantiate(Page((int)glyphs['H'][1],original));owned.Add(coordinated);
                        coordinated.name="Fusion scoped coordinated text";coordinated.fallbackFontAssets=new List<TMP_FontAsset>();
                    }
                    foreach(int n in needed){var page=Page(n,original);if(page.atlas!=coordinated.atlas&&!coordinated.fallbackFontAssets.Contains(page))coordinated.fallbackFontAssets.Add(page);}
                    label.font=coordinated;label.fontSharedMaterial=coordinated.material;return true;
                }
                foreach(char c in value){if(c=='<'){inTag=true;continue;}if(c=='>'){inTag=false;continue;}if(!inTag&&!char.IsControl(c)&&!original.HasCharacter(c,true))missing.Add(c);}
                if(missing.Count==0)return true;ReadIndex();if(!loaded)return false;
                foreach(int c in missing)if(!glyphs.ContainsKey(c))return false;
                TMP_FontAsset clone;
                if(cloneIds.Contains(original.GetInstanceID()))clone=original;
                else if(!clones.TryGetValue(original.GetInstanceID(),out clone))
                {clone=UnityEngine.Object.Instantiate(original);owned.Add(clone);clone.name="FusionClone"+original.GetInstanceID();clone.fallbackFontAssets=original.fallbackFontAssets==null?new List<TMP_FontAsset>():new List<TMP_FontAsset>(original.fallbackFontAssets);clones[original.GetInstanceID()]=clone;cloneIds.Add(clone.GetInstanceID());}
                // This TMP version stops searching at the first null fallback entry.
                clone.fallbackFontAssets.RemoveAll(delegate(TMP_FontAsset asset){return asset==null;});
                foreach(int c in missing){var page=Page((int)glyphs[c][1],original);if(!clone.fallbackFontAssets.Contains(page))clone.fallbackFontAssets.Add(page);}
                // The clone retains the original atlas. Keep a per-control material
                // preset for original glyphs; only missing glyphs use fallback materials.
                var material=label.fontSharedMaterial;label.font=clone;if(material!=null)label.fontSharedMaterial=material;return true;
            }
            catch{return false;}
        }
        internal bool Prepare(Text label,string value,bool coordinated=false)
        {
            if(label==null)return false;bool missing=false;bool tag=false;
            foreach(char c in value){if(c=='<'){tag=true;continue;}if(c=='>'){tag=false;continue;}if(!tag&&!char.IsControl(c)&&(label.font==null||!label.font.HasCharacter(c))){missing=true;break;}}
            if(!missing&&!coordinated)return true;
            if(uiFallback==null)
            {uiFallback=Font.CreateDynamicFontFromOSFont(new[]{"Microsoft YaHei","DengXian","SimHei","Yu Gothic","Malgun Gothic","Arial Unicode MS"},36);if(uiFallback!=null)owned.Add(uiFallback);}
            if(uiFallback==null)return false;
            tag=false;foreach(char c in value){if(c=='<'){tag=true;continue;}if(c=='>'){tag=false;continue;}if(!tag&&!char.IsControl(c)&&!uiFallback.HasCharacter(c))return false;}
            // Explicit .15 UI roles render CJK, Latin and numbers in the same existing OS font.
            // All other labels retain the .14 missing-glyph-only path; no shared asset changes.
            label.font=uiFallback;fallbackLabels[label.GetInstanceID()]=label;return true;
        }
        public void Dispose(){Font.textureRebuilt-=AtlasRebuilt;fallbackLabels.Clear();foreach(var obj in owned)if(obj!=null)UnityEngine.Object.Destroy(obj);owned.Clear();}
    }
}
