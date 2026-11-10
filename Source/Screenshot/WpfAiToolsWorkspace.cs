using W = System.Windows;
using C = System.Windows.Controls;
using I = System.Windows.Input;

namespace ScreenshotTranslationUiTester;

// The view coordinates explicit actions only. Conversation text is never executed.
internal sealed partial class WpfAiToolsWorkspace : C.Grid, IDisposable
{
    private readonly AiToolsController controller;
    private readonly bool ownsController;
    private readonly List<Action> bindings = [];
    private readonly C.Grid configuration=new(), chat=new(), modPage=new(), composerActions=new();
    private readonly C.ScrollViewer bodyViewport=new(){VerticalScrollBarVisibility=C.ScrollBarVisibility.Disabled,HorizontalScrollBarVisibility=C.ScrollBarVisibility.Disabled};
    private readonly C.Expander history=new(){IsExpanded=false,Margin=new(0,0,0,12)};
    private readonly C.ListBox tasks=new(){DisplayMemberPath="Title",MinHeight=50};
    private readonly C.ComboBox games=new(){DisplayMemberPath="Name",SelectedValuePath="Id",IsEditable=true,IsTextSearchEnabled=false,StaysOpenOnEdit=true,MinHeight=40};
    private readonly C.ComboBox profiles=new(){DisplayMemberPath="Label",SelectedValuePath="Id",IsTextSearchEnabled=true,MinHeight=36};
    private readonly C.ComboBox harnessChoice=new(){DisplayMemberPath="Label",SelectedValuePath="Id",MinHeight=40,HorizontalContentAlignment=W.HorizontalAlignment.Stretch};
    private readonly C.ComboBox chatModel=new(){IsEditable=true,IsTextSearchEnabled=false,StaysOpenOnEdit=true,MinHeight=36,HorizontalContentAlignment=W.HorizontalAlignment.Stretch};
    private readonly C.Grid composerChoices=new();
    private readonly C.Button fetchModels;
    private readonly C.TextBlock nativeState=Label(13),workspaceState=Label(12),approvalTitle=Label(15,true);
    private readonly C.TextBox nativeOutput=ReadOnlyText(),approvalDetails=ReadOnlyText();
    private readonly C.Border nativeActivity=Card(),nativeApprovalCard=Card();
    private readonly C.WrapPanel approvalActions=new();
    private readonly C.Border nativeQuestionCard=Card();
    private readonly C.StackPanel questionBody=new();
    private readonly List<(C.Button Button,string Key)> questionButtons=[];
    private readonly C.Button permissionButton,workspaceButton,unlinkGame;
    private readonly WpfAiOverlay144 permissionPopup=new();
    private readonly C.StackPanel permissionChoices=new();
    private readonly C.ComboBox nativeAgent=new(){DisplayMemberPath="Label",SelectedValuePath="Id",MinHeight=36};
    private readonly Dictionary<string,AiModelDescriptor> modelCatalog=new(StringComparer.Ordinal);
    private object? displayedQuestion;
    private CancellationTokenSource? modelLifetime;
    private bool modelBusy;
    private string modelCatalogProfile="";
    private string? modelDraft146;
    private object? displayedApproval;
    private sealed record HarnessChoice(string Id,string Label) { public override string ToString()=>Label; }
    private readonly C.Grid sidebar=new();
    private readonly C.Grid billingPanel=new();
    private readonly WpfAiOverlay144 thinkingPopup=new(),billingPopup=new();
    private readonly C.TextBlock gamePlaceholder=Label(14),popupModel=Label(15,true),balanceDetail=Label(),roundCostDetail=Label();
    private readonly C.Button thinkingButton,billingButton;
    private readonly C.TextBlock balance=Label(),roundCost=Label();
    private bool thinkingDragging145;
    private string thinkingDragTask145="",thinkingDragProfile145="",thinkingDragModel145="";
    private readonly C.WrapPanel modGames=new();
    private readonly C.ScrollViewer modGameScroll=new(){VerticalScrollBarVisibility=C.ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=C.ScrollBarVisibility.Disabled};
    private readonly C.Grid modList=new();
    private readonly C.Button balanceRefresh,balanceLink;
    private readonly C.Slider thinkingSlider=new(){Minimum=0,Maximum=0,TickFrequency=1,IsSnapToTickEnabled=true,SmallChange=1,LargeChange=1,MinHeight=24};
    private readonly C.TextBlock thinkingState=Label(13);
    private string[] effortValues=[""];
    private string requestedBalanceProfile="",requestedBalanceKey="",completedBalanceKey="";
    private bool balanceBusy;
    private readonly Dictionary<string,string> drafts=new(StringComparer.Ordinal),lastTaskByGame=new(StringComparer.Ordinal),lastProfileByGame=new(StringComparer.Ordinal);
    private string draftKey="",selectedGameId="",selectedProfileId="",localStatusTarget="";
    private bool restoringDraft;
    private sealed record ChatContext(string? TaskId,string? GameId,string? ProfileId,bool History);
    private ChatContext? modChatContext;
    private sealed record TaskRow(AiTaskRecord Task,string GameName) { public string Title=>(Task.IsPinned?"📌 ":"")+(Task.Messages.Count==0&&!Task.TitleIsCustom?AiToolsText.Text("NewConversation"):Task.Title); public string Hint=>GameName+"\n"+Task.Title; public string OptionsId=>"AiTaskOptions_"+Task.Id; public string OptionsLabel=>AiWorkspaceText146.Text("TaskOptions")+" · "+Title; }
    private string? modGameId;
    private string modGamesSignature="";
    private int modArtworkEpoch;
    private readonly C.TextBlock gameState=Label(),apiState=Label(),status=Label(),usage=Label(),budget=Label(),sourceName=Label(),emptyMods=Label();
    private readonly C.TextBlock welcomeTitle=Label(22,true),welcomeBody=Label(15),inputPlaceholder=Label(15);
    private readonly C.Border welcomeCard=Card(),conversationCard=Card(),proposalCard=Card();
    private readonly C.WrapPanel examples=new();
    private readonly C.StackPanel messages=new(),attachment=new();
    private readonly C.Border composer=Card();
    private readonly C.ScrollViewer conversation=new(){VerticalScrollBarVisibility=C.ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=C.ScrollBarVisibility.Disabled};
    private readonly C.TextBox input=new(){AcceptsReturn=true,TextWrapping=W.TextWrapping.Wrap,MinHeight=52,MaxHeight=112,MaxLength=32000,FontSize=15,VerticalScrollBarVisibility=C.ScrollBarVisibility.Auto};
    private readonly C.TextBox proposal=ReadOnlyText(),modBefore=ReadOnlyText(),modAfter=ReadOnlyText();
    private readonly C.ListBox mods=new(){HorizontalContentAlignment=W.HorizontalAlignment.Stretch};
    private readonly C.TextBlock modPath=Label();
    private readonly C.Border details=new(){Visibility=W.Visibility.Collapsed,Margin=new(0,8,0,0)};
    private readonly C.Expander modDetails=new(){IsExpanded=false,Margin=new(0,14,0,0)};
    private readonly C.Button modBack,send,stop,confirm,cancelProposal,newTask,assistantTab,modTab,attach,import,export,install,disable,configure,welcomeAction;
    private readonly W.Threading.DispatcherTimer refreshTimer=new(){Interval=TimeSpan.FromSeconds(3)};
    private readonly List<(C.TextBlock Role,C.TextBox Body,string RoleKey,string Original,bool IsStatus)> renderedMessages=[];
    private string renderedTask="",localStatus="",query="",tasksSignature="",gamesSignature="\0",profilesSignature="\0",modsSignature="\0";
    private ApplicationLanguage language=(ApplicationLanguage)(-1);
    private ThemePalette? palette;
    private bool syncing,disposed,pendingRefresh,showMods,operationBusy,polling,modIntent,viewingHistory;

    internal WpfAiToolsWorkspace(MainForm main):this(main.SharedAiController,false) { }
    internal WpfAiToolsWorkspace(AiToolsController controller,bool ownsController=true)
    {
        this.controller=controller;this.ownsController=ownsController;WpfAiControls.ApplySlider(thinkingSlider);Margin=new(20,12,20,12);HorizontalAlignment=W.HorizontalAlignment.Stretch;Id(this,"FusionAiTools");SetResourceReference(BackgroundProperty,"Fusion.Main");
        ColumnDefinitions.Add(new(){Width=new(208)});ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star)});
        for(int n=0;n<5;n++)RowDefinitions.Add(new(){Height=n==3?new(1,W.GridUnitType.Star):W.GridLength.Auto});
        // One lightweight game context. Plan, model and reasoning belong to the composer.
        configuration.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});configuration.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});configuration.ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star)});configuration.Height=40;configuration.Margin=new(0,0,0,8);Id(configuration,"AiContextToolbar");
        var gameLabel=Text("GameSelection",14);gameLabel.Margin=new(0,0,10,0);gameLabel.Visibility=W.Visibility.Collapsed;configuration.Children.Add(gameLabel);games.Height=36;games.MinHeight=36;games.Width=240;games.VerticalContentAlignment=W.VerticalAlignment.Center;C.Grid.SetColumn(games,1);configuration.Children.Add(games);gamePlaceholder.IsHitTestVisible=false;gamePlaceholder.TextWrapping=W.TextWrapping.NoWrap;gamePlaceholder.TextTrimming=W.TextTrimming.CharacterEllipsis;gamePlaceholder.HorizontalAlignment=W.HorizontalAlignment.Left;gamePlaceholder.Margin=new(12,0,30,0);gamePlaceholder.SetResourceReference(C.TextBlock.ForegroundProperty,"Fusion.Muted");C.Grid.SetColumn(gamePlaceholder,1);configuration.Children.Add(gamePlaceholder);gameState.FontSize=14;gameState.Margin=new(12,0,0,0);gameState.TextWrapping=W.TextWrapping.NoWrap;gameState.TextTrimming=W.TextTrimming.CharacterEllipsis;C.Grid.SetColumn(gameState,2);gameState.Visibility=W.Visibility.Collapsed;configuration.Children.Add(gameState);C.Grid.SetColumn(configuration,1);Children.Add(configuration);
        assistantTab=Button("Assistant",()=>SwitchPage(false),"AiAssistantTab");modTab=Button("ModLibrary",()=>SwitchPage(true),"AiModLibraryTab");configure=Button("OpenAiSettings",ConfigureProfile,"AiConfigureApi");
        Id(thinkingSlider,"AiThinkingSlider");
        thinkingSlider.AddHandler(C.Primitives.Thumb.DragStartedEvent,new C.Primitives.DragStartedEventHandler((_,_)=>{thinkingDragging145=true;thinkingDragTask145=controller.Current?.Id??"";thinkingDragProfile145=profiles.SelectedValue as string??"";thinkingDragModel145=controller.CurrentModel;}),true);
        thinkingSlider.AddHandler(C.Primitives.Thumb.DragCompletedEvent,new C.Primitives.DragCompletedEventHandler((_,e)=>{thinkingDragging145=false;if(!e.Canceled&&thinkingDragTask145==(controller.Current?.Id??"")&&thinkingDragProfile145==(profiles.SelectedValue as string??"")&&thinkingDragModel145==controller.CurrentModel)CommitThinking145();else Refresh();}),true);
        thinkingSlider.ValueChanged+=(_,_)=>{if(syncing)return;thinkingState.Text=AiUiText.EffortText(SelectedThinking145());if(!thinkingDragging145)CommitThinking145();};
        Id(games,"AiGame");Id(profiles,"AiProfile");games.PreviewMouseWheel+=NoWheelSelection;profiles.PreviewMouseWheel+=NoWheelSelection;
        games.SelectionChanged+=(_,_)=>GameSelected();games.AddHandler(C.TextBox.TextChangedEvent,new C.TextChangedEventHandler((_,e)=>{if(e.OriginalSource is C.TextBox edit)GameQueryChanged(edit.Text);}));
        profiles.SelectionChanged+=(_,_)=>{if(syncing)return;if(controller.Busy||operationBusy){syncing=true;try{profiles.SelectedValue=selectedProfileId;}finally{syncing=false;}return;}modelDraft146=null;modelLifetime?.Cancel();modelCatalogProfile="";modelCatalogAttempt144="";modelCatalog.Clear();chatModel.ItemsSource=null;if(profiles.SelectedValue is string){Run(()=>EnsureTask());_=FetchModels();}else Refresh();};
        harnessChoice.ItemsSource=new[]{new HarnessChoice("codex","Codex"),new("opencode","OpenCode")};Id(harnessChoice,"AiHarness");harnessChoice.PreviewMouseWheel+=NoWheelSelection;harnessChoice.SelectionChanged+=(_,_)=>HarnessSelected();
        Id(chatModel,"AiChatModel");chatModel.AddHandler(C.TextBox.TextChangedEvent,new C.TextChangedEventHandler((_,_)=>{if(!syncing&&modelDraft146 is not null)modelDraft146=chatModel.Text;}));chatModel.PreviewMouseWheel+=NoWheelSelection;chatModel.SelectionChanged+=(_,_)=>{if(!syncing&&chatModel.SelectedItem is string selected)CommitModel(selected);};chatModel.PreviewKeyDown+=(_,e)=>{if(e.Key==I.Key.Enter){e.Handled=true;CommitModel();input.Focus();}};

        var taskRows=new W.FrameworkElementFactory(typeof(C.StackPanel));var taskLabel=new W.FrameworkElementFactory(typeof(C.TextBlock));taskLabel.SetBinding(C.TextBlock.TextProperty,new System.Windows.Data.Binding("Title"));taskLabel.SetBinding(W.FrameworkElement.ToolTipProperty,new System.Windows.Data.Binding("Hint"));taskLabel.SetValue(C.TextBlock.TextTrimmingProperty,W.TextTrimming.CharacterEllipsis);taskRows.AppendChild(taskLabel);var taskGame=new W.FrameworkElementFactory(typeof(C.TextBlock));taskGame.SetBinding(C.TextBlock.TextProperty,new System.Windows.Data.Binding("GameName"));taskGame.SetValue(C.TextBlock.FontSizeProperty,13d);taskGame.SetValue(W.FrameworkElement.MarginProperty,new W.Thickness(0,3,0,0));taskGame.SetResourceReference(C.TextBlock.ForegroundProperty,"Fusion.Muted");taskGame.SetValue(C.TextBlock.TextTrimmingProperty,W.TextTrimming.CharacterEllipsis);taskRows.AppendChild(taskGame);tasks.DisplayMemberPath="";tasks.ItemTemplate=new W.DataTemplate{VisualTree=taskRows};tasks.HorizontalContentAlignment=W.HorizontalAlignment.Stretch;C.ScrollViewer.SetHorizontalScrollBarVisibility(tasks,C.ScrollBarVisibility.Disabled);
        tasks.BorderThickness=new(0);tasks.SetResourceReference(C.Control.BackgroundProperty,"Fusion.Secondary");history.Content=tasks;history.IsExpanded=true;history.SetResourceReference(C.Control.ForegroundProperty,"Fusion.Text");Id(tasks,"AiTasks");tasks.MaxHeight=double.PositiveInfinity;
        tasks.SelectionChanged+=(_,_)=>{if(syncing)return;if(controller.Busy||operationBusy||taskActionNavigation146){syncing=true;try{tasks.SelectedItem=tasks.Items.OfType<TaskRow>().FirstOrDefault(x=>x.Task.Id==controller.Current?.Id);}finally{syncing=false;}return;}if(tasks.SelectedItem is TaskRow row)Run(()=>{ClosePopups(false);query="";controller.SelectTask(row.Task.Id);showMods=false;modIntent=false;viewingHistory=true;});};
        var body=new C.Grid();body.Children.Add(chat);body.Children.Add(modPage);bodyViewport.Content=body;C.Grid.SetRow(bodyViewport,3);C.Grid.SetColumn(bodyViewport,1);Children.Add(bodyViewport);
        for(int n=0;n<4;n++)chat.RowDefinitions.Add(new(){Height=n==0?new(1,W.GridUnitType.Star):W.GridLength.Auto});
        var content=new C.Grid{MaxWidth=860};Id(content,"AiConversationArea");content.Children.Add(welcomeCard);content.Children.Add(conversationCard);chat.Children.Add(content);
        welcomeCard.MaxWidth=820;welcomeCard.HorizontalAlignment=W.HorizontalAlignment.Stretch;welcomeCard.VerticalAlignment=W.VerticalAlignment.Center;welcomeCard.Padding=new(12);welcomeCard.BorderThickness=new(0);welcomeCard.Background=System.Windows.Media.Brushes.Transparent;var welcome=new C.StackPanel();welcomeTitle.Margin=new(0,0,0,12);welcomeBody.Margin=new(0,0,0,16);welcome.Children.Add(welcomeTitle);welcome.Children.Add(welcomeBody);
        welcomeAction=Button("OpenAiSettings",ConfigureProfile,"AiWelcomeAction",true);welcomeAction.HorizontalAlignment=W.HorizontalAlignment.Left;welcome.Children.Add(welcomeAction);
        examples.Orientation=C.Orientation.Horizontal;examples.Children.Add(Button("NumericExample",()=>FillPrompt("NumericPrompt",false),"AiNumericExample"));examples.Children.Add(Button("ModExample",()=>FillPrompt("ModPrompt",true),"AiModExample"));welcome.Children.Add(examples);welcomeCard.Child=new C.ScrollViewer{Content=welcome,VerticalScrollBarVisibility=C.ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=C.ScrollBarVisibility.Disabled};
        messages.MaxWidth=double.PositiveInfinity;messages.HorizontalAlignment=W.HorizontalAlignment.Stretch;conversation.HorizontalContentAlignment=W.HorizontalAlignment.Stretch;conversationCard.MaxWidth=860;var conversationBody=new C.StackPanel();conversationBody.Children.Add(messages);nativeActivity.Padding=new(12);nativeActivity.Margin=new(0,0,0,16);nativeActivity.BorderThickness=new(0);nativeActivity.Visibility=W.Visibility.Collapsed;var nativeBody=new C.StackPanel();nativeState.Margin=new(0,0,0,6);Id(nativeState,"AiNativeState");nativeBody.Children.Add(nativeState);nativeOutput.MaxHeight=260;nativeOutput.Background=System.Windows.Media.Brushes.Transparent;Id(nativeOutput,"AiNativeOutput");nativeBody.Children.Add(nativeOutput);nativeActivity.Child=nativeBody;conversationBody.Children.Add(nativeActivity);conversation.Content=conversationBody;Id(conversation,"AiConversation");conversationCard.Padding=new(8,4,8,4);conversationCard.BorderThickness=new(0);conversationCard.Background=System.Windows.Media.Brushes.Transparent;conversationCard.Child=conversation;conversationCard.MinHeight=65;
        var decisions=new C.StackPanel();C.Grid.SetRow(decisions,1);chat.Children.Add(decisions);var proposalPanel=new C.StackPanel();proposalPanel.Children.Add(Text("Pending",15,true));proposalPanel.Children.Add(proposal);var proposalActions=new C.WrapPanel();confirm=Button("Confirm",()=>_=RunAsync(controller.ConfirmAsync),"AiConfirmChange",true);cancelProposal=Button("CancelProposal",()=>Run(controller.CancelProposal),"AiCancelChange");proposalActions.Children.Add(confirm);proposalActions.Children.Add(cancelProposal);proposalPanel.Children.Add(proposalActions);proposalCard.Child=proposalPanel;proposalCard.Margin=new(0,10,0,0);decisions.Children.Add(proposalCard);
        nativeApprovalCard.MaxWidth=860;nativeQuestionCard.MaxWidth=860;proposalCard.MaxWidth=860;nativeApprovalCard.Margin=new(0,8,0,0);nativeApprovalCard.Visibility=W.Visibility.Collapsed;Id(nativeApprovalCard,"AiNativeApproval");
        var approvalBody=new C.StackPanel();approvalBody.Children.Add(Text("NativeApproval",14,true));approvalBody.Children.Add(approvalTitle);
        approvalDetails.MaxHeight=130;approvalDetails.Background=System.Windows.Media.Brushes.Transparent;Id(approvalDetails,"AiNativeApprovalDetails");approvalBody.Children.Add(approvalDetails);approvalBody.Children.Add(approvalActions);nativeApprovalCard.Child=approvalBody;decisions.Children.Add(nativeApprovalCard);
        nativeQuestionCard.Margin=new(0,8,0,0);nativeQuestionCard.Visibility=W.Visibility.Collapsed;Id(nativeQuestionCard,"AiNativeQuestion");
        nativeQuestionCard.Child=new C.ScrollViewer{Content=questionBody,MaxHeight=240,VerticalScrollBarVisibility=C.ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=C.ScrollBarVisibility.Disabled};decisions.Children.Add(nativeQuestionCard);
        composer.Margin=new(0,10,0,0);composer.Padding=new(12,10,12,10);composer.CornerRadius=new(18);composer.MaxWidth=860;Id(composer,"AiComposer");var composerBody=new C.StackPanel();composer.Child=composerBody;Id(input,"AiInput");input.BorderThickness=new(0);input.Background=System.Windows.Media.Brushes.Transparent;input.Padding=new(4,2,4,2);input.VerticalContentAlignment=W.VerticalAlignment.Top;var inputArea=new C.Grid{MaxHeight=112};inputArea.Children.Add(input);inputPlaceholder.IsHitTestVisible=false;inputPlaceholder.VerticalAlignment=W.VerticalAlignment.Top;inputPlaceholder.Margin=new(4,2,4,2);inputPlaceholder.SetResourceReference(C.TextBlock.ForegroundProperty,"Fusion.Muted");bindings.Add(()=>inputPlaceholder.Text=AiToolsText.Text("ComposerHint"));Id(inputPlaceholder,"AiInputPlaceholder");inputArea.Children.Add(inputPlaceholder);composerBody.Children.Add(inputArea);input.PreviewKeyDown+=(_,e)=>{if(e.Key==I.Key.Enter && !I.Keyboard.Modifiers.HasFlag(I.ModifierKeys.Shift)){e.Handled=true;_=Send();}};
        composerActions.Margin=new(0,6,0,0);composerActions.Height=36;Id(composerActions,"AiComposerActions");foreach(var width in new[]{W.GridLength.Auto,W.GridLength.Auto,new W.GridLength(1,W.GridUnitType.Star),W.GridLength.Auto,W.GridLength.Auto})composerActions.ColumnDefinitions.Add(new(){Width=width});
        attach=Button("AttachCompact",()=>_=AddFiles144(),"AiAttachText");attach.Margin=new(0,0,8,0);composerActions.Children.Add(attach);
        permissionButton=Button("PermissionAsk",()=>TogglePopup(permissionPopup,permissionButton!),"AiPermissionMenu");permissionButton.Margin=new(0,0,8,0);permissionButton.MaxWidth=150;C.Grid.SetColumn(permissionButton,1);composerActions.Children.Add(permissionButton);
        workspaceButton=Button("ChooseWorkspace",ChooseWorkspace,"AiChooseWorkspace");workspaceButton.Margin=new(0,0,12,0);workspaceButton.MaxWidth=300;configuration.Children.Add(workspaceButton);C.Grid.SetColumn(workspaceButton,0);unlinkGame=Button("UnlinkGame",UnlinkGame,"AiUnlinkGame");unlinkGame.Content="×";unlinkGame.Width=36;unlinkGame.MinWidth=36;unlinkGame.MaxWidth=36;unlinkGame.Margin=new(4,0,0,0);unlinkGame.HorizontalAlignment=W.HorizontalAlignment.Left;C.Grid.SetColumn(unlinkGame,2);configuration.Children.Add(unlinkGame);
        var permissionsBody=new C.StackPanel();permissionsBody.Children.Add(PopupHeading(permissionPopup,permissionButton,Text("Permissions",18,true)));permissionsBody.Children.Add(permissionChoices);
        var agentLabel=Text("AgentMode",13);agentLabel.Margin=new(0,12,0,6);permissionsBody.Children.Add(agentLabel);Id(nativeAgent,"AiNativeAgent");permissionsBody.Children.Add(nativeAgent);nativeAgent.PreviewMouseWheel+=NoWheelSelection;
        nativeAgent.SelectionChanged+=(_,_)=>{if(!syncing&&nativeAgent.SelectedValue is string mode)Run(()=>{if(EnsureTask(false))controller.SetNativeAgent(mode);});};
        SetupPopup(permissionPopup,permissionButton,permissionsBody,"AiPermissionPopup");
        Id(composerChoices,"AiComposerChoices");composerChoices.Margin=new(0,0,0,10);composerChoices.ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star)});composerChoices.RowDefinitions.Add(new(){Height=W.GridLength.Auto});composerChoices.RowDefinitions.Add(new(){Height=W.GridLength.Auto});var planField=new C.StackPanel{Margin=new(0,0,0,12)};Id(planField,"AiPlanField");var planLabel=Text("Connection",12);planLabel.Margin=new(0,0,0,4);planLabel.SetResourceReference(C.TextBlock.ForegroundProperty,"Fusion.Muted");planField.Children.Add(planLabel);profiles.Height=36;profiles.MinWidth=0;profiles.HorizontalAlignment=W.HorizontalAlignment.Stretch;planField.Children.Add(profiles);composerChoices.Children.Add(planField);var modelField=new C.StackPanel();var modelLabel=Text("Model",12);Id(modelLabel,"AiChatModelLabel");modelLabel.Margin=new(0,0,0,4);modelLabel.SetResourceReference(C.TextBlock.ForegroundProperty,"Fusion.Muted");modelField.Children.Add(modelLabel);var modelRow=new C.Grid();modelRow.ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star)});modelRow.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});modelRow.Children.Add(chatModel);fetchModels=Button("RefreshCompact",()=>_=FetchModels(true),"AiFetchChatModels");fetchModels.Margin=new(6,0,0,0);fetchModels.Padding=new(8,5,8,5);C.Grid.SetColumn(fetchModels,1);modelRow.Children.Add(fetchModels);modelField.Children.Add(modelRow);C.Grid.SetRow(modelField,1);composerChoices.Children.Add(modelField);
        thinkingButton=Button("ThinkingCompact",()=>TogglePopup(thinkingPopup,thinkingButton!),"AiThinkingMenu");thinkingButton.Margin=new(0,0,8,0);thinkingButton.HorizontalAlignment=W.HorizontalAlignment.Right;C.Grid.SetColumn(thinkingButton,2);composerActions.Children.Add(thinkingButton);stop=Button("Stop",()=>Run(controller.Stop),"AiStop");C.Grid.SetColumn(stop,3);composerActions.Children.Add(stop);send=Button("Send",()=>_=Send(),"AiSend",true);send.Margin=new(0);C.Grid.SetColumn(send,4);composerActions.Children.Add(send);newTask=Button("NewConversation",NewTask,"AiNewTask");composerBody.Children.Add(composerActions);
        attachment.Margin=new(4,6,4,0);sourceName.FontSize=14;attachment.Children.Add(sourceName);composerBody.Children.Add(attachment);C.Grid.SetRow(composer,2);chat.Children.Add(composer);
        var thoughtBody=new C.StackPanel();var thoughtHeading=PopupHeading(thinkingPopup,thinkingButton,thinkingState);thinkingState.FontSize=18;thinkingState.FontWeight=W.FontWeights.SemiBold;thoughtBody.Children.Add(thoughtHeading);thoughtBody.Children.Add(composerChoices);popupModel.Margin=new(0,6,0,14);Id(popupModel,"AiPopupModel");thoughtBody.Children.Add(popupModel);thinkingSlider.MinHeight=30;thinkingSlider.Height=30;thoughtBody.Children.Add(thinkingSlider);thoughtBody.Children.Add(Text("SessionOptionsHelp",13));configure.Margin=new(0,12,0,0);configure.HorizontalAlignment=W.HorizontalAlignment.Left;thoughtBody.Children.Add(configure);SetupPopup(thinkingPopup,thinkingButton,thoughtBody,"AiThinkingPopup");thinkingPopup.Opened+=(_,_)=>Dispatcher.BeginInvoke(new Action(()=>{if(thinkingPopup.IsOpen)(thinkingSlider.IsVisible&&thinkingSlider.IsEnabled?(W.UIElement)thinkingSlider:configure).Focus();}),W.Threading.DispatcherPriority.Input);
        var detailPanel=new C.StackPanel();foreach(var key in new[]{"NativeCapabilityHelp"}){var t=Text(key,14);t.Margin=new(0,4,0,4);detailPanel.Children.Add(t);}usage.FontSize=budget.FontSize=14;usage.Margin=new(0,4,0,4);detailPanel.Children.Add(usage);detailPanel.Children.Add(budget);details.Child=detailPanel;
        input.TextChanged+=(_,_)=>{if(!restoringDraft&&draftKey.Length>0){drafts[draftKey]=input.Text;if(draftKey=="task:"+controller.Current?.Id)controller.SetDraftText(input.Text);}inputPlaceholder.Visibility=input.Text.Length==0?W.Visibility.Visible:W.Visibility.Collapsed;RefreshActions();};

        modPage.RowDefinitions.Add(new(){Height=W.GridLength.Auto});modPage.RowDefinitions.Add(new(){Height=W.GridLength.Auto});modPage.RowDefinitions.Add(new(){Height=new(1,W.GridUnitType.Star)});modPage.RowDefinitions.Add(new(){Height=W.GridLength.Auto});
        var modIntro=new C.StackPanel();modIntro.Children.Add(Text("LocalMods",22,true));var modHelp=Text("ModGameHint");modHelp.Margin=new(0,8,0,14);modIntro.Children.Add(modHelp);modPage.Children.Add(modIntro);
        var modActions=new C.WrapPanel();modBack=Button("BackToModLibrary",()=>{modGameId=null;Refresh();},"AiAllModGames");modActions.Children.Add(modBack);modActions.Children.Add(Button("MakeMod",MakeMod,"AiMakeMod",true));import=Button("Import",ImportMod,"AiImportMod");export=Button("Export",ExportMod,"AiExportMod");install=Button("Install",()=>_=ChangeMod(true),"AiInstallMod");disable=Button("Disable",()=>_=ChangeMod(false),"AiDisableMod");foreach(var b in new[]{import,export,install,disable})modActions.Children.Add(b);C.Grid.SetRow(modActions,1);modPage.Children.Add(modActions);
        modList.Margin=new(0,12,0,0);modList.Children.Add(mods);emptyMods.Margin=new(20);emptyMods.FontSize=16;emptyMods.IsHitTestVisible=false;C.Panel.SetZIndex(emptyMods,1);emptyMods.MaxWidth=420;emptyMods.TextAlignment=W.TextAlignment.Center;emptyMods.VerticalAlignment=W.VerticalAlignment.Center;emptyMods.HorizontalAlignment=W.HorizontalAlignment.Center;C.Grid.SetRow(emptyMods,2);modPage.Children.Add(emptyMods);C.Grid.SetRow(modList,2);modPage.Children.Add(modList);modGameScroll.SizeChanged+=(_,_)=>ArrangeModCards();modGameScroll.Margin=new(0,12,0,0);modGameScroll.Content=modGames;C.Grid.SetRow(modGameScroll,2);modPage.Children.Add(modGameScroll);Id(mods,"AiModList");mods.SelectionChanged+=(_,_)=>{if(!syncing && mods.SelectedItem is C.ListBoxItem{Tag:AiModEntry item})Run(()=>{modGameId=item.GameId;controller.SelectMod(item.Id);});};
        var preview=new C.StackPanel();modPath.Margin=new(0,8,0,10);preview.Children.Add(modPath);preview.Children.Add(Text("Original",14,true));modBefore.MaxHeight=140;preview.Children.Add(modBefore);preview.Children.Add(Text("Modified",14,true));modAfter.MaxHeight=140;preview.Children.Add(modAfter);modDetails.Content=new C.ScrollViewer{Content=preview,MaxHeight=220,VerticalScrollBarVisibility=C.ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=C.ScrollBarVisibility.Disabled};modDetails.SetResourceReference(C.Control.ForegroundProperty,"Fusion.Text");C.Grid.SetRow(modDetails,3);modPage.Children.Add(modDetails);
        status.Margin=new(0,0,0,8);status.FontWeight=W.FontWeights.SemiBold;Id(status,"AiStatus");C.Grid.SetRow(status,2);C.Grid.SetColumn(status,1);Children.Add(status);
        sidebar.SetResourceReference(C.Panel.BackgroundProperty,"Fusion.Secondary");sidebar.Margin=new(0,0,20,0);sidebar.RowDefinitions.Add(new(){Height=W.GridLength.Auto});sidebar.RowDefinitions.Add(new(){Height=new(1,W.GridUnitType.Star)});sidebar.RowDefinitions.Add(new(){Height=W.GridLength.Auto});sidebar.RowDefinitions.Add(new(){Height=W.GridLength.Auto});
        newTask.HorizontalAlignment=W.HorizontalAlignment.Stretch;newTask.Margin=new(0,0,0,4);C.Grid.SetRow(history,1);sidebar.Children.Add(history);var navigation=new C.StackPanel();assistantTab.HorizontalAlignment=modTab.HorizontalAlignment=W.HorizontalAlignment.Stretch;assistantTab.Margin=new(0,0,0,4);modTab.Margin=new(0);harnessChoice.Margin=new(0,0,0,14);harnessChoice.FontSize=17;harnessChoice.FontWeight=W.FontWeights.SemiBold;navigation.Children.Add(harnessChoice);navigation.Children.Add(newTask);navigation.Children.Add(modTab);navigation.Children.Add(assistantTab);navigation.Margin=new(0,0,0,12);sidebar.Children.Add(navigation);workspaceState.TextWrapping=W.TextWrapping.NoWrap;workspaceState.TextTrimming=W.TextTrimming.CharacterEllipsis;workspaceState.Margin=new(12,6,12,0);workspaceState.SetResourceReference(C.TextBlock.ForegroundProperty,"Fusion.Muted");Id(workspaceState,"AiNativeWorkspace");C.Grid.SetRow(workspaceState,2);sidebar.Children.Add(workspaceState);C.Grid.SetRowSpan(sidebar,5);Children.Add(sidebar);
        // Billing belongs to the sidebar; the composer remains the last chat row.
        billingButton=Button("ApiBalance145",()=>TogglePopup(billingPopup,billingButton!),"AiBillingToggle");billingButton.Height=40;billingButton.MinHeight=40;billingButton.Margin=new(0,8,0,0);billingButton.HorizontalAlignment=W.HorizontalAlignment.Stretch;billingButton.MaxWidth=double.PositiveInfinity;billingButton.Padding=new(4,6,4,6);Id(billingPanel,"AiBillingSummary");billingPanel.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});billingPanel.ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star)});var balanceLabel=Text("ApiBalance145",13);balanceLabel.TextWrapping=W.TextWrapping.NoWrap;balanceLabel.Margin=new(0,0,6,0);billingPanel.Children.Add(balanceLabel);Id(balance,"AiBalance");balance.FontSize=13;balance.TextWrapping=W.TextWrapping.NoWrap;balance.TextTrimming=W.TextTrimming.CharacterEllipsis;C.Grid.SetColumn(balance,1);billingPanel.Children.Add(balance);billingButton.Content=billingPanel;C.Grid.SetRow(billingButton,3);sidebar.Children.Add(billingButton);
        var billingBody=new C.StackPanel();billingBody.Children.Add(PopupHeading(billingPopup,billingButton,Text("ApiBalance145",18,true)));balanceDetail.Margin=new(0,6,0,8);billingBody.Children.Add(balanceDetail);var billingActions=new C.WrapPanel();balanceRefresh=Button("RefreshBalance",()=>_=RefreshBalance(),"AiRefreshBalance");billingActions.Children.Add(balanceRefresh);balanceLink=Button("OfficialBilling",OpenBilling,"AiOfficialBilling");billingActions.Children.Add(balanceLink);billingBody.Children.Add(billingActions);SetupPopup(billingPopup,billingButton,billingBody,"AiBillingPopup");billingPopup.Opened+=(_,_)=>Dispatcher.BeginInvoke(new Action(()=>{if(billingPopup.IsOpen)balanceRefresh.Focus();}),W.Threading.DispatcherPriority.Input);
        InitializeWorkspace144();InitializeWorkspace146();controller.Changed+=Changed;controller.NativeProgressChanged+=NativeProgress;SizeChanged+=(_,_)=>ArrangePanels();Loaded+=(_,_)=>{Refresh();if(IsVisible){refreshTimer.Start();_=RefreshRunningGames();}};Unloaded+=(_,_)=>{refreshTimer.Stop();ClosePopups(false);};IsVisibleChanged+=(_,_)=>{if(IsVisible && IsLoaded){refreshTimer.Start();_=RefreshRunningGames();}else{refreshTimer.Stop();ClosePopups(false);}};refreshTimer.Tick+=(_,_)=>_=RefreshRunningGames();Refresh();
    }

    private AiGameChoice? SelectedGame=>query.Length==0 && games.SelectedItem is AiGameChoice g?g:null;
    private bool HasProfile{get{try{if(profiles.SelectedValue is not string id||!controller.ProfileUsesHarness(id))return false;var settings=controller.Current?.ProfileId==id?controller.GetCurrentProfileSettings():controller.GetProfileSettings(id);var options=controller.GetExecutionOptions(id);return controller.ProfileHarnessError(id).Length==0&&(settings.Model.Length>0)&&(options.AuthMode=="account"||settings.ApiUrl.Length>0&&(settings.ApiKey.Length>0||settings.AllowEmptyApiKey));}catch{return false;}}}
    private bool RetiredProfile=>profiles.SelectedValue is string id?!controller.ProfileUsesHarness(id):controller.Current is {ProfileId.Length:>0} task&&!controller.ProfileUsesHarness(task.ProfileId);
    private bool SameTask=>controller.Current is {} current && (SelectedGame?.Id??"")==current.GameId;
    private string GameName(string id)=>id.Length==0?AiToolsText.Text("GeneralChat"):controller.Games.FirstOrDefault(g=>g.Id==id)?.Name??AiToolsText.Text("TaskGameUnavailable");
    private void SwitchDraft(string next)
    {
        if(next==draftKey)return;if(draftKey.Length>0)drafts[draftKey]=input.Text;draftKey=next;
        restoringDraft=true;try{input.Text=drafts.TryGetValue(next,out var value)?value:next=="task:"+controller.Current?.Id?controller.Current?.DraftText??"":"";input.CaretIndex=input.Text.Length;}finally{restoringDraft=false;}
    }
    private static C.TextBlock Label(double size=14,bool bold=false){var t=new C.TextBlock{FontSize=size,FontWeight=bold?W.FontWeights.SemiBold:W.FontWeights.Normal,TextWrapping=W.TextWrapping.Wrap,VerticalAlignment=W.VerticalAlignment.Center};t.SetResourceReference(C.TextBlock.ForegroundProperty,"Fusion.Text");return t;}
    private C.TextBlock Text(string key,double size=14,bool bold=false){var t=Label(size,bold);bindings.Add(()=>t.Text=AiToolsText.Text(key));return t;}
    private static C.TextBox ReadOnlyText()=>new(){IsReadOnly=true,TextWrapping=W.TextWrapping.Wrap,AcceptsReturn=true,VerticalScrollBarVisibility=C.ScrollBarVisibility.Auto,BorderThickness=new(0),Padding=new(0),Margin=new(0,6,0,10),FontSize=15};
    private static C.Border Card(){var c=new C.Border{CornerRadius=new(10),BorderThickness=new(1),Padding=new(16)};c.SetResourceReference(C.Border.BackgroundProperty,"Fusion.Secondary");c.SetResourceReference(C.Border.BorderBrushProperty,"Fusion.Border");return c;}
    private static void ButtonText(C.Button b,string key,bool primary=false){string id=W.Automation.AutomationProperties.GetAutomationId(b);bool light=id is "AiPermissionMenu" or "AiChooseWorkspace" or "AiUnlinkGame" or "AiConfigureApi" or "AiRefreshBalance" or "AiCostHistory" or "AiOfficialBilling" or "AiAttachText" or "AiUsageDetails" or "AiThinkingMenu" or "AiBillingToggle" or "AiAssistantTab" or "AiModLibraryTab" or "AiNewTask";WpfAiControls.SetWorkspaceButton(b,AiToolsText.Text(key),primary,light,id is "AiNumericExample" or "AiModExample");string full=id switch{"AiRefreshBalance"=>"RefreshBalance","AiCostHistory"=>"CostHistory","AiAttachText"=>"AttachFile","AiUsageDetails"=>"TokenDetails",_=>key};b.ToolTip=AiToolsText.Text(full);W.Automation.AutomationProperties.SetName(b,AiToolsText.Text(full));
        if(id is "AiAttachText" or "AiSend" or "AiStop")
        {
            var path=new System.Windows.Shapes.Path{Data=System.Windows.Media.Geometry.Parse(id=="AiAttachText"?"M 8,0 L 8,16 M 0,8 L 16,8":id=="AiSend"?"M 8,16 L 8,0 M 1,7 L 8,0 L 15,7":"M 2,2 L 14,2 L 14,14 L 2,14 Z"),StrokeThickness=1.8,Width=16,Height=16,Stretch=System.Windows.Media.Stretch.Uniform};path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,new System.Windows.Data.Binding("Foreground"){Source=b});if(id=="AiStop")path.SetBinding(System.Windows.Shapes.Shape.FillProperty,new System.Windows.Data.Binding("Foreground"){Source=b});b.Content=path;b.Width=36;b.Padding=new(9);
        }
        if(id is "AiNewTask" or "AiModLibraryTab" or "AiAssistantTab")
        {
            b.SetResourceReference(C.Control.ForegroundProperty,"Fusion.Text");
            // Localized navigation may need two lines. Give the label the actual
            // remaining sidebar width instead of an unrelated 120-pixel ceiling.
            b.Height=double.NaN;b.MinHeight=36;b.MaxHeight=double.PositiveInfinity;
            b.HorizontalContentAlignment=W.HorizontalAlignment.Stretch;b.Padding=new(6,7,6,7);
            var row=new C.Grid();row.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});row.ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star)});
            var icon=new System.Windows.Shapes.Path{Data=System.Windows.Media.Geometry.Parse(id=="AiNewTask"?"M 7,0 L 7,14 M 0,7 L 14,7":id=="AiModLibraryTab"?"M 0,3 L 6,3 L 8,5 L 15,5 L 15,14 L 0,14 Z":"M 0,0 L 15,0 L 15,11 L 6,11 L 2,15 L 2,11 L 0,11 Z"),StrokeThickness=1.3,Width=15,Height=15,Margin=new(0,0,8,0),VerticalAlignment=W.VerticalAlignment.Center};icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"Fusion.Text");row.Children.Add(icon);
            var text=Label(14);text.Text=AiToolsText.Text(key);text.TextWrapping=W.TextWrapping.Wrap;text.TextTrimming=W.TextTrimming.None;C.Grid.SetColumn(text,1);row.Children.Add(text);b.Content=row;
        }
    }
    private C.Button Button(string key,Action action,string id,bool primary=false){var b=WpfThemeResources.Button(AiToolsText.Text(key),null,action,primary);b.MaxWidth=280;b.Margin=new(0,0,8,0);Id(b,id);ButtonText(b,key,primary);if(id!="AiBillingToggle")bindings.Add(()=>ButtonText(b,key,primary));return b;}
    private static void Id(W.DependencyObject e,string id)=>W.Automation.AutomationProperties.SetAutomationId(e,id);
    private C.Grid PopupHeading(WpfAiOverlay144 popup,C.Button owner,C.TextBlock title)
    {
        var row=new C.Grid{Margin=new(0,0,0,12)};row.ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});row.Children.Add(title);
        var close=Button("ClosePopup",()=>ClosePopup(popup,true),popup==thinkingPopup?"AiThinkingClose":popup==permissionPopup?"AiPermissionClose":"AiBillingClose");close.Margin=new(12,0,0,0);C.Grid.SetColumn(close,1);row.Children.Add(close);return row;
    }
    private void SetupPopup(WpfAiOverlay144 popup,W.FrameworkElement owner,W.FrameworkElement content,string id)
    {
        var surface=Card();surface.CornerRadius=new(14);surface.Padding=new(16);surface.Width=420;surface.MaxHeight=600;surface.Child=new C.ScrollViewer{Content=content,VerticalScrollBarVisibility=C.ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=C.ScrollBarVisibility.Disabled};Id(surface,id);
        surface.Tag=420d;I.KeyboardNavigation.SetTabNavigation(surface,I.KeyboardNavigationMode.Cycle);popup.Child=surface;popup.PlacementTarget=owner;Children.Add(popup);
        surface.PreviewKeyDown+=(_,e)=>{if(e.Key==I.Key.Escape){e.Handled=true;ClosePopup(popup,true);}};
        popup.Closed+=(_,_)=>{bool restore=popup.Tag is true||surface.IsKeyboardFocusWithin;popup.Tag=false;if(restore&&!disposed&&owner.IsVisible&&owner.IsEnabled)owner.Focus();};
    }
    private void TogglePopup(WpfAiOverlay144 popup,C.Button owner)
    {
        if(disposed||!owner.IsEnabled)return;bool open=popup.IsOpen;ClosePopups(false);if(open)return;
        Refresh();games.IsDropDownOpen=profiles.IsDropDownOpen=false;popup.IsOpen=true;
    }
    private void SetThinkingButton(string effort,string? model)
    {
        var row=new C.Grid();row.ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});
        var value=Label(14);value.Text=string.IsNullOrWhiteSpace(model)?AiToolsText.Text("ChooseConnection"):model+" · "+(effort.Length==0?AiToolsText.Text("ThinkingDefaultShort"):AiUiText.EffortText(effort));value.TextWrapping=W.TextWrapping.NoWrap;value.TextTrimming=W.TextTrimming.CharacterEllipsis;row.Children.Add(value);
        var chevron=new System.Windows.Shapes.Path{Data=System.Windows.Media.Geometry.Parse("M 0,0 L 4,4 L 8,0"),Width=8,Height=4,StrokeThickness=1.5,VerticalAlignment=W.VerticalAlignment.Center,Margin=new(8,0,0,0)};chevron.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"Fusion.Text");C.Grid.SetColumn(chevron,1);row.Children.Add(chevron);thinkingButton.Content=row;thinkingButton.MaxWidth=320;W.Automation.AutomationProperties.SetName(thinkingButton,AiToolsText.Text("Thinking")+": "+value.Text);
    }
    private void ClosePopup(WpfAiOverlay144 popup,bool restoreFocus){popup.Tag=restoreFocus;popup.IsOpen=false;}
    private void ClosePopups(bool restoreFocus){CloseExtraPopups144();ClosePopup(thinkingPopup,restoreFocus);ClosePopup(billingPopup,restoreFocus);ClosePopup(permissionPopup,restoreFocus);}
    private static void NoWheelSelection(object sender,I.MouseWheelEventArgs e){if(sender is C.ComboBox{IsDropDownOpen:false})e.Handled=true;}
    private void SwitchPage(bool mod)
    {
        ClosePopups(false);if(mod&&!showMods)modChatContext=new(controller.Current?.Id,games.SelectedValue as string,profiles.SelectedValue as string,viewingHistory);
        showMods=mod;if(!mod&&modChatContext is {} saved)
        {
            modChatContext=null;query="";
            if(saved.TaskId is {} id&&controller.Tasks.Any(t=>t.Id==id)&&controller.Current?.Id!=id)controller.SelectTask(id);
            Refresh();syncing=true;try{games.SelectedValue=saved.GameId;profiles.SelectedValue=saved.ProfileId;viewingHistory=saved.History;}finally{syncing=false;}
        }
        Refresh();
    }
    private void MakeMod()
    {
        if(controller.Busy||operationBusy)return;ClosePopups(false);modChatContext=null;showMods=false;viewingHistory=false;modIntent=true;
        if(modGameId is {} target&&!controller.Games.Any(g=>g.Id==target&&g.Running))
        {
            syncing=true;try{query="";games.SelectedIndex=-1;}finally{syncing=false;}
            if(controller.PendingSummary is not null)controller.CancelProposal();localStatusTarget=GameName(target);localStatus="mod_game_not_running";Refresh();return;
        }
        if(modGameId is {} running)games.SelectedValue=running;FillPrompt("ModPrompt",true);
    }
    private void FillPrompt(string key,bool mod){modIntent=mod;input.Text=AiToolsText.Text(key);Refresh();input.Focus();input.CaretIndex=input.Text.Length;}
    private void FilterChanged(){if(syncing)return;gamesSignature="\0";Refresh();}
    private void GameSelected()
    {
        if(syncing)return;if(controller.Busy||operationBusy){syncing=true;try{games.SelectedValue=selectedGameId;}finally{syncing=false;}return;}if(games.SelectedItem is not AiGameChoice game)return;
        ClosePopups(false);query="";viewingHistory=false;localStatus="";bool changed=selectedGameId!=game.Id;
        if(changed&&selectedGameId.Length>0&&profiles.SelectedValue is string priorProfile)lastProfileByGame[selectedGameId]=priorProfile;
        if(controller.Current?.GameId!=game.Id){if(controller.PendingSummary is not null)controller.CancelProposal();var target=lastTaskByGame.TryGetValue(game.Id,out var id)?controller.Tasks.FirstOrDefault(t=>t.Id==id):controller.Tasks.FirstOrDefault(t=>t.GameId==game.Id);if(target is not null)controller.SelectTask(target.Id);}
        if(changed){string? profile=controller.Current?.GameId==game.Id?controller.Current.ProfileId:lastProfileByGame.GetValueOrDefault(game.Id);if(profile is not null&&controller.Profiles.Any(p=>p.Id==profile)){syncing=true;try{profiles.SelectedValue=profile;}finally{syncing=false;}}}
        Refresh();
    }
    private void GameQueryChanged(string text)
    {
        if(syncing || !games.IsKeyboardFocusWithin)return;
        if(games.SelectedItem is AiGameChoice selected && text==selected.Name)return;
        query=text;if(controller.PendingSummary is not null)controller.CancelProposal();gamesSignature="\0";Refresh();games.IsDropDownOpen=true;
    }
    private async Task RefreshRunningGames(){if(disposed || polling || !IsVisible)return;polling=true;try{await controller.RefreshGamesAsync();if(!disposed)Refresh();}catch{ }finally{polling=false;}}
    private void ConfigureProfile(){ClosePopups(false);Run(controller.OpenSettings);}
    private bool ProfileMatchesHarness(string id)
    {
        try{return controller.ProfileMatchesHarness(id);}catch{return false;}
    }
    private void HarnessSelected()
    {
        if(syncing||harnessChoice.SelectedValue is not string backend||backend==controller.SelectedHarness)return;
        Run(()=>
        {
            if(draftKey.Length>0)drafts[draftKey]=input.Text;
            if(controller.Current is null)drafts["harness:"+backend]=input.Text;
            string modelText=chatModel.Text;modelDraft146=modelText!=controller.CurrentModel?modelText:null;
            modelLifetime?.Cancel();ClosePopups(false);controller.SelectHarness(backend);
            profilesSignature="\0";modelCatalogProfile="";modelCatalogAttempt144="";modelCatalog.Clear();modelCatalogError144="";modelBusy=false;
            syncing=true;try{chatModel.ItemsSource=null;chatModel.Text=modelDraft146??controller.CurrentModel;}finally{syncing=false;}
            showMods=false;modChatContext=null;nativeOutput.Text="";nativeState.Text="";
        });
    }
    private void CommitModel(string? selectedModel=null)
    {
        if(syncing||controller.Busy||operationBusy||profiles.SelectedValue is not string)return;
        string modelId=(selectedModel??chatModel.Text).Trim();if(modelId.Length==0)return;if(selectedModel is null&&!modelCatalog.ContainsKey(modelId)&&AiModelSearch144.Filter(modelCatalog.Values,modelId).Count>0)return;
        modelDraft146=null;Run(()=>{if(EnsureTask()&&controller.CurrentModel!=modelId)controller.SetModel(modelId,modelCatalog.GetValueOrDefault(modelId));});
    }
    private Task FetchModels(bool forceRefresh=false)=>Dispatcher.InvokeAsync(()=>FetchModelsOnDispatcher144(forceRefresh)).Task.Unwrap();
    private async Task FetchModelsOnDispatcher144(bool forceRefresh)
    {
        if(disposed||modelBusy||controller.Busy||profiles.SelectedValue is not string profileId)return;string compatibility=controller.ProfileHarnessError(profileId);if(compatibility.Length>0){modelCatalogError144=compatibility;modelCatalogAttempt144=ModelIdentity144(profileId);RefreshModelStatus144();return;}
        modelLifetime?.Cancel();modelLifetime?.Dispose();var cancel=new CancellationTokenSource();modelLifetime=cancel;modelBusy=true;modelCatalogError144="";localStatus="";RefreshModelStatus144();RefreshActions();
        try
        {
            string requestIdentity=ModelIdentity144(profileId);modelCatalogAttempt144=requestIdentity;var result=await controller.DiscoverModelsAsync(profileId,cancel.Token,forceRefresh);
            if(disposed||cancel.IsCancellationRequested||profiles.SelectedValue as string!=profileId||requestIdentity!=ModelIdentity144(profileId))return;
            if(result.Status!="available"){modelCatalogError144=result.Code.Length>0?result.Code:"RefreshFailed";localStatus=modelCatalogError144;return;}modelCatalogError144="";
            string current=chatModel.Text;bool previous=syncing;syncing=true;
            try{modelCatalog.Clear();foreach(var model in result.Models)modelCatalog[model.Id]=model;chatModel.ItemsSource=AiModelSearch144.Filter(modelCatalog.Values,"").Select(x=>x.Id).ToArray();chatModel.Text=current;modelCatalogProfile=profileId;}
            finally{syncing=previous;}
            if(result.Models.Count==0)localStatus="NoModelsFound";else if(controller.Current?.ProfileId==profileId&&modelCatalog.TryGetValue(controller.CurrentModel,out var descriptor))controller.SetModel(descriptor.Id,descriptor);
        }
        catch(OperationCanceledException){ }
        catch(Exception ex){if(!disposed&&!cancel.IsCancellationRequested){modelCatalogError144=ErrorCode(ex);localStatus=modelCatalogError144;}}
        finally{if(ReferenceEquals(modelLifetime,cancel)){modelBusy=false;modelLifetime=null;}cancel.Dispose();if(!disposed){Refresh();RefreshModelStatus144();}}
    }
    private void NativeProgress(AiNativeProgress progress)=>Changed();
    private void ChooseWorkspace()=>Run(()=>
    {
        if(!EnsureTask(false))return;
        var dialog=new Microsoft.Win32.OpenFolderDialog{Title=AiToolsText.Text("ChooseWorkspace"),Multiselect=false};
        if(Directory.Exists(controller.CurrentWorkspace))dialog.InitialDirectory=controller.CurrentWorkspace;
        if(dialog.ShowDialog(W.Window.GetWindow(this))==true)controller.SetWorkspace(dialog.FolderName);
    });
    private void SetPermission(string mode){if(mode=="full"&&controller.PermissionMode!="full"){ConfirmFullPermission144();return;}Run(()=>{if(EnsureTask(false))controller.SetPermissionMode(mode);ClosePopup(permissionPopup,true);});}
    private static string ApprovalLabel(string label)
    { string key=label switch
    {
        "Allow once"=>"AllowOnce","Allow for this session"=>"AllowSession","Deny"=>"Deny","Cancel turn"=>"CancelTurn",
        "Allow matching commands"=>"AllowCommandRule","Allow network rule"=>"AllowNetworkRule",
        "Set up Windows sandbox"=>"SetupWindowsSandbox","Set up Codex Windows sandbox"=>"SetupCodexWindowsSandbox",_=>""
    };return key.Length>0?AiToolsText.Text(key):AiUiText.TryTranslateValue(label,UiLanguage.Current,out var localized)?localized:label;}
    private void RefreshNativeControls(bool repaint)
    {
        if(repaint||permissionChoices.Tag as string!=controller.SelectedHarness)
        {
            permissionChoices.Children.Clear();permissionChoices.Tag=controller.SelectedHarness;
            foreach(var mode in new[]{"ask","auto","full"})
            {
                string key=mode=="ask"?"PermissionAsk":mode=="auto"?"PermissionAuto":"PermissionFull";
                var button=WpfThemeResources.Button(AiToolsText.Text(key),null,()=>SetPermission(mode));button.MinHeight=36;button.HorizontalAlignment=W.HorizontalAlignment.Stretch;button.HorizontalContentAlignment=W.HorizontalAlignment.Left;button.Margin=new(0,8,0,0);Id(button,"AiPermission_"+mode);
                bool supported=mode!="auto"||controller.SelectedHarness=="codex";button.IsEnabled=supported;
                var body=new C.StackPanel();var title=Label(14,true);title.Text=AiToolsText.Text(key);body.Children.Add(title);var help=Label(12);help.Text=AiToolsText.Text(supported?key+"Help":"PermissionAutoUnsupported");help.Margin=new(0,3,0,0);body.Children.Add(help);button.Content=body;button.ToolTip=help.Text;permissionChoices.Children.Add(button);
            }
            nativeAgent.ItemsSource=new[]{new HarnessChoice("build",AiToolsText.Text("AgentBuild")),new HarnessChoice("plan",AiToolsText.Text("AgentPlan"))};
        }
        ButtonText(permissionButton,controller.PermissionMode=="full"?"PermissionFull":controller.PermissionMode=="auto"?"PermissionAuto":"PermissionAsk");nativeAgent.SelectedValue=controller.NativeAgent;
        WpfAiControls.SetWorkspaceButton(workspaceButton,controller.Current is {Workspace.Length:>0}?Path.GetFileName(controller.CurrentWorkspace.TrimEnd(Path.DirectorySeparatorChar)):AiToolsText.Text("ChooseWorkspace"),false,true);
        workspaceButton.ToolTip=controller.CurrentWorkspace.Length>0?controller.CurrentWorkspace:AiToolsText.Text("WorkspaceDefault");
        W.Automation.AutomationProperties.SetName(workspaceButton,AiToolsText.Text("ChooseWorkspace"));
        var approval=controller.PendingNativeApproval;nativeApprovalCard.Visibility=approval is null?W.Visibility.Collapsed:W.Visibility.Visible;
        if(!ReferenceEquals(displayedApproval,approval)||repaint)
        {
            displayedApproval=approval;approvalTitle.Text=approval is null?"":ApprovalLabel(approval.Title);approvalDetails.Text=approval?.Details??"";approvalActions.Children.Clear();
            if(approval is not null)foreach(var choice in approval.Choices)
            {
                var button=WpfThemeResources.Button(ApprovalLabel(choice.Label),null,()=>{if(ReferenceEquals(controller.PendingNativeApproval,approval))Run(()=>controller.ResolveNativeApproval(choice.Id));});
                button.MinHeight=36;button.Margin=new(0,6,8,0);Id(button,"AiNativeChoice_"+choice.Id);approvalActions.Children.Add(button);
            }
        }
        var question=controller.PendingNativeQuestion;nativeQuestionCard.Visibility=question is null?W.Visibility.Collapsed:W.Visibility.Visible;
        if(!ReferenceEquals(displayedQuestion,question)||(repaint&&question is null))
        {
            displayedQuestion=question;questionBody.Children.Clear();questionButtons.Clear();if(question is not null)BuildQuestion(question);
        }
        if(repaint)foreach(var entry in questionButtons)WpfThemeResources.SetButton(entry.Button,AiToolsText.Text(entry.Key),null,entry.Key=="SubmitAnswer");
    }
    private void BuildQuestion(AiNativeQuestion question)
    {
        questionBody.Children.Add(Text("NativeQuestion",15,true));var readers=new Dictionary<string,Func<string[]>>(StringComparer.Ordinal);
        foreach(var field in question.Fields)
        {
            var prompt=Label(14);prompt.Text=field.Header.Length>0?field.Header+" · "+field.Question:field.Question;prompt.Margin=new(0,8,0,6);questionBody.Children.Add(prompt);
            var options=new C.ListBox{SelectionMode=field.Multiple?C.SelectionMode.Multiple:C.SelectionMode.Single,ItemsSource=field.Options,MaxHeight=120};Id(options,"AiQuestionOptions_"+field.Id);
            if(field.Options.Count>0)questionBody.Children.Add(options);
            var custom=new C.TextBox{MinHeight=36,TextWrapping=W.TextWrapping.Wrap,MaxLength=16000};var secret=new C.PasswordBox{MinHeight=36,MaxLength=16000};Id(custom,"AiQuestionText_"+field.Id);Id(secret,"AiQuestionSecret_"+field.Id);
            if(field.AllowCustom){questionBody.Children.Add(field.Secret?secret:custom);W.Automation.AutomationProperties.SetName(field.Secret?secret:custom,field.Question);}
            readers[field.Id]=()=>
            {
                string value=(field.Secret?secret.Password:custom.Text).Trim();
                if(field.AllowCustom&&value.Length>0)return field.Multiple?options.SelectedItems.Cast<string>().Append(value).Distinct().ToArray():new[]{value};
                return options.SelectedItems.Cast<string>().ToArray();
            };
        }
        var actions=new C.WrapPanel{Margin=new(0,8,0,0)};var submit=WpfThemeResources.Button(AiToolsText.Text("SubmitAnswer"),null,()=>Run(()=>
        {
            if(!ReferenceEquals(controller.PendingNativeQuestion,question))return;
            var answers=readers.ToDictionary(x=>x.Key,x=>x.Value());
            if(answers.Any(x=>x.Value.Length==0)){localStatus="AnswerRequired";return;}controller.ResolveNativeQuestion(answers);
        }),true);submit.MinHeight=36;submit.Margin=new(0,0,8,0);Id(submit,"AiNativeAnswer");actions.Children.Add(submit);questionButtons.Add((submit,"SubmitAnswer"));
        var cancel=WpfThemeResources.Button(AiToolsText.Text("Stop"),null,()=>Run(controller.Stop));cancel.MinHeight=36;actions.Children.Add(cancel);questionButtons.Add((cancel,"Stop"));questionBody.Children.Add(actions);
    }
    private bool EnsureModTask()
    {
        if(modGameId is null || !controller.Games.Any(g=>g.Id==modGameId)){localStatus="SelectGame";Refresh();return false;}
        query="";
        if(controller.Current?.GameId!=modGameId)
        {
            var target=lastTaskByGame.TryGetValue(modGameId,out var id)?controller.Tasks.FirstOrDefault(t=>t.Id==id):controller.Tasks.FirstOrDefault(t=>t.GameId==modGameId);
            if(target is not null)controller.SelectTask(target.Id);
            else {string? remembered=lastProfileByGame.GetValueOrDefault(modGameId);string profile=remembered is not null&&controller.Profiles.Any(p=>p.Id==remembered)?remembered:profiles.SelectedValue as string??"";controller.NewTask(modGameId,profile);}
        }
        return true;
    }
    private bool EnsureTask(bool requireProfile=true)
    {
        if(requireProfile && (profiles.SelectedValue is not string id || !controller.ProfileUsesHarness(id))){localStatus=RetiredProfile?"ai_backend_retired":"ConfigureAIFirst";Refresh();return false;}
        string profile=profiles.SelectedValue as string??"",gameId=SelectedGame?.Id??"";
        if(controller.Current is null || controller.Current.GameId!=gameId){string text=input.Text,previousKey=draftKey;controller.NewTask(gameId,profile);string next="task:"+controller.Current!.Id;drafts[next]=text;SwitchDraft(next);if(previousKey.StartsWith("game:",StringComparison.Ordinal))drafts.Remove(previousKey);}
        else if(profile.Length>0 && controller.Current.ProfileId!=profile)controller.SetProfile(profile);
        return true;
    }
    private void UnlinkGame()=>Run(()=>{ClosePopups(false);query="";viewingHistory=false;modIntent=false;syncing=true;try{games.SelectedIndex=-1;games.Text="";}finally{syncing=false;}controller.NewTask("",profiles.SelectedValue as string??"");SwitchDraft("task:"+controller.Current!.Id);});
    private void NewTask()=>Run(()=>{controller.NewTask(SelectedGame?.Id??"",profiles.SelectedValue as string??"");SwitchDraft("task:"+controller.Current!.Id);modIntent=false;showMods=false;viewingHistory=false;nativeOutput.Text="";nativeState.Text="";});
    private async Task Send(){if(controller.Busy || operationBusy || string.IsNullOrWhiteSpace(input.Text)&&controller.DraftAttachments.Count==0)return;if(!HasProfile){localStatus=profiles.SelectedValue is string?"ModelRequired":"ChooseConnection";Refresh();return;}await RunAsync(async()=>{if(!EnsureTask())return;string text=input.Text;try{await controller.SendAsync(text);input.Text=controller.Current?.DraftText??"";}catch{if(input.Text.Length==0)input.Text=text;throw;}});}
    private void AttachFile()=>Run(()=>{if(SelectedGame is null){localStatus="GameForTools";return;}if(!EnsureTask(false))return;var d=new Microsoft.Win32.OpenFileDialog{Title=AiToolsText.Text("AttachHint"),Filter=AiToolsText.Text("TextFiles")+" (*.txt;*.json;*.ini;*.cfg;*.csv;*.xml)|*.txt;*.json;*.ini;*.cfg;*.csv;*.xml",CheckFileExists=true,Multiselect=false};if(d.ShowDialog(W.Window.GetWindow(this))==true)controller.AttachModSource(d.FileName);});
    
    private void ExportMod()=>Run(()=>{if(controller.SelectedModId is not {} selected)return;var d=new Microsoft.Win32.SaveFileDialog{Title=AiToolsText.Text("Export"),Filter="Fusion MOD (*.zip)|*.zip",DefaultExt=".zip",AddExtension=true,OverwritePrompt=true,FileName="Fusion-Mod.zip"};if(d.ShowDialog(W.Window.GetWindow(this))==true)controller.ExportMod(selected,d.FileName);});
    private async Task ChangeMod(bool installing)
    {
        if(controller.Busy||operationBusy||modGameId is not {} gameId||!controller.Games.Any(g=>g.Id==gameId)||controller.SelectedModId is not {} selected||controller.ModPreview is not {} preview||preview.Id!=selected||preview.GameId!=gameId)return;
        modDetails.IsExpanded=true;UpdateLayout();
        if(W.MessageBox.Show(W.Window.GetWindow(this),AiToolsText.Text(installing?"InstallConfirm":"DisableConfirm")+"\n\n"+preview.Name+"\n"+preview.RelativePath,AiToolsText.Text("ModLibrary"),W.MessageBoxButton.OKCancel,W.MessageBoxImage.Question)!=W.MessageBoxResult.OK)return;
        await RunAsync(async()=>{if(modGameId!=gameId||controller.SelectedModId!=selected||!controller.Games.Any(g=>g.Id==gameId)||!EnsureModTask())return;controller.SelectMod(selected);if(installing)await controller.InstallModAsync(selected);else await controller.DisableModAsync(selected);});
    }
    private void Run(Action action){if(disposed)return;localStatus="";try{action();}catch(Exception ex){localStatus=ErrorCode(ex);}Refresh();}
    private async Task RunAsync(Func<Task> action){if(disposed || operationBusy)return;localStatus="";operationBusy=true;RefreshActions();try{await action();}catch(Exception ex){localStatus=ErrorCode(ex);}finally{operationBusy=false;if(!disposed)Refresh();}}
    private static string ErrorCode(Exception ex)=>ex switch{AiToolsException e=>e.Code,AiModException e=>e.Code,AiNumericFailure e=>e.Code,OperationCanceledException=>"Stopped",_=>"ActionFailed"};
    private void Changed(){if(disposed || pendingRefresh)return;pendingRefresh=true;Dispatcher.BeginInvoke(new Action(()=>{pendingRefresh=false;if(!disposed)Refresh();}),W.Threading.DispatcherPriority.Background);}

    internal void Refresh()
    {
        if(disposed)return;syncing=true;
        try
        {
            bool repaint=language!=UiLanguage.Current || !Equals(palette,UiTheme.Current);
            if(repaint){ClosePopups(false);language=UiLanguage.Current;palette=UiTheme.Current;foreach(var bind in bindings)bind();emptyMods.Text=AiToolsText.Text("NoMods");modDetails.Header=AiToolsText.Text("Details");budget.Text=AiToolsText.Text("NativeBudgetHelp");W.Automation.AutomationProperties.SetName(input,AiToolsText.Text("ComposerHint"));W.Automation.AutomationProperties.SetName(games,AiToolsText.Text("GameSearch"));W.Automation.AutomationProperties.SetName(profiles,AiToolsText.Text("AIModel"));foreach(var row in renderedMessages){row.Role.Text=AiToolsText.Text(row.RoleKey);if(row.IsStatus)row.Body.Text=AiToolsText.StatusText(row.Original);}modsSignature="\0";modGamesSignature="\0";}
            var allGames=controller.Games;string? selectedId=games.SelectedValue as string;
            var gameList=allGames.Where(g=>g.Running).Where(g=>query.Length==0 || g.Name.Contains(query,StringComparison.CurrentCultureIgnoreCase)).ToArray();
            string newGames=string.Join('\n',gameList.Select(g=>g.Id+"|"+g.Name+"|"+g.Running));
            if(newGames!=gamesSignature){games.ItemsSource=gameList;gamesSignature=newGames;if(selectedId is not null && gameList.Any(g=>g.Id==selectedId))games.SelectedValue=selectedId;else games.SelectedIndex=-1;}
            if(query.Length>0){games.SelectedIndex=-1;games.Text=query;}
            harnessChoice.SelectedValue=controller.SelectedHarness;W.Automation.AutomationProperties.SetName(harnessChoice,AiToolsText.Text("Backend"));
            IReadOnlyList<WorkspaceProfileChoice> profileList;try{profileList=controller.Profiles.Where(p=>ProfileMatchesHarness(p.Id)).ToArray();}catch(Exception ex){profileList=[];localStatus=ErrorCode(ex);}
            string newProfiles=string.Join('\n',profileList.Select(p=>p.Id+"|"+p.Label));
            if(newProfiles!=profilesSignature){string? selected=profiles.SelectedValue as string;profiles.ItemsSource=profileList;profilesSignature=newProfiles;if(selected is not null && profileList.Any(p=>p.Id==selected))profiles.SelectedValue=selected;else profiles.SelectedIndex=-1;}
            if(renderedTask!=(controller.Current?.Id??"")){modelDraft146=null;renderedTask=controller.Current?.Id??"";messages.Children.Clear();renderedMessages.Clear();modsSignature="\0";nativeOutput.Text="";nativeState.Text="";if(controller.Current is {} t){query="";games.SelectedValue=gameList.Any(g=>g.Id==t.GameId)?t.GameId:null;profiles.SelectedValue=profileList.Any(p=>p.Id==t.ProfileId)?t.ProfileId:null;}}
            string nextTasks=UiLanguage.Current+"|"+string.Join('|',controller.Tasks.Select(t=>t.Id+":"+t.Title+":"+t.IsPinned+":"+t.TitleIsCustom+":"+t.Messages.Count+":"+GameName(t.GameId)));if(nextTasks!=tasksSignature){tasks.ItemsSource=controller.Tasks.Select(t=>new TaskRow(t,GameName(t.GameId))).ToArray();tasksSignature=nextTasks;}tasks.SelectedItem=tasks.Items.OfType<TaskRow>().FirstOrDefault(x=>x.Task.Id==controller.Current?.Id);history.Header=AiToolsText.Format("ChatCountCompact",controller.Tasks.Count);history.Visibility=W.Visibility.Visible;
            var selectedGame=SelectedGame;selectedGameId=selectedGame?.Id??"";selectedProfileId=profiles.SelectedValue as string??"";if(selectedGameId.Length>0&&selectedProfileId.Length>0)lastProfileByGame[selectedGameId]=selectedProfileId;if(controller.Current is {} bound&&(SameTask||viewingHistory))lastTaskByGame[bound.GameId]=bound.Id;SwitchDraft(viewingHistory&&controller.Current is {} historyTask?"task:"+historyTask.Id:SameTask?"task:"+controller.Current!.Id:selectedGame is not null?"game:"+controller.SelectedHarness+":"+selectedGame.Id:"harness:"+controller.SelectedHarness);gameState.Text=selectedGame is null?AiToolsText.Text(query.Length>0 && gameList.Length==0?"NoSearchResults":"GameOptional"):AiToolsText.Text(selectedGame.Running?"RunningGame":"StoppedGame")+" · "+selectedGame.Engine;apiState.Text=AiToolsText.Text(RetiredProfile?"ai_backend_retired":"ChooseConnection");apiState.Visibility=HasProfile?W.Visibility.Collapsed:W.Visibility.Visible;profiles.ToolTip=AiToolsText.Text("ConnectionHelp");W.Automation.AutomationProperties.SetName(profiles,AiToolsText.Text("Connection"));if(selectedGame is null&&viewingHistory&&controller.Current is {GameId.Length:>0} archived)gameState.Text=GameName(archived.GameId)+" · "+AiToolsText.Text("StoppedGame");games.ToolTip=gameState.Text;
            bool hasConversation=controller.Current is {Messages.Count:>0} && (SameTask||viewingHistory);welcomeCard.Visibility=hasConversation||controller.Busy?W.Visibility.Collapsed:W.Visibility.Visible;conversationCard.Visibility=hasConversation||controller.Busy?W.Visibility.Visible:W.Visibility.Collapsed;
            string titleKey=HasProfile?"NativeWelcomeTitle":"ChooseConnection",bodyKey=HasProfile?"NativeWelcomeBody":RetiredProfile?"ai_backend_retired":"ConnectionHelp";welcomeAction.Visibility=!HasProfile&&profileList.Count==0?W.Visibility.Visible:W.Visibility.Collapsed;ButtonText(welcomeAction,"OpenAiSettings",true);welcomeTitle.Text=AiToolsText.Text(titleKey);welcomeBody.Text=AiToolsText.Text(bodyKey);welcomeBody.Visibility=W.Visibility.Visible;examples.Visibility=selectedGame is not null && HasProfile?W.Visibility.Visible:W.Visibility.Collapsed;
            composer.Visibility=W.Visibility.Visible;
            attachment.Visibility=W.Visibility.Visible;sourceName.Text=SameTask && controller.SelectedSourceName is {} file?AiToolsText.Format("Attached",file):AiToolsText.Text("NoAttachment");
            if(controller.Busy&&controller.Current?.DraftText.Length==0&&input.Text.Length>0){restoringDraft=true;try{input.Text="";if(draftKey.Length>0)drafts[draftKey]="";}finally{restoringDraft=false;}}RefreshConversation144(repaint);RefreshAttachments144();
            proposal.Text=SameTask?controller.PendingSummary??"":"";proposalCard.Visibility=proposal.Text.Length>0?W.Visibility.Visible:W.Visibility.Collapsed;
            RefreshNativeControls(repaint);nativeActivity.Visibility=W.Visibility.Collapsed;if(controller.Busy&&nativeState.Text.Length==0)nativeState.Text=AiToolsText.Text("NativeWorking");workspaceState.Visibility=W.Visibility.Collapsed;
            IReadOnlyList<AiTurnUsage> turnUsage=SameTask?controller.CurrentCostHistory:[];string inputUsage=turnUsage.Count>0&&turnUsage.All(x=>x.InputTokens.HasValue)?turnUsage.Sum(x=>x.InputTokens!.Value).ToString():"—";string outputUsage=turnUsage.Count>0&&turnUsage.All(x=>x.OutputTokens.HasValue)?turnUsage.Sum(x=>x.OutputTokens!.Value).ToString():"—";string requestUsage=turnUsage.Count>0&&turnUsage.All(x=>x.ApiRequestCount.HasValue)?turnUsage.Sum(x=>x.ApiRequestCount!.Value).ToString():"—";usage.Text=AiToolsText.Format("Usage",requestUsage,inputUsage,outputUsage);string compatibility=profiles.SelectedValue is string activeProfile?controller.ProfileHarnessError(activeProfile):"";string code=controller.HistorySaveFailed?"storage_error":compatibility.Length>0?compatibility:localStatus.Length>0?localStatus:controller.StatusCode;status.Visibility=code is "ready" or "session_restored"?W.Visibility.Collapsed:W.Visibility.Visible;status.Text=AiWorkspaceText146.Contains(code)?AiWorkspaceText146.Text(code):AiWorkspaceText144.Contains(code)?AiWorkspaceText144.Text(code):code=="mod_game_not_running"?AiToolsText.Format("StartModGame",localStatusTarget):code.StartsWith("ai_connection_",StringComparison.Ordinal)?AiConnectionText.Text(code):AiToolsText.Text(code);
            chat.Visibility=showMods?W.Visibility.Collapsed:W.Visibility.Visible;modPage.Visibility=showMods?W.Visibility.Visible:W.Visibility.Collapsed;ButtonText(assistantTab,"Assistant");ButtonText(modTab,"ModLibrary",showMods);assistantTab.Visibility=showMods?W.Visibility.Visible:W.Visibility.Collapsed;
            newTask.HorizontalContentAlignment=assistantTab.HorizontalContentAlignment=modTab.HorizontalContentAlignment=W.HorizontalAlignment.Left;billingButton.HorizontalContentAlignment=W.HorizontalAlignment.Stretch;newTask.Padding=assistantTab.Padding=modTab.Padding=new(12,5,12,5);gamePlaceholder.Text=AiToolsText.Text("GameOptional");gamePlaceholder.Visibility=games.SelectedValue is null&&query.Length==0?W.Visibility.Visible:W.Visibility.Collapsed;unlinkGame.Visibility=games.SelectedValue is null?W.Visibility.Collapsed:W.Visibility.Visible;unlinkGame.Content="×";billingButton.Padding=new(12,6,12,6);
            configuration.Visibility=showMods?W.Visibility.Collapsed:W.Visibility.Visible;RefreshThinking();RefreshBilling();RefreshMods();ArrangePanels();RefreshActions();RefreshTaskActions146();AutoLoadModels144();
        }
        finally{syncing=false;}
    }
    private string SelectedThinking145()=>effortValues[Math.Clamp((int)thinkingSlider.Value,0,effortValues.Length-1)];
    private void CommitThinking145(){if(profiles.SelectedValue is string){string effort=SelectedThinking145();Run(()=>{if(EnsureTask())controller.SetCurrentReasoningEffort(effort);});}}
    private void RefreshThinking()
    {
        if(thinkingDragging145)return;
        AiAgentOptions? options=null;ApiSettings? settings=null;try{if(profiles.SelectedValue is string id){bool current=controller.Current?.ProfileId==id;options=current?controller.GetCurrentProfileOptions():controller.GetExecutionOptions(id);settings=current?controller.GetCurrentProfileSettings():controller.GetProfileSettings(id);if(modelDraft146 is not null)chatModel.Text=modelDraft146;else if(!chatModel.IsKeyboardFocusWithin)chatModel.Text=settings.Model;}}catch{ }
        budget.Text=AiToolsText.Text("NativeBudgetHelp");
        effortValues=new[]{""}.Concat(options is not null&&settings is not null&&!RetiredProfile?options.EffortsFor(settings):[]).Distinct().ToArray();thinkingSlider.Maximum=effortValues.Length-1;thinkingSlider.Visibility=effortValues.Length>1?W.Visibility.Visible:W.Visibility.Collapsed;
        string effort=options is not null&&settings is not null?options.EffectiveEffort(settings):"";thinkingSlider.Value=Math.Max(0,Array.IndexOf(effortValues,effort));thinkingState.Text=AiUiText.EffortText(effort);SetThinkingButton(effort,settings?.Model);popupModel.Text=settings?.Model??AiToolsText.Text("Unknown");popupModel.ToolTip=popupModel.Text;thinkingButton.ToolTip=AiToolsText.Text("Thinking")+" · "+AiUiText.EffortText(effort)+"\n"+popupModel.Text;thinkingState.ToolTip=AiToolsText.Text("Thinking")+" · "+AiUiText.EffortText(effort);thinkingSlider.ToolTip=AiToolsText.Text(effortValues.Length>1?"Thinking":"ThinkingUnknown");
    }
    private void RefreshBilling()
    {
        string id=profiles.SelectedValue as string??"",identity="";AiBalanceResult? result=null;try{if(id.Length>0){result=controller.GetBalance(id);identity=controller.GetBalanceIdentity(id);}}catch(Exception ex){localStatus=ErrorCode(ex);}
        balanceLink.Visibility=Uri.TryCreate(result?.OfficialUrl,UriKind.Absolute,out var official)&&official.Scheme=="https"?W.Visibility.Visible:W.Visibility.Collapsed;
        if(result?.Items.Count>0)
        {
            balance.Text=string.Join(" · ",result.Items.Select(x=>(x.Kind=="prepaid_ledger"?AiToolsText.Text("PrepaidLedger")+" · ":"")+Money(x.Total)+" "+x.Currency));
            if(result.Status=="stale")balance.Text+=" · "+AiToolsText.Text("StaleBalance");balance.ToolTip=result.CheckedAt.ToLocalTime().ToString("g");
        }
        else {bool account=false;try{account=id.Length>0&&controller.GetProfileOptions(id).AuthMode=="account";}catch{}balance.Text=AiToolsText.Text(account?"BalanceAccount":result?.Status=="unsupported"?"BalanceUnsupported":result?.Status=="error"?"RefreshFailed":"Unknown");balance.ToolTip=AiToolsText.Text("BillingHelp");}
        balanceDetail.Text=balance.Text+(result is null?"":"\n"+result.CheckedAt.ToLocalTime().ToString("g"));balance.ToolTip=balanceDetail.Text;W.Automation.AutomationProperties.SetName(billingButton,AiToolsText.Text("ApiBalance145"));billingButton.ToolTip=AiToolsText.Text("ApiBalance145")+": "+balanceDetail.Text;
        var lastCompleted=controller.CostHistory.FirstOrDefault(x=>x.ProfileId==id&&x.CompletedAt.HasValue);
        if(RetiredProfile)identity="";
        requestedBalanceProfile=identity.Length>0?id:"";
        requestedBalanceKey=identity.Length==0?"":id+"|"+identity+"|"+lastCompleted?.Id;
        if(requestedBalanceKey.Length>0&&requestedBalanceKey!=completedBalanceKey&&!balanceBusy)_=RefreshBalanceLoop();
    }
    private static string Money(decimal value)=>value.ToString("0.############################");
    private static string CostLabel(AiTurnUsage turn)=>(turn.Cost is {} value?Money(value)+" "+turn.Currency+" · "+AiToolsText.Text(turn.CostKind=="reported"?"Actual":"Estimated"):AiToolsText.Text("Unknown"))+(turn.Requests.Any(x=>x.EstimateScope=="standard_text_base")?"\n"+AiSettingsText144.Text("EstimateScope144"):"")+(turn.Requests.Any(x=>x.CacheDiscountUnknown)?"\n"+AiWorkspaceText144.Text("CacheDiscountUnknown"):"");
    private void OpenBilling()
    {
        if(profiles.SelectedValue is not string id)return;var result=controller.GetBalance(id);if(Uri.TryCreate(result?.OfficialUrl,UriKind.Absolute,out var url)&&url.Scheme=="https")Run(()=>System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.AbsoluteUri){UseShellExecute=true}));
    }
    private Task RefreshBalance()
    {
        completedBalanceKey="";return RefreshBalanceLoop();
    }
    private async Task RefreshBalanceLoop()
    {
        if(disposed||balanceBusy)return;balanceBusy=true;RefreshActions();
        try
        {
            while(!disposed&&requestedBalanceProfile.Length>0&&requestedBalanceKey!=completedBalanceKey)
            {
                string id=requestedBalanceProfile,key=requestedBalanceKey;
                try{await controller.RefreshBalanceAsync(id);}catch(Exception ex){localStatus=ErrorCode(ex);}
                completedBalanceKey=key;
                // A switch or edit during the awaited request is processed next. An old
                // completion never marks a different profile/credential identity as fresh.
                if(!disposed)Refresh();
            }
        }
        finally{balanceBusy=false;if(!disposed)Refresh();}
    }
    private void ShowCostHistory()=>ShowCostHistory144();
    internal W.Window CreateCostHistoryWindow()
    {
        var panel=new C.StackPanel{Margin=new(22)};Id(panel,"AiCostHistoryEntries");
        var window=new W.Window{Title=AiToolsText.Text("CostHistory"),Width=620,Height=650,MinWidth=420,MinHeight=380,MaxWidth=W.SystemParameters.WorkArea.Width,MaxHeight=W.SystemParameters.WorkArea.Height,Owner=W.Window.GetWindow(this),WindowStartupLocation=W.WindowStartupLocation.CenterOwner,ShowInTaskbar=false,FontFamily=new(FontManager.ProductDefaultFamily),FontSize=14,UseLayoutRounding=true};
        Id(window,"AiCostHistoryWindow");WpfPresentationRecovery.Attach(window);window.Resources.MergedDictionaries.Add(WpfThemeResources.Create());WpfThemeResources.Apply(window.Resources);window.SetResourceReference(W.Window.BackgroundProperty,"Fusion.Main");window.SetResourceReference(W.Window.ForegroundProperty,"Fusion.Text");
        var scroll=new C.ScrollViewer{Content=panel,VerticalScrollBarVisibility=C.ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=C.ScrollBarVisibility.Disabled};window.Content=scroll;
        bool closed=false,queued=false;string signature="";
        void Render()
        {
            if(closed)return;var entries=controller.CostHistory;
            string next=UiLanguage.Current+"|"+string.Join('|',entries.Select(x=>x.Id+":"+x.CompletedAt+":"+x.Status+":"+x.Cost+":"+x.Currency+":"+x.CostKind+":"+x.InputTokens+":"+x.OutputTokens+":"+x.ApiRequestCount));
            if(next==signature)return;signature=next;
            var expanded=panel.Children.OfType<C.Border>().Select(x=>x.Child).OfType<C.StackPanel>().SelectMany(x=>x.Children.OfType<C.Expander>()).Where(x=>x.IsExpanded).Select(x=>x.Tag as string).ToHashSet();
            double offset=scroll.VerticalOffset;panel.Children.Clear();var title=Label(23,true);title.Text=AiToolsText.Text("CostHistory");panel.Children.Add(title);
            foreach(var entry in entries)
            {
                var card=Card();card.Margin=new(0,14,0,0);var row=new C.StackPanel();var heading=Label(15,true);heading.Text=entry.ProfileName+" · "+entry.Model;row.Children.Add(heading);
                var summary=Label();Id(summary,"AiCostValue_"+entry.Id);summary.Text=entry.StartedAt.ToLocalTime().ToString("g")+"\n"+CostLabel(entry)+"\n"+AiToolsText.Text("Thinking")+" · "+AiUiText.EffortText(entry.Effort);row.Children.Add(summary);
                var token=Label();token.Text=AiToolsText.Format("Usage",entry.ApiRequestCount?.ToString()??"—",entry.InputTokens?.ToString()??"—",entry.OutputTokens?.ToString()??"—");var detail=new C.Expander{Header=AiToolsText.Text("TokenDetails"),Content=token,Tag=entry.Id,IsExpanded=expanded.Contains(entry.Id),Margin=new(0,8,0,0)};detail.SetResourceReference(C.Control.ForegroundProperty,"Fusion.Text");row.Children.Add(detail);card.Child=row;panel.Children.Add(card);
            }
            if(entries.Count==0){var none=Label();none.Text=AiToolsText.Text("NoCostHistory");none.Margin=new(0,18,0,0);panel.Children.Add(none);}
            scroll.ScrollToVerticalOffset(offset);
        }
        void Changed()
        {
            if(closed||queued)return;queued=true;window.Dispatcher.BeginInvoke(new Action(()=>{queued=false;if(!closed)Render();}),W.Threading.DispatcherPriority.Background);
        }
        controller.Changed+=Changed;window.Closed+=(_,_)=>{closed=true;controller.Changed-=Changed;};Render();return window;
    }
private void RefreshMods()
{
    var allGames=controller.Games;
    var allMods=controller.AllMods;
    var grouped=allMods.GroupBy(m=>m.GameId,StringComparer.Ordinal)
        .Select(group=>new {Id=group.Key,Game=allGames.FirstOrDefault(g=>g.Id==group.Key),Count=group.Count()})
        .OrderBy(x=>x.Game is null).ThenBy(x=>x.Game?.Name??x.Id,StringComparer.CurrentCultureIgnoreCase).ToArray();
    string next=UiLanguage.Current+"|"+string.Join('|',grouped.Select(x=>string.Join("\0",
        x.Id,x.Count,x.Game?.Name,x.Game?.DisplayName,x.Game?.Artwork?.Key)));
    if(next!=modGamesSignature)
    {
        modArtworkEpoch++;modGames.Children.Clear();
        foreach(var group in grouped)modGames.Children.Add(ModGameCard(group.Game,group.Id,group.Count,modArtworkEpoch));
        modGamesSignature=next;ArrangeModCards();
    }
    // A removed library entry remains browseable while it still owns MODs.
    if(modGameId is not null&&!allGames.Any(g=>g.Id==modGameId)&&!allMods.Any(m=>m.GameId==modGameId))modGameId=null;
    bool groupedView=modGameId is null;modBack.Visibility=groupedView?W.Visibility.Collapsed:W.Visibility.Visible;
    modGameScroll.Visibility=groupedView?W.Visibility.Visible:W.Visibility.Collapsed;
    modList.Visibility=groupedView?W.Visibility.Collapsed:W.Visibility.Visible;
    IReadOnlyList<AiModEntry> entries=modGameId is {} gameId?allMods.Where(m=>m.GameId==gameId).ToArray():[];
    string signature=modGameId+"|"+UiLanguage.Current+"|"+string.Join('|',entries.Select(x=>x.Id+":"+x.Name+":"+x.Status));
    if(signature!=modsSignature)
    {
        mods.Items.Clear();
        foreach(var item in entries)
        {
            var p=new C.StackPanel{Margin=new(6)};var name=Label(15,true);name.Text=item.Name;p.Children.Add(name);
            var state=Label();state.Text=AiToolsText.Text(item.Status switch{"draft"=>"Draft","installed"=>"Installed","disabled"=>"Disabled",_=>"Conflict"});p.Children.Add(state);
            mods.Items.Add(new C.ListBoxItem{Content=p,Tag=item});
        }
        modsSignature=signature;
    }
    foreach(C.ListBoxItem row in mods.Items)if(row.Tag is AiModEntry item&&item.Id==controller.SelectedModId)mods.SelectedItem=row;
    emptyMods.Text=AiToolsText.Text(groupedView?"NoModsLibrary":"NoMods");
    emptyMods.Visibility=(groupedView?grouped.Length==0:entries.Count==0)?W.Visibility.Visible:W.Visibility.Collapsed;
    var preview=controller.ModPreview is {} currentPreview&&currentPreview.GameId==modGameId?currentPreview:null;
    modDetails.Visibility=preview is null?W.Visibility.Collapsed:W.Visibility.Visible;
    modPath.Text=preview?.RelativePath??"";modBefore.Text=preview?.Before??"";modAfter.Text=preview?.After??"";
}

private void ArrangeModCards()
{
    double available=modGameScroll.ViewportWidth;
    if(!double.IsFinite(available)||available<1)available=Math.Max(180,modGameScroll.ActualWidth-18);
    int columns=Math.Max(1,(int)((available+16)/276));
    double width=Math.Clamp((available-(columns-1)*16)/columns,180,360);
    int index=0;
    foreach(C.Button card in modGames.Children){card.Width=width;card.Margin=new(0,0,(++index%columns)==0?0:16,16);if(card.Content is C.Grid content)content.RowDefinitions[0].Height=new(Math.Max(1,width-2)*9/16);}
}

private C.Button ModGameCard(AiGameChoice? game,string gameId,int count,int epoch)
{
    string title=game?.Name??AiToolsText.Format("MissingModGameTitle",gameId[..Math.Min(8,gameId.Length)]);
    var button=WpfThemeResources.Button(title,null,()=>{modGameId=gameId;modsSignature="\0";Refresh();});
    button.Padding=new(0);button.MinHeight=0;button.HorizontalContentAlignment=W.HorizontalAlignment.Stretch;
    button.VerticalContentAlignment=W.VerticalAlignment.Top;Id(button,"AiModGame_"+gameId);
    button.ToolTip=game is null?AiToolsText.Text("MissingModGameHelp"):title;
    W.Automation.AutomationProperties.SetName(button,title+" · "+AiToolsText.Format("ModCount",count));
    var card=new C.Grid();card.RowDefinitions.Add(new(){Height=new(148)});card.RowDefinitions.Add(new(){Height=W.GridLength.Auto});
    var cover=new C.Border{CornerRadius=new(7,7,0,0),ClipToBounds=true};cover.SetResourceReference(C.Border.BackgroundProperty,"Fusion.Control");
    var placeholder=Label(13);placeholder.Text=AiToolsText.Text("NoCover");placeholder.HorizontalAlignment=W.HorizontalAlignment.Center;placeholder.VerticalAlignment=W.VerticalAlignment.Center;
    cover.Child=placeholder;card.Children.Add(cover);
    var identity=new C.Grid{Margin=new(10,9,10,10),MinHeight=68};identity.ColumnDefinitions.Add(new(){Width=new(32)});identity.ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star)});
    var icon=new C.Image{Source=WpfDrawing.Icon("game",UiTheme.Current.Accent),Width=24,Height=24,VerticalAlignment=W.VerticalAlignment.Top,Stretch=System.Windows.Media.Stretch.Uniform};identity.Children.Add(icon);
    var text=new C.StackPanel();var name=Label(14,true);name.Text=title;name.TextWrapping=W.TextWrapping.NoWrap;name.TextTrimming=W.TextTrimming.CharacterEllipsis;text.Children.Add(name);
    var translated=Label(13);translated.Text=game?.DisplayName??"";translated.TextWrapping=W.TextWrapping.NoWrap;translated.TextTrimming=W.TextTrimming.CharacterEllipsis;
    translated.Visibility=translated.Text.Length==0?W.Visibility.Collapsed:W.Visibility.Visible;text.Children.Add(translated);
    var amount=Label(14);amount.Text=AiToolsText.Format("ModCount",count);amount.Margin=new(0,4,0,0);text.Children.Add(amount);C.Grid.SetColumn(text,1);identity.Children.Add(text);
    C.Grid.SetRow(identity,1);card.Children.Add(identity);button.Content=card;
    bool loading=false,loaded=false;
    async void LoadArtwork()
    {
        if(loading||loaded||disposed||!showMods||!button.IsVisible||game?.Artwork is not {} artwork)return;
        loading=true;
        try
        {
            var image=await artwork.LoadAsync();
            if(disposed||!showMods||!button.IsVisible||epoch!=modArtworkEpoch||!modGames.Children.Contains(button)||controller.Games.FirstOrDefault(g=>g.Id==gameId)?.Artwork?.Key!=artwork.Key)return;
            loaded=true;if(image.Icon is not null)icon.Source=image.Icon;
            if(image.Cover is {} source)
            {
                var brush=new System.Windows.Media.ImageBrush(source){Stretch=System.Windows.Media.Stretch.Fill,ViewboxUnits=System.Windows.Media.BrushMappingMode.Absolute};
                void Crop(){brush.Viewbox=(artwork.Frame??CoverFraming.Centered).Crop(source.PixelWidth,source.PixelHeight,Math.Max(1,cover.ActualWidth)/Math.Max(1,cover.ActualHeight));}
                cover.SizeChanged+=(_,_)=>Crop();Crop();cover.Background=brush;cover.Child=null;
            }
        }
        catch(Exception){/* Keep the local placeholder; image failure is not a task failure. */}
        finally{loading=false;}
    }
    button.Loaded+=(_,_)=>LoadArtwork();button.IsVisibleChanged+=(_,_)=>{if(button.IsVisible)LoadArtwork();};
    return button;
}

// Independent target picker includes stopped games and zero-MOD games. It does
// not change the chat selector and cannot select an orphan as a write target.
private async Task<string?> PickModImportGame()
{
    var available=controller.Games;
    if(available.Count==0){localStatus="SelectGame";Refresh();return null;}
    if(modGameId is {} id&&available.Any(g=>g.Id==id))return id;
    var completion=new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
    var popup=new WpfAiOverlay144();var choice=new C.ComboBox{ItemsSource=available,DisplayMemberPath="Name",SelectedValuePath="Id",MinHeight=36,Margin=new(0,0,0,16)};Id(choice,"AiImportGameChoice");
    if(SelectedGame is {} selected&&available.Any(g=>g.Id==selected.Id))choice.SelectedValue=selected.Id;
    var body=new C.StackPanel();var hint=Label();hint.Text=AiToolsText.Text("ChooseImportGame");hint.Margin=new(0,0,0,12);body.Children.Add(hint);body.Children.Add(choice);
    var buttons=new C.WrapPanel();var okay=Button("Import",()=>{completion.TrySetResult((choice.SelectedItem as AiGameChoice)?.Id);popup.IsOpen=false;},"AiConfirmImportGame",true);okay.IsEnabled=choice.SelectedItem is AiGameChoice;choice.SelectionChanged+=(_,_)=>okay.IsEnabled=choice.SelectedItem is AiGameChoice;buttons.Children.Add(okay);buttons.Children.Add(Button("Cancel",()=>popup.IsOpen=false,"AiCancelImportGame"));body.Children.Add(buttons);
    SetupPopup(popup,import,body,"AiImportGamePopup");var selection=new WpfAiSelection144(this,choice,()=>choice.Items.Cast<object>(),x=>((AiGameChoice)x).Name);popup.Closed+=(_,_)=>completion.TrySetResult(null);popup.IsOpen=true;
    try{return await completion.Task;}finally{selection.Dispose();Children.Remove(popup);}
}
private void ImportMod()=>_=RunAsync(async()=>
{
    string? target=await PickModImportGame();if(target is null)return;
    var d=new Microsoft.Win32.OpenFileDialog{Title=AiToolsText.Text("Import"),Filter="Fusion MOD (*.zip)|*.zip",CheckFileExists=true};
    if(d.ShowDialog(W.Window.GetWindow(this))!=true)return;
    modGameId=target;if(!EnsureModTask())return;controller.ImportMod(d.FileName);modsSignature="\0";
});

    private void RefreshActions()
    {
        bool idle=!controller.Busy && !operationBusy;if(!idle){ClosePopup(thinkingPopup,false);ClosePopup(permissionPopup,false);}games.IsEnabled=profiles.IsEnabled=harnessChoice.IsEnabled=workspaceButton.IsEnabled=permissionButton.IsEnabled=nativeAgent.IsEnabled=unlinkGame.IsEnabled=idle;chatModel.IsEnabled=idle&&profiles.SelectedValue is string;fetchModels.IsEnabled=idle&&!modelBusy&&profiles.SelectedValue is string;configure.IsEnabled=idle;thinkingButton.IsEnabled=idle;billingButton.IsEnabled=!disposed;tasks.IsEnabled=true;tasks.IsHitTestVisible=true;tasks.IsTabStop=true;tasks.Opacity=1;welcomeAction.IsEnabled=idle;
        send.IsEnabled=idle && HasProfile && (!string.IsNullOrWhiteSpace(input.Text)||controller.DraftAttachments.Count>0);send.Visibility=controller.Busy?W.Visibility.Collapsed:W.Visibility.Visible;stop.Visibility=controller.Busy?W.Visibility.Visible:W.Visibility.Collapsed;stop.IsEnabled=controller.Busy;input.IsEnabled=idle;
        input.ToolTip=HasProfile?AiToolsText.Text("InputHint"):AiToolsText.Text(RetiredProfile?"ai_backend_retired":"ConfigureAIFirst");newTask.Visibility=W.Visibility.Visible;newTask.IsEnabled=idle;attach.IsEnabled=idle;confirm.IsEnabled=idle && SameTask && controller.PendingSummary is not null;cancelProposal.IsEnabled=idle && SameTask;import.IsEnabled=idle&&controller.Games.Count>0;bool selectedMod=controller.ModPreview is {} selectedPreview&&selectedPreview.GameId==modGameId&&controller.SelectedModId==selectedPreview.Id;export.IsEnabled=idle&&selectedMod;install.IsEnabled=disable.IsEnabled=idle&&selectedMod&&controller.Games.Any(g=>g.Id==modGameId);export.Visibility=install.Visibility=disable.Visibility=selectedMod?W.Visibility.Visible:W.Visibility.Collapsed;balanceRefresh.IsEnabled=idle&&!balanceBusy&&profiles.SelectedValue is string billingId&&controller.ProfileUsesHarness(billingId);thinkingSlider.IsEnabled=idle&&effortValues.Length>1;
    }
    private void ArrangePanels()
    {
        ColumnDefinitions[0].Width=new(ActualWidth<760?160:ActualWidth<1000?188:216);sidebar.Margin=new(0,0,ActualWidth<760?12:20,0);games.Width=ActualWidth<850?195:240;workspaceButton.MaxWidth=ActualWidth<850?175:300;gamePlaceholder.MaxWidth=Math.Max(40,games.Width-42);bodyViewport.VerticalScrollBarVisibility=C.ScrollBarVisibility.Disabled;chat.RowDefinitions[0].Height=new(1,W.GridUnitType.Star);conversationCard.Height=double.NaN;welcomeCard.VerticalAlignment=W.VerticalAlignment.Center;welcomeCard.MaxHeight=double.PositiveInfinity;modPage.RowDefinitions[2].Height=new(1,W.GridUnitType.Star);
    }
    public void Dispose(){if(disposed)return;disposed=true;modArtworkEpoch++;ClosePopups(false);refreshTimer.Stop();controller.Changed-=Changed;controller.NativeProgressChanged-=NativeProgress;modelLifetime?.Cancel();modelLifetime?.Dispose();foreach(var selection in inlineSelections144)selection.Dispose();if(ownsController)controller.Dispose();}
}
