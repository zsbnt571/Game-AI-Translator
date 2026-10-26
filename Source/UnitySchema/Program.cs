using Cpp2IL.Core;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.InstructionSets;
using LibCpp2IL;
using AssetRipper.Primitives;

// Offline parser. Input binaries are data only; generated assembly bodies are
// never loaded or run. Only bundled, versioned parser libraries are executable.
if(args.Length!=4)throw new ArgumentException("binary metadata unityVersion output");
string binary=Path.GetFullPath(args[0]),metadata=Path.GetFullPath(args[1]),output=Path.GetFullPath(args[3]);
if(Directory.Exists(output))throw new IOException("Output already exists");
if(new FileInfo(binary).Length>536870912||new FileInfo(metadata).Length>134217728)throw new IOException("Input limit exceeded");
InstructionSetRegistry.RegisterInstructionSet<X86InstructionSet>(DefaultInstructionSets.X86_32);
InstructionSetRegistry.RegisterInstructionSet<X86InstructionSet>(DefaultInstructionSets.X86_64);
LibCpp2IlBinaryRegistry.RegisterBuiltInBinarySupport();
Cpp2IlApi.ConfigureLib(false);
Console.WriteLine("FUSION_SCHEMA_STAGE=initialize");
Cpp2IlApi.InitializeLibCpp2Il(binary,metadata,UnityVersion.Parse(args[2]),false);
Console.WriteLine("FUSION_SCHEMA_STAGE=native-display");
NativeDisplayCatalog.Result display;
try { display=new NativeDisplayCatalog(Cpp2IlApi.CurrentAppContext!).Read(); }
catch(Exception e) when(e is not OutOfMemoryException)
{
    // Native flow analysis is supplementary. An unsupported instruction/layout
    // must not discard the independently readable serialized field catalogue.
    display=new([], [e.GetType().Name+": native display tracing unavailable"],0);
}
// Display-flow analysis is independently useful even if the later field
// assembly writer exhausts its budget. Publish this bounded data with its own
// integrity marker before allocating the assembly output graphs.
Directory.CreateDirectory(output);
string displayFile=Path.Combine(output,"native-display.json");
File.WriteAllText(displayFile,System.Text.Json.JsonSerializer.Serialize(display));
if(new FileInfo(displayFile).Length>16*1024*1024)throw new IOException("Native display output limit exceeded");
File.WriteAllText(Path.Combine(output,"native-display.sha256"),Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(displayFile))));
File.WriteAllText(Path.Combine(output,"native-display.complete"),"FusionUnitySchema/119");
Console.WriteLine("FUSION_SCHEMA_STAGE=attributes");
Console.WriteLine("FUSION_SCHEMA_STAGE=assemblies");
using(var fields=new SerializedFieldAssemblies())fields.Write(Cpp2IlApi.CurrentAppContext!,output);
var files=Directory.GetFiles(output,"*.dll",SearchOption.TopDirectoryOnly);
if(files.Length is <1 or >512||files.Sum(p=>new FileInfo(p).Length)>128L*1024*1024)throw new IOException("Output limit exceeded");
Console.WriteLine("FUSION_SCHEMA_STAGE=manifest");
var hashes=files.ToDictionary(p=>Path.GetFileName(p)!,p=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))));
hashes.Add("native-display.json",Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(displayFile))));
File.WriteAllText(Path.Combine(output,"manifest.json"),System.Text.Json.JsonSerializer.Serialize(hashes));
File.WriteAllText(Path.Combine(output,"complete"),"FusionUnitySchema/119");
Console.WriteLine("FUSION_SCHEMA_STAGE=complete");
