using W = System.Windows;
using C = System.Windows.Controls;
using I = System.Windows.Input;

namespace ScreenshotTranslationUiTester;

internal sealed partial class WpfAiToolsWorkspace
{
    private readonly List<WpfAiSelection144> inlineSelections144=[];
    private readonly Dictionary<string,bool> expandedActivities144=new(StringComparer.Ordinal);
    private readonly WpfAiOverlay144 locationPopup144=new(),historyPopup144=new(),confirmPopup144=new();
    private readonly C.TextBlock modelStatus144=Label(13);
    private readonly C.WrapPanel attachmentChips144=new(){Margin=new(0,4,0,0)};
    private string conversationSignature144="",attachmentSignature144="",modelCatalogError144="",modelCatalogAttempt144="";
    private C.Button? locationsButton144;

    private C.Button Button144(string key,Action action,string id,bool primary=false)
    {
        var b=WpfThemeResources.Button(AiWorkspaceText144.Text(key),null,action,primary);Id(b,id);b.Margin=new(0,0,8,0);b.MinHeight=36;
        bindings.Add(()=>{b.Content=AiWorkspaceText144.Text(key);b.ToolTip=AiWorkspaceText144.Text(key);W.Automation.AutomationProperties.SetName(b,AiWorkspaceText144.Text(key));});return b;
    }
    private C.TextBlock Text144(string key,int size=14,bool bold=false){var t=Label(size,bold);bindings.Add(()=>t.Text=AiWorkspaceText144.Text(key));t.Text=AiWorkspaceText144.Text(key);return t;}
    private void InitializeWorkspace144()
    {
        inlineSelections144.Add(new(this,harnessChoice,()=>harnessChoice.Items.Cast<object>(),x=>((HarnessChoice)x).Label));
        inlineSelections144.Add(new(this,games,()=>games.Items.Cast<object>(),x=>((AiGameChoice)x).Name));
        inlineSelections144.Add(new(this,profiles,()=>profiles.Items.Cast<object>(),x=>((WorkspaceProfileChoice)x).Label));
        inlineSelections144.Add(new(this,nativeAgent,()=>nativeAgent.Items.Cast<object>(),x=>((HarnessChoice)x).Label));
        inlineSelections144.Add(new(this,chatModel,()=>chatModel.Items.Cast<object>(),x=>x.ToString()??"",q=>AiModelSearch144.Filter(modelCatalog.Values,q).Select(x=>(object)x.Id),()=>!syncing));
        var modelField=composerChoices.Children.OfType<C.StackPanel>().Last();modelStatus144.Margin=new(0,7,0,0);modelStatus144.SetResourceReference(C.TextBlock.ForegroundProperty,"Fusion.Muted");Id(modelStatus144,"AiModelCatalogStatus");modelField.Children.Add(modelStatus144);
        configuration.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});locationsButton144=Button144("Locations",()=>TogglePopup(locationPopup144,locationsButton144!),"AiLocations");locationsButton144.Content="…";locationsButton144.Width=36;locationsButton144.Padding=new(0);locationsButton144.Margin=new(4,0,0,0);C.Grid.SetColumn(locationsButton144,3);configuration.Children.Add(locationsButton144);
        var locations=new C.StackPanel();locations.Children.Add(PopupHeading(locationPopup144,locationsButton144,Text144("Locations",18,true)));
        locations.Children.Add(Button144("OpenProject",()=>OpenLocation144(controller.CurrentWorkspace),"AiOpenProject"));
        locations.Children.Add(Button144("OpenConversation",OpenConversation144,"AiOpenConversation"));
        var modSource=Button144("ModSource",()=>{ClosePopup(locationPopup144,false);AttachFile();},"AiModSource");modSource.Margin=new(0,10,0,0);locations.Children.Add(modSource);locationPopup144.Opened+=(_,_)=>{modSource.IsEnabled=SelectedGame is not null;};SetupPopup(locationPopup144,locationsButton144,locations,"AiLocationsPopup");
        attachment.Children.Clear();sourceName.TextWrapping=W.TextWrapping.NoWrap;sourceName.TextTrimming=W.TextTrimming.CharacterEllipsis;attachment.Children.Add(sourceName);attachment.Children.Add(new C.ScrollViewer{Content=attachmentChips144,MaxHeight=110,VerticalScrollBarVisibility=C.ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=C.ScrollBarVisibility.Disabled});
        AllowDrop=true;PreviewDragOver+=(_,e)=>{e.Handled=true;e.Effects=!controller.Busy&&!operationBusy&&e.Data.GetDataPresent(W.DataFormats.FileDrop)?W.DragDropEffects.Copy:W.DragDropEffects.None;};
        PreviewDrop+=(_,e)=>{e.Handled=true;if(!controller.Busy&&!operationBusy&&e.Data.GetData(W.DataFormats.FileDrop) is string[] paths)_=AddPaths144(paths);};
        bindings.Add(()=>{locationsButton144.Content="…";attach.ToolTip=AiWorkspaceText144.Text("AttachFiles")+"\n"+AiWorkspaceText144.Text("AttachmentHelp");W.Automation.AutomationProperties.SetName(attach,AiWorkspaceText144.Text("AttachFiles"));RefreshModelStatus144();});
    }
    private void CloseExtraPopups144(){taskMenu146.IsOpen=false;taskEdit146.IsOpen=false;foreach(var selection in inlineSelections144)selection.Close();locationPopup144.IsOpen=false;historyPopup144.IsOpen=false;confirmPopup144.IsOpen=false;foreach(var combo in new[]{games,profiles,harnessChoice,chatModel,nativeAgent})combo.IsDropDownOpen=false;}
    private string ModelIdentity144(string id){try{return id+"|"+AiModelCatalogCache144.Identity(controller.GetProfileSettings(id),controller.GetExecutionOptions(id));}catch{return id;}}
    private void AutoLoadModels144()
    {
        if(disposed||modelBusy||controller.Busy||profiles.SelectedValue is not string id||ModelIdentity144(id)==modelCatalogAttempt144)return;
        Dispatcher.BeginInvoke(new Action(()=>{if(!disposed&&!modelBusy&&!controller.Busy&&profiles.SelectedValue as string==id&&ModelIdentity144(id)!=modelCatalogAttempt144)_=FetchModels();}),W.Threading.DispatcherPriority.Background);
    }
    private void RefreshModelStatus144()
    {
        modelStatus144.Text=modelBusy?AiWorkspaceText144.Text("LoadingModels"):modelCatalogError144.Length>0?AiWorkspaceText144.Text("CatalogFailed")+"\n"+AiWorkspaceText146.Text(modelCatalogError144):modelCatalog.Count==0?AiWorkspaceText144.Text("NoChoices"):"";
        modelStatus144.Visibility=modelStatus144.Text.Length>0?W.Visibility.Visible:W.Visibility.Collapsed;
        foreach(var choice in inlineSelections144)if(choice.IsOpen)choice.Refresh();
    }
    private async Task AddFiles144()
    {
        if(controller.Busy||operationBusy)return;
        var dialog=new Microsoft.Win32.OpenFileDialog{Title=AiWorkspaceText144.Text("AttachFiles"),Filter="All files (*.*)|*.*",CheckFileExists=true,Multiselect=true};
        if(dialog.ShowDialog(W.Window.GetWindow(this))==true)await AddPaths144(dialog.FileNames);
    }
    private Task AddPaths144(IEnumerable<string> paths)=>RunAsync(async()=>{if(!EnsureTask(false))return;await controller.AddAttachmentsAsync(paths);});
    private void RefreshAttachments144()
    {
        sourceName.Visibility=SameTask&&controller.SelectedSourceName is not null?W.Visibility.Visible:W.Visibility.Collapsed;
        string signature=UiLanguage.Current+"|"+controller.Busy+"|"+operationBusy+"|"+controller.ImageInputCapability+"|"+string.Join('|',controller.DraftAttachments.Select(x=>x.Id+":"+x.Name+":"+x.Size+":"+x.AsFileReference));
        if(signature!=attachmentSignature144)
        {
            attachmentSignature144=signature;attachmentChips144.Children.Clear();
            foreach(var file in controller.DraftAttachments)
            {
                var chip=Card();chip.Padding=new(9,6,9,6);chip.Margin=new(0,0,6,6);chip.MaxWidth=330;Id(chip,"AiAttachment_"+file.Id);var rows=new C.StackPanel();var header=new C.Grid();header.ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star)});header.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});
                var name=Label(13);name.Text=(file.Kind=="image"?"▧ ":"▤ ")+file.Name+" · "+FileSize144(file.Size);name.TextWrapping=W.TextWrapping.NoWrap;name.TextTrimming=W.TextTrimming.CharacterEllipsis;name.ToolTip=file.Name;header.Children.Add(name);
                var remove=Button144("Remove",()=>Run(()=>controller.RemoveAttachment(file.Id)),"AiRemoveAttachment_"+file.Id);remove.Content="×";remove.Width=28;remove.Height=28;remove.MinHeight=28;remove.Padding=new(0);remove.Margin=new(7,0,0,0);remove.IsEnabled=!controller.Busy&&!operationBusy;C.Grid.SetColumn(remove,1);header.Children.Add(remove);rows.Children.Add(header);
                if(file.Kind=="image")
                {
                    var reference=new C.CheckBox{Content=AiWorkspaceText144.Text("FileReference"),IsChecked=file.AsFileReference,ToolTip=AiWorkspaceText144.Text("FileReferenceHelp"),Margin=new(0,5,0,0),FontSize=12,IsEnabled=!controller.Busy&&!operationBusy};reference.SetResourceReference(C.Control.ForegroundProperty,"Fusion.Text");Id(reference,"AiImageAsFile_"+file.Id);reference.Checked+=(_,_)=>Run(()=>controller.SetAttachmentAsFileReference(file.Id,true));reference.Unchecked+=(_,_)=>Run(()=>controller.SetAttachmentAsFileReference(file.Id,false));rows.Children.Add(reference);
                    if(!file.AsFileReference&&controller.ImageInputCapability!=true){var warning=Label(12);warning.Text=AiWorkspaceText144.Text(controller.ImageInputCapability==false?"attachment_image_unsupported":"attachment_image_unknown");warning.Margin=new(0,4,0,0);rows.Children.Add(warning);}
                }
                chip.Child=rows;attachmentChips144.Children.Add(chip);
            }
        }
        attachment.Visibility=controller.DraftAttachments.Count>0||sourceName.Visibility==W.Visibility.Visible?W.Visibility.Visible:W.Visibility.Collapsed;
    }
    private static string FileSize144(long size)=>size>=1024*1024?(size/(1024d*1024)).ToString("0.##")+" MB":size>=1024?(size/1024d).ToString("0.#")+" KB":size+" B";
    private void OpenConversation144()=>Run(()=>{string path=controller.CurrentConversationRecordPath;if(path.Length==0||!File.Exists(path)){localStatus="LocationMissing";return;}var start=new System.Diagnostics.ProcessStartInfo("explorer.exe"){UseShellExecute=false};start.ArgumentList.Add("/select,"+Path.GetFullPath(path));System.Diagnostics.Process.Start(start);});
    private void OpenLocation144(string path)=>Run(()=>{if(string.IsNullOrWhiteSpace(path)||!Directory.Exists(path)){localStatus="LocationMissing";return;}System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetFullPath(path)){UseShellExecute=true});});
    private void ConfirmFullPermission144()
    {
        ClosePopups(false);var body=new C.StackPanel();body.Children.Add(Text("PermissionFull",18,true));var help=Text("PermissionFullHelp");help.Margin=new(0,10,0,16);body.Children.Add(help);var buttons=new C.WrapPanel();buttons.Children.Add(Button("Confirm",()=>Run(()=>{if(EnsureTask(false))controller.SetPermissionMode("full");confirmPopup144.IsOpen=false;}),"AiConfirmFull",true));buttons.Children.Add(Button("Cancel",()=>confirmPopup144.IsOpen=false,"AiCancelFull"));body.Children.Add(buttons);
        if(!Children.Contains(confirmPopup144))SetupPopup(confirmPopup144,permissionButton,body,"AiFullPermissionConfirmation");else ((C.ScrollViewer)((C.Border)confirmPopup144.Child!).Child).Content=body;confirmPopup144.IsOpen=true;
    }
    private void RefreshConversation144(bool repaint)
    {
        if(controller.Current is not {} current||!(SameTask||viewingHistory))return;
        var turns=controller.CurrentExecutionSnapshot();
        string signature=current.Id+"|"+UiLanguage.Current+"|"+string.Join('|',current.Messages.Select(x=>x.Role+":"+x.Text.GetHashCode()))+"|"+string.Join('|',turns.Select(t=>t.Id+":"+t.Status+":"+t.Activities.Count+":"+string.Join(',',t.Activities.Select(a=>a.ItemId+":"+a.UpdatedAt.Ticks+":"+a.Status))+":"+t.Attachments.Count));
        if(!repaint&&signature==conversationSignature144)return;conversationSignature144=signature;
        bool bottom=conversation.ScrollableHeight-conversation.VerticalOffset<60;double offset=conversation.VerticalOffset;messages.Children.Clear();renderedMessages.Clear();
        // A turn starts before its user message. Insert the execution below that
        // prompt, so each result and its activity remain together after restart.
        var grouped=turns.GroupBy(t=>Math.Clamp(t.MessageStartIndex+1,0,current.Messages.Count)).ToDictionary(x=>x.Key,x=>x.ToArray());
        for(int n=0;n<=current.Messages.Count;n++)
        {
            if(grouped.TryGetValue(n,out var related))foreach(var turn in related)messages.Children.Add(ExecutionTurn144(turn));
            if(n==current.Messages.Count)break;var message=current.Messages[n];string roleKey=message.Role=="user"?"You":message.Role=="assistant"?"Assistant":"Notice";var role=Label(13,true);role.Text=AiToolsText.Text(roleKey);var text=ReadOnlyText();text.Text=message.Role=="status"?AiToolsText.StatusText(message.Text):message.Text;text.Background=System.Windows.Media.Brushes.Transparent;text.Margin=new(0);var row=new C.StackPanel();bool user=message.Role=="user";role.Visibility=user?W.Visibility.Collapsed:W.Visibility.Visible;role.Margin=new(0,0,0,7);row.Children.Add(role);row.Children.Add(text);
            var surface=new C.Border{Child=row,CornerRadius=new(16),Padding=user?new(14,10,14,10):new(0),Margin=new(user?40:0,0,user?0:24,20),HorizontalAlignment=user?W.HorizontalAlignment.Right:W.HorizontalAlignment.Stretch,MaxWidth=user?660:820};if(user)surface.SetResourceReference(C.Border.BackgroundProperty,"Fusion.Control");messages.Children.Add(surface);renderedMessages.Add((role,text,roleKey,message.Text,message.Role=="status"));
        }
        nativeActivity.Visibility=W.Visibility.Collapsed;
        Dispatcher.BeginInvoke(new Action(()=>{if(bottom)conversation.ScrollToEnd();else conversation.ScrollToVerticalOffset(offset);}),W.Threading.DispatcherPriority.Loaded);
    }
    private static string Status144(string value)=>AiWorkspaceText144.Text(value switch{"running" or "working" or "in_progress"=>"running","completed" or "done" or "succeeded" or "success"=>"completed","failed" or "error"=>"failed","cancelled" or "canceled" or "stopped"=>"stopped","interrupted"=>"interrupted","pending" or "not_started"=>"pending",_=>"unknown"});
    private C.Expander ExecutionTurn144(AiExecutionTurn turn)
    {
        var rows=new C.StackPanel();
        if(turn.Attachments.Count>0){var files=Label(12);files.Text=string.Join(" · ",turn.Attachments.Select(x=>x.Name));files.Margin=new(0,0,0,8);rows.Children.Add(files);}
        foreach(var activity in turn.Activities)
        {
            var details=new C.StackPanel();
            void Add(string value,bool code=false){if(string.IsNullOrWhiteSpace(value))return;var text=ReadOnlyText();text.FontSize=13;text.Text=value.Length>16000?value[..16000]+"\n"+AiWorkspaceText144.Text("Truncated"):value;text.MaxHeight=240;text.Background=System.Windows.Media.Brushes.Transparent;if(code)text.FontFamily=new("Consolas");details.Children.Add(text);}
            Add(activity.Command,true);if(activity.WorkingDirectory.Length>0)Add(activity.WorkingDirectory);
            foreach(var file in activity.Files){Add(file.Action+" · "+file.Path);Add(file.Diff,true);}
            foreach(var step in activity.Plan)Add(Status144(step.Status)+" · "+step.Text);
            if(activity.Text.Length>0&&activity.Text!=activity.Command)Add(activity.Text);
            if(activity.Truncated)Add(AiWorkspaceText144.Text("Truncated"));
            string title=activity.Title.Length>0?activity.Title:activity.Command.Length>0?activity.Command:AiWorkspaceText144.Text(AiWorkspaceText144.Contains(activity.Kind)?activity.Kind:"status");
            var label=Label(13);label.Text=Status144(activity.Status)+" · "+title;label.TextWrapping=W.TextWrapping.NoWrap;label.TextTrimming=W.TextTrimming.CharacterEllipsis;label.ToolTip=title;
            string key=turn.Id+":"+activity.ItemId;var item=new C.Expander{Header=label,Content=details,IsExpanded=expandedActivities144.GetValueOrDefault(key),Margin=new(activity.ParentItemId.Length>0?16:0,2,0,5)};item.SetResourceReference(C.Control.ForegroundProperty,"Fusion.Text");Id(item,"AiActivity_"+activity.ItemId);item.Expanded+=(_,_)=>expandedActivities144[key]=true;item.Collapsed+=(_,_)=>expandedActivities144[key]=false;rows.Children.Add(item);
        }
        string turnKey="turn:"+turn.Id;var heading=Label(13,true);heading.Text=AiWorkspaceText144.Text("Flow")+" · "+Status144(turn.Status)+" · "+turn.StartedAt.ToLocalTime().ToString("t");
        var result=new C.Expander{Header=heading,Content=rows,IsExpanded=expandedActivities144.TryGetValue(turnKey,out bool open)?open:turn.Status=="running",Margin=new(0,0,0,18)};result.SetResourceReference(C.Control.ForegroundProperty,"Fusion.Muted");Id(result,"AiTurn_"+turn.Id);result.Expanded+=(_,_)=>expandedActivities144[turnKey]=true;result.Collapsed+=(_,_)=>expandedActivities144[turnKey]=false;return result;
    }
    private void ShowCostHistory144()
    {
        ClosePopups(false);var body=new C.StackPanel();body.Children.Add(PopupHeading(historyPopup144,billingButton,Text("CostHistory",18,true)));
        foreach(var entry in controller.CostHistory)
        {
            var row=new C.StackPanel{Margin=new(0,8,0,12)};var title=Label(14,true);title.Text=entry.ProfileName+" · "+entry.Model;row.Children.Add(title);var value=Label(13);value.Text=entry.StartedAt.ToLocalTime().ToString("g")+" · "+CostLabel(entry);row.Children.Add(value);var tokens=Label(12);tokens.Text=AiToolsText.Format("Usage",entry.ApiRequestCount?.ToString()??"—",entry.InputTokens?.ToString()??"—",entry.OutputTokens?.ToString()??"—");row.Children.Add(tokens);body.Children.Add(row);
        }
        if(controller.CostHistory.Count==0)body.Children.Add(Text("NoCostHistory"));
        if(!Children.Contains(historyPopup144))SetupPopup(historyPopup144,billingButton,body,"AiCostHistoryPopup");else ((C.ScrollViewer)((C.Border)historyPopup144.Child!).Child).Content=body;historyPopup144.IsOpen=true;
    }
}
