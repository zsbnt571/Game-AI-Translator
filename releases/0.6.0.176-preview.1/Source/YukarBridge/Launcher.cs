using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using HarmonyLib;

namespace FusionYukar;

// Let the game's own launcher prepare its resources and command line. Only the
// verified player process is redirected; archive decoding and checks stay with
// the original launcher. The supervisor lives until that child has exited.
internal static class Launcher
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static string launcher,player;static Assembly original;static Process child;
    internal static int Run(string path,string playerPath)
    {
        launcher=path;player=playerPath;
        if(!string.Equals(Path.GetDirectoryName(player),Path.Combine(Path.GetDirectoryName(launcher),"data"),StringComparison.OrdinalIgnoreCase))throw new IOException("Unsupported packaged player layout.");
        original=Assembly.LoadFrom(launcher);
        Program.Stage("launcher-loaded");
        var entry=original.EntryPoint;
        if(entry==null||entry.DeclaringType.FullName!="Yukar_Engine_Launcher.Program"||entry.GetParameters().Length!=0)throw new IOException("Unsupported game launcher.");
        var h=new Harmony("local.fusion.yukar.launcher.143");
        void Patch(MethodInfo method,string name){if(method==null)throw new IOException("Launcher method missing.");h.Patch(method,new HarmonyMethod(typeof(Launcher).GetMethod(name,All)));}
        Patch(typeof(Application).GetProperty("StartupPath").GetGetMethod(),nameof(StartupPath));
        Patch(typeof(Application).GetProperty("ExecutablePath").GetGetMethod(),nameof(ExecutablePath));
        Patch(typeof(Assembly).GetMethod("GetEntryAssembly"),nameof(EntryAssembly));
        Patch(typeof(Process).GetMethod("Start",new[]{typeof(ProcessStartInfo)}),nameof(StartPlayer));
        if(TestEnvironment.Active)Patch(original.GetType("Yukar_Engine_Launcher.Form1")?.GetMethod("doExtractRuntime",All),nameof(Extraction));
        Program.Stage("launcher-hooks-ready");
        Directory.SetCurrentDirectory(Path.GetDirectoryName(launcher));
        entry.Invoke(null,null);
        Program.Stage("launcher-returned");
        if(child==null)throw new IOException("The original launcher did not start a player.");
        using(child){child.WaitForExit();return child.ExitCode;}
    }
    static bool StartupPath(ref string __result){__result=Path.GetDirectoryName(launcher);return false;}
    static bool ExecutablePath(ref string __result){__result=launcher;return false;}
    static bool EntryAssembly(ref Assembly __result){__result=original;return false;}
    static void Extraction(object __instance){Program.Stage("extract-temp="+__instance.GetType().GetField("tmpPath",All)?.GetValue(__instance));}
    static bool StartPlayer(ProcessStartInfo __0,ref Process __result)
    {
        // Leave unrelated helpers untouched, including any original validation.
        if(!string.Equals(Path.GetFullPath(__0.FileName),player,StringComparison.OrdinalIgnoreCase))return true;
        Program.Stage("player-start-requested");
        if(child!=null)throw new IOException("Duplicate player start.");
        var info=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(player),CreateNoWindow=true};
        info.Arguments="--player "+Quote(player)+" "+__0.Arguments;
        // The child inherits the test desktop/job and isolated environment.
        info.EnvironmentVariables["FUSION_YUKAR_SUPERVISOR"]=Process.GetCurrentProcess().Id.ToString();
        child=new Process{StartInfo=info};if(!child.Start())throw new IOException("Player could not start.");
        Program.Stage("player-started-"+child.Id);
        __result=child;return false;
    }
    static string Quote(string value)
    {
        var b=new StringBuilder("\"");int slashes=0;
        foreach(char c in value){if(c=='\\'){slashes++;continue;}if(c=='\"'){b.Append('\\',slashes*2+1);b.Append(c);}else{b.Append('\\',slashes);b.Append(c);}slashes=0;}
        b.Append('\\',slashes*2);return b.Append('"').ToString();
    }
}
