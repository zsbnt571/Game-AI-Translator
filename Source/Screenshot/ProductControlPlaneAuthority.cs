using System.Collections.Concurrent;
using ScreenshotTranslationUiTester.CorePipelineV2;

namespace ScreenshotTranslationUiTester;

internal static class ProductControlPlaneAuthority
{
    private static readonly AsyncLocal<int> ProductDepth = new();
    private static readonly ConcurrentDictionary<string, int> LegacyExecutions = new(StringComparer.Ordinal);

    internal static IDisposable Begin(string action)
    {
        ProductDepth.Value++;
        AppLog.Write("core-authority", $"PRODUCT_ACTION_BEGIN action={action} owner=CoreV2");
        return new Scope(action);
    }

    internal static CorePipelineDocument RequireCore(CorePipelineDocument? core, string action)
    {
        if (core is not null) return core;
        throw new InvalidOperationException($"CORE_STATE_REQUIRED:{action}");
    }

    internal static void LegacyReached(string operation)
    {
        if (ProductDepth.Value <= 0) return;
        LegacyExecutions.AddOrUpdate(operation, 1, (_, count) => count + 1);
        throw new InvalidOperationException($"LEGACY_PRODUCT_REACHABILITY:{operation}");
    }

    internal static IReadOnlyDictionary<string, int> SnapshotLegacyExecutions() =>
        new Dictionary<string, int>(LegacyExecutions, StringComparer.Ordinal);

    internal static void ResetForTests() => LegacyExecutions.Clear();

    private sealed class Scope(string action) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ProductDepth.Value = Math.Max(0, ProductDepth.Value - 1);
            AppLog.Write("core-authority", $"PRODUCT_ACTION_END action={action} owner=CoreV2");
        }
    }
}
