namespace ScreenshotTranslationUiTester;

public sealed class RegionEditorSession
{
    private List<RecognitionRegion> _regions;
    private readonly Stack<List<RecognitionRegion>> _undo = new();
    private readonly Stack<List<RecognitionRegion>> _redo = new();
    public IReadOnlyList<RecognitionRegion> Regions => _regions;
    public RegionEditorSession(IEnumerable<RecognitionRegion> regions) => _regions = Clone(regions);
    private static List<RecognitionRegion> Clone(IEnumerable<RecognitionRegion> value) => value.Select(x => x.Clone()).ToList();
    private void Change(Action action) { _undo.Push(Clone(_regions)); _redo.Clear(); action(); }
    private RecognitionRegion Find(string id) => _regions.Single(x => x.RegionId == id);
    public void Create(PointF[] polygon) => Change(() => _regions.Add(new RecognitionRegion { Polygon = polygon, DetectionSources = [DetectionSource.Manual], ManualOverrideFlags = RegionManualOverrideFlags.Geometry }));
    public void Delete(IEnumerable<string> ids) { var set = ids.ToHashSet(); Change(() => _regions.RemoveAll(x => set.Contains(x.RegionId))); }
    public void Move(string id, float dx, float dy) => Change(() => { var r = Find(id); r.Polygon = r.Polygon.Select(x => new PointF(x.X + dx, x.Y + dy)).ToArray(); r.ManualOverrideFlags |= RegionManualOverrideFlags.Geometry; });
    public void Resize(string id, RectangleF bounds) => Change(() => { var r = Find(id); r.Polygon = GeometryV2.RectanglePolygon(bounds); r.ManualOverrideFlags |= RegionManualOverrideFlags.Geometry; });
    public void EditPolygon(string id, PointF[] polygon) => Change(() => { var r = Find(id); r.Polygon = [.. polygon]; r.ManualOverrideFlags |= RegionManualOverrideFlags.Geometry; });
    public void SetRole(string id, RegionRoleType role) => Change(() => { var r = Find(id); r.RoleType = role; r.RoleConfidence = 1; r.ManualOverrideFlags |= RegionManualOverrideFlags.Role; });
    public void SetReadingOrder(string id, int order) => Change(() => { var r = Find(id); r.ReadingOrder = order; r.ManualOverrideFlags |= RegionManualOverrideFlags.ReadingOrder; });
    public void SetIgnored(string id, bool value) => Change(() => { var r = Find(id); r.IsIgnored = value; r.ManualOverrideFlags |= RegionManualOverrideFlags.Ignore; });
    public void SetPreserveOriginal(string id, bool value) => Change(() => { var r = Find(id); r.PreserveOriginal = value; r.ManualOverrideFlags |= RegionManualOverrideFlags.PreserveOriginal; });
    public void ReplaceOcr(string id, IReadOnlyList<OcrEngineBlock> blocks) => Change(() => { var r = Find(id); r.DetectedBlocks = [.. blocks]; r.SourceBlockIds = blocks.Select(x => x.Id).ToList(); r.SourceLinePolygons = blocks.Select(x => x.Polygon.Length >= 3 ? x.Polygon.ToArray() : GeometryV2.RectanglePolygon(x.BoundingBox)).ToList(); r.OcrText = string.Join("\n", blocks.Select(x => x.RawText)); r.CorrectedText = string.Join("\n", blocks.Select(x => x.CorrectedText)); r.StructuredText = r.CorrectedText; r.DetectionSources.Add(DetectionSource.CropOcr); });
    public void Merge(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet(); Change(() => { var selected = _regions.Where(x => set.Contains(x.RegionId)).ToArray(); if (selected.Length < 2) return;
            var first = selected[0]; var box = selected.Select(x => x.BoundingBox).Aggregate(RectangleF.Union); first.Polygon = GeometryV2.RectanglePolygon(box);
            first.SourceBlockIds = selected.SelectMany(x => x.SourceBlockIds).Distinct().ToList(); first.DetectedBlocks = selected.SelectMany(x => x.DetectedBlocks).ToList(); first.SourceLinePolygons = selected.SelectMany(x => x.SourceLinePolygons).Select(x => x.ToArray()).ToList();
            first.StructuredText = string.Join(" ", selected.OrderBy(x => x.ReadingOrder).Select(x => x.StructuredText)); first.ManualOverrideFlags |= RegionManualOverrideFlags.Geometry;
            _regions.RemoveAll(x => x.RegionId != first.RegionId && set.Contains(x.RegionId)); });
    }
    public void Split(string id, IReadOnlyList<PointF[]> polygons) => Change(() => { var source = Find(id); _regions.Remove(source); foreach (var polygon in polygons) { var bounds=GeometryV2.Bounds(polygon); var blocks=source.DetectedBlocks.Where(x => bounds.Contains(new PointF(x.BoundingBox.Left+x.BoundingBox.Width/2, x.BoundingBox.Top+x.BoundingBox.Height/2))).ToList(); var text=string.Join(" ",blocks.Select(x => string.IsNullOrWhiteSpace(x.CorrectedText)?x.RawText:x.CorrectedText)); var child = new RecognitionRegion { Polygon = [.. polygon], SourceBlockIds=blocks.Select(x=>x.Id).ToList(), DetectedBlocks=blocks, SourceLinePolygons=blocks.Select(x=>x.Polygon.Length>=3?x.Polygon.ToArray():GeometryV2.RectanglePolygon(x.BoundingBox)).ToList(), OcrText = string.Join("\n",blocks.Select(x=>x.RawText)), CorrectedText = text, StructuredText = text, RoleType = source.RoleType, RoleConfidence=source.RoleConfidence, ReadingOrder = source.ReadingOrder, DetectionSources = [DetectionSource.Manual], ManualOverrideFlags = source.ManualOverrideFlags | RegionManualOverrideFlags.Geometry }; _regions.Add(child); } });
    public bool Undo() { if (_undo.Count == 0) return false; _redo.Push(Clone(_regions)); _regions = _undo.Pop(); return true; }
    public bool Redo() { if (_redo.Count == 0) return false; _undo.Push(Clone(_regions)); _regions = _redo.Pop(); return true; }
    public void ResetSelected(string id, RecognitionRegion automatic) => Change(() => { var index = _regions.FindIndex(x => x.RegionId == id); if (index >= 0) _regions[index] = automatic.Clone(); });
    public void ResetAllAuto(IEnumerable<RecognitionRegion> automatic) => Change(() => _regions = Clone(automatic));
}

public sealed record RegionRenderWarning(string RegionId, string Message);
public sealed record RenderCopyMetrics(int FullBitmapCloneCount,int FullBitmapBlitCount,
    int RegionBitmapAllocationCount,long TemporaryBitmapBytes,long TotalPixelBytesCopied,
    long NormalRenderMs=0,long FallbackRenderMs=0,int FallbackRegionCount=0,int RegionRoiCount=0);
public sealed record RegionRenderResult(Bitmap Bitmap, IReadOnlyList<RegionRenderWarning> Warnings, long ElapsedMs = 0,
    long MaskMs = 0, long LayoutMs = 0, long DrawMs = 0,
    IReadOnlyList<RegionRenderDiagnostic>? Diagnostics = null,IReadOnlyDictionary<string,RectangleF>? TranslationRenderRects=null,
    RenderCopyMetrics? CopyMetrics=null, RenderResourceMetrics? ResourceMetrics=null) : IDisposable
{
    public void Dispose() => Bitmap.Dispose();
}

public static class LegacyRegionRendererV2
{
    public static RegionRenderResult Render(Bitmap source, IReadOnlyList<RecognitionRegion> regions, RenderSettings settings)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var output = new Bitmap(source); var warnings = new List<RegionRenderWarning>(); using var g = Graphics.FromImage(output);
        long maskTicks=0, layoutTicks=0, drawTicks=0;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        foreach (var r in regions.Where(x => !x.IsIgnored && !x.PreserveOriginal && !string.IsNullOrWhiteSpace(x.TranslationText)))
        {
            var box = RectangleF.Intersect(new RectangleF(PointF.Empty, source.Size), r.RendererTargetRegion.IsEmpty ? r.BoundingBox : r.RendererTargetRegion);
            if (box.Width < 3 || box.Height < 3 || r.Polygon.Length < 3) { warnings.Add(new(r.RegionId, "Renderer target is empty.")); continue; }
            var layoutStarted=System.Diagnostics.Stopwatch.GetTimestamp();
            var size = Math.Max(8, Math.Min(box.Height * .72f, 34) * settings.FontScale);
            Font? fit = null; string text = r.TranslationText;
            var style = r.RoleType is RegionRoleType.Title or RegionRoleType.Header or RegionRoleType.CharacterName or RegionRoleType.Button ? FontStyle.Bold : FontStyle.Regular;
            var layout = RectangleF.Inflate(box, -Math.Min(3, box.Width * .03f), -Math.Min(2, box.Height * .05f));
            for (var candidate = size; candidate >= 7; candidate -= 1) { var f = new Font("Segoe UI", candidate, style, GraphicsUnit.Pixel); var measured = g.MeasureString(text, f, new SizeF(layout.Width, float.MaxValue)); if (measured.Height <= layout.Height) { fit = f; break; } f.Dispose(); }
            layoutTicks += System.Diagnostics.Stopwatch.GetTimestamp()-layoutStarted;
            if (fit is null) { warnings.Add(new(r.RegionId, "译文无法完整放入；已保留原文。")); continue; }
            using (fit)
            {
                var sampled = SampleBackground(source, box);
                var maskStarted=System.Diagnostics.Stopwatch.GetTimestamp(); using (var erase = new SolidBrush(sampled))
                {
                    var masks = r.SourceLinePolygons.Count > 0 ? r.SourceLinePolygons : [r.Polygon];
                    foreach (var mask in masks.Where(x => x.Length >= 3)) g.FillPolygon(erase, mask);
                }
                if (settings.Background)
                    using (var tint = new SolidBrush(Color.FromArgb(settings.BackgroundOpacity, settings.BackgroundColor))) g.FillPolygon(tint, r.Polygon);
                maskTicks += System.Diagnostics.Stopwatch.GetTimestamp()-maskStarted; var drawStarted=System.Diagnostics.Stopwatch.GetTimestamp();
                var foreground = settings.TextColorMode switch { TextColorMode.Black => Color.Black, TextColorMode.White => Color.White,
                    TextColorMode.Custom => settings.CustomTextColor, _ => Luminance(sampled) > 145 ? Color.Black : Color.White };
                using var fg = new SolidBrush(foreground);
                using var format = new StringFormat { Trimming = StringTrimming.EllipsisWord, Alignment = StringAlignment.Near,
                    LineAlignment = r.RoleType == RegionRoleType.Button ? StringAlignment.Center : StringAlignment.Near };
                if (settings.Outline)
                {
                    var outlineColor = settings.AutomaticOutlineColor ? (foreground.GetBrightness() > .5f ? Color.Black : Color.White) : settings.OutlineColor;
                    using var outline = new SolidBrush(Color.FromArgb(210, outlineColor));
                    foreach (var offset in new[] { new PointF(-1,0), new PointF(1,0), new PointF(0,-1), new PointF(0,1) })
                        g.DrawString(text, fit, outline, new RectangleF(layout.X + offset.X, layout.Y + offset.Y, layout.Width, layout.Height), format);
                }
                g.DrawString(text, fit, fg, layout, format);
                drawTicks += System.Diagnostics.Stopwatch.GetTimestamp()-drawStarted;
            }
        }
        watch.Stop(); static long Ms(long ticks)=>(long)(ticks*1000d/System.Diagnostics.Stopwatch.Frequency);
        return new(output, warnings, watch.ElapsedMilliseconds,Ms(maskTicks),Ms(layoutTicks),Ms(drawTicks));
    }

    private static Color SampleBackground(Bitmap source, RectangleF bounds)
    {
        var points = new[] { new PointF(bounds.Left + 1, bounds.Top + 1), new PointF(bounds.Right - 2, bounds.Top + 1),
            new PointF(bounds.Left + 1, bounds.Bottom - 2), new PointF(bounds.Right - 2, bounds.Bottom - 2) };
        var colors = points.Select(p => source.GetPixel(Math.Clamp((int)p.X, 0, source.Width - 1), Math.Clamp((int)p.Y, 0, source.Height - 1))).ToArray();
        return Color.FromArgb((int)colors.Average(x => x.R), (int)colors.Average(x => x.G), (int)colors.Average(x => x.B));
    }

    private static double Luminance(Color color) => .2126 * color.R + .7152 * color.G + .0722 * color.B;
}
