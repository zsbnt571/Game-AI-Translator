using System.Text.Json;

namespace ScreenshotTranslationUiTester;

public enum TranslationAllocationStatus { Valid, LegacyWithoutAllocation, Invalid, Unrecoverable }

public sealed record TranslationAllocationValidation(
    TranslationAllocationStatus Status, string Reason,
    Dictionary<string, TranslationAllocationSegment[]> Allocations)
{
    public bool IsValid => Status == TranslationAllocationStatus.Valid;
}

public static class TranslationAllocationContractV1
{
    public static TranslationAllocationValidation Validate(
        IReadOnlyList<TranslationItem> items,
        IReadOnlyDictionary<string, string> translations,
        IReadOnlyDictionary<string, TranslationAllocationSegment[]>? allocations,
        bool parserRecovered = false)
    {
        if (allocations is null || allocations.Count == 0)
            return new(TranslationAllocationStatus.LegacyWithoutAllocation,
                "LEGACY_TRANSLATION_WITHOUT_ALLOCATION", new(StringComparer.Ordinal));
        var expected = items.Where(x => !string.IsNullOrWhiteSpace(x.Text))
            .ToDictionary(x => x.Id, StringComparer.Ordinal);
        var extra = allocations.Keys.Except(expected.Keys, StringComparer.Ordinal).ToArray();
        if (extra.Length > 0) return Invalid($"unknown TranslationUnitId: {string.Join(',', extra)}");
        var missingUnits = expected.Keys.Except(allocations.Keys, StringComparer.Ordinal).ToArray();
        if (missingUnits.Length > 0) return Invalid($"missing allocation unit: {string.Join(',', missingUnits)}");
        var normalized = new Dictionary<string, TranslationAllocationSegment[]>(StringComparer.Ordinal);
        foreach (var (unitId, item) in expected)
        {
            if (!translations.TryGetValue(unitId, out var full)) return Invalid($"missing full translation: {unitId}");
            var segments = allocations[unitId].OrderBy(x => x.Sequence).ToArray();
            if (segments.Length == 0) return Invalid($"mapping missing: {unitId}");
            if (!segments.Select(x => x.Sequence).SequenceEqual(Enumerable.Range(0, segments.Length)))
                return Invalid($"invalid or duplicate sequence: {unitId}");
            var expectedSources = item.StableSourceIds.ToHashSet(StringComparer.Ordinal);
            if (expectedSources.Count == 0) return Invalid($"source ids unavailable: {unitId}");
            var ownership = new HashSet<string>(StringComparer.Ordinal);
            foreach (var segment in segments)
            {
                if (!string.Equals(segment.TranslationUnitId, unitId, StringComparison.Ordinal))
                    return Invalid($"segment unit mismatch: {unitId}");
                if (string.IsNullOrEmpty(segment.TranslatedText)) return Invalid($"missing translated span: {unitId}");
                if (segment.SourceIds.Count == 0) return Invalid($"segment source ids missing: {unitId}");
                foreach (var sourceId in segment.SourceIds)
                {
                    if (!expectedSources.Contains(sourceId)) return Invalid($"unknown SourceId: {sourceId}");
                    if (!ownership.Add(sourceId)) return Invalid($"duplicate illegal ownership: {sourceId}");
                }
            }
            var missingSources = expectedSources.Except(ownership, StringComparer.Ordinal).ToArray();
            if (missingSources.Length > 0) return Invalid($"missing SourceId: {string.Join(',', missingSources)}");
            var reconstructed = string.Concat(segments.Select(x => x.TranslatedText));
            if (!Equivalent(full, reconstructed)) return Invalid($"target span coverage mismatch: {unitId}");
            var confidence = parserRecovered ? .95 : 1.0;
            normalized[unitId] = segments.Select((x, index) => x with
            {
                SegmentId = string.IsNullOrWhiteSpace(x.SegmentId) ? $"{unitId}-A{index + 1:000}" : x.SegmentId,
                MappingConfidence = confidence
            }).ToArray();
        }
        return new(TranslationAllocationStatus.Valid, "PASS", normalized);
    }

    public static (Dictionary<string,string> Translations,
        Dictionary<string,TranslationAllocationSegment[]> Allocations, bool Recovered, bool LegacyFormat)
        ParseContent(string content)
    {
        Exception? last = null;
        foreach (var candidate in Candidates(content))
        {
            try { return ParseJson(candidate, !string.Equals(candidate, content.TrimStart('\uFEFF'), StringComparison.Ordinal)); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { last = ex; }
        }
        throw new BatchJsonException("ALLOCATION_UNRECOVERABLE", last);
    }

    private static (Dictionary<string,string>, Dictionary<string,TranslationAllocationSegment[]>, bool, bool)
        ParseJson(string json, bool recovered)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("translations", out var wrapped)) root = wrapped;
        var translations = new Dictionary<string,string>(StringComparer.Ordinal);
        var allocations = new Dictionary<string,TranslationAllocationSegment[]>(StringComparer.Ordinal);
        if (root.ValueKind == JsonValueKind.Object && root.EnumerateObject().All(x => x.Value.ValueKind == JsonValueKind.String))
        {
            foreach (var p in root.EnumerateObject())
                if (!translations.TryAdd(p.Name, p.Value.GetString() ?? ""))
                    throw new JsonException($"duplicate translation id: {p.Name}");
            return (translations, allocations, recovered, true);
        }
        if (root.ValueKind != JsonValueKind.Array) throw new JsonException("translations must be an array or legacy object");
        foreach (var item in root.EnumerateArray())
        {
            var id = String(item, "id", "translationUnitId", "translation_unit_id");
            var full = String(item, "translation", "translatedText", "fullTranslatedText");
            if (!translations.TryAdd(id, full)) throw new JsonException($"duplicate translation id: {id}");
            if (!TryProperty(item, out var array, "allocations", "segments", "allocationSegments")) continue;
            if (array.ValueKind != JsonValueKind.Array) throw new JsonException("allocations must be an array");
            var list = new List<TranslationAllocationSegment>();
            foreach (var segment in array.EnumerateArray())
            {
                var sourceIds = Property(segment, "sourceIds", "source_ids").EnumerateArray()
                    .Select(x => x.GetString() ?? "").ToArray();
                list.Add(new(id, sourceIds, String(segment, "translatedText", "text", "translation"),
                    Int(segment, "sequence", "order"), 0, OptionalString(segment, "segmentId", "segment_id")));
            }
            allocations[id] = list.ToArray();
        }
        return (translations, allocations, recovered, false);
    }

    private static IEnumerable<string> Candidates(string content)
    {
        var clean = content.TrimStart('\uFEFF').Trim(); yield return clean;
        if (clean.StartsWith("```", StringComparison.Ordinal))
        {
            var first = clean.IndexOf('\n'); var last = clean.LastIndexOf("```", StringComparison.Ordinal);
            if (first >= 0 && last > first) yield return clean[(first + 1)..last].Trim();
        }
        var start = clean.IndexOfAny(['{','[']); var end = Math.Max(clean.LastIndexOf('}'), clean.LastIndexOf(']'));
        if (start >= 0 && end > start) yield return clean[start..(end + 1)];
    }
    private static string Normalize(string value) => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static bool Equivalent(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);
    private static TranslationAllocationValidation Invalid(string reason) => new(TranslationAllocationStatus.Invalid, reason, new(StringComparer.Ordinal));
    private static bool TryProperty(JsonElement e, out JsonElement value, params string[] names)
    { foreach (var n in names) if (e.TryGetProperty(n, out value)) return true; value = default; return false; }
    private static JsonElement Property(JsonElement e, params string[] names) =>
        TryProperty(e, out var value, names) ? value : throw new JsonException($"missing {names[0]}");
    private static string String(JsonElement e, params string[] names) => Property(e,names).GetString() ?? throw new JsonException($"empty {names[0]}");
    private static string OptionalString(JsonElement e, params string[] names) => TryProperty(e,out var v,names) && v.ValueKind==JsonValueKind.String ? v.GetString() ?? "" : "";
    private static int Int(JsonElement e, params string[] names) => Property(e,names).GetInt32();
}
