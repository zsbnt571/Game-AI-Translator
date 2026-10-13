using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Owned by one synchronous region-build call. No global pool/cache, no sharing
// between tasks. Only scratch uses capacity; every consumer uses the current
// scan's width * height prefix. Escaping region/layout/output arrays stay owned.
internal sealed class RegionWorkspace : IDisposable
{
    internal bool[] Near=[], Expanded=[], Closed=[], Plate=[], Exterior=[];
    internal int[] Labels=[], Queue=[];
    internal float[] Red=[], Green=[], Blue=[];
    internal long AllocatedBytes { get; private set; }
    internal int Allocations { get; private set; }
    internal int ObserveCalls { get; private set; }
    internal int RecoverCalls { get; private set; }
    internal long RetainedBytes => (long)Near.Length+Expanded.Length+Closed.Length+Plate.Length+Exterior.Length+
        4L*(Labels.Length+Queue.Length+Red.Length+Green.Length+Blue.Length);
    internal long PeakRetainedBytes { get; private set; }
    private void Grow<T>(ref T[] buffer,int length,int elementBytes)
    {
        if(buffer.Length>=length)return;
        buffer=new T[length];AllocatedBytes+=(long)length*elementBytes;Allocations++;
    }
    internal void Observe(int n)
    {
        ObserveCalls++;
        Grow(ref Near,n,1);Grow(ref Expanded,n,1);Grow(ref Closed,n,1);
        Grow(ref Plate,n,1);Grow(ref Exterior,n,1);Grow(ref Labels,n,4);Grow(ref Queue,n,4);
        PeakRetainedBytes=Math.Max(PeakRetainedBytes,RetainedBytes);
    }
    internal void Recover(int n)
    {
        RecoverCalls++;
        Grow(ref Labels,n,4);Grow(ref Queue,n,4);
        Grow(ref Red,n,4);Grow(ref Green,n,4);Grow(ref Blue,n,4);
        PeakRetainedBytes=Math.Max(PeakRetainedBytes,RetainedBytes);
    }
    public void Dispose()
    {
        var before=RetainedBytes;
        Near=[];Expanded=[];Closed=[];Plate=[];Exterior=[];Labels=[];Queue=[];Red=[];Green=[];Blue=[];
        var directory=Environment.GetEnvironmentVariable("ST_REGION_WORKSPACE_TRACE");
        if(!string.IsNullOrEmpty(directory)&&Path.IsPathFullyQualified(directory))
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory,"workspace-"+Guid.NewGuid().ToString("N")+".json"),
                JsonSerializer.Serialize(new{ObserveCalls,RecoverCalls,AllocatedBytes,Allocations,PeakRetainedBytes,
                    BeforeDisposeBytes=before,AfterDisposeBytes=RetainedBytes,
                    Ownership="one Build; current scan prefix; no returned array aliases scratch; no pool/cache"}));
        }
    }
}
