using System.Drawing.Imaging;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class TranslationImageVisualPipelineDiagnostics
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string? _latestOutput;
    private static int _previewCaptured;

    internal static string? LatestOutput => _latestOutput;

    internal static void Capture(Bitmap source, IReadOnlyList<RecognitionRegion> regions, string output)
    {
        Directory.CreateDirectory(output);
        _latestOutput = output;
        Interlocked.Exchange(ref _previewCaptured, 0);
        var baseline = Baseline();
        using var auto = RegionRendererV2.Render(source, regions, baseline);
        using var white = RegionRendererV2.Render(source, regions, Variant(baseline, x => x.TextColorMode = TextColorMode.White));
        using var high = RegionRendererV2.Render(source, regions, Variant(baseline, x => x.DiagnosticUsePostCleanupContrast = true));
        using var textOnly = RegionRendererV2.Render(source, regions, Variant(baseline, x => x.DiagnosticSkipCleanup = true));
        using var cleanOnly = RegionRendererV2.Render(source, regions, Variant(baseline, x => x.DiagnosticSkipText = true));
        using var mask = RegionRendererV2.Render(source, regions, Variant(baseline, x => { x.DiagnosticSkipText = true; x.DiagnosticMaskOnly = true; }));

        source.Save(Path.Combine(output, "SOURCE.png"));
        auto.Bitmap.Save(Path.Combine(output, "RENDER-OUTPUT-1X.png"));
        auto.Bitmap.Save(Path.Combine(output, "AUTO-COLOR.png"));
        white.Bitmap.Save(Path.Combine(output, "PURE-WHITE.png"));
        high.Bitmap.Save(Path.Combine(output, "HIGH-CONTRAST-AUTO.png"));
        textOnly.Bitmap.Save(Path.Combine(output, "TEXT-ONLY-DIAGNOSTIC.png"));
        auto.Bitmap.Save(Path.Combine(output, "CURRENT-CLEANUP.png"));
        cleanOnly.Bitmap.Save(Path.Combine(output, "BACKGROUND-CLEAN-ONLY.png"));
        auto.Bitmap.Save(Path.Combine(output, "BACKGROUND-CLEAN-PLUS-TEXT.png"));

        var top = TopRegionBounds(regions, source.Size);
        SaveCrop(source, top, Path.Combine(output, "TOP-SOURCE-CROP.png"));
        SaveCrop(mask.Bitmap, top, Path.Combine(output, "TOP-CLEANUP-MASK.png"));
        SaveCrop(cleanOnly.Bitmap, top, Path.Combine(output, "TOP-BACKGROUND-CLEAN.png"));
        SaveCrop(auto.Bitmap, top, Path.Combine(output, "TOP-FINAL.png"));

        var outputBitmap = auto.Bitmap;
        File.WriteAllText(Path.Combine(output, "DISPLAY-PIPELINE.json"), JsonSerializer.Serialize(new
        {
            source = BitmapInfo(source), output = BitmapInfo(outputBitmap),
            renderer = new
            {
                sourceToOutputPixelRatio = new { x = outputBitmap.Width / (double)source.Width, y = outputBitmap.Height / (double)source.Height },
                resize = false, intermediateScaledBitmap = false, finalScaleTransform = false,
                interpolation = "None in RegionRendererV2; Graphics draws directly into same-size output bitmap",
                textCoordinates = "Rounded to integer X/Y in DrawLines",
                textDrawingBeforeAfterSizeEqual = source.Size == outputBitmap.Size
            },
            preview = new
            {
                control = "System.Windows.Forms.PictureBox",
                fitMode = "PictureBoxSizeMode.Zoom",
                oneToOneMode = "PictureBoxSizeMode.StretchImage with control size equal to bitmap at zoom 1.0",
                interpolationMode = "Not explicitly set by product code; native PictureBox paint implementation decides",
                pixelOffsetMode = "Not explicitly set", compositingQuality = "Not explicitly set",
                smoothingMode = "Not explicitly set", cachedScaledBitmap = false,
                originalAndTranslatedSameDisplayPath = true,
                screenshots = "Preview display values are appended by PreviewForm after image assignment"
            }
        }, JsonOptions));

        File.WriteAllText(Path.Combine(output, "TEXT-COLOR-EVIDENCE.json"), JsonSerializer.Serialize(new
        {
            fixedBaseline = new { font = baseline.FontFamily, baseline.FontScale, rasterizer = "Graphics.DrawString / AntiAliasGridFit", drawCount = 1, shadow = false, outline = false },
            auto = ColorEvidence(source, cleanOnly.Bitmap, regions, postCleanup: false),
            pureWhite = new { rgba = "#FFFFFFFF", source = "Diagnostic fixed color", textAlpha = 255 },
            highContrastAuto = ColorEvidence(source, cleanOnly.Bitmap, regions, postCleanup: true),
            samplingOrderFinding = "Formal Auto samples background and foreground from source bitmap before cleanup. Diagnostic high-contrast samples the cleanup-only bitmap and does not reuse source foreground ink."
        }, JsonOptions));

        File.WriteAllText(Path.Combine(output, "BACKGROUND-EVIDENCE.json"), JsonSerializer.Serialize(new
        {
            source = "SOURCE.png", textOnly = "TEXT-ONLY-DIAGNOSTIC.png",
            currentCleanup = "CURRENT-CLEANUP.png", backgroundCleanOnly = "BACKGROUND-CLEAN-ONLY.png",
            backgroundCleanPlusText = "BACKGROUND-CLEAN-PLUS-TEXT.png",
            topRegion = top, cleanupMask = "TOP-CLEANUP-MASK.png",
            implementation = new
            {
                masks = "SourceLinePolygons widened by line-height-derived padding",
                simpleBackground = "Median edge color fill",
                complexBackground = "Local edge texture extension",
                commitModel = "Current renderer modifies output region-by-region; diagnostics do not change formal behavior"
            }
        }, JsonOptions));

        File.WriteAllText(Path.Combine(output, "TYPOGRAPHY-ROLE-AUDIT.json"), JsonSerializer.Serialize(
            (auto.Diagnostics ?? []).Select(x => new
            {
                x.RegionId, role = x.Role.ToString(), x.FontFamily, x.FontSize,
                fontStyle = x.Role is RegionRoleType.Title or RegionRoleType.Header or RegionRoleType.CharacterName or RegionRoleType.Button ? "Bold" : "Regular",
                lineSpacing = baseline.LineSpacingScale, alignment = x.Role is RegionRoleType.Button or RegionRoleType.CharacterName ? "Center" : "Near",
                padding = "Layout target inset: horizontal min(5px, 3.5% width), vertical min(3px, 4.5% height)",
                x.LineCount, x.RenderBounds, x.TranslationRenderRect, x.RenderStatus, x.FallbackReason
            }), JsonOptions));
    }

    internal static bool BeginPreviewCapture() => LatestOutput is not null && Interlocked.Exchange(ref _previewCaptured, 1) == 0;

    internal static void SavePreviewMetadata(object value)
    {
        if (LatestOutput is not { } output) return;
        File.WriteAllText(Path.Combine(output, "PREVIEW-DISPLAY-EVIDENCE.json"), JsonSerializer.Serialize(value, JsonOptions));
    }

    private static RenderSettings Baseline() => new()
    {
        FontFamily = FontManager.BuildTranslationImageChain("Microsoft YaHei UI")[0], FontScale = 1F,
        TextColorMode = TextColorMode.Auto, Shadow = false, Outline = false, StrokeWidth = RendererStrokeWidth.None,
        LegacyTextRasterizationForDiagnostics = false, BackgroundStrategy = TranslationOverlayBackgroundStyle.Automatic
    };

    private static RenderSettings Variant(RenderSettings s, Action<RenderSettings> change)
    {
        var value = new RenderSettings
        {
            FontFamily = s.FontFamily, FontScale = s.FontScale, TextColorMode = s.TextColorMode,
            CustomTextColor = s.CustomTextColor, Shadow = s.Shadow, Outline = s.Outline,
            StrokeWidth = s.StrokeWidth, AutomaticOutlineColor = s.AutomaticOutlineColor,
            OutlineColor = s.OutlineColor, MinFontSize = s.MinFontSize,
            LineSpacingScale = s.LineSpacingScale, CompactLineSpacingScale = s.CompactLineSpacingScale,
            SafeExpansionScale = s.SafeExpansionScale, Background = s.Background,
            BackgroundStrategy = s.BackgroundStrategy, BackgroundColor = s.BackgroundColor,
            BackgroundOpacity = s.BackgroundOpacity
        };
        change(value); return value;
    }

    private static object BitmapInfo(Bitmap value) => new
    {
        value.Width, value.Height, horizontalDpi = value.HorizontalResolution,
        verticalDpi = value.VerticalResolution, pixelFormat = value.PixelFormat.ToString(),
        rawFormat = value.RawFormat.ToString()
    };

    private static object[] ColorEvidence(Bitmap source, Bitmap cleaned, IReadOnlyList<RecognitionRegion> regions, bool postCleanup)
    {
        return regions.Where(x => !x.IsIgnored && !x.PreserveOriginal && !string.IsNullOrWhiteSpace(x.TranslationText)).Select(region =>
        {
            var target = region.RendererTargetRegion.IsEmpty ? region.BoundingBox : region.RendererTargetRegion;
            var sample = postCleanup ? cleaned : source;
            var background = BackgroundEstimator.Estimate(sample, target).Color;
            var sampled = postCleanup ? null : TextStyleHintExtractor.EstimateForeground(source, region, background);
            var foreground = ResolveAuto(background, sampled);
            return (object)new
            {
                region.RegionId, role = region.RoleType.ToString(), sampleStage = postCleanup ? "After background cleanup" : "Original source including source text",
                backgroundRgba = Hex(background), sampledSourceForeground = sampled is { } c ? Hex(c) : null,
                resolvedTextRgba = Hex(foreground), textAlpha = foreground.A,
                contrastRatio = Contrast(foreground, background)
            };
        }).ToArray();
    }

    private static Color ResolveAuto(Color background, Color? sampled)
    {
        var foreground = sampled is { } value && Contrast(value, background) >= 3 ? value : Luminance(background) > 145 ? Color.Black : Color.White;
        return Contrast(foreground, background) < 3 ? (Luminance(background) > 145 ? Color.Black : Color.White) : foreground;
    }

    private static Rectangle TopRegionBounds(IReadOnlyList<RecognitionRegion> regions, Size size)
    {
        var region = regions.Where(x => !x.IsIgnored && x.SourceLinePolygons.Count > 0).OrderBy(x => x.BoundingBox.Top).ThenByDescending(x => x.BoundingBox.Width).First();
        var bounds = region.SourceLinePolygons.Select(GeometryV2.Bounds).Aggregate(RectangleF.Union);
        return Rectangle.Round(RectangleF.Intersect(RectangleF.Inflate(bounds, 12, 12), new RectangleF(PointF.Empty, size)));
    }

    private static void SaveCrop(Bitmap source, Rectangle crop, string path)
    {
        using var value = source.Clone(crop, PixelFormat.Format32bppArgb); value.Save(path);
    }

    private static string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    private static double Luminance(Color c) => .2126 * c.R + .7152 * c.G + .0722 * c.B;
    private static double Contrast(Color a, Color b) { var ratio = (Luminance(a) + 5) / (Luminance(b) + 5); return ratio < 1 ? 1 / ratio : ratio; }
}
