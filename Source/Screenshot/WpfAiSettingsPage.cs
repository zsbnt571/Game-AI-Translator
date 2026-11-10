using W = System.Windows;
using C = System.Windows.Controls;
using I = System.Windows.Input;

namespace ScreenshotTranslationUiTester;

internal sealed class WpfAiSettingsPage : C.StackPanel, IDisposable
{
    private readonly AiToolsController controller;
    private readonly bool ownsController;
    private readonly List<Action> labels=[];
    private readonly C.ComboBox saved=Choice(),backend=Choice(),auth=Choice(),model=Choice(),providerChoice=Choice();
    private readonly WpfAiModelSearch144 modelSearch;
    private readonly C.TextBox name=Input(),address=Input(),provider=Input(),harnessPath=Input(),team=Input(),priceInput=Input(),priceCache=Input(),priceOutput=Input(),priceCurrency=Input();
    private readonly C.TextBlock priceSource=TextBlock();
    private readonly C.Expander prices=new(){IsExpanded=false,Margin=new(0,8,0,16)};
    private bool priceEdited,priceSyncing;
    private readonly C.PasswordBox key=new(){MinHeight=38,Padding=new(10,7,10,7)},managementKey=new(){MinHeight=38,Padding=new(10,7,10,7)};
    private readonly C.CheckBox keyless=new(){Margin=new(0,2,0,12)};
    private readonly C.StackPanel backendFields146=new();
    private readonly C.TextBlock sharedApiHelp146=TextBlock();
    private readonly C.StackPanel apiFields=new(),harnessFields=new(),accountHelp=new(),thinkingPanel=new(),providerFields=new(),customProviderFields=new(),balanceInfo=new();
    private readonly C.Expander advanced=new(){IsExpanded=false,Margin=new(0,8,0,16)};
    private readonly C.TextBlock balanceHelp=TextBlock();
    private readonly C.Button billingPortal;
    private readonly C.Slider thinking=new(){Minimum=0,Maximum=0,TickFrequency=1,IsSnapToTickEnabled=true,SmallChange=1,LargeChange=1,MinHeight=32};
    private readonly C.TextBlock thinkingValue=TextBlock(),thinkingHelp=TextBlock(),status=TextBlock(),backendNotice=TextBlock();
    private readonly C.Button remove,save,discover,login,checkLogin,cancelLogin,openLogin,newButton;
    private readonly C.TextBlock loginState=TextBlock();
    private readonly C.TextBox loginCode=new(){IsReadOnly=true,MinHeight=34,Visibility=W.Visibility.Collapsed};
    private string loginUrl="";
    private CancellationTokenSource? loginLifetime;
    private readonly CancellationTokenSource pageLifetime=new();
    private readonly C.Expander billing=new(){IsExpanded=false,Margin=new(0,8,0,16)};
    private List<AiModelDescriptor> models=[];
    private AiAgentOptions options=new();
    private string editingId="",language="";
    private string[] effortValues=[""];
    private bool syncing,busy,disposed;
    internal event Action<string?>? Saved;
    internal WpfAiSettingsPage(AiToolsController controller,string? selectedId=null,bool ownsController=false)
    {
        this.controller=controller;this.ownsController=ownsController;WpfAiControls.ApplySlider(thinking);MaxWidth=900;
        Id(this,"AiSettingsPage");Children.Add(Label("SettingsTitle",28,true));Children.Add(Label("Separate"));
        saved.DisplayMemberPath="Label";saved.SelectedValuePath="Id";
        var chooser=new C.Grid();chooser.ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star)});chooser.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});chooser.Children.Add(saved);
        newButton=Button("New",()=>Select(null),"AiConnectionNew");newButton.Margin=new(10,0,0,0);C.Grid.SetColumn(newButton,1);chooser.Children.Add(newButton);Field(this,"Profile",chooser);
        Field(this,"Name",name);Field(this,"Auth",auth);Field(backendFields146,"LoginAssistant",backend);backendFields146.Children.Add(Label("BackendHelp"));Children.Add(backendFields146);Id(sharedApiHelp146,"AiSharedApiHelp");labels.Add(()=>sharedApiHelp146.Text=AiWorkspaceText146.Text("SharedApiHelp"));Children.Add(sharedApiHelp146);Id(backendNotice,"AiBackendNotice");Children.Add(backendNotice);
        foreach(var box in new[]{backend,auth}){box.DisplayMemberPath="Label";box.SelectedValuePath="Id";}
        providerChoice.DisplayMemberPath="Label";providerChoice.SelectedValuePath="Id";Field(providerFields,"Provider144",providerChoice);Children.Add(providerFields);
        Field(harnessFields,"HarnessPath",harnessPath);harnessFields.Children.Add(Label("ProgramHelp144"));Field(customProviderFields,"Provider",provider);customProviderFields.Children.Add(Label("ProviderIdHelp144"));harnessFields.Children.Add(customProviderFields);advanced.Content=harnessFields;advanced.SetResourceReference(C.Control.ForegroundProperty,"Fusion.Text");labels.Add(()=>advanced.Header=AiSettingsText144.Text("Advanced144"));Children.Add(advanced);
        accountHelp.Children.Add(Label("AccountScope"));var loginActions=new C.WrapPanel();login=Button("LoginAccount",()=>_=Login(false),"AiAccountLogin");checkLogin=Button("CheckLogin",()=>_=Login(true),"AiCheckLogin");cancelLogin=Button("CancelLogin",()=>loginLifetime?.Cancel(),"AiCancelLogin");openLogin=Button("OpenLogin",OpenLogin,"AiOpenLogin");foreach(var b in new[]{login,checkLogin,cancelLogin,openLogin})loginActions.Children.Add(b);accountHelp.Children.Add(loginActions);accountHelp.Children.Add(loginState);accountHelp.Children.Add(loginCode);Children.Add(accountHelp);
        Field(apiFields,"Address",address);Field(apiFields,"Key",key);labels.Add(()=>keyless.Content=new C.TextBlock{Text=AiConnectionText.Text("Keyless"),TextWrapping=W.TextWrapping.Wrap});apiFields.Children.Add(keyless);Children.Add(apiFields);
        modelSearch=new(model,_=>{if(!syncing)ApplyModel();});
        Field(this,"DefaultConnectionModel",model);discover=Button("Discover144",()=>_=Discover(),"AiDiscoverModels");discover.HorizontalAlignment=W.HorizontalAlignment.Left;Children.Add(discover);Children.Add(Label("ModelHelp144"));
        thinkingPanel.Margin=new(0,16,0,16);thinkingPanel.Children.Add(Label("Thinking",15,true));thinkingPanel.Children.Add(thinkingValue);thinkingPanel.Children.Add(thinking);thinkingPanel.Children.Add(thinkingHelp);Children.Add(thinkingPanel);
        var priceFields=new C.StackPanel{Margin=new(0,10,0,0)};priceFields.Children.Add(Label("PriceHelp"));Field(priceFields,"PriceInput",priceInput);Field(priceFields,"PriceCache",priceCache);Field(priceFields,"PriceOutput",priceOutput);Field(priceFields,"PriceCurrency",priceCurrency);priceFields.Children.Add(priceSource);prices.Content=priceFields;prices.SetResourceReference(C.Control.ForegroundProperty,"Fusion.Text");labels.Add(()=>prices.Header=AiConnectionText.Text("PriceSettings"));Id(prices,"AiPriceSettings");Id(priceInput,"AiPriceInput");Id(priceCache,"AiPriceCache");Id(priceOutput,"AiPriceOutput");Id(priceCurrency,"AiPriceCurrency");/* Detached legacy price fields preserve saved options when the connection is saved. */foreach(var field in new[]{priceInput,priceCache,priceOutput,priceCurrency})field.TextChanged+=(_,_)=>{if(!syncing&&!priceSyncing)priceEdited=true;};
        var billingFields=new C.StackPanel{Margin=new(0,10,0,0)};billingFields.Children.Add(Label("XaiHelp144"));Field(billingFields,"ManagementKey",managementKey);Field(billingFields,"TeamId",team);billing.Content=billingFields;billing.SetResourceReference(C.Control.ForegroundProperty,"Fusion.Text");labels.Add(()=>billing.Header=AiConnectionText.Text("AdvancedBilling"));Children.Add(billing);
        balanceInfo.Children.Add(balanceHelp);billingPortal=Button("BillingPortal144",OpenBilling,"AiOfficialBilling");billingPortal.HorizontalAlignment=W.HorizontalAlignment.Left;balanceInfo.Children.Add(billingPortal);Children.Add(balanceInfo);
        status.Margin=new(0,8,0,12);Id(status,"AiConnectionStatus");Children.Add(status);
        var actions=new C.WrapPanel();save=Button("Save",Save,"AiConnectionSave",true);remove=Button("Delete",Delete,"AiConnectionDelete");actions.Children.Add(save);actions.Children.Add(remove);Children.Add(actions);
        Id(saved,"AiSavedConnections");Id(name,"AiConnectionName");Id(backend,"AiBackend");Id(auth,"AiAuthMode");Id(address,"AiConnectionAddress");Id(key,"AiConnectionKey");Id(model,"AiConnectionModel");Id(thinking,"AiThinkingSlider");Id(thinkingValue,"AiThinkingValue");Id(providerChoice,"AiProviderChoice");Id(advanced,"AiAdvancedConnection");Id(billing,"AiXaiBalanceCredentials");
        foreach(var box in new[]{saved,backend,auth,model,providerChoice})box.PreviewMouseWheel+=ScrollForm;
        saved.SelectionChanged+=(_,_)=>{if(!syncing)Select(saved.SelectedValue as string);};
        backend.SelectionChanged+=(_,_)=>{if(syncing)return;if(backend.SelectedValue is string selected&&selected is "codex" or "opencode"){options.Backend=selected;ResetCapability();status.Text="";}RefreshVisibility();};
        auth.SelectionChanged+=(_,_)=>{if(syncing)return;options.AuthMode=auth.SelectedValue as string??"api";ResetCapability();RefreshVisibility();};
        providerChoice.SelectionChanged+=(_,_)=>{if(syncing)return;string selected=providerChoice.SelectedValue as string??"custom";syncing=true;provider.Text=selected;string official=AiProviderServices.OfficialAddress(selected);if(official.Length>0)address.Text=official;syncing=false;ResetCapability();RefreshVisibility();};
        address.TextChanged+=(_,_)=>{if(!syncing){ResetCapability();RefreshVisibility();}};key.PasswordChanged+=(_,_)=>{if(!syncing)ResetCapability();};
        provider.TextChanged+=(_,_)=>{if(!syncing){ResetCapability();RefreshVisibility();}};harnessPath.TextChanged+=(_,_)=>{if(!syncing)ResetCapability();};
        thinking.ValueChanged+=(_,_)=>{if(syncing)return;options.ReasoningEffort=effortValues[Math.Clamp((int)thinking.Value,0,effortValues.Length-1)];RefreshThinking();};
        controller.Changed+=ControllerChanged;Refresh();RefreshChoices();Select(selectedId);
    }
    private sealed record Option(string Id,string Label){public override string ToString()=>Label;}
    private static C.ComboBox Choice()=>new(){MinHeight=38,HorizontalContentAlignment=W.HorizontalAlignment.Stretch};
    private static C.TextBox Input()=>new(){MinHeight=38,Padding=new(10,7,10,7)};
    private static C.TextBlock TextBlock(){var t=new C.TextBlock{TextWrapping=W.TextWrapping.Wrap,FontSize=14,Margin=new(0,4,0,8)};t.SetResourceReference(C.TextBlock.ForegroundProperty,"Fusion.Text");return t;}
    private C.TextBlock Label(string key,double size=14,bool bold=false){var t=TextBlock();t.FontSize=size;t.FontWeight=bold?W.FontWeights.SemiBold:W.FontWeights.Normal;labels.Add(()=>t.Text=key.StartsWith("xAI ",StringComparison.Ordinal)?key:AiWorkspaceText146.Contains(key)?AiWorkspaceText146.Text(key):AiSettingsText144.Text(key));return t;}
    private void Field(C.Panel panel,string label,W.FrameworkElement value){panel.Children.Add(Label(label));value.Margin=new(0,0,0,12);panel.Children.Add(value);labels.Add(()=>W.Automation.AutomationProperties.SetName(value,label.StartsWith("xAI ",StringComparison.Ordinal)?label:AiWorkspaceText146.Contains(label)?AiWorkspaceText146.Text(label):AiSettingsText144.Text(label)));}
    private C.Button Button(string key,Action action,string id,bool primary=false){var b=WpfThemeResources.Button(AiSettingsText144.Text(key),null,action,primary);b.MinHeight=38;b.Margin=new(0,0,8,8);b.Padding=new(12,7,12,7);Id(b,id);labels.Add(()=>WpfThemeResources.SetButton(b,AiSettingsText144.Text(key),null,primary));return b;}
    private static void ScrollForm(object sender,I.MouseWheelEventArgs e)
    {
        if(sender is not C.ComboBox {IsDropDownOpen:false} box)return;e.Handled=true;
        for(W.DependencyObject? parent=System.Windows.Media.VisualTreeHelper.GetParent(box);parent is not null;parent=System.Windows.Media.VisualTreeHelper.GetParent(parent))
            if(parent is C.ScrollViewer scroll){for(int n=0;n<3;n++){if(e.Delta<0)scroll.LineDown();else scroll.LineUp();}break;}
    }
    private static void Id(W.DependencyObject e,string id)=>W.Automation.AutomationProperties.SetAutomationId(e,id);
    internal void Refresh()
    {
        if(disposed)return;
        if(language!=UiLanguage.Current.ToString())
        {
            language=UiLanguage.Current.ToString();foreach(var bind in labels)bind();bool prior=syncing;syncing=true;
            backend.ItemsSource=new[]{new Option("codex","Codex"),new("opencode","OpenCode")};backend.SelectedValue=options.Backend;
            auth.ItemsSource=new[]{new Option("api","API"),new("account",AiConnectionText.Text("Account"))};auth.SelectedValue=options.AuthMode;syncing=prior;
            prior=syncing;syncing=true;string selectedProvider=providerChoice.SelectedValue as string??"openai";providerChoice.ItemsSource=new[]{new Option("openai","OpenAI"),new("deepseek","DeepSeek"),new("anthropic","Anthropic"),new("gemini","Google Gemini"),new("xai","xAI / Grok"),new("custom",AiSettingsText144.Text("Custom144"))};providerChoice.SelectedValue=selectedProvider;syncing=prior;
        }
        RefreshThinking();RefreshVisibility();
    }
    private void ControllerChanged(){if(!disposed)Dispatcher.BeginInvoke(new Action(()=>{if(!disposed){RefreshChoices();RefreshVisibility();}}));}
    private void RefreshChoices(){bool prior=syncing;syncing=true;try{saved.ItemsSource=controller.Profiles.ToArray();saved.SelectedValue=editingId;}catch(Exception ex){status.Text=Error(ex);}finally{syncing=prior;}}
    private void Select(string? id)
    {
        if(busy)return;syncing=true;
        try
        {
            bool found=controller.Profiles.Any(p=>p.Id==id);editingId=found?id!:Guid.NewGuid().ToString("N");saved.SelectedValue=found?id:null;remove.IsEnabled=found;loginUrl="";loginCode.Text="";loginCode.Visibility=W.Visibility.Collapsed;loginState.Text=AiConnectionText.Text("LoginUnknown");
            var value=found?controller.GetProfileSettings(editingId):new ApiSettings{ApiUrl="",ApiKey="",Model="",RequestTimeoutSeconds=60};options=found?controller.GetProfileOptions(editingId):new(){Backend=controller.SelectedHarness,AuthMode="api"};models=[];
            if(!found){options.ProviderId="openai";value.ApiUrl=AiProviderServices.OfficialAddress("openai");}
            name.Text=found?value.ProviderDisplayName:AiConnectionText.Text("DefaultName");address.Text=value.ApiUrl;key.Password=value.ApiKey;modelSearch.SetCatalog([],value.Model);keyless.IsChecked=value.AllowEmptyApiKey;
            backend.SelectedValue=options.Backend;auth.SelectedValue=options.AuthMode;provider.Text=options.ProviderId;string providerId=AiProviderServices.Provider(value,options);providerChoice.SelectedValue=providerId is "openai" or "deepseek" or "anthropic" or "gemini" or "xai"?providerId:"custom";harnessPath.Text=options.HarnessPath;managementKey.Password=options.XaiManagementApiKey;team.Text=options.XaiTeamId;
            if(AiModelCatalogCache144.Shared.TryGet(value,DiscoveryOptions146(),out var cached)){models=cached.Models;modelSearch.SetCatalog(models,value.Model);}
            FillPrice();status.Text="";
        }
        catch(Exception ex){status.Text=Error(ex);}
        finally{syncing=false;RefreshThinking();RefreshVisibility();}
    }
    private ApiSettings Value()=>new(){ApiUrl=address.Text.Trim(),ApiKey=key.Password,Model=model.Text.Trim(),AllowEmptyApiKey=keyless.IsChecked==true,RequestTimeoutSeconds=60};
    private bool HasExecutionAssistant=>backend.SelectedValue is "codex" or "opencode";
    private AiAgentOptions Options(){if(!HasExecutionAssistant)throw new AiToolsException("ai_backend_retired");var result=options.Clone();result.Backend=(string)backend.SelectedValue;result.AuthMode=auth.SelectedValue as string??"api";result.ProviderId=result.AuthMode=="account"?"openai":provider.Text.Trim();result.HarnessPath=harnessPath.Text.Trim();result.XaiManagementApiKey=managementKey.Password;result.XaiTeamId=team.Text.Trim();if(priceEdited)result.Price=ReadPrice();return result;}
    private AiAgentOptions DiscoveryOptions146(){if(!HasExecutionAssistant)return options.Clone();var saved=Options();return AiExecutionConnection146.Resolve(saved,saved.AuthMode=="account"?saved.Backend:controller.SelectedHarness);}
    private void ResetCapability(){options.EffortBackend="";models=[];modelSearch.SetCatalog([],model.Text);options.ReasoningEffort="";options.SupportedEfforts=[];options.EffortSource="";options.EffortDiscoveredAt=null;options.Price=null;FillPrice();RefreshThinking();}
    private void ApplyModel(bool force=false)
    {
        bool same=options.EffortModelId==model.Text.Trim()&&options.EffortEndpoint==address.Text.Trim();if(same&&!force)return;
        var chosen=models.FirstOrDefault(x=>x.Id==model.Text.Trim());string previous=same?options.ReasoningEffort:"";options.SupportedEfforts=chosen?.SupportedEfforts.ToList()??[];options.ReasoningEffort=options.SupportedEfforts.Contains(previous,StringComparer.Ordinal)?previous:"";options.EffortSource=chosen?.EffortSource??"";options.EffortModelId=model.Text.Trim();options.EffortEndpoint=address.Text.Trim();options.Price=chosen?.Price is {} price?System.Text.Json.JsonSerializer.Deserialize<AiModelPrice>(System.Text.Json.JsonSerializer.Serialize(price)):null;options.Price?.BindTo(Value());options.EffortDiscoveredAt=chosen?.EffortDiscoveredAt;options.EffortBackend=options.AuthMode=="account"?options.Backend:controller.SelectedHarness;FillPrice();RefreshThinking();
    }
    private void FillPrice()
    {
        priceSyncing=true;var p=options.Price;priceInput.Text=p?.InputPerMillion?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"";priceCache.Text=p?.CachedInputPerMillion?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"";priceOutput.Text=p?.OutputPerMillion?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"";priceCurrency.Text=p?.Currency??"USD";priceSource.Text=p is null?AiConnectionText.Text("Unknown"):AiConnectionText.Text("Estimated")+" · "+(p.Source=="User settings"?AiConnectionText.Text("UserPrice"):p.Source)+(p.AsOf is {} date?" · "+date.ToLocalTime().ToString("g"):"")+(p.ScopeNote=="standard_text_base"?"\n"+AiSettingsText144.Text("EstimateScope144"):"");priceEdited=false;priceSyncing=false;
    }
    private static decimal? PriceValue(C.TextBox field)
    {
        if(string.IsNullOrWhiteSpace(field.Text))return null;
        var style=System.Globalization.NumberStyles.AllowDecimalPoint|System.Globalization.NumberStyles.AllowLeadingSign;
        if((decimal.TryParse(field.Text.Trim(),style,System.Globalization.CultureInfo.CurrentCulture,out var value)||decimal.TryParse(field.Text.Trim(),style,System.Globalization.CultureInfo.InvariantCulture,out value))&&value>=0&&value<=1000000)return value;
        throw new AiToolsException("PriceInvalid");
    }
    private AiModelPrice? ReadPrice()
    {
        if(new[]{priceInput,priceCache,priceOutput}.All(f=>string.IsNullOrWhiteSpace(f.Text)))return null;
        string currency=priceCurrency.Text.Trim().ToUpperInvariant();if(currency.Length!=3||currency.Any(c=>c<'A'||c>'Z'))throw new AiToolsException("PriceInvalid");
        var price=new AiModelPrice{InputPerMillion=PriceValue(priceInput),CachedInputPerMillion=PriceValue(priceCache),OutputPerMillion=PriceValue(priceOutput),Currency=currency,ModelId=model.Text.Trim(),Endpoint=address.Text.Trim(),Source="User settings",AsOf=DateTimeOffset.UtcNow,IsEstimate=true};price.BindTo(Value());return price;
    }
    private void RefreshThinking()
    {
        bool prior=syncing;syncing=true;var effective=DiscoveryOptions146();var values=effective.EffortsFor(Value());effortValues=new[]{""}.Concat(values.Where(x=>x.Length>0).Distinct()).ToArray();thinking.Maximum=effortValues.Length-1;thinking.Visibility=effortValues.Length>1?W.Visibility.Visible:W.Visibility.Collapsed;thinking.IsEnabled=!busy&&effortValues.Length>1;
        int selected=Array.IndexOf(effortValues,effective.EffectiveEffort(Value()));thinking.Value=Math.Max(0,selected);thinkingValue.Text=AiUiText.EffortText(selected<=0?"":effortValues[selected]);thinkingHelp.Text=effective.EffortProtocolUnsupported(Value())?AiConnectionText.Text("ai_protocol_effort_unsupported"):effortValues.Length==1?AiConnectionText.Text("ThinkingUnknown"):"";syncing=prior;
    }
    private void RefreshVisibility()
    {
        bool selected=HasExecutionAssistant,account=selected&&options.AuthMode=="account";string providerId=AiProviderServices.Provider(Value(),new(){ProviderId=provider.Text.Trim()});backendFields146.Visibility=account||!selected?W.Visibility.Visible:W.Visibility.Collapsed;((C.TextBlock)backendFields146.Children[0]).Text=selected?AiWorkspaceText146.Text("LoginAssistant"):AiConnectionText.Text("Backend");sharedApiHelp146.Visibility=!account&&selected?W.Visibility.Visible:W.Visibility.Collapsed;auth.IsEnabled=selected&&!busy;apiFields.Visibility=providerFields.Visibility=account?W.Visibility.Collapsed:W.Visibility.Visible;harnessFields.Visibility=W.Visibility.Visible;customProviderFields.Visibility=!account&&providerChoice.SelectedValue is "custom"?W.Visibility.Visible:W.Visibility.Collapsed;accountHelp.Visibility=account?W.Visibility.Visible:W.Visibility.Collapsed;billing.Visibility=!account&&providerId=="xai"?W.Visibility.Visible:W.Visibility.Collapsed;prices.Visibility=account?W.Visibility.Collapsed:W.Visibility.Visible;name.IsEnabled=address.IsEnabled=key.IsEnabled=model.IsEnabled=keyless.IsEnabled=managementKey.IsEnabled=team.IsEnabled=priceInput.IsEnabled=priceCache.IsEnabled=priceOutput.IsEnabled=priceCurrency.IsEnabled=newButton.IsEnabled=!busy;remove.IsEnabled=!busy&&saved.SelectedValue is string;discover.IsEnabled=selected&&!busy;save.IsEnabled=selected&&!busy&&!controller.Busy;saved.IsEnabled=backend.IsEnabled=provider.IsEnabled=providerChoice.IsEnabled=harnessPath.IsEnabled=!busy;login.IsEnabled=checkLogin.IsEnabled=selected&&account&&!busy;cancelLogin.Visibility=loginLifetime is not null?W.Visibility.Visible:W.Visibility.Collapsed;openLogin.Visibility=loginUrl.Length>0?W.Visibility.Visible:W.Visibility.Collapsed;
        balanceInfo.Visibility=!account&&AiProviderServices.BalanceHelp(providerId).Length>0&&providerId is not("deepseek" or "xai")?W.Visibility.Visible:W.Visibility.Collapsed;balanceHelp.Text=AiSettingsText144.Text("BalanceUnavailable144");
        backendNotice.Text=selected?"":AiConnectionText.Text("ai_backend_retired");backendNotice.Visibility=selected?W.Visibility.Collapsed:W.Visibility.Visible;W.Automation.AutomationProperties.SetHelpText(backend,selected?AiConnectionText.Text("BackendHelp"):backendNotice.Text);
    }
    private void OpenLogin()
    {
        if(Uri.TryCreate(loginUrl,UriKind.Absolute,out var url)&&url.Scheme=="https"&&url.Host=="auth.openai.com"&&url.AbsolutePath=="/codex/device")
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.AbsoluteUri){UseShellExecute=true});
    }
    private void OpenBilling()
    {
        string providerId=AiProviderServices.Provider(Value(),new(){ProviderId=provider.Text.Trim()});string url=AiProviderServices.BalanceHelp(providerId);
        if(url.Length>0)System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url){UseShellExecute=true});
    }
    private void LoginNotice(AiHarnessLoginNotice notice)
    {
        if(disposed)return;loginUrl=notice.OfficialUrl;loginCode.Text=notice.UserCode;loginCode.Visibility=notice.UserCode.Length>0?W.Visibility.Visible:W.Visibility.Collapsed;
        loginState.Text=AiConnectionText.Text(notice.Status=="logged_in"?"LoginReady":notice.Status=="awaiting_login"?"LoginPending":notice.Status=="error"?"RefreshFailed":"LoginUnknown");
        openLogin.Visibility=loginUrl.Length>0?W.Visibility.Visible:W.Visibility.Collapsed;
    }
    private async Task Login(bool check)
    {
        if(busy)return;busy=true;loginLifetime=new();RefreshVisibility();
        try
        {
            var result=check?await controller.CheckHarnessLoginAsync(Options(),loginLifetime.Token):await controller.LoginHarnessAsync(Options(),notice=>Dispatcher.BeginInvoke(new Action(()=>LoginNotice(notice))),loginLifetime.Token);
            LoginNotice(result);
        }
        catch(OperationCanceledException){loginState.Text=AiToolsText.Text("Stopped");}
        catch(Exception ex){loginState.Text=Error(ex);}
        finally{loginLifetime?.Dispose();loginLifetime=null;busy=false;if(!disposed)RefreshVisibility();}
    }
    private async Task Discover()
    {
        if(busy)return;busy=true;RefreshVisibility();status.Text=AiToolsText.Text("Working");
        try
        {
            var value=Value();var requestOptions=DiscoveryOptions146();string identity=AiModelCatalogCache144.Identity(value,requestOptions);
            var result=await controller.DiscoverModelsAsync(value,requestOptions,true).WaitAsync(pageLifetime.Token);if(disposed||identity!=AiModelCatalogCache144.Identity(Value(),DiscoveryOptions146()))return;
            if(result.Status!="available"){status.Text=AiConnectionText.Text(result.Status=="unsupported"?"ThinkingUnknown":"RefreshFailed");return;}
            string selected=model.Text;models=result.Models;syncing=true;modelSearch.SetCatalog(models,selected);syncing=false;ApplyModel(true);int count=AiModelSearch144.Filter(models,"").Count;status.Text=count==0?AiSettingsText144.Text("NoModels144"):AiSettingsText144.Text("CatalogReady144")+count;
        }
        catch(Exception ex){status.Text=Error(ex);}
        finally{busy=false;if(!disposed){RefreshVisibility();RefreshThinking();}}
    }
    private void Save(){try{if(!modelSearch.Commit()){status.Text=AiSettingsText144.Text("ChooseModel144");return;}controller.SaveProfile(editingId,name.Text.Trim(),Value(),Options());RefreshChoices();options=controller.GetProfileOptions(editingId);FillPrice();RefreshThinking();remove.IsEnabled=true;status.Text=AiConnectionText.Text("Saved");Saved?.Invoke(editingId);}catch(Exception ex){status.Text=Error(ex);}}
    private void Delete()
    {
        if(!remove.IsEnabled||W.MessageBox.Show(W.Window.GetWindow(this),AiConnectionText.Text("DeleteQuestion"),AiConnectionText.Text("SettingsTitle"),W.MessageBoxButton.YesNo,W.MessageBoxImage.Question)!=W.MessageBoxResult.Yes)return;
        try{controller.DeleteProfile(editingId);RefreshChoices();Select(null);Saved?.Invoke(null);}catch(Exception ex){status.Text=Error(ex);}
    }
    private static string Error(Exception ex){string code=ex is AiToolsException tool?tool.Code:"ai_connection_storage";return AiWorkspaceText146.Contains(code)?AiWorkspaceText146.Text(code):AiConnectionText.Text(code);}
    public void Dispose(){if(disposed)return;disposed=true;controller.Changed-=ControllerChanged;loginLifetime?.Cancel();pageLifetime.Cancel();modelSearch.Dispose();pageLifetime.Dispose();if(ownsController)controller.Dispose();}
}
