using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ScreenshotTranslationUiTester;

// Editable connection data belongs to the page, not to hidden input controls.
// This model has no Windows Forms or WPF types and never persists on its own.
internal sealed class ApiProfileEditorState : INotifyPropertyChanged
{
    private string name = "", apiUrl = "", apiKey = "", model = "", provider = "", advanced = "";
    private int firstTimeout = 30, requestTimeout = 90;
    private bool allowEmptyKey, expanded, loading, testing, returning;
    private TranslationProviderKind providerKind;
    private string saveStatus = "", testStatus = "连接尚未测试。测试不会保存或应用方案。", origin = "", usage = "", returnLabel = "保存并返回";
    private string? commonId, editingId;
    private ApiProfileChoice[] commonChoices = [], editorChoices = [];
    public event PropertyChangedEventHandler? PropertyChanged;
    internal event Action? DraftChanged;

    public string Name { get => name; set => SetDraft(ref name, value ?? ""); }
    public string ApiUrl { get => apiUrl; set => SetDraft(ref apiUrl, value ?? ""); }
    public string ApiKey { get => apiKey; set => SetDraft(ref apiKey, value ?? ""); }
    public string Model { get => model; set => SetDraft(ref model, value ?? ""); }
    public string ProviderDisplayName { get => provider; set => SetDraft(ref provider, value ?? ""); }
    public TranslationProviderKind ProviderKind => providerKind;
    public string AdvancedParameters { get => advanced; set => SetDraft(ref advanced, value ?? ""); }
    public bool AllowEmptyKey { get => allowEmptyKey; set => SetDraft(ref allowEmptyKey, value); }
    public int FirstTimeout { get => firstTimeout; set => SetDraft(ref firstTimeout, Math.Clamp(value, 10, 90)); }
    public int RequestTimeout { get => requestTimeout; set => SetDraft(ref requestTimeout, Math.Clamp(value, 30, 180)); }
    public bool AdvancedExpanded { get => expanded; set => Set(ref expanded, value); }
    public string SaveStatus { get => saveStatus; set => Set(ref saveStatus, value); }
    public string TestStatus { get => testStatus; set => Set(ref testStatus, value); }
    public string Origin { get => origin; set => Set(ref origin, value); }
    public string Usage { get => usage; set => Set(ref usage, value); }
    public bool Testing { get => testing; set => Set(ref testing, value); }
    public bool Returning { get => returning; set => Set(ref returning, value); }
    public string ReturnLabel { get => returnLabel; set => Set(ref returnLabel, value); }
    public string? CommonId { get => commonId; private set => Set(ref commonId, value); }
    public string? EditingId { get => editingId; private set => Set(ref editingId, value); }
    public ApiProfileChoice[] CommonChoices { get => commonChoices; private set => Set(ref commonChoices, value); }
    public ApiProfileChoice[] EditorChoices { get => editorChoices; private set => Set(ref editorChoices, value); }

    internal void Load(ApiSettings settings, string profileName, bool resetDisclosure = true)
    {
        loading = true;
        try
        {
            Name = profileName; ApiUrl = settings.ApiUrl; ApiKey = settings.ApiKey; Model = settings.Model;
            ProviderDisplayName = settings.ProviderDisplayName; providerKind = settings.TranslationProviderKind;
            AllowEmptyKey = settings.AllowEmptyApiKey; FirstTimeout = settings.FirstByteTimeoutSeconds;
            RequestTimeout = settings.RequestTimeoutSeconds; AdvancedParameters = settings.AdvancedApiParameters ?? "";
            if (resetDisclosure) AdvancedExpanded = !string.IsNullOrWhiteSpace(AdvancedParameters);
        }
        finally { loading = false; }
        PropertyChanged?.Invoke(this, new(null));
    }
    internal ApiSettings Snapshot() => new()
    {
        ApiUrl = ApiUrl.Trim(), ApiKey = ApiKey, Model = Model.Trim(), AllowEmptyApiKey = AllowEmptyKey,
        ProviderDisplayName = ProviderDisplayName.Trim(), TranslationProviderKind = providerKind,
        FirstByteTimeoutSeconds = FirstTimeout, RequestTimeoutSeconds = RequestTimeout,
        AdvancedApiParameters = AdvancedParameters
    };
    internal void SetChoices(ApiProfileChoice[] common, ApiProfileChoice[] editor, string? selectedCommon, string? selectedEditor)
    {
        if (!CommonChoices.SequenceEqual(common)) CommonChoices = common;
        if (!EditorChoices.SequenceEqual(editor)) EditorChoices = editor;
        CommonId = selectedCommon; EditingId = selectedEditor;
    }
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

internal sealed record ApiProfileChoice(string Id, string Name, bool Incomplete = false, bool Unsaved = false);
