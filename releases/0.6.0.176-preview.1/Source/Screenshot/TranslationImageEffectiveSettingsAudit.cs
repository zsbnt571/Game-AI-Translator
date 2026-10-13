using System.Drawing.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class TranslationImageEffectiveSettingsAudit
{
    internal const string OutputEnvironmentVariable = "SCREENSHOT_TRANSLATOR_EFFECTIVE_RENDER_AUDIT";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static int _captureNumber;

    internal static void TryCapture(Bitmap source, IReadOnlyList<RecognitionRegion> regions,
        ApiSettings settings, RenderSettings current, RegionRenderResult currentResult)
    {
        var root = Environment.GetEnvironmentVariable(OutputEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(root)) return;
        try
        {
            var output = NextOutputDirectory(Path.GetFullPath(root));
            Directory.CreateDirectory(output);
            var policyOnly = new RenderSettings();
            FontSettingsPolicy.ApplyOverlay(policyOnly, settings);
            var clean = CreateCleanB(current);
            var cleanResult = RegionRendererV2.Render(source, regions, clean);
            try
            {
                source.Save(Path.Combine(output, "SOURCE.png"));
                currentResult.Bitmap.Save(Path.Combine(output, "CURRENT-EFFECTIVE.png"));
                cleanResult.Bitmap.Save(Path.Combine(output, "CLEAN-B.png"));
                var currentLayout = (currentResult.Diagnostics ?? []).Select(LayoutIdentity).ToArray();
                var cleanLayout = (cleanResult.Diagnostics ?? []).Select(LayoutIdentity).ToArray();
                var layoutIdentical = JsonSerializer.Serialize(currentLayout) == JsonSerializer.Serialize(cleanLayout);
                File.WriteAllText(Path.Combine(output, "CURRENT-EFFECTIVE-RenderSettings.json"),
                    JsonSerializer.Serialize(Snapshot(current, settings, policyOnly, false), JsonOptions));
                File.WriteAllText(Path.Combine(output, "CLEAN-B-RenderSettings.json"),
                    JsonSerializer.Serialize(Snapshot(clean, settings, policyOnly, true), JsonOptions));
                File.WriteAllText(Path.Combine(output, "AUDIT-SUMMARY.json"), JsonSerializer.Serialize(new
                {
                    capturedAtUtc = DateTime.UtcNow,
                    sourceSize = new { source.Width, source.Height }, regionCount = regions.Count,
                    identityGuarantees = new
                    {
                        sameSourceBitmap = true, sameRecognitionRegionObjects = true,
                        sameOcrTranslationAndAuthorization = true, sameWrapAndAutoFitAlgorithm = true,
                        sameBackgroundCleanupAlgorithm = true, sameBackgroundSettings = SameBackground(current, clean)
                    },
                    layoutIdentical,
                    layoutNote = layoutIdentical ? "Identical layout diagnostics." :
                        "A text-style difference changed fitted layout; both layouts are recorded instead of hiding the difference.",
                    currentLayout, cleanLayout, currentWarnings = currentResult.Warnings,
                    cleanWarnings = cleanResult.Warnings, realApiCallsAddedByAudit = 0,
                    legacyPreviewStyleLifecycle = Lifecycle()
                }, JsonOptions));
                TranslationImageVisualPipelineDiagnostics.Capture(source, regions, output);
            }
            finally { cleanResult.Bitmap.Dispose(); }
        }
        catch (Exception ex)
        {
            try { Directory.CreateDirectory(root!); File.WriteAllText(Path.Combine(root!, "AUDIT-ERROR.txt"), ex.ToString()); }
            catch { }
        }
    }

    private static RenderSettings CreateCleanB(RenderSettings current)
    {
        var profile = FontManager.ResolveTranslationImageProfile(new ApiSettings());
        return new RenderSettings
        {
            FontFamily = profile.PrimaryFamily, FontScale = 1F,
            TextColorMode = TextColorMode.Auto, CustomTextColor = Color.White,
            Shadow = false, Outline = false, StrokeWidth = RendererStrokeWidth.None,
            AutomaticOutlineColor = true, OutlineColor = Color.Black,
            MinFontSize = current.MinFontSize, LineSpacingScale = current.LineSpacingScale,
            CompactLineSpacingScale = current.CompactLineSpacingScale,
            SafeExpansionScale = current.SafeExpansionScale,
            Background = current.Background, BackgroundStrategy = current.BackgroundStrategy,
            BackgroundColor = current.BackgroundColor, BackgroundOpacity = current.BackgroundOpacity,
            LegacyTextRasterizationForDiagnostics = false
        };
    }

    private static object Snapshot(RenderSettings v, ApiSettings persisted, RenderSettings policy, bool clean)
    {
        var outlineDrawn = v.Outline && v.StrokeWidth != RendererStrokeWidth.None;
        return new
        {
            mode = clean ? "CLEAN B diagnostic override" : "CURRENT EFFECTIVE live Preview RenderSettings",
            settingsPath = ConfigurationManager.UserSettingsPath,
            values = new
            {
                fontFamily = Field(v.FontFamily, clean ? "CleanBOverride" : Source(v.FontFamily, policy.FontFamily, "FontSettingsPolicy.ApplyOverlay / TranslationImageTypographyProfile", "Preview Translation Style Session or live mutation")),
                resolvedFontFamily = Field(ResolveActualFamily(v.FontFamily), "System.Drawing.Font resolution"),
                fontScale = Field(v.FontScale, clean ? "CleanBOverride" : Source(v.FontScale, policy.FontScale, "RenderSettings default / persisted custom size", "Preview Translation Style Session")),
                fontSize = Field("Per-region value in AUDIT-SUMMARY layout", "TranslatedTextLayoutEngine.Fit"),
                textRenderingHint = Field(v.LegacyTextRasterizationForDiagnostics ? TextRenderingHint.ClearTypeGridFit.ToString() : TextRenderingHint.AntiAliasGridFit.ToString(), clean ? "CleanBOverride" : "RegionRendererV2 formal rasterizer"),
                shadowEnabled = Field(v.Shadow, clean ? "CleanBOverride" : Source(v.Shadow, policy.Shadow, "RenderSettings default", "Direct live mutation")),
                shadowOffset = Field(v.Shadow ? new { x = 1, y = 1 } : new { x = 0, y = 0 }, "RegionRendererV2.DrawStyled"),
                shadowAlpha = Field(v.Shadow ? 150 : 0, "RegionRendererV2.DrawStyled"),
                outlineEnabled = Field(v.Outline, clean ? "CleanBOverride" : Source(v.Outline, policy.Outline, "RenderSettings default", "Preview Translation Style Session")),
                outlineWidth = Field(v.StrokeWidth.ToString(), clean ? "CleanBOverride" : "RenderSettings/live value"),
                outlineColor = Field(Hex(v.OutlineColor), v.AutomaticOutlineColor ? "Automatic contrast" : "Preview Translation Style Session"),
                outlineAlpha = Field(outlineDrawn ? 220 : 0, "RegionRendererV2.DrawStyled"),
                textColorMode = Field(v.TextColorMode.ToString(), clean ? "CleanBOverride" : Source(v.TextColorMode, policy.TextColorMode, "RenderSettings default", "Preview Translation Style Session")),
                textColor = Field(Hex(v.CustomTextColor), clean ? "CleanBOverride" : "Custom value; used only in Custom mode"),
                textAlpha = Field(255, "RegionRendererV2 foreground brush"),
                fontStyle = Field("Role-dependent; BodyParagraph Regular", "TranslatedTextLayoutEngine"),
                lineSpacing = Field(new { v.LineSpacingScale, v.CompactLineSpacingScale }, "RenderSettings / layout engine"),
                backgroundRelatedTextOverrides = Field(new { v.Background, strategy = v.BackgroundStrategy.ToString(), v.BackgroundOpacity, color = Hex(v.BackgroundColor) }, clean ? "Copied from CURRENT EFFECTIVE" : "Policy plus Preview session"),
                complexBackgroundOverride = Field(v.LegacyTextRasterizationForDiagnostics, v.LegacyTextRasterizationForDiagnostics ? "Legacy diagnostic path" : clean ? "CleanBOverride: blocked" : "Formal default: disabled"),
                drawCount = Field(1 + (v.Shadow ? 1 : 0) + (outlineDrawn ? 4 : 0), "One body draw plus enabled effects")
            },
            persistedOverlaySubset = new { persisted.OverlayFontMode, persisted.OverlayFontFamily, persisted.OverlayFontSize, persisted.OverlayBackgroundStyle, persisted.OverlayBackgroundOpacity },
            cleanBIsolation = clean ? new { previewTranslationStyle = "blocked", persistedOverlayTextStyle = "blocked", sessionStyle = "blocked", complexBackgroundTextOverride = "blocked" } : null
        };
    }

    private static object Lifecycle() => new
    {
        persistedToConfiguration = false, inheritedAcrossPreviewInstances = false,
        lifetime = "Current PreviewForm instance only",
        styleDialogMutates = new[] { "TextColorMode", "CustomTextColor", "Outline", "OutlineColor", "AutomaticOutlineColor", "Background", "BackgroundColor", "BackgroundOpacity", "FontScale" },
        appearanceRefreshReapplies = new[] { "FontFamily", "FontScale only for persisted Custom mode", "BackgroundStrategy", "Background", "BackgroundOpacity" },
        appearanceRefreshDoesNotReset = new[] { "TextColorMode", "CustomTextColor", "Outline", "OutlineColor", "AutomaticOutlineColor", "BackgroundColor", "Shadow", "FontScale outside persisted Custom mode" },
        restoreDefaultScope = "Global ApiSettings; live Preview RenderSettings is not reconstructed",
        precedence = "Style dialog mutates live RenderSettings after ApplyOverlay; appearance refresh replaces only a subset",
        duplicateSourcesOfTruth = new[] { "TranslationImageTypographyProfile / persisted overlay subset", "Preview Translation Style Session / live RenderSettings" }
    };

    private static object Field<T>(T value, string source) => new { value, source };
    private static string NextOutputDirectory(string root)
    {
        Directory.CreateDirectory(root);
        while (true)
        {
            var candidate = Path.Combine(root, $"capture-{Interlocked.Increment(ref _captureNumber):D2}");
            if (!Directory.Exists(candidate)) return candidate;
        }
    }
    private static string Source<T>(T actual, T policy, string policySource, string mutationSource) => EqualityComparer<T>.Default.Equals(actual, policy) ? policySource : mutationSource;
    private static string ResolveActualFamily(string family) { try { using var f = new Font(family, 10F, FontStyle.Regular, GraphicsUnit.Pixel); return f.Name; } catch { return FontFamily.GenericSansSerif.Name; } }
    private static string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    private static bool SameBackground(RenderSettings a, RenderSettings b) => a.Background == b.Background && a.BackgroundStrategy == b.BackgroundStrategy && a.BackgroundColor.ToArgb() == b.BackgroundColor.ToArgb() && a.BackgroundOpacity == b.BackgroundOpacity;
    private static object LayoutIdentity(RegionRenderDiagnostic x) => new { x.RegionId, role = x.Role.ToString(), status = x.RenderStatus.ToString(), x.FontFamily, x.FontSize, x.LineCount, x.RenderBounds, x.TranslationRenderRect, lines = x.RenderedLines.Select(l => new { l.Text, l.Bounds }).ToArray() };
}
