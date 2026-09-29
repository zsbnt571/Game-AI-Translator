using System.Drawing.Drawing2D;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScreenshotTranslationUiTester.CorePipelineV2;

namespace ScreenshotTranslationUiTester;

internal enum SourceStyleRenderModeR2 { ObserverOnly, StyleOnly, Integrated }
internal enum SourceVisualRoleR2 { ArtisticTitle, Heading, Body, Metadata, Control, Dialogue, Unknown }
internal enum SourcePolarityR2 { LightOnDark, DarkOnLight, MidTone, Unknown }
internal enum SourceWeightR2 { Regular, Semibold, Bold }
internal enum SourceRelativeSizeR2 { Micro, Small, Body, Large, Display }
internal enum SourceSurfaceClassR2 { Flat, Gradient, Textured, Complex, Unknown }
internal enum SourceStyleFallbackLevelR2
{
    DirectOwner, SiblingConsensus, ContainerConsensus, AdjacentCompatible,
    ParentSurfaceContrast, GenericContrast
}

internal sealed record SourceStyleLineEvidenceR2(int ForegroundArgb, string Foreground, float Confidence,
    int BackgroundArgb, string Background, float BackgroundConfidence, float ForegroundOccupancy,
    float EdgeRatio, float ContrastRatio, int OutlineArgb, string OutlineColor,
    float OutlineConfidence, float OutlineWidth, int ShadowArgb, string ShadowColor,
    float ShadowConfidence, string Reason);

internal sealed record SourceStyleEvidenceR2(
    string BlockId, IReadOnlyList<string> SourceIds, object Bounds, SourceVisualRoleR2 VisualRole,
    int DirectForegroundArgb, string DirectForeground, float DirectConfidence,
    int BackgroundArgb, string Background, float BackgroundConfidence,
    SourcePolarityR2 Polarity, bool OutlineDetected, int OutlineArgb, string OutlineColor,
    float OutlineConfidence, float OutlineWidth, bool ShadowDetected, int ShadowArgb,
    string ShadowColor, float ShadowConfidence, SourceWeightR2 Weight,
    SourceRelativeSizeR2 RelativeSize, string Alignment, float MedianLineHeight,
    float ForegroundOccupancy, float ForegroundEdgeRatio, float ContrastRatio,
    string AcceptedReason, IReadOnlyList<SourceStyleLineEvidenceR2> LineEvidence);

internal sealed record VisualStyleOwnerR2(
    string OwnerId, IReadOnlyList<string> UnderlyingBlockIds, SourceVisualRoleR2 VisualRole,
    int ForegroundArgb, string Foreground, float Confidence, SourcePolarityR2 Polarity,
    bool Outline, int OutlineArgb, string OutlineColor, float OutlineWidth,
    bool Shadow, int ShadowArgb, string ShadowColor, SourceWeightR2 Weight,
    SourceRelativeSizeR2 RelativeSize, string Alignment, float AnchorLineHeight,
    string DeterministicReason);

internal sealed record VisualContainerOwnerR2(
    string OwnerId, IReadOnlyList<string> UnderlyingBlockIds, object Bounds,
    int SurfaceArgb, string SurfaceColor, float Confidence, SourceSurfaceClassR2 SurfaceClass,
    string DeterministicReason);

internal sealed record BackgroundSurfaceOwnerR2(
    string OwnerId, string ContainerOwnerId, IReadOnlyList<string> UnderlyingBlockIds,
    object Bounds, SourceSurfaceClassR2 SurfaceClass, int RepresentativeArgb,
    string RepresentativeColor, float Confidence, string ReconstructionPolicy,
    bool ProductMutationEnabled);

internal sealed record RenderPlanR2(
    string BlockId, string StyleOwnerId, string ContainerOwnerId, string SurfaceOwnerId,
    SourceVisualRoleR2 VisualRole, int ForegroundArgb, string Foreground,
    float ForegroundConfidence, SourcePolarityR2 Polarity, bool Outline,
    int OutlineArgb, string OutlineColor, float OutlineWidth, bool Shadow,
    int ShadowArgb, string ShadowColor, SourceWeightR2 Weight,
    SourceRelativeSizeR2 RelativeSize, string Alignment, float SourceLineHeight,
    SourceStyleFallbackLevelR2 FallbackLevel, string FallbackReason,
    string ContainerPolicy, string BackgroundPolicy);

internal sealed record SourceStyleAnalysisR2(
    IReadOnlyList<SourceStyleEvidenceR2> Evidence,
    IReadOnlyList<VisualStyleOwnerR2> StyleOwners,
    IReadOnlyList<VisualContainerOwnerR2> ContainerOwners,
    IReadOnlyList<BackgroundSurfaceOwnerR2> SurfaceOwners,
    IReadOnlyDictionary<string, RenderPlanR2> Plans);

/// <summary>
/// R2's read-only source-style observer.  It owns no OCR, translation, cleanup,
/// block-allocation, or bitmap mutation decision.  Every ID is derived from the
/// stable ordered Core V2 lineage so three replays produce byte-stable plans.
/// </summary>
internal static class SourceStyleReconstructionR2
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static SourceStyleAnalysisR2 Analyze(Bitmap source, CorePipelineDocument document,
        string output, SourceStyleRenderModeR2 mode, bool writeEvidence = true)
    {
        using var pixels=ReadOnlyBitmapPixelBuffer.Create(source);
        return Analyze(source,document,output,mode,pixels,writeEvidence);
    }

    internal static SourceStyleAnalysisR2 Analyze(Bitmap source,CorePipelineDocument document,
        string output,SourceStyleRenderModeR2 mode,ReadOnlyBitmapPixelBuffer pixels,bool writeEvidence)
    {
        if (writeEvidence) Directory.CreateDirectory(output);
        var median = Median(document.VisualBlocks.SelectMany(x => x.Lines)
            .Select(x => Math.Max(1f, x.Bounds.Height)).ToArray());
        var estimates = document.VisualBlocks.Select(block => EstimateBlock(pixels, block, median)).ToArray();
        // A single OCR line has no internal left-vs-center evidence. Use a repeated
        // vertical axis only when two separate short runs above it support that axis.
        // Broad paragraphs, fields and two-dimensional menus remain with their owner.
        estimates=estimates.Select(e=>
        {
            var block=document.VisualBlocks.Single(b=>b.BlockId==e.BlockId);
            if(block.Lines.Count!=1 || e.Alignment=="Center" ||
                block.Bounds.Width>source.Width*.28f || block.Bounds.Width<block.Bounds.Height*3 ||
                block.SourceText.Contains(':') || block.Bounds.Top>source.Height*.88f)return e;
            var axis=CenterX(block.Bounds);
            var anchors=document.VisualBlocks.Where(b=>b.BlockId!=block.BlockId && b.Lines.Count==1 &&
                b.Bounds.Bottom<block.Bounds.Top && block.Bounds.Top-b.Bounds.Bottom<block.Bounds.Width*1.8f &&
                Math.Abs(CenterX(b.Bounds)-axis)<=Math.Max(3,block.Bounds.Height*.28f) &&
                b.Bounds.Width<=block.Bounds.Width*.7f && b.SourceText.Length<=28).ToArray();
            if(anchors.Length<2 || anchors.Max(b=>b.Bounds.Top)-anchors.Min(b=>b.Bounds.Top)<block.Bounds.Height*2)return e;
            return e with {Alignment="Center",AcceptedReason=e.AcceptedReason+";REPEATED_SOURCE_VERTICAL_AXIS"};
        }).ToArray();
        var byId = estimates.ToDictionary(x => x.BlockId, StringComparer.Ordinal);
        var blocks = document.VisualBlocks.ToDictionary(x => x.BlockId, StringComparer.Ordinal);

        var styleGroups = ConnectedGroups(estimates, (a, b) => StyleCompatible(a, b, byId, blocks, source.Size));
        var styleOwners = styleGroups.Select(group => BuildStyleOwner(group.Select(x => byId[x]).ToArray())).ToArray();
        var styleOwnerByBlock = styleOwners.SelectMany(owner => owner.UnderlyingBlockIds.Select(id => (id, owner)))
            .ToDictionary(x => x.id, x => x.owner, StringComparer.Ordinal);

        var containerGroups = ConnectedGroups(estimates,
            (a, b) => ContainerCompatible(a, b, byId, blocks, source.Size));
        var containers = containerGroups.Select(group => BuildContainerOwner(group.Select(x => byId[x]).ToArray(), blocks)).ToArray();
        var containerByBlock = containers.SelectMany(owner => owner.UnderlyingBlockIds.Select(id => (id, owner)))
            .ToDictionary(x => x.id, x => x.owner, StringComparer.Ordinal);
        var surfaces = containers.Select(c => new BackgroundSurfaceOwnerR2(
            StableId("SURF", c.UnderlyingBlockIds), c.OwnerId, c.UnderlyingBlockIds, c.Bounds,
            c.SurfaceClass, c.SurfaceArgb, c.SurfaceColor, c.Confidence,
            "OBSERVER_ONLY_NO_RECONSTRUCTION_COMMIT", false)).ToArray();
        var surfaceByContainer = surfaces.ToDictionary(x => x.ContainerOwnerId, StringComparer.Ordinal);

        var plans = estimates.ToDictionary(e => e.BlockId, e =>
        {
            var styleOwner = styleOwnerByBlock[e.BlockId];
            var container = containerByBlock[e.BlockId];
            var surface = surfaceByContainer[container.OwnerId];
            var (foreground, confidence, fallback, fallbackReason) = ResolveForeground(
                e, styleOwner, container, estimates, blocks, source.Size);
            var polarity = Polarity(foreground, Color.FromArgb(e.BackgroundArgb));
            return new RenderPlanR2(e.BlockId, styleOwner.OwnerId, container.OwnerId, surface.OwnerId,
                e.VisualRole, foreground.ToArgb(), Hex(foreground), confidence, polarity,
                styleOwner.Outline, styleOwner.OutlineArgb, styleOwner.OutlineColor, styleOwner.OutlineWidth,
                styleOwner.Shadow, styleOwner.ShadowArgb, styleOwner.ShadowColor, styleOwner.Weight,
                styleOwner.RelativeSize, styleOwner.Alignment, styleOwner.AnchorLineHeight, fallback,
                fallbackReason, "SOURCE_GEOMETRY_ONLY", "OBSERVE_UNTIL_SURFACE_GATE");
        }, StringComparer.Ordinal);

        var analysis = new SourceStyleAnalysisR2(estimates, styleOwners, containers, surfaces, plans);
        if (writeEvidence) SaveEvidence(source, document, analysis, output, mode);
        return analysis;
    }

    private static SourceStyleEvidenceR2 EstimateBlock(ReadOnlyBitmapPixelBuffer source, VisualBlock block, float canvasMedian)
    {
        // A selected control inside a compound block cannot define the style of its other lines.
        // Until the block owns that control independently, retain the established line estimator.
        var lines = block.Lines.Select(line => EstimateLine(source, line.Bounds, block.Lines.Count == 1)).ToArray();
        var reliable = lines.Where(x => x.Confidence >= .24f).ToArray();
        var foreground = WeightedColor((reliable.Length > 0 ? reliable : lines)
            .Select(x => (Color.FromArgb(x.ForegroundArgb), Math.Max(.05f, x.Confidence))).ToArray());
        var background = WeightedColor(lines.Select(x => (Color.FromArgb(x.BackgroundArgb),
            Math.Max(.05f, x.BackgroundConfidence))).ToArray());
        var confidence = reliable.Length == 0 ? lines.Select(x => x.Confidence).DefaultIfEmpty().Average() * .55f :
            reliable.Select(x => x.Confidence).Average();
        var lineHeight = Median(block.Lines.Select(x => Math.Max(1f, x.Bounds.Height)).ToArray());
        var role = InferRole(source.Size, block, lineHeight, canvasMedian);
        var relative = lineHeight >= canvasMedian * 2.25f ? SourceRelativeSizeR2.Display :
            lineHeight >= canvasMedian * 1.35f ? SourceRelativeSizeR2.Large :
            lineHeight <= canvasMedian * .55f ? SourceRelativeSizeR2.Micro :
            lineHeight <= canvasMedian * .78f ? SourceRelativeSizeR2.Small : SourceRelativeSizeR2.Body;
        var occupancy = lines.Select(x => x.ForegroundOccupancy).DefaultIfEmpty().Average();
        var weight = occupancy >= .19f ? SourceWeightR2.Bold : occupancy >= .105f ? SourceWeightR2.Semibold : SourceWeightR2.Regular;
        var alignment = InferAlignment(block, role);
        var outlineCandidates = lines.Where(x => x.OutlineConfidence >= .32f).ToArray();
        var outline = outlineCandidates.Length >= Math.Max(1, (int)Math.Ceiling(lines.Length * .35));
        var outlineColor = outline ? WeightedColor(outlineCandidates.Select(x =>
            (Color.FromArgb(x.OutlineArgb), x.OutlineConfidence)).ToArray()) : Color.Transparent;
        var outlineConfidence = outlineCandidates.Select(x => x.OutlineConfidence).DefaultIfEmpty().Average();
        var shadowCandidates = lines.Where(x => x.ShadowConfidence >= .38f).ToArray();
        var shadow = shadowCandidates.Length >= Math.Max(1, (int)Math.Ceiling(lines.Length * .4));
        var shadowColor = shadow ? WeightedColor(shadowCandidates.Select(x =>
            (Color.FromArgb(x.ShadowArgb), x.ShadowConfidence)).ToArray()) : Color.Transparent;
        var ratio = Contrast(foreground, background);
        return new(block.BlockId, block.Lines.Select(x => x.SourceId).ToArray(), Rect(block.Bounds), role,
            foreground.ToArgb(), Hex(foreground), Math.Clamp(confidence, 0, 1), background.ToArgb(), Hex(background),
            lines.Select(x => x.BackgroundConfidence).DefaultIfEmpty().Average(), Polarity(foreground, background),
            outline, outlineColor.ToArgb(), Hex(outlineColor), outlineConfidence,
            outlineCandidates.Select(x => x.OutlineWidth).DefaultIfEmpty().Average(), shadow,
            shadowColor.ToArgb(), Hex(shadowColor), shadowCandidates.Select(x => x.ShadowConfidence).DefaultIfEmpty().Average(),
            weight, relative, alignment, lineHeight, occupancy,
            lines.Select(x => x.EdgeRatio).DefaultIfEmpty().Average(), (float)ratio,
            confidence >= .55f ? "DIRECT_GLYPH_COLOR_CLUSTER" : reliable.Length > 0 ?
                "PARTIAL_DIRECT_GLYPH_EVIDENCE" : "LOW_CONFIDENCE_PENDING_OWNER_FALLBACK",
            lines);
    }

    private sealed record Cluster(int QuantizedKey, Color Color, int Count, int EdgeCount, int BorderCount,
        int RowSpan, int ColumnSpan, double Score) { public Rectangle PixelBounds {get;init;} }

    private static SourceStyleLineEvidenceR2 EstimateLine(ReadOnlyBitmapPixelBuffer source, RectangleF bounds, bool independentControl)
    {
        var rect = Clamp(Rectangle.Round(bounds), source.Size);
        if (rect.Width < 2 || rect.Height < 2)
            return new(Color.White.ToArgb(), Hex(Color.White), 0, Color.Black.ToArgb(), Hex(Color.Black), 0,
                0, 0, 1, Color.Transparent.ToArgb(), Hex(Color.Transparent), 0, 0,
                Color.Transparent.ToArgb(), Hex(Color.Transparent), 0, "EMPTY_GEOMETRY");
        var control=independentControl ? SourceControlMaterial.Observe(source,rect) : null;
        if(control is not null)
            return new(control.Fill.ToArgb(),Hex(control.Fill),.84f,control.Surface.ToArgb(),Hex(control.Surface),.94f,
                control.Pixels.Count/(float)Math.Max(1,control.Bounds.Width*control.Bounds.Height),.8f,
                (float)Contrast(control.Fill,control.Surface),Color.Transparent.ToArgb(),Hex(Color.Transparent),0,0,
                Color.Transparent.ToArgb(),Hex(Color.Transparent),0,control.Reason);
        var inflateX = Math.Clamp((int)MathF.Round(rect.Height * .28f), 2, 12);
        var inflateY = Math.Clamp((int)MathF.Round(rect.Height * .20f), 2, 9);
        var scan = Clamp(Rectangle.Inflate(rect, inflateX, inflateY), source.Size);
        var area = Math.Max(1, rect.Width * rect.Height);
        var quantizedPixels = new int[area];
        var buckets = new Dictionary<(int R, int G, int B), Bucket>();
        for (var y = rect.Top; y < rect.Bottom; y++)
        for (var x = rect.Left; x < rect.Right; x++)
        {
            var c = source.GetPixel(x, y); var key = (c.R / 16, c.G / 16, c.B / 16);
            quantizedPixels[(y-rect.Top)*rect.Width+x-rect.Left]=Pack(key);
            if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = new Bucket();
            bucket.Add(c, x, y, LocalEdge(source, x, y) >= 42,
                x <= rect.Left + 1 || x >= rect.Right - 2 || y <= rect.Top + 1 || y >= rect.Bottom - 2);
        }
        var ring = new List<Color>();
        for (var x = scan.Left; x < scan.Right; x++) { ring.Add(source.GetPixel(x, scan.Top)); ring.Add(source.GetPixel(x, scan.Bottom - 1)); }
        for (var y = scan.Top + 1; y < scan.Bottom - 1; y++) { ring.Add(source.GetPixel(scan.Left, y)); ring.Add(source.GetPixel(scan.Right - 1, y)); }
        // OCR rectangles can be almost exactly glyph-tight.  In that case a large white
        // glyph core can be the dominant *interior* bucket and must not be called the
        // background.  Corners remain source-surface evidence.  Conversely, a control's
        // corners remain inside its panel even when the exterior ring sees the scene.
        var corners=new List<Color>();
        var patchX=Math.Clamp(rect.Width/10,1,4);var patchY=Math.Clamp(rect.Height/8,1,3);
        foreach(var y0 in new[]{rect.Top,Math.Max(rect.Top,rect.Bottom-patchY)})
        foreach(var x0 in new[]{rect.Left,Math.Max(rect.Left,rect.Right-patchX)})
            for(var y=y0;y<Math.Min(rect.Bottom,y0+patchY);y++)
            for(var x=x0;x<Math.Min(rect.Right,x0+patchX);x++)corners.Add(source.GetPixel(x,y));
        var clusters = buckets.Select(entry => entry.Value.Finish(rect,Pack(entry.Key))).OrderByDescending(x => x.Count).ToArray();
        var interiorBackground = clusters.OrderByDescending(c => c.Count * (1.15 - Math.Min(.85, c.EdgeCount / (double)Math.Max(1, c.Count)))).First().Color;
        var ringBackground = RobustColor(ring);
        var cornerBackground=RobustColor(corners);
        // A text fill can dominate the rectangle interior (Difficulty), while an inflated
        // ring can leave a narrow control and sample the scene outside it (FW buttons).
        // Surface pixels have the distinguishing topology: they reach the OCR rectangle
        // perimeter and are locally smoother than glyph edges.  Prefer that bounded,
        // border-supported surface; use the exterior ring only when the rectangle supplies
        // no credible surface evidence.  This is image-generic and never inspects text or
        // fixture identity.
        var perimeter=Math.Max(1,rect.Width*2+rect.Height*2-4);
        var surfaceCluster=clusters
            .Where(c=>c.BorderCount>=Math.Max(2,(int)Math.Ceiling(perimeter*.01)) &&
                      c.EdgeCount/(double)Math.Max(1,c.Count)<=.72)
            .OrderByDescending(c=>
            {
                var edge=c.EdgeCount/(double)Math.Max(1,c.Count);
                var border=c.BorderCount/(double)perimeter;
                return Math.Log2(c.Count+1)*(.30+Math.Min(1,border*8))*(1-edge);
            })
            .FirstOrDefault();
        var background = surfaceCluster?.Color ?? ringBackground;
        var candidates = clusters.Select(c =>
        {
            var contrast = Contrast(c.Color, background);
            var distance = ColorDistance(c.Color, background);
            var occupancy = c.Count / (double)area;
            var edge = c.EdgeCount / (double)Math.Max(1, c.Count);
            var span = Math.Min(1, (c.RowSpan / (double)Math.Max(1, rect.Height) + c.ColumnSpan / (double)Math.Max(1, rect.Width)) / 1.2);
            var occupancyGate = occupancy is >= .002 and <= .36 ? 1d : occupancy < .002 ? .12 : .08;
            var score = Math.Log2(c.Count + 1) * (distance / 255d) * Math.Max(.15, contrast - .75) *
                        (.35 + edge) * (.4 + span) * occupancyGate;
            return c with { Score = score };
        }).OrderByDescending(x => x.Score).ToArray();
        // A narrow horizontal surface/illustration edge may cross the OCR rectangle.
        // It cannot explain glyphs occupying most of its height. Keep punctuation-only
        // observations unchanged unless a separate tall, edge-supported run exists.
        var tallGlyphs=candidates.Where(c=>c.RowSpan>=rect.Height*.45 &&
            c.ColumnSpan>=rect.Width*.35 && c.EdgeCount/(double)Math.Max(1,c.Count)>=.45 &&
            c.Count>=area*.012 && ColorDistance(c.Color,background)>=40).ToArray();
        bool SurfaceStripe(Cluster c)=>tallGlyphs.Length>0 && c.RowSpan<rect.Height*.23 &&
            c.ColumnSpan>rect.Width*.55 && (c.PixelBounds.Top<=rect.Top+2 || c.PixelBounds.Bottom>=rect.Bottom-2);
        var foregroundCluster = candidates.FirstOrDefault(c => !SurfaceStripe(c) &&
            ColorDistance(c.Color, background) >= 40) ?? candidates[0];
        var rawForeground=foregroundCluster;
        // Bright chromatic fills need not be smaller than their dark outline.
        // Require nested source bounds, increased chroma/luminance, and actual
        // contact with the outer layer. Neutral antialias ramps cannot enter.
        float Chroma(Color c)=>Math.Max(c.R,Math.Max(c.G,c.B))-Math.Min(c.R,Math.Min(c.G,c.B));
        var chromaticCore=candidates.Where(c=>c.QuantizedKey!=foregroundCluster.QuantizedKey &&
            c.Count>=area*.025 && c.Count<=foregroundCluster.Count*3 &&
            c.RowSpan>=foregroundCluster.RowSpan*.6 && c.ColumnSpan>=foregroundCluster.ColumnSpan*.6 &&
            c.PixelBounds.Left>=foregroundCluster.PixelBounds.Left &&
            c.PixelBounds.Right<=foregroundCluster.PixelBounds.Right &&
            c.PixelBounds.Top>=foregroundCluster.PixelBounds.Top &&
            c.PixelBounds.Bottom<=foregroundCluster.PixelBounds.Bottom &&
            Chroma(c.Color)>=Chroma(foregroundCluster.Color)+90 &&
            Luminance(c.Color)>=Luminance(foregroundCluster.Color)+.20 &&
            c.Score>=foregroundCluster.Score*.12 &&
            LayerContactRatio(quantizedPixels,rect.Width,rect.Height,c.Color,foregroundCluster.Color)>=.15)
            .OrderByDescending(c=>Chroma(c.Color)).ThenByDescending(c=>c.Count).FirstOrDefault();
        if(chromaticCore is not null)foregroundCluster=chromaticCore;
        // Outlined glyphs contain two legitimate, high-contrast layers.  The outline is
        // often the larger cluster and can therefore win the raw score.  A fill is the
        // nested layer: it spans the same text run, is smaller, and a larger fraction of
        // its pixels touch the outer layer than the reverse.  Resolve only that strongly
        // evidenced topology; otherwise preserve the raw winner.
        var nestedCore = candidates
            .Where(c=>c.QuantizedKey!=foregroundCluster.QuantizedKey &&
                      c.Count>=foregroundCluster.Count*.12 && c.Count<=foregroundCluster.Count*.82 &&
                      c.Count>=area*.015 &&
                      c.RowSpan>=foregroundCluster.RowSpan*.55 && c.ColumnSpan>=foregroundCluster.ColumnSpan*.55 &&
                      c.Score>=foregroundCluster.Score*.08 &&
                      // Anti-alias ramps are often 40-250 RGB-Manhattan units away from
                      // the core.  Only a genuinely high-contrast fill/outline pair may
                      // reverse the raw cluster winner.
                      ColorDistance(c.Color,foregroundCluster.Color)>=300 &&
                      ColorDistance(c.Color,background)>=40)
            .Select(c=>new
            {
                Cluster=c,
                InnerContact=LayerContactRatio(quantizedPixels,rect.Width,rect.Height,c.Color,foregroundCluster.Color),
                OuterContact=LayerContactRatio(quantizedPixels,rect.Width,rect.Height,foregroundCluster.Color,c.Color)
            })
            .Where(x=>x.InnerContact>=.08 && x.InnerContact>=x.OuterContact*1.55)
            .OrderByDescending(x=>x.InnerContact/(Math.Max(.01,x.OuterContact)))
            .ThenByDescending(x=>x.Cluster.Score)
            .FirstOrDefault();
        if(chromaticCore is null && nestedCore is not null)foregroundCluster=nestedCore.Cluster;
        var foreground = foregroundCluster.Color;
        var fgOccupancy = foregroundCluster.Count / (float)area;
        var edgeRatio = foregroundCluster.EdgeCount / (float)Math.Max(1, foregroundCluster.Count);
        var contrastRatio = Contrast(foreground, background);
        var confidence = (float)Math.Clamp(
            .24 + Math.Min(.32, foregroundCluster.Count / Math.Max(8d, area * .11)) * .32 +
            Math.Min(.22, edgeRatio * .24) + Math.Min(.28, Math.Max(0, contrastRatio - 1) / 8), 0, 1);
        if (contrastRatio < 1.55 || fgOccupancy > .36f) confidence *= .35f;

        var adjacency = NeighborColorEvidence(source, rect, foreground, background);
        return new(foreground.ToArgb(), Hex(foreground), confidence, background.ToArgb(), Hex(background),
            Math.Max(ColorDistance(cornerBackground,interiorBackground),ColorDistance(cornerBackground,ringBackground))<=72 ? .84f : .64f,
            fgOccupancy, edgeRatio, (float)contrastRatio, adjacency.Outline.ToArgb(), Hex(adjacency.Outline),
            adjacency.OutlineConfidence, adjacency.OutlineWidth, adjacency.Shadow.ToArgb(), Hex(adjacency.Shadow),
            adjacency.ShadowConfidence, chromaticCore is not null?"SOURCE_NESTED_CHROMATIC_FILL":rawForeground!=foregroundCluster?"SOURCE_NESTED_FILL":"EDGE_SUPPORTED_QUANTIZED_GLYPH_CLUSTER");
    }

    private sealed class Bucket
    {
        private long r, g, b; private int count, edge, border;
        private int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        internal void Add(Color c, int x, int y, bool isEdge, bool isBorder)
        { r += c.R; g += c.G; b += c.B; count++; if (isEdge) edge++; if (isBorder) border++;
          minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        internal Cluster Finish(Rectangle rect,int quantizedKey) => new(quantizedKey,
            Color.FromArgb((int)(r / count), (int)(g / count), (int)(b / count)),
            count, edge, border, maxY - minY + 1, maxX - minX + 1, 0) { PixelBounds=Rectangle.FromLTRB(minX,minY,maxX+1,maxY+1) };
    }

    private static int Pack((int R,int G,int B) key)=>(key.R<<8)|(key.G<<4)|key.B;

    private static bool QuantizedNear(int key,Color color)
    {
        var r=((key>>8)&15)*16+8;var g=((key>>4)&15)*16+8;var b=(key&15)*16+8;
        return Math.Abs(r-color.R)+Math.Abs(g-color.G)+Math.Abs(b-color.B)<=55;
    }

    private static double LayerContactRatio(int[] pixels,int width,int height,Color core,Color layer)
    {
        var corePixels=0;var touching=0;
        for(var y=0;y<height;y++)for(var x=0;x<width;x++)
        {
            if(!QuantizedNear(pixels[y*width+x],core))continue;
            corePixels++;var touches=false;
            for(var oy=-2;oy<=2&&!touches;oy++)for(var ox=-2;ox<=2;ox++)
            {
                if(ox==0&&oy==0||ox*ox+oy*oy>4)continue;
                var xx=x+ox;var yy=y+oy;
                if(xx<0||yy<0||xx>=width||yy>=height)continue;
                if(QuantizedNear(pixels[yy*width+xx],layer)){touches=true;break;}
            }
            if(touches)touching++;
        }
        return corePixels==0?0:touching/(double)corePixels;
    }

    private sealed record Adjacency(Color Outline, float OutlineConfidence, float OutlineWidth,
        Color Shadow, float ShadowConfidence);

    private static Adjacency NeighborColorEvidence(ReadOnlyBitmapPixelBuffer source, Rectangle rect, Color foreground, Color background)
    {
        var outlineColors = new List<Color>(); var shadowColors = new List<Color>(); var coreCount = 0;
        var radius = Math.Clamp((int)MathF.Round(rect.Height * .11f), 1, 5);
        for (var y = rect.Top; y < rect.Bottom; y++)
        for (var x = rect.Left; x < rect.Right; x++)
        {
            var c = source.GetPixel(x, y);
            if (ColorDistance(c, foreground) > Math.Min(86, ColorDistance(c, background) + 12)) continue;
            coreCount++;
            for (var oy = -radius; oy <= radius; oy++) for (var ox = -radius; ox <= radius; ox++)
            {
                if (ox == 0 && oy == 0 || ox * ox + oy * oy > radius * radius) continue;
                var xx = x + ox; var yy = y + oy;
                if (xx < 0 || yy < 0 || xx >= source.Width || yy >= source.Height) continue;
                var n = source.GetPixel(xx, yy);
                if (ColorDistance(n, foreground) < 46 || ColorDistance(n, background) < 34) continue;
                outlineColors.Add(n);
                if (ox >= 0 && oy >= 0) shadowColors.Add(n);
            }
        }
        var outline = outlineColors.Count == 0 ? Color.Transparent : RobustColor(outlineColors);
        var outlineConfidence = coreCount == 0 ? 0 : Math.Clamp(outlineColors.Count / (float)(coreCount * Math.Max(2, radius * 3)), 0, 1);
        if (ColorDistance(outline, foreground) < 70 || ColorDistance(outline, background) < 25) outlineConfidence *= .35f;
        var shadow = shadowColors.Count == 0 ? Color.Transparent : RobustColor(shadowColors);
        var directional = outlineColors.Count == 0 ? 0 : shadowColors.Count / (float)outlineColors.Count;
        var shadowConfidence = outlineConfidence * Math.Clamp((directional - .26f) * 1.9f, 0, 1);
        return new(outline, outlineConfidence, Math.Max(1, radius * .62f), shadow, shadowConfidence);
    }

    private static VisualStyleOwnerR2 BuildStyleOwner(SourceStyleEvidenceR2[] group)
    {
        var reliable = group.Where(x => x.DirectConfidence >= .36f).ToArray();
        var source = reliable.Length > 0 ? reliable : group;
        var foreground = WeightedColor(source.Select(x => (Color.FromArgb(x.DirectForegroundArgb),
            Math.Max(.05f, x.DirectConfidence))).ToArray());
        var confidence = source.Select(x => x.DirectConfidence).DefaultIfEmpty().Average();
        var outlines = source.Where(x => x.OutlineDetected).ToArray();
        var outline = outlines.Length >= Math.Max(1, (int)Math.Ceiling(source.Length * .34));
        var outlineColor = outline ? WeightedColor(outlines.Select(x =>
            (Color.FromArgb(x.OutlineArgb), Math.Max(.05f, x.OutlineConfidence))).ToArray()) : Color.Transparent;
        var shadows = source.Where(x => x.ShadowDetected).ToArray();
        var shadow = shadows.Length >= Math.Max(1, (int)Math.Ceiling(source.Length * .4));
        var shadowColor = shadow ? WeightedColor(shadows.Select(x =>
            (Color.FromArgb(x.ShadowArgb), Math.Max(.05f, x.ShadowConfidence))).ToArray()) : Color.Transparent;
        return new(StableId("STYLE", group.Select(x => x.BlockId)), group.Select(x => x.BlockId).Order(StringComparer.Ordinal).ToArray(),
            Mode(group.Select(x => x.VisualRole)), foreground.ToArgb(), Hex(foreground), confidence,
            Mode(group.Select(x => x.Polarity)), outline, outlineColor.ToArgb(), Hex(outlineColor),
            outlines.Select(x => x.OutlineWidth).DefaultIfEmpty().Average(), shadow, shadowColor.ToArgb(), Hex(shadowColor),
            Mode(group.Select(x => x.Weight)), Mode(group.Select(x => x.RelativeSize)),
            Mode(group.Select(x => x.Alignment)), Median(group.Select(x => x.MedianLineHeight).ToArray()),
            "SOURCE_GEOMETRY_STYLE_COMPATIBILITY_AND_ORDERED_LINEAGE");
    }

    private static VisualContainerOwnerR2 BuildContainerOwner(SourceStyleEvidenceR2[] group,
        IReadOnlyDictionary<string, VisualBlock> blocks)
    {
        var bounds = group.Select(x => blocks[x.BlockId].Bounds).Aggregate(RectangleF.Union);
        var surface = WeightedColor(group.Select(x => (Color.FromArgb(x.BackgroundArgb),
            Math.Max(.05f, x.BackgroundConfidence))).ToArray());
        var dispersion = group.Select(x => ColorDistance(Color.FromArgb(x.BackgroundArgb), surface)).DefaultIfEmpty().Average();
        var surfaceClass = dispersion < 34 ? SourceSurfaceClassR2.Flat : dispersion < 78 ?
            SourceSurfaceClassR2.Gradient : dispersion < 132 ? SourceSurfaceClassR2.Textured : SourceSurfaceClassR2.Complex;
        return new(StableId("CONT", group.Select(x => x.BlockId)), group.Select(x => x.BlockId).Order(StringComparer.Ordinal).ToArray(),
            Rect(bounds), surface.ToArgb(), Hex(surface), (float)Math.Clamp(1d - dispersion / 220d, .08d, 1d), surfaceClass,
            "LOCAL_SOURCE_SURFACE_AND_GEOMETRY_COMPONENT");
    }

    private static (Color Color, float Confidence, SourceStyleFallbackLevelR2 Level, string Reason) ResolveForeground(
        SourceStyleEvidenceR2 evidence, VisualStyleOwnerR2 styleOwner, VisualContainerOwnerR2 container,
        IReadOnlyList<SourceStyleEvidenceR2> all, IReadOnlyDictionary<string, VisualBlock> blocks, Size canvas)
    {
        if (evidence.DirectConfidence >= .55f)
            return (Color.FromArgb(evidence.DirectForegroundArgb), evidence.DirectConfidence,
                SourceStyleFallbackLevelR2.DirectOwner, "DIRECT_OWNER_CONFIDENCE_GE_0_55");
        if (styleOwner.UnderlyingBlockIds.Count > 1 && styleOwner.Confidence >= .42f)
            return (Color.FromArgb(styleOwner.ForegroundArgb), styleOwner.Confidence,
                SourceStyleFallbackLevelR2.SiblingConsensus, "COMPATIBLE_STYLE_SIBLING_CONSENSUS");
        var members = container.UnderlyingBlockIds.Select(id => all.First(x => x.BlockId == id))
            .Where(x => x.DirectConfidence >= .38f).ToArray();
        if (members.Length > 1)
        {
            var color = WeightedColor(members.Select(x => (Color.FromArgb(x.DirectForegroundArgb), x.DirectConfidence)).ToArray());
            return (color, members.Select(x => x.DirectConfidence).Average(),
                SourceStyleFallbackLevelR2.ContainerConsensus, "SAME_CONTAINER_FOREGROUND_CONSENSUS");
        }
        var target = blocks[evidence.BlockId];
        var adjacent = all.Where(x => x.BlockId != evidence.BlockId && x.VisualRole == evidence.VisualRole && x.DirectConfidence >= .5f)
            .OrderBy(x => Distance(target.Bounds, blocks[x.BlockId].Bounds)).FirstOrDefault();
        if (adjacent is not null && Distance(target.Bounds, blocks[adjacent.BlockId].Bounds) <= Math.Max(canvas.Width, canvas.Height) * .22f)
            return (Color.FromArgb(adjacent.DirectForegroundArgb), adjacent.DirectConfidence * .82f,
                SourceStyleFallbackLevelR2.AdjacentCompatible, "NEAREST_VISUAL_ROLE_COMPATIBLE_OWNER");
        var background = Color.FromArgb(evidence.BackgroundArgb);
        var compatible = background.GetBrightness() < .53f ? Color.White : Color.Black;
        return (compatible, .28f, SourceStyleFallbackLevelR2.ParentSurfaceContrast,
            "SOURCE_COMPATIBLE_CONTRAST_FROM_PARENT_SURFACE");
    }

    private static bool StyleCompatible(string aId, string bId,
        IReadOnlyDictionary<string, SourceStyleEvidenceR2> evidence,
        IReadOnlyDictionary<string, VisualBlock> blocks, Size canvas)
    {
        var a = evidence[aId]; var b = evidence[bId];
        if (a.Polarity != b.Polarity || a.RelativeSize != b.RelativeSize) return false;
        if (Math.Max(a.MedianLineHeight, b.MedianLineHeight) / Math.Max(1, Math.Min(a.MedianLineHeight, b.MedianLineHeight)) > 1.48f) return false;
        if (ColorDistance(Color.FromArgb(a.DirectForegroundArgb), Color.FromArgb(b.DirectForegroundArgb)) > 118) return false;
        var ba = blocks[aId].Bounds; var bb = blocks[bId].Bounds;
        var row = Math.Abs(CenterY(ba) - CenterY(bb)) <= Math.Max(ba.Height, bb.Height) * 1.35f && HorizontalGap(ba, bb) <= canvas.Width * .24f;
        var column = HorizontalOverlap(ba, bb) >= .28f && VerticalGap(ba, bb) <= Math.Max(ba.Height, bb.Height) * 4.5f;
        return row || column;
    }

    private static bool ContainerCompatible(string aId, string bId,
        IReadOnlyDictionary<string, SourceStyleEvidenceR2> evidence,
        IReadOnlyDictionary<string, VisualBlock> blocks, Size canvas)
    {
        var a = evidence[aId]; var b = evidence[bId];
        if (ColorDistance(Color.FromArgb(a.BackgroundArgb), Color.FromArgb(b.BackgroundArgb)) > 105) return false;
        var ba = blocks[aId].Bounds; var bb = blocks[bId].Bounds;
        return Distance(ba, bb) <= Math.Max(canvas.Width, canvas.Height) * .105f &&
               (HorizontalOverlap(ba, bb) >= .16f || VerticalOverlap(ba, bb) >= .2f ||
                Math.Abs(CenterY(ba) - CenterY(bb)) <= Math.Max(ba.Height, bb.Height) * 1.4f);
    }

    private static IReadOnlyList<string[]> ConnectedGroups(IReadOnlyList<SourceStyleEvidenceR2> evidence,
        Func<string, string, bool> compatible)
    {
        var remaining = evidence.Select(x => x.BlockId).ToHashSet(StringComparer.Ordinal);
        var result = new List<string[]>();
        foreach (var seed in evidence.Select(x => x.BlockId).Order(StringComparer.Ordinal))
        {
            if (!remaining.Remove(seed)) continue;
            var group = new List<string> { seed }; var queue = new Queue<string>(); queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var candidate in remaining.Order(StringComparer.Ordinal).ToArray())
                    if (compatible(current, candidate)) { remaining.Remove(candidate); group.Add(candidate); queue.Enqueue(candidate); }
            }
            result.Add(group.Order(StringComparer.Ordinal).ToArray());
        }
        return result;
    }

    private static void SaveEvidence(Bitmap source, CorePipelineDocument document, SourceStyleAnalysisR2 analysis,
        string output, SourceStyleRenderModeR2 mode)
    {
        File.WriteAllText(Path.Combine(output, "R2-SOURCE-STYLE-OBSERVER.json"), JsonSerializer.Serialize(new
        {
            Contract = "R2_SOURCE_STYLE_OBSERVER_V1", Mode = mode.ToString(), BitmapMutation = false,
            CoreBlockIdMutation = false, TranslationOwnerMutation = false, analysis.Evidence
        }, JsonOptions));
        File.WriteAllText(Path.Combine(output, "R2-STYLE-OWNER-TRACE.json"), JsonSerializer.Serialize(analysis.StyleOwners, JsonOptions));
        File.WriteAllText(Path.Combine(output, "R2-CONTAINER-OWNER-TRACE.json"), JsonSerializer.Serialize(analysis.ContainerOwners, JsonOptions));
        File.WriteAllText(Path.Combine(output, "R2-BACKGROUND-SURFACE-OWNER-TRACE.json"), JsonSerializer.Serialize(analysis.SurfaceOwners, JsonOptions));
        File.WriteAllText(Path.Combine(output, "R2-RENDER-PLAN.json"), JsonSerializer.Serialize(new
        {
            Contract = "EXPLICIT_RENDER_PLAN_R2_V1", Mode = mode.ToString(),
            Plans = analysis.Plans.Values.OrderBy(x => x.BlockId)
        }, JsonOptions));
        using var overlay = new Bitmap(source); using var g = Graphics.FromImage(overlay);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var labelFont = new Font("Segoe UI", 10, FontStyle.Bold, GraphicsUnit.Pixel);
        foreach (var block in document.VisualBlocks)
        {
            var plan = analysis.Plans[block.BlockId]; var color = Color.FromArgb(plan.ForegroundArgb);
            using var pen = new Pen(color, 3); g.DrawRectangle(pen, Rectangle.Round(block.Bounds));
            var label = $"{block.BlockId} {plan.Foreground} {plan.FallbackLevel}";
            var size = g.MeasureString(label, labelFont); var y = Math.Max(0, block.Bounds.Top - size.Height);
            using var back = new SolidBrush(Color.FromArgb(210, Color.Black)); g.FillRectangle(back, block.Bounds.Left, y, size.Width, size.Height);
            using var brush = new SolidBrush(color); g.DrawString(label, labelFont, brush, block.Bounds.Left, y);
        }
        overlay.Save(Path.Combine(output, "R2-STYLE-OWNER-OVERLAY.png"));
    }

    private static SourceVisualRoleR2 InferRole(Size canvas, VisualBlock block, float lineHeight, float median)
    {
        if(block.SourceRole?.Role=="Prose")return SourceVisualRoleR2.Body;
        if(block.SourceRole?.Role=="Control")return SourceVisualRoleR2.Control;
        if(block.SourceRole?.Role=="Heading")return SourceVisualRoleR2.Heading;
        if (block.RoleHint == "PossibleControl") return SourceVisualRoleR2.Control;
        if (block.RoleHint == "PossibleTitle" || lineHeight >= median * 1.35f) return SourceVisualRoleR2.Heading;
        if (block.Lines.Count >= 3 || block.LayoutBehavior == BlockLayoutBehavior.Flow) return SourceVisualRoleR2.Body;
        if (lineHeight <= median * .72f) return SourceVisualRoleR2.Metadata;
        if (block.Bounds.Top >= canvas.Height * .58f && block.Bounds.Width >= canvas.Width * .28f) return SourceVisualRoleR2.Dialogue;
        return SourceVisualRoleR2.Unknown;
    }

    private static string InferAlignment(VisualBlock block, SourceVisualRoleR2 role)
    {
        if(block.SourceRole is {Alignment:"Left" or "Center"} sourceRole)return sourceRole.Alignment;
        if (role == SourceVisualRoleR2.Control) return "Center";
        var centers = block.Lines.Select(x => CenterX(x.Bounds)).ToArray();
        var lefts = block.Lines.Select(x => x.Bounds.Left).ToArray();
        var centered = centers.Count(x => Math.Abs(x - CenterX(block.Bounds)) <= Math.Max(4, block.Bounds.Width * .08f));
        var leftVariance = lefts.Length == 0 ? 0 : lefts.Max() - lefts.Min();
        return centered >= Math.Max(1, (int)Math.Ceiling(block.Lines.Count * .65)) &&
               leftVariance > Math.Max(3, block.Bounds.Width * .025f) ? "Center" : "Left";
    }

    private static SourcePolarityR2 Polarity(Color foreground, Color background)
    { var delta = Luminance(foreground) - Luminance(background); return delta >= .16 ? SourcePolarityR2.LightOnDark : delta <= -.16 ? SourcePolarityR2.DarkOnLight : SourcePolarityR2.MidTone; }
    private static int LocalEdge(ReadOnlyBitmapPixelBuffer bitmap, int x, int y)
    {
        var c = bitmap.GetPixel(x, y); var max = 0;
        foreach (var (ox, oy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
        { var xx = Math.Clamp(x + ox, 0, bitmap.Width - 1); var yy = Math.Clamp(y + oy, 0, bitmap.Height - 1); max = Math.Max(max, ColorDistance(c, bitmap.GetPixel(xx, yy))); }
        return max;
    }
    private static Color RobustColor(IReadOnlyList<Color> colors)
    {
        if (colors.Count == 0) return Color.Black;
        var group = colors.GroupBy(c => (c.R / 16, c.G / 16, c.B / 16)).OrderByDescending(x => x.Count()).ThenBy(x => x.Key).First();
        return Color.FromArgb((int)group.Average(x => x.R), (int)group.Average(x => x.G), (int)group.Average(x => x.B));
    }
    private static Color WeightedColor(IReadOnlyList<(Color Color, float Weight)> values)
    {
        if (values.Count == 0) return Color.White; double r = 0, g = 0, b = 0, total = 0;
        foreach (var value in values) { var w = Math.Max(.001, value.Weight); r += value.Color.R * w; g += value.Color.G * w; b += value.Color.B * w; total += w; }
        return Color.FromArgb((int)Math.Clamp(Math.Round(r / total), 0, 255), (int)Math.Clamp(Math.Round(g / total), 0, 255), (int)Math.Clamp(Math.Round(b / total), 0, 255));
    }
    private static Color Blend(Color a, Color b, float weightA) => Color.FromArgb(
        (int)Math.Round(a.R * weightA + b.R * (1 - weightA)), (int)Math.Round(a.G * weightA + b.G * (1 - weightA)), (int)Math.Round(a.B * weightA + b.B * (1 - weightA)));
    private static int ColorDistance(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
    private static double Luminance(Color c) { double F(byte v) { var x = v / 255d; return x <= .03928 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); } return .2126 * F(c.R) + .7152 * F(c.G) + .0722 * F(c.B); }
    private static double Contrast(Color a, Color b) { var la = Luminance(a); var lb = Luminance(b); return (Math.Max(la, lb) + .05) / (Math.Min(la, lb) + .05); }
    private static string Hex(Color c) => c.A == 0 ? "#00000000" : $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    private static string StableId(string prefix, IEnumerable<string> ids) { var value = string.Join("\n", ids.Order(StringComparer.Ordinal)); return prefix + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12]; }
    private static T Mode<T>(IEnumerable<T> values) where T : notnull => values.GroupBy(x => x).OrderByDescending(x => x.Count()).ThenBy(x => x.Key!.ToString(), StringComparer.Ordinal).First().Key;
    private static float Median(float[] values) { if (values.Length == 0) return 16; Array.Sort(values); return values.Length % 2 == 1 ? values[values.Length / 2] : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2f; }
    private static Rectangle Clamp(Rectangle r, Size s) { var l = Math.Clamp(r.Left, 0, s.Width); var t = Math.Clamp(r.Top, 0, s.Height); var rr = Math.Clamp(r.Right, l, s.Width); var bb = Math.Clamp(r.Bottom, t, s.Height); return Rectangle.FromLTRB(l, t, rr, bb); }
    private static object Rect(RectangleF r) => new { r.X, r.Y, r.Width, r.Height };
    private static float CenterX(RectangleF r) => (r.Left + r.Right) / 2f; private static float CenterY(RectangleF r) => (r.Top + r.Bottom) / 2f;
    private static float HorizontalGap(RectangleF a, RectangleF b) => Math.Max(0, Math.Max(a.Left, b.Left) - Math.Min(a.Right, b.Right));
    private static float VerticalGap(RectangleF a, RectangleF b) => Math.Max(0, Math.Max(a.Top, b.Top) - Math.Min(a.Bottom, b.Bottom));
    private static float HorizontalOverlap(RectangleF a, RectangleF b) => Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) / Math.Max(1, Math.Min(a.Width, b.Width));
    private static float VerticalOverlap(RectangleF a, RectangleF b) => Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top)) / Math.Max(1, Math.Min(a.Height, b.Height));
    private static float Distance(RectangleF a, RectangleF b) => HorizontalGap(a, b) + VerticalGap(a, b);

}
