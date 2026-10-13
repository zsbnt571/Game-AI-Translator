using System.Text;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

// Source-language membership, target preservation and catalogue locale selection
// are separate decisions. In particular, Mixed is never permission to load every
// translated language pack shipped with a game.
internal static class TranslationLanguagePolicy
{
    internal const string Version = "language-selection-4";
    internal static readonly (string Code, string Name)[] Choices =
    [("en", "英语"), ("ja", "日语"), ("zh", "中文"), ("ko", "韩语"),
     ("fr", "法语"), ("de", "德语"), ("es", "西班牙语"), ("pt", "葡萄牙语"), ("ru", "俄语")];
    private static readonly HashSet<string> LatinLanguages = new(["en", "fr", "de", "es", "pt"]);
    private static readonly HashSet<string> EnglishWords = new(
        "the a an you your yours are is this that these those with have from what where who should would could new game load save settings options continue back exit yes no language volume controls quality window resolution start resume quit cancel confirm accept apply audio music sound voice full screen fullscreen on off enable disabled enabled pause paused select selected level inventory equipment item items skill skills attack defend player character quest map health mana experience welcome please press click open close return next previous left right up down not and or to in of for at it its be been was were i we he she they my our his her their can will do does did don't cannot now here there all none change read write reset restart chapter warning error failed success completed minutes seconds today tomorrow bring find take use talk enter leave go get help thank thanks hello goodbye adventure begins village forest house town yes no continue battle change resolution display graphics subtitles controller keyboard mouse master mute voices difficulty easy normal hard language english japanese chinese french german spanish russian portuguese korean exit saving loading delete copy rename folder file files name description choose switch enable disable speed text dialogue message messages unread unlocked locked victory defeat hp mp exp fps gpu cpu menu world story voices".Split(' '), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> OtherLatinWords = new(
        "bonjour merci jouer sauvegarder charger quitter paramètres français espagnol deutsch speichern einstellungen zurück spiel laden französisch hola guardar cargar juego ajustes salir idioma español português você não salvar carregar configurações obrigado guten bitte dans avec pour mais und nicht der die das les une los las una el".Split(' '), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LanguageNames = new(
        ["English", "日本語", "にほんご", "日本语", "日语", "日語", "英语", "英語", "Japanese", "中文", "汉语", "漢語", "中国語", "简体中文", "簡體中文", "繁体中文", "繁體中文", "简体", "繁體", "简体汉语", "繁體漢語", "Chinese", "Simplified Chinese", "Traditional Chinese", "한국어", "조선말", "Korean", "韩语", "韓語", "Français", "French", "法语", "法語", "Deutsch", "German", "德语", "德語", "Español", "Spanish", "西班牙语", "西班牙語", "Português", "Portuguese", "葡萄牙语", "葡萄牙語", "Русский", "Russian", "俄语", "俄語", "Italiano", "Italian", "العربية", "ไทย", "Tiếng Việt"], StringComparer.OrdinalIgnoreCase);
    private static readonly Regex Words = new(@"[\p{L}]+(?:['’][\p{L}]+)?", RegexOptions.Compiled);
    // Common visible ending labels are prose, not IDs. Match the whole Latin
    // passage so identifier fragments and arbitrary uppercase tokens stay out.
    internal static bool IsOrdinaryEndingLabel(string source)=>Regex.IsMatch(source.Trim(),
        @"^(?:good|bad|true|normal|secret) end(?:ing)?$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    private static readonly Regex MenuOptionNames = new(@"(?<![\p{L}\p{N}])(?:"+
        string.Join("|",LanguageNames.OrderByDescending(n=>n.Length).Select(Regex.Escape))+@")(?!(?:[\p{L}\p{N}]))",
        RegexOptions.IgnoreCase|RegexOptions.Compiled);
    private static bool IsLanguageMenu(string plain) => Regex.IsMatch(plain,
        @"^(?:(?:voice|audio|text|subtitles?|interface)(?:\s+language)?|language|(?:配音|语音|字幕|界面|文本|文字)?语言|配音|语音|音声言語|言語)\s*[:：\r\n]",
        RegexOptions.IgnoreCase|RegexOptions.Multiline);

    internal static string Canonical(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "en" or "english" or "英语" or "英語" => "en",
        "ja" or "japanese" or "日语" or "日語" => "ja",
        "zh" or "zh-cn" or "zh-tw" or "zh-hans" or "zh-hant" or "simplified chinese" or "traditional chinese" or "中文" or "简体中文" or "繁体中文" or "繁體中文" => "zh",
        "ko" or "korean" or "韩语" => "ko", "fr" or "french" => "fr", "de" or "german" => "de",
        "es" or "spanish" => "es", "pt" or "portuguese" => "pt", "ru" or "russian" => "ru",
        var other => other
    };
    internal static string[] Normalize(IEnumerable<string>? values) =>
        (values ?? ["en", "ja"]).Select(Canonical).Where(v => Choices.Any(c => c.Code == v))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    internal static string[] Selected(ApiSettings settings) => settings.SourceLanguage switch
    {
        SourceLanguageMode.Auto => [], SourceLanguageMode.Mixed => Normalize(settings.SourceLanguages),
        SourceLanguageMode.English => ["en"], SourceLanguageMode.Japanese => ["ja"],
        SourceLanguageMode.SimplifiedChinese => ["zh"], SourceLanguageMode.Korean => ["ko"], _ => []
    };
    internal static string Identity(ApiSettings settings) => Version + ":" + settings.SourceLanguage + ":" +
        (settings.SourceLanguage == SourceLanguageMode.Mixed ? string.Join(',', Normalize(settings.SourceLanguages)) : "");

    internal static string Plain(string source)
    {
        var protectedText = EmbeddedTranslationText.Protect(source).Text;
        return Regex.Replace(protectedText, @"⟦RPG\d+⟧|</?[^>]+>|(?:\\|\x1b)[A-Za-z]+\[[^\]]*\]", " ").Trim();
    }
    internal static bool IsLanguageChoice(string source)
    {
        var value = Plain(source).Normalize(NormalizationForm.FormKC).Trim();
        if (value.Length == 0 || value.Length > 180) return false;
        if (LanguageNames.Contains(value)) return true;
        var parts = Regex.Split(value, @"\s*[/|,，;；\r\n]+\s*").Where(p => p.Length > 0).ToArray();
        return parts.Length > 1 && parts.All(LanguageNames.Contains);
    }
    internal static bool MenuNamesPreserved(string source, string translated)
    {
        string plain = Plain(source);
        if (!IsLanguageMenu(plain)) return true;
        foreach (string name in LanguageNames)
        {
            string expression = @"(?<![\p{L}\p{N}])" + Regex.Escape(name) + @"(?![\p{L}\p{N}])";
            int count = Regex.Matches(plain, expression).Count;
            if (count > 0 && Regex.Matches(Plain(translated), expression).Count != count) return false;
        }
        return true;
    }

    // Script presence is used to avoid dropping a foreign clause. A Latin
    // alphabet alone is never evidence that the text is English; a Han-only
    // label cannot distinguish Chinese from Japanese.
    internal static IReadOnlySet<string> Languages(string source)
    {
        string text = Plain(source);
        // Autonyms describe choices, not the language of the menu itself.
        // An English menu containing a Japanese option remains English.
        if(IsLanguageMenu(text))text=MenuOptionNames.Replace(text," ");
        var found = new HashSet<string>(StringComparer.Ordinal);
        // Kana in one clause is not evidence about a separate Han-only clause.
        // Keep that clause ambiguous instead of incorrectly treating a Chinese
        // sentence next to Japanese as already translated into Japanese.
        foreach (Match run in Regex.Matches(text, @"[\u3040-\u30ff\uff66-\uff9d\u3400-\u9fff]+"))
        {
            bool kana = run.Value.Any(c => c is >= '\u3040' and <= '\u30ff' or >= '\uff66' and <= '\uff9d');
            if (kana) found.Add("ja");
            else if (run.Value.Any(c => c is >= '\u3400' and <= '\u9fff'))
            {
                // Two distinct common simplified-only forms provide positive
                // evidence. Shared kanji alone, or one short label, still do not.
                // These forms differ from ordinary Japanese kanji/shinjitai.
                // Shared forms such as 国/体/学/会/画 remain ambiguous. Keep the
                // two-distinct-character threshold and evaluate each Han run
                // separately so kana in a later clause cannot absorb Chinese.
                const string simplified="这说话语们吗让认该请谢谁读译戏书东乐业买仅传伤伦侦价众优伞仪侨偿冻净删剂剑务劳卢坝汉汤沟没泽测济浓渔润湾灯灵炼爷狮现疗矿码稳纸线练织给绿缩罚脑脚脸舰药莹萤萨蚀订讲许论设访证诉词试诗误课谈谱负财货质贪贫贯贺贼资赖赚钱铁铅铜银链锅键镜饭饱饼饿馆饺鸡鹅齿龄条险题开图检关门问卖车见贝长马风边龙选项转时滚侧";
                // "文本框" is a positive Chinese UI phrase even though its
                // characters are shared Han. Keep this evidence narrow: generic
                // labels such as 透明度/文字速度 and names stay ambiguous, and
                // neither one simplified character nor kana elsewhere grants it.
                bool chineseUiPhrase=run.Value.Contains("文本框",StringComparison.Ordinal);
                found.Add(chineseUiPhrase||run.Value.Where(simplified.Contains).Distinct().Take(2).Count()>=2?"zh":"han");
            }
        }
        if (text.Any(c => c is >= '\uac00' and <= '\ud7af' or >= '\u1100' and <= '\u11ff')) found.Add("ko");
        if (text.Any(c => c is >= '\u0400' and <= '\u052f')) found.Add("ru");
        foreach(Match latinRun in Regex.Matches(text,@"[A-Za-z\u00c0-\u024f]+(?:[ '\u2019-]+[A-Za-z\u00c0-\u024f]+)*"))
        {
            string latin=latinRun.Value;
            var identified = EmbeddedSourceLanguagePolicy.Identify(latin);
            if (IsOrdinaryEndingLabel(latin)) identified = "en";
            if (identified == "latin" && Words.Matches(latin).Any(m => EnglishWords.Contains(m.Value)) &&
                !Words.Matches(latin).Any(m => OtherLatinWords.Contains(m.Value))) identified = "en";
            found.Add(identified.Length == 0 ? "latin" : identified);
            // Explicit lexical evidence can expose a short same-script mixture
            // without declaring every Latin alphabet passage to be English.
            foreach(var (code,forms) in new[]{("fr","bonjour merci sauvegarder"),("de","speichern einstellungen zurück"),("es","hola guardar ajustes"),("pt","obrigado configurações você")})
                if(Words.Matches(latin).Any(m=>forms.Split(' ').Contains(m.Value,StringComparer.OrdinalIgnoreCase)))found.Add(code);
            if(HasOrdinaryEnglishPhrase(latin)||Words.Matches(latin).Any(m=>m.Value.Length>=4&&EnglishWords.Contains(m.Value)&&!OtherLatinWords.Contains(m.Value)))found.Add("en");
            // A known foreign clause must not disappear just because a mixed
            // sentence contains more English stop words.
            if (identified == "en" && Words.Matches(latin).Any(m => OtherLatinWords.Contains(m.Value))) found.Add("latin");
        }
        // Unknown tokens beside positive evidence are often names, not a grant
        // to override a known French/Portuguese clause with English-only mode.
        if(found.Overlaps(LatinLanguages))found.Remove("latin");
        if (found.Count == 0 && text.Any(char.IsLetter))
        {
            var identified = EmbeddedSourceLanguagePolicy.Identify(text);
            found.Add(identified.Length == 0 ? "und" : identified);
        }
        return found;
    }
    internal static bool HasOrdinaryEnglishPhrase(string source)=>Words.Matches(source).Select(m=>m.Value)
        .Where(EnglishWords.Contains).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).Count()>=2;
    internal static bool AlreadyTarget(string source, ApiSettings settings)
    {
        var target = Canonical(settings.TargetLanguage);
        var languages = Languages(source);
        if (languages.Count == 0) return false;
        bool simplifiedTarget = settings.TargetLanguage.Trim().ToLowerInvariant() is
            "zh" or "zh-cn" or "zh-hans" or "simplified chinese" or "中文" or "简体中文";
        // Positive simplified-Chinese evidence is already enough to preserve a
        // purely Chinese item. Shared Han and separate foreign clauses remain
        // eligible; a game locale alone never makes their language certain.
        if (target == "zh" && simplifiedTarget && languages.SetEquals(["zh"])) return true;
        // Explicit Chinese input is an authority for Chinese, but Auto/Mixed
        // cannot identify a Japanese menu merely from its shared Han characters.
        if (target == "zh" && simplifiedTarget && settings.SourceLanguage == SourceLanguageMode.SimplifiedChinese &&
            languages.SetEquals(["han"])) return true;
        if (languages.SetEquals([target]) && target != "zh") return true;
        // An explicit single-language profile can identify an otherwise unknown
        // Latin label. Auto and custom combinations cannot make this assumption.
        return target == "en" && settings.SourceLanguage == SourceLanguageMode.English && languages.SetEquals(["latin"]);
    }
    internal static string PreserveReason(string source, ApiSettings settings)
    {
        if (IsLanguageChoice(source)) return "LANGUAGE_OPTION_NAME";
        if (AlreadyTarget(source, settings)) return "ALREADY_TARGET_LANGUAGE";
        if (settings.SourceLanguage == SourceLanguageMode.Auto) return "";
        var selected = Selected(settings).ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0) return "SOURCE_LANGUAGE_EXCLUDED";
        var languages = Languages(source);
        if (languages.Count == 0 || languages.Contains("und")) return "";
        bool Allowed(string language) => selected.Contains(language) ||
            language == "han" && selected.Overlaps(["zh", "ja"]) ||
            language == "latin" && selected.Overlaps(LatinLanguages);
        return languages.Any(Allowed) ? "" : "SOURCE_LANGUAGE_EXCLUDED";
    }
    internal static bool ShouldTranslate(string source, ApiSettings settings) => PreserveReason(source, settings).Length == 0;
    internal static string Prompt(ApiSettings settings) =>
        (settings.SourceLanguage == SourceLanguageMode.Auto ? "Automatic source selection: translate EVERY foreign language into the target, not only English or Japanese. Detect all languages within each item, including a foreign clause next to a target-language clause. " :
         "Selected source languages: " + string.Join(", ", Selected(settings)) + ". Translate only content in these languages; preserve other-language content. ") +
        "Preserve text already in the target language; do not paraphrase or retranslate it. " +
        "For a mixed-language sentence, use the complete sentence as context and translate the selected foreign-language content into natural target-language text. " +
        "In language or voice selection menus, preserve language option names/autonyms such as English, 日本語 and 中文 exactly; translate the surrounding labels and explanations normally. " +
        "Do not treat all Latin text as English or all Han characters as Chinese. Shared Han-only labels need the game's source context. " +
        "Source selection is a hard boundary: the result can intentionally contain untranslated passages in excluded languages. Fluency or the instruction to translate a complete item never overrides this boundary. ";
}
