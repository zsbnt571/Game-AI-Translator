using System.Diagnostics;

namespace ScreenshotTranslationUiTester;

internal static class RendererResourceBudget
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal static async Task<IDisposable> EnterAsync(CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        return new Lease();
    }
    private sealed class Lease : IDisposable
    {
        public void Dispose() => Gate.Release();
    }

    internal static T RunBelowNormal<T>(Func<T> work)
    {
        var thread = Thread.CurrentThread;
        var original = thread.Priority;
        try { thread.Priority = ThreadPriority.BelowNormal; return work(); }
        finally { thread.Priority = original; }
    }
}

public sealed record RenderResourceMetrics(
    long RendererTotalMs, long CpuTimeMs, double PeakWorkingSetMB, double PeakPrivateBytesMB,
    long ManagedAllocatedBytes, int Gen0GC, int Gen1GC, int Gen2GC, int ThreadCount,
    int FullBitmapCloneCount, long FullBitmapBytesCopied, int RoiBitmapAllocationCount,
    long RoiAllocatedBytes, int GraphicsObjectCount, int FontMeasureCount, int TextMeasureCount,
    int BackgroundRegionCount, int FallbackRegionCount, int UiCommitCount = 0, long UiBlockTimeMs = 0);

internal sealed class RenderResourceProbe
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly TimeSpan _cpu;
    private readonly long _workingSet;
    private readonly long _privateBytes;
    private readonly long _allocated;
    private readonly int[] _gc = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    private readonly long _started = Stopwatch.GetTimestamp();

    internal RenderResourceProbe()
    {
        _process.Refresh(); _cpu = _process.TotalProcessorTime; _workingSet = _process.WorkingSet64;
        _privateBytes = _process.PrivateMemorySize64; _allocated = GC.GetTotalAllocatedBytes(false);
    }

    internal RenderResourceMetrics Complete(RenderCopyMetrics copies, int graphicsObjects, int fontMeasures,
        int textMeasures, int backgrounds, int fallbacks)
    {
        _process.Refresh();
        static double Mb(long value) => Math.Round(value / 1048576d, 2);
        return new(
            (long)((Stopwatch.GetTimestamp() - _started) * 1000d / Stopwatch.Frequency),
            (long)(_process.TotalProcessorTime - _cpu).TotalMilliseconds,
            Mb(Math.Max(_workingSet, _process.WorkingSet64)), Mb(Math.Max(_privateBytes, _process.PrivateMemorySize64)),
            Math.Max(0, GC.GetTotalAllocatedBytes(false) - _allocated),
            GC.CollectionCount(0) - _gc[0], GC.CollectionCount(1) - _gc[1], GC.CollectionCount(2) - _gc[2],
            _process.Threads.Count, copies.FullBitmapCloneCount,
            copies.TotalPixelBytesCopied - copies.TemporaryBitmapBytes,
            copies.RegionBitmapAllocationCount, copies.TemporaryBitmapBytes,
            graphicsObjects, fontMeasures, textMeasures, backgrounds, fallbacks);
    }
}
