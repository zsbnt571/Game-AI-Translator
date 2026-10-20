using W = System.Windows;
using WC = System.Windows.Controls;
namespace ScreenshotTranslationUiTester;

internal sealed class NativeTranslationSettingsDialog : NativeDialogWindow
{
    private readonly ApiSettings working;
    private readonly WC.TextBox display=NativeDialogControls.Input(),address=NativeDialogControls.Input(),model=NativeDialogControls.Input(),custom=NativeDialogControls.Input("",true);
    private readonly WC.PasswordBox credential=new(){MinHeight=36,Padding=new(10,7,10,7),Margin=new(0,0,0,12)};
    private readonly WC.ComboBox target,source,style;
    private readonly Dictionary<string,WC.CheckBox> languages=[];
    private readonly WC.CheckBox ids,numbers,variables;
    private readonly TranslationStyle[] styles;
    private readonly string[] targets=UiTargetLanguageDisplay.DisplayNames.ToArray();
    private readonly CancellationTokenSource lifetime=new();
    internal ApiSettings ResultSettings
    {
        get
        {
            var result=ApiSettingsSnapshot.Copy(working);result.TranslationProviderKind=TranslationProviderKind.OpenAiCompatible;result.TranslationProvider="openai-compatible";
            result.ProviderDisplayName=display.Text.Trim();result.ApiUrl=address.Text.Trim();result.ApiKey=credential.Password;result.Model=model.Text.Trim();
            result.TargetLanguage=UiTargetLanguageDisplay.CodeFromDisplay(targets[Math.Max(0,target.SelectedIndex)]);result.SourceLanguage=(SourceLanguageMode)Math.Max(0,source.SelectedIndex);result.SourceLanguages=languages.Where(p=>p.Value.IsChecked==true).Select(p=>p.Key).Order().ToArray();
            result.TranslationStyle=styles[Math.Max(0,style.SelectedIndex)];result.CustomTranslationPrompt=custom.Text.Trim();result.PreserveIdentifiers=ids.IsChecked==true;result.PreserveNumbers=numbers.IsChecked==true;result.PreserveVariables=variables.IsChecked==true;result.TranslationPromptVersion=TranslationPromptBuilder.Version;return result;
        }
    }
    internal NativeTranslationSettingsDialog(ApiSettings settings):base("翻译设置",800,660)
    {
        working=ApiSettingsSnapshot.Copy(settings);MinWidth=680;MinHeight=520;
        var tabs=new WC.TabControl();WC.StackPanel Page(string caption){var body=new WC.StackPanel{Margin=new(16)};tabs.Items.Add(new WC.TabItem{Header=UiLanguage.Text(caption),Content=new WC.ScrollViewer{Content=body,VerticalScrollBarVisibility=WC.ScrollBarVisibility.Auto}});return body;}
        var general=Page("常规");target=NativeDialogControls.Choice(targets,Array.IndexOf(targets,UiTargetLanguageDisplay.DisplayFromCode(settings.TargetLanguage)));source=NativeDialogControls.Choice(Enum.GetValues<SourceLanguageMode>().Select(UiStrings.SourceLanguageName),(int)settings.SourceLanguage);
        styles=TranslationStylePolicy.Modes.Concat(settings.TranslationStyle==TranslationStyle.Literal?[TranslationStyle.Literal]:Array.Empty<TranslationStyle>()).ToArray();style=NativeDialogControls.Choice(styles.Select(UiStrings.TranslationStyleName),Array.IndexOf(styles,settings.TranslationStyle));
        var choices=new WC.WrapPanel();foreach(var (code,name) in TranslationLanguagePolicy.Choices){var check=new WC.CheckBox{Content=UiLanguage.Text(name),IsChecked=TranslationLanguagePolicy.Normalize(settings.SourceLanguages).Contains(code),MinHeight=30,Margin=new(0,0,16,8)};languages.Add(code,check);choices.Children.Add(check);}
        void SourceVisibility()=>choices.Visibility=source.SelectedIndex==(int)SourceLanguageMode.Mixed?W.Visibility.Visible:W.Visibility.Collapsed;source.SelectionChanged+=(_,_)=>SourceVisibility();SourceVisibility();
        NativeAppearanceDialog.Add(general,"目标语言",target);NativeAppearanceDialog.Add(general,"原文语言",NativeDialogControls.Column(source,choices));NativeAppearanceDialog.Add(general,"翻译风格",style);
        var description=NativeDialogControls.Text(TranslationStylePolicy.Description(settings.TranslationStyle));style.SelectionChanged+=(_,_)=>description.Text=UiLanguage.Text(TranslationStylePolicy.Description(styles[Math.Max(0,style.SelectedIndex)]));NativeAppearanceDialog.Add(general,"",description);NativeAppearanceDialog.Add(general,"",NativeDialogControls.Text(TranslationStylePolicy.FidelityHint));
        var provider=Page("服务提供方");display.Text=settings.ProviderDisplayName;address.Text=settings.ApiUrl;credential.Password=settings.ApiKey;model.Text=settings.Model;
        NativeAppearanceDialog.Add(provider,"Provider 类型",NativeDialogControls.Choice(TranslationProviderRegistry.All.Select(p=>p.DisplayName)));NativeAppearanceDialog.Add(provider,"显示名称",display);NativeAppearanceDialog.Add(provider,"Base URL / API 地址",address);NativeAppearanceDialog.Add(provider,"API Key",credential);NativeAppearanceDialog.Add(provider,"模型",model);
        var status=NativeDialogControls.Text("");var test=NativeDialogControls.Button("测试 Provider",()=>{});test.Click+=async(_,_)=>{test.IsEnabled=false;status.Text=UiLanguage.Text("正在测试…");try{using var timeout=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);timeout.CancelAfter(TimeSpan.FromSeconds(30));var response=await new TranslationService().TranslatePlainAsync("Hello.",ResultSettings,timeout.Token);if(!IsDisposed)status.Text=$"HTTP {response.HttpStatusCode} · "+UiLanguage.Text("测试成功");}catch(Exception ex){if(!IsDisposed)status.Text=TranslationSettingsDialog.ClassifyException(ex);}finally{if(!IsDisposed)test.IsEnabled=true;}};NativeAppearanceDialog.Add(provider,"",NativeDialogControls.Column(test,status));
        WC.CheckBox Check(string caption,bool value)=>new(){Content=UiLanguage.Text(caption),IsChecked=value,MinHeight=36,Margin=new(0,0,12,8)};
        ids=Check("保留 ID / 代码",settings.PreserveIdentifiers);numbers=Check("保留数字",settings.PreserveNumbers);variables=Check("保留变量 / 占位符",settings.PreserveVariables);custom.Text=settings.CustomTranslationPrompt;
        var prompt=Page("提示词");NativeAppearanceDialog.Add(prompt,"用户提示",custom);NativeAppearanceDialog.Add(prompt,"文字保护",NativeDialogControls.Row(ids,numbers,variables));
        var ok=NativeDialogControls.Button("确定",()=>{if(source.SelectedIndex==(int)SourceLanguageMode.Mixed&&languages.Values.All(c=>c.IsChecked!=true)){AppDialog.Show(this,"原文语言","请至少选择一种需要翻译的原文语言。",AppDialogKind.Warning);return;}Finish(System.Windows.Forms.DialogResult.OK);},true);ok.IsDefault=true;
        Content=NativeDialogControls.Layout(tabs,NativeDialogControls.Row(ResultButton("取消",System.Windows.Forms.DialogResult.Cancel),ok),false);Closed+=(_,_)=>lifetime.Cancel();
    }
}
