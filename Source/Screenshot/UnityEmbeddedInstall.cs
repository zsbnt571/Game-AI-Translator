using System.IO.Compression;
using System.Security.Cryptography;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static partial class UnityEmbeddedAdapter
{
    internal static void CheckCompatibility(UnityEmbeddedGame game)
    {
        if(game.Backend!="IL2CPP")return;
        if(UnityNativeRuntime.Required(game)){UnityNativeApiMap.Create(game);return;}
        if(game.Architecture!="x64")throw new EmbeddedCompatibilityException("此 IL2CPP 游戏的架构尚不支持内嵌翻译，可普通启动。");
        string metadata=Path.Combine(game.DataRoot,"il2cpp_data","Metadata","global-metadata.dat");
        if(!File.Exists(metadata))throw new EmbeddedCompatibilityException("未找到标准 IL2CPP 文本组件元数据，本次不安装翻译组件。可普通启动。");
        using var input=File.OpenRead(metadata);using var reader=new BinaryReader(input);
        if(input.Length<64)throw new EmbeddedCompatibilityException("IL2CPP 元数据头不完整，当前组件未安装；可普通启动。");
        if(reader.ReadUInt32()!=0xFAB11BAF)
            throw new EmbeddedCompatibilityException("未识别标准 IL2CPP 元数据头，不能按普通版本差异接入；已保留原安装，可普通启动。反复重启不会解决此兼容问题。");
        int version=reader.ReadInt32();
        // BepInEx 788 pins Cpp2IL development.1452, whose known metadata range is 23-106.
        if(version is <23 or >106)throw new EmbeddedCompatibilityException($"此游戏的 IL2CPP 元数据版本 {version} 不在当前组件的支持范围（23–106）。可普通启动。");
    }

    private sealed record UpdateRecord(InstallLedger? Before,InstallLedger After,string Backup,string[]? MissingBefore=null);
    private static string TransactionPath(UnityEmbeddedGame game)=>Path.Combine(game.Root,".fusion-unity-embedded-update.json");
    private static void WriteJsonAtomic<T>(string path,T value)
    {
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(stream,value,new JsonSerializerOptions{WriteIndented=true});stream.Flush(true);}File.Move(temporary,path,true);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    private static void RecoverUpdate(UnityEmbeddedGame game)
    {
        string pending=TransactionPath(game);if(!File.Exists(pending))return;
        if(new FileInfo(pending).Length>2*1024*1024)throw new IOException("组件更新记录异常，未改动游戏文件。");
        var record=JsonSerializer.Deserialize<UpdateRecord>(File.ReadAllText(pending))??throw new IOException("组件更新记录不可读。");
        if(!string.Equals(record.After.ExePath,game.ExePath,StringComparison.OrdinalIgnoreCase)||record.After.Files.Length>1024||record.Before?.Files.Length>1024)
            throw new IOException("组件更新记录与游戏不一致。");
        var before=record.Before?.Files.ToDictionary(x=>x.Path,StringComparer.OrdinalIgnoreCase)??new();
        var after=record.After.Files.ToDictionary(x=>x.Path,StringComparer.OrdinalIgnoreCase);
        var missing=new HashSet<string>(record.MissingBefore??Array.Empty<string>(),StringComparer.OrdinalIgnoreCase);
        if(missing.Any(p=>!before.TryGetValue(p,out var file)||!file.Owned))throw new IOException("组件缺失记录与原安装不一致。");
        foreach(var item in before.Values.Concat(after.Values).GroupBy(x=>x.Path,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()))
        {
            string target=SafePath(game.Root,item.Path);
            if(File.Exists(target))
            {
                string hash=Hash(target);
                if(!(before.TryGetValue(item.Path,out var old)&&hash==old.Hash)&&!(after.TryGetValue(item.Path,out var next)&&hash==next.Hash))
                    throw new IOException("组件更新中断后文件又有改变，请保留备份并检查："+item.Path);
            }
            if(before.TryGetValue(item.Path,out var previous)&&previous.Owned&&!missing.Contains(item.Path))
            {
                string backup=SafePath(game.Root,record.Backup+"/"+item.Path);
                if(!File.Exists(backup)||Hash(backup)!=previous.Hash)throw new IOException("组件更新备份校验失败："+item.Path);
            }
        }
        foreach(var next in after.Values.Where(x=>x.Owned&&(!before.ContainsKey(x.Path)||missing.Contains(x.Path))))
        {string path=SafePath(game.Root,next.Path);if(File.Exists(path))File.Delete(path);}
        foreach(var old in before.Values.Where(x=>x.Owned&&!missing.Contains(x.Path)))
        {string path=SafePath(game.Root,old.Path);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.Copy(SafePath(game.Root,record.Backup+"/"+old.Path),path,true);}
        string ledger=Path.Combine(game.Root,LedgerName);
        if(record.Before is not null)WriteJsonAtomic(ledger,record.Before);else if(File.Exists(ledger))File.Delete(ledger);
        File.Delete(pending);
    }

    private static void InstallPayload(UnityEmbeddedGame game)
    {
        if(UnityNativeRuntime.Required(game)){InstallNativeRuntime(game);return;}
        var previous=ReadLedger(game);
        var old=previous?.Files.ToDictionary(x=>x.Path,StringComparer.OrdinalIgnoreCase)??new();
        var missing=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Deleted Fusion-owned files are repairable from our payload. A changed
        // file or a missing borrowed dependency still requires explicit review.
        foreach(var file in old.Values)
        {
            string path=SafePath(game.Root,file.Path);
            if(!File.Exists(path))
            {
                if(!file.Owned)throw new IOException("现有加载器文件缺失，未覆盖原安装："+file.Path);
                missing.Add(file.Path);
            }
            else if(Hash(path)!=file.Hash)throw new IOException("Unity 翻译组件文件已被改动，未覆盖："+file.Path);
        }
        var reuse=InspectReusableLoader(game,previous);
        using var payload=UnityEmbeddedPayload.Open(reuse?.PayloadBackend??game.Backend,game.Architecture);using var zip=new ZipArchive(payload,ZipArchiveMode.Read);
        var plan=new List<(ZipArchiveEntry? Entry,InstalledFile File)>();var unique=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in zip.Entries.Where(e=>!e.FullName.EndsWith('/')))
        {
            if(!unique.Add(entry.FullName)||entry.Length>96L*1024*1024)throw new IOException("Unity 组件清单无效。");
            string target=SafePath(game.Root,entry.FullName);
            if(reuse is not null&&!entry.FullName.StartsWith(FusionPluginPrefix,StringComparison.Ordinal))continue;
            using var input=entry.Open();string hash=Convert.ToHexString(SHA256.HashData(input));
            bool own=old.TryGetValue(entry.FullName,out var prior)&&prior.Owned;
            if(File.Exists(target)&&Hash(target)!=hash&&!own)throw new EmbeddedCompatibilityException("现有文件与翻译组件冲突："+entry.FullName+"。已保留现有安装；请使用独立干净副本接入翻译。");
            plan.Add((entry,new(entry.FullName,hash,own||!File.Exists(target))));
        }
        if(reuse is not null)
        {
            ValidateReusablePlugin(game,reuse,zip);
            foreach(string relative in reuse.Dependencies.Order(StringComparer.OrdinalIgnoreCase))
            {
                if(plan.Any(x=>string.Equals(x.File.Path,relative,StringComparison.OrdinalIgnoreCase)))throw new IOException("加载器依赖与插件路径重叠。");
                plan.Add((null,new(relative,reuse.Hashes[relative],false)));
            }
            if(!plan.Any(x=>x.Entry is not null&&x.File.Path==FusionPluginPrefix+"FusionUnityEmbedded.dll"))throw new IOException("缺少匹配现有加载器的 Fusion 插件。");
        }
        if(missing.Any(p=>!plan.Any(x=>x.Entry is not null&&string.Equals(x.File.Path,p,StringComparison.OrdinalIgnoreCase))))
            throw new IOException("缺失组件不在当前安装包中，未自动修复。");
        var next=new InstallLedger(1,game.ExePath,game.Backend,game.Architecture,plan.Select(x=>x.File).ToArray());
        if(previous is not null&&missing.Count==0&&old.Count==plan.Count&&plan.All(x=>old.TryGetValue(x.File.Path,out var f)&&f==x.File))return;
        using var dependencyLease=reuse is null?null:LockReusableDependencies(game,reuse);
        string backup=".fusion-unity-backups/"+Guid.NewGuid().ToString("N");
        foreach(var item in old.Values.Where(x=>x.Owned&&!missing.Contains(x.Path)))
        {string dest=SafePath(game.Root,backup+"/"+item.Path);Directory.CreateDirectory(Path.GetDirectoryName(dest)!);File.Copy(SafePath(game.Root,item.Path),dest);if(Hash(dest)!=item.Hash)throw new IOException("组件备份失败，未安装。");}
        WriteJsonAtomic(TransactionPath(game),new UpdateRecord(previous,next,backup,missing.ToArray()));
        try
        {
            foreach(var item in plan.Where(x=>x.File.Owned))
            {
                string target=SafePath(game.Root,item.File.Path);if(File.Exists(target)&&Hash(target)==item.File.Hash)continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);string temp=target+".fusion-"+Guid.NewGuid().ToString("N")+".tmp";
                try{using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var input=item.Entry!.Open()){input.CopyTo(output);output.Flush(true);}if(Hash(temp)!=item.File.Hash)throw new IOException("组件写入校验失败。");File.Move(temp,target,true);}
                finally{if(File.Exists(temp))File.Delete(temp);}
            }
            foreach(var retired in old.Values.Where(x=>x.Owned&&!unique.Contains(x.Path)))File.Delete(SafePath(game.Root,retired.Path));
            WriteJsonAtomic(Path.Combine(game.Root,LedgerName),next);
            File.Delete(TransactionPath(game));
        }
        catch{RecoverUpdate(game);throw;}
    }

    private const string FusionPluginPrefix="BepInEx/plugins/FusionUnityEmbedded/";
    private sealed record ReusableLoader(string PayloadBackend,string Family,Dictionary<string,UnityEmbeddedLoaderMetadata.AssemblySurface> Assemblies,HashSet<string> Dependencies,Dictionary<string,string> Hashes,int RuntimeMajor);
    private static ReusableLoader? InspectReusableLoader(UnityEmbeddedGame game,InstallLedger? previous)
    {
        // Fusion-owned complete loaders keep their existing transactional update path.
        if(previous?.Files.Any(f=>f.Owned&&f.Path.Equals("doorstop_config.ini",StringComparison.OrdinalIgnoreCase))==true)return null;
        string config=SafePath(game.Root,"doorstop_config.ini");if(!File.Exists(config))return null;
        var hashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        string configHash=Hash(config);var settings=ReadDoorstopSettings(config);
        if(Hash(config)!=configHash)throw new IOException("加载器配置在检查中改变，未安装翻译组件。");
        bool general=settings.TryGetValue("General/enabled",out string? enabled);
        if(!general)settings.TryGetValue("UnityDoorstop/enabled",out enabled);
        if(!string.Equals(enabled,"true",StringComparison.OrdinalIgnoreCase))throw new EmbeddedCompatibilityException("现有加载器未启用，已保留其配置；本次未接入翻译。");
        string key=general?"General/target_assembly":"UnityDoorstop/targetAssembly";
        if(!settings.TryGetValue(key,out string? target))throw new EmbeddedCompatibilityException("现有加载器入口无法确认，未覆盖现有安装。");
        target=RelativeLoaderPath(game,target);
        string family,payloadBackend,baseAssembly,baseType,entryAssembly;
        if(game.Backend=="IL2CPP"&&target.Equals("BepInEx/core/BepInEx.Unity.IL2CPP.dll",StringComparison.OrdinalIgnoreCase))
        {family="IL2CPP6";payloadBackend="IL2CPP";baseAssembly="BepInEx.Core";baseType="BepInEx.BepInPlugin";entryAssembly="BepInEx.Unity.IL2CPP";}
        else if(game.Backend=="Mono"&&target.Equals("BepInEx/core/BepInEx.Preloader.dll",StringComparison.OrdinalIgnoreCase))
        {family="Mono5";payloadBackend="Mono";baseAssembly="BepInEx";baseType="BepInEx.BaseUnityPlugin";entryAssembly="BepInEx.Preloader";}
        else if(game.Backend=="Mono"&&target.Equals("BepInEx/core/BepInEx.Unity.Mono.Preloader.dll",StringComparison.OrdinalIgnoreCase))
        {family="Mono6";payloadBackend="Mono6";baseAssembly="BepInEx.Core";baseType="BepInEx.BepInPlugin";entryAssembly="BepInEx.Unity.Mono.Preloader";}
        else throw new EmbeddedCompatibilityException("现有加载器入口与此 Unity 后端不匹配，未覆盖加载器或 Mods。");
        var dependencies=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"doorstop_config.ini",target,"winhttp.dll",".doorstop_version"};
        foreach(string relative in dependencies)if(!File.Exists(SafePath(game.Root,relative)))throw new EmbeddedCompatibilityException("现有加载器文件不完整，未覆盖现有安装："+relative);
        string markerPath=SafePath(game.Root,".doorstop_version"),markerHash=Hash(markerPath);
        string marker=ReadBoundedText(markerPath,64).Trim();
        if(Hash(markerPath)!=markerHash)throw new IOException("加载器版本记录在检查中改变，未安装翻译组件。");
        hashes[".doorstop_version"]=markerHash;
        if(!Version.TryParse(marker,out var nativeVersion)||nativeVersion.Major is not (3 or 4))throw new EmbeddedCompatibilityException("现有 Doorstop 版本无法确认，未改动原安装。");
        using(var native=File.OpenRead(SafePath(game.Root,"winhttp.dll")))using(var pe=new PEReader(native,PEStreamOptions.LeaveOpen))
        {
            if(pe.PEHeaders.CoffHeader.Machine!=(game.Architecture=="x64"?Machine.Amd64:Machine.I386))throw new EmbeddedCompatibilityException("现有加载器位数与游戏不匹配，未改动原安装。");
            native.Position=0;hashes["winhttp.dll"]=Convert.ToHexString(SHA256.HashData(native));
        }
        var assemblies=new Dictionary<string,UnityEmbeddedLoaderMetadata.AssemblySurface>(StringComparer.Ordinal);
        hashes["doorstop_config.ini"]=configHash;
        void Add(string name)
        {
            if(assemblies.ContainsKey(name))return;string relative="BepInEx/core/"+name+".dll",path=SafePath(game.Root,relative);
            if(!File.Exists(path)||new FileInfo(path).Length>32L*1024*1024)throw new EmbeddedCompatibilityException("现有加载器缺少匹配接口："+name);
            using var stream=File.OpenRead(path);var surface=UnityEmbeddedLoaderMetadata.Read(stream);
            if(surface.Name!=name)throw new EmbeddedCompatibilityException("现有加载器组件身份不匹配："+name);
            stream.Position=0;hashes[relative]=Convert.ToHexString(SHA256.HashData(stream));
            assemblies.Add(name,surface);dependencies.Add(relative);
        }
        Add(entryAssembly);Add(baseAssembly);Add("0Harmony");
        int major=family=="Mono5"?5:6;
        if(assemblies[entryAssembly].Version.Major!=major||assemblies[baseAssembly].Version.Major!=major||!assemblies[baseAssembly].Types.Contains(baseType))throw new EmbeddedCompatibilityException("现有 BepInEx 接口系列不匹配，未替换现有加载器。");
        if(family=="Mono6")Add("BepInEx.Unity.Mono");
        int runtimeMajor=0;
        if(family=="IL2CPP6")
        {
            Add("Il2CppInterop.Runtime");
            if(!assemblies[entryAssembly].Types.Contains("BepInEx.Unity.IL2CPP.BasePlugin")||!assemblies[entryAssembly].Types.Contains("BepInEx.Unity.IL2CPP.IL2CPPChainloader"))throw new EmbeddedCompatibilityException("现有 IL2CPP 加载器未提供所需插件入口。");
            if(!settings.TryGetValue("Il2Cpp/coreclr_path",out string? clr)||!settings.TryGetValue("Il2Cpp/corlib_dir",out string? corlib))throw new EmbeddedCompatibilityException("现有 IL2CPP 运行环境路径无法确认，未改动原配置。");
            string coreclr=RelativeLoaderPath(game,clr),libraries=RelativeLoaderPath(game,corlib).TrimEnd('/');
            string corelib=libraries+"/System.Private.CoreLib.dll";
            foreach(string relative in new[]{coreclr,corelib})if(!File.Exists(SafePath(game.Root,relative)))throw new EmbeddedCompatibilityException("现有 IL2CPP 运行环境不完整，未改动原安装。");
            using(var stream=File.OpenRead(SafePath(game.Root,corelib)))
            {
                var identity=UnityEmbeddedLoaderMetadata.Identity(stream);
                if(identity.Name!="System.Private.CoreLib")throw new EmbeddedCompatibilityException("现有 IL2CPP 运行环境组件身份不匹配。");
                runtimeMajor=identity.Version.Major;stream.Position=0;hashes[corelib]=Convert.ToHexString(SHA256.HashData(stream));
            }
            using(var stream=File.OpenRead(SafePath(game.Root,coreclr)))using(var pe=new PEReader(stream,PEStreamOptions.LeaveOpen))
            {
                if(pe.PEHeaders.CoffHeader.Machine!=Machine.Amd64)throw new EmbeddedCompatibilityException("现有 IL2CPP 运行环境位数不匹配。");
                stream.Position=0;hashes[coreclr]=Convert.ToHexString(SHA256.HashData(stream));
            }
            dependencies.Add(coreclr);dependencies.Add(corelib);
        }
        if(settings.TryGetValue("UnityMono/dll_search_path_override",out string? search)&&!string.IsNullOrWhiteSpace(search))
            foreach(string segment in search.Split(';'))RelativeLoaderPath(game,segment);
        foreach(string relative in dependencies)if(!hashes.ContainsKey(relative))hashes[relative]=Hash(SafePath(game.Root,relative));
        return new(payloadBackend,family,assemblies,dependencies,hashes,runtimeMajor);
    }
    private static void ValidateReusablePlugin(UnityEmbeddedGame game,ReusableLoader loader,ZipArchive zip)
    {
        var entry=zip.GetEntry(FusionPluginPrefix+"FusionUnityEmbedded.dll")??throw new IOException("缺少匹配加载器的翻译插件。");
        using var input=entry.Open();using var buffer=new MemoryStream();input.CopyTo(buffer);buffer.Position=0;
        var plugin=UnityEmbeddedLoaderMetadata.Read(buffer);
        if(plugin.Name!="FusionUnityEmbedded")throw new IOException("Fusion 插件身份不匹配。");
        if(loader.Family=="IL2CPP6"&&plugin.RuntimeMajor>loader.RuntimeMajor)throw new EmbeddedCompatibilityException("现有 IL2CPP 运行环境版本低于此 Fusion 插件所需版本，未升级或替换原环境。");
        foreach(string requirement in plugin.RequiredMembers)
        {
            int separator=requirement.IndexOf('|');string assemblyName=requirement[..separator],member=requirement[(separator+1)..];
            if(!loader.Assemblies.TryGetValue(assemblyName,out var surface))
            {
                string relative="BepInEx/core/"+assemblyName+".dll",path=SafePath(game.Root,relative);
                if(!File.Exists(path)||new FileInfo(path).Length>32L*1024*1024)throw new EmbeddedCompatibilityException("现有加载器缺少 Fusion 插件所需接口："+assemblyName);
                using var stream=File.OpenRead(path);surface=UnityEmbeddedLoaderMetadata.Read(stream);
                if(surface.Name!=assemblyName)throw new EmbeddedCompatibilityException("加载器依赖身份不匹配："+assemblyName);
                stream.Position=0;loader.Hashes[relative]=Convert.ToHexString(SHA256.HashData(stream));
                loader.Assemblies.Add(assemblyName,surface);loader.Dependencies.Add(relative);
            }
            if(!surface.Members.Contains(member))throw new EmbeddedCompatibilityException("现有加载器与 Fusion 插件接口不兼容，未覆盖原安装："+assemblyName);
        }
        if(loader.Family=="IL2CPP6"&&!loader.Assemblies["BepInEx.Unity.IL2CPP"].Members.Any(m=>m.StartsWith("BepInEx.Unity.IL2CPP.IL2CPPChainloader::AddUnityComponent`0(System.Type):",StringComparison.Ordinal)))
            throw new EmbeddedCompatibilityException("现有 IL2CPP 加载器缺少所需帧回调接口，未安装翻译组件。");
    }
    private sealed class ReusableDependencyLease:IDisposable
    {
        internal readonly List<FileStream> Streams=new();
        public void Dispose(){foreach(var stream in Streams)stream.Dispose();Streams.Clear();}
    }
    private static ReusableDependencyLease LockReusableDependencies(UnityEmbeddedGame game,ReusableLoader loader)
    {
        var lease=new ReusableDependencyLease();
        try
        {
            foreach(string relative in loader.Dependencies.Order(StringComparer.OrdinalIgnoreCase))
            {
                var stream=File.OpenRead(SafePath(game.Root,relative));lease.Streams.Add(stream);
                if(Convert.ToHexString(SHA256.HashData(stream))!=loader.Hashes[relative])throw new IOException("现有加载器在检查后改变，未安装翻译组件。");
            }
            return lease;
        }
        catch{lease.Dispose();throw;}
    }
    private static Dictionary<string,string> ReadDoorstopSettings(string path)
    {
        var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);string section="";
        foreach(string source in ReadBoundedText(path,64*1024).Split('\n'))
        {
            string line=source.Trim();if(line.Length==0||line[0] is '#' or ';')continue;
            if(line.StartsWith('[')&&line.EndsWith(']')){section=line[1..^1].Trim();continue;}
            int separator=line.IndexOf('=');if(separator<1)throw new EmbeddedCompatibilityException("现有加载器配置格式无法确认，未改动原配置。");
            string key=section+"/"+line[..separator].Trim(),value=line[(separator+1)..].Trim();
            if(!result.TryAdd(key,value))throw new EmbeddedCompatibilityException("现有加载器配置有重复选项，未猜测其生效值。");
        }
        return result;
    }
    private static string ReadBoundedText(string path,int maximum)
    {
        if(new FileInfo(path).Length>maximum)throw new EmbeddedCompatibilityException("现有加载器配置超过安全读取范围。");
        return File.ReadAllText(path,new UTF8Encoding(false,true)).TrimStart('\uFEFF');
    }
    private static string RelativeLoaderPath(UnityEmbeddedGame game,string value)
    {
        value=value.Trim();if(value.StartsWith('"')&&value.EndsWith('"')&&value.Length>1)value=value[1..^1];
        if(value.Length==0||value.Contains('"'))throw new EmbeddedCompatibilityException("现有加载器路径不可确认，未改动原配置。");
        string path=SafePath(game.Root,value);return Path.GetRelativePath(game.Root,path).Replace('\\','/');
    }
}
