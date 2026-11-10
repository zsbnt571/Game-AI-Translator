using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ScreenshotTranslationUiTester;

// A page-local draft; persistence and translation policy remain in their existing services.
internal sealed class TranslationContentEditorState : INotifyPropertyChanged
{
    private string presetId="",presetText="";
    private string[] packIds=[];
    public string PresetId { get=>presetId; set=>SetDraft(ref presetId,value??""); }
    public string PresetText { get=>presetText; set=>SetDraft(ref presetText,value??""); }
    public string[] PackIds { get=>[..packIds]; set=>SetDraft(ref packIds,value.Distinct().ToArray()); }
    internal void Choose(TranslationPreset preset)
    {
        if(preset.LegacyStyle is {} old) { Style=old; PresetText=""; PresetId=""; }
        else { PresetText=preset.Instructions; PresetId=preset.Id; }
    }
    private string targetLanguage = "zh-CN", prompt = "", status = "";
    private SourceLanguageMode sourceMode;
    private TranslationStyle style = TranslationStyle.GameLocalization;
    private string[] sourceLanguages = ["en", "ja"];
    private bool preserveIdentifiers = true, preserveNumbers = true, preserveVariables = true, characterGenderEnhancementEnabled, loading;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal event Action? DraftChanged;

    public string TargetLanguage { get => targetLanguage; set => SetDraft(ref targetLanguage, FusionConfiguration.NormalizeLanguage(value)); }
    public SourceLanguageMode SourceMode { get => sourceMode; set => SetDraft(ref sourceMode, Enum.IsDefined(value) ? value : SourceLanguageMode.Auto); }
    public string[] SourceLanguages => (string[])sourceLanguages.Clone();
    public TranslationStyle Style { get => style; set => SetDraft(ref style, Enum.IsDefined(value) ? value : TranslationStyle.GameLocalization); }
    public string Prompt { get => prompt; set => SetDraft(ref prompt, value ?? ""); }
    public bool PreserveIdentifiers { get => preserveIdentifiers; set => SetDraft(ref preserveIdentifiers, value); }
    public bool PreserveNumbers { get => preserveNumbers; set => SetDraft(ref preserveNumbers, value); }
    public bool PreserveVariables { get => preserveVariables; set => SetDraft(ref preserveVariables, value); }
    public bool CharacterGenderEnhancementEnabled { get => characterGenderEnhancementEnabled; set => SetDraft(ref characterGenderEnhancementEnabled, value); }
    public string Status { get => status; set => Set(ref status, value); }

    internal void SetSourceLanguages(IEnumerable<string>? values)
    {
        var normalized = TranslationLanguagePolicy.Normalize(values);
        if (sourceLanguages.SequenceEqual(normalized)) return;
        SetDraft(ref sourceLanguages, normalized, nameof(SourceLanguages));
    }
    internal void Load(TranslationContentSettings content)
    {
        loading = true;
        try
        {
            PresetId=content.PromptPresetId;PresetText=content.PromptPresetText;PackIds=content.GlossaryPackIds??[];
            TargetLanguage = content.TargetLanguage; SourceMode = content.SourceLanguage;
            SetSourceLanguages(content.SourceLanguages); Style = content.TranslationStyle; Prompt = content.CustomTranslationPrompt;
            PreserveIdentifiers = content.PreserveIdentifiers; PreserveNumbers = content.PreserveNumbers; PreserveVariables = content.PreserveVariables;
            CharacterGenderEnhancementEnabled = content.CharacterGenderEnhancementEnabled;
        }
        finally { loading = false; }
        PropertyChanged?.Invoke(this, new(null));
    }
    internal TranslationContentSettings Snapshot() => new()
    {
        TargetLanguage = TargetLanguage, SourceLanguage = SourceMode, SourceLanguages = SourceLanguages,
        PromptPresetId=PresetId,PromptPresetText=PresetText,GlossaryPackIds=PackIds,
        TranslationStyle = Style, CustomTranslationPrompt = Prompt,
        PreserveIdentifiers = PreserveIdentifiers, PreserveNumbers = PreserveNumbers, PreserveVariables = PreserveVariables,
        CharacterGenderEnhancementEnabled = CharacterGenderEnhancementEnabled
    };
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        if (!loading) PropertyChanged?.Invoke(this, new(property));
        return true;
    }
    private void SetDraft<T>(ref T field, T value, [CallerMemberName] string? property = null)
    { if (Set(ref field, value, property) && !loading) DraftChanged?.Invoke(); }
}
