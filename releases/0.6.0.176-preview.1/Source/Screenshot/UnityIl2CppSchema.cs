using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

/// <summary>Recover field metadata offline; generated assemblies are only read by Cecil.</summary>
internal static class UnityIl2CppSchema
{
    private const string Format="FusionUnitySchema/105";
    private static readonly SemaphoreSlim Gate=new(1,1);
    private static readonly ConcurrentDictionary<string,DateTime> Failed=new();
    internal sealed record Result(string? Directory,string? Warning);
    internal static Result Read(UnityEmbeddedGame game,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        string binary=Path.Combine(game.Root,"GameAssembly.dll");
        string metadata=Path.Combine(game.DataRoot,"il2cpp_data","Metadata","global-metadata.dat");
        string worker=Path.Combine(AppContext.BaseDirectory,"tools","unity-schema","FusionUnitySchema.exe");
        if(!File.Exists(worker))return new(null,"缺少字段读取组件，未能提前读取部分资源文字");
        string? key=null;
        try
        {
            if(game.UnityVersion=="unknown"||!File.Exists(metadata))return new(null,"缺少版本或元数据，部分资源文字由运行时补译");
            RejectLinks(binary);RejectLinks(metadata);
            if(new FileInfo(binary).Length>512L*1024*1024||new FileInfo(metadata).Length>128L*1024*1024)return new(null,"字段数据超过读取上限，部分文字由运行时补译");
            using(var input=File.OpenRead(metadata))
            using(var reader=new BinaryReader(input))
                if(input.Length<8||reader.ReadUInt32()!=0xfab11baf||reader.ReadInt32() is <24 or >31)return new(null,"字段数据格式暂不支持，部分文字由运行时补译");
            // Content identity, rather than the install path, invalidates an old
            // layout when the game updates. Hashing is read-only and cancellable.
            key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Format+"|"+game.UnityVersion+"|"+Hash(binary,cancellation)+"|"+Hash(metadata,cancellation))));
            string cache=Path.Combine(AppDataPaths.CacheRoot,"unity-fields-105");RejectLinks(cache);
            string final=Path.Combine(cache,key);
            Gate.Wait(cancellation);
            try
            {
                if(Valid(final,cancellation))return new(final,null);
                if(Failed.TryGetValue(key,out var until)&&DateTime.UtcNow<until)return new(null,"本次未能还原部分资源字段，运行时继续补译");
                Directory.CreateDirectory(cache);
                string output=Path.Combine(cache,key+"-"+Guid.NewGuid().ToString("N"));
                var start=new ProcessStartInfo(worker){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Path.GetDirectoryName(worker)!,RedirectStandardOutput=true,RedirectStandardError=true};
                foreach(var arg in new[]{binary,metadata,game.UnityVersion,output})start.ArgumentList.Add(arg);
                using var process=Process.Start(start)??throw new IOException("Field reader did not start");
                // Drain without retaining game text or unbounded parser logs.
                var stdout=Drain(process.StandardOutput);var stderr=Drain(process.StandardError);
                var watch=Stopwatch.StartNew();
                try
                {
                    while(!process.WaitForExit(100))
                    {
                        cancellation.ThrowIfCancellationRequested();process.Refresh();
                        if(watch.Elapsed>TimeSpan.FromSeconds(40)||process.WorkingSet64>1536L*1024*1024)throw new IOException("Field reader budget exceeded");
                    }
                    cancellation.ThrowIfCancellationRequested();
                    Task.WhenAll(stdout,stderr).GetAwaiter().GetResult();
                    if(process.ExitCode!=0||!Valid(output,cancellation))throw new IOException("Field reader incomplete");
                }
                finally
                {
                    if(!process.HasExited){process.Kill(entireProcessTree:true);process.WaitForExit(3000);}
                }
                // Do not delete an earlier incomplete directory. A unique good
                // result is usable even if a prior cache directory was damaged.
                if(!Directory.Exists(final)){Directory.Move(output,final);return new(final,null);}
                return new(output,null);
            }
            finally{Gate.Release();}
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException or InvalidDataException or ArgumentException)
        {
            if(key is not null)Failed[key]=DateTime.UtcNow.AddMinutes(10);
            return new(null,"本次未能还原部分资源字段，运行时继续补译");
        }
    }
    private static async Task Drain(StreamReader reader)
    {var buffer=new char[4096];while(await reader.ReadAsync(buffer)>0){}}
    private static string Hash(string path,CancellationToken cancellation)
    {
        using var input=File.OpenRead(path);using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[65536];int count;
        while((count=input.Read(buffer))>0){cancellation.ThrowIfCancellationRequested();hash.AppendData(buffer,0,count);}return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static bool Valid(string folder,CancellationToken cancellation)
    {
        if(!Directory.Exists(folder))return false;RejectLinks(folder);
        string complete=Path.Combine(folder,"complete"),manifest=Path.Combine(folder,"manifest.json");RejectLinks(complete);RejectLinks(manifest);
        if(!File.Exists(complete)||new FileInfo(complete).Length>64||File.ReadAllText(complete)!=Format||!File.Exists(manifest)||new FileInfo(manifest).Length>131072)return false;
        var entries=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(manifest));
        if(entries is null||entries.Count is <2 or >513||!entries.ContainsKey("native-display.json"))return false;long total=0;
        foreach(var pair in entries)
        {
            if(Path.GetFileName(pair.Key)!=pair.Key||pair.Key.Contains(':')||(!pair.Key.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)&&pair.Key!="native-display.json"))return false;
            string path=Path.Combine(folder,pair.Key);RejectLinks(path);
            if(!File.Exists(path)||(total+=new FileInfo(path).Length)>128L*1024*1024||Hash(path,cancellation)!=pair.Value)return false;
        }
        return true;
    }
    private sealed record NativeSource(string Text,string Assembly,int Token,long Offset,string Sink);
    private sealed record NativeResult(NativeSource[] Sources,string[] Warnings,int Methods);
    internal static EmbeddedCatalogSource[] ReadDisplay(string directory,CancellationToken cancellation,out bool complete)
    {
        complete=false;string file=Path.Combine(directory,"native-display.json");RejectLinks(file);
        if(!File.Exists(file)||new FileInfo(file).Length>16*1024*1024)return [];
        cancellation.ThrowIfCancellationRequested();
        var result=JsonSerializer.Deserialize<NativeResult>(File.ReadAllText(file));
        if(result?.Sources is null||result.Sources.Length>50000)return [];
        var sources=new List<EmbeddedCatalogSource>();int characters=0;
        string identity=Hash(file,cancellation);
        foreach(var source in result.Sources)
        {
            cancellation.ThrowIfCancellationRequested();
            if(source is null||source.Text is null||source.Text.Length is <2 or >6000||source.Text.Contains('\0')||source.Assembly is null||source.Assembly.Length>256||source.Sink is null||source.Sink.Length>8192||source.Offset is <0 or >262144||(characters+=source.Text.Length)>1000000)return [];
            sources.Add(new(source.Text,"il2cpp-display","GameAssembly.dll",identity,source.Assembly,source.Token,(int)source.Offset,source.Sink,["native display argument"]));
        }
        complete=result.Warnings is {Length:0};return sources.ToArray();
    }
    private static void RejectLinks(string path)
    {
        for(string? at=Path.GetFullPath(path);at is not null;at=Path.GetDirectoryName(at))
            if((File.Exists(at)||Directory.Exists(at))&&(File.GetAttributes(at)&FileAttributes.ReparsePoint)!=0)throw new IOException("Field data path is redirected");
    }
}
