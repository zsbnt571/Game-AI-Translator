using System.Text.RegularExpressions;
namespace ScreenshotTranslationUiTester;

// A narrow machine-result check, not language guessing or name translation.
// Human overrides remain authoritative. The normal queue bounds retries.
internal static class AutomaticTranslationQuality
{
    private static readonly Regex Stammer=new(@"(?<![A-Za-z])(?<first>[A-Za-z])(?i:(?:[-‑–]\k<first>){1,})[-‑–](?<word>[a-z]{3,})(?![A-Za-z])",RegexOptions.Compiled);
    internal static bool Valid(string source,string text,ApiSettings settings,IReadOnlyDictionary<string,string>? terms=null,EmbeddedTextSyntax syntax=EmbeddedTextSyntax.Generic)
    {
        if(!TranslationWorkspaceStore.Valid(source,text,syntax))return false;
        if(!TranslationLanguagePolicy.MenuNamesPreserved(source,text)||!TranslationLanguageSpans.Preserved(source,text,settings))return false;
        if(TranslationLanguageSpans.UnchangedEligibleText(source,text,settings,terms:terms))return false;
        if(IntroducesWrongScript(source,text,settings,terms))return false;
        // Machine explanations are not game text. Do not reject the same words
        // when they were already authored in the source or entered manually.
        string trimmed=text.TrimStart();
        if(!source.TrimStart().StartsWith("```")&&trimmed.StartsWith("```"))return false;
        const string introduction=@"^(?:here(?:'s| is) (?:the |your )?translation\s*[:：]|(?:translation|translated text|译文|翻译结果|以下是翻译|这是翻译)\s*[:：]|(?:sorry[,，]?\s*)?I (?:cannot|can't|am unable to) (?:translate|assist|help)\b|(?:抱歉[，,。]?\s*)?(?:我无法|我不能)(?:翻译|协助|帮助))";
        if(Regex.IsMatch(trimmed,introduction,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant)
            &&!Regex.IsMatch(source.TrimStart(),introduction,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant))return false;
        if(!FusionConfiguration.NormalizeLanguage(settings.TargetLanguage).StartsWith("zh",StringComparison.OrdinalIgnoreCase))return true;
        if(syntax==EmbeddedTextSyntax.Rpg)
            foreach(Match effect in Regex.Matches(source,@"(?:\\|\x1b)(?i:SHAKE|WAVE|SLIDE|CIRCLE)(?:<[^>\r\n]{0,160}>)?(?<word>[a-z]{2,})(?![A-Za-z])"))
            {
                string word=effect.Groups["word"].Value;
                if(terms is not null&&terms.Any(p=>p.Key.Equals(word,StringComparison.OrdinalIgnoreCase)&&p.Value.Contains(word,StringComparison.OrdinalIgnoreCase)))continue;
                // Older code protected the letters following an effect command
                // as part of that command. Retry only affected automatic entries.
                if(text.Contains(effect.Value,StringComparison.Ordinal))return false;
            }
        if(syntax==EmbeddedTextSyntax.Generic&&EmbeddedTextContinuity.HasFormatArtifact(source,text))return false;
        string visible=EmbeddedTranslationText.Protect(source).Text;
        foreach(Match match in Stammer.Matches(visible))
        {
            string word=match.Groups["word"].Value;
            if(char.ToLowerInvariant(match.Groups["first"].Value[0])!=word[0])continue;
            if(terms is not null&&terms.Any(p=>p.Key.Equals(word,StringComparison.OrdinalIgnoreCase)&&p.Value.Contains(word,StringComparison.OrdinalIgnoreCase)))continue;
            if(text.Contains(match.Value,StringComparison.OrdinalIgnoreCase))return false;
        }
        return true;
    }

    // This is a wrong-output guard, not a new source-language detector. Unknown
    // Latin prose must remain eligible, but a Latin target must not accept a
    // entirely non-Latin CJK/Hangul/Cyrillic response to it. Existing mixed text
    // and explicit glossary spellings remain authoritative.
    internal static bool IntroducesWrongScript(string source,string text,ApiSettings settings,IReadOnlyDictionary<string,string>? terms=null)
    {
        if(TranslationLanguagePolicy.Canonical(settings.TargetLanguage) is not ("en" or "fr" or "de" or "es" or "pt"))return false;
        string original=TranslationLanguagePolicy.Plain(source),translated=TranslationLanguagePolicy.Plain(text);
        if(terms is not null)
            foreach(var term in terms)
                if(term.Key.Length>0&&term.Value.Length>0&&source.Contains(term.Key,StringComparison.OrdinalIgnoreCase))
                    translated=translated.Replace(term.Value,"",StringComparison.Ordinal);
        static int Script(char c)=>c switch
        {
            >= '\u3400' and <= '\u9fff'=>1,
            >= '\u3040' and <= '\u30ff' or >= '\uff66' and <= '\uff9d'=>2,
            >= '\uac00' and <= '\ud7af' or >= '\u1100' and <= '\u11ff'=>3,
            >= '\u0400' and <= '\u052f'=>4,
            _=>0
        };
        var originalScripts=original.Select(Script).Where(s=>s!=0).ToHashSet();
        int introduced=translated.Count(c=>char.IsLetter(c)&&Script(c) is var script&&script!=0&&!originalScripts.Contains(script));
        bool latin=translated.Any(c=>c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '\u00c0' and <= '\u024f');
        return introduced>=2&&!latin;
    }
}
