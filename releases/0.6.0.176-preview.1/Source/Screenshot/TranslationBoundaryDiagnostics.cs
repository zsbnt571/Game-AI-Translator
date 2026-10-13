namespace ScreenshotTranslationUiTester;

internal sealed record TranslationBoundaryEvent(string Stage,string RequestId,string? BlockId,string Value);

// Opt-in observer used by explicit diagnostics. Normal product calls have no observer.
// Values are output copies: transport, parser and accepted values are never replaced.
internal static class TranslationBoundaryDiagnostics
{
    private static readonly AsyncLocal<Action<TranslationBoundaryEvent>?> Observer=new();
    internal static bool Enabled=>Observer.Value is not null;
    internal static IDisposable Begin(Action<TranslationBoundaryEvent> observer)
    {
        var previous=Observer.Value;Observer.Value=observer;return new Scope(previous);
    }
    internal static void Record(string stage,string requestId,string? blockId,string value)
    {
        var observer=Observer.Value;if(observer is null)return;
        observer(new(stage,SafeDiagnosticOutput.Redact(requestId),
            blockId is null?null:SafeDiagnosticOutput.Redact(blockId),SafeDiagnosticOutput.Redact(value)));
    }
    private sealed class Scope(Action<TranslationBoundaryEvent>? previous):IDisposable
    {
        private bool _disposed;
        public void Dispose(){if(_disposed)return;_disposed=true;Observer.Value=previous;}
    }
}
