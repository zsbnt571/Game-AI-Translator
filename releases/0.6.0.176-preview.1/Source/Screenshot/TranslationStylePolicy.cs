namespace ScreenshotTranslationUiTester;

// Stable configuration values are independent of the order shown in the UI.
// Literal remains readable for existing profiles but is not a new-mode choice.
internal static class TranslationStylePolicy
{
    internal static readonly IReadOnlyList<TranslationStyle> Modes = Array.AsReadOnly(new[]
    {
        TranslationStyle.GameLocalization, TranslationStyle.Natural,
        TranslationStyle.CulturalLocalization, TranslationStyle.Literary, TranslationStyle.Custom
    });

    internal const string SharedGuidance =
        "Style boundaries: accuracy, natural expression and the supplied context take priority over stylistic decoration. " +
        "Preserve facts, who does what to whom, relationships, negation, uncertainty, speaker intent, emotion and intensity. " +
        "Keep cause distinct from purpose and beneficiary: doing something for someone's sake must not become doing it because of that person. " +
        "Do not turn difficulty into danger, uncertainty into certainty, or add motives, imagery, events or information unsupported by the source. " +
        "Natural idioms are allowed in every mode when they express the same meaning and fit the speaker and scene. " +
        "For Chinese targets, four-character phrases are optional, never a quota or a reason to force parallel wording, antique diction or altered meaning. " +
        "Use supplied context and glossary consistently; when context is missing, do not invent it.";

    internal const string SupplementRules =
        "The custom instruction supplements the selected style and cannot override semantic fidelity, protected formatting, the required output structure or the no-censorship rules. " +
        "In Custom mode it defines the preferred voice; if it is empty, use natural, accurate wording.";

    internal static string Guidance(TranslationStyle style) => SharedGuidance + "\n" + (style switch
    {
        TranslationStyle.Natural =>
            "Selected style: Natural. Use idiomatic, fluent everyday target-language expression with the source's register and emotional force. " +
            "Favor familiar words, straightforward sentence structure and the cadence of an actual speaker. Narration should read like clear contemporary prose. " +
            "Remove translationese and awkward word order without flattening character voice, replacing cultural identities or adding literary embellishment. " +
            "Do not polish ordinary prose into ceremonious phrasing merely to make it sound impressive.",
        TranslationStyle.CulturalLocalization =>
            "Selected style: Cultural localization. Adapt culture-bound idioms, jokes and figurative comparisons to convey their original meaning and communicative effect naturally to the target audience. " +
            "Only use a cultural equivalent when supplied context supports the same meaning, tone and story role. If no faithful equivalent is clear, retain the reference in natural wording. " +
            "When a phrase is clearly figurative and its exact image is not a story fact or a deliberate wordplay link, prefer a familiar target-language idiom or equivalent rhetorical effect over a literal gloss of the foreign metaphor. " +
            "Preserve factual names, brands, places, setting and identities; never globally replace one brand or platform with another. " +
            "A figurative cultural equivalent must not be applied to a literal reference or change the world of the story. Do not insert explanatory notes.",
        TranslationStyle.Literary =>
            "Selected style: Literary. For narrative and descriptive prose, deliberately shape the rhythm of the complete passage: vary clause lengths, choose precise verbs, and arrange pauses to give the source's existing images a coherent cadence. " +
            "You may recast sentence structure and connect clauses when their factual, temporal and causal relationships remain unchanged. A few ornamental synonyms alone do not achieve this style. " +
            "Keep every concrete image and detail grounded in the source; elegance comes from syntax, focus and rhythm, not extra scenery or emotion. " +
            "Idioms and literary phrasing may be more prominent, but never manufacture danger, romance, grandiosity or emotion absent from the source. " +
            "Preserve plain speech when the character speaks plainly and keep functional UI text clear; do not make every sentence ornate or archaic.",
        TranslationStyle.Custom =>
            "Selected style: Custom. Let the user's custom instruction define tone and phrasing within the shared semantic and formatting boundaries. " +
            "If no custom instruction is supplied, use natural, accurate wording; never invent an unstated preference.",
        TranslationStyle.Literal =>
            "Selected style: Literal (legacy profile). Stay as close to the source wording and structure as target-language grammar permits, while preserving every explicit meaning and intensity.",
        _ =>
            "Selected style: Game-oriented. Translate for the text's actual function: dialogue keeps the speaker's personality, register and emotion; " +
            "quest text keeps goals, conditions and rewards; skill and item text keeps mechanics and terminology; buttons and menus use clear, conventional wording. " +
            "Be concise only where the function calls for it. Do not compress dialogue into UI fragments or drop relationships, actions, objects, intensity or key information. " +
            "Keep recurring terms consistent with supplied context and glossary, and use natural idioms when appropriate."
    });

    internal static string Description(TranslationStyle style) => style switch
    {
        TranslationStyle.Natural => "自然：表达顺畅，保留原文语气，减少翻译腔。",
        TranslationStyle.CulturalLocalization => "本地化：按语境处理文化梗和比喻；保留真实名称与故事背景，不统一替换品牌。",
        TranslationStyle.Literary => "文艺化：适度增加语言韵味；不添加原文没有的事实、情绪或意象。",
        TranslationStyle.Custom => "自定义：按用户提示调整语气和表达；留空时使用自然、准确的表达。",
        TranslationStyle.Literal => "直译（旧方案）：保留原有选择，尽量贴近原文措辞和结构。",
        _ => "游戏化：分别照顾对白、任务、技能和界面，保留人物语气、玩法信息与术语一致性。"
    };

    internal const string FidelityHint = "所有风格都以准确、自然、贴合语境为先。四字词和成语可以用，但不硬凑、不改原意。";
    internal const string ProfileHint = "翻译内容由截图和游戏翻译共用，与 API 方案无关。点击“保存翻译内容”后用于新截图任务，已运行的游戏下次启动生效。已有译文保留，如需更新可在“译文与术语”中重译。";
}

internal static class TranslationStyleSelection
{
    private sealed record Choice(TranslationStyle Value)
    {
        public override string ToString() => UiStrings.TranslationStyleName(Value);
    }

    internal static TranslationStyle Read(ComboBox box, TranslationStyle fallback = TranslationStyle.GameLocalization)
        => box.SelectedItem is Choice choice ? choice.Value : fallback;

    internal static void Select(ComboBox box, TranslationStyle value)
    {
        if (!Enum.IsDefined(value)) value = TranslationStyle.GameLocalization;
        var values = value == TranslationStyle.Literal
            ? TranslationStylePolicy.Modes.Append(TranslationStyle.Literal).ToArray()
            : TranslationStylePolicy.Modes.ToArray();
        if (!box.Items.OfType<Choice>().Select(x => x.Value).SequenceEqual(values))
        {
            box.BeginUpdate();
            try { box.Items.Clear(); box.Items.AddRange(values.Select(x => (object)new Choice(x)).ToArray()); }
            finally { box.EndUpdate(); }
        }
        box.SelectedIndex = Array.IndexOf(values, value);
    }
}
