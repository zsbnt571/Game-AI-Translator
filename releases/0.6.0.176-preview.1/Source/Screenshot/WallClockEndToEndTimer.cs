using System.Diagnostics;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

public sealed class WallClockEndToEndTimer
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly List<TimingStageRecord> _stages = [];
    public string OperationId { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public DateTimeOffset? EndedAt { get; private set; }
    public long ElapsedMs => FinalResultReadyMs ?? _watch.ElapsedMilliseconds;
    public long? FinalResultReadyMs { get; private set; }
    public DateTimeOffset? FinalResultReadyAt { get; private set; }
    public IReadOnlyList<TimingStageRecord> Stages { get { lock(_stages)return _stages.ToArray(); } }
    public Dictionary<string, object?> Metadata { get; } = new(StringComparer.Ordinal);

    public WallClockEndToEndTimer(string operationId) => OperationId = operationId;
    public IDisposable Stage(string name, int? workerPid = null) => new Scope(this, name, workerPid);
    // Imported durations have no observed position on this operation's timeline.
    public void AddCompletedStage(string name, long durationMs, int? workerPid = null)
    { lock(_stages)_stages.Add(new(name, null, null, durationMs,
            Environment.CurrentManagedThreadId, workerPid, OperationId)); }
    public void MarkFinalResultReady()
    { if(FinalResultReadyMs is not null)return; FinalResultReadyMs=_watch.ElapsedMilliseconds;FinalResultReadyAt=DateTimeOffset.Now; }
    public void Complete(string outputPath)
    {
        if (EndedAt is not null) return;
        _watch.Stop(); EndedAt = DateTimeOffset.Now;
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(new { OperationId, StartedAt, EndedAt,
            EndToEndWallClockMs=ElapsedMs, FinalResultReadyAt, FinalResultReadyMs,
            LifecycleCompleteMs=_watch.ElapsedMilliseconds, ProcessId=Environment.ProcessId, Stages, Metadata },
            new JsonSerializerOptions { WriteIndented=true }));
    }
    private sealed class Scope : IDisposable
    {
        private readonly WallClockEndToEndTimer _owner; private readonly string _name; private readonly int? _workerPid;
        private readonly Stopwatch _watch=Stopwatch.StartNew(); private readonly DateTimeOffset _start=DateTimeOffset.Now;
        private readonly int _thread=Environment.CurrentManagedThreadId; private bool _done;
        public Scope(WallClockEndToEndTimer owner,string name,int? workerPid){_owner=owner;_name=name;_workerPid=workerPid;}
        public void Dispose(){if(_done)return;_done=true;_watch.Stop();lock(_owner._stages)_owner._stages.Add(new(_name,_start,DateTimeOffset.Now,_watch.ElapsedMilliseconds,_thread,_workerPid,_owner.OperationId));}
    }
}

public sealed record TimingStageRecord(string Name, DateTimeOffset? StartTimestamp, DateTimeOffset? EndTimestamp,
    long DurationMs, int ThreadId, int? WorkerPid, string OperationId);
