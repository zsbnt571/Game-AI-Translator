using System.Diagnostics;

namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private sealed record ProfileChoice(string? Id,string Label){public override string ToString()=>Label;}
    internal static string InterfaceItemText(object? item)
    {
        if(item is ProfileChoice {Id:not null} profile)
        {
            if(!UiLanguage.IsTranslated)return profile.Label;
            foreach(var suffix in new[]{("（未配置完成）",UiLanguage.IsEnglish?" (incomplete)":" (не настроен)"),("（未保存）",UiLanguage.IsEnglish?" (unsaved)":" (не сохранено)")})
                if(profile.Label.EndsWith(suffix.Item1,StringComparison.Ordinal))return profile.Label[..^suffix.Item1.Length]+suffix.Item2;
            return profile.Label;
        }
        return item?.ToString()=="关闭"?UiLanguage.Choose("关闭","Off","Выкл."):UiLanguage.Text(item?.ToString());
    }
    private readonly TextBox _schemeName=new(){Name="ProfileName",AccessibleName="方案名称"};
    private readonly CheckBox _noCredential=new(){Text="无需密钥（本地／免鉴权）",AutoSize=true};
    private readonly Label _profileOrigin=Info("");
    private readonly Label _profileUsage=Info("");
    private readonly Label _profileSaveStatus=Info("");
    private readonly Label _translationStyleDescription=Info(TranslationStylePolicy.Description(TranslationStyle.GameLocalization));
    private readonly Label _profileTestStatus=Info("连接尚未测试。测试不会保存或应用方案。");
    private readonly Button _testProfileButton=new(){Text="测试连接",Name="TestProfile",AutoSize=true,MinimumSize=new(145,36),FlatStyle=FlatStyle.Flat};
    private readonly Button _saveProfileButton=new(){Text="保存 API 方案",Name="SaveProfile",AutoSize=true,MinimumSize=new(145,36),FlatStyle=FlatStyle.Flat};
    private Control? _settingsSaveFooter;
    private ApiSettings? _draftOriginal;
    private string _draftOriginalName="",_draftOrigin="用户创建";
    private bool _draftNew,_guardingProfile;
    private int _lastSettingsCategory;
    private CancellationTokenSource? _profileTestCancellation;
    private string? _testedIdentity,_testedProfile;
    private static string? ChoiceId(ComboBox box)=>(box.SelectedItem as ProfileChoice)?.Id;
    private ApiSettings ReadProfileDraft(){var p=new ApiSettings();FusionConfiguration.ApplyConnection(p,UiSettings);p.AllowEmptyApiKey=_noCredential.Checked;return p;}
    private bool ProfileHasChanges=>_draftOriginal is not null&&(_draftNew||_schemeName.Text.Trim()!=_draftOriginalName||FusionConfiguration.ConnectionIdentity(ReadProfileDraft())!=FusionConfiguration.ConnectionIdentity(_draftOriginal));
    private bool IsProfileControl(object? sender)=>new object[]{_apiUrlBox,_apiKeyBox,_modelBox,_firstByteTimeoutBox,_requestTimeoutBox,_providerDisplayNameBox}.Contains(sender!);

    private TableLayoutPanel BuildProfileSettings()
    {
        var table=SettingsTable();table.Name="ProfileSettingsTable";
        AddSettingRow(table,"公共默认方案",_commonProfile);
        AddSettingRow(table,"",Info("公共默认及模式选择立即保存；跟随公共者随之切换，固定选择保持不变。"));
        AddSettingRow(table,"正在编辑的方案",_profileEditor);
        var create=MakeButton("新建方案",Point.Empty,new(120,34));create.AutoSize=true;create.MinimumSize=new(120,34);create.Click+=(_,_)=>NewProfile();
        var more=MakeButton("更多 ▾",Point.Empty,new(95,34));more.AutoSize=true;more.MinimumSize=new(95,34);
        var menu=new LocalizedContextMenuStrip();menu.Items.Add("重命名",null,(_,_)=>{_schemeName.Focus();_schemeName.SelectAll();_profileSaveStatus.Text="修改名称后点击“保存方案”，所有引用保持不变。";});
        menu.Items.Add("复制为独立方案",null,(_,_)=>CopyProfile());menu.Items.Add("删除方案",null,(_,_)=>DeleteProfile());more.Click+=(_,_)=>menu.Show(more,new Point(0,more.Height));
        AddSettingRow(table,"",Stack(create,more));AddSettingRow(table,"方案名称",_schemeName);
        AddSettingRow(table,"来源与使用范围",Stack(_profileOrigin,_profileUsage));
        AddSettingRow(table,"接口地址",_apiUrlBox);AddSettingRow(table,"API 密钥",_apiKeyBox);
        AddSettingRow(table,"",_noCredential);AddSettingRow(table,"模型",_modelBox);
        AddSettingRow(table,"首响应 / 总超时（秒）",Stack(_firstByteTimeoutBox,_requestTimeoutBox));
        // A flow row with explicit minimum button sizes prevents native table
        // auto-sizing from collapsing a left-anchored standalone button.
        var actions=new FlowLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=true,MinimumSize=new(0,46),Name="ProfileApiActions"};
        actions.Controls.AddRange([_testProfileButton,_saveProfileButton,_profileReturnButton,_profileBackButton]);
        _profileReturnButton.Click+=(_,_)=>ReturnFromProfile(true);_profileBackButton.Click+=(_,_)=>ReturnFromProfile(false);AddSettingRow(table,"",actions);
        AddSettingRow(table,"",_profileTestStatus);AddSettingRow(table,"",_profileSaveStatus);
        AddSettingRow(table,"",Info("API 方案只保存接口、密钥、模型和连接超时。下方翻译内容独立保存，切换 API 方案不会改变它。"));
        foreach(var pair in new (string,Control)[]{("目标语言",_targetLanguageBox),("原文语言",LanguageCombinationPicker.WithMode(_sourceLanguageBox,_sourceLanguageChoices)),("翻译风格",_translationStyleBox)})AddSettingRow(table,pair.Item1,pair.Item2);
        _translationStyleDescription.Name="TranslationStyleDescription";
        AddSettingRow(table,"",_translationStyleDescription);
        AddSettingRow(table,"",Info(TranslationStylePolicy.FidelityHint));
        AddSettingRow(table,"用户提示",_customPromptBox);
        AddSettingRow(table,"",Info(TranslationStylePolicy.ProfileHint));
        _translationStyleBox.SelectedIndexChanged+=(_,_)=>_translationStyleDescription.Text=TranslationStylePolicy.Description(TranslationStyleSelection.Read(_translationStyleBox));
        AddSettingRow(table,"文字保护",Stack(_preserveIdentifiersBox,_preserveNumbersBox,_preserveVariablesBox));
        AddSettingRow(table,"",Stack(_saveTranslationContentButton));
        AddSettingRow(table,"",_translationContentStatus);
        _saveTranslationContentButton.Click+=(_,_)=>SaveTranslationContentEditor();
        _schemeName.TextChanged+=(_,_)=>ProfileEdited();_noCredential.CheckedChanged+=(_,_)=>ProfileEdited();
        _testProfileButton.Click+=async(_,_)=>await TestProfileAsync();_saveProfileButton.Click+=(_,_)=>SaveProfileEditor();
        return table;
    }
    private void FillChoices(ComboBox box,string? selected,bool following,bool editor=false)
    {
        box.Items.Clear();if(following)box.Items.Add(new ProfileChoice(null,FollowCommon));
        foreach(var pair in _fusion!.State.Profiles.OrderBy(x=>FusionConfiguration.MissingConnection(x.Value).Count>0))
        {
            var missing=FusionConfiguration.MissingConnection(pair.Value);var current=pair.Key==selected;
            if(!editor&&missing.Count>0&&!current)continue;
            box.Items.Add(new ProfileChoice(pair.Key,_fusion.Name(pair.Key)+(missing.Count>0?"（未配置完成）":"")));
        }
        if(editor&&_draftNew&&_editingProfile is not null)box.Items.Add(new ProfileChoice(_editingProfile,_schemeName.Text+"（未保存）"));
        for(var i=0;i<box.Items.Count;i++)if(box.Items[i] is ProfileChoice choice&&choice.Id==selected){box.SelectedIndex=i;break;}
    }
    private void PopulateProfiles()
    {
        _loadingFusion=true;
        var fusion=_fusion!;
        try{FillChoices(_commonProfile,fusion.State.CommonProfile,false);FillChoices(_shotProfile,fusion.State.ScreenshotProfile,true);FillChoices(_embeddedProfile,_selectedGame is { } game?fusion.GameProfileId(game.ExePath):fusion.State.EmbeddedProfile,_selectedGame is null);FillChoices(_profileEditor,_editingProfile,false,true);}
        finally{_loadingFusion=false;}
    }
    private void LoadFusionUi()
    {
        _editingProfile=_fusion!.State.CommonProfile;PopulateProfiles();
        _loadingFusion=true;_embeddedKey.SetShortcut(GameShortcut.TryNormalize(_fusion.State.EmbeddedToggleKey,out var loadedKey)?loadedKey:"F8");_loadingFusion=false;
        // Restored game recognition runs asynchronously after the first paint.
        RefreshFusionChrome();
    }
    private void LoadEditedProfile()
    {
        if(_editingProfile is null||!_fusion!.State.Profiles.TryGetValue(_editingProfile,out var p))return;
        _draftNew=false;_draftOrigin=_fusion.State.ProfileInfo[_editingProfile].Origin;LoadDraft(p,_fusion.Name(_editingProfile));
    }
    private void LoadDraft(ApiSettings p,string name)
    {
        _loadingFusion=true;_applyingSettings=true;
        try{
            _draftOriginal=ApiSettingsSnapshot.Copy(p);_draftOriginalName=name;_schemeName.Text=name;
            _apiUrlBox.Text=p.ApiUrl;_apiKeyBox.Text=p.ApiKey;_modelBox.Text=p.Model;_noCredential.Checked=p.AllowEmptyApiKey;
            _providerDisplayNameBox.Text=p.ProviderDisplayName;
            _firstByteTimeoutBox.Value=p.FirstByteTimeoutSeconds;_requestTimeoutBox.Value=p.RequestTimeoutSeconds;
        }finally{_loadingFusion=false;_applyingSettings=false;}
        PopulateProfiles();ProfileEdited();RefreshFusionChrome();
    }
    private void ProfileEdited()
    {
        if(_loadingFusion||_applyingSettings||_draftOriginal is null)return;
        var p=ReadProfileDraft();SafeDiagnosticOutput.RegisterCredential(p.ApiKey);
        var identity=FusionConfiguration.ConnectionIdentity(p);
        if(_testedIdentity!=identity||_testedProfile!=_editingProfile)
        { _profileTestCancellation?.Cancel();_profileTestStatus.Text="当前编辑内容尚未测试。测试不会保存或应用方案。"; }
        var missing=FusionConfiguration.MissingConnection(p);
        _profileSaveStatus.Text=(ProfileHasChanges?"未保存修改":"已保存")+(missing.Count>0?" · 草稿，缺少："+string.Join("、",missing):" · 配置完整，连接结果见上方");
        var host=Uri.TryCreate(p.ApiUrl,UriKind.Absolute,out var uri)?uri.Host:"尚未填写地址";
        _profileOrigin.Text=$"来源：{_draftOrigin} · 接收服务：{host}\n协议：OpenAI 兼容格式（实际请求发送至上方接口地址）";
        var refs=_editingProfile is null?[]:_fusion!.References(_editingProfile);
        _profileUsage.Text=refs.Length>0?"配置引用："+string.Join("；",refs)+"。保存会影响这些功能后续使用的方案，不表示任务正在运行。":"没有模式使用此方案，保存不会自动应用。";
    }
    private bool SaveProfileEditor()
    {
        if(_editingProfile is null)return false;
        RememberPosition(_pages["设置"]);
        try{
            var p=ReadProfileDraft();var missing=FusionConfiguration.MissingConnection(p);
            if(missing.Count>0&&AppDialog.Show(this,"保存未完成草稿","仍缺少："+string.Join("、",missing)+"。保存为草稿，补全前不能用于翻译。",AppDialogKind.Confirmation)!=DialogResult.Yes)return false;
            _fusion!.SaveProfile(_editingProfile,_schemeName.Text,p,_draftOrigin);LoadEditedProfile();
            _profileSaveStatus.Text="保存成功 · "+_fusion.Name(_editingProfile)+(missing.Count>0?"（未完成草稿）":" · 已从文件重载复核整份方案");
            return true;
        }catch(Exception ex){_profileSaveStatus.Text="保存失败，编辑内容已保留："+SafeDiagnosticOutput.ExceptionSummary(ex);return false;}
        finally{RestorePosition(_pages["设置"]);}
    }
    private bool ConfirmProfileLeave()
    {
        if(_guardingProfile||!ProfileHasChanges)return true;
        _guardingProfile=true;
        try{
            var result=FusionProfileDialogs.Unsaved(this,_schemeName.Text,UiSettings);
            if(result==DialogResult.Cancel)return false;
            if(result==DialogResult.Yes)return SaveProfileEditor();
            _profileTestCancellation?.Cancel();
            if(_draftNew)_editingProfile=_fusion!.State.CommonProfile;
            LoadEditedProfile();return true;
        }finally{_guardingProfile=false;}
    }
    private void SwitchEditingProfile()
    {
        if(_loadingFusion)return;var chosen=ChoiceId(_profileEditor);
        if(chosen==_editingProfile)return;
        if(!ConfirmProfileLeave()){PopulateProfiles();return;}
        _editingProfile=chosen;_profileTestCancellation?.Cancel();LoadEditedProfile();
    }
    private void NewProfile()
    {
        if(!ConfirmProfileLeave())return;
        var template=FusionProfileDialogs.Template(this,UiSettings);if(template is null)return;
        var p=ApiSettingsSnapshot.Copy(CurrentSettings);p.ApiKey="";p.CustomTranslationPrompt="";
        p.ApiUrl=template.Value switch{0=>"https://api.deepseek.com",1=>"https://api.openai.com/v1",_=>""};
        p.Model=template.Value switch{0=>"deepseek-v4-flash",1=>"gpt-4.1-mini",_=>""};p.AllowEmptyApiKey=template.Value==3;
        _editingProfile=Guid.NewGuid().ToString("N");_draftNew=true;_draftOrigin="用户从接口模板新建";
        LoadDraft(p,UniqueProfileName("新方案"));_schemeName.Focus();_schemeName.SelectAll();
    }
    private string UniqueProfileName(string basis){var name=basis;var n=2;while(_fusion!.State.ProfileInfo.Values.Any(x=>x.Name.Equals(name,StringComparison.OrdinalIgnoreCase)))name=basis+" "+n++;return name;}
    private void CopyProfile()
    {
        if(!ConfirmProfileLeave())return;
        var p=ApiSettingsSnapshot.Copy(_draftOriginal!);var name=UniqueProfileName(_draftOriginalName+" · 副本");
        _editingProfile=Guid.NewGuid().ToString("N");_draftNew=true;_draftOrigin="用户复制";LoadDraft(p,name);
    }
    private void DeleteProfile()
    {
        if(_editingProfile is null)return;
        var refs=_fusion!.References(_editingProfile);
        if(refs.Length>0){_profileSaveStatus.Text="不能删除："+string.Join("、",refs)+"。请先在对应位置选择替代方案。";return;}
        if(_draftNew){if(ConfirmProfileLeave())LoadEditedProfile();return;}
        if(AppDialog.Show(this,"删除方案","删除“"+_schemeName.Text+"”？当前没有模式或公共默认引用它。",AppDialogKind.Confirmation)!=DialogResult.Yes)return;
        try{_fusion.DeleteProfile(_editingProfile);_editingProfile=_fusion.State.CommonProfile;LoadEditedProfile();_profileSaveStatus.Text="方案已删除，其他方案保持不变。";}
        catch(Exception ex){_profileSaveStatus.Text=SafeDiagnosticOutput.ExceptionSummary(ex);}
    }
    private void PersistChoice(ComboBox box)
    {
        if(_loadingFusion)return;
        var id=ChoiceId(box);
        try{
            if(box==_embeddedProfile&&_selectedGame is { } game) { if(id is null)throw new InvalidOperationException("请选择此游戏的翻译方案。"); _fusion!.SelectGameProfile(game.ExePath,id); }
            else _fusion!.Change(next=>{if(box==_commonProfile){if(id is null)throw new InvalidOperationException("请选择公共默认方案。");next.CommonProfile=id;}else if(box==_shotProfile)next.ScreenshotProfile=id;else next.EmbeddedProfile=id;});
            PopulateProfiles();RefreshFusionChrome();ProfileEdited();_statusLabel.Text="方案选择已保存；截图新任务使用，游戏配置需应用后生效。";
        }catch(Exception ex){PopulateProfiles();_statusLabel.Text="选择保存失败："+SafeDiagnosticOutput.ExceptionSummary(ex);}
    }
    private void EditModeProfile(bool embedded)
    {
        EnsureSettingsCategory(3);
        if(!ConfirmProfileLeave())return;
        _profileReturnPage=embedded?"内嵌翻译":"主页";
        _profileReturnButton.Visible=_profileBackButton.Visible=true;
        _profileReturnButton.Text=embedded?"保存并返回游戏":"保存并返回截图";
        _editingProfile=embedded&&_selectedGame is { } game?_fusion!.GameProfileId(game.ExePath):_fusion!.SelectedId(embedded);LoadEditedProfile();ShowPage("设置");_settingsNavigation.SelectedIndex=3;
    }
    private void SettingsCategoryChanged()
    {
        using var uiTiming=UiPerformanceTrace.Measure("settings-navigation" );
        if(_loadingFusion)return;var next=_settingsNavigation.SelectedIndex;if(next<0)return;
        if(_lastSettingsCategory==3&&next!=3&&!ConfirmSettingsLeave())
        {
            _loadingFusion=true;_settingsNavigation.SelectedIndex=3;_loadingFusion=false;
            // Native ListBox finishes the original mouse selection after this
            // event. Restore the cancelled choice once that message has ended.
            BeginInvoke((Action)(()=>{
                if(IsDisposed)return;_loadingFusion=true;_settingsNavigation.SelectedIndex=3;_loadingFusion=false;_settingsNavigation.Commit(3);
            }));
            return;
        }
        using var repaint=new WorkspacePaintBatch(_settingsTabs!);
        EnsureSettingsCategory(next);
        if(_settingsTabs!.SelectedTab is { } previous)RememberPosition(previous);
        _lastSettingsCategory=next;CancelBindingCapture();_settingsTabs.SelectedIndex=next;
        _settingsNavigation.Commit(next);
        if(_settingsTabs.SelectedTab is { } current)RestorePosition(current);
        if(_settingsSaveFooter is not null)_settingsSaveFooter.Visible=true;UpdateSettingsSaveState();
        ValidateSettingsHost("FusionSettingsNavigation");RecordPageLocation();
    }
    private void SaveFusionState()
    {
        if(_fusion is null)return;var key=_embeddedKey.Shortcut;
        if(_fusion.State.EmbeddedToggleKey!=key)_fusion.Change(next=>next.EmbeddedToggleKey=key);
    }
    private async Task TestProfileAsync()
    {
        if(!_testProfileButton.Enabled)return;
        var snapshot=ReadProfileDraft();var missing=FusionConfiguration.MissingConnection(snapshot);
        if(missing.Count>0){_profileTestStatus.Text="无法测试，请填写："+string.Join("、",missing);return;}
        _testedIdentity=FusionConfiguration.ConnectionIdentity(snapshot);_testedProfile=_editingProfile;
        var identity=_testedIdentity;var id=_editingProfile;var sw=Stopwatch.StartNew();
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(snapshot.RequestTimeoutSeconds,1,45)));
        _profileTestCancellation=cancel;_testProfileButton.Enabled=false;_profileTestStatus.Text="正在测试当前编辑内容…";
        try{
            var response=await _translationService.TranslatePlainAsync("Hello, welcome to the village.",snapshot,cancel.Token);
            if(string.IsNullOrWhiteSpace(response.Text))throw new InvalidDataException("服务返回空译文。");
            if(id==_editingProfile&&identity==FusionConfiguration.ConnectionIdentity(ReadProfileDraft()))_profileTestStatus.Text=$"测试成功 · 收到有效译文 · {sw.Elapsed.TotalSeconds:0.00} 秒 · 未自动保存";
        }catch(Exception ex){if(id==_editingProfile&&identity==FusionConfiguration.ConnectionIdentity(ReadProfileDraft()))_profileTestStatus.Text=$"测试失败 · {sw.Elapsed.TotalSeconds:0.00} 秒："+SafeDiagnosticOutput.ExceptionSummary(ex);}
        finally{_profileTestCancellation=null;if(!IsDisposed)_testProfileButton.Enabled=true;}
    }
}

internal static class FusionProfileDialogs
{
    internal static DialogResult Unsaved(Form owner,string name,ApiSettings settings)
    {
        using var f=new ProfileDialogForm("方案有未保存修改","方案有未保存修改",
            "“"+name+"”的 API 连接尚未保存。保存连接、放弃修改，或取消并继续编辑；下方翻译内容单独保存。",settings,false);
        return ModalSafety.Show(owner,f);
    }
    internal static int? Template(Form owner,ApiSettings settings)
    {
        using var f=new ProfileDialogForm("新建翻译方案","选择接口模板",
            "模板仅预填接口地址和模型，继续后可编辑。",settings,true);
        return ModalSafety.Show(owner,f)==DialogResult.OK?f.TemplateBox!.SelectedIndex:null;
    }
}
