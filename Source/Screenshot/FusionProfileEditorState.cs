namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private readonly ApiProfileEditorState _profileDraft = new();
    internal ApiProfileEditorState ProfileDraft => _profileDraft;
    internal event Action? ProfileNameFocusRequested;
    private string ProfileDraftName => WpfWorkspace.Enabled ? _profileDraft.Name : _schemeName.Text;
    private string ProfileAdvancedParameters
    {
        get => WpfWorkspace.Enabled ? _profileDraft.AdvancedParameters : _advancedApiParameters.Text;
        set { if (WpfWorkspace.Enabled) _profileDraft.AdvancedParameters = value; else _advancedApiParameters.Text = value; }
    }
    private string ProfileSaveMessage
    { set { _profileDraft.SaveStatus = value; if (!WpfWorkspace.Enabled) _profileSaveStatus.Text = value; } }
    private string ProfileTestMessage
    { set { _profileDraft.TestStatus = value; if (!WpfWorkspace.Enabled) _profileTestStatus.Text = value; } }
    private void UpdateNativeProfileChoices()
    {
        var all = _fusion!.State.Profiles.OrderBy(x => FusionConfiguration.MissingConnection(x.Value).Count > 0)
            .Select(pair => new ApiProfileChoice(pair.Key, _fusion.Name(pair.Key), FusionConfiguration.MissingConnection(pair.Value).Count > 0)).ToArray();
        var editor = all.AsEnumerable();
        if (_draftNew && _editingProfile is not null) editor = editor.Append(new(_editingProfile, ProfileDraftName, Unsaved: true));
        _profileDraft.SetChoices(all.Where(p => !p.Incomplete || p.Id == _fusion.State.CommonProfile).ToArray(),
            editor.ToArray(), _fusion.State.CommonProfile, _editingProfile);
    }
    private void FocusProfileName()
    {
        if (WpfWorkspace.Enabled) ProfileNameFocusRequested?.Invoke();
        else { _schemeName.Focus(); _schemeName.SelectAll(); }
    }
    internal void NativeRenameProfile() { FocusProfileName(); ProfileSaveMessage = "修改名称后点击“保存方案”，所有引用保持不变。"; }
    internal void NativeNewProfile() => NewProfile();
    internal void NativeCopyProfile() => CopyProfile();
    internal void NativeDeleteProfile() => DeleteProfile();
    internal bool NativeSaveProfile() => SaveProfileEditor();
    internal Task NativeTestProfileAsync() => TestProfileAsync();
    internal void NativeReturnFromProfile(bool save) => ReturnFromProfile(save);
    internal void NativeSelectProfile(string id)
    {
        if (_loadingFusion || id == _editingProfile) return;
        if (!ConfirmProfileLeave()) { UpdateNativeProfileChoices(); return; }
        _editingProfile = id; _profileTestCancellation?.Cancel(); LoadEditedProfile();
    }
    internal void NativeSelectCommonProfile(string id)
    {
        if (_loadingFusion || id == _fusion!.State.CommonProfile) return;
        PersistProfileChoice(id, common: true, embedded: false);
    }
    internal void NativeApplyParameterExample(string json)
    {
        try
        {
            var value = ChatRequestParameters.Parse(ProfileAdvancedParameters);
            foreach (var pair in ChatRequestParameters.Parse(json)) value[pair.Key] = pair.Value?.DeepClone();
            ProfileAdvancedParameters = value.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex) { ProfileSaveMessage = SafeDiagnosticOutput.ExceptionSummary(ex); }
    }
}
