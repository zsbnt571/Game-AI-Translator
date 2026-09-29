using System.Diagnostics;

namespace ScreenshotTranslationUiTester;

public enum SourceStylePolarity { Unknown, Light, Dark, Chromatic }

public sealed record SourceStyleBundle(
    Color FillColor,
    Color OutlineColor,
    float OutlineWidth,
    Color ShadowColor,
    PointF ShadowOffset,
    float Glow,
    byte Alpha,
    FontStyle Weight,
    SourceStylePolarity Polarity,
    RegionRoleType Role,
    float Confidence,
    string Owner,
    string Evidence)
{
    public bool HasOutline => OutlineWidth > .05f && OutlineColor.A > 0;
    public bool HasShadow => ShadowColor.A > 0 && (Math.Abs(ShadowOffset.X) > .05f || Math.Abs(ShadowOffset.Y) > .05f);
}

public sealed record SourceStyleGuardDecision(
    SourceStyleBundle Source,
    SourceStyleBundle Effective,
    double FillContrast,
    double CombinedReadability,
    string Adjustment,
    bool FillPolarityChanged,
    long ElapsedTicks);

/// <summary>
/// Owns source-derived text appearance.  It is intentionally image- and text-agnostic:
/// no fixture names, coordinates, strings or expected translations participate in selection.
/// </summary>
public static class SourceStyleBundleOwner
{
    private sealed record PixelSample(Color Color,bool Border,bool Edge,int X,int Y,int Step);
    private sealed record Cluster(Color Color, int Count, double MeanDistance,int BorderCount,int EdgeCount,int SpanX,int SpanY);

    public static SourceStyleBundle Create(Bitmap source, RecognitionRegion region) => Create(source,
        region.SourceLinePolygons.Count > 0 ? region.SourceLinePolygons : [region.Polygon],
        region.BoundingBox, region.RoleType);

    public static SourceStyleBundle Create(Bitmap source, IReadOnlyList<PointF[]> sourceLinePolygons,
        RectangleF boundingBox, RegionRoleType role)
    {
        var background = EstimateSourceBackground(source, sourceLinePolygons, boundingBox);
        var samples = InteriorSamples(source, sourceLinePolygons, boundingBox);
        var allClusters=ClusterSamples(samples,background,false);
        var interiorBackground=allClusters.OrderByDescending(x=>x.Count*(1.2-Math.Min(.95,x.EdgeCount/(double)Math.Max(1,x.Count)))).FirstOrDefault();
        if(interiorBackground is not null)background=interiorBackground.Color;
        var clusters = ClusterSamples(samples, background);
        var probe = new RecognitionRegion
        {
            Polygon = GeometryV2.RectanglePolygon(boundingBox),
            SourceLinePolygons = sourceLinePolygons.Select(x=>x.ToArray()).ToList(),
            RoleType = role
        };
        var hinted = TextStyleHintExtractor.EstimateForeground(source, probe, background);
        var fillCluster = SelectFill(clusters, hinted, background);
        var fill = fillCluster?.Color ?? hinted ?? (background.GetBrightness() > .5f ? Color.Black : Color.White);
        var confidence = fillCluster is null ? (hinted is null ? .28f : .68f) : Math.Clamp(.48f + fillCluster.Count / 90f, .52f, .94f);

        var outlineCluster = clusters
            .Where(x => ColorDistance(x.Color, fill) >= 72 && x.Count >= Math.Max(2, (fillCluster?.Count ?? 4) / 12))
            .OrderByDescending(x => OutlineScore(x, fill, background))
            .FirstOrDefault();
        var hasOutline = outlineCluster is not null &&
            TranslationTextColorResolver.Contrast(outlineCluster.Color, fill) >= 1.55;
        var outlineWidth = hasOutline ? EstimateOutlineWidth(sourceLinePolygons, boundingBox, fillCluster?.Count ?? 0, outlineCluster!.Count) : 0;
        var outline = hasOutline ? Color.FromArgb(255, outlineCluster!.Color) : Color.Transparent;

        var shadowCluster = clusters
            .Where(x => x != outlineCluster && ColorDistance(x.Color, fill) >= 56 && x.Count >= 2)
            .OrderByDescending(x => x.Count * Math.Log2(1 + TranslationTextColorResolver.Contrast(x.Color, fill)))
            .FirstOrDefault();
        var hasShadow = shadowCluster is not null && outlineWidth > 0 && shadowCluster.Count >= Math.Max(2, (fillCluster?.Count ?? 8) / 7);
        var shadow = hasShadow ? Color.FromArgb(150, shadowCluster!.Color) : Color.Transparent;
        var relativeHeight = Math.Max(1, sourceLinePolygons.Select(GeometryV2.Bounds).DefaultIfEmpty(boundingBox).Average(x => Math.Max(1, x.Height)));
        var weight = fillCluster is not null && fillCluster.Count / Math.Max(1d, samples.Count) > .19 ? FontStyle.Bold : FontStyle.Regular;
        var polarity = ClassifyPolarity(fill);
        var evidence = $"LOCAL_GLYPH_CLUSTER;bg={Hex(background)};hint={(hinted is null?"none":Hex(hinted.Value))};fillSamples={fillCluster?.Count ?? 0};outlineSamples={outlineCluster?.Count ?? 0};clusters={string.Join(',',clusters.Take(8).Select(x=>$"{Hex(x.Color)}:{x.Count}"))}";
        return new(Color.FromArgb(255, fill), outline, outlineWidth, shadow,
            hasShadow ? new PointF(Math.Clamp(relativeHeight * .045f, 1, 3), Math.Clamp(relativeHeight * .055f, 1, 3)) : PointF.Empty,
            0, 255, weight, polarity, role, confidence, "SourceStyleBundleOwner", evidence);
    }

    public static Color EstimateSourceBackground(Bitmap image, RecognitionRegion region) => EstimateSourceBackground(image,
        region.SourceLinePolygons.Count > 0 ? region.SourceLinePolygons : [region.Polygon], region.BoundingBox);

    public static Color EstimateSourceBackground(Bitmap image, IReadOnlyList<PointF[]> sourceLinePolygons,
        RectangleF boundingBox)
    {
        var valid = sourceLinePolygons.Where(x => x.Length >= 3).Select(GeometryV2.Bounds).ToArray();
        var bounds = valid.Length > 0 ? valid.Aggregate(RectangleF.Union) : boundingBox;
        var inner = Rectangle.Intersect(Rectangle.Ceiling(bounds), new Rectangle(Point.Empty, image.Size));
        var ring = Rectangle.Intersect(Rectangle.Inflate(inner, Math.Clamp(inner.Width / 18, 3, 14), Math.Clamp(inner.Height / 8, 3, 12)), new Rectangle(Point.Empty, image.Size));
        var colors = new List<Color>();
        var sx = Math.Max(1, ring.Width / 48); var sy = Math.Max(1, ring.Height / 24);
        for (var x = ring.Left; x < ring.Right; x += sx)
        {
            if (ring.Top < inner.Top) colors.Add(image.GetPixel(x, ring.Top));
            if (ring.Bottom - 1 >= inner.Bottom) colors.Add(image.GetPixel(x, ring.Bottom - 1));
        }
        for (var y = ring.Top; y < ring.Bottom; y += sy)
        {
            if (ring.Left < inner.Left) colors.Add(image.GetPixel(ring.Left, y));
            if (ring.Right - 1 >= inner.Right) colors.Add(image.GetPixel(ring.Right - 1, y));
        }
        var corners=new List<Color>();var patchX=Math.Clamp(inner.Width/10,1,4);var patchY=Math.Clamp(inner.Height/8,1,3);
        foreach(var y0 in new[]{inner.Top,Math.Max(inner.Top,inner.Bottom-patchY)})
        foreach(var x0 in new[]{inner.Left,Math.Max(inner.Left,inner.Right-patchX)})
        for(var y=y0;y<Math.Min(inner.Bottom,y0+patchY);y++)
        for(var x=x0;x<Math.Min(inner.Right,x0+patchX);x++)corners.Add(image.GetPixel(x,y));
        if (colors.Count < 4||corners.Count<4)
        {
            var estimate = BackgroundEstimator.Estimate(image, bounds);
            return estimate.Color;
        }
        var ringColor=Median(colors);var cornerColor=Median(corners);
        return ColorDistance(ringColor,cornerColor)<=72?Blend(cornerColor,ringColor,.68f):cornerColor;
    }

    public static SourceStyleBundle WithExplicitSettings(SourceStyleBundle source, RenderSettings settings)
    {
        var fill = settings.TextColorMode switch
        {
            TextColorMode.Black => Color.Black,
            TextColorMode.White => Color.White,
            TextColorMode.Custom => settings.CustomTextColor,
            _ => source.FillColor
        };
        if (settings.TextColorMode == TextColorMode.Auto) return source;
        var outline = settings.Outline
            ? (settings.AutomaticOutlineColor ? Opposite(fill) : settings.OutlineColor)
            : source.OutlineColor;
        var width = settings.Outline ? settings.StrokeWidth switch { RendererStrokeWidth.Medium => 2f, RendererStrokeWidth.None => 0f, _ => 1f } : source.OutlineWidth;
        return source with
        {
            FillColor = Color.FromArgb(255, fill), Alpha = 255, Polarity = ClassifyPolarity(fill),
            OutlineColor = width > 0 ? Color.FromArgb(220, outline) : Color.Transparent, OutlineWidth = width,
            Owner = "UserTextColorOverride", Confidence = 1,
            Evidence = source.Evidence + ";EXPLICIT_TEXT_COLOR_SETTING"
        };
    }

    internal static SourceStylePolarity ClassifyPolarity(Color color)
    {
        var max = Math.Max(color.R, Math.Max(color.G, color.B));
        var min = Math.Min(color.R, Math.Min(color.G, color.B));
        if (max - min >= 48 && color.GetSaturation() >= .25f) return SourceStylePolarity.Chromatic;
        return RelativeLuminance(color) >= .48 ? SourceStylePolarity.Light : SourceStylePolarity.Dark;
    }

    internal static Color Opposite(Color color) => RelativeLuminance(color) >= .48 ? Color.Black : Color.White;

    private static List<PixelSample> InteriorSamples(Bitmap image, IReadOnlyList<PointF[]> sourceLinePolygons,
        RectangleF boundingBox)
    {
        var result = new List<PixelSample>();
        var polygons = sourceLinePolygons.Count > 0 ? sourceLinePolygons : [GeometryV2.RectanglePolygon(boundingBox)];
        foreach (var polygon in polygons.Where(x => x.Length >= 3))
        {
            var bounds = Rectangle.Intersect(Rectangle.Ceiling(GeometryV2.Bounds(polygon)), new Rectangle(Point.Empty, image.Size));
            if (bounds.Width < 1 || bounds.Height < 1) continue;
            var step = Math.Max(1,(int)Math.Ceiling(Math.Min(bounds.Width,bounds.Height)/20d));
            for (var y = bounds.Top; y < bounds.Bottom; y += step)
            for (var x = bounds.Left; x < bounds.Right; x += step)
                result.Add(new(image.GetPixel(x,y),x<=bounds.Left+step||x>=bounds.Right-1-step||y<=bounds.Top+step||y>=bounds.Bottom-1-step,false,x,y,step));
        }
        var lookup=result.GroupBy(x=>(x.X,x.Y)).ToDictionary(x=>x.Key,x=>x.First().Color);
        return result.Select(x=>x with{Edge=new[]{(x.X-x.Step,x.Y),(x.X+x.Step,x.Y),(x.X,x.Y-x.Step),(x.X,x.Y+x.Step)}
            .Any(p=>lookup.TryGetValue(p,out var n)&&ColorDistance(n,x.Color)>=48)}).ToList();
    }

    private static List<Cluster> ClusterSamples(IReadOnlyList<PixelSample> colors, Color background,bool excludeBackground=true) => colors
        .Where(x => x.Color.A >= 64 && (!excludeBackground||ColorDistance(x.Color, background) >= 32))
        .GroupBy(x => (R: x.Color.R / 20, G: x.Color.G / 20, B: x.Color.B / 20))
        .Select(g => new Cluster(Median(g.Select(x=>x.Color).ToArray()), g.Count(), g.Average(x => ColorDistance(x.Color, background)),g.Count(x=>x.Border),g.Count(x=>x.Edge),g.Max(x=>x.X)-g.Min(x=>x.X)+1,g.Max(x=>x.Y)-g.Min(x=>x.Y)+1))
        .OrderByDescending(x => x.Count * Math.Max(20, x.MeanDistance))
        .Take(14).ToList();

    private static Cluster? SelectFill(IReadOnlyList<Cluster> clusters, Color? hinted, Color background)
    {
        var pairs=(from i in Enumerable.Range(0,clusters.Count)
                   from j in Enumerable.Range(i+1,clusters.Count-i-1)
                   let a=clusters[i] let b=clusters[j]
                   where a.Count>=2&&b.Count>=2&&TranslationTextColorResolver.Contrast(a.Color,b.Color)>=2.35
                     &&TranslationTextColorResolver.Contrast(a.Color,background)>=1.12
                     &&TranslationTextColorResolver.Contrast(b.Color,background)>=1.12
                   let score=Math.Min(a.Count,b.Count)*Math.Log2(1+TranslationTextColorResolver.Contrast(a.Color,b.Color))
                   orderby score descending
                   select (A:a,B:b,Score:score)).ToArray();
        var pair=pairs.FirstOrDefault();
        if(pair.A is not null)
        {
            var neutral=pairs.FirstOrDefault(x=>x.A.Color.GetSaturation()<.18f&&x.B.Color.GetSaturation()<.18f&&x.Score>=pair.Score*.72);
            if(neutral.A is not null)pair=neutral;
        }
        if(pair.A is not null&&pair.B is not null)
        {
            var aSaturation=pair.A.Color.GetSaturation();var bSaturation=pair.B.Color.GetSaturation();
            if(aSaturation>=.24f&&bSaturation<.16f)return pair.A;
            if(bSaturation>=.24f&&aSaturation<.16f)return pair.B;
            var aContrast=TranslationTextColorResolver.Contrast(pair.A.Color,background);
            var bContrast=TranslationTextColorResolver.Contrast(pair.B.Color,background);
            if(Math.Max(aContrast,bContrast)/Math.Max(1.01,Math.Min(aContrast,bContrast))>=1.35)return aContrast>=bContrast?pair.A:pair.B;
            var aBorder=pair.A.BorderCount/(double)Math.Max(1,pair.A.Count);var bBorder=pair.B.BorderCount/(double)Math.Max(1,pair.B.Count);
            if(Math.Abs(aBorder-bBorder)>=.012)return aBorder<bBorder?pair.A:pair.B;
            if(Math.Abs(aContrast-bContrast)>=.12)return aContrast>=bContrast?pair.A:pair.B;
        }
        if (hinted is { } hint)
        {
            var near = clusters.OrderBy(x => ColorDistance(x.Color, hint)).FirstOrDefault();
            if (near is not null && ColorDistance(near.Color, hint) <= 82) return near;
            return new(hint,3,ColorDistance(hint,background),0,0,0,0);
        }
        return clusters.OrderByDescending(x => x.Count * Math.Log2(1 + TranslationTextColorResolver.Contrast(x.Color, background))).FirstOrDefault();
    }

    private static double OutlineScore(Cluster cluster, Color fill, Color background) =>
        cluster.Count * Math.Log2(1 + TranslationTextColorResolver.Contrast(cluster.Color, fill)) *
        Math.Max(1, Math.Log2(1 + TranslationTextColorResolver.Contrast(cluster.Color, background)));

    private static float EstimateOutlineWidth(IReadOnlyList<PointF[]> sourceLinePolygons, RectangleF boundingBox,
        int fillCount, int outlineCount)
    {
        var height = sourceLinePolygons.Select(GeometryV2.Bounds).DefaultIfEmpty(boundingBox).Average(x => Math.Max(1, x.Height));
        var ratio = outlineCount / Math.Max(1f, fillCount);
        return Math.Clamp((float)(height * (.025 + Math.Min(.04, ratio * .015))), .75f, 3.5f);
    }

    private static int ColorDistance(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
    private static Color Blend(Color a,Color b,float aWeight)=>Color.FromArgb(255,
        (int)(a.R*aWeight+b.R*(1-aWeight)),(int)(a.G*aWeight+b.G*(1-aWeight)),(int)(a.B*aWeight+b.B*(1-aWeight)));
    private static Color Median(IReadOnlyList<Color> colors)
    {
        if (colors.Count == 0) return Color.Black;
        int M(Func<Color, int> f) => colors.Select(f).OrderBy(x => x).ElementAt(colors.Count / 2);
        return Color.FromArgb(M(x => x.A), M(x => x.R), M(x => x.G), M(x => x.B));
    }
    private static double RelativeLuminance(Color c)
    {
        static double Linear(byte v) { var x = v / 255d; return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); }
        return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
    }
    internal static string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
}

/// <summary>
/// Readability is evaluated on the composed fill + edge treatment. The fill is source-owned.
/// Candidate-14 first improves fill opacity/luminance and only then adds restrained support;
/// this avoids turning ordinary CJK body copy into a bright, thick outlined label.
/// </summary>
public static class SourceStyleLegibilityGuard
{
    public static SourceStyleGuardDecision Evaluate(SourceStyleBundle source, Color background)
    {
        var started = Stopwatch.GetTimestamp();
        var fillOnlyThreshold = source.Role is RegionRoleType.Title or RegionRoleType.Header or RegionRoleType.Button or RegionRoleType.UILabel ? 3d : 4.5d;
        var effective = source with { FillColor = Color.FromArgb(source.Alpha, source.FillColor) };
        var score = Combined(effective, background);
        var adjustment = "SOURCE_BUNDLE_UNCHANGED";
        double FillContrast()=>TranslationTextColorResolver.Contrast(Color.FromArgb(255,effective.FillColor),background);

        bool NeedsFillFirst()=>source.Confidence<.75f &&
            !(source.Polarity==SourceStylePolarity.Chromatic && source.Confidence>=.65f &&
              source.HasOutline && TranslationTextColorResolver.Contrast(source.FillColor,source.OutlineColor)>=3)
            ?FillContrast()<fillOnlyThreshold
            :score<Required(effective,fillOnlyThreshold);

        if (NeedsFillFirst() && effective.Alpha < 255)
        {
            effective = effective with { Alpha = 255, FillColor = Color.FromArgb(255, effective.FillColor) };
            score = Combined(effective, background);
            adjustment = "ALPHA_OPAQUE";
        }
        if (NeedsFillFirst() && source.Owner!="UserTextColorOverride")
        {
            var adjusted = BoundedLuminanceMove(effective.FillColor, background, effective.Polarity);
            effective = effective with { FillColor = adjusted };
            score = Combined(effective, background);
            adjustment = Append(adjustment,"BOUNDED_LUMINANCE_FIRST");
        }
        if(FillContrast()<fillOnlyThreshold&&source.Owner!="UserTextColorOverride"&&
           source.Confidence<.75f&&source.Polarity!=SourceStylePolarity.Chromatic)
        {
            // An accepted near-white source fill carries polarity evidence even
            // when its sampled edge is weak. Retain it and use the existing restrained
            // support guard; uncertain grey fills retain their prior contrast fallback.
            var brightSourceEvidence=source.Confidence>=.52f&&source.Polarity==SourceStylePolarity.Light&&
                Math.Min(source.FillColor.R,Math.Min(source.FillColor.G,source.FillColor.B))>=220;
            var preservePolarity=brightSourceEvidence||(source.HasOutline&&TranslationTextColorResolver.Contrast(
                Color.FromArgb(255,source.FillColor),Color.FromArgb(255,source.OutlineColor))>=1.55);
            if(brightSourceEvidence)adjustment=Append(adjustment,"SOURCE_BRIGHT_FILL_POLARITY_PRESERVED");
            effective=effective with {FillColor=MinimumReadableFill(effective.FillColor,background,fillOnlyThreshold,
                source.Polarity,preservePolarity)};
            score=Combined(effective,background);
            adjustment=Append(adjustment,"MINIMUM_READABLE_FILL");
        }
        if(source.Owner!="UserTextColorOverride"&&effective.HasOutline&&FillContrast()>=fillOnlyThreshold&&
           effective.OutlineColor.GetBrightness()>effective.FillColor.GetBrightness()+.10f)
        {
            effective=effective with {OutlineColor=Color.Transparent,OutlineWidth=0};
            score=Combined(effective,background);
            adjustment=Append(adjustment,"AUTO_BRIGHT_OUTLINE_REMOVED");
        }
        if(source.Owner!="UserTextColorOverride"&&source.Confidence<.75f&&effective.HasOutline&&
           effective.Role is not RegionRoleType.Title and not RegionRoleType.Header&&effective.OutlineWidth>1.15f)
        {
            effective=effective with
            {
                OutlineWidth=1.15f,
                OutlineColor=Color.FromArgb(Math.Min(145,(int)effective.OutlineColor.A),effective.OutlineColor)
            };
            score=Combined(effective,background);
            adjustment=Append(adjustment,"ORDINARY_OUTLINE_RESTRAINED");
        }
        if (score < Required(effective,fillOnlyThreshold))
        {
            var support = ReadabilitySupport(effective.FillColor,background,effective.Role);
            var title=effective.Role is RegionRoleType.Title or RegionRoleType.Header;
            var maxWidth=title?2.25f:1.15f;
            var width=Math.Clamp(effective.HasOutline?effective.OutlineWidth:MinimumSupportWidth(effective.Role),.55f,maxWidth);
            var alpha=title?205:145;
            effective=effective with {OutlineColor=Color.FromArgb(alpha,support),OutlineWidth=width};
            score=Combined(effective,background);
            adjustment=Append(adjustment,source.HasOutline?"OUTLINE_RESTRAINED":"SUBTLE_SUPPORT_ADDED");
        }
        if (score < Required(effective,fillOnlyThreshold))
        {
            var support = ReadabilitySupport(effective.FillColor,background,effective.Role);
            effective = effective with { ShadowColor = Color.FromArgb(105, support), ShadowOffset = new PointF(.8f, .8f) };
            score = Combined(effective, background);
            adjustment=Append(adjustment,"SUBTLE_SHADOW_ADDED");
        }
        // A polarity flip is deliberately absent from normal product behavior.  A source-owned
        // fill with a strong opposite edge remains readable without destroying the source role.
        var changed = SourceStyleBundleOwner.ClassifyPolarity(source.FillColor) != SourceStyleBundleOwner.ClassifyPolarity(effective.FillColor);
        return new(source, effective, TranslationTextColorResolver.Contrast(source.FillColor, background), score,
            adjustment, changed, Stopwatch.GetTimestamp() - started);
    }

    public static SourceStyleGuardDecision FinalizeForFontSize(SourceStyleGuardDecision decision,Color background,float fontSize)
    {
        var style=decision.Effective;
        if(!style.HasOutline)return decision;
        var title=style.Role is RegionRoleType.Title or RegionRoleType.Header;
        var cap=title?Math.Clamp(fontSize*.065f,.8f,2.25f):Math.Clamp(fontSize*.045f,.55f,1.15f);
        var alphaCap=title?220:165;
        var alpha=(byte)Math.Min(style.OutlineColor.A,alphaCap);
        var width=Math.Min(style.OutlineWidth,cap);
        if(Math.Abs(width-style.OutlineWidth)<.001f&&alpha==style.OutlineColor.A)return decision;
        style=style with {OutlineWidth=width,OutlineColor=Color.FromArgb(alpha,style.OutlineColor)};
        return decision with
        {
            Effective=style,
            CombinedReadability=Combined(style,background),
            Adjustment=Append(decision.Adjustment,"FONT_RELATIVE_OUTLINE_CAP")
        };
    }

    public static double Combined(SourceStyleBundle style, Color background)
    {
        var fill = TranslationTextColorResolver.Contrast(Color.FromArgb(255, style.FillColor), background);
        var edge = 0d;
        if (style.HasOutline)
        {
            var fillEdge = TranslationTextColorResolver.Contrast(Color.FromArgb(255, style.FillColor), Color.FromArgb(255, style.OutlineColor));
            var edgeBackground = TranslationTextColorResolver.Contrast(Color.FromArgb(255, style.OutlineColor), background);
            var widthFactor = Math.Clamp(.78 + style.OutlineWidth * .13, .82, 1.18);
            edge = Math.Min(fillEdge, edgeBackground) * widthFactor;
        }
        var shadow = 0d;
        if (style.HasShadow)
        {
            var fillShadow = TranslationTextColorResolver.Contrast(Color.FromArgb(255, style.FillColor), Color.FromArgb(255, style.ShadowColor));
            var shadowBackground = TranslationTextColorResolver.Contrast(Color.FromArgb(255, style.ShadowColor), background);
            shadow = Math.Min(fillShadow, shadowBackground) * .68;
        }
        return Math.Max(fill, Math.Max(edge, shadow));
    }

    private static string Append(string current,string next)=>current=="SOURCE_BUNDLE_UNCHANGED"?next:current+"+"+next;
    private static float MinimumSupportWidth(RegionRoleType role) => role is RegionRoleType.Title or RegionRoleType.Header ? 1.25f : .75f;
    private static double Required(SourceStyleBundle style,double fillOnlyThreshold)=>style.HasOutline?3d:fillOnlyThreshold;

    private static Color ReadabilitySupport(Color fill,Color background,RegionRoleType role)
    {
        var opposite=SourceStyleBundleOwner.Opposite(fill);
        if(role is RegionRoleType.Title or RegionRoleType.Header)return opposite;
        var backgroundSpread=Math.Max(background.R,Math.Max(background.G,background.B))-
            Math.Min(background.R,Math.Min(background.G,background.B));
        var mutedDarkChromaticSurface=backgroundSpread>=24&&background.GetBrightness()<.5f&&fill.GetBrightness()<.4f;
        if(!mutedDarkChromaticSurface)return opposite;
        // Dark text on a dark, strongly coloured card needs a restrained local edge rather than
        // a white glow.  Neutral grey information panels keep the brighter (but width-capped)
        // support because CJK body copy otherwise loses too much legibility.
        const float amount=.45f;
        return Color.FromArgb(255,
            (int)Math.Round(background.R*(1-amount)+opposite.R*amount),
            (int)Math.Round(background.G*(1-amount)+opposite.G*amount),
            (int)Math.Round(background.B*(1-amount)+opposite.B*amount));
    }

    private static Color BoundedLuminanceMove(Color fill, Color background, SourceStylePolarity polarity)
    {
        var target = SourceStyleBundleOwner.Opposite(background);
        var max = polarity == SourceStylePolarity.Chromatic ? .16 : .12;
        var adjusted=Color.FromArgb(255,
            (int)Math.Clamp(fill.R * (1 - max) + target.R * max, 0, 255),
            (int)Math.Clamp(fill.G * (1 - max) + target.G * max, 0, 255),
            (int)Math.Clamp(fill.B * (1 - max) + target.B * max, 0, 255));
        return polarity is SourceStylePolarity.Unknown||SourceStyleBundleOwner.ClassifyPolarity(adjusted)==polarity?adjusted:Color.FromArgb(255,fill);
    }

    private static Color MinimumReadableFill(Color fill,Color background,double threshold,SourceStylePolarity polarity,
        bool preservePolarity)
    {
        var target=preservePolarity?polarity switch
        {
            SourceStylePolarity.Light=>Color.White,
            SourceStylePolarity.Dark=>Color.Black,
            _=>TranslationTextColorResolver.Contrast(Color.White,background)>=
               TranslationTextColorResolver.Contrast(Color.Black,background)?Color.White:Color.Black
        }:TranslationTextColorResolver.Contrast(Color.White,background)>=
          TranslationTextColorResolver.Contrast(Color.Black,background)?Color.White:Color.Black;
        var best=Color.FromArgb(255,fill);
        for(var step=1;step<=50;step++)
        {
            var t=step/50f;
            var candidate=Color.FromArgb(255,
                (int)Math.Round(fill.R*(1-t)+target.R*t),
                (int)Math.Round(fill.G*(1-t)+target.G*t),
                (int)Math.Round(fill.B*(1-t)+target.B*t));
            if(preservePolarity&&polarity is not SourceStylePolarity.Unknown and not SourceStylePolarity.Chromatic&&
               SourceStyleBundleOwner.ClassifyPolarity(candidate)!=polarity)break;
            best=candidate;
            if(TranslationTextColorResolver.Contrast(candidate,background)>=threshold+.15)return candidate;
        }
        return best;
    }
}
