using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

internal sealed record UnrealEngine(string Runtime,string Project,int Major,int Minor)
{
    internal string Version=>$"{Major}.{Minor}";
    internal string Paks=>Path.Combine(Project,"Content","Paks");
}

// Offline eligibility for the pinned regular Game loader. These necessary
// reflection names do not prove native signature resolution or displayed text;
// the authenticated native write/read/restore probe remains mandatory.
internal static class UnrealStructuralPreflight
{
    internal sealed record Result(bool Eligible,string Reason);
    private static readonly ConcurrentDictionary<string,(long Size,long Stamp,int Minor,Result Result)> Cache=new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] Required={"KismetTextLibrary","KismetInternationalizationLibrary","PolyglotDataToText","FindTextInLocalizationTable","GetCurrentLanguage","PolyglotTextData","TextBlock","GetText","DataTable","SpawnObject"};
    internal static Result Check(UnrealEngine engine,bool fresh=false)
    {
        if(engine.Major!=4||engine.Minor is <22 or >27)return new(false,"loader-version-unreviewed");
        try
        {
            var info=new FileInfo(engine.Runtime);
            if(!info.Exists||info.Length is <4096 or >536870912)return new(false,"runtime-size-unreviewed");
            if(!fresh&&Cache.TryGetValue(info.FullName,out var cached)&&cached.Size==info.Length&&cached.Stamp==info.LastWriteTimeUtc.Ticks&&cached.Minor==engine.Minor)return cached.Result;
            using var input=new FileStream(info.FullName,FileMode.Open,FileAccess.Read,FileShare.Read);
            var result=Inspect(input,engine.Minor);Cache[info.FullName]=(info.Length,info.LastWriteTimeUtc.Ticks,engine.Minor,result);return result;
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException or OverflowException){return new(false,"runtime-structure-unreadable");}
    }
    internal static Result Inspect(Stream input,int minor)
    {
        if(minor is <22 or >27||!input.CanSeek||input.Length is <4096 or >536870912)return new(false,"runtime-size-or-version-unreviewed");
        byte[] dos=new byte[64];input.Position=0;input.ReadExactly(dos);
        if(BinaryPrimitives.ReadUInt16LittleEndian(dos)!=0x5a4d)return new(false,"not-pe");
        int pe=BinaryPrimitives.ReadInt32LittleEndian(dos.AsSpan(60));
        if(pe<64||pe>1048576||pe>input.Length-264)return new(false,"invalid-pe-offset");
        input.Position=pe;byte[] header=new byte[264];input.ReadExactly(header);
        if(BinaryPrimitives.ReadUInt32LittleEndian(header)!=0x4550||BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4))!=0x8664||BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(24))!=0x20b)return new(false,"requires-amd64-pe32plus");
        int count=BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6)),optional=BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20));
        uint imageSize=BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80)),headersSize=BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84));
        long table=(long)pe+24+optional;
        if(count is <1 or >96||optional is <112 or >4096||imageSize is <4096 or >2147483648||headersSize<table+40L*count||headersSize>input.Length||table+40L*count>input.Length)return new(false,"invalid-pe-sections");
        var ranges=new List<(uint Raw,uint Size,uint Rva,uint Virtual,uint Flags)>();bool code=false;
        input.Position=table;byte[] section=new byte[40];
        for(int i=0;i<count;i++)
        {
            input.ReadExactly(section);
            uint virtualSize=BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(8)),rva=BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(12));
            uint size=BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(16)),raw=BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(20)),flags=BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(36));
            ulong mapped=Math.Max(virtualSize,size);
            if((ulong)rva+mapped>imageSize||(size>0&&(raw<headersSize||(ulong)raw+size>(ulong)input.Length)))return new(false,"section-out-of-bounds");
            foreach(var previous in ranges)
                if(size>0&&previous.Size>0&&(ulong)raw<(ulong)previous.Raw+previous.Size&&(ulong)previous.Raw<(ulong)raw+size
                    ||mapped>0&&previous.Virtual>0&&(ulong)rva<(ulong)previous.Rva+previous.Virtual&&(ulong)previous.Rva<(ulong)rva+mapped)return new(false,"overlapping-sections");
            ranges.Add((raw,size,rva,(uint)mapped,flags));
            code|=size>0&&(flags&0x60000020)==0x60000020&&(flags&0x80000000)==0;
        }
        if(!code)return new(false,"missing-readable-executable-code");
        var names=Required.Select(name=>(Ansi:Encoding.ASCII.GetBytes(name+"\0"),Wide:Encoding.Unicode.GetBytes(name+"\0"))).ToArray();
        var found=new bool[names.Length];bool version=false;byte[] buffer=new byte[1048576+256];
        foreach(var data in ranges.Where(s=>s.Size>0&&(s.Flags&0x40000040)==0x40000040&&(s.Flags&0xa0000000)==0))
        {
            input.Position=data.Raw;long remaining=data.Size;int carry=0;
            while(remaining>0)
            {
                int length=(int)Math.Min(1048576,remaining);input.ReadExactly(buffer.AsSpan(carry,length));int total=carry+length;var span=buffer.AsSpan(0,total);
                for(int n=0;n<names.Length;n++)if(!found[n])found[n]=span.IndexOf(names[n].Ansi)>=0||span.IndexOf(names[n].Wide)>=0;
                if(!version)version=Regex.IsMatch(Encoding.Latin1.GetString(span).Replace("\0",""),@"\+\+UE4\+Release-4\."+minor+@"\D");
                remaining-=length;carry=Math.Min(256,total);buffer.AsSpan(total-carry,carry).CopyTo(buffer);
            }
        }
        if(!version)return new(false,"engine-version-not-in-valid-readonly-section");
        int missing=Array.IndexOf(found,false);return missing>=0?new(false,"required-reflection-name-missing:"+Required[missing]):new(true,"structural-candidate-native-selfcheck-required");
    }
}

internal static class UnrealBuildProfiles
{
    internal const string SrteSha256="29615C9A59B3565DC96BDA0AEA85617F9963C38AA235580075B1CF99F9FF495B";
    private static readonly ConcurrentDictionary<string,(long Size,long Stamp,bool Matches)> Checked=new(StringComparer.OrdinalIgnoreCase);
    internal static bool MatchesSrte(UnrealEngine engine,bool fresh=false)
    {
        if(engine.Major!=4||engine.Minor!=26)return false;
        try
        {
            var info=new FileInfo(engine.Runtime);
            if(!info.Exists||info.Length!=101956096)return false;
            if(!fresh&&Checked.TryGetValue(info.FullName,out var old)&&old.Size==info.Length&&old.Stamp==info.LastWriteTimeUtc.Ticks)return old.Matches;
            using var input=File.OpenRead(info.FullName);bool matches=Convert.ToHexString(SHA256.HashData(input))==SrteSha256;
            Checked[info.FullName]=(info.Length,info.LastWriteTimeUtc.Ticks,matches);return matches;
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException){return false;}
    }
    internal static void Add(UnrealEngine engine,Dictionary<string,byte[]> payload)
    {
        if(engine.Major!=4)return;
        // Re-hash at installation, even if UI recognition was already cached.
        if(!UnrealStructuralPreflight.Check(engine,fresh:true).Eligible)throw new EmbeddedCompatibilityException("此虚幻游戏未通过运行结构与所需文本接口的检查，未安装翻译组件。");
        // Known profile remains an exact-build fallback. Generic candidates
        // use the pinned loader's own signatures; never reuse this offset.
        if(!MatchesSrte(engine,fresh:true))return;
        const string pattern="48 89 5C 24 10 48 89 74 24 18 48 89 7C 24 20 41 56 48 83 EC 20 8B 01 48 8B F2 8B F8 0F B7 D8 C1 EF 10 4C 8B F1 E8 26 E9 FF FF 45 8B 46 04 44 8D";
        // Matching PE/PDB GUID 1a376b68-7081-4fb1-87b9-4709a453fdfd age 1.
        // Anchor: FName::ToString(FString&), RVA 0x1b36070, unique 48 bytes,
        // no overlapping PE base relocations. Target RVAs are public symbols:
        // GUObjectArray 0x5bd4368; IConsoleManager::SetupSingleton 0x1a222a0.
        // The pinned loader's documented signature callback accepts addresses;
        // this introduces no guessed object layout or additional native hook.
        foreach(var target in new[]{("GUObjectArray",67756792), ("ConsoleManager",-1129936)})
        {
            string script="-- Exact SRTE binary SHA256 "+SrteSha256+"; PDB-derived profile.\n"
                +"function Register() return '"+pattern+"' end\n"
                +"function OnMatchFound(address) return address + ("+target.Item2.ToString(System.Globalization.CultureInfo.InvariantCulture)+") end\n";
            payload.Add("ue4ss\\UE4SS_Signatures\\"+target.Item1+".lua",Encoding.UTF8.GetBytes(script));
        }
    }
}

internal static class UnrealGameAdapter
{
    private static readonly ConcurrentDictionary<string,(long Size,long Stamp,UnrealEngine? Engine)> Versions=new(StringComparer.OrdinalIgnoreCase);
    internal static UnrealEngine? Detect(string exe)
    {
        var matches=EngineStructureDetection.UnrealRuntimeExecutables(exe);
        if(matches.Length!=1)return null;
        string runtime=matches[0];var f=new FileInfo(runtime);
        if(Versions.TryGetValue(runtime,out var cached)&&cached.Size==f.Length&&cached.Stamp==f.LastWriteTimeUtc.Ticks)return cached.Engine;
        UnrealEngine? result=null;
        using var input=File.OpenRead(runtime);byte[] buffer=new byte[1024*1024];string carry="";
        while(input.Position<input.Length)
        {
            int length=input.Read(buffer);string text=carry+Encoding.Latin1.GetString(buffer,0,length).Replace("\0","");
            var match=Regex.Match(text,@"\+\+UE([45])\+Release-([45])\.(\d{1,2})(?:\D|$)");
            if(match.Success&&match.Groups[1].Value==match.Groups[2].Value)
            {result=new(runtime,Directory.GetParent(Path.GetDirectoryName(runtime)!)!.Parent!.FullName,int.Parse(match.Groups[2].Value),int.Parse(match.Groups[3].Value));break;}
            carry=text[^Math.Min(100,text.Length)..];
        }
        Versions[runtime]=(f.Length,f.LastWriteTimeUtc.Ticks,result);return result;
    }
    // Version alone is insufficient: generic UE4 candidates require valid PE
    // structure and the bridge's reflected surface before native self-checks.
    internal static bool Candidate(UnrealEngine engine)=>engine.Major==5&&engine.Minor is >=0 and <=7
        ||UnrealStructuralPreflight.Check(engine).Eligible;
    internal static string Storage(string exe)=>Path.Combine(AppDataPaths.Root,"unreal-adapters",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(Detect(exe)?.Runtime??exe).ToUpperInvariant()))));
    private sealed record Ledger(string Exe,string Runtime,Dictionary<string,string> Files);
    private static string Record(string exe)=>Path.Combine(Storage(exe),"install.json");
    internal static bool HasRecord(string exe)=>File.Exists(Record(exe));
    private static string Hash(string path){using var f=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(f));}
    internal static string SafePath(string root,string relative)
    {
        string path=Path.GetFullPath(Path.Combine(root,relative));
        if(!path.StartsWith(Path.GetFullPath(root).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))throw new IOException("组件路径越界。");
        for(string? p=path;p is not null;p=Path.GetDirectoryName(p))
            if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("游戏或组件路径包含链接，未写入。");
        return path;
    }
    private static void Stopped(string exe)
    {
        var game=new GameInfo(exe,"Unreal","Unreal Engine","64 位","",SupportLevel.Candidate,null,"unreal");
        if(FusionAdapterInstaller.ProcessState(game)!=FusionGameProcessState.Stopped)throw new IOException("请先退出此游戏，再准备或恢复翻译组件。");
    }
    internal static string InspectionStamp(string exe)
    {
        string? root=Path.GetDirectoryName(Detect(exe)?.Runtime??exe);
        var paths=new[]{Record(exe),Path.Combine(root!,"ue4ss","UE4SS.dll"),Path.Combine(root!,"ue4ss","UE4SS-settings.ini"),Path.Combine(root!,"ue4ss","Mods","FusionTranslation","Scripts","main.lua")}
            .Concat(UnrealComponentCompatibility.Components.Select(name=>Path.Combine(root!,name)));
        return exe+"|"+string.Join("|",paths.Select(p=>
        {
            var f=new FileInfo(p);if(!f.Exists)return Directory.Exists(p)?"directory":"missing";
            if(f.Name.Equals("xinput1_3.dll",StringComparison.OrdinalIgnoreCase))return UnrealComponentCompatibility.InputStamp(p);
            return f.Length+":"+f.LastWriteTimeUtc.Ticks;
        }));
    }
    private static Ledger ReadLedger(string exe)
    {
        var path=Record(exe);if(new FileInfo(path).Length>1024*1024)throw new IOException("组件记录超过限制。");
        var record=JsonSerializer.Deserialize<Ledger>(File.ReadAllText(path))??throw new IOException("组件记录无效。");
        var engine=Detect(exe)??throw new IOException("游戏入口已改变。");
        if(!record.Exe.Equals(engine.Runtime,StringComparison.OrdinalIgnoreCase)||!record.Runtime.Equals(engine.Runtime,StringComparison.OrdinalIgnoreCase))throw new IOException("组件记录与所选游戏不一致。");
        return record;
    }
    internal static bool IsInstalled(string exe)
    {
        try{var record=ReadLedger(exe);string root=Path.GetDirectoryName(record.Runtime)!;return record.Files.Count>0&&record.Files.All(p=>File.Exists(SafePath(root,p.Key))&&Hash(SafePath(root,p.Key))==p.Value);}catch{return false;}
    }
    private static Dictionary<string,byte[]> Payload(UnrealEngine engine)
    {
        using var input=RuntimePayloadProvider.Open("unreal-runtime","Unreal","Native","x64",RuntimePayloadProvider.PackageVersion);
        using var zip=new ZipArchive(input);var result=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in zip.Entries)
        {
            if(entry.Length==0)continue;if(entry.Length>64*1024*1024)throw new IOException("组件大小无效。");
            using var data=new MemoryStream();using(var source=entry.Open())source.CopyTo(data);result.Add(entry.FullName.Replace('/','\\'),data.ToArray());
        }
        UnrealBuildProfiles.Add(engine,result);return result;
    }
    internal static void Install(string exe)
    {
        var engine=Detect(exe)??throw new IOException("无法确认虚幻的实际游戏入口或引擎版本。");
        if(!Candidate(engine))throw new EmbeddedCompatibilityException("已识别 UE "+engine.Version+"；当前游戏尚未通过内嵌接入所需的结构与接口检查。");
        Stopped(exe);string root=Path.GetDirectoryName(engine.Runtime)!;SafePath(root,"dwmapi.dll");
        var payload=Payload(engine);
        if(HasRecord(exe)&&IsInstalled(exe))
        {
            var existing=ReadLedger(exe);
            if(existing.Files.Count==payload.Count&&payload.All(p=>existing.Files.GetValueOrDefault(p.Key)==Convert.ToHexString(SHA256.HashData(p.Value))))
            {
                if(UnrealComponentCompatibility.FindConflict(root,ownedPayloadInstalled:true) is {} added)
                    throw new IOException("检测到后来加入的 "+added+"，未叠加加载翻译组件。");
                PrepareCatalog(exe,engine);return;
            }
        }
        if(HasRecord(exe))Restore(exe);
        if(UnrealComponentCompatibility.FindConflict(root,ownedPayloadInstalled:false) is {} conflict)
            throw new IOException("游戏已有 "+conflict+" 组件；已保留，当前不能叠加安装虚幻翻译。");
        PrepareCatalog(exe,engine);
        var record=new Ledger(engine.Runtime,engine.Runtime,payload.ToDictionary(p=>p.Key,p=>Convert.ToHexString(SHA256.HashData(p.Value)),StringComparer.OrdinalIgnoreCase));
        foreach(var p in payload)if(File.Exists(SafePath(root,p.Key)))throw new IOException("游戏已有同名组件，未覆盖。");
        PortableDataStorage.WriteJson(Record(exe),record);
        try
        {
            foreach(var p in payload)
            {
                string path=SafePath(root,p.Key);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);output.Write(p.Value);output.Flush(true);
            }
        }
        catch{try{Restore(exe);}catch{}throw;}
    }
    internal static void Restore(string exe)
    {
        if(!HasRecord(exe))return;Stopped(exe);var record=ReadLedger(exe);string root=Path.GetDirectoryName(record.Runtime)!;
        foreach(var p in record.Files)if(File.Exists(SafePath(root,p.Key))&&Hash(SafePath(root,p.Key))!=p.Value)throw new IOException("虚幻组件已被其他程序改变，未覆盖或删除。");
        string archive=Path.Combine(Storage(exe),"restored-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(archive);
        foreach(var p in record.Files)
        {
            string path=SafePath(root,p.Key);if(!File.Exists(path))continue;
            string target=SafePath(archive,p.Key);Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Move(path,target);
        }
        // Logs/cache generated by our private loader are retained in the same
        // recovery record. Foreign files are never removed or overwritten.
        string owned=SafePath(root,"ue4ss");
        if(Directory.Exists(owned))
        {
            foreach(string log in new[]{"UE4SS.log","UE4SS.log.old","UE4SS.cache"})
            {string path=SafePath(owned,log);if(File.Exists(path))File.Move(path,Path.Combine(archive,log));}
            foreach(string dir in Directory.EnumerateDirectories(owned,"*",SearchOption.AllDirectories).OrderByDescending(p=>p.Length))
                if(!Directory.EnumerateFileSystemEntries(dir).Any())Directory.Delete(dir);
            if(!Directory.EnumerateFileSystemEntries(owned).Any())Directory.Delete(owned);
        }
        File.Move(Record(exe),Path.Combine(archive,"install.json"));
    }
    internal static void PrepareCatalog(string exe,UnrealEngine engine)
    {
        SafePath(engine.Project,"Content\\Paks");
        var paks=Directory.GetFiles(engine.Paks).Where(p=>new[]{".pak",".utoc",".ucas"}.Contains(Path.GetExtension(p),StringComparer.OrdinalIgnoreCase)).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase);
        string fingerprint="78.2|"+engine.Version+"|"+string.Join("|",paks.Select(p=>{var f=new FileInfo(p);return f.Name+":"+f.Length+":"+f.LastWriteTimeUtc.Ticks;}));
        string catalog=Path.Combine(Storage(exe),"catalog.json"),stamp=catalog+".stamp";
        if(File.Exists(catalog)&&File.Exists(stamp)&&File.ReadAllText(stamp)==fingerprint)return;
        Directory.CreateDirectory(Storage(exe));string temporary=catalog+"."+Guid.NewGuid().ToString("N")+".tmp";
        string worker=Path.Combine(AppContext.BaseDirectory,"tools","unreal-catalog","FusionUnrealCatalog.exe");
        if(!File.Exists(worker))throw new IOException("缺少虚幻文本提取工具。");
        var info=new ProcessStartInfo(worker){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        info.ArgumentList.Add(engine.Paks);info.ArgumentList.Add($"GAME_UE{engine.Major}_{engine.Minor}");info.ArgumentList.Add(temporary);
        using var process=Process.Start(info)??throw new IOException("无法启动文本提取工具。");var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        try
        {
            if(!process.WaitForExit(90000)){process.Kill(true);throw new IOException("虚幻文本提取超时，游戏文件未改动。");}
            if(process.ExitCode!=0||!File.Exists(temporary))throw new IOException("虚幻资源提取失败："+SafeDiagnosticOutput.Summary(stderr.GetAwaiter().GetResult()));
            if(new FileInfo(temporary).Length>32*1024*1024)throw new IOException("虚幻文本目录超过限制。");
            using var doc=JsonDocument.Parse(File.ReadAllText(temporary));var data=doc.RootElement;
            if(data.GetProperty("protocol").GetInt32()!=76||data.GetProperty("entries").ValueKind!=JsonValueKind.Array)
                throw new IOException("虚幻文本目录格式不匹配，未安装翻译组件。");
            // An empty static catalogue is not proof that a running game has no
            // readable text. The bridge separately checks identity-preserving
            // runtime discovery before it registers any localization changes.
            File.Move(temporary,catalog,true);File.WriteAllText(stamp,fingerprint);
        }
        finally{if(!process.HasExited){process.Kill(true);process.WaitForExit(5000);}if(File.Exists(temporary))File.Delete(temporary);}
    }
}
