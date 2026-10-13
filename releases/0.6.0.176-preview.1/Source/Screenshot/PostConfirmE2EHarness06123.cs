using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class PostConfirmE2EHarness06123
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var trace = Path.Combine(output, "POST-CONFIRM-TRACE.jsonl");
        if (File.Exists(trace)) File.Delete(trace);
        Environment.SetEnvironmentVariable("ST_06123_PERF_TRACE", trace);
        var process = Process.GetCurrentProcess();
        var gc0 = GC.CollectionCount(0); var gc1 = GC.CollectionCount(1); var gc2 = GC.CollectionCount(2);
        var failures = new List<string>();
        PreviewForm? preview = null; CaptureOverlay? overlay = null;
        var closedAt = DateTime.MinValue; var stage = 0;
        using var host = new Form { ShowInTaskbar = false, Opacity = 0, Bounds = new Rectangle(0, 0, 1, 1) };
        var timer = new System.Windows.Forms.Timer { Interval = 40 };
        host.Shown += (_, _) =>
        {
            var frozen = Create4KFrame();
            overlay = new CaptureOverlay(new Rectangle(0, 0, 3840, 2160), frozen, true, true);
            overlay.SetSelectionForRealE2ETest(new Rectangle(0, 0, 3840, 2160));
            timer.Start();
        };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (stage == 0)
                {
                    stage++;
                    overlay!.ConfirmSelectionForRealE2ETest(PreviewMode.OcrOnly);
                    var capture = overlay.Result ?? throw new InvalidOperationException("4K selection confirmation returned no image");
                    var settings = new ApiSettings { VisualModel = VisualModelKind.Off, OcrEngine = OcrEngineKind.Windows, PreviewTextPanelVisible = true };
                    preview = new PreviewForm(capture.Image, capture.Mode, settings, new OcrService(), new TranslationService());
                    capture.ImageOwnershipTransferred = true;
                    var show = Stopwatch.StartNew(); preview.Show(); show.Stop();
                    PostConfirmPerformanceTrace.Write("PreviewShow", new { PreviewShowMs = show.Elapsed.TotalMilliseconds });
                    return;
                }
                if (stage == 1 && preview!.ImageHashDiagnosticsForSmoke.ImageHashCount == 1)
                {
                    stage++; preview.Close(); closedAt = DateTime.UtcNow; return;
                }
                if (stage == 2 && (DateTime.UtcNow - closedAt).TotalMilliseconds >= 800)
                {
                    timer.Stop(); overlay?.Dispose(); host.Close();
                }
            }
            catch (Exception ex) { failures.Add(ex.ToString()); timer.Stop(); host.Close(); }
        };
        Application.Run(host);
        Environment.SetEnvironmentVariable("ST_06123_PERF_TRACE", null);

        var events = ReadEvents(trace);
        double Metric(string stageName, string property) => events.Where(x => x.Stage == stageName)
            .Select(x => x.Value.TryGetProperty(property, out var p) ? p.GetDouble() : -1).DefaultIfEmpty(-1).Last();
        var hashEvents = events.Where(x => x.Stage == "ImageHash").ToArray();
        var imageHashCount = hashEvents.Length == 0 ? 0 : hashEvents[^1].Value.GetProperty("ImageHashCount").GetInt32();
        var pngCount = hashEvents.Length == 0 ? -1 : hashEvents[^1].Value.GetProperty("PngEncodeForHashCount").GetInt32();
        var hashMs = hashEvents.Length == 0 ? -1 : hashEvents[^1].Value.GetProperty("RawPixelHashMs").GetInt64();
        var serialized = hashEvents.Length == 0 ? -1 : hashEvents[^1].Value.GetProperty("BytesSerializedForHash").GetInt64();
        var uiBlocks = new[] { Metric("SelectionConfirmHandler", "SelectionConfirmHandlerMs"), Metric("PreviewShow", "PreviewShowMs"), Metric("RunOcrSyncPrefix", "RunOcrSyncPrefixMs") };
        var report = new
        {
            Resolution = "3840x2160",
            SelectionConfirmHandlerMs = uiBlocks[0], PreviewShowMs = uiBlocks[1], RunOcrSyncPrefixMs = uiBlocks[2],
            ImageHashCount = imageHashCount, PngEncodeForHashCount = pngCount,
            HashMethod = hashEvents.Length == 0 ? "missing" : hashEvents[^1].Value.GetProperty("HashMethod").GetString(),
            RawPixelHashMs = hashMs, BytesSerializedForHash = serialized,
            TimeToFirstAsyncYieldMs = Metric("TimeToFirstAsyncYield", "TimeToFirstAsyncYieldMs"),
            MaxPostConfirmUiThreadBlockMs = uiBlocks.Where(x => x >= 0).DefaultIfEmpty(-1).Max(),
            GC0 = GC.CollectionCount(0) - gc0, GC1 = GC.CollectionCount(1) - gc1, GC2 = GC.CollectionCount(2) - gc2,
            WorkingSet = process.WorkingSet64,
            PreviewClosedDuringBackgroundOcr = stage >= 2,
            Status = "Manual Acceptance Pending"
        };
        if (imageHashCount != 1) failures.Add($"ImageHashCount={imageHashCount}");
        if (pngCount != 0) failures.Add($"PngEncodeForHashCount={pngCount}");
        if (report.TimeToFirstAsyncYieldMs < 0 || report.TimeToFirstAsyncYieldMs >= 100) failures.Add($"TimeToFirstAsyncYieldMs={report.TimeToFirstAsyncYieldMs}");
        File.WriteAllText(Path.Combine(output, "POST-CONFIRM-METRICS.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllLines(Path.Combine(output, "RESULT.txt"), failures.Count == 0 ? ["PASS", "Status=Manual Acceptance Pending"] : ["FAIL", .. failures]);
        return failures.Count == 0 ? 0 : 2;
    }

    private static Bitmap Create4KFrame()
    {
        var bitmap = new Bitmap(3840, 2160, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        using var background = new LinearGradientBrush(new Rectangle(0, 0, 3840, 2160), Color.FromArgb(18, 38, 74), Color.FromArgb(72, 30, 28), 32f);
        graphics.FillRectangle(background, 0, 0, 3840, 2160);
        using var font = new Font("Segoe UI", 42, FontStyle.Bold);
        graphics.DrawString("REAL 3840x2160 POST-CONFIRM PATH", font, Brushes.White, 180, 160);
        return bitmap;
    }

    private static List<TraceEvent> ReadEvents(string path)
    {
        var result = new List<TraceEvent>();
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadLines(path))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            result.Add(new TraceEvent(root.GetProperty("Stage").GetString()!, root.GetProperty("Value").Clone()));
        }
        return result;
    }
    private sealed record TraceEvent(string Stage, JsonElement Value);
}
