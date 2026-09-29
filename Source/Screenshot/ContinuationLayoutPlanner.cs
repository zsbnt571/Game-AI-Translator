using System.Security.Cryptography;
using System.Text;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

internal sealed record ContinuationLayoutBlockEvidence(
    string BlockId,
    IReadOnlyList<string> SourceIds,
    RectangleF OwnerBounds,
    RectangleF AvailableRect,
    string TranslatedText,
    string FontFamily,
    FontStyle FontStyle,
    float CurrentFontSize,
    Color TextColor,
    bool Outline=false,
    Color OutlineColor=default,
    float OutlineWidth=0,
    bool Shadow=false,
    Color ShadowColor=default,
    PointF ShadowOffset=default,
    float Glow=0,
    byte Alpha=255);

internal sealed record ContinuationLayoutFragment(
    string BlockId,
    IReadOnlyList<string> SourceIds,
    int Start,
    int Length,
    string Text,
    string Utf8Hash);

internal sealed record ContinuationLayoutLine(
    int Index,
    string Text,
    int Start,
    int Length,
    RectangleF Bounds,
    bool ContainedInAvailable,
    bool ContainedInEnvelope,
    IReadOnlyList<string> ProtectedHits);

internal sealed record ContinuationLayoutOwnerPlan(
    string ProposalId,
    IReadOnlyList<string> UnderlyingBlockIds,
    IReadOnlyList<string> UnderlyingSourceIds,
    IReadOnlyList<ContinuationLayoutFragment> Fragments,
    RectangleF ConservativeEnvelope,
    RectangleF AvailableRect,
    string StyleAnchorBlockId,
    string StyleSelectionReason,
    string FontFamily,
    FontStyle FontStyle,
    float PreferredFontSize,
    float FontSize,
    Color TextColor,
    bool Outline,
    Color OutlineColor,
    float OutlineWidth,
    bool Shadow,
    Color ShadowColor,
    PointF ShadowOffset,
    float Glow,
    byte Alpha,
    string CombinedText,
    string CombinedTextUtf8Hash,
    IReadOnlyList<ContinuationLayoutLine> Lines,
    float LineHeight,
    float RequiredWidth,
    float RequiredHeight,
    int FitAttempts,
    string FitStatus,
    bool CharactersStable,
    bool FragmentOrderStable,
    bool FragmentHashesStable,
    bool Clipped,
    bool Ellipsis,
    bool GeometrySafe,
    bool BoundaryTextSeparatorRequired,
    bool Active,
    string ActivationReason);

internal sealed record ContinuationLayoutPlanningResult(
    ContinuationProposalResult Relation,
    IReadOnlyList<ContinuationLayoutOwnerPlan> Owners,
    IReadOnlyDictionary<string, ContinuationLayoutOwnerPlan> ActiveOwnerByBlock);

internal static class ContinuationLayoutPlanner
{
    internal const string Mode = "LAYOUT_ONLY_PROJECTION";

    internal static ContinuationLayoutPlanningResult Plan(
        Graphics graphics,
        CorePipelineDocument document,
        ContinuationProposalResult relation,
        IReadOnlyList<ContinuationLayoutBlockEvidence> blockEvidence)
    {
        var evidenceById = blockEvidence.ToDictionary(x => x.BlockId, StringComparer.Ordinal);
        var blockById = document.VisualBlocks.ToDictionary(x => x.BlockId, StringComparer.Ordinal);
        var owners = new List<ContinuationLayoutOwnerPlan>();

        foreach (var proposal in relation.Proposals)
        {
            if (proposal.UnderlyingBlocks.Any(x => !evidenceById.ContainsKey(x) || !blockById.ContainsKey(x)))
            {
                owners.Add(Inactive(proposal, "INACTIVE_MEMBER_TRANSLATION_OR_CURRENT_FIT_UNAVAILABLE"));
                continue;
            }

            var evidence = proposal.UnderlyingBlocks.Select(x => evidenceById[x]).ToArray();
            var blocks = proposal.UnderlyingBlocks.Select(x => blockById[x]).ToArray();
            var envelope = blocks.Select(x => x.Bounds).Aggregate(RectangleF.Union);
            var available = RectangleF.Inflate(envelope, -2, -1);
            var anchor = evidence.OrderByDescending(x => x.AvailableRect.Width * x.AvailableRect.Height)
                .ThenBy(x => Array.IndexOf(evidence, x)).First();

            var combined = new StringBuilder();
            var fragments = new List<ContinuationLayoutFragment>();
            var boundarySeparator = false;
            foreach (var item in evidence)
            {
                if (combined.Length > 0)
                    boundarySeparator |= NeedsBoundarySeparator(combined[^1], item.TranslatedText.FirstOrDefault());
                var start = combined.Length;
                combined.Append(item.TranslatedText);
                fragments.Add(new(item.BlockId, item.SourceIds, start, item.TranslatedText.Length,
                    item.TranslatedText, HashText(item.TranslatedText)));
            }

            var text = combined.ToString();
            var sourceSet = proposal.UnderlyingSourceIds.ToHashSet(StringComparer.Ordinal);
            var sourceHeights = blocks.SelectMany(x => x.Lines).Where(x => sourceSet.Contains(x.SourceId))
                .Select(x => x.Bounds.Height).Order().ToArray();
            var median = Median(sourceHeights);
            var preferred = Math.Clamp(median * .72f, 9, 48);
            var floor = Math.Max(7, Math.Min(preferred, median * .45f));
            var attempts = 0;
            float selected = 0, lineHeight = 0, requiredWidth = 0, requiredHeight = 0;
            IReadOnlyList<string> lines = [];
            var fitStatus = "FIT_FAILED_PRESERVE_CURRENT_BLOCK_LAYOUT";

            for (var size = preferred; size >= floor; size -= .5f)
            {
                attempts++;
                using var font = FontManager.CreatePixel(anchor.FontFamily, size, anchor.FontStyle);
                var candidate = WrapComplete(graphics, text, font, Math.Max(4, available.Width));
                var height = font.GetHeight(graphics) * 1.02f;
                var width = candidate.Select(x => graphics.MeasureString(x, font, PointF.Empty,
                    StringFormat.GenericTypographic).Width).DefaultIfEmpty().Max();
                var totalHeight = candidate.Count * height;
                if (MathF.Ceiling(width) > available.Width || totalHeight > available.Height + .001f) continue;
                selected = size;
                lineHeight = height;
                requiredWidth = width;
                requiredHeight = totalHeight;
                lines = candidate;
                fitStatus = candidate.Count > 1 ? "FIT_WRAPPED" : size < preferred - .1f ? "FIT_SCALED" : "FIT_OK";
                break;
            }

            var memberSet = proposal.UnderlyingBlocks.ToHashSet(StringComparer.Ordinal);
            var obstacles = document.VisualBlocks.Where(x => !memberSet.Contains(x.BlockId)).ToArray();
            var linePlans = new List<ContinuationLayoutLine>();
            var cursor = 0;
            if (selected > 0)
            {
                using var font = FontManager.CreatePixel(anchor.FontFamily, selected, anchor.FontStyle);
                var y = available.Top + Math.Max(0, (available.Height - requiredHeight) / 2f);
                for (var index = 0; index < lines.Count; index++)
                {
                    var line = lines[index];
                    var measured = graphics.MeasureString(line, font, PointF.Empty, StringFormat.GenericTypographic);
                    var bounds = new RectangleF(available.Left, y, measured.Width, lineHeight);
                    var hits = obstacles.Where(x => bounds.IntersectsWith(x.Bounds)).Select(x => x.BlockId).ToArray();
                    linePlans.Add(new(index, line, cursor, line.Length, bounds,
                        Contains(available, bounds), Contains(envelope, bounds), hits));
                    cursor += line.Length;
                    y += lineHeight;
                }
            }

            var drawn = string.Concat(lines);
            var charactersStable = string.Equals(drawn, text, StringComparison.Ordinal);
            var orderStable = fragments.Select(x => x.BlockId).SequenceEqual(proposal.UnderlyingBlocks, StringComparer.Ordinal);
            var hashesStable = fragments.All(x => x.Utf8Hash == HashText(x.Text));
            var clipped = linePlans.Any(x => !x.ContainedInAvailable || !x.ContainedInEnvelope);
            var ellipsis = drawn.Contains('…') && !text.Contains('…');
            var geometrySafe = !clipped && linePlans.All(x => x.ProtectedHits.Count == 0);
            var active = selected > 0 && charactersStable && orderStable && hashesStable && !ellipsis && geometrySafe;
            owners.Add(new(proposal.ProposalId, proposal.UnderlyingBlocks, proposal.UnderlyingSourceIds,
                fragments, envelope, available, anchor.BlockId, "LARGEST_AVAILABLE_AREA_THEN_STABLE_PROPOSAL_ORDER",
                anchor.FontFamily, anchor.FontStyle, preferred, selected, anchor.TextColor,
                anchor.Outline,anchor.OutlineColor,anchor.OutlineWidth,anchor.Shadow,anchor.ShadowColor,
                anchor.ShadowOffset,anchor.Glow,anchor.Alpha,
                text, HashText(text),
                linePlans, lineHeight, requiredWidth, requiredHeight, attempts, fitStatus, charactersStable,
                orderStable, hashesStable, clipped, ellipsis, geometrySafe, boundarySeparator, active,
                active ? "ACTIVE_SAFE_LAYOUT_ONLY_OWNER" : "INACTIVE_UNSAFE_OR_UNFIT_FALLBACK_TO_CURRENT_BLOCK_LAYOUT"));
        }

        var activeByBlock = owners.Where(x => x.Active)
            .SelectMany(owner => owner.UnderlyingBlockIds.Select(blockId => (blockId, owner)))
            .ToDictionary(x => x.blockId, x => x.owner, StringComparer.Ordinal);
        return new(relation, owners, activeByBlock);
    }

    internal static void Draw(Graphics graphics, ContinuationLayoutOwnerPlan owner)
    {
        if (!owner.Active) return;
        using var font = FontManager.CreatePixel(owner.FontFamily, owner.FontSize, owner.FontStyle);
        foreach (var line in owner.Lines)
            SourceStyleTextDrawingR2.Draw(graphics,line.Text,font,line.Bounds.Location,owner.TextColor,
                owner.Outline,owner.OutlineColor,owner.OutlineWidth,owner.Shadow,owner.ShadowColor,
                owner.ShadowOffset,owner.Glow,owner.Alpha);
    }

    private static ContinuationLayoutOwnerPlan Inactive(ContinuationGroupProposal proposal, string reason) =>
        new(proposal.ProposalId, proposal.UnderlyingBlocks, proposal.UnderlyingSourceIds, [], RectangleF.Empty,
            RectangleF.Empty, "", "NONE", "", FontStyle.Regular, 0, 0, Color.Empty,
            false,Color.Transparent,0,false,Color.Transparent,PointF.Empty,0,255,"", HashText(""), [],
            0, 0, 0, 0, reason, true, true, true, false, false, false, false, false, reason);

    private static IReadOnlyList<string> WrapComplete(Graphics graphics, string text, Font font, float width)
    {
        var result = new List<string>();
        const string noStart = "，。！？：；、,.!?:;)）】》";
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            if (paragraph.Length == 0) { result.Add(""); continue; }
            var line = "";
            foreach (var character in paragraph)
            {
                var next = line + character;
                if (line.Length > 0 && graphics.MeasureString(next, font, PointF.Empty,
                        StringFormat.GenericTypographic).Width > width)
                {
                    if (noStart.Contains(character)) { line += character; continue; }
                    result.Add(line);
                    line = character.ToString();
                }
                else line = next;
            }
            if (line.Length > 0) result.Add(line);
        }
        return result.Count == 0 ? [""] : result;
    }

    private static bool NeedsBoundarySeparator(char left, char right) =>
        left <= 127 && right <= 127 && char.IsLetterOrDigit(left) && char.IsLetterOrDigit(right);
    private static float Median(float[] values) => values.Length == 0 ? 16 :
        values.Length % 2 == 1 ? values[values.Length / 2] :
        (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2f;
    private static bool Contains(RectangleF outer, RectangleF inner) =>
        inner.Left >= outer.Left - .001f && inner.Top >= outer.Top - .001f &&
        inner.Right <= outer.Right + .001f && inner.Bottom <= outer.Bottom + .001f;
    private static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
