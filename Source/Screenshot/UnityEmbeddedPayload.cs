using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace ScreenshotTranslationUiTester;

internal static class UnityEmbeddedPayload
{
    internal static Stream Open(string backend,string architecture)
    {
        string name=backend switch{"Mono" when architecture=="x64"=>"UnityEmbeddedMono64.zip","Mono" when architecture=="x86"=>"UnityEmbeddedMono32.zip","Mono6" when architecture is "x64" or "x86"=>"UnityEmbeddedMono6.zip","IL2CPP" when architecture=="x64"=>"UnityEmbeddedIl2Cpp64.zip",_=>throw new IOException("没有匹配此 Unity 后端与位数的翻译组件。")};
        var assembly=typeof(UnityEmbeddedPayload).Assembly;string? resource=assembly.GetManifestResourceNames().SingleOrDefault(r=>r.EndsWith("."+name,StringComparison.Ordinal));
        if(resource is null)throw new IOException("此版本尚未包含 Unity 内嵌翻译载荷。");return assembly.GetManifestResourceStream(resource)??throw new IOException("无法读取 Unity 翻译载荷。");
    }
}

// This reads assembly metadata only. It never loads or executes a game's existing loader.
internal static class UnityEmbeddedLoaderMetadata
{
    internal sealed record AssemblySurface(string Name,Version Version,int RuntimeMajor,HashSet<string> Types,HashSet<string> Members,HashSet<string> RequiredMembers);
    internal static AssemblySurface Read(Stream stream)
    {
        using var pe=new PEReader(stream,PEStreamOptions.PrefetchEntireImage|PEStreamOptions.LeaveOpen);
        if(!pe.HasMetadata)throw new IOException("现有加载器不是可识别的托管组件。");
        var reader=pe.GetMetadataReader();if(!reader.IsAssembly)throw new IOException("现有加载器缺少程序集信息。");
        if(reader.TypeDefinitions.Count>65536||reader.MemberReferences.Count>262144)throw new IOException("加载器接口清单超过安全读取范围。");
        var assembly=reader.GetAssemblyDefinition();var provider=new SurfaceNames();
        var types=new HashSet<string>(StringComparer.Ordinal);var members=new HashSet<string>(StringComparer.Ordinal);var required=new HashSet<string>(StringComparer.Ordinal);
        foreach(var handle in reader.TypeDefinitions)
        {
            var type=reader.GetTypeDefinition(handle);string name=provider.GetTypeFromDefinition(reader,handle,0);types.Add(name);
            foreach(var methodHandle in type.GetMethods())
            {
                var method=reader.GetMethodDefinition(methodHandle);var access=method.Attributes&MethodAttributes.MemberAccessMask;
                if(access is MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem)
                    members.Add(Method(name,reader.GetString(method.Name),method.DecodeSignature(provider,(object?)null)));
            }
            foreach(var fieldHandle in type.GetFields())
            {
                var field=reader.GetFieldDefinition(fieldHandle);var access=field.Attributes&FieldAttributes.FieldAccessMask;
                if(access is FieldAttributes.Public or FieldAttributes.Family or FieldAttributes.FamORAssem)
                    members.Add(name+"::"+reader.GetString(field.Name)+":"+field.DecodeSignature(provider,(object?)null));
            }
        }
        foreach(var handle in reader.MemberReferences)
        {
            var member=reader.GetMemberReference(handle);
            if(member.Parent.Kind!=HandleKind.TypeReference)continue;
            var typeHandle=(TypeReferenceHandle)member.Parent;string? dependency=Dependency(reader,typeHandle);
            if(dependency is null||!IsLoaderAssembly(dependency))continue;
            string type=provider.GetTypeFromReference(reader,typeHandle,0),name=reader.GetString(member.Name);
            string key=member.GetKind()==MemberReferenceKind.Method?Method(type,name,member.DecodeMethodSignature(provider,(object?)null)):type+"::"+name+":"+member.DecodeFieldSignature(provider,(object?)null);
            required.Add(dependency+"|"+key);
        }
        int runtimeMajor=0;
        foreach(var handle in reader.AssemblyReferences){var reference=reader.GetAssemblyReference(handle);if(reader.GetString(reference.Name)=="System.Runtime")runtimeMajor=Math.Max(runtimeMajor,reference.Version.Major);}
        return new(reader.GetString(assembly.Name),assembly.Version,runtimeMajor,types,members,required);
    }
    internal static (string Name,Version Version) Identity(Stream stream)
    {
        using var pe=new PEReader(stream,PEStreamOptions.PrefetchMetadata|PEStreamOptions.LeaveOpen);
        if(!pe.HasMetadata)throw new IOException("现有运行环境缺少托管组件信息。");
        var reader=pe.GetMetadataReader();if(!reader.IsAssembly)throw new IOException("现有运行环境组件不可确认。");
        var assembly=reader.GetAssemblyDefinition();return(reader.GetString(assembly.Name),assembly.Version);
    }
    internal static bool IsLoaderAssembly(string name)=>name=="BepInEx"||name.StartsWith("BepInEx.",StringComparison.Ordinal)||name=="0Harmony"||name.StartsWith("Il2CppInterop.",StringComparison.Ordinal);
    private static string? Dependency(MetadataReader reader,TypeReferenceHandle handle)
    {
        for(int count=0;count<32;count++)
        {
            var scope=reader.GetTypeReference(handle).ResolutionScope;
            if(scope.Kind==HandleKind.AssemblyReference)return reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
            if(scope.Kind!=HandleKind.TypeReference)return null;handle=(TypeReferenceHandle)scope;
        }
        throw new IOException("加载器类型信息超出安全限制。");
    }
    private static string Method(string type,string name,MethodSignature<string> signature)=>type+"::"+name+"`"+signature.GenericParameterCount+"("+string.Join(",",signature.ParameterTypes)+"):"+signature.ReturnType+"|"+signature.Header.IsInstance+"|"+signature.Header.CallingConvention;
    private sealed class SurfaceNames:ISignatureTypeProvider<string,object?>
    {
        public string GetArrayType(string element,ArrayShape shape)=>element+"["+new string(',',Math.Max(0,shape.Rank-1))+"]";
        public string GetByReferenceType(string element)=>element+"&";
        public string GetFunctionPointerType(MethodSignature<string> signature)=>Method("fn","",signature);
        public string GetGenericInstantiation(string type,ImmutableArray<string> arguments)=>type+"<"+string.Join(",",arguments)+">";
        public string GetGenericMethodParameter(object? context,int index)=>"!!"+index;
        public string GetGenericTypeParameter(object? context,int index)=>"!"+index;
        public string GetModifiedType(string modifier,string type,bool required)=>type+(required?" modreq(":" modopt(")+modifier+")";
        public string GetPinnedType(string type)=>type+" pinned";
        public string GetPointerType(string type)=>type+"*";
        public string GetPrimitiveType(PrimitiveTypeCode code)=>"System."+code;
        public string GetSZArrayType(string type)=>type+"[]";
        public string GetTypeFromDefinition(MetadataReader reader,TypeDefinitionHandle handle,byte rawTypeKind)
        {
            var names=new List<string>();string space="";
            for(int depth=0;depth<32;depth++)
            {
                var type=reader.GetTypeDefinition(handle);names.Add(reader.GetString(type.Name));var parent=type.GetDeclaringType();
                if(parent.IsNil){space=reader.GetString(type.Namespace);names.Reverse();return(space.Length==0?"":space+".")+string.Join("+",names);}handle=parent;
            }
            throw new IOException("加载器嵌套类型超过安全范围。");
        }
        public string GetTypeFromReference(MetadataReader reader,TypeReferenceHandle handle,byte rawTypeKind)
        {
            var names=new List<string>();string space="";
            for(int depth=0;depth<32;depth++)
            {
                var type=reader.GetTypeReference(handle);names.Add(reader.GetString(type.Name));
                if(type.ResolutionScope.Kind!=HandleKind.TypeReference){space=reader.GetString(type.Namespace);names.Reverse();return(space.Length==0?"":space+".")+string.Join("+",names);}handle=(TypeReferenceHandle)type.ResolutionScope;
            }
            throw new IOException("加载器类型引用超过安全范围。");
        }
        public string GetTypeFromSpecification(MetadataReader reader,object? context,TypeSpecificationHandle handle,byte rawTypeKind)=>reader.GetTypeSpecification(handle).DecodeSignature(this,context);
    }
}
