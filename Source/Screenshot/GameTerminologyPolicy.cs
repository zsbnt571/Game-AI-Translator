using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

// A display policy: never grants write access to script identifiers, asset names
// or the value of a player-entered variable.
internal static class GameTerminologyPolicy
{
    internal const string Prompt = "Readable game terminology: by default translate ALL player-visible foreign-language names and meaningful text into the target language, including people, nicknames, honorifics, locations, countries, organizations, factions, fantasy races, skills, magic, classes, items, equipment, quests, UI labels, ending categories, brands and work titles. Use a known target-language name when supported, otherwise a faithful transliteration or semantic translation; do not invent a different person, place or product. A proper noun is NOT a keep-original exception. For Chinese targets, kana names and honorifics must not remain untranslated inside Chinese dialogue. Provide understandable target-language font names or technical explanations; retain an exact identifier only where needed to locate the original setting. Keep the same established complete entity name across dialogue, quests, maps and descriptions. Only identified entities and necessary game-mechanic terms have a fixed translation; ordinary words remain context-sensitive and homonyms must not be mechanically replaced. Supplied glossary mappings are authoritative; a mapping whose source equals its target is the user's explicit keep-original choice. Language-selection options/autonyms stay exactly as authored. Character-entry alphabet/kana cells, key labels, opaque codes, URLs, protected placeholders and player-entered names are input data, not words to expand or rename. Translate preset character names and meaningful full words or sentences normally. Never rewrite source code, asset paths or internal lookup keys.";

    // A bare character is most commonly an input cell or hotkey. This excludes
    // only a complete single-character item, not I/a inside a real sentence.
    internal static bool InputCharacter(string text)
    {
        string value=TranslationLanguagePolicy.Plain(text).Trim();
        return value.Length==1&&(value[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '\u3041' and <= '\u3096' or >= '\u30a1' and <= '\u30fa' or >= '\uff66' and <= '\uff9d');
    }

    internal static bool KanaName(string text)=>Regex.IsMatch(text, @"^[\u30a1-\u30fa\u30fc\uff66-\uff9d]{2,24}(?:[・･][\u30a1-\u30fa\u30fc\uff66-\uff9d]{2,24})*$",RegexOptions.CultureInvariant);
}
