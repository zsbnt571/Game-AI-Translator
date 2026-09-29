using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace ScreenshotTranslationUiTester;

public sealed class OcrService
{
    private sealed record Candidate(string Name, Bitmap Image, float Scale);
    private sealed record CandidateResult(Candidate Candidate, string Language, OcrDocument Document, double Score);

    public async Task<IReadOnlyList<string>> GetAvailableLanguagesAsync()
    {
        var output = await RunHelperAsync(["--languages"]);
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<OcrDocument> RecognizeAsync(
        Bitmap original, string mode, CancellationToken cancellationToken = default,
        CleanupStrength cleanupStrength = CleanupStrength.Normal)
    {
        var total = Stopwatch.StartNew();
        var metrics = new TimingMetrics();
        var preprocess = Stopwatch.StartNew();
        var candidates = BuildCandidates(original);
        preprocess.Stop();
        metrics.PreprocessMs = preprocess.ElapsedMilliseconds;

        try
        {
            var available = await GetAvailableLanguagesAsync();
            var languages = ResolveLanguages(mode, available);
            if (languages.Count == 0) throw new InvalidOperationException("没有可用的 Windows OCR 语言包。");

            var ocrWatch = Stopwatch.StartNew();
            var results = new List<CandidateResult>();
            // All visual preprocessing/scale candidates are evaluated with the primary
            // recognizer. Other enabled languages supplement the two strongest images.
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var doc = await RecognizeCandidateAsync(candidate, languages[0], cancellationToken);
                    results.Add(new(candidate, languages[0], doc, Score(doc)));
                }
                catch (OperationCanceledException) { throw; }
                catch { /* Unsupported/oversized candidates must not abort the entire OCR run. */ }
            }
            if (results.Count == 0) throw new InvalidOperationException("所有OCR候选均识别失败。");
            var bestPrimary = results.OrderByDescending(x => x.Score).First();
            foreach (var language in languages.Skip(1))
            {
                foreach (var candidate in new[] { candidates[0], bestPrimary.Candidate }
                             .DistinctBy(x => x.Name))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var doc = await RecognizeCandidateAsync(candidate, language, cancellationToken);
                        results.Add(new(candidate, language, doc, Score(doc)));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }
            }
            ocrWatch.Stop();
            metrics.OcrMs = ocrWatch.ElapsedMilliseconds;

            var mergeWatch = Stopwatch.StartNew();
            var best = results.OrderByDescending(x => x.Score).First();
            var useful = results.GroupBy(x => x.Language, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(x => x.Score).First())
                .OrderByDescending(x => x.Score).ToList();
            var lines = MergeLanguageResults(useful, original.Size);
            for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                lines[lineIndex].Id = $"RAW{lineIndex + 1:000}";
                lines[lineIndex].ReadingOrder = lineIndex + 1;
            }
            var diagnostics = results.Select(result => BuildDiagnostic(
                result, result.Candidate.Name == best.Candidate.Name &&
                        result.Language.Equals(best.Language, StringComparison.OrdinalIgnoreCase))).ToList();
            var snapshot = CreateSnapshot(lines, mode, diagnostics);
            var paragraphs = BuildOrganizedRegions(lines, original.Size, cleanupStrength);
            var groups = paragraphs.Select(ToSegmentGroup).ToList();
            mergeWatch.Stop();
            metrics.MergeMs = mergeWatch.ElapsedMilliseconds;
            total.Stop();
            metrics.TotalMs = total.ElapsedMilliseconds;

            return new OcrDocument
            {
                Text = string.Join(Environment.NewLine + Environment.NewLine, paragraphs.Select(x => x.Text)),
                RawText = string.Join(Environment.NewLine, lines.Select(x => x.Text)),
                Regions = paragraphs,
                RawLines = lines.Select(CloneRegion).ToList(),
                OriginalSnapshot = snapshot,
                Groups = groups,
                SourceWidth = original.Width,
                SourceHeight = original.Height,
                SelectedCandidate = $"{best.Candidate.Name} / {best.Language}",
                DebugImage = new Bitmap(best.Candidate.Image),
                CandidateDiagnostics = diagnostics,
                Metrics = metrics
            };
        }
        finally
        {
            foreach (var candidate in candidates) candidate.Image.Dispose();
        }
    }

    private static List<string> ResolveLanguages(string mode, IReadOnlyList<string> available)
    {
        string? Find(string prefix) => available.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var en = Find("en");
        var zh = Find("zh-Hans") ?? Find("zh-CN") ?? Find("zh");
        var ja = Find("ja");
        var ko = Find("ko");
        IEnumerable<string?> selected = mode switch
        {
            "英语" => [en, zh, ja, ko],
            "简体中文" => [zh, en, ja, ko],
            "日语" => [ja, zh, en, ko],
            "韩语" => [ko, zh, en, ja],
            _ => [zh, en, ja, ko]
        };
        return selected.Where(x => x is not null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<OcrDocument> RecognizeCandidateAsync(
        Candidate candidate, string language, CancellationToken cancellationToken)
    {
        var path = Path.Combine(AppDataPaths.TempRoot, $"screenshot-ocr-{Guid.NewGuid():N}.png");
        candidate.Image.Save(path, ImageFormat.Png);
        try
        {
            var output = await RunHelperAsync([path, language], cancellationToken);
            var result = JsonSerializer.Deserialize<OcrJsonDocument>(output)
                ?? throw new InvalidOperationException("Windows OCR 返回了空结果。");
            return new OcrDocument
            {
                Text = result.Text,
                SourceWidth = result.Width,
                SourceHeight = result.Height,
                Regions = result.Regions.Select(region => new OcrRegion
                {
                    Text = NormalizeText(region.Text),
                    Bounds = new RectangleF(region.X / candidate.Scale, region.Y / candidate.Scale,
                        region.Width / candidate.Scale, region.Height / candidate.Scale),
                    Language = language
                }).Where(x => !string.IsNullOrWhiteSpace(x.Text)).ToList()
            };
        }
        finally { try { File.Delete(path); } catch { } }
    }

    private static List<Candidate> BuildCandidates(Bitmap original)
    {
        float Scale(float requested) => Math.Min(requested, 4090f / Math.Max(original.Width, original.Height));
        Candidate Color(float requested)
        {
            var scale = Math.Max(1, Scale(requested));
            return new($"原始彩色 {requested:0}×（实际{scale:0.00}×）", Resize(original, scale), scale);
        }
        Candidate Process(float requested, PixelMode mode, string name)
        {
            var scale = Math.Max(1, Scale(requested));
            return new($"{name} {requested:0}×（实际{scale:0.00}×）", Transform(original, scale, mode), scale);
        }
        return
        [
            new("原始彩色 1×", new Bitmap(original), 1),
            Color(2), Color(3), Color(4),
            Process(2, PixelMode.Gray, "灰度增强"),
            Process(2, PixelMode.Contrast, "高对比度"),
            Process(2, PixelMode.Sharpen, "锐化"),
            Process(2, PixelMode.Binary, "自适应二值化")
        ];
    }

    private enum PixelMode { Gray, Contrast, Sharpen, Binary }

    private static Bitmap Resize(Bitmap source, float scale)
    {
        var result = new Bitmap(Math.Max(1, (int)Math.Round(source.Width * scale)),
            Math.Max(1, (int)Math.Round(source.Height * scale)), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.DrawImage(source, new Rectangle(0, 0, result.Width, result.Height));
        return result;
    }

    private static Bitmap Transform(Bitmap source, float scale, PixelMode mode)
    {
        using var enlarged = Resize(source, scale);
        var output = new Bitmap(enlarged.Width, enlarged.Height, PixelFormat.Format32bppArgb);
        var contrast = mode switch { PixelMode.Contrast => 1.75f, PixelMode.Sharpen => 1.45f, _ => 1f };
        var offset = .5f * (1f - contrast);
        var matrix = new ColorMatrix([
            [.299f * contrast, .299f * contrast, .299f * contrast, 0, 0],
            [.587f * contrast, .587f * contrast, .587f * contrast, 0, 0],
            [.114f * contrast, .114f * contrast, .114f * contrast, 0, 0],
            [0, 0, 0, 1, 0],
            [offset, offset, offset, 0, 1]
        ]);
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(matrix);
        if (mode == PixelMode.Binary) attributes.SetThreshold(.56f);
        using var graphics = Graphics.FromImage(output);
        graphics.DrawImage(enlarged, new Rectangle(0, 0, output.Width, output.Height),
            0, 0, enlarged.Width, enlarged.Height, GraphicsUnit.Pixel, attributes);
        return output;
    }

    private static double Score(OcrDocument doc)
    {
        var text = string.Join(" ", doc.Regions.Select(x => x.Text));
        var useful = text.Count(c => char.IsLetterOrDigit(c) || IsCjk(c));
        var replacement = text.Count(c => c == '�' || c == '?' || (c < 32 && !char.IsWhiteSpace(c)));
        var shortNoise = doc.Regions.Count(x => x.Text.Length <= 2 && !x.Text.Any(char.IsLetterOrDigit));
        var fragmented = Regex.Matches(text, @"\b[A-Za-z]{2,}\s+[A-Za-z]\s+[A-Za-z]{1,}\b").Count;
        var oddCase = Regex.Matches(text, @"[a-z][A-Z]\b").Count;
        var noise = text.Count(c => !char.IsLetterOrDigit(c) && !IsCjk(c) &&
            !char.IsWhiteSpace(c) && !"，。！？；：、,.!?;:'\"—-()[]".Contains(c));
        return useful + doc.Regions.Count * 2 - replacement * 8 - shortNoise * 6 -
               fragmented * 10 - oddCase * 3 - noise * 2;
    }

    private static OcrCandidateDiagnostic BuildDiagnostic(CandidateResult result, bool selected)
    {
        var text = string.Join(" ", result.Document.Regions.Select(x => x.Text));
        var characters = text.Count(c => char.IsLetterOrDigit(c) || IsCjk(c));
        var words = Regex.Matches(text, @"[A-Za-z0-9]+|[\u3040-\u30ff\u3400-\u9fff\uac00-\ud7af]+").Count;
        var noise = text.Count(c => !char.IsLetterOrDigit(c) && !IsCjk(c) &&
            !char.IsWhiteSpace(c) && !"，。！？；：、,.!?;:'\"—-()[]".Contains(c));
        var singles = result.Document.Regions.Count(x => x.Text.Trim().Length == 1);
        var fragmented = Regex.Matches(text, @"\b[A-Za-z]{2,}\s+[A-Za-z]\s+[A-Za-z]{1,}\b").Count;
        var ratio = text.Length == 0 ? 1f : noise / (float)text.Length;
        var confidence = Math.Clamp(1f - ratio - fragmented * .04f - singles * .01f, 0, 1);
        return new OcrCandidateDiagnostic
        {
            Name = result.Candidate.Name,
            Language = result.Language,
            CharacterCount = characters,
            WordCount = words,
            EstimatedConfidence = confidence,
            NoiseRatio = ratio,
            SingleCharacterNoise = singles,
            FragmentedWordCount = fragmented,
            Score = result.Score,
            Selected = selected
        };
    }

    private static List<OcrRegion> MergeLanguageResults(IEnumerable<CandidateResult> results, Size sourceSize)
    {
        var merged = new List<OcrRegion>();
        foreach (var result in results)
        foreach (var incoming in result.Document.Regions)
        {
            incoming.Bounds = RectangleF.Intersect(
                new RectangleF(PointF.Empty, sourceSize), incoming.Bounds);
            incoming.Confidence = (float)Math.Clamp(result.Score / Math.Max(1, result.Document.Text.Length), 0, 1);
            var duplicate = merged.FirstOrDefault(x => Overlap(x.Bounds, incoming.Bounds) > .58f);
            if (duplicate is null) merged.Add(incoming);
            else if (Quality(incoming.Text) > Quality(duplicate.Text))
            {
                duplicate.Text = incoming.Text;
                duplicate.Language = incoming.Language;
                duplicate.Confidence = incoming.Confidence;
                duplicate.Bounds = RectangleF.Union(duplicate.Bounds, incoming.Bounds);
            }
        }
        return merged.OrderBy(x => x.Bounds.Top).ThenBy(x => x.Bounds.Left).ToList();
    }

    public static List<OcrRegion> MergeLinesIntoParagraphs(IReadOnlyList<OcrRegion> lines, Size sourceSize)
    {
        var paragraphs = new List<List<OcrRegion>>();
        foreach (var line in lines.Where(x => !IsLikelyIconNoise(x))
                     .OrderBy(x => x.Bounds.Top).ThenBy(x => x.Bounds.Left))
        {
            var target = paragraphs.LastOrDefault(p =>
            {
                if (IsStandaloneUiLine(line.Text) || p.Any(x => IsStandaloneUiLine(x.Text)))
                    return false;
                var bounds = p.Select(x => x.Bounds).Aggregate(RectangleF.Union);
                var typicalHeight = p.Average(x => x.Bounds.Height);
                var gap = line.Bounds.Top - bounds.Bottom;
                var leftTolerance = Math.Max(typicalHeight * 2.2f,
                    Math.Min(bounds.Width, line.Bounds.Width) * .18f);
                var aligned = Math.Abs(bounds.Left - line.Bounds.Left) < leftTolerance;
                var horizontalGap = line.Bounds.Left - bounds.Right;
                var sameBaseline = Math.Abs(line.Bounds.Bottom - bounds.Bottom) <= typicalHeight * .45f;
                var similarHeight = line.Bounds.Height >= typicalHeight * .65f &&
                                    line.Bounds.Height <= typicalHeight * 1.45f;
                var adjacentFragment = sameBaseline && similarHeight &&
                    horizontalGap >= -typicalHeight * .5f && horizontalGap <= typicalHeight * 3.2f;
                return adjacentFragment ||
                       (gap >= -typicalHeight * .35f && gap <= typicalHeight * 1.25f && aligned);
            });
            if (target is null) paragraphs.Add([line]); else target.Add(line);
        }

        var index = 0;
        return paragraphs.Select(group =>
        {
            var ordered = group.OrderBy(x => x.Bounds.Top).ThenBy(x => x.Bounds.Left).ToList();
            var bounds = ordered.Select(x => x.Bounds).Aggregate(RectangleF.Union);
            bounds.Inflate(Math.Max(3, bounds.Height * .02f), Math.Max(2, bounds.Height * .025f));
            bounds.Intersect(new RectangleF(0, 0, sourceSize.Width, sourceSize.Height));
            return new OcrRegion
            {
                Id = $"SEG{++index:000}",
                Text = ShouldPreserveRawLineBreaks(ordered)
                    ? string.Join(Environment.NewLine, ordered.Select(x => NormalizeText(x.Text)))
                    : JoinNaturalLines(ordered.Select(x => x.Text)),
                RawText = string.Join(Environment.NewLine, ordered.Select(x => x.Text)),
                RawLines = ordered.Select(CloneRegion).ToList(),
                ReadingOrder = index,
                SegmentType = ClassifySegment(ordered, bounds),
                Bounds = bounds,
                Confidence = ordered.Average(x => x.Confidence),
                Language = string.Join("+", ordered.Select(x => x.Language).Distinct())
            };
        }).ToList();
    }

    public static List<OcrRegion> BuildOrganizedRegions(
        IReadOnlyList<OcrRegion> rawLines, Size sourceSize, CleanupStrength strength)
    {
        var workingCopy = rawLines.Select(CloneRegion).ToList();
        var paragraphs = MergeLinesIntoParagraphs(workingCopy, sourceSize);
        foreach (var paragraph in paragraphs)
        {
            paragraph.Text = paragraph.Text.Contains('\n')
                ? string.Join(Environment.NewLine, paragraph.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => CleanupText(line, paragraph, strength)).Where(line => !string.IsNullOrWhiteSpace(line)))
                : CleanupText(paragraph.Text, paragraph, strength);
        }
        return paragraphs.Where(x => !string.IsNullOrWhiteSpace(x.Text)).ToList();
    }

    public static OcrSnapshot CreateSnapshot(
        IReadOnlyList<OcrRegion> lines, string language,
        IReadOnlyList<OcrCandidateDiagnostic>? diagnostics = null) => new(
        lines.Select(x => new OcrLineSnapshot(x.Id, x.Text, x.Bounds,
            x.ReadingOrder, x.Language, x.Confidence)).ToList().AsReadOnly(),
        string.Join(Environment.NewLine, lines.Select(x => x.Text)),
        language,
        (diagnostics ?? []).Select(x => new OcrCandidateSnapshot(x.Name, x.Language,
            x.CharacterCount, x.WordCount, x.EstimatedConfidence, x.NoiseRatio,
            x.SingleCharacterNoise, x.FragmentedWordCount, x.Score, x.Selected)).ToList().AsReadOnly());

    public static List<SegmentGroup> BuildSegmentGroups(IReadOnlyList<OcrRegion> organizedRegions) =>
        organizedRegions.Select(ToSegmentGroup).ToList();

    public static string ComputeSnapshotHash(OcrSnapshot snapshot)
    {
        var content = new StringBuilder(snapshot.RawText).Append('\n').Append(snapshot.OcrLanguage);
        foreach (var line in snapshot.Lines.OrderBy(x => x.ReadingOrder))
            content.Append('\n').Append(line.SegmentId).Append('|').Append(line.Text).Append('|')
                .Append(line.Bounds.X).Append(',').Append(line.Bounds.Y).Append(',')
                .Append(line.Bounds.Width).Append(',').Append(line.Bounds.Height).Append('|')
                .Append(line.ReadingOrder).Append('|').Append(line.Language).Append('|').Append(line.Confidence);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content.ToString())));
    }

    public static string CleanupText(string text, OcrRegion region, CleanupStrength strength)
    {
        if (strength == CleanupStrength.Off) return text;
        var value = NormalizeText(text)
            .Replace('，', ',').Replace('。', '.').Replace('：', ':').Replace('；', ';');
        value = Regex.Replace(value, @"(?<=\p{L})\s+(?=\p{L})", " ");
        value = Regex.Replace(value, @"(?<=\d)\s*[-.．·]\s*(?=\d)", ".");
        value = Regex.Replace(value, @"([!?。，,.])\1+", "$1");
        value = Regex.Replace(value, @"\b([A-Za-z])\s+([A-Za-z])\s+([A-Za-z])\b", "$1$2$3");
        value = Regex.Replace(value, @"^(?:台|○|◯|@)\s+(?=[A-Z][a-z])", "");
        value = Regex.Replace(value,
            @"^(?:O|○|◯|@|»|›)\s*(?=(?:大约\s*)?\d+\s*(?:条\s*)?(?:秒|分钟|小时|天|消息|seconds?|minutes?|hours?|days?|messages?))",
            "", RegexOptions.IgnoreCase);
        value = CorrectNumericOcrTokens(value);
        var trimmed = value.Trim();
        var isolatedIcon = trimmed is "O" or "○" or "◯" or "»" or "台" or "@";
        var smallIsolated = region.RawLines.Count <= 1 && region.Bounds.Width <= region.Bounds.Height * 2.2f;
        if (isolatedIcon && smallIsolated && region.SegmentType is not SegmentType.Button and not SegmentType.Tag)
            return "";
        if (strength == CleanupStrength.Strict && trimmed.Length == 1 && !char.IsLetterOrDigit(trimmed[0]) && trimmed[0] is not '@' and not '#' and not '%' and not '$')
            return "";
        return trimmed;
    }

    public static string CorrectNumericOcrTokens(string text)
    {
        return Regex.Replace(text, @"\b[A-Za-z0-9]+\b", match =>
        {
            var token = match.Value;
            if (!token.Any(char.IsDigit) || !token.Any(c => c is 'o' or 'O')) return token;
            var numericCharacters = token.Count(c => char.IsDigit(c) || c is 'o' or 'O');
            if (numericCharacters != token.Length || token.Count(char.IsDigit) < 2) return token;
            return token.Replace('o', '0').Replace('O', '0');
        });
    }

    public static bool IsLikelyIconNoise(OcrRegion region)
    {
        var text = NormalizeText(region.Text).Trim();
        if (text.Length == 0 || text.Length > 2) return false;
        var squareLike = region.Bounds.Width <= Math.Max(3, region.Bounds.Height * 1.8f);
        if (!squareLike) return false;
        if (text.Length == 1 && !char.IsDigit(text[0])) return true;
        return text is "O" or "○" or "◯" or "@" or "台" or "a" or "»" or "›" or "锁";
    }

    private static SegmentType ClassifySegment(IReadOnlyList<OcrRegion> lines, RectangleF bounds)
    {
        var text = string.Join(" ", lines.Select(x => x.Text)).Trim();
        if (Regex.IsMatch(text, @"^\D{0,3}(?:大约\s*)?\d+\s*(?:hours?|minutes?|seconds?|days?|小时|分钟|秒|天)(?:\s*(?:前|ago))?$", RegexOptions.IgnoreCase)) return SegmentType.Time;
        if (Regex.IsMatch(text, @"^\D{0,3}\d+\s*(?:条\s*)?(?:messages?|消息)$", RegexOptions.IgnoreCase)) return SegmentType.MessageCount;
        if (Regex.IsMatch(text, @"^(继续|确认|取消|关闭|设置|continue|ok|cancel|close|settings?)$", RegexOptions.IgnoreCase)) return SegmentType.Button;
        if (text.StartsWith('@')) return SegmentType.UserName;
        if (text.StartsWith('#')) return SegmentType.Tag;
        if (Regex.IsMatch(text, @"^[A-Z][\p{L}' -]{1,40}\s*[|｜]\s*[A-Z][\p{L}' -]{1,40}$")) return SegmentType.CharacterInfo;
        if (Regex.IsMatch(text, "^(?:[\\\"“‘「『]|[-—–]\\s).*")) return SegmentType.Dialogue;
        if (text.All(c => char.IsDigit(c) || char.IsPunctuation(c) || char.IsWhiteSpace(c))) return SegmentType.NumericInfo;
        var averageHeight = lines.Average(x => x.Bounds.Height);
        var singleVisualLine = bounds.Height <= averageHeight * 1.55f;
        if (singleVisualLine && text.Length < 100 && averageHeight >= 22) return SegmentType.Title;
        if (singleVisualLine && averageHeight <= 16 && text.Length <= 100) return SegmentType.ImageCaption;
        if (singleVisualLine && text.Length <= 80) return SegmentType.Label;
        return lines.Count > 1 ? SegmentType.Body : SegmentType.Other;
    }

    private static bool IsStandaloneUiLine(string text)
    {
        var value = NormalizeText(text);
        if (value.Length == 0) return false;
        if (Regex.IsMatch(value, @"^\D{0,3}\d+\s*(条\s*)?(消息|messages?)\b", RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(value, @"^\D{0,3}(大约\s*)?\d+\s*(秒|分钟|小时|天|seconds?|minutes?|hours?|days?)", RegexOptions.IgnoreCase))
            return true;
        return value.Length <= 8 &&
               Regex.IsMatch(value, @"^(继续|设置|关闭|确认|取消|保存|复制|continue|settings?|close|ok|cancel|save|copy)$",
                   RegexOptions.IgnoreCase);
    }

    private static string JoinNaturalLines(IEnumerable<string> lines)
    {
        var values = lines.Select(NormalizeText).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (values.Count == 0) return "";
        var result = new StringBuilder(values[0]);
        for (var i = 1; i < values.Count; i++)
        {
            var next = values[i];
            var previous = result.Length == 0 ? '\0' : result[^1];
            var first = next[0];
            if (NeedsWordSpace(previous, first)) result.Append(' ');
            result.Append(next);
        }
        return result.ToString();
    }

    private static bool ShouldPreserveRawLineBreaks(IReadOnlyList<OcrRegion> lines)
    {
        if (lines.Count < 3) return false;
        var values = lines.Select(x => NormalizeText(x.Text).Trim())
            .Where(x => x.Length > 0).ToList();
        if (values.Count < 3) return false;

        var averageHeight = Math.Max(1f, lines.Average(x => x.Bounds.Height));
        var aligned = lines.Count(x => Math.Abs(x.Bounds.Left - lines[0].Bounds.Left) <= averageHeight * 1.35f)
            >= Math.Ceiling(lines.Count * .75);
        var shortRatio = values.Count(x => x.Length <= 28) / (double)values.Count;
        var terminalRatio = values.Count(x => Regex.IsMatch(x, "[.!?。！？][\\\"'”’）)]?$")) / (double)values.Count;
        var tokenLikeRatio = values.Count(x =>
            !x.Any(char.IsWhiteSpace) ||
            Regex.IsMatch(x, @"^[\p{L}\p{N}_./:@%+\-]+(?:\s+[\p{L}\p{N}_./:@%+\-]+){0,2}$")) / (double)values.Count;
        var repeatedPattern = values.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1);
        var listStructure = values.Count >= 5 || repeatedPattern || tokenLikeRatio >= .75;

        return aligned && shortRatio >= .75 && terminalRatio <= .34 && listStructure;
    }

    private static bool NeedsWordSpace(char left, char right)
    {
        if (left == '\0' || char.IsWhiteSpace(left) || char.IsWhiteSpace(right)) return false;
        if ("，。！？；：、,.!?;:)]}》」』”’".Contains(right)) return false;
        if ("([{《「『“‘".Contains(left)) return false;
        var leftCjk = IsCjk(left);
        var rightCjk = IsCjk(right);
        if (leftCjk && rightCjk) return false;
        return char.IsLetterOrDigit(left) || char.IsLetterOrDigit(right) || leftCjk || rightCjk;
    }

    private static SegmentGroup ToSegmentGroup(OcrRegion source) => new()
    {
        GroupId = source.Id.Replace("SEG", "GRP", StringComparison.Ordinal),
        GroupType = source.SegmentType,
        SourceSegmentIds = source.RawLines.Select(x => x.Id).ToList().AsReadOnly(),
        OriginalText = string.Join(Environment.NewLine, source.RawLines.Select(x => x.Text)),
        OrganizedText = source.Text,
        Bounds = source.Bounds,
        RenderRectangle = source.Bounds,
        ReadingOrder = source.ReadingOrder,
        CanOverlay = source.CanOverlay
    };

    private static OcrRegion CloneRegion(OcrRegion source) => new()
    {
        Id = source.Id,
        Text = source.Text,
        Bounds = source.Bounds,
        Confidence = source.Confidence,
        Language = source.Language,
        Translation = source.Translation,
        SegmentType = source.SegmentType,
        RawText = source.RawText,
        ReadingOrder = source.ReadingOrder,
        HasReliableTranslation = source.HasReliableTranslation,
        CanOverlay = source.CanOverlay,
        RawLines = source.RawLines.Select(CloneRegion).ToList()
    };

    private static int Quality(string text)
    {
        var useful = text.Count(c => char.IsLetterOrDigit(c) || IsCjk(c));
        var invalid = text.Count(c => c == '�' || c == '?');
        var fragmented = Regex.Matches(text, @"\b[A-Za-z]{2,}\s+[A-Za-z]\s+[A-Za-z]{1,}\b").Count;
        var oddCase = Regex.Matches(text, @"[a-z][A-Z]\b").Count;
        return useful * 3 - invalid * 5 - fragmented * 10 - oddCase * 3;
    }
    private static bool IsCjk(char c) => c is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff' or >= '\uac00' and <= '\ud7af';
    private static string NormalizeText(string value)
    {
        var text = string.Join(" ", value.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        const string cjk = @"\u3040-\u30ff\u3400-\u9fff\uac00-\ud7af";
        text = Regex.Replace(text, $@"(?<=[{cjk}])\s+(?=[{cjk}，。！？；：、])", "");
        text = Regex.Replace(text, $@"(?<=[（《“‘])\s+(?=[{cjk}])", "");
        text = Regex.Replace(text, $@"(?<=[{cjk}])\s+(?=[）》”’])", "");
        return text;
    }
    private static float Overlap(RectangleF a, RectangleF b)
    {
        var intersection = RectangleF.Intersect(a, b);
        if (intersection.IsEmpty) return 0;
        var area = intersection.Width * intersection.Height;
        return area / Math.Max(1, Math.Min(a.Width * a.Height, b.Width * b.Height));
    }

    private static async Task<string> RunHelperAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var helper = Path.Combine(AppContext.BaseDirectory, "tools", "ocr-helper", "ScreenshotOcrHelper.exe");
        if (!File.Exists(helper)) helper = Path.Combine(AppContext.BaseDirectory, "ScreenshotOcrHelper.exe");
        if (!File.Exists(helper)) throw new FileNotFoundException("本地 OCR 组件缺失。", helper);
        var info = new ProcessStartInfo
        {
            FileName = helper, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动本地 OCR 组件。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            throw;
        }
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(error) ? "Windows OCR 执行失败。" : error.Trim());
        return output.Trim();
    }
}
