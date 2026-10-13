namespace ScreenshotTranslationUiTester;

internal static class UnityStartupCache
{
    internal static Dictionary<string,string> Select(IReadOnlyDictionary<string,string> entries,SourceLanguageMode mode)
        =>EmbeddedStartupCache.Select(entries,mode);
}

internal static class EmbeddedStartupCache
{
    // Auto has no catalogue decision before the game starts. The Unity bridge
    // keeps these entries in a separate exact-only map: only an actual control's
    // complete original text can use one, never a fuzzy/substring match.
    internal static Dictionary<string,string> Select(IReadOnlyDictionary<string,string> entries,SourceLanguageMode mode)
    {
        var policy=new EmbeddedSourceLanguagePolicy(mode);
        return entries.Where(p=>!string.IsNullOrWhiteSpace(p.Key)&&!string.IsNullOrWhiteSpace(p.Value)
            &&p.Key.Length<=6000&&p.Value.Length<=12000&&!p.Key.Contains('\0')&&!p.Value.Contains('\0')
            &&(!policy.Resolved||policy.Allows(p.Key)))
            .ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
    }
}
