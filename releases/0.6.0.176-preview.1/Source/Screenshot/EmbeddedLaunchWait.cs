using System.Diagnostics;

namespace ScreenshotTranslationUiTester;

internal static class EmbeddedLaunchWait
{
    // A timed connection wait alone leaves a crashed game showing "connecting"
    // for several minutes. Observe the exact process which was just launched.
    internal static async Task AcceptAsync(IEmbeddedTranslationConnection connection,Process process,CancellationToken cancellation,
        Action<string>? reportProgress=null,bool observeLaunchedProcessExit=true)
    {
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var accept=connection.AcceptAsync(stop.Token);
        var exited=observeLaunchedProcessExit?process.WaitForExitAsync(stop.Token):Task.Delay(Timeout.Infinite,stop.Token);
        var clock=Stopwatch.StartNew();
        try
        {
            Task completed;
            while(true)
            {
                reportProgress?.Invoke(Describe(connection.ConnectionStage,clock.Elapsed));
                completed=await Task.WhenAny(accept,exited,Task.Delay(1000,stop.Token));
                cancellation.ThrowIfCancellationRequested();
                if(completed==accept||completed==exited)break;
                if(!connection.Connected&&connection is RpgGameDataConnection unity&&unity.StartupFailure() is {} failure)
                {
                    stop.Cancel();try{await accept;}catch(OperationCanceledException){}catch(IOException){}
                    throw new IOException(failure);
                }
            }
            cancellation.ThrowIfCancellationRequested();
            if(completed==exited&&!connection.Connected)
            {
                stop.Cancel();
                try{await accept;}catch(OperationCanceledException){}catch(IOException){}
                throw new EmbeddedLaunchExitedException(process.ExitCode);
            }
            try{await accept;}
            catch(OperationCanceledException)when(!cancellation.IsCancellationRequested)
            {
                if(observeLaunchedProcessExit&&process.HasExited)throw new EmbeddedLaunchExitedException(process.ExitCode);
                throw new IOException("翻译连接未能在等待时限内完成："+Describe(connection.ConnectionStage,clock.Elapsed)+"。请检查游戏加载日志；本次尚未进入翻译阶段。");
            }
            catch(IOException ex)when(ex is not EmbeddedLaunchExitedException&&observeLaunchedProcessExit&&process.HasExited&&!cancellation.IsCancellationRequested)
            {throw new EmbeddedLaunchExitedException(process.ExitCode,ex);}
        }
        finally{stop.Cancel();try{await exited;}catch(OperationCanceledException){}}
    }
    internal static string Describe(EmbeddedConnectionStage stage,TimeSpan elapsed)
    {
        string text=stage switch
        {
            EmbeddedConnectionStage.Authenticating=>"正在核对翻译组件所属的游戏进程",
            EmbeddedConnectionStage.WaitingForHello=>"游戏通道已建立，等待翻译组件完成初始化",
            EmbeddedConnectionStage.Ready=>"翻译连接已确认",
            _=>"正在等待游戏中的翻译组件启动"
        };
        if(stage!=EmbeddedConnectionStage.Ready)
        {
            text+=$"（{Math.Max(0,(int)elapsed.TotalSeconds)} 秒）";
            if(elapsed.TotalSeconds>=30)text+="；请保持游戏画面运行，首次准备组件可能较慢";
        }
        return text;
    }
}

internal sealed class EmbeddedCompatibilityException(string message):IOException(message);
internal sealed class EmbeddedLaunchExitedException(int exitCode,Exception? inner=null):IOException($"游戏在翻译连接建立前已退出（退出代码 {exitCode}）。可从启动菜单选择“仅启动（不接入翻译）”。",inner);
