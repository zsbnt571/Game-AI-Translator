using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Shared translation choices contain no connection fields or credentials.
internal sealed class TranslationContentSettings
{
    public string TargetLanguage { get; set; } = "zh-CN";
    public SourceLanguageMode SourceLanguage { get; set; } = SourceLanguageMode.Auto;
    public string[] SourceLanguages { get; set; } = ["en", "ja"];
    public TranslationStyle TranslationStyle { get; set; } = TranslationStyle.GameLocalization;
    public string CustomTranslationPrompt { get; set; } = "";
    public bool PreserveIdentifiers { get; set; } = true;
    public bool PreserveNumbers { get; set; } = true;
    public bool PreserveVariables { get; set; } = true;

    internal static TranslationContentSettings From(ApiSettings s) => new()
    {
        TargetLanguage=FusionConfiguration.NormalizeLanguage(s.TargetLanguage),
        SourceLanguage=s.SourceLanguage,SourceLanguages=TranslationLanguagePolicy.Normalize(s.SourceLanguages),
        TranslationStyle=s.TranslationStyle,CustomTranslationPrompt=s.CustomTranslationPrompt,
        PreserveIdentifiers=s.PreserveIdentifiers,PreserveNumbers=s.PreserveNumbers,PreserveVariables=s.PreserveVariables
    };
    internal void ApplyTo(ApiSettings s)
    {
        s.TargetLanguage=FusionConfiguration.NormalizeLanguage(TargetLanguage);
        s.SourceLanguage=SourceLanguage;s.SourceLanguages=TranslationLanguagePolicy.Normalize(SourceLanguages);
        s.TranslationStyle=TranslationStyle;s.CustomTranslationPrompt=CustomTranslationPrompt;
        s.PreserveIdentifiers=PreserveIdentifiers;s.PreserveNumbers=PreserveNumbers;s.PreserveVariables=PreserveVariables;
    }
    internal string Identity => JsonSerializer.Serialize(this);
    internal bool HasEmptyCombination => SourceLanguage==SourceLanguageMode.Mixed && TranslationLanguagePolicy.Normalize(SourceLanguages).Length==0;
}
