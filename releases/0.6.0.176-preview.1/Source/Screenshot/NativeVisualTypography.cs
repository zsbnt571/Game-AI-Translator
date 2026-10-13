namespace ScreenshotTranslationUiTester.CorePipelineV2;

internal sealed record NativeWeightLineEvidence(string SourceId, int InkPixels, int InteriorPixels,
    int Area, float InkFraction, float InteriorFraction, float StrokeRatio, bool StrongSolidStroke);

internal sealed record NativeWeightDecision(SourceStyleBundle Effective, bool Applied, string Reason,
    IReadOnlyList<NativeWeightLineEvidence> Lines);

internal sealed record NativeBodySupportDecision(SourceStyleGuardDecision Decision,bool Applied,string Reason,
    int BackgroundCandidatePixels,int CandidateFarFromInkPixels,float CandidateFarFraction,
    float CandidateCoverage,double SourceFillContrast,IReadOnlyList<NativeWeightLineEvidence> SourceLines)
{
    public bool UseGridFittedFill {get;init;}
}

/// <summary>Recovers solid light body weight from glyph strokes, independently of a single color cluster's share.</summary>
internal static class NativeVisualTypography
{
    internal static NativeBodySupportDecision RefineBodySupport(Bitmap source,VisualBlock block,
        SourceStyleGuardDecision guard,float fontSize)
    {
        var bundle=guard.Source;
        NativeBodySupportDecision No(string reason,IReadOnlyList<NativeWeightLineEvidence>? evidence=null) =>
            new(guard,false,reason,0,0,0,0,0,evidence??[]);
        if(bundle.Owner=="UserTextColorOverride" || bundle.Role!=RegionRoleType.BodyParagraph ||
            block.Lines.Count<3 || CorePipelineCorpusRunner.PreserveIndependentFieldBreaks(block) ||
            bundle.Polarity!=SourceStylePolarity.Light || bundle.Confidence<.52f || !bundle.HasOutline ||
            !bundle.Weight.HasFlag(FontStyle.Bold) || fontSize<18 ||
            Math.Min(bundle.FillColor.R,Math.Min(bundle.FillColor.G,bundle.FillColor.B))<220)
            return No("EXISTING_BODY_SUPPORT_OWNER");
        var evidence=block.Lines.Select(line=>Measure(source,line,bundle.FillColor)).ToArray();
        if(evidence.Count(x=>x.StrongSolidStroke)<Math.Ceiling(evidence.Length*.67))
            return No("NO_SOLID_SOURCE_STROKE_SUPPORT",evidence);
        var background=SourceStyleBundleOwner.EstimateSourceBackground(source,block.Lines.Select(x=>x.Polygon).ToArray(),block.Bounds);
        var contrast=TranslationTextColorResolver.Contrast(bundle.FillColor,background);
        // Very low contrast source still needs the existing guard. A demonstrably
        // dark outline must also remain intact even when the fill is near-white.
        if(contrast<1.25 || Distance(bundle.OutlineColor,background)>85 ||
            bundle.OutlineColor.GetBrightness()<background.GetBrightness()-.15f)
            return No("OUTLINE_OR_LOW_CONTRAST_SUPPORT_REQUIRED",evidence);
        var matched=0;var far=0;var area=0;
        foreach(var line in block.Lines)
        {
            var bounds=Rectangle.Intersect(Rectangle.Ceiling(line.Bounds),new Rectangle(Point.Empty,source.Size));
            for(var y=bounds.Top;y<bounds.Bottom;y++)for(var x=bounds.Left;x<bounds.Right;x++)
            {
                area++;var c=source.GetPixel(x,y);
                if(Distance(c,bundle.OutlineColor)>65 || Distance(c,bundle.FillColor)<70)continue;
                matched++;var near=false;
                for(var dy=-1;dy<=1&&!near;dy++)for(var dx=-1;dx<=1;dx++)
                {
                    var nx=x+dx;var ny=y+dy;if(nx<0||ny<0||nx>=source.Width||ny>=source.Height)continue;
                    var neighbor=source.GetPixel(nx,ny);
                    if(Math.Min(neighbor.R,Math.Min(neighbor.G,neighbor.B))>=220 && Distance(neighbor,bundle.FillColor)<=65){near=true;break;}
                }
                if(!near)far++;
            }
        }
        var fraction=far/(float)Math.Max(1,matched);var coverage=matched/(float)Math.Max(1,area);
        if(coverage<.25f || fraction<.65f)
            return new(guard,false,"CANDIDATE_EDGE_NOT_PROVEN_BACKGROUND",matched,far,fraction,coverage,contrast,evidence);
        // The source's supposed outline is widespread panel material. Keep its
        // opaque light fill and bold stroke. A smaller translucent support edge
        // remains on low-contrast panels instead of a heavy black contour.
        var effective=guard.Effective with
        {
            FillColor=Color.FromArgb(255,guard.Effective.FillColor),Alpha=255,
            OutlineColor=Color.Transparent,OutlineWidth=0,
            ShadowColor=contrast>=4.5?Color.Transparent:Color.FromArgb(65,guard.Effective.OutlineColor),
            ShadowOffset=contrast>=4.5?PointF.Empty:new PointF(.35f,.35f),
            Evidence=guard.Effective.Evidence+";NATIVE_SOURCE_OUTLINE_IS_PANEL_MATERIAL"
        };
        var decision=guard with {Effective=effective,Adjustment=guard.Adjustment+"+NATIVE_SOLID_BODY_SUPPORT",
            CombinedReadability=SourceStyleLegibilityGuard.Combined(effective,background)};
        return new(decision,true,"BACKGROUND_COLOR_REJECTED_AS_SOURCE_OUTLINE",matched,far,fraction,coverage,contrast,evidence)
            {UseGridFittedFill=true};
    }

    internal static void DrawSupportedBody(Graphics graphics,string text,Font font,PointF origin,NativeBodySupportDecision support)
    {
        var style=support.Decision.Effective;
        if(!support.Applied || !support.UseGridFittedFill)
        {
            SourceStyleTextDrawingR2.Draw(graphics,text,font,origin,style.FillColor,style.HasOutline,style.OutlineColor,
                style.OutlineWidth,style.HasShadow,style.ShadowColor,style.ShadowOffset,style.Glow,style.Alpha);
            return;
        }
        // Raster-grid fitting preserves small filled CJK strokes. Outlining a
        // vector path bypasses that fitting and makes the same Bold font look
        // thinner. This route is restricted to the source-proven solid body.
        var previous=graphics.TextRenderingHint;
        try
        {
            graphics.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            if(style.HasShadow)
            {
                using var shadow=new SolidBrush(style.ShadowColor);
                graphics.DrawString(text,font,shadow,new PointF(origin.X+style.ShadowOffset.X,origin.Y+style.ShadowOffset.Y),StringFormat.GenericTypographic);
            }
            using var fill=new SolidBrush(Color.FromArgb(style.Alpha,style.FillColor));
            graphics.DrawString(text,font,fill,origin,StringFormat.GenericTypographic);
        }
        finally{graphics.TextRenderingHint=previous;}
    }

    private static int Distance(Color a,Color b)=>Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B);

    internal static NativeWeightDecision RefineSourceWeight(Bitmap source, VisualBlock block, SourceStyleBundle bundle)
    {
        if (bundle.Owner == "UserTextColorOverride" || bundle.Weight.HasFlag(FontStyle.Bold) ||
            bundle.Polarity != SourceStylePolarity.Light || bundle.Confidence < .52f ||
            Math.Min(bundle.FillColor.R, Math.Min(bundle.FillColor.G, bundle.FillColor.B)) < 220 ||
            bundle.Role != RegionRoleType.BodyParagraph || block.Lines.Count < 3 ||
            CorePipelineCorpusRunner.PreserveIndependentFieldBreaks(block))
            return new(bundle, false, "EXISTING_WEIGHT_OWNER", []);
        var lines = block.Lines.Select(line => Measure(source, line, bundle.FillColor)).ToArray();
        var usable = lines.Where(x => x.Area >= 160 && x.InkFraction is >= .05f and <= .48f).ToArray();
        if (usable.Length < 3 || usable.Count(x => x.StrongSolidStroke) < Math.Ceiling(usable.Length * .67))
            return new(bundle, false, "NO_CONSISTENT_SOLID_BODY_STROKES", lines);
        var effective = bundle with
        {
            Weight = bundle.Weight | FontStyle.Bold,
            Evidence = bundle.Evidence + ";NATIVE_SOLID_LIGHT_BODY_STROKES;strong=" + usable.Count(x => x.StrongSolidStroke) + "/" + usable.Length
        };
        return new(effective, true, "SOURCE_SOLID_LIGHT_BODY_WEIGHT", lines);
    }

    internal static NativeWeightLineEvidence Measure(Bitmap source, NormalizedOcrLine line, Color fill)
    {
        var bounds = Rectangle.Intersect(Rectangle.Ceiling(line.Bounds), new Rectangle(Point.Empty, source.Size));
        if (bounds.Width < 4 || bounds.Height is < 14 or > 72) return new(line.SourceId, 0, 0, 0, 0, 0, 0, false);
        var ink = new bool[bounds.Width, bounds.Height]; var count = 0; var core = 0;
        for (var y = 0; y < bounds.Height; y++)
        for (var x = 0; x < bounds.Width; x++)
        {
            var c = source.GetPixel(bounds.Left + x, bounds.Top + y);
            var min = Math.Min(c.R, Math.Min(c.G, c.B)); var max = Math.Max(c.R, Math.Max(c.G, c.B));
            if (min >= 220 && max - min <= 40 && Math.Abs(c.R - fill.R) + Math.Abs(c.G - fill.G) + Math.Abs(c.B - fill.B) <= 65)
            { ink[x, y] = true; count++; }
        }
        for (var y = 1; y < bounds.Height - 1; y++)
        for (var x = 1; x < bounds.Width - 1; x++)
            if (ink[x, y] && ink[x - 1, y] && ink[x + 1, y] && ink[x, y - 1] && ink[x, y + 1]) core++;
        var fraction = count / (float)Math.Max(1, bounds.Width * bounds.Height);
        var interior = core / (float)Math.Max(1, count);
        var horizontal = new int[bounds.Width, bounds.Height]; var strokes = new List<int>();
        for (var y = 0; y < bounds.Height; y++)
        for (var x = 0; x < bounds.Width;)
        {
            if (!ink[x, y]) { x++; continue; }
            var start = x; while (x < bounds.Width && ink[x, y]) x++;
            for (var fillX = start; fillX < x; fillX++) horizontal[fillX, y] = x - start;
        }
        for (var x = 0; x < bounds.Width; x++)
        for (var y = 0; y < bounds.Height;)
        {
            if (!ink[x, y]) { y++; continue; }
            var start = y; while (y < bounds.Height && ink[x, y]) y++;
            for (var fillY = start; fillY < y; fillY++)
            {
                var width = Math.Min(horizontal[x, fillY], y - start);
                if (width < bounds.Height * .45f) strokes.Add(width);
            }
        }
        strokes.Sort();
        var strokeRatio = strokes.Count == 0 ? 0 : strokes[(int)((strokes.Count - 1) * .75f)] / (float)bounds.Height;
        return new(line.SourceId, count, core, bounds.Width * bounds.Height, fraction, interior, strokeRatio,
            // Normalizing the stroke width prevents a large Regular font from
            // appearing Bold merely because its strokes span more pixels.
            fraction is >= .14f and <= .42f && interior >= .18f && strokeRatio >= .12f);
    }
}
