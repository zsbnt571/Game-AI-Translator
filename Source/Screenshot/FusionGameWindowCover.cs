using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ScreenshotTranslationUiTester;

internal sealed record CoverCaptureResult(Bitmap? Image,string Message,string Diagnostic,CoverTarget? Target=null);
internal static class GameWindowCover
{
    internal static string HelperPath => Path.Combine(AppContext.BaseDirectory,"tools","cover-capture","FusionCoverCapture.exe");
    internal static bool Available => File.Exists(HelperPath);
    internal const string MissingMessage = "Not included in this alpha：本版未包含封面截图组件，可从本地文件选择图片。";
    private sealed record Reply(string Code,string Message,int Width,int Height,long ElapsedMs,string? Image,int Error,string Quality,int RejectedFrames);
    private static readonly SemaphoreSlim CaptureGate=new(1,1);
    private static bool backendBlocked;
    internal static void NoteLaunch(string path,int pid)=>GameLibraryWindowIdentity.NoteLaunch(RecentGameStore.Normalize(path),pid);
    internal static Bitmap Thumbnail(Image image,int width,int height)
    {
        var scale=Math.Min(1d,Math.Min((double)width/image.Width,(double)height/image.Height));
        var result=new Bitmap(Math.Max(1,(int)(image.Width*scale)),Math.Max(1,(int)(image.Height*scale)));
        using var g=Graphics.FromImage(result);g.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(image,new Rectangle(Point.Empty,result.Size));return result;
    }
    internal static async Task<CoverCaptureResult> CaptureAsync(string path,bool automatic,CancellationToken token,Action<string>? report=null)
    {
        if(!Available)return new(null,MissingMessage,"backend=WGC code=helper-missing");
        if(!OperatingSystem.IsWindowsVersionAtLeast(10,0,18362))return new(null,"需要 Windows 10 1903 或更新系统","backend=WGC code=unsupported-os");
        var exe=RecentGameStore.Normalize(path);var elapsed=Stopwatch.StartNew();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(automatic?25000:9000);
        var entered=false;var sawProcess=false;var workers=0;var attempts=0;string? lastStage=null;IntPtr settlingWindow=IntPtr.Zero;
        CoverCaptureResult last=new(null,"未找到对应游戏窗口","backend=WGC code=window-not-found");
        try
        {
            await CaptureGate.WaitAsync(deadline.Token);entered=true;
            if(backendBlocked)return new(null,"上次捕获进程无法结束，请重启软件后重试","backend=WGC code=worker-still-running");
            // Waiting for a game window does not consume a capture worker.
            // The total deadline bounds startup, including games whose window
            // appears after the former eight one-second discovery attempts.
            for(var attempt=0;;attempt++)
            {
                attempts=attempt+1;
                deadline.Token.ThrowIfCancellationRequested();
                var found=await Task.Run(()=>GameLibraryWindowIdentity.Find(exe),deadline.Token);
                if(found.Target is null)
                {
                    last=new(null,found.Message,"backend=WGC code="+found.Code+" attempt="+(attempt+1));
                    if(lastStage!=found.Code){report?.Invoke("stage="+found.Code+" attempt="+attempts);lastStage=found.Code;}
                    if(sawProcess&&found.Code=="process-not-found")return new(null,"游戏已退出","backend=WGC code=game-exited");
                    if(found.Code!="process-not-found")sawProcess=true;
                    if(found.Code=="ambiguous-child")return last;
                }
                else
                {
                    sawProcess=true;
                    if(automatic&&await Task.Run(()=>GameLibraryWindowIdentity.IsStartupWindow(found.Target),deadline.Token))
                    {
                        last=new(null,"正在等待游戏主窗口，暂不使用启动设置或加载窗口","backend=WGC code=startup-window");
                        report?.Invoke("stage=startup-window-skipped attempt="+attempts);
                        await Task.Delay(1000,deadline.Token);continue;
                    }
                    if(automatic&&settlingWindow!=found.Target.Window)
                    {
                        settlingWindow=found.Target.Window;
                        await Task.Delay(2500,deadline.Token);
                        if(!GameLibraryWindowIdentity.Matches(found.Target))continue;
                    }
                    // Up to four worker processes, each checks up to eight frames in its existing session.
                    if(workers>=(automatic?4:1))break;
                    workers++;report?.Invoke("stage=capture attempt="+attempts+" worker="+workers);
                    last=await Task.Run(()=>CaptureInProcessAsync(found.Target,deadline.Token),deadline.Token);
                    report?.Invoke("stage=result attempt="+attempts+" worker="+workers+" "+last.Diagnostic);
                    if(last.Image is not null||backendBlocked||last.Diagnostic.Contains("code=unsupported")||last.Diagnostic.Contains("code=helper-missing"))return last;
                    if(last.Diagnostic.Contains("code=window-changed"))return last;
                }
                if(!automatic)break;
                await Task.Delay(1000,deadline.Token);
            }
            return last with{Diagnostic=last.Diagnostic+" stop="+(automatic?"worker-limit":"manual-attempt")+" attempts="+attempts+" workers="+workers+" totalMs="+elapsed.ElapsedMilliseconds};
        }
        catch(OperationCanceledException)when(!token.IsCancellationRequested){return new(null,"封面等待已到上限，请在游戏窗口可用时手动更新","backend=WGC code=total-timeout elapsedMs="+elapsed.ElapsedMilliseconds+" last="+last.Diagnostic);}
        finally{if(entered)CaptureGate.Release();}
    }
    private static async Task<CoverCaptureResult> CaptureInProcessAsync(CoverTarget target,CancellationToken token)
    {
        var helper=HelperPath;
        if(!File.Exists(helper))return new(null,MissingMessage,"backend=WGC code=helper-missing");
        if(!GameLibraryWindowIdentity.Matches(target))return new(null,"游戏窗口已改变","backend=WGC code=window-changed");
        var info=new ProcessStartInfo(helper){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
            RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=true,WorkingDirectory=Path.GetDirectoryName(helper)!};
        info.ArgumentList.Add(target.Window.ToInt64().ToString());info.ArgumentList.Add(target.ProcessId.ToString());
        info.ArgumentList.Add(target.ExePath);info.ArgumentList.Add(target.StartedUtcTicks.ToString());
        using var job=new CaptureJob();
        using var process=new Process{StartInfo=info};var started=false;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(7000);
        try
        {
            token.ThrowIfCancellationRequested();if(!process.Start())throw new IOException("无法启动封面组件");started=true;
            if(!job.Assign(process.Handle))throw new IOException("无法为封面任务建立资源边界");
            var output=process.StandardOutput.ReadToEndAsync(timeout.Token);var errors=process.StandardError.ReadToEndAsync(timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            await process.StandardInput.WriteLineAsync("GO");process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var text=await output;await errors; // stderr is not propagated; no paths or dumps in ordinary status.
            if(process.ExitCode!=0||text.Length>1_000_000)return new(null,"封面组件未完成取图","backend=WGC code=helper-exit exit="+process.ExitCode);
            var reply=JsonSerializer.Deserialize<Reply>(text)??throw new InvalidDataException("封面组件返回值无法读取");
            var diagnostic=$"backend=WGC code={reply.Code} hwnd={target.Window.ToInt64()} pid={target.ProcessId} size={reply.Width}x{reply.Height} elapsedMs={reply.ElapsedMs} error=0x{reply.Error:X8} rejectedFrames={reply.RejectedFrames} {reply.Quality}";
            if(reply.Image is null)return new(null,reply.Message,diagnostic);
            token.ThrowIfCancellationRequested();
            if(!GameLibraryWindowIdentity.Matches(target))return new(null,"游戏窗口已退出或改变",diagnostic+" final=window-changed");
            try{
                using var bytes=new MemoryStream(Convert.FromBase64String(reply.Image));using var image=Image.FromStream(bytes);
                if(image.Width>640||image.Height>360||image.Width<1||image.Height<1)throw new InvalidDataException();
                var quality=CoverQuality.Analyze(image);
                if(!quality.Usable)return new(null,"尚未取得有效画面",diagnostic+" parent-rejected "+quality.Diagnostic);
                return new(new Bitmap(image),"已取得有效窗口帧",diagnostic,target);
            }catch(Exception ex)when(ex is not OperationCanceledException){return new(null,"封面图像解码失败，可重试",diagnostic+" final=decode-failed");}
        }
        catch(OperationCanceledException)when(!token.IsCancellationRequested){return new(null,"窗口捕获超时，请稍后更新","backend=WGC code=worker-timeout");}
        finally
        {
            if(started&&!process.HasExited)
            {
                // Only this newly created helper is terminated; never the game or an existing process.
                try{process.Kill(entireProcessTree:true);}catch{}
                job.Dispose();
                using var cleanup=new CancellationTokenSource(1000);
                try{await process.WaitForExitAsync(cleanup.Token);}catch{backendBlocked=true;}
            }
        }
    }
    private sealed class CaptureJob:SafeHandleZeroOrMinusOneIsInvalid
    {
        [StructLayout(LayoutKind.Sequential)]private struct Basic
        {public long ProcessTime,JobTime;public uint Flags;public UIntPtr MinWorking,MaxWorking;public uint ActiveLimit;public UIntPtr Affinity;public uint Priority,Scheduling;}
        [StructLayout(LayoutKind.Sequential)]private struct Io {public ulong ReadOps,WriteOps,OtherOps,ReadBytes,WriteBytes,OtherBytes;}
        [StructLayout(LayoutKind.Sequential)]private struct Extended
        {public Basic Basic;public Io Io;public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;}
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr CreateJobObject(IntPtr attributes,string? name);
        [DllImport("kernel32.dll")]private static extern bool SetInformationJobObject(IntPtr job,int type,ref Extended info,uint size);
        [DllImport("kernel32.dll")]private static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
        [DllImport("kernel32.dll")]private static extern bool CloseHandle(IntPtr handle);
        internal CaptureJob():base(true)
        {
            SetHandle(CreateJobObject(IntPtr.Zero,null));
            var limits=new Extended{Basic=new Basic{Flags=0x2000}};
            if(IsInvalid||!SetInformationJobObject(handle,9,ref limits,(uint)Marshal.SizeOf<Extended>())){Dispose();throw new IOException("无法建立封面任务资源边界");}
        }
        internal bool Assign(IntPtr process)=>AssignProcessToJobObject(handle,process);
        protected override bool ReleaseHandle()=>CloseHandle(handle);
    }
}
