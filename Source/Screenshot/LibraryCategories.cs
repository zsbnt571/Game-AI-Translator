namespace ScreenshotTranslationUiTester;

// Present legacy virtual collections as categories without rewriting the user's
// preferences or game annotations on upgrade. Real filesystem roots stay separate.
internal static class LibraryCategories
{
    internal static LibraryFolder[] LegacyCollections(LibraryPreferences? preferences) =>
        (preferences?.Folders ?? []).Where(f => string.IsNullOrWhiteSpace(f.Root)).ToArray();
    internal static string[] Names(LibraryPreferences preferences, IEnumerable<RecentGame> games, LibraryMetadata metadata) =>
        (preferences.Categories ?? []).Concat(LegacyCollections(preferences).Select(f => f.Name))
        .Concat(games.SelectMany(g => Tokens(metadata.Get(g.Id).Tags))).Distinct(StringComparer.Ordinal).OrderBy(n => n).ToArray();
    internal static string[] Tokens(string tags) => tags.Split([',', '，', ';', '；'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    internal static bool Contains(LibraryPreferences? preferences, RecentGame game, LibraryAnnotation note, string name) =>
        Tokens(note.Tags).Contains(name, StringComparer.Ordinal) || LegacyCollections(preferences).Any(f => f.Name == name && LibraryFolders.Contains(f, game));
}
