using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// The active resource locale is independent of the user's translation allow-list.
// Only actual runtime observations enter this tracker, never an extracted backup pack.
internal sealed class GameSourceLanguageTracker
{
    private readonly Queue<(string Text,string Language,string Locale,int Weight)> recent = new();
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly string path = "", fingerprint = "";
    private bool declared;
    internal string Language { get; private set; } = "";
    // Language membership and resource identity have different granularity.
    // zh-Hant must not select a zh (simplified) pack merely because both are Chinese.
    internal string Locale { get; private set; } = "";
    internal string Evidence { get; private set; } = "none";
    internal bool HasDeclaredLocale => declared && Evidence == "active-game-locale";
    internal GameSourceLanguageTracker(string? game = null)
    {
        if(string.IsNullOrWhiteSpace(game))return;
        try
        {
            string full=Path.GetFullPath(game);
            var file=new FileInfo(full);
            fingerprint=file.Exists?$"{file.Length}:{file.LastWriteTimeUtc.Ticks}":"missing";
            path=Path.Combine(AppDataPaths.Root,"source-languages",TranslationPromptBuilder.Sha256(full.ToUpperInvariant())+".json");
            if(!File.Exists(path))return;
            var saved=JsonSerializer.Deserialize<Remembered>(File.ReadAllText(path));
            if(saved?.Version is 1 or 2&&saved.Fingerprint==fingerprint&&Canonical(saved.Language).Length>0)
            {Language=Canonical(saved.Language);Locale=CanonicalLocale(saved.Locale??saved.Language);Evidence="remembered-runtime";}
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
    }
    private sealed record Remembered(int Version,string Fingerprint,string Language,string? Locale=null);
    internal static string Canonical(string? language)
    {
        string value=(language??"").Trim().Replace('_','-');
        string known=TranslationLanguagePolicy.Canonical(value);
        if(TranslationLanguagePolicy.Choices.Any(c=>c.Code==known))return known;
        known=UnityTextResourceLanguageSelection.Locale(value)??"";
        if(known.Length>0)return known.Split('-')[0];
        return System.Text.RegularExpressions.Regex.IsMatch(value,@"^[a-zA-Z]{2,3}(?:-[a-zA-Z0-9]{2,8})*$")
            ?value.Split('-')[0].ToLowerInvariant():"";
    }
    internal static string CanonicalLocale(string? language)
    {
        string major=Canonical(language);
        if(major!="zh")return major;
        return TranslationLanguagePolicy.ChineseTargetScript(language)=="Hant"?"zh-Hant":"zh";
    }
    internal bool ObserveDeclared(string? locale)
    {
        if(locale==""&&declared)
        {declared=false;recent.Clear();seen.Clear();Evidence="remembered-runtime";return false;}
        string language=CanonicalLocale(locale);if(language.Length==0||language is "und" or "han" or "lat")return false;
        declared=true;
        return Set(language,"active-game-locale");
    }
    internal bool ObserveVisible(IEnumerable<string> texts)
    {
        if(declared)return false;
        foreach(string raw in texts)
        {
            string text=TranslationLanguagePolicy.Plain(raw);
            if(text.Length<12||TranslationLanguagePolicy.IsLanguageChoice(text)||!seen.Add(text))continue;
            var languages=TranslationLanguagePolicy.Languages(text).Where(l=>l is not ("latin" or "han" or "und")).ToArray();
            // A bilingual line is real content, but cannot vote for a whole pack.
            if(languages.Length!=1){seen.Remove(text);continue;}
            string locale=languages[0];
            if(locale=="zh")
            {
                string script=TranslationLanguagePolicy.ChineseTextScript(text);
                if(script=="mixed"){seen.Remove(text);continue;}
                if(script=="Hant")locale="zh-Hant";
            }
            recent.Enqueue((text,languages[0],locale,Math.Min(160,text.Count(char.IsLetter))));
            while(recent.Count>48)seen.Remove(recent.Dequeue().Text);
        }
        var ranks=recent.GroupBy(x=>x.Locale).Select(g=>new{Locale=g.Key,Rows=g.Count(),Weight=g.Sum(x=>x.Weight)})
            .OrderByDescending(x=>x.Weight).ToArray();
        if(ranks.Length==0)return false;
        var best=ranks[0];int total=ranks.Sum(x=>x.Weight);
        bool switching=Locale.Length>0&&Locale!=best.Locale;
        if(best.Rows<(switching?8:4)||best.Weight<(switching?200:80)||best.Weight<total*(switching?.85:.8))return false;
        return Set(best.Locale,"stable-visible-text");
    }
    private bool Set(string language,string evidence)
    {
        string locale=CanonicalLocale(language),major=Canonical(language);
        bool changed=Language!=major||Locale!=locale;
        if(!changed&&Evidence==evidence)return false;
        Language=major;Locale=locale;Evidence=evidence;
        if(path.Length>0)try{PortableDataStorage.WriteJson(path,new Remembered(2,fingerprint,major,locale));}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
        return changed;
    }
}
