using ScreenshotTranslationUiTester.CorePipelineV2;

namespace ScreenshotTranslationUiTester;

// A render owns its GDI source and copies every mutable Core container/value.
// Nothing in this input is a live Preview/UI object.
internal sealed class PreviewRenderInput : IDisposable
{
    private int _disposed;
    internal Bitmap Source { get; }
    internal CorePipelineDocument Core { get; }
    internal TranslationImageTypographyProfile Typography { get; }
    internal int DisposeCount => Volatile.Read(ref _disposed);

    internal PreviewRenderInput(Bitmap source, CorePipelineDocument core, TranslationImageTypographyProfile typography)
    {
        Core = core with
        {
            RawLines = core.RawLines.Select(x => x with { Polygon = x.Polygon.ToArray() }).ToArray(),
            NormalizedLines = core.NormalizedLines.Select(x => x with { Polygon = x.Polygon.ToArray() }).ToArray(),
            VisualBlocks = core.VisualBlocks.Select(x => new VisualBlock
            {
                BlockId = x.BlockId, Bounds = x.Bounds, LayoutBehavior = x.LayoutBehavior,
                RoleHint = x.RoleHint, TextSelection = x.TextSelection, TextSelectionReason = x.TextSelectionReason,
                PreserveExplicitLineBreaks=x.PreserveExplicitLineBreaks,
                SourceCell=x.SourceCell is {} cell?cell with{}:null,
                SourceCaptionCorridor=x.SourceCaptionCorridor, SourceControlCorridor=x.SourceControlCorridor,
                SourceRole=x.SourceRole is {} role?role with{}:null,
                Lines = x.Lines.Select(line => line with { Polygon = line.Polygon.ToArray() }).ToArray()
            }).ToArray(),
            Translations = core.Translations.ToDictionary(x => x.Key, x => new BlockTranslation
            {
                BlockId = x.Value.BlockId, SourceText = x.Value.SourceText,
                TranslatedText = x.Value.TranslatedText, State = x.Value.State, FailureReason = x.Value.FailureReason
            }, StringComparer.Ordinal),
            VisualEvidence = core.VisualEvidence.ToArray(), RegionOwners = core.RegionOwners.ToArray(),
            GroupingEdges = core.GroupingEdges.ToArray()
        };
        Typography = typography with { FallbackFamilies = typography.FallbackFamilies.ToArray() };
        Source = new Bitmap(source); // Last allocation: earlier copy failures cannot leak a Bitmap.
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) Source.Dispose();
    }
}

internal sealed class PendingPreviewRender(CorePipelineProductRender result) : IDisposable
{
    private Bitmap? _ownedBitmap = result.Bitmap;
    private int _disposeCount;
    internal CorePipelineProductTiming Timing { get; } = result.Timing;
    internal IReadOnlyList<BlockRenderAudit> Audits { get; } = result.Audits;
    internal Bitmap? OwnedBitmap => _ownedBitmap;
    internal int DisposeCount => Volatile.Read(ref _disposeCount);
    internal Bitmap TransferBitmap() => Interlocked.Exchange(ref _ownedBitmap, null)
        ?? throw new InvalidOperationException("Render bitmap ownership was already transferred or released.");
    public void Dispose()
    {
        var bitmap = Interlocked.Exchange(ref _ownedBitmap, null);
        if (bitmap is null) return;
        Interlocked.Increment(ref _disposeCount);
        bitmap.Dispose();
    }
}

// Explicit test injection only. Production instances leave this null.
internal sealed class PreviewRenderTestHooks
{
    internal Action<string>? Phase { get; init; }
    internal Action<PreviewRenderInput>? InputCaptured { get; init; }
    internal Action<PendingPreviewRender>? ResultCreated { get; init; }
    internal Func<PreviewRenderInput, CorePipelineProductRender>? Render { get; init; }
}
