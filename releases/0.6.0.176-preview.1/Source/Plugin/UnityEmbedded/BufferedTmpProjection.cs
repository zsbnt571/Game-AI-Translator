using System;using System.Reflection;using HarmonyLib;
namespace Fusion.UnityEmbedded
{
    // Older TMP converts a complete string straight into its character buffer.
    // Intercept that rendering argument; SetText does not populate .text there.
    internal sealed class BufferedTmpProjection
    {
        static BufferedTmpProjection current;
        readonly Type type;readonly Func<object,string,string> project;
        internal BufferedTmpProjection(Harmony harmony,Type type,Func<object,string,string> project)
        {
            this.type=type;this.project=project;
            var method=type.GetMethod("StringToCharArray",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new[]{typeof(string),typeof(int[]).MakeByRefType()},null);
            if(method==null||type.GetProperty("textPreprocessor")!=null)throw new NotSupportedException("Not the legacy TMP string buffer API");
            harmony.Patch(method,prefix:new HarmonyMethod(typeof(BufferedTmpProjection).GetMethod("Convert",BindingFlags.Static|BindingFlags.NonPublic)));current=this;
        }
        internal bool Supports(object target){return type.IsInstanceOfType(target);}
        static void Convert(object __instance,ref string __0){var hook=current;if(hook==null)return;try{__0=hook.project(__instance,__0);}catch{}}
        internal void Dispose(){if(current==this)current=null;}
    }
}
