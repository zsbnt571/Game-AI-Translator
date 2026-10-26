using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

// Locale-aware extractors are authoritative. This is a conservative fallback
// for old bridges and untagged catalogues, not a general language detector.
internal sealed class EmbeddedSourceLanguagePolicy
{
    private readonly SourceLanguageMode mode;
    private readonly ApiSettings? settings;
    internal GameSourceLanguageTracker Tracker {get;private set;}=new();
    internal EmbeddedSourceLanguagePolicy(ApiSettings value,string? game=null) : this(value.SourceLanguage)
    {settings=ApiSettingsSnapshot.Copy(value);Tracker=new(game);}
    internal string CatalogHint=>Tracker.Locale;
    internal void ObserveGameLocale(string? locale)=>Tracker.ObserveDeclared(locale);
    internal bool AllowsSelectedText(string text) => settings is null || TranslationLanguagePolicy.ShouldTranslate(text, settings);
    private readonly HashSet<string> runtimeLanguages=new(StringComparer.Ordinal);
    private readonly HashSet<string> catalogueLanguages=new(StringComparer.Ordinal);
    private bool runtimeSelection;
    internal string SelectedLanguage {get;private set;}
    internal bool Resolved {get;private set;}
    internal bool CatalogResolved=>Resolved&&!runtimeSelection;
    internal string EvidenceSource {get;private set;}="none";
    internal EmbeddedSourceLanguagePolicy(SourceLanguageMode mode)
    {
        this.mode=mode;SelectedLanguage=mode switch
        {SourceLanguageMode.English=>"en",SourceLanguageMode.Japanese=>"ja",SourceLanguageMode.SimplifiedChinese=>"zh",SourceLanguageMode.Korean=>"ko",SourceLanguageMode.Mixed=>"*",_=>""};
        Resolved=mode!=SourceLanguageMode.Auto;
        if(Resolved)EvidenceSource="user";
    }
    private static readonly Regex Words=new(@"[\p{L}]+",RegexOptions.Compiled);
    private static readonly Regex Markup=new(@"</?[^>]+>|\\[A-Za-z]+\[[^\]]*\]",RegexOptions.Compiled);
    private static readonly Dictionary<string,HashSet<string>> Evidence=new()
    {
        ["en"]=new("the you your are is this that with have from don't what where who should new game load save settings options continue back exit yes no language volume controls quality window resolution".Split(' '),StringComparer.OrdinalIgnoreCase),
        ["es"]=new("el los las una unos unas tienes eres está estás dónde quién qué para pero como guardar cargar juego ajustes salir idioma".Split(' '),StringComparer.OrdinalIgnoreCase),
        ["fr"]=new("les une des vous votre avec dans pour mais êtes où quoi jouer sauvegarder charger quitter paramètres".Split(' '),StringComparer.OrdinalIgnoreCase),
        ["de"]=new("der die das ein eine und ist sind nicht dein deine spiel laden speichern zurück einstellungen".Split(' '),StringComparer.OrdinalIgnoreCase),
        ["pt"]=new("você não uma são suas seu para jogo salvar carregar configurações sair".Split(' '),StringComparer.OrdinalIgnoreCase)
    };
    internal static string Identify(string source)
    {
        // Variables and formatting are not language evidence. Keep the same
        // protected-token boundary as the generic translation payload.
        string text=Markup.Replace(Regex.Replace(EmbeddedTranslationText.Protect(source).Text,@"⟦RPG\d+⟧",""),"");int kana=0,han=0,korean=0,cyrillic=0,latin=0;
        var otherScripts=new Dictionary<string,int>(StringComparer.Ordinal);
        foreach(char c in text)
        {
            if(c is >= '\u3040' and <= '\u30ff' or >= '\uff66' and <= '\uff9d')kana++;
            else if(c is >= '\u3400' and <= '\u9fff')han++;
            else if(c is >= '\uac00' and <= '\ud7af' or >= '\u1100' and <= '\u11ff')korean++;
            else if(c is >= '\u0400' and <= '\u052f')cyrillic++;
            else if(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z')latin++;
            else if(char.IsLetter(c))
            {
                string script=c switch
                {>= '\u0600' and <= '\u06ff' or >= '\u0750' and <= '\u077f' or >= '\u08a0' and <= '\u08ff'=>"ar",
                 >= '\u0590' and <= '\u05ff'=>"he",>= '\u0e00' and <= '\u0e7f'=>"th",
                 >= '\u0370' and <= '\u03ff' or >= '\u1f00' and <= '\u1fff'=>"el",
                 >= '\u0900' and <= '\u097f'=>"hi",_=>"und"};
                otherScripts[script]=otherScripts.GetValueOrDefault(script)+1;
            }
        }
        if(kana>0)return "ja";
        if(korean>0)return "ko";
        if(han>0)return "han"; // Han-only labels cannot distinguish Japanese from Chinese.
        if(cyrillic>0)return "ru";
        var other=otherScripts.Where(p=>p.Key!="und").OrderByDescending(p=>p.Value).FirstOrDefault().Key;
        if(other is not null)return other;
        if(latin==0&&otherScripts.Count>0)return "und";
        if(latin==0)return "";
        // A homograph must not become a definitive runtime/catalogue vote.
        // The full translation policy retains its alternatives for source scope.
        if(TranslationLanguagePolicy.IsAmbiguousLatinWord(text))return "latin";
        var words=Words.Matches(text).Select(m=>m.Value).Where(w=>!TranslationLanguagePolicy.IsAmbiguousLatinToken(w)).ToArray();
        var scores=Evidence.Select(p=>(p.Key,Score:words.Count(p.Value.Contains))).OrderByDescending(p=>p.Score).ToArray();
        if(scores[0].Score>0&&scores[0].Score>scores[1].Score)return scores[0].Key;
        return "latin"; // A proper name or unknown Latin-script label is not proof of English.
    }
    internal static string Choose(IEnumerable<string> texts,bool allowLatinFallback=true)
    {
        var counts=new Dictionary<string,(int Rows,int Weight)>(StringComparer.Ordinal);
        // A language name, fallback font sample or secondary locale's short label
        // must not make an otherwise English catalogue Japanese (or vice versa).
        // Untagged text is evidence by distinct row, not authority for the whole
        // resource merely because one character belongs to a particular script.
        foreach(var text in texts.Distinct(StringComparer.Ordinal))
        {
            var language=Identify(text);var old=counts.GetValueOrDefault(language);
            counts[language]=(old.Rows+1,old.Weight+Math.Min(text.Length,512));
        }
        var strongest=counts.Where(p=>p.Key is not ("" or "latin" or "han" or "und"))
            .OrderByDescending(p=>p.Value.Rows).ThenByDescending(p=>p.Value.Weight).ThenBy(p=>p.Key,StringComparer.Ordinal).FirstOrDefault().Key;
        if(strongest is not null)return strongest;
        return counts.ContainsKey("han")?"zh":allowLatinFallback&&counts.ContainsKey("latin")?"en":"";
    }
    internal void SelectCatalog(IEnumerable<string> texts,string hint="")
    {
        if(CatalogResolved)return;
        string selected=string.IsNullOrWhiteSpace(hint)?Choose(texts):hint.ToLowerInvariant();
        if(selected.Length==0)return; // An empty catalogue cannot erase real runtime evidence.
        SelectedLanguage=selected;Resolved=true;runtimeSelection=false;
        EvidenceSource=string.IsNullOrWhiteSpace(hint)?"catalog-text":"declared-catalog";
    }
    internal void ResetCatalogEvidence()
    {
        catalogueLanguages.Clear();runtimeLanguages.Clear();
        if(mode!=SourceLanguageMode.Auto)return;
        SelectedLanguage="";Resolved=false;runtimeSelection=false;EvidenceSource="none";
    }
    internal void ObserveRuntime(IEnumerable<string> texts)
    {
        var observations=texts.ToArray();Tracker.ObserveVisible(observations);
        if(mode!=SourceLanguageMode.Auto)return;
        foreach(string text in observations)
        {
            string detected=Identify(text);
            if(detected.Length>0)runtimeLanguages.Add(detected);
        }
        if(runtimeLanguages.Count==0||CatalogResolved)return;
        SelectedLanguage=DescribeLanguages(runtimeLanguages);
        Resolved=true;runtimeSelection=true;EvidenceSource="runtime-text";
    }
    // Extractors select declared locale views before publishing them. Untagged
    // source strings are not alternate language packs merely because they use
    // another script. Only an explicit user source language narrows those rows.
    internal bool AllowsCatalog(string text)=>AllowsSelectedText(text) && (settings is not null||mode==SourceLanguageMode.Auto?Identify(text).Length>0:Allows(text));
    // Bulk resource discovery is not proof that a string is used by this session.
    // A declared game locale authorizes bulk work. Remembered/inferred locales
    // only help local extraction: foreign dialogue can resemble a locale change.
    // Explicit active-pack provenance may contain genuine foreign dialogue.
    // Unmarked foreign rows wait for a visible/ahead event, which stays independent.
    // A single explicit source selection can authorize that exact resource's
    // batch work without claiming it is the game's active locale. Auto, guessed
    // catalogue hints and multiple selected languages never grant this permission.
    internal bool AllowsSelectedResource(string text,string resourceLanguage,bool trustedSource)
    {
        if(!trustedSource||settings is null||!AllowsSelectedText(text))return false;
        var selected=TranslationLanguagePolicy.Selected(settings);
        string resource=GameSourceLanguageTracker.CanonicalLocale(resourceLanguage);
        return selected.Length==1&&resource.Length>0&&
            resource==GameSourceLanguageTracker.CanonicalLocale(selected[0]);
    }
    internal bool AllowsActiveCatalog(string text,bool trustedActivePack=false)
    {
        if(!Tracker.HasDeclaredLocale||!AllowsSelectedText(text))return false;
        if(trustedActivePack)return true;
        var languages=TranslationLanguagePolicy.Languages(text);
        string current=Tracker.Language;
        return languages.Count>0&&languages.All(language=>language==current||
            language=="han"&&current is "ja" or "zh"||
            language=="latin"&&current is "en" or "fr" or "de" or "es" or "pt");
    }
    internal bool ScriptCatalogConfirmed(string inferredLanguage)
        =>inferredLanguage.Length>0&&!Tracker.HasDeclaredLocale&&Tracker.Evidence=="stable-visible-text"
            &&Tracker.Language==inferredLanguage;
    internal bool AllowsScriptCatalog(string text,string inferredLanguage)
        =>ScriptCatalogConfirmed(inferredLanguage)&&AllowsSelectedText(text)
            // When the whole source catalogue already uses the target, unknown
            // names alone must not cause a catalogue-wide translation pass.
            &&(settings is null||TranslationLanguagePolicy.Canonical(settings.TargetLanguage)!=inferredLanguage);
    internal void ObserveCatalogSource(string text)
    {
        if(mode!=SourceLanguageMode.Auto)return;
        string detected=Identify(text);if(detected.Length>0)catalogueLanguages.Add(detected);
    }
    private static string DescribeLanguages(IEnumerable<string> evidence)
    {
        var languages=evidence.Where(l=>l.Length>0).ToHashSet(StringComparer.Ordinal);
        if(languages.Contains("*"))return "*";
        if(languages.Contains("ja")||languages.Contains("zh"))languages.Remove("han");
        var identified=languages.Where(l=>l is not ("latin" or "und")).ToArray();
        // Names/unknown Latin labels are not proof of a second language.
        return identified.Length>1?"*":identified.Length==1?identified[0]:languages.Contains("latin")?"latin":languages.Contains("und")?"und":"";
    }
    // Locale selection bounds a bulk catalogue. Actual visible text is a
    // separate authority: Auto must not discard a bilingual menu merely because
    // its catalogue or an earlier screen happened to choose another language.
    internal bool AllowsRuntime(string text)
        =>AllowsSelectedText(text)&&Identify(text).Length>0&&(mode is SourceLanguageMode.Auto or SourceLanguageMode.Mixed||settings is not null&&AllowsSelectedText(text)||Allows(text));
    internal string RuntimePolicyIdentity=>(mode==SourceLanguageMode.Auto?"auto-per-visible-text":SelectedLanguage)+(settings is null?"":"|"+TranslationLanguagePolicy.Identity(settings));
    internal bool Allows(string text,string provisional="")
    {
        var selected=Resolved?SelectedLanguage:provisional;
        if(!AllowsSelectedText(text))return false;
        if(selected=="*")return true;
        if(selected.Length==0)return false;
        return TranslationLanguagePolicy.Languages(text).Any(detected=>detected==selected||detected=="han"&&selected is "ja" or "zh"||
            detected=="latin"&&selected is "en" or "es" or "fr" or "de" or "pt");
    }
    internal string Label=>LanguageLabel(mode==SourceLanguageMode.Auto
        ?DescribeLanguages(catalogueLanguages.Concat(runtimeLanguages).Append(SelectedLanguage)):SelectedLanguage)+(runtimeSelection?"（运行时文本）":"");
    internal static string LanguageLabel(string language)=>language switch
    {"en"=>"英语","ja"=>"日语","zh"=>"中文","ko"=>"韩语","ru"=>"俄语","es"=>"西班牙语","fr"=>"法语","de"=>"德语","pt"=>"葡萄牙语","ar"=>"阿拉伯语","he"=>"希伯来语","th"=>"泰语","el"=>"希腊语","hi"=>"印地语","han"=>"含汉字的原文","latin"=>"拉丁文字（语种未确定）","und"=>"其他原文","*"=>"混合语言",""=>"正在识别",_=>language};
}

