using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

internal sealed record AiGameChoice(string Id, string Name, string Engine, bool Running)
{
    internal string DisplayName { get; init; } = "";
    internal AiGameArtwork? Artwork { get; init; }
    public override string ToString() => Name;
}

public sealed partial class MainForm
{
    private readonly Dictionary<string, GameInfo> _aiGameIndex = new(StringComparer.Ordinal);
    internal IReadOnlyList<AiGameChoice> AiGames()
    {
        var byPath = _gameSessions.Snapshot().ToDictionary(s => RecentGameStore.Normalize(s.Game.ExePath), s => s.Game, StringComparer.OrdinalIgnoreCase);
        if (_selectedGame is { } selected) byPath[RecentGameStore.Normalize(selected.ExePath)] = selected;
        var store = _recentStoreTask is { IsCompletedSuccessfully: true } loaded ? loaded.Result : _librarySubscribedStore;
        var choices = new List<AiGameChoice>(); _aiGameIndex.Clear();
        foreach (var record in store?.SnapshotAll() ?? [])
        {
            string path = RecentGameStore.Normalize(record.ExePath);
            var game = byPath.GetValueOrDefault(path) ?? new GameInfo(path, record.Name,
                _libraryEngines.GetValueOrDefault(record.ExePath) ?? "", "", "", SupportLevel.Unknown, null);
            _aiGameIndex[record.Id] = game;
            choices.Add(new(record.Id, record.Name, game.Engine, AiKnownRunning(game))
            {
                DisplayName = _libraryMetadata?.Get(record.Id).DisplayName ?? "",
                Artwork = AiArtwork(store!, record)
            });
            byPath.Remove(path);
        }
        foreach (var game in byPath.Values)
        {
            string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RecentGameStore.Normalize(game.ExePath).ToUpperInvariant())));
            _aiGameIndex[id] = game; choices.Add(new(id, game.Name, game.Engine, AiKnownRunning(game)));
        }
        return choices.OrderByDescending(c => c.Running).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    private bool AiKnownRunning(GameInfo game)
    {
        var session = _gameSessions.Find(game.ExePath);
        if (session is not null && _aiObservedProcesses.TryGetValue(RecentGameStore.Normalize(game.ExePath), out var seen)
            && seen.Revision == session.Revision && DateTime.UtcNow - seen.AtUtc < TimeSpan.FromSeconds(10))
            return seen.State == FusionGameProcessState.Running;
        return session?.Process == FusionGameProcessState.Running || ModificationSession(game)?.Connection.Connected == true;
    }
    private GameInfo AiResolveGame(string id)
    {
        if (!_aiGameIndex.TryGetValue(id, out var game)) { AiGames(); if (!_aiGameIndex.TryGetValue(id, out game)) throw new AiNumericFailure("game_missing"); }
        // Prefer the current live descriptor; a library record may predate detection.
        return _gameSessions.Find(game.ExePath)?.Game ?? (SameGame(_selectedGame?.ExePath, game.ExePath) ? _selectedGame! : game);
    }
    internal string AiGameRoot(string id) => Path.GetDirectoryName(Path.GetFullPath(AiResolveGame(id).ExePath)) ?? throw new AiNumericFailure("game_missing");
    // Called only for explicit MOD operations, never from the paint/refresh path.
    // Unknown process identity blocks file modification just like running does.
    internal bool AiGameIsRunning(string id) => FusionAdapterInstaller.ProcessState(AiResolveGame(id)) != FusionGameProcessState.Stopped;
    internal AiNumericTools CreateAiNumericTools(string gameId) => new(new AiEngineNumericBackend(this, AiResolveGame(gameId)));

    private sealed class AiEngineNumericBackend : IAiNumericBackend
    {
        private readonly MainForm owner;
        private readonly GameInfo game;
        private readonly IGameDataConnection? connection;
        private readonly int revision;
        private int lastGeneration;
        private string? saveDirectory;
        private bool rpgReadOnlyVerified;
        public string Engine => game.Engine;
        public bool Supported => game.AdapterId is "renpy" or "rpg-maker-mv" or "rpg-maker-mz";
        public IReadOnlyList<string> Scopes => game.AdapterId == "renpy" ? ["all"] : Supported ? ["all", "gold", "items", "variables"] : [];
        public bool IsCurrent => Supported && !owner.IsDisposed && !owner.Disposing && connection is { Connected: true }
            && owner._gameSessions.Get(game).Revision == revision && owner.ModificationAuthorized(game)
            && ReferenceEquals(owner.ModificationSession(game)?.Connection, connection);
        public string UnavailableCode => !Supported ? "unsupported_engine" : owner.ModificationSession(game)?.Connection.Connected != true
            ? "game_not_connected" : owner.ModificationSession(game)?.Enabled != true ? "modification_disabled" : "session_changed";
        internal AiEngineNumericBackend(MainForm owner, GameInfo game)
        {
            this.owner = owner; this.game = game; revision = owner._gameSessions.Get(game).Revision;
            connection = owner.ModificationSession(game)?.Connection;
        }
        private void Require(CancellationToken cancellation)
        { cancellation.ThrowIfCancellationRequested(); if (!IsCurrent) throw new AiNumericFailure(UnavailableCode); }
        private async Task RequireRpgReadOnlyAsync(CancellationToken cancellation)
        {
            Require(cancellation);
            if (rpgReadOnlyVerified) return;
            JsonElement capability;
            try { capability = await connection!.RequestAsync(new() { ["op"] = "aiNumericCapabilities" }, cancellation); }
            catch (GameDataOperationException)
            {
                Require(cancellation);
                throw new AiNumericFailure("ai_adapter_restart_required");
            }
            Require(cancellation);
            if (!capability.TryGetProperty("protocol", out var protocol) || !protocol.TryGetInt32(out int version) || version != 1
                || !capability.TryGetProperty("readOnlySnapshots", out var safeRead) || safeRead.ValueKind != JsonValueKind.True
                || !capability.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.True
                || !capability.TryGetProperty("generation", out var generation) || !generation.TryGetInt32(out _))
                throw new AiNumericFailure("ai_adapter_restart_required");
            rpgReadOnlyVerified = true;
        }
        public async Task<AiNumericSnapshot> ReadAsync(string scope, CancellationToken cancellation)
        {
            Require(cancellation);
            if (game.AdapterId == "renpy")
            {
                var result = await connection!.RequestAsync(new() { ["op"] = "dataSnapshot" }, cancellation);
                Require(cancellation);
                lastGeneration = result.GetProperty("generation").GetInt32();
                bool ready = !result.GetProperty("mainMenu").GetBoolean();
                saveDirectory = result.TryGetProperty("savedir", out var directory) && directory.ValueKind == JsonValueKind.String ? directory.GetString() : null;
                var rows = AiNumericTools.ReadRenpyRows(result.GetProperty("rows"), out bool numericTruncated);
                return new(connection, revision, lastGeneration, ready,
                    result.GetProperty("truncated").GetBoolean() || numericTruncated, rows);
            }
            // Older running bridges ignore unknown snapshot flags. Probe first so
            // reconnecting an old game cannot accidentally enqueue translation labels.
            await RequireRpgReadOnlyAsync(cancellation);
            var scopes = scope switch { "all" => new[] { "gold", "items", "weapons", "armors", "variables", "actors" },
                "items" => new[] { "items", "weapons", "armors" }, "gold" => ["gold"], "variables" => ["variables"], _ => throw new AiNumericFailure("unsupported_scope") };
            int? generation = null; var collected = new List<AiNumericRow>(); bool truncated = false;
            foreach (string part in scopes)
            {
                Require(cancellation);
                // Small bounded pages; there is no automatic poller or queued whole-memory scan.
                var result = await connection!.RequestAsync(new() { ["op"] = "snapshot", ["scope"] = part, ["offset"] = 0, ["limit"] = 256, ["aiReadOnly"] = true }, cancellation);
                Require(cancellation);
                if (!result.GetProperty("enabled").GetBoolean()) throw new AiNumericFailure("modification_disabled");
                int seen = result.GetProperty("generation").GetInt32();
                if (generation.HasValue && generation != seen) throw new AiNumericFailure("session_changed");
                generation = seen;
                if (!result.GetProperty("ready").GetBoolean()) return new(connection!, revision, seen, false, false, []);
                var source = result.GetProperty("rows"); truncated |= result.GetProperty("total").GetInt32() > source.GetArrayLength();
                foreach (var row in source.EnumerateArray()) if (RpgRow(row) is { } numeric) collected.Add(numeric);
            }
            lastGeneration = generation ?? 0;
            return new(connection!, revision, lastGeneration, true, truncated, collected);
        }
        private static AiNumericRow? RpgRow(JsonElement row)
        {
            if (!row.TryGetProperty("type", out var type) || type.GetString() != "number"
                || !row.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Number
                || !row.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String) return null;
            RenpyNumericFinder.Number number;
            if (value.TryGetInt64(out long integer)) number = new("int", integer, 0);
            else if (value.TryGetDouble(out double floating) && double.IsFinite(floating)) number = new("float", 0, floating);
            else return null;
            string label = row.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : key.GetString()!;
            return new(key.GetString()!, label, number, row.Clone());
        }
        private static JsonNode NumericJson(RenpyNumericFinder.Number value) => value.Kind == "int" ? JsonValue.Create(value.Integer)! : JsonValue.Create(value.Floating)!;
        public async Task WriteAsync(AiNumericRow row, RenpyNumericFinder.Number value, CancellationToken cancellation)
        {
            Require(cancellation);
            string journalRoot = Path.Combine(AiStoragePaths147.Root, "value-changes");
            AppDataPaths.RejectReparsePoints(journalRoot);
            // Keep the exact previous value before attempting the write. No address or key leaves this local journal.
            string journal = Path.Combine(journalRoot, Guid.NewGuid().ToString("N") + ".json");
            if (game.AdapterId == "renpy")
            {
                if (value.Kind == "int" && Math.Abs((decimal)value.Integer) > 9007199254740991m
                    || value.Kind == "float" && Math.Abs(value.Floating) > 9007199254740991d) throw new AiNumericFailure("invalid_number");
                if (!owner._renpyDataBackups.ContainsKey(connection!))
                {
                    if (string.IsNullOrWhiteSpace(saveDirectory)) throw new AiNumericFailure("backup_unavailable");
                    string backup = Path.Combine(RenpyGameAdapter.Storage(game.ExePath), "save-backups", "ai-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture));
                    await Task.Run(() => BackupRenpySaveLocations(game.ExePath, saveDirectory, backup), cancellation);
                    Require(cancellation); owner._renpyDataBackups[connection!] = backup;
                }
                // Backup and another reader can invalidate a Ren'Py entry. Re-read; never reuse stale entry tokens.
                int expectedGeneration = lastGeneration;
                var fresh = await ReadAsync("all", cancellation);
                var current = fresh.Rows.FirstOrDefault(r => r.Key == row.Key);
                if (!fresh.Ready || fresh.Generation != expectedGeneration || current is null
                    || current.Number.Kind != row.Number.Kind || !current.Number.EqualsValue(row.Number)) throw new AiNumericFailure("value_changed");
                Require(cancellation);
                PortableDataStorage.WriteJson(journal, new { game = game.ExePath, generation = lastGeneration, key = row.Key,
                    oldValue = AiNumericTools.ValueText(row.Number), newValue = AiNumericTools.ValueText(value), status = "submitted", utc = DateTime.UtcNow });
                owner._renpySnapshotsCache.Invalidate(connection!);
                await connection!.RequestAsync(new() { ["op"] = "dataSet", ["entry"] = current.Source.GetProperty("id").GetString(), ["value"] = NumericJson(value) }, cancellation);
                Require(cancellation); return;
            }
            if (row.Source.TryGetProperty("min", out var min) && min.ValueKind == JsonValueKind.Number && double.TryParse(AiNumericTools.ValueText(value), NumberStyles.Float, CultureInfo.InvariantCulture, out double numeric) && numeric < min.GetDouble()
                || row.Source.TryGetProperty("max", out var max) && max.ValueKind == JsonValueKind.Number && double.TryParse(AiNumericTools.ValueText(value), NumberStyles.Float, CultureInfo.InvariantCulture, out numeric) && numeric > max.GetDouble())
                throw new AiNumericFailure("invalid_number");
            // RPG's existing bridge validates generation but has no compare-and-set.
            // Read immediately before set, then the caller performs independent readback.
            var described = await connection!.RequestAsync(new() { ["op"] = "describe", ["generation"] = lastGeneration, ["aiReadOnly"] = true,
                ["keys"] = new JsonArray(JsonValue.Create(row.Key)) }, cancellation);
            Require(cancellation);
            var actual = described.GetProperty("rows").EnumerateArray().Select(RpgRow).FirstOrDefault(r => r?.Key == row.Key);
            if (actual is null || actual.Number.Kind != row.Number.Kind || !actual.Number.EqualsValue(row.Number)) throw new AiNumericFailure("value_changed");
            PortableDataStorage.WriteJson(journal, new { game = game.ExePath, generation = lastGeneration, key = row.Key,
                oldValue = AiNumericTools.ValueText(row.Number), newValue = AiNumericTools.ValueText(value), status = "submitted", utc = DateTime.UtcNow });
            Require(cancellation);
            await connection.RequestAsync(new() { ["op"] = "set", ["generation"] = lastGeneration, ["key"] = row.Key, ["value"] = NumericJson(value) }, cancellation);
            Require(cancellation);
        }
    }
}
