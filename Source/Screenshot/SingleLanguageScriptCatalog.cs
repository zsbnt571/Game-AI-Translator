using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

// Only opt in for an adapter's flat executable-script literals, never an
// arbitrary resource scan. This is inferred evidence, not a declared locale.
// A second language, a language-choice label or a sizeable unknown passage
// keeps the catalogue deferred. Runtime evidence must independently agree.
internal static class SingleLanguageScriptCatalog
{
    // The general selector deliberately has a small UI vocabulary. For this
    // catalogue-only check, two distinct English connective/determiner words
    // can identify an otherwise unknown ASCII sentence. One borrowed word,
    // a proper name, accented text or positive foreign evidence cannot do so.
    // This does not change source selection or declare the game's locale.
    private static readonly HashSet<string> EnglishConnectives = new(
        "some any each every both either neither while whereas although because though without within before after among between during until unless".Split(' '),
        StringComparer.OrdinalIgnoreCase);
    private static bool HasEnglishConnectives(string plain) =>
        !plain.Any(c => char.IsLetter(c) && c is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z')) &&
        Regex.Matches(plain, @"[A-Za-z]+(?:['’][A-Za-z]+)?").Select(m => m.Value)
            .Where(EnglishConnectives.Contains).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).Count() == 2;

    internal static string Infer(IReadOnlyCollection<string> texts)
    {
        if(texts.Count is <8 or >=100000)return "";
        string language="";int clearRows=0;long clearLetters=0,totalLetters=0;
        foreach(string text in texts)
        {
            if(!RpgTranslationText.HasTranslatableText(text,EmbeddedTextSyntax.Generic))continue;
            if(TranslationLanguagePolicy.IsLanguageChoice(text))return "";
            string plain=TranslationLanguagePolicy.Plain(text);
            int letters=plain.Count(char.IsLetter);totalLetters+=letters;
            var detected=TranslationLanguagePolicy.Languages(text);
            if(detected.SetEquals(["latin"])&&HasEnglishConnectives(plain))detected=new HashSet<string>{"en"};
            var certain=detected.Where(l=>l is not ("latin" or "han" or "und")).ToArray();
            if(certain.Length>1)return "";
            if(certain.Length==1)
            {
                if(language.Length>0&&language!=certain[0])return "";
                language=certain[0];clearLetters+=letters;
                if(letters>=20)clearRows++;
            }
            else if(detected.Contains("und")||letters>32)return "";
        }
        if(clearRows<8||clearLetters<240||clearLetters<totalLetters*.95)return "";
        // Shared Han cannot silently become an English name, nor can Latin
        // passages silently become part of a Japanese/Chinese language pack.
        foreach(string text in texts)
            if(TranslationLanguagePolicy.Languages(text).Any(l=>l!=language&&
                !(l=="han"&&language is "ja" or "zh")&&
                !(l=="latin"&&language is "en" or "fr" or "de" or "es" or "pt")))return "";
        return language;
    }
}
