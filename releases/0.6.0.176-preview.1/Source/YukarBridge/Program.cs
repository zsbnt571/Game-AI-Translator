using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FusionYukar;
internal static class Program
{
    [DllImport("kernel32",CharSet=CharSet.Unicode)] static extern bool SetDllDirectory(string path);
    [STAThread] static int Main(string[] args)
    {
        string dir=Environment.GetEnvironmentVariable("FUSION_YUKAR_SESSION");
        try
        {
            if(args.Length<2||string.IsNullOrEmpty(dir))return 2;
            string mode=args[0],player=Path.GetFullPath(args[1]),baseDir=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if(!string.Equals(Path.GetDirectoryName(player),baseDir,StringComparison.OrdinalIgnoreCase)||!File.Exists(player))return 3;
            Directory.SetCurrentDirectory(baseDir);SetDllDirectory(baseDir);
            AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{
                string name=new AssemblyName(e.Name).Name;
                if(name.IndexOfAny(Path.GetInvalidFileNameChars())>=0)return null;
                string owned=Path.Combine(baseDir,".fusion-yukar",name+".dll"),game=Path.Combine(baseDir,name+".dll");
                return File.Exists(owned)?Assembly.LoadFrom(owned):File.Exists(game)?Assembly.LoadFrom(game):null;
            };
            TestEnvironment.Start(baseDir);
            Stage("profile-ready");
            if(mode=="--launcher"&&args.Length==3)return Launcher.Run(Path.GetFullPath(args[2]),player);
            if(mode!="--player")return 4;
            return Start(player);
        }
        catch(Exception e){try{File.WriteAllText(Path.Combine(dir,"failure.txt"),e.ToString());}catch{}return 1;}
    }
    internal static void Stage(string name){try{File.AppendAllText(Path.Combine(Environment.GetEnvironmentVariable("FUSION_YUKAR_SESSION"),"stages.txt"),DateTime.UtcNow.ToString("o")+" "+System.Diagnostics.Process.GetCurrentProcess().Id+" "+name+Environment.NewLine);}catch{}}
    [MethodImpl(MethodImplOptions.NoInlining)] static int Start(string player)
    {
        Runtime.Start(player);
        Assembly target=Assembly.LoadFrom(player);var entry=target.EntryPoint;
        if(entry==null||entry.GetParameters().Length!=0)throw new InvalidOperationException("Unsupported player entry point.");
        entry.Invoke(null,null);return 0;
    }
}
