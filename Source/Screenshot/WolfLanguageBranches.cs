using System.Text.RegularExpressions;
namespace ScreenshotTranslationUiTester;

// Some WOLF display resources store two language alternatives in one value.
// Derive exact display keys only from explicit markers and paired structure;
// never use fuzzy matching, change the stored translation, or guess a branch.
internal static class WolfLanguageBranches
{
    private static readonly string[] Markers=["[ENG]","[EN]","[JPN]","[JP]","[CHS]","[CHT]","[KOR]"];
    private static readonly Regex Prefix=new(@"^@[0-9]{1,3}(?![0-9])",RegexOptions.CultureInvariant);
    private static string Clean(string value)=>value.Replace("\r\n","\n").Trim(' ','\t','\r','\n');
    private static (string Marker,string[] Parts)? Split(string source)
    {
        foreach(var marker in Markers)
        {
            int at=source.IndexOf(marker,StringComparison.Ordinal);
            if(at<=0||source.IndexOf(marker,at+marker.Length,StringComparison.Ordinal)>=0)continue;
            var parts=new[]{Clean(source[..at]),Clean(source[(at+marker.Length)..])};
            if(parts.Any(s=>s.Length<2)||Markers.Any(m=>parts.Any(s=>s.Contains(m,StringComparison.Ordinal))))continue;
            // Reject prose discussing tags: real alternatives contain different scripts.
            bool kana(string s)=>s.Any(c=>c is >= '\u3040' and <= '\u30ff');
            bool latin(string s)=>s.Count(char.IsAsciiLetter)>=4;
            if(kana(parts[0])==kana(parts[1])&&latin(parts[0])==latin(parts[1]))continue;
            return(marker,parts);
        }
        return null;
    }
    internal static bool IsTemplate(string source)=>Split(source) is not null;
    internal static IEnumerable<string> Sources(string source)
    {
        if(Split(source) is not {} split){yield return source;yield break;}
        string prefix=Prefix.Match(split.Parts[0]).Value;
        bool paired=prefix.Length>0&&Prefix.Match(split.Parts[1]).Value==prefix;
        foreach(string part in split.Parts){yield return part;if(paired&&part.Length>prefix.Length)yield return part[prefix.Length..];}
    }
    internal static IEnumerable<KeyValuePair<string,string>> Pairs(string source,string translated)
    {
        if(Split(source) is not {} split)yield break;
        int at=translated.IndexOf(split.Marker,StringComparison.Ordinal);
        if(at<0||translated.IndexOf(split.Marker,at+split.Marker.Length,StringComparison.Ordinal)>=0)yield break;
        string[] targets=[Clean(translated[..at]),Clean(translated[(at+split.Marker.Length)..])];
        if(targets.Any(string.IsNullOrWhiteSpace))yield break;
        string prefix=Prefix.Match(split.Parts[0]).Value;
        bool paired=prefix.Length>0&&Prefix.Match(split.Parts[1]).Value==prefix;
        for(int i=0;i<2;i++)
        {
            yield return new(split.Parts[i],targets[i]);
            if(paired&&Prefix.Match(targets[i]).Value==prefix&&targets[i].Length>prefix.Length)
                yield return new(split.Parts[i][prefix.Length..],targets[i][prefix.Length..]);
        }
    }
}
