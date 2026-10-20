using System.Text.RegularExpressions;
namespace ScreenshotTranslationUiTester;

// A narrow machine-result check, not language guessing or name translation.
// Human overrides remain authoritative. The normal queue bounds retries.
internal static class AutomaticTranslationQuality
{
    private static readonly Regex Stammer=new(@"(?<![A-Za-z])(?<first>[A-Za-z])(?i:(?:[-‑–]\k<first>){1,})[-‑–](?<word>[a-z]{3,})(?![A-Za-z])",RegexOptions.Compiled);
    internal static bool Valid(string source,string text,ApiSettings settings,IReadOnlyDictionary<string,string>? terms=null,EmbeddedTextSyntax syntax=EmbeddedTextSyntax.Generic)
        =>Reason(source,text,settings,terms,syntax).Length==0;
    internal static bool Soft(string reason)=>reason is "term-mismatch" or "untranslated-content";
    // Readable provisional text may be displayed while the queue repairs it.
    // An unchanged foreign sentence is not a translation and cannot replace a
    // previously useful result. Valid remains the stricter completion contract.
    internal static bool Displayable(string source,string text,ApiSettings settings,IReadOnlyDictionary<string,string>? terms=null,EmbeddedTextSyntax syntax=EmbeddedTextSyntax.Generic)
    {
        string reason=Reason(source,text,settings,terms,syntax);
        return reason.Length==0||Soft(reason)&&
            !string.Equals(TranslationLanguagePolicy.Plain(source).Trim(),TranslationLanguagePolicy.Plain(text).Trim(),StringComparison.Ordinal);
    }
    internal static string Reason(string source,string text,ApiSettings settings,IReadOnlyDictionary<string,string>? terms=null,EmbeddedTextSyntax syntax=EmbeddedTextSyntax.Generic)
    {
        if(!TranslationWorkspaceStore.Valid(source,text,syntax))return "placeholder-mismatch";
        if(!GameTranslationLiterals.NumbersPreserved(source,text,settings,syntax))return "numeric-mismatch";
        if(!GameTranslationLiterals.IdentityPreserved(source,text,terms??settings.RequestTerms,syntax))return "protected-term-mismatch";
        if(!TranslationLanguagePolicy.MenuNamesPreserved(source,text))return "menu-name-mismatch";
        if(!TranslationLanguageSpans.Preserved(source,text,settings))return "language-boundary";
        if(TranslationLanguagePolicy.Canonical(settings.TargetLanguage)=="zh"&&TranslationLanguagePolicy.ShouldTranslate(source,settings)
            &&ClockDisplayTemplate.TrySource(source.Trim(),out _)&&source.Trim()==text.Trim()
            &&!((terms??settings.RequestTerms) is {} clockTerms&&TranslationGlossary.IdentityCovers(source,clockTerms)))return "untranslated-content";
        if(IntroducesWrongScript(source,text,settings,terms))return "target-script-mismatch";
        // Machine explanations are not game text. Do not reject the same words
        // when they were already authored in the source or entered manually.
        string trimmed=text.TrimStart();
        if(!source.TrimStart().StartsWith("```")&&trimmed.StartsWith("```"))return "nontranslation-output";
        const string introduction=@"^(?:here(?:'s| is) (?:the |your )?translation\s*[:：]|(?:translation|translated text|译文|翻译结果|以下是翻译|这是翻译)\s*[:：]|(?:sorry[,，]?\s*)?I (?:cannot|can't|am unable to) (?:translate|assist|help)\b|(?:抱歉[，,。]?\s*)?(?:我无法|我不能)(?:翻译|协助|帮助))";
        if(Regex.IsMatch(trimmed,introduction,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant)
            &&!Regex.IsMatch(source.TrimStart(),introduction,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant))return "nontranslation-output";
        if(syntax==EmbeddedTextSyntax.Generic&&EmbeddedTextContinuity.HasFormatArtifact(source,text))return "format-artifact";
        if(TranslationGlossary.MissingTranslation(source,text,terms??settings.RequestTerms,settings,syntax))return "term-mismatch";
        if(TranslationLanguageSpans.UnchangedEligibleText(source,text,settings,terms:terms))return "untranslated-content";
        if(TranslationLanguageSpans.ResidualJapanese(source,text,settings,terms??settings.RequestTerms,syntax))return "untranslated-content";
        if(!FusionConfiguration.NormalizeLanguage(settings.TargetLanguage).StartsWith("zh",StringComparison.OrdinalIgnoreCase))return "";
        if(syntax==EmbeddedTextSyntax.Rpg)
            foreach(Match effect in Regex.Matches(source,@"(?:\\|\x1b)(?i:SHAKE|WAVE|SLIDE|CIRCLE)(?:<[^>\r\n]{0,160}>)?(?<word>[a-z]{2,})(?![A-Za-z])"))
            {
                string word=effect.Groups["word"].Value;
                if(terms is not null&&terms.Any(p=>p.Key.Equals(word,StringComparison.OrdinalIgnoreCase)&&p.Value.Contains(word,StringComparison.OrdinalIgnoreCase)))continue;
                // Older code protected the letters following an effect command
                // as part of that command. Retry only affected automatic entries.
                if(text.Contains(effect.Value,StringComparison.Ordinal))return "untranslated-content";
            }
        string visible=EmbeddedTranslationText.Protect(source).Text;
        foreach(Match match in Stammer.Matches(visible))
        {
            string word=match.Groups["word"].Value;
            if(char.ToLowerInvariant(match.Groups["first"].Value[0])!=word[0])continue;
            if(terms is not null&&terms.Any(p=>p.Key.Equals(word,StringComparison.OrdinalIgnoreCase)&&p.Value.Contains(word,StringComparison.OrdinalIgnoreCase)))continue;
            if(text.Contains(match.Value,StringComparison.OrdinalIgnoreCase))return "untranslated-content";
        }
        return "";
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
