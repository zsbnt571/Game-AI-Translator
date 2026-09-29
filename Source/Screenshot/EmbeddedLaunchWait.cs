using System.Diagnostics;

namespace ScreenshotTranslationUiTester;

internal static class EmbeddedLaunchWait
{
    // A timed connection wait alone leaves a crashed game showing "connecting"
    // for several minutes. Observe the exact process which was just launched.
    internal static async Task AcceptAsync(IEmbeddedTranslationConnection connection,Process process,CancellationToken cancellation)
    {
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var accept=connection.AcceptAsync(stop.Token);
        var exited=process.WaitForExitAsync(stop.Token);
        try
        {
            var completed=await Task.WhenAny(accept,exited);
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
                if(process.HasExited)throw new EmbeddedLaunchExitedException(process.ExitCode);
                throw new IOException("等待翻译连接超时；游戏仍在运行，本次未接入翻译。首次生成组件可能较慢，请检查游戏的加载日志。");
            }
            catch(IOException ex)when(ex is not EmbeddedLaunchExitedException&&process.HasExited&&!cancellation.IsCancellationRequested)
            {throw new EmbeddedLaunchExitedException(process.ExitCode,ex);}
        }
        finally{stop.Cancel();try{await exited;}catch(OperationCanceledException){}}
    }
}

internal sealed class EmbeddedCompatibilityException(string message):IOException(message);
internal sealed class EmbeddedLaunchExitedException(int exitCode,Exception? inner=null):IOException($"游戏在翻译连接建立前已退出（退出代码 {exitCode}）。可从启动菜单选择“仅启动（不接入翻译）”。",inner);
