using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Shared translation choices contain no connection fields or credentials.
internal sealed class TranslationContentSettings
{
    public string TargetLanguage { get; set; } = "zh-CN";
    public bool CharacterGenderEnhancementEnabled { get; set; }
    public SourceLanguageMode SourceLanguage { get; set; } = SourceLanguageMode.Auto;
    public string[] SourceLanguages { get; set; } = ["en", "ja"];
    public TranslationStyle TranslationStyle { get; set; } = TranslationStyle.GameLocalization;
    public string PromptPresetId { get; set; } = "";
    public string PromptPresetText { get; set; } = "";
    public string[] GlossaryPackIds { get; set; } = [];
    public string CustomTranslationPrompt { get; set; } = "";
    public bool PreserveIdentifiers { get; set; } = true;
    public bool PreserveNumbers { get; set; } = true;
    public bool PreserveVariables { get; set; } = true;

    internal static TranslationContentSettings From(ApiSettings s) => new()
    {
        TargetLanguage=FusionConfiguration.NormalizeLanguage(s.TargetLanguage),
        CharacterGenderEnhancementEnabled=s.CharacterGenderEnhancementEnabled,
        SourceLanguage=s.SourceLanguage,SourceLanguages=TranslationLanguagePolicy.Normalize(s.SourceLanguages),
        PromptPresetId=s.PromptPresetId,PromptPresetText=s.PromptPresetText,GlossaryPackIds=[..s.GlossaryPackIds],
        TranslationStyle=s.TranslationStyle,CustomTranslationPrompt=s.CustomTranslationPrompt,
        PreserveIdentifiers=s.PreserveIdentifiers,PreserveNumbers=s.PreserveNumbers,PreserveVariables=s.PreserveVariables
    };
    internal void ApplyTo(ApiSettings s)
    {
        s.TargetLanguage=FusionConfiguration.NormalizeLanguage(TargetLanguage);
        s.CharacterGenderEnhancementEnabled=CharacterGenderEnhancementEnabled;
        s.CharacterGenderEnhancementEpoch=CharacterGenderEnhancementPolicy.Current;
        s.SourceLanguage=SourceLanguage;s.SourceLanguages=TranslationLanguagePolicy.Normalize(SourceLanguages);
        s.PromptPresetId=PromptPresetId;s.PromptPresetText=PromptPresetText;s.GlossaryPackIds=[..(GlossaryPackIds??[])];
        s.LibraryPacks=TranslationLibrary.Resolve(s);
        s.TranslationStyle=TranslationStyle;s.CustomTranslationPrompt=CustomTranslationPrompt;
        s.PreserveIdentifiers=PreserveIdentifiers;s.PreserveNumbers=PreserveNumbers;s.PreserveVariables=PreserveVariables;
    }
    internal string Identity => JsonSerializer.Serialize(this);
    internal bool HasEmptyCombination => SourceLanguage==SourceLanguageMode.Mixed && TranslationLanguagePolicy.Normalize(SourceLanguages).Length==0;
}
