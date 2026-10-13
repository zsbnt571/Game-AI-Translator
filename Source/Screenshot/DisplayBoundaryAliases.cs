namespace ScreenshotTranslationUiTester;

// Opt-in for renderers that extract surrounding whitespace before lookup and
// put it back at display time. Never remove speaker IDs, internal newlines,
// formatting commands, variable names or punctuation to guess a cache match.
internal static class DisplayBoundaryAliases
{
    internal static string Key(string source)=>source.Replace("\r\n","\n",StringComparison.Ordinal).Trim(' ','\r','\n','\t');
    internal static Dictionary<string,string> Index(IEnumerable<string> sources)
    {
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(string source in sources.OrderBy(s=>s.Length).ThenBy(s=>s,StringComparer.Ordinal))
        {
            string key=Key(source);if(key.Length>0)result.TryAdd(key,source);
        }
        return result;
    }
    internal static Dictionary<string,string> Values(IReadOnlyDictionary<string,string> entries)
        =>Index(entries.Keys).ToDictionary(p=>p.Key,p=>Key(entries[p.Value]),StringComparer.Ordinal);
}
