using ScreenshotTranslationUiTester.CorePipelineV2;

namespace ScreenshotTranslationUiTester;

public sealed record HistoryCorePoint(float X, float Y);

public sealed record HistoryCoreLine(
    string SourceId, string RawText, string CorrectedText,
    IReadOnlyList<HistoryCorePoint> Polygon, float X, float Y, float Width, float Height,
    float Confidence, int ReadingOrder)
{
    public string OcrRequestImageSha256 { get; init; } = "";
    public IReadOnlyList<OcrAlternativeEvidence> OcrAlternatives { get; init; } = [];
}

public sealed record HistoryCoreSnapshot(
    int CanvasWidth, int CanvasHeight, IReadOnlyList<HistoryCoreLine> Lines,
    IReadOnlyDictionary<string, string> AcceptedTranslations)
{
    public CoreProcessingProvenance? Provenance { get; init; }
    public IReadOnlyDictionary<string,string> AcceptedSources { get; init; } = new Dictionary<string,string>();
    internal static HistoryCoreSnapshot Capture(CorePipelineDocument document,
        CoreProcessingProvenance? provenance = null)
    {
        var lines = document.RawLines.Select(line => new HistoryCoreLine(
            line.SourceId, line.RawText, line.CorrectedText,
            line.Polygon.Select(point => new HistoryCorePoint(point.X, point.Y)).ToArray(),
            line.Bounds.X, line.Bounds.Y, line.Bounds.Width, line.Bounds.Height,
            line.Confidence, line.ReadingOrder)
        { OcrRequestImageSha256 = line.OcrRequestImageSha256,
            OcrAlternatives = line.OcrAlternatives.Select(c => c.Copy()).ToArray() }).ToArray();
        var translations = document.Translations.Values
            .Where(value => value.State == BlockTranslationState.Accepted && !string.IsNullOrWhiteSpace(value.TranslatedText))
            .ToDictionary(value => value.BlockId, value => value.TranslatedText, StringComparer.Ordinal);
        return new(document.Canvas.Width, document.Canvas.Height, lines, translations)
        { Provenance=provenance, AcceptedSources=document.VisualBlocks
            .Where(x=>translations.ContainsKey(x.BlockId)).ToDictionary(x=>x.BlockId,x=>x.SourceText,StringComparer.Ordinal) };
    }

    internal CorePipelineDocument Rebuild(Size actualCanvas,Bitmap? sourceImage=null)
    {
        var canvas = CanvasWidth > 0 && CanvasHeight > 0 ? new Size(CanvasWidth, CanvasHeight) : actualCanvas;
        if (canvas != actualCanvas) canvas = actualCanvas;
        var raw = Lines.OrderBy(line => line.ReadingOrder).Select(line =>
        {
            var bounds = new RectangleF(line.X, line.Y, line.Width, line.Height);
            var polygon = line.Polygon.Count >= 3
                ? line.Polygon.Select(point => new PointF(point.X, point.Y)).ToArray()
                : new[] { new PointF(bounds.Left, bounds.Top), new PointF(bounds.Right, bounds.Top),
                    new PointF(bounds.Right, bounds.Bottom), new PointF(bounds.Left, bounds.Bottom) };
            return new RawOcrLine(line.SourceId, line.RawText, line.CorrectedText, polygon, bounds,
                line.Confidence, line.ReadingOrder)
            { OcrRequestImageSha256 = line.OcrRequestImageSha256,
                OcrAlternatives = actualCanvas.Width == CanvasWidth && actualCanvas.Height == CanvasHeight
                    ? line.OcrAlternatives.Select(c => c.Copy()).ToArray() : [] };
        }).ToArray();
        var core = CorePipelineEngine.Analyze(canvas, raw,sourceImage:sourceImage);
        foreach (var block in core.VisualBlocks)
        {
            block.PreserveExplicitLineBreaks=HistoryRegenerationPolicy.HasProtectedSourceCorrection(this);
            if (!AcceptedTranslations.TryGetValue(block.BlockId, out var text) || string.IsNullOrWhiteSpace(text)) continue;
            var state = core.Translations[block.BlockId];
            state.TranslatedText = text;
            state.State = BlockTranslationState.Accepted;
        }
        return core;
    }
}
