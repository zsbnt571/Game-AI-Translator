using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace Fusion.UnityEmbedded
{
    // UI.Text has no preprocessor. Project its getter only while its own mesh or
    // layout is being generated. Game reads, append buffers and button keys keep
    // their original values. No property setter runs inside the rendering scope.
    internal sealed class LegacyTextProjection
    {
        [ThreadStatic] static object drawing;
        static LegacyTextProjection current;
        readonly Func<object,string,string> project;
        readonly Func<object,float,float> rasterDensity;
        readonly Func<object,bool,float> preferredSize;
        readonly Func<object,object,object> generationSettings;
        internal bool DensityAvailable{get;private set;}
        Func<object,bool> translatedCjk;
        PropertyInfo spacingGraphic;
        readonly Type textType;
        internal LegacyTextProjection(Harmony harmony,Type type,Func<object,string,string> project,Func<object,float,float> rasterDensity=null,Func<object,bool,float> preferredSize=null,Func<object,object,object> generationSettings=null)
        {
            this.project=project;this.rasterDensity=rasterDensity;this.preferredSize=preferredSize;this.generationSettings=generationSettings;textType=type;
            var getter=type.GetProperty("text").GetGetMethod();
            var methods=type.GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly)
                .Where(m=>!m.IsAbstract&&(m.Name=="OnPopulateMesh"||m.Name=="get_preferredWidth"||m.Name=="get_preferredHeight")).ToList();
            if(!methods.Any(m=>m.Name=="OnPopulateMesh"))throw new NotSupportedException("UI.Text mesh method unavailable");
            // Mesh effects run AFTER Text.OnPopulateMesh. Their letter/line
            // indexing must see the same projected text as the generated mesh.
            // Scope only the engine's mesh pipeline, never game Update/callbacks.
            for(var parent=type.BaseType;parent!=null;parent=parent.BaseType)
                if(parent.FullName=="UnityEngine.UI.Graphic")
                {
                    methods.AddRange(parent.GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly)
                        .Where(m=>!m.IsAbstract&&(m.Name=="DoMeshGeneration"||m.Name=="DoLegacyMeshGeneration")));
                    break;
                }
            harmony.Patch(getter,postfix:new HarmonyMethod(typeof(LegacyTextProjection).GetMethod("Read",BindingFlags.Static|BindingFlags.NonPublic)));
            foreach(var method in methods)harmony.Patch(method,
                prefix:new HarmonyMethod(typeof(LegacyTextProjection).GetMethod("Enter",BindingFlags.Static|BindingFlags.NonPublic)),
                finalizer:new HarmonyMethod(typeof(LegacyTextProjection).GetMethod("Leave",BindingFlags.Static|BindingFlags.NonPublic)));
            if(preferredSize!=null)foreach(string name in new[]{"preferredWidth","preferredHeight"})
            {
                var property=type.GetProperty(name);
                if(property!=null&&property.PropertyType==typeof(float))harmony.Patch(property.GetGetMethod(),prefix:new HarmonyMethod(typeof(LegacyTextProjection).GetMethod(name=="preferredWidth"?"Width":"Height",BindingFlags.Static|BindingFlags.NonPublic)));
            }
            if(generationSettings!=null)
            {
                var settings=type.GetMethod("GetGenerationSettings");
                if(settings!=null)harmony.Patch(settings,postfix:new HarmonyMethod(typeof(LegacyTextProjection).GetMethod("Generation",BindingFlags.Static|BindingFlags.NonPublic).MakeGenericMethod(settings.ReturnType)));
            }
            if(rasterDensity!=null)try
            {
                var density=type.GetProperty("pixelsPerUnit");
                if(density!=null&&density.PropertyType==typeof(float)&&density.GetGetMethod()!=null)
                {harmony.Patch(density.GetGetMethod(),postfix:new HarmonyMethod(typeof(LegacyTextProjection).GetMethod("ReadDensity",BindingFlags.Static|BindingFlags.NonPublic)));DensityAvailable=true;}
            }catch{}
            current=this;
        }
        internal bool Supports(object target){return textType.IsInstanceOfType(target);}
        internal void InstallLetterSpacing(Harmony harmony,Type effectType,Func<object,bool> ownsCjk)
        {
            // The common UI.Extensions LetterSpacing algorithm removes the
            // character before each wrapped line, assuming an English space.
            // CJK wrapping has no such separator (and can report index zero).
            // Keep the font's native advance for translated CJK only. Do not
            // disable arbitrary mesh effects, mutate the component, or catch
            // unrelated game exceptions.
            if(effectType==null||effectType.FullName!="UnityEngine.UI.LetterSpacing")return;
            var parent=effectType.BaseType;
            while(parent!=null&&parent.FullName!="UnityEngine.UI.BaseMeshEffect")parent=parent.BaseType;
            var graphic=parent==null?null:parent.GetProperty("graphic",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance);
            var spacing=effectType.GetProperty("spacing");
            if(graphic==null||graphic.PropertyType.FullName!="UnityEngine.UI.Graphic"||spacing==null||spacing.PropertyType!=typeof(float))return;
            spacingGraphic=graphic;translatedCjk=ownsCjk;
            foreach(var method in effectType.GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly))
                if(method.Name=="ModifyMesh"&&method.ReturnType==typeof(void)&&method.GetParameters().Length==1&&
                    (method.GetParameters()[0].ParameterType.FullName=="UnityEngine.UI.VertexHelper"||method.GetParameters()[0].ParameterType.FullName=="UnityEngine.Mesh"))
                    harmony.Patch(method,prefix:new HarmonyMethod(typeof(LegacyTextProjection).GetMethod("Spacing",BindingFlags.Static|BindingFlags.NonPublic)));
        }
        static bool Spacing(object __instance)
        {
            var hook=current;if(hook==null||hook.translatedCjk==null)return true;
            try{var graphic=hook.spacingGraphic.GetValue(__instance,null);return !hook.Supports(graphic)||!hook.translatedCjk(graphic);}catch{return true;}
        }
        internal static bool IsDrawing{get{return drawing!=null;}}
        static void Enter(object __instance,out object __state){__state=drawing;drawing=current!=null&&current.Supports(__instance)?__instance:null;}
        static Exception Leave(Exception __exception,object __state){drawing=__state;return __exception;}
        static void Read(object __instance,ref string __result)
        {
            var hook=current;if(hook==null||!ReferenceEquals(drawing,__instance))return;
            try{__result=hook.project(__instance,__result);}catch{}
        }
        static void ReadDensity(object __instance,ref float __result)
        {
            var hook=current;if(hook==null||hook.rasterDensity==null||!ReferenceEquals(drawing,__instance))return;
            try{__result=hook.rasterDensity(__instance,__result);}catch{}
        }
        static bool Width(object __instance,ref float __result){return Preferred(__instance,true,ref __result);}
        static bool Height(object __instance,ref float __result){return Preferred(__instance,false,ref __result);}
        static bool Preferred(object target,bool horizontal,ref float result)
        {
            try{var hook=current;if(hook==null||hook.preferredSize==null)return true;float size=hook.preferredSize(target,horizontal);if(size<0||float.IsNaN(size)||float.IsInfinity(size))return true;result=size;return false;}catch{return true;}
        }
        static void Generation<T>(object __instance,ref T __result)
        {
            var hook=current;if(hook==null||hook.generationSettings==null||!ReferenceEquals(drawing,__instance))return;
            try{__result=(T)hook.generationSettings(__instance,__result);}catch{}
        }
        internal void Dispose(){if(current==this){current=null;drawing=null;}}
    }

    // A transformed text control can rasterize at one texel per local UI unit,
    // then be magnified by its canvas/camera. Measure documented Unity geometry.
    // A per-text rendering projection raises density without resizing the text,
    // changing a shared canvas/font, or changing pixelsPerUnit for game readers.
    internal static class LegacyRasterDensity
    {
        internal static float Measure(object target,double nativeScale,int pointSize)
        {
            try
            {
                if(nativeScale<=0||double.IsNaN(nativeScale)||double.IsInfinity(nativeScale))return 0;
                double density=ScreenDensity(target);
                if(double.IsNaN(density)||double.IsInfinity(density)||density<=nativeScale*1.25)return 0;
                double cap=Math.Min(nativeScale*4,384.0/Math.Max(1,pointSize));
                double result=Math.Min(cap,Math.Ceiling(density*2)/2);
                return result>nativeScale?(float)result:0;
            }catch{return 0;}
        }
        internal static double ScreenDensity(object target)
        {string status;return ScreenDensity(target,out status);}
        internal static double ScreenDensity(object target,out string status)
        {
            status="unavailable";
            try
            {
                var type=target.GetType();var cp=type.GetProperty("canvas");object canvas=cp==null?null:cp.GetValue(target,null);
                if(canvas==null)return 0;
                string mode=Convert.ToString(canvas.GetType().GetProperty("renderMode").GetValue(canvas,null));
                bool overlay=mode=="ScreenSpaceOverlay";
                if(!overlay&&mode!="WorldSpace"&&mode!="ScreenSpaceCamera")return 0;
                var cameraType=UnityBridgeRuntime.FindType("UnityEngine.Camera");var vector=UnityBridgeRuntime.FindType("UnityEngine.Vector3");if(cameraType==null||vector==null)return 0;
                object transform=type.GetProperty("rectTransform").GetValue(target,null);
                var world=transform.GetType().GetMethod("TransformPoint",new[]{vector});var screen=cameraType.GetMethod("WorldToScreenPoint",new[]{vector});if(world==null||screen==null)return 0;
                FieldInfo x=vector.GetField("x"),y=vector.GetField("y"),z=vector.GetField("z");if(x==null||y==null||z==null)return 0;
                object[] worldPoints=new object[3];
                for(int i=0;i<3;i++)
                {
                    object local=Activator.CreateInstance(vector);if(i==1)x.SetValue(local,1f);if(i==2)y.SetValue(local,1f);
                    worldPoints[i]=world.Invoke(transform,new[]{local});
                }
                if(overlay){status="overlay";return Distance(worldPoints,x,y);}
                // World-space Canvas.worldCamera is also its event camera. A
                // visible canvas need not have one, nor a tagged Camera.main.
                // Consider only enabled engine cameras that render this layer.
                int layer=-1;var gp=type.GetProperty("gameObject");object go=gp==null?null:gp.GetValue(target,null);
                var lp=go==null?null:go.GetType().GetProperty("layer");if(lp!=null)layer=Convert.ToInt32(lp.GetValue(go,null));
                var cameras=new List<object>();var wp=canvas.GetType().GetProperty("worldCamera");if(wp!=null)AddCamera(cameras,wp.GetValue(canvas,null));
                var main=cameraType.GetProperty("main",BindingFlags.Static|BindingFlags.Public);if(main!=null)AddCamera(cameras,main.GetValue(null,null));
                var all=cameraType.GetProperty("allCameras",BindingFlags.Static|BindingFlags.Public);var available=all==null?null:all.GetValue(null,null) as IEnumerable;
                if(available!=null){int count=0;foreach(object camera in available){if(++count>16)break;AddCamera(cameras,camera);}}
                double density=0;status=cameras.Count==0?"no-render-camera":"no-visible-projection";
                foreach(object camera in cameras)
                {
                    var active=cameraType.GetProperty("isActiveAndEnabled");if(active!=null&&!(bool)active.GetValue(camera,null))continue;
                    var mask=cameraType.GetProperty("cullingMask");if(layer>=0&&layer<32&&mask!=null&&(Convert.ToInt32(mask.GetValue(camera,null))&(1<<layer))==0)continue;
                    var nearProperty=cameraType.GetProperty("nearClipPlane");var farProperty=cameraType.GetProperty("farClipPlane");
                    double near=nearProperty==null?0:Convert.ToDouble(nearProperty.GetValue(camera,null)),far=farProperty==null?double.MaxValue:Convert.ToDouble(farProperty.GetValue(camera,null));
                    object[] points=new object[3];bool visible=true;
                    // Orthographic 2D cameras may deliberately use a negative
                    // near plane. Their visible text can lie behind z=0.
                    for(int i=0;i<3;i++){points[i]=screen.Invoke(camera,new[]{worldPoints[i]});double depth=Convert.ToDouble(z.GetValue(points[i]));if(depth<near||depth>far){visible=false;break;}}
                    if(!visible)continue;
                    double value=Distance(points,x,y);if(value>density){density=value;status="camera-projection";}
                }
                return density;
            }catch(Exception error){status="projection-"+error.GetType().Name;return 0;}
        }
        static void AddCamera(List<object> cameras,object camera){if(camera!=null&&!cameras.Any(c=>ReferenceEquals(c,camera)))cameras.Add(camera);}
        static double Distance(object[] points,FieldInfo x,FieldInfo y)
        {double density=0;for(int i=1;i<3;i++){double dx=Convert.ToDouble(x.GetValue(points[i]))-Convert.ToDouble(x.GetValue(points[0])),dy=Convert.ToDouble(y.GetValue(points[i]))-Convert.ToDouble(y.GetValue(points[0]));density=Math.Max(density,Math.Sqrt(dx*dx+dy*dy));}return density;}
    }
}
