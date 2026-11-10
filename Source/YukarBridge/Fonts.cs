using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace FusionYukar;

// Font objects stay owned by the engine. We substitute draw/measure arguments,
// never change game resources, text, saves, or the translation enable flag.
internal static class Fonts
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static readonly object Gate=new object();
    static readonly ConditionalWeakTable<object,Metrics> Originals=new ConditionalWeakTable<object,Metrics>();
    static readonly Dictionary<string,object> Loaded=new Dictionary<string,object>(StringComparer.Ordinal);
    static readonly Dictionary<string,Metrics> Known=new Dictionary<string,Metrics>(StringComparer.Ordinal);
    static Type fontType;
    static ConstructorInfo constructor;
    static MethodInfo available,leading;
    static string path="",family="",failure="";
    static bool ready;
    static Dictionary<string,object> startup;
    static long substitutions,untracked;
    // Native render commands may retain font resources after EndDraw. Retain our
    // small bounded pool until the process exits instead of releasing in flight.
    const int MaxFonts=32;
    sealed class Metrics
    {
        internal int Size,Bold;internal float Scale,Y,Line;
        internal string Key=>Size+"|"+Scale.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+"|"+Bold+"|"+Y+"|"+Line;
    }
    internal static bool Initialize(Assembly engine,Harmony harmony)
    {
        try
        {
            var core=engine.GetType("Yukar.Engine.GraphicsCore");
            var create=core?.GetMethods(All).SingleOrDefault(m=>m.Name=="createFont"&&m.GetParameters().Length==1&&m.GetParameters()[0].ParameterType.FullName=="Yukar.Common.Resource.Font"&&m.ReturnType.FullName=="SharpKmyGfx.Font");
            if(create==null)return false;
            fontType=create.ReturnType;
            constructor=fontType.GetConstructor(new[]{typeof(string),typeof(int),typeof(float),typeof(int)});
            available=fontType.GetMethod("isAvailable",Type.EmptyTypes);
            leading=fontType.GetMethod("setLeadingOffset",new[]{typeof(float),typeof(float)});
            if(constructor==null||available?.ReturnType!=typeof(bool)||leading==null)return false;
            var resource=create.GetParameters()[0].ParameterType;
            foreach(var name in new[]{"Size","DefaultScale","BoldWeight","YOffset","LineOffset"})if(resource.GetProperty(name,All)==null)return false;
            var drawMethods=engine.GetTypes().Where(t=>t.FullName=="Yukar.Engine.Graphics"||t.FullName=="Yukar.Engine.GraphicsCore")
                .SelectMany(t=>t.GetMethods(All)).Where(m=>(m.Name.StartsWith("DrawString",StringComparison.Ordinal)||m.Name=="MeasureString")&&m.GetParameters().Length>1&&m.GetParameters()[0].ParameterType==fontType&&m.GetParameters()[1].ParameterType==typeof(string)).ToArray();
            if(drawMethods.Length==0)return false;
            harmony.Patch(create,postfix:new HarmonyMethod(typeof(Fonts).GetMethod(nameof(Created),All)));
            foreach(var method in drawMethods)
                harmony.Patch(method,prefix:new HarmonyMethod(typeof(Fonts).GetMethod(nameof(FontArgument),All)));
            ready=true;
            string selected=Environment.GetEnvironmentVariable("FUSION_GAME_FONT_PATH"),selectedFamily=Environment.GetEnvironmentVariable("FUSION_GAME_FONT_FAMILY");
            if(Environment.GetEnvironmentVariable("FUSION_GAME_FONT_INDEPENDENT")=="1"&&!string.IsNullOrEmpty(selected)&&!string.IsNullOrEmpty(selectedFamily))
                startup=new Dictionary<string,object>{{"op","fontPreferences"},{"mode","custom"},{"path",selected},{"family",selectedFamily}};
            return true;
        }
        catch(Exception){failure="This engine font interface is unavailable. The previous font and translations were retained.";return false;}
    }
    static void Created(object __0,object __result)
    {
        if(__0==null||__result==null)return;
        try
        {
            var t=__0.GetType();object P(string name)=>t.GetProperty(name,All).GetValue(__0,null);
            var m=new Metrics{Size=Convert.ToInt32(P("Size")),Scale=Convert.ToSingle(P("DefaultScale")),Bold=Convert.ToInt32(P("BoldWeight")),Y=Convert.ToSingle(P("YOffset")),Line=Convert.ToSingle(P("LineOffset"))};
            if(m.Size<1||m.Size>256||m.Scale<=0||m.Scale>16||float.IsNaN(m.Scale)||m.Bold<0||m.Bold>32)return;
            lock(Gate)
            {
                Originals.Remove(__result);Originals.Add(__result,m);if(Known.Count<MaxFonts)Known[m.Key]=m;
                // createFont is called after the native graphics backend exists,
                // and before its returned original font reaches the first draw.
                if(startup!=null){var request=startup;startup=null;try{Command(request);}catch(Exception ex){failure=(ex.InnerException??ex).Message;}}
            }
        }
        catch(Exception){/* Unknown resource versions retain the original font. */}
    }
    static object Load(string selected,Metrics m)
    {
        var key=selected+"|"+m.Key;
        if(Loaded.TryGetValue(key,out var value))return value;
        if(Loaded.Count>=MaxFonts)throw new InvalidOperationException("The font preview limit for this game session was reached. Restart the game to select more fonts.");
        try{value=constructor.Invoke(new object[]{selected,m.Size,m.Scale,m.Bold});}
        catch(Exception){throw new InvalidOperationException("The running engine could not load the selected font. The previous preference was retained.");}
        bool usable=false;try{usable=value!=null&&Equals(available.Invoke(value,null),true);}catch{}
        if(!usable)
        {
            try{fontType.GetMethod("Release",Type.EmptyTypes)?.Invoke(value,null);}catch{}
            throw new InvalidOperationException("The running engine could not load the selected font. The previous preference was retained.");
        }
        try{leading.Invoke(value,new object[]{m.Y,m.Line});}
        catch{try{fontType.GetMethod("Release",Type.EmptyTypes)?.Invoke(value,null);}catch{}throw new InvalidOperationException("The running engine could not load the selected font. The previous preference was retained.");}
        Loaded.Add(key,value);return value;
    }
    static void FontArgument(ref object __0)
    {
        lock(Gate)
        {
            if(!ready||path.Length==0)return;
            var original=__0;
            if(original==null||Loaded.Values.Any(x=>ReferenceEquals(x,original)))return;
            if(!Originals.TryGetValue(original,out var metrics)){untracked++;return;}
            try{__0=Load(path,metrics);substitutions++;}
            catch(Exception ex){failure=(ex.InnerException??ex).Message;}
        }
    }
    internal static object Command(Dictionary<string,object> request)
    {
        lock(Gate)
        {
            if(Runtime.Get(request,"op")=="fontState")return State(false);
            string mode=Runtime.Get(request,"mode");
            if(mode=="default"){path=family=failure="";return State(true);}
            if(mode!="custom")throw new InvalidOperationException("Invalid font preference mode.");
            if(!ready)throw new InvalidOperationException("This engine font interface is unavailable. The previous font and translations were retained.");
            string selected=Runtime.Get(request,"path"),selectedFamily=Runtime.Get(request,"family");
            if(!Path.IsPathRooted(selected)||selectedFamily.Length==0||selectedFamily.Length>256||selectedFamily.IndexOf('\0')>=0)throw new InvalidOperationException("Invalid custom font preference.");
            selected=Path.GetFullPath(selected);string extension=Path.GetExtension(selected).ToLowerInvariant();
            if(extension!=".ttf"&&extension!=".otf")throw new InvalidOperationException("This engine requires a standalone TTF or OTF font. The previous preference was retained.");
            var info=new FileInfo(selected);
            if(!info.Exists||info.Length<12||info.Length>32*1024*1024||(info.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("The selected font file is unavailable or exceeds 32 MiB.");
            using(var stream=File.OpenRead(selected))
            {
                var header=new byte[4];if(stream.Read(header,0,4)!=4||!((header[0]==0&&header[1]==1&&header[2]==0&&header[3]==0)||(header[0]==79&&header[1]==84&&header[2]==84&&header[3]==79)))throw new InvalidOperationException("Invalid custom font file.");
            }
            // Prepare before committing. Loading errors keep the previous choice.
            Load(selected,new Metrics{Size=16,Scale=1});
            foreach(var metrics in Known.Values)Load(selected,metrics);
            path=selected;family=selectedFamily;failure="";return State(true);
        }
    }
    static object State(bool accepted)=>new{accepted,independent=true,supported=ready,custom=path.Length>0,family,applied=substitutions,untracked,failure};
    internal static void Stop(){lock(Gate){path=family="";startup=null;}}
}
