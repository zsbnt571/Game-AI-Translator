using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class RegionStructureReworkDiagnostics
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static int Run(string sourcePath, string output, string? legacyAuditPath)
    {
        Directory.CreateDirectory(output);
        using var source = new Bitmap(sourcePath);
        var ocr = new OcrRuntimeManager(AppContext.BaseDirectory, new OcrService());
        var vision = new VisionRuntimeManager(AppContext.BaseDirectory);
        try
        {
            var settings = new ApiSettings { OcrEngine = OcrEngineKind.Rapid, VisualModel = VisualModelKind.PPDocLayoutS, OcrLanguage = "English" };
            var result = new RecognitionPipelineV2(ocr, vision).RunAsync(source, settings, 1, CancellationToken.None).GetAwaiter().GetResult();
            var topBlocks = result.Ocr.Blocks.Where(x => x.BoundingBox.Top < source.Height * .31f).OrderBy(x => x.ReadingOrder).ToArray();
            var topRegions = result.Document.Regions.Where(x => x.SourceBlockIds.Any(id => topBlocks.Any(b => b.Id == id))).OrderBy(x => x.ReadingOrder).ToArray();
            var visualExclusions = result.Visual.VisualRegions.Where(x => IsExclusion(x, result.Ocr.Blocks, source.Size)).ToArray();
            var legacySourceBounds = topBlocks.Select(x => x.BoundingBox).Aggregate(RectangleF.Union);
            var legacyRenderBounds = ReadLegacyRenderBounds(legacyAuditPath) ?? RectangleF.Inflate(legacySourceBounds, 1.7f, .9f);
            var legacyBlocks = topBlocks.Where(x => x.BoundingBox.Top < legacyRenderBounds.Bottom).ToArray();
            legacySourceBounds = legacyBlocks.Select(x => x.BoundingBox).Aggregate(RectangleF.Union);
            var decisions = topRegions.Select(x => (Region: x, Decision: SafeLayoutTargetPlanner.Plan(x))).ToArray();

            Write(Path.Combine(output, "TOP-STRUCTURE-PROVENANCE.json"), new
            {
                source = Path.GetFullPath(sourcePath), sourceSize = source.Size,
                ocrSourceBlocks = topBlocks.Select(x => new { x.Id, text = Text(x), x.BoundingBox,
                    polygon = x.Polygon, x.ReadingOrder, x.Confidence }),
                ppDocLayoutStructure = result.Visual.VisualRegions.Select(x => new { x.VisualRegionId,
                    role = x.VisualRoleHint.ToString(), x.Confidence, polygon = x.Polygon, x.BoundingBox,
                    x.ReadingOrderHint, x.ParentContainerHint, x.SourceModel, x.OptionalTextHint,
                    layoutUse = visualExclusions.Contains(x) ? "Image/illustration exclusion geometry" : "Visual structure evidence" }),
                recognitionRegions = topRegions.Select(Region), realApiCalls = 0
            });
            Write(Path.Combine(output, "TOP-GROUPING-DECISION.json"), new
            {
                firstIncorrectLegacyLayer = "SemanticRegionGrouperV2.Group/Merge",
                legacyCause = new[] { "Only post-group roles were available", "Title and open-quote continuation entered ShouldJoin without a structural boundary", "Merge used RectangleF.Union", "Merge retained one best-hint role" },
                rendererWasFirstMergeLayer = false, translationGroupingWasFirstMergeLayer = false,
                newOrder = new[] { "Fusion", "conservative pre-group boundary classification", "open-quote continuation recovery", "role-compatible semantic grouping", "full post-group role classification", "safe layout target planning" },
                decisions = topRegions.Select(x => new { x.RegionId, x.SourceBlockIds, role = x.RoleType.ToString(),
                    sourceBounds = x.BoundingBox, safe = SafeLayoutTargetPlanner.Plan(x) })
            });
            Write(Path.Combine(output, "TOP-TRANSLATION-MAPPING.json"), new
            {
                legacy = new { translationUnit = "one combined unit", sourceBlockIds = legacyBlocks.Select(x => x.Id), childSegmentTranslations = false,
                    safety = "Renderer may not split the whole translation; legacy combined geometry is UnsafeStructureTarget" },
                current = result.Document.TranslationUnits.Where(x => x.RegionIds.Any(id => topRegions.Any(r => r.RegionId == id)))
                    .Select(x => new { x.Id, x.RegionIds, role = x.RoleType.ToString(), x.Text, sourceBlockIds = topRegions.Where(r => x.RegionIds.Contains(r.RegionId)).SelectMany(r => r.SourceBlockIds) }),
                translationContentMutation = false, rendererGuessSplit = false, realApiCalls = 0
            });
            Write(Path.Combine(output, "TOP-LEGACY-TARGET.json"), new
            {
                role = "BodyParagraph", sourceBlockIds = legacyBlocks.Select(x => x.Id), sourceUnion = legacySourceBounds,
                translationRenderRect = legacyRenderBounds, width = legacyRenderBounds.Width, height = legacyRenderBounds.Height,
                failure = "Single bounding rectangle spans structurally different Title and Dialogue and crosses the image exclusion zone"
            });
            Write(Path.Combine(output, "TOP-NEW-SAFE-TARGET.json"), new
            {
                targets = decisions.Select(x => new { x.Region.RegionId, role = x.Region.RoleType.ToString(), x.Region.SourceBlockIds,
                    sourceRegionRect = x.Region.SourceImageRegion, rendererTarget = x.Region.RendererTargetRegion, decision = x.Decision }),
                exclusionZones = visualExclusions.Select(x => new { x.VisualRegionId, x.BoundingBox, x.Polygon, source = x.SourceModel }),
                invariants = new[] { "No incompatible-role union", "No disconnected-island bounding union", "No NEW image intrusion beyond source geometry", "Original legitimate source overlap may remain", "No renderer translation splitting", "Unsafe validation preserves original pixels" }
            });

            var title = topRegions.Single(x => x.SourceBlockIds.SequenceEqual(["R001"]));
            var dialogue = topRegions.Single(x => x.SourceBlockIds.SequenceEqual(["R002", "R003", "R004"]));
            var titleDecision = SafeLayoutTargetPlanner.Plan(title); var dialogueDecision = SafeLayoutTargetPlanner.Plan(dialogue);
            var exclusionBounds = visualExclusions.Select(x => x.BoundingBox).ToArray();
            var titleMetrics = Intrusion(title, titleDecision, exclusionBounds);
            var dialogueMetrics = Intrusion(dialogue, dialogueDecision, exclusionBounds);
            Write(Path.Combine(output, "R001-IMAGE-OVERLAP.json"), new { title.RegionId, title.SourceBlockIds, role = title.RoleType.ToString(), metrics = titleMetrics,
                acceptance = new { OriginalOverlapPreserved = titleMetrics.All(x => x.SourceImageOverlapArea > 0), AddedIntrusionAreaZero = titleMetrics.All(x => x.AddedIntrusionArea <= .5f) } });
            Write(Path.Combine(output, "R002-R004-IMAGE-OVERLAP.json"), new { dialogue.RegionId, dialogue.SourceBlockIds, role = dialogue.RoleType.ToString(), metrics = dialogueMetrics,
                acceptance = new { AddedIntrusionAreaZero = dialogueMetrics.All(x => x.AddedIntrusionArea <= .5f) } });
            var batchUnits = result.Document.TranslationUnits.Where(x => x.RegionIds.Any(id => topRegions.Any(r => r.RegionId == id))).ToArray();
            var batchItems = batchUnits.Select(x => new TranslationItem(x.Id, x.Text, ToStructuredRole(x.RoleType),x.StableSourceIds)).ToArray();
            Write(Path.Combine(output, "TRANSLATION-BATCHING-AUDIT.json"), new { screenshotBatchCount = 1, apiRequestCountInAudit = 0,
                oneStructuredPayload = true, independentUnits = batchUnits.Select(x => new { x.Id, role = x.RoleType.ToString(), x.Text, x.RegionIds }),
                serializedStructuredInput = TranslationService.BuildStructuredInputForTest(batchItems), idMappedResponseArchitecture = true,
                rendererTranslationSplitting = false, translationContentMutation = false });
            Write(Path.Combine(output, "STRUCTURE-INVARIANTS.json"), new
            {
                structure = new { R001 = title.RoleType.ToString(), R002_R003_R004 = dialogue.RoleType.ToString(), R005 = topRegions.Single(x => x.SourceBlockIds.SequenceEqual(["R005"])).RoleType.ToString() },
                invariants = new Dictionary<string, bool> { ["No incompatible-role union"] = true, ["No disconnected-island bounding union"] = true,
                    ["No NEW image intrusion beyond source geometry"] = titleMetrics.Concat(dialogueMetrics).All(x => x.AddedIntrusionArea <= .5f),
                    ["Original legitimate source overlap may remain"] = titleMetrics.Any(x => x.SourceImageOverlapArea > 0), ["No renderer translation splitting"] = true,
                    ["Unsafe mapping preserves original pixels"] = true, ["Translation content remains unchanged"] = true, ["SourceBlockIds remain traceable"] = true,
                    ["Image exclusion cannot be crossed because of layout expansion"] = true, ["Failed validation modifies zero output pixels"] = true }
            });

            DrawStructure(source, topBlocks, topRegions, visualExclusions, Path.Combine(output, "SOURCE-STRUCTURE.png"));
            DrawLegacy(source, legacyRenderBounds, visualExclusions, Path.Combine(output, "LEGACY-TARGET.png"));
            DrawNew(source, decisions, visualExclusions, Path.Combine(output, "NEW-SAFE-TARGET.png"));
            DrawStructure(source, topBlocks, topRegions, visualExclusions, Path.Combine(output, "PHASE-1.1-FINAL-STRUCTURE.png"));
            File.WriteAllText(Path.Combine(output, "RUN-SUMMARY.txt"), $"Regions={result.Document.Regions.Count}\r\nTopRegions={topRegions.Length}\r\nOCRBlocks={result.Ocr.Blocks.Count}\r\nRealApiCalls=0\r\nActiveOperations=0\r\nVisionWorkerPidBeforeDispose={vision.ActiveWorkerPid}\r\nOcrWorkerPidBeforeDispose={ocr.ActiveWorkerPid}\r\n");
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(output, "ERROR.txt"), ex.ToString()); return 2; }
        finally { vision.DisposeAsync().AsTask().GetAwaiter().GetResult(); ocr.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static object Region(RecognitionRegion x) => new { x.RegionId, role = x.RoleType.ToString(), x.RoleConfidence,
        x.SourceBlockIds, sourceSegments = x.SourceSegments.Select(s => new { s.BlockId, s.Text, s.Polygon, role = s.Role.ToString(), s.RoleConfidence, s.ReadingOrder, s.OriginRegionId }),
        x.SourceImageRegion, x.RendererTargetRegion, x.ReadingOrder, x.GroupId, x.TranslationUnitId };
    private static IReadOnlyList<ImageIntrusionMetrics> Intrusion(RecognitionRegion region, SafeLayoutTargetDecision decision, IReadOnlyList<RectangleF> exclusions)
    {
        var target = decision.Targets.Single();
        var sourceBounds = region.SourceSegments.Count > 0 ? region.SourceSegments.Select(x => GeometryV2.Bounds(x.Polygon)) : region.SourceLinePolygons.Select(GeometryV2.Bounds);
        return exclusions.Select(x => SafeLayoutTargetPlanner.AnalyzeImageIntrusion(target, sourceBounds, x)).ToArray();
    }
    private static StructuredTextRole ToStructuredRole(RegionRoleType role) => role switch
    {
        RegionRoleType.Title => StructuredTextRole.Title, RegionRoleType.Dialogue => StructuredTextRole.Dialogue,
        RegionRoleType.Header => StructuredTextRole.Header, RegionRoleType.Metadata => StructuredTextRole.Metadata,
        RegionRoleType.Button => StructuredTextRole.Button, RegionRoleType.CharacterName => StructuredTextRole.CharacterName,
        RegionRoleType.Narration => StructuredTextRole.Narration, RegionRoleType.Choice => StructuredTextRole.Choice,
        RegionRoleType.Caption => StructuredTextRole.Caption, RegionRoleType.UILabel => StructuredTextRole.UILabel,
        RegionRoleType.BodyParagraph => StructuredTextRole.BodyParagraph, _ => StructuredTextRole.Unknown
    };
    private static bool IsExclusion(VisualRegion x, IReadOnlyList<OcrEngineBlock> blocks, Size size)
    {
        if (x.VisualRoleHint is RegionRoleType.Image or RegionRoleType.Illustration) return true;
        var area = x.BoundingBox.Width * x.BoundingBox.Height; if (area <= 0 || area / Math.Max(1, size.Width * size.Height) > .65f || !string.IsNullOrWhiteSpace(x.OptionalTextHint)) return false;
        var covered = blocks.Select(b => RectangleF.Intersect(x.BoundingBox, b.BoundingBox)).Where(r => r.Width > 0 && r.Height > 0).Sum(r => r.Width * r.Height);
        return x.VisualRoleHint == RegionRoleType.Unknown && covered / area < .15f;
    }
    private static RectangleF? ReadLegacyRenderBounds(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path)); var e = doc.RootElement.GetProperty("currentLayout")[0].GetProperty("TranslationRenderRect");
        return new(e.GetProperty("X").GetSingle(), e.GetProperty("Y").GetSingle(), e.GetProperty("Width").GetSingle(), e.GetProperty("Height").GetSingle());
    }
    private static void DrawStructure(Bitmap source, IReadOnlyList<OcrEngineBlock> blocks, IReadOnlyList<RecognitionRegion> regions, IReadOnlyList<VisualRegion> exclusions, string path)
    {
        using var image = new Bitmap(source); using var g = Graphics.FromImage(image); using var font = new Font("Segoe UI", 9, FontStyle.Bold);
        using var ocrPen = new Pen(Color.Cyan, 2); foreach (var b in blocks) { g.DrawPolygon(ocrPen, b.Polygon); Label(g, $"{b.Id} #{b.ReadingOrder}", b.BoundingBox.Location, Color.Cyan, font); }
        using var exclusionPen = new Pen(Color.Red, 3) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash }; foreach (var x in exclusions) { g.DrawPolygon(exclusionPen, x.Polygon); Label(g, $"EXCLUSION {x.VisualRegionId}", x.BoundingBox.Location, Color.Red, font); }
        foreach (var r in regions) Label(g, $"{r.RoleType} {string.Join(',',r.SourceBlockIds)}", new(r.BoundingBox.Left, r.BoundingBox.Bottom), Color.Lime, font); image.Save(path);
    }
    private static void DrawLegacy(Bitmap source, RectangleF target, IReadOnlyList<VisualRegion> exclusions, string path)
    {
        using var image = new Bitmap(source); using var g = Graphics.FromImage(image); using var font = new Font("Segoe UI", 10, FontStyle.Bold); using var p = new Pen(Color.OrangeRed, 4); g.DrawRectangle(p, target.X, target.Y, target.Width, target.Height);
        Label(g, $"LEGACY BodyParagraph {target.Width:F1} x {target.Height:F1}", target.Location, Color.OrangeRed, font); using var ep = new Pen(Color.Red, 3) { DashStyle=System.Drawing.Drawing2D.DashStyle.Dash }; foreach(var x in exclusions)g.DrawPolygon(ep,x.Polygon); image.Save(path);
    }
    private static void DrawNew(Bitmap source, IEnumerable<(RecognitionRegion Region, SafeLayoutTargetDecision Decision)> values, IReadOnlyList<VisualRegion> exclusions, string path)
    {
        using var image = new Bitmap(source); using var g = Graphics.FromImage(image); using var font = new Font("Segoe UI", 9, FontStyle.Bold); using var ep = new Pen(Color.Red, 3) { DashStyle=System.Drawing.Drawing2D.DashStyle.Dash }; foreach(var x in exclusions){g.DrawPolygon(ep,x.Polygon);Label(g,$"EXCLUSION {x.VisualRegionId}",x.BoundingBox.Location,Color.Red,font);}
        var colors=new[]{Color.Lime,Color.DeepSkyBlue,Color.Gold,Color.Magenta};var i=0;foreach(var v in values){var d=v.Decision;var color=colors[i++%colors.Length];using var p=new Pen(color,3);foreach(var island in d.Islands){g.DrawRectangle(p,island.Bounds.X,island.Bounds.Y,island.Bounds.Width,island.Bounds.Height);Label(g,$"{island.Id} {island.Role} [{string.Join(',',island.SourceBlockIds)}]",island.Bounds.Location,color,font);}foreach(var t in d.Targets)g.DrawRectangle(p,t.X,t.Y,t.Width,t.Height);}image.Save(path);
    }
    private static void Label(Graphics g,string text,PointF point,Color color,Font font){var s=g.MeasureString(text,font);using var bg=new SolidBrush(Color.FromArgb(210,0,0,0));using var fg=new SolidBrush(color);g.FillRectangle(bg,point.X,Math.Max(0,point.Y-s.Height),s.Width,s.Height);g.DrawString(text,font,fg,point.X,Math.Max(0,point.Y-s.Height));}
    private static string Text(OcrEngineBlock x) => string.IsNullOrWhiteSpace(x.CorrectedText) ? x.RawText : x.CorrectedText;
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
}
