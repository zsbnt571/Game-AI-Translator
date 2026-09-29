using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ScreenshotTranslationUiTester.CorePipelineV2;

namespace ScreenshotTranslationUiTester;

/// <summary>
/// One local background backend. Source and declared masks only; no text, identity,
/// fixture path, translation or layout enters inference. Model installation is external.
/// </summary>
internal sealed class BackgroundAccelerationException : InvalidOperationException
{
    internal BackgroundAccelerationException(Exception? inner=null) : base("GPU 背景加速不可用或执行失败。请在设置 → 译图 → 背景计算中选择 CPU 后重试。",inner) {}
}

internal static class GeneralBackgroundRecovery
{
    private static readonly SemaphoreSlim Gate = new(1,1);
    private static Process? _worker;
    private static string? _runtimeIdentity;
    private static Task? _stderr;
    private static bool _modelWarm;
    static GeneralBackgroundRecovery() => AppDomain.CurrentDomain.ProcessExit += (_,_) => StopWorker();

    internal static bool Available =>
        File.Exists(ModelPath) && File.Exists(PythonPath) &&
        File.Exists(Path.Combine(AppContext.BaseDirectory,"workers","background_worker.py"));
    private static string RuntimeRoot => Environment.GetEnvironmentVariable("ST_BACKGROUND_RUNTIME_ROOT")
        ?? Path.Combine(AppContext.BaseDirectory,"runtime");
    private static string PythonPath => Path.Combine(RuntimeRoot,"python","python.exe");
    private static string ModelPath => Environment.GetEnvironmentVariable("ST_BACKGROUND_MODEL_PATH")
        ?? Path.Combine(RuntimeRoot,"background","lama_fp32.onnx");

    // Prepare model weights while the independent translation request is pending.
    // No source pixels, masks or translated text enter this operation.
    internal static async Task PrewarmAsync(CancellationToken cancellationToken,BackgroundComputeDevice device=BackgroundComputeDevice.Cpu)
    {
        using var traceStage=ProcessingTaskTrace.Current?.Timer.Stage("Background Prewarm (overlaps API)");
        if(!Available)return;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var entered=false;
        try
        {
            await Gate.WaitAsync(timeout.Token).ConfigureAwait(false);entered=true;
            timeout.Token.ThrowIfCancellationRequested();
            EnsureStarted(device);
            if(_modelWarm)return;
            var timer=Stopwatch.StartNew();
            await _worker!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{command="warmup",model=ModelPath})).ConfigureAwait(false);
            await _worker.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            var response=await _worker.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false)
                ??throw new EndOfStreamException("Background warmup stopped.");
            using var message=JsonDocument.Parse(response);
            if(message.RootElement.GetProperty("status").GetString()!="OK")
                throw new InvalidDataException("Background warmup failed.");
            _modelWarm=true;
            AppLog.Write("background-runtime",$"pid={_worker.Id} operation={ProcessingTaskTrace.Current?.Timer.OperationId} requested={device} state=prewarmed elapsed_ms={timer.ElapsedMilliseconds}");
        }
        catch(Exception ex)
        {
            if(entered)StopWorker();
            AppLog.Write("background-runtime",$"state=prewarm-not-used reason={ex.GetType().Name}");
        }
        finally{if(entered)Gate.Release();}
    }

    internal static async Task ReleaseAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try{StopWorker();}finally{Gate.Release();}
    }
    internal static Bitmap? Recover(Bitmap source,bool[,] authority,bool[,] excluded,string diagnosticOutput,
        CancellationToken cancellationToken=default,SourceDialogueMaterial.Result? dialogue=null,BackgroundComputeDevice device=BackgroundComputeDevice.Cpu)
    {
        if(!Available)return null;
        return RecoverAsync(source,authority,excluded,diagnosticOutput,cancellationToken,dialogue,device).GetAwaiter().GetResult();
    }
    private static async Task<Bitmap> RecoverAsync(Bitmap source,bool[,] authority,bool[,] excluded,
        string diagnosticOutput,CancellationToken cancellationToken,SourceDialogueMaterial.Result? dialogue,BackgroundComputeDevice device)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var token=timeout.Token;
        using(ProcessingTaskTrace.Current?.Timer.Stage("Background Queue"))await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            var requestRoot=Path.Combine(AppDataPaths.TempRoot,"background",Guid.NewGuid().ToString("N"));
            var operation=ProcessingTaskTrace.Current;
            using var preparation=operation?.Timer.Stage("Background Input Preparation");
            Directory.CreateDirectory(requestRoot);
            var input=Path.Combine(requestRoot,"source.png");
            var mask=Path.Combine(requestRoot,"authority.png");
            var context=Path.Combine(requestRoot,"context-exclusion.png");
            var result=Path.Combine(requestRoot,"candidate.png");
            var trace=Path.Combine(requestRoot,"runtime.json");
            source.Save(input,ImageFormat.Png);
            SaveMask(authority,mask);SaveMask(excluded,context);
            string? dialogueMask=null;
            if(dialogue?.Surfaces.Count>0)
            {
                var allDialogueText=new bool[source.Width,source.Height];
                foreach(var points in dialogue.GlyphMaterials.Values)foreach(var point in points)allDialogueText[point.X,point.Y]=true;
                dialogueMask=Path.Combine(requestRoot,"source-dialogue-material.png");SaveMask(allDialogueText,dialogueMask);
            }
            EnsureStarted(device);
            var process=_worker!;
            preparation?.Dispose();
            using var requestStage=operation?.Timer.Stage("Background Worker Request",process.Id);
            var operationId=operation?.Timer.OperationId;
            var imageSessionId=operation?.ImageSessionId;
            var backgroundRequestId=Path.GetFileName(requestRoot);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
                {source=input,authority=mask,exclusion=context,output=result,trace,model=ModelPath,
                    dialogueSurfaces=dialogue?.Surfaces,dialogueMask,operationId,imageSessionId,backgroundRequestId})).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
            var response=await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false)
                ?? throw new EndOfStreamException("Background worker stopped before responding.");
            using var message=JsonDocument.Parse(response);
            if(message.RootElement.GetProperty("status").GetString()!="OK")
                throw device==BackgroundComputeDevice.Gpu ? new BackgroundAccelerationException() : new InvalidDataException("Local background reconstruction failed.");
            requestStage?.Dispose();
            operation?.RecordExecution(message.RootElement,process.Id,process.StartTime,backgroundRequestId);
            using var decodeStage=operation?.Timer.Stage("Background Output Decode",process.Id);
            using var loaded=new Bitmap(result);
            if(loaded.Size!=source.Size)throw new InvalidDataException("Background dimensions changed.");
            token.ThrowIfCancellationRequested();
            if(!string.IsNullOrWhiteSpace(diagnosticOutput))
            {
                File.Copy(mask,Path.Combine(diagnosticOutput,"BG-DECLARED-AUTHORITY.png"),true);
                File.Copy(context,Path.Combine(diagnosticOutput,"BG-CONTEXT-EXCLUSION.png"),true);
                File.Copy(result,Path.Combine(diagnosticOutput,"BG-CANDIDATE-PRE-COMMIT.png"),true);
                File.Copy(trace,Path.Combine(diagnosticOutput,"BG-LOCAL-RUNTIME.json"),true);
            }
            AppLog.Write("background-runtime",$"pid={process.Id} operation={operationId} imageSession={imageSessionId} request={backgroundRequestId} requested={device} actual={operation?.Execution.ActualDevice} mode=SOURCE_SURFACE_OR_LOCAL_LAMA_V2 status=OK workingSetBytes={process.WorkingSet64}");
            return loaded.Clone(new Rectangle(Point.Empty,loaded.Size),PixelFormat.Format32bppArgb);
        }
        catch(Exception ex)
        {
            // A canceled/failed request must never feed the next request or continue
            // consuming resources after the caller abandons its immutable input.
            StopWorker();
            if(device==BackgroundComputeDevice.Gpu && ex is not OperationCanceledException && ex is not BackgroundAccelerationException)
                throw new BackgroundAccelerationException(ex);
            throw;
        }
        finally{try{StopWorker();}finally{Gate.Release();}}
    }
    private static void EnsureStarted(BackgroundComputeDevice device)
    {
        var gpuSite=Environment.GetEnvironmentVariable("ST_BACKGROUND_GPU_SITE")
            ?? Path.Combine(RuntimeRoot,"background-gpu","site");
        if(device==BackgroundComputeDevice.Gpu && !File.Exists(Path.Combine(gpuSite,"onnxruntime","capi","onnxruntime_providers_cuda.dll")))
            throw new BackgroundAccelerationException();
        var identity=PythonPath+"|"+ModelPath+"|"+device+"|"+gpuSite;
        if(_worker is {HasExited:false} && _runtimeIdentity==identity)return;
        StopWorker();
        var info=new ProcessStartInfo(PythonPath)
        {
            WorkingDirectory=AppDataPaths.TempRoot,UseShellExecute=false,CreateNoWindow=true,
            WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardInput=true,
            RedirectStandardOutput=true,RedirectStandardError=true,
            StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8
        };
        info.ArgumentList.Add("-u");info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"workers","background_worker.py"));
        AppDataPaths.ConfigureChildEnvironment(info);
        var cpuSite=Path.Combine(RuntimeRoot,"venv","Lib","site-packages");
        info.Environment["PYTHONPATH"]=device==BackgroundComputeDevice.Gpu ? gpuSite+Path.PathSeparator+cpuSite : cpuSite;
        info.Environment["ST_BACKGROUND_DEVICE"]=device==BackgroundComputeDevice.Gpu ? "cuda" : "cpu";
        info.Environment["NVIDIA_TF32_OVERRIDE"]="0";
        var cudaCache=Path.Combine(AppDataPaths.CacheRoot,"cuda");
        info.Environment["CUDA_CACHE_PATH"]=cudaCache;
        if(device==BackgroundComputeDevice.Gpu)Directory.CreateDirectory(cudaCache);
        info.Environment["PYTHONDONTWRITEBYTECODE"]="1";
        info.Environment["PYTHONNOUSERSITE"]="1";info.Environment["PYTHONUTF8"]="1";
        info.Environment["HF_HUB_OFFLINE"]="1";
        info.Environment["OMP_NUM_THREADS"]="1";info.Environment["OPENBLAS_NUM_THREADS"]="1";info.Environment["MKL_NUM_THREADS"]="1";
        _worker=Process.Start(info)??throw new InvalidOperationException("Background worker could not start.");
        // Yield CPU scheduling to foreground games; only this owned worker changes.
        try{_worker.PriorityClass=ProcessPriorityClass.BelowNormal;}
        catch(System.ComponentModel.Win32Exception){AppLog.Write("background-runtime","priority=default reason=not-permitted");}
        _runtimeIdentity=identity;
        var process=_worker;
        _stderr=Task.Run(async () =>
        {
            try
            {
                while(await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                    AppLog.Write("background-worker",line);
            }
            catch(ObjectDisposedException){}
            catch(IOException){}
            catch(InvalidOperationException){}
        });
        AppLog.Write("background-runtime",$"pid={process.Id} operation={ProcessingTaskTrace.Current?.Timer.OperationId} requested={device} state=started");
    }
    private static void StopWorker()
    {
        var process=_worker;_worker=null;_runtimeIdentity=null;_modelWarm=false;
        if(process is null)return;
        using var releaseStage=ProcessingTaskTrace.Current?.Timer.Stage("Background Worker Exit",process.Id);
        try
        {
            if(!process.HasExited)process.Kill(entireProcessTree:true);
            process.WaitForExit(5000);
            AppLog.Write("background-runtime",$"pid={process.Id} state=stopped exit={process.ExitCode}");
        }
        catch(InvalidOperationException){}
        finally{process.Dispose();}
    }
    internal static void SaveMask(bool[,] mask,string path)
    {
        var w=mask.GetLength(0);var h=mask.GetLength(1);
        using var bitmap=new Bitmap(w,h,PixelFormat.Format32bppArgb);
        var data=bitmap.LockBits(new Rectangle(0,0,w,h),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
        try
        {
            var row=new int[w];
            for(var y=0;y<h;y++)
            {
                for(var x=0;x<w;x++)row[x]=mask[x,y]?unchecked((int)0xFFFFFFFF):unchecked((int)0xFF000000);
                Marshal.Copy(row,0,data.Scan0+y*data.Stride,w);
            }
        }
        finally{bitmap.UnlockBits(data);}
        bitmap.Save(path,ImageFormat.Png);
    }
}

