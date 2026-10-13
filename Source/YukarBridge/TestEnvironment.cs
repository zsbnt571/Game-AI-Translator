using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using HarmonyLib;

namespace FusionYukar;
internal static class TestEnvironment
{
    internal static bool Active;static string data;
    internal static void Start(string directory)
    {
        if(Environment.GetEnvironmentVariable("FUSION_YUKAR_TEST_MUTE")!="1")return;
        string root=directory;
        if(!File.Exists(Path.Combine(root,".fusion-test-copy")))root=Path.GetDirectoryName(root);
        string marker=Path.Combine(root,".fusion-test-copy");
        if(!File.Exists(marker)||File.ReadAllText(marker).Trim()!="FusionTranslationTestCopy/1")throw new IOException("Silent test marker missing.");
        data=Path.Combine(root,".fusion-test-data");
        for(string p=data;p!=null;p=Path.GetDirectoryName(p))if((Directory.Exists(p)||File.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("Test profile contains a link.");
        Directory.CreateDirectory(data);Active=true;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Program.Stage("silent-profile-start");
        var h=new Harmony("local.fusion.yukar.silent-test.143");
        foreach(var m in typeof(Environment).GetMethods(BindingFlags.Public|BindingFlags.Static).Where(m=>m.Name=="GetFolderPath"&&m.GetParameters().Length>0&&m.GetParameters()[0].ParameterType==typeof(Environment.SpecialFolder)))
            h.Patch(m,new HarmonyMethod(typeof(TestEnvironment).GetMethod(nameof(Folder),BindingFlags.NonPublic|BindingFlags.Static)));
        // Background diagnostics abort on a modal message. Never auto-confirm a
        // choice, and never allow MessageBox to play a system notification sound.
        foreach(var m in typeof(MessageBox).GetMethods(BindingFlags.Public|BindingFlags.Static).Where(m=>m.Name=="Show"))
            h.Patch(m,new HarmonyMethod(typeof(TestEnvironment).GetMethod(nameof(Message),BindingFlags.NonPublic|BindingFlags.Static)));
        h.Patch(typeof(Process).GetMethod("Start",BindingFlags.Public|BindingFlags.Instance,null,Type.EmptyTypes,null),new HarmonyMethod(typeof(TestEnvironment).GetMethod(nameof(ChildProcess),BindingFlags.NonPublic|BindingFlags.Static)));
        Program.Stage("silent-profile-patched");
    }
    static bool Folder(Environment.SpecialFolder __0,ref string __result)
    {
        if(__0!=Environment.SpecialFolder.ApplicationData&&__0!=Environment.SpecialFolder.LocalApplicationData&&__0!=Environment.SpecialFolder.CommonApplicationData&&__0!=Environment.SpecialFolder.MyDocuments)return true;
        __result=Path.Combine(data,__0.ToString());Directory.CreateDirectory(__result);return false;
    }
    static bool Message(object[] __args)
    {throw new InvalidOperationException("Background test stopped at game message: "+string.Join(" | ",__args.OfType<string>()));}
    static bool ChildProcess(Process __instance)
    {
        var info=__instance.StartInfo;
        if(!info.UseShellExecute&&string.Equals(Path.GetFullPath(info.FileName),Assembly.GetExecutingAssembly().Location,StringComparison.OrdinalIgnoreCase)&&info.Arguments.StartsWith("--player ",StringComparison.Ordinal))return true;
        // ShellExecute cleanup/URL helpers could be brokered onto the user's
        // desktop. A hidden test stops instead of launching those helpers.
        throw new InvalidOperationException("Background test stopped before an external helper launch.");
    }
}
