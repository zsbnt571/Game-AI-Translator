using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenshotTranslationUiTester;

// Injection targets are independent of cover/window discovery. A matching title,
// window size or a child under the same directory never grants process access.
internal static class GameSpeedRuntimeTargets
{
    private static readonly object sync=new();
    private static readonly Dictionary<string,GameSpeedProcess> launched=new(StringComparer.OrdinalIgnoreCase);
    internal static void NoteLaunch(string exe,int pid)
    {
        var identity=GameSpeedProcessDiscovery.Read(pid);
        // Keep a failed/expired explicit selection as a tombstone. Falling back to
        // an already running same-image instance would silently change the target.
        if(identity is null||!AllowedPaths(exe).Contains(identity.ExePath,StringComparer.OrdinalIgnoreCase))identity=new(pid,0,"",false);
        lock(sync)launched[GameLaunchPathIdentity.Canonical(exe)]=identity;
    }
    internal static GameSpeedProcess? KnownLaunch(string exe)
    {lock(sync)return launched.GetValueOrDefault(GameLaunchPathIdentity.Canonical(exe));}
    internal static string[] AllowedPaths(string exe)=>GameRuntimeExecutables.Resolve(exe)
        .Select(GameLaunchPathIdentity.Canonical).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static GameSpeedProcess SelectRoot(IReadOnlyCollection<string> allowed,
        IReadOnlyCollection<GameSpeedProcess> candidates,IReadOnlyDictionary<int,int> parents,
        IReadOnlyDictionary<int,string> types,GameSpeedProcess? explicitRoot,bool pinned)
    {
        var exact=candidates.Where(p=>allowed.Contains(p.ExePath,StringComparer.OrdinalIgnoreCase)).Distinct().ToArray();
        bool IsRoot(GameSpeedProcess p)=>!types.TryGetValue(p.Pid,out string? type)||type.Length==0;
        bool DescendsFrom(GameSpeedProcess child,GameSpeedProcess ancestor)
        {
            var seen=new HashSet<int>();
            while(seen.Add(child.Pid)&&seen.Count<=16&&parents.TryGetValue(child.Pid,out int parent))
            {
                var live=exact.FirstOrDefault(p=>p.Pid==parent);
                if(live is null||live.Created>child.Created)return false;
                if(live==ancestor)return true;
                child=live;
            }
            return false;
        }
        if(explicitRoot is not null)
        {
            if(!exact.Contains(explicitRoot))throw new IOException("原游戏进程已退出或变化；未接入其他实例。请从 Fusion 重新启动目标游戏。");
            if(pinned)
            {
                if(!IsRoot(explicitRoot))throw new IOException("无法确认已选择的游戏主进程。");
                return explicitRoot;
            }
            // Only engine-resolved exact runtime paths may replace a live launcher.
            // Their complete parent chain must consist of validated allowed images.
            var runtimes=exact.Where(p=>IsRoot(p)&&!SameImage(p,explicitRoot)&&DescendsFrom(p,explicitRoot)).ToArray();
            if(runtimes.Length==1)return runtimes[0];
            if(runtimes.Length>1)throw new IOException("此游戏有多个运行实例，无法确定速度目标。请直接选择实际游戏 EXE。");
            if(IsRoot(explicitRoot))return explicitRoot;
            var roots=exact.Where(p=>IsRoot(p)&&SameImage(p,explicitRoot)&&DescendsFrom(explicitRoot,p)).ToArray();
            if(roots.Length==1)return roots[0];
            throw new IOException("无法确认所选游戏实例的主进程；未接入其他实例。");
        }
        var matches=exact.Where(IsRoot).ToArray();
        if(matches.Length==1)return matches[0];
        if(matches.Length>1)throw new IOException("检测到多个游戏实例，无法确定速度目标。请从 Fusion 启动要调速的实例，或直接选择实际游戏 EXE。");
        throw new IOException("未找到对应游戏进程，请先启动游戏；启动器已退出时请直接选择实际游戏 EXE。");
    }
    internal static bool SameImage(GameSpeedProcess a,GameSpeedProcess b)=>string.Equals(a.ExePath,b.ExePath,StringComparison.OrdinalIgnoreCase);
}

internal sealed class WindowsGameSpeedPlatform : IGameSpeedPlatform
{
    public Task<GameSpeedTargets> DiscoverAsync(GameInfo game,CancellationToken token,GameSpeedProcess? boundRoot=null)=>Task.Run(()=>
    {
        token.ThrowIfCancellationRequested();
        var allowed=GameSpeedRuntimeTargets.AllowedPaths(game.ExePath);
        var family=GameSpeedProcessDiscovery.Snapshot();
        var cache=new Dictionary<int,GameSpeedProcess?>();
        var commands=new Dictionary<int,string>();
        GameSpeedProcess? Read(int pid)
        {
            if(!cache.TryGetValue(pid,out var value))cache[pid]=value=GameSpeedProcessDiscovery.Read(pid);
            return value;
        }
        string Type(GameSpeedProcess p)
        {
            if(!commands.TryGetValue(p.Pid,out var value))commands[p.Pid]=value=GameSpeedProcessDiscovery.ProcessType(GameSpeedProcessDiscovery.CommandLine(p));
            return value;
        }
        var candidates=new List<GameSpeedProcess>();
        foreach(var name in allowed.Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach(var process in Process.GetProcessesByName(name!))
                using(process)
                {
                    token.ThrowIfCancellationRequested();
                    if(Read(process.Id) is {} identity&&allowed.Contains(identity.ExePath,StringComparer.OrdinalIgnoreCase))candidates.Add(identity);
                }
        bool chromium=candidates.Any(p=>GameSpeedProcessDiscovery.IsChromium(game,p));
        if(chromium)foreach(var candidate in candidates)_=Type(candidate);
        var root=GameSpeedRuntimeTargets.SelectRoot(allowed,candidates,family,commands,
            boundRoot??GameSpeedRuntimeTargets.KnownLaunch(game.ExePath),boundRoot is not null);
        if(!IsAlive(root))throw new IOException("游戏进程已变化，请重新设置速度。");
        if(!chromium)return new GameSpeedTargets(root,[root],false);
        var all=new List<GameSpeedProcess>{root};var reached=new HashSet<int>{root.Pid};
        for(int depth=0;depth<8;depth++)
        {
            bool changed=false;
            foreach(var pair in family)
            {
                if(reached.Contains(pair.Key)||!reached.Contains(pair.Value))continue;
                if(Read(pair.Value) is not {} parent||Read(pair.Key) is not {} child||child.Created<parent.Created||!GameSpeedRuntimeTargets.SameImage(root,child))continue;
                // A same-image child with no Chromium process type is another
                // application/browser instance, not a renderer in this session.
                string type=Type(child);if(type.Length==0)continue;
                reached.Add(child.Pid);changed=true;
                if(type=="renderer")all.Add(child);
                if(reached.Count>256||all.Count>64)throw new IOException("游戏渲染进程数量超出速度控制范围。");
            }
            if(!changed)break;
        }
        if(all.Count<2)throw new IOException("尚未确认网页游戏的渲染进程，请等游戏画面出现后重试。");
        if(!IsAlive(root))throw new IOException("游戏进程已变化，请重新设置速度。");
        return new GameSpeedTargets(root,all,true);
    },token);
    public bool IsAlive(GameSpeedProcess process)=>GameSpeedProcessDiscovery.Read(process.Pid)==process;
    public Task<IGameSpeedChannel> AttachAsync(GameSpeedProcess process,CancellationToken token)=>GameSpeedNativeChannel.AttachAsync(process,token);
}

internal static class GameSpeedProcessDiscovery
{
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct ProcessEntry
    {
        public uint Size,Usage,Id;public UIntPtr Heap;public uint Module,Threads,ParentId;public int Priority;public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string Name;
    }
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length,MaximumLength;public IntPtr Buffer; }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetProcessTimes(IntPtr handle,out long creation,out long exit,out long kernel,out long user);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool QueryFullProcessImageName(IntPtr handle,uint flags,StringBuilder text,ref uint size);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool IsWow64Process2(IntPtr handle,out ushort processMachine,out ushort nativeMachine);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern bool Process32First(IntPtr snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern bool Process32Next(IntPtr snapshot,ref ProcessEntry entry);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process,int informationClass,IntPtr information,int length,out int needed);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr CommandLineToArgvW(string text,out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr allocation);

    internal static GameSpeedProcess? Read(int pid)
    {
        if(pid<=4||pid==Environment.ProcessId)return null;
        var handle=OpenProcess(0x1000,false,pid);if(handle==IntPtr.Zero)return null;
        try
        {
            var path=new StringBuilder(32768);uint size=(uint)path.Capacity;
            if(!QueryFullProcessImageName(handle,0,path,ref size)||!GetProcessTimes(handle,out long created,out long exited,out _,out _)||exited!=0||
                !IsWow64Process2(handle,out ushort machine,out ushort native))return null;
            ushort effective=machine==0?native:machine;if(effective is not (0x014c or 0x8664))return null;
            return new(pid,(ulong)created,GameLaunchPathIdentity.Canonical(path.ToString()),effective==0x8664);
        }
        catch(Exception e)when(e is ArgumentException or IOException or EntryPointNotFoundException){return null;}
        finally{CloseHandle(handle);}
    }
    internal static Dictionary<int,int> Snapshot()
    {
        var result=new Dictionary<int,int>();var snapshot=CreateToolhelp32Snapshot(2,0);
        if(snapshot==new IntPtr(-1))throw new Win32Exception();
        try
        {
            var entry=new ProcessEntry{Size=(uint)Marshal.SizeOf<ProcessEntry>(),Name=""};
            if(Process32First(snapshot,ref entry))do
            {result[(int)entry.Id]=(int)entry.ParentId;entry.Size=(uint)Marshal.SizeOf<ProcessEntry>();}while(Process32Next(snapshot,ref entry));
            return result;
        }
        finally{CloseHandle(snapshot);}
    }
    internal static bool IsChromium(GameInfo game,GameSpeedProcess root)=>
        game.AdapterId is "rpg-maker-mv" or "rpg-maker-mz" || game.AdapterId?.StartsWith("web-",StringComparison.Ordinal)==true||
        new[]{"Cocos","Electron","NW.js","Construct"}.Any(name=>game.Engine.Contains(name,StringComparison.OrdinalIgnoreCase))||
        File.Exists(Path.Combine(Path.GetDirectoryName(root.ExePath)!,"nw.dll"));
    internal static string CommandLine(GameSpeedProcess process)
    {
        var handle=OpenProcess(0x1000,false,process.Pid);if(handle==IntPtr.Zero)throw new IOException("无法读取网页游戏的进程身份。");
        IntPtr allocation=IntPtr.Zero;
        try
        {
            if(!GetProcessTimes(handle,out long created,out long exit,out _,out _)||(ulong)created!=process.Created||exit!=0)
                throw new IOException("游戏进程已变化，请重新设置速度。");
            _=NtQueryInformationProcess(handle,60,IntPtr.Zero,0,out int needed);
            if(needed<Marshal.SizeOf<UnicodeString>()||needed>131072)throw new IOException("无法确认网页游戏的渲染进程类型。");
            allocation=Marshal.AllocHGlobal(needed);
            if(NtQueryInformationProcess(handle,60,allocation,needed,out _)!=0)throw new IOException("无法确认网页游戏的渲染进程类型。");
            var text=Marshal.PtrToStructure<UnicodeString>(allocation);
            long offset=text.Buffer.ToInt64()-allocation.ToInt64();
            if(text.Length%2!=0||offset<0||offset+text.Length>needed)throw new IOException("网页游戏进程信息无效。");
            return Marshal.PtrToStringUni(text.Buffer,text.Length/2)??"";
        }
        finally{if(allocation!=IntPtr.Zero)Marshal.FreeHGlobal(allocation);CloseHandle(handle);}
    }
    internal static string ProcessType(string commandLine)
    {
        var argv=CommandLineToArgvW(commandLine,out int count);if(argv==IntPtr.Zero)throw new IOException("无法确认网页游戏的进程参数。");
        try
        {
            for(int i=1;i<count;i++)
            {
                string arg=Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv,i*IntPtr.Size))??"";
                if(arg.StartsWith("--type=",StringComparison.Ordinal))return arg[7..];
                if(arg=="--type"&&i+1<count)return Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv,(i+1)*IntPtr.Size))??"";
            }
            return "";
        }
        finally{LocalFree(argv);}
    }
}
