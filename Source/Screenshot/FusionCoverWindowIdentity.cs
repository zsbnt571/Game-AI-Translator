using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
namespace ScreenshotTranslationUiTester;

internal sealed record CoverTarget(IntPtr Window,int ProcessId,string ExePath,long StartedUtcTicks);
internal static class CoverWindowIdentity
{
    private delegate bool EnumWindow(IntPtr hwnd,IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] private struct Rect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct ProcessEntry
    {
        public uint Size,Usage,Id;public UIntPtr Heap;public uint Module,Threads,ParentId;public int Priority;public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string Name;
    }
    [DllImport("user32.dll")]private static extern bool EnumWindows(EnumWindow cb,IntPtr param);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")]private static extern bool GetClientRect(IntPtr hwnd,out Rect rect);
    [DllImport("kernel32.dll")]private static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern bool QueryFullProcessImageName(IntPtr p,uint flags,StringBuilder path,ref uint size);
    [DllImport("kernel32.dll")]private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")]private static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern bool Process32First(IntPtr snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern bool Process32Next(IntPtr snapshot,ref ProcessEntry entry);
    [DllImport("dwmapi.dll")]private static extern int DwmGetWindowAttribute(IntPtr hwnd,int attribute,out int value,int size);
    private sealed record Identity(int Pid,string Path,long Started);
    private static readonly object gate=new();
    private static readonly Dictionary<string,List<Identity>> known=new(StringComparer.OrdinalIgnoreCase);
    private static Identity? Read(int pid)
    {
        try{
            var h=OpenProcess(0x1000,false,(uint)pid);if(h==IntPtr.Zero)return null;
            try{var path=new StringBuilder(32768);uint size=(uint)path.Capacity;if(!QueryFullProcessImageName(h,0,path,ref size))return null;
                using var p=Process.GetProcessById(pid);return new(pid,Path.GetFullPath(path.ToString()),p.StartTime.ToUniversalTime().Ticks);}
            finally{CloseHandle(h);}
        }catch{return null;}
    }
    internal static void NoteLaunch(string exe,int pid)
    {
        var item=Read(pid);if(item is null||!StringComparer.OrdinalIgnoreCase.Equals(item.Path,exe))return;
        lock(gate){known.Clear();known[exe]=[item];}
    }
    internal static bool Matches(CoverTarget t)
    {
        GetWindowThreadProcessId(t.Window,out var pid);
        var p=Read(t.ProcessId);
        return pid==t.ProcessId&&p is not null&&p.Started==t.StartedUtcTicks&&StringComparer.OrdinalIgnoreCase.Equals(p.Path,t.ExePath)
            &&IsWindowVisible(t.Window)&&!IsIconic(t.Window)&&GetClientRect(t.Window,out var r)&&r.Right>r.Left&&r.Bottom>r.Top
            &&(DwmGetWindowAttribute(t.Window,14,out var cloaked,4)!=0||cloaked==0);
    }
    internal static (CoverTarget? Target,string Code,string Message) Find(string exe)
    {
        lock(gate)
        {
            if(!known.TryGetValue(exe,out var identities)){known.Clear();known[exe]=identities=[];}
            identities.RemoveAll(i=>Read(i.Pid) is not { } live||live.Started!=i.Started||!StringComparer.OrdinalIgnoreCase.Equals(live.Path,i.Path));
            foreach(var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
                using(p){var i=Read(p.Id);if(i is not null&&StringComparer.OrdinalIgnoreCase.Equals(i.Path,exe)&&!identities.Any(x=>x.Pid==i.Pid))identities.Add(i);}
            // Follow live, verified parents into children within this game's directory. PID alone is never an identity.
            var pairs=new List<(uint Id,uint Parent)>();var snap=CreateToolhelp32Snapshot(2,0);
            if(snap!=new IntPtr(-1)){
                try{var e=new ProcessEntry{Size=(uint)Marshal.SizeOf<ProcessEntry>(),Name=""};
                    if(Process32First(snap,ref e))do{pairs.Add((e.Id,e.ParentId));e.Size=(uint)Marshal.SizeOf<ProcessEntry>();}while(Process32Next(snap,ref e));}
                finally{CloseHandle(snap);}
            }
            var directory=Path.GetDirectoryName(exe)!.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            for(var depth=0;depth<4&&identities.Count<32;depth++)
                foreach(var (id,parent) in pairs){
                    if(identities.Any(x=>x.Pid==id)||identities.Count>=32)continue;
                    var ancestor=identities.FirstOrDefault(x=>x.Pid==parent);if(ancestor is null)continue;
                    var parentNow=Read(ancestor.Pid);if(parentNow is null||parentNow.Started!=ancestor.Started||!StringComparer.OrdinalIgnoreCase.Equals(parentNow.Path,ancestor.Path))continue;
                    var child=Read((int)id);
                    if(child is not null&&child.Started>=ancestor.Started&&child.Path.StartsWith(directory,StringComparison.OrdinalIgnoreCase))
                        identities.Add(child);
                }
            if(identities.Count==0)return(null,"process-not-found","未找到对应游戏进程；启动器已退出时请直接选择实际游戏 EXE");
            var windows=new List<(CoverTarget Target,long Area)>();var minimized=false;var invalidSize=false;
            EnumWindows((hwnd,_)=>{
                GetWindowThreadProcessId(hwnd,out var pid);var i=identities.FirstOrDefault(x=>x.Pid==pid);
                if(i is null||!IsWindowVisible(hwnd))return true;
                if(IsIconic(hwnd)){minimized=true;return true;}
                if(DwmGetWindowAttribute(hwnd,14,out var cloak,4)==0&&cloak!=0)return true;
                if(!GetClientRect(hwnd,out var r))return true;
                var w=r.Right-r.Left;var h=r.Bottom-r.Top;
                if(w<100||h<60||w>8192||h>8192||(long)w*h>16_777_216){invalidSize=true;return true;}
                windows.Add((new(hwnd,i.Pid,i.Path,i.Started),(long)w*h));return true;
            },IntPtr.Zero);
            // More than one child executable with windows is ambiguous, never choose an unrelated launcher child by area.
            var children=windows.Where(x=>!StringComparer.OrdinalIgnoreCase.Equals(x.Target.ExePath,exe)).ToArray();
            if(children.Select(x=>x.Target.ExePath).Distinct(StringComparer.OrdinalIgnoreCase).Count()>1)
                return(null,"ambiguous-child","启动器有多个子窗口，无法确认游戏；请直接选择实际游戏 EXE");
            var chosen=(children.Length>0?children:windows.ToArray()).OrderByDescending(x=>x.Area).FirstOrDefault().Target;
            if(chosen is not null)return(chosen,"window-ready","");
            if(minimized)return(null,"minimized","游戏窗口已最小化，请恢复后更新");
            if(invalidSize)return(null,"invalid-size","游戏窗口尚无可用尺寸，或超过取图尺寸上限");
            return(null,"window-not-found","未找到对应游戏窗口，请等窗口出现后更新");
        }
    }
}
