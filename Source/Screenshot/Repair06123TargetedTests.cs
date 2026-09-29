using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class Repair06123TargetedTests
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var failures = new List<string>();
        try
        {
            using var original = Create4K();
            using var identical = (Bitmap)original.Clone();
            using var changed = (Bitmap)original.Clone();
            changed.SetPixel(3839, 2159, Color.Magenta);

            var identity = new SessionImageIdentity(original);
            var watch = Stopwatch.StartNew();
            var requests = Enumerable.Range(0, 4).Select(_ => identity.GetHashAsync()).ToArray();
            Task.WaitAll(requests);
            watch.Stop();
            var hash = requests[0].Result;
            Check(requests.All(x => x.Result == hash), "parallel requests returned different hashes", failures);
            Check(identity.Diagnostics.ImageHashCount == 1, $"hash count was {identity.Diagnostics.ImageHashCount}", failures);
            Check(identity.Diagnostics.PngEncodeForHashCount == 0, "PNG was used for hash", failures);
            Check(identity.Diagnostics.BytesSerializedForHash == 0, "hash serialized an encoded image", failures);

            var identicalHash = new SessionImageIdentity(identical).GetHashAsync().GetAwaiter().GetResult();
            var changedHash = new SessionImageIdentity(changed).GetHashAsync().GetAwaiter().GetResult();
            Check(hash == identicalHash, "identical pixels produced different hashes", failures);
            Check(hash != changedHash, "different pixels produced the same hash", failures);

            var historyRoot = Path.Combine(output, "history");
            using var sessions = new SessionServices(historyRoot);
            var key = SessionServices.CreateOcrKey(hash, OcrEngineKind.Rapid, "rapid-v1");
            sessions.PutOcr(key, new OcrEngineResult
            {
                ImageHash = hash, EngineRequested = OcrEngineKind.Rapid, EngineActual = OcrEngineKind.Rapid,
                ModelName = "rapid-v1", Blocks = [new OcrEngineBlock { Id = "B1", RawText = "test", CorrectedText = "test" }]
            });
            Check(sessions.TryGetOcr(key, out var cached) && cached.CacheHit, "repeat OCR did not hit cache", failures);

            var items = new[] { new TranslationItem("T1", "Hello", StructuredTextRole.BodyParagraph) };
            var zh = new ApiSettings { TargetLanguage = "Simplified Chinese", CustomTranslationPrompt = "" };
            var zh2 = new ApiSettings { TargetLanguage = "Simplified Chinese", CustomTranslationPrompt = "" };
            var ja = new ApiSettings { TargetLanguage = "Japanese", CustomTranslationPrompt = "" };
            var prompt = new ApiSettings { TargetLanguage = "Simplified Chinese", CustomTranslationPrompt = "literal" };
            var zhKey = SessionServices.TranslationKey(items, zh);
            Check(zhKey == SessionServices.TranslationKey(items, zh2), "same translation settings produced different keys", failures);
            Check(zhKey != SessionServices.TranslationKey(items, ja), "target language missing from translation key", failures);
            Check(zhKey != SessionServices.TranslationKey(items, prompt), "prompt missing from translation key", failures);

            var report = new
            {
                Resolution = "3840x2160",
                Hash = new
                {
                    identity.Diagnostics.ImageHashCount,
                    identity.Diagnostics.PngEncodeForHashCount,
                    identity.Diagnostics.HashMethod,
                    identity.Diagnostics.RawPixelHashMs,
                    identity.Diagnostics.BytesSerializedForHash,
                    identity.Diagnostics.BytesHashed,
                    FourConcurrentRequestWallMs = watch.Elapsed.TotalMilliseconds,
                    IdenticalPixelsStable = hash == identicalHash,
                    DifferentPixelDistinct = hash != changedHash
                },
                OcrCacheHit = cached?.CacheHit == true,
                TranslationKey = new { SameSettingsStable = true, TargetLanguageSeparated = true, PromptSeparated = true },
                Status = failures.Count == 0 ? "PASS" : "FAIL",
                Failures = failures
            };
            File.WriteAllText(Path.Combine(output, "TARGETED-METRICS.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }

        File.WriteAllLines(Path.Combine(output, "RESULT.txt"), failures.Count == 0 ? ["PASS", "Status=Manual Acceptance Pending"] : ["FAIL", .. failures]);
        return failures.Count == 0 ? 0 : 2;
    }

    private static Bitmap Create4K()
    {
        var bitmap = new Bitmap(3840, 2160, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(31, 37, 49));
        using var brush = new LinearGradientBrush(new Rectangle(0, 0, bitmap.Width, bitmap.Height), Color.CornflowerBlue, Color.DarkSlateGray, 27f);
        graphics.FillRectangle(brush, 0, 0, bitmap.Width, bitmap.Height);
        graphics.DrawString("ST-0506123 real-size pixel identity", SystemFonts.DefaultFont, Brushes.White, 80, 80);
        return bitmap;
    }

    private static void Check(bool condition, string message, List<string> failures)
    {
        if (!condition) failures.Add(message);
    }
}
