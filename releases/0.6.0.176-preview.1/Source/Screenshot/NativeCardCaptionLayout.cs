using System.Drawing.Drawing2D;
using System.Globalization;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

/// <summary>Two source rows on bounded light paper: a caption and a numeric effect.
/// Owns draw geometry only; source polygons and restoration authority never change.</summary>
internal static class NativeCardCaptionLayout
{
    internal static NativeVisualLayoutPlan Plan(Graphics g, Bitmap source, VisualBlock block,
        IReadOnlyList<VisualBlock> blocks, string text, string family, FontStyle weight,
        float preferred, string alignment, SourceStyleBundle style)
    {
        const string kind = "PaperCardCaptionEffect";
        var checkedPixels = 0;
        var failure = "";
        var search = block.Bounds;
        NativeVisualLayoutPlan Reject(string reason, bool candidate = true) =>
            new(candidate, false, reason, kind, block.Bounds, block.Bounds, search,
                preferred, preferred, 0, 0, 0, alignment, "SourceRowTop", [], checkedPixels, true, true)
                { LayoutInput = text, BreakPolicy = "CaptionAndEffectHardRows" };
        if (block.Lines.Count != 2 || block.RoleHint is "PossibleControl" or "PossibleTitle" ||
            style.Role is not (RegionRoleType.Unknown or RegionRoleType.Caption) ||
            style.Polarity != SourceStylePolarity.Dark || style.Confidence < .75f ||
            preferred < 14 || preferred > 28 || block.Lines.Any(l => l.Confidence < .8f))
            return Reject("NOT_BOUNDED_PAPER_CAPTION_EFFECT", false);
        var sourceLines = block.Lines.OrderBy(l => l.Bounds.Top).ToArray();
        var first = sourceLines[0]; var second = sourceLines[1];
        var sourceHeight = sourceLines.Max(l => l.Bounds.Height);
        var step = second.Bounds.Top - first.Bounds.Top;
        // A short caption above a separately placed quantitative effect is narrower
        // than arbitrary two-line prose, choices, metadata, or same-baseline labels.
        if (sourceLines.Any(l => l.SourceText.IndexOfAny([':', '：', '\n', '\r']) >= 0) ||
            first.SourceText.TrimEnd().EndsWithAnySentencePunctuation() ||
            !second.SourceText.Any(char.IsDigit) || first.SourceText.Length > 64 ||
            second.SourceText.Length > 160 || second.Bounds.Top - first.Bounds.Bottom < 2 ||
            step < sourceHeight * 1.05f || step > sourceHeight * 3 ||
            block.Bounds.Width < preferred * 4 || block.Bounds.Width > source.Width * .3f)
            return Reject("SOURCE_CAPTION_EFFECT_STRUCTURE_NOT_PROVEN", false);
        var normalized = text.Replace("\r", "");
        var translated = normalized.Split('\n');
        if (translated.Length != 2 || translated.Any(string.IsNullOrWhiteSpace))
            return Reject("TWO_ACCEPTED_HARD_ROWS_REQUIRED");
        var others = blocks.Where(b => b.BlockId != block.BlockId)
            .Select(b => RectangleF.Inflate(b.Bounds, 3, 3)).ToArray();
        var middle = block.Bounds.Left + block.Bounds.Width / 2;
        var seedY = (int)MathF.Floor((first.Bounds.Bottom + second.Bounds.Top) / 2);
        var samples = new List<Color>();
        for (var x = (int)MathF.Ceiling(block.Bounds.Left); x < block.Bounds.Right; x++)
        {
            var c = source.GetPixel(Math.Clamp(x, 0, source.Width - 1), Math.Clamp(seedY, 0, source.Height - 1));
            if (LightPaper(c)) samples.Add(c);
        }
        if (samples.Count < block.Bounds.Width * .9f) return Reject("CAPTION_GAP_NOT_LIGHT_PAPER");
        int Median(Func<Color, int> channel) => samples.Select(channel).Order().ElementAt(samples.Count / 2);
        var paper = Color.FromArgb(Median(c => c.R), Median(c => c.G), Median(c => c.B));
        if (TranslationTextColorResolver.Contrast(paper, style.FillColor) < 5)
            return Reject("SOURCE_INK_PAPER_CONTRAST_NOT_PROVEN");
        bool Paper(int x, int y)
        {
            checkedPixels++;
            if (x < 0 || y < 0 || x >= source.Width || y >= source.Height ||
                others.Any(b => b.Contains(x + .5f, y + .5f))) return false;
            var c = source.GetPixel(x, y);
            return LightPaper(c) && MaxDistance(c, paper) <= 24 && Distance(c, paper) <= 56;
        }
        var extension = Math.Min(block.Bounds.Width * .8f, preferred * 6);
        var leftLimit = Math.Max(1, (int)MathF.Ceiling(block.Bounds.Left - extension));
        var rightLimit = Math.Min(source.Width - 2, (int)MathF.Floor(block.Bounds.Right + extension));
        var corridors = new List<(int Left, int Right)>();
        foreach (var y in new[] { seedY, (int)MathF.Ceiling(block.Bounds.Bottom + 2),
            (int)MathF.Ceiling(block.Bounds.Bottom + preferred * .65f) })
        {
            var center = (int)MathF.Round(middle);
            if (!Paper(center, y)) return Reject("PAPER_CORRIDOR_CENTER_BLOCKED");
            var left = center; while (left > leftLimit && Paper(left - 1, y)) left--;
            var right = center + 1; while (right < rightLimit && Paper(right, y)) right++;
            // Both borders must be observed; an unbounded blank background is not
            // evidence of a card, and scanning is never allowed to cross a border.
            if (left == leftLimit || right == rightLimit) return Reject("BOUNDED_PAPER_SIDES_NOT_PROVEN");
            corridors.Add((left, right));
        }
        if (corridors.Max(x => x.Left) - corridors.Min(x => x.Left) > preferred * .35f ||
            corridors.Max(x => x.Right) - corridors.Min(x => x.Right) > preferred * .35f)
            return Reject("PAPER_SIDES_NOT_CONTINUOUS");
        var leftEdge = corridors.Max(x => x.Left) + 3;
        var rightEdge = corridors.Min(x => x.Right) - 3;
        var panelWidth = rightEdge - leftEdge;
        if (panelWidth < block.Bounds.Width * .8f || panelWidth > preferred * 20)
            return Reject("PAPER_WIDTH_OUTSIDE_CAPTION_SCALE");
        var panelCenter = (leftEdge + rightEdge) / 2f;
        var centered = sourceLines.All(l => Math.Abs(l.Bounds.Left + l.Bounds.Width / 2 - panelCenter) <= sourceHeight * .55f);
        var commonLeft = Math.Abs(first.Bounds.Left - second.Bounds.Left) <= sourceHeight * .3f;
        if (!centered && !commonLeft) return Reject("SOURCE_ROW_ALIGNMENT_NOT_PROVEN");
        var bottomLimit = Math.Min(source.Height - 2, block.Bounds.Bottom + preferred * 3.4f);
        search = RectangleF.FromLTRB(leftEdge, first.Bounds.Top, rightEdge, bottomLimit);
        bool Safe(RectangleF ink)
        {
            var padded = RectangleF.Inflate(ink, 2, 2);
            for (var y = (int)MathF.Floor(padded.Top); y < MathF.Ceiling(padded.Bottom); y++)
            for (var x = (int)MathF.Floor(padded.Left); x < MathF.Ceiling(padded.Right); x++)
            {
                if (x < leftEdge || x >= rightEdge || y < first.Bounds.Top - 2 || y >= bottomLimit ||
                    others.Any(b => b.Contains(x + .5f, y + .5f))) { failure=$"BOUNDS_OR_NEIGHBOR:{x},{y}"; return false; }
                if (Paper(x, y)) continue;
                // Existing source glyphs may occupy their original polygons only.
                // Outside them every pixel, including antialias fringe, must be paper.
                if (!sourceLines.Any(l => Inside(new PointF(x + .5f, y + .5f), l.Polygon))) { failure=$"OUTSIDE_SOURCE_POLYGON:{x},{y};pixel={source.GetPixel(x,y)};paper={paper}"; return false; }
                var c = source.GetPixel(x, y);
                // The estimated source fill is a cluster median; solid black
                // interior glyph pixels can be darker than that median.
                var darkerNeutralInk = Math.Max(c.R,Math.Max(c.G,c.B)) <= Math.Max(style.FillColor.R,Math.Max(style.FillColor.G,style.FillColor.B)) &&
                    Math.Max(c.R,Math.Max(c.G,c.B))-Math.Min(c.R,Math.Min(c.G,c.B)) <= 16;
                if (!darkerNeutralInk && Distance(c, style.FillColor) > 80 && !OnInkSegment(c, paper, style.FillColor)) { failure=$"NOT_INK_OR_PAPER:{x},{y};pixel={c};paper={paper}"; return false; }
            }
            return true;
        }
        using var font = FontManager.CreatePixel(family, preferred, weight);
        float Measure(string value) => g.MeasureString(value, font, PointF.Empty, StringFormat.GenericTypographic).Width;
        RectangleF Ink(string value)
        {
            using var path = new GraphicsPath();
            path.AddString(value, font.FontFamily, (int)weight, preferred, PointF.Empty, StringFormat.GenericTypographic);
            return path.PointCount == 0 ? RectangleF.Empty : path.GetBounds();
        }
        var availableWidth = centered ? panelWidth - 8 : rightEdge - first.Bounds.Left - 6;
        if (Measure(translated[0]) > availableWidth) return Reject("CAPTION_EXCEEDS_PREFERRED_WIDTH");
        var rows = new List<string> { translated[0] };
        var elements = Elements(translated[1]); var cursor = 0;
        while (cursor < elements.Length)
        {
            if (rows.Count >= 4) return Reject("EFFECT_EXCEEDS_BOUNDED_READABLE_LINES");
            var end = cursor;
            while (end < elements.Length && Measure(string.Concat(elements.Skip(cursor).Take(end - cursor + 1))) <= availableWidth) end++;
            if (end == cursor) return Reject("EFFECT_UNIT_EXCEEDS_PREFERRED_WIDTH");
            if (end < elements.Length)
            {
                var cut = end;
                while (cut > cursor && Word(elements[cut - 1]) && Word(elements[cut])) cut--;
                if (cut > cursor) end = cut;
                else if (Word(elements[end - 1]) && Word(elements[end])) return Reject("EFFECT_WORD_EXCEEDS_PREFERRED_WIDTH");
                if (end > cursor + 1 && Closing(elements[end])) end--;
            }
            rows.Add(string.Concat(elements.Skip(cursor).Take(end - cursor))); cursor = end;
        }
        var plans = new List<NativeVisualLine>(); var safeRects = new List<RectangleF>();
        var yTop = first.Bounds.Top + 1; var lastBottom = yTop;
        foreach (var (value, index) in rows.Select((s, i) => (s, i)))
        {
            var advance = Measure(value); var ink = Ink(value);
            if (index == 1) yTop = Math.Max(second.Bounds.Top + 1, lastBottom + 3);
            if (index > 1) yTop = lastBottom + Math.Max(3, preferred * .15f);
            var x = centered ? panelCenter - advance / 2 : first.Bounds.Left + 1;
            var inkRect = new RectangleF(x + ink.Left, yTop, ink.Width, ink.Height);
            if (ink.Height <= 0 || !Safe(inkRect)) return Reject("ACTUAL_INK_NOT_ON_SAFE_PAPER;"+failure);
            plans.Add(new(value, new RectangleF(x, yTop - ink.Top, advance, font.GetHeight(g))) { AdvanceWidth = advance });
            safeRects.Add(RectangleF.FromLTRB(leftEdge, yTop - 2, rightEdge, inkRect.Bottom + 2));
            lastBottom = inkRect.Bottom;
        }
        if (plans[0].Text != translated[0] || string.Concat(plans.Skip(1).Select(l => l.Text)) != translated[1])
            return Reject("CAPTION_EFFECT_LITERAL_CONTENT_CHANGED");
        var union = plans.Select(l => l.Bounds).Aggregate(RectangleF.Union);
        return new(true, true, "BOUNDED_PAPER_CAPTION_EFFECT_AT_SOURCE_PREFERENCE", kind,
            block.Bounds, union, search, preferred, preferred, font.GetHeight(g),
            plans.Max(l => l.AdvanceWidth), union.Height, centered ? "SourcePanelCenter" : "SourceRowLeft",
            "SourceRowTop", plans, checkedPixels, true, true)
            { LayoutInput = normalized, SafeLineRects = safeRects, BreakPolicy = "CaptionAndEffectHardRows_EffectMayWrap" };
    }
    private static bool EndsWithAnySentencePunctuation(this string value) => value.Length > 0 && ".!?。！？".Contains(value[^1]);
    private static bool LightPaper(Color c) => c.A == 255 && c.R >= 180 && c.G >= 175 && c.B >= 150 &&
        Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) <= 65;
    private static int Distance(Color a, Color b) => Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B);
    private static int MaxDistance(Color a, Color b) => Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B)));
    private static bool OnInkSegment(Color c, Color paper, Color ink)
    {
        var dr=ink.R-paper.R;var dg=ink.G-paper.G;var db=ink.B-paper.B;var norm=dr*dr+dg*dg+db*db;
        if(norm<100)return false;var t=((c.R-paper.R)*dr+(c.G-paper.G)*dg+(c.B-paper.B)*db)/(float)norm;
        return t>=0&&t<=1.1f&&Math.Abs(c.R-paper.R-t*dr)+Math.Abs(c.G-paper.G-t*dg)+Math.Abs(c.B-paper.B-t*db)<=45;
    }
    private static bool Word(string s)=>s.Length==1&&(char.IsAsciiLetterOrDigit(s[0])||s[0] is '-' or '%' or '.');
    private static bool Closing(string s)=>s.Length==1&&"，。！？：；、,.!?:;)）】》」』".Contains(s[0]);
    private static bool Inside(PointF p,IReadOnlyList<PointF> polygon)
    {
        var inside=false;
        for(int i=0,j=polygon.Count-1;i<polygon.Count;j=i++)
        {var a=polygon[i];var b=polygon[j];if((a.Y>p.Y)!=(b.Y>p.Y)&&p.X<(b.X-a.X)*(p.Y-a.Y)/(b.Y-a.Y)+a.X)inside=!inside;}
        return inside;
    }
    private static string[] Elements(string value){var parts=new List<string>();var e=StringInfo.GetTextElementEnumerator(value);while(e.MoveNext())parts.Add(e.GetTextElement());return parts.ToArray();}
}
