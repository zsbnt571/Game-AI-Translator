using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
namespace ScreenshotTranslationUiTester;

internal static class LooseElectronGameAdapter
{
    private static string Record(string exe)=>Path.Combine(WebGameAdapter.Storage(exe),"electron-loose","install.json");
    internal static bool HasRecord(string exe)=>File.Exists(Record(exe));
    private static void Stopped(string exe){if(FusionAdapterInstaller.ProcessState(new(exe,"","Electron","","",SupportLevel.Candidate,null))!=FusionGameProcessState.Stopped)throw new IOException("请先退出游戏，再准备或恢复翻译。");}
    internal static bool Candidate(string exe,WebGameArchive archive,string main)
    {
        if(!main.EndsWith(".js",StringComparison.OrdinalIgnoreCase)||!archive.Contains("index.html"))return false;
        string code=archive.Text(main,1024*1024);
        // Support Node-enabled renderer configurations only. Do not lower sandbox,
        // context isolation or integrity settings in order to gain access.
        bool node=Regex.IsMatch(code,@"\bnodeIntegration\s*:\s*true\b"),isolated=Regex.IsMatch(code,@"\bcontextIsolation\s*:\s*true\b"),sandbox=Regex.IsMatch(code,@"\bsandbox\s*:\s*true\b");
        bool explicitLegacy=Regex.IsMatch(code,@"\bcontextIsolation\s*:\s*false\b");
        string versionFile=WebGameAdapter.InGame(exe,"version");
        int major=File.Exists(versionFile)&&new FileInfo(versionFile).Length<100&&Version.TryParse(File.ReadAllText(versionFile).Trim(),out var version)?version.Major:0;
        return node&&!isolated&&!sandbox&&(explicitLegacy||major is >=1 and <=11)&&Regex.IsMatch(code,@"(?:loadURL|loadFile)\s*\([^\r\n]*index\.html");
    }
    internal static bool IsInstalled(string exe)=>OwnedGameFiles.Installed(exe,Record(exe),WebGameAdapter.PayloadHash());
    internal static string Stamp(string exe)=>string.Join("|",new[]{Record(exe),WebGameAdapter.InGame(exe,"resources/app/main.js"),WebGameAdapter.InGame(exe,"resources/app/index.html")}.Select(p=>{var f=new FileInfo(p);return f.Exists?f.Length+":"+f.LastWriteTimeUtc.Ticks:"missing";}));
    internal static void Install(string exe,WebGame game)
    {
        if(HasRecord(exe)){if(IsInstalled(exe))return;Restore(exe);}
        using var archive=new WebGameArchive(game.Archive);
        if(game.Host!="Electron"||!Directory.Exists(game.Archive)||!Candidate(exe,archive,game.Main))throw new IOException("当前 Electron 启动配置尚未接入。");
        string prefix=Path.GetRelativePath(Path.GetDirectoryName(exe)!,game.Archive).Replace('\\','/')+"/",mainDir=game.Main[..(game.Main.LastIndexOf('/')+1)];
        var changes=new Dictionary<string,byte[]>(StringComparer.Ordinal);
        void Add(string name,string text)=>changes.Add(prefix+name,Encoding.UTF8.GetBytes(text));
        string html=archive.Text("index.html",1024*1024);var head=Regex.Match(html,@"<head(?:\s[^>]*)?>",RegexOptions.IgnoreCase);
        if(!head.Success)throw new IOException("游戏页面入口无效。");
        Add("index.html",html.Insert(head.Index+head.Length,"\n<script src=\".fusion-web-start.js\"></script>\n"));
        Add(game.Main,"require('./.fusion-web-main.js');\n"+archive.Text(game.Main,1024*1024));
        Add(".fusion-web-start.js",WebGameAdapter.Startup(game));
        Add(mainDir+".fusion-web-main.js",WebGameAdapter.Resource("WebGameMain.js"));
        OwnedGameFiles.Install(exe,Record(exe),WebGameAdapter.PayloadHash(),changes,new HashSet<string>{prefix+".fusion-web-start.js",prefix+mainDir+".fusion-web-main.js"},()=>Stopped(exe));
    }
    internal static void Restore(string exe)=>OwnedGameFiles.Restore(exe,Record(exe),()=>Stopped(exe));
}
