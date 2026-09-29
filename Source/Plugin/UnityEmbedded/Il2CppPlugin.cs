using System;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace Fusion.UnityEmbedded
{
    [BepInPlugin("local.fusion.unity.embedded", "Fusion Embedded Translation", "0.6.0.84")]
    public sealed class Il2CppPlugin : BasePlugin
    {
        static UnityBridgeRuntime bridge;
        public override void Load()
        {
            if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FUSION_UNITY_PIPE")))return;
            // Generated Unity interop assemblies differ with each game. Create only the tiny
            // injected frame callback against the actual MonoBehaviour type after interop loads.
            var mono=UnityBridgeRuntime.FindType("UnityEngine.MonoBehaviour");
            if(mono==null)throw new InvalidOperationException("Unity interop has not loaded MonoBehaviour.");
            var assembly=AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("FusionUnityFrameHost73"),AssemblyBuilderAccess.Run);
            var type=assembly.DefineDynamicModule("main").DefineType("FusionUnityFrameHost",TypeAttributes.Public|TypeAttributes.Class,mono);
            var ctor=type.DefineConstructor(MethodAttributes.Public,CallingConventions.Standard,new[]{typeof(IntPtr)});
            var constructor=mono.GetConstructor(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance,null,new[]{typeof(IntPtr)},null);
            if(constructor==null)throw new MissingMethodException("Unity interop MonoBehaviour(IntPtr) unavailable.");
            var il=ctor.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Call,constructor);il.Emit(OpCodes.Ret);
            var update=type.DefineMethod("Update",MethodAttributes.Public,typeof(void),Type.EmptyTypes);var body=update.GetILGenerator();body.Emit(OpCodes.Call,typeof(Il2CppPlugin).GetMethod("Frame"));body.Emit(OpCodes.Ret);
            var late=type.DefineMethod("LateUpdate",MethodAttributes.Public,typeof(void),Type.EmptyTypes);var lateBody=late.GetILGenerator();lateBody.Emit(OpCodes.Call,typeof(Il2CppPlugin).GetMethod("LateFrame"));lateBody.Emit(OpCodes.Ret);
            var host=type.CreateType();
            var add=typeof(IL2CPPChainloader).GetMethod("AddUnityComponent",BindingFlags.Public|BindingFlags.Static,null,new[]{typeof(Type)},null);
            if(add==null)throw new MissingMethodException("BepInEx Unity component registration unavailable.");
            add.Invoke(null,new object[]{host});
            bridge=new UnityBridgeRuntime(message=>Log.LogInfo(message),true);if(!bridge.Start()){bridge.Dispose();bridge=null;}
        }
        public static void Frame(){if(bridge!=null)bridge.Tick();}
        public static void LateFrame(){if(bridge!=null)bridge.LateTick();}
        public override bool Unload(){if(bridge!=null)bridge.RestoreAndDispose();bridge=null;return true;}
    }
}
