namespace ScreenshotTranslationUiTester.CorePipelineV2;

internal sealed record NativeFootnoteComponent(int Pixels, Rectangle Bounds, bool Accepted, string Reason);
internal sealed record NativeFootnoteMaterialPlan(bool Applied, string Reason, RectangleF SourceBounds,
    Color Background, Color SourceFill, FontStyle SuggestedWeight, float Confidence,
    IReadOnlyList<Point> CorePixels, IReadOnlyList<Point> AntialiasPixels,
    IReadOnlyList<NativeFootnoteComponent> Components, float PaperFraction, float InkHorizontalCoverage)
{
    public IEnumerable<Point> AllPixels => CorePixels.Concat(AntialiasPixels);
    public float OuterRingPaperFraction { get; init; }
    public int OuterRingPixels { get; init; }
    public bool UsedTightBoundsPaperEvidence { get; init; }
    public bool OutlineRecommended => false;
}

/// <summary>
/// Separates small dark attribution glyphs from proven light paper. Its only
/// output authority is the original source polygon; layout expansion is ignored.
/// </summary>
internal static class NativeFootnoteMaterial
{
    internal static NativeFootnoteMaterialPlan Analyze(Bitmap source, VisualBlock block,
        IReadOnlyList<VisualBlock> blocks, NativeVisualLayoutPlan native)
    {
        NativeFootnoteMaterialPlan Reject(string reason) => new(false, reason, block.Bounds,
            Color.Transparent, Color.Transparent, FontStyle.Regular, 0, [], [], [], 0, 0);
        if (!native.Applied || native.Kind != "IndependentFootnote" || native.SourceBounds != block.Bounds ||
            block.Lines.Count != 1 || block.Lines[0].Confidence < .65f)
            return Reject("NO_PROVEN_NATIVE_FOOTNOTE");
        var line = block.Lines[0]; var polygon = line.Polygon;
        if (polygon.Length < 3) return Reject("INVALID_SOURCE_POLYGON");
        var bounds = Rectangle.Intersect(Rectangle.Ceiling(block.Bounds), new Rectangle(Point.Empty, source.Size));
        if (bounds.Width < 8 || bounds.Height < 3) return Reject("INVALID_SOURCE_BOUNDS");
        if (blocks.Any(x => x.BlockId != block.BlockId && RectangleF.Inflate(x.Bounds, 2, 2).IntersectsWith(block.Bounds)))
            return Reject("NEIGHBOR_TEXT_INTERSECTS_SOURCE");
        var eligible = new bool[bounds.Width, bounds.Height]; var pixels = new Color[bounds.Width, bounds.Height];
        var paper = new List<Color>(); var area = 0;
        for (var y = 0; y < bounds.Height; y++)
        for (var x = 0; x < bounds.Width; x++)
        {
            if (!Contains(polygon, bounds.Left + x + .5f, bounds.Top + y + .5f)) continue;
            eligible[x, y] = true; area++;
            var c = source.GetPixel(bounds.Left + x, bounds.Top + y); pixels[x, y] = c;
            if (Neutral(c) && Math.Min(c.R, Math.Min(c.G, c.B)) >= 220) paper.Add(c);
        }
        var paperFraction = paper.Count / (float)Math.Max(1, area);
        // A tightly detected line can contain more ink than padding. A narrow
        // exterior ring supplies independent material evidence, never write
        // authority. This alternative requires stronger OCR confidence.
        var ringBounds = Rectangle.Intersect(Rectangle.Inflate(bounds, 2, 2), new Rectangle(Point.Empty, source.Size));
        var ringArea = 0; var ringPaper = 0;
        for (var y = ringBounds.Top; y < ringBounds.Bottom; y++)
        for (var x = ringBounds.Left; x < ringBounds.Right; x++)
        {
            if (bounds.Contains(x, y)) continue;
            ringArea++;
            var c = source.GetPixel(x, y);
            if (Math.Min(c.R, Math.Min(c.G, c.B)) >= 220 &&
                Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) <= 26) ringPaper++;
        }
        var ringFraction = ringPaper / (float)Math.Max(1, ringArea);
        var tightPaper = paperFraction >= .55f && line.Confidence >= .85f && ringArea >= 24 && ringFraction >= .95f;
        if ((paperFraction < .65f && !tightPaper) || paper.Count < 16)
            return new(false, "SOURCE_POLYGON_NOT_PAPER_DOMINATED", block.Bounds, Color.Transparent,
                Color.Transparent, FontStyle.Regular, 0, [], [], [], paperFraction, 0)
                { OuterRingPaperFraction = ringFraction, OuterRingPixels = ringArea };
        var background = Median(paper);
        var core = new bool[bounds.Width, bounds.Height]; var visited = new bool[bounds.Width, bounds.Height];
        for (var y = 0; y < bounds.Height; y++)
        for (var x = 0; x < bounds.Width; x++)
        {
            var c = pixels[x, y];
            core[x, y] = eligible[x, y] && Neutral(c) && Math.Max(c.R, Math.Max(c.G, c.B)) <= 190 && Delta(c, background) >= 120;
        }
        var accepted = new List<Point>(); var components = new List<NativeFootnoteComponent>();
        for (var y = 0; y < bounds.Height; y++)
        for (var x = 0; x < bounds.Width; x++)
        {
            if (!core[x, y] || visited[x, y]) continue;
            var component = new List<Point>(); var queue = new Queue<Point>(); queue.Enqueue(new(x, y)); visited[x, y] = true;
            while (queue.TryDequeue(out var p))
            {
                component.Add(p);
                for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++)
                {
                    var nx = p.X + dx; var ny = p.Y + dy;
                    if (nx < 0 || ny < 0 || nx >= bounds.Width || ny >= bounds.Height || visited[nx, ny] || !core[nx, ny]) continue;
                    visited[nx, ny] = true; queue.Enqueue(new(nx, ny));
                }
            }
            var local = Rectangle.FromLTRB(component.Min(p => p.X), component.Min(p => p.Y), component.Max(p => p.X) + 1, component.Max(p => p.Y) + 1);
            // A tiny clipped part of a shoe/border can resemble punctuation
            // inside the OCR polygon. Continuity into dark material outside the
            // source polygon is independent evidence that it is not an isolated
            // glyph. Interior punctuation remains eligible.
            var clippedExteriorMaterial = component.Count <= 2 && component.Any(p =>
            {
                for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++)
                {
                    var sx = bounds.Left + p.X + dx; var sy = bounds.Top + p.Y + dy;
                    if (sx < 0 || sy < 0 || sx >= source.Width || sy >= source.Height ||
                        Contains(polygon, sx + .5f, sy + .5f)) continue;
                    var exterior = source.GetPixel(sx, sy);
                    if (Neutral(exterior) && Math.Max(exterior.R, Math.Max(exterior.G, exterior.B)) <= 190 &&
                        Delta(exterior, background) >= 120) return true;
                }
                return false;
            });
            var acceptedShape = local.Height <= Math.Max(3, bounds.Height * .72f) && local.Width <= bounds.Width * .6f &&
                component.Count <= area * .12f && local.Height > 0 && !clippedExteriorMaterial;
            components.Add(new(component.Count, new Rectangle(bounds.Left + local.X, bounds.Top + local.Y, local.Width, local.Height), acceptedShape,
                acceptedShape ? "SMALL_DARK_GLYPH_COMPONENT" : clippedExteriorMaterial ? "CLIPPED_EXTERIOR_MATERIAL_REJECTED" : "SOURCE_ART_OR_BORDER_SHAPE_REJECTED"));
            if (acceptedShape) accepted.AddRange(component);
        }
        var distinctColumns = accepted.Select(x => x.X).Distinct().Count();
        var horizontalCoverage = accepted.Count > 0 ? (accepted.Max(p => p.X) - accepted.Min(p => p.X) + 1f) / bounds.Width : 0;
        if (accepted.Count < 12 || components.Count(x => x.Accepted && x.Pixels >= 2) < 4 ||
            horizontalCoverage < .65f || distinctColumns < bounds.Width * .22f ||
            accepted.Count > area * .3f)
            return new(false, "NO_DISTRIBUTED_TEXT_MATERIAL", block.Bounds, background, Color.Transparent,
                FontStyle.Regular, 0, [], [], components, paperFraction, horizontalCoverage)
                { OuterRingPaperFraction = ringFraction, OuterRingPixels = ringArea, UsedTightBoundsPaperEvidence = paperFraction < .65f && tightPaper };
        var approvedCore = new bool[bounds.Width, bounds.Height]; foreach (var p in accepted) approvedCore[p.X, p.Y] = true;
        var antialias = new List<Point>();
        for (var y = 0; y < bounds.Height; y++)
        for (var x = 0; x < bounds.Width; x++)
        {
            if (!eligible[x, y] || approvedCore[x, y] || core[x, y]) continue;
            var c = pixels[x, y];
            if (!Neutral(c) || Delta(c, background) < 18 || c.GetBrightness() >= background.GetBrightness()) continue;
            var adjacent = false;
            for (var dy = -1; dy <= 1 && !adjacent; dy++) for (var dx = -1; dx <= 1; dx++)
            {
                var nx = x + dx; var ny = y + dy;
                if (nx >= 0 && ny >= 0 && nx < bounds.Width && ny < bounds.Height && approvedCore[nx, ny]) { adjacent = true; break; }
            }
            if (adjacent) antialias.Add(new(bounds.Left + x, bounds.Top + y));
        }
        var fill = Median(accepted.Select(p => pixels[p.X, p.Y]).ToArray());
        return new(true, "LOCAL_DARK_FOOTNOTE_GLYPHS_ON_PROVEN_PAPER", block.Bounds, background, fill,
            FontStyle.Regular, Math.Clamp(.75f + horizontalCoverage * .1f, .75f, .9f),
            accepted.Select(p => new Point(bounds.Left + p.X, bounds.Top + p.Y)).ToArray(), antialias,
            components, paperFraction, horizontalCoverage)
            { OuterRingPaperFraction = ringFraction, OuterRingPixels = ringArea, UsedTightBoundsPaperEvidence = paperFraction < .65f && tightPaper };
    }

    private static bool Neutral(Color c) => Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) <= 40;
    private static int Delta(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
    private static Color Median(IReadOnlyList<Color> colors)
    {
        int M(Func<Color, int> channel) { var values = colors.Select(channel).Order().ToArray(); return values[values.Length / 2]; }
        return Color.FromArgb(M(c => c.R), M(c => c.G), M(c => c.B));
    }
    private static bool Contains(PointF[] polygon, float x, float y)
    {
        var inside = false;
        for (var i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Length];
            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }
}
