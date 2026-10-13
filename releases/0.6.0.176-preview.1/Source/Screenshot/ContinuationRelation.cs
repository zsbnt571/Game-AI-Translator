using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

// Production copy of the Phase 2 Task 1 fixed gate. Conditions, thresholds,
// ProposalId construction and transitive component behavior are intentionally
// unchanged. Frozen prototype source SHA-256:
// D02C903E45C4B748FA8B3E739724344A99DE5F74B905455B10E578FAFD6B0104
internal sealed record ContinuationMetrics(
    int TailReadingOrder,
    int HeadReadingOrder,
    int ReadingOrderDelta,
    float VerticalGap,
    float VerticalGapRatio,
    float HorizontalOverlap,
    float HorizontalCenterDelta,
    float HorizontalCenterDeltaRatio,
    float HeightRatio,
    float NextToPreviousWidthRatio,
    bool ForwardVerticalFlow,
    bool AdjacentSourceLineage,
    bool CloseVerticalRhythm,
    bool StrongHorizontalOverlap,
    bool CenterAligned,
    bool CompatibleLineHeight,
    bool CenteredHangingShape,
    bool PreviousTextUnfinished,
    bool OpenQuotation,
    bool TextContinuationSignal,
    bool DifferentColumnSignal,
    bool StructuralSeparatorSignal,
    bool CompactHeaderBodySignal,
    bool IndependentCompactElementsSignal);

internal sealed record ContinuationDecision(
    string FromBlockId,
    string ToBlockId,
    IReadOnlyList<string> FromSourceIds,
    IReadOnlyList<string> ToSourceIds,
    string TailSourceId,
    string HeadSourceId,
    bool IsContinuation,
    string Reason,
    IReadOnlyList<string> PositiveEvidence,
    IReadOnlyList<string> NegativeBoundarySignals,
    ContinuationMetrics Metrics);

internal sealed record ContinuationGroupProposal(
    string ProposalId,
    IReadOnlyList<string> UnderlyingBlocks,
    IReadOnlyList<string> UnderlyingSourceIds,
    IReadOnlyList<string> DecisionReasons);

internal sealed record ContinuationProposalResult(
    IReadOnlyList<ContinuationDecision> CandidateDecisions,
    IReadOnlyList<ContinuationGroupProposal> Proposals);

internal static class ContinuationRelation
{
    internal const string FrozenPrototypeSourceSha256 = "D02C903E45C4B748FA8B3E739724344A99DE5F74B905455B10E578FAFD6B0104";

    private static readonly Regex SentenceTerminal = new(
        "(?:[.!?。！？]|…|\\.{2,})[\\\"'’”)\\]]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ContinuationDecision Evaluate(VisualBlock from, VisualBlock to)
    {
        var orderedFrom = from.Lines.OrderBy(x => x.ReadingOrder).ThenBy(x => x.SourceId, StringComparer.Ordinal).ToArray();
        var orderedTo = to.Lines.OrderBy(x => x.ReadingOrder).ThenBy(x => x.SourceId, StringComparer.Ordinal).ToArray();
        var tail = orderedFrom[^1];
        var head = orderedTo[0];
        var a = tail.Bounds;
        var b = head.Bounds;
        var medianHeight = (Math.Max(1f, a.Height) + Math.Max(1f, b.Height)) / 2f;
        var verticalGap = AxisGap(a.Top, a.Bottom, b.Top, b.Bottom);
        var verticalGapRatio = verticalGap / Math.Max(1f, medianHeight);
        var overlap = HorizontalOverlap(a, b);
        var centerDelta = Math.Abs(CenterX(a) - CenterX(b));
        var minWidth = Math.Max(1f, Math.Min(a.Width, b.Width));
        var centerDeltaRatio = centerDelta / minWidth;
        var heightRatio = Math.Max(a.Height, b.Height) / Math.Max(1f, Math.Min(a.Height, b.Height));
        var widthRatio = b.Width / Math.Max(1f, a.Width);
        var orderDelta = head.ReadingOrder - tail.ReadingOrder;

        var forward = CenterY(b) > CenterY(a) && b.Top >= a.Top - medianHeight * .15f;
        var adjacent = orderDelta == 1;
        var closeRhythm = verticalGapRatio <= .72f;
        var strongOverlap = overlap >= .72f;
        var centerAligned = centerDelta <= Math.Max(12f, minWidth * .18f);
        var compatibleHeight = heightRatio <= 1.40f;
        var hanging = widthRatio is >= .20f and <= .78f;
        var unfinished = !SentenceTerminal.IsMatch(tail.SourceText.Trim());
        var openQuote = HasOpenQuotation(from.SourceText);
        var textContinuation = unfinished || openQuote;

        var differentColumn = overlap < .25f;
        var structuralSeparator = ContainsStructuralSeparator(tail.SourceText) || ContainsStructuralSeparator(head.SourceText);
        var compactHeaderBody = CompactLength(tail.SourceText) <= 48 && b.Width > a.Width * 1.55f && verticalGapRatio >= .20f;
        var independentCompact = CompactLength(tail.SourceText) <= 32 && CompactLength(head.SourceText) <= 32 &&
            verticalGapRatio >= .78f && widthRatio is >= .65f and <= 1.45f;

        var positive = new List<string>();
        if (adjacent) positive.Add("ADJACENT_ORDERED_SOURCE_LINEAGE");
        if (forward) positive.Add("FORWARD_VERTICAL_FLOW");
        if (closeRhythm) positive.Add("CLOSE_VERTICAL_RHYTHM");
        if (strongOverlap) positive.Add("STRONG_HORIZONTAL_OVERLAP");
        if (centerAligned) positive.Add("HORIZONTAL_CENTERS_ALIGNED");
        if (compatibleHeight) positive.Add("COMPATIBLE_LINE_HEIGHT");
        if (hanging) positive.Add("NEXT_LINE_CENTERED_AND_NARROWER");
        if (unfinished) positive.Add("PREVIOUS_LINE_UNFINISHED");
        if (openQuote) positive.Add("OPEN_QUOTATION_CONTINUES");

        var negative = new List<string>();
        if (!adjacent) negative.Add("NON_ADJACENT_SOURCE_LINEAGE");
        if (!forward) negative.Add("NON_FORWARD_VERTICAL_FLOW");
        if (!closeRhythm) negative.Add("EXCESS_VERTICAL_GAP");
        if (!strongOverlap) negative.Add("LOW_HORIZONTAL_OVERLAP");
        if (!centerAligned) negative.Add("CENTER_ALIGNMENT_BREAK");
        if (!compatibleHeight) negative.Add("TEXT_SCALE_BOUNDARY");
        if (!hanging) negative.Add("NOT_CENTERED_HANGING_SHAPE");
        if (!textContinuation) negative.Add("NO_TEXT_CONTINUATION_SIGNAL");
        if (differentColumn) negative.Add("DIFFERENT_COLUMN_OR_CONTAINER");
        if (structuralSeparator) negative.Add("STRUCTURAL_SEPARATOR_BOUNDARY");
        if (compactHeaderBody) negative.Add("COMPACT_HEADER_BODY_BOUNDARY");
        if (independentCompact) negative.Add("INDEPENDENT_COMPACT_ELEMENTS");

        var accepted = from.BlockId != to.BlockId && adjacent && forward && closeRhythm && strongOverlap &&
            centerAligned && compatibleHeight && hanging && textContinuation && !differentColumn &&
            !structuralSeparator && !compactHeaderBody && !independentCompact;

        var reason = accepted ? "ACCEPT_CENTERED_HANGING_TEXT_CONTINUATION" : PrimaryRejection(
            from.BlockId == to.BlockId, adjacent, forward, differentColumn, centerAligned, closeRhythm,
            compatibleHeight, structuralSeparator, compactHeaderBody, independentCompact, hanging,
            strongOverlap, textContinuation);

        var metrics = new ContinuationMetrics(
            tail.ReadingOrder, head.ReadingOrder, orderDelta,
            Round(verticalGap), Round(verticalGapRatio), Round(overlap), Round(centerDelta), Round(centerDeltaRatio),
            Round(heightRatio), Round(widthRatio), forward, adjacent, closeRhythm, strongOverlap, centerAligned,
            compatibleHeight, hanging, unfinished, openQuote, textContinuation, differentColumn,
            structuralSeparator, compactHeaderBody, independentCompact);

        return new(from.BlockId, to.BlockId,
            orderedFrom.Select(x => x.SourceId).ToArray(), orderedTo.Select(x => x.SourceId).ToArray(),
            tail.SourceId, head.SourceId, accepted, reason, positive, negative, metrics);
    }

    public static ContinuationProposalResult Propose(IReadOnlyList<VisualBlock> blocks)
    {
        var ordered = blocks.OrderBy(MinReadingOrder).ThenBy(x => x.Bounds.Top).ThenBy(x => x.Bounds.Left).ToArray();
        var decisions = new List<ContinuationDecision>();
        foreach (var from in ordered)
        foreach (var to in ordered)
        {
            if (ReferenceEquals(from, to) || MaxReadingOrder(from) + 1 != MinReadingOrder(to)) continue;
            decisions.Add(Evaluate(from, to));
        }

        var accepted = decisions.Where(x => x.IsContinuation).ToArray();
        var adjacency = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var decision in accepted)
        {
            Add(adjacency, decision.FromBlockId, decision.ToBlockId);
            Add(adjacency, decision.ToBlockId, decision.FromBlockId);
        }

        var byId = blocks.ToDictionary(x => x.BlockId, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var proposals = new List<ContinuationGroupProposal>();
        foreach (var start in adjacency.Keys.Order(StringComparer.Ordinal))
        {
            if (!visited.Add(start)) continue;
            var queue = new Queue<string>();
            var component = new List<string>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                component.Add(current);
                foreach (var next in adjacency[current].Order(StringComparer.Ordinal))
                    if (visited.Add(next)) queue.Enqueue(next);
            }

            var componentBlocks = component.Select(x => byId[x]).OrderBy(MinReadingOrder).ThenBy(x => x.BlockId, StringComparer.Ordinal).ToArray();
            var sourceIds = componentBlocks.SelectMany(x => x.Lines)
                .OrderBy(x => x.ReadingOrder).ThenBy(x => x.SourceId, StringComparer.Ordinal)
                .Select(x => x.SourceId).Distinct(StringComparer.Ordinal).ToArray();
            var blockIds = componentBlocks.Select(x => x.BlockId).ToArray();
            var blockSet = blockIds.ToHashSet(StringComparer.Ordinal);
            var reasons = accepted.Where(x => blockSet.Contains(x.FromBlockId) && blockSet.Contains(x.ToBlockId))
                .OrderBy(x => x.Metrics.TailReadingOrder).Select(x => x.Reason).ToArray();
            proposals.Add(new(StableProposalId(sourceIds), blockIds, sourceIds, reasons));
        }

        return new(decisions.OrderBy(x => x.Metrics.TailReadingOrder).ThenBy(x => x.Metrics.HeadReadingOrder).ToArray(),
            proposals.OrderBy(x => x.UnderlyingSourceIds.Count == 0 ? "" : x.UnderlyingSourceIds[0], StringComparer.Ordinal).ToArray());
    }

    private static string PrimaryRejection(bool sameBlock, bool adjacent, bool forward, bool differentColumn,
        bool centerAligned, bool closeRhythm, bool compatibleHeight, bool structuralSeparator,
        bool compactHeaderBody, bool independentCompact, bool hanging, bool strongOverlap, bool textContinuation)
    {
        if (sameBlock) return "REJECT_SAME_CORE_BLOCK";
        if (!forward) return "REJECT_NON_FORWARD_VERTICAL_FLOW";
        if (differentColumn) return "REJECT_DIFFERENT_COLUMN_OR_CONTAINER";
        if (!adjacent) return "REJECT_NON_ADJACENT_SOURCE_LINEAGE";
        if (!centerAligned) return "REJECT_CENTER_ALIGNMENT_BREAK";
        if (!closeRhythm) return "REJECT_EXCESS_VERTICAL_GAP";
        if (!compatibleHeight) return "REJECT_TEXT_SCALE_BOUNDARY";
        if (structuralSeparator) return "REJECT_STRUCTURAL_SEPARATOR_BOUNDARY";
        if (compactHeaderBody) return "REJECT_COMPACT_HEADER_BODY_BOUNDARY";
        if (independentCompact) return "REJECT_INDEPENDENT_COMPACT_ELEMENTS";
        if (!hanging) return "REJECT_NOT_CENTERED_HANGING_SHAPE";
        if (!strongOverlap) return "REJECT_LOW_HORIZONTAL_OVERLAP";
        if (!textContinuation) return "REJECT_NO_TEXT_CONTINUATION_SIGNAL";
        return "REJECT_UNCLASSIFIED_CONSERVATIVE_FALLBACK";
    }

    private static bool HasOpenQuotation(string text)
    {
        var ascii = text.Count(c => c == '"');
        var curlyOpen = text.Count(c => c == '\u201c');
        var curlyClose = text.Count(c => c == '\u201d');
        return ascii % 2 == 1 || curlyOpen > curlyClose;
    }

    private static bool ContainsStructuralSeparator(string text) => text.Contains('|') || text.Contains('\u2502');
    private static int CompactLength(string text) => text.Count(char.IsLetterOrDigit);
    private static float CenterX(RectangleF value) => value.Left + value.Width / 2f;
    private static float CenterY(RectangleF value) => value.Top + value.Height / 2f;
    private static float AxisGap(float a0, float a1, float b0, float b1) => Math.Max(0, Math.Max(a0, b0) - Math.Min(a1, b1));
    private static float HorizontalOverlap(RectangleF a, RectangleF b) =>
        Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) / Math.Max(1, Math.Min(a.Width, b.Width));
    private static float Round(float value) => MathF.Round(value, 4, MidpointRounding.AwayFromZero);
    private static int MinReadingOrder(VisualBlock block) => block.Lines.Min(x => x.ReadingOrder);
    private static int MaxReadingOrder(VisualBlock block) => block.Lines.Max(x => x.ReadingOrder);
    private static void Add(Dictionary<string, HashSet<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var values)) map[key] = values = new(StringComparer.Ordinal);
        values.Add(value);
    }
    private static string StableProposalId(IEnumerable<string> orderedSourceIds)
    {
        var payload = Encoding.UTF8.GetBytes(string.Join("\n", orderedSourceIds));
        return "CTN-" + Convert.ToHexString(SHA256.HashData(payload))[..12];
    }
}
