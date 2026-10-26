using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

/// <summary>Recover field metadata offline; generated assemblies are only read by Cecil.</summary>
internal static class UnityIl2CppSchema
{
    private const string Format="FusionUnitySchema/119";
    private static readonly SemaphoreSlim Gate=new(1,1);
    private sealed record Failure(DateTime Until,string Code,string Warning,string? DisplayDirectory);
    private static readonly ConcurrentDictionary<string,Failure> Failed=new();
    internal sealed record Result(string? Directory,string? Warning,string? DisplayDirectory=null);
    internal static Result Read(UnityEmbeddedGame game,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        string binary=Path.Combine(game.Root,"GameAssembly.dll");
        string metadata=Path.Combine(game.DataRoot,"il2cpp_data","Metadata","global-metadata.dat");
        string worker=Path.Combine(AppContext.BaseDirectory,"tools","unity-schema","FusionUnitySchema.exe");
        if(!File.Exists(worker)){Log("preflight","helper-missing",null,0,0,"");return new(null,"缺少字段读取组件，未能提前读取部分资源文字");}
        string? key=null,failureKey=null,output=null;string stage="preflight",code="schema-unavailable",signals="";int? exit=null;long elapsed=0,peak=0;
        try
        {
            if(game.UnityVersion=="unknown"||!File.Exists(metadata))return new(null,"缺少版本或元数据，部分资源文字由运行时补译");
            RejectLinks(binary);RejectLinks(metadata);
            if(new FileInfo(binary).Length>512L*1024*1024||new FileInfo(metadata).Length>128L*1024*1024)return new(null,"字段数据超过读取上限，部分文字由运行时补译");
            using(var input=File.OpenRead(metadata))
            using(var reader=new BinaryReader(input))
                if(input.Length<8||reader.ReadUInt32()!=0xfab11baf||reader.ReadInt32() is <24 or >31){Log("preflight","metadata-format-unsupported",null,0,0,"");return new(null,"字段数据格式暂不支持，部分文字由运行时补译");}
            // Content identity, rather than the install path, invalidates an old
            // layout when the game updates. Hashing is read-only and cancellable.
            key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Format+"|"+game.UnityVersion+"|"+Hash(binary,cancellation)+"|"+Hash(metadata,cancellation))));
            // A repaired/replaced helper must not inherit a ten-minute failure
            // from the same game. Successful field caches retain their format
            // identity; only the transient failure key follows helper files.
            failureKey=key+"|"+WorkerStamp(worker);
            string cache=Path.Combine(AppDataPaths.CacheRoot,"unity-fields-119");RejectLinks(cache);
            string final=Path.Combine(cache,key);
            Gate.Wait(cancellation);
            try
            {
                if(Valid(final,cancellation))return new(final,null);
                if(Failed.TryGetValue(failureKey,out var failed)&&DateTime.UtcNow<failed.Until){Log("cached-failure",failed.Code,null,0,0,"");return new(null,failed.Warning,NativeDisplayValid(failed.DisplayDirectory,cancellation)?failed.DisplayDirectory:null);}
                Directory.CreateDirectory(cache);
                output=Path.Combine(cache,key+"-"+Guid.NewGuid().ToString("N"));
                var start=new ProcessStartInfo(worker){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Path.GetDirectoryName(worker)!,RedirectStandardOutput=true,RedirectStandardError=true};
                foreach(var arg in new[]{binary,metadata,game.UnityVersion,output})start.ArgumentList.Add(arg);
                stage="worker-start";code="helper-start-failed";
                using var process=Process.Start(start)??throw new IOException("Field reader did not start");
                // Retain only allowlisted signal tokens. Parser output can
                // contain game strings/paths and must not become a debug dump.
                var stdout=Drain(process.StandardOutput);var stderr=Drain(process.StandardError);
                var watch=Stopwatch.StartNew();
                try
                {
                    stage="worker-running";code="helper-budget-exceeded";
                    while(!process.WaitForExit(100))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        stage="worker-monitor";code="helper-monitor-failed";
                        long? memory=ObserveWorkingSet(()=>{process.Refresh();return process.WorkingSet64;},()=>process.HasExited);
                        elapsed=watch.ElapsedMilliseconds;
                        if(memory is null)break;
                        peak=Math.Max(peak,memory.Value);
                        stage="worker-running";code="helper-budget-exceeded";
                        if(watch.Elapsed>TimeSpan.FromSeconds(40)||memory>1536L*1024*1024)throw new IOException("Field reader budget exceeded");
                    }
                    cancellation.ThrowIfCancellationRequested();
                    stage="worker-output";code="helper-output-drain-failed";
                    WorkerSignals[] observed;
                    try{observed=Task.WhenAll(stdout,stderr).WaitAsync(TimeSpan.FromSeconds(3),cancellation).GetAwaiter().GetResult();}
                    catch(TimeoutException ex){throw new IOException("Field reader output did not close",ex);}
                    exit=process.ExitCode;elapsed=watch.ElapsedMilliseconds;signals=string.Join(',',observed.SelectMany(s=>s.Codes).Distinct(StringComparer.Ordinal));
                    stage="worker-exit";code=WorkerCode(process.ExitCode,observed.Any(s=>s.RuntimeMissing));
                    if(process.ExitCode!=0)throw new IOException("Field reader failed");
                    stage="output-validation";code="helper-output-invalid";
                    if(!Valid(output,cancellation))throw new IOException("Field reader incomplete");
                    Log("complete","schema-ready",exit,elapsed,peak,signals);
                }
                finally
                {
                    string cleanup=StopWorker(()=>process.Kill(entireProcessTree:true),()=>process.HasExited,process.WaitForExit);
                    if(cleanup.Length>0)Log("worker-cleanup",cleanup,null,watch.ElapsedMilliseconds,peak,"");
                    // Preserve bounded phase signals on budget/error exits too,
                    // without logging parser text, game content or paths.
                    var drains=Task.WhenAll(stdout,stderr);
                    if(drains.Wait(3000))signals=string.Join(',',drains.Result.SelectMany(s=>s.Codes).Distinct(StringComparer.Ordinal));
                }
                // Do not delete an earlier incomplete directory. A unique good
                // result is usable even if a prior cache directory was damaged.
                if(!Directory.Exists(final)){Directory.Move(output,final);return new(final,null);}
                return new(output,null);
            }
            finally{Gate.Release();}
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            string? display=NativeDisplayValid(output,cancellation)?output:null;
            string warning=Warning(code);if(failureKey is not null)Failed[failureKey]=new(DateTime.UtcNow.AddMinutes(10),code,warning,display);
            Log(stage,code,exit,elapsed,peak,signals,ex.GetType().Name);
            if(display is not null)Log("partial-display","display-sources-retained",exit,elapsed,peak,signals);
            return new(null,warning,display);
        }
    }
    internal static long? ObserveWorkingSet(Func<long> read,Func<bool> exited)
    {
        try{return read();}
        catch(InvalidOperationException)when(exited()){return null;}
    }
    internal static string StopWorker(Action kill,Func<bool> exited,Func<int,bool> wait)
    {
        try
        {
            if(exited())return "";
            try{kill();}catch(InvalidOperationException)when(exited()){return "";}
            return wait(3000)?"":"helper-stop-timeout";
        }
        catch(Exception ex)when(ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {return "helper-stop-failed";}
    }
    internal static string WorkerCode(int exit,bool runtimeMissing)=>runtimeMissing||exit==unchecked((int)0x80008096)?"helper-runtime-missing":exit==0?"helper-output-invalid":"helper-parser-failed";
    internal static string Warning(string code)=>code switch
    {
        "helper-runtime-missing"=>"字段读取组件的运行环境不完整，未能提前读取资源文字；请使用完整软件包，详情见诊断日志。",
        "helper-start-failed"=>"字段读取组件未能启动，未能提前读取资源文字；详情见诊断日志。",
        "helper-budget-exceeded"=>"字段读取组件达到时间或内存上限，部分资源文字仍需运行时补译；详情见诊断日志。",
        "helper-parser-failed"=>"字段读取组件解析失败，未能提前读取部分资源文字；详情见诊断日志，不能据此判断没有文字。",
        "helper-output-invalid"=>"字段读取组件输出未通过完整性检查，未使用不完整字段；详情见诊断日志。",
        "helper-monitor-failed"=>"字段读取组件状态检查失败，已保留其他可读文本；详情见诊断日志。",
        "helper-output-drain-failed"=>"字段读取组件结束后未能完整收取结果，已保留其他可读文本；详情见诊断日志。",
        _=>"本次未能还原部分资源字段，运行时继续补译；详情见诊断日志。"
    };
    private static void Log(string stage,string code,int? exit,long elapsed,long peak,string signals,string exception="")
        =>AppLog.Write("unity-schema",$"stage={stage} code={code} exit={(exit is {} value?"0x"+unchecked((uint)value).ToString("X8"):"none")} elapsedMs={elapsed} peakBytes={peak} signals={signals} exception={exception}");
    private static string WorkerStamp(string worker)
    {
        var text=new StringBuilder();foreach(string file in new[]{worker,Path.ChangeExtension(worker,".dll"),Path.ChangeExtension(worker,".runtimeconfig.json")})
        {var info=new FileInfo(file);text.Append(info.Exists?info.Length:-1).Append(':').Append(info.Exists?info.LastWriteTimeUtc.Ticks:0).Append('|');}
        return text.ToString();
    }
    internal sealed class WorkerSignals
    {
        private string tail="";private readonly HashSet<string> codes=new(StringComparer.Ordinal);
        internal IEnumerable<string> Codes=>codes;internal bool RuntimeMissing=>codes.Contains("dotnet-runtime-missing");
        internal void Observe(string text)
        {
            text=tail+text;
            if(text.Contains("You must install or update .NET",StringComparison.OrdinalIgnoreCase)||text.Contains("No frameworks were found",StringComparison.OrdinalIgnoreCase))codes.Add("dotnet-runtime-missing");
            if(text.Contains("hostpolicy",StringComparison.OrdinalIgnoreCase))codes.Add("hostpolicy-mentioned");
            foreach(string phase in new[]{"initialize","native-display","attributes","assemblies","field-types","field-write","manifest","complete"})
                if(text.Contains("FUSION_SCHEMA_STAGE="+phase,StringComparison.Ordinal))codes.Add("phase-"+phase);
            foreach(string name in new[]{"BadImageFormatException","FormatException","EndOfStreamException","InvalidDataException","NotSupportedException","ArgumentOutOfRangeException","NullReferenceException","FileNotFoundException","TypeLoadException","MissingMethodException","DllNotFoundException","OutOfMemoryException"})
                if(text.Contains(name,StringComparison.Ordinal))codes.Add(name);
            tail=text.Length>1024?text[^1024..]:text;
        }
    }
    private static async Task<WorkerSignals> Drain(StreamReader reader)
    {
        var signals=new WorkerSignals();var buffer=new char[4096];int count;
        try{while((count=await reader.ReadAsync(buffer))>0)signals.Observe(new string(buffer,0,count));}
        catch(Exception ex)when(ex is IOException or ObjectDisposedException){}
        return signals;
    }
    private static string Hash(string path,CancellationToken cancellation)
    {
        using var input=File.OpenRead(path);using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[65536];int count;
        while((count=input.Read(buffer))>0){cancellation.ThrowIfCancellationRequested();hash.AppendData(buffer,0,count);}return Convert.ToHexString(hash.GetHashAndReset());
    }
    internal static bool NativeDisplayValid(string? folder,CancellationToken cancellation)
    {
        if(folder is null)return false;
        try
        {
            string data=Path.Combine(folder,"native-display.json"),hash=Path.Combine(folder,"native-display.sha256"),complete=Path.Combine(folder,"native-display.complete");
            foreach(var path in new[]{folder,data,hash,complete})RejectLinks(path);
            if(!File.Exists(data)||new FileInfo(data).Length>16*1024*1024||!File.Exists(hash)||new FileInfo(hash).Length!=64||!File.Exists(complete)||new FileInfo(complete).Length>64)return false;
            return File.ReadAllText(complete)==Format&&File.ReadAllText(hash)==Hash(data,cancellation);
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException){return false;}
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
