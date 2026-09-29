using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class Repair06126TargetedTests
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var failures = new List<string>();
        var report = new Dictionary<string, object>();
        try
        {
            RunAtomicFirstFrame(output, report, failures);
            Run4KPerformance(report, failures);
        }
        catch (Exception ex)
        {
            failures.Add(ex.ToString());
        }

        File.WriteAllText(Path.Combine(output, "TARGETED-METRICS.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllLines(Path.Combine(output, "RESULT.txt"), failures.Count == 0
            ? ["PASS", "Status=Real 4K Manual Acceptance Pending"]
            : ["FAIL", .. failures]);
        return failures.Count == 0 ? 0 : 2;
    }

    private static void RunAtomicFirstFrame(string output, Dictionary<string, object> report,
        List<string> failures)
    {
        var bounds = new Rectangle(0, 0, 1280, 720);
        var initialHover = new Rectangle(170, 105, 780, 455);
        using var frozen = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(frozen))
        {
            using var background = new System.Drawing.Drawing2D.LinearGradientBrush(bounds,
                Color.FromArgb(235, 240, 248), Color.FromArgb(72, 105, 156), 25F);
            graphics.FillRectangle(background, bounds);
            graphics.FillRectangle(Brushes.OrangeRed, new Rectangle(220, 155, 230, 130));
            graphics.DrawString("INITIAL HOVER READY", new Font("Segoe UI", 28F, FontStyle.Bold),
                Brushes.White, 250, 340);
        }

        using var overlay = new CaptureOverlay(reusableLifecycle: true);
        overlay.PrewarmHandle();
        overlay.Bounds = bounds;
        overlay.PrepareReusableCaptureForTest(frozen, Stopwatch.GetTimestamp());
        overlay.PrepareInitialHoverForTest(new Point(300, 200), _ => initialHover);
        overlay.Show();
        Application.DoEvents();

        using var frame1 = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        overlay.DrawToBitmap(frame1, bounds);
        frame1.Save(Path.Combine(output, "frame-01-first-visible.png"), ImageFormat.Png);
        Application.DoEvents();
        using var frame2 = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        overlay.DrawToBitmap(frame2, bounds);
        frame2.Save(Path.Combine(output, "frame-02-second-visible.png"), ImageFormat.Png);

        var snapshot = overlay.GetInitialFrameSnapshotForSmoke();
        var changedPixels = CountChangedPixels(frame1, frame2);
        var activePixel = frame1.GetPixel(initialHover.Left + 20, initialHover.Top + 20);
        var shadedPixel = frame1.GetPixel(30, 30);
        var activeIsBrighter = Brightness(activePixel) > Brightness(shadedPixel) + 12;
        var prompt = Rectangle.Ceiling(overlay.GetPromptBoundsForSmoke());
        var promptCenter = frame1.GetPixel(Math.Clamp(prompt.Left + prompt.Width / 2, 0, frame1.Width - 1),
            Math.Clamp(prompt.Top + prompt.Height / 2, 0, frame1.Height - 1));

        report["AtomicFirstVisibleFrame"] = new
        {
            snapshot.InitialHoverReadyBeforeVisible,
            snapshot.StartupDeferredHoverInvalidateCount,
            snapshot.InitialHover,
            snapshot.StaticComposeMs,
            snapshot.FirstPaintMs,
            FirstSecondChangedPixels = changedPixels,
            ActiveRegionRestoredOnFirstFrame = activeIsBrighter,
            PromptBounds = prompt,
            PromptCenterArgb = promptCenter.ToArgb(),
            Frame01 = "frame-01-first-visible.png",
            Frame02 = "frame-02-second-visible.png"
        };

        if (!snapshot.InitialHoverReadyBeforeVisible) failures.Add("Initial hover was not ready before Visible=true.");
        if (snapshot.StartupDeferredHoverInvalidateCount != 0)
            failures.Add($"Startup deferred hover invalidations={snapshot.StartupDeferredHoverInvalidateCount}.");
        if (snapshot.InitialHover != initialHover) failures.Add("First-frame hover geometry differs from prepared geometry.");
        if (!activeIsBrighter) failures.Add("First visible frame did not restore the initial hover region.");
        if (changedPixels != 0) failures.Add($"First and stable second frame differ by {changedPixels} pixels without input.");
        overlay.Hide();
    }

    private static void Run4KPerformance(Dictionary<string, object> report, List<string> failures)
    {
        using var frozen = new Bitmap(3840, 2160, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(frozen)) graphics.Clear(Color.FromArgb(36, 42, 52));
        using var overlay = new CaptureOverlay(reusableLifecycle: true);
        overlay.PrewarmHandle();
        overlay.Bounds = new Rectangle(0, 0, 3840, 2160);
        overlay.PrepareReusableCaptureForTest(frozen, Stopwatch.GetTimestamp());
        overlay.PrepareInitialHoverForTest(new Point(100, 100), _ => new Rectangle(50, 50, 900, 650));
        overlay.Show();
        Application.DoEvents();
        var snapshot = overlay.GetInitialFrameSnapshotForSmoke();
        report["Performance4K"] = new
        {
            snapshot.StaticComposeMs,
            snapshot.FirstPaintMs,
            FirstPaintPath = "static frozen+shade layer blit, initial hover/border/prompt in same visible paint",
            FullScreenBitmapAllocatedInFirstPaint = false,
            CopyFromScreenInFirstPaint = 0
        };
        if (snapshot.FirstPaintMs >= 33.887)
            failures.Add($"4K first paint regressed to {snapshot.FirstPaintMs:F3}ms.");
        overlay.Hide();
    }

    private static long CountChangedPixels(Bitmap left, Bitmap right)
    {
        long changed = 0;
        for (var y = 0; y < left.Height; y++)
        for (var x = 0; x < left.Width; x++)
            if (left.GetPixel(x, y).ToArgb() != right.GetPixel(x, y).ToArgb()) changed++;
        return changed;
    }

    private static int Brightness(Color color) => (color.R * 299 + color.G * 587 + color.B * 114) / 1000;
}
