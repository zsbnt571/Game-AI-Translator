namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private readonly Label _translationContentStatus=Info("");
    private readonly GameActionButton _saveTranslationContentButton=new("保存翻译内容")
        {Name="SaveTranslationContent",Primary=true,MinimumLogicalWidth=136};
    private bool _translationContentLoaded,_guardingTranslationContent;
    private TranslationContentSettings ReadTranslationContentDraft()=>TranslationContentSettings.From(UiSettings);
    private bool TranslationContentHasChanges=>_translationContentLoaded&&_fusion is not null&&
        ReadTranslationContentDraft().Identity!=_fusion.State.TranslationContent!.Identity;
    private bool IsTranslationContentControl(object? sender)=>new object[]{_targetLanguageBox,_sourceLanguageBox,
        _sourceLanguageChoices,_translationStyleBox,_customPromptBox,_preserveIdentifiersBox,_preserveNumbersBox,_preserveVariablesBox}.Contains(sender!);

    private void LoadTranslationContentToControls()
    {
        if(_fusion?.State.TranslationContent is not {} content)return;
        var previous=_applyingSettings;_applyingSettings=true;
        try
        {
            content.ApplyTo(_translationOptions);
            _targetLanguageBox.SelectedItem=UiTargetLanguageDisplay.DisplayFromCode(content.TargetLanguage);
            _sourceLanguageBox.SelectedIndex=(int)content.SourceLanguage;
            _sourceLanguageChoices.SelectLanguages(content.SourceLanguages);
            TranslationStyleSelection.Select(_translationStyleBox,content.TranslationStyle);
            _translationStyleDescription.Text=TranslationStylePolicy.Description(content.TranslationStyle);
            _customPromptBox.Text=content.CustomTranslationPrompt;
            _preserveIdentifiersBox.Checked=content.PreserveIdentifiers;
            _preserveNumbersBox.Checked=content.PreserveNumbers;_preserveVariablesBox.Checked=content.PreserveVariables;
            _translationContentLoaded=true;
        }
        finally { _applyingSettings=previous; }
        UpdateTranslationContentStatus();
    }
    private void UpdateTranslationContentStatus()
    {
        _translationContentStatus.Text=TranslationContentHasChanges?"翻译内容有未保存的修改":"翻译内容已保存";
    }
    private bool SaveTranslationContentEditor()
    {
        if(_fusion is null||!_translationContentLoaded)return false;
        try
        {
            _fusion.SaveTranslationContent(ReadTranslationContentDraft());
            _fusion.State.TranslationContent!.ApplyTo(_translationOptions);
            UpdateTranslationContentStatus();RefreshFusionChrome();
            _statusLabel.Text="翻译内容已保存：新截图任务使用；已运行的游戏下次启动生效。";
            return true;
        }
        catch(Exception ex)
        {
            _translationContentStatus.Text="翻译内容保存失败："+SafeDiagnosticOutput.ExceptionSummary(ex);
            return false;
        }
    }
    private bool ConfirmTranslationContentLeave()
    {
        if(_guardingTranslationContent||!TranslationContentHasChanges)return true;
        _guardingTranslationContent=true;
        try
        {
            using var dialog=new ProfileDialogForm("翻译内容有未保存的修改","翻译内容有未保存的修改",
                "保存目标语言、原文组合、风格、提示词和文字保护；也可以放弃修改，或取消并继续编辑。",UiSettings,false)
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
