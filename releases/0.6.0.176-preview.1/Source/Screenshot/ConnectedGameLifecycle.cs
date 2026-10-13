namespace ScreenshotTranslationUiTester;

// Optional negotiated capability on the same authenticated game connection.
// Older bridges and other engines retain the normal window-close fallback.
internal static class ConnectedGameLifecycle
{
    internal static async Task<bool> TryRequestNormalQuitAsync(IGameDataConnection? connection,CancellationToken token,Action? onQuitRequest=null)
    {
        token.ThrowIfCancellationRequested();
        if(connection?.Connected!=true)return false;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            // Cancel only this UI wait. Canceling an in-flight framed request
            // would discard its reply and close the game's shared data pipe.
            var state=await connection.RequestAsync(new(){["op"]="lifecycleState"}).WaitAsync(timeout.Token);
            if(!state.TryGetProperty("protocol",out var protocol)||!protocol.TryGetInt32(out int version)||version!=1||
                !state.TryGetProperty("normalQuit",out var capable)||capable.ValueKind!=System.Text.Json.JsonValueKind.True)return false;
            timeout.Token.ThrowIfCancellationRequested();
            onQuitRequest?.Invoke();
            var answer=await connection.RequestAsync(new(){["op"]="prepareRestart"}).WaitAsync(timeout.Token);
            return answer.TryGetProperty("accepted",out var accepted)&&accepted.ValueKind==System.Text.Json.JsonValueKind.True;
        }
        catch(OperationCanceledException) when(!token.IsCancellationRequested){return false;}
        catch(Exception ex) when(ex is not OperationCanceledException)
        {
            AppLog.Write("game-restart","normal-exit capability unavailable: "+SafeDiagnosticOutput.ExceptionSummary(ex));
            return false;
        }
    }
}
