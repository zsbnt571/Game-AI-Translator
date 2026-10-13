using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace Fusion.UnityEmbedded
{
#if FUSION_IL2CPP
    // TMP's public preprocessing interface changes the rendering input, not the
    // game's text property. No native detours or game-controller hooks are used.
    public sealed class NativeTmpPreprocessor : IDisposable
    {
        static Type injectedType;
        static PropertyInfo pointer;
        static readonly Dictionary<IntPtr,WeakReference> callbacks=new Dictionary<IntPtr,WeakReference>();
        readonly object target,instance,wrapper;
        readonly PropertyInfo property;
        readonly Func<string,string> process;
        readonly IntPtr key;
        bool disposed,inside;
        NativeTmpPreprocessor(object target,PropertyInfo property,object instance,object wrapper,Func<string,string> process)
        {
            this.target=target;this.property=property;this.instance=instance;this.wrapper=wrapper;this.process=process;
            key=Pointer(instance);callbacks[key]=new WeakReference(this);
        }
        static IntPtr Pointer(object value){return value==null?IntPtr.Zero:(IntPtr)pointer.GetValue(value,null);}
        static void Initialize(Type contract)
        {
            if(injectedType!=null)return;
            var basis=UnityBridgeRuntime.FindType("Il2CppSystem.Object");
            var objectBase=UnityBridgeRuntime.FindType("Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase");
            var injector=UnityBridgeRuntime.FindType("Il2CppInterop.Runtime.Injection.ClassInjector");
            var optionsType=UnityBridgeRuntime.FindType("Il2CppInterop.Runtime.Injection.RegisterTypeOptions");
            if(basis==null||objectBase==null||injector==null||optionsType==null)throw new NotSupportedException("Native interface registration unavailable");
            pointer=objectBase.GetProperty("Pointer");
            var pointerCtor=basis.GetConstructor(new[]{typeof(IntPtr)});
            var make=Array.Find(injector.GetMethods(),m=>m.Name=="DerivedConstructorPointer"&&m.IsGenericMethodDefinition&&m.GetParameters().Length==0);
            var body=Array.Find(injector.GetMethods(),m=>m.Name=="DerivedConstructorBody"&&m.GetParameters().Length==1);
            var register=injector.GetMethod("RegisterTypeInIl2Cpp",new[]{typeof(Type),optionsType});
            var interfaces=optionsType.GetProperty("Interfaces");
            if(pointer==null||pointerCtor==null||make==null||body==null||register==null||interfaces==null)throw new NotSupportedException("Native interface API incomplete");
            var assembly=AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("FusionTmpPreprocessor104"),AssemblyBuilderAccess.Run);
            var type=assembly.DefineDynamicModule("main").DefineType("FusionTmpPreprocessor104",TypeAttributes.Public|TypeAttributes.Class,basis);
            var native=type.DefineConstructor(MethodAttributes.Public,CallingConventions.Standard,new[]{typeof(IntPtr)});
            var il=native.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Call,pointerCtor);il.Emit(OpCodes.Ret);
            var create=type.DefineConstructor(MethodAttributes.Public,CallingConventions.Standard,Type.EmptyTypes);
            il=create.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Call,make.MakeGenericMethod(type));il.Emit(OpCodes.Call,pointerCtor);
            il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Call,body.IsGenericMethodDefinition?body.MakeGenericMethod(type):body);il.Emit(OpCodes.Ret);
            var method=type.DefineMethod("PreprocessText",MethodAttributes.Public,typeof(string),new[]{typeof(string)});
            il=method.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Call,pointer.GetGetMethod());il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call,typeof(NativeTmpPreprocessor).GetMethod("Dispatch"));il.Emit(OpCodes.Ret);
            var created=type.CreateType();var options=Activator.CreateInstance(optionsType);
            var convert=Array.Find(interfaces.PropertyType.GetMethods(BindingFlags.Static|BindingFlags.Public),m=>m.Name=="op_Implicit"&&m.GetParameters().Length==1&&m.GetParameters()[0].ParameterType==typeof(Type[]));
            if(convert==null)throw new NotSupportedException("Native interface list unavailable");
            interfaces.SetValue(options,convert.Invoke(null,new object[]{new[]{contract}}),null);
            register.Invoke(null,new[]{(object)created,options});injectedType=created;
        }
        public static string Dispatch(IntPtr key,string source)
        {
            WeakReference reference;var hook=callbacks.TryGetValue(key,out reference)?reference.Target as NativeTmpPreprocessor:null;
            if(hook==null||hook.disposed||hook.inside)return source;
            hook.inside=true;
            try{return hook.process(source)??source;}catch{return source;}finally{hook.inside=false;}
        }
        internal static NativeTmpPreprocessor Attach(object target,Func<string,string> process)
        {
            var property=target.GetType().GetProperty("textPreprocessor");
            if(property==null||!property.CanRead||!property.CanWrite||property.PropertyType.FullName!="TMPro.ITextPreprocessor")return null;
            // Respect other render processors. Do not replace, invoke or reorder
            // another mod's preprocessing contract without a compatibility path.
            if(property.GetValue(target,null)!=null)return null;
            Initialize(property.PropertyType);
            var instance=Activator.CreateInstance(injectedType);
            var wrapper=Activator.CreateInstance(property.PropertyType,new object[]{Pointer(instance)});
            var hook=new NativeTmpPreprocessor(target,property,instance,wrapper,process);
            try{property.SetValue(target,wrapper,null);if(!hook.IsInstalled)throw new InvalidOperationException("TMP processor readback mismatch");return hook;}
            catch{hook.Dispose();throw;}
        }
        internal bool IsInstalled
        {get{try{return !disposed&&Pointer(property.GetValue(target,null))==key;}catch{return false;}}}
        public void Dispose()
        {
            if(disposed)return;callbacks.Remove(key);
            try{if(IsInstalled)property.SetValue(target,null,null);}catch{}
            disposed=true;GC.KeepAlive(instance);GC.KeepAlive(wrapper);
        }
    }
#else
    internal sealed class NativeTmpPreprocessor : IDisposable
    {
        static readonly Dictionary<Type,ConstructorInfo> constructors=new Dictionary<Type,ConstructorInfo>();
        readonly object target;readonly PropertyInfo property;readonly Func<string,string> process;
        readonly object previous;readonly MethodInfo previousProcess;
        object instance;bool disposed,inside;
        NativeTmpPreprocessor(object target,PropertyInfo property,Func<string,string> process)
        {this.target=target;this.property=property;this.process=process;previous=property.GetValue(target,null);previousProcess=property.PropertyType.GetMethod("PreprocessText",new[]{typeof(string)});}
        string Process(string source)
        {
            if(inside)return source;inside=true;
            string parsed=source;
            try
            {
                // The game parser runs once, in its original order. Translate its
                // output, retaining custom tags/reveal bookkeeping and source keys.
                if(previous!=null)parsed=(string)previousProcess.Invoke(previous,new object[]{source})??source;
                if(disposed)return parsed;
                try{return process(parsed)??parsed;}catch{return parsed;}
            }
            finally{inside=false;}
        }
        static ConstructorInfo Constructor(Type contract)
        {
            ConstructorInfo constructor;if(constructors.TryGetValue(contract,out constructor))return constructor;
            var signature=contract.GetMethod("PreprocessText",new[]{typeof(string)});
            if(!contract.IsInterface||signature==null||signature.ReturnType!=typeof(string))return null;
            var name=new AssemblyName("FusionManagedTmpPreprocessor112_"+constructors.Count);
            // Mono games include .NET 3.5, while controlled regression fixtures
            // use modern .NET. Emit only the public interface/delegate contract.
            var legacy=typeof(AppDomain).GetMethod("DefineDynamicAssembly",new[]{typeof(AssemblyName),typeof(AssemblyBuilderAccess)});
            var assembly=(AssemblyBuilder)(legacy!=null?legacy.Invoke(AppDomain.CurrentDomain,new object[]{name,AssemblyBuilderAccess.Run}):
                typeof(AssemblyBuilder).GetMethod("DefineDynamicAssembly",new[]{typeof(AssemblyName),typeof(AssemblyBuilderAccess)}).Invoke(null,new object[]{name,AssemblyBuilderAccess.Run}));
            var type=assembly.DefineDynamicModule("main").DefineType("FusionManagedTmpPreprocessor112",TypeAttributes.Public|TypeAttributes.Sealed,typeof(object),new[]{contract});
            var callback=type.DefineField("callback",typeof(Func<string,string>),FieldAttributes.Private|FieldAttributes.InitOnly);
            var create=type.DefineConstructor(MethodAttributes.Public,CallingConventions.Standard,new[]{typeof(Func<string,string>)});
            var il=create.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Call,typeof(object).GetConstructor(Type.EmptyTypes));
            il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Stfld,callback);il.Emit(OpCodes.Ret);
            var method=type.DefineMethod("PreprocessText",MethodAttributes.Public|MethodAttributes.Virtual|MethodAttributes.Final|MethodAttributes.NewSlot,typeof(string),new[]{typeof(string)});
            il=method.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldfld,callback);il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt,typeof(Func<string,string>).GetMethod("Invoke"));il.Emit(OpCodes.Ret);type.DefineMethodOverride(method,signature);
            constructor=type.CreateType().GetConstructor(new[]{typeof(Func<string,string>)});constructors[contract]=constructor;return constructor;
        }
        internal static NativeTmpPreprocessor Attach(object target,Func<string,string> process)
        {
            var property=target.GetType().GetProperty("textPreprocessor");
            if(property==null||!property.CanRead||!property.CanWrite||property.PropertyType.FullName!="TMPro.ITextPreprocessor")return null;
            var constructor=Constructor(property.PropertyType);if(constructor==null)return null;
            var hook=new NativeTmpPreprocessor(target,property,process);
            hook.instance=constructor.Invoke(new object[]{new Func<string,string>(hook.Process)});
            try{property.SetValue(target,hook.instance,null);if(!hook.IsInstalled)throw new InvalidOperationException("TMP processor readback mismatch");return hook;}
            catch{hook.Dispose();throw;}
        }
        internal bool IsInstalled{get{try{return !disposed&&ReferenceEquals(property.GetValue(target,null),instance);}catch{return false;}}}
        public void Dispose()
        {
            if(disposed)return;
            try{if(IsInstalled)property.SetValue(target,previous,null);}catch{}
            disposed=true;
        }
    }
#endif
}
