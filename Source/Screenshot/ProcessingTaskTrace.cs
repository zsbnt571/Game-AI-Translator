using System.Text.Json;

namespace ScreenshotTranslationUiTester;

public sealed record BackgroundExecutionIdentity(string OperationId, string ImageSessionId,
    string RequestedDevice, string ActualDevice, int? WorkerPid, DateTimeOffset? WorkerStartedAt,
    string? BackgroundRequestId);

// The reference follows one operation through prewarm, render and release.
// It is never a process-global "last device" and never reads changed UI settings.
internal sealed class ProcessingTaskTrace(string imageSessionId, BackgroundComputeDevice device, string trigger, BackgroundTreatment treatment = BackgroundTreatment.FineRepair, TranslationImageTypographyProfile? typography = null, OcrLoad ocrLoad = OcrLoad.Standard)
{
    private static readonly AsyncLocal<ProcessingTaskTrace?> Ambient = new();
    internal static ProcessingTaskTrace? Current => Ambient.Value;
    internal WallClockEndToEndTimer Timer { get; } = new(Guid.NewGuid().ToString("N"));
    internal BackgroundComputeDevice Device { get; } = device;
    internal string ImageSessionId { get; } = imageSessionId;
    internal string Trigger { get; } = trigger;
    internal BackgroundTreatment Treatment { get; } = treatment;
    internal OcrLoad OcrLoad { get; } = ocrLoad;
    internal bool DetailedTimings { get; } = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ST_OCR_TIMING_PATH"));
    internal TranslationImageTypographyProfile? Typography { get; } = typography;
    internal void RecordLightweight() => Execution=new(Timer.OperationId,ImageSessionId,Device.ToString(),"Lightweight",null,null,null);
    internal BackgroundExecutionIdentity Execution { get; private set; } = new("", imageSessionId,
        device.ToString(), "NotExecuted", null, null, null);
    internal Action<string>? PhaseChanged { get; set; }
    internal IDisposable Enter() { var previous=Ambient.Value;Ambient.Value=this;return new Restore(previous); }
    private sealed class Restore(ProcessingTaskTrace? previous) : IDisposable
    { public void Dispose()=>Ambient.Value=previous; }
    internal void RecordExecution(JsonElement message,int pid,DateTimeOffset started,string requestId)
    {
        var actual="CpuSurfaceOnly";
        if(message.TryGetProperty("tiles",out var tiles) && tiles.GetArrayLength()>0 &&
            message.TryGetProperty("backend",out var backend) && backend.TryGetProperty("device",out var value))
            actual=value.GetString()??"Unknown";
        Execution=new(Timer.OperationId,ImageSessionId,Device.ToString(),actual,pid,started,requestId);
    }
    internal BackgroundExecutionIdentity FinalIdentity => Execution with { OperationId=Timer.OperationId };
    internal void WriteMetadata()
    { Timer.Metadata["BackgroundExecution"]=FinalIdentity;Timer.Metadata["Trigger"]=Trigger;Timer.Metadata["OcrLoad"]=OcrLoad.ToString();Timer.Metadata["BackgroundTreatment"]=Treatment.ToString();Timer.Metadata["RenderStrategyVersion"]=Treatment==BackgroundTreatment.Lightweight?LightweightBackgroundRecovery.StrategyVersion:"fix3-fine";Timer.Metadata["ImageSessionId"]=ImageSessionId; }
}
