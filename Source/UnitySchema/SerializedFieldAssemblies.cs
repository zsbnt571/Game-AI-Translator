using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.Utils;
using LibCpp2IL.BinaryStructures;
using Mono.Cecil;

/// <summary>
/// Read-only field schemas for AssetsTools. These deliberately contain no game
/// method bodies and must never be loaded as executable game assemblies.
/// Keep the complete type universe so SerializeReference and cross-assembly
/// fields can be resolved without guessing a dialogue-only dependency list.
/// </summary>
internal sealed class SerializedFieldAssemblies : IDisposable
{
    private readonly Dictionary<AssemblyAnalysisContext, AssemblyDefinition> assemblies = new();
    private readonly Dictionary<TypeAnalysisContext, TypeDefinition> types = new();
    private readonly Dictionary<(TypeAnalysisContext, ModuleDefinition), TypeReference> references = new();
    // Cecil's CoreTypeSystem assumes mscorlib was read from a PE image. These
    // are newly-created, sometimes stripped schemas, so obtain the ECMA-335
    // primitive signatures from a non-core module. They encode inline in field
    // signatures and do not add a dependency on this scratch module.
    private readonly ModuleDefinition primitiveSignatures = ModuleDefinition.CreateModule("Fusion.FieldSchema.Primitives", ModuleKind.Dll);
    private int fieldCount, serializedPrivateCount, referenceCount;

    internal void Write(ApplicationAnalysisContext app, string output)
    {
        var contexts = app.AllTypes.Where(t => t.Name != "<Module>").ToArray();
        if (app.Assemblies.Count is < 1 or > 512 || contexts.Length > 200000)
            throw new InvalidDataException("Field schema type limit exceeded");
        foreach (var assembly in app.Assemblies)
        {
            string name = assembly.CleanAssemblyName;
            if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("Invalid field schema assembly name");
            assemblies.Add(assembly, AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(assembly.Name, assembly.Version), name + ".dll", ModuleKind.Dll));
        }
        // Declare every type before importing signatures, preserving nesting and
        // generic owner/position. This avoids method/attribute reconstruction.
        foreach (var type in contexts)
            types.Add(type, new TypeDefinition(type.Namespace, type.Name, (TypeAttributes)type.Attributes));
        foreach (var type in contexts)
        {
            var definition = types[type];
            if (type.DeclaringType is { } declaring)
                types[declaring].NestedTypes.Add(definition);
            else
                assemblies[type.DeclaringAssembly].MainModule.Types.Add(definition);
            foreach (var parameter in type.GenericParameters)
                definition.GenericParameters.Add(new GenericParameter(parameter.Name, definition) { Attributes = (GenericParameterAttributes)parameter.Attributes });
        }
        Console.WriteLine("FUSION_SCHEMA_STAGE=field-types");
        // ImportReference captures value/class identity immediately. Prime all
        // value types before importing any forward field or generic signature.
        foreach (var type in contexts)
        {
            if (type.Definition is not { } metadata) continue;
            string? basis = metadata.IsEnumType ? "System.Enum" : metadata.IsValueType ? "System.ValueType" : null;
            if (basis is not null)
            {
                var baseDefinition = types.FirstOrDefault(p => p.Key.FullName == basis).Value
                    ?? throw new InvalidDataException("Missing value type base");
                types[type].BaseType = types[type].Module.ImportReference(baseDefinition);
            }
        }
        foreach (var type in contexts)
        {
            var definition = types[type];
            if (type.BaseType is { } parent) definition.BaseType = Reference(parent, definition.Module);
        }
        foreach (var type in contexts)
        {
            var definition = types[type];
            foreach (var field in type.Fields)
            {
                var attributes = (FieldAttributes)field.Attributes;
                // None of these fields participate in Unity serialization. Do
                // not emit static data, constants, RVA blobs or readonly state.
                if ((attributes & (FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.InitOnly | FieldAttributes.NotSerialized)) != 0) continue;
                var markers = SerializationMarkers(field, app.MetadataVersion);
                bool serialize = markers.SerializeField;
                bool serializeReference = markers.SerializeReference;
                if ((attributes & FieldAttributes.FieldAccessMask) != FieldAttributes.Public && !serialize && !serializeReference) continue;
                var emitted = new FieldDefinition(field.Name, attributes & ~(FieldAttributes.HasFieldRVA | FieldAttributes.HasDefault), Reference(field.FieldType, definition.Module));
                if (serialize) AddMarker(emitted, definition.Module, "UnityEngine.SerializeField");
                if (serializeReference) { AddMarker(emitted, definition.Module, "UnityEngine.SerializeReference"); referenceCount++; }
                definition.Fields.Add(emitted);
                if (++fieldCount > 1000000) throw new InvalidDataException("Field schema field limit exceeded");
                if (!emitted.IsPublic && (serialize || serializeReference)) serializedPrivateCount++;
            }
        }
        Console.WriteLine("FUSION_SCHEMA_STAGE=field-write");
        long bytes = 0;
        foreach (var assembly in assemblies.Values)
        {
            string path = Path.Combine(output, assembly.MainModule.Name);
            assembly.Write(path);
            if ((bytes += new FileInfo(path).Length) > 128L * 1024 * 1024) throw new InvalidDataException("Field schema output limit exceeded");
        }
        // Structural counts only. Never put game strings into diagnostic output.
        Console.WriteLine($"FUSION_SCHEMA_COUNTS=types:{types.Count},fields:{fieldCount},private:{serializedPrivateCount},references:{referenceCount}");
    }

    private static (bool SerializeField, bool SerializeReference) SerializationMarkers(FieldAnalysisContext field, float metadataVersion)
    {
        // Only attribute identity is needed. On pre-29 metadata the identity
        // table remains valid even when native attribute constructors cannot be
        // analyzed. An empty analyzed list must not hide that table. On v29+
        // read constructor identities without decoding arbitrary payloads.
        IEnumerable<string> names;
        if (metadataVersion >= 29)
        {
            if (field.RawIl2CppCustomAttributeData.Length == 0) return (false, false);
            using var stream = new MemoryStream(field.RawIl2CppCustomAttributeData.ToArray());
            uint count = stream.ReadUnityCompressedUint();
            if (count > 65536) throw new InvalidDataException("Field attribute limit exceeded");
            names = V29AttributeUtils.ReadConstructors(stream, count, field.AppContext)
                .Select(c => c.DeclaringType?.FullName ?? throw new InvalidDataException("Unresolved field attribute identity")).ToArray();
        }
        else
        {
            if (field.AttributeTypes is null) throw new InvalidDataException("Unavailable field attribute identity table");
            names = field.AttributeTypes.Where(t => t.Type == Il2CppTypeEnum.IL2CPP_TYPE_CLASS)
                .Select(t => t.AsClass()?.FullName ?? throw new InvalidDataException("Unresolved field attribute identity"));
        }
        bool serialize = false, reference = false;
        foreach (string name in names)
        {
            serialize |= name == "UnityEngine.SerializeField";
            reference |= name == "UnityEngine.SerializeReference";
        }
        return (serialize, reference);
    }

    private TypeReference Reference(TypeAnalysisContext type, ModuleDefinition module)
    {
        if (references.TryGetValue((type, module), out var cached)) return cached;
        TypeReference result;
        if (type is GenericParameterTypeAnalysisContext parameter)
        {
            if (parameter.Owner is not TypeAnalysisContext owner || !types.TryGetValue(owner, out var declaration) || parameter.Index < 0 || parameter.Index >= declaration.GenericParameters.Count)
                throw new InvalidDataException("Unresolved field generic parameter owner");
            result = declaration.GenericParameters[parameter.Index];
        }
        else if (type is GenericInstanceTypeAnalysisContext generic)
        {
            var instance = new GenericInstanceType(Reference(generic.GenericType, module));
            foreach (var argument in generic.GenericArguments) instance.GenericArguments.Add(Reference(argument, module));
            result = instance;
        }
        else if (type is SzArrayTypeAnalysisContext vector) result = new ArrayType(Reference(vector.ElementType, module));
        else if (type is ArrayTypeAnalysisContext array) result = new ArrayType(Reference(array.ElementType, module), array.Rank);
        else if (type is PointerTypeAnalysisContext pointer) result = new PointerType(Reference(pointer.ElementType, module));
        else if (type is ByRefTypeAnalysisContext byref) result = new ByReferenceType(Reference(byref.ElementType, module));
        else if (type is PinnedTypeAnalysisContext pinned) result = new PinnedType(Reference(pinned.ElementType, module));
        else
        {
            result = type.FullName switch
            {
                "System.Void" => primitiveSignatures.TypeSystem.Void,
                "System.Boolean" => primitiveSignatures.TypeSystem.Boolean,
                "System.Char" => primitiveSignatures.TypeSystem.Char,
                "System.SByte" => primitiveSignatures.TypeSystem.SByte,
                "System.Byte" => primitiveSignatures.TypeSystem.Byte,
                "System.Int16" => primitiveSignatures.TypeSystem.Int16,
                "System.UInt16" => primitiveSignatures.TypeSystem.UInt16,
                "System.Int32" => primitiveSignatures.TypeSystem.Int32,
                "System.UInt32" => primitiveSignatures.TypeSystem.UInt32,
                "System.Int64" => primitiveSignatures.TypeSystem.Int64,
                "System.UInt64" => primitiveSignatures.TypeSystem.UInt64,
                "System.Single" => primitiveSignatures.TypeSystem.Single,
                "System.Double" => primitiveSignatures.TypeSystem.Double,
                "System.String" => primitiveSignatures.TypeSystem.String,
                "System.Object" => primitiveSignatures.TypeSystem.Object,
                "System.IntPtr" => primitiveSignatures.TypeSystem.IntPtr,
                "System.UIntPtr" => primitiveSignatures.TypeSystem.UIntPtr,
                "System.TypedReference" => primitiveSignatures.TypeSystem.TypedReference,
                _ => types.TryGetValue(type, out var definition) ? module.ImportReference(definition) : throw new InvalidDataException("Unresolved field schema type")
            };
        }
        references.Add((type, module), result);
        return result;
    }

    private void AddMarker(FieldDefinition field, ModuleDefinition module, string fullName)
    {
        var marker = types.FirstOrDefault(pair => pair.Key.FullName == fullName).Value ?? throw new InvalidDataException("Missing serialization attribute type");
        var constructor = new MethodReference(".ctor", primitiveSignatures.TypeSystem.Void, module.ImportReference(marker)) { HasThis = true };
        field.CustomAttributes.Add(new CustomAttribute(constructor));
    }

    public void Dispose()
    {
        foreach (var assembly in assemblies.Values) assembly.Dispose();
        assemblies.Clear(); types.Clear(); references.Clear();
        primitiveSignatures.Dispose();
    }
}
