using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

internal sealed record AiNumericRow(string Key, string Name, RenpyNumericFinder.Number Number, JsonElement Source);
internal sealed record AiNumericSnapshot(object Identity, int Revision, int Generation, bool Ready, bool Truncated, IReadOnlyList<AiNumericRow> Rows);
internal sealed record AiNumericProposal(string Id, string CandidateId, string Name, string OldValue, string NewValue);
internal interface IAiNumericBackend
{
    string Engine { get; }
    bool Supported { get; }
    bool IsCurrent { get; }
    string UnavailableCode { get; }
    IReadOnlyList<string> Scopes { get; }
    Task<AiNumericSnapshot> ReadAsync(string scope, CancellationToken cancellation);
    Task WriteAsync(AiNumericRow row, RenpyNumericFinder.Number value, CancellationToken cancellation);
}

internal sealed class AiNumericFailure(string code) : Exception(code)
{
    internal string Code { get; } = code;
}

// No game names, address discovery, model-generated code or translation state.
// The model can prepare a change; only a separate, local confirmation can commit.
internal sealed class AiNumericTools : IDisposable
{
    private sealed record Candidate(string Id, AiNumericRow Row, int Changes);
    private sealed record Approval(AiNumericProposal View, RenpyNumericFinder.Number Before,
        RenpyNumericFinder.Number After, string Key, DateTime ExpiresUtc, int Epoch);
    private readonly IAiNumericBackend backend;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Func<DateTime> clock;
    private CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, Candidate> candidates = new(StringComparer.Ordinal);
    private object? identity;
    private int revision, generation, epoch, rounds, initialCount;
    private string scope = "all";
    private bool active, truncated, disposed;
    private DateTime lastReadUtc;
    private Approval? approval;
    internal bool IsCurrent => !disposed && backend.IsCurrent;
    internal AiNumericProposal? PendingChange => approval is { } p && p.Epoch == epoch && p.ExpiresUtc >= clock() && backend.IsCurrent ? p.View : null;
    internal AiNumericTools(IAiNumericBackend backend, Func<DateTime>? clock = null)
    { this.backend = backend; this.clock = clock ?? (() => DateTime.UtcNow); }

    internal static JsonObject Error(string code) => new() { ["ok"] = false, ["code"] = code };
    private static JsonObject Success(string code) => new() { ["ok"] = true, ["code"] = code };
    internal static string ValueText(RenpyNumericFinder.Number value) => value.Kind == "int"
        ? value.Integer.ToString(CultureInfo.InvariantCulture) : value.Floating.ToString("R", CultureInfo.InvariantCulture);
    private static bool Exact(RenpyNumericFinder.Number a, RenpyNumericFinder.Number b) => a.Kind == b.Kind && a.EqualsValue(b);
    internal static IReadOnlyList<AiNumericRow> ReadRenpyRows(JsonElement source, out bool truncated)
    {
        var rows = new List<AiNumericRow>(); truncated = false;
        // Existing gallery booleans come first. Budget numbers after filtering so
        // a large gallery does not hide the money/inventory values that follow.
        foreach (var row in source.EnumerateArray())
            if (RenpyNumericFinder.TryNumber(row, out var value) && row.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String && row.TryGetProperty("id", out var entry) && entry.ValueKind == JsonValueKind.String)
            {
                if (rows.Count >= 2000) { truncated = true; break; }
                rows.Add(new(name.GetString()!, name.GetString()!, value, row.Clone()));
            }
        return rows;
    }
    private static void Fields(JsonObject args, params string[] allowed)
    {
        if (args.Any(p => !allowed.Contains(p.Key, StringComparer.Ordinal))) throw new AiNumericFailure("invalid_arguments");
    }
    private static string? String(JsonObject args, string key, bool required = false)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is null)
        { if (required) throw new AiNumericFailure("invalid_arguments"); return null; }
        if (node is not JsonValue scalar || !scalar.TryGetValue<string>(out var text) || text.Length > 128)
            throw new AiNumericFailure("invalid_arguments");
        return text;
    }
    private static int Integer(JsonObject args, string key, int fallback, int minimum, int maximum)
    {
        if (!args.TryGetPropertyValue(key, out var node)) return fallback;
        if (node is not JsonValue scalar || !scalar.TryGetValue<int>(out var number) || number < minimum || number > maximum)
            throw new AiNumericFailure("invalid_arguments");
        return number;
    }
    private static RenpyNumericFinder.Number? Number(JsonObject args, string key, bool required)
    {
        var text = String(args, key, required)?.Trim();
        if (!required && string.IsNullOrEmpty(text)) return null;
        if (text is null || !RenpyNumericFinder.TryQuery(text, out var value)) throw new AiNumericFailure("invalid_number");
        return value;
    }
    private void Reset()
    { approval = null; candidates.Clear(); active = false; identity = null; rounds = 0; initialCount = 0; truncated = false; }
    internal void Stop()
    {
        if (disposed) return;
        epoch++; approval = null; lifetime.Cancel(); lifetime.Dispose(); lifetime = new(); Reset();
    }
    internal void Invalidate() => Stop();
    public void Dispose() { if (disposed) return; Stop(); disposed = true; lifetime.Cancel(); lifetime.Dispose(); }
    private void EnsureCurrent(int expectedEpoch, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (disposed || expectedEpoch != epoch) throw new AiNumericFailure("session_changed");
        if (!backend.IsCurrent) { Reset(); throw new AiNumericFailure(backend.UnavailableCode); }
    }
    private async Task<AiNumericSnapshot> ReadAsync(int expectedEpoch, bool starting, CancellationToken cancellation)
    {
        EnsureCurrent(expectedEpoch, cancellation);
        // Each user action makes one bounded snapshot; no polling or background scan.
        var delay = TimeSpan.FromMilliseconds(500) - (clock() - lastReadUtc);
        if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellation);
        EnsureCurrent(expectedEpoch, cancellation); lastReadUtc = clock();
        var snapshot = await backend.ReadAsync(scope, cancellation);
        EnsureCurrent(expectedEpoch, cancellation);
        if (!snapshot.Ready) { Reset(); throw new AiNumericFailure("game_not_ready"); }
        if (!starting && (!ReferenceEquals(identity, snapshot.Identity) || revision != snapshot.Revision || generation != snapshot.Generation))
        { Reset(); throw new AiNumericFailure("session_changed"); }
        return snapshot;
    }
    private static Dictionary<string, AiNumericRow> Unique(IReadOnlyList<AiNumericRow> rows)
    {
        var unique = new Dictionary<string, AiNumericRow>(StringComparer.Ordinal);
        var duplicate = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.Take(2000))
        {
            if (string.IsNullOrEmpty(row.Key) || row.Key.Length > 2048 || duplicate.Contains(row.Key)) continue;
            if (row.Number.Kind is not ("int" or "float") || row.Number.Kind == "float" && !double.IsFinite(row.Number.Floating)) continue;
            // User variables can contain credentials; never expose their names/values as candidates.
            if (new[] { "password", "secret", "api_key", "apikey", "authorization", "access_token", "refresh_token", "cookie" }
                .Any(word => row.Key.Contains(word, StringComparison.OrdinalIgnoreCase) || row.Name.Contains(word, StringComparison.OrdinalIgnoreCase))) continue;
            if (!unique.TryAdd(row.Key, row)) { unique.Remove(row.Key); duplicate.Add(row.Key); }
        }
        return unique;
    }
    private JsonObject Results(int offset, int limit)
    {
        var result = Success("scan_results");
        result["candidateCount"] = candidates.Count; result["initialCount"] = initialCount;
        result["round"] = rounds; result["truncated"] = truncated; result["scope"] = scope;
        result["more"] = offset + limit < candidates.Count;
        var list = new JsonArray();
        foreach (var candidate in candidates.Values.Skip(offset).Take(limit))
            list.Add(new JsonObject { ["candidateId"] = candidate.Id,
                ["name"] = new string(candidate.Row.Name.Where(c => !char.IsControl(c)).Take(120).ToArray()),
                ["type"] = candidate.Row.Number.Kind, ["value"] = ValueText(candidate.Row.Number),
                ["observedChanges"] = candidate.Changes });
        result["candidates"] = list; return result;
    }
    internal async Task<JsonObject> ExecuteAsync(string name, JsonObject arguments, CancellationToken cancellation = default)
    {
        if (disposed) return Error("session_changed");
        try { if (!await gate.WaitAsync(0, cancellation)) return Error("busy"); }
        catch (OperationCanceledException) { return Error("stopped"); }
        int expectedEpoch = epoch;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        try
        {
            var token = linked.Token;
            if (name == "capabilities")
            {
                Fields(arguments); var result = Success("capabilities"); result["engine"] = backend.Engine;
                result["supported"] = backend.Supported; result["connected"] = backend.IsCurrent;
                result["scanBackend"] = "engine_variables"; result["memoryScan"] = false;
                result["requiresModificationEnabled"] = true; result["requiresLocalConfirmation"] = true;
                result["maximumCandidatesReturned"] = 20; result["maximumRounds"] = 10;
                result["unavailableCode"] = backend.IsCurrent ? "" : backend.UnavailableCode;
                result["scopes"] = new JsonArray(backend.Scopes.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
                return result;
            }
            if (name is not ("scan_start" or "scan_refine" or "scan_results" or "propose_value_change")) return Error("tool_not_allowed");
            if (!backend.Supported) return Error("unsupported_engine");
            EnsureCurrent(expectedEpoch, token);
            if (name == "scan_start")
            {
                Fields(arguments, "value", "scope"); var wanted = Number(arguments, "value", false);
                string requestedScope = String(arguments, "scope") ?? "all";
                if (!backend.Scopes.Contains(requestedScope, StringComparer.Ordinal)) return Error("unsupported_scope");
                Reset(); scope = requestedScope;
                var snapshot = await ReadAsync(expectedEpoch, true, token);
                identity = snapshot.Identity; revision = snapshot.Revision; generation = snapshot.Generation;
                truncated = snapshot.Truncated || snapshot.Rows.Count > 2000;
                foreach (var row in Unique(snapshot.Rows).Values)
                    if (wanted is null || row.Number.EqualsValue(wanted.Value))
                        candidates.Add(row.Key, new(Guid.NewGuid().ToString("N"), row, 0));
                initialCount = candidates.Count; active = true; return Results(0, 20);
            }
            if (!active) return Error("scan_required");
            if (name == "scan_results")
            {
                Fields(arguments, "offset", "limit");
                return Results(Integer(arguments, "offset", 0, 0, 2000), Integer(arguments, "limit", 20, 1, 20));
            }
            if (name == "scan_refine")
            {
                Fields(arguments, "comparison", "value");
                string comparison = String(arguments, "comparison", true)!;
                if (comparison is not ("equal" or "increased" or "decreased" or "changed" or "unchanged")) return Error("invalid_arguments");
                var wanted = Number(arguments, "value", comparison == "equal");
                if (rounds >= 10) return Error("round_limit");
                approval = null;
                var snapshot = await ReadAsync(expectedEpoch, false, token); var rows = Unique(snapshot.Rows);
                truncated |= snapshot.Truncated || snapshot.Rows.Count > 2000;
                var next = new Dictionary<string, Candidate>(StringComparer.Ordinal);
                foreach (var old in candidates.Values)
                {
                    if (!rows.TryGetValue(old.Row.Key, out var row) || row.Number.Kind != old.Row.Number.Kind) continue;
                    int change = row.Number.CompareSameKind(old.Row.Number);
                    bool match = comparison switch { "equal" => row.Number.EqualsValue(wanted!.Value), "increased" => change > 0,
                        "decreased" => change < 0, "changed" => change != 0, _ => change == 0 };
                    if (match) next.Add(row.Key, old with { Row = row, Changes = old.Changes + (change == 0 ? 0 : 1) });
                }
                candidates.Clear(); foreach (var row in next) candidates.Add(row.Key, row.Value);
                rounds++; return Results(0, 20);
            }
            Fields(arguments, "candidateId", "value"); string id = String(arguments, "candidateId", true)!;
            var candidate = candidates.Values.FirstOrDefault(c => c.Id == id);
            if (candidate is null) return Error("candidate_missing");
            var after = Number(arguments, "value", true)!.Value;
            if (candidate.Row.Number.Kind == "int" && after.Kind != "int") return Error("invalid_number");
            if (candidate.Row.Number.Kind == "float" && after.Kind == "int")
            {
                if (Math.Abs((decimal)after.Integer) > 9007199254740991m) return Error("invalid_number");
                after = new("float", 0, after.Integer);
            }
            if (candidate.Changes < 2) return Error("verification_required");
            approval = null;
            var fresh = await ReadAsync(expectedEpoch, false, token);
            if (!Unique(fresh.Rows).TryGetValue(candidate.Row.Key, out var current) || !Exact(current.Number, candidate.Row.Number))
                return Error("value_changed");
            if (Exact(current.Number, after)) return Error("no_change");
            var view = new AiNumericProposal(Guid.NewGuid().ToString("N"), candidate.Id,
                new string(current.Name.Where(c => !char.IsControl(c)).Take(120).ToArray()), ValueText(current.Number), ValueText(after));
            approval = new(view, current.Number, after, current.Key, clock().AddMinutes(3), epoch);
            var response = Success("confirmation_required");
            response["proposalId"] = view.Id; response["candidateId"] = view.CandidateId;
            response["name"] = view.Name; response["oldValue"] = view.OldValue; response["newValue"] = view.NewValue;
            response["applied"] = false; return response;
        }
        catch (AiNumericFailure e) { return Error(e.Code); }
        catch (OperationCanceledException) { approval = null; return Error("stopped"); }
        catch (Exception) { approval = null; return Error("read_failed"); }
        finally { gate.Release(); }
    }
    // Never registered as a model tool. UI must pass the exact ID it displayed.
    internal async Task<JsonObject> CommitAsync(string proposalId, CancellationToken cancellation = default)
    {
        if (disposed) return Error("session_changed");
        try { if (!await gate.WaitAsync(0, cancellation)) return Error("busy"); }
        catch (OperationCanceledException) { return Error("stopped"); }
        int expectedEpoch = epoch; bool submitted = false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        try
        {
            var proposal = approval;
            if (proposal is null || proposal.View.Id != proposalId || proposal.Epoch != epoch || proposal.ExpiresUtc < clock())
                return Error("confirmation_expired");
            approval = null; var token = linked.Token;
            var snapshot = await ReadAsync(expectedEpoch, false, token);
            if (!Unique(snapshot.Rows).TryGetValue(proposal.Key, out var current) || !Exact(current.Number, proposal.Before))
                return Error("value_changed");
            EnsureCurrent(expectedEpoch, token);
            submitted = true;
            await backend.WriteAsync(current, proposal.After, token);
            var readback = await ReadAsync(expectedEpoch, false, token);
            if (!Unique(readback.Rows).TryGetValue(proposal.Key, out var actual) || !Exact(actual.Number, proposal.After))
                return Error("write_unverified");
            var result = Success("value_changed_successfully"); result["oldValue"] = proposal.View.OldValue;
            result["newValue"] = ValueText(actual.Number); result["candidateId"] = proposal.View.CandidateId;
            result["applied"] = true; result["readbackVerified"] = true;
            Reset(); return result;
        }
        catch (AiNumericFailure e) { return Error(submitted ? "write_unverified" : e.Code); }
        catch (OperationCanceledException) { return Error(submitted ? "write_unverified" : "stopped"); }
        catch (Exception) { return Error(submitted ? "write_unverified" : "read_failed"); }
        finally { gate.Release(); }
    }
}
