using System.Drawing.Drawing2D;
using ScreenshotTranslationUiTester.CorePipelineV2;
namespace ScreenshotTranslationUiTester;

public sealed partial class PreviewForm
{
    internal override System.Windows.FrameworkElement CreateNativeContent(WpfSettingsFields fields)
    {
        return _nativePreviewRoot!;
    }
    private void DrawNativeOverlay(WpfCanvasGraphics graphics)
    {
        _paintCount++;
        if(_awaitingFirstFinalPaint)
        {
            _awaitingFirstFinalPaint=false;RealExeE2ETrace.Mark("T18 FirstFinalPaint");
            _stableFrameTimer.Stop();_stableFrameTimer.Tick-=CompleteStableFrameTrace;_stableFrameTimer.Tick+=CompleteStableFrameTrace;_stableFrameTimer.Start();
        }
        if (_document is null || _picture.Image is null) return;
        var scale = Math.Min(_picture.ClientSize.Width / (float)_picture.Image.Width, _picture.ClientSize.Height / (float)_picture.Image.Height);
        var ox = (_picture.ClientSize.Width - _picture.Image.Width * scale) / 2f;
        var oy = (_picture.ClientSize.Height - _picture.Image.Height * scale) / 2f;
        RectangleF Map(RectangleF value) => new(ox + value.X * scale, oy + value.Y * scale, value.Width * scale, value.Height * scale);
        PointF[] MapPolygon(PointF[] value) => value.Select(p => new PointF(ox + p.X * scale, oy + p.Y * scale)).ToArray();
        if (_visualAnalysisV2 is not null && _visibleV2Layers.Contains(RegionOverlayLayer.VisualRegions))
        {
            using var visualPen = new Pen(Color.Magenta, 2) { DashStyle = DashStyle.Dash };
            foreach (var visual in _visualAnalysisV2.VisualRegions) { var polygon = MapPolygon(visual.Polygon); if (polygon.Length >= 3) graphics.DrawPolygon(visualPen, polygon); }
        }
        if (_recognitionV2 is not null && _visibleV2Layers.Contains(RegionOverlayLayer.OcrBlocks))
        {
            using var ocrPen = new Pen(Color.DeepSkyBlue, 1);
            foreach (var block in _recognitionV2.Regions.SelectMany(x => x.DetectedBlocks).DistinctBy(x => x.Id))
            { var polygon = MapPolygon(block.Polygon.Length >= 3 ? block.Polygon : GeometryV2.RectanglePolygon(block.BoundingBox)); if (polygon.Length >= 3) graphics.DrawPolygon(ocrPen, polygon); }
        }
        if (_recognitionV2 is not null && _visibleV2Layers.Contains(RegionOverlayLayer.RecognitionRegions))
        {
            using var regionPen = new Pen(Color.FromArgb(210, 80, 220, 130), 2);
            using var manualPen = new Pen(Color.Gold, 2);
            using var roleFont = new Font("Microsoft YaHei UI", 8f, FontStyle.Bold);
            foreach (var region in _recognitionV2.Regions)
            {
                var polygon = MapPolygon(region.Polygon); if (polygon.Length < 3) continue;
                graphics.DrawPolygon(region.ManualOverrideFlags == RegionManualOverrideFlags.None ? regionPen : manualPen, polygon);
                if (_visibleV2Layers.Contains(RegionOverlayLayer.RoleLabels))
                {
                    var order = _visibleV2Layers.Contains(RegionOverlayLayer.ReadingOrder) ? $"#{region.ReadingOrder} " : "";
                    var label = $"{order}{region.RoleType} {region.RegionId[4..10]}";
                    var at = polygon.OrderBy(p => p.Y).ThenBy(p => p.X).First(); var size = graphics.MeasureString(label, roleFont);
                    graphics.FillRectangle(Brushes.Black, at.X, Math.Max(0, at.Y - size.Height), size.Width, size.Height);
                    graphics.DrawString(label, roleFont, Brushes.Lime, at.X, Math.Max(0, at.Y - size.Height));
                }
            }
        }
        if (_recognitionV2 is not null && _visibleV2Layers.Contains(RegionOverlayLayer.TranslationMapping))
        {
            using var mappingPen = new Pen(Color.Cyan, 3) { DashStyle = DashStyle.Dot };
            foreach (var region in _recognitionV2.Regions.Where(x => !string.IsNullOrWhiteSpace(x.TranslationUnitId)))
            {if(_displayedImageMode==ImageViewMode.Translated){if(region.TranslationRenderRect is{} rr)graphics.DrawRectangle(mappingPen,Rectangle.Round(Map(rr)));}else{var polygon=MapPolygon(region.Polygon);if(polygon.Length>=3)graphics.DrawPolygon(mappingPen,polygon);}}
        }
        if (_recognitionV2 is not null && _visibleV2Layers.Contains(RegionOverlayLayer.Coverage))
        {
            foreach (var line in _recognitionV2.SourceLineCoverage.Where(x => x.Polygon.Length >= 3))
            {
                var color = line.Disposition switch { SourceLineDisposition.AssignedToTranslationUnit => Color.Lime,
                    SourceLineDisposition.PreserveOriginal => Color.DeepSkyBlue, SourceLineDisposition.IgnoredWithReason => Color.Gold, _ => Color.Red };
                using var coveragePen = new Pen(color, line.Disposition == SourceLineDisposition.Unaccounted ? 5 : 3);
                var polygon = MapPolygon(line.Polygon); if (polygon.Length >= 3) graphics.DrawPolygon(coveragePen, polygon);
            }
        }
        if (_recognitionV2 is not null && _visibleV2Layers.Contains(RegionOverlayLayer.SemanticGroups))
        {
            using var groupPen = new Pen(Color.Gold, 2) { DashStyle = DashStyle.DashDot };
            foreach (var unit in _recognitionV2.TranslationUnits)
            {
                var members = _recognitionV2.Regions.Where(x => unit.RegionIds.Contains(x.RegionId)).Select(x => x.BoundingBox).ToArray();
                if (members.Length > 0) graphics.DrawRectangle(groupPen, Rectangle.Round(Map(members.Aggregate(RectangleF.Union))));
            }
        }
        if (_showGroupBounds)
        {
            using var sourcePen = new Pen(Color.Gold, 2); using var renderPen = new Pen(Color.Lime, 2) { DashStyle = DashStyle.Dash };
            using var labelFont = new Font("Microsoft YaHei UI", 8f); using var labelBg = new SolidBrush(Color.FromArgb(210, 0, 0, 0));
            foreach (var group in _document.Groups)
            {
                var source = Map(group.SourceRectangle); var render = Map(group.RenderRectangle.IsEmpty ? group.SourceRectangle : group.RenderRectangle);
                graphics.DrawRectangle(sourcePen, Rectangle.Round(source)); graphics.DrawRectangle(renderPen, Rectangle.Round(render));
                var label = $"{group.GroupId} {group.GroupType} S:{group.SourceRectangle.X:0},{group.SourceRectangle.Y:0},{group.SourceRectangle.Width:0},{group.SourceRectangle.Height:0} R:{group.RenderRectangle.X:0},{group.RenderRectangle.Y:0},{group.RenderRectangle.Width:0},{group.RenderRectangle.Height:0}";
                var size = graphics.MeasureString(label, labelFont); graphics.FillRectangle(labelBg, source.X, Math.Max(0, source.Y - size.Height), size.Width, size.Height); graphics.DrawString(label, labelFont, Brushes.White, source.X, Math.Max(0, source.Y - size.Height));
            }
        }
        var ids = ActiveHighlightSourceBlockIds();
        if (ids.Count == 0) return;
        using var pen = new Pen(Color.Cyan, 3);
        if (_recognitionV2 is not null)
        {
            var selectedSourceIds=ids.ToHashSet(StringComparer.Ordinal);
            var selectedRenderIds=(ActiveRightIdentity?.StableRenderUnitIds??[]).ToHashSet(StringComparer.Ordinal);
            foreach (var region in _recognitionV2.Regions.Where(x =>
                         x.SourceBlockIds.Any(selectedSourceIds.Contains)||selectedRenderIds.Contains(x.RegionId)))
            {
                if(_displayedImageMode==ImageViewMode.Translated){if(region.TranslationRenderRect is{} rr)graphics.DrawRectangle(pen,Rectangle.Round(Map(rr)));continue;}
                for(var i=0;i<region.SourceBlockIds.Count;i++)
                {
                    if(!selectedSourceIds.Contains(region.SourceBlockIds[i]))continue;
                    var line=i<region.SourceLinePolygons.Count?region.SourceLinePolygons[i]:region.Polygon;
                    var polygon=MapPolygon(line);if(polygon.Length>=3)graphics.DrawPolygon(pen,polygon);
                }
            }
        }
        else foreach (var segment in _document.Groups.Where(x => ids.Contains(x.GroupId, StringComparer.Ordinal)))
        {var rect=_displayedImageMode==ImageViewMode.Translated?segment.RenderRectangle:segment.SourceRectangle;if(!rect.IsEmpty)graphics.DrawRectangle(pen,Rectangle.Round(Map(rect)));}
    
    }
}

public sealed partial class RecognitionRegionEditorForm
{
    internal override System.Windows.FrameworkElement CreateNativeContent(WpfSettingsFields fields)
    {
        return _nativeRegionRoot!;
    }
    private void DrawNativeOverlay(WpfCanvasGraphics graphics)
    {
        using var normal = new Pen(Color.Lime, 2); using var selected = new Pen(Color.Gold, 3); using var sourceLine = new Pen(Color.DeepSkyBlue, 1) { DashStyle=System.Drawing.Drawing2D.DashStyle.Dash }; using var vertex = new SolidBrush(Color.Cyan); using var font = new Font("Segoe UI", 8, FontStyle.Bold);
        foreach (var r in _session.Regions) { var points = r.Polygon.Select(Screen).ToArray(); if (points.Length < 3) continue; graphics.DrawPolygon(_selected.Contains(r.RegionId) ? selected : normal, points); if(_showSourceLines) foreach(var line in r.SourceLinePolygons){var lp=line.Select(Screen).ToArray();if(lp.Length>=3)graphics.DrawPolygon(sourceLine,lp);} var label=$"#{r.ReadingOrder} {r.RoleType}"; var labelSize=graphics.MeasureString(label,font); var labelY=Math.Max(0,points[0].Y-labelSize.Height); graphics.FillRectangle(Brushes.Black,points[0].X,labelY,labelSize.Width,labelSize.Height); graphics.DrawString(label, font, _selected.Contains(r.RegionId)?Brushes.Gold:Brushes.Lime, points[0].X,labelY); if (_tool == Tool.Polygon && _selected.Contains(r.RegionId)) foreach (var p in points) graphics.FillEllipse(vertex, p.X - 5, p.Y - 5, 10, 10); }
        if (_dragStart is not null && _dragCurrent is not null && _tool == Tool.Create) { var a = Screen(_dragStart.Value); var b = Screen(_dragCurrent.Value); graphics.DrawRectangle(selected, Rectangle.Round(RectangleF.FromLTRB(Math.Min(a.X,b.X),Math.Min(a.Y,b.Y),Math.Max(a.X,b.X),Math.Max(a.Y,b.Y)))); }
    
    }
}

public sealed partial class OcrComparisonForm
{
    internal override System.Windows.FrameworkElement CreateNativeContent(WpfSettingsFields fields)
    {
        return _nativeCompareRoot!;
    }
    private void DrawNativeOverlay(WpfCanvasGraphics graphics)
    {
        if (_source is null || (_tabs.SelectedItem as System.Windows.Controls.TabItem)?.Tag is not OcrEngineKind kind ||
            !_results.TryGetValue(kind, out var result) || result.ImageSessionId != _imageSessionId) return;
        var scale = Math.Min((float)_image.ClientSize.Width / _source.Width,
            (float)_image.ClientSize.Height / _source.Height);
        var offsetX = (_image.ClientSize.Width - _source.Width * scale) / 2f;
        var offsetY = (_image.ClientSize.Height - _source.Height * scale) / 2f;
        using var normalPen = new Pen(result.ValidForQualityComparison ? Color.Cyan : Color.OrangeRed, 2);
        using var lowPen = new Pen(Color.Yellow, 2);
        using var correctedPen = new Pen(Color.LimeGreen, 2);
        using var suspectPen = new Pen(Color.Orange, 2);
        using var ambiguousPen = new Pen(Color.MediumPurple, 2);
        foreach (var block in result.Blocks)
        {
            var pen = block.SuspectedFalsePositive ? suspectPen : block.UnresolvedAmbiguity ? ambiguousPen : block.Corrections.Count > 0 ? correctedPen : block.LowConfidence ? lowPen : normalPen;
            graphics.DrawRectangle(pen, offsetX + block.BoundingBox.X * scale,
                offsetY + block.BoundingBox.Y * scale, block.BoundingBox.Width * scale,
                block.BoundingBox.Height * scale);
        }
    
    }
}
