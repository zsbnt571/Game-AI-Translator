namespace ScreenshotTranslationUiTester;

public sealed record ParagraphGeometry(
    RectangleF SourceParagraphRect,
    RectangleF SourceTextInkBounds,
    float PreferredLeft,
    float PreferredRight,
    float PreferredWidth,
    float PreferredTop,
    float SourceMedianLineHeight,
    int SourceLineCount,
    StringAlignment Alignment,
    IReadOnlyList<RectangleF> LocalExclusionSet);

public sealed record GeneralizationMetrics(
    string RegionId,
    float ParagraphAnchorDrift,
    float WidthPreservationRatio,
    float SourceToTranslatedFontScaleRatio,
    float HeightInflationRatio,
    float InvalidWhitespaceRatio,
    int ExclusionLeakage,
    float BottomSafetyMargin,
    float RegionOverlapArea,
    bool TextMissing,
    bool TextClipping);

/// <summary>
/// Geometry-only policy. It deliberately has no knowledge of file names, OCR text,
/// character names, games, or evidence cases.
/// </summary>
public static class ParagraphGeometryPlanner
{
    public static ParagraphGeometry Plan(RecognitionRegion region)
    {
        var lines=region.SourceLinePolygons.Select(GeometryV2.Bounds).Where(x=>!x.IsEmpty).ToArray();
        var ink=lines.Length==0?region.BoundingBox:lines.Aggregate(RectangleF.Union);
        var paragraph=region.RendererTargetRegion.IsEmpty?ink:region.RendererTargetRegion;
        if(paragraph.IsEmpty)paragraph=region.BoundingBox;
        var heights=lines.Select(x=>x.Height).Where(x=>x>0).OrderBy(x=>x).ToArray();
        var median=heights.Length==0?Math.Max(1,paragraph.Height):heights[(heights.Length-1)/2];
        var yMargin=Math.Clamp(median*.12f,2,8);
        var local=region.LayoutExclusionPolygons.Select(GeometryV2.Bounds).Where(x=>!x.IsEmpty)
            .Where(x=>RangesTouch(paragraph.Top,paragraph.Bottom,x.Top,x.Bottom,yMargin)).ToArray();
        var alignment=ResolveAlignment(lines,paragraph);
        return new(paragraph,ink,paragraph.Left,paragraph.Right,paragraph.Width,paragraph.Top,
            median,Math.Max(1,lines.Length),alignment,local);
    }

    public static IReadOnlyList<RectangleF> LocalExclusions(RecognitionRegion region)=>Plan(region).LocalExclusionSet;

    public static bool AffectsParagraph(RectangleF exclusion,ParagraphGeometry paragraph)
    {
        // Exclusions are geometric, not page-wide style constraints. A small anti-alias/box
        // tolerance is enough; the previous 45% line-height margin leaked an avatar into the
        // following paragraph even when their Y ranges did not intersect.
        var margin=Math.Clamp(paragraph.SourceMedianLineHeight*.12f,2,8);
        return RangesTouch(paragraph.SourceParagraphRect.Top,paragraph.SourceParagraphRect.Bottom,
            exclusion.Top,exclusion.Bottom,margin);
    }

    private static bool RangesTouch(float aTop,float aBottom,float bTop,float bBottom,float margin)=>
        aTop<=bBottom+margin&&bTop<=aBottom+margin;

    private static StringAlignment ResolveAlignment(IReadOnlyList<RectangleF> lines,RectangleF paragraph)
    {
        if(lines.Count==0)return StringAlignment.Near;
        var centers=lines.Select(x=>x.Left+x.Width/2).ToArray();
        var centerError=centers.Average(x=>Math.Abs(x-(paragraph.Left+paragraph.Width/2)));
        var leftError=lines.Average(x=>Math.Abs(x.Left-paragraph.Left));
        var rightError=lines.Average(x=>Math.Abs(x.Right-paragraph.Right));
        if(centerError<leftError*.65f&&centerError<rightError*.65f)return StringAlignment.Center;
        return rightError<leftError*.6f?StringAlignment.Far:StringAlignment.Near;
    }
}

public static class GeneralizationMetricEvaluator
{
    public static GeneralizationMetrics Evaluate(RecognitionRegion region,RegionRenderDiagnostic diagnostic,Size canvas,
        IEnumerable<RectangleF>? otherRenderedBounds=null)
    {
        var source=ParagraphGeometryPlanner.Plan(region);
        var rendered=diagnostic.RenderBounds.IsEmpty?source.SourceParagraphRect:diagnostic.RenderBounds;
        var layoutTarget=diagnostic.LayoutTarget.IsEmpty?source.SourceParagraphRect:diagnostic.LayoutTarget;
        var sourceHeight=Math.Max(1,source.SourceParagraphRect.Height);
        var sourceWidth=Math.Max(1,source.PreferredWidth);
        var usedWidth=diagnostic.RenderedLines.Count==0?0:diagnostic.RenderedLines.Max(x=>x.Bounds.Right)-diagnostic.RenderedLines.Min(x=>x.Bounds.Left);
        var overlap=(otherRenderedBounds??[]).Where(x=>!x.IsEmpty).Sum(x=>Area(RectangleF.Intersect(rendered,x)));
        var clipping=rendered.Left<0||rendered.Top<0||rendered.Right>canvas.Width+.5f||rendered.Bottom>canvas.Height+.5f;
        var leaked=region.LayoutExclusionPolygons.Select(GeometryV2.Bounds)
            .Count(x=>!ParagraphGeometryPlanner.AffectsParagraph(x,source)&&Area(RectangleF.Intersect(rendered,x))>0);
        return new(region.RegionId,
            Math.Abs(rendered.Left-source.PreferredLeft),
            layoutTarget.Width/sourceWidth,
            diagnostic.FontSize/Math.Max(1,source.SourceMedianLineHeight),
            layoutTarget.Height/sourceHeight,
            Math.Clamp((layoutTarget.Width-Math.Max(0,usedWidth))/Math.Max(1,layoutTarget.Width),0,1),
            leaked,
            canvas.Height-rendered.Bottom,
            overlap,
            !string.IsNullOrWhiteSpace(region.TranslationText)&&diagnostic.RenderedLines.Count==0,
            clipping);
    }

    private static float Area(RectangleF r)=>r.Width<=0||r.Height<=0?0:r.Width*r.Height;
}
