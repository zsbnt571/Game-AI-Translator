using System;using System.Collections.Generic;using System.Linq;using System.Reflection;using HarmonyLib;
namespace Fusion.UnityEmbedded
{
    // Older TMP converts a complete string straight into its character buffer.
    // Intercept that rendering argument; SetText does not populate .text there.
    internal sealed class BufferedTmpProjection
    {
        static BufferedTmpProjection current;
        sealed class Measurement { internal object Target; }
        [ThreadStatic] static List<Measurement> measurements;
        readonly Type type;readonly Func<object,string,string> project;
        internal BufferedTmpProjection(Harmony harmony,Type type,Func<object,string,string> project)
        {
            this.type=type;this.project=project;
            var method=type.GetMethod("StringToCharArray",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new[]{typeof(string),typeof(int[]).MakeByRefType()},null);
            if(method==null||type.GetProperty("textPreprocessor")!=null)throw new NotSupportedException("Not the legacy TMP string buffer API");
            // TMP also uses this converter to measure arbitrary candidate text.
            // That input is not a display assignment: our own translated fit
            // measurement must not become the control's next captured source.
            var measure=type.GetMethods(BindingFlags.Public|BindingFlags.Instance).Where(IsMeasurement).ToArray();
            if(measure.Length==0)throw new NotSupportedException("Legacy TMP measurement boundary unavailable");
            var begin=new HarmonyMethod(typeof(BufferedTmpProjection).GetMethod("BeginMeasurement",BindingFlags.Static|BindingFlags.NonPublic));
            var end=new HarmonyMethod(typeof(BufferedTmpProjection).GetMethod("EndMeasurement",BindingFlags.Static|BindingFlags.NonPublic));
            foreach(var candidate in measure)harmony.Patch(candidate,prefix:begin,finalizer:end);
            harmony.Patch(method,prefix:new HarmonyMethod(typeof(BufferedTmpProjection).GetMethod("Convert",BindingFlags.Static|BindingFlags.NonPublic)));current=this;
        }
        static bool IsMeasurement(MethodInfo method)
        {
            if(method.Name!="GetPreferredValues"||method.IsGenericMethod||method.ReturnType.FullName!="UnityEngine.Vector2")return false;
            var p=method.GetParameters();int start=p.Length>0&&p[0].ParameterType==typeof(string)?1:0;
            return (p.Length==start||p.Length==start+2)&&p.Skip(start).All(v=>v.ParameterType==typeof(float));
        }
        static void BeginMeasurement(object __instance,out Measurement __state)
        {
            __state=null;var hook=current;if(hook==null||!hook.Supports(__instance))return;
            if(measurements==null)measurements=new List<Measurement>();
            __state=new Measurement{Target=__instance};measurements.Add(__state);
        }
        static Exception EndMeasurement(Exception __exception,Measurement __state)
        {
            if(__state!=null&&measurements!=null)measurements.Remove(__state);
            return __exception;
        }
        internal bool Supports(object target){return type.IsInstanceOfType(target);}
        static void Convert(object __instance,ref string __0)
        {
            var hook=current;if(hook==null||measurements!=null&&measurements.Any(m=>ReferenceEquals(m.Target,__instance)))return;
            try{__0=hook.project(__instance,__0);}catch{}
        }
        internal void Dispose(){if(current==this)current=null;}
    }
}
