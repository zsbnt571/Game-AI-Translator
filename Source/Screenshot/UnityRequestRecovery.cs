using System.Diagnostics;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Retry only after a complete receipt: Disable is idempotent, and a paused
// receipt explicitly says the operation never started. Transport failures and
// unknown completion are deliberately outside this recovery policy.
internal static class UnityRequestRecovery
{
    internal static async Task<JsonElement> ExecuteAsync(
        Func<CancellationToken,Task<JsonElement>> send,bool disable,bool deferIfPaused,
        CancellationToken cancellation,TimeSpan budget,TimeSpan? restorePollInterval=null)
    {
        var elapsed=Stopwatch.StartNew();bool paused=false,restoring=false;int? pending=null;
        var restoreInterval=restorePollInterval??TimeSpan.FromMilliseconds(100);
        while(true)
        {
            cancellation.ThrowIfCancellationRequested();
            var remaining=budget-elapsed.Elapsed;
            if((disable||paused)&&remaining<=TimeSpan.Zero)throw Deadline();
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            if(disable||paused)deadline.CancelAfter(remaining);
            try
            {
                var result=await send(deadline.Token).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                if(!disable)return result;
                if(Restored(result,out pending))return result;
                // An incomplete receipt is not evidence that restoration can
                // progress. Preserve the unconfirmed state for the caller.
                if(pending is null)throw new UnityRestoreUnconfirmedException();
                restoring=true;
                await Delay(restoreInterval).ConfigureAwait(false);
            }
            catch(OperationCanceledException)when(deadline.IsCancellationRequested&&!cancellation.IsCancellationRequested)
            {throw Deadline();}
            catch(UnityMainThreadPausedException)
            {
                if(deferIfPaused)throw;
                paused=true;
                await Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }
        }
        Exception Deadline()=>disable||restoring?new UnityRestoreUnconfirmedException(pending):new UnityMainThreadPausedTimeoutException();
        async Task Delay(TimeSpan interval)
        {
            var remaining=budget-elapsed.Elapsed;
            if(remaining<=TimeSpan.Zero)throw Deadline();
            await Task.Delay(remaining<interval?remaining:interval,cancellation).ConfigureAwait(false);
        }
    }
    private static bool Restored(JsonElement result,out int? pending)
    {
        pending=null;if(result.ValueKind!=JsonValueKind.Object)return false;
        bool restored=result.TryGetProperty("restored",out var confirmation)&&confirmation.ValueKind==JsonValueKind.True;
        if(result.TryGetProperty("restorePending",out var count)&&count.ValueKind==JsonValueKind.Number&&count.TryGetInt32(out int value)&&value>=0)pending=value;
        return restored&&pending==0;
    }
}
internal sealed class UnityMainThreadPausedException:Exception;
internal sealed class UnityMainThreadPausedTimeoutException():IOException("游戏主线程暂停超过等待上限（30 秒）。本次请求尚未开始，连接仍保留；请回到游戏让画面运行后再重试。");
internal sealed class UnityRestoreUnconfirmedException(int? pending=null):IOException("翻译已停止继续写入，但原文恢复未确认"+(pending is {} count?$"（仍有 {count} 项）":"（恢复回执不完整）")+"。请回到游戏让主线程运行后再尝试关闭翻译。");
