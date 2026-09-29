using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace ScreenshotTranslationUiTester;

internal sealed record RenpyInstallation(string Root,string GameDirectory,string Version,string[] RuntimeExecutables);
internal static class RenpyGameAdapter
{
    internal const string ScriptName="zz_fusion_translation.rpy";
    internal static RenpyInstallation? Detect(string exe)
    {
        string root=Path.GetDirectoryName(Path.GetFullPath(exe))!,game=Path.Combine(root,"game"),engine=Path.Combine(root,"renpy","__init__.py");
        if(!Directory.Exists(game)||!File.Exists(engine)||!File.Exists(Path.Combine(root,"renpy","config.py"))||!Directory.Exists(Path.Combine(root,"lib")))return null;
        if(new FileInfo(engine).Length>256*1024)return null;
        var versions=Regex.Matches(File.ReadAllText(engine),@"version_tuple\s*=\s*\((\d+),\s*(\d+),\s*(\d+)").Select(m=>m.Groups[1].Value+"."+m.Groups[2].Value+"."+m.Groups[3].Value).Distinct().ToArray();
        bool python2=Directory.Exists(Path.Combine(root,"lib","python2.7"));
        string version=versions.FirstOrDefault(v=>v.StartsWith(python2?"7.":"8.",StringComparison.Ordinal))??versions.FirstOrDefault()??"未知版本";
        string versionFile=Path.Combine(root,"renpy","vc_version.py");
        if(version=="未知版本"&&File.Exists(versionFile)&&new FileInfo(versionFile).Length<65536)
        {var match=Regex.Match(File.ReadAllText(versionFile),"version\\s*=\\s*['\"]([0-9.]+)");if(match.Success)version=match.Groups[1].Value;}
        var runtimes=Directory.EnumerateDirectories(Path.Combine(root,"lib"),"*",SearchOption.TopDirectoryOnly)
            .Where(d=>Path.GetFileName(d).Contains("windows",StringComparison.OrdinalIgnoreCase))
            .SelectMany(d=>new[]{Path.Combine(d,"pythonw.exe"),Path.Combine(d,"python.exe"),Path.Combine(d,Path.GetFileName(exe))}).Where(File.Exists).ToArray();
        return runtimes.Length>0?new(root,game,version,runtimes):null;
    }
    internal static string Storage(string exe)=>Path.Combine(AppDataPaths.Root,"renpy-adapters",Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(exe).ToUpperInvariant()))).ToLowerInvariant());
    private sealed record InstallRecord(string Exe,string Script,string Hash);
    private static string RecordPath(string exe)=>Path.Combine(Storage(exe),"install.json");
    internal static bool HasRecord(string exe)=>File.Exists(RecordPath(exe));
    internal static bool IsInstalled(string exe)
    {
        try
        {
            var install=Detect(exe);if(install is null||!HasRecord(exe))return false;
            var record=JsonSerializer.Deserialize<InstallRecord>(File.ReadAllText(RecordPath(exe)));
            string path=Path.Combine(install.GameDirectory,ScriptName);CheckPath(path);
            return record is not null&&record.Exe.Equals(Path.GetFullPath(exe),StringComparison.OrdinalIgnoreCase)&&record.Script.Equals(path,StringComparison.OrdinalIgnoreCase)
                &&File.Exists(path)&&Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))==record.Hash;
        }
        catch{return false;}
    }
    private static void CheckPath(string path)
    {
        for(string? part=Path.GetFullPath(path);part is not null;part=Path.GetDirectoryName(part))
            if((File.Exists(part)||Directory.Exists(part))&&(File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException("组件路径包含链接，未改变文件。");
    }
    private static void RequireStopped(string exe)
    {
        var game=new GameInfo(exe,"Ren'Py","Ren'Py","","",SupportLevel.Candidate,null,"renpy");
        if(FusionAdapterInstaller.ProcessState(game)!=FusionGameProcessState.Stopped)throw new IOException("请先退出所选游戏，再安装或恢复翻译组件。");
    }
    internal static string Payload()
    {
        using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Fusion.RenpyTranslationBridge.rpy")??throw new IOException("缺少 Ren'Py 翻译组件。");
        using var data=Assembly.GetExecutingAssembly().GetManifestResourceStream("Fusion.RenpyGameDataBridge.rpy")??throw new IOException("缺少 Ren’Py 修改组件。");
        using var dataReader=new StreamReader(data);
        using var reader=new StreamReader(stream);return dataReader.ReadToEnd()+"\n"+reader.ReadToEnd();
    }
    internal static void Install(string exe)
    {
        RequireStopped(exe);
        var install=Detect(exe)??throw new IOException("没有找到支持的 Ren'Py 桌面运行环境。");
        string path=Path.Combine(install.GameDirectory,ScriptName),payload=Payload();
        CheckPath(path);CheckPath(path+"c");
        if(HasRecord(exe)){Restore(exe);}
        if(File.Exists(path)||File.Exists(path+"c"))throw new IOException("游戏里已有同名组件，未覆盖。");
        for(string? p=install.GameDirectory;p is not null&&p.Length>=install.Root.Length;p=Path.GetDirectoryName(p))
            if((File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("游戏目录包含链接，未写入组件。");
        string hash=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload)));
        PortableDataStorage.WriteJson(RecordPath(exe),new InstallRecord(Path.GetFullPath(exe),path,hash));
        using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        output.Write(System.Text.Encoding.UTF8.GetBytes(payload));output.Flush(true);
    }
    internal static void Restore(string exe)
    {
        if(!HasRecord(exe))return;
        RequireStopped(exe);
        var record=JsonSerializer.Deserialize<InstallRecord>(File.ReadAllText(RecordPath(exe)))??throw new IOException("翻译组件记录无效。");
        var install=Detect(exe)??throw new IOException("游戏结构已改变，未恢复组件。");
        string path=Path.Combine(install.GameDirectory,ScriptName);
        CheckPath(path);CheckPath(path+"c");
        if(!Path.GetFullPath(exe).Equals(record.Exe,StringComparison.OrdinalIgnoreCase)||!path.Equals(record.Script,StringComparison.OrdinalIgnoreCase))throw new IOException("翻译组件记录与所选游戏不一致。");
        if(File.Exists(path)&&Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))!=record.Hash)throw new IOException("翻译组件已被其他程序改变，未覆盖。");
        string archive=Path.Combine(Storage(exe),"restored-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(archive);
        foreach(var file in new[]{path,path+"c"})if(File.Exists(file))File.Move(file,Path.Combine(archive,Path.GetFileName(file)));
        File.Move(RecordPath(exe),Path.Combine(archive,"install.json"));
    }
}
