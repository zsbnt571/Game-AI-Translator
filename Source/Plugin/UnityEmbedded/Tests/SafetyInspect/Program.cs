using System.Reflection;
using LibCpp2IL;
using AssetRipper.Primitives;
using System.Text.Json;
if(args.Length==2)
{
    if(!LibCpp2IlMain.LoadFromFile(args[0],args[1],UnityVersion.Parse("2022.3.31f1")))throw new Exception("IL2CPP parse failed");
    foreach(var type in LibCpp2IlMain.TheMetadata.typeDefs.Where(t=>t.FullName=="MonsterBox.Systems.Saving.SaveManager"))
        Console.WriteLine(JsonSerializer.Serialize(new{Type=type.FullName,Methods=type.Methods.Select(m=>new{Name=m.Name,Signature=m.HumanReadableSignature,Rva=m.Rva,Address=m.MethodPointer,Offset=m.MethodOffsetInFile})}));
    foreach(var method in LibCpp2IlMain.TheMetadata.methodDefs.Where(m=>m.DeclaringType.FullName=="UnityEngine.Application"&&m.Name=="get_persistentDataPath"))Console.WriteLine(JsonSerializer.Serialize(new{Method=method.HumanReadableSignature,Rva=method.Rva,Address=method.MethodPointer}));
    foreach(var method in LibCpp2IlMain.TheMetadata.methodDefs.Where(m=>new ulong[]{0x11A31A0,0x1E03160,0x1E01C70,0x1DFE230,0x1DFE2D0}.Contains((ulong)m.Rva)))Console.WriteLine(JsonSerializer.Serialize(new{Type=method.DeclaringType.FullName,Method=method.HumanReadableSignature,Rva=method.Rva}));
    return;
}
var assembly=typeof(LibCpp2IL.LibCpp2IlMain).Assembly;
foreach(var type in assembly.GetTypes().Where(t=>t.Name is "LibCpp2IlMain" or "Il2CppTypeDefinition" or "Il2CppMethodDefinition" or "Il2CppMetadata" or "Il2CppBinary"))
{
    Console.WriteLine(type.FullName);foreach(var member in type.GetMembers(BindingFlags.Public|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly))Console.WriteLine("  "+member);
}


