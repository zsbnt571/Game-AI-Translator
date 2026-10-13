using System;
using System.Linq;
using System.Reflection;

namespace Fusion.UnityEmbedded
{
    // Some stripped IL2CPP wrappers retain Unity's native font initializer but
    // lose the managed constructor used by CreateDynamicFontFromOSFont. Allocate
    // through the runtime's exported API and call that same generated initializer.
    // No game font, class layout, native offset, or detour is involved.
    internal static class NativeLegacyFont
    {
        internal static object Create(Type fontType,string[] families,int size)
        {
            var runtime=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name=="Il2CppInterop.Runtime");
            if(runtime==null||fontType.FullName!="UnityEngine.Font")return null;
            var arrayType=runtime.GetType("Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray",false);
            var store=runtime.GetType("Il2CppInterop.Runtime.Il2CppClassPointerStore`1",false);
            var api=runtime.GetType("Il2CppInterop.Runtime.IL2CPP",false);
            var wrapper=fontType.GetConstructor(new[]{typeof(IntPtr)});
            var initialize=arrayType==null?null:fontType.GetMethod("Internal_CreateDynamicFont",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static,null,new[]{fontType,arrayType,typeof(int)},null);
            var arrayConstructor=arrayType==null?null:arrayType.GetConstructor(new[]{typeof(string[])});
            var allocate=api==null?null:api.GetMethod("il2cpp_object_new",BindingFlags.Public|BindingFlags.Static,null,new[]{typeof(IntPtr)},null);
            if(store==null||wrapper==null||initialize==null||arrayConstructor==null||allocate==null)return null;
            var field=store.MakeGenericType(fontType).GetField("NativeClassPtr",BindingFlags.Public|BindingFlags.Static);
            if(field==null)return null;
            var klass=(IntPtr)field.GetValue(null);if(klass==IntPtr.Zero)return null;
            var pointer=(IntPtr)allocate.Invoke(null,new object[]{klass});if(pointer==IntPtr.Zero)return null;
            object font=wrapper.Invoke(new object[]{pointer});
            try{initialize.Invoke(null,new[]{font,arrayConstructor.Invoke(new object[]{families}),(object)size});return font;}
            catch
            {
                // A failed initializer can still create a Unity native object.
                // Destroy only this newly allocated object; wrappers keep the GC
                // handle alive until the managed object is collected.
                try{var objectType=fontType.BaseType;var destroy=objectType.GetMethod("Destroy",BindingFlags.Public|BindingFlags.Static,null,new[]{objectType},null);if(destroy!=null)destroy.Invoke(null,new[]{font});}catch{}
                throw;
            }
        }
    }
}
