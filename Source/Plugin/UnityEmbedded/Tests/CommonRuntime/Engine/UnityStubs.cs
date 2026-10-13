using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Reflection;
namespace Il2CppSystem {public class Type {public System.Type Managed; public Type(System.Type t){Managed=t;}}}
namespace Il2CppInterop.Runtime
{
    public static class Il2CppType
    {
        public static Type Unavailable,Missing;public static readonly Dictionary<Type,int> FromCalls=new();
        public static Il2CppSystem.Type From(Type t)=>From(t,true);
        public static Il2CppSystem.Type From(Type t,bool throwIfNotSupported)
        {
            FromCalls[t]=FromCalls.TryGetValue(t,out int count)?count+1:1;
            if(t==Unavailable)throw new System.Reflection.TargetInvocationException(new InvalidOperationException("DO_NOT_WRITE_EXCEPTION_TEXT"));
            if(t==Missing){if(throwIfNotSupported)throw new ArgumentException("DO_NOT_WRITE_EXCEPTION_TEXT");return null;}
            return new(t);
        }
    }
    public static class DelegateSupport {public static T ConvertDelegate<T>(Delegate d)=> (T)Activator.CreateInstance(typeof(T),d);}
}
namespace Il2CppInterop.Runtime.InteropTypes {public class Il2CppObjectBase {public virtual T TryCast<T>() where T:class =>this as T;}}
namespace Il2CppInterop.Runtime.InteropTypes.Arrays {public class Il2CppStringArray {public string[] Values;public Il2CppStringArray(string[] values){Values=values;}}public class Il2CppStructArray<T> {readonly T[] values;public Il2CppStructArray(T[] source){values=source;}public int Length=>values.Length;public T this[int index]=>values[index];}}
namespace UnityEngine
{
    public struct Vector2 {public float x,y;public Vector2(float x,float y){this.x=x;this.y=y;}}
    public enum FindObjectsSortMode {None,InstanceID}
    public class Object:Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
    {
        static int next;readonly int id=++next;public bool destroyed;public int GetInstanceID()=>id;
        public static implicit operator bool(Object o)=>o!=null&&!o.destroyed;
        public static readonly List<Object> Scene=new();public static int Queries;public static bool WrapResults,ModernUnavailable,EnumerationUnavailable;
        sealed class BadSequence:System.Collections.IEnumerable,System.Collections.IEnumerator {public System.Collections.IEnumerator GetEnumerator()=>this;public bool MoveNext()=>throw new System.Reflection.TargetInvocationException(new InvalidOperationException("DO_NOT_WRITE_EXCEPTION_TEXT"));public object Current=>null;public void Reset(){}}
        public sealed class NativeAlias:Object {public Object Target;public override T TryCast<T>()=>Target as T;}
        public static T[] FindObjectsByType<T>(FindObjectsSortMode mode)=>throw new NotSupportedException("Method unstripping failed");
        public static T[] FindObjectsOfType<T>()=>throw new NotSupportedException("Method unstripping failed");
        public static System.Collections.IEnumerable FindObjectsByType(Il2CppSystem.Type type,FindObjectsSortMode mode){Queries++;if(ModernUnavailable)throw new NotSupportedException();if(EnumerationUnavailable)return new BadSequence();return Scene.Where(o=>type.Managed.IsInstanceOfType(o)&&(!(o is Component c)||c.gameObject.activeInHierarchy)).Select(o=>WrapResults?(Object)new NativeAlias{Target=o}:o).ToArray();}
        public static Object[] FindObjectsOfType(Il2CppSystem.Type type){Queries++;return Scene.Where(o=>type.Managed.IsInstanceOfType(o)&&(!(o is Component c)||c.gameObject.activeInHierarchy)).ToArray();}
    }
    public class Resources {public static T[] FindObjectsOfTypeAll<T>()=>throw new NotSupportedException("Method unstripping failed");public static Object[] FindObjectsOfTypeAll(Il2CppSystem.Type type)=>Object.Scene.Where(type.Managed.IsInstanceOfType).ToArray();}
    public class GameObject:Object {[MethodImpl(MethodImplOptions.NoInlining)] public void SetActive(bool value){activeInHierarchy=value;if(value)foreach(var c in Object.Scene.OfType<UnityEngine.UI.Text>().Where(t=>t.gameObject==this))c.OnEnable();} public bool activeInHierarchy{get;set;}=true;public SceneManagement.Scene scene{get;set;}=new SceneManagement.Scene(true);}
    public class Component:Object {public GameObject gameObject{get;set;}=new();public T GetComponentInParent<T>() where T:class=>null;}
    public class TextMesh:Component {public string text{get;set;}public Font font{get;set;}=new();}
    public class LaterTextMesh:TextMesh{}
    public enum FontStyle {Normal,Bold,Italic,BoldAndItalic}
#if !STRIPPED_LEGACY
    public struct CharacterInfo {public int minY,maxY;}
#endif
    public class Font:Object
    {
        public int fontSize{get;set;}=24;public int lineHeight{get;set;}=24;
        public bool Chinese,AtlasFull;public int Requests;public readonly HashSet<string> Allocated=new();
        public static int AllRequests;public static int ActiveAtlasCallbacks;
        static event Action<Font> Rebuilt;
        public static event Action<Font> textureRebuilt {add{Rebuilt+=value;ActiveAtlasCallbacks++;}remove{Rebuilt-=value;ActiveAtlasCallbacks--;}}
        public void Rebuild(){Allocated.Clear();Rebuilt?.Invoke(this);}
        public void RequestCharactersInTexture(string text,int size,FontStyle style){Requests++;AllRequests++;foreach(char c in text)if(!AtlasFull)Allocated.Add(c+"|"+size+"|"+(int)style);}
#if !STRIPPED_LEGACY
        public bool GetCharacterInfo(char c,out CharacterInfo info,int size,FontStyle style){info=new CharacterInfo{maxY=(int)(size*(c=='H'?0.75:1.0))};return !AtlasFull&&HasCharacter(c)&&Allocated.Contains(c+"|"+size+"|"+(int)style);}
#endif
        public static int Factories;public static bool FailFactory,ManagedArrayAllowed;
        public readonly HashSet<char> ExtraCharacters=new HashSet<char>(); public bool HasCharacter(char c)=>c<256||Chinese||ExtraCharacters.Contains(c);
        public static Font CreateDynamicFontFromOSFont(string name,int size)=>throw new NotSupportedException("Method unstripping failed");
        public static Font CreateDynamicFontFromOSFont(string[] names,int size){if(!ManagedArrayAllowed||FailFactory)throw new NotSupportedException();return new Font{Chinese=true};}
        public static Font CreateDynamicFontFromOSFont(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray names,int size){Factories++;if(FailFactory)throw new InvalidOperationException("font unavailable");return new Font{Chinese=true};}
    }
    public class Canvas
    {
        public class WillRenderCanvases {readonly Delegate d;public WillRenderCanvases(Delegate d){this.d=d;}public void Invoke()=>d.DynamicInvoke();}
        static readonly List<WillRenderCanvases> callbacks=new();
        public static void add_preWillRenderCanvases(WillRenderCanvases d)=>callbacks.Add(d);
        public static void remove_preWillRenderCanvases(WillRenderCanvases d)=>callbacks.Remove(d);
        public static void Draw(){foreach(var d in callbacks.ToArray())d.Invoke();}
        public static int CallbackCount=>callbacks.Count;
    }
}
namespace UnityEngine.UI
{
    public enum HorizontalWrapMode {Wrap,Overflow}
    public enum VerticalWrapMode {Truncate,Overflow}
    public struct Settings {public UnityEngine.Font font;public UnityEngine.Vector2 pivot,generationExtents;public int fontSize;public float lineSpacing,scaleFactor;public UnityEngine.FontStyle fontStyle;public HorizontalWrapMode horizontalOverflow;public VerticalWrapMode verticalOverflow;}
    public class Rect {public float height{get;set;}=200;public float width{get;set;}=800;}
    public class RectTransform {public Rect rect{get;set;}=new();}
    public class Text:UnityEngine.Component
    {
        string value;UnityEngine.Font current=new();
        public int fontSize{get;set;}=24;public UnityEngine.FontStyle fontStyle{get;set;}
        public bool resizeTextForBestFit{get;set;}public int resizeTextMinSize{get;set;}=12;public int resizeTextMaxSize{get;set;}=24;
        public RectTransform rectTransform{get;set;}=new();
        public VerticalWrapMode verticalOverflow{get;set;}=VerticalWrapMode.Truncate;
        public HorizontalWrapMode horizontalOverflow{get;set;}public float lineSpacing{get;set;}=1;public float pixelsPerUnit{get;set;}=1;
        [MethodImpl(MethodImplOptions.NoInlining)]public Settings GetGenerationSettings(UnityEngine.Vector2 size)=>new(){font=font,fontSize=fontSize,pivot=new(0,1),generationExtents=size,lineSpacing=lineSpacing,scaleFactor=pixelsPerUnit};
        public Generator cachedTextGeneratorForLayout{get;}=new();
        public float preferredWidth{[MethodImpl(MethodImplOptions.NoInlining)]get=>cachedTextGeneratorForLayout.GetPreferredWidth(text,GetGenerationSettings(new()));}
        public float preferredHeight{[MethodImpl(MethodImplOptions.NoInlining)]get=>cachedTextGeneratorForLayout.GetPreferredHeight(text,GetGenerationSettings(new()));}
        public sealed class Generator {public int Invalidations;public void Invalidate(){Invalidations++;}public float GetPreferredWidth(string text,Settings s)=>(text??"").Length*s.fontSize;public float GetPreferredHeight(string text,Settings s)=>s.fontSize*s.lineSpacing;}
        public Generator cachedTextGenerator{get;}=new();public int DirtyCalls;
        public void SetVerticesDirty(){DirtyCalls++;}
        public bool IgnoreTextWrites,IgnoreFontWrites,ThrowTextWrites;public int FontWrites;
        [MethodImpl(MethodImplOptions.NoInlining)] public void SetBackingDirect(string source){value=source;} public Action SetterCallback; public readonly List<string> Frames=new List<string>(); public readonly List<bool> FrameGlyphs=new List<bool>(); public string text{[MethodImpl(MethodImplOptions.NoInlining)] get{return value;} [MethodImpl(MethodImplOptions.NoInlining)] set{if(ThrowTextWrites)throw new InvalidOperationException("fixture-setter-failure");if(!IgnoreTextWrites)this.value=value;if(SetterCallback!=null)SetterCallback();}} [MethodImpl(MethodImplOptions.NoInlining)] public void OnEnable(){} [MethodImpl(MethodImplOptions.NoInlining)] public void OnPopulateMesh(){Frames.Add(value);FrameGlyphs.Add(value==null||value.All(c=>font.HasCharacter(c)));}
        public UnityEngine.Font font{get=>current;set{FontWrites++;if(!IgnoreFontWrites)current=value;}}
    }
    public class InputField:UnityEngine.Component{}
}
namespace TMPro
{
    public struct SizePair {public float x,y;}
    public enum AtlasPopulationMode {Static,Dynamic}
    public class TMP_FontAsset:UnityEngine.Object
    {
        public bool Chinese,FallbackChinese;public bool isMultiAtlasTexturesEnabled{get;set;}public static int Factories,FullFactories,OutAdds;
        public static bool FailHasString,FailHasFallbacks,FailSimpleFactory,FailSimplePopulate,FailAllGlyphs,EmptyFactory;
        public bool HasCharacters(string text){if(FailHasString)throw new NotSupportedException();return !FailAllGlyphs&&text.All(c=>c<256||Chinese);}
        public bool HasCharacter(char c,bool searchFallbacks,bool tryAddCharacter){if(FailHasFallbacks)throw new NotSupportedException();return !FailAllGlyphs&&(c<256||Chinese||searchFallbacks&&FallbackChinese);}
        public bool HasCharacter(int c)=>!FailAllGlyphs&&(c<256||Chinese);
        public bool TryAddCharacters(string text,bool includeFontFeatures){if(FailSimplePopulate)throw new NotSupportedException();Chinese=true;return !FailAllGlyphs;}
        public bool TryAddCharacters(string text,out string missing,bool includeFontFeatures){OutAdds++;Chinese=true;missing=FailAllGlyphs?text:"";return !FailAllGlyphs;}
        public static TMP_FontAsset CreateFontAsset(string family,string style,int pointSize){if(FailSimpleFactory)throw new NotSupportedException();if(UnityEngine.Font.ManagedArrayAllowed)return new TMP_FontAsset{Chinese=!EmptyFactory};throw new NotSupportedException("Method unstripping failed");}
        public static TMP_FontAsset CreateFontAsset(UnityEngine.Font font){Factories++;if(FailSimpleFactory)throw new NotSupportedException();return new TMP_FontAsset{Chinese=font.Chinese&&!EmptyFactory};}
        public static TMP_FontAsset CreateFontAsset(UnityEngine.Font font,int size,int padding,UnityEngine.TextCore.LowLevel.GlyphRenderMode renderMode,int width,int height,AtlasPopulationMode population,bool multiAtlas){FullFactories++;return new TMP_FontAsset{Chinese=font.Chinese&&!EmptyFactory};}
    }
    public struct TMP_CharacterInfo {public char character;}
    public enum TextOverflowModes {Overflow,Truncate,Page,Linked}
    public class TMP_TextInfo {public int characterCount;public object characterInfo=new TMP_CharacterInfo[0];}
    public enum TextInputSources {Text,SetText,SetTextArray}
    public class TMP_Text:UnityEngine.Component
    {
        string value;public string Backing;public TextInputSources m_inputSource;public bool m_IsTextBackingStringDirty;
        public int maxVisibleCharacters{get;set;}=int.MaxValue;public TMP_TextInfo textInfo{get;set;}=new();
        public int firstVisibleCharacter{get;set;}=0;public int maxVisibleWords{get;set;}=99999;public int maxVisibleLines{get;set;}=99999;
        public bool isRightToLeftText{get;set;}=false;public TextOverflowModes overflowMode{get;set;}=TextOverflowModes.Overflow;
        public void ParseMesh(){textInfo=new TMP_TextInfo{characterCount=value.Length,characterInfo=value.Select(c=>new TMP_CharacterInfo{character=c}).ToArray()};}
        public string text{get=>value;set{this.value=value;Backing=value;m_inputSource=TextInputSources.Text;m_IsTextBackingStringDirty=false;}}
        public string InternalTextBackingArrayToString(){m_IsTextBackingStringDirty=false;return Backing;}
        public TMP_FontAsset font{get;set;}=new();
    }
    public class TMP_InputField:UnityEngine.Component{}
    public class TextMeshProUGUI:TMP_Text
    {
        public float fontSize{get;set;}=60;public float fontSizeMin{get;set;}=8;public float fontSizeMax{get;set;}=60;
        public bool enableAutoSizing{get;set;}public bool autoSizeTextContainer{get;set;}public bool enableWordWrapping{get;set;}
        public bool raycastTarget{get;set;}public bool interactable{get;set;}=true;
        public UnityEngine.UI.RectTransform rectTransform{get;set;}=new();
        public SizePair GetPreferredValues(string text,float width,float height)=>new(){x=300,y=fontSize*2};
    }
}
namespace UnityEngine.TextCore.LowLevel {public enum GlyphRenderMode {SDFAA}}

namespace UnityEngine.SceneManagement {public struct Scene {public bool Valid;public Scene(bool valid){Valid=valid;}public bool IsValid()=>Valid;public bool isLoaded=>Valid;}}
