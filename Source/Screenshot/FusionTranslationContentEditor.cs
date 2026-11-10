namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private readonly TranslationContentEditorState _contentDraft = new();
    internal TranslationContentEditorState ContentDraft => _contentDraft;
    internal bool NativeSaveTranslationContent() => SaveTranslationContentEditor();
    void ITranslationContentHost.NativeOpenTranslationLibrary()
    {
        try { using var dialog=new NativeTranslationLibraryDialog(_contentDraft);dialog.ShowDialog(this);UpdateTranslationContentStatus(); }
        catch(Exception ex){TranslationContentMessage="资料未打开："+SafeDiagnosticOutput.ExceptionSummary(ex);}
    }
    private void TranslationContentEdited()
    { if (!_applyingSettings && !_loadingFusion && _translationContentLoaded) UpdateTranslationContentStatus(); }
    private string TranslationContentMessage
    {
        set { if (WpfWorkspace.Enabled) _contentDraft.Status = value; else _translationContentStatus.Text = value; }
    }
    private readonly Label _translationContentStatus=WpfWorkspace.Enabled ? null! : Info("");
    private readonly GameActionButton _saveTranslationContentButton=WpfWorkspace.Enabled ? null! : new("保存翻译内容")
        {Name="SaveTranslationContent",Primary=true,MinimumLogicalWidth=136};
    private bool _translationContentLoaded,_guardingTranslationContent;
    private bool _characterEnhancementNeedsApply;
    private int _characterEnhancementApplyRevision;
    private TranslationContentSettings ReadTranslationContentDraft()=>WpfWorkspace.Enabled?_contentDraft.Snapshot():TranslationContentSettings.From(UiSettings);
    private bool TranslationContentHasChanges=>_translationContentLoaded&&_fusion is not null&&
        ReadTranslationContentDraft().Identity!=_fusion.State.TranslationContent!.Identity;
    private bool IsTranslationContentControl(object? sender)=>new object[]{_targetLanguageBox,_sourceLanguageBox,
        _sourceLanguageChoices,_translationStyleBox,_customPromptBox,_preserveIdentifiersBox,_preserveNumbersBox,_preserveVariablesBox,_characterGenderEnhancementBox}.Contains(sender!);

    private void LoadTranslationContentToControls()
    {
        if(_fusion?.State.TranslationContent is not {} content)return;
        var previous=_applyingSettings;_applyingSettings=true;
        try
        {
            content.ApplyTo(_translationOptions);
            if (WpfWorkspace.Enabled) _contentDraft.Load(content);
            else
            {
            _targetLanguageBox.SelectedItem=UiTargetLanguageDisplay.DisplayFromCode(content.TargetLanguage);
            _sourceLanguageBox.SelectedIndex=(int)content.SourceLanguage;
            _sourceLanguageChoices.SelectLanguages(content.SourceLanguages);
            TranslationStyleSelection.Select(_translationStyleBox,content.TranslationStyle);
            _translationStyleDescription.Text=TranslationStylePolicy.Description(content.TranslationStyle);
            _customPromptBox.Text=content.CustomTranslationPrompt;
            _preserveIdentifiersBox.Checked=content.PreserveIdentifiers;
            _preserveNumbersBox.Checked=content.PreserveNumbers;_preserveVariablesBox.Checked=content.PreserveVariables;
            _characterGenderEnhancementBox.Checked=content.CharacterGenderEnhancementEnabled;
            }
            _translationContentLoaded=true;
        }
        finally { _applyingSettings=previous; }
        UpdateTranslationContentStatus();
    }
    private void UpdateTranslationContentStatus()
    {
        TranslationContentMessage=TranslationContentHasChanges?"翻译内容有未保存的修改":"翻译内容已保存";
    }
    private bool SaveTranslationContentEditor()
    {
        if(_fusion is null||!_translationContentLoaded)return false;
        try
        {
            var draft=ReadTranslationContentDraft();
            bool characterChanged=_fusion.State.TranslationContent?.CharacterGenderEnhancementEnabled!=draft.CharacterGenderEnhancementEnabled;
            _fusion.SaveTranslationContent(draft);
            _fusion.State.TranslationContent!.ApplyTo(_translationOptions);
            UpdateTranslationContentStatus();RefreshFusionChrome();
            bool showCharacterResult=characterChanged||_characterEnhancementNeedsApply;
            _characterEnhancementNeedsApply=true;
            int revision=++_characterEnhancementApplyRevision;
            if(showCharacterResult)
            {
                TranslationContentMessage="翻译内容已保存，正在应用角色增强开关…";
                DesktopStatus="翻译内容已保存，正在应用角色增强开关…";
            }
            else DesktopStatus="翻译内容已保存：新截图任务使用；已运行的游戏下次启动生效。";
            _=ApplyCharacterEnhancementAfterSaveAsync(draft.CharacterGenderEnhancementEnabled,revision,showCharacterResult);
            return true;
        }
        catch(Exception ex)
        {
            TranslationContentMessage="翻译内容保存失败："+SafeDiagnosticOutput.ExceptionSummary(ex);
            return false;
        }
    }
    private async Task ApplyCharacterEnhancementAfterSaveAsync(bool enabled,int revision,bool showResult)
    {
        string error;
        try { error=await ApplyCharacterGenderEnhancementChangeAsync(enabled); }
        catch(Exception ex) { error=SafeDiagnosticOutput.ExceptionSummary(ex); }
        if(IsDisposed||Disposing||revision!=_characterEnhancementApplyRevision)return;
        _characterEnhancementNeedsApply=!string.IsNullOrWhiteSpace(error);
        if(!showResult&&!_characterEnhancementNeedsApply)return;
        if(!_characterEnhancementNeedsApply)
        {
            TranslationContentMessage="角色增强开关已保存并应用。";
            DesktopStatus="角色增强开关已保存并应用。";
        }
        else
        {
            TranslationContentMessage="角色增强开关已保存，但未能应用到所有运行中的游戏。请再次保存以重试。";
            DesktopStatus=UiLanguage.Text("角色增强开关已保存，但未能应用到所有运行中的游戏。请再次保存以重试。")+" "+UiLanguage.Text(error);
        }
        if(TranslationContentHasChanges)UpdateTranslationContentStatus();
    }
    private bool ConfirmTranslationContentLeave()
    {
        if(_guardingTranslationContent||!TranslationContentHasChanges)return true;
        _guardingTranslationContent=true;
        try
        {
            if(WpfWorkspace.Enabled)
            {
                using var native=new NativeProfileDialog("翻译内容有未保存的修改","翻译内容有未保存的修改","保存目标语言、原文组合、风格、提示词、文字保护和角色增强；也可以放弃修改，或取消并继续编辑。",false);
                var choice=native.ShowDialog(this);
                if(choice==DialogResult.Cancel)return false;
                if(choice==DialogResult.Yes)return SaveTranslationContentEditor();
                LoadTranslationContentToControls();return true;
            }
            using var dialog=new ProfileDialogForm("翻译内容有未保存的修改","翻译内容有未保存的修改",
                "保存目标语言、原文组合、风格、提示词、文字保护和角色增强；也可以放弃修改，或取消并继续编辑。",UiSettings,false)
                {Name="UnsavedTranslationContentDialog"};
            var result=ModalSafety.Show(this,dialog);
            if(result==DialogResult.Cancel)return false;
            if(result==DialogResult.Yes)return SaveTranslationContentEditor();
            LoadTranslationContentToControls();return true;
        }
        finally { _guardingTranslationContent=false; }
    }
    private bool ConfirmSettingsLeave()=>ConfirmProfileLeave()&&ConfirmTranslationContentLeave();
}
