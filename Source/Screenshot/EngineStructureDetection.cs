using System.Buffers.Binary;

namespace ScreenshotTranslationUiTester;

// Recognition only. No container extraction, process launch, or adapter installation.
internal static class EngineStructureDetection
{
    private const uint PckMagic=0x43504447, PakMagic=0x5A6F12E1;
    private const int DirectoryLimit=64, PackageLimit=128;
    private static readonly EnumerationOptions TopOnly=new(){RecurseSubdirectories=false,IgnoreInaccessible=true,AttributesToSkip=FileAttributes.ReparsePoint};

    internal static bool NeedsRefresh(string? engine)=>string.IsNullOrWhiteSpace(engine)||engine is "未知引擎" or "待识别" or "待重新定位";

    internal static string? UnityDataDirectory(string root,string stem)
    {
        string exact=Path.Combine(root,stem+"_Data");
        if(IsUnityData(root,exact))return exact;
        // Renamed launchers may have a differently named Data directory. Never pick
        // an arbitrary folder, or guess between two unrelated Unity installations.
        var matches=Directories(root,"*_Data").Where(p=>IsUnityData(root,p)).Take(2).ToArray();
        return matches.Length==1?matches[0]:null;
    }

    private static bool IsUnityData(string root,string data)
    {
        if(!Directory.Exists(data)||IsLink(data))return false;
        bool runtime=File.Exists(Path.Combine(root,"UnityPlayer.dll"))
            ||File.Exists(Path.Combine(data,"Managed","UnityEngine.dll"))
            ||File.Exists(Path.Combine(data,"Managed","UnityEngine.CoreModule.dll"));
        bool resource=File.Exists(Path.Combine(data,"globalgamemanagers"))
            ||File.Exists(Path.Combine(data,"mainData"))
            ||HasPrefix(Path.Combine(data,"data.unity3d"),"UnityFS\0"u8.ToArray())
            ||HasPrefix(Path.Combine(data,"data.unity3d"),"UnityWeb\0"u8.ToArray())
            ||HasPrefix(Path.Combine(data,"data.unity3d"),"UnityRaw\0"u8.ToArray());
        bool il2cpp=File.Exists(Path.Combine(root,"GameAssembly.dll"))
            &&HasPrefix(Path.Combine(data,"il2cpp_data","Metadata","global-metadata.dat"),[0xaf,0x1b,0xb1,0xfa]);
        return runtime&&resource||il2cpp;
    }

    internal static GameInfo? Detect(string exe,string name,string architecture)
    {
        if(!IsPortableExecutable(exe))return null;
        if(UnrealBootstrap.Target(exe) is {} declared&&Detect(declared,Path.GetFileNameWithoutExtension(declared),architecture) is {} target&&target.Engine=="Unreal Engine")
            return target with{ExePath=exe,Name=name};
        string root=Path.GetDirectoryName(exe)!;
        string? godot=ReadPck(Path.ChangeExtension(exe,".pck"),false)??ReadPck(exe,true);
        if(godot is not null)return new(exe,name,"Godot",architecture,"Godot "+godot+" · 已识别引擎，内嵌翻译尚未接入",SupportLevel.Planned,root);
        foreach(string project in UnrealProjects(root))
        {
            string paks=Path.Combine(project,"Content","Paks");
            if(!Directory.Exists(paks)||IsLink(paks)||!HasMatchingUnrealExecutable(project,exe,name))continue;
            var packages=Files(paks,"*").Take(PackageLimit).ToArray();
            bool pak=packages.Any(p=>p.EndsWith(".pak",StringComparison.OrdinalIgnoreCase)&&IsPak(p));
            bool io=packages.Any(p=>p.EndsWith(".utoc",StringComparison.OrdinalIgnoreCase)&&IsIoStore(p));
            if(pak||io)return new(exe,name,"Unreal Engine",architecture,"已识别虚幻 "+(pak&&io?"PAK / IoStore":pak?"PAK":"IoStore")+" 资源 · 内嵌翻译尚未接入",SupportLevel.Planned,project);
        }
        return null;
    }

    private static IEnumerable<string> UnrealProjects(string root)
    {
        // A selected Shipping executable lives exactly two levels below its project.
        var directory=new DirectoryInfo(root);
        if((directory.Name.Equals("Win64",StringComparison.OrdinalIgnoreCase)||directory.Name.Equals("Win32",StringComparison.OrdinalIgnoreCase))
            &&directory.Parent?.Name.Equals("Binaries",StringComparison.OrdinalIgnoreCase)==true&&directory.Parent.Parent is {} project)
        {yield return project.FullName;yield break;}
        yield return root;
        foreach(string child in Directories(root,"*"))yield return child;
    }

    private static bool HasMatchingUnrealExecutable(string project,string selected,string stem)
        =>MatchingUnrealExecutables(project,selected,stem).Any();

    internal static string[] UnrealRuntimeExecutables(string selected)
    {
        if(UnrealBootstrap.Target(selected) is {} declared)return UnrealRuntimeExecutables(declared);
        string stem=Path.GetFileNameWithoutExtension(selected);
        var game=Detect(selected,stem,"");
        return game?.Engine=="Unreal Engine"&&game.DataDirectory is {} project
            ?MatchingUnrealExecutables(project,selected,stem).ToArray():[];
    }

    private static IEnumerable<string> MatchingUnrealExecutables(string project,string selected,string stem)
    {
        foreach(string platform in new[]{"Win64","Win32"})
        {
            string binaryRoot=Path.Combine(project,"Binaries",platform);
            foreach(string binary in Files(binaryRoot,"*.exe").Take(DirectoryLimit))
            {
                string binaryStem=Path.GetFileNameWithoutExtension(binary);
                bool isSelected=Path.GetFullPath(binary).Equals(selected,StringComparison.OrdinalIgnoreCase);
                string expected=isSelected?Path.GetFileName(project):stem;
                if(MatchesUnrealStem(binaryStem,expected)&&IsPortableExecutable(binary))yield return binary;
            }
        }
    }

    private static bool MatchesUnrealStem(string actual,string expected)=>new[]{"","-Shipping","-Win64-Shipping","-Win32-Shipping","-Win64-Development","-Win32-Development"}
        .Any(suffix=>actual.Equals(expected+suffix,StringComparison.OrdinalIgnoreCase));

    private static bool IsPortableExecutable(string path)
    {
        try
        {
            using var stream=File.OpenRead(path);using var reader=new BinaryReader(stream);
            if(stream.Length<64||reader.ReadUInt16()!=0x5A4D)return false;
            stream.Position=60;int pe=reader.ReadInt32();if(pe<64||pe>stream.Length-24)return false;
            stream.Position=pe;return reader.ReadUInt32()==0x00004550;
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){return false;}
    }

    private static string? ReadPck(string path,bool embedded)
    {
        try
        {
            if(!File.Exists(path)||IsLink(path))return null;
            using var stream=File.OpenRead(path);using var reader=new BinaryReader(stream);
            long offset=0,limit=stream.Length;
            if(embedded)
            {
                if(!IsPortableExecutable(path)||stream.Length<12)return null;
                stream.Position=stream.Length-12;ulong size=reader.ReadUInt64();
                if(reader.ReadUInt32()!=PckMagic||size>(ulong)(stream.Length-12))return null;
                offset=stream.Length-12-(long)size;limit=stream.Length-12;
            }
            if(limit-offset<88)return null;
            stream.Position=offset;
            if(reader.ReadUInt32()!=PckMagic)return null;
            uint format=reader.ReadUInt32(),major=reader.ReadUInt32(),minor=reader.ReadUInt32(),patch=reader.ReadUInt32();
            // Official PCK headers: 2.x/3.x use v1; 4.x uses v2/v3/v4.
            if(!((format==1&&major is 2 or 3)||(format is >=2 and <=4&&major==4))||minor>100||patch>100)return null;
            if(format>=2&&limit-offset<100)return null;
            if(format>=3)
            {
                stream.Position=offset+32;ulong directoryOffset=reader.ReadUInt64();
                if(directoryOffset<40||directoryOffset>(ulong)(limit-offset-4))return null;
            }
            return $"{major}.{minor}.{patch}";
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){return null;}
    }

    private static bool IsPak(string path)
    {
        try
        {
            using var stream=File.OpenRead(path);
            byte[] tail=new byte[(int)Math.Min(256,stream.Length)];stream.Position=stream.Length-tail.Length;stream.ReadExactly(tail);
            // Known official footer lengths, measured from magic to EOF. Do not
            // accept a magic-looking string anywhere inside arbitrary file data.
            foreach(int suffix in new[]{44,172,204,205})
            {
                int at=tail.Length-suffix;if(at<0)continue;
                var header=tail.AsSpan(at);
                if(BinaryPrimitives.ReadUInt32LittleEndian(header)!=PakMagic)continue;
                uint version=BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
                if(!((version is >=1 and <=7&&suffix==44)||(version==8&&suffix is 172 or 204)||(version==9&&suffix==205)||(version is 10 or 11 or 12&&suffix==204)))continue;
                long footer=stream.Length-suffix-(version>=7?17:version>=4?1:0);
                ulong index=BinaryPrimitives.ReadUInt64LittleEndian(header[8..]),size=BinaryPrimitives.ReadUInt64LittleEndian(header[16..]);
                if(footer>=0&&index<=(ulong)footer&&size<=(ulong)footer-index)return true;
            }
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
        return false;
    }

    private static bool IsIoStore(string path)
    {
        try
        {
            string data=Path.ChangeExtension(path,".ucas");
            if(!File.Exists(data)||IsLink(data))return false;
            using var stream=File.OpenRead(path);if(stream.Length<144)return false;
            Span<byte> header=stackalloc byte[24];stream.ReadExactly(header);
            uint size=BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
            return header[..16].SequenceEqual("-==--==--==--==-"u8)&&header[16] is >=1 and <=10&&size>=144&&size<=stream.Length;
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){return false;}
    }

    private static bool HasPrefix(string path,byte[] prefix)
    {
        try{if(!File.Exists(path))return false;using var stream=File.OpenRead(path);if(stream.Length<prefix.Length)return false;byte[] bytes=new byte[prefix.Length];stream.ReadExactly(bytes);return bytes.SequenceEqual(prefix);}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){return false;}
    }
    private static bool IsLink(string path)=>(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0;
    private static string[] Directories(string path,string pattern)
    {try{return Directory.Exists(path)&&!IsLink(path)?Directory.EnumerateDirectories(path,pattern,TopOnly).Take(DirectoryLimit).ToArray():[];}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){return [];}}
    private static IEnumerable<string> Files(string path,string pattern)
    {try{return Directory.Exists(path)&&!IsLink(path)?Directory.EnumerateFiles(path,pattern,TopOnly).Take(PackageLimit).ToArray():[];}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){return [];}}
}
