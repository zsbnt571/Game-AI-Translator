using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class TranslationImageTextRenderingDiagnostics
{
    private static readonly int[] Sizes = [14, 18, 24, 32, 40, 48];
    private static readonly string[] Samples =
    [
        "我的现金被拒了，所以我现在用……",
        "生活曾经很简单。",
        "截图翻译测试：你好，世界！",
        "April说：“Hello!” 价格是5000円 → 100%",
        "繁體中文：畫面文字清晰測試。",
        "日本語：スクリーン翻訳テスト。",
        "한국어: 화면 번역 선명도 테스트."
    ];

    private sealed record Variant(string Id, string Label, string Rasterizer, string Hint, string Flags, int DrawCount);
    private static readonly Variant[] Variants =
    [
        new("A-cleartype-drawstring", "A ClearType DrawString", "Graphics.DrawString", "ClearTypeGridFit", "N/A", 1),
        new("B-antialias-drawstring", "B AntiAlias DrawString", "Graphics.DrawString", "AntiAliasGridFit", "N/A", 1),
        new("C-gdi-textrenderer", "C GDI TextRenderer", "TextRenderer.DrawText", "GDI font quality", "NoPadding | NoPrefix | SingleLine | PreserveGraphicsClipping", 1),
        new("D-graphicspath-fill", "D GraphicsPath Fill", "GraphicsPath.AddString + FillPath", "SmoothingMode.HighQuality", "No DrawPath", 1)
    ];

    private enum BackgroundKind { Dark, Light, Complex }

    public static int DumpEffectiveConfiguration(string output)
    {
        Directory.CreateDirectory(output);
        var path = ConfigurationManager.UserSettingsPath;
        var settings = ConfigurationManager.Load(path, false);
        var render = new RenderSettings();
        var initial = Snapshot(render);
        FontSettingsPolicy.ApplyOverlay(render, settings);
        var afterOverlay = Snapshot(render);
        var profile = FontManager.ResolveTranslationImageProfile(settings);
        var audit = new
        {
            settingsPath = path,
            persisted = new
            {
                settings.OverlayFontMode,
                settings.OverlayFontFamily,
                settings.OverlayFontSize,
                settings.OverlayBackgroundStyle,
                settings.OverlayBackgroundOpacity
            },
            profile = new { profile.RequestedFamily, profile.PrimaryFamily, profile.FallbackFamilies, profile.Size },
            stages = new[]
            {
                new { stage = "RenderSettings constructor", values = initial },
                new { stage = "FontSettingsPolicy.ApplyOverlay", values = afterOverlay }
            },
            previewSessionStylePersistence = new
            {
                persistedToConfiguration = false,
                inheritedAcrossPreviewInstances = false,
                lifetime = "current PreviewForm instance only",
                appearanceRefreshReapplies = new[] { "FontFamily", "FontScale when persisted custom font size > 0", "BackgroundStrategy", "BackgroundOpacity" },
                appearanceRefreshDoesNotReset = new[] { "TextColorMode", "CustomTextColor", "Outline", "AutomaticOutlineColor", "OutlineColor", "Background flag", "BackgroundColor", "FontScale when persisted mode is not custom", "Shadow" },
                sourceOfTruth = "RenderSettings is mutated in place by ShowStyleDialog; ApplyAppearanceSettings mutates only the overlay subset"
            }
        };
        File.WriteAllText(Path.Combine(output, "effective-configuration.json"),
            JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static object Snapshot(RenderSettings s) => new
    {
        s.FontFamily, s.FontScale, s.MinFontSize, textRenderingHint = s.LegacyTextRasterizationForDiagnostics ? "ClearTypeGridFit" : "AntiAliasGridFit",
        s.Shadow, shadowOffset = new { x = s.Shadow ? 1 : 0, y = s.Shadow ? 1 : 0 }, shadowAlpha = s.Shadow ? 150 : 0,
        s.Outline, s.StrokeWidth, s.OutlineColor, outlineAlpha = s.Outline ? 220 : 0,
        s.TextColorMode, s.CustomTextColor, textAlpha = 255,
        fontStyle = "role-dependent; BodyParagraph=Regular", s.LineSpacingScale, s.CompactLineSpacingScale,
        s.Background, s.BackgroundStrategy, s.BackgroundOpacity, s.BackgroundColor,
        complexBackgroundOverride = s.LegacyTextRasterizationForDiagnostics,
        defaultDrawCount = 1 + (s.Shadow ? 1 : 0) + (s.Outline ? 4 : 0)
    };

    public static int RunOldNewApril(string output, string sourcePath)
    {
        Directory.CreateDirectory(output);
        using var source = new Bitmap(sourcePath);
        var regions = BuildAprilEvidenceRegions(source.Size);
        var settings = new ApiSettings();
        var profile = FontManager.ResolveTranslationImageProfile(settings);
        var common = new RenderSettings
        {
            FontFamily = profile.PrimaryFamily,
            Shadow = false,
            Outline = false,
            BackgroundStrategy = TranslationOverlayBackgroundStyle.Automatic
        };
        common.LegacyTextRasterizationForDiagnostics = true;
        var oldResult = RegionRendererV2.Render(source, regions, common);
        common.LegacyTextRasterizationForDiagnostics = false;
        var newResult = RegionRendererV2.Render(source, regions, common);
        using (oldResult.Bitmap) oldResult.Bitmap.Save(Path.Combine(output, "April-OLD-current-renderer.png"));
        using (newResult.Bitmap) newResult.Bitmap.Save(Path.Combine(output, "April-NEW-antialias-single-draw.png"));
        source.Save(Path.Combine(output, "April-SOURCE.png"));

        var oldLayout = oldResult.Diagnostics!.Select(LayoutIdentity).ToArray();
        var newLayout = newResult.Diagnostics!.Select(LayoutIdentity).ToArray();
        var oldJson = JsonSerializer.Serialize(oldLayout);
        var newJson = JsonSerializer.Serialize(newLayout);
        if (!string.Equals(oldJson, newJson, StringComparison.Ordinal))
            throw new InvalidOperationException("OLD/NEW layout identity differs; evidence rejected.");
        var evidence = new
        {
            source = sourcePath,
            sourceSize = source.Size,
            realApiCalls = 0,
            sameRecognitionInput = true,
            sameTranslation = true,
            sameRegions = true,
            sameLayout = true,
            sameFontSize = true,
            sameRenderRect = true,
            sameBackground = true,
            fontProfile = new { profile.PrimaryFamily, profile.FallbackFamilies },
            oldRasterizer = new { hint = "ClearTypeGridFit", forcedComplexOutline = true, shadow = false, explicitOutline = false },
            newRasterizer = new { hint = "AntiAliasGridFit", forcedComplexOutline = false, shadow = false, outline = false, foregroundDrawCount = 1 },
            regions = regions.Select(r => new { r.RegionId, r.OcrText, r.TranslationText, r.BoundingBox, r.RoleType }),
            layout = newLayout,
            oldWarnings = oldResult.Warnings,
            newWarnings = newResult.Warnings
        };
        File.WriteAllText(Path.Combine(output, "April-OLD-NEW-evidence.json"),
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static object LayoutIdentity(RegionRenderDiagnostic d) => new
    {
        d.RegionId, d.Role, d.TranslatedLength, d.RenderStatus, d.FontSize, d.LineCount,
        d.FontFamily, d.RenderBounds, d.TranslationRenderRect,
        lines = d.RenderedLines.Select(x => new { x.Text, x.Bounds })
    };

    private static IReadOnlyList<RecognitionRegion> BuildAprilEvidenceRegions(Size size)
    {
        var sx = size.Width / 1097f;
        var sy = size.Height / 1440f;
        RecognitionRegion Make(string id, RectangleF reference, RegionRoleType role, string source, string translation)
        {
            var rect = new RectangleF(reference.X * sx, reference.Y * sy, reference.Width * sx, reference.Height * sy);
            return new RecognitionRegion
            {
                RegionId = id,
                Polygon = GeometryV2.RectanglePolygon(rect),
                RendererTargetRegion = rect,
                RoleType = role,
                OcrText = source,
                StructuredText = source,
                TranslationText = translation,
                SourceBlockIds = [$"{id}-LINE-1"],
                SourceLinePolygons = [GeometryV2.RectanglePolygon(rect)],
                CoverageValid = true
            };
        }
        return
        [
            Make("APRIL-TITLE", new RectangleF(187, 72, 820, 54), RegionRoleType.Header,
                "18:30 | August 01, 2026 | April's Dorm Room, BCT University",
                "18:30｜2026年8月1日｜April 的大学宿舍"),
            Make("APRIL-BODY-1", new RectangleF(187, 132, 820, 288), RegionRoleType.BodyParagraph,
                "April somehow convinced Yan Yi to go on a party/date with him, just to remind old times.",
                "April 不知怎么说服了颜易陪她参加派对约会，只是为了重温旧时光。四月的宿舍里弥漫着香草身体喷雾与香水的气味。"),
            Make("APRIL-DIALOGUE-1", new RectangleF(187, 450, 820, 90), RegionRoleType.Dialogue,
                "Fuuuck... I look fuckable as hell right now.",
                "“操……”她低声说道，“我现在看起来真要命。”"),
            Make("APRIL-BODY-2", new RectangleF(187, 560, 820, 300), RegionRoleType.BodyParagraph,
                "And she was right. The top was black, cropped so short it barely kissed the bottom of her ribs.",
                "她说得没错。上衣是黑色的，短得几乎只触到肋骨下缘；牛仔短裤剪裁很高，随着每次动作都显得格外醒目。"),
            Make("APRIL-DIALOGUE-2", new RectangleF(187, 1190, 820, 150), RegionRoleType.Dialogue,
                "Well, well, well... Look who actually showed up. Get in before someone sees me.",
                "“哎呀，看看是谁真的来了。进来吧，免得有人看见我。”")
        ];
    }

    public static int Run(string output, string? complexBackgroundPath)
    {
        Directory.CreateDirectory(output);
        var requested = FontManager.IsInstalled("Microsoft YaHei UI") ? "Microsoft YaHei UI" : "Microsoft YaHei";
        if (!FontManager.IsInstalled(requested)) throw new InvalidOperationException("Microsoft YaHei UI / Microsoft YaHei is not installed.");
        using var proofFont = new Font(requested, 14, FontStyle.Regular, GraphicsUnit.Pixel);
        var actualFamily = proofFont.Name;
        using var complex = !string.IsNullOrWhiteSpace(complexBackgroundPath) && File.Exists(complexBackgroundPath)
            ? new Bitmap(complexBackgroundPath) : null;

        const int cellWidth = 640;
        var rowHeights = Sizes.Select(RowHeight).ToArray();
        var sectionHeight = 48 + rowHeights.Sum();
        using var master = new Bitmap(cellWidth * Variants.Length, sectionHeight * 3);
        master.SetResolution(96, 96);
        using (var g = Graphics.FromImage(master))
        {
            g.Clear(Color.Black);
            var section = 0;
            foreach (var background in new[] { BackgroundKind.Dark, BackgroundKind.Light, BackgroundKind.Complex })
            {
                var sectionTop = section * sectionHeight;
                DrawSectionHeader(g, background, sectionTop, master.Width, actualFamily, complexBackgroundPath);
                var y = sectionTop + 48;
                for (var row = 0; row < Sizes.Length; row++)
                {
                    var size = Sizes[row];
                    for (var column = 0; column < Variants.Length; column++)
                    {
                        var bounds = new Rectangle(column * cellWidth, y, cellWidth, rowHeights[row]);
                        DrawCell(g, bounds, Variants[column], background, complex, actualFamily, size);
                    }
                    y += rowHeights[row];
                }
                section++;
            }
        }
        var masterPath = Path.Combine(output, "translation-image-final-sharpness-ab.png");
        master.Save(masterPath);

        foreach (var background in new[] { BackgroundKind.Dark, BackgroundKind.Light, BackgroundKind.Complex })
        {
            using var sheet = master.Clone(new Rectangle(0, (int)background * sectionHeight, master.Width, sectionHeight), master.PixelFormat);
            sheet.SetResolution(96, 96);
            sheet.Save(Path.Combine(output, $"translation-image-final-sharpness-{background.ToString().ToLowerInvariant()}.png"));
        }

        using var measureBitmap = new Bitmap(1000, 200);
        measureBitmap.SetResolution(96, 96);
        using var measureGraphics = Graphics.FromImage(measureBitmap);
        var measurements = Sizes.Select(size => Measure(actualFamily, size, measureGraphics)).ToArray();
        var manifest = new
        {
            status = "diagnostic-only",
            requestedFamily = requested,
            actualCreatedFontFamily = actualFamily,
            fallback = "Microsoft YaHei",
            sizesPx = Sizes,
            textOrigin = new { x = 16, y = 36, integerAligned = true },
            canvas = new { dpi = 96, pixelScale = "1:1", resize = false, intermediateBitmap = false, transform = false, alpha = 255 },
            complexBackground = complexBackgroundPath,
            variants = Variants,
            textRendererFlags = (TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsClipping).ToString(),
            measurements,
            productionCoordinateAudit = new
            {
                source = "Renderer2Engine.cs DrawLines",
                xBeforeDraw = "MathF.Round(x)",
                yBeforeDraw = "MathF.Round(y)",
                fractionalDrawPosition = false,
                fontSizeMayBeFractional = true,
                postDrawScale = false,
                fractionalPositionComparisonGenerated = false,
                reason = "Production draw coordinates are already integer-aligned."
            }
        };
        File.WriteAllText(Path.Combine(output, "translation-image-final-sharpness-manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static object Measure(string family, int size, Graphics g)
    {
        const string sample = "截图翻译测试：你好，世界！";
        using var font = new Font(family, size, FontStyle.Regular, GraphicsUnit.Pixel);
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var drawString = g.MeasureString(sample, font, int.MaxValue, StringFormat.GenericTypographic);
        var defaultGdi = TextRenderer.MeasureText(g, sample, font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        var noPaddingGdi = TextRenderer.MeasureText(g, sample, font, Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsClipping);
        return new
        {
            sizePx = size,
            graphicsMeasureString = new { width = drawString.Width, height = drawString.Height },
            textRendererDefault = new { width = defaultGdi.Width, height = defaultGdi.Height },
            textRendererNoPadding = new { width = noPaddingGdi.Width, height = noPaddingGdi.Height },
            noPaddingDelta = new { width = noPaddingGdi.Width - defaultGdi.Width, height = noPaddingGdi.Height - defaultGdi.Height }
        };
    }

    private static int RowHeight(int size) => 42 + Samples.Length * Math.Max(size + 10, (int)Math.Ceiling(size * 1.42f));

    private static void DrawSectionHeader(Graphics g, BackgroundKind background, int y, int width, string family, string? complexPath)
    {
        using var fill = new SolidBrush(Color.FromArgb(12, 14, 18));
        g.FillRectangle(fill, 0, y, width, 48);
        using var font = new Font("Segoe UI", 18, FontStyle.Bold, GraphicsUnit.Pixel);
        var suffix = background == BackgroundKind.Complex ? $" | source={Path.GetFileName(complexPath) ?? "generated fallback"}" : "";
        g.DrawString($"{background.ToString().ToUpperInvariant()} BACKGROUND | actual FontFamily={family} | 96 DPI | 1:1{suffix}", font, Brushes.White, 12, y + 12);
    }

    private static void DrawCell(Graphics g, Rectangle bounds, Variant variant, BackgroundKind background, Bitmap? complex, string family, int size)
    {
        PaintBackground(g, bounds, background, complex);
        using var border = new Pen(background == BackgroundKind.Light ? Color.Silver : Color.FromArgb(75, 82, 94));
        g.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        var foreground = background == BackgroundKind.Light ? Color.FromArgb(20, 23, 28) : Color.White;
        using var labelFont = new Font("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
        using var labelBack = new SolidBrush(background == BackgroundKind.Light ? Color.FromArgb(225, 255, 255, 255) : Color.FromArgb(180, 8, 10, 14));
        g.FillRectangle(labelBack, bounds.X + 1, bounds.Y + 1, bounds.Width - 2, 28);
        using var labelBrush = new SolidBrush(background == BackgroundKind.Light ? Color.Black : Color.White);
        g.DrawString($"{variant.Label} | {size}px | {family}", labelFont, labelBrush, bounds.X + 8, bounds.Y + 7);

        using var font = new Font(family, size, FontStyle.Regular, GraphicsUnit.Pixel);
        var lineHeight = Math.Max(size + 10, (int)Math.Ceiling(font.GetHeight(g) * 1.25f));
        var x = bounds.X + 16;
        var y = bounds.Y + 36;
        foreach (var text in Samples)
        {
            DrawVariant(g, variant.Id, text, font, foreground, new Point(x, y));
            y += lineHeight;
        }
    }

    private static void PaintBackground(Graphics g, Rectangle bounds, BackgroundKind kind, Bitmap? complex)
    {
        if (kind == BackgroundKind.Dark) { using var b = new SolidBrush(Color.FromArgb(38, 42, 48)); g.FillRectangle(b, bounds); return; }
        if (kind == BackgroundKind.Light) { using var b = new SolidBrush(Color.FromArgb(246, 247, 249)); g.FillRectangle(b, bounds); return; }
        if (complex is not null)
        {
            var sourceX = Math.Max(0, Math.Min(complex.Width - bounds.Width, (bounds.X / Math.Max(1, bounds.Width)) * 97));
            var sourceY = Math.Max(0, Math.Min(complex.Height - bounds.Height, (bounds.Y / Math.Max(1, bounds.Height)) * 53));
            var copyWidth = Math.Min(bounds.Width, complex.Width - sourceX);
            var copyHeight = Math.Min(bounds.Height, complex.Height - sourceY);
            g.DrawImageUnscaledAndClipped(complex, new Rectangle(bounds.X - sourceX, bounds.Y - sourceY, complex.Width, complex.Height));
            if (copyWidth == bounds.Width && copyHeight == bounds.Height) return;
        }
        using var fallback = new LinearGradientBrush(bounds, Color.FromArgb(31, 79, 104), Color.FromArgb(122, 48, 84), 28f);
        g.FillRectangle(fallback, bounds);
    }

    private static void DrawVariant(Graphics g, string variant, string text, Font font, Color color, Point origin)
    {
        switch (variant)
        {
            case "A-cleartype-drawstring":
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                using (var brush = new SolidBrush(color)) g.DrawString(text, font, brush, origin.X, origin.Y, StringFormat.GenericTypographic);
                break;
            case "B-antialias-drawstring":
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (var brush = new SolidBrush(color)) g.DrawString(text, font, brush, origin.X, origin.Y, StringFormat.GenericTypographic);
                break;
            case "C-gdi-textrenderer":
                TextRenderer.DrawText(g, text, font, origin, color,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsClipping);
                break;
            case "D-graphicspath-fill":
                g.SmoothingMode = SmoothingMode.HighQuality;
                using (var family = new FontFamily(font.Name))
                using (var format = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.None })
                using (var path = new GraphicsPath())
                using (var brush = new SolidBrush(color))
                {
                    path.AddString(text, family, (int)font.Style, font.Size, origin, format);
                    g.FillPath(brush, path);
                }
                break;
        }
    }
}
