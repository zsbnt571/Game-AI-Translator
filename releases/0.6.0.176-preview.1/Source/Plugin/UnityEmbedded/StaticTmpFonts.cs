using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Fusion.UnityEmbedded
{
    // Owned static SDF pages derived from Noto CJK, shared with the untouched
    // Cloud reference. Public modern TMP/TextCore APIs only; no native pointers,
    // game font asset mutations, OS font factory, or dialogue/controller methods.
    internal sealed class StaticTmpFonts
    {
        readonly Func<object,bool> alive;readonly Func<object,int> id;
        readonly List<object> owned=new List<object>();
        readonly Dictionary<int,object> clones=new Dictionary<int,object>();
        readonly HashSet<int> cloneIds=new HashSet<int>();
        readonly Dictionary<string,Page> pages=new Dictionary<string,Page>();
        readonly Dictionary<int,object> textures=new Dictionary<int,object>();
        readonly Dictionary<int,int> cloneOrigins=new Dictionary<int,int>();
        readonly Dictionary<string,long> failedPageTemplates=new Dictionary<string,long>();
        readonly Dictionary<int,IList> glyphs=new Dictionary<int,IList>();
        readonly Dictionary<string,long> retries=new Dictionary<string,long>();
        readonly System.Diagnostics.Stopwatch clock=System.Diagnostics.Stopwatch.StartNew();
        Dictionary<string,object> index;IList pageIndex;bool indexTried;
        internal string Status="not-requested";
        internal Exception LastError;
        internal int PageCount{get{return pages.Count;}}
        internal int CloneCount{get{return clones.Count;}}
        internal int MaterialAdjustments;
        sealed class Page {internal object Font,Glyphs,Characters;internal bool Failed;internal readonly HashSet<int> Codes=new HashSet<int>();}
        internal bool IsClone(object value){return alive(value)&&cloneIds.Contains(id(value));}
        internal StaticTmpFonts(Func<object,bool> alive,Func<object,int> id){this.alive=alive;this.id=id;}
        static Type T(string name){return UnityBridgeRuntime.FindType(name);}
        static object Get(object o,string name){var p=o.GetType().GetProperty(name);if(p!=null)return p.GetValue(o,null);var f=o.GetType().GetField(name);return f==null?null:f.GetValue(o);}
        static void Set(object o,string name,object value)
        {var p=o.GetType().GetProperty(name);if(p!=null&&p.CanWrite){p.SetValue(o,value,null);return;}var f=o.GetType().GetField(name);if(f==null)throw new MissingMemberException(name);f.SetValue(o,value);}
        static void Optional(object o,string name,object value){try{Set(o,name,value);}catch{}}
        static int N(object value){return Convert.ToInt32(value);}
        static float F(object value){return Convert.ToSingle(value,System.Globalization.CultureInfo.InvariantCulture);}
        static Stream Resource(string name)
        {return typeof(StaticTmpFonts).Assembly.GetManifestResourceStream("Fusion.UnityEmbedded.FontAssets."+name)??throw new FileNotFoundException("Embedded font resource unavailable");}
        void Index()
        {
            if(index!=null)return;if(indexTried)throw new InvalidDataException("Font index unavailable");indexTried=true;
            Dictionary<string,object> parsed;
            using(var input=Resource("font-index.json"))using(var reader=new StreamReader(input))
            {if(input.Length>2*1024*1024)throw new InvalidDataException();parsed=BridgeJson.Parse(reader.ReadToEnd()) as Dictionary<string,object>;}
            if(parsed==null||N(parsed["schema"])!=1||N(parsed["size"])!=2048||N(parsed["pointSize"])!=36)throw new InvalidDataException();
            var parsedPages=parsed["pages"] as IList;var rows=parsed["glyphs"] as IList;
            if(parsedPages==null||parsedPages.Count>32||rows==null||rows.Count>50000)throw new InvalidDataException();
            var validated=new Dictionary<int,IList>();
            foreach(var item in rows){var row=item as IList;if(row==null||row.Count!=9)throw new InvalidDataException();int code=N(row[0]),page=N(row[1]);if(code<0||code>0xffff||page<0||page>=parsedPages.Count||N(row[2])<0||N(row[3])<0||N(row[4])<0||N(row[5])<0||N(row[2])+N(row[4])>2048||N(row[3])+N(row[5])>2048)throw new InvalidDataException();validated.Add(code,row);}
            foreach(var pair in validated)glyphs.Add(pair.Key,pair.Value);pageIndex=parsedPages;index=parsed;
        }
        object Cast(object value,Type type)
        {
            if(value==null)return null;if(type.IsInstanceOfType(value))return value;
            var wrapper=T("Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase");
            var method=wrapper==null?null:wrapper.GetMethods().FirstOrDefault(m=>m.Name=="TryCast"&&m.IsGenericMethodDefinition&&m.GetParameters().Length==0);
            return method==null?null:method.MakeGenericMethod(type).Invoke(value,null);
        }
        object Create(Type type)
        {
            var scriptable=T("UnityEngine.ScriptableObject");if(scriptable==null)throw new NotSupportedException();
            var create=scriptable.GetMethod("CreateInstance",BindingFlags.Public|BindingFlags.Static,null,new[]{typeof(Type)},null);
            object value=null;if(create!=null)try{value=Cast(create.Invoke(null,new object[]{type}),type);}catch{}
            if(!alive(value))
            {
                var bridge=T("Il2CppInterop.Runtime.Il2CppType");var from=bridge==null?null:bridge.GetMethod("From",new[]{typeof(Type),typeof(bool)});
                object native=from==null?null:from.Invoke(null,new object[]{type,false});if(native==null)throw new NotSupportedException();
                create=scriptable.GetMethods(BindingFlags.Public|BindingFlags.Static).FirstOrDefault(m=>m.Name=="CreateInstance"&&!m.IsGenericMethod&&m.GetParameters().Length==1&&m.GetParameters()[0].ParameterType.IsInstanceOfType(native));
                value=create==null?null:Cast(create.Invoke(null,new[]{native}),type);
            }
            if(!alive(value))throw new InvalidOperationException();owned.Add(value);return value;
        }
        object Clone(object original)
        {
            var unity=T("UnityEngine.Object");var instantiate=unity.GetMethod("Instantiate",BindingFlags.Public|BindingFlags.Static,null,new[]{unity},null);
            object clone=instantiate==null?null:Cast(instantiate.Invoke(null,new[]{original}),original.GetType());
            if(!alive(clone))throw new InvalidOperationException();owned.Add(clone);return clone;
        }
        static object NewList(Type type,IEnumerable values)
        {object list=Activator.CreateInstance(type);var add=type.GetMethods().First(m=>m.Name=="Add"&&m.GetParameters().Length==1);if(values!=null)foreach(object value in values)add.Invoke(list,new[]{value});return list;}
        static object ArrayFor(Type type,Type element,object value)
        {
            if(type.IsArray){var array=Array.CreateInstance(element,1);array.SetValue(value,0);return array;}
            var length=type.GetConstructor(new[]{typeof(long)})??type.GetConstructor(new[]{typeof(int)});if(length==null)throw new NotSupportedException();
            object result=length.Invoke(new[]{length.GetParameters()[0].ParameterType==typeof(long)?(object)1L:1});
            type.GetMethod("set_Item",new[]{typeof(int),element}).Invoke(result,new object[]{0,value});return result;
        }
        object Texture(int page)
        {
            object cached;if(textures.TryGetValue(page,out cached)&&alive(cached))return cached;
            var row=(Dictionary<string,object>)pageIndex[page];string name=(string)row["file"];
            if(name!="page-"+page.ToString("00")+".sdf.gz")throw new InvalidDataException();byte[] zipped;
            using(var stream=Resource(name))using(var copy=new MemoryStream()){if(stream.Length>1024*1024)throw new InvalidDataException();byte[] buffer=new byte[8192];int count;while((count=stream.Read(buffer,0,buffer.Length))>0)copy.Write(buffer,0,count);zipped=copy.ToArray();}
            using(var hash=SHA256.Create())if(BitConverter.ToString(hash.ComputeHash(zipped)).Replace("-","")!=(string)row["sha256"])throw new InvalidDataException();
            int size=N(index["size"]);byte[] pixels=new byte[size*size];
            using(var zip=new GZipStream(new MemoryStream(zipped),CompressionMode.Decompress))
            {int offset=0,count;while(offset<pixels.Length&&(count=zip.Read(pixels,offset,pixels.Length-offset))>0)offset+=count;if(offset!=pixels.Length||zip.ReadByte()!=-1)throw new InvalidDataException();}
            var texture=T("UnityEngine.Texture2D");var format=T("UnityEngine.TextureFormat");if(texture==null||format==null)throw new NotSupportedException();
            object result=CreateLinearTexture(texture,format,size);if(!alive(result))throw new InvalidOperationException();owned.Add(result);
            var load=texture.GetMethod("LoadRawTextureData",new[]{typeof(IntPtr),typeof(int)});
            if(load==null)throw new NotSupportedException();var pinned=GCHandle.Alloc(pixels,GCHandleType.Pinned);
            try{load.Invoke(result,new object[]{pinned.AddrOfPinnedObject(),pixels.Length});}finally{pinned.Free();}
            texture.GetMethod("Apply",new[]{typeof(bool),typeof(bool)}).Invoke(result,new object[]{false,true});
            var filter=T("UnityEngine.FilterMode");var wrap=T("UnityEngine.TextureWrapMode");if(filter!=null)Optional(result,"filterMode",Enum.Parse(filter,"Bilinear"));if(wrap!=null)Optional(result,"wrapMode",Enum.Parse(wrap,"Clamp"));
            Optional(result,"name","Fusion CJK static atlas "+page);textures[page]=result;return result;
        }
        static object CreateLinearTexture(Type texture,Type format,int size)
        {
            // Some IL2CPP interop assemblies omit the bool mipChain overload,
            // while exposing the equivalent explicit mipCount API. Both paths
            // must create exactly one linear Alpha8 level; the four-argument
            // overload cannot preserve the required linear sampling contract.
            object alpha=Enum.Parse(format,"Alpha8");
            var simple=texture.GetConstructor(new[]{typeof(int),typeof(int),format,typeof(bool),typeof(bool)});
            if(simple!=null)
            {
                try{return simple.Invoke(new object[]{size,size,alpha,false,true});}
                catch(TargetInvocationException ex)
                {if(!(ex.InnerException is NotSupportedException)&&!(ex.InnerException is MissingMethodException))throw;}
            }
            var levels=texture.GetConstructor(new[]{typeof(int),typeof(int),format,typeof(int),typeof(bool)});
            if(levels==null)throw new NotSupportedException("Linear single-level texture API unavailable");
            return levels.Invoke(new object[]{size,size,alpha,1,true});
        }
        Page MakePage(int number,object template)
        {
            var type=template.GetType();var glyph=T("UnityEngine.TextCore.Glyph");var character=T("TMPro.TMP_Character");
            var face=T("UnityEngine.TextCore.FaceInfo");var shader=T("UnityEngine.Shader");var material=T("UnityEngine.Material");
            if(glyph==null||character==null||face==null||shader==null||material==null||type.GetProperty("glyphTable")==null||type.GetProperty("characterTable")==null)throw new NotSupportedException();
            object font=Create(type),texture=Texture(number),sourceMaterial=Get(template,"material");
            // ScriptableObject constructors do not stamp TMP's serialized asset
            // version. Without it modern TMP enters its legacy upgrade path and
            // dereferences the absent legacy FaceInfo (observed on Unity 2021.3).
            string version=Get(template,"version") as string;
            if(string.IsNullOrEmpty(version))throw new NotSupportedException("TMP asset version unavailable");
            Set(font,"version",version);
            var feature=type.GetProperty("fontFeatureTable");
            if(feature!=null&&feature.CanWrite)Set(font,"fontFeatureTable",Activator.CreateInstance(feature.PropertyType));
            object originalShader=sourceMaterial==null?null:Get(sourceMaterial,"shader"),chosen=null;string shaderName=originalShader==null?null:Get(originalShader,"name") as string;
            if(shaderName!=null&&shaderName.StartsWith("TextMeshPro/",StringComparison.Ordinal))chosen=originalShader;
            if(!alive(chosen))chosen=shader.GetMethod("Find",new[]{typeof(string)}).Invoke(null,new object[]{"TextMeshPro/Distance Field"});
            if(!alive(chosen))throw new NotSupportedException();
            object mat=alive(sourceMaterial)&&chosen==originalShader?material.GetConstructor(new[]{material}).Invoke(new[]{sourceMaterial}):material.GetConstructor(new[]{shader}).Invoke(new[]{chosen});
            if(!alive(mat))throw new InvalidOperationException();owned.Add(mat);Set(mat,"mainTexture",texture);
            var setFloat=material.GetMethod("SetFloat",new[]{typeof(string),typeof(float)});
            foreach(var pair in new[]{new object[]{"_TextureWidth",(float)N(index["size"])},new object[]{"_TextureHeight",(float)N(index["size"])},new object[]{"_GradientScale",F(index["spread"])+1},new object[]{"_WeightNormal",0f},new object[]{"_WeightBold",0.5f}})setFloat.Invoke(mat,pair);
            if(!PreserveFallbackFace(sourceMaterial,mat))throw new NotSupportedException("Static font material preset unavailable");
            Set(font,"material",mat);Optional(font,"name","Fusion CJK static page "+number);
            object faceInfo=Activator.CreateInstance(face);
            foreach(var pair in new[]{new object[]{"familyName","Fusion CJK Fallback"},new object[]{"styleName","Regular"},new object[]{"pointSize",N(index["pointSize"])},new object[]{"scale",1f},new object[]{"lineHeight",F(index["lineHeight"])},new object[]{"ascentLine",F(index["ascender"])},new object[]{"descentLine",F(index["descender"])},new object[]{"capLine",27f},new object[]{"baseline",0f}})Optional(faceInfo,(string)pair[0],pair[1]);
            Set(font,"faceInfo",faceInfo);Set(font,"atlasWidth",N(index["size"]));Set(font,"atlasHeight",N(index["size"]));Set(font,"atlasPadding",N(index["spread"]));
            var population=type.GetProperty("atlasPopulationMode");Set(font,"atlasPopulationMode",Enum.Parse(population.PropertyType,"Static"));
            var textureProperty=type.GetProperty("atlasTextures");Set(font,"atlasTextures",ArrayFor(textureProperty.PropertyType,T("UnityEngine.Texture2D"),texture));
            var render=type.GetProperty("atlasRenderMode");if(render!=null&&render.CanWrite)Set(font,"atlasRenderMode",Enum.Parse(render.PropertyType,"SDFAA"));
            var page=new Page{Font=font,Glyphs=NewList(type.GetProperty("glyphTable").PropertyType,null),Characters=NewList(type.GetProperty("characterTable").PropertyType,null)};
            Set(font,"glyphTable",page.Glyphs);Set(font,"characterTable",page.Characters);return page;
        }
        Page MakeLegacyPage(int number,object template)
        {
            // TMP 1.x exposes FaceInfo/TMP_Glyph instead of TextCore. Build the
            // same owned SDF pages through that public API, without game types.
            var type=template.GetType();var glyph=T("TMPro.TMP_Glyph");var face=T("TMPro.FaceInfo");
            var material=T("UnityEngine.Material");var shader=T("UnityEngine.Shader");
            if(glyph==null||face==null||material==null||shader==null)throw new NotSupportedException();
            object font=Create(type),texture=Texture(number),sourceMaterial=Get(template,"material");
            var kind=type.GetField("fontAssetType");if(kind!=null)Set(font,"fontAssetType",Enum.Parse(kind.FieldType,"SDF"));
            Set(font,"atlas",texture);Optional(font,"name","Fusion CJK legacy page "+number);
            object chosen=shader.GetMethod("Find",new[]{typeof(string)}).Invoke(null,new object[]{"TextMeshPro/Mobile/Distance Field"});
            if(!alive(chosen))chosen=Get(sourceMaterial,"shader");
            object mat=material.GetConstructor(new[]{shader}).Invoke(new[]{chosen});owned.Add(mat);Set(mat,"mainTexture",texture);
            var setFloat=material.GetMethod("SetFloat",new[]{typeof(string),typeof(float)});
            foreach(var pair in new[]{new object[]{"_TextureWidth",(float)N(index["size"])},new object[]{"_TextureHeight",(float)N(index["size"])},new object[]{"_GradientScale",F(index["spread"])+1},new object[]{"_WeightNormal",0f},new object[]{"_WeightBold",0.5f}})setFloat.Invoke(mat,pair);
            Set(font,"material",mat);
            // Match actual cap height, not nominal point size. Pixel fonts often
            // draw a 10-unit glyph at 20 points. Only our missing-glyph page is
            // scaled; original Latin glyphs, shared assets and label sizes stay put.
            float scale=LegacyScale(template);
            object info=Activator.CreateInstance(face);
            foreach(var pair in new[]{new object[]{"Name","Fusion CJK Fallback"},new object[]{"PointSize",F(index["pointSize"])},new object[]{"Scale",scale},new object[]{"LineHeight",F(index["lineHeight"])},new object[]{"Ascender",F(index["ascender"])},new object[]{"Descender",F(index["descender"])},new object[]{"CapHeight",27f},new object[]{"AtlasWidth",F(index["size"])},new object[]{"AtlasHeight",F(index["size"])},new object[]{"Padding",F(index["spread"])},new object[]{"SubSize",0.5f},new object[]{"Underline",-4f},new object[]{"UnderlineThickness",2f}})Set(info,(string)pair[0],pair[1]);
            type.GetMethod("AddFaceInfo",new[]{face}).Invoke(font,new[]{info});
            var rows=glyphs.Values.Where(r=>N(r[1])==number).ToArray();var array=Array.CreateInstance(glyph,rows.Length);var page=new Page{Font=font};
            for(int i=0;i<rows.Length;i++)
            {
                var row=rows[i];object item=Activator.CreateInstance(glyph);Set(item,"id",N(row[0]));
                string[] fields={"x","y","width","height","xOffset","yOffset","xAdvance"};
                for(int j=0;j<fields.Length;j++)Set(item,fields[j],F(row[j+2]));Set(item,"scale",1f);
                array.SetValue(item,i);page.Codes.Add(N(row[0]));
            }
            type.GetMethod("AddGlyphInfo",new[]{array.GetType()}).Invoke(font,new object[]{array});
            var kerning=T("TMPro.KerningTable");if(kerning!=null)type.GetMethod("AddKerningInfo",new[]{kerning}).Invoke(font,new[]{Activator.CreateInstance(kerning)});
            var fallback=type.GetField("fallbackFontAssets");if(fallback==null)throw new NotSupportedException();
            fallback.SetValue(font,NewList(fallback.FieldType,null));type.GetMethod("ReadFontDefinition",Type.EmptyTypes).Invoke(font,null);return page;
        }
        float LegacyScale(object template)
        {
            try
            {
                var table=Get(template,"characterDictionary") as IDictionary;var face=Get(template,"fontInfo");
                if(face==null)return 1f;
                // Serialized fonts can be prepared before TMP builds their runtime
                // character dictionary. Use the authored cap height at that stage.
                float height=F(Get(face,"CapHeight")),faceScale=F(Get(face,"Scale"));
                if(table!=null&&table.Contains((int)'H')){object cap=table[(int)'H'];height=F(Get(cap,"height"))*F(Get(cap,"scale"));}
                if(faceScale==0)faceScale=1f; // TMP ReadFontDefinition's default.
                float native=height*faceScale/F(Get(face,"PointSize"));
                IList han;if(!glyphs.TryGetValue('国',out han))return 1f;
                float replacement=F(han[5])/F(index["pointSize"]),ratio=native/replacement;
                return !float.IsNaN(ratio)&&!float.IsInfinity(ratio)&&ratio>=0.2f&&ratio<=2f?Math.Min(1f,ratio):1f;
            }catch{return 1f;}
        }
        // A centered outline that fits the game's heavy font can consume the
        // entire face of a regular CJK fallback. Keep the fallback face at the
        // source preset's inner edge and grow the outline outward instead.
        // Only owned atlas materials (including TMP's cached fallback copies)
        // are changed. The original material, font, color and size stay intact.
        bool PreserveFallbackFace(object source,object material)
        {
            if(!alive(source)||!alive(material))return true;
            var type=material.GetType();var has=type.GetMethod("HasProperty",new[]{typeof(string)});
            var get=type.GetMethod("GetFloat",new[]{typeof(string)});var set=type.GetMethod("SetFloat",new[]{typeof(string),typeof(float)});
            if(has==null||get==null||set==null)return true;
            if(!(bool)has.Invoke(source,new object[]{"_OutlineWidth"})||!(bool)has.Invoke(source,new object[]{"_FaceDilate"}))return true;
            float outline=F(get.Invoke(source,new object[]{"_OutlineWidth"})),face=F(get.Invoke(source,new object[]{"_FaceDilate"}));
            if(float.IsNaN(outline)||float.IsInfinity(outline)||float.IsNaN(face)||float.IsInfinity(face))return false;
            if(!(bool)has.Invoke(material,new object[]{"_FaceDilate"}))return false;
            float wanted=Math.Max(-1f,Math.Min(1f,face+Math.Max(0f,outline)));
            float current=F(get.Invoke(material,new object[]{"_FaceDilate"}));
            if(Math.Abs(wanted-current)>0.0001f){set.Invoke(material,new object[]{"_FaceDilate",wanted});MaterialAdjustments++;}
            return true;
        }
        internal bool PrepareMaterials(object target,string text)
        {
            try
            {
                object font=Get(target,"font");if(!IsClone(font))return true;
                object source=Get(target,"fontSharedMaterial");if(!alive(source))return false;
                var material=T("UnityEngine.Material");var manager=T("TMPro.TMP_MaterialManager");
                var fallback=manager==null||material==null?null:manager.GetMethod("GetFallbackMaterial",BindingFlags.Public|BindingFlags.Static,null,new[]{material,material},null);
                if(fallback==null)return true;
                var needed=new HashSet<int>();bool tag=false;
                foreach(char c in text){if(c=='<'){tag=true;continue;}if(c=='>'){tag=false;continue;}IList row;if(!tag&&c>=0x2e80&&glyphs.TryGetValue(c,out row))needed.Add(N(row[1]));}
                foreach(int number in needed)
                {
                    int origin;string pageKey=font.GetType().GetProperty("glyphTable")==null&&cloneOrigins.TryGetValue(id(font),out origin)?origin+":"+number:"modern:"+number;
                    Page page;if(!pages.TryGetValue(pageKey,out page)||!alive(page.Font))continue;
                    object pageMaterial=Get(page.Font,"material");if(!alive(pageMaterial))return false;
                    object cached=fallback.Invoke(null,new[]{source,pageMaterial});if(!alive(cached))return false;
                    // Do not edit a game preset even if an unexpected API returns it.
                    object ownedTexture=Get(pageMaterial,"mainTexture"),actualTexture=Get(cached,"mainTexture");
                    if(id(cached)==id(source)||!alive(ownedTexture)||!alive(actualTexture)||id(ownedTexture)!=id(actualTexture))return false;
                    if(!PreserveFallbackFace(source,cached))return false;
                }
                return true;
            }
            catch(Exception error){while(error is TargetInvocationException&&error.InnerException!=null)error=error.InnerException;LastError=error;Status="static-material-"+error.GetType().Name;return false;}
        }
        void Add(Page page,int code)
        {
            if(page.Codes.Contains(code))return;IList row=glyphs[code];var glyph=T("UnityEngine.TextCore.Glyph");var metrics=T("UnityEngine.TextCore.GlyphMetrics");var rect=T("UnityEngine.TextCore.GlyphRect");var character=T("TMPro.TMP_Character");
            object m=Activator.CreateInstance(metrics,new object[]{F(row[4]),F(row[5]),F(row[6]),F(row[7]),F(row[8])});
            // Cloud's index is top-left; modern TextCore rectangles are bottom-left.
            object r=Activator.CreateInstance(rect,new object[]{N(row[2]),N(index["size"])-N(row[3])-N(row[5]),N(row[4]),N(row[5])});
            object g=Activator.CreateInstance(glyph,new object[]{(uint)code,m,r,1f,0});
            var ctor=character.GetConstructor(new[]{typeof(uint),page.Font.GetType(),glyph})??character.GetConstructor(new[]{typeof(uint),glyph});
            if(ctor==null)throw new NotSupportedException();object ch=ctor.Invoke(ctor.GetParameters().Length==3?new object[]{(uint)code,page.Font,g}:new object[]{(uint)code,g});
            page.Glyphs.GetType().GetMethod("Add",new[]{glyph}).Invoke(page.Glyphs,new[]{g});page.Characters.GetType().GetMethod("Add",new[]{character}).Invoke(page.Characters,new[]{ch});page.Codes.Add(code);
        }
        internal object Prepare(object original,string text)
        {
            if(!alive(original)||original.GetType().FullName!="TMPro.TMP_FontAsset")return null;
            bool legacy=original.GetType().GetProperty("glyphTable")==null;
            if(legacy?(T("TMPro.TMP_Glyph")==null||original.GetType().GetField("fallbackFontAssets")==null):T("UnityEngine.TextCore.Glyph")==null){Status="static-TMP-api-unavailable";return null;}
            int origin; if(!cloneOrigins.TryGetValue(id(original),out origin))origin=id(original);
            string key=origin.ToString();long retry;if(retries.TryGetValue(key,out retry)&&clock.ElapsedMilliseconds<retry)return null;
            string stage="index";
            try
            {
                Index();var needed=new HashSet<int>();bool tag=false;
                foreach(char c in text){if(c=='<'){tag=true;continue;}if(c=='>'){tag=false;continue;}if(!tag&&c>=0x2e80){if(char.IsSurrogate(c)||!glyphs.ContainsKey(c)){Status="static-glyph-unavailable";return null;}needed.Add(c);}}
                if(needed.Count==0||needed.Count>512)return null;
                var pageFonts=new List<object>();stage="page";
                foreach(var group in needed.GroupBy(c=>N(glyphs[c][1])))
                {
                    string pageKey=legacy?key+":"+group.Key:"modern:"+group.Key;
                    Page page;if(!pages.TryGetValue(pageKey,out page)||!alive(page.Font))
                    {
                        if(pages.Count>=256)throw new InvalidOperationException("Static font family limit reached");
                        string templateKey=id(original)+":"+group.Key;long failedUntil;
                        if(failedPageTemplates.TryGetValue(templateKey,out failedUntil)&&clock.ElapsedMilliseconds<failedUntil)throw new InvalidDataException("Static font template retry deferred");
                        int firstOwned=owned.Count;if(failedPageTemplates.Count<2048)failedPageTemplates[templateKey]=clock.ElapsedMilliseconds+30000;
                        try{page=legacy?MakeLegacyPage(group.Key,original):MakePage(group.Key,original);pages[pageKey]=page;failedPageTemplates.Remove(templateKey);}
                        catch{DestroyOwned(firstOwned);throw;}
                    }
                    if(page.Failed)throw new InvalidDataException("Static glyph table unavailable");
                    try
                    {
                        stage="glyph-add";bool changed=false;foreach(int code in group){if(!page.Codes.Contains(code)){Add(page,code);changed=true;}}
                        stage="page-definition";if(changed)page.Font.GetType().GetMethod("ReadFontAssetDefinition",Type.EmptyTypes).Invoke(page.Font,null);
                    }
                    catch{page.Failed=true;throw;}
                    pageFonts.Add(page.Font);
                }
                stage="clone";object clone;
                if(cloneIds.Contains(id(original)))clone=original;
                else if(!clones.TryGetValue(id(original),out clone)||!alive(clone))
                {if(clones.Count>=128)throw new InvalidOperationException();clone=Clone(original);clones[id(original)]=clone;cloneIds.Add(id(clone));cloneOrigins[id(clone)]=origin;}
                var fallback=clone.GetType().GetProperty("fallbackFontAssetTable");var legacyFallback=clone.GetType().GetField("fallbackFontAssets");
                if(legacy?legacyFallback==null:(fallback==null||!fallback.CanWrite))throw new NotSupportedException();
                var values=new List<object>();object old=legacy?legacyFallback.GetValue(clone):fallback.GetValue(clone,null);var enumerable=old as IEnumerable;
                if(enumerable!=null)foreach(object value in enumerable){if(values.Count>=128)throw new InvalidDataException();if(alive(value))values.Add(value);}
                else if(old!=null)
                {
                    var listType=old.GetType();if(!listType.IsGenericType||listType.GetGenericTypeDefinition().FullName!="Il2CppSystem.Collections.Generic.List`1")throw new NotSupportedException();
                    int count=N(Get(old,"Count"));if(count<0||count>128)throw new InvalidDataException();var item=listType.GetMethod("get_Item",new[]{typeof(int)});
                    for(int i=0;i<count;i++){object value=item.Invoke(old,new object[]{i});if(alive(value))values.Add(value);}
                }
                foreach(object value in pageFonts)if(!values.Any(v=>id(v)==id(value)))values.Add(value);
                if(legacy)legacyFallback.SetValue(clone,NewList(legacyFallback.FieldType,values));else fallback.SetValue(clone,NewList(fallback.PropertyType,values),null);
                clone.GetType().GetMethod(legacy?"ReadFontDefinition":"ReadFontAssetDefinition",Type.EmptyTypes).Invoke(clone,null);
                LastError=null;Status="static-pages-ready";return clone;
            }
            catch(Exception error){while(error is TargetInvocationException&&error.InnerException!=null)error=error.InnerException;LastError=error;Status="static-"+stage+"-"+error.GetType().Name;retries[key]=clock.ElapsedMilliseconds+10000;return null;}
        }
        // Called only after all component references were restored by the bridge.
        internal void Dispose()
        {
            DestroyOwned(0);pages.Clear();textures.Clear();failedPageTemplates.Clear();clones.Clear();cloneIds.Clear();cloneOrigins.Clear();glyphs.Clear();
        }
        void DestroyOwned(int first)
        {
            var unity=T("UnityEngine.Object");var destroy=unity==null?null:unity.GetMethod("Destroy",BindingFlags.Public|BindingFlags.Static,null,new[]{unity},null);
            if(destroy!=null)for(int i=owned.Count-1;i>=first;i--)if(alive(owned[i]))try{destroy.Invoke(null,new[]{owned[i]});}catch{}
            owned.RemoveRange(first,owned.Count-first);
        }
    }
}
