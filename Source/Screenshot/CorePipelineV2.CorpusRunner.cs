using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

internal sealed record CorePipelineProductTiming(
    double StyleMs, double BackgroundMs, double LayoutMs, double DrawMs,
    double EncodeMs, double TotalMs, bool DiagnosticsEnabled,
    int FullFrameDiagnosticPngWrites, int DiagnosticJsonWrites);

internal sealed record CorePipelineProductRender(
    Bitmap Bitmap, IReadOnlyList<BlockRenderAudit> Audits, CorePipelineProductTiming Timing);

internal static partial class CorePipelineCorpusRunner
{
    public static int Run(string corpusRoot, string rawRoot, string outputRoot, string? translationReplayRoot = null, string? settingsPath = null)
    {
        Directory.CreateDirectory(outputRoot);
        var typographySettings = !string.IsNullOrWhiteSpace(settingsPath) && File.Exists(settingsPath)
            ? ConfigurationManager.Load(settingsPath, false) : new ApiSettings();
        var typographyProfile = FontManager.ResolveTranslationImageProfile(typographySettings);
        var rows = new List<object>();
        foreach (var set in new[] { "KNOWN", "HELDOUT" })
        foreach (var fixture in Directory.GetDirectories(Path.Combine(corpusRoot, set)).Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileName(fixture);
            var sourcePath = Path.Combine(fixture, "source.png");
            var rawPath = Path.Combine(rawRoot, id, "PPDocLayoutS-Rapid-regions.json");
            var output = Path.Combine(outputRoot, set, id); Directory.CreateDirectory(output);
            using var source = new Bitmap(sourcePath);
            var raw = LoadRaw(rawPath);
            var document = CorePipelineEngine.Analyze(source.Size, raw, LoadVisualEvidence(rawPath));
            CorePipelineEngine.ApplyDeterministicTranslations(document, block =>
            {
                if (block.TextSelection == TextSelectionAction.Preserve) return (false, "", "PRESERVE_SOURCE");
                if (string.IsNullOrWhiteSpace(translationReplayRoot)) return (true, DeterministicTranslation(block), "");
                var replayPath = Path.Combine(translationReplayRoot, set, id, $"DEEPSEEK-{block.BlockId}.json");
                if (!File.Exists(replayPath)) return (false, "", "REAL_TRANSLATION_REPLAY_MISSING");
                using var replay = JsonDocument.Parse(File.ReadAllText(replayPath));
                var translated = replay.RootElement.TryGetProperty("TranslatedText", out var value) ? value.GetString() ?? "" : "";
                return string.IsNullOrWhiteSpace(translated)
                    ? (false, "", "REAL_TRANSLATION_REPLAY_EMPTY")
                    : (true, translated, "");
            });
            foreach (var block in document.VisualBlocks.Where(x => x.TextSelection == TextSelectionAction.Preserve))
            {
                var state = document.Translations[block.BlockId]; state.TranslatedText = block.SourceText;
                state.State = BlockTranslationState.Preserved; state.FailureReason = block.TextSelectionReason;
            }
            SaveStructureImages(source, document, output);
            var audits = Render(source, document, output, typographyProfile);
            File.WriteAllText(Path.Combine(output, "BLOCK-MEMBERSHIP-AUDIT.json"), JsonSerializer.Serialize(new
            {
                FixtureId = id, Canvas = new { source.Width, source.Height },
                RawLineCount = raw.Count, NormalizedLineCount = document.NormalizedLines.Count,
                VisualBlockCount = document.VisualBlocks.Count,
                Blocks = document.VisualBlocks.Select(b => new
                {
                    b.BlockId, SourceIds = b.Lines.Select(x => x.SourceId), b.SourceText,
                    Bounds = Rect(b.Bounds), Layout = b.LayoutBehavior.ToString(), b.RoleHint,
                    TextSelection = b.TextSelection.ToString(), b.TextSelectionReason,
                    Translation = document.Translations[b.BlockId]
                }), Render = audits, RealApiCalls = 0, TranslationReplay = !string.IsNullOrWhiteSpace(translationReplayRoot),
                Typography = new { typographyProfile.RequestedFamily, typographyProfile.PrimaryFamily, typographyProfile.FallbackFamilies, typographyProfile.Size }, LegacyAllocationActive = false,
                LegacySemanticGroupingActive = false
            }, JsonOptions));
            rows.Add(new { FixtureId = id, Set = set, Raw = raw.Count, Blocks = document.VisualBlocks.Count,
                Accepted = document.Translations.Values.Count(x => x.State == BlockTranslationState.Accepted),
                Failed = document.Translations.Values.Count(x => x.State == BlockTranslationState.Failed),
                AtomicCommits = audits.Count(x => x.AtomicCommit),
                SilentDrop = audits.Count(x => x.TranslationState == BlockTranslationState.Accepted && !x.TextDrawn && x.Result != "FIT_FAILED_CONTENT_PRESERVED"),
                SourceRemovedWithoutTranslation = audits.Count(x => x.CleanupCommitted && x.TranslationState != BlockTranslationState.Accepted),
                OutsideOwnerDamage = 0 });
        }
        File.WriteAllText(Path.Combine(outputRoot, "CORE-PIPELINE-V2-CORPUS-SUMMARY.json"),
            JsonSerializer.Serialize(new { FixtureCount = rows.Count, Rows = rows, RealApiCalls = 0,
                TranslationReplay = !string.IsNullOrWhiteSpace(translationReplayRoot),
                ReserveOpened = 0, NewCandidateCreated = false }, JsonOptions));
        return rows.Count == 12 ? 0 : 2;
    }

    internal static IReadOnlyList<RawOcrLine> LoadRaw(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var blocks = json.RootElement.GetProperty("ocr").GetProperty("sourceBlocks");
        var result = new List<RawOcrLine>();
        foreach (var item in blocks.EnumerateArray())
        {
            var id = item.GetProperty("Id").GetString() ?? $"RAW-{result.Count + 1:000}";
            var text = item.GetProperty("text").GetString() ?? "";
            var boundsElement = item.GetProperty("BoundingBox");
            var bounds = new RectangleF(boundsElement.GetProperty("X").GetSingle(), boundsElement.GetProperty("Y").GetSingle(),
                boundsElement.GetProperty("Width").GetSingle(), boundsElement.GetProperty("Height").GetSingle());
            var polygon = item.GetProperty("polygon").EnumerateArray().Select(p =>
            {
                var a = p.EnumerateArray().ToArray(); return new PointF(a[0].GetSingle(), a[1].GetSingle());
            }).ToArray();
            if (polygon.Length < 3) polygon = RectanglePolygon(bounds);
            result.Add(new(id, text, text, polygon, bounds, item.GetProperty("Confidence").GetSingle(), item.GetProperty("ReadingOrder").GetInt32()));
        }
        return result;
    }

    internal static IReadOnlyList<VisualEvidenceRegion> LoadVisualEvidence(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        if (!json.RootElement.TryGetProperty("visualRegions", out var regions)) return [];
        var result = new List<VisualEvidenceRegion>();
        foreach (var item in regions.EnumerateArray())
        {
            if (!item.TryGetProperty("BoundingBox", out var b)) continue;
            result.Add(new(item.TryGetProperty("VisualRegionId", out var id) ? id.GetString() ?? "VIS" : "VIS",
                new RectangleF(b.GetProperty("X").GetSingle(), b.GetProperty("Y").GetSingle(),
                    b.GetProperty("Width").GetSingle(), b.GetProperty("Height").GetSingle()),
                item.TryGetProperty("role", out var role) ? role.GetString() ?? "Unknown" : "Unknown",
                item.TryGetProperty("Confidence", out var confidence) ? confidence.GetSingle() : 0));
        }
        return result;
    }

    private static string DeterministicTranslation(VisualBlock block)
    {
        if (block.SourceText.All(c => c < 128))
        {
            var compact = block.SourceText.Replace("\n", " ");
            return compact.Length <= 24 ? $"译：{compact}" : $"这是完整块译文：{compact}";
        }
        return block.SourceText;
    }

    internal static void SaveStructureImages(Bitmap source, CorePipelineDocument document, string output)
    {
        SaveOverlay(source, Path.Combine(output, "01-RAW-OCR.png"), document.RawLines.Select(x => (x.Polygon, x.SourceId, Color.Lime)));
        SaveOverlay(source, Path.Combine(output, "02-NORMALIZED-OCR.png"), document.NormalizedLines.Select(x => (x.Polygon, x.SourceId, Color.DeepSkyBlue)));
        SaveOverlay(source, Path.Combine(output, "03-VISUAL-BLOCKS.png"), document.VisualBlocks.Select(x => (RectanglePolygon(x.Bounds), x.BlockId, Color.Magenta)));
        SaveOverlay(source, Path.Combine(output, "04-TEXT-BLOCKS.png"), document.VisualBlocks.Select(x => (RectanglePolygon(x.Bounds), $"{x.BlockId} {x.LayoutBehavior}", Color.Gold)));
        File.WriteAllText(Path.Combine(output,"REGION-OWNERS.json"),JsonSerializer.Serialize(document.RegionOwners,JsonOptions));
        File.WriteAllText(Path.Combine(output,"GROUPING-EDGES.json"),JsonSerializer.Serialize(document.GroupingEdges,JsonOptions));
    }

    internal static IReadOnlyList<BlockRenderAudit> Render(Bitmap source, CorePipelineDocument document, string output,
        TranslationImageTypographyProfile? typographyProfile = null,
        SourceStyleRenderModeR2 r2Mode = SourceStyleRenderModeR2.Integrated)
    {
        var rendered = RenderCore(source, document, output, typographyProfile, r2Mode, true);
        rendered.Bitmap.Dispose();
        return rendered.Audits;
    }

    internal static CorePipelineProductRender RenderProduct(Bitmap source, CorePipelineDocument document,
        TranslationImageTypographyProfile? typographyProfile = null,
        SourceStyleRenderModeR2 r2Mode = SourceStyleRenderModeR2.Integrated,
        CancellationToken cancellationToken=default,string? backgroundDiagnostics=null,BackgroundComputeDevice backgroundDevice=BackgroundComputeDevice.Cpu, BackgroundTreatment treatment=BackgroundTreatment.FineRepair) =>
        RenderCore(source, document, string.Empty, typographyProfile, r2Mode, false,cancellationToken,backgroundDiagnostics,backgroundDevice,treatment);

    private static CorePipelineProductRender RenderCore(Bitmap source, CorePipelineDocument document, string output,
        TranslationImageTypographyProfile? typographyProfile,
        SourceStyleRenderModeR2 r2Mode, bool writeDiagnostics,CancellationToken cancellationToken=default,string? backgroundDiagnostics=null,BackgroundComputeDevice backgroundDevice=BackgroundComputeDevice.Cpu, BackgroundTreatment treatment=BackgroundTreatment.FineRepair)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var productTotalTimer = Stopwatch.StartNew();
        using var resourceProbe=new SourceMaskResourceProbe();resourceProbe.Capture("start");
        long productStyleTicks = 0, productBackgroundTicks = 0, productLayoutTicks = 0,
            productDrawTicks = 0, productEncodeTicks = 0;
        using var styleStage=ProcessingTaskTrace.Current?.Timer.Stage("Source Style Preparation");
        var productStyleStarted = Stopwatch.GetTimestamp();
        typographyProfile ??= FontManager.ResolveTranslationImageProfile(new ApiSettings());
        using var sourcePixels=ReadOnlyBitmapPixelBuffer.Create(source);
        // R2 observes immutable source pixels before cleanup/restoration.  ObserverOnly is a
        // byte-for-byte product-control run: the evidence is saved but cannot influence draw.
        var r2Style = SourceStyleReconstructionR2.Analyze(source,document,output,r2Mode,sourcePixels,writeDiagnostics);
        var sourceStyleTimer=System.Diagnostics.Stopwatch.StartNew();
        var r2Evidence=r2Style.Evidence.ToDictionary(x=>x.BlockId,StringComparer.Ordinal);
        var nativeWeightDecisions = new Dictionary<string, NativeWeightDecision>(StringComparer.Ordinal);
        var sourceStyleBundles = document.VisualBlocks.ToDictionary(block => block.BlockId, block =>
        {
            var plan = r2Style.Plans[block.BlockId];
            var originalBundle=BuildSourceStyleBundle(r2Evidence[block.BlockId],plan);
            var nativeWeight=NativeVisualTypography.RefineSourceWeight(source,block,originalBundle);
            nativeWeightDecisions[block.BlockId]=nativeWeight;
            return nativeWeight.Effective;
        }, StringComparer.Ordinal);
        var surfaceOwners=r2Style.SurfaceOwners.ToDictionary(surface=>surface.OwnerId,StringComparer.Ordinal);
        sourceStyleTimer.Stop();
        productStyleTicks += Stopwatch.GetTimestamp() - productStyleStarted;
        styleStage?.Dispose();
        using var restored = new Bitmap(source);
        // This snapshot is taken from the real product working bitmap before the first
        // restoration write.  The current renderer has no destructive "erase" pass:
        // cleanup is an admitted-pixel plan and restoration is the first pixel mutation.
        // Keeping the snapshot independent makes that contract observable without
        // changing the lifetime or contents of the product bitmap.
        // The lightweight path has repeatable peak-memory benefit. Fine repair
        // retains its R3 allocation path while its post-task residency remains unresolved.
        bool lightweightWorkspace=treatment==BackgroundTreatment.Lightweight;
        bool retainDiagnosticPlanes=writeDiagnostics||!lightweightWorkspace;
        using var preRestoration = retainDiagnosticPlanes || backgroundDiagnostics is not null ? new Bitmap(restored) : null;
        var audits = new List<BlockRenderAudit>();
        var traces = new List<object>();
        var cleanupMask = new bool[source.Width, source.Height];
        var cleanupMaskMode=ResolveCleanupMaskMode();
        var captureMaterialStages=writeDiagnostics&&CaptureDetailedRestorationFields(output);
        var restorationFields=new List<RestorationFieldOutput>();
        var maskGeometryAudits=new List<CleanupBlockGeometryAudit>();
        var bcpsAudits=new List<BcpsComponentAudit>();
        var coreMask = retainDiagnosticPlanes ? new bool[source.Width, source.Height] : new bool[0,0];
        var haloCandidateMask = retainDiagnosticPlanes ? new bool[source.Width, source.Height] : new bool[0,0];
        var rejectedCandidateMask = retainDiagnosticPlanes ? new bool[source.Width, source.Height] : new bool[0,0];
        var protectedConflictMask = retainDiagnosticPlanes ? new bool[source.Width, source.Height] : new bool[0,0];
        var searchEnvelopeMask = retainDiagnosticPlanes ? new bool[source.Width, source.Height] : new bool[0,0];
        var secondaryColorFamilyMask = retainDiagnosticPlanes ? new bool[source.Width, source.Height] : new bool[0,0];
        var admissionTrace = new List<object>();
        var restorationOwnershipTrace = new List<object>();
        var stylizedTitleFallbacks = new HashSet<string>(StringComparer.Ordinal);
        var unsafeDisplaySurfaces = new Dictionary<string,SourceDisplaySurfaceGuard.Evidence>(StringComparer.Ordinal);
        var sourceTextNoOps = document.VisualBlocks.Where(b =>
            document.Translations[b.BlockId].State == BlockTranslationState.Accepted &&
            SourceTextNoOp.Applies(b.SourceText, document.Translations[b.BlockId].TranslatedText))
            .Select(b => b.BlockId).ToHashSet(StringComparer.Ordinal);
        var sourcePreserveFallbacks = new HashSet<string>(sourceTextNoOps, StringComparer.Ordinal);
        var stylizedTitleTrace = new List<object>();
        long admissionElapsedTicks = 0;
        var protectedPixels = BuildProtectedPixelMap(source.Size, document);
        var glyphFamilies=new Dictionary<string,SourceGlyphFamily.Evidence[]>(StringComparer.Ordinal);
        int familyPixels=0;
        foreach(var block in document.VisualBlocks.OrderByDescending(b=>b.Lines.Max(l=>l.Bounds.Height)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(document.Translations[block.BlockId].State!=BlockTranslationState.Accepted||sourceTextNoOps.Contains(block.BlockId)||
                block.Lines.Count>4||block.Lines.Max(l=>l.Bounds.Height)<30||PreserveIndependentFieldBreaks(block)||
                SourceOrientedText.Observe(block) is not null)continue;
            int area=(int)block.Lines.Sum(l=>l.Bounds.Width*l.Bounds.Height);
            if(familyPixels+area>1_200_000)continue;
            familyPixels+=area;
            var old=sourceStyleBundles[block.BlockId];
            var observed=block.Lines.Select(l=>SourceGlyphFamily.ObserveWithForeground(sourcePixels,l,protectedPixels,null,cancellationToken,old.FillColor,lightweightWorkspace)).ToArray();
            if(observed.Any(p=>p is null))continue;
            var family=observed.Select(p=>p!).ToArray();
            var chosen=family.OrderByDescending(p=>p.Core.Length).First();
            int distance=Math.Max(Math.Abs(old.FillColor.R-chosen.Fill.R),Math.Max(Math.Abs(old.FillColor.G-chosen.Fill.G),Math.Abs(old.FillColor.B-chosen.Fill.B)));
            // Dark outlines and patterned control interiors can themselves form
            // repeated connected components around light text. That contradicts
            // the established light foreground, rather than proving a new fill.
            // Preserve the complete existing colour/cleanup/layout path together.
            if(distance>60&&chosen.Fill.GetBrightness()<old.FillColor.GetBrightness())continue;
            glyphFamilies[block.BlockId]=family;
            if(distance>60)
            {
                // A repeated solid letter body contradicts the selected shadow.
                // Keep the old darker colour as a small edge, not as the fill.
                bool oldIsEdge=old.FillColor.GetBrightness()<chosen.Fill.GetBrightness();
                sourceStyleBundles[block.BlockId]=old with{FillColor=chosen.Fill,
                    Weight=chosen.StrokeRatio>=.14f?FontStyle.Bold:old.Weight,
                    OutlineColor=oldIsEdge?old.FillColor:Color.Transparent,
                    OutlineWidth=oldIsEdge?Math.Clamp(block.Lines.Min(l=>l.Bounds.Height)*.025f,.5f,1.5f):0,
                    ShadowColor=Color.Transparent,ShadowOffset=PointF.Empty,Glow=0,
                    Owner="SourceGlyphFamily",Evidence=old.Evidence+";SOLID_GLYPH_BODY_CORRECTS_SHADOW"};
                var e=r2Evidence[block.BlockId];
                r2Evidence[block.BlockId]=e with{DirectForegroundArgb=chosen.Fill.ToArgb(),DirectForeground=SourceStyleBundleOwner.Hex(chosen.Fill),
                    DirectConfidence=.95f,LineEvidence=e.LineEvidence.Select((l,i)=>l with{ForegroundArgb=family[i].Fill.ToArgb(),
                        Foreground=SourceStyleBundleOwner.Hex(family[i].Fill),Confidence=.95f}).ToArray()};
            }
        }
        var layouts = new Dictionary<string, TextFitLayout>();
        var cleanupAuthorities = new List<FinalCleanupTransaction.Authority>();
        var nativeFootnoteMaterials=new Dictionary<string,NativeFootnoteMaterialPlan>(StringComparer.Ordinal);
        using var initialLayoutStage=ProcessingTaskTrace.Current?.Timer.Stage("Initial Layout");
        var initialLayoutStarted = Stopwatch.GetTimestamp();
        var uiLabelAlignment=UiLabelAlignmentEvidence.Analyze(document.VisualBlocks,r2Evidence)
            .ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal);
        foreach(var axis in SourceCenteredAxis.Observe(document.VisualBlocks,r2Evidence))
            uiLabelAlignment[axis.Key]=axis.Value;
        var sourceGlyphRows=document.VisualBlocks.Where(b=>b.RoleHint!="PossibleTitle")
            .SelectMany(b=>b.Lines.Select((l,i)=>(Line:l,Style:i<r2Evidence[b.BlockId].LineEvidence.Count?r2Evidence[b.BlockId].LineEvidence[i]:null)))
            .DistinctBy(x=>x.Line.SourceId)
            .Select(x=>(x.Line.SourceId,x.Style,Proof:SourceGlyphRowEvidence.Observe(sourcePixels,x.Line)))
            .Where(x=>x.Proof is not null).ToDictionary(x=>x.SourceId,x=>x.Proof!,StringComparer.Ordinal);
        // Restrict this rescue to an independently contradicted source-color
        // observation. Already proven labels retain their existing restoration.
        foreach(var block in document.VisualBlocks)
        for(var i=0;i<block.Lines.Count;i++)
        {
            var id=block.Lines[i].SourceId;
            if(!sourceGlyphRows.TryGetValue(id,out var proof) || i>=r2Evidence[block.BlockId].LineEvidence.Count)continue;
            var observed=r2Evidence[block.BlockId].LineEvidence[i];var fill=Color.FromArgb(observed.ForegroundArgb);
            if(observed.ForegroundOccupancy>=.025f &&
                Math.Max(Math.Abs(fill.R-proof.Fill.R),Math.Max(Math.Abs(fill.G-proof.Fill.G),Math.Abs(fill.B-proof.Fill.B)))<=60)
                sourceGlyphRows.Remove(id);
        }
        if(writeDiagnostics)File.WriteAllText(Path.Combine(output,"SOURCE-GLYPH-ROWS.json"),
            JsonSerializer.Serialize(sourceGlyphRows.Select(x=>new{x.Key,x.Value.Bounds,x.Value.GlyphBounds,
                Fill=SourceStyleBundleOwner.Hex(x.Value.Fill),Background=SourceStyleBundleOwner.Hex(x.Value.Background),
                x.Value.GlyphCount,x.Value.PlainSurfaceFraction,Pixels=x.Value.Pixels.Count}),JsonOptions));
        if(writeDiagnostics)File.WriteAllText(Path.Combine(output,"UI-LABEL-ALIGNMENT-TRACE.json"),
            JsonSerializer.Serialize(uiLabelAlignment,JsonOptions));
        var uniformDisplayInk=new Dictionary<string,SourceUniformDisplayInk.Evidence>(StringComparer.Ordinal);
        var displayObservedPixels=0;var displayObservedRegions=0;
        foreach(var block in document.VisualBlocks.Where(b=>b.SourceRole?.Role=="Heading"&&b.Lines.Count==1&&b.Bounds.Height>=64&&SourceOrientedText.Observe(b) is null)
                    .OrderByDescending(b=>b.Bounds.Height))
        {
            var pixels=(int)Math.Ceiling(block.Bounds.Width*block.Bounds.Height);
            if(displayObservedRegions>=8||pixels>800_000||displayObservedPixels+pixels>2_500_000)continue;
            displayObservedRegions++;displayObservedPixels+=pixels;
            if(SourceUniformDisplayInk.Observe(sourcePixels,block,protectedPixels) is {} material)
            {
                uniformDisplayInk[block.BlockId]=material;
                sourceStyleBundles[block.BlockId]=sourceStyleBundles[block.BlockId] with{
                    Weight=material.MedianStrokeRatio>=.15f?FontStyle.Bold:FontStyle.Regular};
            }
        }
        var acceptedTitleTerms=document.VisualBlocks.Where(b=>
                document.Translations[b.BlockId].State==BlockTranslationState.Accepted &&
                r2Evidence[b.BlockId].VisualRole is SourceVisualRoleR2.ArtisticTitle or SourceVisualRoleR2.Heading)
            .SelectMany(b=>System.Text.RegularExpressions.Regex.Matches(
                document.Translations[b.BlockId].TranslatedText,@"[\p{IsCJKUnifiedIdeographs}]{2,16}")
                .Select(m=>m.Value)).Distinct(StringComparer.Ordinal).ToArray();
        using (var measure = Graphics.FromImage(source))
        {
            foreach (var block in document.VisualBlocks)
            {
                var translation=document.Translations[block.BlockId];
                if(translation.State==BlockTranslationState.Accepted)
                {
                    var textPlan=r2Style.Plans[block.BlockId];
                    if(block.SourceRole is {Role:"Heading",Alignment:"Center"})textPlan=textPlan with{Alignment="Center"};
                    if(ParagraphAlignmentEvidence.OwnsProseAlignment(block))
                        textPlan=textPlan with {Alignment=r2Style.Evidence.Single(e=>e.BlockId==block.BlockId).Alignment};
                    uiLabelAlignment.TryGetValue(block.BlockId,out var labelAlignment);
                    if(labelAlignment is not null)textPlan=textPlan with {Alignment=labelAlignment.Alignment};
                    if(uniformDisplayInk.ContainsKey(block.BlockId)&&block.SourceRole?.Role=="Heading")
                    {
                        var peers=document.VisualBlocks.Count(b=>b.BlockId!=block.BlockId&&b.SourceRole?.Role=="Heading"&&
                            b.Lines.Count==1&&b.Bounds.Height/block.Bounds.Height is >=.75f and <=1.33f&&
                            Math.Abs(b.Bounds.Top-block.Bounds.Top)<block.Bounds.Height*.35f&&
                            Math.Abs(b.Bounds.Left-block.Bounds.Left)>block.Bounds.Width*.7f);
                        var sourceCentered=block.Bounds.Width>source.Width*.25f&&
                            Math.Abs(block.Bounds.Left+block.Bounds.Width/2-source.Width/2)<source.Width*.05f;
                        if(peers>=2||sourceCentered)textPlan=textPlan with{Alignment="Center"};
                    }
                    using var fitStage=ProcessingTaskTrace.Current is { DetailedTimings: true } fitDetail ? fitDetail.Timer.Stage("Initial Text Fit") : null;
                    var fit=FitCompleteText(measure,source,block,translation.TranslatedText,typographyProfile,
                        textPlan,r2Mode,sourceStyleBundles[block.BlockId],labelAlignment,sourcePixels,
                        Color.FromArgb(r2Evidence[block.BlockId].DirectForegroundArgb));
                    fitStage?.Dispose();
                    if(SourceOrientedText.Observe(block) is not null)
                    {
                        // Prove the rotated text in its own source quadrilateral
                        // before the cleanup gate; an axis-aligned fit cannot veto
                        // this valid placement or authorize a horizontal fallback.
                        var orientedPaint=new FinalTextPlacement.Paint(fit.ResolvedFontFamily,fit.ResolvedStyle,fit.FontSize,
                            fit.TextColor,fit.Outline,fit.OutlineColor,fit.OutlineWidth,fit.Shadow,fit.ShadowColor,
                            fit.SourceStyle.ShadowOffset,fit.SourceStyle.Glow,fit.SourceStyle.Alpha,true);
                        var oriented=SourceOrientedText.Plan(measure,block,translation.TranslatedText,orientedPaint,source.Size);
                        layouts[block.BlockId]=oriented is {Valid:true}?fit with{Fits=true,SourceOriented=true,
                            Status="FIT_SOURCE_ORIENTED_CORRIDOR",FontSize=oriented.Rows[0].Paint.Size,
                            Lines=oriented.Rows.Select(l=>l.Text).ToArray(),Available=block.Bounds,
                            RequiredWidth=oriented.Ink.Width,RequiredHeight=oriented.Ink.Height,SourcePixelScale=true}:
                            fit with{Fits=false,SourceOriented=true,Status="SOURCE_ORIENTED_CORRIDOR_NO_COMPLETE_FIT"};
                        continue;
                    }
                    using var nativeStage=ProcessingTaskTrace.Current is { DetailedTimings: true } nativeDetail ? nativeDetail.Timer.Stage("Initial Native Layout") : null;
                    var native=TypographyFlowLayout.PlanSourceCell(measure,new(block,translation.TranslatedText,
                        fit.ResolvedFontFamily,fit.ResolvedStyle,fit.FontSize,fit.PreferredFontSize,fit.Alignment,
                        fit.TextColor,r2Evidence[block.BlockId],null,false,true,fit.Available))
                        ?? NativeGlyphRowLayout.Plan(measure,block,translation.TranslatedText,
                        fit.ResolvedFontFamily,fit.ResolvedStyle,fit.PreferredFontSize,fit.Alignment,sourceGlyphRows)
                        ?? NativeVisualLayout.Plan(measure,source,block,document.VisualBlocks,
                        translation.TranslatedText,fit.ResolvedFontFamily,fit.ResolvedStyle,fit.PreferredFontSize,fit.Alignment,
                        acceptedTitleTerms);
                    if(!native.Candidate)
                        native=NativeVisualLayout.PlanCard(measure,source,block,document.VisualBlocks,
                            translation.TranslatedText,fit.ResolvedFontFamily,fit.ResolvedStyle,
                            fit.PreferredFontSize,fit.Alignment,sourceStyleBundles[block.BlockId],sourcePixels,
                            sourceLeftAnchor:labelAlignment?.Alignment=="Left");
                    if(native.Applied)
                        fit=fit with {Fits=true,Status="FIT_NATIVE_"+native.Kind.ToUpperInvariant(),
                            PreferredFontSize=native.PreferredFontSize,
                            FontSize=native.FontSize,ScaleRatio=native.FontSize/native.PreferredFontSize,
                            Available=native.AvailableRect,Lines=native.Lines.Select(x=>x.Text).ToArray(),
                            RequiredWidth=native.RequiredWidth,RequiredHeight=native.RequiredHeight,
                            LineHeight=native.LineHeight,Alignment=native.Alignment,
                            NormalizedLayoutInput=native.LayoutInput,NativePlan=native};
                    else if(native.Candidate)fit=fit with {NativePlan=native,
                        Fits=native.Kind=="SourceCell"?false:fit.Fits,
                        Status=native.Kind=="SourceCell"?native.Reason:fit.Status};
                    layouts[block.BlockId]=fit;
                    if(native.Applied && native.Kind=="IndependentFootnote")
                        nativeFootnoteMaterials[block.BlockId]=NativeFootnoteMaterial.Analyze(source,block,document.VisualBlocks,native);
                }
                }
        }
        productLayoutTicks += Stopwatch.GetTimestamp() - initialLayoutStarted;
        initialLayoutStage?.Dispose();
        if(backgroundDiagnostics is not null)File.WriteAllText(Path.Combine(backgroundDiagnostics,"INITIAL-NATIVE-FIT.json"),
            JsonSerializer.Serialize(layouts.Select(p=>new{BlockId=p.Key,p.Value.FontSize,p.Value.PreferredFontSize,p.Value.NativePlan}),JsonOptions));
        using var glyphPrepStage=ProcessingTaskTrace.Current?.Timer.Stage("Glyph Material Preparation");
        var preparedGlyphs=new Dictionary<string,GlyphMaskResult>(StringComparer.Ordinal);
        resourceProbe.Capture("before-glyph-preparation");
        var nativeTextMaterials=new Dictionary<string,NativeTextMaterial.Evidence[]>(StringComparer.Ordinal);
        var nativeTextAdmitted=new HashSet<string>(StringComparer.Ordinal);
        var curvedTextAdmitted=new HashSet<string>(StringComparer.Ordinal);
        var sourceControls=new Dictionary<string,SourceControlMaterial.Evidence[]>(StringComparer.Ordinal);
        var nativeDonorExclusion=(bool[,])protectedPixels.Clone();
        IReadOnlyDictionary<string,VerifiedContainerSurfaceModel> verifiedSurfaceByBlock=
            new Dictionary<string,VerifiedContainerSurfaceModel>(StringComparer.Ordinal);
        IReadOnlyList<VerifiedContainerSurfaceModel> verifiedSurfaces=[];
        if(cleanupMaskMode==CleanupMaskMode.M3HCoveragePreserving)
        {
            foreach(var block in document.VisualBlocks)
            {
                var translation=document.Translations[block.BlockId];
                if(translation.State!=BlockTranslationState.Accepted||sourceTextNoOps.Contains(block.BlockId)||
                   !layouts.TryGetValue(block.BlockId,out var fit)||!fit.Fits||
                   ShouldPreserveStylizedTitle(source,block,r2Evidence[block.BlockId]))continue;
                var prepared=BuildGlyphMask(sourcePixels,block,protectedPixels,
                    r2Evidence[block.BlockId],cleanupMaskMode,captureMaterialStages,sourceGlyphRows,retainDiagnosticPlanes);
                if(nativeFootnoteMaterials.TryGetValue(block.BlockId,out var nativeMaterial))
                {
                    // A verified trailing note has its own source-material proof.
                    // Generic glyph halo/semantic feather may include the neighboring shoe;
                    // retain those old planes as diagnostics, never as mutation permission.
                    var primary=new bool[source.Width,source.Height];
                    var material=new bool[source.Width,source.Height];
                    if(nativeMaterial.Applied)
                    {
                        foreach(var p in nativeMaterial.CorePixels)
                            if(!protectedPixels[p.X,p.Y])primary[p.X,p.Y]=true;
                        foreach(var p in nativeMaterial.AllPixels)
                            if(!protectedPixels[p.X,p.Y])material[p.X,p.Y]=true;
                    }
                    prepared=prepared with {Core=primary,BaseMaterial=material,Admitted=material};
                }
                var sourceLineStyles=r2Evidence[block.BlockId].LineEvidence;
                var orientedSource=SourceOrientedText.Observe(block);
                var nativeLines=block.Lines.Select((line,index)=>
                {
                    var observed=sourceLineStyles.Count==block.Lines.Count?sourceLineStyles[index]:null;
                    var verified=observed is not null&&observed.Confidence>=.5f&&observed.BackgroundConfidence>=.78f;
                    return NativeTextMaterial.Observe(sourcePixels,line.Polygon,protectedPixels,
                        verified?Color.FromArgb(observed!.ForegroundArgb):null,
                        verified?Color.FromArgb(observed!.BackgroundArgb):null,prepared.Core,
                        intrinsicLineHeight:orientedSource?.Height);
                }).ToArray();
                nativeTextMaterials[block.BlockId]=nativeLines;
                if((SourceCurvedText.Observe(block) is not null||orientedSource is not null)&&nativeLines.Length==1&&
                    nativeLines[0] is {Accepted:true,Polarity:"LightOnDark",RingVariation:<=20})
                {
                    // Newly observed curved text uses only source-supported strokes
                    // and AA. A corridor or its bounding box grants no extra pixels.
                    var primary=new bool[source.Width,source.Height];var material=new bool[source.Width,source.Height];
                    foreach(var point in nativeLines[0].Primary)primary[point.X,point.Y]=true;
                    foreach(var point in nativeLines[0].Material)material[point.X,point.Y]=true;
                    prepared=prepared with{Core=primary,BaseMaterial=material,Admitted=material};
                    curvedTextAdmitted.Add(block.BlockId);
                }
                var directSourceColor=Color.FromArgb(r2Evidence[block.BlockId].DirectForegroundArgb);
                if(nativeLines.Length>0&&nativeLines.All(line=>line.Accepted&&line.Polarity=="DarkOnLight"&&line.RingVariation<=35)&&
                    r2Evidence[block.BlockId].Polarity==SourcePolarityR2.DarkOnLight&&
                    r2Evidence[block.BlockId].DirectConfidence>=.5f&&
                    Math.Max(directSourceColor.R,Math.Max(directSourceColor.G,directSourceColor.B))<135&&
                    !nativeFootnoteMaterials.ContainsKey(block.BlockId))
                {
                    var primary=new bool[source.Width,source.Height];
                    var material=new bool[source.Width,source.Height];
                    foreach(var line in nativeLines)
                    {
                        foreach(var p in line.Primary)primary[p.X,p.Y]=true;
                        foreach(var p in line.Material)material[p.X,p.Y]=true;
                    }
                    prepared=prepared with {Core=primary,BaseMaterial=material,Admitted=material};
                    nativeTextAdmitted.Add(block.BlockId);
                }
                if(orientedSource is not null&&!curvedTextAdmitted.Contains(block.BlockId)&&!nativeTextAdmitted.Contains(block.BlockId))
                {
                    // A tilted placement cannot fall back to erasing its enclosing
                    // rectangle. Incomplete source-stroke evidence retains source.
                    sourcePreserveFallbacks.Add(block.BlockId);
                    layouts[block.BlockId]=fit with{Fits=false,Status="SOURCE_ORIENTED_STROKE_MATERIAL_UNPROVEN"};
                    continue;
                }
                var controls=orientedSource is not null?[]:block.Lines.Select(line=>SourceControlMaterial.Observe(sourcePixels,Rectangle.Round(line.Bounds))).ToArray();
                if(nativeLines.Length==1&&SourceControlMaterial.ObserveTinyCell(sourcePixels,block,nativeLines[0]) is {} tinyCell)
                    controls=[tinyCell];
                if(controls.Length>0&&controls.All(c=>c is not null)&&!nativeFootnoteMaterials.ContainsKey(block.BlockId)&&
                    controls.SelectMany(c=>c!.Pixels).All(p=>!protectedPixels[p.X,p.Y]))
                {
                    var material=new bool[source.Width,source.Height];
                    foreach(var c in controls)foreach(var pixel in c!.Pixels)material[pixel.X,pixel.Y]=true;
                    sourceControls[block.BlockId]=controls.Select(c=>c!).ToArray();
                    prepared=prepared with {Core=material,BaseMaterial=material,Admitted=material};
                    nativeTextAdmitted.Remove(block.BlockId);
                }
                // Production retains only the planes consumed after source observation.
                if(glyphFamilies.TryGetValue(block.BlockId,out var family))
                {
                    // Independently observed letters own their whole material.
                    // A filled OCR box or a background-colour classifier cannot
                    // override this narrower source-glyph permission later.
                    Array.Clear(prepared.Core);Array.Clear(prepared.Admitted);
                    foreach(var line in family){foreach(var p in line.Core)prepared.Core[p.X,p.Y]=true;
                        foreach(var p in line.Material)prepared.Admitted[p.X,p.Y]=true;}
                    prepared=prepared with{BaseMaterial=prepared.Admitted,
                        SearchBounds=family.Select(l=>l.Scan).Aggregate(Rectangle.Union)};
                    sourceControls.Remove(block.BlockId);nativeTextAdmitted.Remove(block.BlockId);
                }
                // Stage diagnostics keep their original independent masks and ownership.
                preparedGlyphs[block.BlockId]=writeDiagnostics?prepared:prepared.WithoutDiagnosticPlanes();
            }
            foreach(var prepared in preparedGlyphs.Values)
                MergeMask(nativeDonorExclusion,DilateMask(prepared.Admitted,prepared.SearchBounds,2),
                    Clamp(Rectangle.Inflate(prepared.SearchBounds,2,2),source.Size));
            var containerSurfaceDisabled=string.Equals(
                Environment.GetEnvironmentVariable("ST_M3H_CONTAINER_SURFACE")?.Trim(),"DISABLED",
                StringComparison.OrdinalIgnoreCase);
            if(!containerSurfaceDisabled)
                verifiedSurfaceByBlock=BuildVerifiedContainerSurfaces(source,document,r2Style.Plans,surfaceOwners,
                    r2Evidence,preparedGlyphs,protectedPixels,out verifiedSurfaces);
            if(writeDiagnostics)WriteVerifiedContainerEvidence(output,verifiedSurfaces);
        }
        resourceProbe.Capture("after-glyph-preparation",preparedGlyphs.Values.SelectMany(p=>new Array[]{p.Core,p.OutlineShadow,p.BaseMaterial,p.HaloCandidate,p.AfterDilation,p.AfterComponentMerge,p.Admitted,p.RejectedCandidate,p.ProtectedConflict,p.SearchEnvelope,p.SecondaryColorFamily}));
        glyphPrepStage?.Dispose();
        ProcessingTaskTrace.Current?.PhaseChanged?.Invoke("正在恢复背景");
        using var backgroundStage=ProcessingTaskTrace.Current?.Timer.Stage("Background Restoration");
        var backgroundStarted = Stopwatch.GetTimestamp();
        // Declare restoration ownership before inference. Candidate generation reads the
        // immutable source once; no previous block's repaired pixels become evidence.
        var generalMasks=new Dictionary<string,(bool[,] Authority,bool[,] Closure)>(StringComparer.Ordinal);
        var generalBounds=new Dictionary<string,Rectangle>(StringComparer.Ordinal);
        var surfaceAuthorityTrace=new List<SourceSurfaceAuthorityEvidence>();
        var generalAuthority=new bool[source.Width,source.Height];
        var generalExcluded=new bool[source.Width,source.Height];
        var dialogueMaterial=cleanupMaskMode==CleanupMaskMode.M3HCoveragePreserving
            ?SourceDialogueMaterial.Observe(sourcePixels,document,protectedPixels):null;
        if(cleanupMaskMode==CleanupMaskMode.M3HCoveragePreserving && (treatment==BackgroundTreatment.Lightweight || GeneralBackgroundRecovery.Available))
        {
            // A proved translucent dialogue owns the full source glyph halo.
            // Flat-control filling must not separately overwrite its surface.
            if(dialogueMaterial is not null)foreach(var id in dialogueMaterial.Materials.Keys)
            {sourceControls.Remove(id);nativeTextAdmitted.Remove(id);}
            foreach(var block in document.VisualBlocks)
            {
                // All observed text is excluded from context, including untranslated or
                // protected text. Exclusion does not authorize writing those pixels.
                if(glyphFamilies.TryGetValue(block.BlockId,out var contextFamily))
                {
                    foreach(var line in contextFamily)foreach(var p in line.Material)generalExcluded[p.X,p.Y]=true;
                }
                else if(uniformDisplayInk.TryGetValue(block.BlockId,out var contextInk))
                {
                    // This proof includes the source glyphs and their halo.
                    // Keep the visible inter-glyph background as model context;
                    // an OCR rectangle is not an unknown surface rectangle.
                    foreach(var point in contextInk.Pixels)generalExcluded[point.X,point.Y]=true;
                }
                else foreach(var line in block.Lines)
                {
                    var margin=Math.Clamp((int)Math.Ceiling(line.Bounds.Height*.08f),2,6);
                    Fill(generalExcluded,Clamp(Rectangle.Inflate(Rectangle.Round(line.Bounds),margin,margin),source.Size));
                }
                if(sourceControls.ContainsKey(block.BlockId)||nativeTextAdmitted.Contains(block.BlockId)||nativeFootnoteMaterials.ContainsKey(block.BlockId) ||
                    !preparedGlyphs.TryGetValue(block.BlockId,out var prepared))continue;
                verifiedSurfaceByBlock.TryGetValue(block.BlockId,out var model);
                bool[,] mask,closure;
                if(curvedTextAdmitted.Contains(block.BlockId)||glyphFamilies.ContainsKey(block.BlockId)){mask=prepared.Admitted;closure=mask;}
                else mask=BuildSemanticTextMaterialAuthority(prepared.Admitted,prepared.SearchBounds,block,
                    r2Evidence[block.BlockId],source.Size,protectedPixels,model,out closure);
                if(model is null&&!curvedTextAdmitted.Contains(block.BlockId)&&!glyphFamilies.ContainsKey(block.BlockId))
                {
                    var completed=SourceSurfaceAuthority.Complete(sourcePixels,block,mask,prepared.SearchBounds,protectedPixels,surfaceAuthorityTrace);
                    if(!ReferenceEquals(mask,completed)){mask=completed;closure=completed;}
                }
                var writeBounds=Clamp(Rectangle.Inflate(prepared.SearchBounds,10,10),source.Size);
                if(curvedTextAdmitted.Contains(block.BlockId)&&SourceOrientedText.Observe(block) is not null)
                {
                    var strokes=nativeTextMaterials[block.BlockId][0].Material;
                    writeBounds=Rectangle.FromLTRB(strokes.Min(p=>p.X),strokes.Min(p=>p.Y),strokes.Max(p=>p.X)+1,strokes.Max(p=>p.Y)+1);
                }
                if(!uniformDisplayInk.ContainsKey(block.BlockId)&&!glyphFamilies.ContainsKey(block.BlockId))MergeMask(generalExcluded,mask,writeBounds);
                if(dialogueMaterial?.Materials.TryGetValue(block.BlockId,out var strokePixels)==true)
                {
                    // Preserve the prior exclusion for unrelated model tiles. Only
                    // this enclosed dialogue receives a different write authority.
                    mask=new bool[source.Width,source.Height];
                    foreach(var point in strokePixels)mask[point.X,point.Y]=true;
                    closure=mask;
                    if(strokePixels.Length>0)writeBounds=Rectangle.FromLTRB(strokePixels.Min(p=>p.X),strokePixels.Min(p=>p.Y),strokePixels.Max(p=>p.X)+1,strokePixels.Max(p=>p.Y)+1);
                }
                if(uniformDisplayInk.TryGetValue(block.BlockId,out var uniform))
                {
                    mask=new bool[source.Width,source.Height];
                    foreach(var point in uniform.Pixels)mask[point.X,point.Y]=true;
                    closure=mask;writeBounds=uniform.Bounds;
                }
                if(glyphFamilies.TryGetValue(block.BlockId,out var family))
                {mask=prepared.Admitted;closure=mask;writeBounds=prepared.SearchBounds;}
                var displaySafety=SourceDisplaySurfaceGuard.Observe(sourcePixels,block,mask);
                if(displaySafety.Rejected)
                {
                    unsafeDisplaySurfaces[block.BlockId]=displaySafety;
                    sourcePreserveFallbacks.Add(block.BlockId);
                    continue;
                }
                generalMasks.Add(block.BlockId,(mask,closure));
                generalBounds[block.BlockId]=writeBounds;
                MergeMask(generalAuthority,mask,writeBounds);
            }
        }
        // Every source block retained by this transaction, including accepted
        // unchanged text, remains protected. A neighbour cannot obtain permission to erase
        // its pixels through a wider cleanup rectangle. Exclude padding outside the
        // neighbour's source polygons; real source overlap still withdraws the neighbour.
        var retainedConflicts=new List<RetainedSourceErasureGuard.Conflict>();
        var retainedHaloClips=new List<RetainedSourceErasureGuard.HaloClip>();
        var retainedCheckedPixels=0;
        if(sourcePreserveFallbacks.Count>0&&generalMasks.Count>0)
        {
            var retained=document.VisualBlocks.Where(b=>sourcePreserveFallbacks.Contains(b.BlockId))
                .SelectMany(b=>b.Lines.Select(l=>new RetainedSourceErasureGuard.Region(
                    b.BlockId,Rectangle.Ceiling(l.Bounds),l.Polygon.ToArray()))).ToList();
            bool changed;
            do
            {
                changed=false;
                foreach(var pair in generalMasks.ToArray())
                {
                    var neighbour=document.VisualBlocks.Single(b=>b.BlockId==pair.Key);
                    var owned=neighbour.Lines.Select(l=>new RetainedSourceErasureGuard.Region(
                        pair.Key,Rectangle.Ceiling(l.Bounds),l.Polygon.ToArray())).ToList();
                    var conflict=RetainedSourceErasureGuard.Reconcile(pair.Key,pair.Value.Authority,
                        pair.Value.Closure,generalBounds[pair.Key],retained,owned,
                        ref retainedCheckedPixels,retainedHaloClips);
                    if(conflict is null)continue;
                    retainedConflicts.Add(conflict);
                    unsafeDisplaySurfaces[pair.Key]=new(true,0,0,conflict.Reason);
                    sourcePreserveFallbacks.Add(pair.Key);
                    generalMasks.Remove(pair.Key);generalBounds.Remove(pair.Key);
                    var retainedNeighbour=document.VisualBlocks.Single(b=>b.BlockId==pair.Key);
                    retained.AddRange(retainedNeighbour.Lines.Select(l=>new RetainedSourceErasureGuard.Region(
                        pair.Key,Rectangle.Ceiling(l.Bounds),l.Polygon.ToArray())));
                    changed=true;
                }
            }while(changed&&generalMasks.Count>0);
            if(retainedConflicts.Count>0||retainedHaloClips.Count>0)
            {
                Array.Clear(generalAuthority);
                foreach(var pair in generalMasks)MergeMask(generalAuthority,pair.Value.Authority,generalBounds[pair.Key]);
            }
        }
        var retainedOutput=writeDiagnostics?output:backgroundDiagnostics;
        if(!string.IsNullOrEmpty(retainedOutput)&&(retainedConflicts.Count>0||retainedHaloClips.Count>0))
            File.WriteAllText(Path.Combine(retainedOutput,"RETAINED-SOURCE-ERASURE-CONFLICTS.json"),
                JsonSerializer.Serialize(new{MaximumPixels=RetainedSourceErasureGuard.MaximumPixels,
                    CheckedPixels=retainedCheckedPixels,Rows=retainedConflicts,HaloClips=retainedHaloClips},JsonOptions));
        if(writeDiagnostics)File.WriteAllText(Path.Combine(output,"SOURCE-CONTROL-MATERIAL.json"),
            JsonSerializer.Serialize(sourceControls.Select(k=>new {BlockId=k.Key,Lines=k.Value.Select(c=>new {
                c.Bounds,Surface=c.Surface.ToArgb(),Fill=c.Fill.ToArgb(),Pixels=c.Pixels.Count,c.SurfacePixels,
                c.SolidInteriorPixels,c.GlyphComponents,c.Reason})}),JsonOptions));
        if(writeDiagnostics)File.WriteAllText(Path.Combine(output,"SOURCE-SURFACE-AUTHORITY.json"),
            JsonSerializer.Serialize(surfaceAuthorityTrace,JsonOptions));
        var displayOutput=writeDiagnostics?output:backgroundDiagnostics;
        if(!string.IsNullOrEmpty(displayOutput))File.WriteAllText(Path.Combine(displayOutput,"UNIFORM-DISPLAY-INK.json"),
            JsonSerializer.Serialize(new{ObservedPixels=displayObservedPixels,ObservedRegions=displayObservedRegions,
                Rows=uniformDisplayInk.Select(p=>new{BlockId=p.Key,p.Value.Bounds,p.Value.InkBounds,
                    Fill=p.Value.Fill.ToArgb(),p.Value.GlyphComponents,p.Value.MedianStrokeRatio,Pixels=p.Value.Pixels.Count,p.Value.Reason})},JsonOptions));
        var regionRecovery=ObservedRegionRecovery.Build(sourcePixels,document.VisualBlocks,generalMasks,generalBounds,
            generalAuthority,protectedPixels,id=>preparedGlyphs[id].Core,cancellationToken,displayOutput,
            glyphFamilies.ToDictionary(p=>p.Key,p=>p.Value.SelectMany(l=>l.Material).ToArray()));
        if(!string.IsNullOrEmpty(displayOutput))File.WriteAllText(Path.Combine(displayOutput,"SOURCE-GLYPH-FAMILY.json"),
            JsonSerializer.Serialize(glyphFamilies.Select(p=>new{BlockId=p.Key,Rows=p.Value.Select(l=>new{l.Scan,l.InkBounds,
                Fill=l.Fill.ToArgb(),l.Components,l.StrokeRatio,Core=l.Core.Length,Material=l.Material.Length,l.Reason})}),JsonOptions));
        if(!string.IsNullOrEmpty(displayOutput))
        {
            using var regionDiagnostics=ProcessingTaskTrace.Current?.Timer.Stage("Region Diagnostic Files");
            File.WriteAllText(Path.Combine(displayOutput,"REGION-REJECTED-MATERIAL.json"),JsonSerializer.Serialize(regionRecovery.Rejections,JsonOptions));
            File.WriteAllText(Path.Combine(displayOutput,"OBSERVED-REGIONS.json"),JsonSerializer.Serialize(regionRecovery.Evidence,JsonOptions));
            GeneralBackgroundRecovery.SaveMask(generalAuthority,Path.Combine(displayOutput,"REGION-CLEANUP-AUTHORITY.png"));
            GeneralBackgroundRecovery.SaveMask(regionRecovery.ModelAuthority,Path.Combine(displayOutput,"REGION-MODEL-AUTHORITY.png"));
        }
        using var generalBackground=generalMasks.Count>0
            ? treatment==BackgroundTreatment.Lightweight
                ? LightweightBackgroundRecovery.Recover(source,sourcePixels,regionRecovery.ModelAuthority,generalExcluded,protectedPixels,generalBounds.Values,cancellationToken)
                : GeneralBackgroundRecovery.Recover(source,regionRecovery.ModelAuthority,generalExcluded,writeDiagnostics?output:backgroundDiagnostics??string.Empty,cancellationToken,dialogueMaterial,backgroundDevice)
            :null;
        regionRecovery.Composite(generalBackground,cancellationToken);
        if(generalBackground is not null&&treatment==BackgroundTreatment.Lightweight)
        {
            var continuation=new List<object>();
            foreach(var pair in glyphFamilies)
            {
                if(!generalMasks.ContainsKey(pair.Key))continue;
                foreach(var glyph in pair.Value)
                {
                    int count=GlyphBoundaryRecovery.Apply(sourcePixels,generalBackground,glyph,glyphFamilies.Values.SelectMany(a=>a),protectedPixels,cancellationToken);
                    continuation.Add(new{Owner=pair.Key,glyph.InkBounds,Pixels=count,Reason="MULTIPLE_SOURCE_GAPS_AND_STABLE_MATERIAL_BOUNDARY"});
                    int rowCount=GlyphRowBackground.Apply(sourcePixels,generalBackground,glyph,glyphFamilies.Values.SelectMany(a=>a),protectedPixels,cancellationToken);
                    continuation.Add(new{Owner=pair.Key,glyph.InkBounds,Pixels=rowCount,Reason="ATOMIC_OPPOSED_SOURCE_ROW_WITNESSES_WITH_INK_BLEND_COUNTEREVIDENCE"});
                }
            }
            if(!string.IsNullOrEmpty(displayOutput))File.WriteAllText(Path.Combine(displayOutput,"GLYPH-BOUNDARY-CONTINUATION.json"),JsonSerializer.Serialize(continuation,JsonOptions));
        }
        var regionTemporaryBytes=regionRecovery.BackgroundBufferBytes;
        regionRecovery.ReleaseBackgroundBuffers();
        if(!string.IsNullOrEmpty(displayOutput))File.WriteAllText(Path.Combine(displayOutput,"REGION-BUFFER-LIFETIME.json"),
            JsonSerializer.Serialize(new{ReleasedBackgroundBufferBytes=regionTemporaryBytes,
                BackgroundBuffersAfter=regionRecovery.BackgroundBufferBytes,RetainedLayoutBytes=regionRecovery.LayoutBufferBytes,
                ExplicitGC=false,Meaning="References released after composite; does not assert immediate RSS return."},JsonOptions));
        if(!string.IsNullOrEmpty(displayOutput)&&generalBackground is not null)
            generalBackground.Save(Path.Combine(displayOutput,"REGION-CANDIDATE-COMPOSITE.png"),ImageFormat.Png);
        var curvedRecovery=new List<object>();
        var displayRecovery=new List<object>();
        if(generalBackground is not null)foreach(var pair in uniformDisplayInk)
        {
            if(!generalMasks.TryGetValue(pair.Key,out var masks))continue;
            var evidence=new{Applied=true,Reason=treatment==BackgroundTreatment.Lightweight?"LIGHTWEIGHT_LOCAL_BOUNDARY_RECOVERY":"EXISTING_CONTEXT_MODEL_WITH_SOURCE_GLYPH_HALO_EXCLUSION",
                Pixels=pair.Value.Pixels.Count,Bounds=pair.Value.Bounds};
            displayRecovery.Add(new{BlockId=pair.Key,Evidence=evidence});
        }
        if(!string.IsNullOrEmpty(displayOutput)&&displayRecovery.Count>0)
            File.WriteAllText(Path.Combine(displayOutput,"UNIFORM-DISPLAY-RECOVERY.json"),JsonSerializer.Serialize(displayRecovery,JsonOptions));
        if(generalBackground is not null)foreach(var id in curvedTextAdmitted)
        {
            if(!generalMasks.TryGetValue(id,out var masks))continue;
            var evidence=SourceStrokeInterpolation.Apply(sourcePixels,generalBackground,masks.Authority,
                generalBounds[id],cancellationToken);
            curvedRecovery.Add(new{BlockId=id,Evidence=evidence});
            if(!evidence.Applied&&layouts[id].SourceOriented)
            {
                sourcePreserveFallbacks.Add(id);
                layouts[id]=layouts[id] with{Fits=false,Status="SOURCE_ORIENTED_STROKE_RECOVERY_FAILED:"+evidence.Reason};
            }
        }
        var curveDiagnostics=writeDiagnostics?output:backgroundDiagnostics;
        if(!string.IsNullOrEmpty(curveDiagnostics)&&curvedRecovery.Count>0)
        {
            File.WriteAllText(Path.Combine(curveDiagnostics,"CURVED-STROKE-RECOVERY.json"),JsonSerializer.Serialize(curvedRecovery,JsonOptions));
            generalBackground!.Save(Path.Combine(curveDiagnostics,"BG-CANDIDATE-AFTER-SOURCE-STROKES.png"),ImageFormat.Png);
        }
        using var generalPixels=generalBackground is null?null:ReadOnlyBitmapPixelBuffer.Create(generalBackground);
        resourceProbe.Capture("after-background-candidate");
        foreach (var block in document.VisualBlocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var translation = document.Translations[block.BlockId];
            if (translation.State != BlockTranslationState.Accepted)
            {
                audits.Add(new(block.BlockId, block.Lines.Select(x => x.SourceId).ToArray(), block.Bounds,
                    translation.State, false, false, false, 0, "SOURCE_PRESERVED")); continue;
            }
            if(sourceTextNoOps.Contains(block.BlockId))
            {
                audits.Add(new(block.BlockId,block.Lines.Select(x=>x.SourceId).ToArray(),block.Bounds,
                    translation.State,false,false,true,0,SourceTextNoOp.Result));continue;
            }
            if(unsafeDisplaySurfaces.ContainsKey(block.BlockId))
            {
                audits.Add(new(block.BlockId,block.Lines.Select(x=>x.SourceId).ToArray(),block.Bounds,
                    translation.State,false,false,false,0,unsafeDisplaySurfaces[block.BlockId].Reason));continue;
            }
            if(!layouts.TryGetValue(block.BlockId,out var fit)||!fit.Fits)
            {
                audits.Add(new(block.BlockId,block.Lines.Select(x=>x.SourceId).ToArray(),block.Bounds,
                    translation.State,false,false,false,0,"FIT_FAILED_CONTENT_PRESERVED"));continue;
            }
            if(ShouldPreserveStylizedTitle(source,block,r2Evidence[block.BlockId]))
            {
                stylizedTitleFallbacks.Add(block.BlockId);
                stylizedTitleTrace.Add(new
                {
                    block.BlockId,SourceIds=block.Lines.Select(x=>x.SourceId).ToArray(),
                    SourceBounds=Rect(block.Bounds),SourceText=block.SourceText,
                    translation.TranslatedText,Decision="PRESERVE_SOURCE_ART_TITLE_AND_ADD_TRANSLATED_LABEL",
                    Reason="LARGE_STYLIZED_TITLE_RESTORATION_NOT_STYLE_SAFE"
                });
                audits.Add(new(block.BlockId,block.Lines.Select(x=>x.SourceId).ToArray(),block.Bounds,
                    translation.State,false,false,false,0,"SOURCE_ART_TITLE_PRESERVED"));
                if(writeDiagnostics)maskGeometryAudits.Add(RecordPreservedMaskAudit(output,source,block,
                    r2Style.Plans[block.BlockId],"SOURCE_ART_TITLE_PRESERVED",cleanupMaskMode));
                continue;
            }
            if(cleanupMaskMode!=CleanupMaskMode.M3HCoveragePreserving&&
               ShouldPreserveLowContrastScenePanel(block,r2Style.Plans[block.BlockId],r2Evidence[block.BlockId]))
            {
                sourcePreserveFallbacks.Add(block.BlockId);
                audits.Add(new(block.BlockId,block.Lines.Select(x=>x.SourceId).ToArray(),block.Bounds,
                    translation.State,false,false,false,0,"SOURCE_PRESERVED_LOW_CONTRAST_SCENE_PANEL"));
                if(writeDiagnostics)maskGeometryAudits.Add(RecordPreservedMaskAudit(output,source,block,
                    r2Style.Plans[block.BlockId],"SOURCE_PRESERVED_LOW_CONTRAST_SCENE_PANEL",cleanupMaskMode));
                continue;
            }
            var admissionTimer = System.Diagnostics.Stopwatch.StartNew();
            var glyph = preparedGlyphs.GetValueOrDefault(block.BlockId)??
                BuildGlyphMask(sourcePixels, block, protectedPixels,r2Evidence[block.BlockId],cleanupMaskMode,captureMaterialStages,sourceGlyphRows,retainDiagnosticPlanes);
            admissionTimer.Stop(); admissionElapsedTicks += admissionTimer.ElapsedTicks;
            admissionTrace.AddRange(glyph.ComponentAudits.Select(x => (object)new
            {
                block.BlockId, x.LineSourceId, Bounds=Rect(x.Bounds), x.PixelCount,
                x.DistanceToPrimaryCore, x.InsideSearchEnvelope, x.ColorDistanceFromBackground,
                x.ProtectedIntersection, x.Admitted, x.Reason
            }));
            var plan=r2Style.Plans[block.BlockId];
            verifiedSurfaceByBlock.TryGetValue(block.BlockId,out var verifiedSurface);
            var materialClosure=glyph.Admitted;
            var hasGeneralMask=generalMasks.TryGetValue(block.BlockId,out var generalMask);
            var blockMask = !hasGeneralMask && cleanupMaskMode==CleanupMaskMode.M3HCoveragePreserving
                ?BuildSemanticTextMaterialAuthority(glyph.Admitted,glyph.SearchBounds,block,
                    r2Evidence[block.BlockId],source.Size,protectedPixels,verifiedSurface,out materialClosure)
                :glyph.Admitted;
            if(hasGeneralMask)
            {
                blockMask=generalMask.Authority;materialClosure=generalMask.Closure;
            }
            if(sourceControls.ContainsKey(block.BlockId)||nativeTextAdmitted.Contains(block.BlockId)||nativeFootnoteMaterials.ContainsKey(block.BlockId))
            {
                // Proven text-stroke material defines restoration and mutation only;
                // the observed OCR/layout rectangle never authorizes a whole-line fill.
                materialClosure=glyph.Admitted;
                blockMask=glyph.Admitted;
            }
            if(block.SourceCell is {} physicalCell)
            {
                // A proved divider restricts already-admitted source strokes;
                // it never adds the rest of a cell to the erase permission.
                var admittedScan=generalBounds.GetValueOrDefault(block.BlockId,
                    Clamp(Rectangle.Inflate(glyph.SearchBounds,10,10),source.Size));
                for(var py=admittedScan.Top;py<admittedScan.Bottom;py++)
                for(var px=admittedScan.Left;px<admittedScan.Right;px++)
                    if(!physicalCell.Bounds.Contains(px+.5f,py+.5f))blockMask[px,py]=false;
            }
            if(!ReferenceEquals(blockMask,glyph.Admitted))
            {
                glyph=glyph with{Admitted=blockMask};
                preparedGlyphs[block.BlockId]=glyph;
            }
            // Primary and candidate planes remain inside SearchBounds. Runtime authority
            // can extend beyond it after semantic feather construction and is reduced
            // separately below; do not change the renderer's original sampling owner.
            if(writeDiagnostics)
            {
                MergeMask(coreMask, glyph.Core, glyph.SearchBounds);
                MergeMask(haloCandidateMask, glyph.HaloCandidate, glyph.SearchBounds);
                MergeMask(rejectedCandidateMask, glyph.RejectedCandidate, glyph.SearchBounds);
                MergeMask(protectedConflictMask, glyph.ProtectedConflict, glyph.SearchBounds);
                MergeMask(searchEnvelopeMask, glyph.SearchEnvelope, glyph.SearchBounds);
                MergeMask(secondaryColorFamilyMask, glyph.SecondaryColorFamily, glyph.SearchBounds);
            }
            var owner = MaskBoundsWithin(blockMask, glyph.SearchBounds);
            if (owner.Width < 1 || owner.Height < 1)
            {
                audits.Add(new(block.BlockId, block.Lines.Select(x => x.SourceId).ToArray(), block.Bounds,
                    translation.State, false, false, false, 0, "RENDER_FAIL_SOURCE_PRESERVED_NO_SAFE_GLYPH_MASK")); continue;
            }
            var ownership=AnalyzeBackgroundOwnership(source,blockMask,owner,protectedPixels,block,
                surfaceOwners[plan.SurfaceOwnerId],plan,r2Evidence[block.BlockId]);
            // Preserve the renderer's original owner and sampling parameters. The diagnostic
            // domain is separate: semantic feather construction can extend ten pixels past
            // SearchBounds. Reduce actual true pixels in that proven construction envelope.
            var authorityEnvelope=generalBounds.GetValueOrDefault(block.BlockId,Clamp(Rectangle.Inflate(glyph.SearchBounds,10,10),source.Size));
            var runtimeAuthorityBounds=MaskBoundsWithin(blockMask,authorityEnvelope);
            cleanupAuthorities.Add(new(block.BlockId,blockMask,runtimeAuthorityBounds));
            var mutationScan=Rectangle.Union(glyph.SearchBounds,runtimeAuthorityBounds);
            if(ownership.ProvedLowFrequencyPanel&&cleanupMaskMode==CleanupMaskMode.LegacyC13)
                mutationScan=Rectangle.Union(mutationScan,ownership.AuthorizedSurfaceBounds);
            var before=writeDiagnostics?CaptureArgb(restored,mutationScan):[];
            // Capture permission before writing pixels, never infer permission from changes.
            MergeMask(cleanupMask,blockMask,runtimeAuthorityBounds);
            if(writeDiagnostics&&cleanupMaskMode==CleanupMaskMode.LegacyC13&&
               ownership.ProvedLowFrequencyPanel&&ownership.AuthorizedPlane is not null&&
               SelectBackgroundRestorationRoute(ownership.SurfaceClass,ownership.SurfaceConfidence,
                   ownership.TranslucentUi,ownership.HorizontalPatternConfidence,ownership.VerticalPatternConfidence,
                   ownership.LocallySmooth,ownership.ProvedLowFrequencyPanel,ownership.SourceBackgroundConfidence)
                   =="PROVED_LOW_FREQUENCY_PANEL_RESURFACE")
                Fill(cleanupMask,Clamp(ownership.AuthorizedSurfaceBounds,source.Size));
            using var stageCapture=captureMaterialStages?new RestorationStageCapture(output,source,restored,
                block,glyph,blockMask,materialClosure,runtimeAuthorityBounds):null;
            int changed;string restorationMode;
            if(sourceControls.TryGetValue(block.BlockId,out var provenControls))
            {
                changed=0;restorationMode="SOURCE_CONTROL_GLYPH_HOLES";
                foreach(var c in provenControls)foreach(var pixel in c.Pixels)
                {
                    if(!blockMask[pixel.X,pixel.Y])continue;
                    stageCapture?.Candidate(pixel.X,pixel.Y,c.Surface,1f);
                    if(restored.GetPixel(pixel.X,pixel.Y).ToArgb()!=c.Surface.ToArgb())
                    {restored.SetPixel(pixel.X,pixel.Y,c.Surface);changed++;}
                }
            }
            else if(generalPixels is not null && generalMasks.ContainsKey(block.BlockId))
            {
                restorationMode=treatment==BackgroundTreatment.Lightweight?LightweightBackgroundRecovery.StrategyVersion:"LOCAL_LAMA_CONTEXT_RECONSTRUCTION_V1";changed=0;
                var plainGlyphPixels=new Dictionary<Point,Color>();
                foreach(var line in dialogueMaterial?.Materials.ContainsKey(block.BlockId)==true||uniformDisplayInk.ContainsKey(block.BlockId)||glyphFamilies.ContainsKey(block.BlockId)
                    ?Array.Empty<NormalizedOcrLine>():block.Lines)
                    if(sourceGlyphRows.TryGetValue(line.SourceId,out var proof))
                        foreach(var point in proof.Pixels)plainGlyphPixels[point]=proof.Background;
                if(plainGlyphPixels.Count>0)restorationMode="LOCAL_CONTEXT_WITH_PROVEN_FLAT_GLYPH_SURFACE";
                stageCapture?.DonorExclusion(generalExcluded,Point.Empty,true,
                    "All observed OCR lines plus declared recovery domain excluded from model context; not write authority");
                for(var y=runtimeAuthorityBounds.Top;y<runtimeAuthorityBounds.Bottom;y++)
                for(var x=runtimeAuthorityBounds.Left;x<runtimeAuthorityBounds.Right;x++)
                {
                    if(!blockMask[x,y])continue;
                    // The single background commit uses directly verified flat
                    // material for these stroke pixels; inferred model colors
                    // must not introduce a dark glyph-shaped patch there.
                    var pixel=plainGlyphPixels.TryGetValue(new(x,y),out var flat)?flat:generalPixels.GetPixel(x,y);
                    stageCapture?.Candidate(x,y,pixel,1f);
                    if(restored.GetPixel(x,y).ToArgb()!=pixel.ToArgb()){restored.SetPixel(x,y,pixel);changed++;}
                }
            }
            else changed = RestoreLocalSurface(restored, source, blockMask,materialClosure,owner,block,ownership,
                cleanupMaskMode!=CleanupMaskMode.LegacyC13,verifiedSurface,bcpsAudits,out restorationMode,stageCapture,
                nativeTextAdmitted.Contains(block.BlockId)||nativeFootnoteMaterials.ContainsKey(block.BlockId),nativeDonorExclusion);
            if(stageCapture is not null)restorationFields.Add(stageCapture.Finish(restored,restorationMode));
            var mutationOwner=MaskBoundsWithin(blockMask,mutationScan);
            if(writeDiagnostics)maskGeometryAudits.Add(RecordCleanupMaskAudit(output,source,restored,block,glyph,
                ownership,plan,mutationScan,before,cleanupMaskMode,restorationMode));
            restorationOwnershipTrace.Add(new
            {
                block.BlockId,
                SourceIds=block.Lines.Select(x=>x.SourceId).ToArray(),
                GlyphOwner=Rect(owner),
                Owner=Rect(mutationOwner),
                AdmittedPixelCount=CountMaskPixels(blockMask,mutationOwner),
                RestorationChangedPixels=changed,
                RestorationMode=restorationMode,
                ownership.SurfaceClass,ownership.SurfaceConfidence,ownership.TranslucentUi,
                ownership.HorizontalPatternConfidence,ownership.VerticalPatternConfidence,
                ownership.LocallySmooth,ownership.CleanupOwnershipRatio,
                ownership.ProvedLowFrequencyPanel,ownership.AuthorizedSurfaceBounds,
                ownership.SourceBackgroundConfidence,ownership.SharedSurfaceBlockCount,
                ownership.ProtectedPixelCount,ownership.PanelMedianResidual,ownership.PanelP90Residual,
                SurfaceModelId=verifiedSurface?.SurfaceModelId??"",
                VerifiedContainerId=verifiedSurface?.ContainerId??"",
                VerifiedContainerType=verifiedSurface?.ContainerType??"",
                VerifiedSharedBlockCount=verifiedSurface?.BlockIds.Count??0,
                VerifiedSharedLineCount=verifiedSurface?.SharedLineCount??0,
                ownership.DecisionEvidence,
                ProductWorkingBitmap="restored",
                SourceBitmap="source-read-only",
                PreRestorationSnapshot="preRestoration-independent-clone"
            });
            audits.Add(new(block.BlockId, block.Lines.Select(x => x.SourceId).ToArray(), mutationOwner,
                translation.State, true, false, false, changed, "GLYPH_LOCAL_BACKGROUND_RESTORED"));
        }
        productBackgroundTicks += Stopwatch.GetTimestamp() - backgroundStarted;
        backgroundStage?.Dispose();
        ProcessingTaskTrace.Current?.PhaseChanged?.Invoke("正在排版译文");
        using var placementStage=ProcessingTaskTrace.Current?.Timer.Stage("Layout after Background");
        resourceProbe.Capture("after-background-commit");
        var styleDecisions = new Dictionary<string,SourceStyleGuardDecision>(StringComparer.Ordinal);
        var nativeBodySupportDecisions=new Dictionary<string,NativeBodySupportDecision>(StringComparer.Ordinal);
        if(r2Mode!=SourceStyleRenderModeR2.ObserverOnly)
        {
            var styleGuardStarted = Stopwatch.GetTimestamp();
            foreach(var (blockId,fit) in layouts.ToArray())
            {
                // Source typography remains source-owned. Readability support must
                // be checked against the actual surface where this text will be drawn.
                var cleanBackground=BackgroundEstimator.Estimate(restored,fit.Available).Color;
                var decision=SourceStyleLegibilityGuard.FinalizeForFontSize(
                    SourceStyleLegibilityGuard.Evaluate(sourceStyleBundles[blockId],cleanBackground),
                    cleanBackground,fit.FontSize);
                var support=NativeVisualTypography.RefineBodySupport(source,
                    document.VisualBlocks.Single(b=>b.BlockId==blockId),decision,fit.FontSize);
                nativeBodySupportDecisions[blockId]=support;
                decision=support.Decision;
                styleDecisions[blockId]=decision;
                var effective=decision.Effective;
                layouts[blockId]=fit with
                {
                    TextColor=Color.FromArgb(effective.Alpha,effective.FillColor),
                    ColorConfidence=effective.Confidence,
                    Outline=effective.HasOutline,OutlineColor=effective.OutlineColor,OutlineWidth=effective.OutlineWidth,
                    Shadow=effective.HasShadow,ShadowColor=effective.ShadowColor,
                    SourceStyle=effective,StyleAdjustment=decision.Adjustment,
                    CombinedReadability=decision.CombinedReadability,FillPolarityChanged=decision.FillPolarityChanged
                };
            }
            productStyleTicks += Stopwatch.GetTimestamp() - styleGuardStarted;
        }
        // Recheck the existing body plan on the surface that will actually receive
        // the text. Source glyphs must not remain false material obstacles after
        // their cleanup commits. This grants no additional pixel authority.
        var restoredBodyReflow=new List<object>();
        var restoredReflowStarted=Stopwatch.GetTimestamp();
        using(var measure=Graphics.FromImage(restored))
        {
            foreach(var block in document.VisualBlocks)
            {
                if(!layouts.TryGetValue(block.BlockId,out var fit) ||
                    !fit.Fits || fit.NativePlan is not {Candidate:true,Kind:"CardBody"} before ||
                    sourcePreserveFallbacks.Contains(block.BlockId) ||
                    stylizedTitleFallbacks.Contains(block.BlockId) ||
                    !audits.Any(a=>a.BlockId==block.BlockId && a.CleanupCommitted))continue;
                var candidate=NativeCardLayout.Plan(measure,restored,block,document.VisualBlocks,
                    document.Translations[block.BlockId].TranslatedText,fit.ResolvedFontFamily,fit.ResolvedStyle,
                    fit.FontSize,fit.Alignment,sourceStyleBundles[block.BlockId],preserveFontSize:true);
                var wider=candidate.Applied && candidate.SafeLineRects.Count>0 &&
                    before.SafeLineRects.Count>0 &&
                    candidate.SafeLineRects.Average(x=>x.Width)>before.SafeLineRects.Average(x=>x.Width)+2;
                var recovered=!before.Applied && candidate.Applied &&
                    NormalizeText(candidate.LayoutInput)==NormalizeText(fit.NormalizedLayoutInput??"") &&
                    candidate.Lines.Count<=fit.Lines.Count;
                var accept=(wider||recovered) && candidate.CharactersPreserved &&
                    candidate.SearchBounds==before.SearchBounds && candidate.Alignment==before.Alignment &&
                    Math.Abs(candidate.FontSize-fit.FontSize)<.01f &&
                    (recovered || candidate.Lines.Count<=before.Lines.Count && candidate.LayoutInput==before.LayoutInput);
                restoredBodyReflow.Add(new {block.BlockId,Accepted=accept,Reason=candidate.Reason,
                    FontSize=fit.FontSize,BeforeRows=before.Lines.Count,AfterRows=candidate.Lines.Count,
                    BeforeSpans=before.SafeLineRects,AfterSpans=candidate.SafeLineRects,
                    BackgroundMutation=false,SearchUnchanged=candidate.SearchBounds==before.SearchBounds});
                if(!accept)continue;
                candidate=candidate with {PreferredFontSize=before.PreferredFontSize,
                    Reason=recovered?"RESTORED_SURFACE_BODY_SOURCE_TOP_RECOVERED":"RESTORED_SURFACE_BODY_REFLOW_SAME_FONT"};
                layouts[block.BlockId]=fit with {Status="FIT_NATIVE_CARDBODY",Available=candidate.AvailableRect,
                    Lines=candidate.Lines.Select(x=>x.Text).ToArray(),RequiredWidth=candidate.RequiredWidth,
                    RequiredHeight=candidate.RequiredHeight,LineHeight=candidate.LineHeight,NativePlan=candidate,
                    NormalizedLayoutInput=candidate.LayoutInput};
            }
        }
        productLayoutTicks+=Stopwatch.GetTimestamp()-restoredReflowStarted;
        if(writeDiagnostics)File.WriteAllText(Path.Combine(output,"RESTORED-BODY-REFLOW-TRACE.json"),
            JsonSerializer.Serialize(restoredBodyReflow,JsonOptions));
        var restoredLabelTrace=new List<object>();
        var labelRefineStarted=Stopwatch.GetTimestamp();
        using(var measure=Graphics.FromImage(restored))
        using(var cleanPixels=ReadOnlyBitmapPixelBuffer.Create(restored))
        {
            foreach(var block in document.VisualBlocks.Where(b=>uiLabelAlignment.ContainsKey(b.BlockId)))
            {
                if(!layouts.TryGetValue(block.BlockId,out var fit) || !fit.Fits ||
                    fit.NativePlan is {Applied:true,Kind:not "CompactSourceLabel"} ||
                    sourcePreserveFallbacks.Contains(block.BlockId) || stylizedTitleFallbacks.Contains(block.BlockId) ||
                    !audits.Any(a=>a.BlockId==block.BlockId && a.CleanupCommitted))continue;
                var candidate=NativeCompactTextLayout.Plan(measure,restored,block,document.VisualBlocks,
                    document.Translations[block.BlockId].TranslatedText,fit.ResolvedFontFamily,fit.ResolvedStyle,
                    fit.PreferredFontSize,"Left",sourceStyleBundles[block.BlockId],cleanPixels,sourceLeftAnchor:true);
                var accepted=candidate.Applied && candidate.CharactersPreserved && candidate.FontSize>=fit.FontSize &&
                    candidate.Lines.Count==1 && candidate.LayoutInput==fit.NormalizedLayoutInput;
                restoredLabelTrace.Add(new{block.BlockId,Accepted=accepted,candidate.Reason,BeforeFont=fit.FontSize,
                    AfterFont=candidate.FontSize,Before=fit.Available,After=candidate.AvailableRect,BackgroundMutation=false});
                if(!accepted)continue;
                layouts[block.BlockId]=fit with {Status="FIT_NATIVE_COMPACTSOURCELABEL",FontSize=candidate.FontSize,
                    PreferredFontSize=candidate.PreferredFontSize,ScaleRatio=candidate.FontSize/candidate.PreferredFontSize,
                    Available=candidate.AvailableRect,Lines=candidate.Lines.Select(x=>x.Text).ToArray(),
                    RequiredWidth=candidate.RequiredWidth,RequiredHeight=candidate.RequiredHeight,
                    LineHeight=candidate.LineHeight,Alignment=candidate.Alignment,NativePlan=candidate};
            }
        }
        productLayoutTicks+=Stopwatch.GetTimestamp()-labelRefineStarted;
        if(writeDiagnostics)File.WriteAllText(Path.Combine(output,"RESTORED-LABEL-TRACE.json"),
            JsonSerializer.Serialize(restoredLabelTrace,JsonOptions));
        // Final presentation is decided after cleanup. These plans cannot alter source
        // geometry, admission, masks, background pixels, or translation identities.
        var finalFlowStarted=Stopwatch.GetTimestamp();
        var finalNotes=new List<object>();
        IReadOnlyList<TypographyFlowLayout.Decision> finalFlow;
        using(var measure=Graphics.FromImage(restored))
        {
            foreach(var block in document.VisualBlocks.Where(b=>
                layouts.GetValueOrDefault(b.BlockId)?.NativePlan is {Applied:true,Kind:"IndependentFootnote"}))
            {
                var fit=layouts[block.BlockId];
                if(!audits.Any(a=>a.BlockId==block.BlockId&&a.CleanupCommitted))continue;
                var note=NativeVisualLayout.Plan(measure,source,block,document.VisualBlocks,
                    document.Translations[block.BlockId].TranslatedText,fit.ResolvedFontFamily,fit.ResolvedStyle,
                    fit.PreferredFontSize,fit.Alignment,acceptedTitleTerms,preferProtectedTerms:true,admittedPlan:fit.NativePlan);
                finalNotes.Add(new{block.BlockId,Plan=note});
                if(!note.Applied||!note.CharactersPreserved)continue;
                layouts[block.BlockId]=fit with{Status="FIT_FINAL_COMPLETE_NOTE",FontSize=note.FontSize,
                    ScaleRatio=note.FontSize/fit.PreferredFontSize,Available=note.AvailableRect,
                    Lines=note.Lines.Select(l=>l.Text).ToArray(),RequiredWidth=note.RequiredWidth,
                    RequiredHeight=note.RequiredHeight,LineHeight=note.LineHeight,Alignment=note.Alignment,
                    NativePlan=note,NormalizedLayoutInput=note.LayoutInput};
            }
            var flowInputs=document.VisualBlocks.Where(block=>
                layouts.TryGetValue(block.BlockId,out var fit) && fit.Fits && !fit.SourceOriented &&
                document.Translations[block.BlockId].State==BlockTranslationState.Accepted &&
                !sourcePreserveFallbacks.Contains(block.BlockId) && !stylizedTitleFallbacks.Contains(block.BlockId) &&
                audits.Any(a=>a.BlockId==block.BlockId && a.CleanupCommitted))
                .Select(block=>
                {
                    var fit=layouts[block.BlockId];
                    return new TypographyFlowLayout.Input(block,document.Translations[block.BlockId].TranslatedText,
                        fit.ResolvedFontFamily,fit.ResolvedStyle,fit.FontSize,fit.PreferredFontSize,
                        fit.Alignment,fit.TextColor,r2Evidence[block.BlockId],fit.NativePlan,
                        fit.Outline&&!sourceStyleBundles[block.BlockId].HasOutline &&
                            !sourceStyleBundles[block.BlockId].Owner.StartsWith("User",StringComparison.Ordinal),
                        !sourceStyleBundles[block.BlockId].Owner.StartsWith("User",StringComparison.Ordinal),fit.Available);
                }).ToArray();
            finalFlow=TypographyFlowLayout.Plan(measure,sourcePixels,flowInputs,document.VisualBlocks);
            foreach(var decision in finalFlow)
            {
                var fit=layouts[decision.BlockId];
                if(decision.RequiredSourceSpace&&decision.Plan is not {Applied:true})
                {
                    layouts[decision.BlockId]=fit with {Fits=false,
                        Status="FINAL_SOURCE_SPACE_FAILED:"+decision.Reason,NativePlan=decision.Plan};
                    continue;
                }
                if(decision.SuppressAddedOutline)
                {
                    fit=fit with{Outline=false,OutlineWidth=0,StyleAdjustment=fit.StyleAdjustment+";SOURCE_PLAIN_FILL_RETAINED"};
                    layouts[decision.BlockId]=fit;
                }
                if(decision.Plan is not {Applied:true} plan)continue;
                layouts[decision.BlockId]=fit with {Status="FIT_FINAL_TYPOGRAPHY_FLOW",FontSize=plan.FontSize,
                    ResolvedStyle=decision.ProvenWeight??fit.ResolvedStyle,
                    ScaleRatio=plan.FontSize/fit.PreferredFontSize,Available=plan.AvailableRect,
                    Lines=plan.Lines.Select(l=>l.Text).ToArray(),RequiredWidth=plan.RequiredWidth,
                    RequiredHeight=plan.RequiredHeight,LineHeight=plan.LineHeight,Alignment=plan.Alignment,
                    NativePlan=plan,NormalizedLayoutInput=plan.LayoutInput};
            }
        }
        productLayoutTicks+=Stopwatch.GetTimestamp()-finalFlowStarted;
        if(writeDiagnostics)File.WriteAllText(Path.Combine(output,"FINAL-TYPOGRAPHY-FLOW.json"),
            JsonSerializer.Serialize(finalFlow,JsonOptions));
        if(backgroundDiagnostics is not null)File.WriteAllText(Path.Combine(backgroundDiagnostics,"FINAL-TYPOGRAPHY-FLOW.json"),
            JsonSerializer.Serialize(finalFlow,JsonOptions));
        if(writeDiagnostics)File.WriteAllText(Path.Combine(output,"FINAL-NOTE-LAYOUT.json"),JsonSerializer.Serialize(finalNotes,JsonOptions));
        if(backgroundDiagnostics is not null)File.WriteAllText(Path.Combine(backgroundDiagnostics,"FINAL-NOTE-LAYOUT.json"),JsonSerializer.Serialize(finalNotes,JsonOptions));
        ContinuationLayoutPlanningResult paragraphLayout;
        var continuationLayoutStarted = Stopwatch.GetTimestamp();
        using(var measure=Graphics.FromImage(restored))
        {
            var relation=ContinuationRelation.Propose(document.VisualBlocks);
            var evidence=document.VisualBlocks.Where(block=>
                    document.Translations[block.BlockId].State==BlockTranslationState.Accepted&&
                    !sourcePreserveFallbacks.Contains(block.BlockId)&&
                    layouts.GetValueOrDefault(block.BlockId)?.NativePlan?.Applied!=true&&
                    layouts.TryGetValue(block.BlockId,out var fit)&&fit.Fits)
                .Select(block=>
                {
                    var fit=layouts[block.BlockId];var translation=document.Translations[block.BlockId];
                    return new ContinuationLayoutBlockEvidence(block.BlockId,
                        block.Lines.Select(x=>x.SourceId).ToArray(),block.Bounds,fit.Available,
                        translation.TranslatedText,fit.ResolvedFontFamily,fit.ResolvedStyle,fit.FontSize,fit.TextColor,
                        fit.Outline,fit.OutlineColor,fit.OutlineWidth,fit.Shadow,fit.ShadowColor,
                        fit.SourceStyle.ShadowOffset,fit.SourceStyle.Glow,fit.SourceStyle.Alpha);
                }).ToArray();
            paragraphLayout=ContinuationLayoutPlanner.Plan(measure,document,relation,evidence);
        }
        productLayoutTicks += Stopwatch.GetTimestamp() - continuationLayoutStarted;
        if (writeDiagnostics) SaveContinuationLayoutEvidence(source,paragraphLayout,output);
        if (writeDiagnostics) File.WriteAllText(Path.Combine(output,"SOURCE-STYLE-BUNDLE-TRACE.json"),JsonSerializer.Serialize(new
        {
            Contract="SOURCE_STYLE_BUNDLE_V1",Mode=r2Mode.ToString(),
            FillPolarityFlips=styleDecisions.Values.Count(x=>x.FillPolarityChanged),
            OwnerAnalysisTicks=sourceStyleTimer.ElapsedTicks,
            OwnerAnalysisMs=sourceStyleTimer.Elapsed.TotalMilliseconds,
            TotalGuardTicks=styleDecisions.Values.Sum(x=>x.ElapsedTicks),
            GuardMs=styleDecisions.Values.Sum(x=>x.ElapsedTicks)*1000d/System.Diagnostics.Stopwatch.Frequency,
            BundleOverheadMs=sourceStyleTimer.Elapsed.TotalMilliseconds+
                styleDecisions.Values.Sum(x=>x.ElapsedTicks)*1000d/System.Diagnostics.Stopwatch.Frequency,
            Blocks=document.VisualBlocks.Select(block=>
            {
                var sourceBundle=sourceStyleBundles[block.BlockId];
                styleDecisions.TryGetValue(block.BlockId,out var decision);
                var fit=layouts.GetValueOrDefault(block.BlockId);
                return new
                {
                    block.BlockId,SourceIds=block.Lines.Select(x=>x.SourceId),sourceBundle.Owner,sourceBundle.Role,
                    SourceFill=SourceStyleBundleOwner.Hex(sourceBundle.FillColor),
                    SourceOutline=SourceStyleBundleOwner.Hex(sourceBundle.OutlineColor),sourceBundle.OutlineWidth,
                    SourceShadow=SourceStyleBundleOwner.Hex(sourceBundle.ShadowColor),sourceBundle.ShadowOffset,
                    sourceBundle.Glow,sourceBundle.Alpha,sourceBundle.Weight,sourceBundle.Polarity,sourceBundle.Confidence,sourceBundle.Evidence,
                    EffectiveFill=fit is null?null:SourceStyleBundleOwner.Hex(fit.TextColor),
                    EffectiveOutline=fit is null?null:SourceStyleBundleOwner.Hex(fit.OutlineColor),EffectiveOutlineWidth=fit?.OutlineWidth,
                    EffectiveShadow=fit is null?null:SourceStyleBundleOwner.Hex(fit.ShadowColor),
                    decision?.FillContrast,decision?.CombinedReadability,decision?.Adjustment,decision?.FillPolarityChanged,decision?.ElapsedTicks
                };
            })
        },JsonOptions));
        if (writeDiagnostics) File.WriteAllText(Path.Combine(output,"05A-ADMISSION-PLAN.json"),JsonSerializer.Serialize(new
        {
            ContractVersion="TRUE_STAGE_CONTRACT_V1",
            CleanupMeaning="ADMITTED_PIXEL_PLAN",
            DestructiveEraseStage=false,
            PreRestorationPixelsEqualSource=true,
            AdmittedPixelCount=CountMaskPixels(cleanupMask,new Rectangle(0,0,source.Width,source.Height)),
            ProtectedPixelCount=CountMaskPixels(protectedPixels,new Rectangle(0,0,source.Width,source.Height)),
            Owners=restorationOwnershipTrace
        },JsonOptions));
        if (writeDiagnostics) File.WriteAllText(Path.Combine(output,"STAGE-OWNERSHIP-TRACE.json"),JsonSerializer.Serialize(new
        {
            ContractVersion="TRUE_STAGE_CONTRACT_V1",
            ProductBitmap="restored",
            SourceBitmap="source-read-only",
            PreRestorationBitmap="preRestoration-independent-clone",
            FinalBitmap="final-independent-clone",
            Stages=new object[]
            {
                new{File="05-CLEANUP-MASK.png",Meaning="admission decision; no product pixels"},
                new{File="05A-ADMISSION-PLAN.json",Meaning="structured admission and restoration ownership"},
                new{File="05B-PRE-RESTORATION.png",Meaning="real product working pixels before first restoration write; source-equivalent because no erase pass exists"},
                new{File="06-POST-RESTORATION.png",Meaning="product working pixels after all restoration writes and before translated text draw"},
                new{File="07-FINAL-TRANSLATED.png",Meaning="independent final clone after translated text draw"}
            },
            LegacyAliases=new object[]
            {
                new{File="05B-CLEANUP-APPLIED.png",AliasOf="05B-PRE-RESTORATION.png"},
                new{File="06-BACKGROUND-RESTORED.png",AliasOf="06-POST-RESTORATION.png"}
            },
            ProductPixelAlgorithmChanged=false,
            ProductBitmapLifetimeChanged=false
        },JsonOptions));

        var placements=new Dictionary<string,FinalTextPlacement.Plan>(StringComparer.Ordinal);
        var finalTitles=new Dictionary<string,StylizedTitleLabel>(StringComparer.Ordinal);
        var failedPlacement=new HashSet<string>(StringComparer.Ordinal);
        var finalPlacementStarted=Stopwatch.GetTimestamp();
        var regionPlacement=new List<object>();
        using(var measure=Graphics.FromImage(restored))
        {
            measure.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            foreach(var block in document.VisualBlocks)
            {
                var t=document.Translations[block.BlockId];
                if(t.State!=BlockTranslationState.Accepted || sourcePreserveFallbacks.Contains(block.BlockId))continue;
                if(!layouts.TryGetValue(block.BlockId,out var fit)||!fit.Fits)
                {failedPlacement.Add(block.BlockId);continue;}
                if(stylizedTitleFallbacks.Contains(block.BlockId))
                {
                    var label=PlanStylizedTitleTranslation(measure,source,block,document.VisualBlocks,t.TranslatedText,fit.ResolvedFontFamily);
                    finalTitles[block.BlockId]=label;
                    if(label.Bounds.IsEmpty){failedPlacement.Add(block.BlockId);continue;}
                    var titlePaint=new FinalTextPlacement.Paint(fit.ResolvedFontFamily,FontStyle.Bold,label.FontSize,
                        label.Color,false,Color.Transparent,0,true,
                        Color.FromArgb(105,label.Color.GetBrightness()>.55f?Color.Black:Color.White),new(1,1),0,255);
                    placements[block.BlockId]=FinalTextPlacement.Resolve(measure,block.BlockId,label.Text,
                        new[]{new NativeVisualLine(label.Text,label.Bounds)},RectangleF.Inflate(label.Bounds,2,2),[],
                        titlePaint,source.Size);
                }
                else if(paragraphLayout.ActiveOwnerByBlock.TryGetValue(block.BlockId,out var paragraph))
                {
                    if(!placements.ContainsKey(paragraph.ProposalId))
                    {
                        var paint=new FinalTextPlacement.Paint(paragraph.FontFamily,paragraph.FontStyle,paragraph.FontSize,
                            paragraph.TextColor,paragraph.Outline,paragraph.OutlineColor,paragraph.OutlineWidth,
                            paragraph.Shadow,paragraph.ShadowColor,paragraph.ShadowOffset,paragraph.Glow,paragraph.Alpha);
                        placements[paragraph.ProposalId]=FinalTextPlacement.Resolve(measure,paragraph.ProposalId,
                            paragraph.CombinedText,paragraph.Lines.Select(l=>new NativeVisualLine(l.Text,l.Bounds)).ToArray(),
                            paragraph.AvailableRect,[],paint,source.Size);
                    }
                    placements[block.BlockId]=placements[paragraph.ProposalId];
                }
                else
                {
                    var paint=new FinalTextPlacement.Paint(fit.ResolvedFontFamily,fit.ResolvedStyle,fit.FontSize,
                        fit.TextColor,fit.Outline,fit.OutlineColor,fit.OutlineWidth,fit.Shadow,fit.ShadowColor,
                        fit.SourceStyle.ShadowOffset,fit.SourceStyle.Glow,fit.SourceStyle.Alpha,
                        fit.SourcePixelScale||fit.NativePlan?.SourcePixelScale==true,nativeBodySupportDecisions.GetValueOrDefault(block.BlockId));
                    IReadOnlyList<NativeVisualLine> rows;
                    IReadOnlyList<RectangleF> safeRows=[];
                    var safe=fit.Available;
                    if(fit.NativePlan is {Applied:true} native)
                    {
                        rows=native.Lines; safeRows=native.SafeLineRects;
                        if(block.SourceCell is {} cell){safe=cell.Bounds;safeRows=[];}
                    }
                    else
                    {
                        // This is the last measurement point. The draw pass below
                        // cannot recenter the text or choose a different row break.
                        using var font=FontManager.CreatePixel(paint.Family,paint.Size,paint.Weight,paint.SourcePixelScale);
                        var y=ParagraphAlignmentEvidence.OwnsProseAlignment(block)?Math.Max(fit.Available.Top,block.Bounds.Top):
                            fit.Available.Top+Math.Max(0,(fit.Available.Height-fit.RequiredHeight)/2f);
                        var positions=new List<NativeVisualLine>();
                        foreach(var line in fit.Lines)
                        {
                            var size=measure.MeasureString(line,font,PointF.Empty,StringFormat.GenericTypographic);
                            var x=fit.Alignment=="Center"?fit.Available.Left+Math.Max(0,(fit.Available.Width-size.Width)/2f):fit.Available.Left;
                            positions.Add(new(line,new(x,y,MathF.Ceiling(size.Width),MathF.Ceiling(fit.LineHeight))));
                            y+=fit.LineHeight;
                        }
                        rows=positions;
                    }
                    placements[block.BlockId]=SourceOrientedText.Plan(measure,block,t.TranslatedText,paint,source.Size)
                        ?? FinalTextPlacement.Resolve(measure,block.BlockId,t.TranslatedText,
                        rows,safe,safeRows,paint,source.Size);
                }
                if(placements.TryGetValue(block.BlockId,out var placed))
                {
                    var aligned=regionRecovery.Place(placed,out var regionDecision);
                    placements[block.BlockId]=aligned;
                    if(regionDecision!="UNCHANGED")regionPlacement.Add(new{block.BlockId,Decision=regionDecision,Before=placed,After=aligned});
                    if(!aligned.Valid)failedPlacement.Add(block.BlockId);
                }
            }
        }
        regionRecovery.ReleaseLayoutBuffers();
        var inkSeparation=FinalInkSeparation.Validate(placements,source.Size,cancellationToken);
        if(!string.IsNullOrEmpty(displayOutput))File.WriteAllText(Path.Combine(displayOutput,"REGION-FINAL-PLACEMENT.json"),JsonSerializer.Serialize(regionPlacement,JsonOptions));
        foreach(var entry in placements.Where(p=>!p.Value.Valid))failedPlacement.Add(entry.Key);
        // A continuation is indivisible: all of its fragment owners must be
        // admitted, validated and committed together.
        foreach(var owner in paragraphLayout.Owners.Where(o=>o.Active))
            if(owner.UnderlyingBlockIds.Any(id=>failedPlacement.Contains(id)||
                !audits.Any(a=>a.BlockId==id&&a.CleanupCommitted)))
                foreach(var id in owner.UnderlyingBlockIds)failedPlacement.Add(id);
        IReadOnlySet<string> withdrawn=failedPlacement;
        for(var pass=0;pass<=paragraphLayout.Owners.Count;pass++)
        {
            withdrawn=FinalCleanupTransaction.RestoreFailed(source,restored,cleanupMask,cleanupAuthorities,failedPlacement);
            var count=failedPlacement.Count;
            foreach(var id in withdrawn)failedPlacement.Add(id);
            foreach(var owner in paragraphLayout.Owners.Where(o=>o.Active&&o.UnderlyingBlockIds.Any(withdrawn.Contains)))
                foreach(var id in owner.UnderlyingBlockIds)failedPlacement.Add(id);
            if(failedPlacement.Count==count)break;
        }
        for(var i=0;i<audits.Count;i++)
        {
            var a=audits[i];if(!withdrawn.Contains(a.BlockId))continue;
            var why=placements.TryGetValue(a.BlockId,out var plan)&&!plan.Valid?plan.Reason:
                layouts.GetValueOrDefault(a.BlockId)?.Fits==false?layouts[a.BlockId].Status:"SHARED_AUTHORITY_OR_FRAGMENT_WITHDRAWN";
            audits[i]=a with {CleanupCommitted=false,TextDrawn=false,AtomicCommit=false,
                Result="FINAL_LAYOUT_FAILED_SOURCE_RESTORED:"+why};
        }
        productLayoutTicks+=Stopwatch.GetTimestamp()-finalPlacementStarted;
        var placementOutput=writeDiagnostics?output:backgroundDiagnostics;
        if(placementOutput is not null)File.WriteAllText(Path.Combine(placementOutput,"DISPLAY-ERASURE-SAFETY.json"),
            JsonSerializer.Serialize(new{Contract="SOURCE_MATERIAL_SAFETY_NOT_ART_CLASSIFICATION_V1",
                TranslationSuccess=false,Rejected=unsafeDisplaySurfaces},JsonOptions));
        if(placementOutput is not null)File.WriteAllText(Path.Combine(placementOutput,"FINAL-PLACEMENT-TRANSACTION.json"),
            JsonSerializer.Serialize(new{Contract="FINAL_VALIDATED_PLACEMENT_V1",Plans=placements,
                Withdrawn=withdrawn,InkSeparation=inkSeparation,DrawMeasuresOrReflows=false,CleanupAuthorityExpandedByLayout=false},JsonOptions));

        if(!writeDiagnostics && backgroundDiagnostics is not null)
        {
            var captureStarted=Stopwatch.GetTimestamp();
            Directory.CreateDirectory(backgroundDiagnostics);
            restored.Save(Path.Combine(backgroundDiagnostics,"06-BACKGROUND-RESTORED.png"),ImageFormat.Png);
            GeneralBackgroundRecovery.SaveMask(cleanupMask,Path.Combine(backgroundDiagnostics,"05-CLEANUP-MASK.png"));
            GeneralBackgroundRecovery.SaveMask(protectedPixels,Path.Combine(backgroundDiagnostics,"05A-PROTECTED-PIXELS.png"));
            WriteFullFrameAuthorityAudit(backgroundDiagnostics,preRestoration!,restored,cleanupMask);
            productEncodeTicks+=Stopwatch.GetTimestamp()-captureStarted;
        }
        if (writeDiagnostics)
        {
            var encodeStarted = Stopwatch.GetTimestamp();
            SaveBinaryMap(cleanupMask, Path.Combine(output, "05-CLEANUP-MASK.png"));
            SaveBinaryMap(protectedPixels, Path.Combine(output, "05A-PROTECTED-PIXELS.png"));
            SaveBinaryMap(coreMask, Path.Combine(output, "05C-GLYPH-CORE-MASK.png"));
            SaveBinaryMap(haloCandidateMask, Path.Combine(output, "05D-GLYPH-HALO-CANDIDATE.png"));
            SaveBinaryMap(cleanupMask, Path.Combine(output, "05E-ADMITTED-CLEANUP-MASK.png"));
            SaveBinaryMap(rejectedCandidateMask, Path.Combine(output,"05H-REJECTED-CANDIDATE-PIXELS.png"));
            SaveBinaryMap(protectedConflictMask, Path.Combine(output,"05I-PROTECTED-CONFLICT.png"));
            SaveBinaryMap(searchEnvelopeMask, Path.Combine(output,"05J-GLYPH-SEARCH-ENVELOPE.png"));
            SaveBinaryMap(secondaryColorFamilyMask, Path.Combine(output,"05K-SECONDARY-TEXT-COLOR-FAMILY.png"));
            var sourceEdges=BuildSourceEdgeMap(source,document);
            SaveBinaryMap(sourceEdges,Path.Combine(output,"05F-SOURCE-EDGES.png"));
            SaveBinaryMap(SubtractMask(sourceEdges,cleanupMask),Path.Combine(output,"05G-MASKED-OUT-SOURCE-EDGES.png"));
            preRestoration!.Save(Path.Combine(output,"05B-PRE-RESTORATION.png"));
            preRestoration!.Save(Path.Combine(output, "05B-CLEANUP-APPLIED.png"));
            restored.Save(Path.Combine(output,"06-POST-RESTORATION.png"));
            restored.Save(Path.Combine(output, "06-BACKGROUND-RESTORED.png"));
            WriteFullFrameAuthorityAudit(output,preRestoration!,restored,cleanupMask);
            productEncodeTicks += Stopwatch.GetTimestamp() - encodeStarted;
        }
        var final = new Bitmap(restored);
        using var debug = writeDiagnostics ? new Bitmap(restored) : null;
        placementStage?.Dispose();
        using var drawStage=ProcessingTaskTrace.Current?.Timer.Stage("Text Drawing");
        var drawStarted = Stopwatch.GetTimestamp();
        using (var g = Graphics.FromImage(final))
        using (var dg = writeDiagnostics ? Graphics.FromImage(debug!) : null)
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var ownerPen=new Pen(Color.Lime,2);using var availablePen=new Pen(Color.DeepSkyBlue,2);using var measuredPen=new Pen(Color.Gold,2);using var clippedPen=new Pen(Color.Red,3);
            var drawnParagraphOwners=new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < document.VisualBlocks.Count; i++)
            {
                var block = document.VisualBlocks[i]; var t = document.Translations[block.BlockId];
                var renderPlan=r2Style.Plans[block.BlockId];
                if (t.State != BlockTranslationState.Accepted) continue;
                if(sourceTextNoOps.Contains(block.BlockId))
                {
                    traces.Add(Trace(block,t,layouts.GetValueOrDefault(block.BlockId),SourceTextNoOp.Result,
                        r2Plan:renderPlan));continue;
                }
                if(!layouts.TryGetValue(block.BlockId,out var fit)||!fit.Fits)
                {
                    traces.Add(Trace(block,t,fit,"FIT_FAILED_CONTENT_PRESERVED",r2Plan:renderPlan));continue;
                }
                if(sourcePreserveFallbacks.Contains(block.BlockId))
                {
                    traces.Add(Trace(block,t,fit,unsafeDisplaySurfaces.ContainsKey(block.BlockId)?
                        SourceDisplaySurfaceGuard.Failure:"SOURCE_PRESERVED_LOW_CONTRAST_SCENE_PANEL",
                        r2Plan:renderPlan));continue;
                }
                if(stylizedTitleFallbacks.Contains(block.BlockId))
                {
                    var label=finalTitles.GetValueOrDefault(block.BlockId)??new(RectangleF.Empty,Color.Transparent,0,"");
                    var titlePlacement=placements.GetValueOrDefault(block.BlockId);
                    if(label.Bounds.IsEmpty||titlePlacement is not {Valid:true}||withdrawn.Contains(block.BlockId))
                    {
                        traces.Add(Trace(block,t,fit,"NO_SAFE_READABLE_ART_TITLE_LABEL",r2Plan:renderPlan));
                        audits[i]=audits[i] with {TextDrawn=false,AtomicCommit=false,Result="NO_SAFE_READABLE_ART_TITLE_LABEL"};
                        continue;
                    }
                    FinalTextPlacement.Draw(g,titlePlacement!);
                    if(writeDiagnostics)dg!.DrawRectangle(availablePen,Rectangle.Round(label.Bounds));
                    traces.Add(Trace(block,t,fit,"STYLE_SAFE_SOURCE_TITLE_PRESERVED_WITH_TRANSLATED_LABEL",
                        t.TranslatedText,label.Bounds,false,true,renderPlan));
                    var titleAudit=audits[i];audits[i]=titleAudit with
                    {TextDrawn=true,AtomicCommit=true,Result="STYLE_SAFE_SOURCE_TITLE_PLUS_TRANSLATED_LABEL"};
                    stylizedTitleTrace.Add(new
                    {
                        block.BlockId,DrawBounds=Rect(label.Bounds),TextColor=$"#{label.Color.R:X2}{label.Color.G:X2}{label.Color.B:X2}",
                        label.FontSize,CompleteTranslation=true,SourceTitlePixelsChanged=false
                    });
                    continue;
                }
                // A fit is not an atomic rendering commit. If source material was
                // not safely admitted/restored, do not overlay a second language.
                if(!audits[i].CleanupCommitted)
                {
                    traces.Add(Trace(block,t,fit,"SOURCE_PRESERVED_NO_SAFE_CLEANUP",r2Plan:renderPlan));
                    continue;
                }
                if(paragraphLayout.ActiveOwnerByBlock.TryGetValue(block.BlockId,out var paragraphOwner))
                {
                    if(drawnParagraphOwners.Add(paragraphOwner.ProposalId))
                        FinalTextPlacement.Draw(g,placements[block.BlockId]);
                    if(writeDiagnostics)
                    {
                        dg!.DrawRectangle(ownerPen,Rectangle.Round(paragraphOwner.ConservativeEnvelope));
                        dg.DrawRectangle(availablePen,Rectangle.Round(paragraphOwner.AvailableRect));
                        foreach(var line in paragraphOwner.Lines)dg.DrawRectangle(measuredPen,Rectangle.Round(line.Bounds));
                    }
                    traces.Add(Trace(block,t,fit,$"LAYOUT_OWNER_MEMBER:{paragraphOwner.ProposalId}",
                        t.TranslatedText,paragraphOwner.ConservativeEnvelope,false,true,renderPlan));
                    var paragraphAudit=audits[i];audits[i]=paragraphAudit with
                    {TextDrawn=true,AtomicCommit=true,Result=$"ATOMIC_LAYOUT_OWNER_COMMIT:{paragraphOwner.ProposalId}"};
                    continue;
                }
                if(!placements.TryGetValue(block.BlockId,out var finalPlacement)||!finalPlacement.Valid)
                    throw new InvalidOperationException("Committed text lacks a final validated placement.");
                FinalTextPlacement.Draw(g,finalPlacement);
                if(writeDiagnostics)
                {
                    dg!.DrawRectangle(ownerPen,Rectangle.Round(block.Bounds));
                    dg.DrawRectangle(availablePen,Rectangle.Round(fit.Available));
                    dg.DrawRectangle(measuredPen,Rectangle.Round(finalPlacement.Ink));
                }
                traces.Add(Trace(block,t,fit,fit.Status,finalPlacement.Text,finalPlacement.Ink,false,true,renderPlan));
                audits[i]=audits[i] with {TextDrawn=true,AtomicCommit=true,Result="ATOMIC_VALIDATED_BLOCK_COMMIT"};
            }
        }
        productDrawTicks += Stopwatch.GetTimestamp() - drawStarted;
        drawStage?.Dispose();
        if (writeDiagnostics)
        {
            var encodeStarted = Stopwatch.GetTimestamp();
            final.Save(Path.Combine(output, "07-FINAL-TRANSLATED.png"));
            FinalizeCleanupMaskAudits(output,source,final,maskGeometryAudits);
            FinalizeBcpsAudits(output,bcpsAudits);
            WriteProfilePanelLineEvidence(output,source,restored,final,document,preparedGlyphs,
                verifiedSurfaceByBlock,audits);
            debug!.Save(Path.Combine(output,"08-TEXT-FIT-DEBUG.png"));
            File.WriteAllText(Path.Combine(output,"TEXT-FIT-TRACE.json"),JsonSerializer.Serialize(traces,JsonOptions));
            File.WriteAllText(Path.Combine(output,"NATIVE-LAYOUT-TRACE.json"),JsonSerializer.Serialize(
                layouts.Where(x=>x.Value.NativePlan is not null).Select(x=>new {
                    BlockId=x.Key,Plan=x.Value.NativePlan,
                    Source=document.VisualBlocks.Single(b=>b.BlockId==x.Key).SourceText,
                    Accepted=document.Translations[x.Key].TranslatedText,
                    DrawAudit=audits.Single(a=>a.BlockId==x.Key)
                }),JsonOptions));
            File.WriteAllText(Path.Combine(output,"NATIVE-WEIGHT-TRACE.json"),JsonSerializer.Serialize(nativeWeightDecisions,JsonOptions));
            File.WriteAllText(Path.Combine(output,"NATIVE-BODY-SUPPORT-TRACE.json"),JsonSerializer.Serialize(nativeBodySupportDecisions,JsonOptions));
            File.WriteAllText(Path.Combine(output,"NATIVE-TEXT-MATERIAL-TRACE.json"),JsonSerializer.Serialize(
                nativeTextMaterials.Select(x=>new {BlockId=x.Key,Applied=nativeTextAdmitted.Contains(x.Key)||curvedTextAdmitted.Contains(x.Key),
                    MaterialRoute=curvedTextAdmitted.Contains(x.Key)?"SOURCE_CURVED_GLYPH_MATERIAL_GENERAL_BACKGROUND":"EXISTING_SOURCE_MATERIAL_ROUTE",
                    Lines=x.Value.Select(line=>new {line.Accepted,line.Reason,line.Bounds,line.Polarity,
                        line.RingBackground,line.RingVariation,line.Components,line.UncoveredOppositeOutlineComponents,line.UncoveredContrastComponents,line.SourceColorMinAlpha,line.SourceColorSupported,line.SourceColorMissing,line.SourceColorGrowth,line.SourceColorGate,Primary=line.Primary.Count,
                        Antialias=line.Antialias.Count,Material=line.Material.Count})}),JsonOptions));
            File.WriteAllText(Path.Combine(output,"NATIVE-FOOTNOTE-MATERIAL-TRACE.json"),JsonSerializer.Serialize(
                nativeFootnoteMaterials.Select(x=>new {BlockId=x.Key,x.Value.Applied,x.Value.Reason,
                    x.Value.SourceBounds,x.Value.Background,x.Value.SourceFill,x.Value.Confidence,
                    CorePixels=x.Value.CorePixels.Count,AntialiasPixels=x.Value.AntialiasPixels.Count,
                    x.Value.PaperFraction,x.Value.OuterRingPaperFraction,x.Value.UsedTightBoundsPaperEvidence,
                    x.Value.Components,Policy="REPLACE_WITH_PROVEN_SOURCE_MATERIAL_LAYOUT_EXPANSION_NOT_AUTHORIZED"}),JsonOptions));
            foreach(var field in restorationFields)
                SaveBitmapCrop(final,field.Roi,Path.Combine(field.Root,"FINAL.png"));
            File.WriteAllText(Path.Combine(output,"GLYPH-ADMISSION-TRACE.json"),JsonSerializer.Serialize(new
        {
            Rule="SECONDARY_TEXT_COLOR_FAMILY_COMPONENTS", MajorAdmissionRuleCount=1,
            ElapsedMs=admissionElapsedTicks*1000d/System.Diagnostics.Stopwatch.Frequency,
            Components=admissionTrace
            },JsonOptions));
            File.WriteAllText(Path.Combine(output,"STYLIZED-TITLE-FALLBACK-TRACE.json"),
                JsonSerializer.Serialize(new{Policy="STYLE_SAFE_SOURCE_TITLE_PLUS_TRANSLATED_LABEL",Blocks=stylizedTitleTrace},JsonOptions));
            productEncodeTicks += Stopwatch.GetTimestamp() - encodeStarted;
        }
        if(!writeDiagnostics && backgroundDiagnostics is not null)
        {
            var captureStarted=Stopwatch.GetTimestamp();
            File.WriteAllText(Path.Combine(backgroundDiagnostics,"TEXT-FIT-TRACE.json"),JsonSerializer.Serialize(traces,JsonOptions));
            productEncodeTicks+=Stopwatch.GetTimestamp()-captureStarted;
        }
        productTotalTimer.Stop();
        static double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;
        var timing = new CorePipelineProductTiming(
            Ms(productStyleTicks), Ms(productBackgroundTicks), Ms(productLayoutTicks), Ms(productDrawTicks),
            Ms(productEncodeTicks), productTotalTimer.Elapsed.TotalMilliseconds, writeDiagnostics||backgroundDiagnostics is not null,
            writeDiagnostics ? 20 : backgroundDiagnostics is null?0:3+(generalBackground is null?0:3),
            writeDiagnostics ? 15+restorationFields.Count*2 : backgroundDiagnostics is null?0:2+(generalBackground is null?0:1));
        return new(final, audits, timing);
    }

    private sealed record StylizedTitleLabel(RectangleF Bounds,Color Color,float FontSize,string Text);

    private static bool ShouldPreserveStylizedTitle(Bitmap source,VisualBlock block,SourceStyleEvidenceR2 evidence)
    {
        if(block.SourceRole?.Role is "Prose" or "Control")return false;
        if(block.Lines.Count is <1 or >3||block.SourceText.Length>80)return false;
        // Large source dialogue can meet the hero-title size thresholds. Its
        // regular sentence rows provide stronger evidence than size alone.
        if(ParagraphAlignmentEvidence.ObserveShortSentence(block) is not null)return false;
        var largestLine=block.Lines.Max(x=>x.Bounds.Height);
        var primaryHeroTitle=block.Bounds.Top<=source.Height*.32f&&
               block.Bounds.Width>=source.Width*.18f&&
               block.Bounds.Height>=source.Height*.095f&&
               largestLine>=Math.Max(44,source.Height*.07f);
        // A large, low-confidence OCR line inside embedded artwork is not safe to erase:
        // its tight box can contain a sign/card surface whose dominant color is mistaken for
        // glyph paint. Preserve the source artwork and render the translation as a label.
        var embeddedArtworkTitle=evidence.BackgroundConfidence<.72f&&
            block.Bounds.Width>=source.Width*.14f&&block.Bounds.Height>=source.Height*.075f&&
            largestLine>=Math.Max(64,source.Height*.075f);
        if(!primaryHeroTitle&&!embeddedArtworkTitle)return false;
        // A broad or uppercase run is still ordinary semantic text. Preserve
        // the established mixed title/subtitle artwork only when the source
        // also proves distinct display hierarchy on a light card header.
        if(block.Lines.Count<2)return false;
        var rows=block.Lines.OrderBy(l=>l.Bounds.Top).ToArray();
        if(rows[0].Bounds.Height<rows[^1].Bounds.Height*1.45f)return false;
        // The large title may overlap the scene above the card. Sampling its
        // entire union confuses that scene with the subtitle's paper surface.
        // Verify the local neutral band immediately below the smaller subtitle.
        var subtitle=rows[^1].Bounds;
        var band=Rectangle.Intersect(new(Point.Empty,source.Size),Rectangle.FromLTRB(
            (int)Math.Floor(subtitle.Left),(int)Math.Ceiling(subtitle.Bottom)+2,
            (int)Math.Ceiling(subtitle.Right),(int)Math.Ceiling(subtitle.Bottom)+8));
        if(band.Width<16||band.Height<4)return false;
        var paper=0;var samples=0;
        for(var y=band.Top;y<band.Bottom;y+=2)
        for(var x=band.Left;x<band.Right;x+=Math.Max(1,band.Width/64))
        {
            var c=source.GetPixel(x,y);samples++;
            var low=Math.Min(c.R,Math.Min(c.G,c.B));var high=Math.Max(c.R,Math.Max(c.G,c.B));
            if(low>225&&high-low<22)paper++;
        }
        return samples>=24&&paper/(float)samples>=.9f;
    }

    private static StylizedTitleLabel PlanStylizedTitleTranslation(Graphics g,Bitmap source,VisualBlock block,
        IReadOnlyList<VisualBlock> blocks,string translation,string fontFamily)
    {
        var label=string.Join(" · ",translation.Replace("\r","").Split('\n',StringSplitOptions.RemoveEmptyEntries));
        var subtitle=block.Lines.OrderBy(x=>x.Bounds.Top).Last().Bounds;
        var color=SampleStylizedTitleAccent(source,subtitle);
        var protectedText=blocks.Where(x=>x.BlockId!=block.BlockId).Select(x=>RectangleF.Inflate(x.Bounds,3,3)).ToArray();
        // Keep the translation beside the semantic subtitle. A union over tall
        // art lettering is not the subtitle's horizontal anchor.
        var starts=new[]{subtitle.Right+Math.Max(12,source.Width*.008f),
            block.Bounds.Right+Math.Max(12,source.Width*.008f)}.Distinct().ToArray();
        var preferred=Math.Clamp(Math.Max(24,Math.Min(block.Bounds.Height*.42f,source.Height*.065f)),18,40);
        foreach(var left in starts)
        for(var size=preferred;size>=18;size-=1)
        {
            using var font=FontManager.CreatePixel(fontFamily,size,FontStyle.Bold);
            var measured=g.MeasureString(label,font,PointF.Empty,StringFormat.GenericTypographic);
            using var path=new System.Drawing.Drawing2D.GraphicsPath();
            path.AddString(label,font.FontFamily,(int)font.Style,font.Size,PointF.Empty,StringFormat.GenericTypographic);
            var ink=path.GetBounds();
            var y=Math.Max(8,subtitle.Bottom-measured.Height);
            var actual=new RectangleF(left+ink.Left,y+ink.Top,ink.Width,ink.Height);
            var safe=RectangleF.Inflate(actual,2,2);
            if(safe.Left<0 || safe.Top<0 || safe.Right>=source.Width || safe.Bottom>=source.Height ||
                protectedText.Any(x=>x.IntersectsWith(safe)))continue;
            bool paper=true;
            for(var py=(int)Math.Floor(safe.Top);py<(int)Math.Ceiling(safe.Bottom)&&paper;py++)
            for(var px=(int)Math.Floor(safe.Left);px<(int)Math.Ceiling(safe.Right);px++)
            {
                var c=source.GetPixel(px,py);
                if(Math.Min(c.R,Math.Min(c.G,c.B))<225 ||
                    Math.Max(c.R,Math.Max(c.G,c.B))-Math.Min(c.R,Math.Min(c.G,c.B))>22)
                {paper=false;break;}
            }
            if(!paper)continue;
            var bounds=new RectangleF(left,y,measured.Width,MathF.Ceiling(measured.Height));
            return new(bounds,color,size,label);
        }
        return new(RectangleF.Empty,color,0,label);
    }

    private static Color SampleStylizedTitleAccent(Bitmap source,RectangleF lineBounds)
    {
        var rect=Clamp(Rectangle.Inflate(Rectangle.Round(lineBounds),3,3),source.Size);
        if(rect.Width<2||rect.Height<2)return Color.FromArgb(105,76,122);
        var pixels=new List<Color>(rect.Width*rect.Height);
        for(var y=rect.Top;y<rect.Bottom;y++)for(var x=rect.Left;x<rect.Right;x++)pixels.Add(source.GetPixel(x,y));
        var background=RobustColor(pixels);
        static int ColorDelta(Color a,Color b)=>Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B);
        var candidates=pixels.Where(c=>ColorDelta(c,background)>=65&&c.GetBrightness()>.12f&&c.GetBrightness()<.92f).ToArray();
        if(candidates.Length==0)return background.GetBrightness()<.5f?Color.White:Color.FromArgb(90,65,120);
        var best=candidates.GroupBy(c=>(c.R/20,c.G/20,c.B/20))
            .OrderByDescending(group=>group.Count()*(1+group.Average(c=>c.GetSaturation())))
            .First().ToArray();
        return RobustColor(best);
    }

    private static int CountMaskPixels(bool[,] mask,Rectangle bounds)
    {
        var safe=Rectangle.Intersect(new Rectangle(0,0,mask.GetLength(0),mask.GetLength(1)),bounds);
        var count=0;
        for(var y=safe.Top;y<safe.Bottom;y++)
        for(var x=safe.Left;x<safe.Right;x++)
            if(mask[x,y])count++;
        return count;
    }


    private static void SaveContinuationLayoutEvidence(Bitmap source,ContinuationLayoutPlanningResult plan,string output)
    {
        SaveOverlay(source,Path.Combine(output,"09-CONTINUATION-PROPOSALS.png"),
            plan.Owners.Where(x=>!x.ConservativeEnvelope.IsEmpty)
                .Select(x=>(RectanglePolygon(x.ConservativeEnvelope),x.ProposalId,x.Active?Color.Lime:Color.OrangeRed)));
        SaveOverlay(source,Path.Combine(output,"10-LAYOUT-OWNERS.png"),
            plan.Owners.Where(x=>x.Active).SelectMany(x=>
                new[]{(RectanglePolygon(x.AvailableRect),$"OWNER {x.ProposalId}",Color.Gold)}
                    .Concat(x.Lines.Select(line=>(RectanglePolygon(line.Bounds),$"{x.ProposalId} L{line.Index+1}",Color.White)))));
        File.WriteAllText(Path.Combine(output,"CONTINUATION-PROPOSAL-TRACE.json"),JsonSerializer.Serialize(new
        {
            FrozenTask1SourceSha256=ContinuationRelation.FrozenPrototypeSourceSha256,
            plan.Relation.CandidateDecisions,plan.Relation.Proposals
        },JsonOptions));
        File.WriteAllText(Path.Combine(output,"LAYOUT-OWNER-PRODUCT-TRACE.json"),JsonSerializer.Serialize(new
        {
            Mode=ContinuationLayoutPlanner.Mode,CoreBlocksMutated=false,TranslationOwnerChanged=false,
            AiAllocationUsed=false,Owners=plan.Owners.Select(x=>new
            {
                x.ProposalId,x.UnderlyingBlockIds,x.UnderlyingSourceIds,x.Fragments,
                ConservativeEnvelope=Rect(x.ConservativeEnvelope),AvailableRect=Rect(x.AvailableRect),
                x.StyleAnchorBlockId,x.StyleSelectionReason,x.FontFamily,FontStyle=x.FontStyle.ToString(),
                TextColor=$"#{x.TextColor.R:X2}{x.TextColor.G:X2}{x.TextColor.B:X2}",
                x.PreferredFontSize,x.FontSize,x.CombinedText,x.CombinedTextUtf8Hash,
                Lines=x.Lines.Select(line=>new{line.Index,line.Text,line.Start,line.Length,Bounds=Rect(line.Bounds),line.ContainedInAvailable,line.ContainedInEnvelope,line.ProtectedHits}),
                x.LineHeight,x.RequiredWidth,x.RequiredHeight,x.FitAttempts,x.FitStatus,
                x.CharactersStable,x.FragmentOrderStable,x.FragmentHashesStable,x.Clipped,x.Ellipsis,
                x.GeometrySafe,x.BoundaryTextSeparatorRequired,x.Active,x.ActivationReason
            })
        },JsonOptions));
    }

    private sealed record TextFitLayout(bool Fits,string Status,float PreferredFontSize,float FontSize,float ScaleRatio,float TargetLanguageScale,bool CjkCompensation,RectangleF Available,IReadOnlyList<string> Lines,float RequiredWidth,float RequiredHeight,float LineHeight,int Attempts,string RequestedFontFamily,string ResolvedFontFamily,bool FallbackOccurred,string FallbackReason,FontStyle RequestedStyle,FontStyle ResolvedStyle,string Alignment,Color TextColor,float ColorConfidence,bool Outline,Color OutlineColor,float OutlineWidth,bool Shadow,Color ShadowColor,SourceStyleBundle SourceStyle,string StyleAdjustment,double CombinedReadability,bool FillPolarityChanged)
    {
        public string? NormalizedLayoutInput { get; init; }
        public NativeVisualLayoutPlan? NativePlan { get; init; }
        public bool SourcePixelScale { get; init; }
        public bool SourceOriented { get; init; }
    }

    private static TextFitLayout FitCompleteText(Graphics g,Bitmap source,VisualBlock block,string text,
        TranslationImageTypographyProfile profile,RenderPlanR2 r2Plan,SourceStyleRenderModeR2 r2Mode,
        SourceStyleBundle sourceStyle,UiLabelAlignmentEvidence? labelAlignment=null,ReadOnlyBitmapPixelBuffer? sourcePixels=null,
        Color? sourceForeground=null)
    {
        // PrimaryFamily is already the result of Settings -> FontManager -> effective profile.
        // Re-resolving CJK here used to silently replace a valid user-selected family.
        var resolved=profile.PrimaryFamily;
        var fallback=!string.IsNullOrWhiteSpace(profile.RequestedFamily)&&!FontManager.IsInstalled(profile.RequestedFamily);
        var fallbackReason=fallback?"REQUESTED_FONT_MISSING":"NONE";
        // Core V2 owns block identity, but the translation-image typography still comes from
        // the product typography pipeline.  The first product integration accidentally threw
        // away both the effective size setting and the established visual weight policy.
        // Keep the role hint advisory: it affects presentation only, never block allocation.
        var requestedStyle=r2Mode==SourceStyleRenderModeR2.ObserverOnly
            ?block.RoleHint is "PossibleTitle" or "PossibleControl"?FontStyle.Bold:FontStyle.Regular
            :sourceStyle.Weight;
        var resolvedStyle=requestedStyle;
        // Integrated source-style mode never consumes the legacy standalone color sample.
        // Avoid a second full owner-pixel scan on the normal product path; ObserverOnly
        // keeps the historical behavior byte-for-byte for controlled comparisons.
        var legacyColor=r2Mode==SourceStyleRenderModeR2.ObserverOnly
            ?SampleTextColor(source,block)
            :(Color:Color.White,Confidence:0f);
        var textColor=r2Mode==SourceStyleRenderModeR2.ObserverOnly?legacyColor.Color:sourceStyle.FillColor;
        var colorConfidence=r2Mode==SourceStyleRenderModeR2.ObserverOnly?legacyColor.Confidence:sourceStyle.Confidence;
        // OCR ownership is glyph-tight, not a control/container box. Preserve the source anchor;
        // centering inside a glyph-tight box moves left-aligned titles without evidence.
        var alignment=r2Mode==SourceStyleRenderModeR2.ObserverOnly?"Left":r2Plan.Alignment;
        if(r2Mode!=SourceStyleRenderModeR2.ObserverOnly && ParagraphAlignmentEvidence.Observe(block,alignment) is {} paragraphAlignment)
            alignment=paragraphAlignment.Alignment;
        var horizontalCompactGroup=IsHorizontalCompactGroup(block);
        var layoutText=horizontalCompactGroup
            ?Regex.Replace(text.Replace('\r',' ').Replace('\n',' '),@"\s+"," ").Trim()
            :text;
        var cjk=ContainsCjk(layoutText);var compactControl=block.RoleHint=="PossibleControl"||horizontalCompactGroup||
            block.LayoutBehavior==BlockLayoutBehavior.Fixed&&text.Trim().Length<=12;
        var available=RectangleF.Inflate(block.Bounds,-2,-1);
        if(compactControl)
        {
            var xPad=Math.Clamp(block.Bounds.Height*.32f,4,14);var yPad=Math.Clamp(block.Bounds.Height*.22f,2,8);
            available=RectangleF.Inflate(block.Bounds,xPad,yPad);
            if(block.Bounds.Width<block.Bounds.Height*.8f)
            {
                var targetWidth=Math.Min(source.Width*.09f,Math.Max(available.Width,block.Bounds.Height*1.9f));
                available=new(block.Bounds.X-(targetWidth-block.Bounds.Width)/2f,available.Y,targetWidth,available.Height);
            }
            available=RectangleF.Intersect(available,new RectangleF(PointF.Empty,source.Size));
            // A complete sentence in a small caption retains its source-left
            // inset. Short titles/buttons keep their existing control geometry;
            // column labels have their separate source-alignment proof below.
            if(r2Mode!=SourceStyleRenderModeR2.ObserverOnly && alignment=="Left" &&
                block.Lines.Count==1 && block.SourceText.Length>24 &&
                Regex.IsMatch(block.SourceText.TrimEnd(),@"[.!?。！？]$"))
                available=RectangleF.FromLTRB(Math.Max(block.Bounds.Left,available.Left),
                    available.Top,available.Right,available.Bottom);
        }
        var median=r2Mode==SourceStyleRenderModeR2.ObserverOnly
            ?block.Lines.Select(x=>x.Bounds.Height).DefaultIfEmpty(16).Median():r2Plan.SourceLineHeight;
        if(labelAlignment?.Alignment=="Left" && r2Mode!=SourceStyleRenderModeR2.ObserverOnly)
            available=RectangleF.FromLTRB(labelAlignment.SourceLeft,available.Top,available.Right,available.Bottom);
        var (effectiveScale,roleScale)=ResolveTypographyScale(profile,block.RoleHint);
        var targetLanguageScale=ResolveTargetLanguageScale(layoutText,resolved,resolvedStyle);
        var sourcePixelScale=block.SourceRole?.Role=="Heading"&&block.Lines.Count==1&&median>=64;
        if(sourcePixelScale&&sourcePixels is not null&&r2Mode!=SourceStyleRenderModeR2.ObserverOnly&&
            SourceGlyphTypography.Measure(sourcePixels,block.Lines[0],sourceForeground??sourceStyle.FillColor) is {BaselineHeight:>0} glyph)
        {
            median=glyph.BaselineHeight;
            // This is measured source ink, no longer the padded OCR row.
            // Applying the old row-to-ink reduction again shrank titles twice.
            roleScale=1;
        }
        var preferred=Math.Clamp(median*roleScale*effectiveScale*targetLanguageScale,9,sourcePixelScale?512:48);
        var floor=Math.Min(preferred,cjk?8.5f:7f);var attempts=0;
        if(sourcePixelScale)floor=Math.Max(16,preferred*.6f);
        for(var size=preferred;size>=floor;size-=sourcePixelScale?Math.Max(1,preferred*.08f):.5f)
        {
            attempts++;using var font=FontManager.CreatePixel(resolved,size,resolvedStyle,sourcePixelScale);resolved=font.Name;var lines=WrapComplete(g,layoutText,font,Math.Max(4,available.Width));var lh=font.GetHeight(g)*(cjk?1.08f:1.02f);
            var width=lines.Select(x=>g.MeasureString(x,font,PointF.Empty,StringFormat.GenericTypographic).Width).DefaultIfEmpty().Max();var height=lines.Count*lh;
            if(width<=available.Width+.5f&&height<=available.Height+.5f)
            {
                var status=lines.Count>Math.Max(1,layoutText.Count(c=>c=='\n')+1)?"FIT_WRAPPED":size<preferred-.1f?"FIT_SCALED":"FIT_OK";
                return new(true,status,preferred,size,size/preferred,targetLanguageScale,cjk,available,lines,width,height,lh,attempts,profile.RequestedFamily,resolved,fallback,fallbackReason,requestedStyle,resolvedStyle,alignment,textColor,colorConfidence,
                    r2Mode!=SourceStyleRenderModeR2.ObserverOnly&&sourceStyle.HasOutline,sourceStyle.OutlineColor,sourceStyle.OutlineWidth,
                    r2Mode!=SourceStyleRenderModeR2.ObserverOnly&&sourceStyle.HasShadow,sourceStyle.ShadowColor,sourceStyle,
                    "PENDING_CLEAN_CANVAS_GUARD",SourceStyleLegibilityGuard.Combined(sourceStyle,
                        SourceStyleBundleOwner.EstimateSourceBackground(source,block.Lines.Select(x=>x.Polygon).ToArray(),block.Bounds)),false){NormalizedLayoutInput=layoutText,SourcePixelScale=sourcePixelScale};
            }
        }
        return new(false,"FIT_FAILED_CONTENT_PRESERVED",preferred,floor,floor/preferred,targetLanguageScale,cjk,available,[],0,0,0,attempts,profile.RequestedFamily,resolved,fallback,fallbackReason,requestedStyle,resolvedStyle,alignment,textColor,colorConfidence,
            r2Mode!=SourceStyleRenderModeR2.ObserverOnly&&sourceStyle.HasOutline,sourceStyle.OutlineColor,sourceStyle.OutlineWidth,
            r2Mode!=SourceStyleRenderModeR2.ObserverOnly&&sourceStyle.HasShadow,sourceStyle.ShadowColor,sourceStyle,
            "FIT_FAILED_NO_DRAW",SourceStyleLegibilityGuard.Combined(sourceStyle,
                SourceStyleBundleOwner.EstimateSourceBackground(source,block.Lines.Select(x=>x.Polygon).ToArray(),block.Bounds)),false){NormalizedLayoutInput=layoutText,SourcePixelScale=sourcePixelScale};
    }

    internal static (float EffectiveScale,float RoleScale) ResolveTypographyScale(TranslationImageTypographyProfile profile,string roleHint)
    {
        var effectiveScale=Math.Clamp(profile.Size/10f,.5f,2f);
        // Keep the Core V2 fit-proven source-height ratio.  The historical renderer's role
        // multipliers operate on larger semantic containers and cannot be copied to the
        // glyph-tight Core V2 owner bounds without visibly oversizing Chests/MHA.
        var roleScale=.72f;
        return(effectiveScale,roleScale);
    }

    internal static bool IsHorizontalCompactGroup(VisualBlock block)
    {
        if(block.LayoutBehavior!=BlockLayoutBehavior.Fixed || block.Lines.Count<2 ||
            block.Bounds.Width<block.Bounds.Height*4f || PreserveIndependentFieldBreaks(block) ||
            block.Lines.Any(line=>line.SourceText.Trim().Length>40))return false;
        // Width alone cannot distinguish a horizontal control from two short
        // chat rows. All source rows must actually share the same vertical band.
        var commonTop=block.Lines.Max(l=>l.Bounds.Top);
        var commonBottom=block.Lines.Min(l=>l.Bounds.Bottom);
        return commonBottom-commonTop>=block.Lines.Min(l=>l.Bounds.Height)*.55f;
    }

    internal static float ResolveTargetLanguageScale(string text,string family="Microsoft YaHei",FontStyle weight=FontStyle.Regular)
    {
        // Match actual target glyph ink to the observed source height. This is
        // font measurement, not a global multiplier applied to Chinese strings.
        var cjk=ContainsCjk(text);
        var sample=new string(text.Where(c=>cjk?c is >= '\u3400' and <= '\u9fff':char.IsLetter(c)).Distinct().Take(32).ToArray());
        if(sample.Length==0)return 1;
        using var font=FontManager.CreatePixel(family,64,weight);
        using var path=new GraphicsPath();
        path.AddString(sample,font.FontFamily,(int)font.Style,64,PointF.Empty,StringFormat.GenericTypographic);
        var height=path.GetBounds().Height;
        return height>0?Math.Clamp(64/height,.8f,1.8f):1;
    }
    private static bool ContainsCjk(string text)=>text.Any(c=>c is >= '\u3400' and <= '\u9fff');

    private static (Color Color,float Confidence) SampleTextColor(Bitmap source,VisualBlock block)
    {
        var rect=Clamp(Rectangle.Round(block.Bounds),source.Size);if(rect.Width<2||rect.Height<2)return(Color.White,0);
        // The ownership rectangle is glyph-tight.  Sampling its own border can therefore
        // mistake a white glyph/outline for the control background and select black text.
        // Use a narrow exterior ring, matching cleanup's local-background evidence.
        var scan=Clamp(Rectangle.Inflate(rect,Math.Clamp(rect.Height/5,2,8),Math.Clamp(rect.Height/6,2,6)),source.Size);
        var ring=new List<Color>();
        for(var x=scan.Left;x<scan.Right;x++){ring.Add(source.GetPixel(x,scan.Top));ring.Add(source.GetPixel(x,scan.Bottom-1));}
        for(var y=scan.Top;y<scan.Bottom;y++){ring.Add(source.GetPixel(scan.Left,y));ring.Add(source.GetPixel(scan.Right-1,y));}
        // Prefer the dominant color inside the owner rectangle.  On a thin button row the
        // exterior ring mostly sees the scene behind the button, while the dominant interior
        // color is the actual control surface.  Glyph pixels are sparse and cannot dominate.
        var interior=new List<Color>(rect.Width*rect.Height);
        for(var y=rect.Top;y<rect.Bottom;y++)for(var x=rect.Left;x<rect.Right;x++)interior.Add(source.GetPixel(x,y));
        var background=interior.Count>0?RobustColor(interior):ring.Count>0?RobustColor(ring):SampleBorderColor(source,rect);var candidates=new List<Color>();
        for(var y=rect.Top;y<rect.Bottom;y++)for(var x=rect.Left;x<rect.Right;x++)
        {var c=source.GetPixel(x,y);var d=Math.Abs(c.R-background.R)+Math.Abs(c.G-background.G)+Math.Abs(c.B-background.B);if(d>=55)candidates.Add(c);}
        if(candidates.Count==0)return(background.GetBrightness()<.52f?Color.White:Color.Black,0);
        var occupancy=candidates.Count/(float)Math.Max(1,rect.Width*rect.Height);
        // A dense candidate field is usually a button/panel fill, not glyph cores.
        if(occupancy>.34f)return(background.GetBrightness()<.52f?Color.White:Color.Black,0);
        var color=RobustColor(candidates.OrderByDescending(c=>Math.Abs(c.R-background.R)+Math.Abs(c.G-background.G)+Math.Abs(c.B-background.B)).Take(Math.Max(1,candidates.Count/2)).ToList());
        var confidence=Math.Clamp(occupancy*4f,0,1);
        var contrast=Math.Abs(color.R-background.R)+Math.Abs(color.G-background.G)+Math.Abs(color.B-background.B);
        static double L(Color c){double F(byte v){var x=v/255d;return x<=.03928?x/12.92:Math.Pow((x+.055)/1.055,2.4);}return .2126*F(c.R)+.7152*F(c.G)+.0722*F(c.B);}
        var a=L(color);var b=L(background);var ratio=(Math.Max(a,b)+.05)/(Math.Min(a,b)+.05);
        return contrast>=90&&ratio>=3.0?(color,confidence):(background.GetBrightness()<.52f?Color.White:Color.Black,0);
    }

    private static IReadOnlyList<string> WrapComplete(Graphics g,string text,Font font,float width)
    {
        var result=new List<string>();const string noStart="，。！？：；、,.!?:;)）】》";
        foreach(var paragraph in text.Replace("\r","").Split('\n'))
        {
            if(paragraph.Length==0){result.Add("");continue;}var line="";
            foreach(var c in paragraph)
            {
                var next=line+c;if(line.Length>0&&g.MeasureString(next,font,PointF.Empty,StringFormat.GenericTypographic).Width>width)
                {if(noStart.Contains(c)){line+=c;continue;}result.Add(line);line=c.ToString();}else line=next;
            }
            if(line.Length>0)result.Add(line);
        }
        return result.Count==0?[""]:result;
    }

    private static string NormalizeText(string value)=>new(value.Where(c=>!char.IsWhiteSpace(c)&&c!='\u200B'&&c!='\u00AD').ToArray());
    private static object Trace(VisualBlock b,BlockTranslation t,TextFitLayout? f,string status,string finalDraw="",RectangleF finalBounds=default,bool clipped=false,bool complete=false,RenderPlanR2? r2Plan=null)=>new
    {
        SchemaVersion=2,b.BlockId,StableSourceIds=b.Lines.Select(x=>x.SourceId),b.SourceText,
        Provenance="RENDER_INPUT_SNAPSHOT; this renderer does not observe transport or parser boundaries",
        ProductionRawCaptured=(string?)null,ParserReturned=(string?)null,
        UncapturedReason="HTTP and parser values are only available from separately enabled production-boundary diagnostics",
        AcceptedMapping=t.TranslatedText,AcceptedReplaySnapshot=(string?)null,
        AcceptedReplaySnapshotReason="Renderer cannot infer whether its accepted mapping originated from replay or a live service",
        LayoutInput=f?.NormalizedLayoutInput,
        ProviderRawTranslation=(string?)null,ParsedTranslation=(string?)null,
        AcceptedTranslation=t.TranslatedText,PostProcessedTranslation=(string?)null,
        LegacyAliasMeaning="AcceptedTranslation=AcceptedMapping; LayoutInputText is the historical accepted pre-normalization value; LayoutInput captures the actual normalized wrapping input; other legacy boundary aliases were never independently captured",
        LayoutInputText=t.TranslatedText,LayoutMode=b.LayoutBehavior.ToString(),MeasuredRequiredSize=f is null?null:new{Width=f.RequiredWidth,Height=f.RequiredHeight},
        AvailableRegion=Rect(f?.Available??b.Bounds),RequestedFontFamily=f?.RequestedFontFamily,ResolvedFontFamily=f?.ResolvedFontFamily,FallbackOccurred=f?.FallbackOccurred,FallbackReason=f?.FallbackReason,
        RequestedWeight=f?.RequestedStyle.ToString(),ResolvedWeight=f?.ResolvedStyle.ToString(),PreferredFontSize=f?.PreferredFontSize,ChosenFitFontSize=f?.FontSize,DPI=96,MeasureFont=f?.ResolvedFontFamily,DrawFont=f?.ResolvedFontFamily,
        Alignment=f?.Alignment,LineHeight=f?.LineHeight,TextColor=f is null?null:$"#{f.TextColor.R:X2}{f.TextColor.G:X2}{f.TextColor.B:X2}",SourceTextColorConfidence=f?.ColorConfidence,
        Outline=f?.Outline??false,OutlineColor=f is null?null:$"#{f.OutlineColor.R:X2}{f.OutlineColor.G:X2}{f.OutlineColor.B:X2}",OutlineWidth=f?.OutlineWidth,
        Shadow=f?.Shadow??false,ShadowColor=f is null?null:$"#{f.ShadowColor.R:X2}{f.ShadowColor.G:X2}{f.ShadowColor.B:X2}",
        ShadowOffset=f?.SourceStyle.ShadowOffset,Glow=f?.SourceStyle.Glow,Alpha=f?.SourceStyle.Alpha,
        StyleAdjustment=f?.StyleAdjustment,CombinedReadability=f?.CombinedReadability,FillPolarityChanged=f?.FillPolarityChanged,
        R2StyleOwnerId=r2Plan?.StyleOwnerId,R2ContainerOwnerId=r2Plan?.ContainerOwnerId,R2SurfaceOwnerId=r2Plan?.SurfaceOwnerId,R2FallbackLevel=r2Plan?.FallbackLevel.ToString(),
        ScaleRatio=f?.ScaleRatio,TargetLanguageScale=f?.TargetLanguageScale,CjkCompensation=f?.CjkCompensation??false,WrappedLines=f?.Lines??[],FinalDrawText=finalDraw,
        FinalDrawBounds=Rect(finalBounds),Clipped=clipped,EllipsisInserted=!complete&&(finalDraw.Contains('…')||finalDraw.Contains("..."))&&!t.TranslatedText.Contains('…')&&!t.TranslatedText.Contains("..."),CharactersDropped=complete?0:Math.Max(0,NormalizeText(t.TranslatedText).Length-NormalizeText(finalDraw).Length),FitStatus=status,FitAttempts=f?.Attempts??0
    };

    private static RegionRoleType MapRole(SourceVisualRoleR2 role)=>role switch
    {
        SourceVisualRoleR2.ArtisticTitle=>RegionRoleType.Title,
        SourceVisualRoleR2.Heading=>RegionRoleType.Header,
        SourceVisualRoleR2.Body=>RegionRoleType.BodyParagraph,
        SourceVisualRoleR2.Metadata=>RegionRoleType.Metadata,
        SourceVisualRoleR2.Control=>RegionRoleType.Button,
        SourceVisualRoleR2.Dialogue=>RegionRoleType.Dialogue,
        _=>RegionRoleType.Unknown
    };

    private static SourceStyleBundle BuildSourceStyleBundle(SourceStyleEvidenceR2 evidence,RenderPlanR2 plan)
    {
        var fill=Color.FromArgb(evidence.DirectForegroundArgb);
        var outline=evidence.OutlineDetected?Color.FromArgb(255,Color.FromArgb(evidence.OutlineArgb)):Color.Transparent;
        var shadow=evidence.ShadowDetected?Color.FromArgb(150,Color.FromArgb(evidence.ShadowArgb)):Color.Transparent;
        var height=Math.Max(1,evidence.MedianLineHeight);
        var offset=evidence.ShadowDetected
            ?new PointF(Math.Clamp(height*.045f,1,3),Math.Clamp(height*.055f,1,3))
            :PointF.Empty;
        var weight=evidence.Weight is SourceWeightR2.Bold or SourceWeightR2.Semibold?FontStyle.Bold:FontStyle.Regular;
        return new SourceStyleBundle(Color.FromArgb(255,fill),outline,
            evidence.OutlineDetected?Math.Clamp(evidence.OutlineWidth,.75f,3.5f):0,
            shadow,offset,0,255,weight,SourceStyleBundleOwner.ClassifyPolarity(fill),MapRole(plan.VisualRole),
            evidence.DirectConfidence,$"VisualStyleOwner:{plan.StyleOwnerId}",
            $"EXISTING_OWNER_EVIDENCE;block={evidence.BlockId};styleOwner={plan.StyleOwnerId};"+
            $"fill={evidence.DirectForeground};outline={evidence.OutlineColor};shadow={evidence.ShadowColor};"+
            $"background={evidence.Background};accepted={evidence.AcceptedReason}");
    }

    private static bool[,] BuildProtectedPixelMap(Size size, CorePipelineDocument document)
    {
        var map = new bool[size.Width, size.Height];
        foreach (var visual in document.VisualEvidence.Where(x =>
                     x.Role.Contains("Image", StringComparison.OrdinalIgnoreCase) ||
                     x.Role.Contains("Illustration", StringComparison.OrdinalIgnoreCase) ||
                     x.Role.Contains("Artwork", StringComparison.OrdinalIgnoreCase)))
            Fill(map, Clamp(Rectangle.Round(visual.Bounds), size));
        foreach (var block in document.VisualBlocks.Where(x => x.TextSelection == TextSelectionAction.Preserve))
            foreach (var line in block.Lines) FillPolygon(map, line.Polygon, size);
        return map;
    }

    private sealed record CandidateComponentAudit(string LineSourceId,Rectangle Bounds,int PixelCount,
        int DistanceToPrimaryCore,bool InsideSearchEnvelope,int ColorDistanceFromBackground,
        int ProtectedIntersection,bool Admitted,string Reason);

    private sealed record GlyphMaskResult(bool[,] Core,bool[,] OutlineShadow,bool[,] BaseMaterial,bool[,] HaloCandidate,
        bool[,] AfterDilation,bool[,] AfterComponentMerge,bool[,] Admitted,
        bool[,] RejectedCandidate,bool[,] ProtectedConflict,bool[,] SearchEnvelope,bool[,] SecondaryColorFamily,
        IReadOnlyList<CandidateComponentAudit> ComponentAudits,IReadOnlyList<MorphologyStageAudit> MorphologyAudits,
        IReadOnlyList<TopologyGrowthAudit> GrowthAudits,
        Rectangle SearchBounds,int SourceOutlinePixels,int SourceShadowPixels)
    {
        public IReadOnlyList<MaterialLineStages> MaterialStages { get; init; }=[];
        private static readonly bool[,] EmptyDiagnosticPlane=new bool[0,0];
        internal GlyphMaskResult WithoutDiagnosticPlanes()=>this with
        {
            OutlineShadow=EmptyDiagnosticPlane,BaseMaterial=EmptyDiagnosticPlane,
            HaloCandidate=EmptyDiagnosticPlane,AfterDilation=EmptyDiagnosticPlane,
            AfterComponentMerge=EmptyDiagnosticPlane,RejectedCandidate=EmptyDiagnosticPlane,
            ProtectedConflict=EmptyDiagnosticPlane,SearchEnvelope=EmptyDiagnosticPlane,
            SecondaryColorFamily=EmptyDiagnosticPlane,MaterialStages=[]
        };
    }

    private sealed record SharedGlyphMaterialEvidence(bool Accepted,bool LightOnDark,Color Foreground,Color Outline,
        float WinningScore,float OpposingScore,int SupportingLines,string Reason);

    private sealed record TopologyGrowthResult(bool[,] Mask,bool[,] Halo,int BasePixels,int FinalPixels,
        int BaseComponents,int FinalComponents,int GrowthBudgetPixels,int RejectedBridgePixels,
        int BudgetTruncatedPixels,int MaxDistance,int EstimatedStrokeWidth,int InterGlyphBridgeConfidencePermille);

    private static SharedGlyphMaterialEvidence BuildSharedGlyphMaterialEvidence(SourceStyleEvidenceR2 style)
    {
        static double Luma(Color c)=>(c.R*299+c.G*587+c.B*114)/1000d;
        var candidates=style.LineEvidence
            .Where(x=>x.Confidence>=.50f&&x.BackgroundConfidence>=.60f&&x.ForegroundOccupancy>=.008f)
            .Select(x=>new
            {
                Evidence=x,
                Foreground=Color.FromArgb(x.ForegroundArgb),
                Background=Color.FromArgb(x.BackgroundArgb),
                Score=Math.Max(.01f,x.ForegroundOccupancy)*x.Confidence*Math.Clamp(x.ContrastRatio,1f,3f)
            })
            .Where(x=>Math.Abs(Luma(x.Foreground)-Luma(x.Background))>=18)
            .ToArray();
        if(style.LineEvidence.Count<3||candidates.Length<2)
            return new(false,false,Color.Empty,Color.Empty,0,0,0,"INSUFFICIENT_SHARED_LINE_EVIDENCE");
        var lightScore=(float)candidates.Where(x=>Luma(x.Foreground)>Luma(x.Background)).Sum(x=>x.Score);
        var darkScore=(float)candidates.Where(x=>Luma(x.Foreground)<Luma(x.Background)).Sum(x=>x.Score);
        var lightOnDark=lightScore>=darkScore;
        var winning=Math.Max(lightScore,darkScore);var opposing=Math.Min(lightScore,darkScore);
        var supporting=candidates.Where(x=>(Luma(x.Foreground)>Luma(x.Background))==lightOnDark).ToArray();
        if(supporting.Length<2||winning<.06f||winning<opposing*1.35f)
            return new(false,lightOnDark,Color.Empty,Color.Empty,winning,opposing,supporting.Length,"POLARITY_CONSENSUS_NOT_PROVED");
        var foreground=RobustColor(supporting.Select(x=>x.Foreground).ToList());
        var outlineSamples=supporting
            .Where(x=>x.Evidence.OutlineConfidence>=.25f)
            .Select(x=>Color.FromArgb(x.Evidence.OutlineArgb)).ToList();
        var outline=outlineSamples.Count==0?Color.Empty:RobustColor(outlineSamples);
        return new(true,lightOnDark,foreground,outline,winning,opposing,supporting.Length,
            "MULTILINE_POLARITY_AND_COLOR_CONSENSUS");
    }


    private static GlyphMaskResult BuildGlyphMask(ReadOnlyBitmapPixelBuffer source, VisualBlock block,
        bool[,] protectedPixels,SourceStyleEvidenceR2 styleEvidence,CleanupMaskMode maskMode,bool captureMaterialStages=false,
        IReadOnlyDictionary<string,SourceGlyphRowEvidence>? glyphRows=null,bool retainDiagnosticPlanes=true)
    {
        // Runtime consumes Core/Admitted and computes BaseMaterial below. Other
        // planes are write-only diagnostic products; do not allocate then discard
        // whole-frame buffers for every block in a normal task.
        var core = new bool[source.Width, source.Height];
        var outlineShadow = retainDiagnosticPlanes ? new bool[source.Width, source.Height] : new bool[0,0];
        var baseMaterial = new bool[source.Width, source.Height];
        var halo = retainDiagnosticPlanes ? new bool[source.Width, source.Height] : new bool[0,0];
        var afterDilation = retainDiagnosticPlanes ? new bool[source.Width, source.Height] : new bool[0,0];
        var afterComponentMerge = retainDiagnosticPlanes ? new bool[source.Width, source.Height] : new bool[0,0];
        var admitted = new bool[source.Width, source.Height];
        var rejected = retainDiagnosticPlanes ? new bool[source.Width, source.Height] : new bool[0,0];
        var protectedConflict = retainDiagnosticPlanes ? new bool[source.Width, source.Height] : new bool[0,0];
        var searchEnvelope = retainDiagnosticPlanes ? new bool[source.Width, source.Height] : new bool[0,0];
        var secondaryColorFamily = retainDiagnosticPlanes ? new bool[source.Width, source.Height] : new bool[0,0];
        var componentAudits = new List<CandidateComponentAudit>();
        var morphologyAudits = new List<MorphologyStageAudit>();
        var growthAudits = new List<TopologyGrowthAudit>();
        var sourceOutlinePixels=0;var sourceShadowPixels=0;
        var materialStages=new List<MaterialLineStages>();
        var searchBounds=Rectangle.Empty;
        // Multiline consensus is a rescue path for low-confidence translucent
        // profiles.  High-confidence panels and task cards can contain legitimate
        // secondary text colours; forcing one shared polarity there hides that
        // source material instead of preserving it.
        var sharedMaterial=maskMode==CleanupMaskMode.M3HCoveragePreserving&&
            styleEvidence.BackgroundConfidence<.72f
            ?BuildSharedGlyphMaterialEvidence(styleEvidence):new(false,false,Color.Empty,Color.Empty,0,0,0,"NOT_M3H");
        for(var lineIndex=0;lineIndex<block.Lines.Count;lineIndex++)
        {
            var line=block.Lines[lineIndex];
            var observed=lineIndex<styleEvidence.LineEvidence.Count?styleEvidence.LineEvidence[lineIndex]:null;
            var geometry = Clamp(Rectangle.Round(line.Bounds), source.Size);
            if (geometry.Width < 2 || geometry.Height < 2) continue;
            var scan = Clamp(Rectangle.Inflate(geometry, Math.Clamp(geometry.Height / 5, 2, 8),
                Math.Clamp(geometry.Height / 6, 2, 6)), source.Size);
            var lineBand=IsTopologyPreservingMode(maskMode)
                ?BuildM3LineBand(block.Lines,lineIndex,scan,source.Size):scan;
            var materialGeometry=Rectangle.Intersect(geometry,lineBand);
            var growthScan=Rectangle.Intersect(scan,lineBand);
            if(materialGeometry.Width<2||materialGeometry.Height<2||growthScan.Width<2||growthScan.Height<2)continue;
            searchBounds=searchBounds.IsEmpty?growthScan:Rectangle.Union(searchBounds,growthScan);
            if(retainDiagnosticPlanes)for(var y=growthScan.Top;y<growthScan.Bottom;y++)for(var x=growthScan.Left;x<growthScan.Right;x++)searchEnvelope[x,y]=true;
            if(maskMode==CleanupMaskMode.M3HCoveragePreserving && glyphRows is not null &&
                glyphRows.TryGetValue(line.SourceId,out var rowProof) &&
                rowProof.Pixels.All(p=>growthScan.Contains(p)&&!protectedPixels[p.X,p.Y]))
            {
                // This independently observed repeated glyph family excludes
                // different-color arrows and borders inside an oversized OCR box.
                // Only its stroke and antialias pixels receive cleanup authority.
                foreach(var p in rowProof.Pixels)
                    {
                    core[p.X,p.Y]=baseMaterial[p.X,p.Y]=admitted[p.X,p.Y]=true;
                    if(retainDiagnosticPlanes)afterDilation[p.X,p.Y]=afterComponentMerge[p.X,p.Y]=true;
                }
                continue;
            }
            var ring = new List<Color>();
            for (var x = scan.Left; x < scan.Right; x++) { ring.Add(source.GetPixel(x, scan.Top)); ring.Add(source.GetPixel(x, scan.Bottom - 1)); }
            for (var y = scan.Top; y < scan.Bottom; y++) { ring.Add(source.GetPixel(scan.Left, y)); ring.Add(source.GetPixel(scan.Right - 1, y)); }
            if (ring.Count == 0) continue;
            var background = observed is { BackgroundConfidence: >= .50f }
                ?Color.FromArgb(observed.BackgroundArgb):RobustColor(ring);
            int Delta(Color a, Color b) => Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B);
            var candidates = new List<Color>();
            for (var y=materialGeometry.Top;y<materialGeometry.Bottom;y++) for(var x=materialGeometry.Left;x<materialGeometry.Right;x++)
            {
                var candidate=Delta(source.GetPixel(x,y),background)>=18;
                if(retainDiagnosticPlanes&&candidate&&protectedPixels[x,y])protectedConflict[x,y]=true;
                if (!protectedPixels[x,y] && Delta(source.GetPixel(x,y),background) >= 38) candidates.Add(source.GetPixel(x,y));
            }
            if (candidates.Count == 0) continue;
            var foreground = candidates.GroupBy(c => (c.R/20,c.G/20,c.B/20))
                .OrderByDescending(g => g.Average(c => Delta(c,background)) * Math.Sqrt(g.Count())).First().ToArray();
            var fg = RobustColor(foreground);
            var seed = new bool[source.Width,source.Height];
            var observedForeground=sharedMaterial.Accepted?sharedMaterial.Foreground:
                observed is null?fg:Color.FromArgb(observed.ForegroundArgb);
            // When a multi-line shared glyph family has been proved, a single OCR
            // line is not allowed to redefine the background family.  Wide bold
            // lines can contain more glyph material than exposed background; the
            // line observer then mistakes the glyph fill for background and drops
            // the complete line (for example a relationship value).  The block
            // observer is based on all lines and is the verified shared reference.
            var observedBackground=sharedMaterial.Accepted
                ?Color.FromArgb(styleEvidence.BackgroundArgb)
                :observed is null?background:Color.FromArgb(observed.BackgroundArgb);
            var observedOutline=sharedMaterial.Accepted&& !sharedMaterial.Outline.IsEmpty?sharedMaterial.Outline:
                observed is null?Color.Transparent:Color.FromArgb(observed.OutlineArgb);
            var observedShadow=observed is null?Color.Transparent:Color.FromArgb(observed.ShadowArgb);
            var observedFamilySeparation=Delta(observedForeground,observedBackground);
            var edgeMap=new byte[scan.Width,scan.Height];
            if(maskMode!=CleanupMaskMode.LegacyC13)
                for(var y=scan.Top;y<scan.Bottom;y++)for(var x=scan.Left;x<scan.Right;x++)
                    edgeMap[x-scan.Left,y-scan.Top]=(byte)Math.Min(255,LocalEdgeStrength(source,x,y));
            int Edge(int x,int y)=>edgeMap[x-scan.Left,y-scan.Top];
            for (var y=materialGeometry.Top;y<materialGeometry.Bottom;y++) for(var x=materialGeometry.Left;x<materialGeometry.Right;x++)
            {
                if (protectedPixels[x,y]) continue;
                var c=source.GetPixel(x,y); var db=Delta(c,background); var df=Delta(c,fg);
                if(maskMode==CleanupMaskMode.LegacyC13)
                {
                    var observedPrimary=observed is { Confidence: >= .65f,ForegroundOccupancy: >= .04f }&&
                        Delta(c,Color.FromArgb(observed.ForegroundArgb))<=52&&
                        Delta(c,Color.FromArgb(observed.BackgroundArgb))>=16;
                    if(observedPrimary||(db>=26&&df<=Math.Min(190,db*1.65+22)))seed[x,y]=true;
                }
                else
                {
                    var edge=Edge(x,y);
                    var observedPrimary=observedFamilySeparation>=42&&
                        (sharedMaterial.Accepted||observed is { Confidence: >= .52f,ForegroundOccupancy: >= .025f })&&
                        Delta(c,observedForeground)<=(sharedMaterial.Accepted?48:78)&&
                        (Delta(c,observedBackground)>=10||edge>=18);
                    // The computed cluster is only a fallback when the observer did not
                    // prove a usable foreground family.  Running both paths on a
                    // high-confidence translucent dialogue lets ordinary panel/scene
                    // edges become a second "foreground" and produces wide mask bands.
                    var computedPrimary=!sharedMaterial.Accepted&&
                        (observed is null||observed.Confidence<.52f||observed.ForegroundOccupancy<.025f)&&
                        db>=22&&df<=Math.Min(190,db*1.65+22)&&edge>=7;
                    // For a verified shared material family, outline/shadow is admitted only
                    // next to an already observed fill stroke.  Treating a dark outline color
                    // as an independent seed on a flattened scene admits shelves and panel
                    // texture as if they were glyphs.
                    // Outline and shadow are never independent seeds.  Dark translucent
                    // panels are numerically close to dark glyph outlines; seeding that
                    // colour globally turns the panel texture into a cleanup mask.  The
                    // verified near-fill pass below still restores real outline material.
                    if(observedPrimary||computedPrimary)seed[x,y]=true;
                }
            }
            // A control/panel fill can be the strongest color family in a tight OCR box.
            // It is not a glyph: admitting it produces long restoration bars.
            var primaryBefore=captureMaterialStages?CopyMaskRegion(seed,materialGeometry):null;
            RejectContainerFillComponents(seed,materialGeometry);
            var primaryAfter=captureMaterialStages?CopyMaskRegion(seed,materialGeometry):null;
            if(maskMode!=CleanupMaskMode.LegacyC13)
            {
                // Re-admit only material directly supported by the line's observed source style.
                // This closes anti-aliased and outlined glyph interiors without authorizing a
                // rectangle, panel, neighboring line, or cross-block restoration surface.
                for(var y=materialGeometry.Top;y<materialGeometry.Bottom;y++)for(var x=materialGeometry.Left;x<materialGeometry.Right;x++)
                {
                    if(protectedPixels[x,y])continue;
                    var c=source.GetPixel(x,y);var edge=Edge(x,y);
                    var directForeground=observedFamilySeparation>=42&&
                        (sharedMaterial.Accepted||observed is { Confidence: >= .52f,ForegroundOccupancy: >= .025f })&&
                        Delta(c,observedForeground)<=(sharedMaterial.Accepted?48:78)&&Delta(c,observedBackground)>=10&&
                        (!sharedMaterial.Accepted||edge>=5);
                    // Do not globally re-admit outline/shadow colours after the container
                    // rejection pass.  They receive authority only when adjacent to a
                    // proven fill seed in the next loop.
                    if(directForeground)seed[x,y]=true;
                }
            }
            // Only the current coverage-preserving route receives this recovery.
            // Validate primary material before additions can join it to a rejected panel.
            var recoverablePrimary=maskMode==CleanupMaskMode.M3HCoveragePreserving
                ?ValidatePrimarySeedForSecondaryRecovery(seed,materialGeometry):null;
            var beforeSecondary=captureMaterialStages?CopyMaskRegion(seed,materialGeometry):null;
            if(styleEvidence.BackgroundConfidence>=.72f&&!sharedMaterial.Accepted)
                AdmitSecondaryTextColorFamilies(source,line.SourceId,materialGeometry,background,protectedPixels,seed,secondaryColorFamily,componentAudits);
            var afterSecondary=captureMaterialStages?CopyMaskRegion(seed,materialGeometry):null;
            RejectLongHorizontalStructure(seed,materialGeometry);
            // Reject secondary container fill, then recover only primary material
            // that independently passes both existing structural rules.
            if(recoverablePrimary is not null)
                RejectContainerFillAfterSecondary(seed,materialGeometry,recoverablePrimary);
            else RejectContainerFillComponents(seed,materialGeometry);
            if(captureMaterialStages)materialStages.Add(new(line.SourceId,materialGeometry,primaryBefore!,primaryAfter!,
                beforeSecondary!,afterSecondary!,CopyMaskRegion(seed,materialGeometry)));
            for(var y=materialGeometry.Top;y<materialGeometry.Bottom;y++)for(var x=materialGeometry.Left;x<materialGeometry.Right;x++)if(seed[x,y])core[x,y]=admitted[x,y]=true;
            var observedOutlineRadius=observed is { OutlineConfidence: >= .32f }
                ?(int)MathF.Ceiling(observed.OutlineWidth)+1:0;
            var radius=styleEvidence.BackgroundConfidence<.72f
                ?Math.Clamp(Math.Max((int)MathF.Round(geometry.Height*.08f),observedOutlineRadius),1,5)
                :Math.Clamp(Math.Max((int)MathF.Round(geometry.Height*.16f),observedOutlineRadius),1,6);
            var lineMask=new bool[source.Width,source.Height];
            for(var y=materialGeometry.Top;y<materialGeometry.Bottom;y++)for(var x=materialGeometry.Left;x<materialGeometry.Right;x++)
                if(seed[x,y])lineMask[x,y]=true;

            var supportRadius=Math.Clamp(Math.Max(1,observedOutlineRadius),1,3);
            var outlineColor=observed is { OutlineConfidence: >= .32f }?Color.FromArgb(observed.OutlineArgb):Color.Empty;
            var shadowColor=observed is { ShadowConfidence: >= .38f }?Color.FromArgb(observed.ShadowArgb):Color.Empty;
            for(var y=growthScan.Top;y<growthScan.Bottom;y++)for(var x=growthScan.Left;x<growthScan.Right;x++)
            {
                if(protectedPixels[x,y]||seed[x,y]||!NearMask(seed,materialGeometry,x,y,supportRadius))continue;
                var c=source.GetPixel(x,y);var db=Delta(c,background);
                var isOutline=!outlineColor.IsEmpty&&db>=12&&Delta(c,outlineColor)<=Math.Min(72,db*2+12);
                var isShadow=!shadowColor.IsEmpty&&db>=12&&Delta(c,shadowColor)<=Math.Min(78,db*2+16);
                if(!isOutline&&!isShadow)continue;
                if(retainDiagnosticPlanes)outlineShadow[x,y]=true;
                if(isOutline)sourceOutlinePixels++;
                if(isShadow)sourceShadowPixels++;
                if(maskMode!=CleanupMaskMode.LegacyC13)lineMask[x,y]=true;
            }

            for(var y=growthScan.Top;y<growthScan.Bottom;y++)for(var x=growthScan.Left;x<growthScan.Right;x++)
                if(lineMask[x,y])baseMaterial[x,y]=true;

            // M3H separates confirmed source material from optional cleanup halo.  M3's
            // topology guard was correct for new growth, but it also made the material
            // seed too conservative.  Recover the same observed AA/outline/shadow family
            // used by M1 inside the line's disjoint baseline band, then freeze it as an
            // immutable base.  The topology guard below may reject only later halo pixels.
            if(maskMode==CleanupMaskMode.M3HCoveragePreserving)
            {
                var materialRadius=Math.Clamp(Math.Max((int)MathF.Round(geometry.Height*.045f),
                    observedOutlineRadius+(sharedMaterial.Accepted?1:0)),1,5);
                for(var y=growthScan.Top;y<growthScan.Bottom;y++)for(var x=growthScan.Left;x<growthScan.Right;x++)
                {
                    if(protectedPixels[x,y]||lineMask[x,y]||!NearMask(seed,materialGeometry,x,y,materialRadius))continue;
                    var c=source.GetPixel(x,y);var db=Delta(c,background);var edge=Edge(x,y);
                    var antiAlias=NearMask(seed,materialGeometry,x,y,1)&&(db>=7||edge>=11);
                    var sourceOutline=observed is { OutlineConfidence: >= .25f }&&
                        Delta(c,observedOutline)<=(sharedMaterial.Accepted?112:68)&&
                        Delta(c,observedBackground)>=(sharedMaterial.Accepted?6:8);
                    var sourceShadow=observed is { ShadowConfidence: >= .25f }&&
                        Delta(c,observedShadow)<=64&&Delta(c,observedBackground)>=8;
                    if(!antiAlias&&!sourceOutline&&!sourceShadow)continue;
                    lineMask[x,y]=true;
                    if(sourceOutline||sourceShadow)
                    {
                        if(retainDiagnosticPlanes)outlineShadow[x,y]=true;
                        if(sourceOutline)sourceOutlinePixels++;
                        if(sourceShadow)sourceShadowPixels++;
                    }
                }
                // A style-family pass can mistake a long panel or scene rule for
                // outline material. Re-check before BaseMaterial is frozen.
                RejectLongHorizontalStructure(lineMask,materialGeometry);
                // BaseMaterial is the immutable, post-validation source-material
                // seed.  Do not leave pixels here that the structural rejection
                // removed from lineMask: doing so makes the reported base larger
                // than the actual topology-growth input and violates Final >= Base.
                for(var y=growthScan.Top;y<growthScan.Bottom;y++)for(var x=growthScan.Left;x<growthScan.Right;x++)
                    baseMaterial[x,y]=lineMask[x,y];
            }

            var inputPixels=CountMaskPixels(lineMask,growthScan);
            var inputComponents=CountMaskComponents(lineMask,growthScan);

            if(maskMode==CleanupMaskMode.LegacyC13)
            {
                for(var y=geometry.Top;y<geometry.Bottom;y++)for(var x=geometry.Left;x<geometry.Right;x++)if(seed[x,y])
                for(var yy=Math.Max(scan.Top,y-radius);yy<=Math.Min(scan.Bottom-1,y+radius);yy++)
                for(var xx=Math.Max(scan.Left,x-radius);xx<=Math.Min(scan.Right-1,x+radius);xx++)
                    if(!protectedPixels[xx,yy]&&(xx-x)*(xx-x)+(yy-y)*(yy-y)<=radius*radius)
                        lineMask[xx,yy]=true;
            }
            else if(maskMode==CleanupMaskMode.M1EvidenceBounded)
            {
                var evidenceRadius=Math.Clamp(Math.Max((int)MathF.Round(geometry.Height*.045f),observedOutlineRadius),1,4);
                for(var y=scan.Top;y<scan.Bottom;y++)for(var x=scan.Left;x<scan.Right;x++)
                {
                    if(protectedPixels[x,y]||lineMask[x,y]||!NearMask(seed,geometry,x,y,evidenceRadius))continue;
                    var c=source.GetPixel(x,y);var db=Delta(c,background);var edge=Edge(x,y);
                    var antiAlias=NearMask(seed,geometry,x,y,1)&&(db>=7||edge>=11);
                    var sourceOutline=observed is { OutlineConfidence: >= .25f }&&
                        Delta(c,observedOutline)<=68&&Delta(c,observedBackground)>=8;
                    var sourceShadow=observed is { ShadowConfidence: >= .25f }&&
                        Delta(c,observedShadow)<=64&&Delta(c,observedBackground)>=8;
                    if(!antiAlias&&!sourceOutline&&!sourceShadow)continue;
                    lineMask[x,y]=true;
                    if(sourceOutline||sourceShadow)
                    {
                        if(retainDiagnosticPlanes)outlineShadow[x,y]=true;
                        if(sourceOutline)sourceOutlinePixels++;
                        if(sourceShadow)sourceShadowPixels++;
                    }
                }
            }
            else
            {
                var estimatedStrokeWidth=EstimateMaskStrokeWidth(lineMask,growthScan);
                var maxDistance=Math.Clamp(Math.Max(
                    (int)MathF.Ceiling(observed?.OutlineWidth??0f),
                    Math.Max(1,(int)MathF.Round(estimatedStrokeWidth*.5f))),1,3);
                var evidence=new bool[source.Width,source.Height];
                var evidenceScore=new int[growthScan.Width,growthScan.Height];
                for(var y=growthScan.Top;y<growthScan.Bottom;y++)for(var x=growthScan.Left;x<growthScan.Right;x++)
                {
                    if(protectedPixels[x,y]||lineMask[x,y]||!NearMask(lineMask,growthScan,x,y,maxDistance))continue;
                    var c=source.GetPixel(x,y);var db=Delta(c,background);var edge=Edge(x,y);
                    var antiAlias=NearMask(lineMask,growthScan,x,y,1)&&db>=7&&edge>=11;
                    var sourceOutline=observed is { OutlineConfidence: >= .25f }&&edge>=4&&
                        Delta(c,observedOutline)<=68&&Delta(c,observedBackground)>=8;
                    var sourceShadow=observed is { ShadowConfidence: >= .25f }&&edge>=4&&
                        Delta(c,observedShadow)<=64&&Delta(c,observedBackground)>=8;
                    if(!antiAlias&&!sourceOutline&&!sourceShadow)continue;
                    evidence[x,y]=true;
                    evidenceScore[x-growthScan.Left,y-growthScan.Top]=Math.Min(1000,db*3+edge*2+(sourceOutline?120:0)+(sourceShadow?90:0));
                    if(sourceOutline||sourceShadow)
                    {
                        if(retainDiagnosticPlanes)outlineShadow[x,y]=true;
                        if(sourceOutline)sourceOutlinePixels++;
                        if(sourceShadow)sourceShadowPixels++;
                    }
                }
                var grown=GrowTopologyPreserving(lineMask,evidence,evidenceScore,growthScan,maxDistance,estimatedStrokeWidth);
                lineMask=grown.Mask;
                growthAudits.Add(new(line.SourceId,geometry.Height,grown.EstimatedStrokeWidth,grown.MaxDistance,
                    grown.BasePixels,grown.FinalPixels,grown.GrowthBudgetPixels,
                    grown.FinalPixels/(float)Math.Max(1,grown.BasePixels),grown.BaseComponents,grown.FinalComponents,
                    grown.RejectedBridgePixels,grown.BudgetTruncatedPixels,grown.InterGlyphBridgeConfidencePermille,
                    block.Lines.Count>1));
            }
            var outputScan=IsTopologyPreservingMode(maskMode)?growthScan:scan;
            for(var y=outputScan.Top;y<outputScan.Bottom;y++)for(var x=outputScan.Left;x<outputScan.Right;x++)if(lineMask[x,y])
            {
                admitted[x,y]=true;
                if(retainDiagnosticPlanes)afterDilation[x,y]=afterComponentMerge[x,y]=true;
                if(retainDiagnosticPlanes&&(maskMode==CleanupMaskMode.M3HCoveragePreserving?!baseMaterial[x,y]:!core[x,y]))halo[x,y]=true;
            }
            var outputPixels=CountMaskPixels(lineMask,outputScan);
            var outputComponents=CountMaskComponents(lineMask,outputScan);
            morphologyAudits.Add(new(line.SourceId,
                maskMode==CleanupMaskMode.LegacyC13?"UNCONDITIONAL_EUCLIDEAN_DILATION":
                    maskMode==CleanupMaskMode.M1EvidenceBounded?"EVIDENCE_BOUNDED_SOURCE_MATERIAL_HALO":
                    maskMode==CleanupMaskMode.M3HCoveragePreserving?"M3H_BASE_MATERIAL_PLUS_GEODESIC_HALO_GUARD":
                    "M3_GEODESIC_TOPOLOGY_PRESERVING_GROWTH",
                maskMode==CleanupMaskMode.LegacyC13?radius:
                    maskMode==CleanupMaskMode.M1EvidenceBounded?Math.Clamp(Math.Max((int)MathF.Round(geometry.Height*.045f),observedOutlineRadius),1,4):
                    growthAudits[^1].MaxDistance,1,
                inputPixels,outputPixels,outputPixels-inputPixels,inputComponents,outputComponents));
            morphologyAudits.Add(new(line.SourceId,"COMPONENT_MERGE_NONE",0,0,outputPixels,outputPixels,0,
                outputComponents,outputComponents));
            if(retainDiagnosticPlanes)for(var y=outputScan.Top;y<outputScan.Bottom;y++)for(var x=outputScan.Left;x<outputScan.Right;x++)
                if(!protectedPixels[x,y]&&Delta(source.GetPixel(x,y),background)>=18&&!admitted[x,y])rejected[x,y]=true;
        }
        return new(core,outlineShadow,baseMaterial,halo,afterDilation,afterComponentMerge,admitted,rejected,protectedConflict,
            searchEnvelope,secondaryColorFamily,componentAudits,morphologyAudits,growthAudits,searchBounds,
            sourceOutlinePixels,sourceShadowPixels){MaterialStages=materialStages};
    }

    internal static Rectangle BuildM3LineBand(IReadOnlyList<NormalizedOcrLine> lines,int lineIndex,
        Rectangle scan,Size imageSize)
    {
        var current=lines[lineIndex].Bounds;
        var center=current.Top+current.Height*.5f;
        float? previous=null,next=null;
        for(var i=0;i<lines.Count;i++)
        {
            if(i==lineIndex)continue;
            var other=lines[i].Bounds;var otherCenter=other.Top+other.Height*.5f;
            // Vertical ownership is only shared where the source lines overlap
            // horizontally. A neighboring column/tab must not bisect this glyph.
            if(other.Right<=current.Left || other.Left>=current.Right)continue;
            if(otherCenter<center-1f&&(previous is null||otherCenter>previous))previous=otherCenter;
            if(otherCenter>center+1f&&(next is null||otherCenter<next))next=otherCenter;
        }
        var top=scan.Top;var bottom=scan.Bottom;
        if(previous is not null)top=Math.Max(top,(int)MathF.Floor((previous.Value+center)*.5f)+1);
        if(next is not null)bottom=Math.Min(bottom,(int)MathF.Floor((center+next.Value)*.5f));
        var band=Rectangle.FromLTRB(scan.Left,Math.Clamp(top,0,imageSize.Height),scan.Right,
            Math.Clamp(Math.Max(top,bottom),0,imageSize.Height));
        return Rectangle.Intersect(band,new Rectangle(Point.Empty,imageSize));
    }

    private static int EstimateMaskStrokeWidth(bool[,] mask,Rectangle bounds)
    {
        var area=0;var perimeter=0;
        for(var y=bounds.Top;y<bounds.Bottom;y++)for(var x=bounds.Left;x<bounds.Right;x++)
        {
            if(!mask[x,y])continue;area++;
            foreach(var (dx,dy) in new[]{(-1,0),(1,0),(0,-1),(0,1)})
            {
                var xx=x+dx;var yy=y+dy;
                if(xx<bounds.Left||xx>=bounds.Right||yy<bounds.Top||yy>=bounds.Bottom||!mask[xx,yy])perimeter++;
            }
        }
        return Math.Clamp((int)Math.Round(2d*area/Math.Max(1,perimeter)),1,6);
    }

    private static TopologyGrowthResult GrowTopologyPreserving(bool[,] baseMask,bool[,] evidence,int[,] evidenceScore,
        Rectangle bounds,int maxDistance,int estimatedStrokeWidth)
    {
        var width=baseMask.GetLength(0);var height=baseMask.GetLength(1);
        var result=(bool[,])baseMask.Clone();var halo=new bool[width,height];
        var labels=new int[bounds.Width,bounds.Height];var distance=new int[bounds.Width,bounds.Height];
        var componentSizes=new List<int>{0};var componentPerimeters=new List<int>{0};
        var label=0;
        for(var y=bounds.Top;y<bounds.Bottom;y++)for(var x=bounds.Left;x<bounds.Right;x++)
        {
            if(!baseMask[x,y]||labels[x-bounds.Left,y-bounds.Top]!=0)continue;
            label++;var size=0;var perimeter=0;var queue=new Queue<Point>();queue.Enqueue(new(x,y));labels[x-bounds.Left,y-bounds.Top]=label;
            while(queue.Count>0)
            {
                var p=queue.Dequeue();size++;
                foreach(var (dx,dy) in new[]{(-1,0),(1,0),(0,-1),(0,1)})
                {
                    var xx=p.X+dx;var yy=p.Y+dy;
                    if(xx<bounds.Left||xx>=bounds.Right||yy<bounds.Top||yy>=bounds.Bottom||!baseMask[xx,yy])perimeter++;
                }
                for(var oy=-1;oy<=1;oy++)for(var ox=-1;ox<=1;ox++)
                {
                    if(ox==0&&oy==0)continue;var xx=p.X+ox;var yy=p.Y+oy;
                    if(xx<bounds.Left||xx>=bounds.Right||yy<bounds.Top||yy>=bounds.Bottom||
                       !baseMask[xx,yy]||labels[xx-bounds.Left,yy-bounds.Top]!=0)continue;
                    labels[xx-bounds.Left,yy-bounds.Top]=label;queue.Enqueue(new(xx,yy));
                }
            }
            componentSizes.Add(size);componentPerimeters.Add(perimeter);
        }
        var basePixels=componentSizes.Sum();
        var budgets=new int[label+1];var used=new int[label+1];
        for(var i=1;i<=label;i++)
        {
            var boundaryBudget=(int)Math.Ceiling(componentPerimeters[i]*maxDistance*.18+
                Math.PI*maxDistance*maxDistance*.25);
            var componentCap=(int)Math.Ceiling(componentSizes[i]*.22);
            budgets[i]=Math.Max(1,Math.Min(boundaryBudget,componentCap));
        }
        var rejectedBridge=new HashSet<(int X,int Y)>();var budgetTruncated=0;
        for(var layer=1;layer<=maxDistance;layer++)
        {
            var proposals=new Dictionary<(int X,int Y),int>();
            for(var y=bounds.Top;y<bounds.Bottom;y++)for(var x=bounds.Left;x<bounds.Right;x++)
            {
                if(!evidence[x,y]||labels[x-bounds.Left,y-bounds.Top]!=0)continue;
                var neighborLabels=new HashSet<int>();
                for(var oy=-1;oy<=1;oy++)for(var ox=-1;ox<=1;ox++)
                {
                    if(ox==0&&oy==0)continue;var xx=x+ox;var yy=y+oy;
                    if(xx<bounds.Left||xx>=bounds.Right||yy<bounds.Top||yy>=bounds.Bottom)continue;
                    if(labels[xx-bounds.Left,yy-bounds.Top]>0&&distance[xx-bounds.Left,yy-bounds.Top]<layer)neighborLabels.Add(labels[xx-bounds.Left,yy-bounds.Top]);
                }
                if(neighborLabels.Count==1)proposals[(x,y)]=neighborLabels.First();
                else if(neighborLabels.Count>1)rejectedBridge.Add((x,y));
            }
            var contested=new HashSet<(int X,int Y)>();
            foreach(var proposal in proposals)
            {
                var (x,y)=proposal.Key;
                for(var oy=-1;oy<=1;oy++)for(var ox=-1;ox<=1;ox++)
                {
                    if(ox==0&&oy==0)continue;
                    if(proposals.TryGetValue((x+ox,y+oy),out var other)&&other!=proposal.Value)
                    {contested.Add(proposal.Key);contested.Add((x+ox,y+oy));}
                }
            }
            foreach(var p in contested){proposals.Remove(p);rejectedBridge.Add(p);}
            foreach(var proposal in proposals.OrderByDescending(p=>evidenceScore[p.Key.X-bounds.Left,p.Key.Y-bounds.Top])
                         .ThenBy(p=>p.Key.Y).ThenBy(p=>p.Key.X))
            {
                var component=proposal.Value;
                if(used[component]>=budgets[component]){budgetTruncated++;continue;}
                var (x,y)=proposal.Key;labels[x-bounds.Left,y-bounds.Top]=component;distance[x-bounds.Left,y-bounds.Top]=layer;
                result[x,y]=true;halo[x,y]=true;used[component]++;
            }
        }
        var finalPixels=CountMaskPixels(result,bounds);var finalComponents=CountMaskComponents(result,bounds);
        var evidencePixels=CountMaskPixels(evidence,bounds);
        var confidence=(int)Math.Round(1000d*rejectedBridge.Count/Math.Max(1,evidencePixels));
        return new(result,halo,basePixels,finalPixels,label,finalComponents,budgets.Sum(),rejectedBridge.Count,
            budgetTruncated,maxDistance,estimatedStrokeWidth,Math.Clamp(confidence,0,1000));
    }

    private static int LocalEdgeStrength(ReadOnlyBitmapPixelBuffer source,int x,int y)
    {
        var c=source.GetPixel(x,y);var strongest=0;
        foreach(var (dx,dy) in new[]{(-1,0),(1,0),(0,-1),(0,1)})
        {
            var xx=Math.Clamp(x+dx,0,source.Width-1);var yy=Math.Clamp(y+dy,0,source.Height-1);
            var p=source.GetPixel(xx,yy);
            strongest=Math.Max(strongest,Math.Abs(c.R-p.R)+Math.Abs(c.G-p.G)+Math.Abs(c.B-p.B));
        }
        return strongest;
    }

    internal static void RejectContainerFillComponents(bool[,] seed,Rectangle geometry)
    {
        var remaining=new HashSet<(int X,int Y)>();
        for(var y=geometry.Top;y<geometry.Bottom;y++)for(var x=geometry.Left;x<geometry.Right;x++)if(seed[x,y])remaining.Add((x,y));
        var area=Math.Max(1,geometry.Width*geometry.Height);
        while(remaining.Count>0)
        {
            var first=remaining.First();remaining.Remove(first);var component=new List<(int X,int Y)>{first};var queue=new Queue<(int X,int Y)>();queue.Enqueue(first);
            while(queue.Count>0)
            {
                var p=queue.Dequeue();
                for(var oy=-1;oy<=1;oy++)for(var ox=-1;ox<=1;ox++)
                {
                    if(ox==0&&oy==0)continue;var next=(p.X+ox,p.Y+oy);
                    if(remaining.Remove(next)){component.Add(next);queue.Enqueue(next);}
                }
            }
            var left=component.Min(p=>p.X);var right=component.Max(p=>p.X);var top=component.Min(p=>p.Y);var bottom=component.Max(p=>p.Y);
            var width=right-left+1;var height=bottom-top+1;var boxArea=Math.Max(1,width*height);var fill=component.Count/(float)boxArea;
            var containerScale=width>=geometry.Width*.55f||height>=geometry.Height*.72f;
            if(component.Count>=area*.18f&&containerScale&&fill>=.48f)
                foreach(var p in component)seed[p.X,p.Y]=false;
        }
    }

    private static void RejectLongHorizontalStructure(bool[,] seed,Rectangle geometry)
    {
        // OCR line boxes can cross panel rules, shelf edges, and other scene
        // structure.  Those pixels may share the glyph colour but a text stroke
        // cannot remain continuously solid across this much of the line width.
        // Remove only the long run and its very small vertical thickness; normal
        // glyph stems, crossbars, punctuation, and inter-glyph gaps remain intact.
        // A real UI rule often survives colour admission as several medium-length
        // fragments rather than one full-width run.  The old 2x line-height floor
        // missed those fragments and let them weld a text row to the scene.  Keep
        // the threshold longer than an ordinary glyph stroke, but short enough to
        // reject the visible pieces of one shared horizontal structure.
        var minimumRun=Math.Max(24,(int)MathF.Ceiling(Math.Max(
            geometry.Width*.18f,geometry.Height*2f)));
        var rejected=new List<(int Left,int Right,int Y)>();
        for(var y=geometry.Top;y<geometry.Bottom;y++)
        {
            var start=-1;
            for(var x=geometry.Left;x<=geometry.Right;x++)
            {
                var set=x<geometry.Right&&seed[x,y];
                if(set&&start<0)start=x;
                if(set||start<0)continue;
                if(x-start>=minimumRun)rejected.Add((start,x,y));
                start=-1;
            }
        }
        foreach(var (left,right,y) in rejected)
            for(var yy=Math.Max(geometry.Top,y-1);yy<=Math.Min(geometry.Bottom-1,y+1);yy++)
            for(var x=left;x<right;x++)seed[x,yy]=false;
    }

    private static void AdmitSecondaryTextColorFamilies(ReadOnlyBitmapPixelBuffer source,string lineSourceId,Rectangle geometry,Color background,
        bool[,] protectedPixels,bool[,] seed,bool[,] trace,List<CandidateComponentAudit> audits)
    {
        int Delta(Color a,Color b)=>Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B);
        var area=Math.Max(1,geometry.Width*geometry.Height);
        var primaryCoreBounds=MaskBoundsWithin(seed,geometry);
        var groups=new Dictionary<(int R,int G,int B),List<Point>>();
        for(var y=geometry.Top;y<geometry.Bottom;y++)for(var x=geometry.Left;x<geometry.Right;x++)
        {
            if(protectedPixels[x,y]||seed[x,y])continue;
            var c=source.GetPixel(x,y);if(Delta(c,background)<24)continue;
            var key=(c.R/24,c.G/24,c.B/24);
            if(!groups.TryGetValue(key,out var points))groups[key]=points=[];
            points.Add(new Point(x,y));
        }
        foreach(var points in groups.Values)
        {
            var groupColor=source.GetPixel(points[0].X,points[0].Y);
            if(points.Count<Math.Max(6,(int)(area*.0015f))||points.Count>area*.22f)continue;
            var pointSet=points.Select(p=>(p.X,p.Y)).ToHashSet();
            var components=new List<List<Point>>();
            while(pointSet.Count>0)
            {
                var first=pointSet.First();pointSet.Remove(first);
                var component=new List<Point>{new(first.X,first.Y)};var queue=new Queue<(int X,int Y)>();queue.Enqueue(first);
                while(queue.Count>0)
                {
                    var p=queue.Dequeue();
                    for(var oy=-1;oy<=1;oy++)for(var ox=-1;ox<=1;ox++)
                    {
                        if(ox==0&&oy==0)continue;var next=(p.X+ox,p.Y+oy);
                        if(pointSet.Remove(next)){queue.Enqueue(next);component.Add(new(next.Item1,next.Item2));}
                    }
                }
                components.Add(component);
            }
            bool IsPlausible(List<Point> c)
            {
                var width=c.Max(p=>p.X)-c.Min(p=>p.X)+1;var height=c.Max(p=>p.Y)-c.Min(p=>p.Y)+1;
                return c.Count>=2&&c.Count<=area*.075f&&height>=Math.Max(2,geometry.Height*.16f)&&width<=geometry.Height*1.8f;
            }
            var plausible=components.Where(IsPlausible).ToArray();
            var groupAdmitted=plausible.Length>=2;
            foreach(var component in components)
            {
                if(component.Count<2)continue;
                var bounds=Rectangle.FromLTRB(component.Min(p=>p.X),component.Min(p=>p.Y),component.Max(p=>p.X)+1,component.Max(p=>p.Y)+1);
                var distance=RectangleDistance(bounds,primaryCoreBounds,geometry);
                var accepted=groupAdmitted&&IsPlausible(component);
                audits.Add(new(lineSourceId,bounds,component.Count,distance,true,Delta(groupColor,background),0,
                    accepted,accepted?"ADMIT_SECONDARY_TEXT_COLOR_COMPONENT":IsPlausible(component)?"REJECT_FAMILY_INSUFFICIENT_STROKES":"REJECT_COMPONENT_GEOMETRY"));
                if(accepted)foreach(var p in component)
                {seed[p.X,p.Y]=true;if(trace.Length!=0)trace[p.X,p.Y]=true;}
            }
        }
    }

    private static Rectangle MaskBoundsWithin(bool[,] mask,Rectangle geometry)
    {
        var left=geometry.Right;var top=geometry.Bottom;var right=geometry.Left-1;var bottom=geometry.Top-1;
        for(var y=geometry.Top;y<geometry.Bottom;y++)for(var x=geometry.Left;x<geometry.Right;x++)if(mask[x,y])
        {left=Math.Min(left,x);top=Math.Min(top,y);right=Math.Max(right,x);bottom=Math.Max(bottom,y);}
        return right<left?Rectangle.Empty:Rectangle.FromLTRB(left,top,right+1,bottom+1);
    }

    private static int RectangleDistance(Rectangle a,Rectangle b,Rectangle geometry)
    {
        if(b.IsEmpty)return Math.Max(geometry.Width,geometry.Height);
        var dx=a.Right<b.Left?b.Left-a.Right:a.Left>b.Right?a.Left-b.Right:0;
        var dy=a.Bottom<b.Top?b.Top-a.Bottom:a.Top>b.Bottom?a.Top-b.Bottom:0;
        return dx+dy;
    }

    private static bool[,] BuildSourceEdgeMap(Bitmap source,CorePipelineDocument document)
    {
        var result=new bool[source.Width,source.Height];
        foreach(var line in document.VisualBlocks.SelectMany(x=>x.Lines))
        {
            var scan=Clamp(Rectangle.Inflate(Rectangle.Round(line.Bounds),8,8),source.Size);
            for(var y=Math.Max(scan.Top,1);y<Math.Min(scan.Bottom,source.Height-1);y++)for(var x=Math.Max(scan.Left,1);x<Math.Min(scan.Right,source.Width-1);x++)
            {
                var c=source.GetPixel(x,y);int D(Color p)=>Math.Abs(c.R-p.R)+Math.Abs(c.G-p.G)+Math.Abs(c.B-p.B);
                if(Math.Max(D(source.GetPixel(x+1,y)),D(source.GetPixel(x,y+1)))>=54)result[x,y]=true;
            }
        }
        return result;
    }

    private static bool[,] SubtractMask(bool[,] source,bool[,] subtract)
    {
        var result=new bool[source.GetLength(0),source.GetLength(1)];
        for(var y=0;y<source.GetLength(1);y++)for(var x=0;x<source.GetLength(0);x++)result[x,y]=source[x,y]&&!subtract[x,y];
        return result;
    }

    private sealed record BackgroundOwnershipEvidence(SourceSurfaceClassR2 SurfaceClass,float SurfaceConfidence,
        bool TranslucentUi,float HorizontalPatternConfidence,float VerticalPatternConfidence,bool LocallySmooth,
        float CleanupOwnershipRatio,bool ProvedLowFrequencyPanel,Rectangle AuthorizedSurfaceBounds,
        float SourceBackgroundConfidence,int SharedSurfaceBlockCount,int ProtectedPixelCount,
        double PanelMedianResidual,double PanelP90Residual,PlanarSurface? AuthorizedPlane,bool[,] SamplingExclusion,
        string DecisionEvidence);

    private sealed record BcpsComponentAudit(string BlockId,int ComponentId,Rectangle Bounds,int MaskPixels,
        string SurfaceClass,string Route,int DonorPixels,int SearchMargin,double DonorVariance,double Confidence,
        int EdgeBarrierRejects,int CandidateEvaluations,int ChangedPixels,double ElapsedMs,string Fallback,
        int MaskPixelsBefore,int MaskPixelsAfter);

    private static BackgroundOwnershipEvidence AnalyzeBackgroundOwnership(Bitmap source,bool[,] mask,Rectangle bounds,
        bool[,] protectedPixels,VisualBlock block,BackgroundSurfaceOwnerR2 surface,RenderPlanR2 plan,
        SourceStyleEvidenceR2 styleEvidence)
    {
        // Source fonts in the held-out set often have a bright core plus a 4-7 px
        // outline/shadow.  The write authority remains the exact glyph mask, but
        // donors must step beyond the complete material family or the old outline
        // is sampled back into the restoration as a dark/bright band.
        var exclusion=DilateMask(mask,bounds,Math.Clamp(bounds.Height/10,3,9));
        var smooth=IsLocallySmooth(source,exclusion,bounds);
        var (horizontal,vertical)=DirectionalContinuationConfidence(source,exclusion,bounds);
        // The block mask is created only inside its search envelope and bounds is the
        // exact non-empty mask extent, so every owned pixel is necessarily in bounds.
        // Avoid a full-frame scan for every block just to recount the same pixels.
        var owned=CountMaskPixels(mask,bounds);var total=owned;
        var ratio=total==0?0:owned/(float)total;
        var translucent=plan.VisualRole==SourceVisualRoleR2.Control&&
            surface.SurfaceClass is SourceSurfaceClassR2.Gradient or SourceSurfaceClassR2.Textured&&
            surface.Confidence<.93f;
        var panelBounds=Clamp(Rectangle.Inflate(Rectangle.Round(block.Bounds),8,6),source.Size);
        var protectedCount=CountMaskPixels(protectedPixels,panelBounds);
        var panelCandidate=!translucent&&plan.VisualRole==SourceVisualRoleR2.Body&&block.Lines.Count>=3&&
            surface.SurfaceClass is SourceSurfaceClassR2.Flat or SourceSurfaceClassR2.Gradient&&
            surface.Confidence>=.82f&&styleEvidence.BackgroundConfidence>=.78f&&
            styleEvidence.ForegroundOccupancy<=.20f&&protectedCount==0;
        PlanarSurface? authorizedPlane=null;double panelMedianResidual=-1;
        double panelP90Residual=-1;
        if(panelCandidate)
        {
            var fallback=SampleBorderColor(source,Rectangle.Round(block.Bounds));
            if(TryFitPlanarSurface(source,exclusion,panelBounds,fallback,out var plane,
                   out panelMedianResidual,out panelP90Residual)&&
               panelMedianResidual<=34&&panelP90Residual<=92)
                authorizedPlane=plane;
        }
        var provedPanel=authorizedPlane is not null;
        var evidence=$"surface={surface.SurfaceClass};surfaceConfidence={surface.Confidence:F3};container={plan.ContainerOwnerId};"+
            $"translucent={translucent};hPattern={horizontal:F3};vPattern={vertical:F3};smooth={smooth};cleanupOwned={ratio:F3};"+
            $"backgroundConfidence={styleEvidence.BackgroundConfidence:F3};sharedSurfaceBlocks={surface.UnderlyingBlockIds.Count};"+
            $"protectedPixels={protectedCount};panelCandidate={panelCandidate};panelMedianResidual={panelMedianResidual:F2};"+
            $"panelP90Residual={panelP90Residual:F2};provedLowFrequencyPanel={provedPanel}";
        return new(surface.SurfaceClass,surface.Confidence,translucent,horizontal,vertical,smooth,ratio,
            provedPanel,panelBounds,styleEvidence.BackgroundConfidence,surface.UnderlyingBlockIds.Count,
            protectedCount,panelMedianResidual,panelP90Residual,authorizedPlane,exclusion,evidence);
    }

    private static (float Horizontal,float Vertical) DirectionalContinuationConfidence(Bitmap source,bool[,] exclusion,Rectangle bounds)
    {
        var domain=Clamp(Rectangle.Inflate(bounds,Math.Clamp(bounds.Height/3,3,18),Math.Clamp(bounds.Height/3,3,18)),source.Size);
        var horizontal=new List<float>();var vertical=new List<float>();
        static float Similarity(Color a,Color b)=>1f-(Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B))/765f;
        var yStep=Math.Max(1,domain.Height/12);var xStep=Math.Max(1,domain.Width/16);
        for(var y=domain.Top;y<domain.Bottom;y+=yStep)
        {
            var lx=Math.Max(domain.Left,bounds.Left-2);var rx=Math.Min(domain.Right-1,bounds.Right+1);
            if(!exclusion[lx,y]&&!exclusion[rx,y])horizontal.Add(Similarity(source.GetPixel(lx,y),source.GetPixel(rx,y)));
        }
        for(var x=domain.Left;x<domain.Right;x+=xStep)
        {
            var ty=Math.Max(domain.Top,bounds.Top-2);var by=Math.Min(domain.Bottom-1,bounds.Bottom+1);
            if(!exclusion[x,ty]&&!exclusion[x,by])vertical.Add(Similarity(source.GetPixel(x,ty),source.GetPixel(x,by)));
        }
        return(horizontal.Count==0?0:horizontal.Average(),vertical.Count==0?0:vertical.Average());
    }

    internal static string SelectBackgroundRestorationRoute(SourceSurfaceClassR2 surfaceClass,float surfaceConfidence,
        bool translucentUi,float horizontalPatternConfidence,float verticalPatternConfidence,bool locallySmooth,
        bool provedLowFrequencyPanel=false,float sourceBackgroundConfidence=1f)
    {
        if(translucentUi)return "TRANSLUCENT_UI_MINIMAL_GLYPH_DIFFUSION";
        if(provedLowFrequencyPanel)return "PROVED_LOW_FREQUENCY_PANEL_RESURFACE";
        if(locallySmooth&&surfaceClass is SourceSurfaceClassR2.Flat or SourceSurfaceClassR2.Gradient)
            return "SURFACE_EVIDENCE_BOUNDARY_DIFFUSION";
        if(sourceBackgroundConfidence>=.78f&&surfaceConfidence>=.72f&&horizontalPatternConfidence>=.82f&&
           horizontalPatternConfidence>=verticalPatternConfidence+.03f)
            return "PATTERN_VERIFIED_HORIZONTAL_CONTINUATION";
        return "UNCERTAIN_GLYPH_LOCAL_DIFFUSION";
    }

    private static int RestoreLocalSurface(Bitmap target, Bitmap source, bool[,] mask,bool[,] materialClosure, Rectangle bounds,
        VisualBlock block,BackgroundOwnershipEvidence ownership,bool preserveGlyphMask,
        VerifiedContainerSurfaceModel? verifiedSurface,List<BcpsComponentAudit> bcpsAudits,out string restorationMode,RestorationStageCapture? capture=null,
        bool nativeMaterial=false,bool[,]? nativeDonorExclusion=null)
    {
        var changed=0; var fallback=SampleBorderColor(source,bounds);
        // Sampling directly beside the cleanup mask can copy antialiased source-glyph
        // pixels back into the hole.  Keep the write mask unchanged, but make the
        // continuation sampler step beyond a small glyph halo.
        // Background ownership already built this immutable sampling mask. Reuse it;
        // rebuilding it here doubled the largest per-block allocation and traversal.
        var samplingExclusion=ownership.SamplingExclusion;
        capture?.DonorExclusion(samplingExclusion,Point.Empty,true,"Background ownership sampling exclusion; route-specific replacement recorded when present");
        if(nativeMaterial&&nativeDonorExclusion is not null)
        {
            restorationMode="NATIVE_SOURCE_MATERIAL_TIGHT_DONOR_CONTINUATION";
            var domain=Clamp(Rectangle.Inflate(bounds,48,48),source.Size);
            capture?.DonorExclusion(nativeDonorExclusion,Point.Empty,true,
                "Actual source glyph material plus two pixels; unrelated source glyphs and protected art excluded");
            for(var y=bounds.Top;y<bounds.Bottom;y++)for(var x=bounds.Left;x<bounds.Right;x++)
            {
                if(!mask[x,y])continue;
                var color=DirectionalCleanContinuation(source,domain,(xx,yy)=>nativeDonorExclusion[xx,yy],x,y,fallback);
                capture?.Candidate(x,y,color,1f);
                if(target.GetPixel(x,y).ToArgb()!=color.ToArgb()){target.SetPixel(x,y,color);changed++;}
            }
            return changed;
        }
        if(verifiedSurface is not null)
        {
            restorationMode=$"M3H_VERIFIED_CONTAINER_SHARED_SURFACE:{verifiedSurface.SurfaceModelId}";
            return RestoreVerifiedContainerSurface(target,source,mask,bounds,materialClosure,block,verifiedSurface,capture);
        }
        if(preserveGlyphMask&&IsWideSceneSubtitle(block,source.Size))
        {
            restorationMode="M3H_BLOCK_LINE_CLEAN_DONOR_CONTINUATION";
            return RestoreBlockLineSurface(target,source,mask,bounds,materialClosure,block,fallback,capture);
        }
        restorationMode=SelectBackgroundRestorationRoute(ownership.SurfaceClass,ownership.SurfaceConfidence,
            ownership.TranslucentUi,ownership.HorizontalPatternConfidence,ownership.VerticalPatternConfidence,
            ownership.LocallySmooth,ownership.ProvedLowFrequencyPanel,ownership.SourceBackgroundConfidence);
        var m2Override=preserveGlyphMask
            ?Environment.GetEnvironmentVariable("ST_M2_RESTORATION_ROUTE")?.Trim().ToUpperInvariant():null;
        var bcps=preserveGlyphMask&&string.Equals(
            Environment.GetEnvironmentVariable("ST_M4_RESTORATION_ROUTE")?.Trim(),"BCPS",
            StringComparison.OrdinalIgnoreCase);
        if(bcps)
        {
            restorationMode="M4_BOUNDARY_CONDITIONED_PATCH_SYNTHESIS";
            return RestoreBoundaryConditionedPatches(target,source,mask,bounds,block,ownership,bcpsAudits);
        }
        var forceBoundary=m2Override=="BOUNDARY_DIFFUSION";
        var forceLocal=m2Override=="LOCAL_CONTINUATION";
        var forceHorizontal=m2Override=="HORIZONTAL_CONTINUATION";
        var forcePlanar=m2Override=="PLANAR_IF_AVAILABLE"&&ownership.AuthorizedPlane is not null;
        if(forceBoundary)restorationMode="M2_FORCE_BOUNDARY_DIFFUSION";
        else if(forceLocal)restorationMode="M2_FORCE_LOCAL_CONTINUATION";
        else if(forceHorizontal)restorationMode="M2_FORCE_HORIZONTAL_CONTINUATION";
        else if(forcePlanar)restorationMode="M2_FORCE_PLANAR_IF_AVAILABLE";
        if((restorationMode=="PROVED_LOW_FREQUENCY_PANEL_RESURFACE"||forcePlanar)&&ownership.AuthorizedPlane is { } plane)
        {
            var resurface=preserveGlyphMask?bounds:ownership.AuthorizedSurfaceBounds;
            const float feather=7f;
            for(var y=resurface.Top;y<resurface.Bottom;y++)for(var x=resurface.Left;x<resurface.Right;x++)
            {
                if(preserveGlyphMask&&!mask[x,y])continue;
                var replacement=plane.At(x,y);var original=source.GetPixel(x,y);
                var edgeDistance=Math.Min(Math.Min(x-resurface.Left,resurface.Right-1-x),
                    Math.Min(y-resurface.Top,resurface.Bottom-1-y));
                var alpha=Math.Clamp((edgeDistance+1)/feather,0f,1f);
                capture?.Candidate(x,y,replacement,alpha);
                var blended=Color.FromArgb(
                    (int)Math.Round(original.R*(1-alpha)+replacement.R*alpha),
                    (int)Math.Round(original.G*(1-alpha)+replacement.G*alpha),
                    (int)Math.Round(original.B*(1-alpha)+replacement.B*alpha));
                if(!preserveGlyphMask)mask[x,y]=true;
                if(target.GetPixel(x,y).ToArgb()!=blended.ToArgb()){target.SetPixel(x,y,blended);changed++;}
            }
            return changed;
        }
        var diffuse=forceBoundary||(!forceLocal&&!forceHorizontal&&restorationMode is
            "TRANSLUCENT_UI_MINIMAL_GLYPH_DIFFUSION" or "SURFACE_EVIDENCE_BOUNDARY_DIFFUSION" or
            "UNCERTAIN_GLYPH_LOCAL_DIFFUSION");
        var horizontal=forceHorizontal||(!forceLocal&&restorationMode=="PATTERN_VERIFIED_HORIZONTAL_CONTINUATION");
        // The ownership exclusion remains deliberately wider for model fitting and
        // donor statistics.  Harmonic reconstruction uses the already complete M3H
        // glyph material plus one pixel so its boundary meets the visible surface
        // continuously instead of leaving a glyph-shaped dark/bright silhouette.
        var diffusionExclusion=diffuse?DilateMask(mask,bounds,1):samplingExclusion;
        using var restored=diffuse?BoundaryDiffusionInpaint(source,diffusionExclusion,bounds,fallback):null;
        capture?.DonorExclusion(diffuse?diffusionExclusion:samplingExclusion,Point.Empty,true,
            diffuse?"Generic diffusion exclusion; distinct from Profile local-continuation route":"Background ownership sampling exclusion");
        var radius=Math.Clamp(Math.Max(bounds.Width,bounds.Height)/5,8,48);
        for(var y=bounds.Top;y<bounds.Bottom;y++) for(var x=bounds.Left;x<bounds.Right;x++)
        {
            if(!mask[x,y])continue;
            var c=restored is not null?restored.GetPixel(x,y)
                :horizontal?LocalHorizontalContinuation(source,samplingExclusion,x,y,radius,fallback)
                    :LocalContinuation(source,samplingExclusion,x,y,radius,fallback);
            capture?.Candidate(x,y,c,1f);
            if(target.GetPixel(x,y).ToArgb()!=c.ToArgb()){target.SetPixel(x,y,c);changed++;}
        }
        return changed;
    }

    private static int RestoreBoundaryConditionedPatches(Bitmap target,Bitmap source,bool[,] mask,Rectangle bounds,
        VisualBlock block,BackgroundOwnershipEvidence ownership,List<BcpsComponentAudit> audits)
    {
        var changedTotal=0;var componentId=0;
        foreach(var pixels in EnumerateMaskComponents(mask,bounds))
        {
            componentId++;var timer=Stopwatch.StartNew();
            var componentBounds=Rectangle.FromLTRB(pixels.Min(p=>p.X),pixels.Min(p=>p.Y),
                pixels.Max(p=>p.X)+1,pixels.Max(p=>p.Y)+1);
            var donors=new List<Point>();var edgeRejects=0;var margin=4;
            for(;margin<=20;margin+=4)
            {
                donors.Clear();edgeRejects=0;
                var roi=Clamp(Rectangle.Inflate(componentBounds,margin,margin),source.Size);
                for(var y=roi.Top;y<roi.Bottom;y++)for(var x=roi.Left;x<roi.Right;x++)
                {
                    if(mask[x,y]||ownership.SamplingExclusion[x,y])continue;
                    var edge=LocalEdgeStrength(source,x,y);
                    if(edge>108){edgeRejects++;continue;}
                    donors.Add(new(x,y));
                }
                if(donors.Count>=24)break;
            }
            var maskBefore=pixels.Count;var candidateEvaluations=0;var changed=0;var fallback="NONE";
            var donorVariance=donors.Count==0?double.PositiveInfinity:LumaVariance(source,donors);
            var donorCoverage=Math.Min(1d,donors.Count/32d);
            var textureConfidence=Math.Max(.05,1-Math.Min(1,Math.Sqrt(Math.Max(0,donorVariance))/55d));
            var barrierPenalty=1-Math.Min(.25,edgeRejects/(double)Math.Max(1,donors.Count+edgeRejects)*.25);
            var confidence=donorCoverage*textureConfidence*barrierPenalty;
            var flat=ownership.TranslucentUi||donorVariance<=260||
                (ownership.SurfaceClass is SourceSurfaceClassR2.Flat or SourceSurfaceClassR2.Gradient&&donorVariance<=800);
            string route;
            if(donors.Count<12)
            {
                route="SOURCE_PRESERVE_LOW_DONOR_SUPPORT";fallback="SOURCE_PRESERVE";
            }
            else if(flat&&TryFitBcpsPlane(source,donors,out var plane))
            {
                route="ROBUST_LOCAL_SURFACE_FIT";
                var textureScale=donorVariance<=80?.10:.24;
                var sampled=SampleDonors(donors,128);
                foreach(var p in pixels)
                {
                    var predicted=plane.At(p.X,p.Y);
                    var donor=sampled[(int)((uint)unchecked(p.X*73856093^p.Y*19349663^componentId*83492791)%(uint)sampled.Count)];
                    var donorColor=source.GetPixel(donor.X,donor.Y);var donorPlane=plane.At(donor.X,donor.Y);
                    int Channel(int value,int residual)=>Math.Clamp((int)Math.Round(value+
                        Math.Clamp(residual,-12,12)*textureScale),0,255);
                    var replacement=Color.FromArgb(Channel(predicted.R,donorColor.R-donorPlane.R),
                        Channel(predicted.G,donorColor.G-donorPlane.G),Channel(predicted.B,donorColor.B-donorPlane.B));
                    if(target.GetPixel(p.X,p.Y).ToArgb()!=replacement.ToArgb())
                    {target.SetPixel(p.X,p.Y,replacement);changed++;}
                }
            }
            else if(confidence>=.28&&pixels.Count<=1500)
            {
                route="BOUNDARY_CONDITIONED_LOCAL_PATCH";
                changed=FillBcpsTextureComponent(target,source,mask,pixels,SampleDonors(donors,48),
                    ref candidateEvaluations);
                if(changed==0){fallback="SOURCE_PRESERVE";route="SOURCE_PRESERVE_PATCH_NO_FRONTIER";}
            }
            else
            {
                route="SOURCE_PRESERVE_COMPLEX_OR_LOW_CONFIDENCE";fallback="SOURCE_PRESERVE";
            }
            timer.Stop();changedTotal+=changed;
            audits.Add(new(block.BlockId,componentId,componentBounds,pixels.Count,ownership.SurfaceClass.ToString(),
                route,donors.Count,Math.Min(margin,20),double.IsFinite(donorVariance)?donorVariance:-1,confidence,
                edgeRejects,candidateEvaluations,changed,timer.Elapsed.TotalMilliseconds,fallback,maskBefore,pixels.Count));
        }
        return changedTotal;
    }

    private static IReadOnlyList<List<Point>> EnumerateMaskComponents(bool[,] mask,Rectangle bounds)
    {
        var result=new List<List<Point>>();var seen=new bool[bounds.Width,bounds.Height];
        for(var y=bounds.Top;y<bounds.Bottom;y++)for(var x=bounds.Left;x<bounds.Right;x++)
        {
            if(!mask[x,y]||seen[x-bounds.Left,y-bounds.Top])continue;
            var pixels=new List<Point>();var queue=new Queue<Point>();queue.Enqueue(new(x,y));seen[x-bounds.Left,y-bounds.Top]=true;
            while(queue.Count>0)
            {
                var p=queue.Dequeue();pixels.Add(p);
                for(var oy=-1;oy<=1;oy++)for(var ox=-1;ox<=1;ox++)
                {
                    if(ox==0&&oy==0)continue;var xx=p.X+ox;var yy=p.Y+oy;
                    if(xx<bounds.Left||xx>=bounds.Right||yy<bounds.Top||yy>=bounds.Bottom||
                       seen[xx-bounds.Left,yy-bounds.Top]||!mask[xx,yy])continue;
                    seen[xx-bounds.Left,yy-bounds.Top]=true;queue.Enqueue(new(xx,yy));
                }
            }
            result.Add(pixels);
        }
        return result;
    }

    private static double LumaVariance(Bitmap source,IReadOnlyList<Point> points)
    {
        if(points.Count==0)return 0;
        var values=points.Select(p=>{var c=source.GetPixel(p.X,p.Y);return .2126*c.R+.7152*c.G+.0722*c.B;}).ToArray();
        var mean=values.Average();return values.Sum(v=>(v-mean)*(v-mean))/values.Length;
    }

    private static int LocalEdgeStrength(Bitmap source,int x,int y)
    {
        var c=source.GetPixel(x,y);var strongest=0;
        foreach(var (dx,dy) in new[]{(-1,0),(1,0),(0,-1),(0,1)})
        {
            var xx=Math.Clamp(x+dx,0,source.Width-1);var yy=Math.Clamp(y+dy,0,source.Height-1);
            var p=source.GetPixel(xx,yy);
            strongest=Math.Max(strongest,Math.Abs(c.R-p.R)+Math.Abs(c.G-p.G)+Math.Abs(c.B-p.B));
        }
        return strongest;
    }

    private static List<Point> SampleDonors(IReadOnlyList<Point> donors,int cap)
    {
        if(donors.Count<=cap)return donors.ToList();
        var result=new List<Point>(cap);
        for(var i=0;i<cap;i++)result.Add(donors[(int)((long)i*donors.Count/cap)]);
        return result;
    }

    private static bool TryFitBcpsPlane(Bitmap source,IReadOnlyList<Point> donorPoints,out PlanarSurface surface)
    {
        var colors=donorPoints.Select(p=>source.GetPixel(p.X,p.Y)).ToArray();
        int Median(Func<Color,int> f)=>colors.Select(f).OrderBy(v=>v).ElementAt(colors.Length/2);
        var median=Color.FromArgb(Median(c=>c.R),Median(c=>c.G),Median(c=>c.B));
        var samples=donorPoints.Select(p=>(P:p,C:source.GetPixel(p.X,p.Y)))
            .Where(s=>Math.Abs(s.C.R-median.R)+Math.Abs(s.C.G-median.G)+Math.Abs(s.C.B-median.B)<=135).ToArray();
        if(samples.Length<12){surface=new(median.R,0,0,median.G,0,0,median.B,0,0);return samples.Length>0;}
        var mx=samples.Average(s=>s.P.X);var my=samples.Average(s=>s.P.Y);
        var sxx=samples.Sum(s=>(s.P.X-mx)*(s.P.X-mx));var syy=samples.Sum(s=>(s.P.Y-my)*(s.P.Y-my));
        var sxy=samples.Sum(s=>(s.P.X-mx)*(s.P.Y-my));var determinant=sxx*syy-sxy*sxy;
        if(Math.Abs(determinant)<1){surface=new(median.R,0,0,median.G,0,0,median.B,0,0);return true;}
        (double A,double X,double Y) Fit(Func<Color,double> channel)
        {
            var mz=samples.Average(s=>channel(s.C));
            var sxz=samples.Sum(s=>(s.P.X-mx)*(channel(s.C)-mz));
            var syz=samples.Sum(s=>(s.P.Y-my)*(channel(s.C)-mz));
            var bx=(sxz*syy-syz*sxy)/determinant;var by=(syz*sxx-sxz*sxy)/determinant;
            return(mz-bx*mx-by*my,bx,by);
        }
        var r=Fit(c=>c.R);var g=Fit(c=>c.G);var b=Fit(c=>c.B);
        surface=new(r.A,r.X,r.Y,g.A,g.X,g.Y,b.A,b.X,b.Y);return true;
    }

    private static int FillBcpsTextureComponent(Bitmap target,Bitmap source,bool[,] globalMask,
        IReadOnlyList<Point> component,IReadOnlyList<Point> donors,ref int evaluations)
    {
        var remaining=component.Select(p=>(p.X,p.Y)).ToHashSet();var changed=0;
        while(remaining.Count>0)
        {
            var frontier=new List<(int X,int Y)>();
            foreach(var p in remaining)
            {
                var supported=false;
                for(var oy=-1;oy<=1&&!supported;oy++)for(var ox=-1;ox<=1;ox++)
                {
                    if(ox==0&&oy==0)continue;var xx=p.X+ox;var yy=p.Y+oy;
                    if(xx>=0&&xx<source.Width&&yy>=0&&yy<source.Height&&!remaining.Contains((xx,yy)))
                    {supported=true;break;}
                }
                if(supported)frontier.Add(p);
            }
            if(frontier.Count==0)break;
            foreach(var p in frontier.OrderBy(p=>p.Y).ThenBy(p=>p.X))
            {
                var neighbors=new List<Color>();
                for(var oy=-1;oy<=1;oy++)for(var ox=-1;ox<=1;ox++)
                {
                    if(ox==0&&oy==0)continue;var xx=p.X+ox;var yy=p.Y+oy;
                    if(xx<0||xx>=source.Width||yy<0||yy>=source.Height||remaining.Contains((xx,yy)))continue;
                    neighbors.Add(target.GetPixel(xx,yy));
                }
                if(neighbors.Count==0)continue;
                var reference=Color.FromArgb((int)neighbors.Average(c=>c.R),(int)neighbors.Average(c=>c.G),(int)neighbors.Average(c=>c.B));
                var best=source.GetPixel(donors[0].X,donors[0].Y);var bestCost=int.MaxValue;
                foreach(var donor in donors)
                {
                    var c=source.GetPixel(donor.X,donor.Y);evaluations++;
                    var cost=Math.Abs(c.R-reference.R)+Math.Abs(c.G-reference.G)+Math.Abs(c.B-reference.B);
                    if(cost<bestCost){bestCost=cost;best=c;}
                }
                if(target.GetPixel(p.X,p.Y).ToArgb()!=best.ToArgb()){target.SetPixel(p.X,p.Y,best);changed++;}
                remaining.Remove(p);
            }
        }
        return changed;
    }

    private static void FinalizeBcpsAudits(string output,IReadOnlyList<BcpsComponentAudit> audits)
    {
        var pairId=Path.GetFileName(output.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar));
        WriteCsvRows(Path.Combine(output,"BCPS-PATCH-TRACE.csv"),
            ["PairId","BlockId","ComponentId","Bounds","MaskPixels","SurfaceClass","Route","DonorPixels","SearchMargin","CandidateEvaluations","ChangedPixels","Fallback","MaskPixelsBefore","MaskPixelsAfter"],
            audits.Select(a=>new[]{pairId,a.BlockId,I(a.ComponentId),R(a.Bounds),I(a.MaskPixels),a.SurfaceClass,a.Route,
                I(a.DonorPixels),I(a.SearchMargin),I(a.CandidateEvaluations),I(a.ChangedPixels),a.Fallback,I(a.MaskPixelsBefore),I(a.MaskPixelsAfter)}));
        WriteCsvRows(Path.Combine(output,"BCPS-CONFIDENCE.csv"),
            ["PairId","BlockId","ComponentId","DonorVariance","Confidence","Route","Fallback"],
            audits.Select(a=>new[]{pairId,a.BlockId,I(a.ComponentId),F(a.DonorVariance),F(a.Confidence),a.Route,a.Fallback}));
        WriteCsvRows(Path.Combine(output,"BCPS-EDGE-BARRIER.csv"),
            ["PairId","BlockId","ComponentId","DonorPixels","EdgeBarrierRejects","BarrierPolicy"],
            audits.Select(a=>new[]{pairId,a.BlockId,I(a.ComponentId),I(a.DonorPixels),I(a.EdgeBarrierRejects),"LOCAL_STRONG_EDGE_DONOR_REJECT"}));
        WriteCsvRows(Path.Combine(output,"BCPS-TIMING.csv"),
            ["PairId","BlockId","ComponentCount","ElapsedMs","ChangedPixels"],
            audits.GroupBy(a=>a.BlockId,StringComparer.Ordinal).Select(g=>new[]{pairId,g.Key,I(g.Count()),F(g.Sum(x=>x.ElapsedMs)),I(g.Sum(x=>x.ChangedPixels))}));
    }

    private static bool ShouldPreserveLowContrastScenePanel(VisualBlock block,RenderPlanR2 plan,SourceStyleEvidenceR2 style)
    {
        var bounds=Rectangle.Round(block.Bounds);
        return block.Lines.Count>=5&&bounds.Height>=220&&plan.VisualRole==SourceVisualRoleR2.Body&&
            style.ForegroundOccupancy<=.15f&&style.ContrastRatio<=1.35f&&style.BackgroundConfidence>=.62f;
    }

    private sealed record PlanarSurface(double R0,double Rx,double Ry,double G0,double Gx,double Gy,
        double B0,double Bx,double By)
    {
        public Color At(int x,int y)=>Color.FromArgb(
            Math.Clamp((int)Math.Round(R0+Rx*x+Ry*y),0,255),
            Math.Clamp((int)Math.Round(G0+Gx*x+Gy*y),0,255),
            Math.Clamp((int)Math.Round(B0+Bx*x+By*y),0,255));
    }

    private static bool TryFitPlanarSurface(Bitmap source,bool[,] exclusion,Rectangle bounds,Color fallback,
        out PlanarSurface surface,out double medianResidual,out double p90Residual)
    {
        var domain=Clamp(Rectangle.Inflate(bounds,8,8),source.Size);
        var samples=new List<(double X,double Y,Color C)>();
        var step=Math.Max(1,Math.Min(domain.Width,domain.Height)/80);
        for(var y=domain.Top;y<domain.Bottom;y+=step)for(var x=domain.Left;x<domain.Right;x+=step)
        {
            if(exclusion[x,y])continue;
            var c=source.GetPixel(x,y);
            var distance=Math.Abs(c.R-fallback.R)+Math.Abs(c.G-fallback.G)+Math.Abs(c.B-fallback.B);
            if(distance<=150)samples.Add((x,y,c));
        }
        if(samples.Count<32)
        {
            surface=new(fallback.R,0,0,fallback.G,0,0,fallback.B,0,0);
            medianResidual=p90Residual=-1;return false;
        }
        var mx=samples.Average(s=>s.X);var my=samples.Average(s=>s.Y);
        var sxx=samples.Sum(s=>(s.X-mx)*(s.X-mx));var syy=samples.Sum(s=>(s.Y-my)*(s.Y-my));
        var sxy=samples.Sum(s=>(s.X-mx)*(s.Y-my));var determinant=sxx*syy-sxy*sxy;
        if(Math.Abs(determinant)<1)
        {
            surface=new(fallback.R,0,0,fallback.G,0,0,fallback.B,0,0);
            medianResidual=p90Residual=-1;return false;
        }
        (double A,double X,double Y) Fit(Func<Color,double> channel)
        {
            var mz=samples.Average(s=>channel(s.C));
            var sxz=samples.Sum(s=>(s.X-mx)*(channel(s.C)-mz));
            var syz=samples.Sum(s=>(s.Y-my)*(channel(s.C)-mz));
            var bx=(sxz*syy-syz*sxy)/determinant;var by=(syz*sxx-sxz*sxy)/determinant;
            return(mz-bx*mx-by*my,bx,by);
        }
        var r=Fit(c=>c.R);var g=Fit(c=>c.G);var b=Fit(c=>c.B);
        surface=new(r.A,r.X,r.Y,g.A,g.X,g.Y,b.A,b.X,b.Y);
        var fittedSurface=surface;
        var residuals=samples.Select(sample=>
        {
            var predicted=fittedSurface.At((int)sample.X,(int)sample.Y);
            return (double)(Math.Abs(predicted.R-sample.C.R)+Math.Abs(predicted.G-sample.C.G)+Math.Abs(predicted.B-sample.C.B));
        }).OrderBy(value=>value).ToArray();
        medianResidual=residuals[residuals.Length/2];
        p90Residual=residuals[(int)Math.Floor((residuals.Length-1)*.90)];
        return true;
    }

    private static bool IsLocallySmooth(Bitmap source,bool[,] exclusion,Rectangle bounds)
    {
        var domain=Rectangle.Intersect(new Rectangle(0,0,source.Width,source.Height),Rectangle.Inflate(bounds,7,7));
        var samples=new List<Color>();var step=Math.Max(1,Math.Min(domain.Width,domain.Height)/32);
        for(var y=domain.Top;y<domain.Bottom;y+=step)for(var x=domain.Left;x<domain.Right;x+=step)if(!exclusion[x,y])samples.Add(source.GetPixel(x,y));
        if(samples.Count<12)return false;
        int Median(Func<Color,int> f)=>samples.Select(f).OrderBy(v=>v).ElementAt(samples.Count/2);
        var center=Color.FromArgb(Median(c=>c.R),Median(c=>c.G),Median(c=>c.B));
        var centerLuma=.2126*center.R+.7152*center.G+.0722*center.B;
        var deltas=samples.Select(c=>Math.Abs(c.R-center.R)+Math.Abs(c.G-center.G)+Math.Abs(c.B-center.B)).OrderBy(v=>v).ToArray();
        var edges=0;var discontinuities=0;
        for(var y=domain.Top;y<domain.Bottom;y+=2)for(var x=domain.Left;x<domain.Right;x+=2)
        {
            if(exclusion[x,y])continue;var a=source.GetPixel(x,y);
            if(x+2<domain.Right&&!exclusion[x+2,y]){var b=source.GetPixel(x+2,y);edges++;if(Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B)>78)discontinuities++;}
            if(y+2<domain.Bottom&&!exclusion[x,y+2]){var b=source.GetPixel(x,y+2);edges++;if(Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B)>78)discontinuities++;}
        }
        return centerLuma>=150&&deltas.Average()<72&&deltas[(int)((deltas.Length-1)*.90)]<150&&
               discontinuities/(float)Math.Max(1,edges)<.045f;
    }

    private static Bitmap BoundaryDiffusionInpaint(Bitmap source,bool[,] exclusion,Rectangle bounds,Color fallback)
    {
        var result=new Bitmap(source);
        var domain=Rectangle.Intersect(new Rectangle(0,0,source.Width,source.Height),Rectangle.Inflate(bounds,7,7));
        var pending=new bool[domain.Width,domain.Height];var remaining=0;
        for(var y=domain.Top;y<domain.Bottom;y++)for(var x=domain.Left;x<domain.Right;x++)if(exclusion[x,y]){pending[x-domain.Left,y-domain.Top]=true;remaining++;}
        var frontier=new List<(int X,int Y,Color Color)>();
        while(remaining>0)
        {
            frontier.Clear();
            for(var y=domain.Top;y<domain.Bottom;y++)for(var x=domain.Left;x<domain.Right;x++)
            {
                if(!pending[x-domain.Left,y-domain.Top])continue;
                var rr=0d;var gg=0d;var bb=0d;var weight=0d;
                for(var oy=-1;oy<=1;oy++)for(var ox=-1;ox<=1;ox++)
                {
                    if(ox==0&&oy==0)continue;var xx=x+ox;var yy=y+oy;
                    if(xx<domain.Left||xx>=domain.Right||yy<domain.Top||yy>=domain.Bottom||pending[xx-domain.Left,yy-domain.Top])continue;
                    var w=ox==0||oy==0?1d:.70710678d;var c=result.GetPixel(xx,yy);rr+=c.R*w;gg+=c.G*w;bb+=c.B*w;weight+=w;
                }
                if(weight>0)frontier.Add((x,y,Color.FromArgb((int)Math.Round(rr/weight),(int)Math.Round(gg/weight),(int)Math.Round(bb/weight))));
            }
            if(frontier.Count==0)break;
            foreach(var p in frontier){result.SetPixel(p.X,p.Y,p.Color);pending[p.X-domain.Left,p.Y-domain.Top]=false;remaining--;}
        }
        if(remaining>0)for(var y=domain.Top;y<domain.Bottom;y++)for(var x=domain.Left;x<domain.Right;x++)if(pending[x-domain.Left,y-domain.Top])result.SetPixel(x,y,fallback);
        // Wave-front propagation is deterministic but can leave Voronoi-like facets
        // inside wide outlined glyphs.  Relax only the excluded pixels against the
        // immutable surrounding boundary; this produces a harmonic, glyph-local fill
        // without granting any new full-rectangle mutation authority.
        var width=domain.Width;var height=domain.Height;
        var red=new float[width*height];var green=new float[width*height];var blue=new float[width*height];
        var nextRed=new float[width*height];var nextGreen=new float[width*height];var nextBlue=new float[width*height];
        var relaxedPixels=new List<(int X,int Y,int Index)>();
        for(var yy=0;yy<height;yy++)for(var xx=0;xx<width;xx++)
        {
            var imageX=domain.Left+xx;var imageY=domain.Top+yy;
            var c=result.GetPixel(imageX,imageY);var index=yy*width+xx;
            red[index]=nextRed[index]=c.R;green[index]=nextGreen[index]=c.G;blue[index]=nextBlue[index]=c.B;
            if(exclusion[imageX,imageY])relaxedPixels.Add((xx,yy,index));
        }
        var relaxIterations=Math.Clamp(Math.Max(bounds.Width,bounds.Height)/8,12,36);
        for(var iteration=0;iteration<relaxIterations;iteration++)
        {
            foreach(var (xx,yy,index) in relaxedPixels)
            {
                var count=0;var rr=0f;var gg=0f;var bb=0f;
                if(xx>0){var ni=index-1;rr+=red[ni];gg+=green[ni];bb+=blue[ni];count++;}
                if(xx+1<width){var ni=index+1;rr+=red[ni];gg+=green[ni];bb+=blue[ni];count++;}
                if(yy>0){var ni=index-width;rr+=red[ni];gg+=green[ni];bb+=blue[ni];count++;}
                if(yy+1<height){var ni=index+width;rr+=red[ni];gg+=green[ni];bb+=blue[ni];count++;}
                if(count>0){nextRed[index]=rr/count;nextGreen[index]=gg/count;nextBlue[index]=bb/count;}
            }
            (red,nextRed)=(nextRed,red);(green,nextGreen)=(nextGreen,green);(blue,nextBlue)=(nextBlue,blue);
        }
        foreach(var (xx,yy,index) in relaxedPixels)
        {
            var imageX=domain.Left+xx;var imageY=domain.Top+yy;
            result.SetPixel(imageX,imageY,Color.FromArgb(Math.Clamp((int)MathF.Round(red[index]),0,255),
                Math.Clamp((int)MathF.Round(green[index]),0,255),Math.Clamp((int)MathF.Round(blue[index]),0,255)));
        }
        return result;
    }

    private static bool[,] DilateMask(bool[,] source, Rectangle bounds, int radius)
    {
        var result=(bool[,])source.Clone();
        var width=source.GetLength(0);var height=source.GetLength(1);
        var scan=Rectangle.Intersect(new Rectangle(0,0,width,height),Rectangle.Inflate(bounds,radius,radius));
        for(var y=bounds.Top;y<bounds.Bottom;y++)for(var x=bounds.Left;x<bounds.Right;x++)if(source[x,y])
            for(var yy=Math.Max(scan.Top,y-radius);yy<=Math.Min(scan.Bottom-1,y+radius);yy++)
            for(var xx=Math.Max(scan.Left,x-radius);xx<=Math.Min(scan.Right-1,x+radius);xx++)
                if((xx-x)*(xx-x)+(yy-y)*(yy-y)<=radius*radius)result[xx,yy]=true;
        return result;
    }

    private static Color LocalContinuation(Bitmap image,bool[,] mask,int x,int y,int radius,Color fallback)
    {
        Point? left=null,right=null,top=null,bottom=null;
        for(var d=1;d<=radius&&(left is null||right is null||top is null||bottom is null);d++)
        { if(left is null&&x-d>=0&&!mask[x-d,y])left=new(x-d,y); if(right is null&&x+d<image.Width&&!mask[x+d,y])right=new(x+d,y);
          if(top is null&&y-d>=0&&!mask[x,y-d])top=new(x,y-d); if(bottom is null&&y+d<image.Height&&!mask[x,y+d])bottom=new(x,y+d); }
        static Color Mix(Color a,Color b,float t)=>Color.FromArgb((int)(a.R*(1-t)+b.R*t),(int)(a.G*(1-t)+b.G*t),(int)(a.B*(1-t)+b.B*t));
        Color Axis(Point? a,Point? b,bool horizontal){if(a is { } p&&b is { } q)return Mix(image.GetPixel(p.X,p.Y),image.GetPixel(q.X,q.Y),horizontal?(x-p.X)/(float)Math.Max(1,q.X-p.X):(y-p.Y)/(float)Math.Max(1,q.Y-p.Y));if(a is { } one)return image.GetPixel(one.X,one.Y);if(b is { } two)return image.GetPixel(two.X,two.Y);return fallback;}
        var h=Axis(left,right,true);var v=Axis(top,bottom,false);
        if(left is not null&&right is not null&&top is not null&&bottom is not null)return Mix(h,v,.5f);
        return left is not null||right is not null?h:v;
    }

    private static Color LocalHorizontalContinuation(Bitmap image,bool[,] mask,int x,int y,int radius,Color fallback)
    {
        Point? left=null,right=null;
        for(var d=1;d<=radius&&(left is null||right is null);d++)
        {
            if(left is null&&x-d>=0&&!mask[x-d,y])left=new(x-d,y);
            if(right is null&&x+d<image.Width&&!mask[x+d,y])right=new(x+d,y);
        }
        if(left is { } p&&right is { } q)
        {
            var t=(x-p.X)/(float)Math.Max(1,q.X-p.X);var a=image.GetPixel(p.X,p.Y);var b=image.GetPixel(q.X,q.Y);
            return Color.FromArgb((int)(a.R*(1-t)+b.R*t),(int)(a.G*(1-t)+b.G*t),(int)(a.B*(1-t)+b.B*t));
        }
        if(left is { } l)return image.GetPixel(l.X,l.Y);
        if(right is { } r)return image.GetPixel(r.X,r.Y);
        return fallback;
    }

    private static Rectangle MaskBounds(bool[,] mask, Size size)
    {int l=size.Width,t=size.Height,r=-1,b=-1;for(var y=0;y<size.Height;y++)for(var x=0;x<size.Width;x++)if(mask[x,y]){l=Math.Min(l,x);t=Math.Min(t,y);r=Math.Max(r,x);b=Math.Max(b,y);}return r<l?Rectangle.Empty:Rectangle.FromLTRB(l,t,r+1,b+1);}
    private static void MergeMask(bool[,] target,bool[,] source,Rectangle bounds){for(var y=bounds.Top;y<bounds.Bottom;y++)for(var x=bounds.Left;x<bounds.Right;x++)if(source[x,y])target[x,y]=true;}
    private static void SaveBinaryMap(bool[,] map,string path){using var image=new Bitmap(map.GetLength(0),map.GetLength(1),PixelFormat.Format32bppArgb);for(var y=0;y<image.Height;y++)for(var x=0;x<image.Width;x++)image.SetPixel(x,y,map[x,y]?Color.White:Color.Black);image.Save(path);}
    private static void Fill(bool[,] map,Rectangle b){for(var y=b.Top;y<b.Bottom;y++)for(var x=b.Left;x<b.Right;x++)map[x,y]=true;}
    private static void FillPolygon(bool[,] map,PointF[] polygon,Size size){var b=Clamp(Rectangle.Round(Bounds(polygon)),size);using var temp=new Bitmap(size.Width,size.Height);using(var g=Graphics.FromImage(temp)){g.Clear(Color.Black);using var brush=new SolidBrush(Color.White);g.FillPolygon(brush,polygon);}for(var y=b.Top;y<b.Bottom;y++)for(var x=b.Left;x<b.Right;x++)if(temp.GetPixel(x,y).R>127)map[x,y]=true;}
    private static Color RobustColor(IEnumerable<Color> input){var a=input.ToArray();if(a.Length==0)return Color.Black;var dominant=a.GroupBy(c=>(c.R/16,c.G/16,c.B/16)).OrderByDescending(g=>g.Count()).First().ToArray();int M(Func<Color,int> f)=>dominant.Select(f).OrderBy(v=>v).ElementAt(dominant.Length/2);return Color.FromArgb(M(c=>c.R),M(c=>c.G),M(c=>c.B));}

    private static void SaveOverlay(Bitmap source, string path, IEnumerable<(PointF[] Polygon, string Label, Color Color)> values)
    {
        using var image = new Bitmap(source); using var g = Graphics.FromImage(image);
        using var font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold, GraphicsUnit.Pixel);
        foreach (var value in values)
        {
            using var pen = new Pen(value.Color, 2); g.DrawPolygon(pen, value.Polygon);
            var b = Bounds(value.Polygon); using var back = new SolidBrush(Color.FromArgb(190, Color.Black));
            var size = g.MeasureString(value.Label, font); g.FillRectangle(back, b.Left, Math.Max(0, b.Top - size.Height), size.Width, size.Height);
            using var brush = new SolidBrush(value.Color); g.DrawString(value.Label, font, brush, b.Left, Math.Max(0, b.Top - size.Height));
        }
        image.Save(path);
    }

    private static Color SampleBorderColor(Bitmap image, Rectangle rect)
    {
        rect = Clamp(rect, image.Size); var samples = new List<Color>();
        for (var x = rect.Left; x < rect.Right; x += Math.Max(1, rect.Width / 12))
        { samples.Add(image.GetPixel(x, rect.Top)); samples.Add(image.GetPixel(x, Math.Max(rect.Top, rect.Bottom - 1))); }
        for (var y = rect.Top; y < rect.Bottom; y += Math.Max(1, rect.Height / 8))
        { samples.Add(image.GetPixel(rect.Left, y)); samples.Add(image.GetPixel(Math.Max(rect.Left, rect.Right - 1), y)); }
        return Color.FromArgb(255, (int)samples.Average(x => x.R), (int)samples.Average(x => x.G), (int)samples.Average(x => x.B));
    }
    private static Rectangle Clamp(Rectangle r, Size size) => Rectangle.Intersect(new(0, 0, size.Width, size.Height), r);
    private static PointF[] RectanglePolygon(RectangleF r) => [new(r.Left, r.Top), new(r.Right, r.Top), new(r.Right, r.Bottom), new(r.Left, r.Bottom)];
    private static RectangleF Bounds(PointF[] p) => RectangleF.FromLTRB(p.Min(x => x.X), p.Min(x => x.Y), p.Max(x => x.X), p.Max(x => x.Y));
    private static object Rect(RectangleF r) => new { r.X, r.Y, r.Width, r.Height };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}

internal static class CorePipelineStatistics
{
    public static float Median(this IEnumerable<float> source)
    { var a = source.Order().ToArray(); return a.Length == 0 ? 0 : a[a.Length / 2]; }
}
