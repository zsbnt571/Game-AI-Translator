using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
namespace ScreenshotTranslationUiTester;

internal static class YukarGameAdapter
{
    internal sealed record Layout(string Player,string Directory,string? Launcher);
    private static readonly Dictionary<string,(string Stamp,Layout? Layout)> Layouts=new(StringComparer.OrdinalIgnoreCase);
    internal static Layout? Detect(string exe)
    {
        try
        {
            exe=Path.GetFullPath(exe);
            string stamp=string.Join("|",new[]{exe,Path.Combine(Path.GetDirectoryName(exe)!,"bakinplayer.exe"),Path.Combine(Path.GetDirectoryName(exe)!,"bakinengine.dll"),Path.Combine(Path.GetDirectoryName(exe)!,"data","bakinplayer.exe"),Path.Combine(Path.GetDirectoryName(exe)!,"data","bakinengine.dll")}.Select(p=>{var f=new FileInfo(p);return f.Exists?f.Length+":"+f.LastWriteTimeUtc.Ticks:"missing";}));
            lock(Layouts){if(Layouts.TryGetValue(exe,out var cached)&&cached.Stamp==stamp)return cached.Layout;}
            Layout? found=DetectCore(exe);lock(Layouts){if(Layouts.Count>512)Layouts.Clear();Layouts[exe]=(stamp,found);}return found;
        }catch(Exception e)when(e is IOException or BadImageFormatException or UnauthorizedAccessException or ArgumentException){return null;}
    }
    private static Layout? DetectCore(string exe)
    {
        try
        {
            string root=Path.GetDirectoryName(exe)!;
            foreach(string sub in new[]{"","data"})
            {
                string dir=GameMakerGameAdapter.InGame(exe,sub.Length==0?"bakinplayer.exe":sub+"/bakinplayer.exe"),player=dir;dir=Path.GetDirectoryName(player)!;
                if(!File.Exists(player)||!File.Exists(Path.Combine(dir,"bakinengine.dll"))||!File.Exists(Path.Combine(dir,"common.dll"))||!File.Exists(Path.Combine(dir,"SharpKmyCore.dll")))continue;
                using var p=AssemblyDefinition.ReadAssembly(player);using var a=AssemblyDefinition.ReadAssembly(Path.Combine(dir,"bakinengine.dll"));
                var main=a.MainModule.GetType("Yukar.Engine.GameMain");
                if(p.MainModule.Architecture!=TargetArchitecture.AMD64||!p.MainModule.AssemblyReferences.Any(r=>r.Name=="mscorlib"&&r.Version.Major==4)||p.EntryPoint?.FullName!="System.Void Yukar.Player.Program::Main()"||main==null||!main.Fields.Any(f=>f.Name=="data"&&f.FieldType.FullName=="Yukar.Common.GameDataManager")||!main.Methods.Any(m=>m.Name=="update"&&m.Parameters.Count==1&&m.Parameters[0].ParameterType.FullName=="System.Single"))continue;
                string? launcher=null;
                if(sub=="data"){
                    using var l=AssemblyDefinition.ReadAssembly(exe);
                    if(l.EntryPoint?.FullName!="System.Void Yukar_Engine_Launcher.Program::Main()")continue;
                    launcher=exe;
                }else if(File.Exists(Path.Combine(dir,"data.rbpack")))continue;
                return new(player,dir,launcher);
            }
        }catch(Exception e)when(e is IOException or BadImageFormatException or UnauthorizedAccessException or ArgumentException){}
        return null;
    }
    internal static string Storage(string exe)=>Path.Combine(AppDataPaths.Root,"yukar-adapters",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(exe).ToUpperInvariant()))));
    private static string Record(string exe)=>Path.Combine(Storage(exe),"install.json");
    internal static string Runner(string exe)=>Path.Combine(Detect(exe)?.Directory??throw new IOException("未找到兼容的 Bakin 桌面运行环境。"),"FusionYukarBridge.exe");
    private static string Payload=>Path.Combine(AppContext.BaseDirectory,"adapters","yukar");
    private static readonly string[] Files=["FusionYukarBridge.exe","FusionYukarBridge.exe.config","0Harmony.dll","Mono.Cecil.dll","MonoMod.RuntimeDetour.dll","MonoMod.Utils.dll"];
    internal static string Fingerprint()=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|",Files.Select(n=>OwnedGameFiles.Digest(Path.Combine(Payload,n)))))));
    internal static bool HasRecord(string exe)=>File.Exists(Record(exe));
    internal static bool IsInstalled(string exe){try{return OwnedGameFiles.Installed(exe,Record(exe),Fingerprint());}catch(IOException){return false;}}
    internal static string InspectionStamp(string exe)=>string.Join("|",new[]{Record(exe),Runner(exe)}.Select(p=>{var f=new FileInfo(p);return f.Exists?f.Length+":"+f.LastWriteTimeUtc.Ticks:"missing";}));
    private static void Stopped(string exe){if(FusionAdapterInstaller.ProcessState(new(exe,"","Bakin","","",SupportLevel.Candidate,null))!=FusionGameProcessState.Stopped)throw new IOException("请先退出游戏，再准备或恢复组件。");}
    internal static void Install(string exe)
    {
        Stopped(exe);if(HasRecord(exe)){if(IsInstalled(exe))return;Restore(exe);}
        var layout=Detect(exe)??throw new EmbeddedCompatibilityException("Bakin 运行环境接口尚未接入。");
        string root=Path.GetDirectoryName(exe)!,prefix=Path.GetRelativePath(root,layout.Directory).Replace('\\','/');prefix=prefix=="."?"":prefix+"/";
        Directory.CreateDirectory(GameMakerGameAdapter.InGame(exe,prefix+".fusion-yukar"));
        var changes=Files.ToDictionary(n=>prefix+(n.StartsWith("FusionYukarBridge.",StringComparison.Ordinal)?n:".fusion-yukar/"+n),n=>File.ReadAllBytes(Path.Combine(Payload,n)));
        OwnedGameFiles.Install(exe,Record(exe),Fingerprint(),changes,new HashSet<string>(changes.Keys),()=>Stopped(exe));
    }
    internal static void Restore(string exe)=>OwnedGameFiles.Restore(exe,Record(exe),()=>Stopped(exe));
}
