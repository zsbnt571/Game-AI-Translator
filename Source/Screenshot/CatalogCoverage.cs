using System.Security.Cryptography;
using System.Text;
namespace ScreenshotTranslationUiTester;

// Diagnostic comparison only. Normalized matches never authorize translation
// writeback or change a cache key. Retained diagnostic observations are bounded.
internal sealed class CatalogCoverage
{
    private readonly HashSet<string> exact=new(StringComparer.Ordinal),lineEndings=new(StringComparer.Ordinal);
    private readonly Dictionary<string,string> observed=new(StringComparer.Ordinal);
    private long omitted,omittedCatalog;
    internal void ResetCatalog(){exact.Clear();lineEndings.Clear();omittedCatalog=0;}
    private static string Hash(string s)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    internal void AddCatalog(string source)
    {
        string key=Hash(source);if(exact.Contains(key))return;
        if(exact.Count>=150000){omittedCatalog++;return;}
        exact.Add(key);lineEndings.Add(Hash(source.Replace("\r\n","\n")));
    }
    internal void Observe(string source)
    {
        string key=Hash(source);if(observed.ContainsKey(key))return;
        if(observed.Count>=50000){omitted++;return;}observed[key]=Hash(source.Replace("\r\n","\n"));
    }
    internal object Snapshot(bool complete)
    {
        complete&=omittedCatalog==0;
        int matched=0,format=0;var missing=new List<string>();
        foreach(var p in observed){if(exact.Contains(p.Key))matched++;else if(lineEndings.Contains(p.Value))format++;else if(missing.Count<256)missing.Add(p.Key);}
        return new{catalogComplete=complete,observed=observed.Count,exactMatches=matched,lineEndingOnly=format,
            outsideCatalog=complete?observed.Count-matched-format:0,pendingCatalog=complete?0:observed.Count-matched-format,
            untrackedObservations=omitted,untrackedCatalogEntries=omittedCatalog,missingSourceHashes=missing,semantics="outside-catalog-is-not-proof-of-dynamic-text"};
    }
}
