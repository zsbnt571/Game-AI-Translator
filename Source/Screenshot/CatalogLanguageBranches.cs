namespace ScreenshotTranslationUiTester;

// Language belongs to a resource branch, not to the alphabet of its contents.
// An English label in a Japanese resource therefore keeps Japanese provenance.
internal sealed record CatalogLanguageBranch(string Source,string Resource,string Language);

internal sealed class CatalogLanguageBranches
{
    private readonly Dictionary<string,string[]> languages;
    internal CatalogLanguageBranches(IEnumerable<CatalogLanguageBranch> branches)
        =>languages=branches.GroupBy(b=>b.Source,StringComparer.Ordinal).ToDictionary(g=>g.Key,
            g=>g.Select(b=>b.Language).Distinct(StringComparer.Ordinal).ToArray(),StringComparer.Ordinal);
    internal bool Contains(string source)=>languages.ContainsKey(source);
    internal bool Allows(string source,string declaredActiveLocale="",bool observed=false)
    {
        if(observed||!languages.TryGetValue(source,out var available))return true;
        // A source-language setting, remembered guess, or one visible Latin word
        // is not proof of which parallel resource is currently selected.
        if(declaredActiveLocale.Length==0)return false;
        string locale=GameSourceLanguageTracker.CanonicalLocale(declaredActiveLocale);
        return locale.Length>0&&available.Any(l=>l.Length>0&&GameSourceLanguageTracker.CanonicalLocale(l)==locale);
    }
    internal static string RowLocale(string name)=>name.Trim() switch
    {
        "日本語"=>"ja", "简体中文" or "簡体中文"=>"zh", "繁體中文" or "繁体中文"=>"zh-Hant",
        "한국어"=>"ko", "Русский" or "русский"=>"ru", "Español" or "español"=>"es",
        "Français" or "français"=>"fr", "Deutsch" or "deutsch"=>"de",
        _=>UnityTextResourceLanguageSelection.Locale(name)??""
    };
    internal static CatalogLanguageBranch[] FromEntries(IEnumerable<ExperimentalRpgCatalog.Entry> entries)
    {
        var result=new List<CatalogLanguageBranch>();
        foreach(var source in entries.GroupBy(e=>e.Text,StringComparer.Ordinal))
        {
            var alternatives=source.Where(e=>e.LanguageGroup.Length>0).ToArray();
            if(alternatives.Length==0)continue;
            // A separately located ordinary display field is still usable.
            // Unlocated engine collection duplicates cannot erase provenance.
            if(source.Any(e=>e.LanguageGroup.Length==0&&e.Kind is not ("wolf-engine-read-candidate" or "wolf-engine-display-observed")))continue;
            result.AddRange(alternatives.Select(e=>new CatalogLanguageBranch(e.Text,e.File+"|"+e.LanguageGroup,e.Language)));
        }
        return result.Distinct().ToArray();
    }
}
