using System.Diagnostics;

namespace ScreenshotTranslationUiTester;

// Disabled in normal use. Measures actual draw work, not an invented frame rate.
internal static class DesktopRenderMetrics
{
    internal static bool Enabled { get; set; }
    internal static event Action<string,double>? DrawCompleted;
    private static readonly Dictionary<string, List<double>> Samples = [];
    internal static IDisposable Measure(string name) => Enabled ? new Sample(name) : Empty.Instance;
    private sealed class Empty : IDisposable { internal static readonly Empty Instance = new(); public void Dispose() { } }
    private sealed class Sample(string name) : IDisposable
    {
        private readonly long start = Stopwatch.GetTimestamp();
        public void Dispose()
        {
            if (!Samples.TryGetValue(name, out var samples)) Samples[name] = samples = [];
            double elapsed=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (samples.Count < 100000) samples.Add(elapsed);
            DrawCompleted?.Invoke(name,elapsed);
        }
    }
    internal static object Snapshot() => Samples.ToDictionary(pair => pair.Key, pair =>
    {
        var values = pair.Value.Order().ToArray();
        return new { frames = values.Length, meanDrawMs = values.DefaultIfEmpty().Average(),
            p50DrawMs = values.Length == 0 ? 0 : values[(values.Length - 1) / 2],
            p95DrawMs = values.Length == 0 ? 0 : values[(int)((values.Length - 1) * .95)], maxDrawMs = values.LastOrDefault() };
    });
    internal static void Reset() => Samples.Clear();
}
