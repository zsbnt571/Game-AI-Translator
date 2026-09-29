using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameAiTranslator.Runtime;

// Isolated, read-only worker. No package writes, key discovery, game loading or
// network initialization. The desktop bounds its lifetime and validates output.
if(args.Length!=3)return 2;
var rows=new Dictionary<string,CatalogText>(StringComparer.Ordinal);
var conflicts=new HashSet<string>(StringComparer.Ordinal);
var errors=new List<string>();int skipped=0,scanned=0;long total=0;
try {
 string root=Path.GetFullPath(args[0]),output=Path.GetFullPath(args[2]);
 if(!Directory.Exists(root)||!Enum.TryParse<EGame>(args[1],out var game))return 2;
 if((File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked package directory");
 foreach(var f in Directory.EnumerateFileSystemEntries(root))
  if((File.GetAttributes(f)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked package entry");
 using var oodle=LocalRuntimeDependency.OpenOodle(AppContext.BaseDirectory);
 OodleHelper.Initialize(new OodleDotNet.Oodle(oodle.Name));
 using var provider=new DefaultFileProvider(root,SearchOption.TopDirectoryOnly,new VersionContainer(game),StringComparer.OrdinalIgnoreCase);
 provider.Initialize();provider.Mount();
 var files=provider.Files.Values.Where(f=>!f.Path.StartsWith("Engine/",StringComparison.OrdinalIgnoreCase)).OrderBy(f=>f.Path,StringComparer.Ordinal).ToArray();
 var resources=new List<(string Path,string Culture,Dictionary<string,(string Text,uint Hash)> Entries)>();
 var nativeCultures=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
 foreach(var file in files.Where(f=>f.Path.EndsWith(".locmeta",StringComparison.OrdinalIgnoreCase)||f.Path.EndsWith(".locres",StringComparison.OrdinalIgnoreCase))) {
  if(file.Size>8*1024*1024){skipped++;continue;}
  try {
   var data=file.Read();
   if(file.Path.EndsWith(".locmeta",StringComparison.OrdinalIgnoreCase)) {
    if(data.Length<21||!data.AsSpan(0,16).SequenceEqual(Convert.FromHexString("4FEE4CA1684855836C4C46BD70DA507C")))continue;
    int pos=17;string? culture=UnrealTextReader.ReadString(data,ref pos,64);
    if(culture is not null&&Regex.IsMatch(culture,"^[A-Za-z0-9-]{2,32}$"))nativeCultures[file.Path[..file.Path.LastIndexOf('/')]]=culture;
   } else resources.Add((file.Path,file.Path.Split('/')[^2],UnrealTextReader.ReadLocres(data)));
  } catch(Exception ex){skipped++;if(errors.Count<20)errors.Add(Path.GetFileName(file.Path)+": "+ex.GetType().Name);}
 }
 foreach(var file in files.Where(f=>f.Path.EndsWith(".uasset",StringComparison.OrdinalIgnoreCase)||f.Path.EndsWith(".uexp",StringComparison.OrdinalIgnoreCase))) {
  if(file.Size>16*1024*1024){skipped++;continue;}
  if(total+file.Size>1024L*1024*1024||scanned>=20000||rows.Count>=100000){skipped++;break;}
  try {var data=file.Read();total+=data.Length;scanned++;
   foreach(var row in UnrealTextReader.Scan(data))Add(row);
  } catch(Exception ex){skipped++;if(errors.Count<20)errors.Add(Path.GetFileName(file.Path)+": "+ex.GetType().Name);}
 }
 foreach(var group in resources.GroupBy(r=>r.Path[..r.Path.LastIndexOf('/',r.Path.LastIndexOf('/')-1)],StringComparer.OrdinalIgnoreCase)) {
  if(!nativeCultures.TryGetValue(group.Key,out var culture))continue; // Never assume English is the native source.
  var native=group.FirstOrDefault(r=>r.Culture.Equals(culture,StringComparison.OrdinalIgnoreCase));
  if(native.Entries is null)continue;
  foreach(var pair in native.Entries) {
   string[] id=pair.Key.Split('\0');if(id.Length!=2)continue;
   var row=new CatalogText(id[0],id[1],pair.Value.Text,culture,new());
   foreach(var localized in group)
    if(localized.Entries.TryGetValue(pair.Key,out var text)&&text.Hash==pair.Value.Hash)row.localized[localized.Culture]=text.Text;
   Add(row);
  }
 }
 var accepted=rows.Values.Where(r=>!conflicts.Contains(r.ns+"\0"+r.key)&&UnrealTextReader.Accept(r.source)).ToArray();
 skipped+=conflicts.Count;
 string staticReason=accepted.Length>0?"":files.Length==0?"未读取到可枚举的游戏资源，预提取不可用":"未提取到带可替换身份的文本";
 var bytes=JsonSerializer.SerializeToUtf8Bytes(new {protocol=76,entries=accepted,scanned,skipped,errors,mountedFiles=files.Length,runtimeOnly=accepted.Length==0,staticReason});
 if(bytes.Length>32*1024*1024)throw new IOException("Catalog exceeds limit");
 File.WriteAllBytes(output,bytes);Console.WriteLine($"catalog={accepted.Length} scanned={scanned} skipped={skipped}");return 0;
} catch(Exception ex){Console.Error.WriteLine(ex.GetType().Name+": "+ex.Message);return 1;}

void Add(CatalogText row) {
 CatalogMerge.Add(rows,conflicts,row);
}
