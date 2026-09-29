using System.Globalization;
using System.Drawing.Drawing2D;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

internal sealed record NativeVisualLine(string Text, RectangleF Bounds)
{
    public Color? ProvenFill {get;init;}
    public float? ProvenFontSize {get;init;}
    public float AdvanceWidth { get; init; }
    public float TrailingBlankAdvanceTrimmed => Math.Max(0, AdvanceWidth - Bounds.Width);
}

internal sealed record NativeVisualLayoutPlan(
    bool Candidate, bool Applied, string Reason, string Kind, RectangleF SourceBounds,
    RectangleF AvailableRect, RectangleF SearchBounds, float PreferredFontSize, float FontSize,
    float LineHeight, float RequiredWidth, float RequiredHeight, string Alignment,
    string VerticalAnchor, IReadOnlyList<NativeVisualLine> Lines, int CheckedPixels,
    bool CharactersPreserved, bool SourceGeometryUnchanged)
{
    public string LayoutInput { get; init; } = "";
    public ParagraphAlignmentEvidence? ParagraphAlignment { get; init; }
    public string WordBoundaryEngine { get; init; } = "EXISTING";
    public IReadOnlyList<RectangleF> SafeLineRects { get; init; } = [];
    public string BreakPolicy { get; init; } = "Preserved";
    public bool SourcePixelScale {get;init;}
    public IReadOnlyList<FootnoteFitAttempt> FitAttempts { get; init; } = [];
}
internal sealed record FootnoteFitAttempt(float Size,int Rows,IReadOnlyList<RectangleF> Spans,string Outcome)
{
}

/// <summary>
/// Owns translated text geometry only. A successful plan does not grant restoration
/// authority: source polygons, block identities and cleanup masks remain unchanged.
/// </summary>
internal static class NativeVisualLayout
{
    internal static NativeVisualLayoutPlan PlanCard(Graphics graphics, Bitmap source, VisualBlock block,
        IReadOnlyList<VisualBlock> blocks, string text, string fontFamily, FontStyle fontStyle,
        float preferredSize, string sourceAlignment, SourceStyleBundle sourceStyle,ReadOnlyBitmapPixelBuffer? pixels=null,
        bool sourceLeftAnchor=false)
    {
        var card=NativeCardLayout.Plan(graphics,source,block,blocks,text,fontFamily,fontStyle,
            preferredSize,sourceAlignment,sourceStyle);
        return card.Candidate || block.Lines.Count!=1 ? card : NativeCompactTextLayout.Plan(graphics,source,block,blocks,text,
            fontFamily,fontStyle,preferredSize,sourceAlignment,sourceStyle,pixels,sourceLeftAnchor);
    }

    internal static NativeVisualLayoutPlan Plan(Graphics graphics, Bitmap source, VisualBlock block,
        IReadOnlyList<VisualBlock> blocks, string text, string fontFamily, FontStyle fontStyle,
        float preferredSize, string sourceAlignment, IReadOnlyList<string>? protectedTerms = null,
        bool preferProtectedTerms = false, NativeVisualLayoutPlan? admittedPlan = null)
    {
        var vertical=PlanVerticalLabel(graphics,block,text,fontFamily,fontStyle,preferredSize);
        if(vertical is not null)return vertical;
        var attempts=new List<FootnoteFitAttempt>();
        var bounds = block.Bounds;
        var sourceHeight = block.Lines.Select(x => x.Bounds.Height).DefaultIfEmpty(bounds.Height).Max();
        NativeVisualLayoutPlan Decline(bool candidate, string reason, RectangleF search = default, int checkedPixels = 0) =>
            new(candidate, false, reason, candidate ? "IndependentFootnote" : "ExistingLayout", bounds,
                bounds, search, preferredSize, preferredSize, 0, 0, 0, sourceAlignment, "Source",
                [], checkedPixels, true, true) { LayoutInput = text,FitAttempts=attempts };

        // A footer is a separate short-height line near the lower trailing margin,
        // not a multi-line metadata group, a button, or a line inside a paragraph.
        if (block.Lines.Count != 1 || block.RoleHint is "PossibleControl" or "PossibleTitle" ||
            bounds.Width < sourceHeight * 5 || bounds.Width > source.Width * .36f ||
            sourceHeight > source.Height * .035f || bounds.Bottom < source.Height * .82f ||
            bounds.Right < source.Width * .82f || bounds.Width <= 0 || sourceHeight <= 0)
            return Decline(false, "NOT_INDEPENDENT_TRAILING_FOOTNOTE");
        var close = RectangleF.Inflate(bounds, sourceHeight * .55f, sourceHeight * .85f);
        if (blocks.Any(x => x.BlockId != block.BlockId && x.Bounds.IntersectsWith(close)))
            return Decline(false, "NEIGHBOR_TEXT_NOT_INDEPENDENT");
        // A lone label:value entry is still a field. This is structural punctuation,
        // not matching a particular word, language, name or expected translation.
        if (IsField(block.SourceText)) return Decline(false, "SINGLE_METADATA_FIELD");

        var original = Rectangle.Intersect(Rectangle.Ceiling(bounds), new Rectangle(Point.Empty, source.Size));
        if (original.Width < 1 || original.Height < 1) return Decline(false, "INVALID_SOURCE_GEOMETRY");
        var paper = SamplePaper(source, original);
        if (paper is null) return Decline(false, "NO_LIGHT_NEUTRAL_FOOTNOTE_SURFACE");
        var leftLimit = admittedPlan?.SearchBounds.Left ?? Math.Max(0, bounds.Left - Math.Min(source.Width * .15f, bounds.Width * 2));
        var topLimit = admittedPlan?.SearchBounds.Top ?? Math.Max(0, bounds.Top - Math.Min(source.Height * .10f, Math.Max(sourceHeight * 6, preferredSize * 4.5f)));
        var searchBounds = RectangleF.FromLTRB(leftLimit, topLimit, bounds.Right, bounds.Bottom);
        var protectedText = blocks.Where(x => x.BlockId != block.BlockId)
            .Select(x => RectangleF.Inflate(x.Bounds, 3, 3)).ToArray();
        var normalized = text.Replace("\r", "");
        if (string.IsNullOrWhiteSpace(normalized)) return Decline(true, "EMPTY_TRANSLATION", searchBounds);
        // Keep the normal source-derived preference, with a readable floor and a
        // bounded footnote scale. Space is tried before reducing size.
        // A presentation-only second pass reuses the admitted preference; applying
        // the source scale twice can raise the floor until complete text no longer fits.
        var desired = admittedPlan?.PreferredFontSize ?? Math.Clamp(Math.Max(preferredSize * 1.2f, source.Height * .018f), 12f, 24f);
        // Reuse the admitted search space, not its winning size as a new floor.
        // Otherwise a complete-name second pass cannot try the original bounded
        // size range after its first unprotected fit selected the largest font.
        var floor = Math.Max(12f, desired * .8f);
        var checkedPixels = 0;
        var targetRight = Math.Min(source.Width - 1f, bounds.Right - 1f);
        var targetBottom = Math.Min(source.Height - 1f, bounds.Bottom - 1f);
        var sourceAllowance = RectangleF.Inflate(bounds, 1, 1);
        var elements = TextElements(normalized);
        var wordBreaks=PreferredWordBreaks.Create(normalized,protectedTerms);
        var protectedCuts=new HashSet<int>();
        if(preferProtectedTerms)foreach(var term in protectedTerms??Array.Empty<string>())
        {
            if(term.Length<2||term.Length>16)continue;
            for(var at=normalized.IndexOf(term,StringComparison.Ordinal);at>=0;at=normalized.IndexOf(term,at+1,StringComparison.Ordinal))
                for(var cut=at+1;cut<at+term.Length;cut++)protectedCuts.Add(cut);
        }
        NativeVisualLayoutPlan? completeFallback=admittedPlan is {Applied:true,CharactersPreserved:true}&&
            admittedPlan.LayoutInput==normalized?admittedPlan:null;
        // Prefer a compact paragraph over a larger-font staircase. Every row
        // uses a common corridor and one aligned left edge.
        for (var lineCount = 1; lineCount <= 4; lineCount++)
        {
            for (var sizeAttempt = 0; sizeAttempt <= (int)Math.Ceiling((desired-floor)/.5f); sizeAttempt++)
            {
                var size=Math.Max(floor,desired-sizeAttempt*.5f);
                using var font=FontManager.CreatePixel(fontFamily,size,fontStyle);
                using var nominalPath=new GraphicsPath();
                nominalPath.AddString(normalized.Replace("\n",""),font.FontFamily,(int)font.Style,font.Size,
                    PointF.Empty,StringFormat.GenericTypographic);
                var nominalInk=nominalPath.GetBounds();
                // FindSafeSpan already checks a 1px raster margin above and below.
                var lineHeight=nominalInk.Height+.25f;
                var lineGap=Math.Max(2f,size*.12f);
                var totalHeight=lineHeight*lineCount+lineGap*(lineCount-1);
                if(targetBottom-totalHeight<topLimit)continue;
                var spans = new RectangleF[lineCount];
                var safe = true;
                for (var lineIndex = 0; lineIndex < lineCount; lineIndex++)
                {
                    var y = targetBottom - totalHeight + lineIndex * (lineHeight + lineGap);
                    // The last line retains the original trailing anchor. An upper
                    // line may inset along a sloping paper edge, never move right.
                    var allowedInset = lineIndex == lineCount - 1 ? 2f :
                        Math.Min(bounds.Width,Math.Max(sourceHeight*2f,(targetBottom-y)*1.5f));
                    spans[lineIndex] = FindSafeSpan(source, sourceAllowance, protectedText, paper.Value,
                        leftLimit, targetRight, y, lineHeight, allowedInset, ref checkedPixels);
                    if (spans[lineIndex].Width < size * 2) { safe = false; break; }
                }
                if (!safe) {attempts.Add(new(size,lineCount,spans,"SPAN_NOT_SAFE"));continue;}
                var commonLeft=spans.Max(x=>x.Left);var commonRight=spans.Min(x=>x.Right);
                if(targetRight-commonRight>Math.Max(3,sourceHeight*.45f) || commonRight-commonLeft<size*2)
                {attempts.Add(new(size,lineCount,spans,"NO_COMMON_ANCHORED_CORRIDOR"));continue;}
                spans=spans.Select(x=>RectangleF.FromLTRB(commonLeft,x.Top,commonRight,x.Bottom)).ToArray();
                var split = FitLines(graphics, font, elements, spans,wordBreaks,protectedCuts);
                if (split is null) {attempts.Add(new(size,lineCount,spans,"TEXT_EXCEEDS_SPANS"));continue;}
                attempts.Add(new(size,lineCount,spans,"FIT"));
                var paragraphWidth=split.Max(value=>VisibleWidth(graphics,font,value));
                var paragraphLeft=spans[0].Right-paragraphWidth;
                var lines = split.Select((value, index) =>
                {
                    var advance = graphics.MeasureString(value, font, PointF.Empty, StringFormat.GenericTypographic).Width;
                    var width = VisibleWidth(graphics, font, value);
                    using var inkPath=new GraphicsPath();
                    inkPath.AddString(value,font.FontFamily,(int)font.Style,font.Size,PointF.Empty,StringFormat.GenericTypographic);
                    var ink=inkPath.GetBounds();
                    // Bounds remains the actual DrawString origin. SafeLineRects
                    // describes visible ink space, not the font's empty ascender area.
                    return new NativeVisualLine(value, new RectangleF(paragraphLeft,
                        spans[index].Top-ink.Top, width, font.GetHeight(graphics))) { AdvanceWidth = advance };
                }).ToArray();
                var union = lines.Select(x => x.Bounds).Aggregate(RectangleF.Union);
                var preserved = string.Concat(lines.Select(x => x.Text)).Replace("\n", "") == normalized.Replace("\n", "");
                if (!preserved) return Decline(true, "CHARACTER_PRESERVATION_FAILED", searchBounds, checkedPixels);
                var candidate=new NativeVisualLayoutPlan(true, true, lineCount == 1 ? "NATIVE_FOOTNOTE_ONE_LINE" : "NATIVE_FOOTNOTE_SAFE_MULTILINE",
                    "IndependentFootnote", bounds, union, searchBounds, desired, size, lineHeight,
                    lines.Max(x => x.Bounds.Width), union.Height, "Left", "BottomRightParagraph", lines,
                    checkedPixels, true, true) { LayoutInput = normalized, SafeLineRects = spans, FitAttempts=attempts,
                        WordBoundaryEngine=wordBreaks.Engine };
                if(preferProtectedTerms && SplitsProtectedTerm(split,normalized,protectedTerms))
                {
                    completeFallback??=candidate;
                    attempts.Add(new(size,lineCount,spans,"FIT_COMPLETE_BUT_PROTECTED_TERM_SPLIT"));
                    continue;
                }
                return candidate;
            }
        }
        return completeFallback is not null?completeFallback with{Reason="COMPLETE_NOTE_RETAINED_NO_WHOLE_TERM_FIT",FitAttempts=attempts}:
            Decline(true, "NO_READABLE_SAFE_FOOTNOTE_FIT", searchBounds, checkedPixels);
    }

    private static bool SplitsProtectedTerm(IReadOnlyList<string> lines,string text,IReadOnlyList<string>? terms)
    {
        var cuts=new HashSet<int>();var cut=0;
        foreach(var line in lines.Take(lines.Count-1)){cut+=line.Length;cuts.Add(cut);}
        foreach(var term in terms??Array.Empty<string>())
        {
            if(term.Length<2||term.Length>16)continue;
            for(var at=text.IndexOf(term,StringComparison.Ordinal);at>=0;at=text.IndexOf(term,at+1,StringComparison.Ordinal))
                if(cuts.Any(c=>c>at&&c<at+term.Length))return true;
        }
        return false;
    }

    private static NativeVisualLayoutPlan? PlanVerticalLabel(Graphics g,VisualBlock block,string text,
        string family,FontStyle weight,float preferred)
    {
        var bounds=block.Bounds;
        if(block.Lines.Count!=1 || bounds.Height<=bounds.Width*1.35f ||
            block.SourceText.Count(char.IsAsciiLetter)<2 ||
            block.SourceText.Count(char.IsAsciiLetter)<block.SourceText.Count(c=>!char.IsWhiteSpace(c))*.8f ||
            text.Any(c=>char.IsLetterOrDigit(c)&&!(c>='\u3400'&&c<='\u9fff')))return null;
        var elements=TextElements(text);
        if(elements.Length==0)return null;
        var desired=Math.Min(preferred,Math.Max(10,bounds.Width*.90f));
        for(var size=desired;size>=9;size-=.5f)
        {
            using var font=FontManager.CreatePixel(family,size,weight);
            var step=font.GetHeight(g);
            if(step*elements.Length>bounds.Height+1)continue;
            var top=bounds.Top+(bounds.Height-step*elements.Length)/2;
            var lines=new List<NativeVisualLine>();var failed=false;
            foreach(var item in elements)
            {
                var width=g.MeasureString(item,font,PointF.Empty,StringFormat.GenericTypographic).Width;
                if(width>bounds.Width+1){failed=true;break;}
                lines.Add(new(item,new RectangleF(bounds.Left+(bounds.Width-width)/2,top,width,step)){AdvanceWidth=width});
                top+=step;
            }
            if(failed)continue;
            return new(true,true,"SOURCE_VERTICAL_LABEL_WITHIN_OWN_COLUMN","VerticalControl",bounds,bounds,bounds,
                desired,size,step,lines.Max(x=>x.Bounds.Width),step*lines.Count,"Center","SourceColumn",
                lines,0,string.Concat(lines.Select(x=>x.Text))==text,true){LayoutInput=text,
                    BreakPolicy="UprightCjkInSourceVerticalColumn",SafeLineRects=lines.Select(x=>x.Bounds).ToArray()};
        }
        return new(true,false,"VERTICAL_CONTROL_NO_READABLE_FIT","VerticalControl",bounds,bounds,bounds,
            desired,desired,0,0,0,"Center","SourceColumn",[],0,true,true){LayoutInput=text};
    }

    private static bool IsField(string source)
    {
        var colon = source.IndexOfAny([':', '：']);
        return colon is > 0 and <= 24 && source[..colon].All(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || c is '/' or '_' or '-') &&
            source[(colon + 1)..].Any(c => !char.IsWhiteSpace(c));
    }

    private static Color? SamplePaper(Bitmap source, Rectangle bounds)
    {
        var samples = new List<Color>();
        var inner = Rectangle.Inflate(bounds, 2, 2);
        var ring = Rectangle.Intersect(Rectangle.Inflate(bounds, 4, 4), new Rectangle(Point.Empty, source.Size));
        for (var y = ring.Top; y < ring.Bottom; y++)
        for (var x = ring.Left; x < ring.Right; x++)
        {
            if (inner.Contains(x, y)) continue;
            var c = source.GetPixel(x, y);
            if (IsNeutralPaper(c)) samples.Add(c);
        }
        // A footnote adjacent to an illustration can have an imperfect exterior
        // ring; actual proposed line rectangles are checked completely below.
        if (samples.Count < Math.Max(12, (ring.Width * ring.Height - inner.Width * inner.Height) / 3)) return null;
        static int Median(IEnumerable<int> values) { var a = values.Order().ToArray(); return a[a.Length / 2]; }
        return Color.FromArgb(Median(samples.Select(x => (int)x.R)), Median(samples.Select(x => (int)x.G)), Median(samples.Select(x => (int)x.B)));
    }

    private static bool IsNeutralPaper(Color value) => Math.Min(value.R, Math.Min(value.G, value.B)) >= 220 &&
        Math.Max(value.R, Math.Max(value.G, value.B)) - Math.Min(value.R, Math.Min(value.G, value.B)) <= 26;

    private static RectangleF FindSafeSpan(Bitmap source, RectangleF sourceAllowance,
        RectangleF[] protectedText, Color paper, float leftLimit, float right, float top, float height,
        float allowedInset, ref int checkedPixels)
    {
        var y0 = (int)Math.Floor(top - 1); var y1 = (int)Math.Ceiling(top + height + 1);
        if (y0 < 0 || y1 > source.Height) return RectangleF.Empty;
        var start = (int)Math.Floor(right); var minimum = (int)Math.Ceiling(leftLimit);
        var runRight = -1; var runLeft = -1;
        var best=RectangleF.Empty;
        void CommitRun()
        {
            if(runRight<0 || right-runRight>allowedInset || runRight-runLeft<5)return;
            var candidate=new RectangleF(runLeft+2,top,Math.Max(0,Math.Min(right,runRight)-runLeft-3),height);
            if(candidate.Width>best.Width)best=candidate;
        }
        for (var x = start; x >= minimum; x--)
        {
            var columnSafe = true;
            for (var y = y0; y < y1; y++)
            {
                checkedPixels++;
                if (protectedText.Any(r => r.Contains(x, y))) { columnSafe = false; break; }
                if (sourceAllowance.Contains(x, y)) continue;
                var color = source.GetPixel(x, y);
                if (!IsNeutralPaper(color) || Math.Abs(color.R - paper.R) + Math.Abs(color.G - paper.G) + Math.Abs(color.B - paper.B) > 48)
                { columnSafe = false; break; }
            }
            if(columnSafe)
            {
                if(runRight<0)runRight=x;
                runLeft=x;
            }
            else if(runRight>=0)
            {
                CommitRun();runRight=-1;runLeft=-1;
            }
        }
        CommitRun();
        // An upper row may use another contiguous paper run on this same trailing
        // margin. No row spans an intervening illustration, weapon or shadow.
        return best;
    }

    private static IReadOnlyList<string>? FitLines(Graphics graphics, Font font, string[] elements, RectangleF[] spans,
        PreferredWordBreaks wordBreaks,IReadOnlySet<int>? protectedCuts=null)
    {
        var offsets=PreferredWordBreaks.ElementOffsets(elements);
        bool Fits(string s, RectangleF span) => !s.Contains('\n') &&
            VisibleWidth(graphics, font, s) <= span.Width;
        if (spans.Length == 1)
        {
            var entire = string.Concat(elements);
            return Fits(entire, spans[0]) ? [entire] : null;
        }
        var memo=new Dictionary<(int Row,int At),IReadOnlyList<string>?>();
        IReadOnlyList<string>? Split(int row,int at)
        {
            if(row==spans.Length)return at==elements.Length?Array.Empty<string>():null;
            if(memo.TryGetValue((row,at),out var known))return known;
            IReadOnlyList<string>? best=null;double bestCost=double.MaxValue;
            for(var end=at+1;end<=elements.Length;end++)
            {
                var value=string.Concat(elements.Skip(at).Take(end-at));
                if(value.EndsWith('\n'))value=value[..^1];
                if(value.Contains('\n') || !Fits(value,spans[row]))break;
                if(string.IsNullOrWhiteSpace(value))continue;
                if(end<elements.Length)
                {
                    if(protectedCuts?.Contains(offsets[end])==true)continue;
                    var next=elements[end];
                    if(next.Length==1 && "，。！？：；、,.!?:;)）】》」』".Contains(next[0]))continue;
                    if(char.IsAsciiLetterOrDigit(value[^1]) && char.IsAsciiLetterOrDigit(next[0]))continue;
                }
                var tail=Split(row+1,end);
                if(tail is null)continue;
                var candidate=new[]{value}.Concat(tail).ToArray();
                var widths=candidate.Select(x=>VisibleWidth(graphics,font,x)).ToArray();
                var cost=widths.Max()-widths.Min();
                // Word/name cohesion is a preference, not permission to drop
                // content or cross an illustration. If no whole-word fit exists,
                // the complete grapheme-safe candidate remains available.
                var cut=at;
                foreach(var part in candidate.Take(candidate.Length-1))
                {
                    cut+=TextElements(part).Length;
                    if(cut<elements.Length && elements[cut]=="\n")cut++;
                    if(!wordBreaks.Offsets.Contains(offsets[cut]))cost+=spans[row].Width;
                }
                if(cost<bestCost){best=candidate;bestCost=cost;}
            }
            return memo[(row,at)]=best;
        }
        return Split(0,0);
    }

    private static float VisibleWidth(Graphics graphics, Font font, string text)
    {
        var advance = graphics.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width;
        if (text.Length == 0 || !char.IsPunctuation(text[^1])) return advance;
        // A terminal punctuation glyph can have substantial empty advance after
        // its ink (notably a full-em CJK stop). Keep the entire glyph and a 1px
        // antialias margin, but do not reserve invisible trailing space. Drawing
        // still receives the original string and the same point origin.
        using var path = new GraphicsPath();
        path.AddString(text, font.FontFamily, (int)font.Style, font.Size, PointF.Empty, StringFormat.GenericTypographic);
        return path.PointCount == 0 ? advance : Math.Min(advance, path.GetBounds().Right + 1f);
    }

    private static string[] TextElements(string text)
    {
        var result = new List<string>(); var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) result.Add(enumerator.GetTextElement());
        return result.ToArray();
    }
}
