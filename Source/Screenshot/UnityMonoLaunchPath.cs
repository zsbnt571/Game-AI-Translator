using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ScreenshotTranslationUiTester;

// Older Unity Mono builds can give in-memory assemblies an ANSI CodeBase derived
// from the executable directory. Harmony then fails before any plugin is loaded.
// A process-local launch alias retains the SAME directory, including relative
// saves and Mods. No game files, loader settings or machine encoding are changed.
internal sealed class UnityMonoLaunchPath : IDisposable
{
    private readonly string folder,target;
    internal string DirectoryPath { get; }
    internal string ExecutablePath { get; }
    private bool tracked,cleaned;
    private readonly object gate=new();
    private UnityMonoLaunchPath(string executable,string baseDirectory)
    {
        target=Path.GetDirectoryName(Path.GetFullPath(executable))!;
        RequirePlainParents(target);
        Directory.CreateDirectory(baseDirectory);RequirePlainParents(baseDirectory);
        folder=Path.Combine(baseDirectory,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        DirectoryPath=Path.Combine(folder,"game");ExecutablePath=Path.Combine(DirectoryPath,Path.GetFileName(executable));
        try
        {
            Directory.CreateDirectory(DirectoryPath);CreateJunction(DirectoryPath,target);
            if(!string.Equals(GameLaunchPathIdentity.Canonical(ExecutablePath),Path.GetFullPath(executable),StringComparison.OrdinalIgnoreCase))
                throw new IOException("启动路径校验失败，未启动游戏。");
        }
        catch{Cleanup();throw;}
    }
    internal static UnityMonoLaunchPath? Prepare(string exe)
    {
        var game=UnityEmbeddedAdapter.Detect(exe);
        if(!OperatingSystem.IsWindows()||game?.Backend!="Mono"||!game.Root.Any(c=>c>127)&&game.Root.Length<=150)return null;
        // Keep aliases short as well as ASCII: legacy Mono's managed IO also has
        // MAX_PATH limits. Prefer OS temp outside portable data backups. Explicit
        // host data roots may have redirected TEMP to a long project directory.
        string localTemp=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Temp");
        foreach(string root in new[]{Path.GetTempPath(),localTemp,AppDataPaths.TempRoot}.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string baseDirectory=Path.GetFullPath(Path.Combine(root,"FusionUnityLaunch"));
            if(baseDirectory.Any(c=>c>127)||baseDirectory.Length+38>150)continue;
            try{return new(exe,baseDirectory);}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {Trace.WriteLine("Mono launch alias unavailable: "+ex.GetType().Name);}
        }
        throw new EmbeddedCompatibilityException("此 Mono 加载环境需要英文临时启动路径，但当前无法创建。请将软件放到可写的英文路径后重试；游戏和存档未移动。");
    }
    internal void Track(int pid)
    {
        lock(gate){if(tracked)throw new InvalidOperationException("启动路径已经绑定进程。");tracked=true;}
        // Retain the alias after translation is disconnected: the game and other
        // Mods may still use paths under it. Release only after this process exits.
        _=ReleaseAfterExit(pid);
    }
    private async Task ReleaseAfterExit(int pid)
    {
        try{using var process=Process.GetProcessById(pid);await process.WaitForExitAsync().ConfigureAwait(false);Cleanup();}
        catch(ArgumentException){Cleanup();}
        catch(Exception ex)when(ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {Trace.WriteLine("Mono launch alias exit could not be confirmed: "+ex.GetType().Name);}
    }
    public void Dispose(){lock(gate){if(!tracked)Cleanup();}}
    private void Cleanup()
    {
        lock(gate)
        {
            if(cleaned)return;
            try
            {
                RequirePlainParents(folder);
                if(Directory.Exists(DirectoryPath))
                {
                    var directory=new DirectoryInfo(DirectoryPath);
                    if((directory.Attributes&FileAttributes.ReparsePoint)!=0)
                    {
                        var resolved=directory.ResolveLinkTarget(false);
                        if(resolved is null||!string.Equals(Path.GetFullPath(resolved.FullName),target,StringComparison.OrdinalIgnoreCase))return;
                        Directory.Delete(DirectoryPath,false); // Delete the junction only; NEVER recurse.
                    }
                    else if(!directory.EnumerateFileSystemInfos().Any())Directory.Delete(DirectoryPath,false);
                    else return;
                }
                if(Directory.Exists(folder)&&!Directory.EnumerateFileSystemEntries(folder).Any())Directory.Delete(folder,false);
                cleaned=true;
            }
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException)
            {Trace.WriteLine("Mono launch alias cleanup deferred: "+ex.GetType().Name);}
        }
    }
    internal static void RequirePlainParents(string path)
    {
        for(string? part=Path.GetFullPath(path);part is not null;part=Path.GetDirectoryName(part))
            if(Directory.Exists(part)&&(File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException("启动路径的父目录包含链接。");
    }
    private static void CreateJunction(string link,string target)
    {
        byte[] substitute=Encoding.Unicode.GetBytes(@"\??\"+target),print=Encoding.Unicode.GetBytes(target);
        using var buffer=new MemoryStream();using var writer=new BinaryWriter(buffer,Encoding.Unicode,true);
        writer.Write(0xA0000003u);writer.Write(checked((ushort)(8+substitute.Length+print.Length+4)));writer.Write((ushort)0);
        writer.Write((ushort)0);writer.Write(checked((ushort)substitute.Length));writer.Write(checked((ushort)(substitute.Length+2)));writer.Write(checked((ushort)print.Length));
        writer.Write(substitute);writer.Write((ushort)0);writer.Write(print);writer.Write((ushort)0);writer.Flush();
        byte[] bytes=buffer.ToArray();
        using var handle=CreateFile(link,0x40000000,7,IntPtr.Zero,3,0x02200000,IntPtr.Zero);
        if(handle.IsInvalid||!DeviceIoControl(handle,0x000900A4,bytes,bytes.Length,IntPtr.Zero,0,out _,IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool DeviceIoControl(SafeFileHandle handle,uint code,byte[] input,int inputBytes,IntPtr output,int outputBytes,out int returned,IntPtr overlapped);
}
