using System.Text.RegularExpressions;
using ScreenshotTranslationUiTester.CorePipelineV2;

namespace ScreenshotTranslationUiTester;

// Annotation only: Core's source identity, normalization, grouping and accepted text owners are unchanged.
internal static class CoreTranslationItemFactory
{
    private static readonly Regex Words = new(@"[A-Za-z]+(?:['’-][A-Za-z]+)*", RegexOptions.Compiled);
    private static readonly Regex ProtectedSpans = new(
        @"\{[^}\r\n]*\}|<[^>\r\n]+>|https?://\S+|[@#]\w+|[%$]\w+|\b\w*[\d_]\w*\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static TranslationItem Create(CorePipelineDocument document, VisualBlock block,
        StructuredTextRole role = StructuredTextRole.Unknown)
    {
        if(block.SourceRole?.Role=="Heading" && role is not (StructuredTextRole.CharacterName or StructuredTextRole.Species))
            role=StructuredTextRole.Title;
        else if(role==StructuredTextRole.Unknown)
            role=block.SourceRole?.Role switch {"Heading"=>StructuredTextRole.Title,
                "Prose"=>StructuredTextRole.BodyParagraph,"Control"=>StructuredTextRole.UILabel,_=>role};
        var item = new TranslationItem(block.BlockId, block.SourceText, role,
            block.Lines.Select(x => x.SourceId).ToArray(), TranslationIdentityContract.CoreV2Block);
        if(block.Lines.Count==1&&block.SourceText.Length<=24)
        {
            var b=block.Bounds;
            var neighbors=document.NormalizedLines.Where(l=>l.SourceId!=block.Lines[0].SourceId&&
                l.Confidence>=.97f&&Regex.IsMatch(l.SourceText,@"^[+\-]?\d+(?:[.,]\d+)?\s*[%％]$")&&
                l.Bounds.Top+l.Bounds.Height/2<b.Top+b.Height*.45f&&
                b.Top+b.Height/2-l.Bounds.Top-l.Bounds.Height/2<=b.Height*3&&
                Math.Min(l.Bounds.Right,b.Right)>Math.Max(l.Bounds.Left,b.Left))
                .OrderBy(l=>Math.Abs(l.Bounds.Bottom-b.Top)).Take(2)
                .Select(l=>new SourceNeighborText(l.SourceId,l.SourceText,"NEARBY_PERCENTAGE_ABOVE_SOURCE_LABEL")).ToArray();
            if(neighbors.Length>0)item=item with{SourceNeighbors=neighbors};
        }
        var names = NameAnchors(document);
        // A short heading is ambiguous without its neighbouring heading and
        // descriptive paragraph. Preserve that source relationship as context;
        // geometry never assigns a fixed species, person or operating system.
        if(role==StructuredTextRole.Title&&block.Lines.Count==1&&Words.Matches(block.SourceText).Count is >0 and <=4)
        {
            var b=block.Bounds;
            var related=document.NormalizedLines.Where(l=>!block.Lines.Any(own=>own.SourceId==l.SourceId)&&
                l.Confidence>=.85f&&Math.Min(l.Bounds.Right,b.Right)>Math.Max(l.Bounds.Left,b.Left)&&
                ((l.Bounds.Top<b.Top&&l.Bounds.Height>b.Height*1.15f&&b.Top-l.Bounds.Bottom<b.Height*2)||
                 (l.Bounds.Top>=b.Bottom&&l.Bounds.Top-b.Bottom<b.Height*3.5f&&l.Bounds.Width>b.Width*1.5f)))
                .OrderBy(l=>Math.Abs(l.Bounds.Top-b.Top)).Take(5)
                .Select(l=>new SourceNeighborText(l.SourceId,l.SourceText,l.Bounds.Top<b.Top?"SOURCE_HEADING_ABOVE":"SOURCE_DESCRIPTION_BELOW"))
                .Where(l=>l.Text.Length<=400).ToArray();
            if(related.Length>0)item=item with{SourceNeighbors=item.SourceNeighbors.Concat(related).DistinctBy(l=>l.SourceId).ToArray()};
        }
        var sourceWords=Words.Matches(item.Text).Select(m=>m.Value).ToArray();
        var anchoredName=sourceWords.Length is >0 and <=3 && sourceWords.All(w=>names.Contains(w)) &&
            sourceWords.Any(w=>w.Skip(1).Any(char.IsLower));
        // A closed card cell proves where its caption belongs, not whether it
        // names a person, a fictional object, or a functional command. Keep the
        // layout role, but do not turn that geometry alone into semantic proof.
        var geometricCaptionOnly=block.SourceRole?.Role=="Control" && block.SourceCell is not null;
        if(!anchoredName && !geometricCaptionOnly && role is not (StructuredTextRole.CharacterName or StructuredTextRole.Species) &&
            block.SourceRole?.Role is "Heading" or "Control" or "Prose")
            item=item with{SemanticPurpose="SOURCE_"+block.SourceRole.Role.ToUpperInvariant()};
        var hints = new List<OcrTokenHint>();
        var offset = 0;
        foreach (var normalized in block.Lines)
        {
            var raw = document.RawLines.SingleOrDefault(x => x.SourceId == normalized.SourceId);
            if (raw is not null && raw.RawText == normalized.SourceText && raw.OcrAlternatives.Count > 0)
            {
                var checkedEvidence = OcrAlternativeEvidenceValidator.Validate(
                    OcrAlternativeEvidenceValidator.Contract,
                    raw.OcrAlternatives.Where(x => x.Verified && x.Contract == OcrAlternativeEvidenceValidator.Contract),
                    raw.OcrRequestImageSha256, raw.SourceId, raw.Bounds, document.Canvas)
                    .Where(x => x.Verified && x.Confidence >= 0.65f).ToArray();
                hints.AddRange(BuildHints(raw.SourceId, normalized.SourceText, offset, checkedEvidence, names));
            }
            offset += normalized.SourceText.Length + 1;
        }
        if (hints.Count == 0) return item;
        return item with { OcrContext = new(OcrTranslationContextContract.Schema,
            TranslationPromptBuilder.Sha256(item.Text), hints.ToArray()) };
    }

    private static IEnumerable<OcrTokenHint> BuildHints(string sourceId, string source, int offset,
        IReadOnlyList<OcrAlternativeEvidence> candidates, IReadOnlySet<string> anchors)
    {
        // Code/path-like lines are not eligible for ordinary-word OCR interpretation.
        if (source.Contains('\\') || source.Contains((char)96) ||
            Regex.IsMatch(source, @"(?:=>|[{};=]|(?:^|\s)/(?:[\w.-]+/)*[\w.-]+)"))
            yield break;
        var words = Words.Matches(source).Cast<Match>().ToArray();
        if (words.Length < 3 || words.Length > 40) yield break;
        var protectedSpans = ProtectedSpans.Matches(source).Cast<Match>().ToArray();
        var aligned = candidates.Select(c => (Evidence: c, Words: Words.Matches(c.Text).Cast<Match>().ToArray()))
            .Where(c => c.Words.Length == words.Length).ToArray();
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            if (word.Length < 2 || word.Length > 24 ||
                protectedSpans.Any(p => p.Index < word.Index + word.Length && word.Index < p.Index + p.Length) ||
                IsName(word.Value, anchors) || IsAttributionName(words, i))
                continue;
            // Keep an independently supported primary reading. Adding a competing reading to
            // an already supported token can degrade a correct source without resolving ambiguity.
            var primarySupport = aligned.Where(c => CompatibleNeighbours(words, c.Words, i) &&
                c.Words[i].Value == word.Value).Select(c => c.Evidence.CropSha256).Distinct().Count();
            if (primarySupport >= 2) continue;
            var supported = aligned.Where(c =>
                CompatibleNeighbours(words, c.Words, i) &&
                VisualConfusion(word.Value, c.Words[i].Value) &&
                !IsName(c.Words[i].Value, anchors))
                .GroupBy(c => c.Words[i].Value, StringComparer.Ordinal)
                .Select(g => new OcrTokenReading(g.Key,
                    g.GroupBy(x => x.Evidence.CropSha256, StringComparer.Ordinal)
                        .Select(x => x.First().Evidence.EvidenceId).OrderBy(x => x, StringComparer.Ordinal).ToArray()))
                .Where(r => r.EvidenceIds.Count >= 2).OrderBy(r => r.Text, StringComparer.Ordinal).ToArray();
            if (supported.Length > 0)
                yield return new(sourceId, offset + word.Index, word.Length, word.Value, supported);
        }
    }

    private static bool CompatibleNeighbours(Match[] source, Match[] target, int index)
    {
        // An alternative with a different sentence is not evidence for a same-position glyph.
        var unchanged = source.Where((s, i) => i != index &&
            string.Equals(s.Value, target[i].Value, StringComparison.OrdinalIgnoreCase)).Count();
        return unchanged >= Math.Max(2, source.Length - 3);
    }

    private static bool IsAttributionName(Match[] words, int i)
    {
        // Protect an unknown author/person after an attribution preposition even without a title.
        // This is a protection constraint only; it never invents a name or changes source spelling.
        return i > 0 && (words[i - 1].Value.Equals("by", StringComparison.OrdinalIgnoreCase) ||
            words[i - 1].Value.Equals("named", StringComparison.OrdinalIgnoreCase) ||
            words[i - 1].Value.Equals("called", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsName(string word, IReadOnlySet<string> anchors) =>
        anchors.Any(a => string.Equals(a, word, StringComparison.OrdinalIgnoreCase) ||
            (a.Length >= 5 && word.Length >= 5 && EditDistance(a.ToLowerInvariant(), word.ToLowerInvariant()) <= 1));

    private static HashSet<string> NameAnchors(CorePipelineDocument document)
    {
        var occurrences = document.RawLines.SelectMany(line =>
            Words.Matches(line.RawText).Cast<Match>().Select((word, index) =>
                (Line: line, Word: word, Index: index))).ToArray();
        var anchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in occurrences.GroupBy(x => x.Word.Value, StringComparer.Ordinal))
        {
            var value = group.Key;
            if (value.Length < 3 || !char.IsUpper(value[0]) || value.Skip(1).All(char.IsUpper)) continue;
            var distinctLines = group.Select(x => x.Line.SourceId).Distinct().Count();
            // A capital at the beginning of a sentence is insufficient. Require a separate short
            // title AND an in-sentence capitalized occurrence (or multiple in-sentence occurrences).
            var title = group.Any(x => Words.Matches(x.Line.RawText).Count <= 2);
            var internalLines = group.Where(x => x.Index > 0 &&
                    !IsSentenceStart(x.Line.RawText, x.Word.Index))
                .Select(x => x.Line.SourceId).Distinct().Count();
            if (distinctLines >= 2 && internalLines >= 1 && (title || internalLines >= 2))
                anchors.Add(value);
        }
        return anchors;
    }

    private static bool IsSentenceStart(string text, int position)
    {
        var before = text[..position].TrimEnd();
        return before.Length == 0 || ".!?".Contains(before[^1]);
    }

    private static bool VisualConfusion(string original, string alternative)
    {
        if (original == alternative || !Regex.IsMatch(alternative, "^[A-Za-z]+$")) return false;
        // Restrict to actual letter-shape ambiguities, not arbitrary edit-distance spelling correction.
        static string Glyphs(string value) => value.ToLowerInvariant()
            .Replace("rn", "m").Replace("vv", "w").Replace('l', 'i');
        return Glyphs(original) == Glyphs(alternative);
    }

    private static int EditDistance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0]; row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var old = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1),
                    diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = old;
            }
        }
        return row[^1];
    }
}
