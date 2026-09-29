using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

const string ProviderEnvironment="GAME_AI_TRANSLATOR_PAYLOAD_ROOT";
string Option(string name,string? fallback=null){int n=Array.IndexOf(args,name);return n>=0&&n+1<args.Length?Path.GetFullPath(args[n+1]):fallback??throw new ArgumentException("Missing "+name);}
string CandidateRoot=Option("--source-root",Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..","..")));
string ReviewRoot=Option("--report-root");
string baselineDll=Option("--baseline");
string candidateDll=Option("--candidate");
string catalogPath=Path.Combine(CandidateRoot,"Source","Screenshot","RuntimePayloadCatalog.json");
string runId=DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6];
string scratch=Path.Combine(CandidateRoot,"tools","RuntimePayloadTests","scratch",runId),reportDir=Path.Combine(ReviewRoot,"runtime-payload-tests",runId);
Directory.CreateDirectory(scratch);Directory.CreateDirectory(reportDir);
var jsonOptions=new JsonSerializerOptions{PropertyNameCaseInsensitive=true,WriteIndented=true};
var checks=new List<string>();var facts=new List<object>();var boundaries=new List<string>();
void Check(bool condition,string label){if(!condition)throw new InvalidOperationException("CHECK FAILED: "+label);checks.Add(label);Console.WriteLine("PASS "+label);}
void Reject(Action action,string label){bool rejected=false;try{action();}catch(Exception e)when(e is IOException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException){rejected=true;}Check(rejected,label);}
string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
string FileHash(string path){using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream));}
string? failure=null;string? previousRoot=Environment.GetEnvironmentVariable(ProviderEnvironment);
try
{
 var packages=JsonSerializer.Deserialize<Package[]>(File.ReadAllText(catalogPath),jsonOptions)!;
 var baseline=new Surface(baselineDll,Path.Combine(scratch,"baseline-data"));
 var candidate=new Surface(candidateDll,Path.Combine(scratch,"candidate-data"));
 string payloadRoot=Path.Combine(scratch,"payloads"),missingRoot=Path.Combine(scratch,"missing-payloads");Directory.CreateDirectory(missingRoot);
 string PayloadPath(Package p)=>Path.Combine(payloadRoot,p.Id,p.Version,p.FileName);
 Stream Open(Package p)=> (Stream)candidate.Call("RuntimePayloadProvider","Open",p.Id,p.Engine,p.Backend,p.Architecture,p.Version)!;
 Dictionary<string,byte[]> ZipFiles(Stream stream)
 {
  using var zip=new ZipArchive(stream,ZipArchiveMode.Read,true);var files=new Dictionary<string,byte[]>(StringComparer.Ordinal);
  foreach(var e in zip.Entries){if(e.FullName.EndsWith('/'))continue;using var data=e.Open();using var memory=new MemoryStream();data.CopyTo(memory);files.Add(e.FullName,memory.ToArray());}
  return files;
 }
 Dictionary<string,string> Snapshot(string directory,string gameRoot,string dataRoot)
 {
  var result=new Dictionary<string,string>(StringComparer.Ordinal);
  if(!Directory.Exists(directory))return result;
  foreach(string path in Directory.EnumerateFiles(directory,"*",SearchOption.AllDirectories))
  {
   string relative=Path.GetRelativePath(directory,path).Replace('\\','/');
   relative=Regex.Replace(relative,@"\b[a-fA-F0-9]{32}\b","{id}");relative=Regex.Replace(relative,@"\.restored-\d+",".restored-{time}");
   byte[] bytes=File.ReadAllBytes(path);
   if(Path.GetFileName(path).Contains("install.json")||Path.GetFileName(path).StartsWith(".fusion-translator-install.json"))
   {
    string text=Encoding.UTF8.GetString(bytes).Replace(gameRoot.Replace("\\","\\\\"),"{GAME}").Replace(dataRoot.Replace("\\","\\\\"),"{DATA}");
    text=Regex.Replace(text,@"\b[a-fA-F0-9]{32}\b","{id}");bytes=Encoding.UTF8.GetBytes(text);
   }
   result.Add(relative,Hash(bytes));
  }
  return result;
 }
 void Equivalent(Dictionary<string,string> a,Dictionary<string,string> b,string label)
 {Check(a.Count==b.Count&&a.All(p=>b.GetValueOrDefault(p.Key)==p.Value),label);}
 void Write(string path,byte[] bytes){Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,bytes);}
 byte[] Pe(string architecture,string? text=null)
 {
  var bytes=new byte[1024];using var writer=new BinaryWriter(new MemoryStream(bytes));writer.Write((ushort)0x5a4d);writer.BaseStream.Position=60;writer.Write(128);writer.BaseStream.Position=128;writer.Write(0x4550);writer.Write((ushort)(architecture=="x64"?0x8664:0x14c));
  if(text is not null)Encoding.ASCII.GetBytes(text).CopyTo(bytes,512);return bytes;
 }
 string UnityStub(string root,string backend,string architecture)
 {
  string exe=Path.Combine(root,"OfflineFixture.exe"),data=Path.Combine(root,"OfflineFixture_Data");Write(exe,Pe(architecture));Write(Path.Combine(root,"UnityPlayer.dll"),[]);Write(Path.Combine(data,"globalgamemanagers"),Encoding.ASCII.GetBytes("neutral fixture 2021.3.16f1"));
  if(backend=="Mono")Write(Path.Combine(data,"Managed","Assembly-CSharp.dll"),[]);
  else {Write(Path.Combine(root,"GameAssembly.dll"),[]);byte[] meta=new byte[64];BitConverter.GetBytes(0xFAB11BAFu).CopyTo(meta,0);BitConverter.GetBytes(29).CopyTo(meta,4);Write(Path.Combine(data,"il2cpp_data","Metadata","global-metadata.dat"),meta);}
  Write(Path.Combine(root,"player-save.keep"),Encoding.UTF8.GetBytes("neutral original save sentinel"));Write(Path.Combine(root,"BepInEx","cache","sentinel.keep"),Encoding.UTF8.GetBytes("neutral original cache sentinel"));return exe;
 }
 string UnrealStub(string root,Surface surface)
 {
  string project=Path.Combine(root,"NeutralProject"),exe=Path.Combine(project,"Binaries","Win64","NeutralProject-Win64-Shipping.exe");Write(exe,Pe("x64","++UE5+Release-5.3 neutral static marker"));
  string pak=Path.Combine(project,"Content","Paks","neutral.pak");byte[] footer=new byte[44];BitConverter.GetBytes(0x5A6F12E1u).CopyTo(footer,0);BitConverter.GetBytes(1).CopyTo(footer,4);Write(pak,footer);
  Check(surface.Call("UnrealGameAdapter","Detect",exe) is not null,"synthetic UE5 detected without execution: "+surface.Name);
  string storage=(string)surface.Call("UnrealGameAdapter","Storage",exe)!;Directory.CreateDirectory(storage);
  // Exact existing-catalog fast path: deliberately prevent the extractor Process.Start branch.
  File.WriteAllText(Path.Combine(storage,"catalog.json"),"{\"Texts\":[],\"Assets\":0,\"Unreadable\":[]}");
  File.WriteAllText(Path.Combine(storage,"catalog.json.stamp"),"78.2|5.3|neutral.pak:44:"+File.GetLastWriteTimeUtc(pak).Ticks);
  Write(Path.Combine(root,"player-save.keep"),Encoding.UTF8.GetBytes("neutral unreal save sentinel"));return exe;
 }
 var payloadEntries=new Dictionary<string,Dictionary<string,byte[]>>(StringComparer.Ordinal);
 Environment.SetEnvironmentVariable(ProviderEnvironment,payloadRoot);
 foreach(var p in packages)
 {
  string resource=baseline.Assembly.GetManifestResourceNames().Single(n=>n.EndsWith("."+p.FileName,StringComparison.Ordinal));
  using(var raw=baseline.Assembly.GetManifestResourceStream(resource)!){string path=PayloadPath(p);Directory.CreateDirectory(Path.GetDirectoryName(path)!);using var file=File.Create(path);raw.CopyTo(file);}
  File.WriteAllText(PayloadPath(p)+".json",JsonSerializer.Serialize(p,jsonOptions));
  Check(new FileInfo(PayloadPath(p)).Length==p.Bytes&&FileHash(PayloadPath(p)).Equals(p.Sha256,StringComparison.OrdinalIgnoreCase),"pinned raw ZIP equals reviewed baseline: "+p.Id);
  using var external=Open(p);Check(external.Position==0,"verified external stream rewound: "+p.Id);
  var entries=ZipFiles(external);payloadEntries[p.Id]=entries;
  using var embedded=baseline.Assembly.GetManifestResourceStream(resource)!;var old=ZipFiles(embedded);
  Check(entries.Count==old.Count&&entries.All(x=>old.TryGetValue(x.Key,out var data)&&x.Value.AsSpan().SequenceEqual(data)),"all expanded ZIP entry bytes equal baseline: "+p.Id);
  Reject(()=>{using var blocked=new FileStream(PayloadPath(p),FileMode.Open,FileAccess.Write,FileShare.ReadWrite);},"verified open handle denies ZIP mutation: "+p.Id);
  facts.Add(new{package=p.Id,bytes=p.Bytes,sha256=FileHash(PayloadPath(p)),entries=entries.OrderBy(x=>x.Key,StringComparer.Ordinal).Select(x=>new{name=x.Key,bytes=x.Value.Length,sha256=Hash(x.Value)}).ToArray()});
 }
 Check(!candidate.Assembly.GetManifestResourceNames().Any(n=>packages.Any(p=>n.EndsWith("."+p.FileName,StringComparison.Ordinal))),"candidate has no embedded runtime ZIP fallback");
 foreach(var p in packages)
 {
  string path=PayloadPath(p),descriptor=path+".json",metadata=File.ReadAllText(descriptor);
  foreach(string field in new[]{"Id","Engine","Backend","Architecture","Version","FileName","Sha256"})
  {var node=JsonNode.Parse(metadata)!;node[field]="wrong-"+field;File.WriteAllText(descriptor,node.ToJsonString());Reject(()=>{using var s=Open(p);},"mismatched sidecar "+field+" rejected: "+p.Id);File.WriteAllText(descriptor,metadata);}
  var sizeNode=JsonNode.Parse(metadata)!;sizeNode["Bytes"]=p.Bytes+1;File.WriteAllText(descriptor,sizeNode.ToJsonString());Reject(()=>{using var s=Open(p);},"mismatched sidecar size rejected: "+p.Id);File.WriteAllText(descriptor,metadata);
  File.Move(descriptor,descriptor+".held");try{Reject(()=>{using var s=Open(p);},"missing sidecar rejected: "+p.Id);}finally{File.Move(descriptor+".held",descriptor);}
  File.Move(path,path+".held");try{Reject(()=>{using var s=Open(p);},"missing ZIP rejected: "+p.Id);}finally{File.Move(path+".held",path);}
  using(var corrupt=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){int first=corrupt.ReadByte();corrupt.Position=0;corrupt.WriteByte((byte)(first^1));}
  try{Reject(()=>{using var s=Open(p);},"changed ZIP SHA rejected: "+p.Id);}finally{using var fix=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);int first=fix.ReadByte();fix.Position=0;fix.WriteByte((byte)(first^1));}
  foreach(int mismatch in new[]{1,2,3,4}){var values=new[]{p.Id,p.Engine,p.Backend,p.Architecture,p.Version};values[mismatch]="wrong";Reject(()=>{using var s=(Stream)candidate.Call("RuntimePayloadProvider","Open",values.Cast<object>().ToArray())!;},"mismatched request field "+mismatch+" rejected: "+p.Id);}
 }
 var representative=packages.First();Reject(()=>{using var s=(Stream)candidate.Call("RuntimePayloadProvider","Open","unknown-id","Unity","Mono","x64","0.6.0.85")!;},"unknown package id rejected");
 Environment.SetEnvironmentVariable(ProviderEnvironment,"relative-path");Reject(()=>{using var s=Open(representative);},"relative payload root rejected");Environment.SetEnvironmentVariable(ProviderEnvironment,payloadRoot);
 foreach(var p in packages.Where(p=>p.Id is "unity-mono-x86" or "unity-mono-x64" or "unity-il2cpp-x64"))
 {
  var installed=new List<Dictionary<string,string>>();var restored=new List<Dictionary<string,string>>();
  foreach(var surface in new[]{baseline,candidate})
  {
   string root=Path.Combine(scratch,"games",p.Id,surface.Name),exe=UnityStub(root,p.Backend,p.Architecture);
   var shared=payloadEntries[p.Id].First(x=>x.Key.EndsWith("winhttp.dll",StringComparison.OrdinalIgnoreCase));Write(Path.Combine(root,shared.Key),shared.Value);
   var before=Snapshot(root,root,surface.DataRoot);
   Check(surface.Call("UnityEmbeddedAdapter","Detect",exe) is not null,"synthetic Unity detected: "+p.Id+"/"+surface.Name);
   surface.Call("UnityEmbeddedAdapter","Install",exe);Check((bool)surface.Call("UnityEmbeddedAdapter","IsInstalled",exe)!,"real Unity installation verified: "+p.Id+"/"+surface.Name);
   installed.Add(Snapshot(root,root,surface.DataRoot));surface.Call("UnityEmbeddedAdapter","Install",exe);Equivalent(installed[^1],Snapshot(root,root,surface.DataRoot),"same-version Unity reinstallation is unchanged: "+p.Id+"/"+surface.Name);
   if(surface==candidate)Environment.SetEnvironmentVariable(ProviderEnvironment,missingRoot);
   surface.Call("UnityEmbeddedAdapter","Restore",exe);var final=Snapshot(root,root,surface.DataRoot);Equivalent(before,final,"restore preserves preexisting loader/cache/save with ZIP absent: "+p.Id+"/"+surface.Name);restored.Add(final);
   Environment.SetEnvironmentVariable(ProviderEnvironment,payloadRoot);
  }
  Equivalent(installed[0],installed[1],"baseline/external Unity installed bytes and normalized ledger match: "+p.Id);Equivalent(restored[0],restored[1],"baseline/external Unity restored file state matches: "+p.Id);
 }
 var legacyInstalled=new List<Dictionary<string,string>>();var legacyRestored=new List<Dictionary<string,string>>();
 foreach(var surface in new[]{baseline,candidate})
 {
  string root=Path.Combine(scratch,"games","legacy",surface.Name),exe=UnityStub(root,"Mono","x64");object game=surface.Game(exe,"mgi"),installer=surface.New("LegacyMgiAdapterInstaller"),settings=surface.Settings();
  string config=Path.Combine(root,"BepInEx","config","local.codex.mgi.translator.cfg");Write(config,Encoding.UTF8.GetBytes("original neutral configuration\n"));var before=Snapshot(root,root,surface.DataRoot);
  surface.CallInstance(installer,"Install",game,settings,"F8");surface.CallInstance(installer,"Verify",game);legacyInstalled.Add(Snapshot(root,root,surface.DataRoot));
  surface.CallInstance(installer,"Install",game,settings,"F8");Equivalent(legacyInstalled[^1],Snapshot(root,root,surface.DataRoot),"legacy reinstall preserves backup/config/cache: "+surface.Name);
  if(surface==candidate)Environment.SetEnvironmentVariable(ProviderEnvironment,missingRoot);surface.CallInstance(installer,"Restore",game);Environment.SetEnvironmentVariable(ProviderEnvironment,payloadRoot);
  Check(before.All(p=>Snapshot(root,root,surface.DataRoot).GetValueOrDefault(p.Key)==p.Value),"legacy restore recovers all initial files without ZIP: "+surface.Name);
  Check(!File.Exists(Path.Combine(root,"BepInEx","plugins","MGITranslator","MGITranslator.dll")),"legacy restore removes owned plugin: "+surface.Name);legacyRestored.Add(Snapshot(root,root,surface.DataRoot));
 }
 Equivalent(legacyInstalled[0],legacyInstalled[1],"baseline/external legacy payload/config/normalized ledger and backups equal");Equivalent(legacyRestored[0],legacyRestored[1],"baseline/external legacy restored bytes/retained backups equal");
 var unrealInstalled=new List<Dictionary<string,string>>();var unrealLedgers=new List<string>();var unrealRestored=new List<Dictionary<string,string>>();
 foreach(var surface in new[]{baseline,candidate})
 {
  string root=Path.Combine(scratch,"games","unreal",surface.Name),exe=UnrealStub(root,surface);var before=Snapshot(root,root,surface.DataRoot);
  surface.Call("UnrealGameAdapter","Install",exe);Check((bool)surface.Call("UnrealGameAdapter","IsInstalled",exe)!,"real UE5 install verifies: "+surface.Name);
  unrealInstalled.Add(Snapshot(root,root,surface.DataRoot));string storage=(string)surface.Call("UnrealGameAdapter","Storage",exe)!;
  string record=File.ReadAllText(Path.Combine(storage,"install.json")).Replace(root.Replace("\\","\\\\"),"{GAME}");unrealLedgers.Add(record);
  surface.Call("UnrealGameAdapter","Install",exe);Equivalent(unrealInstalled[^1],Snapshot(root,root,surface.DataRoot),"same-version UE5 reinstall remains unchanged: "+surface.Name);
  if(surface==candidate)Environment.SetEnvironmentVariable(ProviderEnvironment,missingRoot);surface.Call("UnrealGameAdapter","Restore",exe);Environment.SetEnvironmentVariable(ProviderEnvironment,payloadRoot);
  Equivalent(before,Snapshot(root,root,surface.DataRoot),"UE5 restore restores initial game file state without ZIP: "+surface.Name);
  var archive=Directory.GetDirectories(storage,"restored-*").Single();unrealRestored.Add(Snapshot(archive,root,surface.DataRoot));
 }
 Equivalent(unrealInstalled[0],unrealInstalled[1],"baseline/external UE5 installed file bytes match");Check(unrealLedgers[0]==unrealLedgers[1],"baseline/external UE5 normalized install ledgers match");Equivalent(unrealRestored[0],unrealRestored[1],"baseline/external UE5 retained recovery bytes match");
 // Cloud's full install requires exact real-game hashes. Do not remove that gate.
 var specializedConfigurations=new List<string>();var specializedRestored=new List<Dictionary<string,string>>();
 foreach(var surface in new[]{baseline,candidate})
 {
  string root=Path.Combine(scratch,"games","specialized",surface.Name),exe=UnityStub(root,"Mono","x64");var game=surface.Game(exe,"cloud-meadow");var installer=surface.New("FusionAdapterInstaller");var settings=surface.Settings();
  var before=Snapshot(root,root,surface.DataRoot);Reject(()=>surface.CallInstance(installer,"Install",game,settings,"F8"),"specialized exact-game gate rejects a synthetic nonmatching game: "+surface.Name);Equivalent(before,Snapshot(root,root,surface.DataRoot),"specialized failed recognition writes no game files: "+surface.Name);
  string config=(string)surface.Call("FusionAdapterInstaller","CloudConfiguration",settings,"F8")!;
  specializedConfigurations.Add(config);
  facts.Add(new{surface=surface.Name,specializedConfigSha256=Hash(Encoding.UTF8.GetBytes(config))});
  string backupId=Guid.NewGuid().ToString("N");var files=new List<object>();
  foreach(var item in payloadEntries["unity-specialized"]){Write(Path.Combine(root,item.Key),item.Value);files.Add(new{Path=item.Key,Backup=(string?)null,BeforeHash=(string?)null,AfterHash=Hash(item.Value),ConfigHash=(string?)null});}
  string realConfig=(string)surface.Type("FusionCloudPayload").GetField("Config",Surface.Flags)!.GetRawConstantValue()!;Write(Path.Combine(root,realConfig),Encoding.UTF8.GetBytes(config));files.Add(new{Path=realConfig,Backup=(string?)null,BeforeHash=(string?)null,AfterHash=Hash(Encoding.UTF8.GetBytes(config)),ConfigHash=(string?)null});
  File.WriteAllText(Path.Combine(root,".fusion-translator-install.json"),JsonSerializer.Serialize(new{Schema=2,AdapterId="cloud-meadow",Version="0.6.0.85",Exe=exe,BackupId=backupId,Complete=false,Files=files,RetainedSharedFiles=Array.Empty<string>()},jsonOptions));
  if(surface==candidate)Environment.SetEnvironmentVariable(ProviderEnvironment,missingRoot);surface.CallInstance(installer,"Restore",game);Environment.SetEnvironmentVariable(ProviderEnvironment,payloadRoot);
  Check(before.All(p=>Snapshot(root,root,surface.DataRoot).GetValueOrDefault(p.Key)==p.Value),"specialized unfinished journal restore retains initial sentinels without ZIP: "+surface.Name);
  Check(!File.Exists(Path.Combine(root,realConfig))&&!File.Exists(Path.Combine(root,".fusion-translator-install.json")),"specialized unfinished journal restore completes without payload source: "+surface.Name);
  specializedRestored.Add(Snapshot(root,root,surface.DataRoot));
 }
 Check(specializedConfigurations[0]==specializedConfigurations[1],"baseline/external specialized production configuration bytes match");
 Equivalent(specializedRestored[0],specializedRestored[1],"baseline/external specialized unfinished-journal restored files match");
 boundaries.Add("Specialized installer exact real-game hash gate is intentionally not bypassed: full Cloud installation is not claimed; expanded bytes, production configuration, refusal/no-write, and synthetic unfinished-journal restore are covered.");
 // Fresh generic/legacy/UE installs must fail without changing the synthetic game.
 foreach(var p in packages.Where(p=>p.Id is "unity-mono-x86" or "unity-mono-x64" or "unity-il2cpp-x64" or "unity-legacy" or "unreal-runtime"))
 {
  string root=Path.Combine(scratch,"no-write",p.Id);string exe=p.Engine=="Unreal"?UnrealStub(root,candidate):UnityStub(root,p.Backend,p.Architecture);var before=Snapshot(root,root,candidate.DataRoot);var beforeData=Snapshot(candidate.DataRoot,root,candidate.DataRoot);
  Action install=p.Id=="unity-legacy"?()=>candidate.CallInstance(candidate.New("LegacyMgiAdapterInstaller"),"Install",candidate.Game(exe,"mgi"),candidate.Settings(),"F8"):p.Engine=="Unreal"?()=>candidate.Call("UnrealGameAdapter","Install",exe):()=>candidate.Call("UnityEmbeddedAdapter","Install",exe);
  Environment.SetEnvironmentVariable(ProviderEnvironment,missingRoot);Reject(install,"missing payload rejects fresh installer: "+p.Id);Equivalent(before,Snapshot(root,root,candidate.DataRoot),"missing payload fresh installer leaves game unchanged: "+p.Id);Equivalent(beforeData,Snapshot(candidate.DataRoot,root,candidate.DataRoot),"missing payload installer leaves data storage unchanged: "+p.Id);Environment.SetEnvironmentVariable(ProviderEnvironment,payloadRoot);
  string path=PayloadPath(p);using(var corrupt=new FileStream(path,FileMode.Open,FileAccess.ReadWrite)){int first=corrupt.ReadByte();corrupt.Position=0;corrupt.WriteByte((byte)(first^1));}
  try{Reject(install,"bad SHA rejects fresh installer: "+p.Id);Equivalent(before,Snapshot(root,root,candidate.DataRoot),"bad SHA fresh installer leaves game unchanged: "+p.Id);Equivalent(beforeData,Snapshot(candidate.DataRoot,root,candidate.DataRoot),"bad SHA installer leaves data storage unchanged: "+p.Id);}finally{using var fix=new FileStream(path,FileMode.Open,FileAccess.ReadWrite);int first=fix.ReadByte();fix.Position=0;fix.WriteByte((byte)(first^1));}
  string metadata=File.ReadAllText(path+".json");
  foreach(string field in new[]{"Backend","Architecture","Version"})
  {
   var node=JsonNode.Parse(metadata)!;node[field]="wrong-"+field;File.WriteAllText(path+".json",node.ToJsonString());
   try{Reject(install,"mismatched "+field+" rejects fresh installer: "+p.Id);Equivalent(before,Snapshot(root,root,candidate.DataRoot),"mismatched "+field+" leaves game unchanged: "+p.Id);Equivalent(beforeData,Snapshot(candidate.DataRoot,root,candidate.DataRoot),"mismatched "+field+" leaves data storage unchanged: "+p.Id);}finally{File.WriteAllText(path+".json",metadata);}
  }
 }
 // Create a real interrupted Unity upgrade journal with fully hashed old backups.
 {
  string root=Path.Combine(scratch,"interrupted-update"),exe=UnityStub(root,"Mono","x64");candidate.Call("UnityEmbeddedAdapter","Install",exe);
  string ledger=Path.Combine(root,".fusion-unity-embedded-install.json"),pending=Path.Combine(root,".fusion-unity-embedded-update.json");var before=JsonNode.Parse(File.ReadAllText(ledger))!;var after=before.DeepClone();string backup=".fusion-unity-backups/"+Guid.NewGuid().ToString("N");
  foreach(var file in before["Files"]!.AsArray().Where(n=>n!["Owned"]!.GetValue<bool>())){string relative=file!["Path"]!.GetValue<string>();Write(Path.Combine(root,backup,relative),File.ReadAllBytes(Path.Combine(root,relative)));}
  var changed=after["Files"]!.AsArray().First(n=>n!["Owned"]!.GetValue<bool>());string changedPath=changed!["Path"]!.GetValue<string>();byte[] newBytes=Encoding.UTF8.GetBytes("neutral incomplete upgrade bytes; never executed");changed["Hash"]=Hash(newBytes);Write(Path.Combine(root,changedPath),newBytes);
  const string added="BepInEx/plugins/FusionUnityEmbedded/interrupted.keep";Write(Path.Combine(root,added),newBytes);after["Files"]!.AsArray().Add(JsonSerializer.SerializeToNode(new{Path=added,Hash=Hash(newBytes),Owned=true}));
  File.WriteAllText(ledger,after.ToJsonString());File.WriteAllText(pending,new JsonObject{["Before"]=before.DeepClone(),["After"]=after.DeepClone(),["Backup"]=backup}.ToJsonString());
  Environment.SetEnvironmentVariable(ProviderEnvironment,missingRoot);Reject(()=>candidate.Call("UnityEmbeddedAdapter","Install",exe),"interrupted upgrade recovers before reporting missing new ZIP");
  Check(!File.Exists(pending)&&!File.Exists(Path.Combine(root,added))&&(bool)candidate.Call("UnityEmbeddedAdapter","IsInstalled",exe)!,"unfinished upgrade restores owned prior files and valid ledger without payload source");
  Check(JsonNode.DeepEquals(before,JsonNode.Parse(File.ReadAllText(ledger))),"unfinished upgrade recovery restores exact prior ledger semantics");
  candidate.Call("UnityEmbeddedAdapter","Restore",exe);Check(!File.Exists(ledger)&&File.Exists(Path.Combine(root,"player-save.keep")),"recovered installation can be restored offline with sentinel intact");Environment.SetEnvironmentVariable(ProviderEnvironment,payloadRoot);
 }
 facts.Add(new{baselineDll,baselineSha256=FileHash(baselineDll),candidateDll,candidateSha256=FileHash(candidateDll),catalogSha256=FileHash(catalogPath),scratch});
 boundaries.Add("All EXE/DLL game fixtures are inert synthetic file structures. No game, hook, loader, extraction worker, translation API, or GUI was executed. The harness itself is a neutral .NET console process.");
}
catch(Exception ex){failure=ex.ToString();Console.Error.WriteLine(failure);Environment.ExitCode=1;}
finally
{
 Environment.SetEnvironmentVariable(ProviderEnvironment,previousRoot);
 File.WriteAllText(Path.Combine(reportDir,"results.json"),JsonSerializer.Serialize(new{success=failure is null,checks=checks.Count,passed=checks,failure,facts,boundaries},jsonOptions));
 Console.WriteLine("REPORT "+Path.Combine(reportDir,"results.json"));
}

sealed record Package(string Id,string Engine,string Backend,string Architecture,string Version,string FileName,long Bytes,string Sha256);
sealed class Surface
{
 internal const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
 internal Assembly Assembly{get;}internal string DataRoot{get;}internal string Name{get;}
 internal Surface(string path,string data)
 {DataRoot=data;Name=Path.GetFileName(data).StartsWith("baseline",StringComparison.Ordinal)?"baseline":"external";var context=new IsolatedContext(path);Assembly=context.LoadFromAssemblyPath(path);Call("AppDataPaths","Initialize",new object[]{new[]{"--data-root="+data,"--disable-global-input"}});}
 internal Type Type(string name)=>Assembly.GetType("ScreenshotTranslationUiTester."+name,true)!;
 internal object New(string name)=>Activator.CreateInstance(Type(name),true)!;
 internal object? Call(string type,string method,params object?[] args)=>Invoke(Type(type),null,method,args);
 internal object? CallInstance(object instance,string method,params object?[] args)=>Invoke(instance.GetType(),instance,method,args);
 private static object? Invoke(Type type,object? instance,string name,object?[] args)
 {var m=type.GetMethods(Flags).Single(x=>x.Name==name&&x.GetParameters().Length==args.Length);try{return m.Invoke(instance,args);}catch(TargetInvocationException e)when(e.InnerException is not null){ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;}}
 internal object Game(string exe,string adapter)=>Activator.CreateInstance(Type("GameInfo"),exe,"Neutral fixture","Unity Mono","64 位","neutral offline fixture",Enum.Parse(Type("SupportLevel"),"Supported"),Path.Combine(Path.GetDirectoryName(exe)!,"OfflineFixture_Data"),adapter)!;
 internal object Settings(){var s=New("ApiSettings");Type("ApiSettings").GetProperty("ApiUrl")!.SetValue(s,"https://offline.invalid/no-request");Type("ApiSettings").GetProperty("ApiKey")!.SetValue(s,"neutral-fixture-not-a-secret");Type("ApiSettings").GetProperty("Model")!.SetValue(s,"offline-neutral-model");return s;}
}
sealed class IsolatedContext(string path):AssemblyLoadContext(isCollectible:false)
{
 private readonly AssemblyDependencyResolver resolver=new(path);
 protected override Assembly? Load(AssemblyName name){string? file=resolver.ResolveAssemblyToPath(name);return file is null?null:LoadFromAssemblyPath(file);}
 protected override nint LoadUnmanagedDll(string name){string? file=resolver.ResolveUnmanagedDllToPath(name);return file is null?0:LoadUnmanagedDllFromPath(file);}
}
