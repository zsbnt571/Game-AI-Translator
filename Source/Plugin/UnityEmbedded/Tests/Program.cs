using System.Reflection;
using System.Text.Json;
using Fusion.UnityEmbedded;
using ScreenshotTranslationUiTester;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

if(args.Length==3&&args[0]=="--type-methods")
{
    using var assembly=Mono.Cecil.AssemblyDefinition.ReadAssembly(args[1]);
    foreach(var type in assembly.MainModule.Types.Where(t=>t.FullName==args[2]))foreach(var method in type.Methods.Where(m=>m.Name.Contains("FontAsset")||m.Name.Contains("HasCharacter")))
    {
        Console.WriteLine(method.FullName+" "+string.Join(", ",method.Parameters.Select(p=>p.Name+"="+(p.HasConstant?p.Constant:"required"))));
        if(method.Name=="CreateFontAsset"&&method.HasBody)foreach(var instruction in method.Body.Instructions)Console.WriteLine(instruction);
    }
    return;
}

if(args.Length==2&&args[0]=="--metadata-save-literals")
{
    using var input=File.OpenRead(args[1]);using var reader=new BinaryReader(input);if(reader.ReadUInt32()!=0xfab11baf)throw new Exception("Not IL2CPP metadata.");int version=reader.ReadInt32(),table=reader.ReadInt32(),tableSize=reader.ReadInt32(),stringOffset=reader.ReadInt32(),stringSize=reader.ReadInt32();
    if(table<24||tableSize<0||table+tableSize>input.Length||stringOffset<24||stringSize<0||stringOffset+stringSize>input.Length||tableSize%8!=0)throw new Exception("Unsupported IL2CPP metadata layout.");
    var found=new HashSet<string>();for(int i=0;i<tableSize/8;i++){input.Position=table+i*8;int length=reader.ReadInt32(),offset=reader.ReadInt32();if(length<1||length>1200||offset<0||offset+length>stringSize)continue;input.Position=stringOffset+offset;string value=System.Text.Encoding.UTF8.GetString(reader.ReadBytes(length));if(System.Text.RegularExpressions.Regex.IsMatch(value,@"save|\.dat$|\.json$|\.sav$|appdata|documents|playerprefs|[A-Z]:\\|MonsterBox|IN HEAT",System.Text.RegularExpressions.RegexOptions.IgnoreCase))found.Add(value);}
    Console.WriteLine(JsonSerializer.Serialize(new{Version=version,LiteralCount=tableSize/8,SaveRelated=found}));return;
}

if(args.Length==3&&args[0]=="--isolate-copy")
{
    string root=IsolatedGamePath.Require(args[1]);
    if(!System.Text.RegularExpressions.Regex.IsMatch(args[2],@"^FusionTest75_[a-f0-9]{8,32}$"))throw new Exception("Only an isolated game copy and unique test identity are allowed.");
    if((File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0)throw new Exception("Test copy must not be a directory link.");
    string data=Directory.GetDirectories(root,"*_Data").Single(),path=Path.Combine(data,"globalgamemanagers"),temporary=path+".fusion-test-new",backup=path+".fusion-test-original";
    if(File.Exists(backup)||File.Exists(temporary))throw new Exception("Test copy was already prepared.");
    var manager=new AssetsManager();manager.LoadClassPackage(Path.Combine(AppContext.BaseDirectory,"adapters","unity","classdata.tpk"));var file=manager.LoadAssetsFile(path,false);manager.LoadClassDatabaseFromPackage(file.file.Metadata.UnityVersion);
    var settings=file.file.GetAssetsOfType(129).Single();var fields=manager.GetBaseField(file,settings);
    string originalCompany=fields["companyName"].AsString,originalProduct=fields["productName"].AsString;
    fields["companyName"].AsString=args[2];fields["productName"].AsString=args[2];settings.SetNewData(fields);
    using(var writer=new AssetsFileWriter(temporary))file.file.Write(writer);manager.UnloadAll();
    File.Copy(path,backup,false);File.Move(temporary,path,true);File.WriteAllText(Path.Combine(data,"app.info"),args[2]+"\n"+args[2]+"\n",new System.Text.UTF8Encoding(false));
    File.WriteAllText(Path.Combine(root,".fusion-test-isolation.json"),JsonSerializer.Serialize(new{Identity=args[2],OriginalCompany=originalCompany,OriginalProduct=originalProduct,PreparedUtc=DateTime.UtcNow}));
    Console.WriteLine("Prepared independent Unity save identity "+args[2]);return;
}

if(args.Length==2&&args[0]=="--save-audit")
{
    using var assembly=Mono.Cecil.AssemblyDefinition.ReadAssembly(args[1]);
    IEnumerable<Mono.Cecil.TypeDefinition> Types(IEnumerable<Mono.Cecil.TypeDefinition> types){foreach(var type in types){yield return type;foreach(var child in Types(type.NestedTypes))yield return child;}}
    foreach(var type in Types(assembly.MainModule.Types))foreach(var method in type.Methods.Where(m=>m.HasBody))
    {
        var selected=method.Body.Instructions.Where(i=>i.Operand is Mono.Cecil.MethodReference reference&&(reference.DeclaringType.FullName.Contains("PlayerPrefs")||reference.FullName.Contains("persistentDataPath")||reference.DeclaringType.FullName.StartsWith("System.IO.")||reference.FullName.Contains("GetFolderPath")||reference.DeclaringType.FullName.Contains("Registry"))).ToArray();
        if(selected.Length>0)Console.WriteLine(JsonSerializer.Serialize(new{Method=method.FullName,Calls=selected.Select(i=>i.Operand.ToString()),Strings=method.Body.Instructions.Where(i=>i.OpCode.Code==Mono.Cecil.Cil.Code.Ldstr).Select(i=>i.Operand)}));
    }return;
}
if(args.Length==2&&args[0]=="--settings")
{
    var manager=new AssetsManager();manager.LoadClassPackage(Path.Combine(AppContext.BaseDirectory,"adapters","unity","classdata.tpk"));var file=manager.LoadAssetsFile(args[1],false);manager.LoadClassDatabaseFromPackage(file.file.Metadata.UnityVersion);
    foreach(var info in file.file.GetAssetsOfType(129)){var value=manager.GetBaseField(file,info);Console.WriteLine(JsonSerializer.Serialize(value.Children.Where(f=>f.TemplateField.ValueType==AssetValueType.String).ToDictionary(f=>f.TemplateField.Name,f=>f.AsString)));}manager.UnloadAll();return;
}

if(args.Length==3&&args[0]=="--catalog-export"){var catalog=UnityEmbeddedCatalog.Read(args[1],CancellationToken.None);File.WriteAllText(args[2],JsonSerializer.Serialize(catalog.Texts));return;}
if(args.Length==2&&args[0]=="--catalog")
{
    var timer=System.Diagnostics.Stopwatch.StartNew();var catalog=UnityEmbeddedCatalog.Read(args[1],CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(new{File=Path.GetFileName(args[1]),Texts=catalog.Texts.Length,Files=catalog.Files,Unreadable=catalog.Unreadable,Elapsed=timer.Elapsed.TotalSeconds,Examples=catalog.Texts.Take(8)}));return;
}
int checks=0;
void Check(bool condition,string name){if(!condition)throw new Exception("FAILED: "+name);checks++;Console.WriteLine("PASS "+name);}
var modelSource=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"contracts","Models.cs.txt"));
var languageContract=System.Text.RegularExpressions.Regex.Match(modelSource,@"public enum SourceLanguageMode\s*\{([^}]+)\}");
Check(languageContract.Success && languageContract.Groups[1].Value.Split(',').Select(x=>x.Trim()).SequenceEqual(Enum.GetNames<SourceLanguageMode>()),"offline source-language enum matches production declaration and ordinal order");
await TransportTests.Run(Check);
IsolationTests.Run(Check);
var runtime=new UnityBridgeRuntime(_=>{});
object Call(string name,params object[] values)=>typeof(UnityBridgeRuntime).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(runtime,values);
Dictionary<string,object> Request(string json)=>(Dictionary<string,object>)Call("Handle",(Dictionary<string,object>)BridgeJson.Parse(json));
var text=new UnityEngine.UI.Text{text="Hello world"};
Check((string)Call("Capture",text,"Hello world")=="Hello world","disabled passes through source");
int epoch=Convert.ToInt32(Request("{\"op\":\"translationPrepare\"}")["epoch"]);
Request("{\"op\":\"translationApply\",\"prime\":true,\"epoch\":"+epoch+",\"entries\":[{\"source\":\"Hello world\",\"text\":\"你好世界\"}]}");
Request("{\"op\":\"translationEnable\"}");
// Production writeback is serviced by LateUpdate, not by the request handler.
// Drive that lifecycle in this fixture; preserve the original display assertion.
Check(text.text=="Hello world","enable leaves original visible until frame writeback");
for(int frame=0;frame<10&&text.text!="你好世界";frame++)runtime.LateTick();
Check(text.text=="你好世界","enable restores existing label from cache");
var originalFont=text.font;
Check((string)Call("Capture",new UnityEngine.UI.Text(),"Hello world")=="你好世界","cached string substituted before setter");
Request("{\"op\":\"translationPrepare\"}");
Check(text.text=="你好世界","prepare does not flash untranslated text");
Request("{\"op\":\"translationDisable\"}");Check(text.text=="Hello world","disable restores original");
bool rejected=false;try{Request("{\"op\":\"translationApply\",\"epoch\":"+epoch+",\"entries\":[]}");}catch(TargetInvocationException){rejected=true;}
Check(rejected,"late translation cannot write after disable");
Request("{\"op\":\"translationEnable\"}");text.text="Changed by game";Request("{\"op\":\"translationDisable\"}");Check(text.text=="Changed by game","disable preserves a newer game value");
Request("{\"op\":\"translationEnable\"}");var missed=new UnityEngine.UI.Text();Call("Capture",missed,"New visible sentence");
var poll=Request("{\"op\":\"translationPoll\"}");Check(((IEnumerable<string>)poll["texts"]).Contains("New visible sentence"),"untranslated visible text is queued for realtime fallback");
var strings=new List<string>();UnityEmbeddedCatalog.ReadTextResource("dialogues.json","{\"id\":\"npc_001\",\"text\":\"A pre-extracted sentence\",\"path\":\"images/a.png\"}",s=>{if(UnityEmbeddedCatalog.IsDisplayText(s))strings.Add(s);},CancellationToken.None);
Check(strings.SequenceEqual(new[]{"A pre-extracted sentence"}),"structured resource skips identifier and file path");
strings.Clear();UnityEmbeddedCatalog.ReadTextResource("strings.csv","id,English,Japanese\nmenu_play,Play game,ゲーム\nquote,\"First line\nSecond line\",test",strings.Add,CancellationToken.None);
Check(strings.SequenceEqual(new[]{"Play game","First line\nSecond line"}),"CSV extraction preserves quoted newline and source column");
string escaped="quote \" slash \\ newline\n中文";Check((string)BridgeJson.Parse(BridgeJson.Write(escaped))==escaped,"JSON wire unicode and escapes roundtrip");
var lookup=new TextLookup();var lookupCache=new Dictionary<string,string>{{"Welcome {player} to the mountain observatory.","欢迎 {player} 来到高山天文台。"},{"A repeated {name} and {name} in dialogue.","对白中重复的 {name} 和 {name}。"}};
lookup.Rebuild(lookupCache);
Check(lookup.TryGet(lookupCache,"<color=red>Welcome Robin to the mountain observatory.</color>",out var wrapped)&&wrapped=="<color=red>欢迎 Robin 来到高山天文台。</color>","neutral generic Unity markup and dynamic name hit ahead cache");
Check(!lookup.TryGet(lookupCache,"A repeated Robin and Alex in dialogue.",out _),"different values for repeated placeholder stay untranslated");
Check(!lookup.TryGet(lookupCache,"Welcome {unknown} to the mountain observatory.",out _),"unknown literal template is not guessed as a player name");
lookupCache["Welcome {visitor} to the mountain observatory."]="另一句 {visitor} 的译文。";lookup.Rebuild(lookupCache);
Check(!lookup.TryGet(lookupCache,"Welcome Robin to the mountain observatory.",out _),"conflicting generic templates never silently pick one translation");
runtime.Dispose();Console.WriteLine("Completed "+checks+" offline contract checks. Real Unity hooking and frame rendering are not covered by this fixture.");

