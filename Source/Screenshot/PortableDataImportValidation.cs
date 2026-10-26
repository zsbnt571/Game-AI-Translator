using System.Text.Json;

namespace ScreenshotTranslationUiTester;

/// <summary>Read-only startup-shape checks. Do not construct stores, normalize, migrate, or save here.</summary>
internal static class PortableDataImportValidation
{
    internal static void Validate(string root)
    {
        try
        {
            using (var document = Read(root, "settings.json", 16 * 1024 * 1024))
                if (document is not null)
                {
                    var settings = document.RootElement.Deserialize<ApiSettings>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (settings is null || settings.ApiKey is null) Fail();
                }
            using (var document = Read(root, "fusion-settings.json", 16 * 1024 * 1024))
                if (document is not null)
                {
                    var state = document.RootElement.Deserialize<FusionState>();
                    if (state is null || state.Profiles is null || state.GameProfiles is null || state.ProfileInfo is null ||
                        state.ArchivedTemplates is null || state.MigrationNotes is null || string.IsNullOrWhiteSpace(state.CommonProfile)) Fail();
                    foreach (var profile in state!.Profiles.Values.Concat(state.ArchivedTemplates.Values))
                        if (profile is null || profile.ApiKey is null) Fail();
                    foreach (var info in state.ProfileInfo.Values)
                        if (info is null || info.Name is null || info.Origin is null) Fail();
                    if (state.GameProfiles.Values.Any(value => value is null) || state.MigrationNotes.Any(value => value is null)) Fail();
                    if (state.SchemaVersion >= 2)
                    {
                        foreach (string? id in new[] { state.CommonProfile, state.ScreenshotProfile, state.EmbeddedProfile })
                            if (id is not null && !state.Profiles.ContainsKey(id)) Fail();
                        foreach (string id in state.GameProfiles.Values)
                            if (!state.Profiles.ContainsKey(id)) Fail();
                    }
                }
            using (var document = Read(root, "library-preferences.json", 4 * 1024 * 1024))
                if (document is not null)
                {
                    var preferences = document.RootElement.Deserialize<LibraryPreferences>();
                    if (preferences is null) Fail();
                    if (preferences!.Folders?.Any(folder => folder is null || string.IsNullOrWhiteSpace(folder.Id) || string.IsNullOrWhiteSpace(folder.Name)) == true) Fail();
                }
            using (var document = Read(root, "recent-games-v1\\games.json", 8_000_000))
                if (document is not null)
                {
                    var games = document.RootElement.Deserialize<GameDocument>();
                    if (games is null || games.Version is not (1 or 2 or 3) || games.Games is null ||
                        games.Games.Length > 10000 || games.RecentLimit is < 1 or > 1000) Fail();
                    var ids = new HashSet<string>(StringComparer.Ordinal);
                    var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var game in games!.Games!)
                    {
                        if (game is null || !Guid.TryParseExact(game.Id, "N", out _) || string.IsNullOrWhiteSpace(game.Name) ||
                            !Path.IsPathFullyQualified(game.ExePath) || !game.ExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                            !ids.Add(game.Id) || !paths.Add(Path.GetFullPath(game.ExePath))) Fail();
                        // Game executable paths intentionally retain their original absolute
                        // locations. Artwork paths are files within this copied collection.
                        RelativeFile(game!.Icon, true); RelativeFile(game.Cover, true);
                    }
                }
            using (var document = Read(root, "history\\index.json", 32 * 1024 * 1024))
                if (document is not null)
                {
                    var entries = document.RootElement.Deserialize<HistoryEntry[]>();
                    if (entries is null || entries.Length > 100000) Fail();
                    foreach (var entry in entries!)
                    {
                        if (entry is null || string.IsNullOrWhiteSpace(entry.Id)) Fail();
                        RelativeFile(entry!.ImageFile, false); RelativeFile(entry.ThumbnailFile, false);
                        RelativeFile(entry.TranslatedImageFile, true);
                    }
                }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        { throw new PortableDataMigration.MigrationException("InvalidConfiguration"); }
    }

    private sealed record GameDocument(int Version, RecentGame[]? Games, int RecentLimit = 20);
    private sealed record HistoryEntry(string Id, DateTimeOffset Timestamp, OcrEngineKind Engine,
        string SourceSummary, string TranslationSummary, string TargetLanguage, string ThumbnailFile,
        string ImageFile, string? SourceText = null, string? TranslationText = null,
        string? TranslatedImageFile = null, HistoryCoreSnapshot? CoreSnapshot = null);

    private static JsonDocument? Read(string root, string relative, int limit)
    {
        string path = Path.Combine(root, relative);
        if (!File.Exists(path)) return null;
        AppDataPaths.RejectReparsePoints(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > limit) Fail();
        byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        ReadOnlyMemory<byte> json = bytes;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) json = json[3..];
        return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
    }

    private static void RelativeFile(string? path, bool optional)
    {
        if (optional && path is null) return;
        if (string.IsNullOrWhiteSpace(path) || path is "." or ".." || path.IndexOfAny(['\\', '/', ':']) >= 0) Fail();
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail() => throw new PortableDataMigration.MigrationException("InvalidConfiguration");
}
