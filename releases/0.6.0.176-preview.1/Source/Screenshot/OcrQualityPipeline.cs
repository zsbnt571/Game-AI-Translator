using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Drawing.Imaging;

namespace ScreenshotTranslationUiTester;

public static partial class OcrQualityPipeline
{
    internal const string EvidenceVersion="ocr-quality-source-consensus-v3-native-confirmation";
    private static readonly HashSet<string> GameUiLexicon = new(StringComparer.OrdinalIgnoreCase)
    {
        "SAVE", "LOAD", "OPTIONS", "BACK", "EXIT", "PLAY", "AUTO", "SKIP", "LOG",
        "PREFS", "TITLE", "RETURN", "FULLSCREEN", "DISPLAY", "LANGUAGE", "MUSIC",
        "SOUND", "MUTE", "STATUS", "LOVE", "OPTION", "ONLINE", "OFFLINE"
    };

    public static OcrEngineResult Apply(OcrEngineResult result)
    {
        var watch = Stopwatch.StartNew();
        foreach (var block in result.Blocks)
        {
            block.NormalizedText = MinimalOcrNormalizer.Normalize(block.RawText);
            block.Script = ClassifyScript(block.RawText);
            block.SemanticType = ClassifySemantic(block.NormalizedText, block.Script);
            block.LowConfidence = block.Confidence is < .55f;
            block.SuspectedFalsePositive = IsFalsePositiveSuspect(block);
            block.PreprocessRetrySuggested = IsRetryCandidate(block);
            block.Corrections = Correct(block.NormalizedText, block.Script, block.SemanticType, result.EngineActual);
            block.CorrectedText = block.Corrections.Count == 0
                ? block.NormalizedText
                : block.Corrections[^1].Corrected;
            if (result.EngineActual == OcrEngineKind.Windows)
            {
                result.WindowsLegacyRuleInvocationCount++;
                ApplyWindowsLegacy(block);
            }
            (block.UnresolvedAmbiguity, block.AmbiguityCandidates) = FindUnresolvedAmbiguity(
                block.NormalizedText, block.SemanticType, block.Corrections.Count > 0);
        }
        if(result.EngineActual!=OcrEngineKind.Windows)
            foreach(var block in result.Blocks.Where(b=>IsParagraphConfidenceOutlier(b,result.Blocks)))
                block.PreprocessRetrySuggested=true;
        result.NormalizedText = string.Join(Environment.NewLine, result.Blocks.Select(x => x.NormalizedText));
        result.CorrectedText = string.Join(Environment.NewLine, result.Blocks.Select(x => x.CorrectedText));
        result.LowConfidenceBlockCount = result.Blocks.Count(x => x.LowConfidence);
        result.CorrectedBlockCount = result.Blocks.Count(x => x.Corrections.Count > 0);
        result.CorrectionCount = result.Blocks.Sum(x => x.Corrections.Count);
        result.FalsePositiveSuspectCount = result.Blocks.Count(x => x.SuspectedFalsePositive);
        result.AmbiguousBlockCount = result.Blocks.Count(x => x.UnresolvedAmbiguity);
        result.ConfidenceAvailableBlockCount = result.Blocks.Count(x => x.Confidence.HasValue);
        result.ConfidenceUnavailableBlockCount = result.Blocks.Count(x => !x.Confidence.HasValue);
        result.CharacterTopKAvailable = false;
        result.QualityPipelineApplied = true;
        watch.Stop();
        result.CorrectionMilliseconds = watch.ElapsedMilliseconds;
        return result;
    }

    public static bool ApplyRetryCandidate(OcrEngineResult result, OcrEngineBlock block,
        string candidateText, float? candidateConfidence, bool neutralParagraphRetry=false)
    {
        block.PreprocessCandidateText = candidateText;
        block.PreprocessCandidateConfidence = candidateConfidence;
        result.RetryOriginalRawText = block.RawText;
        result.RetryRawText = candidateText;
        result.RetryReason = neutralParagraphRetry ? "ParagraphConfidenceOutlier" : block.Confidence.HasValue ? "LowConfidence" : "HeuristicRetry";
        result.RetryPreprocessType = neutralParagraphRetry ? "0.4 line-height padding + 2x nearest-neighbor, original colors" : "10% ROI padding + 2x upscale + grayscale/contrast";
        var oldConfidence = block.Confidence ?? 0;
        var newConfidence = candidateConfidence ?? 0;
        var heuristicImprovement = !candidateConfidence.HasValue &&
            candidateText.Trim().Length >= block.RawText.Trim().Length + 2;
        if (string.IsNullOrWhiteSpace(candidateText) ||
            (candidateConfidence.HasValue && newConfidence < oldConfidence + .12f) ||
            (!candidateConfidence.HasValue && !heuristicImprovement))
        {
            result.RetryChosenText = block.RawText;
            return false;
        }
        var normalized = MinimalOcrNormalizer.Normalize(candidateText);
        block.Corrections.Add(new(block.RawText, normalized,
            "Suspicious full-line ROI retry; higher OCR confidence; "+result.RetryPreprocessType, "High",
            "CommonSafe.RoiRetry", result.EngineActual));
        block.CorrectedText = normalized;
        result.RetryChosenText = normalized;
        result.CorrectedText = string.Join(Environment.NewLine, result.Blocks.Select(x => x.CorrectedText));
        result.CorrectedBlockCount = result.Blocks.Count(x => x.Corrections.Count > 0);
        result.CorrectionCount = result.Blocks.Sum(x => x.Corrections.Count);
        return true;
    }

    public static Bitmap CreateRetryRoi(Bitmap source, RectangleF bounds)
    {
        var paddingX = Math.Max(2, (int)Math.Ceiling(bounds.Width * .10f));
        var paddingY = Math.Max(2, (int)Math.Ceiling(bounds.Height * .10f));
        var crop = Rectangle.Intersect(new Rectangle(0, 0, source.Width, source.Height),
            Rectangle.Round(RectangleF.Inflate(bounds, paddingX, paddingY)));
        if (crop.Width <= 0 || crop.Height <= 0) throw new ArgumentException("Invalid OCR retry ROI.");
        var output = new Bitmap(crop.Width * 2, crop.Height * 2, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(output);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix(new[]
        {
            new[] { .45f, .45f, .45f, 0, 0 },
            new[] { .45f, .45f, .45f, 0, 0 },
            new[] { .45f, .45f, .45f, 0, 0 },
            new[] { 0f, 0f, 0f, 1f, 0f },
            new[] { -.18f, -.18f, -.18f, 0f, 1f }
        }));
        graphics.DrawImage(source, new Rectangle(0, 0, output.Width, output.Height),
            crop.X, crop.Y, crop.Width, crop.Height, GraphicsUnit.Pixel, attributes);
        return output;
    }

    internal static bool IsParagraphConfidenceOutlier(OcrEngineBlock block,IReadOnlyList<OcrEngineBlock> blocks)
    {
        var b=block.BoundingBox;
        var fragmented=IsFragmentedLatinLine(block.RawText);
        if(block.Confidence is not {} confidence || confidence>=.9f || confidence<.6f ||
            b.Height<8 || b.Width<b.Height*9 || (!fragmented&&block.RawText.Count(char.IsLetter)<24))return false;
        var peers=blocks.Where(p=>p!=block && p.Confidence.HasValue &&
            (fragmented ? p.RawText.Count(char.IsLetter)>=10&&p.RawText.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length>=3
                : p.RawText.Count(char.IsLetter)>=24) && !IsFragmentedLatinLine(p.RawText) &&
            p.BoundingBox.Height>=b.Height*.7f && p.BoundingBox.Height<=b.Height*1.4f &&
            Math.Abs(p.BoundingBox.Left-b.Left)<=b.Height*.7f &&
            Math.Abs(p.BoundingBox.Top-b.Top)<=b.Height*7 &&
            p.BoundingBox.Width>=b.Width*.6f && p.BoundingBox.Width<=b.Width*1.6f)
            .Select(p=>p.Confidence!.Value).Order().ToArray();
        return peers.Length>=3 && peers[peers.Length/2]>=.94f && peers[peers.Length/2]-confidence>=.12f;
    }

    internal static bool IsFragmentedLatinLine(string text)
    {
        if(text.Any(c=>char.IsDigit(c)||c is '@' or '/' or '\\' or '_' or '=' or '{' or '}') ||
            !Regex.IsMatch(text,@"^[A-Za-z\s,'!?;:.-]+$"))return false;
        var tokens=text.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
        // Wide, low-confidence paragraph rows can lose most characters. Do not
        // require the damaged OCR string itself to still contain a long sentence.
        return tokens.Length>=4 && tokens.Count(t=>t.Length==1&&t[0] is >= 'b' and <= 'z'&&t[0]!='i')>=2;
    }

    internal static Bitmap CreateNeutralRetryRoi(Bitmap source,RectangleF bounds,int scale=2)
    {
        var pad=Math.Max(3,bounds.Height*.4f);
        var crop=Rectangle.Intersect(new Rectangle(Point.Empty,source.Size),Rectangle.Round(RectangleF.Inflate(bounds,pad,pad)));
        if(crop.Width<=0 || crop.Height<=0)throw new ArgumentException("Invalid OCR retry ROI.");
        var output=new Bitmap(crop.Width*scale,crop.Height*scale,PixelFormat.Format24bppRgb);
        using var graphics=Graphics.FromImage(output);
        graphics.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode=System.Drawing.Drawing2D.PixelOffsetMode.Half;
        graphics.DrawImage(source,new Rectangle(0,0,output.Width,output.Height),crop,GraphicsUnit.Pixel);
        return output;
    }

    internal static bool CoversRetryLine(OcrEngineBlock candidate,RectangleF original)
        =>candidate.BoundingBox.Width>=original.Width*2*.8f &&
          candidate.BoundingBox.Height>=original.Height*2*.5f &&
          candidate.BoundingBox.Height<=original.Height*2*1.8f;

    internal static bool CoversNeutralRetryLine(OcrEngineBlock candidate,RectangleF original,Size roi,int scale)
        =>candidate.BoundingBox.Width>=original.Width*scale*.8f &&
          candidate.BoundingBox.Height>=original.Height*scale*.5f &&
          candidate.BoundingBox.Height<=original.Height*scale*1.8f &&
          Math.Abs(candidate.BoundingBox.Top+candidate.BoundingBox.Height*.5f-roi.Height*.5f)<=original.Height*scale*.35f;

    internal static OcrEngineBlock? ComposeNeutralRetryLine(IReadOnlyList<OcrEngineBlock> blocks,
        RectangleF original,Size roi,int scale,float minimumConfidence)
    {
        var row=blocks.Where(b=>b.Confidence>=minimumConfidence&&
            b.BoundingBox.Height>=original.Height*scale*.5f&&b.BoundingBox.Height<=original.Height*scale*1.8f&&
            Math.Abs(b.BoundingBox.Top+b.BoundingBox.Height*.5f-roi.Height*.5f)<=original.Height*scale*.35f)
            .OrderBy(b=>b.BoundingBox.Left).ToArray();
        if(row.Length==0||row.Length>32)return null;
        for(var i=1;i<row.Length;i++)
        {
            var a=row[i-1].BoundingBox;var b=row[i].BoundingBox;
            if(b.Left<a.Right-original.Height*scale*.15f||b.Left-a.Right>original.Height*scale*.8f||
                Math.Min(a.Bottom,b.Bottom)-Math.Max(a.Top,b.Top)<Math.Min(a.Height,b.Height)*.5f)return null;
        }
        var bounds=row.Select(b=>b.BoundingBox).Aggregate(RectangleF.Union);
        var composed=new OcrEngineBlock{RawText=string.Join(" ",row.Select(b=>b.RawText.Trim())),
            Confidence=row.Min(b=>b.Confidence),BoundingBox=bounds};
        return CoversNeutralRetryLine(composed,original,roi,scale)?composed:null;
    }

    public static OcrScriptKind ClassifyScript(string text)
    {
        var latin = 0; var cjk = 0; var japanese = 0; var digits = 0; var symbols = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var v = rune.Value;
            if (v is >= '0' and <= '9') digits++;
            else if ((v is >= 'A' and <= 'Z') || (v is >= 'a' and <= 'z')) latin++;
            else if (v is >= 0x3040 and <= 0x30ff) japanese++;
            else if (v is >= 0x3400 and <= 0x9fff) cjk++;
            else if (!Rune.IsWhiteSpace(rune)) symbols++;
        }
        if (japanese > 0 && latin == 0 && digits == 0) return OcrScriptKind.Japanese;
        var nonzero = new[] { latin, cjk, japanese, digits }.Count(x => x > 0);
        if (nonzero > 1 || ((latin + cjk + japanese + digits) > 0 && symbols > 0)) return OcrScriptKind.Mixed;
        if (japanese > 0) return OcrScriptKind.Japanese;
        if (cjk > 0) return OcrScriptKind.Cjk;
        if (latin > 0) return OcrScriptKind.Latin;
        if (digits > 0) return OcrScriptKind.Digits;
        if (symbols > 0) return OcrScriptKind.Symbols;
        return OcrScriptKind.Unknown;
    }

    public static OcrSemanticType ClassifySemantic(string text, OcrScriptKind script)
    {
        var t = text.Trim();
        if (TimePattern().IsMatch(t)) return OcrSemanticType.Time;
        if (FractionPattern().IsMatch(t)) return OcrSemanticType.Fraction;
        if (PercentagePattern().IsMatch(t)) return OcrSemanticType.Percentage;
        if (UnitPattern().IsMatch(t)) return OcrSemanticType.UnitValue;
        if (CurrencyPattern().IsMatch(t)) return OcrSemanticType.Currency;
        if (NumericPattern().IsMatch(t)) return OcrSemanticType.Numeric;
        if (GameUiLexicon.Contains(t) || ButtonWithNumberPattern().IsMatch(t)) return OcrSemanticType.Button;
        if (NamePattern().IsMatch(t) && char.IsUpper(t[0])) return OcrSemanticType.PersonNameCandidate;
        if (script is OcrScriptKind.Cjk or OcrScriptKind.Japanese) return OcrSemanticType.NaturalText;
        if (script == OcrScriptKind.Mixed) return OcrSemanticType.Mixed;
        return t.Length > 1 ? OcrSemanticType.NaturalText : OcrSemanticType.Unknown;
    }

    private static List<OcrCorrection> Correct(string text, OcrScriptKind script, OcrSemanticType type,
        OcrEngineKind engine)
    {
        var audit = new List<OcrCorrection>();
        if (string.IsNullOrWhiteSpace(text) || script is OcrScriptKind.Cjk or OcrScriptKind.Japanese) return audit;

        var corrected = text;
        if (type is OcrSemanticType.Fraction or OcrSemanticType.Percentage or OcrSemanticType.Currency or OcrSemanticType.UnitValue or OcrSemanticType.Time)
        {
            corrected = CorrectNumericSlots(corrected);
            AddIfChanged(audit, text, corrected, $"{type} + numeric pattern", "High",
                "CommonSafe.NumericStructure", engine);
            return audit;
        }

        var button = ButtonNumericTailPattern().Match(corrected);
        if (button.Success)
        {
            var tail = CorrectNumericSlots(button.Groups[2].Value);
            var candidate = button.Groups[1].Value + tail;
            AddIfChanged(audit, text, candidate, "Button label + numeric suffix", "High",
                "CommonSafe.ButtonNumericTail", engine);
            return audit;
        }

        if (SingleLatinTokenPattern().IsMatch(corrected) && !LooksLikeProtectedIdentifier(corrected))
        {
            var candidate = BestLexiconCandidate(corrected);
            if (candidate is not null)
                AddIfChanged(audit, text, candidate,
                    "single ambiguous character + high-confidence lexical candidate", "High",
                    "CommonSafe.Lexical.AmbiguousGlyph", engine);
            else if (corrected.Length >= 4 && corrected[0] == '0' && corrected.Skip(1).All(char.IsLetter))
            {
                var name = 'O' + corrected[1..];
                if (NamePattern().IsMatch(name)) AddIfChanged(audit, text, name,
                    "Person-name candidate + leading O/0 ambiguity", "Medium",
                    "CommonSafe.PersonName.LeadingO0", engine);
            }
        }
        return audit;
    }

    private static string CorrectNumericSlots(string value)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var nearDigit = (i > 0 && char.IsDigit(chars[i - 1])) || (i + 1 < chars.Length && char.IsDigit(chars[i + 1]));
            if (!nearDigit && chars.Count(char.IsDigit) == 0) continue;
            chars[i] = chars[i] switch { 'O' or 'o' => '0', 'I' or 'l' => '1', 'S' => '5', 'B' => '8', 'Z' => '2', _ => chars[i] };
        }
        return new string(chars);
    }

    private static string? BestLexiconCandidate(string token)
    {
        foreach (var word in GameUiLexicon)
        {
            if (word.Length != token.Length) continue;
            var differences = 0; var valid = true;
            for (var i = 0; i < word.Length; i++)
            {
                if (char.ToUpperInvariant(token[i]) == word[i]) continue;
                if (!IsAmbiguousPair(token[i], word[i])) { valid = false; break; }
                differences++;
            }
            if (valid && differences == 1) return word;
        }
        return null;
    }

    private static bool IsAmbiguousPair(char actual, char expected) =>
        (actual, expected) is ('0', 'O') or ('O', '0') or ('1', 'I') or ('l', 'I') or
            ('I', '1') or ('5', 'S') or ('S', '5') or ('8', 'B') or ('B', '8') or
            ('2', 'Z') or ('Z', '2');

    private static bool LooksLikeProtectedIdentifier(string token) =>
        token.Contains('-') || token.Contains('_') || token.Contains('.') || token.Contains('/') ||
        Regex.IsMatch(token, @"^(?:Player|User|Room|OCR|Version|ID)\d", RegexOptions.IgnoreCase) ||
        token.Count(char.IsDigit) >= 2;

    private static void ApplyWindowsLegacy(OcrEngineBlock block)
    {
        var value = Regex.Replace(block.CorrectedText, @"^(?:○|◯)\s+(?=[A-Z][a-z])", "");
        if (value == block.CorrectedText) return;
        block.Corrections.Add(new(block.CorrectedText, value,
            "Windows-only leading symbol cleanup", "Medium", "WindowsLegacy.SymbolCleanup",
            OcrEngineKind.Windows));
        block.CorrectedText = value;
    }

    private static bool IsFalsePositiveSuspect(OcrEngineBlock block) =>
        block.RawText.Trim().Length == 1 && block.Confidence is < .55f &&
        block.BoundingBox.Width < Math.Max(28, block.BoundingBox.Height * 1.8f);

    private static bool IsRetryCandidate(OcrEngineBlock block) =>
        block.BoundingBox.Width >= 12 && block.BoundingBox.Height >= 8 &&
        (block.Confidence is < .75f || (!block.Confidence.HasValue && IsHeuristicRetryCandidate(block)));

    private static bool IsHeuristicRetryCandidate(OcrEngineBlock block)
    {
        var text = block.RawText.Trim();
        if (text.Length == 0) return true;
        if (text.Length == 1 && !char.IsLetterOrDigit(text[0])) return true;
        return block.BoundingBox.Width / Math.Max(1, text.Length) > Math.Max(32, block.BoundingBox.Height * 2.2f);
    }

    private static (bool Ambiguous, string Candidates) FindUnresolvedAmbiguity(
        string text, OcrSemanticType semanticType, bool corrected)
    {
        if (corrected || semanticType is OcrSemanticType.Fraction or OcrSemanticType.Percentage or
            OcrSemanticType.Currency or OcrSemanticType.Time or OcrSemanticType.UnitValue) return (false, "");
        var compact = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (compact.Length == 0) return (false, "");
        const string ambiguitySet = "O0Iil1S5B8Z2G6q9";
        if (compact.All(c => ambiguitySet.Contains(c)))
            return (true, "O/0, I/l/1, S/5, B/8, Z/2, G/6, q/9");
        return (false, "");
    }

    private static void AddIfChanged(List<OcrCorrection> audit, string original, string corrected,
        string reason, string confidence, string ruleId, OcrEngineKind engine)
    {
        if (!string.Equals(original, corrected, StringComparison.Ordinal))
            audit.Add(new(original, corrected, reason, confidence, ruleId, engine));
    }

    [GeneratedRegex(@"^[0-9OolISBZ]{1,3}:[0-9OolISBZ]{2}$", RegexOptions.CultureInvariant)] private static partial Regex TimePattern();
    [GeneratedRegex(@"^[0-9OolISBZ]+/[0-9OolISBZ]+$", RegexOptions.CultureInvariant)] private static partial Regex FractionPattern();
    [GeneratedRegex(@"^[0-9OolISBZ]+%$", RegexOptions.CultureInvariant)] private static partial Regex PercentagePattern();
    [GeneratedRegex(@"^(?:[$£€¥]|[0-9OolISBZ]+(?:yen|usd|eur))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex CurrencyPattern();
    [GeneratedRegex(@"^[0-9OolISBZ]+(?:yen|min|kg|cm|mm|m|hp|mp)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex UnitPattern();
    [GeneratedRegex(@"^[0-9]+$", RegexOptions.CultureInvariant)] private static partial Regex NumericPattern();
    [GeneratedRegex(@"^[A-Za-z]+\s+[0-9OolISBZ]+$", RegexOptions.CultureInvariant)] private static partial Regex ButtonWithNumberPattern();
    [GeneratedRegex(@"^([A-Za-z]+\s+)([0-9OolISBZ]+)$", RegexOptions.CultureInvariant)] private static partial Regex ButtonNumericTailPattern();
    [GeneratedRegex(@"^[A-Z][a-z]{2,}$", RegexOptions.CultureInvariant)] private static partial Regex NamePattern();
    [GeneratedRegex(@"^[A-Za-z0-9]+$", RegexOptions.CultureInvariant)] private static partial Regex SingleLatinTokenPattern();
}
