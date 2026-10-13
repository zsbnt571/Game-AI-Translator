using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Translation-only storage. Installation journals, connection credentials and
// save data never follow the content identity or leave the application data root.
internal static class TranslationMemory
{
    private static readonly ConcurrentDictionary<string,(string Stamp,string? Identity)> identities=new(StringComparer.OrdinalIgnoreCase);
    internal static string? RootOverride {get;set;}
    private static string? userStore;
    internal static void EnableUserStore()=>userStore=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FusionR1","translation-memory-v1");
    internal static string Root=>RootOverride??userStore??Path.Combine(AppDataPaths.Root,"translation-memory");
    private static string Digest(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    internal static string? Identity(string exe)
    {
        try
        {
            exe=Path.GetFullPath(exe);if(!File.Exists(exe))return null;string folder=Path.GetDirectoryName(exe)!;
            List<string> files=[];string kind="";
            string data=Path.Combine(folder,Path.GetFileNameWithoutExtension(exe)+"_Data");
            string globals=Path.Combine(data,"globalgamemanagers"),mono=Path.Combine(data,"Managed","Assembly-CSharp.dll"),metadata=Path.Combine(data,"il2cpp_data","Metadata","global-metadata.dat");
            if(!File.Exists(globals))globals=Path.Combine(data,"data.unity3d");
            if(!File.Exists(mono))mono=Path.Combine(data,"Managed","Game.dll");
            if(File.Exists(globals)&&(File.Exists(mono)||File.Exists(metadata)))
            {kind="unity";files.Add(globals);files.Add(File.Exists(mono)?mono:metadata);}
            else if(Directory.Exists(Path.Combine(folder,"game"))&&File.Exists(Path.Combine(folder,"renpy","__init__.py")))
            {
                kind="renpy-v1";string game=Path.Combine(folder,"game");bool limited=false;
                foreach(string file in UnityEmbeddedCatalog.SafeFiles(game,CancellationToken.None,()=>limited=true,20000))
                {
                    string relative=Path.GetRelativePath(game,file).Replace('\\','/');
                    if(relative.Split('/').Any(p=>p is "saves" or "tl")||Path.GetFileName(file).StartsWith("zz_fusion",StringComparison.OrdinalIgnoreCase))continue;
                    if(new[]{".rpy",".rpyc",".rpa"}.Contains(Path.GetExtension(file),StringComparer.OrdinalIgnoreCase))files.Add(file);
                    if(files.Count>4000)return null;
                }
                if(limited||files.Count==0)return null;
                files.Sort(StringComparer.OrdinalIgnoreCase);
            }
            else if(GodotGameAdapter.Detect(exe) is {} godot){kind="godot";files.Add(godot.PackPath);}
            else
            {
                string rpg=Directory.Exists(Path.Combine(folder,"www","data"))?Path.Combine(folder,"www","data"):Path.Combine(folder,"data");
                if(File.Exists(Path.Combine(rpg,"System.json"))&&File.Exists(Path.Combine(rpg,"CommonEvents.json")))
                {kind="rpg";files.Add(Path.Combine(rpg,"System.json"));files.Add(Path.Combine(rpg,"CommonEvents.json"));}
                else return null; // Never infer identity from a game title or a generic engine EXE.
            }
            long total=0;var stamp=new StringBuilder();
            foreach(string file in files)
            {
                AppDataPaths.ValidateExplicitRoot(Path.GetDirectoryName(file)!);
                var info=new FileInfo(file);if((info.Attributes&FileAttributes.ReparsePoint)!=0)return null;
                total+=info.Length;if(info.Length==0||total>512L*1024*1024)return null;
                stamp.Append(file).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
            }
            string version=stamp.ToString();
            if(identities.TryGetValue(exe,out var prior)&&prior.Stamp==version)return prior.Identity;
            var signature=new StringBuilder(kind);
            foreach(string file in files)
            {
                using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read);
                if(kind=="renpy-v1")signature.Append('|').Append(Path.GetRelativePath(Path.Combine(folder,"game"),file).Replace('\\','/').ToLowerInvariant());
                signature.Append('|').Append(stream.Length).Append(':').Append(Convert.ToHexString(SHA256.HashData(stream)));
            }
            string id=Digest(signature.ToString());identities[exe]=(version,id);return id;
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException){return null;}
    }
    private static string? FileFor(string exe,ApiSettings settings,TranslationCacheScope scope,bool display)
    {
        string identity=Identity(exe)??("path-"+Digest(Path.GetFullPath(exe).ToUpperInvariant()));
        string root=AppDataPaths.ValidateExplicitRoot(Path.GetFullPath(Root));
        string profile=RpgTranslationCache.Id(settings,RpgTranslationCache.Endpoint(settings.ApiUrl));
        // Preserve the source-language selection and screen/native/display boundaries.
        string folder=AppDataPaths.ValidateExplicitRoot(Path.Combine(root,identity,Digest(profile+"|"+settings.SourceLanguage)));
        string path=Path.Combine(folder,scope+"-"+(display?"display":"native")+".json");
        if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("译文存储包含链接。");
        return path;
    }
    internal static RpgTranslationCache.Loaded Reuse(string exe,ApiSettings settings,TranslationCacheScope scope,bool display,RpgTranslationCache.Loaded local)
    {
        if(TranslationCacheReset.Read(exe) is not null)return local;
        try
        {
            string? shared=FileFor(exe,settings,scope,display);
            var candidates=new List<string>();if(shared is not null)candidates.Add(shared);
            // A portable package can carry an offline-validated seed. Normal
            // installs publish subsequent updates to the stable user store.
            if(shared is not null&&!PortableDataStorage.Same(Root,Path.Combine(AppDataPaths.Root,"translation-memory")))
                candidates.Add(Path.Combine(AppDataPaths.Root,"translation-memory",Path.GetRelativePath(Root,shared)));
            // Same-path route changes have stronger identity than a title match.
            // Shared translations still need a live writable binding before use.
            string basename=Path.GetFileName(local.Path).Replace(".recovered.json","");
            string storage=RpgMakerDataAdapter.Storage(exe);
            foreach(string route in new[]{storage,Path.Combine(storage,"generic-test")})
                candidates.Add(Path.Combine(route,"translations",scope==TranslationCacheScope.Screen?"screen":"",basename));
            Dictionary<string,string>? merged=null;int imported=0,conflicts=0;
            long bytes=local.Entries.Sum(p=>Cost(p.Key,p.Value));
            void Add(KeyValuePair<string,string> pair)
            {
                if(!TranslationWorkspaceStore.Basic(pair.Key,pair.Value))return;
                var current=merged??local.Entries;
                if(current.TryGetValue(pair.Key,out var old)){if(old!=pair.Value)conflicts++;return;}
                long cost=Cost(pair.Key,pair.Value);if(current.Count>=100000||bytes+cost>60L*1024*1024)return;
                merged??=new(local.Entries,StringComparer.Ordinal);merged.Add(pair.Key,pair.Value);bytes+=cost;imported++;
            }
            foreach(string path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if(PortableDataStorage.Same(path,local.Path))continue;
                var entries=RpgTranslationCache.Read(path+".recovered.json")??RpgTranslationCache.Read(path)??RpgTranslationCache.Read(path+".previous");
                if(entries is null)continue;
                foreach(var pair in entries)Add(pair);
            }
            if(display&&scope==TranslationCacheScope.Native)
                foreach(var pair in LegacyTranslationMemory.Read(exe,settings))
                {
                    // Older plugin stores do not identify the source selection.
                    // A partially translated mixed row is not complete evidence
                    // for a different combination. Keep its original store intact.
                    var languages=TranslationLanguagePolicy.Languages(pair.Key);
                    if(languages.Count==1&&!languages.Any(l=>l is "latin" or "han" or "und")&&
                        TranslationLanguagePolicy.ShouldTranslate(pair.Key,settings)&&TranslationLanguageSpans.Preserved(pair.Key,pair.Value,settings))Add(pair);
                }
            if(imported>0)AppLog.Write("translation-memory",$"reused={imported} localConflictsKept={conflicts} scope={scope} display={display}");
            return merged is null?local:new(local.Path,merged,true);
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException)
        {AppLog.Write("translation-memory","Reuse unavailable; local cache remains active.",ex);return local;}
    }
    private static long Cost(string source,string value)=>12L+Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(source))+Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(value));
    internal static void Publish(string exe,ApiSettings settings,TranslationCacheScope scope,bool display,IReadOnlyDictionary<string,string> entries)
    {
        if(entries.Count==0)return;
        if(TranslationCacheReset.Read(exe) is not null)return;
        try
        {
            string? path=FileFor(exe,settings,scope,display);if(path is null)return;
            using var mutex=new Mutex(false,"Local\\FusionR1.TranslationMemory."+Digest(path));
            bool held=false;
            try
            {
                try{held=mutex.WaitOne(1000);}catch(AbandonedMutexException){held=true;}
                if(!held)return;
                var existing=RpgTranslationCache.Read(path+".recovered.json")??RpgTranslationCache.Read(path);
                var previous=existing is null?RpgTranslationCache.Read(path+".previous"):null;
                if(File.Exists(path+".recovered.json")||existing is null&&File.Exists(path))path+=".recovered.json";
                var merged=existing??previous??new(StringComparer.Ordinal);bool changed=false;
                long bytes=merged.Sum(p=>Cost(p.Key,p.Value));
                foreach(var pair in entries)
                {
                    if(!TranslationWorkspaceStore.Basic(pair.Key,pair.Value)||merged.Count>=100000&&!merged.ContainsKey(pair.Key))continue;
                    if(merged.GetValueOrDefault(pair.Key)==pair.Value)continue;
                    long cost=Cost(pair.Key,pair.Value)-(merged.TryGetValue(pair.Key,out var old)?Cost(pair.Key,old):0);
                    if(bytes+cost>60L*1024*1024)continue;
                    merged[pair.Key]=pair.Value;bytes+=cost;changed=true;
                }
                if(changed)
                {
                    if(JsonSerializer.SerializeToUtf8Bytes(merged,new JsonSerializerOptions{WriteIndented=true}).Length>64*1024*1024)
                        throw new IOException("共用译文副本达到大小上限，已保留当前游戏缓存。");
                    PortableDataStorage.WriteJson(path,merged);
                }
            }
            finally{if(held)mutex.ReleaseMutex();}
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException)
        {AppLog.Write("translation-memory","Shared copy unavailable; local cache was retained.",ex);}
    }
}
