using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ScreenshotTranslationUiTester;

public enum RegionRenderStatus { Rendered, PreservedOriginal, FallbackOverlay, Failed }
public sealed record SourceLineRenderDecision(string SourceLineId, bool Masked, string Decision);
public sealed record RenderedLine(string Text, RectangleF Bounds);
public sealed record RegionRenderDiagnostic(string RegionId, RegionRoleType Role, int SourceLineCount,
    int TranslatedLength, RegionRenderStatus RenderStatus, float FontSize, int LineCount,
    long MaskTime, long BackgroundRestoreTime, long LayoutTime, long DrawTime,
    string FallbackReason, IReadOnlyList<SourceLineRenderDecision> Coverage,
    IReadOnlyList<RenderedLine> RenderedLines,string BackgroundStrategy="Automatic",string BackgroundColor="#000000",int Alpha=0,string FontFamily="",RectangleF RenderBounds=default,RectangleF? TranslationRenderRect=null,int BlurRadius=0,
    string TextColor="#FFFFFFFF",double TextContrast=0,string TextColorReason="",double BackgroundVariance=0,
    int TextDrawCount=0,int DecorationDrawCount=0,bool AtomicRegionCommitted=false,
    string DecisionStage="",string DecisionCode="",string TranslationUnitId="",
    float SourceTextStartX=0,float SourceTextStartY=0,float RenderTextStartX=0,float RenderTextStartY=0,
    float AnchorDeltaX=0,float AnchorDeltaY=0,string Alignment="",string ExpansionDirection="None",string BackgroundSurfaceType="Unknown",
    bool FallbackAttempted=false,bool FallbackRendered=false,string NormalRenderFailureReason="",string FallbackFailureReason="",RectangleF LayoutTarget=default,
    RectangleF SourceGlyphBounds=default,RectangleF OwnedSurfaceBounds=default,RectangleF FinalRenderBounds=default,
    RectangleF AtomicCommitBounds=default,IReadOnlyList<RectangleF>? ReservedRegions=null,
    string SourceStyleOwner="",string SourceFillColor="",string SourceOutlineColor="",float SourceOutlineWidth=0,
    string SourceShadowColor="",float SourceConfidence=0,string SourcePolarity="",double CombinedReadability=0,
    string StyleAdjustment="",bool FillPolarityChanged=false,long StyleBundleTicks=0);

public static class RegionRendererV2
{
    public static RegionRenderResult Render(Bitmap source, RecognitionDocumentV2 document, RenderSettings settings)
    {
        var plan=VisualClusterRenderPlannerV2.Plan(document,source.Size);
        var rendered=Render(source,plan.Regions,settings);
        if(!plan.SourceAligned)return rendered;
        var warnings=rendered.Warnings.Concat(plan.Diagnostics.Where(x=>x.Outcome is VisualClusterRenderOutcome.TRANSLATION_ALLOCATION_FAILED or VisualClusterRenderOutcome.TRANSLATION_ALLOCATION_SPANS_MULTIPLE_CLUSTERS)
            .Select(x=>new RegionRenderWarning(string.IsNullOrWhiteSpace(x.VisualClusterId)?x.TranslationUnitId:x.VisualClusterId,x.Outcome+": "+x.Reason))).ToArray();
        var failed=plan.Diagnostics.Where(x=>x.Outcome is VisualClusterRenderOutcome.TRANSLATION_ALLOCATION_FAILED or VisualClusterRenderOutcome.TRANSLATION_ALLOCATION_SPANS_MULTIPLE_CLUSTERS)
            .Select(x=>new RegionRenderDiagnostic(string.IsNullOrWhiteSpace(x.VisualClusterId)?x.TranslationUnitId:x.VisualClusterId,RegionRoleType.Unknown,x.SourceIds.Count,0,RegionRenderStatus.PreservedOriginal,0,0,0,0,0,0,x.Reason,
                x.SourceIds.Select(id=>new SourceLineRenderDecision(id,false,"PRESERVE_ORIGINAL")).ToArray(),[],DecisionStage:"VisualClusterAllocation",DecisionCode:x.Outcome.ToString(),TranslationUnitId:x.TranslationUnitId,
                SourceGlyphBounds:x.SourceGlyphBounds,OwnedSurfaceBounds:x.OwnedSurfaceBounds,FinalRenderBounds:RectangleF.Empty,AtomicCommitBounds:RectangleF.Empty)).ToArray();
        return rendered with { Warnings=warnings, Diagnostics=(rendered.Diagnostics??[]).Concat(failed).ToArray() };
    }
    public static RegionRenderResult Render(Bitmap source, IReadOnlyList<RecognitionRegion> regions, RenderSettings settings)
    {
        var resourceProbe=new RenderResourceProbe();
        var total = Stopwatch.StartNew();
        var contextStarted=Stopwatch.GetTimestamp();
        var typographyContext=TypographyContextPlanner.Plan(regions,settings);
        var contextTicks=Stopwatch.GetTimestamp()-contextStarted;
        var output = settings.DiagnosticMaskOnly
            ? new Bitmap(source.Width, source.Height, source.PixelFormat)
            : new Bitmap(source);
        var warnings = new List<RegionRenderWarning>();
        var diagnostics = new List<RegionRenderDiagnostic>();
        var renderRects=new Dictionary<string,RectangleF>(StringComparer.Ordinal);
        // Context regions may repeat source lines, but only one visible region may own them.
        // Select the narrowest, highest-confidence display region before drawing anything.
        var visibleOwners=regions.Where(x=>!x.IsIgnored&&!string.IsNullOrWhiteSpace(x.TranslationText))
            .SelectMany(r=>r.SourceBlockIds.Select(id=>(id,region:r)))
            .GroupBy(x=>x.id,StringComparer.Ordinal)
            .ToDictionary(g=>g.Key,g=>g.OrderBy(x=>x.region.SourceBlockIds.Count)
                .ThenByDescending(x=>x.region.RoleConfidence).ThenBy(x=>x.region.ReadingOrder).First().region.RegionId,StringComparer.Ordinal);
        var fullBitmapCloneCount=settings.DiagnosticMaskOnly?0:1;var fullBitmapBlitCount=0;
        var regionBitmapAllocationCount=0;long temporaryBitmapBytes=0;
        long maskTicks = 0, layoutTicks = 0, drawTicks = 0, normalRenderTicks=0, fallbackRenderTicks=0;var fallbackRegionCount=0;
        using var g = Graphics.FromImage(output);
        g.TextRenderingHint = settings.LegacyTextRasterizationForDiagnostics
            ? TextRenderingHint.ClearTypeGridFit
            : TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.Default;

        foreach (var region in regions.Where(x => !x.IsIgnored))
        {
            string? choiceTraceDir=null;Bitmap? choiceAfterCleanup=null;string? choicePreHash=null;string? choicePostHash=null;int choiceMaskCount=0;int choiceModified=0;string choicePolicy="";
            var traceChoice=region.RoleType==RegionRoleType.Choice&&(region.StructuredText.Contains("How did you know my",StringComparison.OrdinalIgnoreCase)||region.OcrText.Contains("How did you know my",StringComparison.OrdinalIgnoreCase));
            if(traceChoice){choiceTraceDir=RealPathDiagnosticTrace.ChoiceDir(region.RegionId);using var snap=new Bitmap(output);snap.Save(Path.Combine(choiceTraceDir,"before-cleanup.png"));choicePreHash=RealPathDiagnosticTrace.Hash(snap);}
            var conflictingOwner=region.SourceBlockIds.FirstOrDefault(id=>visibleOwners.TryGetValue(id,out var owner)&&owner!=region.RegionId);
            if(conflictingOwner is not null)
            {
                diagnostics.Add(Failed(region,"RenderOwnership","DuplicateVisibleRenderOwner",
                    $"SourceId {conflictingOwner} is visibly owned by {visibleOwners[conflictingOwner]}"));
                continue;
            }
            var sourceText=string.IsNullOrWhiteSpace(region.StructuredText)?region.OcrText:region.StructuredText;
            static string VisibleText(string value)=>string.Concat(value.Where(c=>!char.IsWhiteSpace(c)));
            if(!string.IsNullOrWhiteSpace(region.TranslationText)&&string.Equals(VisibleText(sourceText),VisibleText(region.TranslationText),StringComparison.Ordinal))
            {diagnostics.Add(NoOpPreserved(region));continue;}
            if (string.IsNullOrWhiteSpace(region.TranslationText))
            {
                diagnostics.Add(Preserved(region));
                continue;
            }
            // Keep the accepted Card authorization path frozen. Decorative character names are
            // the narrow exception: they must attempt their own source-bound normal reconstruction
            // so a profile name does not become a generic corner label.
            if(region.PreserveOriginal&&region.RoleType!=RegionRoleType.CharacterName)
            {
                var fb=ReadableFallback(source,output,g,region,region.BoundingBox,regions,settings,"Normal authorization requested PreserveOriginal");
                fallbackRenderTicks+=fb.ElapsedTicks;fallbackRegionCount++;regionBitmapAllocationCount+=fb.RoiAllocations;temporaryBitmapBytes+=fb.TemporaryBytes;diagnostics.Add(fb.Diagnostic);if(fb.Diagnostic.AtomicRegionCommitted)renderRects[region.RegionId]=fb.Diagnostic.RenderBounds;continue;
            }
            if (!region.CoverageValid || region.SourceBlockIds.Count != region.SourceLinePolygons.Count ||
                region.SourceLinePolygons.Any(x => !RecognitionCoverageFinalizer.ValidPolygon(x, source.Size)))
            {
                warnings.Add(new(region.RegionId, "译图部分区域无法安全覆盖，已保留原文。"));
                diagnostics.Add(Failed(region, "Coverage", "CoverageIncomplete", region.CoverageFailureReason));
                continue;
            }

            var safeTarget = SafeLayoutTargetPlanner.Plan(region);
            if (safeTarget.Status != SafeLayoutTargetStatus.Safe || safeTarget.Targets.Count != 1)
            {
                var fb=ReadableFallback(source,output,g,region,region.BoundingBox,regions,settings,"UnsafeStructureTarget: "+safeTarget.Reason);
                fallbackRenderTicks+=fb.ElapsedTicks;fallbackRegionCount++;regionBitmapAllocationCount+=fb.RoiAllocations;temporaryBitmapBytes+=fb.TemporaryBytes;
                if(fb.Diagnostic is { } fd){diagnostics.Add(fd);if(fd.AtomicRegionCommitted)renderRects[region.RegionId]=fd.RenderBounds;}
                continue;
            }

            var target = RectangleF.Intersect(new(PointF.Empty, source.Size), safeTarget.Targets[0]);
            target=ConstrainIndependentHeaderAnchor(target,region,regions);
            target=ResolveLocalSiblingCollision(target,region,regions,renderRects,source.Size);
            if (target.Width < 3 || target.Height < 3 || region.Polygon.Length < 3)
            {
                var fb=ReadableFallback(source,output,g,region,region.BoundingBox,regions,settings,"EmptyTarget: Renderer target is empty");
                fallbackRenderTicks+=fb.ElapsedTicks;fallbackRegionCount++;regionBitmapAllocationCount+=fb.RoiAllocations;temporaryBitmapBytes+=fb.TemporaryBytes;diagnostics.Add(fb.Diagnostic);
                continue;
            }
            if(region.TranslationText.Contains('\uFFFD')||HasInvalidSurrogate(region.TranslationText))
            {
                warnings.Add(new(region.RegionId,"Translation contains invalid or replacement glyph data; original pixels preserved."));
                diagnostics.Add(Failed(region,"TranslationIntegrity","TranslationGlyphIntegrityFailed","Invalid surrogate or replacement glyph"));continue;
            }

            var layoutStart = Stopwatch.GetTimestamp();
            var layout = TranslatedTextLayoutEngine.Fit(g, region, target, regions, source.Size, settings,typographyContext);
            layoutTicks += Stopwatch.GetTimestamp() - layoutStart;
            if (layout is null)
            {
                var fb=ReadableFallback(source,output,g,region,target,regions,settings,"NoSafeLayout: Translation cannot fit without content changes");
                fallbackRenderTicks+=fb.ElapsedTicks;fallbackRegionCount++;regionBitmapAllocationCount+=fb.RoiAllocations;temporaryBitmapBytes+=fb.TemporaryBytes;diagnostics.Add(fb.Diagnostic);if(fb.Diagnostic.AtomicRegionCommitted)renderRects[region.RegionId]=fb.Diagnostic.RenderBounds;
                continue;
            }
            if(!TextIntegrityMatches(region.TranslationText,layout.Lines))
            {
                layout.Font.Dispose();warnings.Add(new(region.RegionId,"Translation integrity validation failed; original pixels preserved."));
                diagnostics.Add(Failed(region,"TranslationIntegrity","TranslationIntegrityMismatch","Layout text differs from FullTranslation"));continue;
            }

            long regionMaskTicks = 0, regionRestoreTicks = 0;
            var coverage = new List<SourceLineRenderDecision>();
            var complex = false;
            var planStarted=Stopwatch.GetTimestamp();
            var backgroundPlan=BackgroundIntegrationPlanner.Plan(source,region,target,regions);
            if(traceChoice&&choiceTraceDir is not null){RealPathDiagnosticTrace.SaveMask(backgroundPlan.CleanupMask,Path.Combine(choiceTraceDir,"cleanup-mask.png"));choiceMaskCount=RealPathDiagnosticTrace.Count(backgroundPlan.CleanupMask);choicePolicy=$"SafeToCommit={backgroundPlan.SafeToCommit}; {backgroundPlan.BackgroundType}/{backgroundPlan.ReconstructionStrategy}; {backgroundPlan.FailureReason}";}
            regionMaskTicks+=Stopwatch.GetTimestamp()-planStarted;
            if(!backgroundPlan.SafeToCommit)
            {
                layout.Font.Dispose();var fb=ReadableFallback(source,output,g,region,target,regions,settings,"BackgroundUnsafe: "+backgroundPlan.FailureReason);
                fallbackRenderTicks+=fb.ElapsedTicks;fallbackRegionCount++;regionBitmapAllocationCount+=fb.RoiAllocations;temporaryBitmapBytes+=fb.TemporaryBytes;diagnostics.Add(fb.Diagnostic);if(fb.Diagnostic.AtomicRegionCommitted)renderRects[region.RegionId]=fb.Diagnostic.RenderBounds;continue;
            }
            if(region.RoleType is RegionRoleType.CharacterName or RegionRoleType.Species)
            {
                layout.Font.Dispose();var fb=ReadableFallback(source,output,g,region,target,regions,settings,"Decorative name requires opaque source-owned replacement");
                fallbackRenderTicks+=fb.ElapsedTicks;fallbackRegionCount++;regionBitmapAllocationCount+=fb.RoiAllocations;temporaryBitmapBytes+=fb.TemporaryBytes;diagnostics.Add(fb.Diagnostic);if(fb.Diagnostic.AtomicRegionCommitted)renderRects[region.RegionId]=fb.Diagnostic.RenderBounds;continue;
            }
            var planned=MeasureLines(g,layout);var tight=TightBounds(planned,source.Size,layout.Font.Size);
            var restoreStarted=Stopwatch.GetTimestamp();
            Bitmap? atomicCanvas=null;var atomicBounds=Rectangle.Empty;
            if(settings.DiagnosticMaskOnly)
            {
                using var white=new SolidBrush(Color.White);
                foreach(var polygon in backgroundPlan.SourceTextPolygons)g.FillPolygon(white,polygon);
            }
            else if(!settings.DiagnosticSkipCleanup)
            {
                atomicBounds=Rectangle.Intersect(Rectangle.Inflate(Rectangle.Round(RectangleF.Union(backgroundPlan.CleanupBounds,tight)),3,3),new(0,0,output.Width,output.Height));
                using var cleaned=BackgroundIntegrationExecutor.ExecuteRoi(source,backgroundPlan,atomicBounds);
                if(traceChoice&&choiceTraceDir is not null){choiceModified=cleaned.ChangedPixels;choiceAfterCleanup=new Bitmap(output);using(var ag=Graphics.FromImage(choiceAfterCleanup))ag.DrawImageUnscaled(cleaned.Bitmap,cleaned.Bounds.Location);choiceAfterCleanup.Save(Path.Combine(choiceTraceDir,"after-cleanup.png"));choicePostHash=RealPathDiagnosticTrace.Hash(choiceAfterCleanup);}
                if(!cleaned.Committed)
                {
                    layout.Font.Dispose();var fb=ReadableFallback(source,output,g,region,target,regions,settings,"Background reconstruction failed: "+cleaned.ValidationFailure);
                    fallbackRenderTicks+=fb.ElapsedTicks;fallbackRegionCount++;regionBitmapAllocationCount+=fb.RoiAllocations;temporaryBitmapBytes+=fb.TemporaryBytes;diagnostics.Add(fb.Diagnostic);if(fb.Diagnostic.AtomicRegionCommitted)renderRects[region.RegionId]=fb.Diagnostic.RenderBounds;continue;
                }
                atomicCanvas=new Bitmap(cleaned.Bitmap);atomicBounds=cleaned.Bounds;
                regionBitmapAllocationCount+=2;temporaryBitmapBytes+=(long)atomicBounds.Width*atomicBounds.Height*4*2;
            }
            else
            {
                atomicBounds=Rectangle.Intersect(Rectangle.Inflate(Rectangle.Round(tight),3,3),new(0,0,output.Width,output.Height));
                atomicCanvas=output.Clone(atomicBounds,output.PixelFormat);regionBitmapAllocationCount++;temporaryBitmapBytes+=(long)atomicBounds.Width*atomicBounds.Height*4;
            }
            regionRestoreTicks+=Stopwatch.GetTimestamp()-restoreStarted;
            complex=backgroundPlan.BackgroundType is BackgroundSurfaceType.Texture or BackgroundSurfaceType.ComplexImage;
            coverage.AddRange(region.SourceBlockIds.Select(id=>new SourceLineRenderDecision(id,backgroundPlan.SafeToCommit,
                backgroundPlan.SafeToCommit?$"{backgroundPlan.BackgroundType}/{backgroundPlan.ReconstructionStrategy}":backgroundPlan.FailureReason)));
            maskTicks += regionMaskTicks;

            var status = layout.Fallback ? RegionRenderStatus.FallbackOverlay : RegionRenderStatus.Rendered;
            var fallback = layout.FallbackReason;
            var local=BackgroundEstimator.Estimate(source,tight).Color;var panelColor=local;var opacity=0;
            if(settings.BackgroundStrategy==TranslationOverlayBackgroundStyle.LightOverlay){panelColor=Color.Black;opacity=settings.BackgroundOpacity;}
            else if(settings.BackgroundStrategy==TranslationOverlayBackgroundStyle.Solid){panelColor=settings.BackgroundColor;opacity=255;}
            else if(complex||layout.Fallback){if(complex&&fallback.Length==0)fallback="Complex background restored with local texture extension";status=layout.Fallback?RegionRenderStatus.FallbackOverlay:RegionRenderStatus.Rendered;}
            using var atomicGraphics=atomicCanvas is null?null:Graphics.FromImage(atomicCanvas);
            if(atomicGraphics is not null){ConfigureGraphics(atomicGraphics,settings);atomicGraphics.TranslateTransform(-atomicBounds.Left,-atomicBounds.Top);}
            var drawGraphics=atomicGraphics??g;
            if(opacity>0 && !settings.DiagnosticMaskOnly && !settings.DiagnosticSkipCleanup){using var panel=new SolidBrush(Color.FromArgb(opacity,panelColor));drawGraphics.FillRectangle(panel,tight);}

            var drawStart = Stopwatch.GetTimestamp();
            var sampleBitmap=atomicCanvas??output;
            var sampleBounds=atomicCanvas is null?tight:new RectangleF(tight.X-atomicBounds.X,tight.Y-atomicBounds.Y,tight.Width,tight.Height);
            var backgroundSample=BackgroundEstimator.Estimate(sampleBitmap,sampleBounds);
            var background=backgroundSample.Color;
            var sourceBundle=SourceStyleBundleOwner.Create(source,region);
            if(settings.DiagnosticUseLegacySourceTextColor)
                sourceBundle=sourceBundle with{FillColor=LegacyForeground(source,region,layout.Target),Owner="LegacyDiagnosticForeground"};
            sourceBundle=SourceStyleBundleOwner.WithExplicitSettings(sourceBundle,settings);
            if(settings.Outline)
            {
                var explicitOutline=settings.AutomaticOutlineColor?SourceStyleBundleOwner.Opposite(sourceBundle.FillColor):settings.OutlineColor;
                sourceBundle=sourceBundle with{OutlineColor=Color.FromArgb(220,explicitOutline),OutlineWidth=settings.StrokeWidth switch{RendererStrokeWidth.Medium=>2f,RendererStrokeWidth.None=>0f,_=>1f}};
            }
            if(settings.Shadow&&!sourceBundle.HasShadow)
                sourceBundle=sourceBundle with{ShadowColor=Color.FromArgb(150,Color.Black),ShadowOffset=new PointF(1,1)};
            var styleDecision=SourceStyleLegibilityGuard.Evaluate(sourceBundle,background);
            var effectiveStyle=styleDecision.Effective;
            var foreground=effectiveStyle.FillColor;
            var renderedLines = settings.DiagnosticSkipText || settings.DiagnosticMaskOnly ? MeasureLines(drawGraphics, layout) :
                DrawLines(drawGraphics, layout, effectiveStyle, settings, region.RoleType==RegionRoleType.CharacterName||settings.LegacyTextRasterizationForDiagnostics && complex);
            renderRects[region.RegionId]=tight;
            drawTicks += Stopwatch.GetTimestamp() - drawStart;
            if (coverage.Count != region.SourceBlockIds.Count || coverage.Any(x => !x.Masked))
            {
                status = RegionRenderStatus.Failed;
                warnings.Add(new(region.RegionId, "Renderer coverage incomplete."));
            }
            if(status!=RegionRenderStatus.Failed&&atomicCanvas is not null)g.DrawImageUnscaled(atomicCanvas,atomicBounds.Location);
            if(traceChoice&&choiceTraceDir is not null){using var finalSnap=new Bitmap(output);finalSnap.Save(Path.Combine(choiceTraceDir,"final.png"));RealPathDiagnosticTrace.WriteJson(Path.Combine(choiceTraceDir,"trace.json"),new{OCRText=region.OcrText,region.StructuredText,SourceIds=region.SourceBlockIds,Polygons=region.SourceLinePolygons,CleanupSourceIds=region.SourceSegments.Select(x=>x.BlockId).Distinct().ToArray(),MaskPixelCount=choiceMaskCount,ActualModifiedPixelCount=choiceModified,CleanupPolicy=choicePolicy,PreHash=choicePreHash,PostHash=choicePostHash,FinalHash=RealPathDiagnosticTrace.Hash(finalSnap),RegionId=region.RegionId,TranslationUnitId=region.TranslationUnitId});choiceAfterCleanup?.Dispose();}
            if (layout.Fallback) warnings.Add(new(region.RegionId, "译文使用安全 fallback；内容未截断。"));
            var sourceStart=SourceTextStart(region);var renderStart=renderedLines.Count>0?renderedLines[0].Bounds.Location:layout.Target.Location;
            diagnostics.Add(new(region.RegionId, region.RoleType, region.SourceBlockIds.Count,
                region.TranslationText.Length, status, layout.Font.Size, layout.Lines.Count,
                Ms(regionMaskTicks), Ms(regionRestoreTicks), Ms(Stopwatch.GetTimestamp() - layoutStart),
                Ms(Stopwatch.GetTimestamp() - drawStart), fallback, coverage, renderedLines,settings.BackgroundStrategy.ToString(),$"#{panelColor.R:X2}{panelColor.G:X2}{panelColor.B:X2}",opacity,layout.Font.Name,tight,tight,0,
                $"#{foreground.A:X2}{foreground.R:X2}{foreground.G:X2}{foreground.B:X2}",styleDecision.CombinedReadability,styleDecision.Adjustment,0,
                settings.DiagnosticSkipText||settings.DiagnosticMaskOnly?0:1,0,true,"AtomicCommit","Rendered",region.TranslationUnitId,
                sourceStart.X,sourceStart.Y,renderStart.X,renderStart.Y,renderStart.X-sourceStart.X,renderStart.Y-sourceStart.Y,
                layout.Alignment.ToString(),layout.ExpansionDirection,backgroundPlan.BackgroundType.ToString(),LayoutTarget:layout.Target,
                SourceGlyphBounds:backgroundPlan.CleanupBounds,OwnedSurfaceBounds:layout.Target,FinalRenderBounds:tight,
                AtomicCommitBounds:atomicBounds,SourceStyleOwner:sourceBundle.Owner,
                SourceFillColor:SourceStyleBundleOwner.Hex(sourceBundle.FillColor),SourceOutlineColor:SourceStyleBundleOwner.Hex(sourceBundle.OutlineColor),SourceOutlineWidth:sourceBundle.OutlineWidth,
                SourceShadowColor:SourceStyleBundleOwner.Hex(sourceBundle.ShadowColor),SourceConfidence:sourceBundle.Confidence,SourcePolarity:sourceBundle.Polarity.ToString(),
                CombinedReadability:styleDecision.CombinedReadability,StyleAdjustment:styleDecision.Adjustment,FillPolarityChanged:styleDecision.FillPolarityChanged,StyleBundleTicks:styleDecision.ElapsedTicks));
            layout.Font.Dispose();
            atomicCanvas?.Dispose();
            normalRenderTicks+=Stopwatch.GetTimestamp()-layoutStart;
        }

        total.Stop();
        RealPathDiagnosticTrace.Stage("POST_RENDER_UNIT",regions.Select(RealPathDiagnosticTrace.RegionRow));
        AppLog.Write("render-performance",System.Text.Json.JsonSerializer.Serialize(new { timestamp=DateTimeOffset.Now,
            totalMs=total.ElapsedMilliseconds,regionCount=regions.Count,textLength=regions.Sum(r=>r.TranslationText?.Length??0),pixelArea=(long)source.Width*source.Height,
            typographyContextMs=Ms(contextTicks),safeLayoutAndTypographyMs=Ms(layoutTicks),backgroundMaskAndClassificationMs=Ms(maskTicks),finalTextRenderMs=Ms(drawTicks),
            normalRenderMs=Ms(normalRenderTicks),fallbackRenderMs=Ms(fallbackRenderTicks),fallbackRegionCount,
            sharedBodyFontSize=typographyContext.BaseBodyFontSize,sharedBodyRegions=typographyContext.RegionFontSizes.Count,
            fullBitmapCloneCount,fullBitmapBlitCount,regionBitmapAllocationCount,temporaryBitmapBytes,
            totalPixelBytesCopied=(long)source.Width*source.Height*4+temporaryBitmapBytes }));
        var copyMetrics=new RenderCopyMetrics(fullBitmapCloneCount,fullBitmapBlitCount,regionBitmapAllocationCount,temporaryBitmapBytes,
            (long)source.Width*source.Height*4+temporaryBitmapBytes,Ms(normalRenderTicks),Ms(fallbackRenderTicks),fallbackRegionCount,regionBitmapAllocationCount);
        var textMeasureCount=diagnostics.Sum(x=>Math.Max(1,x.LineCount))*2+regions.Sum(x=>Math.Max(1,x.TranslationText?.Length??0));
        var fontMeasureCount=diagnostics.Sum(x=>Math.Max(1,x.LineCount))+regions.Count;
        var resources=resourceProbe.Complete(copyMetrics,1+regionBitmapAllocationCount,fontMeasureCount,textMeasureCount,
            diagnostics.Count(x=>x.AtomicRegionCommitted),fallbackRegionCount);
        AppLog.Write("renderer-resource",System.Text.Json.JsonSerializer.Serialize(resources));
        return new(output, warnings, total.ElapsedMilliseconds, Ms(maskTicks), Ms(layoutTicks), Ms(drawTicks), diagnostics,renderRects,copyMetrics,resources);
    }

    private static RectangleF ResolveLocalSiblingCollision(RectangleF target,RecognitionRegion region,
        IReadOnlyList<RecognitionRegion> regions,IReadOnlyDictionary<string,RectangleF> committed,Size imageSize)
    {
        if(region.RoleType is not (RegionRoleType.CharacterName or RegionRoleType.Species or RegionRoleType.BodyParagraph or RegionRoleType.Metadata))return target;
        var prior=regions.Where(x=>x.ReadingOrder<region.ReadingOrder&&committed.ContainsKey(x.RegionId)
            &&x.RoleType is RegionRoleType.CharacterName or RegionRoleType.Species or RegionRoleType.BodyParagraph or RegionRoleType.Metadata)
            .OrderByDescending(x=>x.ReadingOrder).Select(x=>committed[x.RegionId]).FirstOrDefault();
        if(prior.IsEmpty)return target;
        var overlap=RectangleF.Intersect(prior,target);if(overlap.IsEmpty||overlap.Width*overlap.Height<Math.Min(prior.Width*prior.Height,target.Width*target.Height)*.08f)return target;
        var gap=Math.Clamp(Math.Min(prior.Height,target.Height)*.18f,3,18);var shifted=new RectangleF(target.X,prior.Bottom+gap,target.Width,target.Height);
        return shifted.Bottom<=imageSize.Height?shifted:target;
    }

    private static RectangleF ConstrainIndependentHeaderAnchor(RectangleF target,RecognitionRegion region,
        IReadOnlyList<RecognitionRegion> regions)
    {
        if(region.RoleType!=RegionRoleType.CharacterName)return target;
        var own=RectangleF.Intersect(target,region.BoundingBox);
        if(own.IsEmpty)own=target;
        var species=regions.Where(x=>x.RegionId!=region.RegionId&&x.RoleType==RegionRoleType.Species)
            .Select(x=>x.BoundingBox).Where(x=>x.Top>own.Top&&x.Left<own.Right&&x.Right>own.Left)
            .OrderBy(x=>x.Top).FirstOrDefault();
        if(species.IsEmpty)return own;
        var bottom=Math.Min(own.Bottom,species.Top);
        return bottom-own.Top>=3?new RectangleF(own.Left,own.Top,own.Width,bottom-own.Top):own;
    }

    private sealed record ReadableFallbackResult(RegionRenderDiagnostic Diagnostic,long ElapsedTicks,int RoiAllocations,long TemporaryBytes);
    private sealed record OwnershipLayout(RectangleF SourceGlyphBounds,RectangleF OwnedSurfaceBounds,RectangleF FinalRenderBounds,IReadOnlyList<RectangleF> ReservedRegions);
    private static OwnershipLayout ResolveOwnershipLayout(Size imageSize,RecognitionRegion region,RectangleF sourceGlyphBounds,RectangleF suggested,IReadOnlyList<RecognitionRegion> regions)
    {
        var imageBounds=new RectangleF(PointF.Empty,imageSize);var reserved=regions.Where(x=>x.RegionId!=region.RegionId).Select(x=>RectangleF.Intersect(imageBounds,x.BoundingBox)).Where(x=>!x.IsEmpty&&RectangleF.Intersect(x,sourceGlyphBounds).IsEmpty).ToArray();
        var owned=RectangleF.Intersect(imageBounds,RectangleF.Union(sourceGlyphBounds,suggested));if(owned.IsEmpty)owned=sourceGlyphBounds;
        // Region/grouping geometry is the only expansion evidence. Neighbouring Regions are hard
        // reservations: neither layout nor cleanup may cross them.
        foreach(var r in reserved)
        {
            if(r.Bottom>owned.Top&&r.Top<owned.Bottom){if(r.Left>=sourceGlyphBounds.Right)owned.Width=Math.Max(sourceGlyphBounds.Width,Math.Min(owned.Right,r.Left)-owned.Left);if(r.Right<=sourceGlyphBounds.Left){var left=Math.Max(owned.Left,r.Right);owned=new(left,owned.Top,owned.Right-left,owned.Height);}}
            if(r.Right>owned.Left&&r.Left<owned.Right){if(r.Top>=sourceGlyphBounds.Bottom)owned.Height=Math.Max(sourceGlyphBounds.Height,Math.Min(owned.Bottom,r.Top)-owned.Top);if(r.Bottom<=sourceGlyphBounds.Top){var top=Math.Max(owned.Top,r.Bottom);owned=new(owned.Left,top,owned.Width,owned.Bottom-top);}}
        }
        owned=RectangleF.Intersect(imageBounds,owned);return new(sourceGlyphBounds,owned,sourceGlyphBounds,reserved);
    }
    private static ReadableFallbackResult ReadableFallback(Bitmap source,Bitmap output,Graphics destination,RecognitionRegion region,RectangleF suggested,IReadOnlyList<RecognitionRegion> regions,RenderSettings settings,string normalFailure)
    {
        var started=Stopwatch.GetTimestamp();
        var imageBounds=new RectangleF(PointF.Empty,source.Size);
        var ownLines=region.SourceLinePolygons.Select(GeometryV2.Bounds).Where(x=>x.Width>=2&&x.Height>=2).ToArray();
        var ownAnchor=ownLines.Length==0?region.BoundingBox:ownLines.Aggregate(RectangleF.Union);
        var anchor=RectangleF.Intersect(imageBounds,ownAnchor.Width>=3&&ownAnchor.Height>=3?ownAnchor:(suggested.Width>=3&&suggested.Height>=3?suggested:region.BoundingBox));
        if(anchor.Width<3||anchor.Height<3)return new(FallbackFailure(region,normalFailure,"No reasonable fallback placement rectangle"),Stopwatch.GetTimestamp()-started,0,0);
        var ownership=ResolveOwnershipLayout(source.Size,region,anchor,suggested,regions);
        var sourceLineHeight=ownLines.Select(x=>x.Height).OrderBy(x=>x).DefaultIfEmpty(anchor.Height).ElementAt(Math.Max(0,(ownLines.Length-1)/2));
        var baseSize=region.RoleType switch{RegionRoleType.Title or RegionRoleType.CharacterName=>Math.Clamp(sourceLineHeight*.72f,20,58),RegionRoleType.Metadata or RegionRoleType.UILabel or RegionRoleType.Button=>Math.Clamp(sourceLineHeight*.82f,11,24),_=>Math.Clamp(sourceLineHeight*.88f,13,32)};
        // The readable fallback owns the original OCR rectangle only.  Growing the panel
        // here can erase neighbouring controls (Cash's Continue button was the real case).
        // A single, shallow body line can be an independently-owned caption embedded in
        // artwork. Keep this geometry-driven: it is not tied to fixture text or ids.
        var compactArtworkLine=region.RoleType==RegionRoleType.BodyParagraph&&region.SourceBlockIds.Count==1&&ownLines.Length==1&&
            anchor.Height<=source.Height*.06f&&anchor.Width<=source.Width*.55f;
        var compactField=region.RoleType is RegionRoleType.Metadata or RegionRoleType.UILabel or RegionRoleType.Header||compactArtworkLine;
        var expanded=ownership.OwnedSurfaceBounds;
        using var measure=Graphics.FromImage(new Bitmap(1,1));ConfigureGraphics(measure,settings);
        Font? font=null;List<string>? lines=null;float chosen=baseSize;
        // A shallow, independently-owned artwork caption can require two CJK lines even
        // though the source was one short Latin line.  Keep the same owned rectangle and
        // cleanup path, but allow a smaller readable floor before declaring that the full
        // translation cannot fit.  Other fields and paragraphs retain their C09 floors.
        var fallbackFloor=compactArtworkLine?6:compactField?8:10;var horizontalInset=compactArtworkLine?2:12;var verticalInset=compactArtworkLine?0:4;
        for(var size=baseSize;size>=fallbackFloor;size-=1){font?.Dispose();font=FontSettingsPolicy.CreatePixel(settings.FontFamily,"Microsoft YaHei UI",size,FontStyle.Regular);var candidate=WrapExact(measure,region.TranslationText,font,Math.Max(20,expanded.Width-horizontalInset));var h=candidate.Count*font.GetHeight(measure)*1.08f;if(h<=expanded.Height-verticalInset){lines=candidate;chosen=size;break;}}
        if(font is null||lines is null||!TextIntegrityMatches(region.TranslationText,lines)){font?.Dispose();return new(FallbackFailure(region,normalFailure,"Full translation cannot fit fallback bounds without mutation"),Stopwatch.GetTimestamp()-started,0,0);}
        var lineHeight=font.GetHeight(measure)*1.12f;var measuredWidth=lines.Max(x=>measure.MeasureString(x,font).Width);var compact=region.RoleType is RegionRoleType.Title or RegionRoleType.Header||compactArtworkLine;
        var neededHeight=Math.Min(expanded.Height,lines.Count*lineHeight+10);
        var compactWidth=region.RoleType==RegionRoleType.CharacterName?expanded.Width:expanded.Width;
        var panel=RectangleF.Intersect(imageBounds,new RectangleF(expanded.Left,expanded.Top,compact?compactWidth:expanded.Width,compact?Math.Min(expanded.Height,Math.Max(neededHeight,Math.Min(expanded.Height,baseSize*1.35f))):expanded.Height));
        var roi=Rectangle.Intersect(Rectangle.Inflate(Rectangle.Ceiling(panel),2,2),new Rectangle(Point.Empty,source.Size));
        if(roi.Width<3||roi.Height<3){font.Dispose();return new(FallbackFailure(region,normalFailure,"Fallback ROI is empty"),Stopwatch.GetTimestamp()-started,0,0);}
        var cleanupPlan=BackgroundIntegrationPlanner.Plan(source,region,anchor,[region]);
        var cleanupRoi=cleanupPlan.CleanupBounds.IsEmpty?roi:Rectangle.Intersect(Rectangle.Union(roi,cleanupPlan.CleanupBounds),new Rectangle(Point.Empty,source.Size));
        var uiSurface=region.RoleType is RegionRoleType.Choice or RegionRoleType.Button or RegionRoleType.UILabel or RegionRoleType.Metadata or RegionRoleType.Header;
        var bg=uiSurface?BackgroundEstimator.Estimate(source,anchor).Color:FallbackRingBackground(source,anchor);var dark=bg.GetBrightness()<.48f;var cleanupColor=Color.FromArgb(255,bg.R,bg.G,bg.B);
        destination.Flush(FlushIntention.Sync);
        using var reconstructed=BackgroundIntegrationExecutor.ExecuteRoi(source,cleanupPlan,cleanupRoi);
        using var localRestored=reconstructed.Committed?null:BackgroundIntegrationExecutor.RestoreMaskLocal(source,cleanupPlan,cleanupRoi);
        var canvas=reconstructed.Committed?reconstructed.Bitmap:localRestored!.Bitmap;
        if(!reconstructed.Committed&&!localRestored!.Committed){font.Dispose();return new(FallbackFailure(region,normalFailure,"Mask-local restoration failed: "+localRestored.ValidationFailure),Stopwatch.GetTimestamp()-started,1,(long)cleanupRoi.Width*cleanupRoi.Height*4);}
        var drawX=panel.Left+(compactArtworkLine?1:7);var contentHeight=lines.Count*lineHeight;var drawY=panel.Top+Math.Max(compactArtworkLine?0:2,(panel.Height-contentHeight)/2);
        var fallbackSourceBundle=SourceStyleBundleOwner.WithExplicitSettings(SourceStyleBundleOwner.Create(source,region),settings);
        if(settings.Outline){var c=settings.AutomaticOutlineColor?SourceStyleBundleOwner.Opposite(fallbackSourceBundle.FillColor):settings.OutlineColor;fallbackSourceBundle=fallbackSourceBundle with{OutlineColor=Color.FromArgb(220,c),OutlineWidth=settings.StrokeWidth switch{RendererStrokeWidth.Medium=>2f,RendererStrokeWidth.None=>0f,_=>1f}};}
        if(settings.Shadow&&!fallbackSourceBundle.HasShadow)fallbackSourceBundle=fallbackSourceBundle with{ShadowColor=Color.FromArgb(150,Color.Black),ShadowOffset=new PointF(1,1)};
        var fallbackStyleDecision=SourceStyleLegibilityGuard.Evaluate(fallbackSourceBundle,cleanupColor);var fallbackStyle=fallbackStyleDecision.Effective;
        using(var cg=Graphics.FromImage(canvas)){ConfigureGraphics(cg,settings);cg.TranslateTransform(-cleanupRoi.Left,-cleanupRoi.Top);
            var y=drawY;foreach(var line in lines){SourceStyleTextDrawingR2.Draw(cg,line,font,new PointF(drawX,y),fallbackStyle.FillColor,fallbackStyle.HasOutline,fallbackStyle.OutlineColor,fallbackStyle.OutlineWidth,fallbackStyle.HasShadow,fallbackStyle.ShadowColor,fallbackStyle.ShadowOffset,fallbackStyle.Glow,fallbackStyle.Alpha);y+=lineHeight;}}
        destination.DrawImageUnscaled(canvas,cleanupRoi.Location);destination.Flush(FlushIntention.Sync);font.Dispose();
        var rendered=new List<RenderedLine>();var yy=drawY;foreach(var line in lines){rendered.Add(new(line,new(drawX,yy,Math.Max(1,panel.Right-drawX-(compactArtworkLine?1:7)),lineHeight)));yy+=lineHeight;}
        var start=SourceTextStart(region);var d=new RegionRenderDiagnostic(region.RegionId,region.RoleType,region.SourceBlockIds.Count,region.TranslationText.Length,RegionRenderStatus.FallbackOverlay,chosen,lines.Count,0,0,0,Ms(Stopwatch.GetTimestamp()-started),reconstructed.Committed?"Readable fallback committed with mask-based reconstruction":"Readable fallback committed with mask-local restoration",region.SourceBlockIds.Select(id=>new SourceLineRenderDecision(id,true,"ReadableFallbackCleanupMask")).ToArray(),rendered,
            "ReadableFallbackBackgroundPreserving",BackgroundColor:$"#{cleanupColor.R:X2}{cleanupColor.G:X2}{cleanupColor.B:X2}",Alpha:0,FontFamily:settings.FontFamily,RenderBounds:panel,TranslationRenderRect:panel,TextColor:SourceStyleBundleOwner.Hex(fallbackStyle.FillColor),TextContrast:fallbackStyleDecision.CombinedReadability,TextColorReason:fallbackStyleDecision.Adjustment,TextDrawCount:1,AtomicRegionCommitted:true,DecisionStage:"ReadableFallback",DecisionCode:"VisibleTranslationCommitted",TranslationUnitId:region.TranslationUnitId,
            SourceTextStartX:start.X,SourceTextStartY:start.Y,RenderTextStartX:drawX,RenderTextStartY:drawY,AnchorDeltaX:drawX-start.X,AnchorDeltaY:drawY-start.Y,Alignment:"Near",ExpansionDirection:"RightDown",FallbackAttempted:true,FallbackRendered:true,NormalRenderFailureReason:normalFailure,LayoutTarget:panel);
        d=d with{SourceGlyphBounds=ownership.SourceGlyphBounds,OwnedSurfaceBounds=ownership.OwnedSurfaceBounds,FinalRenderBounds=panel,
            AtomicCommitBounds=cleanupRoi,ReservedRegions=ownership.ReservedRegions,SourceStyleOwner=fallbackSourceBundle.Owner,
            SourceFillColor=SourceStyleBundleOwner.Hex(fallbackSourceBundle.FillColor),SourceOutlineColor=SourceStyleBundleOwner.Hex(fallbackSourceBundle.OutlineColor),SourceOutlineWidth=fallbackSourceBundle.OutlineWidth,
            SourceShadowColor=SourceStyleBundleOwner.Hex(fallbackSourceBundle.ShadowColor),SourceConfidence=fallbackSourceBundle.Confidence,SourcePolarity=fallbackSourceBundle.Polarity.ToString(),
            CombinedReadability=fallbackStyleDecision.CombinedReadability,StyleAdjustment=fallbackStyleDecision.Adjustment,FillPolarityChanged=fallbackStyleDecision.FillPolarityChanged,StyleBundleTicks=fallbackStyleDecision.ElapsedTicks};
        return new(d,Stopwatch.GetTimestamp()-started,1,(long)cleanupRoi.Width*cleanupRoi.Height*4);
    }
    private static RegionRenderDiagnostic FallbackFailure(RecognitionRegion r,string normal,string failure)=>new(r.RegionId,r.RoleType,r.SourceBlockIds.Count,r.TranslationText.Length,RegionRenderStatus.Failed,0,0,0,0,0,0,"Fallback failed: "+failure,[],[],DecisionStage:"ReadableFallback",DecisionCode:"ExplicitRenderFailure",TranslationUnitId:r.TranslationUnitId,FallbackAttempted:true,FallbackRendered:false,NormalRenderFailureReason:normal,FallbackFailureReason:failure);
    private static List<string> WrapExact(Graphics g,string text,Font font,float width){var result=new List<string>();const string noLineStart="，。！？：；、,.!?:;)）】》";foreach(var paragraph in text.Replace("\r","").Split('\n')){if(paragraph.Length==0){result.Add("");continue;}var line="";foreach(var c in paragraph){var next=line+c;if(line.Length>0&&g.MeasureString(next,font).Width>width){if(noLineStart.Contains(c)){line+=c;continue;}result.Add(line);line=c.ToString();}else line=next;}if(line.Length>0)result.Add(line);}return result;}
    private static Color FallbackRingBackground(Bitmap image,RectangleF area)
    {
        var r=Rectangle.Intersect(Rectangle.Inflate(Rectangle.Ceiling(area),4,3),new Rectangle(Point.Empty,image.Size));var inner=Rectangle.Ceiling(area);var colors=new List<Color>();
        for(var x=r.Left;x<r.Right;x+=Math.Max(1,r.Width/80)){if(r.Top<inner.Top)colors.Add(image.GetPixel(x,r.Top));if(r.Bottom-1>=inner.Bottom)colors.Add(image.GetPixel(x,r.Bottom-1));}
        for(var y=r.Top;y<r.Bottom;y+=Math.Max(1,r.Height/30)){if(r.Left<inner.Left)colors.Add(image.GetPixel(r.Left,y));if(r.Right-1>=inner.Right)colors.Add(image.GetPixel(r.Right-1,y));}
        if(colors.Count<4)return BackgroundEstimator.Estimate(image,area).Color;int M(Func<Color,int> f)=>colors.Select(f).OrderBy(x=>x).ElementAt(colors.Count/2);return Color.FromArgb(M(c=>c.R),M(c=>c.G),M(c=>c.B));
    }

    private static RegionRenderDiagnostic Preserved(RecognitionRegion r) => new(r.RegionId, r.RoleType,
        r.SourceBlockIds.Count, r.TranslationText.Length, RegionRenderStatus.PreservedOriginal, 0, 0, 0, 0, 0, 0,
        r.PreserveOriginal?"Region explicitly preserves original":"No translated text mapped to Region", r.SourceBlockIds.Select(x => new SourceLineRenderDecision(x, false, "PreservedOriginal")).ToArray(), [],
        DecisionStage:r.PreserveOriginal?"Authorization":"TranslationMapping",DecisionCode:r.PreserveOriginal?"PreserveOriginal":"NoTranslationText",TranslationUnitId:r.TranslationUnitId);
    private static RegionRenderDiagnostic NoOpPreserved(RecognitionRegion r)=>new(r.RegionId,r.RoleType,r.SourceBlockIds.Count,r.TranslationText.Length,
        RegionRenderStatus.PreservedOriginal,0,0,0,0,0,0,"Translation is visually identical to source; original pixels retained",r.SourceBlockIds.Select(x=>new SourceLineRenderDecision(x,false,"NoOpTranslation")).ToArray(),[],
        DecisionStage:"Authorization",DecisionCode:"NoOpTranslation",TranslationUnitId:r.TranslationUnitId);
    private static RegionRenderDiagnostic Failed(RecognitionRegion r, string stage,string code,string reason) => new(r.RegionId, r.RoleType,
        r.SourceBlockIds.Count, r.TranslationText.Length, RegionRenderStatus.Failed, 0, 0, 0, 0, 0, 0, $"{code}: {reason}", [], [],DecisionStage:stage,DecisionCode:code,TranslationUnitId:r.TranslationUnitId);
    private static RegionRenderDiagnostic AtomicAbort(RecognitionRegion r,string reason,IReadOnlyList<SourceLineRenderDecision> coverage)=>new(r.RegionId,r.RoleType,
        r.SourceBlockIds.Count,r.TranslationText.Length,RegionRenderStatus.PreservedOriginal,0,0,0,0,0,0,"UnsafeBackgroundIntegration: "+reason,coverage,[],
        TextDrawCount:0,DecorationDrawCount:0,AtomicRegionCommitted:false,DecisionStage:"BackgroundIntegration",DecisionCode:"AtomicAbort",TranslationUnitId:r.TranslationUnitId);
    private static bool TextIntegrityMatches(string translation,IReadOnlyList<string> lines)
    {static string Visible(string value)=>value.Replace("\r","").Replace("\n","");return !translation.Contains('\uFFFD')&&!HasInvalidSurrogate(translation)&&string.Equals(Visible(translation),Visible(string.Concat(lines)),StringComparison.Ordinal);}
    private static bool HasInvalidSurrogate(string value){for(var i=0;i<value.Length;i++){if(char.IsHighSurrogate(value[i])){if(i+1>=value.Length||!char.IsLowSurrogate(value[++i]))return true;}else if(char.IsLowSurrogate(value[i]))return true;}return false;}
    private static void ConfigureGraphics(Graphics g,RenderSettings settings){g.TextRenderingHint=settings.LegacyTextRasterizationForDiagnostics?TextRenderingHint.ClearTypeGridFit:TextRenderingHint.AntiAliasGridFit;g.SmoothingMode=SmoothingMode.HighQuality;g.CompositingQuality=CompositingQuality.HighQuality;g.PixelOffsetMode=PixelOffsetMode.Default;}
    private static IReadOnlyList<RenderedLine> DrawLines(Graphics g, TextLayoutResult layout, SourceStyleBundle style, RenderSettings settings,bool forceContrastOutline)
    {
        var result = new List<RenderedLine>();
        var lineHeight = layout.Font.GetHeight(g) * layout.LineSpacing;
        var totalHeight = lineHeight * layout.Lines.Count;
        var y = layout.Alignment == StringAlignment.Center && layout.VerticalCenter
            ? layout.Target.Top + Math.Max(0, (layout.Target.Height - totalHeight) / 2) : layout.Target.Top;
        foreach (var line in layout.Lines)
        {
            var measured = g.MeasureString(line, layout.Font);
            var x = layout.Alignment switch { StringAlignment.Center => layout.Target.Left + (layout.Target.Width - measured.Width) / 2,
                StringAlignment.Far => layout.Target.Right - measured.Width, _ => layout.Target.Left };
            x=MathF.Round(x);y=MathF.Round(y);var bounds = new RectangleF(x, y, MathF.Ceiling(Math.Min(measured.Width, layout.Target.Width)), MathF.Ceiling(lineHeight));
            DrawStyled(g, line, layout.Font, style, bounds, settings,forceContrastOutline);
            result.Add(new(line, bounds)); y += lineHeight;
        }
        return result;
    }
    private static Color LegacyForeground(Bitmap source,RecognitionRegion region,RectangleF target)
    {var bg=BackgroundEstimator.Estimate(source,target).Color;var hint=TextStyleHintExtractor.EstimateForeground(source,region,bg);if(hint is { } h&&TranslationTextColorResolver.Contrast(h,bg)>=3)return h;return bg.GetBrightness()>.5f?Color.Black:Color.White;}
    private static IReadOnlyList<RenderedLine> MeasureLines(Graphics g,TextLayoutResult layout)
    {var result=new List<RenderedLine>();var h=layout.Font.GetHeight(g)*layout.LineSpacing;var total=h*layout.Lines.Count;var y=layout.Alignment==StringAlignment.Center&&layout.VerticalCenter?layout.Target.Top+Math.Max(0,(layout.Target.Height-total)/2):layout.Target.Top;foreach(var line in layout.Lines){var m=g.MeasureString(line,layout.Font);var x=layout.Alignment switch{StringAlignment.Center=>layout.Target.Left+(layout.Target.Width-m.Width)/2,StringAlignment.Far=>layout.Target.Right-m.Width,_=>layout.Target.Left};x=MathF.Round(x);y=MathF.Round(y);result.Add(new(line,new(x,y,MathF.Ceiling(Math.Min(m.Width,layout.Target.Width)),MathF.Ceiling(h))));y+=h;}return result;}
    private static RectangleF TightBounds(IReadOnlyList<RenderedLine> lines,Size size,float fontSize)
    {var union=lines.Count==0?RectangleF.Empty:lines.Select(x=>x.Bounds).Aggregate(RectangleF.Union);var px=Math.Clamp(fontSize*.35f,3,10);var py=Math.Clamp(fontSize*.2f,2,7);return RectangleF.Intersect(RectangleF.Inflate(union,px,py),new(PointF.Empty,size));}
    private static void DrawStyled(Graphics g, string text, Font font, SourceStyleBundle style, RectangleF bounds, RenderSettings settings,bool forceContrastOutline)
    {
        if(forceContrastOutline&&!style.HasOutline)
        {
            style=style with{OutlineColor=Color.FromArgb(210,SourceStyleBundleOwner.Opposite(style.FillColor)),OutlineWidth=1.25f};
        }
        SourceStyleTextDrawingR2.Draw(g,text,font,bounds.Location,style.FillColor,style.HasOutline,style.OutlineColor,style.OutlineWidth,
            style.HasShadow,style.ShadowColor,style.ShadowOffset,style.Glow,style.Alpha);
    }
    private static long Ms(long ticks) => (long)(ticks * 1000d / Stopwatch.Frequency);
    private static PointF SourceTextStart(RecognitionRegion region)
    {var boxes=region.SourceLinePolygons.Select(GeometryV2.Bounds).Where(x=>!x.IsEmpty).OrderBy(x=>x.Top).ThenBy(x=>x.Left).ToArray();return boxes.Length==0?region.BoundingBox.Location:boxes[0].Location;}
}

public sealed record TextLayoutResult(Font Font, IReadOnlyList<string> Lines, RectangleF Target,
    StringAlignment Alignment, bool VerticalCenter, float LineSpacing, bool Fallback, string FallbackReason,
    string ExpansionDirection="None",int FitAttempts=1);

public static class TextStyleHintExtractor
{
    public static Color? EstimateForeground(Bitmap image, RecognitionRegion region, Color background)
    {
        var candidates = new List<(Color Color, int Distance)>();
        var polygons = region.SourceLinePolygons.Count > 0 ? region.SourceLinePolygons : [region.Polygon];
        foreach (var polygon in polygons.Where(x => x.Length >= 3))
        {
            var bounds = Rectangle.Round(RectangleF.Intersect(GeometryV2.Bounds(polygon), new(PointF.Empty, image.Size)));
            var step = Math.Max(1, Math.Min(bounds.Width, bounds.Height) / 10);
            for (var y = bounds.Top; y < bounds.Bottom; y += step)
            for (var x = bounds.Left; x < bounds.Right; x += step)
            {
                var color = image.GetPixel(x, y);
                var distance = Math.Abs(color.R - background.R) + Math.Abs(color.G - background.G) + Math.Abs(color.B - background.B);
                if (distance >= 70) candidates.Add((color, distance));
            }
        }
        if (candidates.Count < 3) return null;
        // Quantized local clustering favors a repeated glyph color over a single extreme
        // background sample. Every call sees only this field's SourceLinePolygons, so a Name
        // can no longer inherit Race color (or vice versa).
        var preferred=region.RoleType==RegionRoleType.CharacterName?candidates.Where(x=>x.Color.GetBrightness() is >.38f and <.84f&&x.Color.GetSaturation()>.12f).ToList():[];
        var pool=preferred.Count>=3?preferred:candidates;
        var cluster=pool.GroupBy(x=>(R:x.Color.R/24,G:x.Color.G/24,B:x.Color.B/24))
            .Select(g=>new{Items=g.ToArray(),Score=g.Count()*Math.Max(1,g.Average(x=>x.Distance))})
            .OrderByDescending(x=>x.Score).First().Items.Select(x=>x.Color).ToArray();
        int Median(Func<Color,int> selector)=>cluster.Select(selector).OrderBy(x=>x).ElementAt(cluster.Length/2);
        var result=Color.FromArgb(Median(x=>x.R),Median(x=>x.G),Median(x=>x.B));
        return region.RoleType==RegionRoleType.CharacterName||TranslationTextColorResolver.Contrast(result,background)>=3?result:null;
    }
}

public static class TranslatedTextLayoutEngine
{
    private static readonly char[] NoLineStart = ['，','。','！','？','：','；','、',',','.','!','?',':',';',')','）','】','》'];
    public static TextLayoutResult? Fit(Graphics g, RecognitionRegion region, RectangleF target,
        IReadOnlyList<RecognitionRegion> allRegions, Size sourceSize, RenderSettings settings,TypographyContext? context=null)
    {
        context??=TypographyContextPlanner.Plan(allRegions,settings);
        var profile=settings.DiagnosticUseLegacyRoleTypography?RoleTypographyProfiles.ResolveLegacy(region.RoleType):RoleTypographyProfiles.Resolve(region);
        var decorativeName=region.RoleType==RegionRoleType.CharacterName&&target.Width>250&&target.Height>60;
        var repeatedMember=context.VisualStyleGroups?.SelectMany(x=>x.Members).FirstOrDefault(x=>x.RegionId==region.RegionId);
        profile=profile with{Style=TypographyContextPlanner.ResolveStyle(region,context),Alignment=repeatedMember?.Alignment??(decorativeName?StringAlignment.Center:profile.Alignment),VerticalCenter=repeatedMember is not null||decorativeName||profile.VerticalCenter};var inset = Inset(target,profile); var originalHeight = MedianHeight(region);
        // Compact visual roles have their own readability floor. Applying the global body floor
        // made short headers/metadata fail even when the source box visibly had enough room.
        var compactRole=region.RoleType is RegionRoleType.Header or RegionRoleType.Metadata or RegionRoleType.UILabel
            ||region.TranslationText.Length<=16&&target.Height<=32;
        var minimum=compactRole?profile.MinFont:Math.Max(settings.MinFontSize,profile.MinFont);
        if(decorativeName)minimum=Math.Max(minimum,32);
        var shortUiText=region.SourceLinePolygons.Count<=3&&region.TranslationText.Length<=90&&target.Height<=Math.Max(36,originalHeight*5.5f);
        if(shortUiText)minimum=Math.Max(minimum,Math.Min(profile.MaxFont,originalHeight*.72f));
        var maximum = Math.Max(minimum,Math.Min(decorativeName?58:profile.MaxFont,target.Height*.82f));
        var shared=context.TryGet(region,out var sharedSize);var desired=decorativeName?target.Height*.45f:shortUiText?originalHeight*.92f:originalHeight*profile.Scale*settings.FontScale;
        var initial = Math.Clamp(shared?Math.Max(sharedSize,desired):desired, minimum, maximum);
        var fitSpacing=repeatedMember is null?profile.LineSpacing:Math.Min(1.0f,profile.LineSpacing);
        // Body, dialogue and narration share one page-level type scale.  A bold
        // run is allowed to change weight, never to create a smaller size tier.
        // If the common size does not fit safely, atomic fallback preserves the
        // source region instead of silently shrinking this one paragraph.
        var groupFloor=repeatedMember is null?minimum:Math.Max(minimum,repeatedMember.FontSize*.78f);
        var floor=shared?Math.Max(groupFloor,context.AllowedMinFontSize):groupFloor;var attempts=0;
        for (var size = initial; size >= floor; size -= .75f)
        {
            attempts++;
            var font = FontSettingsPolicy.CreatePixel(settings.FontFamily,"Microsoft YaHei UI",size,profile.Style); var lines = Wrap(g, region.TranslationText, font, inset.Width, region.RoleType);
            if (Height(lines.Count, font, fitSpacing) <= inset.Height) return Make(profile, font, lines, inset, fitSpacing, false, "","None",attempts);
            font.Dispose();
        }
        if(shortUiText)return null;
        var expansion=SafeExpand(target,allRegions.Where(x=>x.RegionId!=region.RegionId).Select(x=>x.BoundingBox)
            .Concat(ParagraphGeometryPlanner.LocalExclusions(region)),sourceSize,settings.SafeExpansionScale,region.RoleType,profile.Alignment);
        var expanded=expansion.Box;inset = Inset(expanded,profile); var fallbackSize=shared?floor:minimum;attempts++;
        var fallbackFont = FontSettingsPolicy.CreatePixel(settings.FontFamily,"Microsoft YaHei UI",fallbackSize,profile.Style);
        var fallbackLines = Wrap(g, region.TranslationText, fallbackFont, inset.Width, region.RoleType);
        if(Height(fallbackLines.Count,fallbackFont,Math.Min(profile.LineSpacing,settings.CompactLineSpacingScale))>inset.Height){fallbackFont.Dispose();return null;}
        return Make(profile, fallbackFont, fallbackLines, inset, Math.Min(profile.LineSpacing,settings.CompactLineSpacingScale), true,
            expanded == target ? "Minimum font/compact overlay; expansion blocked by collision" : "Safe expansion/minimum font/compact spacing",expansion.Direction,attempts);
    }
    public static IReadOnlyList<string> Wrap(Graphics g, string text, Font font, float width, RegionRoleType role)
    {
        if (role is RegionRoleType.Metadata or RegionRoleType.Button or RegionRoleType.CharacterName or RegionRoleType.UILabel && g.MeasureString(text, font).Width <= width) return [text];
        var lines = new List<string>(); foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            var current = ""; foreach (var token in Tokens(paragraph))
            {
                if (current.Length == 0 && g.MeasureString(token, font).Width > width)
                {
                    var chunk = "";
                    foreach (var ch in token)
                    {
                        if (chunk.Length > 0 && g.MeasureString(chunk + ch, font).Width > width) { lines.Add(chunk); chunk = ch.ToString(); }
                        else chunk += ch;
                    }
                    current = chunk;
                    continue;
                }
                var proposed = current + token;
                if (current.Length > 0 && g.MeasureString(proposed, font).Width > width)
                { if (token.Length == 1 && NoLineStart.Contains(token[0])) { current += token; continue; } lines.Add(current); current = token; }
                else current = proposed;
            }
            if (current.Length > 0) lines.Add(current); if (paragraph.Length == 0) lines.Add("");
        }
        return lines.Count == 0 ? [""] : lines;
    }
    public static float Height(int count, Font font, float spacing) => count * font.Height * spacing;
    private static TextLayoutResult Make(RoleTypographyProfile p, Font f, IReadOnlyList<string> lines, RectangleF target, float spacing, bool fallback, string reason,string direction,int attempts)
        => new(f, lines, target,p.Alignment,p.VerticalCenter,spacing,fallback,reason,direction,attempts);
    private static RectangleF Inset(RectangleF r,RoleTypographyProfile p) => RectangleF.Inflate(r,-Math.Min(6,r.Width*p.PaddingX),-Math.Min(4,r.Height*p.PaddingY));
    private static float MedianHeight(RecognitionRegion r) { var values = r.SourceLinePolygons.Select(GeometryV2.Bounds).Select(x => x.Height).Where(x => x > 0).OrderBy(x => x).ToArray(); return values.Length == 0 ? Math.Max(10, r.BoundingBox.Height) : values[(values.Length - 1) / 2]; }
    private sealed record Expansion(RectangleF Box,string Direction);
    private static Expansion SafeExpand(RectangleF box, IEnumerable<RectangleF> neighbors, Size size, float scale,RegionRoleType role,StringAlignment alignment)
    {
        var area=new RectangleF(PointF.Empty,size);var blockers=neighbors.ToArray();
        foreach(var candidateScale in new[]{Math.Max(scale,2.2f),Math.Max(scale,1.8f),Math.Max(scale,1.5f),scale}.Distinct().OrderByDescending(x=>x))
        {
            RectangleF expanded;string direction;
            if(role==RegionRoleType.Title&&alignment==StringAlignment.Near)
            {
                // Preserve the source title start anchor. Extra room goes right/down; symmetric
                // inflation moved left-aligned titles toward the panel edge in 0.5.0.6.4.
                expanded=RectangleF.Intersect(new(box.Left,box.Top,box.Width*candidateScale,box.Height*candidateScale),area);direction="RightDown";
            }
            else {expanded=RectangleF.Intersect(RectangleF.Inflate(box,box.Width*(candidateScale-1)/2,box.Height*(candidateScale-1)/2),area);direction="Symmetric";}
            if(!blockers.Any(n=>RectangleF.Intersect(expanded,n) is {Width:>1,Height:>1}))return new(expanded,direction);
        }
        return new(box,"Blocked");
    }
    private static IEnumerable<string> Tokens(string value)
    { var word = ""; foreach (var c in value) { if (c <= 127 && (char.IsLetterOrDigit(c) || c is '_' or '-' or '/' or '@' or '.')) word += c; else { if (word.Length > 0) { yield return word; word = ""; } yield return c.ToString(); } } if (word.Length > 0) yield return word; }
}

public sealed record BackgroundEstimate(Color Color, bool Complex);
public static class BackgroundRestorer
{
    public static void ExtendLocalTexture(Graphics target,Bitmap source,GraphicsPath mask,RectangleF bounds,Color fallback)
    {
        var box=Rectangle.Round(RectangleF.Intersect(bounds,new RectangleF(PointF.Empty,source.Size)));
        if(box.Width<2||box.Height<2){using var fill=new SolidBrush(fallback);target.FillPath(fill,mask);return;}
        try
        {
            using var tile=new Bitmap(box.Width,box.Height);
            var leftX=Math.Max(0,box.Left-2);var rightX=Math.Min(source.Width-1,box.Right+1);var topY=Math.Max(0,box.Top-2);var bottomY=Math.Min(source.Height-1,box.Bottom+1);
            for(var y=0;y<box.Height;y++)for(var x=0;x<box.Width;x++)
            {
                var fy=box.Height<=1?0:y/(float)(box.Height-1);var fx=box.Width<=1?0:x/(float)(box.Width-1);
                var l=source.GetPixel(leftX,Math.Clamp(box.Top+y,0,source.Height-1));var r=source.GetPixel(rightX,Math.Clamp(box.Top+y,0,source.Height-1));
                var t=source.GetPixel(Math.Clamp(box.Left+x,0,source.Width-1),topY);var b=source.GetPixel(Math.Clamp(box.Left+x,0,source.Width-1),bottomY);
                int Mix(int lv,int rv,int tv,int bv)=>(int)(((lv*(1-fx)+rv*fx)*.65f)+((tv*(1-fy)+bv*fy)*.35f));
                tile.SetPixel(x,y,Color.FromArgb(Mix(l.R,r.R,t.R,b.R),Mix(l.G,r.G,t.G,b.G),Mix(l.B,r.B,t.B,b.B)));
            }
            using var texture=new TextureBrush(tile,WrapMode.Clamp);texture.TranslateTransform(box.Left,box.Top);target.FillPath(texture,mask);
        }
        catch{using var fill=new SolidBrush(fallback);target.FillPath(fill,mask);}
    }
}
public static class BackgroundEstimator
{
    public static BackgroundEstimate Estimate(Bitmap image, RectangleF bounds)
    {
        var colors = new List<Color>(); var r = Rectangle.Round(RectangleF.Intersect(bounds, new(PointF.Empty, image.Size)));
        if (r.Width <= 0 || r.Height <= 0) return new(Color.Black, true);
        var step = Math.Max(1, Math.Min(r.Width, r.Height) / 12);
        for (var x = r.Left; x < r.Right; x += step) { colors.Add(image.GetPixel(x, r.Top)); colors.Add(image.GetPixel(x, r.Bottom - 1)); }
        for (var y = r.Top; y < r.Bottom; y += step) { colors.Add(image.GetPixel(r.Left, y)); colors.Add(image.GetPixel(r.Right - 1, y)); }
        int Median(Func<Color, int> f) => colors.Select(f).OrderBy(x => x).ElementAt(colors.Count / 2);
        var color = Color.FromArgb(Median(x => x.R), Median(x => x.G), Median(x => x.B));
        var variation = colors.Average(x => Math.Abs(x.R - color.R) + Math.Abs(x.G - color.G) + Math.Abs(x.B - color.B));
        return new(color, variation > 75);
    }
}
