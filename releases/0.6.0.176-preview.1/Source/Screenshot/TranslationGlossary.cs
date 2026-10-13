using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;

namespace ScreenshotTranslationUiTester;

internal static class TranslationGlossary
{
    private static readonly ConditionalWeakTable<object,Matcher> matchers=new();
    private sealed class Matcher
    {
        internal readonly Dictionary<string,string> Keys=new(StringComparer.OrdinalIgnoreCase);
        internal readonly Regex? Pattern;
        internal Matcher(IEnumerable<string> keys)
        {
            foreach(string key in keys.OrderByDescending(k=>k.Length).ThenBy(k=>k,StringComparer.Ordinal))if(key.Length>0)Keys.TryAdd(key,key);
            if(Keys.Count>0)Pattern=new(string.Join("|",Keys.Values.Select(PatternFor)),RegexOptions.IgnoreCase|RegexOptions.CultureInvariant|RegexOptions.Compiled,TimeSpan.FromMilliseconds(100));
        }
    }
    private static string PatternFor(string term)=>(IsWord(term[0])?@"(?<![\p{L}\p{N}_])":"")+Regex.Escape(term)+(IsWord(term[^1])?@"(?![\p{L}\p{N}_])":"");
    // Western names must be whole words (Ann is not part of manner). CJK
    // phrases have no word separators. Longest phrases take precedence.
    internal static bool Contains(string source,string term)
    {
        if(term.Length==0)return false;
        if(!source.Contains(term,StringComparison.OrdinalIgnoreCase))return false;
        return Regex.IsMatch(source,PatternFor(term),RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    }
    private static bool IsWord(char c)=>c<128&&(char.IsLetterOrDigit(c)||c=='_');
    internal static Dictionary<string,string> Applicable(IReadOnlyDictionary<string,string> terms,IEnumerable<string> sources)
    {
        // Published workspace dictionaries are immutable. Build the matcher
        // once per snapshot, not once per term per catalogue entry.
        var matcher=matchers.GetValue(terms,_=>new(terms.Keys));var found=new HashSet<string>(StringComparer.Ordinal);
        if(matcher.Pattern is not null)foreach(string source in sources)foreach(Match match in matcher.Pattern.Matches(source))found.Add(matcher.Keys[match.Value]);
        return found.OrderByDescending(k=>k.Length).ThenBy(k=>k,StringComparer.Ordinal).Take(60).ToDictionary(k=>k,k=>terms[k],StringComparer.Ordinal);
    }
    internal static string Stamp(IReadOnlyDictionary<string,string> terms,string source)
        =>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Applicable(terms,[source])))));
    internal static string[] Changed(IReadOnlyDictionary<string,string> before,IReadOnlyDictionary<string,string> after)
        =>before.Keys.Concat(after.Keys).Distinct(StringComparer.Ordinal).Where(k=>before.GetValueOrDefault(k)!=after.GetValueOrDefault(k)).ToArray();
    internal static bool Affected(string source,string[] changed)=>matchers.GetValue(changed,_=>new(changed)).Pattern?.IsMatch(source)==true;
}
