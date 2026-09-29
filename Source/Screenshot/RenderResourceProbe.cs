using System.Diagnostics;
using System.Text.Json;
namespace ScreenshotTranslationUiTester;

// Explicit diagnostics switch: production defaults to no snapshots or file writes.
internal sealed class SourceMaskResourceProbe : IDisposable
{
    private readonly string? _directory;
    private readonly Stopwatch _clock=Stopwatch.StartNew();
    private readonly List<object> _rows=[];
    internal SourceMaskResourceProbe()
    {
        var path=Environment.GetEnvironmentVariable("ST_RENDER_RESOURCE_TRACE");
        if(!string.IsNullOrWhiteSpace(path)&&Path.IsPathFullyQualified(path))_directory=Path.GetFullPath(path);
    }
    internal void Capture(string stage,IEnumerable<Array>? ownedPlanes=null)
    {
        if(_directory is null)return;
        using var process=Process.GetCurrentProcess();process.Refresh();
        var planes=ownedPlanes?.Distinct(ReferenceEqualityComparer.Instance).Cast<Array>().ToArray()??[];
        _rows.Add(new{Stage=stage,ElapsedMs=_clock.Elapsed.TotalMilliseconds,
            MainPrivateBytes=process.PrivateMemorySize64,MainWorkingSet=process.WorkingSet64,
            ManagedAllocatedBytes=GC.GetTotalAllocatedBytes(false),ManagedHeapBytes=GC.GetGCMemoryInfo().HeapSizeBytes,
            OwnedPlanes=planes.Length,OwnedPlaneBytes=planes.Sum(p=>(long)Buffer.ByteLength(p))});
    }
    public void Dispose()
    {
        if(_directory is null)return;
        Capture("end");Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory,"render-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N")+".json"),
            JsonSerializer.Serialize(new{BuildId=BuildIdentity.BuildId,MainProcessId=Environment.ProcessId,Rows=_rows},new JsonSerializerOptions{WriteIndented=true}));
    }
}
