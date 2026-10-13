namespace ScreenshotTranslationUiTester.CorePipelineV2;

/// <summary>Source baseline evidence for drawing only, never grouping or cleanup.</summary>
internal sealed record ParagraphAlignmentEvidence(string Alignment, string Reason,
    int Rows, float ReferenceTop, float LeftAtReference, float CenterAtReference,
    float Slope, float LeftResidual, float CenterResidual, float Tolerance)
{
    internal float LeftAt(float top) => LeftAtReference + Slope * (top - ReferenceTop);
    internal float CenterAt(float top) => CenterAtReference + Slope * (top - ReferenceTop);

    internal static bool OwnsProseAlignment(VisualBlock block)
    {
        if(block.SourceRole?.Role=="Prose")return true;
        if(ObserveShortSentence(block) is not null)return true;
        if(block.RoleHint is "PossibleControl" or "PossibleTitle" ||
            CorePipelineCorpusRunner.PreserveIndependentFieldBreaks(block)) return false;
        if(block.LayoutBehavior==BlockLayoutBehavior.Flow && block.Lines.Count>=3)
            return block.Lines.Count(l=>l.SourceText.Count(char.IsLetter)>=3)>=3;
        // A long sentence must not inherit a neighboring short label's center
        // merely because both use the same font and color. With one source row,
        // preserve its own observed anchor instead of inventing a container axis.
        return block.Lines.Count==1 && block.SourceText.Length>=60 &&
            block.SourceText.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).Length>=8;
    }

    internal static ParagraphAlignmentEvidence? Observe(VisualBlock block, string? existingAlignment = null)
    {
        if(block.SourceRole is {Role:"Prose"} role)
            return new(role.Alignment,role.Reason,block.Lines.Count,block.Bounds.Top,block.Bounds.Left,
                block.Bounds.Left+block.Bounds.Width/2,0,0,0,Math.Max(2,block.Lines.Min(l=>l.Bounds.Height)*.22f));
        if(ObserveShortSentence(block) is {} sentence)return sentence;
        if (block.Lines.Count < 3 || block.LayoutBehavior != BlockLayoutBehavior.Flow ||
            block.RoleHint is "PossibleControl" or "PossibleTitle" ||
            CorePipelineCorpusRunner.PreserveIndependentFieldBreaks(block)) return null;
        var rows = block.Lines.Where(l => !string.IsNullOrWhiteSpace(l.SourceText))
            .OrderBy(l => l.Bounds.Top).ToArray();
        if (rows.Length < 3 || rows.Count(l => l.SourceText.Count(char.IsLetter) >= 3) < 3) return null;
        var y0 = rows[0].Bounds.Top;
        var height = Median(rows.Select(l => l.Bounds.Height));
        var tolerance = Math.Max(2, height * .12f);
        var left = Fit(rows.Select(l => (l.Bounds.Top - y0, l.Bounds.Left)).ToArray(), height);
        var center = Fit(rows.Select(l => (l.Bounds.Top - y0, l.Bounds.Left + l.Bounds.Width / 2)).ToArray(), height);
        // Require materially stronger evidence before overriding the old hint.
        // A ragged final row or a first-row indent is not paragraph centering.
        var leftWins = left.Residual <= tolerance &&
            center.Residual > Math.Max(tolerance * 2, left.Residual * 2 + tolerance);
        var centerWins = center.Residual <= tolerance &&
            left.Residual > Math.Max(tolerance * 2, center.Residual * 2 + tolerance);
        if (!leftWins && !centerWins) return null;
        var winner = leftWins ? left : center;
        // A perspective-mapped card can have coherent oblique left edges while
        // its paragraph is intentionally centered on the card. Do not replace
        // that established presentation using only an OCR shear fit.
        if(leftWins && Math.Abs(winner.Slope) >= .08f && existingAlignment=="Center") return null;
        return new(leftWins ? "Left" : "Center", leftWins ?
            "SOURCE_LEFT_BASELINES_MORE_STABLE_THAN_CENTERS" : "SOURCE_CENTERS_MORE_STABLE_THAN_LEFT_BASELINES",
            rows.Length, y0, left.Intercept, center.Intercept, winner.Slope,
            left.Residual, center.Residual, tolerance);
    }

    private static (float Intercept, float Slope, float Residual) Fit((float Y, float X)[] rows, float height)
    {
        var slopes = new List<float>();
        for (var i = 0; i < rows.Length; i++)
        for (var j = i + 1; j < rows.Length; j++)
            if (rows[j].Y - rows[i].Y > height * .7f)
                slopes.Add((rows[j].X - rows[i].X) / (rows[j].Y - rows[i].Y));
        var slope = slopes.Count == 0 ? 0 : Median(slopes);
        // Ignore sub-pixel OCR drift; allow coherent source-card perspective.
        if (Math.Abs(slope) < .04f) slope = 0;
        if (Math.Abs(slope) > .6f) return (0, slope, float.MaxValue);
        var intercept = Median(rows.Select(r => r.X - slope * r.Y));
        var residuals = rows.Select(r => Math.Abs(r.X - intercept - slope * r.Y)).Order().ToArray();
        return (intercept, slope, residuals[Math.Min(rows.Length - 1, (int)Math.Floor((rows.Length - 1) * .8f))]);
    }

    private static float Median(IEnumerable<float> values)
    { var a = values.Order().ToArray(); return (a[(a.Length - 1) / 2] + a[a.Length / 2]) / 2; }

    internal static ParagraphAlignmentEvidence? ObserveShortSentence(VisualBlock block)
    {
        if(block.Lines.Count is <2 or >3 || block.RoleHint=="PossibleControl" ||
            CorePipelineCorpusRunner.PreserveIndependentFieldBreaks(block))return null;
        var rows=block.Lines.OrderBy(l=>l.Bounds.Top).ToArray();
        var text=block.SourceText.Trim();
        if(!System.Text.RegularExpressions.Regex.IsMatch(text,@"[.!?][\""'”’]?$"))return null;
        var letters=text.Where(char.IsLetter).ToArray();
        if(letters.Length<20 || letters.Count(char.IsLower)<letters.Length*.65f ||
            rows[0].SourceText.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).Length<3 ||
            rows.Any(l=>l.SourceText.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).Length<2))return null;
        var height=Median(rows.Select(l=>l.Bounds.Height));
        var tolerance=Math.Max(2,height*.16f);
        var left=rows.Max(l=>l.Bounds.Left)-rows.Min(l=>l.Bounds.Left);
        var centers=rows.Select(l=>l.Bounds.Left+l.Bounds.Width/2).ToArray();
        var center=centers.Max()-centers.Min();
        var gaps=rows.Zip(rows.Skip(1),(a,b)=>b.Bounds.Top-a.Bounds.Top).ToArray();
        if(rows.Max(l=>l.Bounds.Height)>rows.Min(l=>l.Bounds.Height)*1.35f ||
            gaps.Any(gap=>gap<height*.7f||gap>height*1.8f) ||
            left>tolerance || center<=tolerance*2)return null;
        return new("Left","REGULAR_SOURCE_LEFT_ROWS_FORM_COMPLETE_SENTENCE",rows.Length,rows[0].Bounds.Top,
            rows[0].Bounds.Left,rows[0].Bounds.Left+rows[0].Bounds.Width/2,0,left,center,tolerance);
    }
}
