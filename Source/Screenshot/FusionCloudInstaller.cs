using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
namespace ScreenshotTranslationUiTester;
internal enum FusionGameProcessState { Stopped, Running, Unknown }
internal sealed partial class FusionAdapterInstaller
{
    internal bool HasRecord(GameInfo game)=>EmbeddedGameAdapters.Handles(game)?EmbeddedGameAdapters.HasRecord(game):game.AdapterId=="renpy"&&RenpyGameAdapter.HasRecord(game.ExePath);
    internal bool IsInstalled(GameInfo game)=>EmbeddedGameAdapters.Handles(game)?EmbeddedGameAdapters.IsInstalled(game):game.AdapterId=="renpy"&&RenpyGameAdapter.IsInstalled(game.ExePath);
    internal void Install(GameInfo game,ApiSettings settings,string key)
    {
        if(EmbeddedGameAdapters.Handles(game))EmbeddedGameAdapters.Install(game);
        else if(game.AdapterId=="renpy")RenpyGameAdapter.Install(game.ExePath);
        else throw new InvalidOperationException("此游戏尚无可用的内嵌翻译接入。");
    }
    internal void Restore(GameInfo game)
    {
        if(EmbeddedGameAdapters.Handles(game))EmbeddedGameAdapters.Restore(game);
        else if(game.AdapterId=="renpy")RenpyGameAdapter.Restore(game.ExePath);
        else throw new InvalidOperationException("此游戏没有新版翻译组件的安装记录。");
    }
    [DllImport("kernel32.dll",SetLastError=true)]static extern IntPtr OpenProcess(uint access,bool inherit,int id);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder name,ref uint size);
    [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
    internal static FusionGameProcessState ProcessState(GameInfo game)
    {
        bool unknown=false;
        var paths=GameRuntimeExecutables.Resolve(game.ExePath);
        foreach(var process in paths.Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(name=>Process.GetProcessesByName(name!)))using(process)
        {
            var handle=OpenProcess(0x1000,false,process.Id);
            if(handle==IntPtr.Zero){try{if(!process.HasExited)unknown=true;}catch{unknown=true;}continue;}
            try{var name=new StringBuilder(32768);uint size=32768;if(!QueryFullProcessImageName(handle,0,name,ref size)){unknown=true;continue;}
                if(paths.Contains(GameLaunchPathIdentity.Canonical(name.ToString()),StringComparer.OrdinalIgnoreCase))return FusionGameProcessState.Running;
            }finally{CloseHandle(handle);}
        }
        return unknown?FusionGameProcessState.Unknown:FusionGameProcessState.Stopped;
    }
    internal static bool IsRunning(GameInfo g)=>ProcessState(g)!=FusionGameProcessState.Stopped;
}
