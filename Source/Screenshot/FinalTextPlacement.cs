using System.Drawing.Drawing2D;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

// A final placement contains measured ink and immutable draw origins. It owns no
// cleanup rectangle. Drawing this object cannot choose another size or position.
internal static class FinalTextPlacement
{
    internal sealed record Paint(string Family, FontStyle Weight, float Size, Color Fill,
        bool Outline, Color OutlineColor, float OutlineWidth, bool Shadow, Color ShadowColor,
        PointF ShadowOffset, float Glow, byte Alpha, bool SourcePixelScale = false,
        NativeBodySupportDecision? BodySupport = null,float RotationDegrees=0,PointF RotationOrigin=default);
    internal sealed record Row(string Text, PointF Origin, RectangleF Ink, RectangleF Safe, Paint Paint);
    internal sealed record Plan(string Owner, bool Valid, string Reason, string Text,
        IReadOnlyList<Row> Rows, RectangleF Ink);

    internal static Plan Freeze(Graphics g, string owner, string expected,
        IReadOnlyList<NativeVisualLine> lines, RectangleF available,
        IReadOnlyList<RectangleF> safeRows, Paint paint, Size canvas)
    {
        var rows = new List<Row>(); var union = RectangleF.Empty;
        var actual = string.Concat(lines.Select(l => l.Text));
        if (Normalize(expected) != Normalize(actual) || Normalize(actual).Length == 0)
            return new(owner, false, "FINAL_CONTENT_MISMATCH", actual, [], union);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var rowPaint = paint with { Size = line.ProvenFontSize ?? paint.Size,
                Fill = line.ProvenFill ?? paint.Fill };
            if (line.ProvenFill is not null)
                rowPaint = rowPaint with { Outline = false, Shadow = false, Glow = 0,
                    Alpha = 255, BodySupport = null };
            using var font = FontManager.CreatePixel(rowPaint.Family, rowPaint.Size,
                rowPaint.Weight, rowPaint.SourcePixelScale);
            using var path = new GraphicsPath();
            path.AddString(line.Text, font.FontFamily, (int)font.Style, font.Size,
                line.Bounds.Location, StringFormat.GenericTypographic);
            if(rowPaint.RotationDegrees!=0){using var transform=new Matrix();transform.RotateAt(rowPaint.RotationDegrees,rowPaint.RotationOrigin);path.Transform(transform);}
            var ink = path.PointCount == 0 ? RectangleF.Empty : path.GetBounds();
            if (rowPaint.Outline && rowPaint.OutlineWidth > 0)
            {
                var radius = Math.Clamp(rowPaint.OutlineWidth, .5f, 6) / 2;
                if (rowPaint.Glow > 0)
                    radius = Math.Max(radius, Math.Clamp(rowPaint.OutlineWidth + rowPaint.Glow * 2, 1, 8) / 2);
                ink.Inflate(radius, radius);
            }
            if (rowPaint.Shadow)
            {
                var shadow = ink; shadow.Offset(rowPaint.ShadowOffset);
                ink = RectangleF.Union(ink, shadow);
            }
            var safe = safeRows.Count == lines.Count ? safeRows[i] : available;
            rows.Add(new(line.Text, line.Bounds.Location, ink, safe, rowPaint));
            // One source pixel allows antialiasing at a proved boundary, not a
            // second layout margin. The canvas itself has no such tolerance.
            if (!Finite(ink) || !Finite(safe) || !Contains(safe, ink, 1) ||
                !Contains(new RectangleF(PointF.Empty, canvas), ink, 0))
                return new(owner, false, "FINAL_INK_OUTSIDE_PROVED_SPACE", actual, rows.ToArray(), ink);
            if (!ink.IsEmpty) union = union.IsEmpty ? ink : RectangleF.Union(union, ink);
        }
        return new(owner, true, "COMPLETE_FINAL_INK_VALIDATED", actual, rows.ToArray(), union);
    }


    // Negotiate small paint outsets before commit, then freeze one final plan.
    // It cannot enlarge ownership, truncate text, or rescue a generally bad fit.
    internal static Plan Resolve(Graphics g,string owner,string expected,
        IReadOnlyList<NativeVisualLine> lines,RectangleF available,
        IReadOnlyList<RectangleF> safeRows,Paint paint,Size canvas)
    {
        var first=Freeze(g,owner,expected,lines,available,safeRows,paint,canvas);
        if(first.Valid||first.Reason!="FINAL_INK_OUTSIDE_PROVED_SPACE"||
            lines.Count>12||(!paint.Outline&&!paint.Shadow))return first;
        var changed=lines.ToArray();
        for(var i=0;i<changed.Length;i++)
        {
            var safe=safeRows.Count==lines.Count?safeRows[i]:available;
            using var initialFont=FontManager.CreatePixel(paint.Family,
                changed[i].ProvenFontSize??paint.Size,paint.Weight,paint.SourcePixelScale);
            // The effective font may be capped by the chosen font contract.
            // All shrink limits must use that same measured size, not the
            // requested em value which may never have been rendered.
            var originalSize=initialFont.Size;
            for(var attempt=0;attempt<3;attempt++)
            {
                var probe=Freeze(g,owner,changed[i].Text,[changed[i]],safe,[],paint,canvas);
                if(probe.Valid)break;
                if(probe.Reason!="FINAL_INK_OUTSIDE_PROVED_SPACE"||probe.Rows.Count!=1)return first;
                var row=probe.Rows[0];var ink=row.Ink;
                var overflow=Math.Max(Math.Max(safe.Left-ink.Left,ink.Right-safe.Right),
                    Math.Max(safe.Top-ink.Top,ink.Bottom-safe.Bottom));
                if(overflow>4||ink.IsEmpty||safe.Width<7||safe.Height<7)return first;
                var scale=Math.Min(1,Math.Min((safe.Width-.2f)/ink.Width,(safe.Height-.2f)/ink.Height));
                using var measuredFont=FontManager.CreatePixel(row.Paint.Family,
                    row.Paint.Size,row.Paint.Weight,row.Paint.SourcePixelScale);
                var size=measuredFont.Size*Math.Min(1,scale);
                if(size<7||size<originalSize*.92f)return first;
                var bounds=changed[i].Bounds;
                if(scale<.999f)
                {
                    // Keep the existing ink centre while accounting for the actual
                    // font bearing and baseline at the newly negotiated size.
                    using var font=FontManager.CreatePixel(row.Paint.Family,size,row.Paint.Weight,row.Paint.SourcePixelScale);
                    using var path=new GraphicsPath();
                    path.AddString(changed[i].Text,font.FontFamily,(int)font.Style,font.Size,PointF.Empty,StringFormat.GenericTypographic);
                    var glyph=path.GetBounds();
                    bounds.X=ink.Left+ink.Width/2-glyph.Width/2-glyph.X;
                    bounds.Y=ink.Top+ink.Height/2-glyph.Height/2-glyph.Y;
                }
                else
                {
                    bounds.X+=Math.Max(safe.Left-ink.Left,Math.Min(0,safe.Right-ink.Right));
                    bounds.Y+=Math.Max(safe.Top-ink.Top,Math.Min(0,safe.Bottom-ink.Bottom));
                }
                changed[i]=changed[i] with{Bounds=bounds,ProvenFontSize=size};
            }
        }
        var final=Freeze(g,owner,expected,changed,available,safeRows,paint,canvas);
        return final.Valid?final with{Reason="COMPLETE_FINAL_INK_VALIDATED;BOUNDED_PAINT_OUTSET_NEGOTIATION"}:first;
    }

    internal static void Draw(Graphics g, Plan plan)
    {
        if (!plan.Valid) throw new InvalidOperationException("Cannot draw an unvalidated placement.");
        foreach (var row in plan.Rows)
        {
            var p = row.Paint;
            var state=g.Save();
            try{
            if(p.RotationDegrees!=0){using var transform=new Matrix();transform.RotateAt(p.RotationDegrees,p.RotationOrigin);g.MultiplyTransform(transform);}
            using var font = FontManager.CreatePixel(p.Family, p.Size, p.Weight, p.SourcePixelScale);
            if (p.BodySupport is { UseGridFittedFill: true } support)
                NativeVisualTypography.DrawSupportedBody(g, row.Text, font, row.Origin, support);
            else SourceStyleTextDrawingR2.Draw(g, row.Text, font, row.Origin, p.Fill,
                p.Outline, p.OutlineColor, p.OutlineWidth, p.Shadow, p.ShadowColor,
                p.ShadowOffset, p.Glow, p.Alpha);
            }finally{g.Restore(state);}
        }
    }

    private static string Normalize(string text) => new(text.Where(c => !char.IsWhiteSpace(c) &&
        c != '\u200B' && c != '\u00AD').ToArray());
    private static bool Finite(RectangleF b) => float.IsFinite(b.X) && float.IsFinite(b.Y) &&
        float.IsFinite(b.Width) && float.IsFinite(b.Height) && b.Width >= 0 && b.Height >= 0;
    private static bool Contains(RectangleF outer, RectangleF inner, float tolerance) =>
        inner.IsEmpty || inner.Left >= outer.Left - tolerance && inner.Top >= outer.Top - tolerance &&
        inner.Right <= outer.Right + tolerance && inner.Bottom <= outer.Bottom + tolerance;
}

internal static class FinalCleanupTransaction
{
    // References to existing masks, never a full-frame copy per owner.
    internal sealed record Authority(string Owner, bool[,] Pixels, Rectangle Bounds);
    internal static IReadOnlySet<string> RestoreFailed(Bitmap source, Bitmap candidate, bool[,] committed,
        IReadOnlyList<Authority> authorities, IEnumerable<string> failedOwners)
    {
        var failed = new HashSet<string>(failedOwners, StringComparer.Ordinal);
        for (var pass = 0; pass < authorities.Count; pass++)
        {
            var added = false;
            foreach (var a in authorities.Where(a => !failed.Contains(a.Owner)))
            foreach (var b in authorities.Where(b => failed.Contains(b.Owner)))
            {
                var overlap = Rectangle.Intersect(a.Bounds, b.Bounds);
                var shared = false;
                for (var y = overlap.Top; y < overlap.Bottom && !shared; y++)
                for (var x = overlap.Left; x < overlap.Right; x++)
                    if (a.Pixels[x, y] && b.Pixels[x, y]) { shared = true; break; }
                if (!shared) continue;
                failed.Add(a.Owner); added = true; break;
            }
            if (!added) break;
        }
        foreach (var a in authorities.Where(a => failed.Contains(a.Owner)))
        for (var y = a.Bounds.Top; y < a.Bounds.Bottom; y++)
        for (var x = a.Bounds.Left; x < a.Bounds.Right; x++)
            if (a.Pixels[x, y]) { candidate.SetPixel(x, y, source.GetPixel(x, y)); committed[x, y] = false; }
        return failed;
    }
}
