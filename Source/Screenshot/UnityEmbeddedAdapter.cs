using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

internal sealed record UnityEmbeddedGame(string ExePath,string Root,string DataRoot,string Backend,string Architecture,string UnityVersion);
internal static partial class UnityEmbeddedAdapter
{
    private const string LedgerName=".fusion-unity-embedded-install.json";
    internal sealed record InstalledFile(string Path,string Hash,bool Owned);
    internal sealed record InstallLedger(int Version,string ExePath,string Backend,string Architecture,InstalledFile[] Files);
    internal static UnityEmbeddedGame? Detect(string exe)
    {
        if(!File.Exists(exe)||!exe.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))return null;
        exe=Path.GetFullPath(exe);string root=Path.GetDirectoryName(exe)!;
        string? data=EngineStructureDetection.UnityDataDirectory(root,Path.GetFileNameWithoutExtension(exe));if(data is null)return null;
        string backend=File.Exists(Path.Combine(root,"GameAssembly.dll"))?"IL2CPP":HasMonoStructure(root,data)?"Mono":"";
        if(backend.Length==0)return null;
        string architecture="";
        try{using var stream=File.OpenRead(exe);using var reader=new BinaryReader(stream);if(stream.Length<64||reader.ReadUInt16()!=0x5a4d)return null;stream.Position=60;int offset=reader.ReadInt32();if(offset<64||offset>stream.Length-6)return null;stream.Position=offset;if(reader.ReadUInt32()!=0x4550)return null;architecture=reader.ReadUInt16()switch{0x8664=>"x64",0x14c=>"x86",_=>""};}catch(IOException){return null;}
        if(architecture.Length==0)return null;
        string version="unknown";foreach(var path in new[]{Path.Combine(data,"globalgamemanagers"),Path.Combine(data,"data.unity3d")})
            if(File.Exists(path))try{using var stream=File.OpenRead(path);byte[] head=new byte[Math.Min(stream.Length,4096)];stream.ReadExactly(head);var found=Regex.Match(Encoding.ASCII.GetString(head),@"(?:20\d{2}|[5-9]\d{0,3})\.\d+\.\d+[abfp]\d+");if(found.Success){version=found.Value;break;}}catch(IOException){}
        return new(exe,root,data,backend,architecture,version);
    }
    private static bool HasMonoStructure(string root,string data)
    {
        string managed=Path.Combine(data,"Managed");
        if(File.Exists(Path.Combine(managed,"Assembly-CSharp.dll"))||File.Exists(Path.Combine(managed,"Game.dll")))return true;
        // Assembly-definition projects may have neither conventional game assembly.
        // Require both Unity managed assemblies and an actual Mono runtime layout.
        if(!File.Exists(Path.Combine(managed,"UnityEngine.dll"))&&!File.Exists(Path.Combine(managed,"UnityEngine.CoreModule.dll")))return false;
        return new[]{Path.Combine(root,"MonoBleedingEdge","EmbedRuntime","mono-2.0-bdwgc.dll"),Path.Combine(root,"MonoBleedingEdge","EmbedRuntime","mono-2.0-sgen.dll"),Path.Combine(root,"Mono","EmbedRuntime","mono.dll"),Path.Combine(data,"Mono","mono.dll")}.Any(File.Exists);
    }
    internal static bool HasRecord(string exe)=>Detect(exe)is{} game&&File.Exists(Path.Combine(game.Root,LedgerName));
    internal static string InspectionStamp(string exe)
    {
        var game=Detect(exe);if(game is null)return "not-unity";
        try{var ledger=ReadLedger(game);if(ledger is null)return "not-installed";var record=new FileInfo(Path.Combine(game.Root,LedgerName));var stamp=new StringBuilder().Append(record.Length).Append(':').Append(record.LastWriteTimeUtc.Ticks);
            foreach(var item in ledger.Files){var file=new FileInfo(SafePath(game.Root,item.Path));stamp.Append('|').Append(item.Path).Append(':').Append(file.Exists?file.Length:-1).Append(':').Append(file.Exists?file.LastWriteTimeUtc.Ticks:0);}return stamp.ToString();}
        catch{return "invalid-install-record";}
    }
    internal static bool IsInstalled(string exe)
    {
        var game=Detect(exe);if(game is null)return false;
        try{var ledger=ReadLedger(game);return ledger is not null&&ledger.Files.Length>0&&ledger.Files.All(f=>File.Exists(SafePath(game.Root,f.Path))&&Hash(SafePath(game.Root,f.Path))==f.Hash);}catch{return false;}
    }
    internal static void Install(string exe)
    {
        var game=Detect(exe)??throw new IOException("未识别 Unity 桌面游戏。");EnsureStopped(game);
        RecoverUpdate(game); CheckCompatibility(game);
        string pluginRoot=Path.Combine(game.Root,"BepInEx","plugins");
        if(Directory.Exists(pluginRoot)&&Directory.EnumerateFiles(pluginRoot,"*.dll",SearchOption.AllDirectories).Any(p=>Regex.IsMatch(Path.GetFileName(p),"AutoTranslator|MGITranslator|CloudMeadowTranslator|FusionUnityEmbedded",RegexOptions.IgnoreCase)&&
            !(HasRecord(exe)&&string.Equals(p,Path.Combine(pluginRoot,"FusionUnityEmbedded","FusionUnityEmbedded.dll"),StringComparison.OrdinalIgnoreCase))))
            throw new EmbeddedCompatibilityException("此游戏已有其他内嵌翻译插件，请在独立干净副本中使用通用翻译，避免同时替换文字。");
        if(Directory.Exists(Path.Combine(game.Root,"ReiPatcher"))||Directory.EnumerateFiles(game.Root,"*AutoTranslator*.ini",SearchOption.TopDirectoryOnly).Any())throw new EmbeddedCompatibilityException("检测到 ReiPatcher／自动翻译安装痕迹；为保留已有汉化，本次未接入。请使用独立干净副本；反复重启不能消除此冲突。");
        InstallPayload(game);
    }
    internal static void Restore(string exe)
    {
        var game=Detect(exe)??throw new IOException("未识别 Unity 游戏。");EnsureStopped(game);RecoverUpdate(game);var ledger=ReadLedger(game)??throw new IOException("没有此通用翻译组件的安装记录。");
        var owned=ledger.Files.Where(f=>f.Owned).ToArray();
        foreach(var file in owned){string path=SafePath(game.Root,file.Path);if(File.Exists(path)&&Hash(path)!=file.Hash)throw new IOException("安装后文件已变化，未删除："+file.Path);}
        foreach(var file in owned){string path=SafePath(game.Root,file.Path);if(File.Exists(path))File.Delete(path);}
        File.Delete(Path.Combine(game.Root,LedgerName));
    }
    private static InstallLedger? ReadLedger(UnityEmbeddedGame game)
    {
        string file=Path.Combine(game.Root,LedgerName);if(!File.Exists(file))return null;
        if(new FileInfo(file).Length>1024*1024)throw new IOException("安装记录超过限制。");
        var ledger=JsonSerializer.Deserialize<InstallLedger>(File.ReadAllText(file));
        if(ledger is null||ledger.Version!=1||!string.Equals(ledger.ExePath,game.ExePath,StringComparison.OrdinalIgnoreCase)||ledger.Backend!=game.Backend||ledger.Files is null||ledger.Files.Length>512)throw new IOException("安装记录与当前游戏不一致。");
        return ledger;
    }
    private static string SafePath(string root,string relative)
    {
        if(Path.IsPathRooted(relative)||relative.Contains(':'))throw new IOException("不安全的组件路径。");string full=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
        if(!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("组件路径越界。");
        if(File.Exists(full)&&(File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)throw new IOException("组件目标是文件链接。");
        for(string? current=Path.GetDirectoryName(full);current is not null&&current.Length>=root.Length;current=Path.GetDirectoryName(current))if(Directory.Exists(current)&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("组件目标包含目录链接。");
        return full;
    }
    private static string Hash(string file){using var input=File.OpenRead(file);return Convert.ToHexString(SHA256.HashData(input));}
    private static void EnsureStopped(UnityEmbeddedGame game)
    {
        if(FusionAdapterInstaller.ProcessState(new GameInfo(game.ExePath,"","Unity "+game.Backend,"","",SupportLevel.Candidate,game.DataRoot))!=FusionGameProcessState.Stopped)throw new IOException("请先退出此游戏，再安装或恢复内嵌翻译组件。");
    }
}
