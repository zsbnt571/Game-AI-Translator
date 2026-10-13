using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class UnityNativeRuntime
{
    internal const string Folder=".fusion-native-runtime";
    internal const string Dll=Folder+"/FusionUnityNative.dll";
    internal sealed record Descriptor(int Version,string AssemblyHash,string PlayerHash);
    internal static bool Required(UnityEmbeddedGame game)
    {
        if(game.Backend!="IL2CPP"||game.Architecture!="x64")return false;
        string file=Path.Combine(game.DataRoot,"il2cpp_data","Metadata","global-metadata.dat");
        if(!File.Exists(file))return true;using var input=File.OpenRead(file);Span<byte> head=stackalloc byte[4];
        return input.Read(head)!=4||BitConverter.ToUInt32(head)!=0xFAB11BAF;
    }
    internal static Dictionary<string,byte[]> Payload(UnityEmbeddedGame game)
    {
        var map=UnityNativeApiMap.Create(game);
        using var stream=typeof(UnityNativeRuntime).Assembly.GetManifestResourceStream("Fusion.UnityNativeRuntime64.dll")??throw new IOException("缺少原生翻译组件。");
        using var buffer=new MemoryStream();stream.CopyTo(buffer);
        var descriptor=new Descriptor(1,Hash(Path.Combine(game.Root,"GameAssembly.dll")),Hash(Path.Combine(game.Root,"UnityPlayer.dll")));
        return new(StringComparer.OrdinalIgnoreCase){[Dll]=buffer.ToArray(),[Folder+"/api-map.tsv"]=Encoding.UTF8.GetBytes(string.Join("\n",map.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>p.Key+"\t"+p.Value))+"\n"),[Folder+"/runtime.json"]=JsonSerializer.SerializeToUtf8Bytes(descriptor)};
    }
    internal static string Hash(string path){using var input=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(input));}
    internal static void Attach(string exe,int pid)
    {
        var game=UnityEmbeddedAdapter.Detect(exe)??throw new IOException("未找到 Unity 运行时。");
        if(!UnityEmbeddedAdapter.IsNativeRuntimeInstalled(exe))return;
        if(!UnityEmbeddedAdapter.IsInstalled(exe))throw new IOException("原生翻译组件校验失败，请退出游戏后重新应用翻译。");
        string root=Path.Combine(game.Root,Folder),dll=Path.GetFullPath(Path.Combine(game.Root,Dll));
        var descriptor=JsonSerializer.Deserialize<Descriptor>(File.ReadAllText(Path.Combine(root,"runtime.json")))??throw new IOException("原生运行时记录无效。");
        if(descriptor.Version!=1||descriptor.AssemblyHash!=Hash(Path.Combine(game.Root,"GameAssembly.dll"))||descriptor.PlayerHash!=Hash(Path.Combine(game.Root,"UnityPlayer.dll")))throw new IOException("游戏运行时已改变，请退出游戏后重新应用翻译。");
        using var process=Process.GetProcessById(pid);
        if(!string.Equals(process.MainModule?.FileName,game.ExePath,StringComparison.OrdinalIgnoreCase)||process.StartTime.ToUniversalTime()<DateTime.UtcNow.AddMinutes(-2))throw new IOException("游戏进程与本次启动不匹配。");
        var threads=process.Threads.Cast<ProcessThread>().Select(t=>(t.Id,Time:t.StartTime.ToUniversalTime())).OrderBy(t=>t.Time).ToArray();
        if(threads.Length==0||threads.Length>1&&threads[0].Time==threads[1].Time)throw new IOException("未能唯一确认游戏主线程。");
        if(root.Length>=1023)throw new IOException("游戏路径过长。");
        IntPtr handle=OpenProcess(0x0002|0x0008|0x0010|0x0020|0x0400,false,pid);Check(handle!=IntPtr.Zero);
        try
        {
            IntPtr loader=GetProcAddress(GetModuleHandle("kernel32.dll"),"LoadLibraryW");Check(loader!=IntPtr.Zero);
            using var self=Process.GetCurrentProcess();var owner=self.Modules.Cast<ProcessModule>().Single(m=>loader.ToInt64()>=m.BaseAddress.ToInt64()&&loader.ToInt64()<m.BaseAddress.ToInt64()+m.ModuleMemorySize);
            ProcessModule? remoteOwner=null;
            for(int i=0;i<100&&remoteOwner is null;i++){process.Refresh();remoteOwner=process.Modules.Cast<ProcessModule>().FirstOrDefault(m=>m.ModuleName.Equals(owner.ModuleName,StringComparison.OrdinalIgnoreCase));if(remoteOwner is null)Thread.Sleep(20);}
            if(remoteOwner is null)throw new IOException("游戏系统加载器尚未初始化。");
            IntPtr remoteLoader=new(remoteOwner.BaseAddress.ToInt64()+loader.ToInt64()-owner.BaseAddress.ToInt64());
            InvokeRemote(handle,remoteLoader,Encoding.Unicode.GetBytes(dll+'\0'),false);
            process.Refresh();var loaded=process.Modules.Cast<ProcessModule>().SingleOrDefault(m=>string.Equals(m.FileName,dll,StringComparison.OrdinalIgnoreCase))??throw new IOException("原生翻译组件未加载。");
            var image=new UnityNativeApiMap.Image(dll);if(!image.Exports.TryGetValue("FusionStart",out int rva))throw new IOException("原生翻译入口缺失。");
            byte[] bootstrap=new byte[8+1024*2];BitConverter.GetBytes((uint)bootstrap.Length).CopyTo(bootstrap,0);BitConverter.GetBytes((uint)threads[0].Id).CopyTo(bootstrap,4);Encoding.Unicode.GetBytes(root).CopyTo(bootstrap,8);
            InvokeRemote(handle,new IntPtr(loaded.BaseAddress.ToInt64()+rva),bootstrap,true);
        }
        finally{CloseHandle(handle);}
    }
    private static void InvokeRemote(IntPtr process,IntPtr entry,byte[] bytes,bool checkResult)
    {
        IntPtr memory=VirtualAllocEx(process,IntPtr.Zero,(nuint)bytes.Length,0x3000,4);Check(memory!=IntPtr.Zero);bool safeToRelease=true;
        try
        {
            Check(WriteProcessMemory(process,memory,bytes,(nuint)bytes.Length,out nuint written)&&written==(nuint)bytes.Length);
            IntPtr thread=CreateRemoteThread(process,IntPtr.Zero,0,entry,memory,0,IntPtr.Zero);Check(thread!=IntPtr.Zero);
            try{safeToRelease=false;if(WaitForSingleObject(thread,15000)!=0)throw new IOException("原生组件初始化超时，未重试同一进程。");safeToRelease=true;Check(GetExitCodeThread(thread,out uint code));if(checkResult&&code!=0)throw new IOException("原生组件未接受此次启动："+code);}
            finally{CloseHandle(thread);}
        }
        finally{if(safeToRelease)VirtualFreeEx(process,memory,0,0x8000);}
    }
    private static void Check(bool valid){if(!valid)throw new Win32Exception(Marshal.GetLastWin32Error());}
    [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll",CharSet=CharSet.Ansi,ExactSpelling=true,SetLastError=true)]private static extern IntPtr GetProcAddress(IntPtr module,string name);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr VirtualAllocEx(IntPtr process,IntPtr address,nuint size,uint kind,uint protection);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool VirtualFreeEx(IntPtr process,IntPtr address,nuint size,uint kind);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool WriteProcessMemory(IntPtr process,IntPtr address,byte[] buffer,nuint size,out nuint written);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr CreateRemoteThread(IntPtr process,IntPtr attributes,nuint stack,IntPtr start,IntPtr argument,uint flags,IntPtr threadId);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetExitCodeThread(IntPtr thread,out uint code);
}
