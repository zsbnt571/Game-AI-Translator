using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;

namespace ScreenshotTranslationUiTester;

internal static class TranslationGlossary
{
    private static readonly ConditionalWeakTable<object,Matcher> matchers=new();
    private static readonly ConditionalWeakTable<object,Matcher> contextualMatchers=new();
    internal sealed record TermRule(string Scope,string Origin);
    private static readonly IReadOnlyDictionary<string,TermRule> noRules=new Dictionary<string,TermRule>();
    private static readonly ConditionalWeakTable<object,IReadOnlyDictionary<string,TermRule>> rules=new();
    internal static void Register(IReadOnlyDictionary<string,string> terms,IReadOnlyDictionary<string,TermRule> metadata)
        =>rules.GetValue(terms,_=>metadata);
    internal static IReadOnlyDictionary<string,TermRule> Rules(IReadOnlyDictionary<string,string> terms)
        =>rules.TryGetValue(terms,out var value)?value:noRules;
    private static TermRule Rule(IReadOnlyDictionary<string,string> terms,string key)
        =>Rules(terms).GetValueOrDefault(key)??new(terms.TryGetValue(key,out var value)&&value.Equals(key,StringComparison.OrdinalIgnoreCase)?"literal":"label","legacy");
    private static bool Applies(IReadOnlyDictionary<string,string> terms,string source,string key)
    {
        if(Rule(terms,key).Scope is "entity" or "literal")return true;
        // Old dictionaries did not record whether a phrase was a task title or
        // an ordinary clause. Retain its spelling, but do not impose that whole
        // title on a naturally inflected sentence containing the same words.
        string plain=TranslationLanguagePolicy.Plain(source).Trim();
        return plain.Trim(' ','.','。','!','！','?','？',':','：','"','\'','“','”','「','」','『','』')
            .Equals(key,StringComparison.OrdinalIgnoreCase)||
            Regex.IsMatch(plain,"[\\\"“「『]"+Regex.Escape(key)+"[\\\"”」』]",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    }
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
    private const string SpacedWord=@"[\p{L}\p{N}_-[\u3040-\u30ff\u3400-\u9fff\uff66-\uff9d]]";
    private const string Katakana=@"[\u30a1-\u30fa\u30fc\uff66-\uff9d]";
    private static bool IsKatakana(char c)=>c is >= '\u30a1' and <= '\u30fa' or '\u30fc' or >= '\uff66' and <= '\uff9d';
    private static string PatternFor(string term)=>(IsWord(term[0])?"(?<!"+SpacedWord+")":IsKatakana(term[0])?"(?<!"+Katakana+")":"")+Regex.Escape(term)+(IsWord(term[^1])?"(?!"+SpacedWord+")":IsKatakana(term[^1])?"(?!"+Katakana+")":"");
    // Western names must be whole words (Ann is not part of manner). CJK
    // phrases have no word separators. Longest phrases take precedence.
    internal static bool Contains(string source,string term)
    {
        if(term.Length==0)return false;
        if(!source.Contains(term,StringComparison.OrdinalIgnoreCase))return false;
        return Regex.IsMatch(source,PatternFor(term),RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    }
    private static bool IsWord(char c)=>(char.IsLetterOrDigit(c)||c=='_')&&!(c is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff' or >= '\uff66' and <= '\uff9d');
    internal static Dictionary<string,string> Applicable(IReadOnlyDictionary<string,string> terms,IEnumerable<string> sources)
    {
        // Published workspace dictionaries are immutable. Build the matcher
        // once per snapshot, not once per term per catalogue entry.
        var matcher=matchers.GetValue(terms,_=>new(terms.Keys));var found=new HashSet<string>(StringComparer.Ordinal);
        var contextual=contextualMatchers.GetValue(terms,_=>new(terms.Keys.Where(k=>Rule(terms,k).Scope is "entity" or "literal")));
        if(matcher.Pattern is not null)foreach(string source in sources)
        {
            var accepted=new List<(int Start,int End)>();
            foreach(Match match in matcher.Pattern.Matches(source))
            {
                string key=matcher.Keys[match.Value];if(!Applies(terms,source,key))continue;
                found.Add(key);accepted.Add((match.Index,match.Index+match.Length));
            }
            // An inapplicable long event label must not consume the shorter
            // entity inside it. An applicable complete title still takes priority.
            if(contextual.Pattern is not null)foreach(Match match in contextual.Pattern.Matches(source))
                if(!accepted.Any(span=>match.Index<span.End&&match.Index+match.Length>span.Start))
                    found.Add(contextual.Keys[match.Value]);
        }
        return found.OrderByDescending(k=>k.Length).ThenBy(k=>k,StringComparer.Ordinal).Take(60).ToDictionary(k=>k,k=>terms[k],StringComparer.Ordinal);
    }
    internal static string Stamp(IReadOnlyDictionary<string,string> terms,string source)
    {
        var applicable=Applicable(terms,[source]);
        string value=JsonSerializer.Serialize(applicable);
        var metadata=applicable.Keys.Where(k=>Rules(terms).ContainsKey(k)).ToDictionary(k=>k,k=>Rule(terms,k),StringComparer.Ordinal);
        if(metadata.Count>0)value+="\n"+JsonSerializer.Serialize(metadata);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
    internal static string ProtectIdentity(string source,IReadOnlyDictionary<string,string> terms,Func<string,string> replace)
    {
        var matcher=matchers.GetValue(terms,_=>new(terms.Keys));
        return matcher.Pattern is null?source:matcher.Pattern.Replace(source,match=>
            terms[matcher.Keys[match.Value]].Equals(match.Value,StringComparison.OrdinalIgnoreCase)?replace(match.Value):match.Value);
    }
    // A keep-as-written glossary may cover a complete phrase followed by
    // punctuation. Use the same whole-word, longest-match rules as the request;
    // an identity term inside a larger untranslated sentence is not an exemption.
    internal static bool IdentityCovers(string source,IReadOnlyDictionary<string,string> terms)
    {
        string plain=TranslationLanguagePolicy.Plain(source);
        var matcher=matchers.GetValue(terms,_=>new(terms.Keys));
        if(matcher.Pattern is null)return false;
        int end=0;bool any=false;
        foreach(Match match in matcher.Pattern.Matches(plain))
        {
            if(plain[end..match.Index].Any(char.IsLetterOrDigit)||
                !terms[matcher.Keys[match.Value]].Equals(match.Value,StringComparison.OrdinalIgnoreCase))return false;
            end=match.Index+match.Length;any=true;
        }
        return any&&!plain[end..].Any(char.IsLetterOrDigit);
    }
    internal static string[] Changed(IReadOnlyDictionary<string,string> before,IReadOnlyDictionary<string,string> after)
        =>before.Keys.Concat(after.Keys).Distinct(StringComparer.Ordinal).Where(k=>before.GetValueOrDefault(k)!=after.GetValueOrDefault(k)||Rule(before,k)!=Rule(after,k)).ToArray();
    internal static bool Affected(string source,string[] changed)=>matchers.GetValue(changed,_=>new(changed)).Pattern?.IsMatch(source)==true;

    internal static bool MissingTranslation(string source,string translated,IReadOnlyDictionary<string,string>? terms,ApiSettings settings,EmbeddedTextSyntax syntax)
    {
        if(terms is null||terms.Count==0||!TranslationLanguagePolicy.ShouldTranslate(source,settings))return false;
        string visible=GameTranslationLiterals.Visible(source,syntax);
        // Excluded-language and already-target portions cannot become an excuse
        // to impose a name from a different language selection.
        foreach(string keep in TranslationLanguageSpans.Required(source,settings))visible=visible.Replace(keep,"",StringComparison.Ordinal);
        foreach(var term in Applicable(terms,[visible]))
            if(!term.Key.Equals(term.Value,StringComparison.OrdinalIgnoreCase)&&
                // Homonyms in any script need context. Enforce a full named
                // item, multiword name or evidenced name + honorific, never a
                // single ordinary word just because it shares a name's spelling.
                (visible.Trim().Equals(term.Key,StringComparison.OrdinalIgnoreCase)||
                 term.Key.Any(char.IsWhiteSpace)&&Contains(visible,term.Key)||
                 Regex.IsMatch(visible,PatternFor(term.Key)+@"(?:さん|ちゃん|さま|様|君|くん|殿)",RegexOptions.CultureInvariant))&&
                !translated.Contains(term.Value,StringComparison.OrdinalIgnoreCase))return true;
        return false;
    }
}
