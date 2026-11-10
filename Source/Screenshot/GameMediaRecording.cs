using System.Diagnostics;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal sealed record GameMediaRecordingReply(string Code,string Message,int Width=0,int Height=0,long Frames=0,double Seconds=0,string Reason="",int Error=0);

internal sealed class GameMediaRecording : IDisposable
{
    private readonly Process worker;
    private readonly GameWindowCover.CaptureJob job;
    private readonly GameMediaStore store;
    private readonly string fileName;
    private readonly string? thumbnail;
    private readonly Task<string> errors;
    private bool disposed;
    internal Task<GameMediaEntry> Completion {get;private set;}=null!;
    internal DateTimeOffset StartedAt {get;}=DateTimeOffset.UtcNow;
    internal bool IsRunning=>!Completion.IsCompleted;
    private GameMediaRecording(Process worker,GameWindowCover.CaptureJob job,GameMediaStore store,string fileName,string? thumbnail,Task<string> errors)
    {this.worker=worker;this.job=job;this.store=store;this.fileName=fileName;this.thumbnail=thumbnail;this.errors=errors;}
    internal static async Task<GameMediaRecording> StartAsync(CoverTarget target,GameMediaStore store,string? thumbnail,CancellationToken token)
    {
        if(!GameLibraryWindowIdentity.Matches(target))throw new IOException("请先恢复游戏窗口，再开始录像。");
        var helper=Path.Combine(AppContext.BaseDirectory,"tools","cover-capture","FusionCoverCapture.exe");
        if(!File.Exists(helper))throw new IOException("录像组件缺失，请使用完整安装目录。");
        var name=store.NewName("video",".mp4");
        var info=new ProcessStartInfo(helper){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
            RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=true,WorkingDirectory=Path.GetDirectoryName(helper)!};
        foreach(var arg in new[]{target.Window.ToInt64().ToString(),target.ProcessId.ToString(),target.ExePath,target.StartedUtcTicks.ToString(),"--record",store.FilePath(name+".recording.mp4")})info.ArgumentList.Add(arg);
        var worker=new Process{StartInfo=info};var job=new GameWindowCover.CaptureJob();
        try
        {
            if(!worker.Start()||!job.Assign(worker.Handle))throw new IOException("录像组件启动失败。");
            var errors=worker.StandardError.ReadToEndAsync();
            await worker.StandardInput.WriteLineAsync("GO");await worker.StandardInput.FlushAsync();
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(20000);
            var line=await worker.StandardOutput.ReadLineAsync(timeout.Token);
            var ready=ReadReply(line);
            if(ready.Code!="recording")throw new IOException(ready.Message+" ("+ready.Code+")");
            var session=new GameMediaRecording(worker,job,store,name,thumbnail,errors);session.Completion=session.FinishAsync();return session;
        }
        catch
        {
            try{if(!worker.HasExited)worker.Kill(entireProcessTree:true);}catch{}
            job.Dispose();worker.Dispose();throw;
        }
    }
    private static GameMediaRecordingReply ReadReply(string? line)
    {
        if(line is null||line.Length>8192)throw new IOException("录像组件没有返回有效状态。");
        return JsonSerializer.Deserialize<GameMediaRecordingReply>(line)??throw new IOException("录像状态无法读取。");
    }
    private async Task<GameMediaEntry> FinishAsync()
    {
        var result=ReadReply(await worker.StandardOutput.ReadLineAsync());
        using var timeout=new CancellationTokenSource(15000);await worker.WaitForExitAsync(timeout.Token);await errors;
        if(worker.ExitCode!=0||result.Code!="recorded"||result.Frames<1||result.Seconds<=0)throw new IOException(result.Message+" ("+result.Code+")");
        var temporary=store.FilePath(fileName+".recording.mp4");
        if(!File.Exists(temporary)||new FileInfo(temporary).Length<128)throw new IOException("录像文件未完成写入。");
        File.Move(temporary,store.FilePath(fileName),false);
        var entry=new GameMediaEntry(fileName,"video",StartedAt,result.Width,result.Height,result.Seconds,thumbnail,result.Reason);
        store.Commit(entry);return entry;
    }
    internal async Task<GameMediaEntry> StopAsync()
    {
        if(!Completion.IsCompleted)
        {try{await worker.StandardInput.WriteLineAsync("STOP");await worker.StandardInput.FlushAsync();worker.StandardInput.Close();}catch(IOException){} }
        using var timeout=new CancellationTokenSource(35000);return await Completion.WaitAsync(timeout.Token);
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        try{worker.StandardInput.Close();}catch{}
        try{if(!worker.HasExited)worker.Kill(entireProcessTree:true);}catch{}
        job.Dispose();worker.Dispose();
    }
}
