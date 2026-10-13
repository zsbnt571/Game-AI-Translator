using AssetsTools.NET;using AssetsTools.NET.Extra;using Mono.Cecil;using Mono.Cecil.Cil;
namespace ScreenshotTranslationUiTester;

internal sealed class UnityScriptShape(string managed):IDisposable
{
    private readonly Dictionary<string,AssemblyDefinition?> modules=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,bool> empty=new(StringComparer.Ordinal);
    private readonly Dictionary<string,HashSet<string>> displayFields=new(StringComparer.Ordinal);
    private TypeDefinition? ScriptType(AssetsManager manager,AssetsFileInstance file,AssetTypeValueField value)
    {
        var script=manager.GetExtAsset(file,value["m_Script"]).baseField;if(script.IsDummy)return null;
        string module=script["m_AssemblyName"].AsString,space=script["m_Namespace"].AsString,name=script["m_ClassName"].AsString;
        if(Path.GetFileName(module)!=module)return null;
        string path=Path.Combine(managed,module.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)?module:module+".dll");
        if(!modules.TryGetValue(path,out var assembly))
        {
            if(modules.Count>=256||!File.Exists(path)||new FileInfo(path).Length>32*1024*1024)return null;
            var resolver=new DefaultAssemblyResolver();resolver.AddSearchDirectory(managed);
            assembly=AssemblyDefinition.ReadAssembly(path,new ReaderParameters{AssemblyResolver=resolver});modules[path]=assembly;
        }
        return assembly?.MainModule.GetType(space.Length==0?name:space+"."+name);
    }
    internal HashSet<string> DisplayFields(AssetsManager manager,AssetsFileInstance file,AssetTypeValueField value,CancellationToken cancellation)
    {
        try
        {
            cancellation.ThrowIfCancellationRequested();var type=ScriptType(manager,file,value);if(type is null)return [];
            string key=type.Module.Mvid+":"+type.FullName;
            if(displayFields.TryGetValue(key,out var fields))return fields;
            return displayFields[key]=ReadDisplayFields(type,cancellation);
        }
        catch(OperationCanceledException){throw;}catch{return [];}
    }
    internal static HashSet<string> ReadDisplayFields(TypeDefinition type,CancellationToken cancellation)
    {
        var fields=new HashSet<string>(StringComparer.Ordinal);int instructions=0;
        foreach(var method in type.Methods.Take(4096))
        {
            cancellation.ThrowIfCancellationRequested();
            if(!method.HasBody||method.ReturnType.FullName!="System.String"||!UnityTextResourceLanguageSelection.IsTextField(method.Name))continue;
            if(method.Body.Instructions.Count>100000-instructions)break;instructions+=method.Body.Instructions.Count;
            var code=method.Body.Instructions;
            for(int i=1;i<code.Count;i++)
            {
                if((i&255)==0)cancellation.ThrowIfCancellationRequested();
                if(code[i].OpCode.Code!=Code.Ret||code[i-1].OpCode.Code!=Code.Ldfld||code[i-1].Operand is not FieldReference reference||reference.DeclaringType.FullName!=type.FullName)continue;
                var field=type.Fields.FirstOrDefault(f=>f.Name==reference.Name);
                // Two independent author hints: a serialized prose field and a
                // direct return from a dialogue/text accessor. State names alone
                // are not display evidence; editor notes and IDs stay excluded.
                if(field is null||field.IsStatic||field.IsNotSerialized||field.FieldType.FullName!="System.String"||UnityTextResourceLanguageSelection.IsIdentifier(field.Name))continue;
                if(!(field.IsPublic||field.CustomAttributes.Any(a=>a.AttributeType.FullName=="UnityEngine.SerializeField")))continue;
                if(field.CustomAttributes.Any(a=>a.AttributeType.FullName is "UnityEngine.TextAreaAttribute" or "UnityEngine.MultilineAttribute"))fields.Add(field.Name);
            }
        }
        return fields;
    }
    internal bool HasNoSerializedFields(AssetsManager manager,AssetsFileInstance file,AssetTypeValueField value)
    {
        try
        {
            var script=manager.GetExtAsset(file,value["m_Script"]).baseField;
            if(script.IsDummy)return false;
            string module=script["m_AssemblyName"].AsString;
            string space=script["m_Namespace"].AsString,name=script["m_ClassName"].AsString;
            string full=space.Length==0?name:space+"."+name,key=module+":"+full;
            if(empty.TryGetValue(key,out bool result))return result;
            if(Path.GetFileName(module)!=module)return false;
            string path=Path.Combine(managed,module.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)?module:module+".dll");
            if(!modules.TryGetValue(path,out var assembly))
            {
                var resolver=new DefaultAssemblyResolver();resolver.AddSearchDirectory(managed);
                assembly=File.Exists(path)?AssemblyDefinition.ReadAssembly(path,new ReaderParameters{AssemblyResolver=resolver}):null;
                modules[path]=assembly;
            }
            var type=assembly?.MainModule.GetType(full);
            if(type is null)return false;
            var seen=new HashSet<string>();bool complete=false;
            for(int depth=0;depth<32&&type is not null;depth++)
            {
                if(!seen.Add(type.FullName))break;
                if(type.Namespace=="UnityEngine"&&type.Name is "MonoBehaviour" or "Behaviour" or "Component" or "Object"){complete=true;break;}
                if(type.Fields.Any(f=>!f.IsStatic&&!f.IsNotSerialized&&(f.IsPublic||f.CustomAttributes.Any(a=>a.AttributeType.FullName is "UnityEngine.SerializeField" or "UnityEngine.SerializeReference"))))return empty[key]=false;
                if(type.BaseType is null){complete=true;break;}
                type=type.BaseType.Resolve();
            }
            return empty[key]=complete;
        }
        catch{return false;}
    }
    public void Dispose(){foreach(var module in modules.Values)module?.Dispose();}
}
